using System;
using System.Collections.Generic;
using ECommons.Reflection;

namespace ZhyraPluginKit;

/// <summary>Cached "is this plugin loaded" lookup.
/// <para><c>DalamudReflector.TryGetDalamudPlugin</c> with <c>ignoreCache</c> walks Dalamud's whole
/// installed-plugin list through reflection, reading two properties per plugin, on every call. The
/// scheduler checks presence every frame while travelling and the UI checks it per duty group per
/// draw, so the uncached call is far too expensive to make directly. A short TTL still notices a
/// plugin the user loads or unloads mid-session.</para></summary>
public static class PluginPresence
{
    private const long TtlMs = 2000;

    private static readonly Dictionary<string, (long Tick, bool Present)> cache = new();

    public static bool IsInstalled(string internalName)
    {
        var now = Environment.TickCount64;
        if (cache.TryGetValue(internalName, out var entry) && now - entry.Tick < TtlMs)
            return entry.Present;

        // Errors are suppressed: an absent optional plugin is a state we report ourselves, not a fault.
        var present = DalamudReflector.TryGetDalamudPlugin(internalName, out _, true, true);
        cache[internalName] = (now, present);
        return present;
    }
}
