using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XivHubPluginKit.Retainer;

/// <summary>
/// A single retainer market slot row from a live memory read.
/// </summary>
public readonly record struct RetainerMarketRow(uint Slot, uint ItemId, bool Hq, uint Qty, uint Price);

public static class RetainerMarket
{
    /// <summary>
    /// Reads the CURRENTLY-OPEN retainer's market listings directly from game
    /// memory (InventoryManager's RetainerMarket container + RetainerMarketPrices
    /// span), so it sees listings added earlier in the same session that a
    /// polled cache would not yet carry. Only valid while that retainer's
    /// RetainerSellList is open, the game only loads the RetainerMarket
    /// container + price span then. Must be called on the framework thread.
    /// Returns slots in container order, which is NOT the order RetainerSellList
    /// draws them; empty slots are skipped.
    /// </summary>
    public static unsafe List<RetainerMarketRow> ReadLive(IPluginLog log)
    {
        var result = new List<RetainerMarketRow>();
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return result;
            var container = im->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null) return result;
            // Per-slot unit prices, parallel to the container by slot index.
            var prices = im->RetainerMarketPrices;
            // The RetainerMarket container can be sparse (e.g. filled slots
            // 0,3,4,10), so Slot here is the packed index, not the container
            // slot; price/item still come from the real container slot i.
            //
            // That packed index is NOT the RetainerSellList row order, however
            // much it looks like it should be. Measured: the row this named for
            // Country Flooring opened Mahogany Screen, and the rows it named
            // for Classroom Chair opened Glade Bench. Use this for WHAT is
            // listed and how much of it; ask RetainerWalk.ReadSellListRows
            // which row holds what.
            int visualRow = 0;
            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                // RetainerMarketPrices is Span<ulong>; a listing price always
                // fits in uint (game cap is 999,999,999). Quantity is int.
                uint price = i < prices.Length ? (uint)prices[i] : 0u;
                bool hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                result.Add(new RetainerMarketRow((uint)visualRow, slot->ItemId, hq, (uint)slot->Quantity, price));
                visualRow++;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RetainerMarket.ReadLive failed");
        }
        return result;
    }
}
