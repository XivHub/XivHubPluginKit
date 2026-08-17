using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace ZhyraPluginKit.Game;

/// <summary>Determines whether flight is unlocked in a zone, the way Questionable does it:
/// the territory's AetherCurrentCompFlgSet completed in PlayerState. Unlike Control.CanFly this
/// does NOT require being mounted, so we can decide to fly before mounting.</summary>
public static unsafe class FlightHelper
{
    private static Dictionary<uint, uint>? territoryToCompFlgSet;

    public static bool FlyingUnlocked(uint territoryId)
    {
        territoryToCompFlgSet ??= BuildMap();

        var ps = PlayerState.Instance();
        return ps != null
               && territoryToCompFlgSet.TryGetValue(territoryId, out var compFlgSet)
               && ps->IsAetherCurrentZoneComplete(compFlgSet);
    }

    private static Dictionary<uint, uint> BuildMap()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var t in KitServices.DataManager.GetExcelSheet<TerritoryType>())
        {
            if (t.RowId > 0 && t.AetherCurrentCompFlgSet.RowId > 0)
                map[t.RowId] = t.AetherCurrentCompFlgSet.RowId;
        }
        return map;
    }
}
