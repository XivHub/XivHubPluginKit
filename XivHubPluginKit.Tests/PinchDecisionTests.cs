using System.Globalization;
using XivHubPluginKit.Board;
using XivHubPluginKit.Pinch;

namespace XivHubPluginKit.Tests;

public class PinchDecisionTests
{
    public PinchDecisionTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Fact]
    public void UndercutsTheCheapestCompetitorByOne()
    {
        var result = new LivePrice(Competitor: 500, HistoryPrice: 0, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 0, skipIfNoCompetitor: false);

        Assert.Equal((uint?)499, d.Write);
        Assert.Equal("undercutting 500", d.Why);
    }

    [Fact]
    public void FormatsTheCompetitorPriceWithThousandsSeparators()
    {
        var result = new LivePrice(Competitor: 12000, HistoryPrice: 0, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 20000, floor: 0, skipIfNoCompetitor: false);

        Assert.Equal((uint?)11999, d.Write);
        Assert.Equal("undercutting 12,000", d.Why);
    }

    [Fact]
    public void NeverUndercutsBelowOne()
    {
        var result = new LivePrice(Competitor: 1, HistoryPrice: 0, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 5, floor: 0, skipIfNoCompetitor: false);

        Assert.Equal((uint?)1, d.Write);
    }

    [Fact]
    public void HoldsWhenAlreadyOneUnderTheCompetitor()
    {
        var result = new LivePrice(Competitor: 601, HistoryPrice: 0, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 0, skipIfNoCompetitor: false);

        Assert.Null(d.Write);
        Assert.Equal("already at target", d.Why);
    }

    [Fact]
    public void HoldsAndAnnouncesWhenTheBoardNeverAnswered()
    {
        var result = new LivePrice(Competitor: 0, HistoryPrice: 0, OffersComplete: false);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 0, skipIfNoCompetitor: false);

        Assert.Null(d.Write);
        Assert.True(d.Announce);
        Assert.Equal("board did not answer; holding current price", d.Why);
    }

    [Fact]
    public void HoldsWithoutAnnouncingWhenTheSkipFlagIsSet()
    {
        var result = new LivePrice(Competitor: 0, HistoryPrice: 400, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 0, skipIfNoCompetitor: true);

        Assert.Null(d.Write);
        Assert.False(d.Announce);
        Assert.Equal("no live competitor; left as a raise-price opportunity", d.Why);
    }

    [Fact]
    public void HoldsWhenThereIsNoCompetitorAndNoHistory()
    {
        var result = new LivePrice(Competitor: 0, HistoryPrice: 0, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 0, skipIfNoCompetitor: false);

        Assert.Null(d.Write);
        Assert.False(d.Announce);
        Assert.Equal("no live competitor and no sale history", d.Why);
    }

    [Fact]
    public void HoldsWhenTheLastSaleIsNotAboveTheCurrentPrice()
    {
        var result = new LivePrice(Competitor: 0, HistoryPrice: 400, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 0, skipIfNoCompetitor: false);

        Assert.Null(d.Write);
        Assert.Equal("already at target", d.Why);
    }

    [Fact]
    public void RaisesToTheLastSaleWhenNobodyElseIsSelling()
    {
        var result = new LivePrice(Competitor: 0, HistoryPrice: 800, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 0, skipIfNoCompetitor: false);

        Assert.Equal((uint?)800, d.Write);
        Assert.Equal("last sale; nobody else selling", d.Why);
    }

    [Fact]
    public void NeverSitsAboveOurOwnCheapestCopy()
    {
        var result = new LivePrice(Competitor: 500, HistoryPrice: 0, OffersComplete: true, OwnCheapest: 450);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 0, skipIfNoCompetitor: false);

        Assert.Equal((uint?)450, d.Write);
        Assert.Equal("matching our own 450", d.Why);
    }

    [Fact]
    public void HoldsWhenUndercuttingWouldNetLessThanTheProfitFloor()
    {
        var result = new LivePrice(Competitor: 500, HistoryPrice: 0, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 600, floor: 520, skipIfNoCompetitor: false);

        Assert.Null(d.Write);
        Assert.Equal("held; undercutting 500 nets less than the 520 profit floor", d.Why);
    }

    [Fact]
    public void RaisesBackToTheProfitFloorWhenUnderIt()
    {
        var result = new LivePrice(Competitor: 500, HistoryPrice: 0, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 510, floor: 520, skipIfNoCompetitor: false);

        Assert.Equal((uint?)520, d.Write);
        Assert.Equal("was under the 520 profit floor", d.Why);
    }

    [Fact]
    public void AZeroFloorNeverBinds()
    {
        var result = new LivePrice(Competitor: 2, HistoryPrice: 0, OffersComplete: true);

        var d = PinchDecision.Decide(result, curPrice: 10, floor: 0, skipIfNoCompetitor: false);

        Assert.Equal((uint?)1, d.Write);
        Assert.Equal("undercutting 2", d.Why);
    }
}
