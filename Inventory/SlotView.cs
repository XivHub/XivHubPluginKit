using FFXIVClientStructs.FFXIV.Client.Game;

namespace XivHubPluginKit.Inventory;

/// <summary>
/// Lightweight managed view of a single non-empty inventory slot, produced by
/// <see cref="InventoryScan.ScanContainer"/>. Holds no game pointers; safe to retain across frames.
/// Unlike InventoryCleaner's curated (gear-only) <c>Models/InventoryItem.cs</c>, this carries
/// <see cref="Qty"/> since <see cref="InventoryScan.ScanContainer"/> also covers stackable items.
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\XivHubPluginKit\Inventory\SlotView.cs" Link="Kit\Inventory\SlotView.cs" /&gt;
/// </summary>
public sealed class SlotView
{
    /// <summary>The item's row id in the <c>Item</c> sheet.</summary>
    public readonly uint ItemId;

    /// <summary>The container this item lives in (e.g. <see cref="InventoryType.Inventory1"/>).</summary>
    public readonly InventoryType Container;

    /// <summary>0-based index into the container's <c>Items</c> array.</summary>
    public readonly int SlotIndex;

    /// <summary>Stack size, from <c>GameInventoryItem.Quantity</c>.</summary>
    public readonly uint Qty;

    /// <summary>Whether the in-memory item is the high-quality variant.</summary>
    public readonly bool IsHq;

    /// <summary>Spiritbond/collectability percent from <c>GameInventoryItem.SpiritbondOrCollectability</c>.</summary>
    public readonly byte Spiritbond;

    /// <summary>Item name from the Lumina sheet.</summary>
    public readonly string Name;

    /// <summary>Item level from <see cref="Lumina.Excel.Sheets.Item.LevelItem"/> RowId.</summary>
    public readonly ushort Ilvl;

    public SlotView(
        uint itemId,
        InventoryType container,
        int slotIndex,
        uint qty,
        bool isHq,
        byte spiritbond,
        string name,
        ushort ilvl)
    {
        this.ItemId = itemId;
        this.Container = container;
        this.SlotIndex = slotIndex;
        this.Qty = qty;
        this.IsHq = isHq;
        this.Spiritbond = spiritbond;
        this.Name = name;
        this.Ilvl = ilvl;
    }
}
