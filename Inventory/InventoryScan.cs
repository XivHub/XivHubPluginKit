using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using GameInventoryItem = FFXIVClientStructs.FFXIV.Client.Game.InventoryItem;

namespace ZhyraPluginKit.Inventory;

/// <summary>
/// Read-only, generic iteration over live game inventory containers. All methods hit live game
/// state via <see cref="InventoryManager"/>; callers should consume the results within a frame.
/// Unlike InventoryCleaner's curated (gear-only, non-generic) <c>Game/InventoryReader.cs</c>
/// iterators, <see cref="ScanContainer"/> is a single generic entry point for any
/// <see cref="InventoryType"/> — including stackable, qty-bearing containers (retainer pages, main
/// bags) that InventoryCleaner's reader never needed to model.
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\ZhyraPluginKit\Inventory\InventoryScan.cs" Link="Kit\Inventory\InventoryScan.cs" /&gt;
/// </summary>
public static unsafe class InventoryScan
{
    /// <summary>
    /// Free slots in the main inventory. Uses <see cref="InventoryManager.GetEmptySlotsInBag"/>.
    /// NOTE (needs in-game verification, per PLAN.md): <c>GetEmptySlotsInBag</c> may over-count by
    /// including non-bag containers (saddlebag / retainer bags). This is now consumed by two plugins,
    /// including RetainerReach's retrieve-enable gating, so confirm the count is main-bag-only before
    /// relying on it; fall back to iterating <c>Inventory1..4</c> if it over-counts.
    /// </summary>
    public static int FreeSlotsInBag()
    {
        var mgr = InventoryManager.Instance();
        return mgr == null ? 0 : (int)mgr->GetEmptySlotsInBag();
    }

    /// <summary>
    /// Returns the first free slot found across <paramref name="candidates"/>, checked in order;
    /// null if every candidate container is full or missing.
    /// </summary>
    public static (InventoryType Type, int Slot)? FindFreeSlot(params InventoryType[] candidates)
        => FindFreeSlot((IReadOnlyList<InventoryType>)candidates);

    /// <summary>
    /// Allocation-free overload for callers that already hold candidates as a list (e.g.
    /// <see cref="MoveOp.DstCandidates"/>): avoids copying to a <c>params</c> array each call.
    /// Returns the first free slot across <paramref name="candidates"/> in order; null if all
    /// full/missing.
    /// </summary>
    public static (InventoryType Type, int Slot)? FindFreeSlot(IReadOnlyList<InventoryType> candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            var free = FindFreeSlotIn(candidates[i]);
            if (free != null)
                return free;
        }

        return null;
    }

    /// <summary>
    /// Occupied and total slot counts for <paramref name="type"/>. Returns (0, 0) when the container
    /// is missing or not loaded, which callers should treat as "no occupancy information" rather than
    /// "empty".
    /// </summary>
    public static (int Used, int Size) ContainerUsage(InventoryType type)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return (0, 0);
        var container = mgr->GetInventoryContainer(type);
        if (container == null)
            return (0, 0);

        var size = (int)container->Size;
        var used = 0;
        for (var i = 0; i < size; i++)
        {
            if ((container->Items + i)->ItemId != 0)
                used++;
        }

        return (used, size);
    }

    /// <summary>Returns the first free slot in <paramref name="type"/>, or null if full/missing.</summary>
    public static (InventoryType Type, int Slot)? FindFreeSlotIn(InventoryType type)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return null;
        var container = mgr->GetInventoryContainer(type);
        if (container == null)
            return null;
        var size = container->Size;
        for (var i = 0; i < size; i++)
        {
            var slot = container->Items + i;
            if (slot->ItemId == 0)
                return (type, i);
        }

        return null;
    }

    /// <summary>
    /// Yields a managed <see cref="SlotView"/> for every non-empty slot of <paramref name="type"/>.
    /// Reads <see cref="SlotView.Qty"/> from <c>GameInventoryItem.Quantity</c>, HQ from
    /// <c>IsHighQuality()</c>, spiritbond from <c>SpiritbondOrCollectability</c>, and name/ilvl via
    /// <see cref="ItemSheet"/>. Yielded views hold no pointers; safe to retain across frames.
    /// </summary>
    public static IEnumerable<SlotView> ScanContainer(InventoryType type)
    {
        var result = new List<SlotView>();
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return result;
        var container = mgr->GetInventoryContainer(type);
        if (container == null)
            return result;
        var size = container->Size;
        for (var i = 0; i < size; i++)
        {
            var slot = container->Items + i;
            if (slot->ItemId == 0)
                continue;
            result.Add(Build(slot));
        }

        return result;
    }

    private static SlotView Build(GameInventoryItem* slot)
    {
        return new SlotView(
            slot->ItemId,
            slot->Container,
            slot->Slot,
            (uint)slot->Quantity,
            slot->IsHighQuality(),
            (byte)slot->SpiritbondOrCollectability,
            ItemSheet.Name(slot->ItemId),
            ItemSheet.Ilvl(slot->ItemId));
    }
}
