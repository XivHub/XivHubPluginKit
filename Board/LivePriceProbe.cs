using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XivHubPluginKit.Board;

/// <summary>
/// Drives the "Compare Prices" button on an open <c>RetainerSell</c> window and
/// resolves what the board holds for that item.
///
/// FFXIV rate-limits price requests server-side, and a rate-limited request comes
/// back as nothing at all rather than as an error, so the probe paces requests
/// behind a throttle and re-fires a silent one a few times before giving up.
/// <see cref="ResponseTimeoutMs"/> is the budget for that silence and nothing
/// else: a board with nothing on it says so up front (see
/// <see cref="MarketBoardListener"/>), so an item nobody else is selling resolves
/// off its history as soon as that lands rather than waiting the budget out. The
/// result is cached per <c>(itemId, hq)</c> for the session: the same item on a
/// second retainer costs no second request, and our own listings were never in
/// the competitor set, so a cached answer cannot go stale from our own writes.
/// </summary>
public sealed unsafe class LivePriceProbe
{
    private readonly MarketBoardListener _mb;
    private readonly Func<HashSet<ulong>> _ownCids;
    private readonly IPluginLog _log;
    private readonly string _throttleName;
    private readonly string _tag;
    private readonly Action<uint, bool>? _onResolved;

    private readonly Dictionary<(uint Item, bool Hq), LivePrice> _cache = new();
    private (uint Item, bool Hq)? _awaiting;
    /// <summary>When the most recent packet of this request landed; 0 before any has.</summary>
    private long _lastPacketMs;
    /// <summary>Offerings pages folded in when the settle window was last restarted.</summary>
    private int _seenPackets;
    private long _deadlineMs;
    private int _retries;
    private int _issued;

    /// <summary>
    /// How long to wait for a board response before re-firing.
    ///
    /// Measured over 203 lookups: p50 0.54s, p99 0.90s, then nothing at all
    /// until a single 5.55s straggler. Since the band between is empty, this
    /// abandons exactly one reply in two hundred whether it is set to three
    /// seconds or five, and three is still more than three times the p99.
    /// Re-firing early is what costs: the request is rate limited server-side,
    /// and a throttled reply comes back silent, which is what a timeout looks
    /// like, so guessing low feeds itself.
    /// </summary>
    private const long ResponseTimeoutMs = 3000;

    /// <summary>
    /// How long after the LAST page of a reply to keep waiting for another one.
    ///
    /// Offerings arrive ten listings to a packet, cheapest first, and Dalamud
    /// raises OfferingsReceived once per packet rather than once per reply: its
    /// aggregating observable feeds the Universalis path, not the event. So a
    /// board whose cheapest ten listings are all our own retainers answers the
    /// first page with no competitor in it, and the one to undercut is on a page
    /// that has not landed yet. Timing this from the first page instead of the
    /// last priced those items off sale history and reported nobody was selling.
    ///
    /// The window measures a gap between pages, not the length of a reply, so it
    /// bounds the wait without depending on the client's in-flight flag, whose
    /// offset ClientStructs is not certain of.
    /// </summary>
    private const long SettleMs = 500;
    private const int MaxRetries = 3;

    /// <summary>ECommons button id for Compare Prices on <c>RetainerSell</c>.</summary>
    private const int CompareButton = 4;

    public LivePriceProbe(
        MarketBoardListener mb, Func<HashSet<ulong>> ownCids, IPluginLog log,
        string throttleName, string tag, Action<uint, bool>? onResolved)
    {
        _mb = mb;
        _ownCids = ownCids;
        _log = log;
        _throttleName = throttleName;
        _tag = tag;
        _onResolved = onResolved;
    }

    /// <summary>Board reads issued this session.</summary>
    public int Issued => _issued;

    /// <summary>
    /// What the board already told us about this item during this session, if
    /// anything. A second retainer holding the same stack needs no second
    /// lookup, so its rows can be judged before any window is opened.
    /// </summary>
    public bool TryGetCached(uint itemId, bool hq, out LivePrice price)
        => _cache.TryGetValue((itemId, hq), out price);

    public void ResetSession()
    {
        _cache.Clear();
        _awaiting = null;
        _retries = 0;
        _lastPacketMs = 0;
        _seenPackets = 0;
        _issued = 0;
    }

    /// <summary>
    /// Advance one item's lookup against the open sell window. Returns false to be
    /// called again next tick while a request is in flight, true once
    /// <paramref name="price"/> holds the answer, including the answer "the board
    /// told us nothing", which is <c>default</c> and never blocks the caller.
    /// <paramref name="maxPerSession"/> of 0 means no cap.
    /// </summary>
    public bool Step(
        AtkUnitBase* sell, uint itemId, bool hq, string name,
        int maxPerSession, int delayMs, out LivePrice price)
    {
        if (_cache.TryGetValue((itemId, hq), out price))
            return Done();

        price = default;
        bool midRequest = _awaiting == (itemId, hq);

        // The cap must not abort a lookup already in flight, or the item pays for
        // a request whose answer is then thrown away.
        if (maxPerSession > 0 && !midRequest && _issued >= maxPerSession)
        {
            _log.Information("{Tag}: live-price cap of {Cap} reached; no board read for {Item}",
                _tag, maxPerSession, name);
            // No request was ever fired, so OffersComplete stays false by default:
            // this is not a board that answered empty, it is one never asked.
            return Resolve(itemId, hq, default, out price);
        }

        long now = Environment.TickCount64;

        if (!midRequest)
        {
            if (!EzThrottler.Throttle(_throttleName, delayMs))
                return false;
            Fire(sell, itemId, hq);
            _awaiting = (itemId, hq);
            _retries = 0;
            _lastPacketMs = 0;
            _seenPackets = 0;
            _deadlineMs = now + ResponseTimeoutMs;
            _log.Debug("{Tag}: live price requested for {Item} (hq={Hq})", _tag, name, hq);
            return false;
        }

        uint? competitor = _mb.TryGetCheapestCompetitor(out bool offeringsArrived, out bool boardEmpty);
        uint? history = _mb.TryGetHistory(out bool historyArrived);

        // Price from the offerings packets, never from InfoProxyItemSearch's
        // listing array. Read right after a page lands, that array leaves out the
        // cheapest one to four rows on most boards, so an undercut taken from it
        // anchors on a higher listing and can price above the whole market. The
        // packets match the Search Results window row for row.
        //
        // Offerings arrive price-ascending, so the first undercuttable one is the
        // cheapest there is.
        if (competitor is not null)
            return Resolve(itemId, hq, new LivePrice(competitor.Value, history ?? 0u, true, _mb.TryGetCheapestOwn() ?? 0u), out price);

        // Nobody else is selling, so history is the only guide. Its packet can
        // trail the empty answer; the settle window below bounds that wait.
        if (boardEmpty && historyArrived)
            return Resolve(itemId, hq, new LivePrice(0u, history ?? 0u, true, _mb.TryGetCheapestOwn() ?? 0u), out price);

        // Nothing to undercut so far. Offerings come one page at a time, so a
        // competitor behind our own cheap listings can still be on a later page.
        // Restart the window every time one lands: the answer is only settled
        // once the reply has stopped growing, not once it has started.
        int packets = _mb.OfferingsPackets;
        bool anyArrived = offeringsArrived || historyArrived;
        if (anyArrived && (_lastPacketMs == 0 || packets != _seenPackets))
        {
            _lastPacketMs = now;
            _seenPackets = packets;
        }
        if (anyArrived && now >= _lastPacketMs + SettleMs)
        {
            _log.Debug("{Tag}: {Item} — nothing to undercut after {Pages} page(s); no further page for {Settle}ms",
                _tag, name, packets, SettleMs);
            return Resolve(itemId, hq, new LivePrice(0u, history ?? 0u, offeringsArrived, _mb.TryGetCheapestOwn() ?? 0u), out price);
        }

        if (now < _deadlineMs)
            return false;

        // Something came back, just nothing to undercut. offeringsArrived still
        // distinguishes a settled read of the offerings from a deadline that hit
        // while only the (faster) history packet had landed.
        if (offeringsArrived || historyArrived)
            return Resolve(itemId, hq, new LivePrice(0u, history ?? 0u, offeringsArrived, _mb.TryGetCheapestOwn() ?? 0u), out price);

        // Nothing at all: rate-limited, or the request was dropped. Both look
        // identical from here, and both are worth re-firing.
        if (_retries < MaxRetries)
        {
            if (!EzThrottler.Throttle(_throttleName, delayMs))
                return false;
            _retries++;
            Fire(sell, itemId, hq);
            _lastPacketMs = 0;
            _seenPackets = 0;
            _deadlineMs = now + ResponseTimeoutMs;
            _log.Information("{Tag}: no board response for {Item}; retry {N}/{Max}",
                _tag, name, _retries, MaxRetries);
            return false;
        }

        _log.Warning("{Tag}: live price timed out for {Item} after {Max} retries", _tag, name, MaxRetries);
        // Retries exhausted with neither offerings nor history ever landing, so
        // OffersComplete stays false: nothing about this item is actually known.
        return Resolve(itemId, hq, default, out price);
    }

    private void Fire(AtkUnitBase* sell, uint itemId, bool hq)
    {
        _mb.BeginRequest(itemId, hq, _ownCids());
        Callback.Fire(sell, true, CompareButton);
        _issued++;
    }

    private bool Resolve(uint itemId, bool hq, LivePrice result, out LivePrice price)
    {
        // This is the one place in the probe that decides a board reply is
        // done, whether via the proxy, the packet fallback, or a timeout,
        // so onResolved fires here, once per finished lookup, before the
        // cache write. That includes the maxPerSession-cap path above, which
        // resolves without ever firing a request: there the listener holds
        // no data for (itemId, hq), and MarketBoardListener.Observation
        // returns null for it.
        _onResolved?.Invoke(itemId, hq);
        _cache[(itemId, hq)] = result;
        _awaiting = null;
        _retries = 0;
        _lastPacketMs = 0;
        _seenPackets = 0;
        price = result;
        return Done();
    }

    /// <summary>
    /// Compare Prices opens <c>ItemSearchResult</c> on top of the sell window;
    /// close it before the caller confirms or cancels so it does not stack up
    /// across items.
    /// </summary>
    private static bool Done()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var isr) && isr->IsVisible)
            isr->Close(true);
        return true;
    }
}
