using System;
using XivHubPluginKit.Board;

namespace XivHubPluginKit.Pinch;

public static class PinchDecision
{
    /// <summary>
    /// Price one listing against what the board said.
    ///
    /// With a competitor, take the highest price still under the cheapest one.
    /// Our own retainers and mannequins were never in that number, so two
    /// retainers holding the same item land on the same price instead of racing
    /// each other down a gil per pass. With nobody to undercut, either leave it
    /// for a manual raise (config) or match the most recent sale, but only once
    /// <see cref="LivePrice.OffersComplete"/> says the board actually confirmed
    /// that, since offerings can still be paging in behind a history packet that
    /// landed first. A board that never answered holds at the current price
    /// rather than falling back to history, which is often a stale, lower number
    /// than what the item is really listed at.
    ///
    /// A unit an NPC will buy, or that a shop will sell you another of, has a
    /// worth the market cannot argue with. Undercutting past it is not a thin
    /// margin, it is a loss you chose, and against a gil shop the other side has
    /// unlimited stock, so the race has no bottom. Below that floor the listing
    /// is held rather than dropped to it: the market is under water either way,
    /// so nothing sells at the floor that would not have sold higher, and
    /// holding keeps the price for when the cheap stock clears. A listing
    /// already under the floor is raised back to it, the one case worth
    /// touching. The comparison is on the gil that reaches you, since the board
    /// takes its cut first.
    /// </summary>
    public static PriceDecision Decide(LivePrice result, uint curPrice, int floor, bool skipIfNoCompetitor)
    {
        uint target;
        string why;

        if (result.Competitor > 0)
        {
            target = result.Competitor > 1 ? result.Competitor - 1 : 1;
            why = $"undercutting {result.Competitor:N0}";
        }
        else if (!result.OffersComplete)
        {
            // The board never confirmed "nobody selling": offerings simply
            // never arrived in time. Falling back to HistoryPrice here would
            // undercut a live board we never actually saw, so hold instead.
            return new PriceDecision(null, "board did not answer; holding current price", true);
        }
        else if (skipIfNoCompetitor)
        {
            return new PriceDecision(null, "no live competitor; left as a raise-price opportunity", false);
        }
        else if (result.HistoryPrice == 0)
        {
            return new PriceDecision(null, "no live competitor and no sale history", false);
        }
        else
        {
            // Nobody else is selling, so the last sale can raise this listing
            // but never cut it: lowering the only copy on the board wins no
            // buyer from anyone.
            target = Math.Max(result.HistoryPrice, curPrice);
            why = "last sale; nobody else selling";
        }

        // Never sit above our own cheapest copy. Excluding our retainers from
        // the competitor set stops us undercutting ourselves, but it also lets
        // a listing stay stranded above one of our own; a buyer takes the
        // cheapest, so the dearer copy cannot sell until the cheaper one is
        // gone. Matching is idempotent, so two characters converging on one
        // price is stable in the way undercutting never was, and it clears
        // pairs of our own listings sitting a gil apart.
        if (result.OwnCheapest > 0 && target > result.OwnCheapest)
        {
            target = result.OwnCheapest;
            why = $"matching our own {result.OwnCheapest:N0}";
        }

        if (floor > 0 && target < floor)
        {
            return curPrice < floor
                ? new PriceDecision((uint)floor, $"was under the {floor:N0} profit floor", true)
                : new PriceDecision(null, $"held; {why} nets less than the {floor:N0} profit floor", true);
        }

        return target == curPrice
            ? new PriceDecision(null, "already at target", false)
            : new PriceDecision(target, why, true);
    }
}
