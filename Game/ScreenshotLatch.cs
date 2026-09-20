using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Photo;

namespace XivHubPluginKit.Game;

/// <summary>
/// Releases the game's screenshot latch through the game's own completion thunk.
///
/// <c>ScreenShot.ScheduleScreenShot</c> sets <c>ScreenShotRequested</c> before anything
/// that can fail, and refuses every later request while it is set. A capture the game
/// never drains therefore leaves the flag standing with nothing left to clear it: no file,
/// no callback, and the operator's own screenshot key refused until the client restarts.
/// The completion thunk is the only route back, and the only route used here — writing the
/// flag directly would leave the game holding a capture this plugin then believes it can
/// schedule over, so every later <c>ScheduleScreenShot</c> would return true and be
/// dropped.
///
/// The thunk (<c>ffxiv_dx11.exe</c> RVA <c>0x5F68F0</c> in 2026.09.15.0000.0000) reads one
/// dword through its argument and maps it to a result code, returns false untouched when
/// the latch is already clear, and otherwise invokes the stored completion callback with
/// that code, nulls the callback and its parameter, and clears the latch. It reaches the
/// <c>ScreenShot</c> singleton through its own static, so its argument is the result input
/// and nothing else.
///
/// <see cref="Resolve"/> must run once, with the consuming plugin's <see cref="ISigScanner"/>,
/// before <see cref="TryRelease"/> can do anything; called before that, it reports the
/// same false a resolve failure would.
/// </summary>
public static unsafe class ScreenshotLatch
{
    /// <summary>
    /// The completion thunk's prologue through its first result-code branch: the singleton
    /// load, its null check, the dword read from the argument, and the tests for 0 and 1.
    /// One match in <c>.text</c>.
    /// </summary>
    private const string CompleteSignature =
        "40 53 48 83 EC 20 48 8B 1D ?? ?? ?? ?? 48 85 DB 74 ?? 8B 09 85 C9 74 ?? 83 F9 01";

    /// <summary>
    /// The one input that reports a failure without dereferencing the worker object behind
    /// the singleton, and that can never claim success: the thunk maps 0 to success, 1 to a
    /// generic failure, and anything else through <c>ScreenShot+0x38</c>, which a stranded
    /// capture is exactly the case not to trust.
    /// </summary>
    private const int FailureInput = 1;

    /// <summary>
    /// Resolved at most once, by <see cref="Resolve"/>. Read and written only from the
    /// game's main thread, in the framework tick or the draw callback, so no synchronisation
    /// guards them.
    /// </summary>
    private static delegate* unmanaged<int*, byte> _complete;

    private static bool _resolveAttempted;

    /// <summary>
    /// Clears <c>ScreenShotRequested</c> when it is set, and reports whether the game
    /// released it. A no-op returning false when no capture is latched, when the singleton
    /// is unavailable, or when the thunk has not resolved — in which case captures behave
    /// exactly as they do without recovery and the latch clears only when the game
    /// completes a capture of its own.
    ///
    /// Main-thread only: it runs the game's completion callback synchronously and writes
    /// singleton fields.
    /// </summary>
    // SAFETY: the instance pointer is the game's own singleton, alive for the process
    // lifetime, never relocated, and null-checked before ScreenShotRequested is read; that
    // is a named FFXIVClientStructs field ([FieldOffset(65)] of a 648-byte struct), not a
    // raw offset, so it moves with the layout. The thunk takes its own singleton pointer
    // from its own static and reads exactly one dword through its argument, which is the
    // address of a 4-byte stack local live across the call.
    public static bool TryRelease()
    {
        var inst = TryGetInstance();
        if (inst == null || !inst->ScreenShotRequested)
            return false;

        var complete = _complete;
        if (complete == null)
            return false;

        var input = FailureInput;
        var released = complete(&input) != 0;
        KitServices.Log.Debug($"ScreenshotLatch: release returned {released}, enqueued now {inst->ScreenShotRequested}");
        return released;
    }

    /// <summary>
    /// Scans for the thunk once and caches the result, including a failure: an address that
    /// did not resolve will not resolve on a later attempt either, and rescanning on every
    /// refused capture would spend a full text scan to learn that again. Call once during
    /// plugin startup, with the consuming plugin's injected <see cref="ISigScanner"/>; a
    /// call after the first is a no-op.
    /// </summary>
    public static void Resolve(ISigScanner scanner)
    {
        if (_resolveAttempted)
            return;

        _resolveAttempted = true;
        if (scanner.TryScanText(CompleteSignature, out var address))
        {
            _complete = (delegate* unmanaged<int*, byte>)address;
            KitServices.Log.Debug($"ScreenshotLatch: resolved the game's screenshot completion thunk at {address:X}.");
        }
        else
        {
            KitServices.Log.Warning(
                "ScreenshotLatch could not resolve the game's screenshot completion thunk. Captures still " +
                "work, but a capture the game strands leaves screenshots refused until the client restarts.");
        }
    }

    /// <summary>
    /// <c>ScreenShot.Instance()</c> fails two ways and neither is worth telling apart at a
    /// call site: it throws <see cref="InvalidOperationException"/> (InteropGenerator's
    /// <c>ThrowHelper.ThrowNullAddress</c>) when its static address did not resolve, and
    /// it returns null when the address resolved but the singleton behind it is not
    /// constructed yet.
    /// </summary>
    // SAFETY: returns the game's own singleton pointer unchanged, or null; the caller
    // null-checks before dereferencing it.
    private static ScreenShot* TryGetInstance()
    {
        try
        {
            return ScreenShot.Instance();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
