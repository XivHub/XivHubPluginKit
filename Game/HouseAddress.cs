using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace XivHubPluginKit.Game;

/// <summary>
/// Where the player stands in a residential district, as a visitor would be told it: the world and its data
/// centre, the district ("Mist"), the ward and the plot, both from 1. <c>Plot</c> is 0 on the ward's streets and in
/// an apartment; <c>Room</c> is the apartment's number, or the private chamber's inside a company house, else 0.
/// </summary>
public readonly record struct HouseAddress(string World, string DataCenter, string District, int Ward, int Plot, int Room, bool Apartment);

/// <summary>Reads the house the player is in or at. Framework thread only.</summary>
public static unsafe class HouseAddresses
{
    /// <summary>
    /// The address of where the player stands, or null outside a residential district. Inside a house, a chamber or
    /// an apartment it comes from the indoor house id, whose territory is the ward's and not the interior's.
    /// Outside, the ward and plot are the housing manager's and the world is the one the player is on now
    /// (<paramref name="currentWorld"/>), which a visitor from another world shares with the house.
    /// </summary>
    public static HouseAddress? Read(IDataManager data, uint currentWorld, uint territory)
    {
        var manager = HousingManager.Instance();
        if (manager == null) return null;

        // SAFETY: manager is non-null; these are ClientStructs member functions on the HousingManager singleton.
        if (manager->IsInside())
        {
            // All bits set when no indoor territory is loaded.
            var id = manager->GetCurrentIndoorHouseId();
            if (id.Id == ulong.MaxValue) return null;
            // 0x3FF is the company workshop, which nobody is sent to by number.
            int room = id.IsWorkshop ? 0 : id.RoomNumber;
            return Build(data, id.WorldId, id.TerritoryTypeId, id.WardIndex + 1, id.IsApartment ? 0 : id.PlotIndex + 1, room, id.IsApartment);
        }
        if (!manager->IsOutside()) return null;
        // Both are negative when unknown; the plot is also negative on the streets and at an apartment building.
        int ward = manager->GetCurrentWard();
        if (ward < 0) return null;
        int plot = manager->GetCurrentPlot();
        return Build(data, currentWorld, territory, ward + 1, plot < 0 ? 0 : plot + 1, 0, false);
    }

    private static HouseAddress? Build(IDataManager data, uint world, uint territory, int ward, int plot, int room, bool apartment)
    {
        if (!data.GetExcelSheet<World>().TryGetRow(world, out var w)) return null;
        if (!data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t)) return null;
        string district = t.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
        if (district.Length == 0) return null;
        // World.DataCenter names one data centre for a whole region (Chaos for every European world, Aether for every
        // North American one). The world's own is the byte after it, which the sheet's schema leaves unnamed.
        string dataCenter = data.GetExcelSheet<WorldDCGroupType>().TryGetRow(w.Unknown0, out var dc) ? dc.Name.ExtractText() : "";
        return new HouseAddress(w.Name.ExtractText(), dataCenter, district, ward, plot, room, apartment);
    }
}
