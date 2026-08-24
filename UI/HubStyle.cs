using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XivHubPluginKit.UI;

/// <summary>
/// The XIV Hub ImGui theme.
///
/// One declarative table per kind of style value is the whole design: it is what
/// <see cref="Push"/> walks to dress a frame, and what a settings editor walks to
/// draw itself. Adding a themed value is one entry — never a push in one place
/// and a widget in another, which is how the two drift apart.
///
/// The rule the palette encodes: gold marks what the user is acting on and
/// nothing else. Interactive surfaces stay on the dark ramp
/// (<c>HubSurface</c> → <c>HubHovered</c> → <c>HubActive</c>) and borrow gold only
/// for the indicator inside them — the check mark, the slider grab, the active
/// tab, a separator being dragged. Extending the theme means keeping that true;
/// see <c>UI/THEME.md</c>.
/// </summary>
public static class HubStyle
{
    public readonly record struct ColorOption(
        string Key, string Label, string ColorName, ImGuiCol Target, float Alpha = 1f, string? Description = null);

    public readonly record struct FloatOption(
        string Key, string Label, float Default, ImGuiStyleVar Target,
        float Min = 0f, float Max = 30f, string? Description = null);

    public readonly record struct Vector2Option(
        string Key, string Label, float DefaultX, float DefaultY, ImGuiStyleVar Target,
        string? Description = null);

    private static HubThemeConfigService? _config;

    /// <summary>Wire the theme to its config. Call once at plugin start.</summary>
    public static void Init(HubThemeConfigService config) => _config = config;

    public static bool Enabled => _config?.Current.Enabled ?? false;

    private static bool _pushed;
    private static int _colorsPushed;
    private static int _varsPushed;

    // --- the table -----------------------------------------------------------

    private static readonly ColorOption[] _colors =
    [
        new("color.text",            "Text",                     "HubText",        ImGuiCol.Text),
        new("color.textDisabled",    "Text (Disabled)",          "HubFaint",       ImGuiCol.TextDisabled),
        new("color.windowBg",        "Window Background",        "HubWindowBg",    ImGuiCol.WindowBg, 0.97f),
        new("color.childBg",         "Child Background",         "HubChildBg",     ImGuiCol.ChildBg),
        new("color.popupBg",         "Popup Background",         "HubWindowBg",    ImGuiCol.PopupBg, 0.97f),
        new("color.border",          "Border",                   "HubText",        ImGuiCol.Border, 0.08f,
            "Every hairline in the theme. 8% white is the site's glass edge."),
        new("color.borderShadow",    "Border Shadow",            "HubGround",      ImGuiCol.BorderShadow, 0.0f),

        new("color.frameBg",         "Frame Background",         "HubFrameBg",     ImGuiCol.FrameBg),
        new("color.frameBgHovered",  "Frame Background (Hover)", "HubFrameHover",  ImGuiCol.FrameBgHovered),
        new("color.frameBgActive",   "Frame Background (Active)","HubFrameActive", ImGuiCol.FrameBgActive),

        new("color.titleBg",         "Title Background",         "HubTitleBg",     ImGuiCol.TitleBg),
        new("color.titleBgActive",   "Title Background (Active)","HubTitleActive", ImGuiCol.TitleBgActive),
        new("color.titleBgCollapsed","Title Background (Collapsed)","HubTitleBg",  ImGuiCol.TitleBgCollapsed, 0.85f),
        new("color.menuBarBg",       "Menu Bar Background",      "HubChildBg",     ImGuiCol.MenuBarBg),

        new("color.scrollbarBg",     "Scrollbar Background",     "HubGround",      ImGuiCol.ScrollbarBg, 0.35f),
        new("color.scrollbarGrab",   "Scrollbar Grab",           "HubScrollGrab",  ImGuiCol.ScrollbarGrab),
        new("color.scrollbarGrabHovered","Scrollbar Grab (Hover)","HubScrollHover",ImGuiCol.ScrollbarGrabHovered),
        new("color.scrollbarGrabActive","Scrollbar Grab (Active)","HubGoldDim",    ImGuiCol.ScrollbarGrabActive,
            Description: "Gold only while it is actually being dragged."),

        new("color.checkMark",       "Check Mark",               "HubGold",        ImGuiCol.CheckMark),
        new("color.sliderGrab",      "Slider Grab",              "HubGoldDim",     ImGuiCol.SliderGrab),
        new("color.sliderGrabActive","Slider Grab (Active)",     "HubGold",        ImGuiCol.SliderGrabActive),

        new("color.button",          "Button",                   "HubSurface",     ImGuiCol.Button),
        new("color.buttonHovered",   "Button (Hover)",           "HubHovered",     ImGuiCol.ButtonHovered),
        new("color.buttonActive",    "Button (Active)",          "HubActive",      ImGuiCol.ButtonActive),

        new("color.header",          "Header",                   "HubSurface",     ImGuiCol.Header),
        new("color.headerHovered",   "Header (Hover)",           "HubHovered",     ImGuiCol.HeaderHovered),
        new("color.headerActive",    "Header (Active)",          "HubActive",      ImGuiCol.HeaderActive),

        new("color.separator",       "Separator",                "HubText",        ImGuiCol.Separator, 0.06f),
        new("color.separatorHovered","Separator (Hover)",        "HubGoldDim",     ImGuiCol.SeparatorHovered),
        new("color.separatorActive", "Separator (Active)",       "HubGold",        ImGuiCol.SeparatorActive),

        new("color.resizeGrip",      "Resize Grip",              "HubGround",      ImGuiCol.ResizeGrip, 0f),
        new("color.resizeGripHovered","Resize Grip (Hover)",     "HubGoldDim",     ImGuiCol.ResizeGripHovered, 0.5f),
        new("color.resizeGripActive","Resize Grip (Active)",     "HubGold",        ImGuiCol.ResizeGripActive),

        new("color.tab",             "Tab",                      "HubTableHead",   ImGuiCol.Tab),
        new("color.tabHovered",      "Tab (Hover)",              "HubHovered",     ImGuiCol.TabHovered),
        new("color.tabActive",       "Tab (Active)",             "HubTabActive",   ImGuiCol.TabActive),
        new("color.tabUnfocused",    "Tab (Unfocused)",          "HubTableHead",   ImGuiCol.TabUnfocused),
        new("color.tabUnfocusedActive","Tab (Unfocused Active)", "HubSurface",     ImGuiCol.TabUnfocusedActive),

        new("color.tableHeaderBg",   "Table Header Background",  "HubTableHead",   ImGuiCol.TableHeaderBg),
        new("color.tableBorderStrong","Table Border (Strong)",   "HubText",        ImGuiCol.TableBorderStrong, 0.10f),
        new("color.tableBorderLight","Table Border (Light)",     "HubText",        ImGuiCol.TableBorderLight, 0.04f),
        new("color.tableRowBg",      "Table Row Background",     "HubGround",      ImGuiCol.TableRowBg, 0f),
        new("color.tableRowBgAlt",   "Table Row Background (Alt)","HubText",       ImGuiCol.TableRowBgAlt, 0.022f,
            "Zebra striping. Any stronger and gold in the cells stops reading."),

        new("color.textSelectedBg",  "Text Selection",           "HubGold",        ImGuiCol.TextSelectedBg, 0.35f),
        new("color.dragDropTarget",  "Drag & Drop Target",       "HubGoldBright",  ImGuiCol.DragDropTarget),
        new("color.navHighlight",    "Navigation Highlight",     "HubGold",        ImGuiCol.NavHighlight, 0.70f),
        new("color.navWindowingDimBg","Navigation Window Dim",   "HubGround",      ImGuiCol.NavWindowingDimBg, 0.60f),
        new("color.navWindowingHighlight","Navigation Window Highlight","HubGold", ImGuiCol.NavWindowingHighlight, 0.35f),

        new("color.plotLines",       "Plot Lines",               "HubMuted",       ImGuiCol.PlotLines),
        new("color.plotHistogram",   "Plot Histogram",           "HubGoldDim",     ImGuiCol.PlotHistogram,
            Description: "Also the fill of every ImGui.ProgressBar."),

        new("color.modalWindowDimBg","Modal Dim",                "HubGround",      ImGuiCol.ModalWindowDimBg, 0.60f,
            "Behind a BeginPopupModal. The ground colour, so a modal reads as the site dimming."),
        new("color.dockingPreview",  "Docking Preview",          "HubGold",        ImGuiCol.DockingPreview, 0.45f),
        new("color.dockingEmptyBg",  "Docking Empty Background", "HubChildBg",     ImGuiCol.DockingEmptyBg),
    ];

    private static readonly Vector2Option[] _vectors =
    [
        new("vector.windowPadding",   "Window Padding",     9f, 9f, ImGuiStyleVar.WindowPadding),
        new("vector.framePadding",    "Frame Padding",      8f, 4f, ImGuiStyleVar.FramePadding),
        new("vector.cellPadding",     "Cell Padding",       8f, 5f, ImGuiStyleVar.CellPadding),
        new("vector.itemSpacing",     "Item Spacing",       5f, 5f, ImGuiStyleVar.ItemSpacing),
        new("vector.itemInnerSpacing","Item Inner Spacing", 6f, 4f, ImGuiStyleVar.ItemInnerSpacing),
    ];

    private static readonly FloatOption[] _floats =
    [
        new("float.windowRounding",   "Window Rounding",    7f,   ImGuiStyleVar.WindowRounding,    0f, 20f),
        new("float.childRounding",    "Child Rounding",     4f,   ImGuiStyleVar.ChildRounding,     0f, 20f),
        new("float.frameRounding",    "Frame Rounding",     4f,   ImGuiStyleVar.FrameRounding,     0f, 20f),
        new("float.popupRounding",    "Popup Rounding",     4f,   ImGuiStyleVar.PopupRounding,     0f, 20f),
        new("float.scrollbarRounding","Scrollbar Rounding", 5f,   ImGuiStyleVar.ScrollbarRounding, 0f, 20f),
        new("float.grabRounding",     "Grab Rounding",      3f,   ImGuiStyleVar.GrabRounding,      0f, 20f),
        new("float.tabRounding",      "Tab Rounding",       4f,   ImGuiStyleVar.TabRounding,       0f, 20f),
        new("float.windowBorderSize", "Window Border Size", 1.5f, ImGuiStyleVar.WindowBorderSize,  0f, 5f),
        new("float.childBorderSize",  "Child Border Size",  1f,   ImGuiStyleVar.ChildBorderSize,   0f, 5f),
        new("float.popupBorderSize",  "Popup Border Size",  1.5f, ImGuiStyleVar.PopupBorderSize,   0f, 5f),
        new("float.frameBorderSize",  "Frame Border Size",  0f,   ImGuiStyleVar.FrameBorderSize,   0f, 5f),
        new("float.indentSpacing",    "Indent Spacing",     18f,  ImGuiStyleVar.IndentSpacing,     0f, 100f),
        new("float.scrollbarSize",    "Scrollbar Size",     10f,  ImGuiStyleVar.ScrollbarSize,     4f, 30f),
        new("float.grabMinSize",      "Grab Minimum Size",  16f,  ImGuiStyleVar.GrabMinSize,       1f, 80f),
    ];

    public static IReadOnlyList<ColorOption> Colors => _colors;
    public static IReadOnlyList<FloatOption> Floats => _floats;
    public static IReadOnlyList<Vector2Option> Vectors => _vectors;

    // --- resolution ----------------------------------------------------------

    /// <summary>
    /// What an option is worth right now: the user's override if there is one,
    /// otherwise the palette colour the table names, at the table's alpha.
    /// </summary>
    public static Vector4 Resolve(ColorOption o)
        => _config is not null && _config.TryGetColor(o.Key, out var over)
            ? over
            : HubColors.Get(o.ColorName, o.Alpha);

    public static float Resolve(FloatOption o)
    {
        float v = _config is not null && _config.TryGetFloat(o.Key, out var over) ? over : o.Default;
        return Math.Clamp(v, o.Min, o.Max);
    }

    public static Vector2 Resolve(Vector2Option o)
        => _config is not null && _config.TryGetVector2(o.Key, out var over)
            ? over
            : new Vector2(o.DefaultX, o.DefaultY);

    // --- push / pop ----------------------------------------------------------

    /// <summary>
    /// Dress the frame. Wrap the plugin's whole <c>WindowSystem.Draw()</c> in
    /// this and <see cref="Pop"/> — one place, so every window is themed without
    /// a single window class knowing the theme exists.
    ///
    /// Pushes are counted rather than assumed: ImGui's stack is global, and a pop
    /// count that disagrees with the push count corrupts every plugin drawing
    /// after this one, not just this one.
    /// </summary>
    public static void Push()
    {
        if (_pushed)
            Pop();

        if (!Enabled)
            return;

        _pushed = true;
        _colorsPushed = 0;
        _varsPushed = 0;

        foreach (var o in _colors)
        {
            ImGui.PushStyleColor(o.Target, Resolve(o));
            _colorsPushed++;
        }
        foreach (var o in _vectors)
        {
            ImGui.PushStyleVar(o.Target, Resolve(o));
            _varsPushed++;
        }
        foreach (var o in _floats)
        {
            ImGui.PushStyleVar(o.Target, Resolve(o));
            _varsPushed++;
        }
    }

    public static void Pop()
    {
        if (!_pushed)
            return;
        if (_varsPushed > 0)
            ImGui.PopStyleVar(_varsPushed);
        if (_colorsPushed > 0)
            ImGui.PopStyleColor(_colorsPushed);
        _pushed = false;
        _varsPushed = 0;
        _colorsPushed = 0;
    }

    // --- semantic helpers ----------------------------------------------------

    /// <summary>
    /// The one gold fill in the system, for the single irreversible action in a
    /// window ("Confirm and run", "Apply 6 moves"). Wrap the button call:
    /// <c>using (HubStyle.Primary()) if (ImGui.Button("Confirm")) …</c>.
    /// A window with two of these has a hierarchy problem, not a theme problem.
    /// </summary>
    /// <para>
    /// A no-op when the theme is switched off: "off" has to mean every window is
    /// untouched, and a scope that dressed one button anyway would leave a single
    /// themed control in an otherwise default window.
    /// </para>
    public static IDisposable Primary() => Enabled ? new PrimaryScope() : NullScope.Instance;

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }

    private sealed class PrimaryScope : IDisposable
    {
        public PrimaryScope()
        {
            ImGui.PushStyleColor(ImGuiCol.Button, HubColors.Get("HubSurface"));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, HubColors.Get("HubHovered"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, HubColors.Get("HubPrimaryPressed"));
            ImGui.PushStyleColor(ImGuiCol.Text, HubColors.Get("HubGold"));
            ImGui.PushStyleColor(ImGuiCol.Border, HubColors.Get("HubGold", 0.40f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        }

        public void Dispose()
        {
            ImGui.PopStyleVar(1);
            ImGui.PopStyleColor(5);
        }
    }

    /// <summary>
    /// The surface ramp, for the places a call site genuinely has to paint one
    /// itself: a draw-list fill, a per-widget selected state, a recessed cell.
    /// Named members rather than <see cref="HubColors.Get(string)"/> at the call
    /// site — a mistyped colour name is magenta at runtime, where this is a
    /// compile error.
    /// </summary>
    public static Vector4 Ground => HubColors.Get("HubGround");
    public static Vector4 WindowBg => HubColors.Get("HubWindowBg");
    public static Vector4 ChildBg => HubColors.Get("HubChildBg");
    public static Vector4 FrameBg => HubColors.Get("HubFrameBg");
    public static Vector4 Surface => HubColors.Get("HubSurface");
    public static Vector4 Hovered => HubColors.Get("HubHovered");
    public static Vector4 Selected => HubColors.Get("HubActive");

    public static Vector4 Text => HubColors.Get("HubText");
    public static Vector4 Muted => HubColors.Get("HubMuted");
    public static Vector4 Faint => HubColors.Get("HubFaint");
    public static Vector4 Accent => HubColors.Get("HubGold");
    public static Vector4 Info => HubColors.Get("HubCrystal");
    public static Vector4 Good => HubColors.Get("HubGood");
    public static Vector4 Warn => HubColors.Get("HubWarn");
    public static Vector4 Bad => HubColors.Get("HubBad");
}
