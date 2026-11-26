using System;
using System.Collections.Generic;
using System.Linq;
using XivHubPluginKit.Board;

namespace XivHubPluginKit.Board.Buying;

/// <summary>
/// The cheapest whole-listing cover of n units over a live board. <see cref="Taken"/> is cheapest
/// first; <see cref="Units"/> their summed <see cref="BoardListing.Qty"/>;
/// <see cref="Short"/> is <c>max(0, n − Units)</c>; <see cref="Listed"/> the summed
/// <see cref="BoardListing.UnitPrice"/> × <see cref="BoardListing.Qty"/>; <see cref="Gil"/> the
/// summed <see cref="BoardListing.Total"/>, the game's own totals, which equal
/// <see cref="BoardQuote.Gil"/> over the same listings since both charge each listing its own fee.
/// </summary>
public sealed record LiveCover(IReadOnlyList<BoardListing> Taken, int Units, int Short, long Listed, long Gil);

public static class LiveBoard
{
    /// <summary>
    /// The minimum-cost whole-listing cover of <paramref name="n"/> units of
    /// <paramref name="itemId"/> over <paramref name="live"/>, the objective and tie-break of
    /// <see cref="BoardQuoter.Quote"/> applied to this world's live listings: a listing can't be
    /// split, so a listing that crosses n is taken only as part of the minimum cover. The quote
    /// minimises each listing's gil plus <see cref="BoardQuoter.Fee"/>, the same per-listing fee
    /// the game reports as <see cref="BoardListing.TotalTax"/>, so the cover it picks is the
    /// cheapest by the game's own totals. Candidates
    /// are the listings of this item with a quantity and a price, of quality <paramref name="hq"/>
    /// (null takes both), excluding the player's own retainers. Null when <paramref name="n"/> is
    /// below 1 or there are no candidates.
    /// </summary>
    public static LiveCover? Cover(uint itemId, int n, IReadOnlyList<BoardListing> live,
        IReadOnlyCollection<ulong> ownRetainers, bool? hq = null)
    {
        if (n < 1)
            return null;

        var candidates = live
            .Where(l => l.ItemId == itemId && l.Qty > 0 && l.UnitPrice > 0 && (hq is null || l.Hq == hq)
                        && !ownRetainers.Contains(l.RetainerId))
            .ToList();
        if (candidates.Count == 0)
            return null;

        // Same order MarketItem.Create sorts by, with the world fixed: OrderBy/ThenBy is stable,
        // so listings tied on price, quantity and quality keep slot order.
        var sorted = candidates.OrderBy(l => l.UnitPrice).ThenBy(l => l.Qty).ThenBy(l => l.Hq).ToList();
        // The MarketItem constructor is used directly: Create's filtering and price re-sort have
        // already been applied above, and Create would lose the mapping back from a sorted Listing
        // to its BoardListing.
        var item = new MarketItem(
            itemId,
            sorted.Select(l => new Listing(l.UnitPrice, l.Qty, 0, l.Hq)).ToList(),
            0,
            DateTime.MinValue,
            DateTime.MinValue);
        var quote = BoardQuoter.Quote(item, n);
        if (quote is null)
            return null;

        var taken = quote.Taken.Select(i => sorted[i]).ToList();
        var units = taken.Sum(l => l.Qty);
        var listed = taken.Sum(l => l.UnitPrice * l.Qty);
        var gil = taken.Sum(l => l.Total);
        return new LiveCover(taken, units, Math.Max(0, n - units), listed, gil);
    }
}
