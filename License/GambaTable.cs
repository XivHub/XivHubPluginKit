using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XivHubPluginKit.License;

/// <summary>
/// A live table on gamba.xivhub.net: the socket a dealer plugin streams a game's public events into,
/// and the room code watchers use. The server checks the character's license when the table opens
/// and every minute after; a refusal ends the table, anything else reconnects with backoff and asks
/// for the same room code back. Protocol: <c>gamba-server/src/table/mod.rs</c>.
/// </summary>
/// <para>
/// BCL only, so it runs (and is tested) outside the game. <see cref="Send"/> may be called from any
/// thread; events sent while reconnecting wait in a bounded queue and go out in order.
/// </para>
public sealed class GambaTable : IDisposable
{
    /// <summary>Events held while the socket is down; older ones are dropped past this.</summary>
    private const int MaxQueued = 1000;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Uri endpoint;
    private readonly ConcurrentQueue<(string Player, string Url)> links = new();
    private readonly ConcurrentQueue<string> watchers = new();
    private readonly ConcurrentQueue<(string Player, JsonElement Action)> actions = new();
    private readonly string helloToken;
    private readonly string cidHash;
    private readonly string game;
    private readonly ConcurrentQueue<string> outbox = new();
    private readonly SemaphoreSlim wake = new(0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    /// <summary>The code watchers use, once the server gave one; kept across reconnects.</summary>
    public string? Room { get; private set; }

    /// <summary>The customer's public page (their name, schedule and open tables), when they have one.</summary>
    public string? Page { get; private set; }

    /// <summary>Where the table stands, in words for the dealer.</summary>
    public string Status { get; private set; } = "Connecting to the live table.";

    /// <summary>The server refused the table; it stays closed.</summary>
    public bool Refused { get; private set; }

    public string Game => game;

    /// <param name="server">The gamba server's https origin, as <see cref="GambaLicense"/> uses it.</param>
    public GambaTable(string server, string token, string cidHash, string game)
    {
        var uri = new Uri(server.TrimEnd('/'));
        endpoint = new UriBuilder(uri) { Scheme = uri.Scheme == "http" ? "ws" : "wss", Path = "/ws/table" }.Uri;
        helloToken = token;
        this.cidHash = cidHash;
        this.game = game;
        loop = Task.Run(RunAsync);
    }

    /// <summary>Queues one event, an object the server's schema knows (it is serialised as JSON).</summary>
    public void Send(object evt)
    {
        if (Refused) return;
        outbox.Enqueue(JsonSerializer.Serialize(evt));
        while (outbox.Count > MaxQueued) outbox.TryDequeue(out _);
        wake.Release();
    }

    /// <summary>Asks the server for <paramref name="player"/>'s private link; it arrives through <see cref="TryTakeLink"/>.</summary>
    public void RequestLink(string player) => Send(new { kind = "link", player });

    /// <summary>A link the server made, ready to send to its player: a short URL that finds this dealer's table, now and later.</summary>
    public bool TryTakeLink(out (string Player, string Url) link) => links.TryDequeue(out link);

    /// <summary>
    /// A player who just opened their link. Their private stream starts now: send their bank and recent journal
    /// with <see cref="SendPrivate"/>, then each change.
    /// </summary>
    public bool TryTakeWatcher(out string player) => watchers.TryDequeue(out player!);

    /// <summary>
    /// Something a player did from their own link (a bet, a move): the server names the player from the link, and
    /// the action is in gamba-server's closed schema (<c>table/events.rs</c> <c>PlayerAction</c>).
    /// </summary>
    public bool TryTakeAction(out (string Player, JsonElement Action) action) => actions.TryDequeue(out action);

    /// <summary>One event for <paramref name="player"/>'s private link only (their bank, a journal entry).</summary>
    public void SendPrivate(string player, object evt) => Send(new { kind = "private", player, @event = evt });

    private string Hello() => JsonSerializer.Serialize(new
    {
        token = helloToken,
        cid_hash = cidHash,
        game,
        room = Room,
    });

    private async Task RunAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                // A connection that dies without a close (a router dropping it) shows only as pings going unanswered.
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                await ws.ConnectAsync(endpoint, stop.Token);
                await SendText(ws, Hello());
                using var reply = JsonDocument.Parse(await ReceiveText(ws) ?? "{}");
                var root = reply.RootElement;
                string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type == "refused")
                {
                    Refuse(root);
                    return;
                }
                if (type != "room") throw new InvalidOperationException($"unexpected first reply \"{type}\"");
                Room = root.GetProperty("code").GetString();
                Page = root.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.String ? page.GetString() : null;
                Status = $"Live as table {Room}.";
                backoff = TimeSpan.FromSeconds(1);
                await PumpAsync(ws);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Status = $"Live table offline ({e.Message}); retrying.";
            }
            if (Refused) return;
            try
            {
                await Task.Delay(backoff, stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>Sends queued events and reads the server's replies until the socket drops or the table is refused.</summary>
    private async Task PumpAsync(ClientWebSocket ws)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var reader = Task.Run(async () =>
        {
            while (ws.State == WebSocketState.Open)
            {
                var text = await ReceiveText(ws, linked.Token);
                if (text is null) break;
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type == "refused")
                {
                    Refuse(root);
                    break;
                }
                if (type == "link")
                    links.Enqueue((root.GetProperty("player").GetString() ?? "", root.GetProperty("url").GetString() ?? ""));
                if (type == "watching")
                    watchers.Enqueue(root.GetProperty("player").GetString() ?? "");
                if (type == "act")
                    actions.Enqueue((root.GetProperty("player").GetString() ?? "", root.GetProperty("action").Clone()));
                if (type == "error")
                    Status = $"Live as table {Room}; the server refused an event: {root.GetProperty("message").GetString()}";
            }
            linked.Cancel();
        });
        try
        {
            while (!linked.IsCancellationRequested)
            {
                while (outbox.TryPeek(out var next))
                {
                    await SendText(ws, next, linked.Token);
                    outbox.TryDequeue(out _);
                }
                await wake.WaitAsync(linked.Token);
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // The reader saw the socket close or a refusal.
        }
        await reader;
        if (stop.IsCancellationRequested && ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "table closed", CancellationToken.None);
    }

    private void Refuse(JsonElement root)
    {
        Refused = true;
        Status = root.TryGetProperty("message", out var m) ? m.GetString() ?? "The live table was refused." : "The live table was refused.";
    }

    private static Task SendText(ClientWebSocket ws, string text, CancellationToken token = default) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token);

    /// <summary>One whole text message, or null once the socket closes.</summary>
    private static async Task<string?> ReceiveText(ClientWebSocket ws, CancellationToken token = default)
    {
        // Bytes first: a frame boundary can split a multi-byte character.
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        wake.Release();
        bool ended;
        try
        {
            ended = loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            ended = true;
        }
        // A loop still closing its socket keeps using these; they are left to the collector then.
        if (!ended) return;
        stop.Dispose();
        wake.Dispose();
    }
}
