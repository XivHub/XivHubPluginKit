using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace XivHubPluginKit.Inventory;

/// <summary>
/// What one unit of an item is worth away from the market board, so an
/// undercutter knows where to stop.
///
/// A market listing is only worth making below these numbers if you would
/// rather have less gil. The board price is not the gil you receive: the sale
/// is taxed, so the comparison has to be made on the net (<see cref="Floor"/>).
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\XivHubPluginKit\Inventory\VendorPrice.cs" Link="Kit\Inventory\VendorPrice.cs" /&gt;
/// </summary>
public static class VendorPrice
{
    /// <summary>
    /// The share of an asking price the seller keeps. FFXIV taxes a market sale
    /// at 5%; city rates move below that, so keeping the full 5% here makes the
    /// floor conservative rather than optimistic.
    /// </summary>
    public const double NetOfTax = 0.95;

    private static HashSet<uint>? gilShopItems;

    /// <summary>Item ids a gil shop stocks, so <see cref="Item.PriceMid"/> is a
    /// price someone will actually honour rather than a nominal one.</summary>
    private static HashSet<uint> GilShopItems => gilShopItems ??= BuildGilShopItems();

    /// <summary>Gil an NPC hands you for one unit; 0 when none will take it.
    /// The NQ figure, which for an HQ unit is an understatement and so still a
    /// floor.</summary>
    public static int Buyback(uint itemId)
    {
        var row = ItemSheet.ById(itemId);
        return row.HasValue ? (int)row.Value.PriceLow : 0;
    }

    /// <summary>Gil a shop charges to replace one unit; 0 when no gil shop
    /// stocks it. Unlimited supply at this price is what makes undercutting
    /// below it unwinnable rather than merely thin.</summary>
    public static int Replacement(uint itemId)
    {
        if (!GilShopItems.Contains(itemId))
            return 0;
        var row = ItemSheet.ById(itemId);
        return row.HasValue ? (int)row.Value.PriceMid : 0;
    }

    /// <summary>The better of the two: what the unit is worth without the board.</summary>
    public static int Outside(uint itemId)
    {
        int buyback = Buyback(itemId);
        int replacement = Replacement(itemId);
        return buyback > replacement ? buyback : replacement;
    }

    /// <summary>
    /// The lowest asking price whose after-tax proceeds still match what the
    /// unit is worth off the board; 0 when it is worth nothing off the board and
    /// any price beats holding it.
    /// </summary>
    public static int Floor(uint itemId)
    {
        int outside = Outside(itemId);
        return outside <= 0 ? 0 : (int)System.Math.Ceiling(outside / NetOfTax);
    }

    private static HashSet<uint> BuildGilShopItems()
    {
        var set = new HashSet<uint>();
        var sheet = KitServices.DataManager.GetSubrowExcelSheet<GilShopItem>();
        if (sheet == null)
            return set;
        foreach (var row in sheet)
        {
            foreach (var sub in row)
            {
                uint id = sub.Item.RowId;
                if (id != 0) set.Add(id);
            }
        }
        return set;
    }
}
