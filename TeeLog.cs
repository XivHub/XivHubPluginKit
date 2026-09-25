using System;
using Dalamud.Plugin.Services;
using Serilog.Events;

namespace XivHubPluginKit;

/// <summary>
/// An <see cref="IPluginLog"/> that forwards to the real one and also mirrors
/// each line to <see cref="DevTelemetry"/>.
///
/// Formatting is deliberate: Dalamud's sinks take Serilog message templates
/// (<c>{Name}</c> placeholders), which are not <see cref="string.Format"/>
/// patterns, so the mirror renders them itself rather than passing them to
/// <c>string.Format</c> and throwing on the first template it meets.
/// </summary>
public sealed class TeeLog(IPluginLog inner, DevTelemetry telemetry) : IPluginLog
{
    public LogEventLevel MinimumLogLevel
    {
        get => inner.MinimumLogLevel;
        set => inner.MinimumLogLevel = value;
    }

    public Serilog.ILogger Logger => inner.Logger;

    /// <summary>
    /// Render a Serilog template against its arguments. Named holes are filled
    /// positionally, which is what Serilog does for a non-structured sink, and
    /// anything malformed falls back to the raw template plus its arguments so
    /// a logging bug can never lose the line.
    /// </summary>
    private static string Render(string template, object[] values)
    {
        if (values.Length == 0) return template;
        try
        {
            var sb = new System.Text.StringBuilder(template.Length + 32);
            int arg = 0;
            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] != '{') { sb.Append(template[i]); continue; }
                int close = template.IndexOf('}', i);
                if (close < 0) { sb.Append(template[i]); continue; }
                sb.Append(arg < values.Length ? values[arg]?.ToString() ?? "null" : "?");
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

    private void Mirror(string level, string template, object[] values)
        => telemetry.Log($"{level} {Render(template, values)}");

    private void Mirror(string level, Exception? ex, string template, object[] values)
        => telemetry.Log(ex is null
            ? $"{level} {Render(template, values)}"
            : $"{level} {Render(template, values)} :: {ex.GetType().Name}: {ex.Message}");

    public void Fatal(string m, params object[] v) { inner.Fatal(m, v); Mirror("FTL", m, v); }
    public void Fatal(Exception? e, string m, params object[] v) { inner.Fatal(e, m, v); Mirror("FTL", e, m, v); }
    public void Error(string m, params object[] v) { inner.Error(m, v); Mirror("ERR", m, v); }
    public void Error(Exception? e, string m, params object[] v) { inner.Error(e, m, v); Mirror("ERR", e, m, v); }
    public void Warning(string m, params object[] v) { inner.Warning(m, v); Mirror("WRN", m, v); }
    public void Warning(Exception? e, string m, params object[] v) { inner.Warning(e, m, v); Mirror("WRN", e, m, v); }
    public void Information(string m, params object[] v) { inner.Information(m, v); Mirror("INF", m, v); }
    public void Information(Exception? e, string m, params object[] v) { inner.Information(e, m, v); Mirror("INF", e, m, v); }
    public void Info(string m, params object[] v) { inner.Info(m, v); Mirror("INF", m, v); }
    public void Info(Exception? e, string m, params object[] v) { inner.Info(e, m, v); Mirror("INF", e, m, v); }
    public void Debug(string m, params object[] v) { inner.Debug(m, v); Mirror("DBG", m, v); }
    public void Debug(Exception? e, string m, params object[] v) { inner.Debug(e, m, v); Mirror("DBG", e, m, v); }
    public void Verbose(string m, params object[] v) { inner.Verbose(m, v); Mirror("VRB", m, v); }
    public void Verbose(Exception? e, string m, params object[] v) { inner.Verbose(e, m, v); Mirror("VRB", e, m, v); }

    public void Write(LogEventLevel level, Exception? e, string m, params object[] v)
    {
        inner.Write(level, e, m, v);
        Mirror(level.ToString(), e, m, v);
    }
}
