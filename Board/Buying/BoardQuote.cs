using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace XivHubPluginKit.Board.Buying;

/// <summary>
/// The units a quote takes from one world: how many, the cheapest and dearest unit price taken
/// there, the gil those listings cost before the fee, and the listings themselves, in
/// <see cref="MarketItem.Listings"/> order, for the route to show what a buyer clicks.
/// <see cref="GilWithTax"/> is what the game charges for them: each listing's gil plus its own
/// <see cref="BoardQuoter.Fee"/>.
/// </summary>
public sealed record WorldFill(
    uint WorldId,
    int Qty,
    long MinUnit,
    long MaxUnit,
    long ListedGil,
    IReadOnlyList<Listing> Stacks)
{
    public long GilWithTax => Stacks.Sum(s => BoardQuoter.Charged(s.UnitPrice * s.Qty));
}

/// <summary>
/// The price of n units on a data centre's board, bought as whole listings. <see cref="Gil"/> is
/// what the game charges: each taken listing's gil plus its <see cref="BoardQuoter.Fee"/>, and any
/// shortfall; <see cref="Unit"/> is that gil over n, the price per needed unit. <see cref="Short"/>
/// units were missing from the board and were priced as one more listing at its dearest price,
/// with its own fee; <see cref="OnBoard"/> is the units the taken listings hold, which can
/// exceed n since a listing can't be split. <see cref="DepthLimited"/> means the board holds more
/// units than the fetched listings showed, so the shortfall may be an artefact of the listing
/// depth. <see cref="Taken"/> are the indices into <see cref="MarketItem.Listings"/> of the
/// listings bought, ascending. <see cref="Surplus"/> is the units bought beyond n, since a
/// listing can't be split; it is 0 when <see cref="Short"/> is positive.
/// </summary>
public sealed record BoardQuote(
    long Gil,
    double Unit,
    IReadOnlyList<WorldFill> Worlds,
    int Short,
    int OnBoard,
    bool DepthLimited,
    IReadOnlyList<int> Taken,
    int Surplus);

public static class BoardQuoter
{
    /// <summary>
    /// The buyer's fee on one listing of <paramref name="listedGil"/> (its UnitPrice × Quantity):
    /// 5%, rounded down per listing. The game's <c>InfoProxyItemSearch.Listings[i].TotalTax</c>
    /// and its confirm prompt ("for X gil (Y fee included)") charge exactly this: 237 at 34 gil
    /// (8,058) pays 402, 960 at 54 (51,840) pays 2,592, 1 at 23 pays 1 (UiCapture sessions
    /// 37c13073e8c8 and 108ba2790349, every recorded listing).
    /// </summary>
    public static long Fee(long listedGil) => listedGil * 5 / 100;

    /// <summary>The gil the game charges for one listing of <paramref name="listedGil"/>.</summary>
    internal static long Charged(long listedGil) => listedGil + Fee(listedGil);

    /// <summary>
    /// The cheapest set of whole listings covering n units, as the game charges them. It comes
    /// from <c>mb_quote</c> (plan_house.py:177-199), which charged the n cheapest units; a listing
    /// can't be split, so a buyer who needs 3 from a 10-stack pays for 10. The objective, compared
    /// in order: gil charged, each listing's gil plus its <see cref="Fee"/>; then the number of
    /// listings (fewer purchases, fewer "already sold" chances); then units. The fee is floored
    /// per listing, so two listings can pay less fee than one of the same gil, and the objective
    /// sums each listing's charged gil rather than taking 5% of a total. Any tie left goes to the
    /// cover the scan of <see cref="MarketItem.Listings"/> completes first: its last listing is
    /// the earliest that ends any minimum cover, and the listings before it are, by the same rule,
    /// the cheapest way to the most units that last listing can complete n from. That is not
    /// always the lexicographically smallest index set: with 1, 2, 2 and 3 units at 20 gil and
    /// n = 4, both pairs charge 84, and it takes the two 2-stacks, not the 1 and the 3. With single-unit listings every
    /// cover is a set of units, and a unit's charged gil rises strictly with its price, so the
    /// result is <c>mb_quote</c>'s walk: the n cheapest units in listing order.
    /// <para>
    /// When the listings hold no more than n units they are all taken and the shortfall is priced
    /// as one more listing at the board's dearest price. Null for a null item, for n below 1
    /// (<c>mb_quote</c>'s walk stops before its first listing) and for an item with no listings.
    /// HQ and NQ listings both count.
    /// <see cref="BoardQuote.Worlds"/> follows <c>Counter.most_common</c>: most units first,
    /// first-taken first on a tie.
    /// </para>
    /// <para>
    /// When the cheapest-first prefix P of the candidates holds exactly n units, at last price p,
    /// it is taken without the DP if either condition below holds. Compare any other cover S
    /// through A = S \ P and B = P \ S: listings in both pay the same fee, so S costs more than
    /// P exactly when A charges more than B, and S holds at least n units, so A holds at least
    /// B's units. A listing of g gil charges between 1.05g − 0.95 and 1.05g, since the floor
    /// drops under 1 gil.
    /// </para>
    /// <para>
    /// The next candidate is strictly dearer than p. Then every unit of A costs at least p + 1
    /// and every unit of B at most p, so A's gil exceeds B's by D ≥ units(A), and A charges more
    /// than B by at least 1.05D − 0.95 × listings(A) ≥ 0.1 × units(A). A is empty only when S
    /// is P, so P is the unique minimum.
    /// </para>
    /// <para>
    /// Or every candidate at price p holds one unit, charged k = p + Fee(p). A listing of q
    /// units dearer than p charges at least q(p + 1) + q⌊(p + 1)/20⌋ &gt; qk, and one of q units
    /// cheaper than p at most q(p − 1) + ⌊q(p − 1)/20⌋ &lt; qk (by at least q/10). So A charges
    /// at least k × units(A) and B at most k × units(B), each strictly unless A, respectively B,
    /// is only singles at p, and A holding at least B's units makes P a minimum. A tie swaps
    /// singles at p one for one, so it ties on listings and units, and since P takes the
    /// earliest singles at p no tie ends on an earlier listing than P.
    /// </para>
    /// <para>
    /// Both rest on <see cref="MarketItem.Listings"/> being cheapest first and prices being
    /// positive integers.
    /// </para>
    /// </summary>
    public static BoardQuote? Quote(MarketItem? item, int n)
    {
        if (item is null || n < 1 || item.Listings.Count == 0)
            return null;

        var listings = item.Listings;
        var candidates = new List<int>(listings.Count);
        long candidateUnits = 0;
        for (var i = 0; i < listings.Count; i++)
        {
            candidates.Add(i);
            candidateUnits += listings[i].Qty;
        }

        var taken = candidateUnits <= n
            ? candidates
            : ExactPrefix(listings, candidates, n) ?? Cover(listings, candidates, n);
        return Build(item, n, taken, 0);
    }

    /// <summary>
    /// A board piece of <paramref name="piece"/> units and a mat of <paramref name="mat"/> units of
    /// the same item, priced together on one board: each listing goes to the piece, the mat or
    /// neither, and the two <see cref="BoardQuote.Taken"/> sets are disjoint. Quoting one buy and
    /// then the other from what is left can cost several times the joint minimum: with 5@3, 3@6
    /// and 5@20, a piece of 3 alone takes the 5@3 and leaves the mat of 5 the 5@20, 120 gil with
    /// fees, where the piece on the 3@6 and the mat on the 5@3 cost 33.
    /// <para>
    /// The objective, compared in order: the units short over both buys, so the board supplies
    /// all it can (a single quote's rule that a board holding no more than n is taken whole);
    /// the piece's own shortfall, so the piece is supplied before the mat; the gil, listings and
    /// units over both, as <see cref="Quote"/> compares them; then the piece's own gil, listings
    /// and units, so the piece keeps the cheapest share, and where quoting the piece first and
    /// the mat from the rest is already a joint minimum the piece's share costs what it would
    /// alone. Any tie left goes to the assignment the scan of <see cref="MarketItem.Listings"/>
    /// completes first. Each buy's shortfall is priced as one more listing at the board's dearest
    /// price, as in <see cref="Quote"/>. A buy is short only when every listing is taken, since an
    /// untaken listing given to the short buy would shorten the shortfall, so
    /// <see cref="BoardQuote.DepthLimited"/> compares the board's units for sale with every
    /// listing's. A piece's surplus units are not credited to the mat. Null for a null item, for
    /// either quantity below 1 and for an item with no listings.
    /// </para>
    /// </summary>
    public static (BoardQuote Piece, BoardQuote Mat)? QuoteJoint(MarketItem? item, int piece, int mat)
    {
        if (item is null || piece < 1 || mat < 1 || item.Listings.Count == 0)
            return null;

        var (pieceTaken, matTaken) = JointCover(item.Listings, piece, mat);
        var pieceUnits = pieceTaken.Sum(i => item.Listings[i].Qty);
        var matUnits = matTaken.Sum(i => item.Listings[i].Qty);
        return (Build(item, piece, pieceTaken, matUnits), Build(item, mat, matTaken, pieceUnits));
    }

    // The quote for n units over the taken listings. otherUnits is what the rest of the board
    // holds beyond the listings this buy could take, so a board holding more units for sale than
    // all the listings seen marks the shortfall as depth-limited.
    private static BoardQuote Build(MarketItem item, int n, IReadOnlyList<int> taken, int otherUnits)
    {
        var listings = item.Listings;
        long gilCharged = 0;
        var onBoard = 0;
        var fills = new List<(uint WorldId, int Qty, long MinUnit, long MaxUnit, long ListedGil, List<Listing> Stacks)>();
        var fillIndex = new Dictionary<uint, int>();
        foreach (var i in taken)
        {
            var listing = listings[i];
            var gil = listing.UnitPrice * listing.Qty;
            gilCharged += Charged(gil);
            onBoard += listing.Qty;
            if (fillIndex.TryGetValue(listing.WorldId, out var k))
            {
                var f = fills[k];
                f.Stacks.Add(listing);
                fills[k] = (f.WorldId, f.Qty + listing.Qty, Math.Min(f.MinUnit, listing.UnitPrice),
                    Math.Max(f.MaxUnit, listing.UnitPrice), f.ListedGil + gil, f.Stacks);
            }
            else
            {
                fillIndex[listing.WorldId] = fills.Count;
                fills.Add((listing.WorldId, listing.Qty, listing.UnitPrice, listing.UnitPrice, gil, [listing]));
            }
        }

        var shortBy = Math.Max(0, n - onBoard);
        if (shortBy > 0)
            gilCharged += Charged(shortBy * listings[^1].UnitPrice);

        // OrderByDescending is stable, so equal quantities keep first-taken order.
        var worlds = fills
            .OrderByDescending(f => f.Qty)
            .Select(f => new WorldFill(f.WorldId, f.Qty, f.MinUnit, f.MaxUnit, f.ListedGil, f.Stacks))
            .ToList();
        return new BoardQuote(
            gilCharged,
            (double)gilCharged / n,
            worlds,
            shortBy,
            onBoard,
            shortBy > 0 && item.UnitsForSale > onBoard + otherUnits,
            taken,
            Math.Max(0, onBoard - n));
    }

    // The cheapest-first prefix of the candidates when it holds exactly n units and is a minimum
    // under the full objective (the fast-path arguments on Quote), else null. The caller
    // guarantees the candidates hold more than n units, so the walk reaches n with a candidate
    // left over.
    private static List<int>? ExactPrefix(IReadOnlyList<Listing> listings, List<int> candidates, int n)
    {
        Debug.Assert(candidates.Zip(candidates.Skip(1)).All(p => listings[p.First].UnitPrice <= listings[p.Second].UnitPrice),
            "Both fast-path arguments need the candidates cheapest first.");
        long units = 0;
        var k = 0;
        while (units < n)
            units += listings[candidates[k++]].Qty;
        if (units != n)
            return null;

        var last = listings[candidates[k - 1]].UnitPrice;
        if (listings[candidates[k]].UnitPrice > last
            || candidates.All(i => listings[i].UnitPrice != last || listings[i].Qty == 1))
            return candidates.GetRange(0, k);
        return null;
    }

    // Capped 0/1 DP over the candidates. State q in 0..n is min(n, units taken); its value is
    // (gil charged, listings, units), compared lexicographically. Each listing's charged gil is
    // fixed, its own gil plus its own fee, so a set's charged gil is the sum over its listings.
    // Adding a listing adds the same vector to any value, so the order survives the cap: below n
    // the state fixes the units, and at n the units are part of the value. q runs downward so each listing is used at most once, and a
    // state changes only on a strictly smaller value: a state's final value is first reached at
    // the earliest listing that can end it, and within that listing from the largest q.
    // from[c, t] is the state listing c was added to when it last improved t; t = n is never a
    // source, since adding a listing to a cover only costs more. The caller guarantees the
    // candidates hold more than n units, so state n is reachable.
    private static List<int> Cover(IReadOnlyList<Listing> listings, List<int> candidates, int n)
    {
        var width = n + 1;
        var gil = new long[width];
        var count = new int[width];
        var units = new int[width];
        Array.Fill(gil, long.MaxValue);
        gil[0] = 0;
        var from = new int[candidates.Count * width];
        Array.Fill(from, -1);

        for (var c = 0; c < candidates.Count; c++)
        {
            var listing = listings[candidates[c]];
            var cost = Charged(listing.UnitPrice * listing.Qty);
            for (var q = n - 1; q >= 0; q--)
            {
                if (gil[q] == long.MaxValue)
                    continue;
                var t = (int)Math.Min(n, (long)q + listing.Qty);
                var g = gil[q] + cost;
                var k = count[q] + 1;
                var u = units[q] + listing.Qty;
                if (g < gil[t] || (g == gil[t] && (k < count[t] || (k == count[t] && u < units[t]))))
                {
                    gil[t] = g;
                    count[t] = k;
                    units[t] = u;
                    from[c * width + t] = q;
                }
            }
        }

        // The last listing to improve a state set its final value; its predecessor state holds
        // the value it had before that listing, so the walk continues below it.
        var taken = new List<int>();
        var at = n;
        for (var c = candidates.Count - 1; at > 0; c--)
        {
            var q = from[c * width + at];
            if (q < 0)
                continue;
            taken.Add(candidates[c]);
            at = q;
        }
        taken.Reverse();
        Debug.Assert(taken.Sum(i => Charged(listings[i].UnitPrice * listings[i].Qty)) == gil[n],
            "The rebuilt set must cost what the DP found.");
        return taken;
    }

    // Capped 0/1 DP over every listing for two buys. State (qp, qm), qp in 0..piece and qm in
    // 0..mat, is each buy's min(need, units taken), stored at qp * (mat + 1) + qm. Its value is
    // (gil charged, listings, units, piece gil charged, piece listings, piece units): giving a listing to the
    // piece adds its (gil, 1, units) to both halves, giving it to the mat adds it to the first
    // half only. Either way the vector added doesn't depend on the state, so as in Cover the
    // lexicographic order survives the cap. A move targets a state greater in one coordinate and
    // equal in the other (a full buy takes no more listings; that only costs more), so scanning
    // qp and then qm downward reads every source before this listing can change it.
    // from[c, t] packs the source state and the side (0 piece, 1 mat) of the move that last
    // improved t.
    private static (List<int> Piece, List<int> Mat) JointCover(IReadOnlyList<Listing> listings, int piece, int mat)
    {
        var row = mat + 1;
        var width = (piece + 1) * row;
        var value = new (long Gil, int Count, int Units, long PieceGil, int PieceCount, int PieceUnits)[width];
        var reached = new bool[width];
        reached[0] = true;
        var from = new int[listings.Count * width];
        Array.Fill(from, -1);

        for (var c = 0; c < listings.Count; c++)
        {
            var listing = listings[c];
            var cost = Charged(listing.UnitPrice * listing.Qty);
            for (var qp = piece; qp >= 0; qp--)
            {
                for (var qm = mat; qm >= 0; qm--)
                {
                    var s = qp * row + qm;
                    if (!reached[s])
                        continue;
                    var v = value[s];
                    if (qp < piece)
                    {
                        var t = (int)Math.Min(piece, (long)qp + listing.Qty) * row + qm;
                        Improve(c, t, (v.Gil + cost, v.Count + 1, v.Units + listing.Qty,
                            v.PieceGil + cost, v.PieceCount + 1, v.PieceUnits + listing.Qty), s << 1);
                    }
                    if (qm < mat)
                    {
                        var t = qp * row + (int)Math.Min(mat, (long)qm + listing.Qty);
                        Improve(c, t, (v.Gil + cost, v.Count + 1, v.Units + listing.Qty,
                            v.PieceGil, v.PieceCount, v.PieceUnits), (s << 1) | 1);
                    }
                }
            }
        }

        var end = -1;
        for (var qp = piece; qp >= 0; qp--)
        {
            for (var qm = mat; qm >= 0; qm--)
            {
                var s = qp * row + qm;
                if (reached[s] && (end < 0 || EndKey(s).CompareTo(EndKey(end)) < 0))
                    end = s;
            }
        }

        var pieceTaken = new List<int>();
        var matTaken = new List<int>();
        var at = end;
        for (var c = listings.Count - 1; at > 0; c--)
        {
            var move = from[c * width + at];
            if (move < 0)
                continue;
            ((move & 1) == 0 ? pieceTaken : matTaken).Add(c);
            at = move >> 1;
        }
        pieceTaken.Reverse();
        matTaken.Reverse();
        Debug.Assert(!pieceTaken.Intersect(matTaken).Any(), "A listing is bought once.");
        Debug.Assert(pieceTaken.Concat(matTaken).Sum(i => Charged(listings[i].UnitPrice * listings[i].Qty)) == value[end].Gil,
            "The rebuilt sets must cost what the DP found.");
        return (pieceTaken, matTaken);

        void Improve(int c, int t, (long, int, int, long, int, int) candidate, int move)
        {
            if (reached[t] && candidate.CompareTo(value[t]) >= 0)
                return;
            reached[t] = true;
            value[t] = candidate;
            from[c * width + t] = move;
        }

        (int Short, int PieceShort, (long, int, int, long, int, int) Value) EndKey(int s) =>
            (piece - s / row + mat - s % row, piece - s / row, value[s]);
    }
}
