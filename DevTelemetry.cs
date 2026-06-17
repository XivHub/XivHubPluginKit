using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace ZhyraPluginKit;

/// <summary>
/// Plugin-agnostic live-log / telemetry uploader for local dev. Buffers lines and POSTs them to a
/// mini log server on a background timer. Inert unless <c>enabled()</c> is true AND <c>url()</c> is
/// non-empty, so it is safe to ship in Release builds (dormant for normal users).
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\ZhyraPluginKit\DevTelemetry.cs" Link="Dev\DevTelemetry.cs" /&gt;
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
        while (queue.Count > 5000 && queue.TryDequeue(out _)) { } // bound memory if the server is down
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

    private void Flush()
    {
        if (!Active || queue.IsEmpty) return;
        var sb = new StringBuilder();
        while (queue.TryDequeue(out var l)) sb.Append(l).Append('\n');
        if (sb.Length == 0) return;
        try
        {
            using var content = new StringContent(sb.ToString(), Encoding.UTF8, "text/plain");
            using var resp = http.PostAsync(url(), content).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            onError?.Invoke(e.Message);
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        Flush();
        http.Dispose();
    }
}
