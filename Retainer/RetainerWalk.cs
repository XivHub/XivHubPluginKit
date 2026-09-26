using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Utility;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XivHubPluginKit.Retainer;

/// <summary>
/// The summoning-bell UI walk shared by every session that drives a retainer:
/// <c>RetainerList</c> → retainer row → "Sell Items" → <c>RetainerSellList</c>,
/// and the teardown back out of it.
///
/// Each step takes the caller's throttle rather than owning one, so a session
/// keeps its own pacing (Auto Pinch's is user-configurable, another session's is
/// fixed). Steps return the AR-mirror tri-state: <c>false</c> = run me again next
/// tick, <c>true</c> = done.
/// </summary>
public static class RetainerWalk
{
    /// <summary>One retainer as the roster knows it, without opening anything.</summary>
    public readonly record struct RosterEntry(ulong Cid, string Name, int SortedIndex, int MarketCount);

    /// <summary>
    /// What the game calls a summoning bell, localised. Row 2000401 of
    /// <c>EObjName</c>, plus the Japanese literal, which is how AutoRetainer
    /// matches it too.
    /// </summary>
    public static string[] BellNames =>
    [
        Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.EObjName>()
            .GetRow(2000401).Singular.ToDalamudString().GetText(),
        "\u30ea\u30c6\u30a4\u30ca\u30fc\u30d9\u30eb",
    ];

    /// <summary>
    /// The nearest summoning bell within <paramref name="range"/> yalms, or 0.
    /// A bell is an event object rather than an NPC, and the housing ones use a
    /// different object kind from the city ones, so both are accepted.
    /// Framework thread.
    /// </summary>
    public static ulong FindBell(float range)
    {
        var me = Player.Object;
        if (me is null) return 0;
        var names = BellNames;
        var match = Svc.Objects
            .Where(o => o.IsTargetable
                        && (o.ObjectKind == ObjectKind.EventObj
                            || o.ObjectKind == ObjectKind.HousingEventObject)
                        && names.Any(n => string.Equals(o.Name.ToString(), n,
                                                        StringComparison.OrdinalIgnoreCase)))
            .Select(o => (Obj: o, Distance: Vector3.Distance(o.Position, me.Position)))
            .Where(x => x.Distance <= range)
            .OrderBy(x => x.Distance)
            .Select(x => x.Obj)
            .FirstOrDefault();
        return match?.GameObjectId ?? 0;
    }

    /// <summary>How far from a bell the game will still let you ring it.</summary>
    public const float BellRangeYalms = 6f;

    /// <summary>Whether a summoning bell is already in reach, so nothing has to
    /// walk to one. Framework thread.</summary>
    public static bool IsAtBell() => FindBell(BellRangeYalms) != 0;

    public static unsafe bool IsRetainerListReady()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon)
           && GenericHelpers.IsAddonReady(addon);

    /// <summary>
    /// The retainer roster by sorted index. <c>MarketItemCount</c> is readable
    /// without opening the retainer, so free market slots cost no UI round-trip.
    /// Framework thread.
    /// </summary>
    public static unsafe List<RosterEntry> ReadRoster()
    {
        var list = new List<RosterEntry>();
        var mgr = RetainerManager.Instance();
        if (mgr == null)
            return list;
        int count = (int)mgr->GetRetainerCount();
        for (int i = 0; i < count; i++)
        {
            var entry = mgr->GetRetainerBySortedIndex((uint)i);
            if (entry == null) continue;
            list.Add(new RosterEntry(entry->RetainerId, entry->NameString, i, entry->MarketItemCount));
        }
        return list;
    }

    public static unsafe bool? OpenRetainerRow(int sortedIdx, Func<bool> throttle, Action rethrottle)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            rethrottle();
            return false;
        }
        var rl = new AddonMaster.RetainerList(addon);
        if (sortedIdx < 0 || sortedIdx >= rl.Retainers.Length) return true;
        if (!throttle()) return false;
        rl.Retainers[sortedIdx].Select();
        return true;
    }

    public static unsafe bool? ClickSellItems(Func<bool> throttle, Action rethrottle)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            rethrottle();
            return false;
        }
        var ss = new AddonMaster.SelectString(addon);
        if (ss.Entries.Length <= 2) return false;
        if (!throttle()) return false;
        ss.Entries[2].Select();
        return true;
    }

    private static string? _entrustOrWithdrawText;

    // Addon sheet row 2378 is the retainer menu's "Entrust or withdraw items."
    // in the client's language.
    private static string EntrustOrWithdrawText
        => _entrustOrWithdrawText ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
            .GetRow(2378).Text.GetText(true);

    /// <summary>
    /// Click "Entrust or withdraw items" on the retainer <c>SelectString</c>,
    /// which opens the retainer's inventory window and loads its
    /// <c>RetainerPage*</c> containers. Found by the Addon sheet text rather
    /// than by position the way <see cref="ClickSellItems"/> picks
    /// <c>Entries[2]</c>: this entry's index is not confirmed stable across
    /// retainer types and patches. Returns false while no entry matches, so
    /// the caller's step budget decides when that is a failure;
    /// <see cref="DescribeSelectString"/> names what the menu held.
    /// </summary>
    // SAFETY: TryGetAddonByName yields the game's live addon pointer for this
    // frame or fails; AddonMaster reads its entries and Select fires the
    // addon's own callback, all within this call.
    public static unsafe bool? ClickEntrustOrWithdraw(Func<bool> throttle, Action rethrottle)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            rethrottle();
            return false;
        }
        var target = EntrustOrWithdrawText;
        var ss = new AddonMaster.SelectString(addon);
        foreach (var entry in ss.Entries)
        {
            if (!MenuEntryMatches(entry.Text, target)) continue;
            if (!throttle()) return false;
            entry.Select();
            return true;
        }
        return false;
    }

    // Looser than CloseSelectStringBack's exact match: the sheet text and the
    // drawn entry can differ by trailing punctuation, an auto-translate wrapper
    // or stray whitespace, and an exact match would then silently find nothing.
    private static bool MenuEntryMatches(string entryText, string target)
    {
        entryText = entryText.Trim();
        target = target.Trim();
        if (target.Length == 0) return false;
        return entryText.StartsWith(target, StringComparison.OrdinalIgnoreCase)
               || entryText.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The open <c>SelectString</c>'s entries, each quoted, for a failure
    /// message: <c>SelectString entries=['a' | 'b']</c>, or
    /// <c>SelectString=(not open)</c>. Framework thread.
    /// </summary>
    // SAFETY: TryGetAddonByName yields the game's live addon pointer for this
    // frame or fails; AddonMaster only reads its entries within this call.
    public static unsafe string DescribeSelectString()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return "SelectString=(not open)";
        var ss = new AddonMaster.SelectString(addon);
        return $"SelectString entries=[{string.Join(" | ", ss.Entries.Select(e => $"'{e.Text}'"))}]";
    }

    // Close-and-verify-gone pattern (mirror of AutoRetainer's
    // RetainerHandlers.SelectQuit): while the addon is still visible, throttle
    // a Close call and return false so the next tick verifies; only an addon
    // that has actually disappeared returns true. This is what keeps a
    // pipeline from racing: the following step always sees the post-close UI
    // state.
    private static unsafe bool? CloseAddon(string name, Func<bool> throttle)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) || !addon->IsVisible)
            return true;
        if (!throttle()) return false;
        addon->Close(true);
        return false;
    }

    public static bool? CloseRetainerSell(Func<bool> throttle) => CloseAddon("RetainerSell", throttle);
    public static bool? CloseContextMenu(Func<bool> throttle) => CloseAddon("ContextMenu", throttle);
    public static bool? CloseRetainerSellList(Func<bool> throttle) => CloseAddon("RetainerSellList", throttle);
    public static bool? CloseItemSearchResult(Func<bool> throttle) => CloseAddon("ItemSearchResult", throttle);

    /// <summary>
    /// Close the retainer's inventory window, whichever of its two layouts is
    /// open; true once neither is visible. <c>Close(true)</c> is this window's
    /// exit: it returns to the retainer's <c>SelectString</c>, which
    /// RetainerReach's multi-retainer runs rely on. The event menus covered by
    /// the rule against <c>Close(true)</c> are different: hiding one leaves its
    /// event running. Quit the <c>SelectString</c> afterwards with
    /// <see cref="CloseSelectStringBack"/>.
    /// </summary>
    public static bool? CloseRetainerInventory(Func<bool> throttle)
    {
        if (CloseAddon("InventoryRetainerLarge", throttle) != true) return false;
        return CloseAddon("InventoryRetainer", throttle);
    }

    /// <summary>
    /// Click the localised "Quit" entry on the retainer <c>SelectString</c>,
    /// looked up via Addon sheet row 2383 so it works in every client locale.
    /// Closing the widget with <c>Close(true)</c> would hide it without the game
    /// returning control to <c>RetainerList</c>; only Quit fires the exit path.
    /// </summary>
    public static unsafe bool? CloseSelectStringBack(Func<bool> throttle, Action rethrottle)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            rethrottle();
            return false;
        }
        var quitText = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2383).Text.ToDalamudString().GetText();
        var ss = new AddonMaster.SelectString(addon);
        int quitIdx = -1;
        for (int i = 0; i < ss.Entries.Length; i++)
        {
            if (ss.Entries[i].Text == quitText) { quitIdx = i; break; }
        }
        if (quitIdx < 0) return false;
        if (!throttle()) return false;
        ss.Entries[quitIdx].Select();
        return true;
    }

    // --- item names read off retainer windows ------------------------------

    private const char HqGlyph = '\uE03C';
    // FFXIV UI glyphs live in the Unicode private-use area (U+E000–U+F8FF).
    private const char PuaStart = '\uE000';
    private const char PuaEnd = '\uF8FF';

    public static bool HasHqGlyph(string s) => s.IndexOf(HqGlyph) >= 0;

    /// <summary>
    /// Strip every private-use glyph (the HQ marker and friends, none of which
    /// appear in the Item sheet) and trim, so a window's item name can be matched
    /// against a sheet name.
    /// </summary>
    public static string NormalizeItemName(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (char c in s)
        {
            if (c < PuaStart || c > PuaEnd)
                buf[n++] = c;
        }
        return new string(buf[..n]).Trim();
    }

    // RetainerSellList publishes its whole table in AtkValues: [9] is the row
    // count, then thirteen values per row from [10], of which +1 is the item
    // name, +2 the quantity and +3 the price. Reading it costs nothing and
    // needs no clicking, where asking each row's price window what it holds
    // costs three UI round trips a row.
    //
    // Measured on a 16-row list: [10] 51009, [11] "Country Flooring", [12] 1,
    // [13] "79,897"; [23] 51114, [24] "Oasis Pendant Lamp"; [36] 52475,
    // [37] "Mahogany Screen". The leading int is not an item id, it does not
    // resolve to any of these items, so the name is what identifies a row.
    public const int SellListRowCountIndex = 9;
    private const int SellListFirstRow = 10;
    private const int SellListRowStride = 13;

    /// <summary>
    /// The retainer whose sell list is open, or null when none is. A pull runs
    /// against the rows that list is showing, so this is the only retainer it
    /// can reach.
    /// </summary>
    public static unsafe (ulong Cid, string Name)? OpenSellListRetainer()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return null;
        var mgr = RetainerManager.Instance();
        if (mgr == null)
            return null;
        var active = mgr->GetActiveRetainer();
        if (active == null || active->RetainerId == 0)
            return null;
        return (active->RetainerId, active->NameString);
    }

    /// <summary>Rows on the open sell list, or -1 when none is open. Cheap
    /// enough to poll: one AtkValue, no names parsed.</summary>
    public static unsafe int SellListRowCount()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return -1;
        var values = addon->AtkValues;
        if (values == null || addon->AtkValuesCount <= SellListRowCountIndex) return -1;
        return values[SellListRowCountIndex].Int;
    }

    public static unsafe List<(string Name, bool Hq)> ReadSellListRows()
    {
        var rows = new List<(string, bool)>();
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return rows;
        var values = addon->AtkValues;
        if (values == null || addon->AtkValuesCount <= SellListFirstRow) return rows;
        int count = values[SellListRowCountIndex].Int;
        for (int i = 0; i < count; i++)
        {
            int idx = SellListFirstRow + i * SellListRowStride + 1;
            if (idx >= addon->AtkValuesCount) break;
            string raw = ReadAtkString(values[idx]);
            if (string.IsNullOrEmpty(raw))
            {
                // A row we cannot name is a row we must not act on.
                rows.Add((string.Empty, false));
                continue;
            }
            rows.Add((RetainerWalk.NormalizeItemName(raw), RetainerWalk.HasHqGlyph(raw)));
        }
        return rows;
    }

    // The value is a raw SeString, not rendered text: the item name arrives
    // wrapped in link payloads, so the bytes read straight out as UTF-8 carry
    // control codes around the name and match nothing. Parsing to TextValue is
    // what turns it into the same plain name the price window shows. (The
    // price window needs no parsing, because its node text is already
    // rendered, which is why NormalizeItemName only has to strip the HQ
    // glyph and does not touch payloads.)
    private static unsafe string ReadAtkString(AtkValue v)
    {
        switch (v.Type)
        {
            case AtkValueType.String:
            case AtkValueType.ConstString:
            case AtkValueType.ManagedString:
                if (v.String.Value == null) return string.Empty;
                try
                {
                    return MemoryHelper
                        .ReadSeStringNullTerminated((IntPtr)v.String.Value)
                        .TextValue;
                }
                catch
                {
                    return string.Empty;
                }
            default:
                return string.Empty;
        }
    }

    /// <summary>The active retainer's cid and name, or (0, "") when none is
    /// active. Framework thread.</summary>
    public static unsafe (ulong Cid, string Name) ReadActiveRetainer()
    {
        try
        {
            var mgr = RetainerManager.Instance();
            if (mgr == null) return (0, string.Empty);
            var active = mgr->GetActiveRetainer();
            if (active == null) return (0, string.Empty);
            return (active->RetainerId, active->NameString);
        }
        catch { return (0, string.Empty); }
    }

    /// <summary>Every cid in the retainer roster. Framework thread.</summary>
    public static unsafe HashSet<ulong> ReadActiveRetainerCids()
    {
        var set = new HashSet<ulong>();
        try
        {
            var mgr = RetainerManager.Instance();
            if (mgr == null) return set;
            int count = (int)mgr->GetRetainerCount();
            for (int i = 0; i < count; i++)
            {
                var entry = mgr->GetRetainerBySortedIndex((uint)i);
                if (entry == null) continue;
                set.Add(entry->RetainerId);
            }
        }
        catch { }
        return set;
    }

}
