using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace XivHubPluginKit.UI;

/// <summary>
/// The XIV Hub named palette: every colour the theme uses, once, by name.
///
/// Values are lifted from xivhub.net so a plugin window and the site read as one
/// product. Nothing else in the kit or in a plugin should carry a hex literal —
/// a colour worth naming goes here, and a colour not worth naming is a sign the
/// design wants one of these instead.
///
/// Semantic colours mean one thing each and are not interchangeable with the
/// accent: gold marks what the user is acting on, green a realised gain, amber a
/// reversible caution, red a failure that already happened.
/// </summary>
public static class HubColors
{
    private static readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // Accent — the crystal in the logo.
        { "HubGold",       "#d9b370" },
        { "HubGoldBright", "#f4d79a" },
        { "HubGoldDim",    "#a9833f" },

        // Secondary. Informational and inert; never a call to action.
        { "HubCrystal",    "#86b8ec" },

        // Text ramp.
        { "HubText",       "#eef1f8" },
        { "HubMuted",      "#9aa6bd" },
        { "HubFaint",      "#6a7488" },

        // Semantic.
        { "HubGood",       "#3ecf8e" },
        { "HubWarn",       "#ff9147" },
        { "HubBad",        "#e0524a" },

        // Surfaces, darkest to lightest.
        { "HubGround",     "#080a11" },
        { "HubTitleBg",    "#0a0d14" },
        { "HubWindowBg",   "#0d1017" },
        { "HubChildBg",    "#11151f" },
        { "HubTitleActive","#131824" },
        { "HubTableHead",  "#141926" },
        { "HubFrameBg",    "#171b26" },
        { "HubSurface",    "#1a1f2b" },
        { "HubFrameHover", "#1d2230" },
        { "HubScrollGrab", "#232a38" },
        { "HubFrameActive","#232939" },
        { "HubHovered",    "#262f41" },
        { "HubTabActive",  "#2b3446" },
        { "HubScrollHover","#2e3648" },
        { "HubActive",     "#313c52" },
        { "HubPrimaryPressed", "#443729" },
    };

    /// <summary>User overrides, by the same names. Empty until something is set.</summary>
    private static Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Vector4> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every colour name the palette defines, for a settings editor.</summary>
    public static IEnumerable<string> Names => _defaults.Keys;

    public static string DefaultHex(string name)
        => _defaults.TryGetValue(name, out var hex) ? hex : "#ff00ff";

    /// <summary>
    /// Replace the override set and drop the parsed cache. Called by the theme
    /// config when it loads or the user edits a colour.
    /// </summary>
    public static void SetOverrides(Dictionary<string, string>? overrides)
    {
        _overrides = overrides is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        _cache.Clear();
    }

    /// <summary>
    /// The colour for <paramref name="name"/>, override first. An unknown name is
    /// magenta rather than an exception: a theme that throws mid-draw takes the
    /// whole window with it, and magenta is impossible to miss in review.
    /// </summary>
    public static Vector4 Get(string name)
    {
        if (_cache.TryGetValue(name, out var cached))
            return cached;

        string hex = _overrides.TryGetValue(name, out var over) ? over : DefaultHex(name);
        var value = TryParseHex(hex, out var parsed) ? parsed : new Vector4(1f, 0f, 1f, 1f);
        _cache[name] = value;
        return value;
    }

    /// <summary>The same colour at a different alpha, for hairlines and fills.</summary>
    public static Vector4 Get(string name, float alpha)
    {
        var c = Get(name);
        return new Vector4(c.X, c.Y, c.Z, alpha);
    }

    /// <summary>
    /// <c>#rgb</c>, <c>#rrggbb</c> and <c>#rrggbbaa</c>, with or without the hash.
    /// Alpha defaults to opaque.
    /// </summary>
    public static bool TryParseHex(string? hex, out Vector4 rgba)
    {
        rgba = default;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var s = hex.Trim();
        if (s.StartsWith('#'))
            s = s[1..];

        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 && s.Length != 8)
            return false;

        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
            return false;

        if (s.Length == 6)
            packed = (packed << 8) | 0xFF;

        rgba = new Vector4(
            ((packed >> 24) & 0xFF) / 255f,
            ((packed >> 16) & 0xFF) / 255f,
            ((packed >> 8) & 0xFF) / 255f,
            (packed & 0xFF) / 255f);
        return true;
    }

    public static string ToHex(Vector4 rgba)
    {
        static int B(float f) => Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
        return $"#{B(rgba.X):x2}{B(rgba.Y):x2}{B(rgba.Z):x2}{B(rgba.W):x2}";
    }
}
