using Dalamud.Bindings.ImGui;

namespace XivHubPluginKit.UI;

/// <summary>
/// The secondary button: the slate fill from the theme table with a dim gold hairline that brightens on hover.
/// ImGui has one <c>Border</c> colour for every frame, so the gold rim cannot come from the style table without
/// also ringing every input and checkbox in gold; this scopes it to the button. Inside <see cref="HubStyle.Primary"/>
/// it draws a plain <c>ImGui.Button</c>, which keeps the primary's own bright rim. With the theme off it is
/// <c>ImGui.Button</c> exactly.
/// </summary>
public static class HubButton
{
    public static bool Draw(string label) => Draw(label, small: false);

    public static bool Small(string label) => Draw(label, small: true);

    private static bool Draw(string label, bool small)
    {
        if (!HubStyle.Enabled || HubStyle.InPrimary) return small ? ImGui.SmallButton(label) : ImGui.Button(label);

        ImGui.PushStyleColor(ImGuiCol.Border, HubColors.Get("HubGold", 0.40f));
        bool pressed = small ? ImGui.SmallButton(label) : ImGui.Button(label);
        ImGui.PopStyleColor(1);

        // The hover rim is drawn over the button after the fact: whether it is hovered is only known once it exists.
        if (ImGui.IsItemHovered())
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                ImGui.GetColorU32(HubColors.Get("HubGoldBright", 0.85f)), ImGui.GetStyle().FrameRounding);
        return pressed;
    }
}
