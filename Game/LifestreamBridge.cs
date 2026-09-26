using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace XivHubPluginKit.Game;

/// <summary>
/// Asks Lifestream to travel to another world (provider: <c>Lifestream/IPC/IPCProvider.cs</c>,
/// checked at Lifestream commit <c>ef759e9</c>).
///
/// The IPC names come from ECommons <c>EzIPC.Init</c>, which registers each <c>[EzIPC]</c>
/// method as <c>"{prefix}.{method}"</c> with the prefix defaulting to the provider's
/// InternalName (<c>ECommons/EzIpcManager/EzIPC.cs</c>), and the generic arguments as
/// the parameter types followed by the return type:
/// <list type="bullet">
/// <item><c>Lifestream.IsBusy</c>: <c>bool IsBusy()</c>, true while its
/// task manager runs or a path is being followed.</item>
/// <item><c>Lifestream.ChangeWorldById</c>: <c>bool ChangeWorldById(uint worldId)</c>, which
/// forwards the world's name to <c>ChangeWorld</c>. It returns false while Lifestream is busy,
/// for a world id not in
/// the World sheet, and for a world that is neither visitable on this data center nor through
/// data center travel.</item>
/// </list>
/// A trip teleports to a world-visit aetheryte first unless one is already in reach, then
/// changes world. True means Lifestream accepted the request: <c>TPAndChangeWorld</c> can still
/// refuse on its own afterwards (already on that world, data center travel disabled in its
/// settings) and says so in its own notification.
///
/// Lifestream has no API version call, so every call is wrapped and degrades to "unavailable";
/// each kind of failure is logged once. Framework thread only.
/// </summary>
public static class LifestreamBridge
{
    public const string InternalName = "Lifestream";

    private static ICallGateSubscriber<bool>? isBusy;
    private static ICallGateSubscriber<uint, bool>? changeWorldById;

    private static bool loggedAbsent;
    private static bool loggedBusyFailed;
    private static bool loggedChangeFailed;

    public static bool Installed => PluginPresence.IsInstalled(InternalName);

    /// <summary>Whether Lifestream is mid-task; null when it is absent or the call throws.</summary>
    public static bool? IsBusy()
    {
        if (!CheckInstalled()) return null;
        try
        {
            return (isBusy ??= Svc.PluginInterface
                .GetIpcSubscriber<bool>("Lifestream.IsBusy")).InvokeFunc();
        }
        // Not registered yet while Lifestream is still starting; not worth the one warning a real
        // name or type mismatch needs.
        catch (IpcNotReadyError)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (!loggedBusyFailed)
            {
                loggedBusyFailed = true;
                KitServices.Log.Warning(ex, $"{KitServices.LogPrefix} Lifestream's busy check failed; world travel is unavailable.");
            }
            return null;
        }
    }

    /// <summary>
    /// Asks Lifestream to travel to <paramref name="worldId"/> (a World sheet row id). False when
    /// Lifestream is absent, refuses the request, or the call throws.
    /// </summary>
    public static bool ChangeWorld(uint worldId)
    {
        if (!CheckInstalled()) return false;
        try
        {
            return (changeWorldById ??= Svc.PluginInterface
                .GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById")).InvokeFunc(worldId);
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (!loggedChangeFailed)
            {
                loggedChangeFailed = true;
                KitServices.Log.Warning(ex, $"{KitServices.LogPrefix} Lifestream's world change call failed; world travel is unavailable.");
            }
            return false;
        }
    }

    private static bool CheckInstalled()
    {
        if (Installed) return true;
        if (!loggedAbsent)
        {
            loggedAbsent = true;
            KitServices.Log.Information($"{KitServices.LogPrefix} Lifestream is not installed; world travel is unavailable.");
        }
        return false;
    }
}
