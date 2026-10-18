using System;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace XivHubPluginKit.Retainer;

/// <summary>
/// AutoRetainer's suppression switch (<c>AutoRetainer.SetSuppressed</c>): while set, AR's plugin
/// and multi mode both read inactive, so it neither sends ventures nor relogs.
///
/// Every session sets it on start and clears it in a <c>finally</c>, idempotently. A caller
/// that chains several sessions (a run across characters) takes a <see cref="Hold"/> for its
/// whole life instead: while held, <c>Set(false)</c> leaves AR suppressed, so AR cannot start a
/// cycle in the gap between two sessions, and <see cref="Release"/> puts back whatever the
/// switch read when the hold began.
/// </summary>
public static class AutoRetainerSuppress
{
    private static volatile bool held;
    private static bool heldPrior;

    public static void Set(bool value)
    {
        if (!value && held) return;
        Send(value);
    }

    public static void Hold()
    {
        if (held) return;
        heldPrior = TryGet(out bool current) && current;
        held = true;
        Send(true);
    }

    public static void Release()
    {
        if (!held) return;
        held = false;
        Send(heldPrior);
    }

    private static void Send(bool value)
    {
        try
        {
            Svc.PluginInterface
                .GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed")
                .InvokeAction(value);
        }
        catch (IpcNotReadyError) { /* AR not loaded */ }
        catch (Exception ex)
        {
            KitServices.Log.Warning(ex, "AutoRetainerSuppress.Set({V}) failed", value);
        }
    }

    public static bool TryGet(out bool current)
    {
        current = false;
        try
        {
            current = Svc.PluginInterface
                .GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed")
                .InvokeFunc();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
