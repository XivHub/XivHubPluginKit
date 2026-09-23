using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
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
            // The timer's flush is stuck in its /log post when Dispose starts, so Dispose must abort
            // it rather than skip the final flush, and then post the record.
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

    [Fact]
    public void DisposeIsBoundedWhenTheEndpointStalls()
    {
        using var server = new FakeDevlog { LogDelayMs = 60_000, RecordsDelayMs = 60_000 };
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);

        // The timer's flush is stuck in its /log post, and a record waits behind it.
        Assert.True(SpinWait.SpinUntil(() => server.Received("/log") > 0, Wait));
        t.Record("last", new Dictionary<string, object?>());

        var clock = Stopwatch.StartNew();
        t.Dispose();
        clock.Stop();

        // The flush in flight aborts at once, and the final flush of both channels shares one 3 s budget.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3.5), $"Dispose took {clock.Elapsed}");
    }

    private sealed class Node
    {
        public Node? Next;
    }

    private sealed class Throws
    {
        public int Value => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void RecordSerialisesAnyValueWithoutThrowing()
    {
        using var server = new FakeDevlog();
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);
        var cycle = new Node();
        cycle.Next = cycle;
        object deep = 1;
        for (int i = 0; i < 100; i++) deep = new[] { deep };

        t.Record("floats", new Dictionary<string, object?>
        {
            ["nan"] = double.NaN,
            ["inf"] = double.PositiveInfinity,
            ["ninf"] = double.NegativeInfinity,
            ["fnan"] = float.NaN,
        });
        t.Record("structs", new Dictionary<string, object?>
        {
            ["ptr"] = (nint)0x1A2B,
            ["uptr"] = (nuint)0xFF,
            ["vec"] = new Vector3(1, 2, 3),
            ["tuple"] = (7, "a"),
        });
        t.Record("broken", new Dictionary<string, object?>
        {
            ["cycle"] = cycle,
            ["deep"] = deep,
            ["getter"] = new Throws(),
            ["action"] = (Action)(() => { }),
            ["ok"] = 5,
        });
        t.Record("nulls", null!);
        t.Dispose();

        var r = server.Records();
        Assert.Equal([1L, 2L, 3L, 4L], r.Select(x => x.GetProperty("seq").GetInt64()));

        Assert.Equal("NaN", r[0].GetProperty("nan").GetString());
        Assert.Equal("Infinity", r[0].GetProperty("inf").GetString());
        Assert.Equal("-Infinity", r[0].GetProperty("ninf").GetString());
        Assert.Equal("NaN", r[0].GetProperty("fnan").GetString());

        Assert.Equal("0x1A2B", r[1].GetProperty("ptr").GetString());
        Assert.Equal("0xFF", r[1].GetProperty("uptr").GetString());
        Assert.Equal("""{"X":1,"Y":2,"Z":3}""", r[1].GetProperty("vec").GetRawText());
        Assert.Equal("""{"Item1":7,"Item2":"a"}""", r[1].GetProperty("tuple").GetRawText());

        Assert.StartsWith("!JsonException: ", r[2].GetProperty("cycle").GetString());
        Assert.StartsWith("!JsonException: ", r[2].GetProperty("deep").GetString());
        Assert.Equal("!InvalidOperationException: boom", r[2].GetProperty("getter").GetString());
        Assert.StartsWith("!NotSupportedException: ", r[2].GetProperty("action").GetString());
        Assert.Equal(5, r[2].GetProperty("ok").GetInt32());

        Assert.Equal("nulls", r[3].GetProperty("kind").GetString());
        Assert.Equal(["ts", "src", "session", "seq", "kind"], r[3].EnumerateObject().Select(p => p.Name));
    }

    /// <summary>Yields one entry, then throws, as a dictionary mutated on another thread does.</summary>
    private sealed class FailingFields : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => throw new KeyNotFoundException();
        public IEnumerable<string> Keys => this.Select(p => p.Key);
        public IEnumerable<object?> Values => this.Select(p => p.Value);
        public int Count => 1;
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out object? value) => throw new KeyNotFoundException();
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return new("a", 1);
            throw new InvalidOperationException("Collection was modified");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void RecordThatCannotBeReadIsDroppedWithoutUsingASeq()
    {
        using var server = new FakeDevlog();
        var errors = new ConcurrentQueue<string>();
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl, errors.Enqueue);

        t.Record("before", new Dictionary<string, object?>());
        t.Record("failing", new FailingFields());
        t.Record("after", new Dictionary<string, object?>());
        t.Dispose();

        var r = server.Records();
        Assert.Equal(["before", "after"], r.Select(x => x.GetProperty("kind").GetString()));
        Assert.Equal([1L, 2L], r.Select(x => x.GetProperty("seq").GetInt64()));
        Assert.Contains(errors, e => e.Contains("'failing'") && e.Contains("Collection was modified"));
    }

    [Fact]
    public void MultiLineLogKeepsTheOffsetInStepWithTheServer()
    {
        using var server = new FakeDevlog();
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);
        Assert.True(SpinWait.SpinUntil(() => server.Posts("/log").Count > 0, Wait));

        t.Log("one\ntwo\r\nthree\r");
        Assert.True(SpinWait.SpinUntil(() => server.Posts("/log").Count > 1, Wait));
        t.Log("four");
        t.Dispose();

        // The server gives each '\n'-terminated line one stream position, so each post must start
        // where the previous one's lines end.
        var posts = server.Posts("/log").OrderBy(p => long.Parse(p.Offset!, CultureInfo.InvariantCulture)).ToList();
        for (int i = 1; i < posts.Count; i++)
            Assert.Equal(long.Parse(posts[i - 1].Offset!, CultureInfo.InvariantCulture) + posts[i - 1].Text.Split('\n').Length - 1,
                long.Parse(posts[i].Offset!, CultureInfo.InvariantCulture));

        var lines = posts.SelectMany(p => p.Text.Split('\n')[..^1]).ToList();
        Assert.All(lines, l => Assert.Matches(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3} \[Test\] ", l));
        Assert.Equal(["telemetry session started", "one", "two", "three", "four"], lines.Select(l => l[(l.IndexOf("] ", StringComparison.Ordinal) + 2)..]));
    }

    [Fact]
    public async Task RecordsArrivingFasterThanTheyPostDoNotStarveTheLog()
    {
        using var server = new FakeDevlog { RecordsDelayMs = 50 };
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);
        Assert.True(SpinWait.SpinUntil(() => server.Posts("/log").Count > 0, Wait));

        using var stop = new CancellationTokenSource();
        var producer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                t.Record("tick", new Dictionary<string, object?> { ["n"] = 1 });
                Thread.Sleep(1);
            }
        });
        try
        {
            Assert.True(SpinWait.SpinUntil(() => server.Posts("/records").Count > 0, Wait));
            t.Log("marker");
            Assert.True(SpinWait.SpinUntil(() => server.Posts("/log").Any(p => p.Text.Contains("marker")), TimeSpan.FromSeconds(5)),
                "the /log channel never got its turn");
        }
        finally
        {
            stop.Cancel();
            await producer;
            t.Dispose();
        }
    }

    [Fact]
    public void OversizeRecordBecomesAStub()
    {
        using var server = new FakeDevlog();
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl);
        t.Record("small", new Dictionary<string, object?> { ["n"] = 1 });
        t.Record("big", new Dictionary<string, object?> { ["blob"] = new string('x', 2 * OneMiB) });
        t.Record("small", new Dictionary<string, object?> { ["n"] = 2 });
        t.Dispose();

        Assert.All(server.Posts("/records"), p => Assert.True(p.Body.Length <= OneMiB, $"{p.Body.Length} bytes"));
        var r = server.Records();
        Assert.Equal([1L, 2L, 3L], r.Select(x => x.GetProperty("seq").GetInt64()));
        Assert.Equal("oversize", r[1].GetProperty("kind").GetString());
        Assert.Equal("big", r[1].GetProperty("of").GetString());
        Assert.InRange(r[1].GetProperty("bytes").GetInt64(), 2L * OneMiB, 2L * OneMiB + 200);
        Assert.Equal(t.Session, r[1].GetProperty("session").GetString());
        Assert.False(r[1].TryGetProperty("blob", out _));
        Assert.Equal(2, r[2].GetProperty("n").GetInt32());
    }

    [Fact]
    public void OnErrorThrowingOnTheTimerThreadIsContained()
    {
        using var server = new FakeDevlog { LogStatus = 500 };
        int calls = 0;
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("onError failed");
        });

        // An exception escaping a timer callback ends the process, test host included.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) > 0, Wait));
        Thread.Sleep(500);
        t.Record("after", new Dictionary<string, object?>());
        t.Dispose();

        Assert.Single(server.Records());
    }

    [Fact]
    public void OnErrorThrowingInDisposeIsContained()
    {
        using var server = new FakeDevlog { LogStatus = 500 };
        int testThread = Environment.CurrentManagedThreadId;
        var t = new DevTelemetry("Test", () => true, () => server.LogUrl, _ =>
        {
            if (Environment.CurrentManagedThreadId == testThread) throw new InvalidOperationException("onError failed");
        });
        t.Record("last", new Dictionary<string, object?>());

        // The failing /log post reports first; the record must still go out and Dispose return.
        t.Dispose();

        Assert.Single(server.Records());
    }

    [Fact]
    public void ThrowingSettingsReadAsInactiveAndReportOnce()
    {
        using var server = new FakeDevlog();
        var errors = new ConcurrentQueue<string>();
        var t = new DevTelemetry("Test", () => throw new InvalidOperationException("config gone"), () => server.LogUrl,
            errors.Enqueue);

        Assert.False(t.Active);
        t.Record("probe", new Dictionary<string, object?>());
        t.Log("line");
        // Let the timer read Active a few times; a throw there would end the test host.
        Thread.Sleep(2500);
        t.Dispose();

        Assert.Empty(server.Records());
        Assert.Single(errors, e => e.Contains("config gone"));
    }

    [Fact]
    public void QueuedTimerFlushDoesNothingAfterDispose()
    {
        using var server = new FakeDevlog();
        int testThread = Environment.CurrentManagedThreadId;
        using var hold = new ManualResetEventSlim(true);
        using var held = new ManualResetEventSlim(false);
        bool on = true;
        bool released = false;
        int lateUrlReads = 0;
        var errors = new ConcurrentQueue<string>();
        var t = new DevTelemetry("Test", () => Environment.CurrentManagedThreadId != testThread || Volatile.Read(ref on), () =>
        {
            if (Environment.CurrentManagedThreadId != testThread)
            {
                if (Volatile.Read(ref released)) Interlocked.Increment(ref lateUrlReads);
                else if (!hold.IsSet)
                {
                    held.Set();
                    hold.Wait();
                }
            }
            return server.LogUrl;
        }, errors.Enqueue);
        Assert.True(SpinWait.SpinUntil(() => server.Posts("/log").Count > 0, Wait));

        // A timer flush is parked in its Active check, and so has not yet taken the flush gate.
        hold.Reset();
        Assert.True(held.Wait(Wait));
        t.Log("queued");
        // Toggled off, so Dispose's own flush leaves the line queued for the parked one to find.
        Volatile.Write(ref on, false);
        t.Dispose();
        Volatile.Write(ref released, true);
        hold.Set();
        Thread.Sleep(1000);

        // Reading url() again is the parked flush building a post for the disposed HttpClient.
        Assert.Equal(0, Volatile.Read(ref lateUrlReads));
        Assert.Empty(errors);
        Assert.DoesNotContain(server.Posts("/log"), p => p.Text.Contains("queued"));
    }

    [Fact]
    public void EntryPointsAreInertAfterDispose()
    {
        using var server = new FakeDevlog();
        int testThread = Environment.CurrentManagedThreadId;
        bool disposed = false;
        int consulted = 0;
        var t = new DevTelemetry("Test", () =>
        {
            if (Volatile.Read(ref disposed) && Environment.CurrentManagedThreadId == testThread) Interlocked.Increment(ref consulted);
            return true;
        }, () => server.LogUrl);
        t.Dispose();
        Volatile.Write(ref disposed, true);

        bool built = false;
        t.Log("late");
        t.Record("late", new Dictionary<string, object?>());
        t.Snapshot(() => { built = true; return "late"; }, 0);

        Assert.False(built);
        Assert.Equal(0, consulted);
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
        private readonly CancellationTokenSource stopping = new();
        private readonly Task serving;

        public volatile int LogStatus = 204;
        public volatile int RecordsStatus = 204;
        public volatile int LogDelayMs;
        public volatile int RecordsDelayMs;

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

                // Each request on its own task, so a delayed or aborted post never holds up the next.
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var path = req.Url!.AbsolutePath;
                received.AddOrUpdate(path, 1, (_, n) => n + 1);
                using var body = new MemoryStream();
                await req.InputStream.CopyToAsync(body);
                int delay = path == "/records" ? RecordsDelayMs : LogDelayMs;
                if (delay > 0) await Task.Delay(delay, stopping.Token);

                int status = path == "/records" ? RecordsStatus : LogStatus;
                posts.Enqueue(new Post(path, req.Url.Query, req.Headers["X-Devlog-Session"], req.Headers["X-Devlog-Offset"],
                    req.ContentType, status, body.ToArray()));
                ctx.Response.StatusCode = status;
                ctx.Response.Close();
            }
            catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The client gave up or the listener stopped mid-request.
            }
        }

        public void Dispose()
        {
            stopping.Cancel();
            listener.Stop();
            listener.Close();
            serving.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
