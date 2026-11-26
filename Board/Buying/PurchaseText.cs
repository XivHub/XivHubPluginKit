using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XivHubPluginKit.Board;

namespace XivHubPluginKit.Board.Buying;

/// <summary>One board purchase prompt: the units asked, the gil charged and the fee it includes.</summary>
public readonly record struct PurchasePrompt(int Qty, long Gil, long Fee);

/// <summary>
/// Parses the board's confirm prompt and purchase log line (market-board.md, "Search and buy
/// flow"), in the English client text recorded there. Neither parser compares the item name: the
/// item is already fixed by <c>AgentItemSearch.ResultItemId</c> and the listing's
/// <see cref="BoardListing.ItemId"/> before a prompt is read.
/// </summary>
public static class PurchaseText
{
    /// <summary>
    /// Whether <paramref name="logMessageId"/> is a board purchase's success message
    /// (market-board.md, "Success"). LogMessage rows 734 and 735 both read "You purchase {item}.":
    /// 734 came for stackable items (shards, ore, a potion), 735 for a furnishing that doesn't stack
    /// (Butterfly Specimen, Furnisher devlog 2026-09-23 17:09:24). The text's quantity and the held
    /// count rising are what confirm a buy; the id only picks the message out.
    /// </summary>
    public static bool IsPurchasedLog(uint logMessageId) => logMessageId is 734 or 735;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private static readonly Regex PromptPattern = new(
        @"^Purchase (?<qty>\d[\d,]*) .+ for (?<gil>\d[\d,]*) gil( \((?<fee>\d[\d,]*) fee included\))?\?$",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private static readonly Regex PluralLogPattern = new(
        @"^You purchase (?<qty>\d[\d,]*) .+\.$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private static readonly Regex SingularLogPattern = new(
        @"^You purchase (?!\d).+\.$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Parses the board's confirm prompt, "Purchase N &lt;name&gt; for X gil (Y fee included)?"
    /// (sessions 2590cacb35c2, 2e74ce543511, 6746e5f916d5, 37c13073e8c8), including the one-unit
    /// form recorded as "Purchase 1 glade mansion roof (composite) for 5,227 gil (248 fee
    /// included)?" (session 6746e5f916d5) and "Purchase 1 potion for 24 gil (1 fee included)?"
    /// (session 108ba2790349). Every whitespace run collapses to one space and the text is trimmed
    /// before the match, since <c>AddonMaster.SelectYesno.TextLegacy</c> can carry a wrapped line,
    /// and game font icons are removed first: the live prompt puts U+E049 (the gil icon) after the
    /// fee, "(1\uE049 fee included)" (Furnisher devlog, 2026-09-23 17:04:56). Null when the text
    /// doesn't match.
    /// </summary>
    public static PurchasePrompt? ParsePrompt(string text)
    {
        var collapsed = Whitespace.Replace(StripIcons(text), " ").Trim();
        var m = PromptPattern.Match(collapsed);
        if (!m.Success)
            return null;
        return new PurchasePrompt(
            ParseInt(m.Groups["qty"].Value),
            ParseLong(m.Groups["gil"].Value),
            // A listing whose fee rounds down to 0 gets no fee clause at all: "Purchase 2 chunks of
            // tin ore for 2 gil?" (Furnisher devlog 2026-09-23 17:29:30).
            m.Groups["fee"].Success ? ParseLong(m.Groups["fee"].Value) : 0);
    }

    /// <summary>
    /// <paramref name="text"/> without the game font's icon glyphs, which the game draws from the
    /// Unicode private-use area (U+E000 to U+F8FF) inline in UI text, such as the gil icon.
    /// </summary>
    public static string StripIcons(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c is < '\uE000' or > '\uF8FF') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// Whether a <c>SelectYesno</c> text is the board's purchase prompt at all, whatever it asks
    /// for: every recorded one starts "Purchase " (see <see cref="ParsePrompt"/>). Leading
    /// whitespace is ignored, as in <see cref="ParsePrompt"/>.
    /// </summary>
    public static bool IsPurchasePrompt(string text) =>
        text.TrimStart().StartsWith("Purchase ", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="prompt"/> asks for exactly <paramref name="listing"/>: its quantity,
    /// its <see cref="BoardListing.Total"/> (the prompt's gil) and its <see cref="BoardListing.TotalTax"/>
    /// (the prompt's fee).
    /// </summary>
    public static bool Matches(PurchasePrompt prompt, BoardListing listing) =>
        prompt.Qty == listing.Qty && prompt.Gil == listing.Total && prompt.Fee == listing.TotalTax;

    /// <summary>
    /// Parses log messages 734 and 735: "You purchase N &lt;plural&gt;." for two units or more (session
    /// 2590cacb35c2, "You purchase 120 ice shards."), or "You purchase &lt;article&gt; &lt;name&gt;."
    /// for one unit: <c>Item.Singular</c>, preceded by "a" or "an" only when <c>Item.Article</c> is 0
    /// ("You purchase a potion.", session 108ba2790349). With <c>Article</c> 1 the singular is already
    /// complete ("You purchase the Bismarck.", Furnisher devlog 2026-09-23 17:38:49; 290 board items,
    /// such as "Sarnga"). No board item's <c>Singular</c> or <c>Plural</c> starts with a digit, so any
    /// purchase line whose text after "You purchase " doesn't start with one is one unit.
    /// Null for any other text, including other log messages.
    /// </summary>
    public static int? ParseBoughtLog(string text)
    {
        text = StripIcons(text);
        var m = PluralLogPattern.Match(text);
        if (m.Success)
            return ParseInt(m.Groups["qty"].Value);
        return SingularLogPattern.IsMatch(text) ? 1 : null;
    }

    private static int ParseInt(string s) => int.Parse(s, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

    private static long ParseLong(string s) => long.Parse(s, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
}
