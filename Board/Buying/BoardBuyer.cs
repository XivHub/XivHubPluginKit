using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using XivHubPluginKit.Board;
using XivHubPluginKit.Inventory;
using XivHubPluginKit.Shop;

namespace XivHubPluginKit.Board.Buying;

/// <summary>
/// A cover the buyer wants the owner to approve: <see cref="Units"/> of <see cref="Name"/> in
/// <see cref="Listings"/> whole listings for <see cref="Gil"/> (fees included), against the
/// plan's <see cref="PlanGil"/> for the same units, <see cref="DiffPercent"/> apart before fees.
/// <see cref="Reason"/> says why it is asked rather than bought.
/// </summary>
public sealed record BuyAsk(string Name, int Units, int Listings, long Gil, long PlanGil, double DiffPercent,
    string Reason);

/// <summary>
/// What the buyer reads from the plugin that runs it. Every member is read on the framework thread,
/// when it is used.
/// </summary>
public interface IBoardBuyHost
{
    /// <summary>The plugin's name, as messages name what bought or confirmed a listing.</summary>
    string Name { get; }

    /// <summary>The devlog; null when there is none.</summary>
    DevTelemetry? Telemetry { get; }

    /// <summary>The plugin's board search, which opens each line's listings.</summary>
    BoardSearch Search { get; }

    /// <summary>Gil a purchase must leave on hand.</summary>
    long GilReserve { get; }

    /// <summary>When a cover is bought, asked about or skipped (<see cref="BuyGuard.Verdict"/>).</summary>
    BuyThresholds Thresholds { get; }

    /// <summary>Minimum gap between clicks.</summary>
    int UiStepDelayMs { get; }

    /// <summary>The <see cref="Humanizer"/> pause range between whole purchases.</summary>
    int HumanizeMinMs { get; }

    /// <inheritdoc cref="HumanizeMinMs"/>
    int HumanizeMaxMs { get; }

    /// <summary>The world the character is on.</summary>
    uint CurrentWorldId { get; }

    /// <summary>A world's name for messages.</summary>
    string WorldName(uint worldId);

    /// <summary>The plugin's own reason a run can't start, checked before the board's; null when none.</summary>
    string? BlockedReason();

    /// <summary>Whether another of the plugin's automations runs; the buyer checks its own run.</summary>
    bool AutomationBusy { get; }

    /// <summary>The player's own retainers, whose listings are never bought.</summary>
    IReadOnlyCollection<ulong> OwnRetainerIds();

    /// <summary>
    /// Why <paramref name="line"/>, queued when the run on <paramref name="worldId"/> started, is no
    /// longer to be bought, ending in a full stop; null while it still is.
    /// </summary>
    string? LineClosed(uint worldId, BuyLine line);

    /// <summary>Called once at the end of every run.</summary>
    void RunFinished();
}

/// <summary>
/// Buys the current world stop's open board lines one whole listing at a time, at or below the
/// plan's price, asking the owner up to the host's ask threshold above it (or, under
/// <see cref="AskPolicy.SkipAbove"/>, skipping instead) and skipping dearer ones
/// (<see cref="BuyGuard"/>). Every call is one the owner's UiCapture
/// recordings show (market-board.md "Search and buy flow"; sessions 37c13073e8c8 and
/// 108ba2790349):
/// <list type="number">
/// <item>open the line's listings through <see cref="BoardSearch"/>;</item>
/// <item>take the live minimum cover for what is left (<see cref="LiveBoard.Cover"/>);</item>
/// <item>wait for listings bought earlier in the run to leave the slots, then find the listing's
/// slot by <see cref="BoardListing.ListingId"/> in the frame of the click, since a buy shifts
/// later listings down one slot, and click it: <c>ItemSearchResult [2, i]</c>;</item>
/// <item>parse the <c>SelectYesno</c> prompt and answer <c>[0]</c> only when its quantity, gil and
/// fee are the listing's (<see cref="PurchaseText.Matches"/>); another purchase text is answered
/// No, and a prompt that isn't a purchase is left for the owner;</item>
/// <item>in the frame of <c>[0]</c>, check the listing still sits unchanged in slot i and
/// <c>AgentItemSearch.ResultSelectedIndex</c> is i (<see cref="ListingSlot.ConfirmBuys"/>): the
/// game buys <c>Listings[ResultSelectedIndex]</c> as it is at Yes, not the listing clicked;</item>
/// <item>count the buy only on log message 734 with the listing's quantity and the held count
/// rising by it, with <c>LastPurchasedMarketboardItem</c> naming the listing once it changes, then
/// pause (<see cref="Humanizer"/>) and decide again from a fresh read.</item>
/// </list>
/// The recordings show only the successful buy. These are unrecorded: a listing already sold, not
/// enough gil, a price the seller changed, a full bag, a listing of the player's own retainer
/// (left out of every cover, but only once the retainer roster has loaded), the board reloading
/// the item's listings, and the prompt's No answer. Each of them, like anything else that isn't
/// the recorded success, stops the buyer and lands in a <c>boardbuy-unrecognised</c> devlog record
/// with what the game showed, so the signal can be added to market-board.md once met.
/// <para>
/// A stamp task sets a step's deadline, each step returns false to wait, a failure skips the
/// remaining steps, and a check task after each line or <see cref="Stop"/> ends the run. A
/// decision inserts its purchase steps at the front of the queue, so a line's purchases run
/// before the next line's search. Framework thread only:
/// <see cref="Guard"/>, <see cref="Start"/>, <see cref="Answer"/> and <see cref="Stop"/> from
/// window draws; the steps, the watchdog and the log and toast handlers from the framework update.
/// </para>
/// </summary>
public sealed unsafe class BoardBuyer : IDisposable
{
    /// <summary>How long an ask waits for <see cref="Answer"/> before the run stops.</summary>
    public const int AnswerTimeoutMs = 300_000;

    private const string ThrottleName = "KitBoardBuy";
    // SelectYesno opened 40 to 60 ms after ItemSearchResult [2, i] (108ba2790349 seq 134 and 136,
    // 37c13073e8c8 seq 62 and 63).
    private const int PromptTimeoutMs = 5000;
    private const int CloseBoardTimeoutMs = 5000;
    // Log message 734 arrived 290 and 300 ms after SelectYesno [0], and the bag slot filled 400 ms
    // after it (37c13073e8c8 seq 66 and 70; 108ba2790349 seq 145, 151 and 153).
    private const int BoughtTimeoutMs = 10_000;
    // The bought listing left the InfoProxy slots 450 ms after [0], after the log message
    // (37c13073e8c8 seq 66, 70 and 74).
    private const int ListingLeaveTimeoutMs = 5000;
    // A search's own deadlines add up to 30 s (BoardSearch), past the task manager's default.
    private const int SearchWaitLimitMs = 60_000;
    private const int CrystalCap = 9999;

    private readonly TaskManager tasks = new(new TaskManagerConfiguration(timeLimitMS: 30000, abortOnTimeout: true));
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly IToastGui toasts;
    private readonly IPlayerState player;
    private readonly IPluginLog log;
    private readonly IBoardBuyHost host;
    // Held in fields so Finish removes the instances Start added: the toast delegate takes ref
    // parameters, and a method group converts to a new delegate on each use.
    private readonly IChatGui.OnLogMessageDelegate onLogMessage;
    private readonly IToastGui.OnErrorToastDelegate onErrorToast;
    private readonly object seenLock = new();
    private readonly List<(uint Id, string Text)> seenLogs = new();
    private readonly List<string> seenToasts = new();
    private readonly List<string> skipped = new();
    private readonly Dictionary<(uint ItemId, string Kind), int> boughtFor = new();
    private readonly HashSet<ulong> boughtListingIds = new();

    private string worldName = string.Empty;
    private uint worldId;
    private long worldBudget;
    private AskPolicy policy;
    private int lineCount;
    private string? failure;
    // The first failure is one the buyer expects (the owner's Stop, the answer timeout, the board
    // closing) rather than something the game did that the recordings don't show.
    private bool failureKnown;
    private string stage = string.Empty;
    private bool reported;
    private long deadlineMs;
    private bool subscribed;
    private CancellationTokenSource? pauseCts;
    // Set by the search step when the line's item has no listings; the line's decision passes.
    private bool skipLine;
    // The InfoProxy request the line's listings answered. A buy leaves it as it is (37c13073e8c8
    // seq 66 to 74), so a change means the board reloaded the listings under the run.
    private byte? pinnedRequest;
    // True from the frame Yes fires until WaitBought ends: the log and toast handlers store only
    // then, and a run that ends inside it may still see the purchase go through. Read by the
    // handlers, which the framework raises.
    private volatile bool listening;
    // A failure after Yes left the purchase's outcome unknown: the game may have completed it.
    private bool confirmedUnresolved;
    // True from a listing click until its purchase prompt is seen: a prompt that opens after the
    // run ends is the owner's to answer.
    private bool clickUnanswered;
    private bool boardCloseFired;
    // Set once the run starts closing the board itself: the search window going away from then on
    // is the close working, not the board closing under the run.
    private bool closing;
    private bool? answer;
    private IReadOnlyList<ulong> askedIds = [];
    private long askedGil;
    private long askedMs;
    private bool disposed;
    private bool loggedGilUnavailable;

    /// <summary>True from <see cref="Start"/> until the run finishes.</summary>
    public bool Busy { get; private set; }

    /// <summary>The line being bought; null between runs.</summary>
    public string? CurrentName { get; private set; }

    /// <summary>Units bought in the current or last run.</summary>
    public int BoughtUnits { get; private set; }

    /// <summary>Gil spent in the current or last run, fees included.</summary>
    public long Spent { get; private set; }

    /// <summary>The last run's outcome line; null before any run finishes and while one runs.</summary>
    public string? Result { get; private set; }

    /// <summary>Whether the last run finished without a failure; false before any run finishes and while one runs.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>One entry per line the current or last run left, with why.</summary>
    public IReadOnlyList<string> Skipped => skipped;

    /// <summary>A cover waiting for <see cref="Answer"/>; null when nothing is asked.</summary>
    public BuyAsk? PendingAsk { get; private set; }

    /// <param name="framework">The consuming plugin's own <see cref="IFramework"/>; the steps and the
    /// watchdog run on its <c>Update</c>.</param>
    /// <param name="chat">Where purchase log messages are read from during a buy.</param>
    /// <param name="toasts">Where error toasts are read from during a buy.</param>
    /// <param name="player">Whether a character is logged in.</param>
    /// <param name="log">Where stops and step exceptions are logged.</param>
    /// <param name="host">The plugin's settings and state the buyer reads.</param>
    public BoardBuyer(IFramework framework, IChatGui chat, IToastGui toasts, IPlayerState player, IPluginLog log,
        IBoardBuyHost host)
    {
        this.framework = framework;
        this.chat = chat;
        this.toasts = toasts;
        this.player = player;
        this.log = log;
        this.host = host;
        onLogMessage = OnLogMessage;
        onErrorToast = OnErrorToast;
        framework.Update += OnUpdate;
    }

    /// <summary>Why a run on <paramref name="stop"/> can't start right now, or null.</summary>
    public string? Guard(BuyStop? stop)
    {
        if (!player.IsLoaded) return "Log in to buy on the board.";
        if (host.BlockedReason() is { } blocked) return blocked;
        if (stop is null || stop.Done)
            return $"Nothing to buy on {host.WorldName(host.CurrentWorldId)}.";
        if (stop.WorldId != host.CurrentWorldId) return "Buying works on the world you're on.";
        if (host.Search.Guard() is { } board) return board;
        if (Busy || host.AutomationBusy) return "Another automation is running.";
        if (ReadGil() is not { } gil) return "Your gil can't be read.";
        if (gil <= host.GilReserve) return "You're at your gil reserve.";
        return null;
    }

    /// <summary>
    /// Buys <paramref name="stop"/>'s open lines (<see cref="BuyStop.Open"/>) in order. Does nothing
    /// after dispose or while <see cref="Guard"/> gives a reason. An automatic buy that would take
    /// the run's spend past <see cref="BuyGuard.Budget"/> for the stop at this moment is asked first
    /// (<see cref="BuyGuard.WithinBudget"/>). Every ask goes through <paramref name="askPolicy"/>
    /// (<see cref="BuyGuard.UnderPolicy"/>).
    /// </summary>
    public void Start(BuyStop stop, AskPolicy askPolicy = AskPolicy.Ask)
    {
        if (disposed || Guard(stop) != null) return;

        var lines = stop.Open.ToList();
        policy = askPolicy;
        worldId = stop.WorldId;
        worldName = host.WorldName(worldId);
        worldBudget = BuyGuard.Budget(stop, host.Thresholds);
        lineCount = lines.Count;
        failure = null;
        failureKnown = false;
        stage = string.Empty;
        reported = false;
        skipLine = false;
        pinnedRequest = null;
        listening = false;
        confirmedUnresolved = false;
        clickUnanswered = false;
        closing = false;
        answer = null;
        askedIds = [];
        askedGil = 0;
        BoughtUnits = 0;
        Spent = 0;
        Result = null;
        Succeeded = false;
        CurrentName = null;
        PendingAsk = null;
        skipped.Clear();
        boughtFor.Clear();
        boughtListingIds.Clear();
        lock (seenLock)
        {
            seenLogs.Clear();
            seenToasts.Clear();
        }
        pauseCts = new CancellationTokenSource();
        Busy = true;

        chat.LogMessage += onLogMessage;
        toasts.ErrorToast += onErrorToast;
        subscribed = true;

        host.Telemetry?.Log($"boardbuy: start on {worldName}, {lines.Count} lines, budget {worldBudget}");

        foreach (var line in lines)
        {
            var name = line.Name;
            tasks.Enqueue(() => Guarded("search", () => SearchLine(line)), $"Board buy: search {name}");
            tasks.Enqueue(() => Guarded("search", () => WaitSearch(line)), $"Board buy: wait for the search of {name}",
                new TaskManagerConfiguration(timeLimitMS: SearchWaitLimitMs));
            tasks.EnqueueMulti(DecideSteps(line));
            tasks.Enqueue(Check, "Board buy: check");
        }
        tasks.Enqueue(() => { boardCloseFired = false; return Stamp(CloseBoardTimeoutMs); }, "Board buy: stamp");
        tasks.Enqueue(() => Guarded("close", CloseBoard), "Board buy: close the board");
        tasks.Enqueue(() => { Finish(abortQueue: false); return true; }, "Board buy: finish");
    }

    /// <summary>
    /// Closes the board once a run has gone through every line: the results window with the
    /// recorded <c>ItemSearchResult [-1]</c> (37c13073e8c8 seq 114), then the search window, as
    /// Escape does. <see cref="Guarded"/> skips it after a failure or Stop, so a run that ended
    /// early leaves the board open to look at, and an open prompt is left for the owner.
    /// </summary>
    private bool? CloseBoard()
    {
        if (BoardReader.WindowVisible(BoardReader.YesnoAddon)) return true;
        closing = true;

        var results = BoardReader.ReadyAddon(BoardReader.ResultAddon);
        if (results != null)
        {
            if (boardCloseFired) return Expired;
            if (!Throttle()) return false;
            // SAFETY: results is the non-null, ready ItemSearchResult unit read this frame, passed
            // straight to the game's AtkUnitBase.FireCallback.
            Callback.Fire(results, true, -1);
            boardCloseFired = true;
            return false;
        }

        var search = BoardReader.ReadyAddon(BoardReader.SearchAddon);
        // After the results close, wait for the search window to be ready again rather than
        // leave it open.
        if (search == null) return !BoardReader.WindowVisible(BoardReader.SearchAddon) || Expired;
        // SAFETY: search is the non-null, ready ItemSearch unit read this frame; Close is an
        // AtkUnitBase member.
        search->Close(true);
        return true;
    }

    /// <summary>
    /// Answers the pending ask: <paramref name="buy"/> buys the asked cover when the board still
    /// holds it, false skips the line. Ignored when nothing is asked.
    /// </summary>
    public void Answer(bool buy)
    {
        if (!Busy || PendingAsk is null) return;
        answer = buy;
    }

    /// <summary>
    /// Ends a run now and drops its queued steps. Answers no prompt and closes no window, so the
    /// board stays open where the run stopped. No-op when idle.
    /// </summary>
    public void Stop()
    {
        if (!Busy) return;
        Fail("stopped by you", known: true);
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

    // ---- Lines ----

    /// <summary>
    /// Opens the line's listings through the board search. A line the host no longer holds open
    /// (<see cref="IBoardBuyHost.LineClosed"/>) is skipped without a search.
    /// </summary>
    private bool? SearchLine(BuyLine line)
    {
        skipLine = false;
        pinnedRequest = null;
        CurrentName = line.Name;
        if (host.LineClosed(worldId, line) is { } closed)
        {
            skipped.Add($"{line.Name}: {closed}");
            skipLine = true;
            return true;
        }
        if (!host.Search.Start(line.ItemId))
            Fail(host.Search.Guard() ?? $"The board search for {line.Name} didn't start.");
        return true;
    }

    /// <summary>
    /// Waits for the search to end and reads how it went. Opened listings pin the request they
    /// answered, which every later read of the line checks (<see cref="ReadResults"/>).
    /// </summary>
    private bool? WaitSearch(BuyLine line)
    {
        if (BoardClosed() || skipLine) return true;
        if (host.Search.Busy) return false;

        var name = line.Name;
        var outcome = host.Search.Outcome;
        if (outcome is null || outcome.ItemId != line.ItemId)
        {
            Fail($"The board search for {name} ended without a result.");
            return true;
        }

        switch (outcome.Result)
        {
            case BoardSearchResult.Failed:
                Fail(outcome.Reason ?? $"The board search for {name} failed.");
                break;
            case BoardSearchResult.NoListings:
                skipped.Add($"{name}: no listings on {worldName}.");
                skipLine = true;
                break;
            case BoardSearchResult.Opened:
                if (ReadResults(line.ItemId, name) is { } read) pinnedRequest = read.RequestId;
                break;
        }
        return true;
    }

    /// <summary>A fresh deadline, then a wait for the listings bought so far to leave the slots.</summary>
    private TaskManagerTask[] LeaveSteps(BuyLine line) =>
    [
        new(() => Stamp(ListingLeaveTimeoutMs), "Board buy: stamp"),
        new(() => Guarded("leave", () => WaitBoughtLeft(line)),
            $"Board buy: wait for the bought {line.Name} to leave the board"),
    ];

    /// <summary>The bought listings leave the slots, then a decision.</summary>
    private TaskManagerTask[] DecideSteps(BuyLine line) =>
    [
        ..LeaveSteps(line),
        new(() => Guarded("decide", () => Decide(line)), $"Board buy: decide {line.Name}"),
    ];

    /// <summary>
    /// Waits until no listing bought in this run is still in the slots. A bought listing left them
    /// about 170 ms after log message 734 and 450 ms after Yes (37c13073e8c8 seq 66, 70 and 74),
    /// shifting later listings down one slot: a cover read before that would count it again, and a
    /// click before that could land on a slot about to move.
    /// </summary>
    private bool? WaitBoughtLeft(BuyLine line)
    {
        if (skipLine || boughtListingIds.Count == 0) return true;

        var name = line.Name;
        if (ReadResults(line.ItemId, name) is not { } read) return true;
        if (!read.Listings.Any(l => boughtListingIds.Contains(l.ListingId))) return true;
        if (!Expired) return false;
        Fail($"The board still lists the {name} {host.Name} bought.");
        return true;
    }

    /// <summary>
    /// Decides the line from the board as it is now, after its search and after each purchase:
    /// the line is finished (nothing left, or skipped), its cheapest cover listing is bought, or
    /// the owner is asked.
    /// </summary>
    private bool? Decide(BuyLine line)
    {
        if (skipLine) return true;

        var itemId = line.ItemId;
        var name = line.Name;
        var boughtHere = boughtFor.GetValueOrDefault((itemId, line.Kind));
        var remaining = line.Units - boughtHere;
        if (ReadResults(itemId, name) is not { } read) return true;

        var d = BuyGuard.Decide(line, remaining, read.Listings, host.OwnRetainerIds(),
            host.Thresholds);
        // Nothing has no comparison and a no-cover Skip no live cover, so the budget applies only
        // with a live cover.
        var c = d.Comparison;
        var live = c?.Live;
        var budgeted = live is null ? d.Verdict : BuyGuard.WithinBudget(d.Verdict, Spent, live.Gil, worldBudget);
        var verdict = BuyGuard.UnderPolicy(budgeted, policy);
        // WithinBudget turns only a Buy into an Ask; the guard's own reason is a price one.
        var reason = budgeted == BuyVerdict.Ask && d.Verdict == BuyVerdict.Buy ? "Past this world's budget." : d.Reason;
        RecordDecide(line, remaining, d, verdict);

        switch (verdict)
        {
            case BuyVerdict.Skip:
                if (c is not null && live is not null)
                    skipped.Add($"{name}: {reason} Board {live.Gil:N0} gil, plan {c.PlanGil:N0} gil.");
                else if (boughtHere > 0)
                    skipped.Add($"{name}: board sold out after {boughtHere}.");
                else
                    skipped.Add($"{name}: {d.Reason}");
                return true;

            case BuyVerdict.Buy when live is not null:
            {
                var listing = live.Taken[0];
                var steps = new List<TaskManagerTask>(PurchaseSteps(line, listing))
                {
                    PauseStep(remaining - listing.Qty > 0),
                };
                steps.AddRange(DecideSteps(line));
                tasks.InsertMulti(steps.ToArray());
                return true;
            }

            case BuyVerdict.Ask when c is not null && live is not null:
                PendingAsk = new BuyAsk(name, live.Units, live.Taken.Count, live.Gil, c.PlanGil, c.DiffPercent ?? 0,
                    reason);
                askedIds = live.Taken.Select(l => l.ListingId).ToList();
                askedGil = live.Gil;
                askedMs = Environment.TickCount64;
                answer = null;
                tasks.InsertMulti(new TaskManagerTask(() => Guarded("answer", () => WaitAnswer(line, remaining)),
                    $"Board buy: wait for your answer on {name}",
                    new TaskManagerConfiguration(timeLimitMS: AnswerTimeoutMs + 5000)));
                return true;

            case BuyVerdict.Nothing:
                return true;

            default:
                // Buy and Ask always carry a live cover (BuyGuard.Decide reaches them only past one).
                Fail($"{host.Name} decided {verdict} for {name} without a board cover.");
                return true;
        }
    }

    /// <summary>
    /// Waits for <see cref="Answer"/>. Yes buys the asked cover when a fresh read gives the same
    /// listings for the same gil; a cover that changed is decided again, which asks again, buys it
    /// when it now meets the plan's price, or skips it when it has passed the ask limit.
    /// </summary>
    private bool? WaitAnswer(BuyLine line, int remaining)
    {
        var name = line.Name;
        if (answer is not { } buy)
        {
            if (Environment.TickCount64 - askedMs < AnswerTimeoutMs) return false;
            PendingAsk = null;
            Fail("No answer within 5 minutes.", known: true);
            return true;
        }

        PendingAsk = null;
        answer = null;
        if (!buy)
        {
            skipped.Add($"{name}: skipped by you.");
            return true;
        }

        if (ReadResults(line.ItemId, name) is not { } read) return true;
        var cover = LiveBoard.Cover(line.ItemId, remaining, read.Listings, host.OwnRetainerIds(), line.Hq);
        if (cover is null || cover.Gil != askedGil || !cover.Taken.Select(l => l.ListingId).SequenceEqual(askedIds))
        {
            host.Telemetry?.Log($"boardbuy: {name}: the approved cover changed on the board; deciding again");
            tasks.InsertMulti(DecideSteps(line));
            return true;
        }

        var steps = new List<TaskManagerTask>();
        var left = remaining;
        for (var i = 0; i < cover.Taken.Count; i++)
        {
            var listing = cover.Taken[i];
            steps.AddRange(PurchaseSteps(line, listing));
            left -= listing.Qty;
            if (i < cover.Taken.Count - 1) steps.Add(PauseStep(left > 0));
        }
        steps.AddRange(DecideSteps(line));
        tasks.InsertMulti(steps.ToArray());
        return true;
    }

    // ---- One purchase ----

    /// <summary>The listing being bought, and what its steps learn along the way.</summary>
    private sealed class Purchase(BuyLine line, BoardListing listing)
    {
        public BuyLine Line { get; } = line;
        public BoardListing Listing { get; } = listing;
        public uint ItemId => Line.ItemId;
        public string Name => Line.Name;
        public bool Crystal => Line.IsCrystal;
        public int Index { get; set; } = -1;
        public long HeldBefore { get; set; }
        public long GilBefore { get; set; }
        public string? PromptText { get; set; }
        public long ConfirmedMs { get; set; }
        public bool PromptClosed { get; set; }
        // LastPurchasedMarketboardItem.ListingId read in the frame of Yes, before it fired.
        public ulong? LastPurchasedBefore { get; set; }
    }

    private TaskManagerTask[] PurchaseSteps(BuyLine line, BoardListing listing)
    {
        var p = new Purchase(line, listing);
        return
        [
            ..LeaveSteps(line),
            new(() => Guarded("find", () => FindListing(p)), $"Board buy: find the {p.Name} listing"),
            new(() => Guarded("click", () => ClickListing(p)), $"Board buy: pick the {p.Name} listing"),
            new(() => Guarded("prompt", () => WaitPrompt(p)), $"Board buy: read the prompt for {p.Name}"),
            new(() => Guarded("confirm", () => Confirm(p)), $"Board buy: confirm {p.Name}"),
            new(() => Guarded("bought", () => WaitBoughtStep(p)), $"Board buy: wait for {p.Name}"),
        ];
    }

    /// <summary>
    /// Checks the listing is still there as bought, the prompt is closed, the reserve holds and
    /// there is room, and notes what is held and the gil before the buy.
    /// </summary>
    private bool? FindListing(Purchase p)
    {
        if (ReadResults(p.ItemId, p.Name) is not { } read) return true;
        if (CheckSlot(p, read) is null || !CheckBeforeClick(p)) return true;
        p.GilBefore = MainBags.ReadGil();
        p.HeldBefore = BoardReader.HeldForBuy(p.ItemId, p.Crystal);
        return true;
    }

    /// <summary>
    /// Clicks the listing. Its slot is found again by id in the frame of the click, so a board that
    /// shifted since the last read never gets a click meant for another listing.
    /// </summary>
    private bool? ClickListing(Purchase p)
    {
        if (!Throttle()) return false;
        if (ReadResults(p.ItemId, p.Name) is not { } read) return true;
        if (CheckSlot(p, read) is not { } index || !CheckBeforeClick(p)) return true;

        var results = BoardReader.ReadyAddon(BoardReader.ResultAddon);
        if (results == null)
        {
            FailPurchase(p, "The board's results window closed.");
            return true;
        }

        p.Index = index;
        // Recorded ItemSearchResult [2, i], i the slot in InfoProxyItemSearch.Listings
        // (37c13073e8c8 seq 62, 108ba2790349 seq 134).
        // SAFETY: results is the non-null, ready ItemSearchResult unit read this frame, and index is
        // the slot this frame's read holds the listing in, under min(ListingCount, EntryCount, 100).
        Callback.Fire(results, true, 2, index);
        clickUnanswered = true;
        host.Telemetry?.Log($"boardbuy: clicked {p.Listing.Qty} {p.Name} at {p.Listing.UnitPrice} in slot {index}");
        Stamp(PromptTimeoutMs);
        return true;
    }

    /// <summary>
    /// The slot holding <paramref name="p"/>'s listing in <paramref name="read"/>, unchanged, or
    /// null after failing the run. A listing gone before its click stops the run like any other
    /// unrecorded signal (the recordings show a listing leaving only after the player's own buy),
    /// so its <c>boardbuy-unrecognised</c> record is written rather than the line decided again.
    /// </summary>
    private int? CheckSlot(Purchase p, BoardSnapshot read)
    {
        var l = p.Listing;
        var slot = ListingSlot.Find(read.Listings, l);
        switch (slot.State)
        {
            case SlotState.Gone:
                FailPurchase(p, $"The {l.Qty} {p.Name} at {l.UnitPrice:N0} gil left the board before {host.Name} bought it.");
                return null;
            case SlotState.Changed:
                FailPurchase(p, $"The {p.Name} listing changed before {host.Name} bought it.");
                return null;
            default:
                return slot.Index;
        }
    }

    /// <summary>
    /// Whether the purchase can start: no prompt open, the gil reserve kept and room for the units.
    /// False after failing the run.
    /// </summary>
    private bool CheckBeforeClick(Purchase p)
    {
        var l = p.Listing;
        if (BoardReader.WindowVisible(BoardReader.YesnoAddon))
        {
            FailPurchase(p, "Close the open prompt first.");
            return false;
        }

        var reserve = host.GilReserve;
        if (!BuyGuard.KeepsReserve(MainBags.ReadGil(), reserve, l.Total))
        {
            FailPurchase(p, $"Buying {l.Qty} {p.Name} would take you below your {reserve:N0} gil reserve.");
            return false;
        }

        if (p.Crystal)
        {
            if (BoardReader.HeldForBuy(p.ItemId, true) + l.Qty > CrystalCap)
            {
                FailPurchase(p, $"Buying {l.Qty} {p.Name} would pass 9,999.");
                return false;
            }
        }
        else
        {
            var stack = ItemSheet.StackSize(p.ItemId);
            var slots = (l.Qty + stack - 1) / stack;
            if (MainBags.ReadFreeSlots() < slots)
            {
                FailPurchase(p, $"Not enough bag space for {l.Qty} {p.Name}.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Waits for the purchase prompt and checks it asks for exactly this listing. Any other
    /// purchase text is answered No, the button a player clicks, and stops the run.
    /// </summary>
    private bool? WaitPrompt(Purchase p)
    {
        var text = BoardReader.YesnoText();
        if (text == null)
        {
            if (Expired)
            {
                FailPurchase(p, $"The purchase prompt for {p.Name} didn't open.");
                return true;
            }
            Rethrottle();
            return false;
        }

        p.PromptText = text;
        if (PurchaseText.IsPurchasePrompt(text)) clickUnanswered = false;
        if (!PromptMatches(p, text)) return true;

        // Confirm lands UiStepDelayMs after the prompt appeared.
        Rethrottle();
        return true;
    }

    /// <summary>
    /// Answers Yes. The prompt and the board are read again in the frame of the click: Yes only
    /// goes to a prompt that asks for this listing, and only while the slot the game will buy,
    /// <c>Listings[ResultSelectedIndex]</c>, still holds it (<see cref="ListingSlot.ConfirmBuys"/>).
    /// A prompt read at the click can outlive a board that shifted under it, so the prompt alone
    /// doesn't say what Yes buys.
    /// </summary>
    private bool? Confirm(Purchase p)
    {
        if (!Throttle()) return false;

        var yesno = BoardReader.ReadyAddon(BoardReader.YesnoAddon);
        var text = BoardReader.YesnoText();
        if (yesno == null || text == null)
        {
            FailPurchase(p, $"The purchase prompt for {p.Name} closed before {host.Name} answered it.");
            return true;
        }
        p.PromptText = text;
        if (!PromptMatches(p, text)) return true;

        if (!ListingSlot.ConfirmBuys(BoardReader.Read(), p.Listing, p.Index, BoardReader.SelectedIndex()))
        {
            AnswerNo();
            FailPurchase(p, $"The {p.Name} listing moved before {host.Name} confirmed it. Cancelled.");
            return true;
        }

        p.LastPurchasedBefore = BoardReader.LastPurchasedListingId();
        lock (seenLock)
        {
            seenLogs.Clear();
            seenToasts.Clear();
        }
        listening = true;
        // Recorded SelectYesno [0] (37c13073e8c8 seq 66, 108ba2790349 seq 145).
        // SAFETY: yesno is the non-null, ready SelectYesno unit read this frame, whose text and
        // listing slot were checked in the same frame.
        Callback.Fire(yesno, true, 0);
        p.ConfirmedMs = Environment.TickCount64;
        Stamp(BoughtTimeoutMs);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is the prompt for <paramref name="p"/>'s listing. A purchase
    /// prompt for anything else is answered No; a prompt that isn't a purchase is some other
    /// window's question, left open for the owner. Either fails the run.
    /// </summary>
    private bool PromptMatches(Purchase p, string text)
    {
        var l = p.Listing;
        if (!PurchaseText.IsPurchasePrompt(text))
        {
            FailPurchase(p, "Another prompt opened.");
            return false;
        }
        if (PurchaseText.ParsePrompt(text) is { } prompt && PurchaseText.Matches(prompt, l)) return true;

        AnswerNo();
        FailPurchase(p,
            $"The game asked \"{text}\", which doesn't match the listing ({l.Qty} for {l.Total:N0} gil, {l.TotalTax:N0} fee). Cancelled.");
        return false;
    }

    /// <summary>Answers the open <c>SelectYesno</c> No, when it is ready this frame.</summary>
    private static void AnswerNo()
    {
        var yesno = BoardReader.ReadyAddon(BoardReader.YesnoAddon);
        // SAFETY: yesno is null-checked and was read ready this frame; No() clicks its second
        // button through the addon's own button component.
        if (yesno != null) new AddonMaster.SelectYesno(yesno).No();
    }

    /// <summary><see cref="WaitBought"/>, ending the listening window once it has an outcome.</summary>
    private bool? WaitBoughtStep(Purchase p)
    {
        var done = WaitBought(p);
        if (done != false) listening = false;
        return done;
    }

    /// <summary>
    /// The recorded success, both parts: log message 734 with the listing's quantity, and the held
    /// count up by at least that much. An error toast, a 734 with another quantity, the prompt
    /// opening again, or <c>LastPurchasedMarketboardItem</c> changing to another listing fails at
    /// once; other log ids are chat noise.
    /// </summary>
    private bool? WaitBought(Purchase p)
    {
        var l = p.Listing;

        // The Yes handler copies the selected slot into LastPurchasedMarketboardItem before it
        // sends the purchase (0x1405371B9 calling 0x140971C40). The recording first shows the
        // change in the struct sample 146 ms after [0] (108ba2790349 seq 145 and 150), so it is
        // compared on every frame of this step, against the value read just before Yes.
        if (p.LastPurchasedBefore is { } before && BoardReader.LastPurchasedListingId() is { } taken
            && taken != before && taken != l.ListingId)
        {
            FailUnresolved(p, $"it took listing {Id(taken)}, not listing {Id(l.ListingId)} that {host.Name} confirmed");
            return true;
        }

        List<(uint Id, string Text)> logs;
        List<string> toasts;
        lock (seenLock)
        {
            logs = seenLogs.ToList();
            toasts = seenToasts.ToList();
        }

        var logSaysBought = false;
        foreach (var (id, text) in logs)
        {
            if (!PurchaseText.IsPurchasedLog(id)) continue;
            if (PurchaseText.ParseBoughtLog(text) != l.Qty)
            {
                FailUnresolved(p, SaidText(logs, toasts));
                return true;
            }
            logSaysBought = true;
        }
        if (toasts.Count > 0)
        {
            FailUnresolved(p, SaidText(logs, toasts));
            return true;
        }

        // The prompt closes about 120 ms after [0] (37c13073e8c8 seq 66 and 67); one that opens
        // after that is something the recordings don't show.
        var promptVisible = BoardReader.WindowVisible(BoardReader.YesnoAddon);
        if (!promptVisible) p.PromptClosed = true;
        else if (p.PromptClosed)
        {
            FailUnresolved(p, $"{SaidText(logs, toasts)}; the purchase prompt opened again");
            return true;
        }

        var held = BoardReader.HeldForBuy(p.ItemId, p.Crystal);
        if (logSaysBought && held - p.HeldBefore >= l.Qty)
        {
            var key = (p.ItemId, p.Line.Kind);
            boughtFor[key] = boughtFor.GetValueOrDefault(key) + l.Qty;
            boughtListingIds.Add(l.ListingId);
            BoughtUnits += l.Qty;
            Spent += l.Total;
            var gilAfter = MainBags.ReadGil();
            var ms = Environment.TickCount64 - p.ConfirmedMs;
            host.Telemetry?.Log($"boardbuy: bought {l.Qty} {p.Name} for {l.Total} gil in {ms} ms");
            host.Telemetry?.Record("boardbuy", new Dictionary<string, object?>
            {
                ["itemId"] = p.ItemId,
                ["name"] = p.Name,
                ["listingId"] = Id(l.ListingId),
                ["index"] = p.Index,
                ["qty"] = l.Qty,
                ["unitPrice"] = l.UnitPrice,
                ["fee"] = l.TotalTax,
                ["total"] = l.Total,
                ["gilBefore"] = p.GilBefore,
                ["gilAfter"] = gilAfter,
                ["heldBefore"] = p.HeldBefore,
                ["heldAfter"] = held,
                ["msSinceConfirm"] = ms,
            });
            return true;
        }

        if (!Expired) return false;
        confirmedUnresolved = true;
        FailPurchase(p,
            $"Buying {p.Name} didn't confirm within 10 seconds; the game said: {SaidText(logs, toasts)}. Check your gil and bags.");
        return true;
    }

    /// <summary>
    /// Fails a purchase after Yes whose outcome is unknown: the game may have completed it, so the
    /// run's result says so (<see cref="Finish"/>).
    /// </summary>
    private void FailUnresolved(Purchase p, string what)
    {
        confirmedUnresolved = true;
        FailPurchase(p, $"Buying {p.Name} may not have gone through; the game said: {what}. Check your gil and bags.");
    }

    /// <summary>
    /// What the game said since the confirm, error toasts first, quoted and joined; "nothing" when
    /// it said nothing.
    /// </summary>
    private static string SaidText(IReadOnlyCollection<(uint Id, string Text)> logs, IReadOnlyCollection<string> toasts) =>
        logs.Count + toasts.Count == 0
            ? "nothing"
            : string.Join(", ", toasts.Concat(logs.Select(m => m.Text)).Select(t => $"\"{t}\""));

    /// <summary>
    /// The pacing between whole purchases: the kit's <see cref="Humanizer"/> pause, started once and
    /// polled here, so it runs inside the framework-thread queue.
    /// </summary>
    private TaskManagerTask PauseStep(bool nextIsSameLine)
    {
        Task? pause = null;
        return new TaskManagerTask(() => Guarded("pause", () =>
        {
            pause ??= Humanizer.PauseAsync(host.HumanizeMinMs, host.HumanizeMaxMs,
                pauseCts?.Token ?? CancellationToken.None, repeat: nextIsSameLine);
            return pause.IsCompleted;
        }), "Board buy: pause");
    }

    // ---- Messages ----

    /// <summary>
    /// Copies a log message's id and text while a confirmed purchase is awaited: an
    /// <see cref="ILogMessage"/> is valid only during this call (UiCapture <c>MessageSource.cs:116</c>).
    /// </summary>
    private void OnLogMessage(ILogMessage message)
    {
        if (!listening) return;
        try
        {
            var id = message.LogMessageId;
            var text = message.FormatLogMessageForDebugging().ExtractText();
            lock (seenLock) seenLogs.Add((id, text));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Couldn't read a log message during a board buy");
        }
    }

    /// <summary>
    /// Copies an error toast's text while a confirmed purchase is awaited. Never sets
    /// <paramref name="isHandled"/>, so the player still sees it.
    /// </summary>
    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (!listening) return;
        try
        {
            var text = message.ToString();
            lock (seenLock) seenToasts.Add(text);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Couldn't read an error toast during a board buy");
        }
    }

    // ---- Run plumbing ----

    /// <summary>
    /// A read of the results window when the board is open and still shows
    /// <paramref name="itemId"/>'s listings from the pinned request, else null after failing the
    /// run. Closing the results sets
    /// <c>ResultItemId</c> to 0 (37c13073e8c8 seq 115); a new request replaces the slots a
    /// decision or click was made from.
    /// </summary>
    private BoardSnapshot? ReadResults(uint itemId, string name)
    {
        if (BoardClosed()) return null;
        var read = BoardReader.Read();
        if (read is null || read.ResultItemId != itemId)
        {
            Fail($"The board's results window no longer shows {name}.");
            return null;
        }
        if (pinnedRequest is { } pinned && read.RequestId != pinned)
        {
            Fail($"The board reloaded the {name} listings.");
            return null;
        }
        return read;
    }

    private void RecordDecide(BuyLine line, int remaining, BuyDecision d, BuyVerdict verdict)
    {
        var c = d.Comparison;
        var fields = new Dictionary<string, object?>
        {
            ["itemId"] = line.ItemId,
            ["name"] = line.Name,
            ["routeKind"] = line.Kind,
            ["askPolicy"] = policy.ToString(),
            ["verdict"] = verdict.ToString(),
            ["guardVerdict"] = d.Verdict.ToString(),
            ["reason"] = d.Reason,
            ["remaining"] = remaining,
            ["spent"] = Spent,
            ["worldBudget"] = worldBudget,
        };
        if (c?.Live is { } live)
        {
            fields["coverIds"] = live.Taken.Select(l => Id(l.ListingId)).ToList();
            fields["liveUnits"] = live.Units;
            fields["liveGil"] = live.Gil;
            fields["planGil"] = c.PlanGil;
            fields["diffPercent"] = c.DiffPercent;
        }
        host.Telemetry?.Log($"boardbuy: {line.Name}: {verdict} ({d.Reason}), remaining {remaining}");
        host.Telemetry?.Record("boardbuy-decide", fields);
    }

    /// <summary>
    /// Writes a stop: <c>log.Warning</c> and a <c>boardbuy-unrecognised</c> devlog record
    /// with what the game showed. <paramref name="known"/> marks the stops the buyer expects (the
    /// owner's Stop, the answer timeout, the board closing), so the record's readers can tell them
    /// from signals the recordings don't show. Never throws, since it also runs from
    /// <see cref="Finish"/>.
    /// </summary>
    private void Report(string reason, Purchase? p, bool known)
    {
        reported = true;
        try
        {
            log.Warning($"Board buy stopped at {stage}: {reason}");

            List<(uint Id, string Text)> logs;
            List<string> toasts;
            lock (seenLock)
            {
                logs = seenLogs.ToList();
                toasts = seenToasts.ToList();
            }

            var read = BoardReader.Read();
            var fields = new Dictionary<string, object?>
            {
                ["known"] = known,
                ["stage"] = stage,
                ["reason"] = reason,
                ["world"] = worldName,
                ["name"] = p?.Name ?? CurrentName,
                ["listingCount"] = read?.ListingCount,
                ["entryCount"] = read?.EntryCount,
                ["requestId"] = read?.RequestId,
                ["pinnedRequestId"] = pinnedRequest,
                ["selectedIndex"] = BoardReader.SelectedIndex(),
                ["resultItemId"] = read?.ResultItemId,
                ["prompt"] = BoardReader.YesnoText(),
                ["logs"] = logs.Select(m => new Dictionary<string, object?> { ["id"] = m.Id, ["text"] = m.Text }).ToList(),
                ["toasts"] = toasts,
                ["gilAfter"] = ReadGil(),
            };
            if (p is not null)
            {
                var l = p.Listing;
                fields["itemId"] = p.ItemId;
                fields["listing"] = new Dictionary<string, object?>
                {
                    ["listingId"] = Id(l.ListingId),
                    ["retainerId"] = Id(l.RetainerId),
                    ["qty"] = l.Qty,
                    ["unitPrice"] = l.UnitPrice,
                    ["fee"] = l.TotalTax,
                    ["total"] = l.Total,
                    ["hq"] = l.Hq,
                };
                fields["index"] = p.Index;
                fields["promptRead"] = p.PromptText;
                fields["heldBefore"] = p.HeldBefore;
                fields["heldAfter"] = BoardReader.HeldForBuy(p.ItemId, p.Crystal);
                fields["gilBefore"] = p.GilBefore;
                fields["stillListed"] = read?.Listings.Any(x => x.ListingId == l.ListingId);
                fields["msSinceConfirm"] = p.ConfirmedMs == 0 ? null : Environment.TickCount64 - p.ConfirmedMs;
                fields["lastPurchasedBefore"] = p.LastPurchasedBefore is { } before ? Id(before) : null;
                fields["lastPurchased"] = BoardReader.LastPurchasedListingId() is { } now ? Id(now) : null;
            }
            host.Telemetry?.Record("boardbuy-unrecognised", fields);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Couldn't write the board buy report");
        }
    }

    private void FailPurchase(Purchase p, string reason)
    {
        Fail(reason);
        Report(reason, p, known: false);
    }

    /// <summary>
    /// Records the run's first failure, and whether the buyer expects it (see <see cref="Report"/>);
    /// the check task after the line ends the run.
    /// </summary>
    private void Fail(string reason, bool known = false)
    {
        if (failure == null)
        {
            failure = reason;
            failureKnown = known;
        }
        host.Telemetry?.Log($"boardbuy: failed: {reason}");
    }

    /// <summary>Ends the run on the first failure; null tells the task manager to drop the rest.</summary>
    private bool? Check()
    {
        if (failure == null) return true;
        Finish(abortQueue: false);
        return null;
    }

    /// <summary>
    /// Whether the board's search window has closed, after failing the run with the known stop
    /// <see cref="OnUpdate"/> also gives, so a step running before it in the frame reports the
    /// same thing.
    /// </summary>
    private bool BoardClosed()
    {
        if (BoardReader.SearchOpen()) return false;
        Fail("The market board closed.", known: true);
        return true;
    }

    private bool Stamp(int timeoutMs)
    {
        deadlineMs = Environment.TickCount64 + timeoutMs;
        return true;
    }

    private bool Expired => Environment.TickCount64 > deadlineMs;

    private bool Throttle() => EzThrottler.Throttle(ThrottleName, host.UiStepDelayMs);

    /// <summary>
    /// Keeps the click throttle fresh while a step waits, so the next click lands
    /// <c>UiStepDelayMs</c> after the wait ends, as the kit's <c>ShopBuyer</c> does.
    /// </summary>
    private void Rethrottle() => EzThrottler.Throttle(ThrottleName, host.UiStepDelayMs, true);

    /// <summary>
    /// Skips a step once the run has failed or the plugin is unloading, and turns an exception into
    /// a failure instead of letting the task manager abort the queue past the check task.
    /// </summary>
    private bool? Guarded(string stageName, Func<bool?> step)
    {
        if (failure != null || disposed) return true;
        stage = stageName;
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Board buy step failed");
            Fail($"Board buying hit an error ({ex.Message}). See /xllog for details.");
            return true;
        }
    }

    /// <summary>
    /// Idempotent end of a run: drops the queue when asked, stops a search the run started, removes
    /// the log and toast handlers, publishes the result, writes the <c>boardbuy-run</c> record and
    /// asks the planner to re-diff. A task calling this passes <paramref name="abortQueue"/> false
    /// and lets its own return value end the queue, since aborting the task manager from inside a
    /// running task leaves it mid-tick.
    /// </summary>
    private void Finish(bool abortQueue)
    {
        if (!Busy) return;
        Busy = false;

        try
        {
            if (abortQueue) tasks.Abort();
            if (host.Search.Busy) host.Search.Stop();
        }
        finally
        {
            pauseCts?.Cancel();
            pauseCts?.Dispose();
            pauseCts = null;
            PendingAsk = null;
            answer = null;
            if (subscribed)
            {
                chat.LogMessage -= onLogMessage;
                toasts.ErrorToast -= onErrorToast;
                subscribed = false;
            }
        }

        Succeeded = failure == null;
        var unitWord = BoughtUnits == 1 ? "unit" : "units";
        var result = failure == null
            ? $"Bought {BoughtUnits} {unitWord} for {Spent:N0} gil."
            : $"Stopped after {BoughtUnits} {unitWord} for {Spent:N0} gil: {failure}";
        var purchasePromptOpen = false;
        if (BoardReader.WindowVisible(BoardReader.YesnoAddon))
        {
            purchasePromptOpen = BoardReader.YesnoText() is not { } text || PurchaseText.IsPurchasePrompt(text);
            result += purchasePromptOpen
                ? " The purchase prompt is still open; answer it in the game."
                : " A prompt is still open; answer it in the game.";
        }
        // A click's prompt opens 40 to 60 ms after it; a run ended in between leaves it to come.
        if (clickUnanswered && !purchasePromptOpen)
            result += " A listing was clicked; if its purchase prompt opens, answer it in the game.";
        // Yes fired and WaitBought never reached an outcome, or failed on a signal that doesn't
        // rule the purchase out: the game may still complete it.
        if (listening || confirmedUnresolved) result += " A purchase was confirmed and may still go through.";
        listening = false;
        clickUnanswered = false;
        Result = result;
        if (failure != null && !reported) Report(failure, null, failureKnown);

        host.Telemetry?.Log($"boardbuy: {Result}");
        host.Telemetry?.Record("boardbuy-run", new Dictionary<string, object?>
        {
            ["world"] = worldName,
            ["lines"] = lineCount,
            ["units"] = BoughtUnits,
            ["spent"] = Spent,
            ["worldBudget"] = worldBudget,
            ["failure"] = failure,
            ["result"] = Result,
            ["skipped"] = skipped.ToList(),
        });

        CurrentName = null;
        host.RunFinished();
    }

    /// <summary>
    /// Ends the run when the board's search window closes under it (not while the run closes it),
    /// and when the queue emptied without reaching the finish task (a task timeout or an exception
    /// inside the task manager).
    /// </summary>
    private void OnUpdate(IFramework framework)
    {
        if (!Busy) return;

        if (!closing && !BoardReader.SearchOpen())
        {
            Fail("The market board closed.", known: true);
            Finish(abortQueue: true);
            return;
        }

        if (!tasks.IsBusy)
        {
            Fail("a step timed out");
            Finish(abortQueue: true);
        }
    }

    /// <summary>
    /// Gil on hand, or null when the game's inventory manager didn't resolve after a game update.
    /// ClientStructs throws rather than returning null there, and <see cref="Guard"/> runs from a
    /// window draw, so the failure is caught and logged once.
    /// </summary>
    private long? ReadGil()
    {
        try
        {
            return MainBags.ReadGil();
        }
        catch (InvalidOperationException ex)
        {
            if (!loggedGilUnavailable)
            {
                loggedGilUnavailable = true;
                log.Warning(ex, "The inventory manager is unavailable; board buying is off.");
            }
            return null;
        }
    }

    /// <summary>A 64-bit id as a string, so the devlog's JSON keeps every digit.</summary>
    private static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);
}
