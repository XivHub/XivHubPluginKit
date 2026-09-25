using System.Collections.Generic;

namespace XivHubPluginKit.Board;

/// <summary>
/// What the market board says about one item, and nothing else. No policy: the
/// caller decides what to do with an undercut opportunity, because repricing an
/// existing listing and pricing a new one want different answers.
/// <para>
/// <paramref name="Competitor"/> is the cheapest undercuttable unit price on the
/// board, 0 when nobody else is selling. <paramref name="HistoryPrice"/> is the
/// most recent sale's unit price from the history window, 0 when there is none.
/// </para>
/// <para>
/// <paramref name="OffersComplete"/> is whether the offerings side of the reply
/// actually answered: an offerings packet for this item arrived, or the board
/// signalled explicitly that it holds no listings. History arrives in its own
/// packet and often lands first, so <c>Competitor == 0</c> is ambiguous on its
/// own: it means "confirmed nobody undercuttable" when this is true, and "the
/// board never answered" when it is false. The default value is <c>false</c> on
/// purpose, so a <see cref="LivePrice"/> built without deciding this explicitly
/// reads as unanswered rather than as a false "nobody selling".
/// </para>
/// </summary>
public readonly record struct LivePrice(uint Competitor, uint HistoryPrice, bool OffersComplete = false, uint OwnCheapest = 0);

/// <summary>One ask-ladder row as the board sent it. <paramref name="Own"/> is
/// whether the listing belongs to one of the caller's own retainers.</summary>
public sealed record ObservedListing(uint UnitPrice, uint Qty, bool Hq, bool Own);

/// <summary>One sale-history row. <paramref name="Ts"/> is the purchase time in
/// unix seconds.</summary>
public sealed record ObservedSale(uint UnitPrice, uint Qty, bool Hq, long Ts);

/// <summary>
/// The full reply to one board lookup, from <c>MarketBoardListener.Observation</c>
/// (not a cref: this file is BCL-only and is linked without the listener).
/// <para>
/// <paramref name="OffersComplete"/> has the meaning it has on
/// <see cref="LivePrice"/>: an offerings packet or an explicit empty-board
/// signal arrived. When it is false, <paramref name="Listings"/> holds whatever
/// partial ladder arrived before the lookup ended (often nothing), which cannot
/// be told apart from an empty board by its contents alone; check the flag
/// before reading an empty list as "nobody selling".
/// </para>
/// <para>
/// <paramref name="Listings"/> holds every row seen, own and HQ-mismatched
/// included, up to the first 20. <paramref name="History"/> is the game's whole
/// history window for the item (at most 20 rows), not filtered by quality.
/// </para>
/// </summary>
public sealed record BoardObservation(
    bool OffersComplete, IReadOnlyList<ObservedListing> Listings, IReadOnlyList<ObservedSale> History);
