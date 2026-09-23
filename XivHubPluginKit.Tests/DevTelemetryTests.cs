using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XivHubPluginKit.Tests;

public sealed class DevTelemetryTests
{
    private const int OneMiB = 1 << 20;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void RecordsPostWithoutPendingPlainLines()
    {
        using var server = new FakeDevlog();
        using var t = new DevTelemetry("Test", () => true, () => server.LogUrl);

        Assert.True(SpinWait.SpinUntil(() => server.Posts("/log").Any(p => p.Text.Contains("telemetry session started")), Wait));
        t.Record("probe", new Dictionary<string, object?> { ["n"] = 1 });

        Assert.True(SpinWait.SpinUntil(() => server.Records().Any(r => r.GetProperty("kind").GetString() == "probe"), Wait));
        var post = server.Posts("/records").First();
        Assert.Equal(t.Session + "-r", post.Session);
        Assert.Equal("0", post.Offset);
        Assert.Equal("application/x-ndjson", post.ContentType);
        Assert.Equal("?client=test", post.Query);
    }

    [Fact]
    public void RecordsPostWhenTheLogPostFails()
    {
        using var server = new FakeDevlog { LogStatus = 500 };
        using var t = new DevTelemetry("Test", () => true, () => server.LogUrl);
        t.Record("probe", new Dictionary<string, object?>());

        Assert.True(SpinWait.SpinUntil(() => server.Records().Any(r => r.GetProperty("kind").GetString() == "probe"), Wait));
        Assert.Contains(server.Posts("/log"), p => p.Status == 500);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeDeliversTheLastRecord(bool flushInFlight)
    {
        using var server = new FakeDevlog();
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);
        if (flushInFlight)
        {
            // The timer's flush is stuck in its /log post when Dispose starts, so Dispose must wait
            // for it rather than skip, and then post the record.
            server.LogDelayMs = 1500;
            Assert.True(SpinWait.SpinUntil(() => server.Received("/log") > 0, Wait));
        }

        t.Record("session", new Dictionary<string, object?> { ["event"] = "stop" });
        t.Dispose();

        var records = server.Records();
        Assert.Single(records);
        Assert.Equal("stop", records[0].GetProperty("event").GetString());
    }

    // ar-SA formats with the Gregorian calendar and ':' under current ICU, so on its own it would
    // not catch a culture-sensitive timestamp; th-TH (Buddhist era year) and fi-FI ('.' as the time
    // separator) do.
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("fi-FI")]
    public void SeqIncreasesAndTimestampIsInvariant(string cultureName)
    {
        using var server = new FakeDevlog();
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            for (int i = 0; i < 3; i++)
                t.Record("tick", new Dictionary<string, object?> { ["i"] = i, ["price"] = 1.5 });
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        t.Dispose();

        var records = server.Records();
        Assert.Equal([1L, 2L, 3L], records.Select(r => r.GetProperty("seq").GetInt64()));
        var now = DateTimeOffset.Now;
        foreach (var r in records)
        {
            var ts = r.GetProperty("ts").GetString()!;
            Assert.Matches(new Regex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}[+-][0-9]{2}:[0-9]{2}$"), ts);
            var parsed = DateTimeOffset.ParseExact(ts, "yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
            Assert.InRange(parsed, now.AddMinutes(-1), now.AddMinutes(1));
            Assert.Equal("Test", r.GetProperty("src").GetString());
            Assert.Equal(t.Session, r.GetProperty("session").GetString());
            Assert.Equal("tick", r.GetProperty("kind").GetString());
            Assert.Equal(1.5, r.GetProperty("price").GetDouble());
        }
        Assert.Equal([0, 1, 2], records.Select(r => r.GetProperty("i").GetInt32()));
    }

    [Theory]
    [InlineData("ts")]
    [InlineData("src")]
    [InlineData("session")]
    [InlineData("seq")]
    [InlineData("kind")]
    public void ReservedKeyThrows(string key)
    {
        using var server = new FakeDevlog();
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);

        Assert.Throws<ArgumentException>(() => t.Record("probe", new Dictionary<string, object?> { ["ok"] = 1, [key] = "x" }));
        t.Dispose();

        Assert.Empty(server.Records());
    }

    [Fact]
    public void RecordPostsStayUnderOneMegabyte()
    {
        using var server = new FakeDevlog();
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);
        var pad = new string('x', 1000);
        const int count = 3 * 1024;
        for (int i = 0; i < count; i++)
            t.Record("bulk", new Dictionary<string, object?> { ["pad"] = pad });
        t.Dispose();

        var posts = server.Posts("/records");
        Assert.True(posts.Sum(p => (long)p.Body.Length) >= 3L * OneMiB);
        Assert.True(posts.Count >= 3, $"{posts.Count} posts");
        Assert.All(posts, p => Assert.True(p.Body.Length <= OneMiB, $"{p.Body.Length} bytes"));
        Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), server.Records().Select(r => r.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public void QueueDropsOldestPastSixteenMegabytes()
    {
        using var server = new FakeDevlog { RecordsStatus = 500 };
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);

        // A failed records post backs the channel off for 30 s, so nothing drains while it fills.
        t.Record("first", new Dictionary<string, object?>());
        Assert.True(SpinWait.SpinUntil(() => server.Posts("/records").Any(p => p.Status == 500), Wait));

        var pad = new string('x', 1000);
        const int count = 17 * 1024;
        for (int i = 0; i < count; i++)
            t.Record("bulk", new Dictionary<string, object?> { ["pad"] = pad });
        server.RecordsStatus = 204;
        t.Dispose();

        var delivered = server.Posts("/records").Where(p => p.Status == 204).ToList();
        var seqs = delivered.SelectMany(p => p.Lines).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("seq").GetInt64()).ToList();
        long lastSeq = count + 1;
        long bytes = delivered.Sum(p => (long)p.Body.Length);
        long lineBytes = bytes / seqs.Count;

        // The newest records survive, contiguous, and the oldest (including "first") are gone.
        Assert.Equal(lastSeq, seqs[^1]);
        Assert.Equal(Enumerable.Range((int)seqs[0], seqs.Count).Select(i => (long)i), seqs);
        Assert.True(seqs[0] > 2, $"first delivered seq {seqs[0]}");
        // By bytes, not lines: what is kept fills the 16 MiB cap to within one record.
        Assert.InRange(bytes, 16L * OneMiB - lineBytes, 16L * OneMiB);
        // A dropped record keeps its stream position, so the first post starts past the drops.
        Assert.Equal((seqs[0] - 1).ToString(CultureInfo.InvariantCulture), delivered[0].Offset);
        Assert.All(delivered, p => Assert.True(p.Body.Length <= OneMiB, $"{p.Body.Length} bytes"));
    }

    [Fact]
    public void PlainLinesDroppedPastTheCapKeepTheirOffset()
    {
        using var server = new FakeDevlog { LogStatus = 500 };
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);

        // The startup line's post fails and backs the channel off, so it stays pending at offset 0.
        Assert.True(SpinWait.SpinUntil(() => server.Posts("/log").Any(p => p.Status == 500), Wait));
        for (int i = 0; i < 5000; i++) t.Log($"line {i}");
        server.LogStatus = 204;
        t.Dispose();

        // 5001 pending lines over a 5000 cap: the startup line goes, and its position with it.
        var post = server.Posts("/log").Single(p => p.Status == 204);
        Assert.Equal("1", post.Offset);
        Assert.Equal(5000, post.Lines.Count());
        Assert.EndsWith("line 0", post.Lines.First());
    }

    /// <summary>A devlog server on a free loopback port that records every request and answers
    /// with a configurable status per endpoint.</summary>
    private sealed class FakeDevlog : IDisposable
    {
        public sealed record Post(string Path, string Query, string? Session, string? Offset, string? ContentType, int Status, byte[] Body)
        {
            public string Text => Encoding.UTF8.GetString(Body);
            public IEnumerable<string> Lines => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        private readonly HttpListener listener = new();
        private readonly ConcurrentQueue<Post> posts = new();
        private readonly ConcurrentDictionary<string, int> received = new();
        private readonly Task serving;

        public volatile int LogStatus = 204;
        public volatile int RecordsStatus = 204;
        public volatile int LogDelayMs;

        public string LogUrl { get; }

        public FakeDevlog()
        {
            var prefix = $"http://127.0.0.1:{FreePort()}/";
            listener.Prefixes.Add(prefix);
            listener.Start();
            LogUrl = prefix + "log?client=test";
            serving = Task.Run(Serve);
        }

        public List<Post> Posts(string path) => posts.Where(p => p.Path == path).ToList();

        /// <summary>Requests to <paramref name="path"/> that have arrived, answered or not.</summary>
        public int Received(string path) => received.GetValueOrDefault(path);

        /// <summary>Every record in the accepted <c>/records</c> posts, in arrival order.</summary>
        public List<JsonElement> Records() => Posts("/records")
            .Where(p => p.Status == 204)
            .SelectMany(p => p.Lines)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToList();

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task Serve()
        {
            while (true)
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

                var req = ctx.Request;
                var path = req.Url!.AbsolutePath;
                received.AddOrUpdate(path, 1, (_, n) => n + 1);
                using var body = new MemoryStream();
                await req.InputStream.CopyToAsync(body);
                if (path == "/log" && LogDelayMs > 0) await Task.Delay(LogDelayMs);

                int status = path == "/records" ? RecordsStatus : LogStatus;
                posts.Enqueue(new Post(path, req.Url.Query, req.Headers["X-Devlog-Session"], req.Headers["X-Devlog-Offset"],
                    req.ContentType, status, body.ToArray()));
                ctx.Response.StatusCode = status;
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
            serving.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
