using System.Collections.Generic;
using System.Linq;
using XivHubPluginKit.Board;

namespace XivHubPluginKit.Board.Buying;

/// <summary>
/// A buy line's plan against the board now. <see cref="Live"/> is null when the board
/// holds no candidate listings (<see cref="NoListings"/>). <see cref="DiffPercent"/> compares
/// gil before the fee (<see cref="LiveCover.Listed"/> against <see cref="PlanListed"/>);
/// <see cref="LiveCover.Gil"/> and <see cref="PlanGil"/> include each listing's fee, charged the
/// same way on both sides (<see cref="BoardQuoter.Fee"/>).
/// </summary>
public sealed record BoardComparison(
    int Asked,
    int PlanUnits,
    long PlanListed,
    long PlanGil,
    LiveCover? Live,
    double? DiffPercent)
{
    public bool NoListings => Live is null;
}

public static class BoardCompare
{
    /// <summary>
    /// Compares <paramref name="line"/>'s plan with <paramref name="live"/>'s current listings of
    /// the line's quality for <paramref name="asked"/> units. Null when <paramref name="asked"/> is
    /// below 1. With no live cover, <see cref="BoardComparison.PlanUnits"/> and the plan figures are
    /// 0 and <see cref="BoardComparison.DiffPercent"/> is null. Otherwise
    /// <see cref="BoardComparison.PlanUnits"/> is the units the board can supply now
    /// (<c>asked − Live.Short</c>), priced by <see cref="BuyLine.Plan"/>.
    /// </summary>
    public static BoardComparison? Compare(BuyLine line, int asked, IReadOnlyList<BoardListing> live,
        IReadOnlyCollection<ulong> ownRetainers)
    {
        if (asked < 1)
            return null;

        var cover = LiveBoard.Cover(line.ItemId, asked, live, ownRetainers, line.Hq);
        if (cover is null)
            return new BoardComparison(asked, 0, 0, 0, null, null);

        var planUnits = asked - cover.Short;
        var plan = planUnits > 0 ? line.Plan(planUnits) : null;
        var planListed = plan?.Listed ?? 0;
        var planGil = plan?.Gil ?? 0;
        // A line with no plan price for these units leaves nothing to compare.
        double? diffPercent = planListed > 0 ? (cover.Listed - planListed) * 100.0 / planListed : null;
        return new BoardComparison(asked, planUnits, planListed, planGil, cover, diffPercent);
    }
}
