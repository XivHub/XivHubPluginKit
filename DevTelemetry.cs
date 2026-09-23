using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XivHubPluginKit;

/// <summary>
/// Plugin-agnostic live-log / telemetry uploader for local dev. Buffers lines and POSTs them to a
/// mini log server on a background timer. Inert unless <c>enabled()</c> is true AND <c>url()</c> is
/// non-empty, so it is safe to ship in Release builds (dormant for normal users).
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\XivHubPluginKit\DevTelemetry.cs" Link="Dev\DevTelemetry.cs" /&gt;
///
/// Usage:
///   Telemetry = new DevTelemetry("MyPlugin", () =&gt; C.DevLog, () =&gt; C.DevLogUrl);
///   Telemetry.Log("something happened");
///   Telemetry.Snapshot(() =&gt; $"state={...}");   // call each frame; self-throttles
///   Telemetry.Record("callback", new Dictionary&lt;string, object?&gt; { ["addon"] = "Shop" });
///   await Telemetry.UploadFileAsync("capture", "json", bytes);
///   Telemetry.Dispose();
///
/// IMPORTANT: build snapshot strings on the framework thread (where game reads are safe). This class
/// only does string buffering + HTTP on a background thread.
/// </summary>
public sealed class DevTelemetry : IDisposable
{
    private readonly string source;
    private readonly Func<bool> enabled;
    private readonly Func<string?> url;
    private readonly Action<string>? onError;
    private readonly ConcurrentQueue<string> queue = new();
    // Infinite here so a large file upload is not cut off; per-call timeouts are supplied
    // via CancellationToken instead (3 s for log posts, 60 s for file uploads).
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Timer timer;
    // Cancelled first thing in Dispose, and the only disposed flag: every entry point checks it, and
    // every timer post and upload is linked to it, so a post or upload in flight aborts at once
    // instead of holding Dispose up or racing the HttpClient being disposed underneath it. A timer
    // post the server already stored is harmless to abort: the dispose flush re-sends it from the
    // same offset and the server skips it. Never disposed itself: it owns no timer or handle.
    private readonly CancellationTokenSource disposing = new();
    private long lastSnapshotTick;
    private string? lastSnapshot;

    // Lines drained from the queue but not yet accepted by the server. Held so a
    // failed POST costs nothing: the batch goes out again on the next attempt,
    // in order. Guarded by flushGate.
    private readonly List<string> pending = new();
    private readonly object flushGate = new();
    private long backoffUntilTick;

    // A POST the server stored but answered badly (it appends before replying, and closes the
    // connection after) fails here and is retried, so the sink must be able to recognise what it
    // already holds. The pair below is that: an id for this run's line stream, and how many of its
    // lines the server has confirmed. A retry re-sends from the same offset, possibly with newer
    // lines appended, and the server writes only the tail it has not seen. Guarded by flushGate.
    private readonly string session = Guid.NewGuid().ToString("N")[..12];
    private long delivered;

    // Structured records: a second line stream, posted to /records as JSON lines. It has its own
    // offset and backoff so either endpoint failing leaves the other flowing.
    //
    // pendingRecords holds every record not yet confirmed by the server, oldest first, as UTF-8
    // without the trailing newline; there is no separate drained batch, so the byte cap bounds all
    // of it. recordsDelivered is the stream position of its head. A record dropped by the cap still
    // advances recordsDelivered, so every later record keeps the position the server expects: after
    // a post the server stored but the client scored as failed, the retry starts past the drop and
    // the server skips exactly the lines it already holds. Guarded by recordsGate, which is only
    // ever taken briefly and never while posting; Flush takes it inside flushGate.
    private readonly Queue<byte[]> pendingRecords = new();
    private readonly object recordsGate = new();
    private long pendingRecordBytes;
    private long recordsDelivered;
    private long recordsBackoffUntilTick;
    private long recordSeq;

    private const int MaxBufferedLines = 5000;
    private const int FailureBackoffMs = 30_000;
    private const long MaxBufferedRecordBytes = 16L << 20;
    // Keeps one post well inside PostTimeout on a LAN. A record too large for one post is replaced
    // by an "oversize" stub, so every record fits.
    private const int MaxRecordPostBytes = 1 << 20;
    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(3);
    // Dispose runs on plugin unload, on the framework thread: its final flush of both channels,
    // including the wait for a timer flush to let go, gets this much in total.
    private static readonly TimeSpan DisposeFlushBudget = TimeSpan.FromSeconds(3);

    // src, session, seq and kind are what a reader filters records by, and ts orders them, so a
    // caller field may not shadow any of them.
    private static readonly HashSet<string> ReservedRecordKeys = new(StringComparer.Ordinal)
        { "ts", "src", "session", "seq", "kind" };

    // Records are read with jq and by eye, never embedded in HTML, so non-ASCII text stays readable
    // instead of becoming \uXXXX. Control characters, including '\n', are still escaped, which keeps
    // one record on one line.
    private static readonly JavaScriptEncoder RecordEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    private static readonly JsonWriterOptions RecordWriterOptions = new() { Encoder = RecordEncoder };
    // Record is called from game hook detours, so no field value may make it throw. Public fields
    // are included because game structs (Vector3, value tuples) expose nothing else; NaN and the
    // infinities become the strings "NaN", "Infinity" and "-Infinity"; pointers become hex strings.
    // Anything else STJ refuses (cycles, depth past 64, throwing getters, delegates, Type) is caught
    // per value in SerializeValue.
    private static readonly JsonSerializerOptions RecordSerializerOptions = new()
    {
        Encoder = RecordEncoder,
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new HexConverter<nint>(), new HexConverter<nuint>() },
    };

    public DevTelemetry(string source, Func<bool> enabled, Func<string?> url, Action<string>? onError = null)
    {
        this.source = source;
        this.enabled = enabled;
        this.url = url;
        this.onError = onError;
        timer = new Timer(_ => FlushOnTimer(), null, 1000, 1000);
        Log("telemetry session started");
    }

    /// <summary>Whether telemetry is currently active (toggle on and an endpoint set). Never throws:
    /// it runs on the timer thread, where a throw ends the process, and inside <see cref="Record"/>,
    /// which hook detours call. A throwing <c>enabled</c> or <c>url</c> reads as inactive and is
    /// reported once.</summary>
    public bool Active
    {
        get
        {
            try
            {
                return enabled() && !string.IsNullOrWhiteSpace(url());
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref activeFailureReported, 1) == 0)
                    ReportError($"telemetry settings threw: {e.Message}");
                return false;
            }
        }
    }

    private int activeFailureReported;

    /// <summary>This instance's id: the <c>session</c> of every record, and the stream id the
    /// server dedupes retried batches by.</summary>
    public string Session => session;

    /// <summary>Queue a log line (no-op when inactive or disposed). A line holding <c>'\n'</c> is
    /// queued as one line per part, each with the time and source prefix, and a trailing
    /// <c>'\r'</c> on a part is dropped. Safe to call from the framework thread.</summary>
    public void Log(string line)
    {
        if (disposing.IsCancellationRequested || !Active) return;
        var prefix = $"{DateTime.Now:HH:mm:ss.fff} [{source}] ";
        // The server splits a /log body on '\n' alone and gives each line one stream position; one
        // queue entry per part keeps the offset this client sends equal to the server's count.
        foreach (var part in (line ?? "").Split('\n'))
            queue.Enqueue(prefix + part.TrimEnd('\r'));
        // Bound memory while the server is down; oldest lines go first.
        while (queue.Count > MaxBufferedLines && queue.TryDequeue(out _)) { }
    }

    /// <summary>Call every frame; invokes <paramref name="build"/> at most once per
    /// <paramref name="intervalMs"/> and queues the result only when it differs from the last one
    /// queued. A window left open on an idle game costs nothing, so the trace holds transitions
    /// rather than a heartbeat. Build reads game state, so it runs on the calling thread.</summary>
    public void Snapshot(Func<string> build, int intervalMs = 1000)
    {
        if (disposing.IsCancellationRequested || !Active) return;
        var now = Environment.TickCount64;
        if (now - lastSnapshotTick < intervalMs) return;
        lastSnapshotTick = now;
        try
        {
            var s = build();
            if (s == lastSnapshot) return;
            lastSnapshot = s;
            Log(s);
        }
        catch { /* never let telemetry break the loop */ }
    }

    /// <summary>Queue one structured record (no-op when inactive or disposed): a single JSON object
    /// with <c>ts</c> (local time with offset, invariant culture), <c>src</c> (this instance's
    /// source), <c>session</c> (<see cref="Session"/>), <c>seq</c> (1, 2, … per instance) and
    /// <c>kind</c>, followed by every entry of <paramref name="fields"/> in enumeration order; a null
    /// <paramref name="fields"/> counts as empty. The server appends it to <c>records.jsonl</c>.
    /// Safe to call from any thread, including game hook detours; serialisation runs on the calling
    /// thread.
    /// <para>Values are serialised by their runtime type, public fields included. NaN and the
    /// infinities become <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c>, and <c>nint</c> and
    /// <c>nuint</c> become <c>"0x…"</c>. A value that still cannot be serialised (a cycle, nesting
    /// past 64, a throwing getter, a delegate) becomes the string
    /// <c>"!{ExceptionType}: {message}"</c>, and the rest of the record is kept. A record whose line
    /// would exceed 1 MiB is replaced by <c>{…, "kind": "oversize", "bytes": N, "of": kind}</c> with
    /// the same <c>seq</c>. If enumerating <paramref name="fields"/> throws, the record is dropped
    /// and reported to <c>onError</c> without using a <c>seq</c>.</para>
    /// <para><c>seq</c> is taken before the record joins the queue, so calls racing on two threads
    /// can reach the file out of <c>seq</c> order; sort by <c>seq</c> when order matters.</para></summary>
    /// <exception cref="ArgumentException"><paramref name="fields"/> holds <c>ts</c>, <c>src</c>,
    /// <c>session</c>, <c>seq</c> or <c>kind</c>.</exception>
    public void Record(string kind, IReadOnlyDictionary<string, object?> fields)
    {
        if (disposing.IsCancellationRequested || !Active) return;
        var ts = DateTimeOffset.Now;

        byte[] body;
        string? reservedKey;
        try
        {
            body = SerializeFields(fields, out reservedKey);
        }
        catch (Exception e)
        {
            ReportError($"record '{kind}' dropped: {e.GetType().Name}: {e.Message}");
            return;
        }
        if (reservedKey != null)
            throw new ArgumentException($"'{reservedKey}' is a reserved record key", nameof(fields));

        // Nothing after this point can fail, so every seq taken reaches the queue.
        long seq = Interlocked.Increment(ref recordSeq);
        var line = RecordLine(ts, seq, kind, body);
        if (line.Length + 1 > MaxRecordPostBytes)
        {
            var stub = new Dictionary<string, object?> { ["bytes"] = line.Length, ["of"] = kind };
            line = RecordLine(ts, seq, "oversize", SerializeFields(stub, out _));
        }

        lock (recordsGate)
        {
            pendingRecords.Enqueue(line);
            pendingRecordBytes += line.Length + 1;
            // Bound memory while the server is down; oldest records go first.
            while (pendingRecordBytes > MaxBufferedRecordBytes && pendingRecords.TryDequeue(out var dropped))
            {
                pendingRecordBytes -= dropped.Length + 1;
                recordsDelivered++;
            }
        }
    }

    /// <summary>The caller's fields as one JSON object, or an empty array with
    /// <paramref name="reservedKey"/> set when a key is reserved. Only enumerating
    /// <paramref name="fields"/> can throw: every value is serialised on its own by
    /// <see cref="SerializeValue"/>, and the writer replaces invalid UTF-16 in keys.</summary>
    private static byte[] SerializeFields(IReadOnlyDictionary<string, object?>? fields, out string? reservedKey)
    {
        reservedKey = null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, RecordWriterOptions))
        {
            w.WriteStartObject();
            if (fields != null)
            {
                foreach (var (key, value) in fields)
                {
                    if (ReservedRecordKeys.Contains(key))
                    {
                        reservedKey = key;
                        return [];
                    }
                    w.WritePropertyName(key);
                    w.WriteRawValue(SerializeValue(value), skipInputValidation: true);
                }
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>One value as JSON. Serialising into a buffer of its own means a value that throws
    /// halfway leaves no partial output in the record, so it can be replaced by its error.</summary>
    private static byte[] SerializeValue(object? value)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, RecordSerializerOptions);
        }
        catch (Exception e)
        {
            return JsonSerializer.SerializeToUtf8Bytes($"!{e.GetType().Name}: {e.Message}", RecordSerializerOptions);
        }
    }

    /// <summary>The record line: the five reserved keys, then the members of
    /// <paramref name="fields"/> (a serialised JSON object). Writes only strings and a number, which
    /// cannot fail.</summary>
    private byte[] RecordLine(DateTimeOffset ts, long seq, string kind, byte[] fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, RecordWriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("ts", ts.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            w.WriteString("src", source);
            w.WriteString("session", session);
            w.WriteNumber("seq", seq);
            w.WriteString("kind", kind);
            w.WriteEndObject();
        }
        // Splice: the header without its closing '}', then the fields object without its opening '{'
        // (which brings its own '}'), joined by ',' unless the fields object is "{}".
        var head = buffer.WrittenSpan[..^1];
        bool empty = fields.Length <= 2;
        var line = new byte[head.Length + (empty ? 1 : fields.Length)];
        head.CopyTo(line);
        if (empty)
        {
            line[^1] = (byte)'}';
        }
        else
        {
            line[head.Length] = (byte)',';
            fields.AsSpan(1).CopyTo(line.AsSpan(head.Length + 1));
        }
        return line;
    }

    /// <summary>Writes a pointer-sized integer as <c>"0x…"</c>, the form addresses are read in.</summary>
    private sealed class HexConverter<T> : JsonConverter<T> where T : IFormattable
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue("0x" + value.ToString("X", CultureInfo.InvariantCulture));
    }

    /// <summary>Upload one whole artefact (a capture, a struct dump) to the devlog server's
    /// <c>POST /file</c>, tagged with this instance's <paramref name="source"/>. Unlike
    /// <see cref="Log"/> and <see cref="Snapshot"/>, this deliberately ignores
    /// <c>enabled()</c>: it is only ever called from an explicit user action (e.g. "save and
    /// upload"), not from the per-frame log path, so there is nothing to gate.</summary>
    /// <returns>The server-side path the artefact landed at.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> fired, 60 s passed, or this
    /// instance was disposed mid-upload.</exception>
    /// <exception cref="HttpRequestException">The server refused the artefact (e.g. <c>413</c> over
    /// its <c>MAX_DUMP_BYTES</c>) or could not be reached.</exception>
    public async Task<string> UploadFileAsync(string name, string ext, byte[] body, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposing.IsCancellationRequested, this);
        var logUrl = url();
        if (string.IsNullOrWhiteSpace(logUrl)) throw new InvalidOperationException("no devlog URL configured");

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, disposing.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await http.PostAsync(FileUrl(logUrl!, source, name, ext), content, cts.Token).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode}: {respBody.Trim()}");
        return respBody.Trim();
    }

    /// <summary>Builds the <c>/file</c> URL from the configured <c>/log</c> URL's origin, carrying
    /// its query string over so <c>client=</c> (see devlog_server.py) still attributes the upload to
    /// the same machine as the log lines. The server names the artefact from <paramref name="plugin"/>,
    /// <paramref name="name"/> and <paramref name="ext"/> (devlog_server.py's <c>_save_file</c>).</summary>
    private static string FileUrl(string logUrl, string plugin, string name, string ext)
    {
        var uri = new Uri(logUrl);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var existingQuery = uri.Query.TrimStart('?');
        var suffix = $"plugin={Uri.EscapeDataString(plugin)}&name={Uri.EscapeDataString(name)}&ext={Uri.EscapeDataString(ext)}";
        return $"{origin}/file?{(existingQuery.Length > 0 ? existingQuery + "&" : "")}{suffix}";
    }

    /// <summary>The <c>/records</c> URL on the configured <c>/log</c> URL's origin, keeping its
    /// query string for the same attribution <see cref="FileUrl"/> keeps.</summary>
    private static string RecordsUrl(string logUrl)
    {
        var uri = new Uri(logUrl);
        return $"{uri.GetLeftPart(UriPartial.Authority)}/records{uri.Query}";
    }

    /// <summary>The timer's flush. Skipped while another flush holds the gate: the timer fires on a
    /// pool thread and a post can outlive its period, and skipping keeps each batch in order.</summary>
    private void FlushOnTimer()
    {
        if (disposing.IsCancellationRequested || !Active) return;
        if (!Monitor.TryEnter(flushGate)) return;
        try
        {
            // Dispose can run between the check above and taking the gate, and once it has, the
            // HttpClient may be gone.
            if (disposing.IsCancellationRequested) return;
            // Each channel is drained and posted on its own, so a failing endpoint only costs
            // the other one the time the failed post took.
            FlushLines(false, disposing.Token);
            FlushRecords(false, disposing.Token);
        }
        finally
        {
            Monitor.Exit(flushGate);
        }
    }

    /// <summary>Sends one post, throwing on failure or a non-success status. Gives up after
    /// <see cref="PostTimeout"/>, or as soon as <paramref name="stop"/> fires.</summary>
    private void Send(HttpRequestMessage req, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(PostTimeout);
        using var resp = http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Hands a failure to <c>onError</c>. That is caller code running on the timer thread,
    /// where a throw would end the process, or inside Dispose, where it would cut cleanup short, so
    /// whatever it throws is dropped.</summary>
    private void ReportError(string message)
    {
        try
        {
            onError?.Invoke(message);
        }
        catch
        {
            /* a throwing onError must not reach the timer thread or Dispose */
        }
    }

    /// <summary>Posts the plain lines to <c>/log</c>. Caller holds <see cref="flushGate"/>.</summary>
    /// <param name="force">Ignore the post-failure backoff (the dispose flush).</param>
    /// <param name="stop">Aborts the post: <see cref="disposing"/> for the timer, the dispose
    /// budget for the dispose flush.</param>
    private void FlushLines(bool force, CancellationToken stop)
    {
        if (!force && Environment.TickCount64 < Volatile.Read(ref backoffUntilTick)) return;
        while (queue.TryDequeue(out var l)) pending.Add(l);
        if (pending.Count == 0) return;
        int excess = pending.Count - MaxBufferedLines;
        if (excess > 0)
        {
            // A dropped line still holds its stream position: after a post the server stored but
            // the client scored as failed, the retry's offset must still skip what the server holds.
            pending.RemoveRange(0, excess);
            delivered += excess;
        }

        var sb = new StringBuilder();
        foreach (var l in pending) sb.Append(l).Append('\n');
        int count = pending.Count;
        try
        {
            using var content = new StringContent(sb.ToString(), Encoding.UTF8, "text/plain");
            using var req = new HttpRequestMessage(HttpMethod.Post, url()) { Content = content };
            req.Headers.TryAddWithoutValidation("X-Devlog-Session", session);
            req.Headers.TryAddWithoutValidation("X-Devlog-Offset", delivered.ToString(CultureInfo.InvariantCulture));
            Send(req, stop);
            delivered += count;
            pending.Clear();
        }
        catch (Exception e)
        {
            // Keep the batch for the next attempt, and stop retrying every
            // second: the lines that explain why the sink went down are the
            // ones least worth dropping, and a dead endpoint costs a 3s
            // timeout per attempt.
            Volatile.Write(ref backoffUntilTick, Environment.TickCount64 + FailureBackoffMs);
            // A timer post aborted by Dispose is not a sink failure; the dispose flush reports its own.
            if (force || !disposing.IsCancellationRequested) ReportError(e.Message);
        }
    }

    /// <summary>Posts the records queued when it starts to <c>/records</c>, in chunks of at most
    /// <see cref="MaxRecordPostBytes"/>, until they are delivered or a post fails. Records queued
    /// meanwhile wait for the next flush, so a steady stream of them cannot hold the gate and starve
    /// <see cref="FlushLines"/> or Dispose. Caller holds <see cref="flushGate"/>.</summary>
    /// <param name="force">Ignore the post-failure backoff (the dispose flush).</param>
    /// <param name="stop">As for <see cref="FlushLines"/>.</param>
    private void FlushRecords(bool force, CancellationToken stop)
    {
        if (!force && Environment.TickCount64 < recordsBackoffUntilTick) return;
        // A stream position, not a count: a record dropped by the cap during a post advances
        // recordsDelivered, and the queue at the start ends at this position either way.
        long queuedEnd;
        lock (recordsGate) queuedEnd = recordsDelivered + pendingRecords.Count;
        var chunk = new List<byte[]>();
        while (true)
        {
            chunk.Clear();
            long offset;
            int size = 0;
            lock (recordsGate)
            {
                offset = recordsDelivered;
                // recordsDelivered < queuedEnd implies at least queuedEnd - offset records queued.
                if (offset >= queuedEnd) return;
                foreach (var line in pendingRecords)
                {
                    if (offset + chunk.Count >= queuedEnd) break;
                    if (chunk.Count > 0 && size + line.Length + 1 > MaxRecordPostBytes) break;
                    chunk.Add(line);
                    size += line.Length + 1;
                }
            }

            var body = new byte[size];
            int at = 0;
            foreach (var line in chunk)
            {
                line.CopyTo(body, at);
                at += line.Length;
                body[at++] = (byte)'\n';
            }

            try
            {
                using var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
                using var req = new HttpRequestMessage(HttpMethod.Post, RecordsUrl(url()!)) { Content = content };
                // The server keys its dedupe offsets by session, and /log already uses the bare id
                // for the plain line stream, so the record stream needs an id of its own.
                req.Headers.TryAddWithoutValidation("X-Devlog-Session", session + "-r");
                req.Headers.TryAddWithoutValidation("X-Devlog-Offset", offset.ToString(CultureInfo.InvariantCulture));
                Send(req, stop);
            }
            catch (Exception e)
            {
                // Same reasoning as the /log channel: keep the records, back off, retry later.
                recordsBackoffUntilTick = Environment.TickCount64 + FailureBackoffMs;
                if (force || !disposing.IsCancellationRequested) ReportError(e.Message);
                return;
            }

            // Records dropped by the cap during the post already advanced recordsDelivered, so only
            // the part of this chunk still at the head is removed.
            var sentEnd = offset + chunk.Count;
            lock (recordsGate)
            {
                while (recordsDelivered < sentEnd && pendingRecords.TryDequeue(out var sent))
                {
                    pendingRecordBytes -= sent.Length + 1;
                    recordsDelivered++;
                }
            }
        }
    }

    /// <summary>Stops the timer and makes one last attempt to deliver both channels, including a
    /// record queued just before it. A timer flush in flight is aborted rather than waited for, and
    /// the last attempt (waiting for the gate, then every chunk of both channels) gets
    /// <see cref="DisposeFlushBudget"/> in total, so a dead or stalled endpoint costs unload at most
    /// that. Afterwards every entry point is a no-op.</summary>
    public void Dispose()
    {
        if (disposing.IsCancellationRequested) return;
        disposing.Cancel();
        timer.Dispose();
        using (var budget = new CancellationTokenSource(DisposeFlushBudget))
        {
            if (Active && Monitor.TryEnter(flushGate, DisposeFlushBudget))
            {
                try
                {
                    FlushLines(true, budget.Token);
                    FlushRecords(true, budget.Token);
                }
                finally
                {
                    Monitor.Exit(flushGate);
                }
            }
        }
        http.Dispose();
    }
}
