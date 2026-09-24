using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace XivHubPluginKit.Crafting;

/// <summary>
/// Creates and imports Artisan crafting lists through the XivHub Artisan fork's IPC (provider:
/// <c>Artisan/IPC/IPC.cs</c>). <c>Artisan.ApiVersion</c> 1 provides <c>Artisan.CreateList</c>
/// (final items only); 2 adds <c>Artisan.CreateListWithSubcrafts</c>, the same signature, which
/// also adds every intermediate craft through Artisan's own <c>CraftingListUI.AddAllSubcrafts</c>;
/// 3 adds <c>Artisan.ImportList</c>, which imports a full Artisan export JSON, keeping its
/// <c>SkipIfEnough</c>, <c>SkipLiteral</c> and each row's Quick Synthesis choice, instead of
/// building a list from item ids.
///
/// Upstream Artisan registers none of these gates, so an absent plugin, a missing provider, an API
/// version this build was not written against, and a call that throws all read as "unavailable"
/// and the caller falls back to the clipboard. Each failure kind is logged once. Framework thread
/// only.
/// </summary>
public static class ArtisanBridge
{
    public const string InternalName = "Artisan";

    /// <summary>The oldest provider version this bridge understands: <c>Artisan.CreateList</c>.</summary>
    public const int MinApiVersion = 1;

    /// <summary>The provider version that adds <c>Artisan.CreateListWithSubcrafts</c>.</summary>
    public const int SubcraftsApiVersion = 2;

    /// <summary>The provider version that adds <c>Artisan.ImportList</c>.</summary>
    public const int ImportApiVersion = 3;

    private static ICallGateSubscriber<int>? apiVersion;

    // The type arguments must equal the provider's registration exactly. Dalamud matches IPC
    // argument types by base-type chain only, so an array or interface type here fails at call
    // time with IpcTypeMismatchError rather than at build time.
    private static ICallGateSubscriber<string, List<(uint ItemId, uint Qty)>, int>? createList;
    private static ICallGateSubscriber<string, List<(uint ItemId, uint Qty)>, int>? createListWithSubcrafts;
    private static ICallGateSubscriber<string, int>? importList;

    private static bool loggedAbsent;
    private static bool loggedVersionNotReady;
    private static bool loggedVersionFailed;
    private static bool loggedVersion;
    private static bool loggedCreateNotReady;
    private static bool loggedCreateIpcError;
    private static bool loggedCreateFailed;
    private static bool loggedImportNotReady;
    private static bool loggedImportIpcError;
    private static bool loggedImportFailed;

    /// <summary>True when Artisan is loaded and its list IPC speaks <see cref="MinApiVersion"/> or later.</summary>
    public static bool Available => ApiVersion() != null;

    /// <summary>True when Artisan's list IPC also provides <c>Artisan.CreateListWithSubcrafts</c>.</summary>
    public static bool SupportsSubcrafts => ApiVersion() >= SubcraftsApiVersion;

    /// <summary>True when Artisan's list IPC also provides <c>Artisan.ImportList</c>.</summary>
    public static bool SupportsImport => ApiVersion() >= ImportApiVersion;

    /// <summary>The provider's list IPC version, or null when it is unavailable or older than
    /// <see cref="MinApiVersion"/>.</summary>
    private static int? ApiVersion()
    {
        if (!PluginPresence.IsInstalled(InternalName))
        {
            LogOnce(ref loggedAbsent, () => KitServices.Log.Information(
                "Artisan is not installed; Send to Artisan copies the list as Artisan JSON."));
            return null;
        }

        int version;
        try
        {
            version = (apiVersion ??= Svc.PluginInterface
                .GetIpcSubscriber<int>("Artisan.ApiVersion")).InvokeFunc();
        }
        catch (IpcNotReadyError)
        {
            LogOnce(ref loggedVersionNotReady, () => KitServices.Log.Information(
                "Artisan does not provide Artisan.ApiVersion (upstream Artisan, or a build "
                + "without the list IPC); Send to Artisan copies the list as Artisan JSON."));
            return null;
        }
        catch (Exception ex)
        {
            LogOnce(ref loggedVersionFailed, () => KitServices.Log.Warning(
                "Artisan's version check failed; Send to Artisan copies the list as Artisan "
                + "JSON. {Cause}", Flatten(ex)));
            return null;
        }

        if (version < MinApiVersion)
        {
            LogOnce(ref loggedVersion, () => KitServices.Log.Warning(
                "Artisan speaks list IPC version {Found}, this build needs {Wanted} or later; "
                + "Send to Artisan copies the list as Artisan JSON.", version, MinApiVersion));
            return null;
        }

        return version;
    }

    /// <summary>
    /// Creates an Artisan crafting list named <paramref name="name"/> holding the final items in
    /// <paramref name="items"/>, plus every intermediate craft when <paramref name="subcrafts"/> is
    /// set and <see cref="SupportsSubcrafts"/>; an older provider gets final items only. Returns the
    /// new list id, -1 when Artisan refused (no item resolved to a craftable recipe), or null when
    /// the IPC is unavailable or the call threw.
    /// </summary>
    public static int? CreateList(string name, List<(uint ItemId, uint Qty)> items, bool subcrafts)
    {
        if (ApiVersion() is not { } version) return null;

        var withSubcrafts = subcrafts && version >= SubcraftsApiVersion;
        var gate = withSubcrafts ? "Artisan.CreateListWithSubcrafts" : "Artisan.CreateList";
        try
        {
            var subscriber = withSubcrafts
                ? createListWithSubcrafts ??= Svc.PluginInterface
                    .GetIpcSubscriber<string, List<(uint ItemId, uint Qty)>, int>("Artisan.CreateListWithSubcrafts")
                : createList ??= Svc.PluginInterface
                    .GetIpcSubscriber<string, List<(uint ItemId, uint Qty)>, int>("Artisan.CreateList");
            return subscriber.InvokeFunc(name, items);
        }
        catch (IpcNotReadyError)
        {
            LogOnce(ref loggedCreateNotReady, () => KitServices.Log.Warning(
                "Artisan reports list IPC version {Version} but does not provide {Gate}.", version, gate));
        }
        catch (IpcError ex)
        {
            LogOnce(ref loggedCreateIpcError, () => KitServices.Log.Warning(
                "{Gate} IPC call failed. {Cause}", gate, Flatten(ex)));
        }
        catch (Exception ex)
        {
            LogOnce(ref loggedCreateFailed, () => KitServices.Log.Warning(
                "{Gate} threw. {Cause}", gate, Flatten(ex)));
        }

        return null;
    }

    /// <summary>
    /// Imports an Artisan export JSON (the same format the clipboard "Import List From Clipboard"
    /// path reads) through <c>Artisan.ImportList</c>, keeping the JSON's <c>SkipIfEnough</c>,
    /// <c>SkipLiteral</c> and each row's Quick Synthesis choice instead of Artisan's list defaults.
    /// Returns the new list id, -1 when Artisan refused (blank json, a parse failure, or zero
    /// recipes), or null when the provider is older than <see cref="ImportApiVersion"/>, the IPC is
    /// unavailable, or the call threw.
    /// </summary>
    public static int? ImportList(string json)
    {
        if (ApiVersion() is not { } version || version < ImportApiVersion) return null;

        try
        {
            var subscriber = importList ??= Svc.PluginInterface
                .GetIpcSubscriber<string, int>("Artisan.ImportList");
            return subscriber.InvokeFunc(json);
        }
        catch (IpcNotReadyError)
        {
            LogOnce(ref loggedImportNotReady, () => KitServices.Log.Warning(
                "Artisan reports list IPC version {Version} but does not provide Artisan.ImportList.", version));
        }
        catch (IpcError ex)
        {
            LogOnce(ref loggedImportIpcError, () => KitServices.Log.Warning(
                "Artisan.ImportList IPC call failed. {Cause}", Flatten(ex)));
        }
        catch (Exception ex)
        {
            LogOnce(ref loggedImportFailed, () => KitServices.Log.Warning(
                "Artisan.ImportList threw. {Cause}", Flatten(ex)));
        }

        return null;
    }

    private static void LogOnce(ref bool logged, Action log)
    {
        if (logged) return;
        logged = true;
        log();
    }

    /// <summary>
    /// The whole inner exception chain on one line. An <see cref="IpcTypeMismatchError"/>'s own
    /// message names only the two types; the reason sits in its inner exceptions.
    /// </summary>
    private static string Flatten(Exception ex)
    {
        var chain = new List<Exception>();
        for (var e = ex; e != null; e = e.InnerException) chain.Add(e);
        return string.Join(" -> ", chain.Select(e => $"{e.GetType().Name}: {e.Message}"));
    }
}
