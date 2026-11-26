using System;
using System.Collections.Generic;
using System.Linq;

namespace XivHubPluginKit.Board.Buying;

/// <summary>
/// The plan's price for some units of a line: <see cref="Listed"/> before the board's fee and
/// <see cref="Gil"/> with each listing's fee (<see cref="BoardQuoter.Fee"/>), as the game charges.
/// </summary>
public readonly record struct PlanPrice(long Listed, long Gil)
{
    /// <summary>
    /// <paramref name="units"/> at <paramref name="unitGil"/> each, priced as one listing: the plan
    /// of a caller that knows a unit price rather than the listings it came from.
    /// </summary>
    public static PlanPrice AtUnit(long unitGil, int units)
    {
        var listed = unitGil * units;
        return new PlanPrice(listed, listed + BoardQuoter.Fee(listed));
    }
}

/// <summary>
/// One item to buy on the current world. <see cref="Units"/> is what is left to buy here and
/// <see cref="PlanGil"/> the plan's gil for them, fees included. <see cref="Plan"/> prices any
/// number of the units the board can supply now, null when the plan has no price for them.
/// <see cref="Hq"/> fixes the quality bought; null takes either. <see cref="Kind"/> tells two lines
/// of one item apart (a piece and a material) and is written to the devlog. Crystals are counted
/// in the crystal bag and capped at 9,999 (<see cref="IsCrystal"/>).
/// </summary>
public sealed record BuyLine(
    uint ItemId,
    string Name,
    bool? Hq,
    bool IsCrystal,
    int Units,
    long PlanGil,
    Func<int, PlanPrice?> Plan,
    string Kind);

/// <summary>One world's lines, in the order they are bought.</summary>
public sealed record BuyStop(uint WorldId, IReadOnlyList<BuyLine> Lines)
{
    /// <summary>The lines with units left to buy.</summary>
    public IEnumerable<BuyLine> Open => Lines.Where(l => l.Units > 0);

    /// <summary>Whether no line has units left to buy.</summary>
    public bool Done => !Open.Any();

    /// <summary>The plan's gil for every open line, fees included.</summary>
    public long GilLeft => Open.Sum(l => l.PlanGil);
}

/// <summary>
/// What a run does with a cover the thresholds would ask about: <see cref="Ask"/> waits for the
/// owner's answer; <see cref="SkipAbove"/> skips the line, for a run nobody is watching.
/// </summary>
public enum AskPolicy { Ask, SkipAbove }
