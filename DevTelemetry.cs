using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;

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
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Timer timer;
    private long lastSnapshotTick;

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

    /// <summary>Call every frame; invokes <paramref name="build"/> and queues the result at most once
    /// per <paramref name="intervalMs"/>. Build reads game state, so it runs on the calling thread.</summary>
    public void Snapshot(Func<string> build, int intervalMs = 1000)
    {
        if (!Active) return;
        var now = Environment.TickCount64;
        if (now - lastSnapshotTick < intervalMs) return;
        lastSnapshotTick = now;
        try { Log(build()); } catch { /* never let telemetry break the loop */ }
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
                using var resp = http.PostAsync(url(), content).GetAwaiter().GetResult();
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
        http.Dispose();
    }
}
