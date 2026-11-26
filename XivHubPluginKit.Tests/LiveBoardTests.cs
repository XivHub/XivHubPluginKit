using System.Collections.Generic;
using System.Linq;
using XivHubPluginKit.Board;
using XivHubPluginKit.Board.Buying;

namespace XivHubPluginKit.Tests;

// Fixture: recorded listings, session 37c13073e8c8, seq 56, 74 and 147. Retainer ids are 1 to 5
// in slot order per item, since the recording didn't carry them.
public class LiveBoardTests
{
    private const uint FireShard = 2;
    private const uint EarthShard = 5;

    private static readonly List<BoardListing> FireShardListings =
    [
        new(9230197805876779245, 1, FireShard, 45, 1000, 2250, false),
        new(9230197805876779246, 2, FireShard, 45, 1000, 2250, false),
        new(9230197805876793093, 3, FireShard, 54, 960, 2592, false),
        new(9230197805876793038, 4, FireShard, 54, 960, 2592, false),
        new(9230197805874235834, 5, FireShard, 55, 313, 860, false),
    ];

    private static readonly List<BoardListing> EarthShardListings =
    [
        new(9230197805874098746, 1, EarthShard, 34, 237, 402, false),
        new(9229353380125816033, 2, EarthShard, 35, 1014, 1774, false),
        new(9230197805231473436, 3, EarthShard, 39, 212, 413, false),
        new(9225905312236034345, 4, EarthShard, 40, 3554, 7108, false),
    ];

    private static readonly IReadOnlyCollection<ulong> NoOwnRetainers = [];

    [Fact]
    public void CoverTakesTheCheapestWholeListings()
    {
        var cover = LiveBoard.Cover(FireShard, 1500, FireShardListings, NoOwnRetainers)!;

        Assert.Equal(2000, cover.Units);
        Assert.Equal(0, cover.Short);
        Assert.Equal(90000, cover.Listed);
        Assert.Equal(94500, cover.Gil);
        Assert.Equal(
            [9230197805876779245UL, 9230197805876779246UL],
            cover.Taken.Select(l => l.ListingId));
    }

    [Fact]
    public void CoverMapsBackToListingIds()
    {
        var reversed = FireShardListings.AsEnumerable().Reverse().ToList();
        var cover = LiveBoard.Cover(FireShard, 1500, reversed, NoOwnRetainers)!;

        Assert.Equal(
            new HashSet<ulong> { 9230197805876779245UL, 9230197805876779246UL },
            cover.Taken.Select(l => l.ListingId).ToHashSet());
    }

    [Fact]
    public void CoverGilIsTheGamesOwnTotals()
    {
        var cover = LiveBoard.Cover(EarthShard, 200, EarthShardListings, NoOwnRetainers)!;

        var taken = Assert.Single(cover.Taken);
        Assert.Equal(9230197805874098746UL, taken.ListingId);
        Assert.Equal(8058, cover.Listed);
        // The game's own total, 8,058 + 402, is BoardQuoter's charge for the same listing; 5% of
        // the total rounded up would be 8,461.
        Assert.Equal(8460, cover.Gil);
        Assert.Equal(cover.Listed + BoardQuoter.Fee(cover.Listed), cover.Gil);
    }

    // Every Fire Shard and Earth Shard listing recorded with its TotalTax, as (UnitPrice,
    // Quantity, TotalTax): session 37c13073e8c8 seq 56 and 60 (Fire Shard) and seq 147, 149 and
    // 150 (Earth Shard); session 108ba2790349 seq 13 recorded the same Earth Shard listings.
    [Theory]
    [InlineData(45, 1000, 2250)]
    [InlineData(54, 960, 2592)]
    [InlineData(55, 313, 860)]
    [InlineData(60, 360, 1080)]
    [InlineData(221, 9999, 110488)]
    [InlineData(221, 9922, 109638)]
    [InlineData(222, 2222, 24664)]
    [InlineData(299, 7169, 107176)]
    [InlineData(300, 2000, 30000)]
    [InlineData(333, 3333, 55494)]
    [InlineData(333, 2931, 48801)]
    [InlineData(444, 1111, 24664)]
    [InlineData(666, 1111, 36996)]
    [InlineData(1000, 100, 5000)]
    [InlineData(10000, 129, 64500)]
    [InlineData(34, 237, 402)]
    [InlineData(35, 1014, 1774)]
    [InlineData(39, 212, 413)]
    [InlineData(40, 3554, 7108)]
    [InlineData(49, 6480, 15876)]
    [InlineData(50, 5000, 12500)]
    [InlineData(51, 500, 1275)]
    [InlineData(52, 9120, 23712)]
    [InlineData(53, 8640, 22896)]
    [InlineData(54, 1000, 2700)]
    [InlineData(55, 9999, 27497)]
    [InlineData(58, 9360, 27144)]
    [InlineData(59, 7440, 21948)]
    [InlineData(59, 5280, 15576)]
    [InlineData(77, 8640, 33264)]
    [InlineData(77, 5043, 19415)]
    [InlineData(78, 9239, 36032)]
    [InlineData(81, 1000, 4050)]
    [InlineData(82, 9999, 40995)]
    [InlineData(82, 9001, 36904)]
    [InlineData(83, 9999, 41495)]
    [InlineData(83, 9990, 41458)]
    [InlineData(250, 500, 6250)]
    [InlineData(300, 3120, 46800)]
    [InlineData(333, 333, 5544)]
    public void FeeIsTheRecordedTotalTax(long unitPrice, int qty, long totalTax) =>
        Assert.Equal(totalTax, BoardQuoter.Fee(unitPrice * qty));

    [Fact]
    public void EqualListingsKeepSlotOrder()
    {
        var tied = FireShardListings.Where(l => l.UnitPrice == 54).ToList();
        var cover = LiveBoard.Cover(FireShard, 960, tied, NoOwnRetainers)!;

        var taken = Assert.Single(cover.Taken);
        Assert.Equal(9230197805876793093UL, taken.ListingId);
    }

    [Fact]
    public void ShortBoardTakesEverything()
    {
        var cover = LiveBoard.Cover(EarthShard, 6000, EarthShardListings, NoOwnRetainers)!;

        Assert.Equal(5017, cover.Units);
        Assert.Equal(983, cover.Short);
        Assert.Equal(4, cover.Taken.Count);
    }

    [Fact]
    public void OtherItemsSlotsAreIgnored()
    {
        var mixed = EarthShardListings.Concat(FireShardListings).ToList();
        var cover = LiveBoard.Cover(EarthShard, 200, mixed, NoOwnRetainers)!;

        var taken = Assert.Single(cover.Taken);
        Assert.Equal(9230197805874098746UL, taken.ListingId);
        Assert.Equal(8058, cover.Listed);
        Assert.Equal(8460, cover.Gil);
    }

    [Fact]
    public void OwnRetainersListingsAreNeverTaken()
    {
        var cover = LiveBoard.Cover(FireShard, 1000, FireShardListings, new HashSet<ulong> { 1 })!;

        var taken = Assert.Single(cover.Taken);
        Assert.Equal(9230197805876779246UL, taken.ListingId);
    }

    [Fact]
    public void EmptyBoardHasNoCover()
    {
        Assert.Null(LiveBoard.Cover(FireShard, 100, [], NoOwnRetainers));
    }

    [Fact]
    public void NeedBelowOneHasNoCover()
    {
        Assert.Null(LiveBoard.Cover(FireShard, 0, FireShardListings, NoOwnRetainers));
    }
}
