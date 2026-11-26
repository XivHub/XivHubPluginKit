using System;
using System.Collections.Generic;
using System.Linq;

namespace XivHubPluginKit.Board.Buying;

/// <summary>One market board listing: price per unit before tax, units, world and quality.</summary>
public readonly record struct Listing(long UnitPrice, int Qty, uint WorldId, bool Hq);

/// <summary>
/// One item's listings on a data centre, cheapest first, with the board's total units for sale
/// (which can exceed the listings fetched) and when Universalis last saw an upload and when we
/// fetched it. <see cref="UploadedUtc"/> is <see cref="DateTime.MinValue"/> when the board holds none.
/// </summary>
public sealed record MarketItem(
    uint ItemId,
    IReadOnlyList<Listing> Listings,
    int UnitsForSale,
    DateTime UploadedUtc,
    DateTime FetchedUtc)
{
    // The game's largest stack; no listing holds more.
    private const int MaxStack = 9999;

    /// <summary>
    /// Sorts listings by price, then quantity, then world, then HQ (NQ first): plan_house.py:155
    /// sorts <c>(price, qty, world, hq)</c> tuples, and <see cref="BoardQuoter.Quote"/> must break
    /// ties the same way to take the same listings. A listing with no units or no price is
    /// dropped and a quantity above the game's stack cap of 9,999 is clamped to it, so a bad
    /// upload can't add a zero-unit world fill or route stop, and unit sums over a board stay far
    /// from <see cref="int.MaxValue"/>.
    /// </summary>
    public static MarketItem Create(
        uint itemId,
        IEnumerable<Listing> listings,
        int unitsForSale,
        DateTime uploadedUtc,
        DateTime fetchedUtc) =>
        new(itemId,
            listings
                .Where(l => l is { Qty: > 0, UnitPrice: > 0 })
                .Select(l => l.Qty > MaxStack ? l with { Qty = MaxStack } : l)
                .OrderBy(l => l.UnitPrice).ThenBy(l => l.Qty).ThenBy(l => l.WorldId).ThenBy(l => l.Hq)
                .ToList(),
            unitsForSale,
            uploadedUtc,
            fetchedUtc);
}

