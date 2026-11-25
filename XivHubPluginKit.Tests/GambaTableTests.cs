using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using XivHubPluginKit.License;

namespace XivHubPluginKit.Tests;

/// <summary>
/// <see cref="GambaTable"/> against a fake gamba-server that speaks <c>gamba-server/src/table/mod.rs</c>'s protocol:
/// the hello, the room code, events in order, a refusal, and a reconnect that asks for its room back.
/// </summary>
public class GambaTableTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly string Cid = new('c', 64);

    [Fact]
    public void ItSaysWhoIsDealingAndStreamsEventsInOrder()
    {
        using var server = new FakeTableServer();
        using var table = new GambaTable(server.Origin, "tok", Cid, "roulette");
        Assert.True(SpinWait.SpinUntil(() => table.Room == "ABC234", Wait), table.Status);

        var hello = server.Hellos.Single();
        Assert.Equal("tok", hello.GetProperty("token").GetString());
        Assert.Equal(Cid, hello.GetProperty("cid_hash").GetString());
        Assert.Equal("roulette", hello.GetProperty("game").GetString());

        table.Send(new { kind = "say", text = "Place your bets!" });
        table.Send(new { kind = "bet", player = "Asure Namei", spot = "red", amount = 100_000 });
        Assert.True(SpinWait.SpinUntil(() => server.Events.Count == 2, Wait));
        Assert.Equal(new[] { "say", "bet" }, server.Events.Select(e => e.GetProperty("kind").GetString()));
    }

    [Fact]
    public void ALinkRequestComesBackAsTheServersShortLinkAndTheWatchersActionsArrive()
    {
        using var server = new FakeTableServer();
        using var table = new GambaTable(server.Origin, "tok", Cid, "roulette");
        Assert.True(SpinWait.SpinUntil(() => table.Room == "ABC234", Wait));
        table.RequestLink("Asure Namei");
        (string Player, string Url) link = default;
        Assert.True(SpinWait.SpinUntil(() => table.TryTakeLink(out link), Wait));
        Assert.Equal("Asure Namei", link.Player);
        Assert.Equal($"{server.Origin}/v/gilded", table.Page);
        Assert.Equal($"{server.Origin}/re/SHORT234", link.Url);
        string watcher = "";
        Assert.True(SpinWait.SpinUntil(() => table.TryTakeWatcher(out watcher), Wait));
        Assert.Equal("Asure Namei", watcher);
        (string Player, JsonElement Action) act = default;
        Assert.True(SpinWait.SpinUntil(() => table.TryTakeAction(out act), Wait));
        Assert.Equal(("Asure Namei", "hit"), (act.Player, act.Action.GetProperty("move").GetString()));

        table.SendPrivate("Asure Namei", new { kind = "bank", balance = 900_000 });
        Assert.True(SpinWait.SpinUntil(() => server.Events.Any(e => e.GetProperty("kind").GetString() == "private"), Wait));
        var p = server.Events.Single(e => e.GetProperty("kind").GetString() == "private");
        Assert.Equal("Asure Namei", p.GetProperty("player").GetString());
        Assert.Equal(900_000, p.GetProperty("event").GetProperty("balance").GetInt32());
    }

    [Fact]
    public void ARefusedTableStaysClosedWithTheServersReason()
    {
        using var server = new FakeTableServer { Refuse = "This character has no live roulette license." };
        using var table = new GambaTable(server.Origin, "tok", Cid, "roulette");
        Assert.True(SpinWait.SpinUntil(() => table.Refused, Wait));
        Assert.Equal("This character has no live roulette license.", table.Status);
        Thread.Sleep(1500);
        Assert.Single(server.Hellos);
    }

    [Fact]
    public void AReconnectAsksForTheSameRoomAndSendsWhatWaited()
    {
        using var server = new FakeTableServer();
        using var table = new GambaTable(server.Origin, "tok", Cid, "blackjack");
        Assert.True(SpinWait.SpinUntil(() => table.Room == "ABC234", Wait));

        server.DropAll();
        table.Send(new { kind = "say", text = "after the drop" });
        Assert.True(SpinWait.SpinUntil(() => server.Hellos.Count == 2, Wait), table.Status);
        Assert.Equal("ABC234", server.Hellos.Last().GetProperty("room").GetString());
        Assert.True(SpinWait.SpinUntil(() => server.Events.Any(e => e.GetProperty("text").GetString() == "after the drop"), Wait));
    }

    [Fact]
    public void AFreshRoomGetsTheRulesAndTheRoundAgainButNotWhatItAlreadyHad()
    {
        using var server = new FakeTableServer { Fresh = [true, true] };
        using var table = new GambaTable(server.Origin, "tok", Cid, "blackjack");
        Assert.True(SpinWait.SpinUntil(() => table.Room == "ABC234", Wait));
        Assert.Equal(1, table.FreshRooms);
        table.Send(new { kind = "rules", lines = new[] { "Old rules." } });
        table.Send(new { kind = "say", text = "last round" });
        table.Send(new { kind = "bets_open", round = 2, min = 10, max = 100 });
        table.Send(new { kind = "rules", lines = new[] { "Blackjack pays 3 to 2." } });
        table.Send(new { kind = "bets", bets = Array.Empty<object>() });
        table.SendPrivate("Asure Namei", new { kind = "bank", balance = 5 });
        Assert.True(SpinWait.SpinUntil(() => server.Events.Count == 6, Wait));

        server.DropAll();
        Assert.True(SpinWait.SpinUntil(() => table.FreshRooms == 2, Wait), table.Status);
        Assert.True(SpinWait.SpinUntil(() => server.Events.Count == 9, Wait));
        Thread.Sleep(200);
        var again = server.Events.Skip(6).ToList();
        Assert.Equal(new[] { "rules", "bets_open", "bets" }, again.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal("Blackjack pays 3 to 2.", again[0].GetProperty("lines")[0].GetString());
    }

    [Fact]
    public void ARoomThatKeptTheRoundGetsNothingAgain()
    {
        using var server = new FakeTableServer { Fresh = [true, false] };
        using var table = new GambaTable(server.Origin, "tok", Cid, "blackjack");
        Assert.True(SpinWait.SpinUntil(() => table.Room == "ABC234", Wait));
        table.Send(new { kind = "bets_open", round = 1, min = 10, max = 100 });
        Assert.True(SpinWait.SpinUntil(() => server.Events.Count == 1, Wait));

        server.DropAll();
        Assert.True(SpinWait.SpinUntil(() => server.Hellos.Count == 2, Wait), table.Status);
        table.Send(new { kind = "say", text = "after the drop" });
        Assert.True(SpinWait.SpinUntil(() => server.Events.Count == 2, Wait));
        Thread.Sleep(200);
        Assert.Equal("say", server.Events.Last().GetProperty("kind").GetString());
        Assert.Equal(1, table.FreshRooms);
    }

    [Fact]
    public void StoppingClosesTheSocketSoTheServerEndsTheTable()
    {
        using var server = new FakeTableServer();
        var table = new GambaTable(server.Origin, "tok", Cid, "blackjack");
        Assert.True(SpinWait.SpinUntil(() => table.Room == "ABC234", Wait));
        table.Send(new { kind = "say", text = "last words" });
        table.Dispose();
        Assert.True(SpinWait.SpinUntil(() => server.Closed, Wait));
        Assert.Equal("last words", server.Events.Single().GetProperty("text").GetString());
    }

    [Fact]
    public void AWebGamesNewsArrivesTypedAndItsAnswersStayOutOfTheRound()
    {
        using var server = new FakeTableServer
        {
            Fresh = [true, true],
            Greeting =
            [
                """{"type":"buy","sale":"S1","player":"Asure Namei","count":3,"cost":300000}""",
                """{"type":"won","session":"W1","nonce":2,"player":"Asure Namei","gil":0,"payout":50000,"item":"Plush"}""",
                """{"type":"ended","player":"Asure Namei","session":null}""",
            ],
        };
        using var table = new GambaTable(server.Origin, "tok", Cid, "scratch");
        var got = new List<object>();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (table.TryTakeWeb(out var m)) got.Add(m);
            return got.Count == 3;
        }, Wait));
        Assert.Equal(new WebBuy("S1", "Asure Namei", 3, 300_000), got[0]);
        Assert.Equal(new WebWon("W1", 2, "Asure Namei", 0, 50_000, "Plush"), got[1]);
        Assert.Equal(new WebEnded("Asure Namei", null), got[2]);
        Assert.False(table.Settling);

        table.Sold("S1");
        table.Paid("W1", 2);
        table.End("Asure Namei");
        Assert.True(SpinWait.SpinUntil(() => server.Events.Count == 3, Wait));
        Assert.Equal(new[] { "sold", "paid", "end" }, server.Events.Select(e => e.GetProperty("kind").GetString()));
        // A fresh room gets the round again, never an answer: the server asks again for what it still wants.
        server.DropAll();
        Assert.True(SpinWait.SpinUntil(() => table.FreshRooms == 2, Wait), table.Status);
        Thread.Sleep(300);
        Assert.Equal(3, server.Events.Count);
    }

    [Fact]
    public void ASettlingTableSaysSoAndStopsOnceSettled()
    {
        using var server = new FakeTableServer { Settling = true, Greeting = ["""{"type":"settled"}"""] };
        using var table = new GambaTable(server.Origin, "tok", Cid, "scratch");
        Assert.True(SpinWait.SpinUntil(() => table.Settled, Wait), table.Status);
        Assert.True(table.Settling);
        Thread.Sleep(1500);
        Assert.Single(server.Hellos);
    }

    /// <summary>Answers every hello with room ABC234 (or the refusal set) and records what arrives.</summary>
    private sealed class FakeTableServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly ConcurrentBag<WebSocket> open = new();
        private readonly Task serving;

        public string? Refuse { get; init; }
        /// <summary>Lines sent right after the room reply, as a web game's server sends what is owed.</summary>
        public string[] Greeting { get; init; } = [];
        /// <summary>The room reply says the table only settles.</summary>
        public bool Settling { get; init; }
        /// <summary>The <c>fresh</c> flag for each hello in turn; past the end, or empty, the reply leaves it out.</summary>
        public bool[] Fresh { get; init; } = [];
        /// <summary>A client sent a Close frame.</summary>
        public bool Closed { get; private set; }
        public ConcurrentQueue<JsonElement> HellosQueue { get; } = new();
        public List<JsonElement> Hellos => HellosQueue.ToList();
        public ConcurrentQueue<JsonElement> EventsQueue { get; } = new();
        public List<JsonElement> Events => EventsQueue.ToList();
        public string Origin { get; }

        public FakeTableServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Origin = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Origin + "/");
            listener.Start();
            serving = Task.Run(Serve);
        }

        /// <summary>Closes every open socket, as a server restart does.</summary>
        public void DropAll()
        {
            foreach (var ws in open)
                ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "restart", CancellationToken.None).Wait(Wait);
        }

        private async Task Serve()
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            if (ctx.Request.Url!.AbsolutePath != "/ws/table" || !ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            open.Add(ws);
            try
            {
                HellosQueue.Enqueue(Parse(await Receive(ws)));
                int n = HellosQueue.Count - 1;
                string reply = Refuse is not null
                    ? JsonSerializer.Serialize(new { type = "refused", message = Refuse })
                    : n < Fresh.Length
                        ? JsonSerializer.Serialize(new { type = "room", code = "ABC234", page = $"{Origin}/v/gilded", fresh = Fresh[n] })
                        : JsonSerializer.Serialize(new { type = "room", code = "ABC234", page = $"{Origin}/v/gilded", settling = Settling });
                await ws.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, stopping.Token);
                if (Refuse is null)
                    foreach (var line in Greeting)
                        await ws.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, stopping.Token);
                if (Refuse is not null)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "refused", stopping.Token);
                    return;
                }
                while (true)
                {
                    var text = await Receive(ws);
                    if (text is null)
                    {
                        Closed = true;
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", stopping.Token);
                        return;
                    }
                    var e = Parse(text);
                    EventsQueue.Enqueue(e);
                    if (e.GetProperty("kind").GetString() == "link")
                    {
                        var player = e.GetProperty("player").GetString();
                        foreach (var answer in new[]
                                 {
                                     JsonSerializer.Serialize(new { type = "link", player, url = $"{Origin}/re/SHORT234" }),
                                     JsonSerializer.Serialize(new { type = "watching", player }),
                                     JsonSerializer.Serialize(new { type = "act", player, action = new { kind = "move", move = "hit" } }),
                                 })
                            await ws.SendAsync(Encoding.UTF8.GetBytes(answer), WebSocketMessageType.Text, true, stopping.Token);
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Dropped by the test or the client.
            }
        }

        private static JsonElement Parse(string? text) => JsonDocument.Parse(text ?? "{}").RootElement.Clone();

        private async Task<string?> Receive(WebSocket ws)
        {
            var buffer = new byte[4096];
            var r = await ws.ReceiveAsync(buffer, stopping.Token);
            return r.MessageType == WebSocketMessageType.Close ? null : Encoding.UTF8.GetString(buffer, 0, r.Count);
        }

        public void Dispose()
        {
            stopping.Cancel();
            listener.Close();
            try
            {
                serving.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The accept loop ended on the close.
            }
        }
    }
}
