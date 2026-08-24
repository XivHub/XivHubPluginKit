using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivHubPluginKit.UI;

/// <summary>One user override. Exactly one field is set; the rest are null.</summary>
public sealed class HubStyleOverride
{
    /// <summary>Packed RGBA, as <see cref="HubColors.ToHex"/> writes it.</summary>
    public string? Color { get; set; }
    public float? Float { get; set; }
    public float[]? Vector2 { get; set; }
}

/// <summary>
/// The persisted theme: a master switch, palette overrides by colour name, and
/// per-option style overrides keyed by the option table's keys.
/// </summary>
public sealed class HubThemeConfig
{
    public int Version { get; set; } = 1;

    /// <summary>Off leaves every window in the user's own Dalamud style.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Palette overrides, by <see cref="HubColors"/> name.</summary>
    public Dictionary<string, string> Palette { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Style overrides, by option key (<c>color.button</c>, <c>float.frameRounding</c>).</summary>
    public Dictionary<string, HubStyleOverride> StyleOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Loads and saves <see cref="HubThemeConfig"/>.
///
/// The file lives beside the plugin config directories rather than inside one:
/// the theme belongs to the XIV Hub family, not to whichever plugin happened to
/// write it, so setting a colour in one plugin is meant to reach the others. The
/// path is resolved from any plugin's own config directory the same way sibling
/// plugin data is found elsewhere in the kit.
/// </summary>
public sealed class HubThemeConfigService
{
    public const string DirName = "XivHub";
    public const string FileName = "ui-theme.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Action<string, Exception?>? _warn;

    public HubThemeConfig Current { get; private set; } = new();

    /// <param name="pluginConfigDirectory">
    /// <c>IDalamudPluginInterface.GetPluginConfigDirectory()</c> of the calling
    /// plugin; only its parent is used.
    /// </param>
    /// <param name="warn">Optional log sink; the theme never throws at the caller.</param>
    public HubThemeConfigService(string pluginConfigDirectory, Action<string, Exception?>? warn = null)
    {
        _warn = warn;
        string? parent = Path.GetDirectoryName(pluginConfigDirectory);
        _path = parent is null
            ? string.Empty
            : Path.Combine(parent, DirName, FileName);
        Load();
    }

    public string Path_ => _path;

    public void Load()
    {
        Current = ReadOrDefault();
        HubColors.SetOverrides(Current.Palette);
    }

    private HubThemeConfig ReadOrDefault()
    {
        if (_path.Length == 0 || !File.Exists(_path))
            return new HubThemeConfig();
        try
        {
            var text = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<HubThemeConfig>(text, _json) ?? new HubThemeConfig();
        }
        catch (Exception ex)
        {
            // A malformed theme must not stop a plugin loading; fall back to the
            // defaults and leave the file alone so the user can repair it.
            _warn?.Invoke($"Could not read {_path}; using theme defaults", ex);
            return new HubThemeConfig();
        }
    }

    public void Save()
    {
        HubColors.SetOverrides(Current.Palette);
        if (_path.Length == 0)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, _json));
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"Could not write {_path}", ex);
        }
    }

    // --- override accessors, used by HubStyle and the settings editor ---------

    public bool TryGetColor(string key, out Vector4 value)
    {
        value = default;
        return Current.StyleOverrides.TryGetValue(key, out var o)
               && o.Color is not null
               && HubColors.TryParseHex(o.Color, out value);
    }

    public bool TryGetFloat(string key, out float value)
    {
        value = default;
        if (!Current.StyleOverrides.TryGetValue(key, out var o) || o.Float is null)
            return false;
        value = o.Float.Value;
        return true;
    }

    public bool TryGetVector2(string key, out Vector2 value)
    {
        value = default;
        if (!Current.StyleOverrides.TryGetValue(key, out var o) || o.Vector2 is not { Length: 2 } v)
            return false;
        value = new Vector2(v[0], v[1]);
        return true;
    }

    public void SetColor(string key, Vector4 value)
        => Set(key, new HubStyleOverride { Color = HubColors.ToHex(value) });

    public void SetFloat(string key, float value)
        => Set(key, new HubStyleOverride { Float = value });

    public void SetVector2(string key, Vector2 value)
        => Set(key, new HubStyleOverride { Vector2 = [value.X, value.Y] });

    private void Set(string key, HubStyleOverride value)
    {
        Current.StyleOverrides[key] = value;
        Save();
    }

    /// <summary>Drop one override so the option falls back to the table default.</summary>
    public void Clear(string key)
    {
        if (Current.StyleOverrides.Remove(key))
            Save();
    }

    public void ClearAll()
    {
        Current.StyleOverrides.Clear();
        Current.Palette.Clear();
        Save();
    }
}
