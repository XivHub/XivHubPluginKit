using System.Collections.Generic;
using XivHubPluginKit.Board;

namespace XivHubPluginKit.Pinch;

public static class PinchPlanning
{
    /// <summary>
    /// Which sell-list row indices still need a window opened.
    ///
    /// The sell list does not present rows in container-slot order, so there is
    /// no reliable index-to-item mapping; a plan targets rows by name and
    /// quality instead, and this narrows the visual list down to the rows a
    /// target or a live lookup actually claims. An unreadable list (empty
    /// <paramref name="visual"/>) visits every row: slow is the acceptable
    /// failure here, skipping a row that needed a reprice is not.
    /// </summary>
    public static List<int> RowsToVisit(
        IReadOnlyList<(string Name, bool Hq)> visual,
        int rowCount,
        IReadOnlyDictionary<(string, bool), uint> targets,
        IReadOnlyDictionary<(string, bool), uint> live)
    {
        var visit = new List<int>();
        if (visual.Count == 0)
        {
            for (int i = 0; i < rowCount; i++) visit.Add(i);
            return visit;
        }

        for (int i = 0; i < visual.Count; i++)
        {
            if (targets.ContainsKey(visual[i]) || live.ContainsKey(visual[i]))
                visit.Add(i);
        }
        return visit;
    }

    /// <summary>
    /// Whether a live-priced row still has to have its window opened.
    ///
    /// The board is asked once per item per session, so the second retainer
    /// holding a stack already knows the answer. Feeding that answer and the
    /// row's current price to the same <see cref="PinchDecision.Decide"/> the
    /// write path uses says whether anything would change, and a row where
    /// nothing would is one the run can leave shut. An item the board has not
    /// been asked about yet has to be visited, because opening the window is
    /// how it gets asked.
    /// </summary>
    public static bool NeedsVisit(LivePrice? cached, uint price, int floor, bool skipIfNoCompetitor)
    {
        if (cached is null) return true;

        return PinchDecision.Decide(cached.Value, price, floor, skipIfNoCompetitor).Write is not null;
    }
}
