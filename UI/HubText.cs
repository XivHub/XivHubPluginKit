using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XivHubPluginKit.UI;

/// <summary>
/// Coloured text that wraps at the window's right edge, so a role colour never becomes an
/// unreadable single line running off the window. A raw <c>ImGui.TextColored(HubStyle.X, text)</c>
/// call site does not wrap; this is the wrapping default that call site should have used instead.
///
/// Each role reads the matching <see cref="HubStyle"/> value directly rather than caching it, so a
/// plugin with the theme switched off still shows the role's colour: <c>HubStyle.Bad</c> and friends
/// stay meaningful content even when the chrome around them falls back to the user's own style
/// (see "What theme off means" in <c>UI/THEME.md</c>).
/// </summary>
public static class HubText
{
    public static void Bad(string text) => Colored(HubStyle.Bad, text);
    public static void Warn(string text) => Colored(HubStyle.Warn, text);
    public static void Good(string text) => Colored(HubStyle.Good, text);
    public static void Info(string text) => Colored(HubStyle.Info, text);
    public static void Faint(string text) => Colored(HubStyle.Faint, text);
    public static void Muted(string text) => Colored(HubStyle.Muted, text);
    public static void Accent(string text) => Colored(HubStyle.Accent, text);

    /// <summary>Coloured text that wraps at the window edge, for a message whose length isn't fixed
    /// (an error or a result that quotes the game or a server).</summary>
    public static void Colored(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>A coloured fragment on a line built with <c>SameLine</c> (a number inside a
    /// sentence): does not wrap, since it shares the line with text drawn around it.</summary>
    public static void Inline(Vector4 color, string text) => ImGui.TextColored(color, text);
}
