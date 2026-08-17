using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ZhyraPluginKit.Game;

/// <summary>Use Sprint (GeneralAction 4) on long grounded walks, the way ICE does. Only fires
/// when the action is off cooldown (<see cref="ActionManager.GetActionStatus"/> == 0) and the
/// player is unmounted + not already sprinting. Throttled so we don't hammer the action manager.</summary>
public static unsafe class SprintHelper
{
    private const uint SprintAction = 4;

    /// <summary>Use Sprint if it's ready, the player is unmounted, and we haven't tried in 2s.
    /// Returns true if the action was issued this call.</summary>
    public static bool TrySprint()
    {
        if (!EzThrottler.Throttle("ZPK.Sprint", 2000))
            return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        if (am->GetActionStatus(ActionType.GeneralAction, SprintAction) != 0)
            return false; // on cooldown or unavailable
        return am->UseAction(ActionType.GeneralAction, SprintAction);
    }
}