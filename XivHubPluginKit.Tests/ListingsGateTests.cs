using System.Collections.Generic;
using XivHubPluginKit.Board;

namespace XivHubPluginKit.Tests;

public class ListingsGateTests
{
    private const uint EarthShard = 5;
    private const uint FireShard = 2;

    private static BoardSnapshot Snapshot(uint resultItemId, byte requestId, uint itemId, int listingCount,
        uint entryCount) =>
        new(itemId, resultItemId, requestId, listingCount, entryCount,
            BuildListings(itemId, listingCount));

    private static IReadOnlyList<BoardListing> BuildListings(uint itemId, int count)
    {
        var listings = new List<BoardListing>(count);
        for (var i = 0; i < count; i++)
            listings.Add(new BoardListing((ulong)(i + 1), (ulong)(i + 1), itemId, 10, 1, 0, false));
        return listings;
    }

    [Fact]
    public void WaitsUntilTheRequestIdChanges()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 66, startMs: 0);
        var read = Snapshot(EarthShard, 66, EarthShard, 5, 5);

        Assert.Equal(GateState.Waiting, gate.Observe(read, 0));
        Assert.Equal(GateState.Waiting, gate.Observe(read, ListingsGate.SettleMs + 1));
    }

    [Fact]
    public void WaitsWhileSlotsHoldAnotherItem()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 66, startMs: 0);
        // Request 67 has landed, but the slots still hold Fire Shard's listings.
        var read = new BoardSnapshot(EarthShard, EarthShard, 67, 5, 5, BuildListings(FireShard, 5));

        Assert.Equal(GateState.Waiting, gate.Observe(read, 0));
        Assert.Equal(GateState.Waiting, gate.Observe(read, ListingsGate.SettleMs + 1));
    }

    [Fact]
    public void ReadyOnceNothingChangesForTheSettleWindow()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 66, startMs: 0);
        var read = Snapshot(EarthShard, 67, EarthShard, 4, 4);

        Assert.Equal(GateState.Waiting, gate.Observe(read, 0));
        Assert.Equal(GateState.Waiting, gate.Observe(read, ListingsGate.SettleMs - 1));
        Assert.Equal(GateState.Ready, gate.Observe(read, ListingsGate.SettleMs));
    }

    [Fact]
    public void ANewPageRestartsTheSettleWindow()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 66, startMs: 0);
        var first = Snapshot(EarthShard, 67, EarthShard, 10, 10);
        var second = Snapshot(EarthShard, 67, EarthShard, 20, 20);

        Assert.Equal(GateState.Waiting, gate.Observe(first, 0));
        Assert.Equal(GateState.Waiting, gate.Observe(second, 500));
        Assert.Equal(GateState.Waiting, gate.Observe(second, 1499));
        Assert.Equal(GateState.Ready, gate.Observe(second, 1500));
    }

    [Fact]
    public void ZeroListingsSettleAsNoListings()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 66, startMs: 0);
        var read = new BoardSnapshot(EarthShard, EarthShard, 67, 0, 0, []);

        Assert.Equal(GateState.Waiting, gate.Observe(read, 0));
        Assert.Equal(GateState.NoListings, gate.Observe(read, ListingsGate.SettleMs));
    }

    [Fact]
    public void AnUnansweredEmptyReadIsNoListingsAtTheDeadline()
    {
        // Ahriman Tears on Moogle: the request id never moved and nothing arrived.
        var gate = new ListingsGate(EarthShard, 66, 0);
        var read = new BoardSnapshot(EarthShard, EarthShard, 66, 0, 0, []);

        Assert.Equal(GateState.Waiting, gate.Observe(read, ListingsGate.TimeoutMs - 1));
        Assert.Equal(GateState.NoListings, gate.Observe(read, ListingsGate.TimeoutMs));
    }

    [Fact]
    public void AnUnansweredReadOfAnotherItemStillTimesOut()
    {
        var gate = new ListingsGate(EarthShard, 66, 0);
        var read = new BoardSnapshot(EarthShard, FireShard, 66, 0, 0, []);

        Assert.Equal(GateState.TimedOut, gate.Observe(read, ListingsGate.TimeoutMs));
    }

    [Fact]
    public void TimesOutWithoutACurrentRead()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 66, startMs: 0);

        Assert.Equal(GateState.Waiting, gate.Observe(null, ListingsGate.TimeoutMs - 1));
        Assert.Equal(GateState.TimedOut, gate.Observe(null, ListingsGate.TimeoutMs));
    }

    [Fact]
    public void WrongResultItemKeepsWaiting()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 66, startMs: 0);
        var read = new BoardSnapshot(EarthShard, FireShard, 67, 5, 5, BuildListings(EarthShard, 5));

        Assert.Equal(GateState.Waiting, gate.Observe(read, ListingsGate.SettleMs));
    }

    [Fact]
    public void RequestIdWrapsPast255()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 255, startMs: 0);
        var read = Snapshot(EarthShard, 0, EarthShard, 5, 5);

        Assert.Equal(GateState.Waiting, gate.Observe(read, 0));
        Assert.Equal(GateState.Ready, gate.Observe(read, ListingsGate.SettleMs));
    }

    [Fact]
    public void PartlyArrivedPagesAreNeverReady()
    {
        var gate = new ListingsGate(EarthShard, requestBefore: 66, startMs: 0);
        // 18 listings, only the first page of 10 written (37c13073e8c8 mid-arrival).
        var read = Snapshot(EarthShard, 67, EarthShard, 18, 10);

        Assert.Equal(GateState.Waiting, gate.Observe(read, 0));
        Assert.Equal(GateState.Waiting, gate.Observe(read, ListingsGate.SettleMs + 1));
    }
}
