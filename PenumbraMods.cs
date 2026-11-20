using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace XivHubPluginKit;

/// <summary>
/// Installed-mod calls through Penumbra's IPC (Penumbra.Api <c>IpcSubscribers/Mods.cs</c> and
/// <c>Redraw.cs</c>, labels V5). Every call returns why it failed, or null; Penumbra absent is a
/// failure, never an exception. Call on the framework thread.
/// </summary>
public sealed class PenumbraMods(IDalamudPluginInterface pi)
{
    private readonly ICallGateSubscriber<string, string, int> reload =
        pi.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5");
    private readonly ICallGateSubscriber<int, int, object> redraw =
        pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5");

    /// <summary>Penumbra's <c>PenumbraApiEc.Success</c>, <c>NothingChanged</c> and <c>ModMissing</c>.</summary>
    private const int Success = 0, NothingChanged = 1, ModMissing = 3;

    /// <summary>Reads an installed mod's files and options from disk again, as its Reload Mod menu
    /// entry does; the collections keep their settings for it. <paramref name="directory"/> is the
    /// mod's folder name under Penumbra's root.</summary>
    public string? Reload(string directory, string name = "")
    {
        try
        {
            return reload.InvokeFunc(directory, name) switch
            {
                Success or NothingChanged => null,
                ModMissing => $"Penumbra has no mod in folder '{directory}'.",
                var ec => $"Penumbra ReloadMod answered error {ec}.",
            };
        }
        catch (IpcError e)
        {
            return $"Penumbra ReloadMod: {e.Message} (is Penumbra loaded?)";
        }
    }

    /// <summary>Redraws the object at this object table index so it loads its files again.</summary>
    public string? Redraw(int objectIndex)
    {
        try
        {
            redraw.InvokeAction(objectIndex, 0);
            return null;
        }
        catch (IpcError e)
        {
            return $"Penumbra RedrawObject: {e.Message}";
        }
    }
}
