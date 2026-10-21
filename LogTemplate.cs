using System;
using System.Globalization;
using System.Text;

namespace XivHubPluginKit;

/// <summary>
/// Renders a Serilog message template for a plain-text sink.
///
/// Dalamud's sinks take Serilog templates (<c>{Name}</c> holes), which are not
/// <see cref="string.Format"/> patterns, so this fills them itself rather than
/// passing them to <c>string.Format</c> and throwing on the first template it
/// meets. Named holes are filled positionally, which is what Serilog does for
/// a non-structured sink; a hole's format (<c>{Days:0}</c>) applies, under the
/// invariant culture so a client's locale cannot turn <c>14.5</c> into
/// <c>14,5</c>.
/// </summary>
public static class LogTemplate
{
    /// <summary>
    /// <paramref name="template"/> with its holes filled from
    /// <paramref name="values"/>. Anything malformed falls back to the raw
    /// template plus its arguments, so a logging bug can never lose the line.
    /// </summary>
    public static string Render(string template, object?[] values)
    {
        if (values.Length == 0) return template;
        try
        {
            var sb = new StringBuilder(template.Length + 32);
            int arg = 0;
            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] != '{') { sb.Append(template[i]); continue; }
                int close = template.IndexOf('}', i);
                if (close < 0) { sb.Append(template[i]); continue; }
                string hole = template.Substring(i + 1, close - i - 1);
                int colon = hole.IndexOf(':');
                string? format = colon >= 0 ? hole[(colon + 1)..] : null;
                sb.Append(arg < values.Length ? Text(values[arg], format) : "?");
                arg++;
                i = close;
            }
            return sb.ToString();
        }
        catch
        {
            return template + " " + string.Join(" ", values);
        }
    }

    private static string Text(object? value, string? format) => value switch
    {
        null => "null",
        IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };
}
