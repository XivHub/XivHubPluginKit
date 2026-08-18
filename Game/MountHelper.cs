using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XivHubPluginKit.Game;

/// <summary>Mount via the Mount Roulette general action (id 9), as ICE does; leave the mount via
/// Dismount (id 23), which doubles as "land" while airborne. Jump (GeneralAction 2) is used by the
/// anti-stuck watchdog to unstick on geometry. Every action is gated on
/// <see cref="ActionManager.GetActionStatus"/> == 0, so an unusable press is skipped instead of
/// being counted as done.</summary>
public static unsafe class MountHelper
{
    private const uint MountRouletteAction = 9;
    private const uint DismountAction = 23;
    private const uint JumpAction = 2;

    private static bool Usable(uint generalAction)
    {
        var am = ActionManager.Instance();
        return am != null && am->GetActionStatus(ActionType.GeneralAction, generalAction) == 0;
    }

    public static void Mount()
    {
        if (!Player.Mounted && !Player.Mounting && !Player.IsCasting && Player.CanMount && Usable(MountRouletteAction))
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, MountRouletteAction);
    }

    /// <summary>Press Dismount once. While airborne this only starts the descent — the character is
    /// still mounted when it touches down and needs a second press — so callers that just want to be
    /// on foot should drive <see cref="Ground"/> instead.</summary>
    public static void Dismount()
    {
        if (Player.Mounted && !Player.Mounting && Usable(DismountAction))
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, DismountAction);
    }

    /// <summary>Get the character onto the ground and out of the saddle. Returns true once it is on
    /// foot and no longer falling. Call it every frame until it returns true: a flying dismount is
    /// two presses (descend, then leave the mount) separated by the whole descent.</summary>
    public static bool Ground()
    {
        if (!Player.Mounted)
            return !Player.IsJumping;
        if (EzThrottler.Throttle("ZPK.Dismount", 500))
            Dismount();
        return false;
    }

    /// <summary>Trigger a jump (GeneralAction 2). Used by the watchdog to unstick the character.</summary>
    public static void Jump()
    {
        if (Player.Available && !Player.IsJumping && Usable(JumpAction))
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, JumpAction);
    }
}
