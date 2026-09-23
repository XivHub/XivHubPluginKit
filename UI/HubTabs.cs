using Dalamud.Bindings.ImGui;

namespace XivHubPluginKit.UI;

/// <summary>
/// A tab bar that scrolls instead of truncating. Dalamud's ImGui binding predates ImGui's tab
/// overline, so the selected tab shows through <c>color.tabActive</c> alone; without
/// <c>FittingPolicyScroll</c> a bar that outgrows its window shrinks its labels to a cut-off stub
/// instead (see <c>UI/THEME.md</c>'s "Tables and tabs").
/// </summary>
public static class HubTabs
{
    public static bool Begin(string id) => ImGui.BeginTabBar(id, ImGuiTabBarFlags.FittingPolicyScroll);

    public static void End() => ImGui.EndTabBar();
}
