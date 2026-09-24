using System.Collections.Generic;

namespace XivHubPluginKit.Board;

/// <summary>
/// One listing read off the board, mirroring ClientStructs <c>MarketBoardListing</c>.
/// <see cref="Total"/> is the X of the game's prompt "Purchase N &lt;name&gt; for X gil (Y fee
/// included)?" (market-board.md): <see cref="TotalTax"/> is the game's own fee, 5% rounded down
/// per listing, which <c>BoardQuoter.Fee</c> of <c>UnitPrice × Qty</c> reproduces, so
/// summing <see cref="Total"/> over several listings is what the game charges for buying each whole.
/// </summary>
public readonly record struct BoardListing(
    ulong ListingId,
    ulong RetainerId,
    uint ItemId,
    long UnitPrice,
    int Qty,
    long TotalTax,
    bool Hq)
{
    public long Total => UnitPrice * Qty + TotalTax;
}

/// <summary>
/// One read of the item search results window. <see cref="Listings"/> are the InfoProxy slots
/// <c>0..min(ListingCount, EntryCount, 100)</c> in slot order, the index
/// <c>ItemSearchResult [2, i]</c> takes; slots at or past <see cref="EntryCount"/> are the game's
/// unwritten ones, which it refuses to buy from.
/// </summary>
public sealed record BoardSnapshot(
    uint SearchItemId,
    uint ResultItemId,
    byte RequestId,
    int ListingCount,
    uint EntryCount,
    IReadOnlyList<BoardListing> Listings);

/// <summary>
/// One read of the board's search page: whether it has loaded, whether a typed search is still
/// running, and the result item ids in the order <c>ItemSearch [5, i]</c> indexes them.
/// </summary>
public sealed record SearchPage(bool Loaded, bool Searching, IReadOnlyList<uint> ItemIds);
