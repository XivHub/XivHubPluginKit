using System.Collections.Generic;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace XivHubPluginKit.Inventory;

/// <summary>
/// Thin cached wrappers over the Lumina <see cref="Item"/> sheet, backed by
/// <see cref="KitServices.DataManager"/>. Ports only the generic accessors shared across plugins;
/// ClassJob / <c>CanJobEquip</c> / <c>BuildJobSet</c> and <c>ItemSheetOrNull</c> are gearset-specific
/// and stay local to each consuming plugin.
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\XivHubPluginKit\Inventory\ItemSheet.cs" Link="Kit\Inventory\ItemSheet.cs" /&gt;
/// </summary>
public static class ItemSheet
{
    private static ExcelSheet<Item>? sheet;

    private static ExcelSheet<Item>? Sheet => sheet ??= KitServices.DataManager.GetExcelSheet<Item>();

    // The Item sheet is immutable at runtime and Name() is hit in hot paths (live retainer rescans,
    // inventory build, chat echo), where row.Name.ToString() allocated a fresh string every call.
    // Cache by id (game/UI thread only). Bounded by distinct item ids ever queried.
    private static readonly Dictionary<uint, string> nameCache = new();

    /// <summary>Fetches an <see cref="Item"/> row by id; null for id 0 or when not found / sheet missing.</summary>
    public static Item? ById(uint itemId)
    {
        if (itemId == 0 || Sheet == null)
            return null;
        return Sheet.GetRowOrDefault(itemId);
    }

    /// <summary>Item name; empty string for id 0 / not found / sheet missing. Cached by id.</summary>
    public static string Name(uint itemId)
    {
        if (itemId == 0)
            return string.Empty;

        if (nameCache.TryGetValue(itemId, out var cached))
            return cached;

        var row = ById(itemId);
        var name = row.HasValue ? row.Value.Name.ToString() : string.Empty;
        nameCache[itemId] = name;
        return name;
    }

    /// <summary>Item level (<see cref="Item.LevelItem"/> RowId); 0 if not found.</summary>
    public static ushort Ilvl(uint itemId)
    {
        var row = ById(itemId);
        return row.HasValue ? (ushort)row.Value.LevelItem.RowId : (ushort)0;
    }

    /// <summary>Equip slot category (<see cref="Item.EquipSlotCategory"/> RowId); 0 if not found.</summary>
    public static byte EquipSlotCategory(uint itemId)
    {
        var row = ById(itemId);
        return row.HasValue ? (byte)row.Value.EquipSlotCategory.RowId : (byte)0;
    }

    /// <summary>Rarity (<see cref="Item.Rarity"/>); 0 if not found.</summary>
    public static byte Rarity(uint itemId)
    {
        var row = ById(itemId);
        return row.HasValue ? row.Value.Rarity : (byte)0;
    }
}
