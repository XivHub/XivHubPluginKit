using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Network.Structures;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace XivHubPluginKit.Board;

/// <summary>
/// Listens for in-game market-board "current offerings" packets (the data that
/// arrives when the player clicks "Compare Prices" on a RetainerSell window,
/// or opens an item's listings at a market board) and exposes the cheapest
/// competitor price for the most recently requested item, plus the full
/// ladder and history through <see cref="Observation"/>. Read by
/// <see cref="LivePriceProbe"/> for every Compare Prices lookup, and directly
/// by any caller that opens an item's listings at a market board itself. One
/// request is awaited at a time and a new BeginRequest replaces it, so callers
/// keep their lookups apart: a plugin runs one lookup session at a time, and
/// <see cref="EndRequest"/> ends only the request its caller began.
///
/// FFXIV rate-limits market-board price requests server-side; the caller is
/// responsible for spacing requests. This class only records what comes back.
/// </summary>
public sealed class MarketBoardListener : IDisposable
{
    private readonly IMarketBoard _mb;
    private readonly IPluginLog _log;
    private readonly Hook<PacketDispatcher.Delegates.HandleMarketBoardItemRequestStartPacket> _requestStartHook;

    private readonly object _gate = new();
    // The item we're currently waiting on a result for (0 = not waiting).
    private uint _awaitingItemId;
    private bool _awaitingHq;
    // The player's own retainer CIDs, set per request so "is this my listing?"
    // is decided against the caller's current roster.
    private HashSet<ulong> _ownRetainerCids = new();
    // Cheapest genuine competitor for the awaited item; null until one is seen.
    // Our own retainers and housing mannequins are excluded (see OnOfferings).
    private uint? _cheapestCompetitor;
    private uint? _cheapestOwn;
    // An offerings packet for the awaited item has arrived (with or without an
    // undercuttable listing in it); _boardEmpty additionally means the board
    // came back with no listings at all.
    private bool _offeringsArrived;
    private bool _boardEmpty;
    // How many offerings packets for the awaited item have been folded in. The
    // caller watches this to tell "the reply has gone quiet" from "the reply has
    // not started", because offerings arrive ten to a packet and the cheapest
    // competitor can sit on any of them.
    private int _offeringsPackets;
    // History-window fallback: the most recent sale's unit price (HQ-matched) and
    // whether a history packet for the awaited item has arrived at all. The game
    // answers a "Compare Prices" lookup with BOTH an offerings packet and a
    // history packet; when nothing is currently listed it often sends only the
    // history packet, so this is what lets a repricer price an item nobody else is
    // selling instead of waiting out the offerings timeout.
    private uint? _mostRecentSale;
    private bool _historyArrived;
    // Status of the awaited request's start packet (see OnRequestStart); null
    // until that packet arrives.
    private uint? _requestStatus;

    // Full ladder and history for the awaited item, accumulated alongside the
    // two reduced values above, which TryGetCheapestCompetitor and
    // TryGetHistory read. Returned as copies by Observation.
    //
    // Offerings arrive ten rows to a packet, cheapest first, and a search can
    // run many pages deep. 20 rows (two packets) is kept: that is already
    // past the depth an undercut chain reaches before a repricer would act on
    // the price, so the tail of a 100-row ladder would cost memory and copies
    // for data no pricing decision uses. History needs no matching cap: the
    // game itself caps a single history reply at 20 rows
    // (MarketBoardHistory.Read reads a fixed 20-slot packet).
    private const int MaxLadderDepth = 20;
    private readonly List<ObservedListing> _ladder = new();
    private readonly List<ObservedSale> _history = new();

    public MarketBoardListener(IMarketBoard mb, IGameInteropProvider interop, IPluginLog log)
    {
        _mb = mb;
        _log = log;
        _mb.OfferingsReceived += OnOfferings;
        _mb.HistoryReceived += OnHistory;
        _requestStartHook = interop.HookFromAddress<PacketDispatcher.Delegates.HandleMarketBoardItemRequestStartPacket>(
            PacketDispatcher.Addresses.HandleMarketBoardItemRequestStartPacket.Value, OnRequestStart);
        _requestStartHook.Enable();
    }

    /// <summary>
    /// The server opens every offerings reply with a packet holding a status
    /// (0 on success, 0x70000003 when rate limited) and how many listings will
    /// follow. A board with nothing on it answers 0 and sends no offerings page
    /// at all, so this packet is the only thing that tells an empty board from
    /// a reply that never came. Compare Prices from a retainer leaves out that
    /// retainer's own listings, so a board holding only them answers 0 as well.
    /// Dalamud reads the same packet for its Universalis upload but does not
    /// expose it on <see cref="IMarketBoard"/>.
    /// <para>
    /// The packet carries no item id. Only one search runs at a time and this
    /// packet precedes that search's pages, so it belongs to the awaited item
    /// provided no page has been folded in yet.
    /// </para>
    /// </summary>
    private unsafe void OnRequestStart(uint targetId, nint packet)
    {
        try
        {
            uint status = *(uint*)packet;
            uint expected = *(uint*)(packet + 4);
            lock (_gate)
            {
                if (_awaitingItemId != 0 && _offeringsPackets == 0)
                {
                    _log.Debug("MarketBoardListener: reply for item {Item} opens with status 0x{Status:X}, {Expected} listing(s)",
                        _awaitingItemId, status, expected);
                    _requestStatus = status;
                    if (status == 0 && expected == 0)
                    {
                        _offeringsArrived = true;
                        _boardEmpty = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MarketBoardListener: error handling request start");
        }

        _requestStartHook.Original(targetId, packet);
    }

    /// <summary>
    /// Begin waiting for offerings for the given item. Clears any prior result.
    /// Call immediately before clicking "Compare Prices". <paramref name="ownRetainerCids"/>
    /// is the player's retainer CID set, used to flag self-listings.
    /// </summary>
    public void BeginRequest(uint itemId, bool hq, HashSet<ulong> ownRetainerCids)
    {
        lock (_gate)
        {
            _awaitingItemId = itemId;
            _awaitingHq = hq;
            _ownRetainerCids = ownRetainerCids;
            _cheapestCompetitor = null;
            _cheapestOwn = null;
            _offeringsArrived = false;
            _boardEmpty = false;
            _offeringsPackets = 0;
            _mostRecentSale = null;
            _historyArrived = false;
            _requestStatus = null;
            _ladder.Clear();
            _history.Clear();
        }
    }

    /// <summary>
    /// The cheapest unit price we may undercut for the awaited item, or null if
    /// no such listing has been seen. Our own retainers and housing mannequins
    /// are not competitors and never appear here.
    /// <paramref name="offeringsArrived"/> is true once any offerings packet for
    /// the item has been processed; <paramref name="boardEmpty"/> additionally
    /// means the board reported no listings at all.
    /// </summary>
    public uint? TryGetCheapestCompetitor(out bool offeringsArrived, out bool boardEmpty)
    {
        lock (_gate)
        {
            offeringsArrived = _offeringsArrived;
            boardEmpty = _boardEmpty;
            return _cheapestCompetitor;
        }
    }

    /// <summary>Cheapest of OUR OWN listings of the awaited item, any character,
    /// or null when we hold none. Never a thing to undercut; a thing not to sit
    /// above.</summary>
    public uint? TryGetCheapestOwn()
    {
        lock (_gate) return _cheapestOwn;
    }

    /// <summary>
    /// Status of the awaited request's start packet: 0 when the server
    /// answered, 0x70000003 when it refused a rate-limited request, and null
    /// while no start packet has arrived since <see cref="BeginRequest"/>.
    /// </summary>
    public uint? RequestStatus
    {
        get { lock (_gate) return _requestStatus; }
    }

    /// <summary>
    /// Offerings packets folded in for the awaited item since <see cref="BeginRequest"/>.
    /// Rises once per page, so a caller can restart its own settle window each
    /// time the reply grows.
    /// </summary>
    public int OfferingsPackets
    {
        get { lock (_gate) return _offeringsPackets; }
    }

    /// <summary>
    /// The most recent sale's unit price from the history window (HQ-matched),
    /// or null if no qualifying sale was seen. <paramref name="historyArrived"/>
    /// is true once a history packet for the awaited item has been processed
    /// (even an empty one): that's the signal that the board has no
    /// current listings rather than the request still being in flight.
    /// </summary>
    public uint? TryGetHistory(out bool historyArrived)
    {
        lock (_gate)
        {
            historyArrived = _historyArrived;
            return _mostRecentSale;
        }
    }

    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        try
        {
            uint awaitId;
            bool wantHq;
            HashSet<ulong> own;
            lock (_gate)
            {
                awaitId = _awaitingItemId;
                wantHq = _awaitingHq;
                own = _ownRetainerCids;
            }
            if (awaitId == 0) return;

            var listings = offerings.ItemListings;
            if (listings == null || listings.Count == 0)
            {
                lock (_gate)
                {
                    if (_awaitingItemId != awaitId) return;
                    // An empty packet carries no item id, so it cannot be
                    // attributed. Honour it only before any page for this item
                    // has arrived; past that it is someone else's search, or a
                    // terminator, and the pages already folded in are the truth.
                    if (_offeringsPackets > 0) return;
                    _offeringsArrived = true;
                    _boardEmpty = true;
                }
                return;
            }

            // Offerings only ever concern one item; confirm it matches what we
            // asked for (offerings arrive in batches of ~10, possibly several
            // packets; keep the running minimum across them).
            if (listings[0].ItemId != awaitId) return;

            // Only genuine competitors anchor the price:
            //  - our own retainers are excluded, so a second retainer selling the
            //    same item can never make us undercut ourselves;
            //  - housing mannequins are display stock, not a market anchor;
            //  - HQ: when selling an HQ item, only HQ listings are comparable.
            var candidates = listings
                .Where(l => (!wantHq || l.IsHq) && !l.OnMannequin && !own.Contains(l.RetainerId))
                .ToList();
            uint? best = candidates.Count == 0 ? null : candidates.Min(l => l.PricePerUnit);
            // Our own cheapest of the same item, on any character. Excluded from
            // `candidates` so it can never be undercut, but a listing priced
            // ABOVE it is one we will never sell, since a buyer takes the
            // cheapest and ours is in the way of our own.
            var mine = listings
                .Where(l => (!wantHq || l.IsHq) && !l.OnMannequin && own.Contains(l.RetainerId))
                .ToList();
            uint? ours = mine.Count == 0 ? null : mine.Min(l => l.PricePerUnit);

            lock (_gate)
            {
                // Only the item id is matched (not a per-request token; offerings
                // packets carry none). On a LivePriceProbe retry the awaited
                // item is unchanged, so a late packet from the prior request cycle
                // is accepted: harmless, since it's the same item's current price.
                if (_awaitingItemId != awaitId) return; // a new request superseded us
                _offeringsArrived = true;
                _boardEmpty = false;
                _offeringsPackets++;
                if (best is not null && (_cheapestCompetitor is null || best < _cheapestCompetitor))
                    _cheapestCompetitor = best;
                if (ours is not null && (_cheapestOwn is null || ours < _cheapestOwn))
                    _cheapestOwn = ours;

                // Raw ladder for Observation: every row this packet
                // carried, not just the undercuttable candidates above.
                foreach (var l in listings)
                {
                    if (_ladder.Count >= MaxLadderDepth) break;
                    _ladder.Add(new ObservedListing(
                        UnitPrice: l.PricePerUnit,
                        Qty: l.ItemQuantity,
                        Hq: l.IsHq,
                        Own: own.Contains(l.RetainerId)));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MarketBoardListener: error handling offerings");
        }
    }

    private void OnHistory(IMarketBoardHistory history)
    {
        try
        {
            uint awaitId;
            bool wantHq;
            lock (_gate)
            {
                awaitId = _awaitingItemId;
                wantHq = _awaitingHq;
            }
            if (awaitId == 0 || history.ItemId != awaitId) return;

            var listings = history.HistoryListings;
            // Match HQ the same way offerings do: when selling HQ, only HQ sales
            // are comparable; otherwise any sale counts.
            var sales = listings == null
                ? new List<IMarketBoardHistoryListing>()
                : listings.Where(l => !wantHq || l.IsHq).ToList();
            // Most recent qualifying sale (newest purchase time). SalePrice is the
            // per-unit price, matching offerings' PricePerUnit.
            var recent = sales.OrderByDescending(l => l.PurchaseTime).FirstOrDefault();

            lock (_gate)
            {
                if (_awaitingItemId != awaitId) return; // a new request superseded us
                _historyArrived = true;
                _mostRecentSale = recent is null ? null : recent.SalePrice;

                // The game answers a history request with one packet holding
                // its whole window (up to 20 rows), not pages like offerings,
                // so this replaces rather than accumulates. Unfiltered by
                // wantHq: Observation returns every row seen and lets its
                // caller filter, unlike _mostRecentSale above which exists
                // specifically to match the item being sold.
                _history.Clear();
                foreach (var l in listings ?? [])
                {
                    _history.Add(new ObservedSale(
                        UnitPrice: l.SalePrice,
                        Qty: l.Quantity,
                        Hq: l.IsHq,
                        Ts: ((DateTimeOffset)l.PurchaseTime).ToUnixTimeSeconds()));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MarketBoardListener: error handling history");
        }
    }

    /// <summary>
    /// The full reply to the request that just resolved to
    /// (<paramref name="itemId"/>, <paramref name="hq"/>), if the listener
    /// actually holds data for it: copies of the ladder and history, and
    /// whether the offerings side answered. Call it at the point the caller
    /// already decides a reply is done (<see cref="LivePriceProbe"/> does so
    /// for every Compare Prices lookup; a market-board search caller does so
    /// once its listings have settled), so this class needs no polling loop or
    /// timer of its own, and fires no board lookup beyond the one the caller
    /// made.
    /// <para>
    /// Guarded by an (item, hq) match against what this listener is actually
    /// holding, so a call for a request that never fired (a per-session
    /// lookup cap skipping an item without calling <see cref="BeginRequest"/>)
    /// returns null instead of a stale, unrelated item's ladder. Also null
    /// when neither offerings nor history arrived.
    /// </para>
    /// </summary>
    public BoardObservation? Observation(uint itemId, bool hq)
    {
        lock (_gate)
        {
            if (_awaitingItemId != itemId || _awaitingHq != hq) return null;
            if (!_offeringsArrived && !_historyArrived) return null; // nothing came back at all
            return new BoardObservation(
                _offeringsArrived,
                new List<ObservedListing>(_ladder),
                new List<ObservedSale>(_history));
        }
    }

    /// <summary>
    /// Stop waiting on the current request. Offerings and history that arrive
    /// afterwards, such as a board the player then browses by hand, are no
    /// longer folded into it, and <see cref="Observation"/> returns null
    /// until the next <see cref="BeginRequest"/>. A no-op unless
    /// (<paramref name="itemId"/>, <paramref name="hq"/>) is the awaited
    /// request, so it never ends a request another caller has begun since.
    /// </summary>
    public void EndRequest(uint itemId, bool hq)
    {
        lock (_gate)
        {
            if (_awaitingItemId != itemId || _awaitingHq != hq) return;
            _awaitingItemId = 0;
            _awaitingHq = false;
        }
    }

    public void Dispose()
    {
        try { _mb.OfferingsReceived -= OnOfferings; } catch { /* best effort */ }
        try { _mb.HistoryReceived -= OnHistory; } catch { /* best effort */ }
        _requestStartHook.Dispose();
    }
}
