using System;
using System.Linq;

namespace XivHubPluginKit.Board;

/// <summary>
/// Where a read of the item search results is in its arrival: still catching up with a request
/// that hasn't landed, settled on a page, holding no listings, or past the deadline without ever
/// settling.
/// </summary>
public enum GateState { Waiting, Ready, NoListings, TimedOut }

/// <summary>
/// Decides when a <see cref="BoardSnapshot"/> read after opening an item's listings is the
/// finished page rather than a page still loading or the previous item's slots.
/// <see cref="SettleMs"/> is twice the widest gap seen between pages landing (250-520 ms at 250 ms
/// sampling: Fire Shard seq 54, 56, 60 and Earth Shard seq 145, 147, 149, 150 in session
/// 37c13073e8c8), so a page that stops changing for it is done rather than mid-arrival. Slots keep
/// the previous search's listings until the first page of the new one lands, so re-opening the
/// same item can read old slots as current; a buyer must still look a listing up by
/// <see cref="BoardListing.ListingId"/> and check the confirm prompt against it before buying, since
/// this gate alone can't tell a stale slot from a fresh one that happens to match. An item with no
/// listings on the world gets no answer at all: the request id stays at
/// <paramref name="requestBefore"/> with 0 listings and 0 entries while the window shows "No items
/// found." (Ahriman Tears on Moogle, Furnisher devlog 2026-09-23 18:01:55, and UiCapture session
/// 2297ae786885, where opening it wrote nothing to the item search InfoProxy). Such a read for this
/// item at the deadline is <see cref="GateState.NoListings"/>, not <see cref="GateState.TimedOut"/>:
/// a board that was only slow is skipped, never bought from.
/// </summary>
public sealed class ListingsGate(uint itemId, byte requestBefore, long startMs)
{
    public const int SettleMs = 1000;
    public const int TimeoutMs = 10_000;

    private (byte RequestId, int ListingCount, uint EntryCount, string Ids)? lastKey;
    private long? settleStartMs;

    /// <summary>
    /// The gate's state given the latest read and the current time. A current read is one that
    /// answers this item's request: not null, both <see cref="BoardSnapshot.ResultItemId"/> and
    /// <see cref="BoardSnapshot.SearchItemId"/> equal to <paramref name="itemId"/>'s constructor
    /// argument, its request id (a byte from <c>InfoProxyPageInterface</c>, so only inequality is
    /// tested, never a wrap-safe comparison) different from <paramref name="requestBefore"/>, and
    /// every listing belonging to this item with a non-zero id.
    /// </summary>
    public GateState Observe(BoardSnapshot? read, long nowMs)
    {
        var current = IsCurrent(read);
        if (current)
        {
            var key = Key(read!);
            if (lastKey != key)
            {
                lastKey = key;
                settleStartMs = nowMs;
            }
        }
        else
        {
            lastKey = null;
            settleStartMs = null;
        }

        if (current && settleStartMs is { } settled && nowMs - settled >= SettleMs)
            return read!.ListingCount == 0 ? GateState.NoListings : GateState.Ready;

        if (nowMs - startMs < TimeoutMs)
            return GateState.Waiting;
        return IsUnansweredEmpty(read) ? GateState.NoListings : GateState.TimedOut;
    }

    private bool IsUnansweredEmpty(BoardSnapshot? read) =>
        read is { ListingCount: 0, EntryCount: 0, Listings.Count: 0 }
        && read.ResultItemId == itemId
        && read.SearchItemId == itemId;

    private bool IsCurrent(BoardSnapshot? read) =>
        read is not null
        && read.ResultItemId == itemId
        && read.SearchItemId == itemId
        && read.RequestId != requestBefore
        // EntryCount counts slots written, in pages of 10; every settled recorded read has it at or
        // above ListingCount (20/18, 30/28, 20/11) and every read mid-arrival below it (10/18,
        // 20/28). A read carries only the slots under it, so a read below ListingCount is a page
        // still arriving, not the whole board. Capped at the 100 slots InfoProxyItemSearch holds.
        && read.EntryCount >= Math.Min(read.ListingCount, MaxListings)
        && read.Listings.All(l => l.ItemId == itemId && l.ListingId != 0);

    private const int MaxListings = 100;

    private static (byte, int, uint, string) Key(BoardSnapshot read) =>
        (read.RequestId, read.ListingCount, read.EntryCount, string.Join(',', read.Listings.Select(l => l.ListingId)));
}
