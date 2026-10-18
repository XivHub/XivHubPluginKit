using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons;
using ECommons.DalamudServices;

namespace XivHubPluginKit.Game;

/// <summary>
/// Drives Lifestream through its IPC (provider: <c>Lifestream/IPC/IPCProvider.cs</c>, checked at
/// Lifestream commit <c>b363201</c>).
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
/// <item><c>Lifestream.ExecuteCommand</c>: <c>void ExecuteCommand(string arguments)</c>, the
/// <c>/li</c> command with these arguments. An argument equal to an enabled custom alias's
/// name (case-insensitive) enqueues that alias (<c>Lifestream.ProcessCustomShortcuts</c>);
/// an unknown name falls through to the rest of <c>/li</c>'s parsing and reports nothing
/// back.</item>
/// <item><c>Lifestream.ChangeCharacter</c>: <c>ErrorCode ChangeCharacter(string name, string
/// world)</c>, log out and into that character on its home world. Refuses with
/// <c>Plugin_is_busy</c> while Lifestream runs and <c>Invalid_world_specified</c> for a world
/// name that is not a public world; logged out, it needs Lifestream's auto-login.</item>
/// <item><c>Lifestream.Abort</c>: <c>void Abort()</c>, drop its queued tasks and path.</item>
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
    private static ICallGateSubscriber<string, object>? executeCommand;
    private static ICallGateSubscriber<string, string, ErrorCode>? changeCharacter;
    private static ICallGateSubscriber<object>? abort;

    private static bool loggedAbsent;
    private static bool loggedBusyFailed;
    private static bool loggedChangeFailed;
    private static bool loggedCommandFailed;
    private static bool loggedCharacterFailed;
    private static bool loggedAbortFailed;

    public static bool Installed => PluginPresence.IsInstalled(InternalName);

    /// <summary>Whether Lifestream is mid-task; null when it is absent or the call throws.</summary>
    public static bool? IsBusy()
        => Call(() => (bool?)(isBusy ??= Svc.PluginInterface
                .GetIpcSubscriber<bool>("Lifestream.IsBusy")).InvokeFunc(),
            null, ref loggedBusyFailed, "busy check");

    /// <summary>
    /// Asks Lifestream to travel to <paramref name="worldId"/> (a World sheet row id). False when
    /// Lifestream is absent, refuses the request, or the call throws.
    /// </summary>
    public static bool ChangeWorld(uint worldId)
        => Call(() => (changeWorldById ??= Svc.PluginInterface
                .GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById")).InvokeFunc(worldId),
            false, ref loggedChangeFailed, "world change call");

    /// <summary>
    /// Runs the custom alias named <paramref name="alias"/>. True means the call reached
    /// Lifestream, not that an alias by that name exists; watch <see cref="IsBusy"/> for the
    /// trip starting.
    /// </summary>
    public static bool RunAlias(string alias)
        => Call(() =>
            {
                (executeCommand ??= Svc.PluginInterface
                    .GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand")).InvokeAction(alias);
                return true;
            },
            false, ref loggedCommandFailed, "command call");

    /// <summary>Asks Lifestream to log into <paramref name="name"/> on its home world
    /// <paramref name="world"/>; null when Lifestream is absent or the call throws.</summary>
    public static ErrorCode? ChangeCharacter(string name, string world)
        => Call(() => (ErrorCode?)(changeCharacter ??= Svc.PluginInterface
                .GetIpcSubscriber<string, string, ErrorCode>("Lifestream.ChangeCharacter")).InvokeFunc(name, world),
            null, ref loggedCharacterFailed, "character change call");

    public static void Abort()
        => Call(() =>
            {
                (abort ??= Svc.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort")).InvokeAction();
                return true;
            },
            false, ref loggedAbortFailed, "abort call");

    private static T Call<T>(Func<T> call, T unavailable, ref bool logged, string what)
    {
        if (!CheckInstalled()) return unavailable;
        try
        {
            return call();
        }
        // Not registered yet while Lifestream is still starting; not worth the one warning a real
        // name or type mismatch needs.
        catch (IpcNotReadyError)
        {
            return unavailable;
        }
        catch (Exception ex)
        {
            if (!logged)
            {
                logged = true;
                KitServices.Log.Warning(ex, $"{KitServices.LogPrefix} Lifestream's {what} failed; it is unavailable.");
            }
            return unavailable;
        }
    }

    private static bool CheckInstalled()
    {
        if (Installed) return true;
        if (!loggedAbsent)
        {
            loggedAbsent = true;
            KitServices.Log.Information($"{KitServices.LogPrefix} Lifestream is not installed; travel is unavailable.");
        }
        return false;
    }
}
