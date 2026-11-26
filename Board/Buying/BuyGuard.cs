using System;
using System.Collections.Generic;
using XivHubPluginKit.Board;

namespace XivHubPluginKit.Board.Buying;

/// <summary>What a board buy should do for one line's remaining need.</summary>
public enum BuyVerdict { Nothing, Buy, Ask, Skip }

/// <summary>
/// A guard's decision for one line: the verdict, the comparison it decided from (null only
/// when <see cref="BuyVerdict.Nothing"/> was reached before any board read), and the reason shown
/// to the owner.
/// </summary>
public sealed record BuyDecision(BuyVerdict Verdict, BoardComparison? Comparison, string Reason);

/// <summary>
/// The board buyer's thresholds: an overage within <see cref="BuyPercent"/> of the plan or
/// <see cref="BuyGil"/> buys, within <see cref="AskPercent"/> or <see cref="AskGil"/> asks.
/// </summary>
public readonly record struct BuyThresholds(int BuyPercent, long BuyGil, int AskPercent, long AskGil)
{
    /// <summary>
    /// The larger of <paramref name="percent"/> (clamped to 0-1000) of <paramref name="plan"/>,
    /// rounded down, and <paramref name="gil"/> (at least 0).
    /// </summary>
    public static long Allowance(long plan, int percent, long gil) =>
        Math.Max(Math.Max(0, gil), Math.Max(0, plan) * Math.Clamp(percent, 0, 1000) / 100);
}

/// <summary>
/// Whether a board buy of a line's remaining need goes through, is asked first, or is skipped,
/// and the run-level budget and reserve checks around it.
/// </summary>
public static class BuyGuard
{
    /// <summary>
    /// The verdict for <paramref name="remaining"/> units of <paramref name="line"/>'s item against
    /// <paramref name="live"/>'s current listings: <see cref="BoardCompare.Compare"/>, then
    /// <see cref="Verdict"/>. A listing crosses the need only when it belongs to the live minimum
    /// cover for <paramref name="remaining"/> (<see cref="LiveBoard.Cover"/>): the comparison, not
    /// the thresholds, decides which listings are candidates.
    /// </summary>
    public static BuyDecision Decide(BuyLine line, int remaining, IReadOnlyList<BoardListing> live,
        IReadOnlyCollection<ulong> ownRetainers, BuyThresholds thresholds)
    {
        if (remaining < 1)
            return new BuyDecision(BuyVerdict.Nothing, null, "Nothing left to buy here.");

        return Verdict(BoardCompare.Compare(line, remaining, live, ownRetainers)!, thresholds);
    }

    /// <summary>
    /// The thresholds over one comparison, on the cover's overage: its pre-tax
    /// <see cref="LiveCover.Listed"/> minus <see cref="BoardComparison.PlanListed"/>. At or under the
    /// plan buys; within the buy allowance buys; within the ask allowance asks; past it skips. Each
    /// allowance is a percent of the plan or a gil amount, whichever is larger
    /// (<see cref="BuyThresholds.Allowance"/>): a percent alone means nothing on a 50-gil line, and
    /// a gil amount alone nothing on a 500,000-gil one.
    /// </summary>
    public static BuyDecision Verdict(BoardComparison c, BuyThresholds t)
    {
        if (c.Live is not { } cover)
            return new BuyDecision(BuyVerdict.Skip, c, "No listings on this world.");

        var over = cover.Listed - c.PlanListed;
        if (over <= 0)
            return new BuyDecision(BuyVerdict.Buy, c, "At or below the plan's price.");
        if (over <= BuyThresholds.Allowance(c.PlanListed, t.BuyPercent, t.BuyGil))
            return new BuyDecision(BuyVerdict.Buy, c,
                $"Within {t.BuyPercent}% or {t.BuyGil:N0} gil of the plan's price.");
        if (over > BuyThresholds.Allowance(c.PlanListed, t.AskPercent, t.AskGil))
            return new BuyDecision(BuyVerdict.Skip, c,
                $"More than {t.AskPercent}% and {t.AskGil:N0} gil above the plan's price.");
        return new BuyDecision(BuyVerdict.Ask, c, $"{OverText(cover.Gil - c.PlanGil, c.DiffPercent)} over the plan's price.");
    }

    /// <summary>"+1 gil (+0.05%)": the charged difference, with the percent when there is one.</summary>
    public static string OverText(long gil, double? percent) =>
        percent is { } p ? $"{gil:+#,0;-#,0;0} gil ({p:+0.##;-0.##;0}%)" : $"{gil:+#,0;-#,0;0} gil";

    /// <summary>
    /// A run's budget for <paramref name="stop"/>: its <see cref="BuyStop.GilLeft"/> plus each open
    /// line's buy allowance, so lines bought within their allowance never add up to a budget ask.
    /// </summary>
    public static long Budget(BuyStop stop, BuyThresholds t)
    {
        var budget = stop.GilLeft;
        foreach (var l in stop.Open)
            budget += BuyThresholds.Allowance(l.PlanGil, t.BuyPercent, t.BuyGil);
        return budget;
    }

    /// <summary>
    /// Turns a <see cref="BuyVerdict.Buy"/> that would take a run's spend past
    /// <paramref name="worldBudget"/> (<see cref="Budget"/> when the run starts) into
    /// <see cref="BuyVerdict.Ask"/>, so a board price drop mid-run can't spend past what the stop
    /// was budgeted for. Every other verdict passes through unchanged.
    /// </summary>
    public static BuyVerdict WithinBudget(BuyVerdict verdict, long spentThisRun, long cost, long worldBudget) =>
        verdict == BuyVerdict.Buy && spentThisRun + cost > worldBudget ? BuyVerdict.Ask : verdict;

    /// <summary>Whether spending <paramref name="cost"/> keeps at least <paramref name="reserve"/> gil on hand.</summary>
    public static bool KeepsReserve(long gil, long reserve, long cost) => gil - cost >= Math.Max(0, reserve);

    /// <summary>
    /// <paramref name="verdict"/> under <paramref name="policy"/>: <see cref="AskPolicy.SkipAbove"/>
    /// turns an <see cref="BuyVerdict.Ask"/> into <see cref="BuyVerdict.Skip"/>, so a run nobody
    /// watches never waits on an answer and never buys past the buy allowance.
    /// </summary>
    public static BuyVerdict UnderPolicy(BuyVerdict verdict, AskPolicy policy) =>
        policy == AskPolicy.SkipAbove && verdict == BuyVerdict.Ask ? BuyVerdict.Skip : verdict;
}
