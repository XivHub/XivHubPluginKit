using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Throttlers;

namespace XivHubPluginKit.Board;

public enum BoardSearchResult { Opened, NoListings, Failed }

/// <summary>How a search ended; <see cref="Reason"/> is set for <see cref="BoardSearchResult.Failed"/>.</summary>
public sealed record BoardSearchOutcome(uint ItemId, BoardSearchResult Result, string? Reason);

/// <summary>
/// Opens one item's listings on the market board by typing its exact name into the board's
/// search and picking the result by item id, with the callbacks the owner's UiCapture recordings
/// show (market-board.md "Search and buy flow"; sessions 37c13073e8c8 and 2590cacb35c2):
/// <list type="number">
/// <item>close a results window left open: <c>ItemSearchResult [-1]</c>;</item>
/// <item>type the name: <c>ItemSearch [9, _, _, name, name, _, _, _]</c>, eight values whose
/// <c>Undefined</c> slots are sent as <see cref="Callback.ZeroAtkValue"/>;</item>
/// <item>wait for the search page to reload, then find the item's index in
/// <c>AgentItemSearch.ListingPageItemIds</c>. The page is read only after it was seen reloading,
/// so the previous search's ids are never taken as this one's;</item>
/// <item>open it: <c>ItemSearch [5, i]</c>, i the index in those ids;</item>
/// <item>check <c>AgentItemSearch.ResultItemId</c> is the item, then wait for the listings to
/// settle through <see cref="ListingsGate"/>.</item>
/// </list>
/// The search never runs with the board closed: <see cref="Start"/> refuses without a ready
/// <c>ItemSearch</c> window, and the watchdog stops a run the moment it closes. Steps run on
/// an ECommons task manager: a stamp task sets a step's deadline, each step returns false to wait,
/// a failure skips the remaining steps, and the finish task or <see cref="Stop"/> publishes the
/// <see cref="Outcome"/>. Every outcome is written to the devlog as a <c>boardsearch</c> record,
/// since a board with no listings for the item is unrecorded.
/// Fires only ItemSearch [7], [9], [5, i] and ItemSearchResult [-1]; never a listing pick or a
/// purchase prompt.
/// Framework thread only: <see cref="Start"/> and <see cref="Stop"/> from window draws, the steps
/// and the watchdog from <c>IFramework.Update</c>. The watchdog is subscribed to the injected
/// <see cref="IFramework"/> once, in the constructor, and released only by <see cref="Dispose"/>;
/// between runs it returns at once, so a finished, stopped or failed run needs no unsubscribe.
/// </summary>
public sealed unsafe class BoardSearch : IDisposable
{
    private const string ThrottleName = "KitBoardSearch";
    private const int CloseResultsTimeoutMs = 5000;
    // The page read as searching within one 250 ms sample of [9] and loaded 1.0 to 1.5 s after it
    // (37c13073e8c8 seq 34, 36, 41; 108ba2790349 seq 96, 99, 113). A category list on the page is
    // cleared by [7, -1, 0] first (108ba2790349 seq 65, 83, 87, 113).
    private const int SearchStartTimeoutMs = 3000;
    private const int SearchFinishTimeoutMs = 10_000;
    private const int ResultOpenTimeoutMs = 5000;

    private readonly TaskManager tasks = new(new TaskManagerConfiguration(timeLimitMS: 30000, abortOnTimeout: true));
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<int> stepDelayMs;
    private readonly Func<uint, string> nameOf;
    private readonly Func<DevTelemetry?> telemetry;

    private string name = string.Empty;
    private string? failure;
    private long deadlineMs;
    private bool closeFired;
    private long searchFiredMs;
    private bool searchStarted;
    private int index = -1;
    private byte? requestBefore;
    private ListingsGate? gate;
    private BoardSnapshot? lastRead;
    private bool disposed;

    /// <summary>True from <see cref="Start"/> until the run finishes.</summary>
    public bool Busy { get; private set; }

    /// <summary>The item of the current or last search.</summary>
    public uint ItemId { get; private set; }

    /// <summary>How the last search ended; null while one runs and before the first.</summary>
    public BoardSearchOutcome? Outcome { get; private set; }

    /// <param name="framework">The consuming plugin's own <see cref="IFramework"/>; the watchdog
    /// runs on its <c>Update</c>.</param>
    /// <param name="log">Where a step's exception is logged.</param>
    /// <param name="stepDelayMs">Minimum gap between clicks, read on every click.</param>
    /// <param name="nameOf">The item's name as the board's search matches it; empty when unknown.</param>
    /// <param name="telemetry">The devlog, read on every use; null when there is none.</param>
    public BoardSearch(IFramework framework, IPluginLog log, Func<int> stepDelayMs, Func<uint, string> nameOf,
        Func<DevTelemetry?> telemetry)
    {
        this.framework = framework;
        this.log = log;
        this.stepDelayMs = stepDelayMs;
        this.nameOf = nameOf;
        this.telemetry = telemetry;
        this.framework.Update += OnUpdate;
    }

    /// <summary>
    /// Why a search can't start right now, or null. Whether another automation is running is the
    /// caller's check.
    /// </summary>
    public string? Guard()
    {
        if (!BoardReader.SearchOpen()) return "Open the market board first.";
        if (BoardReader.BlockingWindow() is { } window) return $"Close the board's {window} window first.";
        return null;
    }

    /// <summary>
    /// Searches the board for <paramref name="itemId"/> and opens its listings. False, and nothing
    /// happens, while a search runs, after dispose, or while <see cref="Guard"/> gives a reason.
    /// </summary>
    public bool Start(uint itemId)
    {
        if (Busy || disposed || Guard() != null) return false;

        Outcome = null;
        ItemId = itemId;
        name = nameOf(itemId);
        failure = null;
        closeFired = false;
        searchFiredMs = 0;
        searchStarted = false;
        index = -1;
        requestBefore = null;
        gate = null;
        lastRead = null;
        Busy = true;
        telemetry()?.Log($"boardsearch: start {name} ({itemId})");

        tasks.Enqueue(() => Stamp(CloseResultsTimeoutMs), "Board search: stamp");
        tasks.Enqueue(() => Guarded(CloseResults), "Board search: close the results window");
        tasks.Enqueue(() => Guarded(ClearSearch), "Board search: clear the search page");
        tasks.Enqueue(() => Guarded(TypeSearch), $"Board search: type {name}");
        tasks.Enqueue(() => Guarded(WaitSearchPage), "Board search: wait for the search page");
        tasks.Enqueue(() => Guarded(OpenResult), $"Board search: open {name}");
        tasks.Enqueue(() => Stamp(ResultOpenTimeoutMs), "Board search: stamp");
        tasks.Enqueue(() => Guarded(WaitResultOpen), $"Board search: wait for the results of {name}");
        tasks.Enqueue(() => Guarded(WaitListings), $"Board search: wait for the listings of {name}");
        tasks.Enqueue(() => { Finish(abortQueue: false); return true; }, "Board search: finish");
        return true;
    }

    /// <summary>Ends a search now and drops its queued steps, recording <paramref name="reason"/> as
    /// its failure. Closes no window. No-op when idle.</summary>
    public void Stop(string reason = "stopped by you")
    {
        if (!Busy) return;
        failure ??= reason;
        Finish(abortQueue: true);
    }

    public void Dispose()
    {
        disposed = true;
        try
        {
            Stop();
        }
        finally
        {
            framework.Update -= OnUpdate;
            tasks.Dispose();
        }
    }

    /// <summary>
    /// Closes a results window left open, so the <c>ResultItemId</c> checked later is the one the
    /// new result sets. A window still opening is let finish first; [-1] fires once.
    /// </summary>
    private bool? CloseResults()
    {
        if (!BoardReader.WindowVisible(BoardReader.ResultAddon)) return true;
        if (Expired)
        {
            Fail("The board's results window didn't close.");
            return true;
        }

        var results = BoardReader.ReadyAddon(BoardReader.ResultAddon);
        if (closeFired || results == null)
        {
            Rethrottle();
            return false;
        }
        if (!Throttle()) return false;

        // Recorded ItemSearchResult [-1] (37c13073e8c8 seq 114, 108ba2790349 seq 77).
        // SAFETY: results is the non-null, ready ItemSearchResult unit read this frame, passed
        // straight to the game's AtkUnitBase.FireCallback.
        Callback.Fire(results, true, -1);
        closeFired = true;
        return false;
    }

    /// <summary>
    /// Clears the search page the way the game does before every typed search: every recorded
    /// typed search sends ItemSearch [7, -1, 0] first (37c13073e8c8 seq 26 and 118, 108ba2790349
    /// seq 83, 2590cacb35c2 seq 23 and 52), and that call is what sets ListingPageLoaded false and
    /// the page count to 0 (37c13073e8c8 seq 121, 108ba2790349 seq 87).
    /// </summary>
    private bool? ClearSearch()
    {
        // Checked before the clear, so a search that can't run leaves the player's page alone.
        if (name.Length == 0)
        {
            Fail($"Item {ItemId} has no name to search for.");
            return true;
        }
        if (BoardReader.Read() is null)
        {
            Fail("The board's data isn't readable.");
            return true;
        }
        if (!Throttle()) return false;

        var search = BoardReader.ReadyAddon(BoardReader.SearchAddon);
        if (search == null)
        {
            Fail("The market board closed.");
            return true;
        }

        // Recorded as Int 7, Int -1, Int 0 and five Undefined holding leftover stack bytes.
        // SAFETY: search is the non-null, ready ItemSearch unit read this frame; ECommons allocates
        // the eight AtkValues and frees them after the call.
        Callback.Fire(search, true, 7, -1, 0, Callback.ZeroAtkValue, Callback.ZeroAtkValue,
            Callback.ZeroAtkValue, Callback.ZeroAtkValue, Callback.ZeroAtkValue);
        return true;
    }

    /// <summary>
    /// Notes the InfoProxy's request id, which the listings gate needs to change, and types the
    /// item's name into the search window.
    /// </summary>
    private bool? TypeSearch()
    {
        if (!Throttle()) return false;

        if (BoardReader.Read() is not { } read)
        {
            Fail("The board's data isn't readable.");
            return true;
        }
        var search = BoardReader.ReadyAddon(BoardReader.SearchAddon);
        if (search == null)
        {
            Fail("The market board closed.");
            return true;
        }

        lastRead = read;
        requestBefore = read.RequestId;
        // Recorded ItemSearch [9, …] (37c13073e8c8 seq 34 and 128, 108ba2790349 seq 96): Int 9, two
        // Undefined, the typed text twice, three Undefined. The recorded Undefined slots hold
        // leftover stack bytes; ZeroAtkValue is Undefined with value 0.
        // SAFETY: search is the non-null, ready ItemSearch unit read this frame; ECommons allocates
        // the eight AtkValues and the name's null-terminated copy, and frees them after the call.
        Callback.Fire(search, true, 9, Callback.ZeroAtkValue, Callback.ZeroAtkValue, name, name,
            Callback.ZeroAtkValue, Callback.ZeroAtkValue, Callback.ZeroAtkValue);
        searchFiredMs = Environment.TickCount64;
        return true;
    }

    /// <summary>
    /// Waits for the search page to reload and finds the item among its results. The search counts
    /// as started once the page reads searching, and its ids are taken only once it reads loaded
    /// and not searching after that (37c13073e8c8 seq 36 and 41, 130 and 139).
    /// </summary>
    private bool? WaitSearchPage()
    {
        if (BoardReader.ReadSearchPage() is not { } page)
        {
            Fail("The board's data isn't readable.");
            return true;
        }

        // [7, -1, 0] has already left the page not loaded (37c13073e8c8 seq 121), so only
        // IsPartialSearching, true within one sample of every recorded [9] (seq 36, 130;
        // 108ba2790349 seq 99), shows that [9] took.
        if (page.Searching) searchStarted = true;
        if (searchStarted && page.Loaded && !page.Searching)
        {
            index = IndexOf(page.ItemIds, ItemId);
            if (index < 0) Fail($"{name} isn't in the board's search results.");
            return true;
        }

        var elapsed = Environment.TickCount64 - searchFiredMs;
        if (!searchStarted && elapsed >= SearchStartTimeoutMs)
        {
            Fail($"The board didn't start a search for {name}.");
            return true;
        }
        if (elapsed >= SearchFinishTimeoutMs)
        {
            Fail($"The board's search for {name} didn't finish.");
            return true;
        }

        Rethrottle();
        return false;
    }

    /// <summary>
    /// Opens the item's result. The page is re-read in the same frame as the click, so a list
    /// that changed since the lookup never gets a click meant for another item.
    /// </summary>
    private bool? OpenResult()
    {
        if (!Throttle()) return false;

        var page = BoardReader.ReadSearchPage();
        if (page is not { Loaded: true, Searching: false } || index < 0 || index >= page.ItemIds.Count
            || page.ItemIds[index] != ItemId)
        {
            Fail($"The board's search results changed before {name} could be opened.");
            return true;
        }
        var search = BoardReader.ReadyAddon(BoardReader.SearchAddon);
        if (search == null)
        {
            Fail("The market board closed.");
            return true;
        }

        // Recorded ItemSearch [5, i], i the index in ListingPageItemIds: Fire Shard at ids[0] was
        // [5, 0], Earth Shard at ids[3] [5, 3] (37c13073e8c8 seq 48 and 141). The ListItemClick
        // event's param is not i.
        // SAFETY: search is the non-null, ready ItemSearch unit read this frame, and index was
        // checked against the ids the page holds this frame.
        Callback.Fire(search, true, 5, index);
        return true;
    }

    /// <summary>
    /// Waits for the results window and checks it shows the item: <c>ResultItemId</c> changes in
    /// the frame the window opens (37c13073e8c8 seq 51, 144). Another item closes the window and
    /// fails.
    /// </summary>
    private bool? WaitResultOpen()
    {
        var read = BoardReader.Read();
        if (read != null) lastRead = read;

        if (read is { ResultItemId: not 0 } && BoardReader.ResultsOpen())
        {
            if (read.ResultItemId != ItemId)
            {
                if (!Throttle()) return false;
                var results = BoardReader.ReadyAddon(BoardReader.ResultAddon);
                // Recorded ItemSearchResult [-1], as in CloseResults.
                // SAFETY: results is null-checked and was read ready this frame.
                if (results != null) Callback.Fire(results, true, -1);
                Fail($"The board opened another item instead of {name}.");
                return true;
            }

            var before = requestBefore
                         ?? throw new InvalidOperationException("The results opened before the search noted its request id.");
            gate = new ListingsGate(ItemId, before, Environment.TickCount64);
            return true;
        }

        if (!Expired) return false;
        Fail($"The board didn't open the listings for {name}.");
        return true;
    }

    /// <summary>Waits until the listings answer this item's request and stop changing.</summary>
    private bool? WaitListings()
    {
        var read = BoardReader.Read();
        if (read != null) lastRead = read;

        // Closing the results resets ResultItemId to 0 (37c13073e8c8 seq 115); opening another item
        // changes it. Either way these listings will never arrive.
        if (!BoardReader.ResultsOpen())
        {
            Fail($"The results window for {name} closed.");
            return true;
        }
        if (read != null && read.ResultItemId != 0 && read.ResultItemId != ItemId)
        {
            Fail($"The board's results window no longer shows {name}.");
            return true;
        }

        var listingsGate = gate ?? throw new InvalidOperationException("The listings gate wasn't set when the results opened.");
        switch (listingsGate.Observe(read, Environment.TickCount64))
        {
            case GateState.Ready:
                Outcome = new BoardSearchOutcome(ItemId, BoardSearchResult.Opened, null);
                return true;
            case GateState.NoListings:
                Outcome = new BoardSearchOutcome(ItemId, BoardSearchResult.NoListings, null);
                return true;
            case GateState.TimedOut:
                Fail($"The listings for {name} didn't finish loading.");
                return true;
            default:
                return false;
        }
    }

    private static int IndexOf(IReadOnlyList<uint> ids, uint itemId)
    {
        for (var i = 0; i < ids.Count; i++)
            if (ids[i] == itemId) return i;
        return -1;
    }

    private bool Throttle() => EzThrottler.Throttle(ThrottleName, stepDelayMs());

    /// <summary>
    /// Keeps the click throttle fresh while a step waits, so the next click lands the step delay
    /// after the wait ends rather than on its first frame, as the kit's <c>ShopBuyer</c> does.
    /// </summary>
    private void Rethrottle() => EzThrottler.Throttle(ThrottleName, stepDelayMs(), true);

    private void Fail(string reason)
    {
        failure = reason;
        telemetry()?.Log($"boardsearch: failed: {reason}");
    }

    private bool Stamp(int timeoutMs)
    {
        deadlineMs = Environment.TickCount64 + timeoutMs;
        return true;
    }

    private bool Expired => Environment.TickCount64 > deadlineMs;

    /// <summary>
    /// Skips a step once the run has failed, and turns an exception into a failure instead of
    /// letting the task manager abort the queue past the finish task.
    /// </summary>
    private bool? Guarded(Func<bool?> step)
    {
        if (failure != null) return true;
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Board search step failed");
            Fail($"Board search hit an error ({ex.Message}). See /xllog for details.");
            return true;
        }
    }

    /// <summary>
    /// Idempotent end of a search: publishes the outcome and writes the <c>boardsearch</c> devlog
    /// record. A task calling this passes <paramref name="abortQueue"/> false and lets its own
    /// return value end the queue, since aborting the task manager from inside a running task
    /// leaves it mid-tick.
    /// </summary>
    private void Finish(bool abortQueue)
    {
        if (!Busy) return;
        Busy = false;

        if (abortQueue) tasks.Abort();

        if (failure != null)
            Outcome = new BoardSearchOutcome(ItemId, BoardSearchResult.Failed, failure);
        else
            Outcome ??= new BoardSearchOutcome(ItemId, BoardSearchResult.Failed, "the search ended early");

        telemetry()?.Log($"boardsearch: {name} ({ItemId}): {Outcome.Result}{(Outcome.Reason is { } r ? $": {r}" : "")}");
        // The screen for an item with no listings on this world is unrecorded; this record is how
        // it gets learned.
        // A fresh read: lastRead is from before [9] when the run failed early. Finish runs on the
        // framework thread (Tick, Stop, Dispose and the watchdog all do).
        if (BoardReader.Read() is { } finalRead) lastRead = finalRead;
        telemetry()?.Record("boardsearch", new Dictionary<string, object?>
        {
            ["itemId"] = ItemId,
            ["name"] = name,
            ["result"] = Outcome.Result.ToString(),
            ["reason"] = Outcome.Reason,
            ["requestBefore"] = requestBefore,
            ["requestId"] = lastRead?.RequestId,
            ["listingCount"] = lastRead?.ListingCount,
            ["entryCount"] = lastRead?.EntryCount,
            ["listingsRead"] = lastRead?.Listings.Count,
            ["resultItemId"] = lastRead?.ResultItemId,
            ["searchItemId"] = lastRead?.SearchItemId,
        });
    }

    /// <summary>
    /// Ends the search when the board's search window closes under it, and when the queue emptied
    /// without reaching the finish task (a task timeout or an exception inside the task manager).
    /// </summary>
    private void OnUpdate(IFramework _)
    {
        if (!Busy) return;

        if (!BoardReader.SearchOpen())
        {
            failure ??= "The market board closed.";
            Finish(abortQueue: true);
            return;
        }

        if (!tasks.IsBusy)
        {
            failure ??= "a step timed out";
            Finish(abortQueue: true);
        }
    }
}
