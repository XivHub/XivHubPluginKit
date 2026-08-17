using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ZhyraPluginKit.Game;

/// <summary>Mount/dismount via the Mount Roulette general action (id 9), as ICE does.
/// Jump (GeneralAction 2) is used by the anti-stuck watchdog to unstick on geometry.</summary>
public static class MountHelper
{
    private const uint MountRouletteAction = 9;
    private const uint JumpAction = 2;

    public static unsafe void Mount()
    {
        if (!Player.Mounted && !Player.Mounting && !Player.IsCasting && Player.CanMount)
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, MountRouletteAction);
    }

    public static unsafe void Dismount()
    {
        if (Player.Mounted)
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, MountRouletteAction);
    }

    /// <summary>Trigger a jump (GeneralAction 2). Used by the watchdog to unstick the character.</summary>
    public static unsafe void Jump()
    {
        if (Player.Available && !Player.IsJumping)
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, JumpAction);
    }
}
