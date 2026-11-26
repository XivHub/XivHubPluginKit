using System.Collections.Generic;
using XivHubPluginKit.Board;
using XivHubPluginKit.Board.Buying;

namespace XivHubPluginKit.Tests;

/// <summary>The buy line's quality, plan price, stop totals and ask policy.</summary>
public class BuyingTests
{
    private const uint Item = 5057;
    private static readonly IReadOnlyCollection<ulong> NoOwnRetainers = [];
    private static readonly BuyThresholds Defaults = new(5, 5000, 30, 30000);

    private static BoardListing Board(ulong id, long unitPrice, int qty, bool hq) =>
        new(id, 100 + id, Item, unitPrice, qty, BoardQuoter.Fee(unitPrice * qty), hq);

    // An NQ listing cheaper than both HQ ones, so a cover ignoring quality would take it.
    private static readonly List<BoardListing> Mixed =
    [
        Board(1, 100, 5, hq: false),
        Board(2, 300, 5, hq: true),
        Board(3, 400, 5, hq: true),
    ];

    private static BuyLine Line(bool? hq, int units, long unitGil) =>
        new(Item, "Widget", hq, false, units, PlanPrice.AtUnit(unitGil, units).Gil,
            n => PlanPrice.AtUnit(unitGil, n), "Mat");

    [Fact]
    public void AnHqCoverTakesOnlyHqListings()
    {
        var cover = LiveBoard.Cover(Item, 5, Mixed, NoOwnRetainers, hq: true)!;

        Assert.Equal([2UL], cover.Taken.Select(l => l.ListingId));
        Assert.Equal(1500, cover.Listed);
    }

    [Fact]
    public void AnNqCoverTakesOnlyNqListings()
    {
        var cover = LiveBoard.Cover(Item, 10, Mixed, NoOwnRetainers, hq: false)!;

        Assert.Equal([1UL], cover.Taken.Select(l => l.ListingId));
        Assert.Equal(5, cover.Short);
    }

    [Fact]
    public void EitherQualityTakesTheCheapest()
    {
        var cover = LiveBoard.Cover(Item, 5, Mixed, NoOwnRetainers)!;

        Assert.Equal([1UL], cover.Taken.Select(l => l.ListingId));
    }

    [Fact]
    public void NoListingOfTheQualityIsNoCover()
    {
        var nqOnly = new List<BoardListing> { Board(1, 100, 5, hq: false) };

        Assert.Null(LiveBoard.Cover(Item, 5, nqOnly, NoOwnRetainers, hq: true));
        Assert.True(BoardCompare.Compare(Line(true, 5, 100), 5, nqOnly, NoOwnRetainers)!.NoListings);
    }

    [Fact]
    public void AnHqLineIsComparedWithHqListingsOnly()
    {
        // Plan 300 a unit; the HQ board has 5 at 300, the NQ one cheaper at 100.
        var c = BoardCompare.Compare(Line(true, 5, 300), 5, Mixed, NoOwnRetainers)!;

        Assert.Equal(0, c.DiffPercent);
        Assert.Equal(BuyVerdict.Buy, BuyGuard.Verdict(c, Defaults).Verdict);
    }

    [Fact]
    public void AUnitPlanIsPricedAsOneListingWithItsFee()
    {
        Assert.Equal(new PlanPrice(3000, 3150), PlanPrice.AtUnit(1000, 3));
        Assert.Equal(new PlanPrice(19, 19), PlanPrice.AtUnit(19, 1));
    }

    [Fact]
    public void AStopCountsOnlyLinesWithUnitsLeft()
    {
        var stop = new BuyStop(73, [Line(null, 2, 100), Line(null, 0, 100), Line(true, 1, 200_000)]);

        Assert.Equal(2, stop.Open.Count());
        Assert.False(stop.Done);
        Assert.Equal(210 + 210_000, stop.GilLeft);
        // 5,000 (the gil floor) for the 210-gil line, 5% of 210,000 for the other.
        Assert.Equal(210 + 210_000 + 5000 + 10_500, BuyGuard.Budget(stop, Defaults));
        Assert.True(new BuyStop(73, [Line(null, 0, 100)]).Done);
    }

    [Theory]
    [InlineData(BuyVerdict.Ask, AskPolicy.Ask, BuyVerdict.Ask)]
    [InlineData(BuyVerdict.Ask, AskPolicy.SkipAbove, BuyVerdict.Skip)]
    [InlineData(BuyVerdict.Buy, AskPolicy.SkipAbove, BuyVerdict.Buy)]
    [InlineData(BuyVerdict.Skip, AskPolicy.SkipAbove, BuyVerdict.Skip)]
    [InlineData(BuyVerdict.Nothing, AskPolicy.SkipAbove, BuyVerdict.Nothing)]
    public void SkipAboveSkipsWhatWouldBeAsked(BuyVerdict verdict, AskPolicy policy, BuyVerdict expected) =>
        Assert.Equal(expected, BuyGuard.UnderPolicy(verdict, policy));
}
