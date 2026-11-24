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

    /// <summary>Answers every hello with room ABC234 (or the refusal set) and records what arrives.</summary>
    private sealed class FakeTableServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly ConcurrentBag<WebSocket> open = new();
        private readonly Task serving;

        public string? Refuse { get; init; }
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
                string reply = Refuse is null
                    ? JsonSerializer.Serialize(new { type = "room", code = "ABC234" })
                    : JsonSerializer.Serialize(new { type = "refused", message = Refuse });
                await ws.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, stopping.Token);
                if (Refuse is not null)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "refused", stopping.Token);
                    return;
                }
                while (true)
                {
                    var text = await Receive(ws);
                    if (text is null) return;
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
