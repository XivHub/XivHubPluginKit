using FFXIVClientStructs.FFXIV.Client.Game;

namespace XivHubPluginKit.Inventory;

/// <summary>
/// Gil, free slots and item counts in the four main bags: the only containers a
/// purchase can land in. Framework thread.
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\XivHubPluginKit\Inventory\MainBags.cs" Link="Kit\Inventory\MainBags.cs" /&gt;
/// </summary>
public static class MainBags
{
    /// <summary>Main bags only. Saddlebag and retainer bags can't hold a purchase run.</summary>
    public static readonly InventoryType[] Types =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>Gil on the character, live. Framework thread.</summary>
    public static unsafe long ReadGil()
    {
        var mgr = InventoryManager.Instance();
        return mgr == null ? 0 : mgr->GetGil();
    }

    /// <summary>
    /// Free slots across the four main bags. Counted by container rather than
    /// <c>InventoryManager.GetEmptySlotsInBag</c>, which also counts containers a
    /// purchase can never land in.
    /// </summary>
    public static int ReadFreeSlots()
    {
        int free = 0;
        foreach (var bag in Types)
        {
            var (used, size) = InventoryScan.ContainerUsage(bag);
            free += size - used;
        }
        return free;
    }

    /// <summary>Units of an item held in the main bags, of one quality.
    ///
    /// Quality is part of the identity, not a detail: NQ and HQ are separate
    /// market-board channels and separate bag stacks, and a run that counted
    /// both would confirm a listing against the wrong stack.</summary>
    public static int Count(uint itemId, bool hq = false)
    {
        int total = 0;
        foreach (var bag in Types)
        {
            foreach (var slot in InventoryScan.ScanContainer(bag))
            {
                if (slot.ItemId == itemId && slot.IsHq == hq)
                    total += (int)slot.Qty;
            }
        }
        return total;
    }
}
