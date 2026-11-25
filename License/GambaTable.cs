using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XivHubPluginKit.License;

/// <summary>A web game's purchase: take <see cref="Cost"/> from the player's bank, then answer <see cref="GambaTable.Sold"/> or <see cref="GambaTable.Unsold"/>.</summary>
public sealed record WebBuy(string Sale, string Player, int Count, long Cost);

/// <summary>
/// A web game's play that pays: credit <see cref="Gil"/> to the player's bank, or owe them <see cref="Item"/> (worth
/// <see cref="Payout"/>); then answer <see cref="GambaTable.Paid"/>.
/// </summary>
public sealed record WebWon(string Session, long Nonce, string Player, long Gil, long Payout, string? Item);

/// <summary>The player's web game session ended at this plugin's <see cref="GambaTable.End"/>; <see cref="Session"/> null when they had none.</summary>
public sealed record WebEnded(string Player, string? Session);

/// <summary>
/// A live table on gamba.xivhub.net: the socket a dealer plugin streams a game's public events into,
/// and the room code watchers use. The server checks the character's license when the table opens
/// and every minute after; a refusal ends the table, anything else reconnects with backoff and asks
/// for the same room code back. Protocol: <c>gamba-server/src/table/mod.rs</c>, a web game's in <c>table/web.rs</c>.
/// </summary>
/// <para>
/// BCL only, so it runs (and is tested) outside the game. <see cref="Send"/> may be called from any
/// thread; events sent while reconnecting wait in a bounded queue and go out in order.
/// </para>
/// <para>
/// The table keeps its own copy of the latest <c>rules</c> and of the round since the last <c>bets_open</c>. When the
/// server answers with a fresh room (a new code, or the same code after a server restart), that copy goes out first
/// in place of whatever public events were waiting, so the room holds the whole round again; the dealer plugin only
/// reopens its players' private streams (<see cref="FreshRooms"/>). <see cref="Dispose"/> closes the socket with a
/// Close frame, which the server takes as the dealer stopping: the table ends at once.
/// </para>
public sealed class GambaTable : IDisposable
{
    /// <summary>Events held while the socket is down; older ones are dropped past this.</summary>
    private const int MaxQueued = 1000;
    /// <summary>A round's events rarely pass a hundred; the server bounds its own copy at 500 too.</summary>
    private const int MaxRound = 500;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Uri endpoint;
    private readonly ConcurrentQueue<(string Player, string Url)> links = new();
    private readonly ConcurrentQueue<string> watchers = new();
    private readonly ConcurrentQueue<(string Player, JsonElement Action)> actions = new();
    private readonly ConcurrentQueue<object> web = new();
    private readonly string helloToken;
    private readonly string cidHash;
    private readonly string game;
    // Guards the outbox, the rules and the round together, so a fresh room's resend never interleaves with a Send.
    private readonly object gate = new();
    private readonly ConcurrentQueue<(string Json, bool Public)> outbox = new();
    private readonly List<string> round = new();
    private string? rules;
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

    /// <summary>
    /// A web game whose license ended while this character still owed players: the table is open only to hear what
    /// it owes, and sells nothing. Set by the server's answer.
    /// </summary>
    public bool Settling { get; private set; }

    /// <summary>A settling table that owes nothing any more; the server closed it and it stays closed.</summary>
    public bool Settled { get; private set; }

    public string Game => game;

    /// <summary>
    /// How many fresh rooms the server has given this table. Each one holds no private streams: when this changes,
    /// treat every player as not watching yet, so the next <see cref="TryTakeWatcher"/> for them starts theirs again.
    /// </summary>
    public int FreshRooms => freshRooms;
    private volatile int freshRooms;

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
        string json = JsonSerializer.Serialize(evt);
        string kind = KindOf(json);
        bool isPublic = kind is not ("link" or "private" or "owed" or "sold" or "unsold" or "paid" or "end");
        lock (gate)
        {
            if (kind == "rules") rules = json;
            else if (isPublic)
            {
                if (kind == "bets_open") round.Clear();
                if (round.Count == MaxRound) round.RemoveAt(0);
                round.Add(json);
            }
            outbox.Enqueue((json, isPublic));
            while (outbox.Count > MaxQueued) outbox.TryDequeue(out _);
        }
        wake.Release();
    }

    private static string KindOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
    }

    /// <summary>A fresh room holds nothing: the rules and the round go first, then what else was waiting (links, private events).</summary>
    private void Refill()
    {
        lock (gate)
        {
            var waiting = new List<(string, bool)>();
            while (outbox.TryDequeue(out var item))
                if (!item.Public) waiting.Add(item);
            if (rules != null) outbox.Enqueue((rules, true));
            foreach (var e in round) outbox.Enqueue((e, true));
            foreach (var item in waiting) outbox.Enqueue(item);
        }
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

    /// <summary>
    /// A web game's news from the server, oldest first: a <see cref="WebBuy"/>, <see cref="WebWon"/> or
    /// <see cref="WebEnded"/>. Each buy and payout comes again on every connect until answered, so answer by its
    /// sale or (session, nonce), never by counting.
    /// </summary>
    public bool TryTakeWeb(out object message) => web.TryDequeue(out message!);

    /// <summary>The sale's cost is out of the player's bank.</summary>
    public void Sold(string sale) => Send(new { kind = "sold", sale });

    /// <summary>The sale was not made; <paramref name="reason"/> is a sentence for the player's page.</summary>
    public void Unsold(string sale, string reason) => Send(new { kind = "unsold", sale, reason });

    /// <summary>The play's payout is in the bank, or its item owed.</summary>
    public void Paid(string session, long nonce) => Send(new { kind = "paid", session, nonce });

    /// <summary>Ends the player's open session, revealing what is left; <see cref="WebEnded"/> follows its payouts.</summary>
    public void End(string player) => Send(new { kind = "end", player });

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
                Settling = root.TryGetProperty("settling", out var settling) && settling.ValueKind == JsonValueKind.True;
                Page = root.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.String ? page.GetString() : null;
                // A server that does not say is taken to hold nothing; sending the round again is harmless.
                if (!root.TryGetProperty("fresh", out var fresh) || fresh.ValueKind != JsonValueKind.False)
                {
                    Refill();
                    freshRooms++;
                }
                Status = Settling ? "Paying what players are owed; selling nothing." : $"Live as table {Room}.";
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
            if (Refused || Settled) return;
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

    /// <summary>
    /// Sends queued events and reads the server's replies until the socket drops, the table is refused, or the table
    /// stops; a stop sends what is still queued, then a Close frame.
    /// </summary>
    private async Task PumpAsync(ClientWebSocket ws)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        // The reader runs until the socket closes, not until the stop: cancelling a pending receive aborts the socket,
        // and an aborted socket cannot send its Close.
        var reader = Task.Run(async () =>
        {
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var text = await ReceiveText(ws);
                    if (text is null) break;
                    if (!Take(text)) break;
                }
            }
            catch (WebSocketException)
            {
                // Dropped; the loop reconnects unless the table stopped.
            }
            finally
            {
                linked.Cancel();
            }
        });
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await Drain(ws, linked.Token);
                await wake.WaitAsync(linked.Token);
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // The reader saw the socket close or a refusal.
        }
        catch (OperationCanceledException)
        {
            // Stopping: the close below.
        }
        if (stop.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await Drain(ws, grace.Token);
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "dealing stopped", grace.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                // The server is gone; it will keep the code for its hold time instead.
            }
            if (await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(1))) != reader) ws.Abort();
        }
        await reader;
    }

    private async Task Drain(ClientWebSocket ws, CancellationToken token)
    {
        while (outbox.TryPeek(out var next))
        {
            await SendText(ws, next.Json, token);
            outbox.TryDequeue(out _);
        }
    }

    /// <summary>Handles one server message; false once the table is refused.</summary>
    private bool Take(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "refused":
                Refuse(root);
                return false;
            case "link":
                links.Enqueue((root.GetProperty("player").GetString() ?? "", root.GetProperty("url").GetString() ?? ""));
                break;
            case "watching":
                watchers.Enqueue(root.GetProperty("player").GetString() ?? "");
                break;
            case "act":
                actions.Enqueue((root.GetProperty("player").GetString() ?? "", root.GetProperty("action").Clone()));
                break;
            case "buy":
                web.Enqueue(new WebBuy(Str(root, "sale"), Str(root, "player"), root.GetProperty("count").GetInt32(),
                    root.GetProperty("cost").GetInt64()));
                break;
            case "won":
                web.Enqueue(new WebWon(Str(root, "session"), root.GetProperty("nonce").GetInt64(), Str(root, "player"),
                    root.GetProperty("gil").GetInt64(), root.GetProperty("payout").GetInt64(),
                    root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null));
                break;
            case "ended":
                web.Enqueue(new WebEnded(Str(root, "player"),
                    root.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null));
                break;
            case "settling":
                Settling = true;
                Status = "The license ended: paying what players are owed; selling nothing.";
                break;
            case "settled":
                Settled = true;
                Status = "Everything owed is paid; the table closed.";
                return false;
            case "error":
                Status = $"Live as table {Room}; the server refused an event: {root.GetProperty("message").GetString()}";
                break;
        }
        return true;
    }

    private static string Str(JsonElement root, string name) => root.GetProperty(name).GetString() ?? "";

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
