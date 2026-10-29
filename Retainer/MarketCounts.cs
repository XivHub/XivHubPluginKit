using System.Collections.Generic;

namespace XivHubPluginKit.Retainer;

/// <summary>
/// Market-slot counts read off retainers' open sell lists, laid over the
/// game's <c>MarketItemCount</c>.
///
/// The game refreshes <c>MarketItemCount</c> from the server lazily. Right
/// after a burst of pulls it still reports the count from before the burst,
/// or from partway through it, so a plan read then sizes itself on slots that
/// are already free and leaves them idle until the next visit. The live
/// <c>RetainerMarket</c> container is exact while the sell list is open, so the
/// count read there on the way out stands in for the game's, until the game's
/// own count moves: that means the server has sent a newer one, which then wins.
///
/// Not thread-safe; every caller runs on the framework thread.
/// </summary>
public sealed class MarketCounts
{
    private readonly Dictionary<ulong, (int Live, int GameAt, double ReadAt)> _seen = new();

    /// <summary>A retainer's listing count, with the Unix time it was read off
    /// the open sell list, or null when it is the game's own count.</summary>
    public readonly record struct Count(int Listings, double? ReadAt);

    /// <summary>Record <paramref name="live"/> listings on
    /// <paramref name="retainerId"/>, read at Unix time <paramref name="readAt"/>
    /// while the game reported <paramref name="gameCount"/>.</summary>
    public void Remember(ulong retainerId, int live, int gameCount, double readAt)
        => _seen[retainerId] = (live, gameCount, readAt);

    /// <summary>The count to trust for <paramref name="retainerId"/> when the
    /// game reports <paramref name="gameCount"/>.</summary>
    public Count Resolve(ulong retainerId, int gameCount)
    {
        if (!_seen.TryGetValue(retainerId, out var seen))
            return new Count(gameCount, null);
        if (seen.GameAt == gameCount)
            return new Count(seen.Live, seen.ReadAt);
        _seen.Remove(retainerId);
        return new Count(gameCount, null);
    }
}
