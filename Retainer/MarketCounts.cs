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
    private readonly Dictionary<ulong, (int Live, int GameAt)> _seen = new();

    /// <summary>Record <paramref name="live"/> listings on
    /// <paramref name="retainerId"/>, read while the game reported
    /// <paramref name="gameCount"/>.</summary>
    public void Remember(ulong retainerId, int live, int gameCount)
        => _seen[retainerId] = (live, gameCount);

    /// <summary>The count to trust for <paramref name="retainerId"/> when the
    /// game reports <paramref name="gameCount"/>.</summary>
    public int Resolve(ulong retainerId, int gameCount)
    {
        if (!_seen.TryGetValue(retainerId, out var seen))
            return gameCount;
        if (seen.GameAt == gameCount)
            return seen.Live;
        _seen.Remove(retainerId);
        return gameCount;
    }
}
