using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XivHubPluginKit.License;

/// <summary>A plugin's save as gamba.xivhub.net kept it.</summary>
public sealed record SavedCopy(DateTimeOffset SavedAt, string Json);

/// <summary>The server refused or could not be reached; <see cref="Exception.Message"/> is for the dealer.</summary>
public sealed class GambaSaveException(string message) : Exception(message);

/// <summary>
/// A copy of a licensed plugin's own save on gamba.xivhub.net, one per character, so a dead PC or a reinstall
/// gets it back. <see cref="Put"/> hands over the latest save; a background loop uploads it, at most once every
/// <see cref="MinGap"/>, always the newest and never a stale one after it, and retries with backoff while the server
/// is down. <see cref="FetchAsync"/> reads the copy back. Server side: <c>gamba-server/src/save_api.rs</c>.
/// </summary>
/// <para>BCL only, so it runs (and is tested) outside the game. Every member may be called from any thread.</para>
public sealed class GambaSave : IDisposable
{
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FlushOnDispose = TimeSpan.FromSeconds(3);

    private readonly string server;
    private readonly string token;
    private readonly string plugin;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim wake = new(0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private string? pending;

    /// <summary>The character whose copy this is.</summary>
    public string CidHash { get; }

    /// <summary>When the server last took a save from this install, or null before the first.</summary>
    public DateTimeOffset? LastSaved { get; private set; }

    /// <summary>Why the last upload failed, or null after one that worked.</summary>
    public string? Problem { get; private set; }

    /// <param name="server">The gamba server's https origin, as <see cref="GambaLicense"/> uses it.</param>
    /// <param name="plugin">The plugin's internal name: each plugin keeps its own copy.</param>
    public GambaSave(string server, string token, string cidHash, string plugin)
    {
        this.server = server.TrimEnd('/');
        this.token = token;
        this.plugin = plugin;
        CidHash = cidHash;
        loop = Task.Run(RunAsync);
    }

    /// <summary>Queues <paramref name="json"/> (one JSON value) as the latest save; any older one not yet sent is dropped.</summary>
    public void Put(string json)
    {
        Interlocked.Exchange(ref pending, json);
        if (wake.CurrentCount == 0) wake.Release();
    }

    /// <summary>The copy the server holds for this character, or null when it holds none.</summary>
    /// <exception cref="GambaSaveException">The server refused, or could not be reached.</exception>
    public async Task<SavedCopy?> FetchAsync(CancellationToken cancel = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync($"{server}/api/save/fetch", Body(null), cancel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new GambaSaveException($"gamba.xivhub.net is unreachable: {e.Message}");
        }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancel);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new GambaSaveException(ErrorOf(text, response.StatusCode));
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                return new SavedCopy(
                    DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("saved_at").GetInt64()),
                    root.GetProperty("data").GetRawText());
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new GambaSaveException($"gamba.xivhub.net answered with something that is not a save: {e.Message}");
            }
        }
    }

    private async Task RunAsync()
    {
        var backoff = MinGap;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            string? json = Interlocked.Exchange(ref pending, null);
            if (json == null) continue;
            bool retry = await UploadAsync(json, stop.Token);
            if (retry)
            {
                // Put it back unless a newer save arrived meanwhile, and try again after the backoff.
                Interlocked.CompareExchange(ref pending, json, null);
                if (wake.CurrentCount == 0) wake.Release();
                await Delay(backoff);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
            else
            {
                backoff = MinGap;
                await Delay(MinGap);
            }
        }
    }

    private async Task Delay(TimeSpan span)
    {
        try
        {
            await Task.Delay(span, stop.Token);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Sends one save; true when it should be sent again later (the server was unreachable or failed).</summary>
    private async Task<bool> UploadAsync(string json, CancellationToken cancel)
    {
        try
        {
            using var response = await http.PutAsync($"{server}/api/save", Body(json), cancel);
            if (response.IsSuccessStatusCode)
            {
                LastSaved = DateTimeOffset.Now;
                Problem = null;
                return false;
            }
            Problem = ErrorOf(await response.Content.ReadAsStringAsync(cancel), response.StatusCode);
            // A refusal (no license, too large) stands until the next save; a server error is worth retrying.
            return (int)response.StatusCode >= 500;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Problem = $"gamba.xivhub.net is unreachable: {e.Message}";
            return true;
        }
    }

    /// <summary>The request body: who is asking and, for an upload, the save itself, which is already JSON.</summary>
    private StringContent Body(string? data)
    {
        var sb = new StringBuilder();
        sb.Append("{\"token\":").Append(JsonSerializer.Serialize(token))
            .Append(",\"cid_hash\":").Append(JsonSerializer.Serialize(CidHash))
            .Append(",\"plugin\":").Append(JsonSerializer.Serialize(plugin));
        if (data != null) sb.Append(",\"data\":").Append(data);
        sb.Append('}');
        return new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
    }

    private static string ErrorOf(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException) { }
        return $"gamba.xivhub.net refused the save ({(int)status}).";
    }

    /// <summary>Stops the loop, first sending a save still waiting, for up to three seconds.</summary>
    public void Dispose()
    {
        stop.Cancel();
        try
        {
            loop.Wait(FlushOnDispose);
        }
        catch (AggregateException) { }
        if (Interlocked.Exchange(ref pending, null) is { } json)
        {
            using var cancel = new CancellationTokenSource(FlushOnDispose);
            try
            {
                // Off the caller's thread, so a synchronization context there cannot deadlock the wait.
                Task.Run(() => UploadAsync(json, cancel.Token)).Wait(FlushOnDispose);
            }
            catch (AggregateException) { }
        }
        stop.Dispose();
        wake.Dispose();
        http.Dispose();
    }
}
