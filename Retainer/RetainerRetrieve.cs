using System;
using System.Collections.Generic;
using System.Linq;
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
/// <item>A retrieve moves a whole stack. <c>RetrieveFromRetainer</c> has no quantity argument.</item>
/// <item>Framework thread only: it reads the agent module, addon state and inventory containers and
/// calls into the game. <see cref="Retrieve"/> refuses and logs an error off that thread.</item>
/// <item>Only while the retainer's inventory window is open. The <c>RetainerPage*</c> containers
/// are loaded when "Entrust or withdraw items" opens it, and <see cref="Retrieve"/> refuses to
/// fire otherwise.</item>
/// <item>The move round-trips to the server, so the item lands in the bags some frames after
/// <see cref="Retrieve"/> returns. A true return means the command was sent, not that it landed;
/// judge landing by the bags or by <see cref="LivePages"/>.</item>
/// </list>
///
/// The function is bound by signature on first read of <see cref="Bound"/> or first
/// <see cref="Retrieve"/>, and invoked directly rather than hooked: nothing here needs to observe
/// the call, only fire it. The binding lives in its own nested class, so a failed bind leaves
/// <see cref="Pages"/>, <see cref="PagesLoaded"/>, <see cref="LivePages"/> and
/// <see cref="IsInventoryReady"/> working. Needs <c>ECommonsMain.Init</c> (<c>Svc.SigScanner</c>,
/// <c>Svc.Framework</c>) and <c>KitServices.Init</c> (<c>Log</c>, <c>LogPrefix</c>) before first use.
///
/// A typed alternative to the signature exists: FFXIVClientStructs declares <c>AgentRetainer</c>
/// as inheriting <c>AgentInventoryContext.InventoryContextEvent</c>, whose virtual 0,
/// <c>HandleCallback(slot, inventoryType, flags, callbackParam)</c>, lists the same command values
/// for <c>AgentRetainer</c>. Whether the signature-bound function is that virtual is not
/// confirmed; comparing the event's vtable slot 0 with the bound address in game settles it, and
/// only then can the call go through ClientStructs instead of a byte pattern.
/// </summary>
public static unsafe class RetainerRetrieve
{
    // AutoRetainer Internal/InventoryManagement/RetainerItemCommand.cs. The same values appear as
    // AgentRetainer's callbackParam list on FFXIVClientStructs
    // AgentInventoryContext.InventoryContextEvent.HandleCallback.
    private enum RetainerItemCommand : long
    {
        RetrieveFromRetainer = 0,
        EntrustToRetainer = 1,
        RetrieveQuantity = 3,
        EntrustQuantity = 4,
        HaveRetainerSellItem = 5,
    }

    // a4 is the InventoryContextFlag of the click that would have issued the command
    // (FFXIVClientStructs AgentInventoryContext.InventoryContextFlag: HeldCtrlKey = 1,
    // IsPadModeEnabled = 8); 0 means no modifier.
    private delegate void RetainerItemCommandDelegate(nint itemCommandModule, uint slot, InventoryType invType, uint a4, RetainerItemCommand cmd);

    /// <summary>
    /// True once the command's signature bound. False means it did not: either the services it
    /// needs were not initialised, or the byte pattern no longer matches this game version; the
    /// log line says which. Every <see cref="Retrieve"/> is then a no-op returning false, never a
    /// call through a bad pointer, and the caller should report the command as unavailable.
    /// </summary>
    public static bool Bound => Binding.Bound;

    private static class Binding
    {
        // AutoRetainer Internal/Memory.cs, RetainerItemCommandHook.
        private const string Signature =
            "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

        internal static readonly RetainerItemCommandDelegate? Invoke;
        internal static readonly bool Bound;

        // Catches everything, so a bind failure never becomes a TypeInitializationException on
        // every later read of Bound.
        static Binding()
        {
            try
            {
                var scanner = Svc.SigScanner;
                if (scanner == null)
                {
                    KitServices.Log?.Error($"{KitServices.LogPrefix} RetainerItemCommand not bound: Svc.SigScanner is null because ECommonsMain.Init has not run; retrieve is disabled for this session.");
                    return;
                }

                if (!scanner.TryScanText(Signature, out var address))
                {
                    KitServices.Log?.Error($"{KitServices.LogPrefix} RetainerItemCommand signature not found in this game version; retrieve is disabled until the signature is updated from AutoRetainer's RetainerItemCommandHook.");
                    return;
                }

                Invoke = Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(address);
                Bound = true;
            }
            catch (Exception ex)
            {
                Invoke = null;
                Bound = false;
                KitServices.Log?.Error(ex, $"{KitServices.LogPrefix} RetainerItemCommand not bound: the bind threw; retrieve is disabled for this session.");
            }
        }
    }

    /// <summary>The summoned retainer's item pages, in page order.</summary>
    public static IReadOnlyList<InventoryType> Pages { get; } =
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
    /// True when every one of <see cref="Pages"/> is a loaded container, so a scan reads the
    /// summoned retainer's items rather than empty or stale slots. The window can report ready
    /// before its containers fill; wait for this too. Framework thread.
    /// </summary>
    // SAFETY: InventoryManager.Instance() and GetInventoryContainer return the game's own
    // long-lived objects, both null-checked; only the IsLoaded field is read, within this call.
    public static bool PagesLoaded()
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return false;
        foreach (var page in Pages)
        {
            var container = mgr->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Every non-empty slot across the summoned retainer's item pages, right now, page then slot.
    /// Valid only while the retainer's inventory is open and <see cref="PagesLoaded"/>: before
    /// that, a scan reads empty or stale containers. Re-scan before each retrieve, since the pages
    /// shift as stacks leave. Framework thread.
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
    /// that slot's whole stack to the player's bags. Everything is checked on this frame, right
    /// before the call: the caller is on the framework thread, the signature is bound, the
    /// Retainer agent is active and the retainer's inventory is ready (AutoRetainer's
    /// <c>IsAgentRetainerActive</c> and <c>IsRetainerInventoryLoaded</c>); <paramref name="page"/>
    /// is one of <see cref="Pages"/>, its container is loaded and <paramref name="slot"/> is inside
    /// it; and the slot still holds <paramref name="expectItemId"/> x <paramref name="expectQty"/>,
    /// the values the caller scanned (AutoRetainer <c>InventorySpaceManager.SafeSellSlot</c> checks
    /// the same before its command), so a stack that moved since the scan is never retrieved in
    /// its place. Any failed check returns false without invoking anything and logs why; a false
    /// return is never a move. A true return means the command was sent; the stack lands after a
    /// server round trip.
    /// </summary>
    // SAFETY: Binding.Invoke is non-null only when the signature bound to a function in the game's
    // .text. AgentModule.Instance(), GetAgentByInternalId, InventoryManager.Instance() and
    // GetInventoryContainer return the game's own long-lived objects, each null-checked; the item
    // read is Items + slot with slot < Size checked first. The command's first argument is the
    // Retainer agent + 40: FFXIVClientStructs lays AgentRetainer out as AgentInterface
    // (sizeof 0x28 = 40) followed by its AgentInventoryContext.InventoryContextEvent base, and
    // AutoRetainer passes the same address (InventorySpaceManager.AgentRetainerItemCommandModule).
    // The game only reads through it while the agent is active, which is checked on this frame,
    // and every check above runs on the framework thread, so nothing changes between the checks
    // and the call.
    public static bool Retrieve(uint slot, InventoryType page, uint expectItemId, uint expectQty)
    {
        var framework = Svc.Framework;
        if (framework == null || !framework.IsInFrameworkUpdateThread)
        {
            KitServices.Log?.Error(framework == null
                ? $"{KitServices.LogPrefix} Retrieve refused: Svc.Framework is null because ECommonsMain.Init has not run."
                : $"{KitServices.LogPrefix} Retrieve refused: called off the framework thread.");
            return false;
        }

        var invoke = Binding.Invoke;
        if (!Binding.Bound || invoke == null)
            return false;

        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return Refuse(slot, page, "the agent module is not available");

        var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if (agent == null || !agent->IsAgentActive())
            return Refuse(slot, page, "the Retainer agent is not active");

        if (!IsInventoryReady())
            return Refuse(slot, page, "the retainer inventory window is not ready");

        if (!Pages.Contains(page))
            return Refuse(slot, page, "not a retainer page");

        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return Refuse(slot, page, "the inventory manager is not available");

        var container = mgr->GetInventoryContainer(page);
        if (container == null || !container->IsLoaded)
            return Refuse(slot, page, "the page's container is not loaded");

        if (container->Size <= 0 || slot >= (uint)container->Size)
            return Refuse(slot, page, $"the slot is outside the page's {container->Size} slots");

        var item = container->Items + slot;
        if (expectItemId == 0 || item->ItemId != expectItemId || (uint)item->Quantity != expectQty)
            return Refuse(slot, page, $"the slot holds item {item->ItemId} x{item->Quantity}, expected {expectItemId} x{expectQty}");

        try
        {
            invoke((nint)agent + 40, slot, page, 0, RetainerItemCommand.RetrieveFromRetainer);
            return true;
        }
        catch (Exception ex)
        {
            // Only a managed exception reaches here, such as one thrown by another plugin's detour
            // on this function; a native access violation inside the game is not catchable and
            // takes the process down. Escaping into the framework tick would recur every frame.
            KitServices.Log?.Error(ex, $"{KitServices.LogPrefix} RetainerItemCommand invoke threw; treating the retrieve as failed.");
            return false;
        }
    }

    private static bool Refuse(uint slot, InventoryType page, string why)
    {
        KitServices.Log?.Warning($"{KitServices.LogPrefix} Retrieve refused for {page}:{slot}: {why}.");
        return false;
    }
}
