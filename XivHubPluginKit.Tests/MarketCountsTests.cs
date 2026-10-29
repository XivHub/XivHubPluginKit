using XivHubPluginKit.Retainer;

namespace XivHubPluginKit.Tests;

public sealed class MarketCountsTests
{
    [Fact]
    public void UnseenRetainerUsesTheGameCount()
        => Assert.Equal(18, new MarketCounts().Resolve(1, 18).Listings);

    [Fact]
    public void SeenCountWinsWhileTheGameCountLags()
    {
        var counts = new MarketCounts();
        counts.Remember(1, live: 9, gameCount: 18, readAt: 100);
        Assert.Equal(9, counts.Resolve(1, 18).Listings);
        Assert.Equal(9, counts.Resolve(1, 18).Listings);
    }

    [Fact]
    public void GameCountWinsOnceItMoves()
    {
        var counts = new MarketCounts();
        counts.Remember(1, live: 9, gameCount: 18, readAt: 100);
        Assert.Equal(8, counts.Resolve(1, 8).Listings);
        Assert.Equal(18, counts.Resolve(1, 18).Listings);
    }

    [Fact]
    public void RetainersAreTrackedApart()
    {
        var counts = new MarketCounts();
        counts.Remember(1, live: 9, gameCount: 18, readAt: 100);
        Assert.Equal(20, counts.Resolve(2, 20).Listings);
        Assert.Equal(9, counts.Resolve(1, 18).Listings);
    }

    [Fact]
    public void ReadTimeComesOnlyWithTheSeenCount()
    {
        var counts = new MarketCounts();
        counts.Remember(1, live: 9, gameCount: 18, readAt: 100);
        Assert.Equal(100, counts.Resolve(1, 18).ReadAt);
        Assert.Null(counts.Resolve(1, 8).ReadAt);
        Assert.Null(counts.Resolve(2, 20).ReadAt);
    }
}
