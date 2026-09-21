using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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

    private const int MaxBufferedLines = 5000;
    private const int FailureBackoffMs = 30_000;

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

    /// <param name="force">Ignore the post-failure backoff. Used on dispose, where
    /// this is the last chance to deliver whatever is buffered.</param>
    private void Flush(bool force = false)
    {
        if (!Active) return;
        if (!force && Environment.TickCount64 < Volatile.Read(ref backoffUntilTick)) return;
        // The timer fires on a pool thread and a POST can outlive its period, so
        // two flushes can overlap. Skipping the second keeps the batch in order.
        if (!Monitor.TryEnter(flushGate)) return;
        try
        {
            while (queue.TryDequeue(out var l)) pending.Add(l);
            if (pending.Count == 0) return;
            int excess = pending.Count - MaxBufferedLines;
            if (excess > 0) pending.RemoveRange(0, excess);

            var sb = new StringBuilder();
            foreach (var l in pending) sb.Append(l).Append('\n');
            try
            {
                using var content = new StringContent(sb.ToString(), Encoding.UTF8, "text/plain");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var resp = http.PostAsync(url(), content, cts.Token).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
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
        finally
        {
            Monitor.Exit(flushGate);
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
