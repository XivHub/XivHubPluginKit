using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivHubPluginKit.Inventory;

namespace XivHubPluginKit.Retainer;

/// <summary>
/// Moves items out of a summoned retainer's inventory with the game's native retainer item
/// command, the function AutoRetainer calls <c>RetainerItemCommand</c>. It is the only way items
/// leave a retainer here: <c>InventoryManager.MoveItemSlot</c> is never called on a
/// <c>RetainerPage*</c> container, because those containers accept only the native command.
///
/// Invariants every caller relies on:
/// <list type="bullet">
/// <item>A retrieve moves a whole stack. The command has no quantity argument.</item>
/// <item>Framework thread only: it reads the agent module and addon state and calls into the game.</item>
/// <item>Only while the retainer's inventory window is open. The <c>RetainerPage*</c> containers
/// are loaded when "Entrust or withdraw items" opens it, and <see cref="Retrieve"/> refuses to
/// fire otherwise.</item>
/// <item>The move round-trips to the server, so the item lands in the bags some frames after
/// <see cref="Retrieve"/> returns. A true return means the command was sent, not that it landed;
/// judge landing by the bags or by <see cref="LivePages"/>.</item>
/// </list>
///
/// The signature binds once, on first touch of this class, and is invoked directly rather than
/// hooked: nothing here needs to observe the call, only fire it. Needs <c>ECommonsMain.Init</c>
/// (<c>Svc.SigScanner</c>) and <c>KitServices.Init</c> (<c>Log</c>, <c>LogPrefix</c>) before first use.
/// </summary>
public static unsafe class RetainerRetrieve
{
    // AutoRetainer Internal/Memory.cs, RetainerItemCommandHook.
    private const string Signature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    // AutoRetainer Internal/InventoryManagement/RetainerItemCommand.cs.
    private enum RetainerItemCommand : long
    {
        RetrieveFromRetainer = 0,
        EntrustToRetainer = 1,
        EntrustQuantity = 4,
        HaveRetainerSellItem = 5,
    }

    private delegate void RetainerItemCommandDelegate(nint agentModule, uint slot, InventoryType invType, uint a4, RetainerItemCommand cmd);

    private static readonly RetainerItemCommandDelegate? invoke;

    /// <summary>
    /// True once the command's signature bound. False means the byte pattern broke on a game
    /// patch: every <see cref="Retrieve"/> is then a no-op returning false, never a call through
    /// a bad pointer, and the caller should report the command as unavailable.
    /// </summary>
    public static bool Bound { get; private set; }

    static RetainerRetrieve()
    {
        try
        {
            var address = Svc.SigScanner.ScanText(Signature);
            if (address == nint.Zero)
            {
                Bound = false;
                KitServices.Log.Error($"{KitServices.LogPrefix} RetainerItemCommand signature not found (ScanText returned 0); retrieve is disabled until the plugin is updated for this game version.");
                return;
            }

            invoke = Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(address);
            Bound = true;
        }
        catch (Exception ex)
        {
            Bound = false;
            KitServices.Log.Error(ex, $"{KitServices.LogPrefix} Failed to bind the RetainerItemCommand signature; retrieve is disabled until the plugin is updated for this game version.");
        }
    }

    /// <summary>The summoned retainer's item pages.</summary>
    public static readonly InventoryType[] Pages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    /// <summary>
    /// Every non-empty slot across the summoned retainer's item pages, right now, page then slot.
    /// Valid only while the retainer's inventory is open: the pages are populated on demand, and
    /// a scan before that reads empty or stale containers. Re-scan before each retrieve, since the
    /// pages shift as stacks leave. Framework thread.
    /// </summary>
    public static List<SlotView> LivePages()
    {
        var result = new List<SlotView>();
        foreach (var page in Pages)
            result.AddRange(InventoryScan.ScanContainer(page));
        return result;
    }

    /// <summary>
    /// True once the retainer inventory window is present and ready. The game draws
    /// <c>InventoryRetainerLarge</c> or <c>InventoryRetainer</c> depending on the player's
    /// inventory layout setting, so either counts. Framework thread.
    /// </summary>
    // SAFETY: TryGetAddonByName yields the game's live addon pointer for this frame or fails;
    // IsAddonReady only reads its fields, and the pointer is not kept past the call.
    public static bool IsInventoryReady()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InventoryRetainerLarge", out var large)
            && GenericHelpers.IsAddonReady(large))
            return true;

        return GenericHelpers.TryGetAddonByName<AtkUnitBase>("InventoryRetainer", out var addon)
               && GenericHelpers.IsAddonReady(addon);
    }

    /// <summary>
    /// Sends <c>RetrieveFromRetainer</c> for one slot of a <c>RetainerPage*</c> container, moving
    /// that slot's whole stack to the player's bags. Returns false without invoking anything unless
    /// the signature is bound, the Retainer agent is active and the retainer's inventory is ready,
    /// the same preconditions AutoRetainer checks (<c>IsAgentRetainerActive</c>,
    /// <c>IsRetainerInventoryLoaded</c>) before it fires the command. A false return is never a
    /// move. A true return means the command was sent; the stack lands after a server round trip.
    /// Framework thread.
    /// </summary>
    // SAFETY: invoke is non-null only when the signature bound to a function in the game's .text.
    // AgentModule.Instance() and GetAgentByInternalId return the game's own long-lived objects,
    // both null-checked before use. The first argument is the Retainer agent + 40, the item-command
    // module inside it (AutoRetainer InventorySpaceManager.AgentRetainerItemCommandModule); the
    // game only reads through it while the agent is active, which is checked on this same frame.
    public static bool Retrieve(uint slot, InventoryType page)
    {
        if (!Bound || invoke == null)
            return false;

        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return false;

        var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if (agent == null || !agent->IsAgentActive())
            return false;

        if (!IsInventoryReady())
            return false;

        try
        {
            // a4's meaning is not known; AutoRetainer passes 0 for every command it fires.
            invoke((nint)agent + 40, slot, page, 0, RetainerItemCommand.RetrieveFromRetainer);
            return true;
        }
        catch (Exception ex)
        {
            // An exception escaping into the framework tick would recur every frame.
            KitServices.Log.Error(ex, $"{KitServices.LogPrefix} RetainerItemCommand invoke threw; treating the retrieve as failed.");
            return false;
        }
    }
}
