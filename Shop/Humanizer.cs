using System;
using System.Threading;
using System.Threading.Tasks;

namespace XivHubPluginKit.Shop;

/// <summary>
/// A short random pause between the steps a person would take one at a time.
///
/// The UI throttles already stop the run outpacing the game, but they are a
/// fixed interval, so a session otherwise clicks with metronome regularity. The
/// pause goes between whole actions — one purchase, one listing — rather than
/// inside a step's retry loop, where it would just slow down recovering from a
/// dropped click.
/// </summary>
public static class Humanizer
{
    /// <summary>
    /// Wait a random interval from [<paramref name="minMs"/>, <paramref name="maxMs"/>]. Returns immediately
    /// when the range is zero, and never throws on cancellation: a Stop wants
    /// the run to end, not to surface a cancelled delay as a failure.
    /// <paramref name="repeat"/> marks the same action on the same target again
    /// and halves the range: someone putting up the twentieth identical stack is
    /// not deliberating over it, and a run that pauses as long for that one as
    /// for the first reads less like a person, not more.
    /// </summary>
    public static async Task PauseAsync(int minMs, int maxMs, CancellationToken ct, bool repeat = false)
    {
        int lo = Math.Max(0, minMs);
        int hi = Math.Max(lo, maxMs);
        if (repeat) hi = lo + ((hi - lo) / 2);
        if (hi <= 0) return;
        try
        {
            await Task.Delay(Random.Shared.Next(lo, hi + 1), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
