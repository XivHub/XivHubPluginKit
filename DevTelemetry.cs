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
    // Cancelled by Dispose, so an upload still in flight ends as a cancellation instead of racing
    // the HttpClient being disposed underneath it. Never disposed itself: it owns no timer or handle.
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
    // Keeps one post well inside the 3 s timeout on a LAN.
    private const int MaxRecordPostBytes = 1 << 20;

    // src, session, seq and kind are what a reader filters records by, and ts orders them, so a
    // caller field may not shadow any of them.
    private static readonly HashSet<string> ReservedRecordKeys = new(StringComparer.Ordinal)
        { "ts", "src", "session", "seq", "kind" };

    // Records are read with jq and by eye, never embedded in HTML, so non-ASCII text stays readable
    // instead of becoming \uXXXX. Control characters, including '\n', are still escaped, which keeps
    // one record on one line.
    private static readonly JavaScriptEncoder RecordEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    private static readonly JsonWriterOptions RecordWriterOptions = new() { Encoder = RecordEncoder };
    private static readonly JsonSerializerOptions RecordSerializerOptions = new() { Encoder = RecordEncoder };

    public DevTelemetry(string source, Func<bool> enabled, Func<string?> url, Action<string>? onError = null)
    {
        this.source = source;
        this.enabled = enabled;
        this.url = url;
        this.onError = onError;
        timer = new Timer(_ => Flush(), null, 1000, 1000);
        Log("telemetry session started");
    }

    /// <summary>Whether telemetry is currently active (toggle on and an endpoint set).</summary>
    public bool Active => enabled() && !string.IsNullOrWhiteSpace(url());

    /// <summary>This instance's id: the <c>session</c> of every record, and the stream id the
    /// server dedupes retried batches by.</summary>
    public string Session => session;

    /// <summary>Queue a log line (no-op when inactive). Safe to call from the framework thread.</summary>
    public void Log(string line)
    {
        if (!Active) return;
        queue.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [{source}] {line}");
        // Bound memory while the server is down; oldest lines go first.
        while (queue.Count > MaxBufferedLines && queue.TryDequeue(out _)) { }
    }

    /// <summary>Call every frame; invokes <paramref name="build"/> at most once per
    /// <paramref name="intervalMs"/> and queues the result only when it differs from the last one
    /// queued. A window left open on an idle game costs nothing, so the trace holds transitions
    /// rather than a heartbeat. Build reads game state, so it runs on the calling thread.</summary>
    public void Snapshot(Func<string> build, int intervalMs = 1000)
    {
        if (!Active) return;
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

    /// <summary>Queue one structured record (no-op when inactive): a single JSON object with
    /// <c>ts</c> (local time with offset, invariant culture), <c>src</c> (this instance's source),
    /// <c>session</c> (<see cref="Session"/>), <c>seq</c> (1, 2, … per instance) and
    /// <c>kind</c>, followed by every entry of <paramref name="fields"/> in enumeration order.
    /// Values are serialised by their runtime type. The server appends it to <c>records.jsonl</c>.
    /// Safe to call from any thread; serialisation runs on the calling thread.
    /// <para><c>seq</c> is taken before the record joins the queue, so calls racing on two threads
    /// can reach the file out of <c>seq</c> order; sort by <c>seq</c> when order matters.</para></summary>
    /// <exception cref="ArgumentException"><paramref name="fields"/> holds <c>ts</c>, <c>src</c>,
    /// <c>session</c>, <c>seq</c> or <c>kind</c>.</exception>
    /// <exception cref="NotSupportedException">A field value's type cannot be serialised.</exception>
    public void Record(string kind, IReadOnlyDictionary<string, object?> fields)
    {
        if (!Active) return;
        foreach (var key in fields.Keys)
        {
            if (ReservedRecordKeys.Contains(key))
                throw new ArgumentException($"'{key}' is a reserved record key", nameof(fields));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, RecordWriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("ts", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            w.WriteString("src", source);
            w.WriteString("session", session);
            w.WriteNumber("seq", Interlocked.Increment(ref recordSeq));
            w.WriteString("kind", kind);
            foreach (var (key, value) in fields)
            {
                w.WritePropertyName(key);
                JsonSerializer.Serialize(w, value, RecordSerializerOptions);
            }
            w.WriteEndObject();
        }
        var line = buffer.WrittenSpan.ToArray();

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

    /// <param name="force">Ignore the post-failure backoff and wait for a flush already in
    /// flight. Used on dispose, where this is the last chance to deliver whatever is buffered,
    /// including a record queued just before it.</param>
    private void Flush(bool force = false)
    {
        if (!Active) return;
        // The timer fires on a pool thread and a POST can outlive its period, so
        // two flushes can overlap. Skipping the second keeps the batch in order.
        if (force) Monitor.Enter(flushGate);
        else if (!Monitor.TryEnter(flushGate)) return;
        try
        {
            // Each channel is drained and posted on its own, so a failing endpoint only costs
            // the other one the time the failed post took.
            FlushLines(force);
            FlushRecords(force);
        }
        finally
        {
            Monitor.Exit(flushGate);
        }
    }

    /// <summary>Posts the plain lines to <c>/log</c>. Caller holds <see cref="flushGate"/>.</summary>
    private void FlushLines(bool force)
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
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
            onError?.Invoke(e.Message);
        }
    }

    /// <summary>Posts the buffered records to <c>/records</c> in chunks of at most
    /// <see cref="MaxRecordPostBytes"/> (a single larger record goes alone), until none are left or
    /// a post fails. Caller holds <see cref="flushGate"/>.</summary>
    private void FlushRecords(bool force)
    {
        if (!force && Environment.TickCount64 < recordsBackoffUntilTick) return;
        var chunk = new List<byte[]>();
        while (true)
        {
            chunk.Clear();
            long offset;
            int size = 0;
            lock (recordsGate)
            {
                if (pendingRecords.Count == 0) return;
                offset = recordsDelivered;
                foreach (var line in pendingRecords)
                {
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var resp = http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
            }
            catch (Exception e)
            {
                // Same reasoning as the /log channel: keep the records, back off, retry later.
                recordsBackoffUntilTick = Environment.TickCount64 + FailureBackoffMs;
                onError?.Invoke(e.Message);
                return;
            }

            // Records dropped by the cap during the post already advanced recordsDelivered, so only
            // the part of this chunk still at the head is removed.
            var end = offset + chunk.Count;
            lock (recordsGate)
            {
                while (recordsDelivered < end && pendingRecords.TryDequeue(out var sent))
                {
                    pendingRecordBytes -= sent.Length + 1;
                    recordsDelivered++;
                }
            }
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        Flush(force: true);
        disposing.Cancel();
        http.Dispose();
    }
}
