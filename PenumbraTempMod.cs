using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace XivHubPluginKit;

/// <summary>
/// One Penumbra temporary mod applied to every collection, through Penumbra's IPC
/// (Penumbra.Api <c>IpcSubscribers/Temporary.cs</c> and <c>Redraw.cs</c>, labels V5). It redirects
/// game paths to files on disk until <see cref="Remove"/> or Penumbra unloads; it beats a normal mod
/// on the same path when <paramref name="priority"/> is above that mod's priority in the collection.
/// No service dependency. Every call returns why it failed, or null; Penumbra absent is a failure,
/// never an exception. Call on the framework thread.
/// </summary>
public sealed class PenumbraTempMod(IDalamudPluginInterface pi, string tag, int priority)
{
    private readonly ICallGateSubscriber<string, Dictionary<string, string>, string, int, int> add =
        pi.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5");
    private readonly ICallGateSubscriber<string, int, int> remove =
        pi.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5");
    private readonly ICallGateSubscriber<int, int, object> redraw =
        pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5");

    /// <summary>Penumbra's <c>PenumbraApiEc.Success</c>, and <c>NothingChanged</c>, which removing an
    /// unregistered tag answers.</summary>
    private const int Success = 0, NothingChanged = 1;

    /// <summary>True between a successful <see cref="Set"/> and <see cref="Remove"/>.</summary>
    public bool Applied { get; private set; }

    /// <summary>Replaces the mod's whole file set: game path to absolute local file path. An
    /// optional meta manipulation string is Penumbra's base64 format; empty for none.</summary>
    public string? Set(Dictionary<string, string> paths, string manipulations = "")
    {
        var error = Call(() => add.InvokeFunc(tag, paths, manipulations, priority), "AddTemporaryModAll");
        if (error == null) Applied = true;
        return error;
    }

    public string? Remove()
    {
        if (!Applied) return null;
        var error = Call(() => remove.InvokeFunc(tag, priority), "RemoveTemporaryModAll");
        if (error == null) Applied = false;
        return error;
    }

    /// <summary>Redraws the object at this object table index so it loads the files again.</summary>
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

    private static string? Call(System.Func<int> call, string name)
    {
        try
        {
            var ec = call();
            return ec is Success or NothingChanged ? null : $"Penumbra {name} answered error {ec}.";
        }
        catch (IpcError e)
        {
            return $"Penumbra {name}: {e.Message} (is Penumbra loaded?)";
        }
    }
}
