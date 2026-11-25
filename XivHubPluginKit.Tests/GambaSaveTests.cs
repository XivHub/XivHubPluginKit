using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XivHubPluginKit.License;

namespace XivHubPluginKit.Tests;

/// <summary><see cref="GambaSave"/> and <see cref="GambaLooks"/> against a fake of <c>gamba-server</c>'s <c>save_api.rs</c> and <c>looks_api.rs</c>.</summary>
public class GambaSaveTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly string Cid = new('c', 64);

    [Fact]
    public async Task ItUploadsTheNewestSaveAndReadsItBack()
    {
        using var server = new FakeSaveServer();
        using var save = new GambaSave(server.Origin, "tok", Cid, "CrystalTable");
        Assert.Null(await save.FetchAsync());

        save.Put("""{"n":1}""");
        Assert.True(SpinWait.SpinUntil(() => server.Stored != null, Wait), save.Problem);
        // Saves made while the first one waits out the gap collapse into the newest.
        save.Put("""{"n":2}""");
        save.Put("""{"n":3}""");
        Assert.True(SpinWait.SpinUntil(() => server.Stored == """{"n":3}""", Wait), save.Problem);
        Assert.Equal(2, server.Puts);
        Assert.NotNull(save.LastSaved);

        var who = server.LastWho!.Value;
        Assert.Equal("tok", who.GetProperty("token").GetString());
        Assert.Equal(Cid, who.GetProperty("cid_hash").GetString());
        Assert.Equal("CrystalTable", who.GetProperty("plugin").GetString());

        var copy = await save.FetchAsync();
        Assert.Equal("""{"n":3}""", copy!.Json);
        Assert.Equal(1_700_000_000, copy.SavedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ARefusalIsShownInTheServersWords()
    {
        using var server = new FakeSaveServer { Refuse = "This character has no license." };
        using var save = new GambaSave(server.Origin, "tok", Cid, "CrystalTable");
        var e = await Assert.ThrowsAsync<GambaSaveException>(() => save.FetchAsync());
        Assert.Equal("This character has no license.", e.Message);
        save.Put("{}");
        Assert.True(SpinWait.SpinUntil(() => save.Problem != null, Wait));
        Assert.Equal("This character has no license.", save.Problem);
    }

    [Fact]
    public async Task SavedLooksAreListedAndOneIsWorn()
    {
        using var server = new FakeSaveServer();
        using var looks = new GambaLooks(server.Origin, "tok", Cid, "CrystalTable");
        var list = await looks.ListAsync();
        Assert.Equal(new SavedLook(2, "Velvet Hour", "The Velvet Hour", true), list[1]);
        list = await looks.WearAsync(1);
        Assert.True(list[0].Worn);
        Assert.False(list[1].Worn);
        Assert.Equal(Cid, server.LastWho!.Value.GetProperty("cid_hash").GetString());
    }

    [Fact]
    public async Task ALooksRefusalIsShownInTheServersWords()
    {
        using var server = new FakeSaveServer { Refuse = "This character has no license." };
        using var looks = new GambaLooks(server.Origin, "tok", Cid, "CrystalTable");
        var e = await Assert.ThrowsAsync<GambaLooksException>(() => looks.ListAsync());
        Assert.Equal("This character has no license.", e.Message);
    }

    [Fact]
    public void DisposingSendsASaveStillWaiting()
    {
        using var server = new FakeSaveServer();
        var save = new GambaSave(server.Origin, "tok", Cid, "CrystalTable");
        save.Put("""{"n":1}""");
        Assert.True(SpinWait.SpinUntil(() => server.Stored != null, Wait));
        // Inside the gap after the first upload, so only the flush on dispose can send this one.
        save.Put("""{"n":2}""");
        save.Dispose();
        Assert.Equal("""{"n":2}""", server.Stored);
    }

    internal sealed class FakeSaveServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task serving;
        private int puts;

        public string Origin { get; }
        public string? Refuse { get; init; }
        public volatile string? StoredValue;
        public string? Stored => StoredValue;
        public int Puts => puts;
        public JsonElement? LastWho { get; private set; }

        public FakeSaveServer()
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

        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                using var reader = new StreamReader(ctx.Request.InputStream);
                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                LastWho = doc.RootElement.Clone();
                var (status, body) = Answer(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, doc.RootElement);
                ctx.Response.StatusCode = status;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        private (int, string) Answer(string method, string path, JsonElement body)
        {
            if (Refuse != null) return (403, JsonSerializer.Serialize(new { error = Refuse }));
            if (method == "PUT" && path == "/api/save")
            {
                StoredValue = body.GetProperty("data").GetRawText();
                Interlocked.Increment(ref puts);
                return (204, "");
            }
            if (method == "POST" && path == "/api/save/fetch")
                return StoredValue == null
                    ? (404, """{"error":"gamba.xivhub.net holds no save for this character."}""")
                    : (200, $$"""{"saved_at":1700000000,"data":{{StoredValue}}}""");
            if (method == "POST" && path == "/api/looks")
                return (200, Looks());
            if (method == "POST" && path == "/api/looks/wear")
            {
                Worn = body.GetProperty("id").GetInt64();
                return (200, Looks());
            }
            return (404, "{}");
        }

        /// <summary>The saved look worn, by id.</summary>
        public long Worn { get; private set; } = 2;

        private string Looks() =>
            JsonSerializer.Serialize(new[] { (Id: 1L, Name: "Lotus", Venue: ""), (Id: 2L, Name: "Velvet Hour", Venue: "The Velvet Hour") }
                .Select(l => new { id = l.Id, name = l.Name, venue = l.Venue, worn = l.Id == Worn }));

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
            try
            {
                serving.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException) { }
        }
    }
}
