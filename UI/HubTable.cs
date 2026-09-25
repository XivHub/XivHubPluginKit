using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace XivHubPluginKit.UI;

/// <summary>
/// A data table with the flags every plugin's table wants and none of them remembered to ask for:
/// row striping, drag-resizable stretch columns, and no saved layout (see <c>UI/THEME.md</c>'s "Tables and
/// tabs" — ImGui saves column widths by position, so a table that gains a column loads the old
/// first column's width into the new one).
///
/// <see cref="Stretch"/> and <see cref="Fit"/> replace a pixel-width <c>TableSetupColumn</c> call:
/// a pixel width clips at larger numbers and font scales, where <see cref="Fit"/> refits every
/// frame to header and contents. <see cref="Icon"/> is the one call site that legitimately needs a
/// fixed pixel size — an icon column's size is the icon, not its content — and is the only place in
/// this class a size is scaled, since it is the only literal pixel value left.
/// </summary>
public static class HubTable
{
    /// <summary>A table with <c>RowBg | Resizable | NoSavedSettings</c> plus <paramref name="extra"/>.
    /// A table whose <paramref name="extra"/> lets the user hide or reorder columns keeps its saved
    /// settings instead, since those choices are the layout the user wants back next session; give
    /// such a table a new <paramref name="id"/> when its columns change.</summary>
    public static bool Begin(string id, int columns, Vector2 size = default, ImGuiTableFlags extra = 0)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | extra;
        if ((extra & (ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable)) == 0)
            flags |= ImGuiTableFlags.NoSavedSettings;
        return ImGui.BeginTable(id, columns, flags, size);
    }

    public static void End() => ImGui.EndTable();

    /// <summary>The one column that should absorb whatever width the fixed columns don't need
    /// (a name or description). <paramref name="weight"/> splits that space between several
    /// stretch columns in the same table.</summary>
    public static void Stretch(string name, float weight = 1, ImGuiTableColumnFlags extra = 0)
        => ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthStretch | extra, weight);

    /// <summary><c>WidthFixed | NoResize</c> with no width, so ImGui fits the column to its header
    /// and contents every frame. ImGui refits a fixed column every frame only when it can't be
    /// resized; a resizable one keeps the width measured in its first frames (imgui_tables.cpp
    /// <c>TableUpdateLayout</c>, "Latch initial size for fixed columns"), so a table first drawn
    /// with short or no rows would stay too narrow once its real rows arrive. A widget that fills
    /// its column (<c>SetNextItemWidth(-1)</c>) has no width to fit to: use <see cref="Widget"/>.</summary>
    public static void Fit(string name, ImGuiTableColumnFlags extra = 0)
        => ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize | extra);

    /// <summary>A fixed column <paramref name="width"/> pixels wide for input widgets (a combo,
    /// an InputInt) that fill it with <c>SetNextItemWidth(-1)</c>. A <see cref="Fit"/> column
    /// sizes to its contents, and a fill widget reports no width of its own, so it collapses
    /// there; measure the widest thing the widget shows and pass that.</summary>
    public static void Widget(string name, float width, ImGuiTableColumnFlags extra = 0)
        => ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize | extra, width);

    /// <summary>A column exactly <paramref name="size"/> wide, scaled by
    /// <see cref="ImGuiHelpers.GlobalScale"/>, for the icon it holds rather than its text.</summary>
    public static void Icon(string id, float size, ImGuiTableColumnFlags extra = 0)
        => ImGui.TableSetupColumn(id, ImGuiTableColumnFlags.WidthFixed | extra, size * ImGuiHelpers.GlobalScale);

    public static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(text);
    }

    public static void Cell(Vector4 color, string text)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, text);
    }

    /// <summary>A cell right-aligned against the column's edge, for a quantity or a gil amount next
    /// to a label column: a column of numbers reads by its ones digit lining up, not its first
    /// digit.</summary>
    public static void Number(string text)
    {
        ImGui.TableNextColumn();
        var width = ImGui.GetContentRegionAvail().X;
        var textWidth = ImGui.CalcTextSize(text).X;
        if (textWidth < width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - textWidth);
        ImGui.AlignTextToFramePadding();
        ImGui.Text(text);
    }
}
