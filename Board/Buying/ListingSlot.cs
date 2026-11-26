using System.Collections.Generic;
using XivHubPluginKit.Board;

namespace XivHubPluginKit.Board.Buying;

/// <summary>Where a listing being bought sits in a board read.</summary>
public enum SlotState { Found, Gone, Changed }

/// <summary>
/// A listing's slot in a read: <see cref="Index"/> is its position in
/// <see cref="BoardSnapshot.Listings"/>, -1 when <see cref="SlotState.Gone"/>.
/// </summary>
public readonly record struct SlotRead(SlotState State, int Index);

public static class ListingSlot
{
    /// <summary>
    /// The slot holding <paramref name="listing"/>, looked up by <see cref="BoardListing.ListingId"/>
    /// since a buy shifts later listings down one slot. <see cref="SlotState.Changed"/> when the
    /// slot's price, quantity or fee differs from <paramref name="listing"/>'s.
    /// </summary>
    public static SlotRead Find(IReadOnlyList<BoardListing> listings, BoardListing listing)
    {
        for (var i = 0; i < listings.Count; i++)
        {
            var now = listings[i];
            if (now.ListingId != listing.ListingId) continue;
            var same = now.UnitPrice == listing.UnitPrice && now.Qty == listing.Qty && now.TotalTax == listing.TotalTax;
            return new SlotRead(same ? SlotState.Found : SlotState.Changed, i);
        }
        return new SlotRead(SlotState.Gone, -1);
    }

    /// <summary>
    /// Whether answering the purchase prompt Yes now buys <paramref name="listing"/>, clicked in
    /// slot <paramref name="clickedIndex"/>. The game's Yes handler copies
    /// <c>InfoProxyItemSearch.Listings[AgentItemSearch.ResultSelectedIndex]</c> as it is at that
    /// moment into <c>LastPurchasedMarketboardItem</c>, refusing an index at or past
    /// <c>EntryCount</c>, then sends the purchase (market-board.md "Search and buy flow"). So the
    /// read must still show the listing's item, hold the listing unchanged in the clicked slot,
    /// have <paramref name="selectedIndex"/> on that slot, and have written that slot
    /// (<see cref="BoardSnapshot.EntryCount"/>). A null read or selected index is false.
    /// </summary>
    public static bool ConfirmBuys(BoardSnapshot? read, BoardListing listing, int clickedIndex, uint? selectedIndex) =>
        read is not null
        && clickedIndex >= 0
        && read.ResultItemId == listing.ItemId
        && Find(read.Listings, listing) == new SlotRead(SlotState.Found, clickedIndex)
        && selectedIndex == (uint)clickedIndex
        && (uint)clickedIndex < read.EntryCount;
}
