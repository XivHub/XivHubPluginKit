using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivHubPluginKit.Inventory;
using Callback = ECommons.Automation.Callback;

namespace XivHubPluginKit.Shop;

/// <summary>The vendor a run buys from: its name for messages, and every NPC id that serves the shop.</summary>
public sealed record ShopVisit(string Npc, IReadOnlyList<uint> NpcIds);

/// <summary>One item to buy at a <see cref="ShopVisit"/>.</summary>
public sealed record ShopPurchase
{
    public required uint ItemId { get; init; }
    public required string Name { get; init; }
    /// <summary>The gil shop this item is sold from.</summary>
    public required uint ShopId { get; init; }
    /// <summary>The vendor's menu entry that opens the shop, as the game draws it.</summary>
    public required string Menu { get; init; }
    /// <summary>Units to buy.</summary>
    public required int Qty { get; init; }
    public required int StackSize { get; init; }
    /// <summary>Expected price per unit, used until the shop window shows the real one.</summary>
    public required long UnitPrice { get; init; }
}

/// <summary>Per-run timing, read from the consuming plugin's settings on every use.</summary>
public readonly record struct ShopBuyerSettings(int StepDelayMs, int HumanizeMinMs, int HumanizeMaxMs);

/// <summary>What one planned purchase actually did.</summary>
public sealed record BuyResult
{
    public required uint ItemId { get; init; }
    public required string Name { get; init; }
    public required int Requested { get; init; }
    public required int Bought { get; init; }
    /// <summary>Empty when the full quantity was bought.</summary>
    public required string Failure { get; init; }
    /// <summary>Gil per unit the shop charged; 0 when nothing was bought.</summary>
    public long UnitPrice { get; init; }
}

/// <summary>
/// The buy leg of a restock run: walk one <see cref="ShopVisit"/>'s purchases at
/// the NPC the player is standing next to.
///
/// Every quantity is confirmed by inventory delta rather than by assuming the
/// shop callback worked, so a swallowed click costs a retry instead of a phantom
/// purchase report. Each helper self-throttles, re-throttles while its addon isn't ready, and
/// gives up on its own wall-clock deadline so one bad stop can't kill a session.
/// </summary>
public sealed class ShopBuyer
{
    private readonly IPluginLog _log;
    private readonly Func<ShopBuyerSettings> _settings;
    private readonly Action<ShopVisit, ShopPurchase, int, long>? _onBought;
    private readonly TaskManager _tasks;

    public ShopBuyer(
        IPluginLog log, Func<ShopBuyerSettings> settings,
        Action<ShopVisit, ShopPurchase, int, long>? onBought = null)
    {
        _log = log;
        _settings = settings;
        _onBought = onBought;
        _tasks = new TaskManager { TimeLimitMS = 30000, AbortOnTimeout = true };
    }

    public bool IsBusy => _tasks.IsBusy;

    // AutoRetainer-style throttle: gate every click, and
    // keep the throttle fresh while waiting on an addon so the click lands N ms
    // after it becomes ready rather than on the first-ready frame.
    private const string ThrottleName = "KitShopBuyerThrottle";
    /// <summary>Configurable; see <see cref="ShopBuyerSettings.StepDelayMs"/>.</summary>
    private int StepDelayMs => _settings().StepDelayMs;
    private bool GenericThrottle => EzThrottler.Throttle(ThrottleName, StepDelayMs);
    private void RethrottleGeneric() => EzThrottler.Throttle(ThrottleName, StepDelayMs, true);

    /// <summary>How far the player may stand from a vendor and still be served.</summary>
    private const float InteractRangeYalms = 6f;
    private const long OpenShopTimeoutMs = 15000;
    private const long MenuTimeoutMs = 8000;
    private const long PurchaseTimeoutMs = 8000;
    /// <summary>A small buy completes with no confirmation prompt; don't wait long for one.</summary>
    private const long ConfirmWaitMs = 2000;
    /// <summary>Attempts per item before the shortfall is reported instead of retried.</summary>
    private const int MaxAttemptsPerItem = 3;
    /// <summary>Gap between re-fires of a swallowed interact. Slower than the UI
    /// throttle: this is a game action, not a click on an open window.</summary>
    private const string InteractThrottleName = "KitShopBuyerInteract";
    private const int InteractRetryMs = 2000;
    /// <summary>How long a purchase is given to show up in the bags.</summary>
    private const int PurchaseSettleMs = 2500;

    // Wall-clock deadline for the step currently enqueued, stamped by Step().
    private long _deadlineMs;
    // Set by a step that failed in a way retrying can't fix (item not on the
    // shop's list, shop never opened); read by the caller after the drain.
    private string _stepError = string.Empty;
    // Unit price the shop actually charged for the item last bought, which is
    // what the cost basis should record — the plan's price can be stale.
    private uint _observedUnitPrice;
    // Stamped on the confirmation step's first frame, since that step only starts
    // once the purchase click has landed.
    private long _confirmDeadlineMs;
    // Last result InteractWithObject reported, kept for the failure message.
    private long _lastInteractResult;
    // The SelectString entries seen at this NPC, logged once per open so a label
    // that does not match `gil-shop-names.json` is visible without a screenshot.
    private string _seenMenuEntries = string.Empty;
    // Reset per close: the cancel callback gets a few goes before the agent does.
    private int _shopCloseAttempts;

    private void Step(long timeoutMs)
    {
        _deadlineMs = Environment.TickCount64 + timeoutMs;
        _confirmDeadlineMs = 0;
        _stepError = string.Empty;
    }

    /// <summary>True once the current step's deadline has passed.</summary>
    private bool Expired => Environment.TickCount64 > _deadlineMs;

    /// <summary>
    /// Queue steps on the framework thread, where the TaskManager runs, and not at all once the
    /// session is cancelled. A step queued from the pool after a Stop has aborted the queue would
    /// still run, fight <see cref="ConversationUnwinder"/> over the Shop addon, and buy after Stop.
    /// </summary>
    private Task EnqueueAsync(CancellationToken ct, params Func<bool?>[] steps) =>
        Svc.Framework.RunOnFrameworkThread(() =>
        {
            if (ct.IsCancellationRequested) return;
            foreach (var step in steps) _tasks.Enqueue(step);
        });

    /// <summary>Wait for the queue to empty; on cancellation, drop whatever is left on the framework thread.</summary>
    private async Task DrainTasks(CancellationToken ct)
    {
        while (_tasks.IsBusy && !ct.IsCancellationRequested)
            await Task.Delay(200, CancellationToken.None);
        if (ct.IsCancellationRequested)
            await Svc.Framework.RunOnFrameworkThread(() => _tasks.Abort());
    }

    /// <summary>
    /// Buy everything planned at one stop. The player must already be within
    /// interact range of the NPC; routing is a later phase.
    ///
    /// **Every category is its own conversation.** Cancelling a shop does not
    /// reliably hand this NPC's menu back, and carrying on regardless left the
    /// previous category's stock on screen — the next category's items then read
    /// as "not on this shop's list". So each group closes out fully and talks to
    /// the NPC again. It costs a few seconds per category and is the only shape
    /// observed to work in game.
    /// </summary>
    public async Task<List<BuyResult>> RunStopAsync(
        ShopVisit visit, IReadOnlyList<ShopPurchase> purchases, long gilReserve, CancellationToken ct)
    {
        var results = new List<BuyResult>();

        ulong objectId = await Svc.Framework.RunOnFrameworkThread(() => FindVendor(visit.NpcIds));
        if (objectId == 0)
        {
            return FailAll(purchases, $"{visit.Npc} is not within {InteractRangeYalms:0} yalms");
        }

        foreach (var group in purchases.GroupBy(b => b.Menu, StringComparer.Ordinal))
        {
            var items = group.ToList();
            if (ct.IsCancellationRequested)
            {
                results.AddRange(items.Select(b => Fail(b, 0, "session aborted")));
                continue;
            }

            await CloseConversationAsync(ct);

            string? talkFailure = await InteractAsync(visit, objectId, ct);
            if (talkFailure is not null)
            {
                results.AddRange(items.Select(b => Fail(b, 0, talkFailure)));
                continue;
            }

            string? enterFailure = await EnterCategoryAsync(visit, group.Key, ct);
            if (enterFailure is not null)
            {
                results.AddRange(items.Select(b => Fail(b, 0, enterFailure)));
                await CloseConversationAsync(ct);
                continue;
            }

            foreach (var purchase in items)
            {
                if (ct.IsCancellationRequested)
                {
                    results.Add(Fail(purchase, 0, "session aborted"));
                    continue;
                }
                results.Add(await BuyOneAsync(visit, purchase, gilReserve, ct));
                var s = _settings();
                await Humanizer.PauseAsync(s.HumanizeMinMs, s.HumanizeMaxMs, ct);
            }

            await CloseConversationAsync(ct);
        }

        return results;
    }

    /// <summary>Drop queued work. Leaving the conversation is
    /// <see cref="ConversationUnwinder"/>'s job, driven once for both legs.</summary>
    public void AbortAll() => _tasks.Abort();

    // --- opening the shop --------------------------------------------------

    /// <summary>Talk to the vendor. Null once a shop or its menu is up, otherwise the reason it could not be opened.</summary>
    private async Task<string?> InteractAsync(ShopVisit visit, ulong objectId, CancellationToken ct)
    {
        _lastInteractResult = 0;
        _seenMenuEntries = string.Empty;

        Step(OpenShopTimeoutMs);
        await EnqueueAsync(ct, () => InteractWithVendor(visit.Npc, objectId));
        await DrainTasks(ct);
        if (ct.IsCancellationRequested) return "session aborted";
        if (_stepError.Length > 0) return _stepError;

        var addon = await Svc.Framework.RunOnFrameworkThread(ReadOpenShopAddon);
        _log.Information("Shop: {Npc} opened {Addon} (interact result {Result}; visible: {Visible})",
            visit.Npc, addon.Length == 0 ? "nothing" : addon, _lastInteractResult,
            await Svc.Framework.RunOnFrameworkThread(DescribeVisibleAddons));
        return addon.Length == 0
            ? $"{visit.Npc} opened nothing recognisable"
            : null;
    }

    /// <summary>
    /// Get from wherever the conversation is to this category's shop window.
    /// A vendor with no menu is already there. Null on success.
    /// </summary>
    private async Task<string?> EnterCategoryAsync(ShopVisit visit, string menu, CancellationToken ct)
    {
        var addon = await Svc.Framework.RunOnFrameworkThread(ReadOpenShopAddon);
        if (addon == "ShopExchangeCurrency")
            return $"{visit.Npc} opened a currency exchange, which the buy leg does not drive";
        if (addon == "Shop")
            return null;

        Step(MenuTimeoutMs);
        await EnqueueAsync(ct, () => SelectShopMenu(visit.Npc, menu));
        await DrainTasks(ct);
        if (ct.IsCancellationRequested) return "session aborted";
        if (_stepError.Length > 0) return _stepError;

        addon = await Svc.Framework.RunOnFrameworkThread(ReadOpenShopAddon);
        _log.Information("Shop: entered \"{Menu}\" at {Npc} -> {Addon}", menu, visit.Npc, addon);
        return addon switch
        {
            "Shop" => null,
            "ShopExchangeCurrency" =>
                $"{visit.Npc} opened a currency exchange, which the buy leg does not drive",
            _ => $"{visit.Npc}'s shop did not open for \"{menu}\" (saw \"{addon}\")",
        };
    }

    /// <summary>
    /// Whether this stop's NPC is already close enough to serve you. A run that
    /// starts in front of the vendor has no business replaying the walk: it puts
    /// the character back at the bell and returns, which wastes the trip and
    /// leaves it still settling when the interact fires.
    /// </summary>
    public static async Task<bool> IsAtVendorAsync(ShopVisit visit)
        => await Svc.Framework.RunOnFrameworkThread(() => FindVendor(visit.NpcIds) != 0);

    /// <summary>
    /// The nearest targetable NPC among <paramref name="npcIds"/> within interact
    /// range, or 0 when none is. Framework thread.
    /// </summary>
    public static ulong FindVendor(IReadOnlyList<uint> npcIds)
    {
        var me = Player.Object;
        if (me is null || npcIds.Count == 0)
            return 0;
        var match = Svc.Objects
            .Where(o => npcIds.Contains(o.BaseId) && o.IsTargetable)
            .Select(o => (Obj: o, Distance: Vector3.Distance(o.Position, me.Position)))
            .Where(x => x.Distance <= InteractRangeYalms)
            .OrderBy(x => x.Distance)
            .Select(x => x.Obj)
            .FirstOrDefault();
        return match?.GameObjectId ?? 0;
    }

    /// <summary>
    /// Target the vendor and interact, retrying only while nothing opens.
    ///
    /// Firing once and assuming it landed loses the whole budget when the game
    /// drops the call, which it does while the character is still settling
    /// after a walk. Firing every frame is worse: <c>InteractWithObject</c> is
    /// a toggle, so a second call shuts the menu the first one opened.
    ///
    /// So a shop or menu on screen, ready or still animating open, is the only
    /// success test; the call's return value is not trusted, since it has read
    /// as success with nothing opened. Until something appears, the call is
    /// re-fired no faster than <c>InteractRetryMs</c>, long enough for a window
    /// the last call opened to show up before the next could toggle it shut.
    /// </summary>
    private unsafe bool? InteractWithVendor(string npc, ulong objectId)
    {
        if (ReadOpenShopAddon().Length > 0) return true;

        if (Expired)
        {
            _stepError = $"{npc} never opened a shop or a menu "
                       + $"(last interact result {_lastInteractResult}; "
                       + $"visible: {DescribeVisibleAddons()})";
            return true;
        }

        // Something is on screen, ready or still animating: the call took, and
        // re-firing would toggle it shut. That check is the whole guard — the
        // result code is not trustworthy enough to gate on. A run once sat idle
        // for the full 15s budget because InteractWithObject returned a large
        // pointer-like value that read as success while nothing had opened.
        if (AnyShopAddonExists())
        {
            RethrottleGeneric();
            return false;
        }

        var obj = Svc.Objects.FirstOrDefault(o => o.GameObjectId == objectId);
        if (obj is null || !obj.IsTargetable || Player.IsAnimationLocked || !Player.Interactable)
        {
            RethrottleGeneric();
            return false;
        }

        if (Svc.Targets.Target?.GameObjectId != objectId)
        {
            if (!GenericThrottle) return false;
            Svc.Targets.Target = obj;
            return false;
        }

        if (!EzThrottler.Throttle(InteractThrottleName, InteractRetryMs)) return false;
        _lastInteractResult = (long)TargetSystem.Instance()->InteractWithObject(obj.Struct(), false);
        _log.Debug("Shop: interact with {Npc} returned {Result}", npc, _lastInteractResult);
        return false;
    }

    /// <summary>Any shop-ish addon present, ready or still animating open.</summary>
    private static unsafe bool AnyShopAddonExists()
    {
        foreach (var name in ShopAddonNames)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && addon->IsVisible)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every visible addon, by name. A guessed shortlist is what hid this class
    /// of bug twice: the estate servant's menu simply was not one of the names
    /// being looked for, so the run reported "visible: none" while a menu sat on
    /// screen. Enumerating the unit manager cannot be wrong in that way.
    /// </summary>
    private static unsafe string DescribeVisibleAddons()
    {
        try
        {
            var seen = new List<string>();
            var units = &AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
            for (var i = 0; i < units->Count; i++)
            {
                var unit = units->Entries[i].Value;
                if (unit == null || !unit->IsVisible) continue;
                var name = unit->NameString;
                if (!string.IsNullOrEmpty(name)) seen.Add(name);
            }
            seen.Sort(StringComparer.Ordinal);
            return seen.Count == 0 ? "none" : string.Join(", ", seen);
        }
        catch (Exception e)
        {
            return $"<enumeration failed: {e.Message}>";
        }
    }

    /// <summary>
    /// The addons a vendor conversation can be sitting in. An NPC menu is
    /// <c>SelectString</c> for most vendors and <c>SelectIconString</c> for
    /// others — the estate servant draws the latter — and the two are otherwise
    /// interchangeable here, so both count as "the menu".
    /// </summary>
    private static readonly string[] ShopAddonNames =
        ["Shop", "ShopExchangeCurrency", "SelectString", "SelectIconString"];

    private static readonly string[] MenuAddonNames = ["SelectString", "SelectIconString"];

    /// <summary>Which of the shop-ish addons is up and ready; empty when none is.</summary>
    private static unsafe string ReadOpenShopAddon()
    {
        foreach (var name in ShopAddonNames)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)
                && GenericHelpers.IsAddonReady(addon))
                return name;
        }
        return string.Empty;
    }

    /// <summary>
    /// Choose the shop's own menu entry and keep choosing until a shop window
    /// replaces it. The click is dropped as readily as the interact is, and the
    /// entries the game actually drew are logged the first time they are seen: a
    /// label that disagrees with `gil-shop-names.json` is the single most likely
    /// reason a stop fails, and this is what proves it either way.
    /// </summary>
    private unsafe bool? SelectShopMenu(string npc, string menu)
    {
        var open = ReadOpenShopAddon();
        if (open is "Shop" or "ShopExchangeCurrency") return true;

        var entries = ReadMenuEntries();
        if (entries is null)
        {
            RethrottleGeneric();
            if (!Expired) return false;
            _stepError = $"{npc}'s menu vanished before \"{menu}\" could be chosen "
                       + $"(visible: {DescribeVisibleAddons()})";
            return true;
        }

        if (_seenMenuEntries.Length == 0)
        {
            _seenMenuEntries = string.Join(" | ", entries.Select(e => e.Text));
            _log.Information("Shop: {Npc} menu entries: {Entries}", npc, _seenMenuEntries);
        }

        int index = entries.FindIndex(e => string.Equals(e.Text, menu, StringComparison.Ordinal));
        if (index < 0)
        {
            _stepError = $"no menu entry \"{menu}\" (saw: {_seenMenuEntries})";
            return true;
        }

        if (Expired)
        {
            _stepError = $"chose \"{menu}\" but no shop opened "
                       + $"(visible: {DescribeVisibleAddons()})";
            return true;
        }

        if (!GenericThrottle) return false;
        entries[index].Select();
        // Verify next tick that a shop replaced the menu.
        return false;
    }

    /// <summary>
    /// The open NPC menu's entries, whichever of the two addons is drawing it.
    /// Null when no menu is up.
    /// </summary>
    private static unsafe List<(string Text, Action Select)>? ReadMenuEntries()
    {
        foreach (var name in MenuAddonNames)
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)
                || !GenericHelpers.IsAddonReady(addon))
                continue;

            if (name == "SelectString")
            {
                var ss = new AddonMaster.SelectString(addon);
                return ss.Entries
                    .Select(e => (e.Text, (Action)(() => e.Select())))
                    .ToList();
            }

            var sis = new AddonMaster.SelectIconString(addon);
            return sis.Entries
                .Select(e => (e.Text, (Action)(() => e.Select())))
                .ToList();
        }
        return null;
    }

    /// <summary>
    /// Leave the conversation entirely: the shop, then the menu behind it, each
    /// through its own exit handler.
    ///
    /// Does nothing once the run is cancelled. A Stop hands the screen to
    /// <see cref="ConversationUnwinder"/>, and queueing teardown steps here as
    /// well had the two fighting over the same addons — which is why an abort
    /// used to leave the player stranded in the conversation.
    /// </summary>
    private async Task CloseConversationAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        _shopCloseAttempts = 0;
        Step(MenuTimeoutMs);
        await EnqueueAsync(ct, CloseShopAddon, CloseMenuAddon);
        await DrainTasks(ct);
        if (_stepError.Length > 0)
            _log.Warning("Shop: {Failure}", _stepError);
    }

    /// <summary>
    /// Leave an addon through its own cancel handler instead of hiding it.
    ///
    /// <c>Close(true)</c> hides the widget without the game exiting the event
    /// behind it, which strands the player in the conversation: able to move,
    /// unable to interact with anything or even drop the target, with no UI left
    /// to escape through — a client restart. Callback
    /// -1 is the handler the Escape key fires, so it unwinds one level the way
    /// the game intends, which is also what puts the NPC's menu back on screen
    /// after a shop rather than ending the conversation.
    /// </summary>
    private static unsafe bool CancelAddon(string name)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)
            || !addon->IsVisible)
            return false;
        Callback.Fire(addon, true, -1);
        return true;
    }

    /// <summary>
    /// Fire the shop's own close handler until the window is actually gone.
    ///
    /// Firing once and moving on left the shop open, and the next category's
    /// <c>InteractAsync</c> then saw a shop already up, declared success and
    /// bought against the previous category's stock — every item reported "not
    /// on this shop's list". Questionable closes the same way
    /// (<c>addonShop-&gt;FireCallbackInt(-1)</c>); the missing part was checking.
    /// </summary>
    private unsafe bool? CloseShopAddon()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Shop", out var addon) || !addon->IsVisible)
            return true;
        if (Expired)
        {
            _stepError = $"the shop window would not close (visible: {DescribeVisibleAddons()})";
            return true;
        }
        if (!GenericThrottle) return false;
        if (++_shopCloseAttempts <= 3)
        {
            Callback.Fire(addon, true, -1);
        }
        else
        {
            // The cancel callback is not always enough for this window; the
            // agent behind it is what actually ends the shop.
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Shop);
            if (agent != null && agent->IsAgentActive())
                agent->Hide();
            else
                addon->Close(true);
        }
        return false;
    }

    /// <summary>
    /// Leave the NPC's menu through its own last entry — "Nothing" — rather than
    /// the cancel callback.
    ///
    /// Cancelling dismisses the widget without telling the event handler the
    /// conversation is over, and the game then holds the player in it: the NPC
    /// stays selected, Escape does nothing, and no amount of closing windows
    /// helps. Choosing the exit entry is what the player does, and it is the
    /// only thing observed to actually end the event.
    /// </summary>
    private unsafe bool? CloseMenuAddon()
    {
        var entries = ReadMenuEntries();
        if (entries is null || entries.Count == 0) return true;
        if (Expired) return true;
        if (!GenericThrottle) return false;
        entries[^1].Select();
        return false;
    }

    // --- buying ------------------------------------------------------------

    /// <summary>
    /// Buy one item's planned quantity, in as few clicks as the shop allows.
    ///
    /// **Stack size decides the click size.** A stackable good — food, reagents —
    /// gets a quantity field in the shop window and takes its whole run in one
    /// purchase. A furnishing stacks to one, has no quantity field, and is sold
    /// singly: asking for more bought nothing, moved no gil, and left the shop
    /// unable to complete anything after it, so the rest of that category failed
    /// too. Fifteen Orange Juice is one click; seven Dark Wood Flooring is seven.
    /// </summary>
    private async Task<BuyResult> BuyOneAsync(ShopVisit visit, ShopPurchase purchase, long gilReserve, CancellationToken ct)
    {
        // Qty is already the shortfall; callers subtract what they hold.
        int target = Math.Max(0, purchase.Qty);
        if (target == 0)
        {
            return new BuyResult
            {
                ItemId = purchase.ItemId, Name = purchase.Name,
                Requested = 0, Bought = 0, Failure = string.Empty,
            };
        }

        int bought = 0;
        string failure = string.Empty;
        int misses = 0;
        // Per item: the gil clamp below and the emitted cost basis both read this,
        // and the previous item's price is not this item's price.
        _observedUnitPrice = 0;

        while (bought < target && misses < MaxAttemptsPerItem)
        {
            if (ct.IsCancellationRequested)
            {
                failure = "session aborted";
                break;
            }

            var (before, gil, freeSlots) = await Svc.Framework.RunOnFrameworkThread(
                () => (MainBags.Count(purchase.ItemId), MainBags.ReadGil(), MainBags.ReadFreeSlots()));

            // Both caps moved while the plan was on screen: a venture return can
            // eat the bag slots the plan counted on, and the gil floor is re-checked
            // here as well as server-side.
            long spendable = gil - gilReserve;
            long unitPrice = _observedUnitPrice > 0 ? _observedUnitPrice : purchase.UnitPrice;
            if (unitPrice <= 0 || spendable < unitPrice)
            {
                failure = "gil reserve reached";
                break;
            }
            if (FitsInBags(freeSlots, before, purchase.StackSize) <= 0)
            {
                failure = "no free bag slots";
                break;
            }

            // One slot per unit means one purchase per unit; anything that
            // stacks can be taken in a single go.
            int perClick = purchase.StackSize > 1 ? target - bought : 1;

            Step(PurchaseTimeoutMs);
            await EnqueueAsync(ct, () => SelectShopItem(purchase.ItemId, perClick), ConfirmPurchase);
            await DrainTasks(ct);

            if (_stepError.Length > 0)
            {
                failure = _stepError;
                break;
            }

            int after = await SettledBagCountAsync(purchase.ItemId, before, ct);
            int got = after - before;
            if (got <= 0)
            {
                // Whether gil moved separates a click the shop ignored from one
                // that bought something which went somewhere other than the bags.
                var (gilAfter, slotsAfter) = await Svc.Framework.RunOnFrameworkThread(
                    () => (MainBags.ReadGil(), MainBags.ReadFreeSlots()));
                _log.Warning(
                    "Shop: {Item} {Unit}/{Qty} did not arrive — bags {Before}->{After}, "
                    + "gil {GilBefore}->{GilAfter}, {Slots} free slots, miss {Miss}/{Max}; visible: {Visible}",
                    purchase.Name, bought + perClick, target, before, after, gil, gilAfter, slotsAfter,
                    misses + 1, MaxAttemptsPerItem,
                    await Svc.Framework.RunOnFrameworkThread(DescribeVisibleAddons));
                misses++;
                continue;
            }

            bought += got;
            misses = 0;
            if (bought < target)
            {
                var s = _settings();
                await Humanizer.PauseAsync(s.HumanizeMinMs, s.HumanizeMaxMs, ct);
            }
        }

        long paid = bought > 0 ? EmitPurchase(visit, purchase, bought) : 0;

        if (bought < target && failure.Length == 0)
            failure = $"only {bought} of {target} bought";

        if (failure.Length > 0)
            _log.Warning("Shop: {Item} at {Npc}: {Failure}", purchase.Name, visit.Npc, failure);

        return new BuyResult
        {
            ItemId = purchase.ItemId,
            Name = purchase.Name,
            Requested = target,
            Bought = bought,
            Failure = failure,
            UnitPrice = paid,
        };
    }

    /// <summary>
    /// Units of an item the bags can still take: whole free slots plus whatever
    /// room is left in the partial stack the item already occupies. Stack size 1
    /// (every furnishing) makes this exactly the free-slot count.
    /// </summary>
    private static int FitsInBags(int freeSlots, int held, int stackSize)
    {
        int stack = Math.Max(1, stackSize);
        int roomInPartial = held % stack == 0 ? 0 : stack - (held % stack);
        return (freeSlots * stack) + roomInPartial;
    }

    private unsafe bool? SelectShopItem(uint itemId, int qty)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Shop", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            RethrottleGeneric();
            if (!Expired) return false;
            _stepError = "shop window closed mid-purchase";
            return true;
        }

        var shop = new AddonMaster.Shop(addon);
        var row = shop.ShopItems.FirstOrDefault(s => s.ItemId == itemId);
        if (row is null)
        {
            _stepError = "not on this shop's list";
            return true;
        }
        if (!GenericThrottle) return false;
        // Record what the shop charges rather than what the plan predicted: this
        // is the number the cost basis is built from.
        _observedUnitPrice = row.CostAmount;
        _log.Debug("Shop: buying {Qty}x item {ItemId} at {Cost} gil ({Rows} rows on the list)",
            qty, itemId, row.CostAmount, shop.ShopItems.Length);
        row.Select(qty);
        return true;
    }

    /// <summary>
    /// Large purchases raise a confirmation; small ones complete without one, so
    /// this settles on the shorter <see cref="ConfirmWaitMs"/> budget instead of
    /// treating a missing dialog as a failure.
    /// </summary>
    private unsafe bool? ConfirmPurchase()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)
            && GenericHelpers.IsAddonReady(addon))
        {
            if (!GenericThrottle) return false;
            new AddonMaster.SelectYesno(addon).Yes();
            return true;
        }
        if (_confirmDeadlineMs == 0)
            _confirmDeadlineMs = Environment.TickCount64 + ConfirmWaitMs;
        return Environment.TickCount64 > _confirmDeadlineMs || Expired;
    }

    /// <summary>
    /// The count of an item in the bags once it has stopped moving, or once
    /// <see cref="PurchaseSettleMs"/> has passed.
    ///
    /// A purchase does not land in the same frame as the click, and reading the
    /// count immediately made a successful buy look like a failure — so the
    /// retry bought it a second time. Two Selects of one Glade Bench produced
    /// one "bought 2x": both clicks had landed, only the first check was early.
    /// Gil spent twice is worse than a slow step, so this waits.
    /// </summary>
    private static async Task<int> SettledBagCountAsync(uint itemId, int before, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + PurchaseSettleMs;
        int last = before;
        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(150, CancellationToken.None);
            int now = await Svc.Framework.RunOnFrameworkThread(() => MainBags.Count(itemId));
            if (now != last)
            {
                // Moved. Give it a moment more in case the rest is still arriving.
                last = now;
                deadline = Math.Min(deadline, Environment.TickCount64 + 400);
            }
        }
        return last;
    }

    /// <summary>Reports the purchase to <c>onBought</c> and returns the unit price it was reported at.</summary>
    private long EmitPurchase(ShopVisit visit, ShopPurchase purchase, int qty)
    {
        long unitPrice = _observedUnitPrice > 0 ? _observedUnitPrice : purchase.UnitPrice;
        _onBought?.Invoke(visit, purchase, qty, unitPrice);
        _log.Information("Shop: bought {Qty}x {Item} at {Price} gil from {Npc}",
            qty, purchase.Name, unitPrice, visit.Npc);
        return unitPrice;
    }

    private static List<BuyResult> FailAll(IReadOnlyList<ShopPurchase> purchases, string reason)
        => purchases.Select(b => Fail(b, 0, reason)).ToList();

    private static BuyResult Fail(ShopPurchase purchase, int bought, string reason)
        => new()
        {
            ItemId = purchase.ItemId,
            Name = purchase.Name,
            Requested = Math.Max(0, purchase.Qty),
            Bought = bought,
            Failure = reason,
        };
}
