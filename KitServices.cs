using Dalamud.Plugin.Services;

namespace XivHubPluginKit;

/// <summary>
/// Static service holder for kit linked-source. Kit code (<c>Inventory/*</c>, etc.) references
/// <see cref="KitServices"/> instead of any per-plugin <c>Plugin.*</c> statics, so the same source
/// file compiles unmodified into every consuming plugin's dll. Call <see cref="Init"/> once, early
/// in each consuming plugin's constructor, before any other kit code runs.
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\XivHubPluginKit\KitServices.cs" Link="Kit\KitServices.cs" /&gt;
/// </summary>
public static class KitServices
{
    /// <summary>The consuming plugin's injected <see cref="IDataManager"/>.</summary>
    public static IDataManager DataManager = null!;

    /// <summary>The consuming plugin's injected <see cref="IPluginLog"/>.</summary>
    public static IPluginLog Log = null!;

    /// <summary>The consuming plugin's injected <see cref="IChatGui"/>.</summary>
    public static IChatGui Chat = null!;

    /// <summary>Prefix prepended to kit-originated chat/log messages (e.g. <c>"[InventoryCleaner]"</c>).</summary>
    public static string LogPrefix = string.Empty;

    /// <summary>Wires the kit's service statics to the consuming plugin's injected services.</summary>
    public static void Init(IDataManager d, IPluginLog l, IChatGui c, string prefix)
    {
        DataManager = d;
        Log = l;
        Chat = c;
        LogPrefix = prefix;
    }
}
