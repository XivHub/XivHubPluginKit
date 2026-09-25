using System.Collections.Generic;

namespace XivHubPluginKit.Pinch;

/// <summary>Per-run pricing policy, read fresh on every use.</summary>
public readonly record struct PinchSettings(
    int PerItemDelayMs, int MarketBoardDelayMs, bool SkipIfNoCompetitor, bool AnnounceEachReprice);

/// <summary>
/// What to do with one listing: the price to write, or null to leave it where
/// it is, plus the phrase that explains the choice and whether it is worth a
/// chat announce.
/// </summary>
public readonly record struct PriceDecision(uint? Write, string Why, bool Announce);

/// <summary>One price actually written, for a caller's summary and events.</summary>
public readonly record struct PinchWrite(uint ItemId, bool Hq, uint OldPrice, uint NewPrice);

/// <summary>One retainer queued for a run.</summary>
public readonly record struct PinchRetainer(ulong Cid, string Name);

/// <summary>
/// What a plan delegate decided for one retainer's sell list: which rows get a
/// live board lookup.
/// </summary>
public sealed class RetainerPlan
{
    /// <summary>Item id routed to a live board lookup, keyed by the row's name and quality.</summary>
    public Dictionary<(string Name, bool Hq), uint> Live { get; set; } = new();

    /// <summary>Live-lookup rows counted toward the early-exit budget.</summary>
    public int LiveRows { get; set; }

    public int Skipped { get; set; }
    public int NoData { get; set; }
}

/// <summary>Row counts for a dry run that never opened a window.</summary>
public readonly record struct DryRunVisit(int Visit, int Total);

/// <summary>Outcome of one retainer's run.</summary>
public readonly record struct PinchRetainerResult(
    ulong Cid, string Name, int Rows, int Reprices, int Skipped, int NoData,
    IReadOnlyList<PinchWrite> Writes, DryRunVisit? DryRun);

/// <summary>Outcome of a whole-roster session.</summary>
public readonly record struct PinchSessionResult(
    int Retainers, int Reprices, int Skipped, int NoData, int Rows, bool Cancelled);
