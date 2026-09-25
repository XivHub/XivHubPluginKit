using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivHubPluginKit.Board;
using XivHubPluginKit.Retainer;
using XivHubPluginKit.Shop;

namespace XivHubPluginKit.Pinch;

/// <summary>
/// Plans one retainer's sell list: which rows get a live board lookup.
/// Returning null means the plan failed and the retainer is skipped. The token
/// is the run's own, so cancelling the run cancels the plan.
/// </summary>
public delegate Task<RetainerPlan?> PlanFn(PinchRetainer retainer, IReadOnlyList<RetainerMarketRow> rows, CancellationToken ct);

/// <summary>
/// Reprices retainer listings: walks the sell list, opens each row's Adjust
/// Price window, and writes the price a live Compare Prices lookup decides.
/// The consuming plugin supplies the policy: which rows get a live lookup
/// (<see cref="PlanFn"/>), the profit floor, which retainers besides the
/// session's own roster count as ours, and what happens with the result.
///
/// Every UI step runs on the framework thread through an ECommons
/// <see cref="TaskManager"/>; the entry points themselves run on any thread and
/// read addon memory only through <c>Svc.Framework.RunOnFrameworkThread</c>.
/// </summary>
public sealed class PinchEngine : IDisposable
{
    private readonly IPluginLog _log;
    private readonly Func<PinchSettings> _settings;
    private readonly Func<uint, int> _profitFloor;
    private readonly Func<IEnumerable<ulong>>? _extraOwnCids;
    private readonly TaskManager _tasks;
    private readonly LivePriceProbe _probe;
    private readonly string _throttleName;

    private CancellationTokenSource? _cts;

    /// <param name="mb">The plugin's market-board listener; the engine's probe reads the board through it.</param>
    /// <param name="log">The plugin's log. Pass the plugin's tee here, or the engine's lines miss its dev log.</param>
    /// <param name="throttleTag">Prefix for the EzThrottler names, <c>&lt;tag&gt;GenericThrottle</c> and <c>&lt;tag&gt;MBThrottle</c>.</param>
    /// <param name="settings">Read on every use.</param>
    /// <param name="profitFloor">Item id to the lowest asking price a write may set.</param>
    /// <param name="extraOwnCids">Retainers counted as ours on top of the session's roster; null for none.</param>
    /// <param name="onBoardResolved">Fires once per finished board lookup; see <see cref="LivePriceProbe"/>.</param>
    public PinchEngine(
        MarketBoardListener mb, IPluginLog log, string throttleTag,
        Func<PinchSettings> settings, Func<uint, int> profitFloor,
        Func<IEnumerable<ulong>>? extraOwnCids = null, Action<uint, bool>? onBoardResolved = null)
    {
        _log = log;
        _settings = settings;
        _profitFloor = profitFloor;
        _extraOwnCids = extraOwnCids;
        _throttleName = throttleTag + "GenericThrottle";
        _probe = new LivePriceProbe(mb, CollectOwnCids, log, throttleTag + "MBThrottle", "Pinch", onBoardResolved);
        _tasks = new TaskManager { TimeLimitMS = 15000, AbortOnTimeout = true };
    }

    /// <summary>The item the open window is selling; the floor is per item.</summary>
    private uint _liveItemId;

    // Prices written on the current retainer. Each is decided only once its
    // row's window is open and the board has answered, so it is recorded here
    // as it happens and folded into the result after the queue drains.
    private readonly List<PinchWrite> _liveWrites = new();

    // Written from the framework thread as each row is priced, read from the
    // session's own thread once the queue drains, and an aborted session can
    // still have a row in flight while the reader runs, so the list is locked.
    private void RecordLiveWrite(bool hq, uint from, uint to)
    {
        var item = new PinchWrite(_liveItemId, hq, from, to);
        lock (_liveWrites) _liveWrites.Add(item);
    }

    /// <summary>
    /// Move what the board path wrote into a retainer's write list, and say
    /// how many there were. Call once, after the queue has drained.
    /// </summary>
    private int TakeLiveWrites(List<PinchWrite> into)
    {
        lock (_liveWrites)
        {
            int n = _liveWrites.Count;
            into.AddRange(_liveWrites);
            _liveWrites.Clear();
            return n;
        }
    }

    // Active character's retainer CIDs for the current run, read from
    // RetainerManager at run start. Passed to the market-board listener so our
    // own listings are excluded from the competitor set.
    private HashSet<ulong> _sessionOwnCids = new();

    // Own-CID set for the live-board competitor scan: the session roster
    // UNIONED with the caller's extra set. The session roster only covers
    // whoever is logged in right now, so a second character's retainers would
    // otherwise read back as competitors on this one's board and the two would
    // undercut each other by 1 every session.
    private HashSet<ulong> CollectOwnCids()
    {
        var own = new HashSet<ulong>(_sessionOwnCids);
        if (_extraOwnCids is not null) own.UnionWith(_extraOwnCids());
        return own;
    }

    // Early-exit: number of rows on the current retainer still expected to
    // need a window opened for a board lookup. Set per-retainer in EnqueuePinchTasks, decremented on each real
    // write and on each row given up on. Once it hits 0, OpenItemContextMenu
    // short-circuits the remaining rows (no window open) instead of churning
    // through every already-at-target listing.
    private int _rowsRemaining = int.MaxValue;

    // Set when the current row can't be repriced: its context menu offers no
    // "Adjust Price" (a mannequin listing), or the window never opened, so the
    // later steps advance instead of waiting on a window that isn't coming.
    private bool _rowUnavailable;
    // One sample of a sell-list row's context menu per session, for naming
    // its entries in the log.
    private bool _loggedSellRowMenu;
    // Wall-clock deadline for getting this row's RetainerSell window open, stamped
    // by BeginRow. The TaskManager's own time limit aborts the ENTIRE queue on
    // expiry, which turns one bad row into a dead session, so each row gives up on
    // itself first. Covers only the two click steps; the market-board compare that
    // follows has its own, longer, retry budget.
    private long _rowDeadlineMs;
    private const long RowOpenTimeoutMs = 10000;

    // Held for the whole of every entry point. The task queue alone is idle
    // between batches, during framework-thread reads and while a plan is
    // awaited, and a second run started in that gap would cancel this one's
    // token and release AutoRetainer's suppression underneath it.
    private int _running;

    public bool IsBusy => Volatile.Read(ref _running) != 0 || _tasks.IsBusy;

    private bool TryEnterRun()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0) return true;
        _log.Warning("Pinch skipped: a run is already in progress");
        return false;
    }

    private void ExitRun() => Volatile.Write(ref _running, 0);

    // AR-mirror throttle (mirrors AutoRetainer's Utils.GenericThrottle /
    // Utils.RethrottleGeneric). Every UI helper checks `GenericThrottle`
    // before firing its action and calls `RethrottleGeneric()` when the
    // prerequisite addon isn't ready yet: that keeps the throttle fresh
    // during the wait so the click only fires N ms *after* the addon
    // becomes ready, never on the first-ready frame.
    //
    // Uses EzThrottler (time-based, milliseconds) rather than AR's FrameThrottler
    // because the per-item delay is configured in ms and users expect
    // wall-clock pacing: at higher in-game FPS (100+ fps is common) a
    // frame-based throttle of N frames elapses faster than N×16ms.
    private int DelayMs => _settings().PerItemDelayMs;
    private bool GenericThrottle => EzThrottler.Throttle(_throttleName, DelayMs);
    private void RethrottleGeneric() => EzThrottler.Throttle(_throttleName, DelayMs, true);

    // Wait task: return true only when the named addon is visible + ready.
    // Mirrors AR's TaskWaitSelectString.cs pattern. Inserted between steps
    // where the next click target is opened by the previous click's transition
    // (e.g. RetainerSellList appears ~100-200ms after ClickSellItems fires);
    // without this gate, a close-and-verify-gone helper running immediately
    // after the click sees "not visible yet" and returns true incorrectly.
    private unsafe bool? WaitForAddon(string name)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)
            && GenericHelpers.IsAddonReady(addon))
            return true;
        RethrottleGeneric();
        return false;
    }

    /// <summary>Whether a retainer's sell list is open and ready. Framework thread.</summary>
    public unsafe bool CanPinchNow()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
           && GenericHelpers.IsAddonReady(addon);

    // A fresh run: cancel whatever the last one left, link a new token, and
    // forget the last run's board answers and menu sample.
    private CancellationToken StartRun(CancellationToken ct)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _probe.ResetSession();
        _loggedSellRowMenu = false;
        return _cts.Token;
    }

    private static Task<List<RetainerMarketRow>> ReadPricedRows(IPluginLog log)
        => Svc.Framework.RunOnFrameworkThread(
            () => RetainerMarket.ReadLive(log).Where(r => r.Price > 0).ToList());

    private static Task<List<(string Name, bool Hq)>> ReadVisualRows()
        => Svc.Framework.RunOnFrameworkThread(RetainerWalk.ReadSellListRows);

    /// <summary>
    /// Reprice the retainer whose sell list the player has open, and leave the
    /// list closed. Returns null when there is no active retainer, the list is
    /// not open, or nothing on it is priced. A null plan result returns zero
    /// counts and leaves the list open. With <paramref name="dryRun"/> nothing
    /// is opened and the result carries <see cref="PinchRetainerResult.DryRun"/>.
    /// </summary>
    public async Task<PinchRetainerResult?> RunOpenAsync(PlanFn? plan, bool dryRun, CancellationToken ct)
    {
        if (!TryEnterRun()) return null;
        try { return await RunOpenCoreAsync(plan, dryRun, ct); }
        finally { ExitRun(); }
    }

    private async Task<PinchRetainerResult?> RunOpenCoreAsync(PlanFn? plan, bool dryRun, CancellationToken ct)
    {
        // Capture retainer identity on framework thread before any async work.
        (ulong retainerCid, string retainerName) = await Svc.Framework.RunOnFrameworkThread(RetainerWalk.ReadActiveRetainer);

        if (retainerCid == 0 || string.IsNullOrEmpty(retainerName))
        {
            _log.Warning("Pinch: no active retainer");
            return null;
        }

        if (!await Svc.Framework.RunOnFrameworkThread(CanPinchNow))
        {
            _log.Warning("Pinch: RetainerSellList not open");
            return null;
        }

        var token = StartRun(ct);
        _sessionOwnCids = await Svc.Framework.RunOnFrameworkThread(RetainerWalk.ReadActiveRetainerCids);

        // Read listings live from game memory (incl. items listed earlier this
        // session that AllaganTools hasn't re-scraped). RetainerSellList is open
        // here (CanPinchNow gate above), so the RetainerMarket container for the
        // active retainer is loaded.
        List<RetainerMarketRow> rows = await ReadPricedRows(_log);

        if (rows.Count == 0)
        {
            _log.Information("Pinch: no priced slots for retainer {Name}", retainerName);
            return null;
        }

        var retainer = new PinchRetainer(retainerCid, retainerName);
        RetainerPlan? p = plan is null ? AllLive(rows) : await plan(retainer, rows, token);
        if (p is null)
            return Empty(retainer, rows.Count);

        var visual = await ReadVisualRows();
        DryRunVisit? dry;
        try
        {
            EnqueuePinchTasks(p, rows, visual, closeOuterList: true, dryRun, out dry);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Pinch: exception building UI task queue; aborting");
            _tasks.Abort();
            EnqueueTeardown(true);
            return Empty(retainer, rows.Count);
        }

        // Report what the run did, not what it set out to do: the board path
        // decides its prices at the window, so they exist only once it drains.
        await DrainTasks();
        return Result(retainer, rows.Count, p, dry);
    }

    /// <summary>
    /// Reprice the retainer whose menu is already open, from the post-process
    /// window AutoRetainer opens for other plugins once it has finished with a
    /// retainer.
    ///
    /// AR fires that event with the retainer's <c>SelectString</c> menu up and
    /// its own scheduler parked on the finish call, so this steps one window in
    /// ("Sell Items") and back out, leaving the menu as it found it: AR clicks
    /// Quit itself immediately afterwards.
    ///
    /// Nothing suppresses AR here. Both <c>SchedulerMain.PluginEnabled</c> and
    /// <c>MultiMode.Active</c> are gated on <c>!Suppressed</c>, so setting it
    /// mid-cycle would switch multi mode off underneath the rotation; the
    /// post-process lock is already the mutex.
    ///
    /// Returns null when a run is already in progress, or the sell list never
    /// opened or held nothing priced. The caller owns any per-retainer interval.
    /// </summary>
    public async Task<PinchRetainerResult?> RunForOpenRetainerAsync(PlanFn? plan, CancellationToken ct)
    {
        if (!TryEnterRun()) return null;
        try { return await RunForOpenRetainerCoreAsync(plan, ct); }
        finally { ExitRun(); }
    }

    private async Task<PinchRetainerResult?> RunForOpenRetainerCoreAsync(PlanFn? plan, CancellationToken ct)
    {
        (ulong retainerCid, string retainerName) = await Svc.Framework.RunOnFrameworkThread(RetainerWalk.ReadActiveRetainer);
        if (retainerCid == 0 || string.IsNullOrEmpty(retainerName))
            return null;

        var token = StartRun(ct);
        _sessionOwnCids = await Svc.Framework.RunOnFrameworkThread(RetainerWalk.ReadActiveRetainerCids);

        if (!await Svc.Framework.RunOnFrameworkThread(CanPinchNow))
        {
            _tasks.Enqueue(ClickSellItems);
            _tasks.Enqueue(() => WaitForAddon("RetainerSellList"));
            await DrainTasks();
            if (!await Svc.Framework.RunOnFrameworkThread(CanPinchNow))
            {
                _log.Warning("Pinch: {Name} sell list never opened; leaving the retainer alone", retainerName);
                // A list that opened just as the wait gave up would sit on
                // top of the menu AR is about to click Quit on.
                _tasks.Abort();
                _tasks.Enqueue(CloseRetainerSellList);
                await DrainTasks();
                return null;
            }
        }

        var rows = await ReadPricedRows(_log);
        if (rows.Count == 0)
        {
            _log.Information("Pinch: no priced slots for retainer {Name}", retainerName);
            _tasks.Enqueue(CloseRetainerSellList);
            await DrainTasks();
            return null;
        }

        var retainer = new PinchRetainer(retainerCid, retainerName);
        RetainerPlan? p = plan is null ? AllLive(rows) : await plan(retainer, rows, token);
        if (p is null)
        {
            // The list must not stay on top of the menu AR clicks Quit on.
            _tasks.Enqueue(CloseRetainerSellList);
            await DrainTasks();
            return Empty(retainer, rows.Count);
        }

        var visual = await ReadVisualRows();
        EnqueuePinchTasks(p, rows, visual, closeOuterList: true, dryRun: false, out _);
        await DrainTasks();
        return Result(retainer, rows.Count, p, null);
    }

    /// <summary>
    /// Reprice every retainer in the roster, from the open <c>RetainerList</c>.
    /// Returns null when no session ran (already busy, or the list is not open),
    /// so the caller reports nothing for it. An exception inside the session is
    /// logged and the counts so far are returned.
    /// </summary>
    public async Task<PinchSessionResult?> RunAllAsync(PlanFn? plan, CancellationToken ct)
    {
        if (!TryEnterRun()) return null;
        try { return await RunAllCoreAsync(plan, ct); }
        finally { ExitRun(); }
    }

    private async Task<PinchSessionResult?> RunAllCoreAsync(PlanFn? plan, CancellationToken ct)
    {
        if (!await Svc.Framework.RunOnFrameworkThread(RetainerWalk.IsRetainerListReady))
        {
            _log.Warning("Pinch session skipped: RetainerList not open");
            return null;
        }

        int retainers = 0, reprices = 0, skipped = 0, noData = 0, rowsAttempted = 0;
        var token = StartRun(ct);

        bool wasCancelled = false;
        try
        {
            AutoRetainerSuppress.Set(true);
            TalkSkipper.Register();

            // Snapshot retainer roster from RetainerManager by sorted index so
            // each entry carries CID, name, and market-listing count in one
            // framework-thread pass. MarketItemCount is the number of items the
            // retainer currently has up for sale, readable without opening the
            // retainer, so we can skip empties (no UI round-trip) below.
            var snapshot = await Svc.Framework.RunOnFrameworkThread(RetainerWalk.ReadRoster);

            _sessionOwnCids = snapshot.Select(s => s.Cid).ToHashSet();

            foreach (var (cid, name, sortedIdx, marketCount) in snapshot)
            {
                if (token.IsCancellationRequested) break;

                // Leaving the bell by hand (Esc out and walk off) is not a
                // cancel, so without this every remaining retainer would spend
                // its 15s step budget clicking at a list that is gone.
                if (!await Svc.Framework.RunOnFrameworkThread(RetainerWalk.IsRetainerListReady))
                {
                    _log.Information("Pinch session: the retainer list is gone; stopping");
                    KitServices.Chat.Print($"{KitServices.LogPrefix} Pinch stopped: you left the retainer list.");
                    break;
                }

                // Skip retainers with nothing listed without opening them: the
                // roster already told us MarketItemCount, so an empty retainer
                // costs no UI round-trip (saves ~3-4s each).
                if (marketCount <= 0)
                {
                    KitServices.Chat.Print($"{KitServices.LogPrefix} Skip {name}: no listings");
                    continue;
                }

                // Phase 1: open this retainer's sell list so the game loads its
                // RetainerMarket container. Listings are read live (phase 2)
                // rather than from a cache, which can miss items listed earlier
                // in the same session.
                // Each helper self-throttles via GenericThrottle and retries
                // (RethrottleGeneric + return false) until its prerequisite addon
                // is ready, mirroring AutoRetainer's pipeline (RetainerHandlers.cs).
                _tasks.Enqueue(() => OpenRetainerRow(sortedIdx));
                _tasks.Enqueue(ClickSellItems);
                // Gate: wait until RetainerSellList actually opens (~100-200ms
                // after the SelectString click) before reading the container.
                _tasks.Enqueue(() => WaitForAddon("RetainerSellList"));
                await DrainTasks();
                if (token.IsCancellationRequested) break;

                // Phase 2: read listings live from game memory (incl. just-added
                // items) now that this retainer's RetainerSellList is open.
                var rows = await ReadPricedRows(_log);
                if (rows.Count == 0)
                {
                    KitServices.Chat.Print($"{KitServices.LogPrefix} Skip {name}: no listings");
                    EnqueueLeaveRetainer();
                    await DrainTasks();
                    continue;
                }

                var retainer = new PinchRetainer(cid, name);
                RetainerPlan? p = plan is null ? AllLive(rows) : await plan(retainer, rows, token);
                if (p is null)
                {
                    // Back out to the list, or the next retainer waits its whole
                    // step budget for a RetainerList that never returns.
                    EnqueueLeaveRetainer();
                    await DrainTasks();
                    continue;
                }

                // Phase 3: reprice changed items, then close back to RetainerList.
                var visual = await ReadVisualRows();
                EnqueuePinchTasks(p, rows, visual, closeOuterList: false, dryRun: false, out _);
                EnqueueLeaveRetainer();
                await DrainTasks();
                var result = Result(retainer, rows.Count, p, null);

                retainers++;
                reprices += result.Reprices;
                skipped += result.Skipped;
                noData += result.NoData;
                rowsAttempted += rows.Count;
            }

            _tasks.Enqueue(() => { TalkSkipper.Unregister(); return (bool?)true; });
            _tasks.Enqueue(() => { AutoRetainerSuppress.Set(false); return (bool?)true; });
            await DrainTasks();
            wasCancelled = token.IsCancellationRequested;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Pinch session failed");
            wasCancelled = token.IsCancellationRequested;
        }
        finally
        {
            // Cleanup direct (not via _tasks) because Abort() would discard
            // enqueued cleanup. Both helpers are idempotent.
            TalkSkipper.Unregister();
            AutoRetainerSuppress.Set(false);
        }

        return new PinchSessionResult(retainers, reprices, skipped, noData, rowsAttempted, wasCancelled);
    }

    private static PinchRetainerResult Empty(PinchRetainer r, int rows)
        => new(r.Cid, r.Name, rows, 0, 0, 0, Array.Empty<PinchWrite>(), null);

    // Whatever the board path wrote. Call once the queue has drained.
    private PinchRetainerResult Result(PinchRetainer r, int rows, RetainerPlan plan, DryRunVisit? dry)
    {
        var writes = new List<PinchWrite>();
        TakeLiveWrites(writes);
        return new PinchRetainerResult(r.Cid, r.Name, rows, writes.Count, plan.Skipped, plan.NoData, writes, dry);
    }

    public void AbortAll()
    {
        _cts?.Cancel();
        _tasks.Abort();
        // Raw, throttle-bypassing closes: the AR-mirror Close* task helpers
        // gate Close() behind GenericThrottle so the pipeline doesn't race
        // the UI, but abort must fire immediately on the next tick.
        Svc.Framework.RunOnTick(() =>
        {
            unsafe
            {
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var s) && s->IsVisible)
                    s->Close(true);
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var c) && c->IsVisible)
                    c->Close(true);
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var l) && l->IsVisible)
                    l->Close(true);
            }
        });
        TalkSkipper.Unregister();
        AutoRetainerSuppress.Set(false);
    }

    // Await the currently-enqueued task batch (one per-retainer phase) before
    // proceeding. Lets a session interleave async work (live container read,
    // the plan) between UI phases instead of building the whole queue
    // up front. Returns when the queue drains or the session is cancelled.
    private async Task DrainTasks()
    {
        while (_tasks.IsBusy && !(_cts?.Token.IsCancellationRequested ?? true))
            await Task.Delay(250, CancellationToken.None);
    }

    private bool? OpenRetainerRow(int sortedIdx)
        => RetainerWalk.OpenRetainerRow(sortedIdx, () => GenericThrottle, RethrottleGeneric);

    private bool? ClickSellItems()
        => RetainerWalk.ClickSellItems(() => GenericThrottle, RethrottleGeneric);

    /// <summary>
    /// Say what happened to one listing, in one line: which item, where the
    /// price went, and what decided it. The interesting part of a run is which
    /// items moved and why, and that is exactly what the game's own per-change
    /// line leaves out.
    /// </summary>
    private void Announce(string item, bool hq, long? from, long to, string why)
    {
        if (!_settings().AnnounceEachReprice) return;
        string tag = hq ? " (HQ)" : string.Empty;
        string move = from is null || from == to
            ? $"{to:N0}"
            : $"{from:N0} -> {to:N0}";
        KitServices.Chat.Print($"{KitServices.LogPrefix} {item}{tag}  {move}  ({why})");
    }

    /// <summary>
    /// Every resolvable row becomes a live board lookup. An item whose name
    /// cannot be resolved is counted as skipped. No cap on lookups.
    /// </summary>
    public RetainerPlan AllLive(IReadOnlyList<RetainerMarketRow> rows)
    {
        var plan = new RetainerPlan();
        foreach (var row in rows)
        {
            // Resolve the name up front so every skip/keep log line identifies
            // the item (the per-row window log can't say *why* it had no target).
            string name = NormalizeItemName(ResolveItemName(row.ItemId));
            string label = name.Length != 0 ? name : $"item {row.ItemId}";

            if (name.Length == 0)
            {
                plan.Skipped++;
                _log.Warning("Pinch: skip item {Id} — could not resolve item name", row.ItemId);
                continue;
            }
            plan.Live[(name, row.Hq)] = row.ItemId;
            if (LiveRowNeedsVisit(row, label)) plan.LiveRows++;
        }
        return plan;
    }

    private void EnqueuePinchTasks(
        RetainerPlan plan,
        IReadOnlyList<RetainerMarketRow> rows,
        IReadOnlyList<(string, bool)> visual,
        bool closeOuterList,
        bool dryRun,
        out DryRunVisit? dry)
    {
        dry = null;
        lock (_liveWrites) _liveWrites.Clear();

        // Early-exit budget: number of ROWS still expected to need a window
        // opened (live-lookup rows). Once that many have been resolved (a real
        // write, or a row given up on), the loop skips remaining rows without
        // opening a window. A no-op same-value reprice doesn't count, so a
        // duplicate stack already at its live-decided price can't consume
        // budget meant for another stack.
        _rowsRemaining = plan.LiveRows;

        // Visit every row the plan claims once; ApplyRepriceFromWindow reads the
        // item the window actually opened and runs a live market-board compare,
        // or cancels.
        if (plan.Live.Count > 0)
        {
            // The list names its own rows in AtkValues, so the rows that need
            // nothing can be left shut. Opening a row only to learn its item
            // costs a window per row, since container order is not draw order:
            // two reprices on a twenty-listing retainer would open twenty windows.
            //
            // The price window still decides what gets written
            // (ApplyRepriceFromWindow matches on the item it actually opened),
            // so this only narrows which rows are visited; it never becomes the
            // thing that picks a price.
            if (visual.Count == 0)
            {
                // Could not read the list: visit every row. Slow is
                // the acceptable failure here; skipping a row that needed a
                // reprice is not.
                _log.Warning("Pinch: could not read the sell list; visiting every row");
            }
            var visit = PinchPlanning.RowsToVisit(visual, rows.Count, plan.Live);
            if (visual.Count > 0)
                _log.Information("Pinch: {Visit} of {Rows} row(s) need opening", visit.Count, visual.Count);

            if (dryRun)
            {
                // The narrowing is decided before anything is opened, so a dry
                // run can answer it without walking the UI at all: that is
                // the point, checking it should not cost a whole cycle.
                dry = new DryRunVisit(visit.Count, visual.Count == 0 ? rows.Count : visual.Count);
                EnqueueTeardown(closeOuterList);
                return;
            }

            foreach (int rowIndex in visit)
            {
                int captured = rowIndex;
                _tasks.Enqueue(BeginRow);
                _tasks.Enqueue(() => OpenItemContextMenu(captured));
                _tasks.Enqueue(ClickAdjustPrice);
                _tasks.Enqueue(() => ApplyRepriceFromWindow(plan));
            }
        }

        EnqueueTeardown(closeOuterList);
    }

    private void EnqueueTeardown(bool closeOuterList = true)
    {
        _tasks.Enqueue(CloseItemSearchResult);
        _tasks.Enqueue(CloseRetainerSell);
        _tasks.Enqueue(CloseContextMenu);
        if (closeOuterList) _tasks.Enqueue(CloseRetainerSellList);
    }

    private bool? CloseItemSearchResult() => RetainerWalk.CloseItemSearchResult(() => GenericThrottle);
    private bool? CloseRetainerSell() => RetainerWalk.CloseRetainerSell(() => GenericThrottle);
    private bool? CloseContextMenu() => RetainerWalk.CloseContextMenu(() => GenericThrottle);
    private bool? CloseRetainerSellList() => RetainerWalk.CloseRetainerSellList(() => GenericThrottle);

    private bool? CloseSelectStringBack()
        => RetainerWalk.CloseSelectStringBack(() => GenericThrottle, RethrottleGeneric);

    /// <summary>
    /// Close back out to the retainer list: shut the sell window, click Quit on
    /// the menu, then wait for <c>RetainerList</c> to come back.
    ///
    /// Clicking Quit only starts the exit; the list reappears a second or two
    /// later. The wait keeps the caller from sampling a UI mid-transition and
    /// reading it as the player having walked away from the bell. A timeout is
    /// deliberately not an abort: the caller's own check decides what it means.
    /// </summary>
    private void EnqueueLeaveRetainer()
    {
        _tasks.Enqueue(CloseRetainerSellList);
        _tasks.Enqueue(CloseSelectStringBack);
        _tasks.Enqueue(() => WaitForAddon("RetainerList"), RetainerListReturnMs, false);
    }

    /// <summary>How long the game may take to put <c>RetainerList</c> back after Quit.</summary>
    private const int RetainerListReturnMs = 10000;

    // --- Per-row UI helpers (mirrored from Dagobert/AutoPinch.cs) ---

    // Starts a row: clears the previous row's verdict and stamps its deadline.
    private bool? BeginRow()
    {
        _rowUnavailable = false;
        _rowDeadlineMs = Environment.TickCount64 + RowOpenTimeoutMs;
        return true;
    }

    // Give up on the current row instead of letting the step time out and take
    // the whole queue with it. Returns the terminal value for the calling step.
    private bool? SkipRow(string reason)
    {
        _log.Warning("Pinch: {Reason}; skipping row", reason);
        _rowUnavailable = true;
        if (_rowsRemaining != int.MaxValue && _rowsRemaining > 0) _rowsRemaining--;
        return true;
    }

    // Click slot in RetainerSellList → opens ContextMenu.
    //
    // Fire-until-observed pattern: the game can silently drop our callback
    // when RetainerSellList is in a transitional state (e.g. right after a
    // RetainerSell popup closed from the previous item's SetNewPrice). In
    // that case the click vanishes and ContextMenu never opens, so the next
    // ClickAdjustPrice burns its 15s budget for nothing. We keep firing the
    // click on every throttle window until ContextMenu actually shows up.
    private unsafe bool? OpenItemContextMenu(int slot)
    {
        // Early-exit: all expected reprices done, don't open this row's window.
        // The matching ClickAdjustPrice/ApplyRepriceFromWindow steps see no open
        // ContextMenu/RetainerSell and likewise short-circuit (see _rowsRemaining
        // checks there). Saves ~0.6s per already-at-target row.
        if (_rowsRemaining <= 0)
            return true;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var cm) && cm->IsVisible)
            return true;
        if (Environment.TickCount64 >= _rowDeadlineMs)
            return SkipRow($"no context menu for row {slot}");
        // Prereq: RetainerSellList must be ready to receive the slot click.
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            RethrottleGeneric();
            return false;
        }
        if (!GenericThrottle) return false;
        Callback.Fire(addon, true, 0, slot, 1);
        // Throttle prevents re-spamming faster than DelayMs even if the game stalls.
        return false;
    }

    // Click "Adjust Price" on ContextMenu → opens RetainerSell popup.
    //
    // Fire-until-observed: same rationale as OpenItemContextMenu. Done when
    // RetainerSell is visible (price-edit popup is up) OR when ContextMenu
    // is gone but it was a mannequin (we close-and-skip).
    private unsafe bool? ClickAdjustPrice()
    {
        if (_rowUnavailable) return true;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var rs) && rs->IsVisible)
            return true;
        // Early-exit: budget spent and OpenItemContextMenu skipped this row, so
        // no ContextMenu will appear, don't wait for one.
        if (_rowsRemaining <= 0
            && !(GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var cmOpen) && cmOpen->IsVisible))
            return true;
        if (Environment.TickCount64 >= _rowDeadlineMs)
        {
            // Leave nothing open for the next row to trip over.
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var stale) && stale->IsVisible)
                stale->Close(true);
            return SkipRow("price window never opened");
        }
        // Prereq: ContextMenu must be the active addon.
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            RethrottleGeneric();
            return false;
        }
        var reader = new ReaderContextMenu(addon);
        int adjustIdx = reader.Entries.FindIndex(e =>
            e.Name.Equals("adjust price", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("preis ändern", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("価格を変更する", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("changer le prix", StringComparison.CurrentCultureIgnoreCase));
        bool hasAdjust = adjustIdx >= 0;

        if (!hasAdjust)
        {
            if (!GenericThrottle) return false;
            addon->Close(true);
            return SkipRow("context menu has no 'Adjust Price' entry (mannequin)");
        }
        // Log this menu's entries once per session: entry wording varies with
        // client language and can only be read off what the client shows,
        // the way the four spellings of "adjust price" above were arrived at.
        if (!_loggedSellRowMenu)
        {
            _loggedSellRowMenu = true;
            _log.Information("Retainer sell-list row menu: {Entries}",
                string.Join(" | ", reader.Entries.Select(e => e.Name)));
        }
        if (!GenericThrottle) return false;
        // Fire the entry matched by name: the next one down is "Return to
        // Retainer", which a shifted menu would click instead.
        Callback.Fire(addon, true, 0, adjustIdx, 0, 0, 0);
        return false;
    }

    // Reprice the item the RetainerSell window has ACTUALLY opened, looked up by
    // its name+hq. We never trust the row index to tell us which item this is
    // (the list is category-sorted), so reading the window and matching by name
    // is what makes this misprice-proof. Resolution order:
    //   1. routed to a live lookup -> compare sub-machine
    //   2. otherwise               -> cancel, leave unchanged
    private unsafe bool? ApplyRepriceFromWindow(RetainerPlan plan)
    {
        // The row had no "Adjust Price" (mannequin): no window will ever open.
        if (_rowUnavailable) return true;

        if (!GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon)
            || !GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
        {
            // Early-exit: budget spent and the prior steps skipped this row, so
            // no RetainerSell window will open, nothing to do.
            if (_rowsRemaining <= 0) return true;
            RethrottleGeneric();
            return false;
        }

        string raw = addon->ItemName->NodeText.GetText();
        bool hq = HasHqGlyph(raw);
        string name = NormalizeItemName(raw);
        var key = (name, hq);

        // 1. Routed to a live market-board compare.
        if (name.Length != 0 && plan.Live.TryGetValue(key, out uint liveItemId))
        {
            // This stack's own asking price, not a sibling stack's.
            uint curPrice = (uint)addon->AskingPrice->Value;
            // The budget is spent in ApplyCompareResult, on a real write only.
            // A finished compare that changed nothing must not consume it, or a
            // row that does need a change is short-circuited before it is
            // reached: the sell list is in the game's category order, so there
            // is no saying which rows come first.
            return LiveCompareStep(addon, name, hq, curPrice, liveItemId);
        }

        // 2. Nothing to do: cancel out without touching the price.
        if (!GenericThrottle) return false;
        _log.Debug("Pinch: no target for window item '{Item}' (hq={Hq}); leaving unchanged", name, hq);
        Callback.Fire(&addon->AtkUnitBase, true, 1); // cancel
        return true;
    }

    /// <summary>
    /// One tick of the live lookup for the open window. Returns false while a
    /// board request is in flight, true once the price has been written or the
    /// item left alone. Policy stays here: the probe only reports the board.
    /// </summary>
    private unsafe bool? LiveCompareStep(
        AddonRetainerSell* addon, string name, bool hq, uint curPrice, uint itemId)
    {
        _liveItemId = itemId;
        if (!_probe.Step(&addon->AtkUnitBase, itemId, hq, name, maxPerSession: 0,
                         _settings().MarketBoardDelayMs, out var price))
            return false;

        ApplyCompareResult(addon, name, hq, curPrice, price);
        return true;
    }

    /// <summary>
    /// Whether a live-priced row still has to have its window opened.
    ///
    /// The board is asked once per item per session, so the second retainer
    /// holding a stack already knows the answer; <see cref="PinchPlanning.NeedsVisit"/>
    /// feeds it and the row's current price to the same decision the write
    /// path uses. An item the board has not been asked about yet has to be
    /// visited, because opening the window is how it gets asked.
    ///
    /// This only sets the size of the early-exit budget, which is spent on
    /// whichever rows turn out to need work: the visual order of the sell list
    /// is the game's, not ours, so nothing here decides which row is skipped.
    /// </summary>
    public bool LiveRowNeedsVisit(RetainerMarketRow row, string label)
    {
        if (!_probe.TryGetCached(row.ItemId, row.Hq, out LivePrice cached))
            return true;

        if (PinchPlanning.NeedsVisit(cached, row.Price, _profitFloor(row.ItemId), _settings().SkipIfNoCompetitor))
            return true;

        _log.Debug("Pinch: {Item} (hq={Hq}) — already at {Price:N0} from this run's board read; no window needed",
            label, row.Hq, row.Price);
        return false;
    }

    // Write the decided price into the open sell window, or cancel out of it.
    private unsafe void ApplyCompareResult(
        AddonRetainerSell* addon, string name, bool hq, uint curPrice, LivePrice result)
    {
        int floor = _profitFloor(_liveItemId);
        PriceDecision d = PinchDecision.Decide(result, curPrice, floor, _settings().SkipIfNoCompetitor);

        if (d.Write is null)
        {
            _log.Information("Pinch: {Item} (hq={Hq}) — {Why}; holding at {Price:N0}",
                name, hq, d.Why, curPrice);
            if (d.Announce) Announce(name, hq, null, curPrice, d.Why);
            Callback.Fire(&addon->AtkUnitBase, true, 1); // cancel (no change)
            return;
        }

        uint target = d.Write.Value;
        _log.Information("Pinch: {Item} (hq={Hq}) {Old} -> {New} ({Why})",
            name, hq, curPrice, target, d.Why);
        Announce(name, hq, curPrice, target, d.Why);
        RecordLiveWrite(hq, curPrice, target);
        addon->AskingPrice->SetValue((int)target);
        Callback.Fire(&addon->AtkUnitBase, true, 0); // confirm
        if (_rowsRemaining != int.MaxValue) _rowsRemaining--;
    }

    /// <summary>Item display name from the Item sheet; empty on failure.</summary>
    public static string ResolveItemName(uint itemId)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            return sheet?.GetRow(itemId).Name.ToDalamudString().GetText() ?? "";
        }
        catch { return ""; }
    }

    // The sell window appends the HQ glyph (U+E03C) to the item name.
    private static bool HasHqGlyph(string s) => RetainerWalk.HasHqGlyph(s);
    private static string NormalizeItemName(string s) => RetainerWalk.NormalizeItemName(s);

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* best effort */ }
        try { _cts?.Dispose(); } catch { /* best effort */ }
        _cts = null;
        try { _tasks.Abort(); } catch { /* best effort */ }
        TalkSkipper.Unregister();
        AutoRetainerSuppress.Set(false);
    }
}
