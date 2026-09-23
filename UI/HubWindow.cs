using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace XivHubPluginKit.UI;

/// <summary>
/// <see cref="FitHeight"/> caps a <see cref="Window"/> at its own content height, for a window
/// whose content is a fixed amount of stuff (a settings page, a short report) rather than a scroll
/// region: without it, ImGui lets the user drag the window taller than anything it draws, leaving
/// empty chrome below the last widget.
/// </summary>
public static class HubWindow
{
    /// <summary>
    /// Call once, at the very end of <see cref="Window.Draw"/>. Measures how tall this frame's
    /// content was — the cursor position after the last widget, plus the window's bottom padding —
    /// and stores it as next frame's <c>SizeConstraints.MaximumSize.Y</c>. ImGui applies size
    /// constraints before <c>Draw</c> runs, so the new maximum takes effect one frame after the
    /// content that produced it; that lag is one frame and never visible, since a window's content
    /// height does not change every frame.
    ///
    /// <c>MinimumSize</c> and <c>MaximumSize.X</c> are left untouched — the window sets those once,
    /// the way it always did, and this only ever rewrites the one field it owns. A window that has
    /// not set <see cref="Window.SizeConstraints"/> yet has no minimum to keep, so this is a no-op
    /// until the window has (set at least <c>MinimumSize</c> before the first <c>Draw</c>).
    ///
    /// <see cref="Window.SizeConstraints"/> is in the same unscaled units as
    /// <see cref="Window.Size"/>; <see cref="ImGuiHelpers.GlobalScale"/> is applied once, by
    /// <c>WindowHost</c>, when it turns <c>SizeConstraints</c> into the next frame's ImGui call. The
    /// cursor position measured here is already in scaled screen pixels, so it is divided by
    /// <see cref="ImGuiHelpers.GlobalScale"/> before being stored, to land back in that same
    /// unscaled basis rather than being scaled twice.
    ///
    /// Do not call this from a window whose child fills <c>GetContentRegionAvail().Y</c>: that
    /// child sizes itself off the window's *current* height, so the cursor position this method
    /// measures would just report the window's own last height back to it, and the window would
    /// never track real content growth or shrinkage.
    /// </summary>
    public static void FitHeight(Window window)
    {
        if (window.SizeConstraints is not { } constraints)
            return;

        var contentHeight = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y;

        var max = constraints.MaximumSize;
        max.Y = contentHeight / ImGuiHelpers.GlobalScale;
        constraints.MaximumSize = max;

        window.SizeConstraints = constraints;
    }
}
