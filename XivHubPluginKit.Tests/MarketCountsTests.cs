using XivHubPluginKit.Retainer;

namespace XivHubPluginKit.Tests;

public sealed class MarketCountsTests
{
    [Fact]
    public void UnseenRetainerUsesTheGameCount()
        => Assert.Equal(18, new MarketCounts().Resolve(1, 18));

    [Fact]
    public void SeenCountWinsWhileTheGameCountLags()
    {
        var counts = new MarketCounts();
        counts.Remember(1, live: 9, gameCount: 18);
        Assert.Equal(9, counts.Resolve(1, 18));
        Assert.Equal(9, counts.Resolve(1, 18));
    }

    [Fact]
    public void GameCountWinsOnceItMoves()
    {
        var counts = new MarketCounts();
        counts.Remember(1, live: 9, gameCount: 18);
        Assert.Equal(8, counts.Resolve(1, 8));
        Assert.Equal(18, counts.Resolve(1, 18));
    }

    [Fact]
    public void RetainersAreTrackedApart()
    {
        var counts = new MarketCounts();
        counts.Remember(1, live: 9, gameCount: 18);
        Assert.Equal(20, counts.Resolve(2, 20));
        Assert.Equal(9, counts.Resolve(1, 18));
    }
}
