using System.Collections.Generic;
using XivHubPluginKit.Board;
using XivHubPluginKit.Pinch;

namespace XivHubPluginKit.Tests;

public class PinchPlanningTests
{
    [Fact]
    public void AnUnreadableListVisitsEveryRow()
    {
        var visual = new List<(string, bool)>();
        var live = new Dictionary<(string, bool), uint>();

        var visit = PinchPlanning.RowsToVisit(visual, rowCount: 3, live);

        Assert.Equal(new List<int> { 0, 1, 2 }, visit);
    }

    [Fact]
    public void OnlyRowsClaimedByALiveLookupAreVisited()
    {
        var visual = new List<(string, bool)>
        {
            ("A", false),
            ("B", true),
            ("", false),
            ("A", true),
        };
        var live = new Dictionary<(string, bool), uint> { [("B", true)] = 42, [("A", true)] = 99 };

        var visit = PinchPlanning.RowsToVisit(visual, rowCount: visual.Count, live);

        Assert.Equal(new List<int> { 1, 3 }, visit);
    }

    [Fact]
    public void AnUncachedRowNeedsAVisit()
    {
        Assert.True(PinchPlanning.NeedsVisit(cached: null, price: 600, floor: 0, skipIfNoCompetitor: false));
    }

    [Fact]
    public void ACachedRowAlreadyAtTargetNeedsNoVisit()
    {
        var cached = new LivePrice(Competitor: 601, HistoryPrice: 0, OffersComplete: true);

        Assert.False(PinchPlanning.NeedsVisit(cached, price: 600, floor: 0, skipIfNoCompetitor: false));
    }

    [Fact]
    public void ACachedRowThatWouldWriteNeedsAVisit()
    {
        var cached = new LivePrice(Competitor: 500, HistoryPrice: 0, OffersComplete: true);

        Assert.True(PinchPlanning.NeedsVisit(cached, price: 600, floor: 0, skipIfNoCompetitor: false));
    }
}
