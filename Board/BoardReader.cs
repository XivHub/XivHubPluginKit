using System;
using System.Collections.Generic;
using System.Linq;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivHubPluginKit.Inventory;

namespace XivHubPluginKit.Board;

/// <summary>
/// Reads the market board's windows, <see cref="AgentItemSearch"/> and
/// <see cref="InfoProxyItemSearch"/>. Every field read here is one the owner's UiCapture
/// recordings show changing through a search and a buy: market-board.md "Search and buy flow",
/// session 37c13073e8c8 (a typed search for "shard", Fire Shard and Earth Shard opened, a buy),
/// with struct watches on both. Framework thread only.
/// </summary>
public static unsafe class BoardReader
{
    public const string SearchAddon = "ItemSearch";
    public const string ResultAddon = "ItemSearchResult";
    public const string YesnoAddon = "SelectYesno";

    /// <summary>
    /// Board windows that open over the results window (37c13073e8c8 seq 63, 80, 87: the confirm
    /// prompt, the filter and the history). A search started under one of them would close the
    /// results window it belongs to.
    /// </summary>
    private static readonly string[] BlockingAddons = ["ItemSearchFilter", "ItemHistory", YesnoAddon];

    private static bool loggedUnavailable;

    /// <summary>The board's search window is open and ready for a callback.</summary>
    public static bool SearchOpen() => ReadyAddon(SearchAddon) != null;

    /// <summary>The board's results window is open and ready for a callback.</summary>
    public static bool ResultsOpen() => ReadyAddon(ResultAddon) != null;

    /// <summary>
    /// The addon named <paramref name="name"/> when it is visible and fully loaded, else null;
    /// the test the kit's <c>ShopBuyer</c> applies before it fires a callback.
    /// </summary>
    public static AtkUnitBase* ReadyAddon(string name)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)) return null;
        // SAFETY: TryGetAddonByName returned true, so addon is the non-null address of a unit the
        // game's AtkUnitManager holds this frame; IsAddonReady reads its visibility and load state.
        return GenericHelpers.IsAddonReady(addon) ? addon : null;
    }

    /// <summary>The addon named <paramref name="name"/> exists and is visible, loaded or not.</summary>
    public static bool WindowVisible(string name)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)) return false;
        // SAFETY: TryGetAddonByName returned true, so addon is the non-null address of a unit the
        // game's AtkUnitManager holds this frame; IsVisible is a flag read off it.
        return addon->IsVisible;
    }

    /// <summary>The first of the board's filter, history and confirm windows that is visible, else null.</summary>
    public static string? BlockingWindow() => BlockingAddons.FirstOrDefault(WindowVisible);

    /// <summary>
    /// The InfoProxy's listings and the agent's result item, or null when either struct isn't
    /// available. <see cref="BoardSnapshot.Listings"/> holds slots
    /// <c>0..min(ListingCount, EntryCount, 100)</c> in slot order, the index
    /// <c>ItemSearchResult [2, i]</c> takes. Slots at or past <c>EntryCount</c> are left out: the
    /// game's listing lookup (0x140971A90) and its purchase copy (0x140971C40) refuse such an
    /// index, and Yes on one resends the previous purchase. Both structs exist whenever the player
    /// is logged in and keep the last search's slots, so a non-null read does not mean the board
    /// is open: gate on <see cref="ResultsOpen"/> and the read's result item id.
    /// </summary>
    public static BoardSnapshot? Read()
    {
        var agent = Agent();
        var proxy = Proxy();
        if (agent == null || proxy == null) return null;

        // SAFETY: agent and proxy are non-null (checked) and point at the game's AgentItemSearch
        // and InfoProxyItemSearch, which live as long as the UI module; the fields read are plain
        // members of each. Listings spans the proxy's inline 100-slot array (FixedSizeArray100 in
        // ClientStructs) and the loop stops at min(ListingCount, EntryCount, Listings.Length), so a
        // count past 100 never reads outside it.
        var listings = proxy->Listings;
        var count = (int)Math.Min(Math.Min(proxy->ListingCount, proxy->EntryCount), (uint)listings.Length);
        var read = new List<BoardListing>(count);
        for (var i = 0; i < count; i++)
        {
            ref var l = ref listings[i];
            read.Add(new BoardListing(l.ListingId, l.RetainerId, l.ItemId, l.UnitPrice, (int)l.Quantity, l.TotalTax,
                l.IsHqItem));
        }

        return new BoardSnapshot(
            proxy->SearchItemId,
            agent->ResultItemId,
            proxy->CurrentRequestId,
            (int)proxy->ListingCount,
            proxy->EntryCount,
            read);
    }

    /// <summary>
    /// <see cref="AgentItemSearch.ResultSelectedIndex"/>, the slot the last
    /// <c>ItemSearchResult [2, i]</c> picked (i; 37c13073e8c8 seq 62 and 64, 108ba2790349 seq 134
    /// and 137). The game's Yes handler buys <c>Listings</c> at this index as the slot is at Yes
    /// (0x1405371B9). Null when the agent isn't available.
    /// </summary>
    public static uint? SelectedIndex()
    {
        var agent = Agent();
        // SAFETY: agent is null-checked and points at the game's AgentItemSearch;
        // ResultSelectedIndex is a plain uint member at +0x3394.
        return agent == null ? null : agent->ResultSelectedIndex;
    }

    /// <summary>
    /// <c>InfoProxyItemSearch.LastPurchasedMarketboardItem.ListingId</c>, the listing the game
    /// copied from the selected slot when the last purchase prompt was answered Yes (0x140971C40;
    /// 37c13073e8c8 seq 69, 108ba2790349 seq 150). Null when the InfoProxy isn't available.
    /// </summary>
    public static ulong? LastPurchasedListingId()
    {
        var proxy = Proxy();
        // SAFETY: proxy is null-checked and points at the game's InfoProxyItemSearch;
        // LastPurchasedMarketboardItem is an inline struct at +0x5680 and ListingId a plain member.
        return proxy == null ? null : proxy->LastPurchasedMarketboardItem.ListingId;
    }

    /// <summary>
    /// The search page: <see cref="AgentItemSearch.ListingPageLoaded"/>,
    /// <see cref="AgentItemSearch.IsPartialSearching"/> and the first
    /// <see cref="AgentItemSearch.ListingPageItemCount"/> result ids (at most 100). The ids are
    /// the page's only once it is loaded and not searching (37c13073e8c8 seq 36 and 41). Null
    /// when the agent isn't available.
    /// </summary>
    public static SearchPage? ReadSearchPage()
    {
        var agent = Agent();
        if (agent == null) return null;

        // SAFETY: agent is non-null (checked) and points at the game's AgentItemSearch.
        // ListingPageItemIds spans its inline 100-slot array and the copy stops at
        // min(ListingPageItemCount, 100), so it never reads past the array.
        var ids = agent->ListingPageItemIds;
        var count = (int)Math.Min(agent->ListingPageItemCount, (uint)ids.Length);
        return new SearchPage(agent->ListingPageLoaded, agent->IsPartialSearching, ids[..count].ToArray());
    }

    /// <summary>
    /// Units of <paramref name="itemId"/> a board purchase adds to, either quality. Board crystals
    /// land in <see cref="InventoryType.Crystals"/>, not the bags (market-board.md), so a crystal
    /// line sums that container; anything else sums the four main bags, NQ and HQ, since a listing
    /// can be either.
    /// </summary>
    public static long HeldForBuy(uint itemId, bool crystal)
    {
        if (!crystal) return MainBags.Count(itemId, false) + MainBags.Count(itemId, true);

        long held = 0;
        foreach (var slot in InventoryScan.ScanContainer(InventoryType.Crystals))
            if (slot.ItemId == itemId) held += slot.Qty;
        return held;
    }

    /// <summary>
    /// The board's purchase prompt text when <c>SelectYesno</c> is open and ready, else null:
    /// <c>AddonMaster.SelectYesno.TextLegacy</c>, which reads <c>AtkValues[0]</c> and joins its
    /// text payloads, dropping the <c>UIForeground</c>/<c>UIGlow</c> payloads the game wraps the
    /// item name in (37c13073e8c8 seq 63, 108ba2790349 seq 136).
    /// </summary>
    public static string? YesnoText()
    {
        var addon = ReadyAddon(YesnoAddon);
        if (addon == null) return null;

        // SAFETY: addon is the non-null, ready SelectYesno unit read this frame. AtkValues is
        // checked non-null with at least one value, and value 0 is read as text only when it holds
        // a non-null narrow string, the one TextLegacy decodes; a wide or missing string is null.
        if (addon->AtkValuesCount == 0 || addon->AtkValues == null) return null;
        var value = addon->AtkValues[0];
        var type = value.Type & AtkValueType.TypeMask;
        if (type is not (AtkValueType.String or AtkValueType.ConstString) || !value.String.HasValue) return null;
        return new AddonMaster.SelectYesno(addon).TextLegacy;
    }

    /// <summary>
    /// The ItemSearch agent, or null. ClientStructs throws rather than returning null when the
    /// AgentModule or its lookup didn't resolve after a game update, and this runs from window
    /// draws, so the failure is caught and logged once.
    /// </summary>
    private static AgentItemSearch* Agent()
    {
        try
        {
            var module = AgentModule.Instance();
            // SAFETY: module is null-checked before GetAgentByInternalId, a ClientStructs member of
            // the AgentModule singleton that returns the agent for a fixed AgentId; AgentItemSearch
            // is the struct ClientStructs maps to AgentId.ItemSearch.
            return module == null ? null : (AgentItemSearch*)module->GetAgentByInternalId(AgentId.ItemSearch);
        }
        catch (InvalidOperationException ex)
        {
            LogUnavailable(ex);
            return null;
        }
    }

    /// <summary>
    /// The ItemSearch InfoProxy, or null. Read through the InfoModule rather than the agent's
    /// own pointer, which is null until the board is first opened (108ba2790349 seq 12 and 19).
    /// </summary>
    private static InfoProxyItemSearch* Proxy()
    {
        try
        {
            var module = InfoModule.Instance();
            // SAFETY: module is null-checked before GetInfoProxyItemSearch, which casts the
            // InfoModule's proxy for the fixed InfoProxyId.ItemSearch.
            return module == null ? null : module->GetInfoProxyItemSearch();
        }
        catch (InvalidOperationException ex)
        {
            LogUnavailable(ex);
            return null;
        }
    }

    private static void LogUnavailable(InvalidOperationException ex)
    {
        if (loggedUnavailable) return;
        loggedUnavailable = true;
        KitServices.Log.Warning(ex, "The market board's agent or InfoProxy is unavailable; board search is off.");
    }
}
