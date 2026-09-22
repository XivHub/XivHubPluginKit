using System;
using Dalamud.Utility;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Callback = ECommons.Automation.Callback;

namespace XivHubPluginKit.Shop;

/// <summary>
/// Unwinds an open NPC or retainer conversation after a Stop. Two rules, both
/// learned the hard way, and this is the one place that keeps them.
///
/// **Leave an addon through its own exit path, never <c>Close(true)</c>.**
/// Hiding the widget does not exit the event behind it: the player is left able
/// to move but unable to interact with anything or even drop the target, with
/// no UI to escape through, and the only way out is a client restart. The
/// retainer list obeys the same rule: its menu returns to <c>RetainerList</c>
/// only through its own "Quit" entry.
///
/// **Unwind across frames, not in one.** The game handles one cancel per frame,
/// and a confirmation raised by the cancel itself only appears the frame after,
/// so a single pass leaves whatever it uncovered sitting on screen.
/// </summary>
public static class ConversationUnwinder
{
    /// <summary>
    /// One addon to leave. <see cref="Leave"/> overrides the default cancel, and
    /// <see cref="Agent"/> is the agent behind the window, if it has one.
    /// </summary>
    public readonly record struct Step(string Addon, Func<nint, bool>? Leave = null, AgentId? Agent = null);

    private const string ThrottleName = "KitUnwind";
    private const int StepDelayMs = 200;
    private const int DefaultBudgetMs = 8000;

    private static readonly object Gate = new();
    private static IReadOnlyList<Step> _steps = [];
    private static long _deadlineMs;
    private static bool _running;
    private static Action<string>? _log;
    private static readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

    /// <summary>Cancels before escalating to the agent, and again before forcing.</summary>
    private const int CancelAttempts = 3;
    private const int AgentAttempts = 3;

    /// <summary>
    /// Begin unwinding. <paramref name="innermostFirst"/> is checked in order
    /// every tick, so a dialog that appears part-way through is still caught.
    /// Calling again while running just refreshes the list and the budget.
    /// </summary>
    public static void Start(
        IReadOnlyList<Step> innermostFirst, Action<string>? log = null, int budgetMs = DefaultBudgetMs)
    {
        lock (Gate)
        {
            _steps = innermostFirst;
            _log = log;
            _attempts.Clear();
            _deadlineMs = Environment.TickCount64 + budgetMs;
            if (_running) return;
            _running = true;
            Svc.Framework.Update += Tick;
        }
    }

    /// <summary>True while an unwind is still in progress.</summary>
    public static bool IsRunning { get { lock (Gate) return _running; } }

    private static void Finish()
    {
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            Svc.Framework.Update -= Tick;
        }
    }

    private static unsafe void Tick(IFramework _)
    {
        if (Environment.TickCount64 > _deadlineMs)
        {
            _log?.Invoke($"unwind: gave up after the budget; still visible: {StillVisible()}");
            Finish();
            return;
        }
        if (!EzThrottler.Throttle(ThrottleName, StepDelayMs)) return;

        foreach (var step in _steps)
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(step.Addon, out var addon)
                || !addon->IsVisible)
                continue;

            // One per tick: the game will not process a second this frame, and
            // the next may not even exist yet.
            // Escalate rather than repeat. A shop window ignores the cancel
            // callback outright — an abort once fired it seventeen times in
            // three seconds with the window still up — so after a few tries
            // this asks the agent behind the window to close, and only then
            // resorts to hiding it.
            int n = _attempts.GetValueOrDefault(step.Addon) + 1;
            _attempts[step.Addon] = n;

            if (step.Leave is not null)
            {
                _log?.Invoke($"unwind: leaving {step.Addon} (attempt {n})");
                step.Leave((nint)addon);
                return;
            }

            if (n <= CancelAttempts)
            {
                _log?.Invoke($"unwind: cancelling {step.Addon} (attempt {n})");
                Callback.Fire(addon, true, -1);
                return;
            }

            if (step.Agent is { } agentId && n <= CancelAttempts + AgentAttempts)
            {
                _log?.Invoke($"unwind: {step.Addon} ignored cancel; hiding its agent");
                var agent = AgentModule.Instance()->GetAgentByInternalId(agentId);
                if (agent != null && agent->IsAgentActive())
                {
                    agent->Hide();
                    return;
                }
            }

            // Last resort. Hiding does not exit the event, but a window that
            // answers nothing else has already left the player stuck, and a
            // hidden one at least lets them move on.
            _log?.Invoke($"unwind: forcing {step.Addon} closed");
            addon->Close(true);
            return;
        }

        _log?.Invoke("unwind: nothing left on screen");
        // A vendor left selected is the visible half of "still stuck"; drop it
        // so the player can see the conversation really did end.
        if (Svc.Targets.Target is not null)
            Svc.Targets.Target = null;
        Finish();
    }

    /// <summary>Which of the steps' addons are still up, for the give-up message.</summary>
    private static unsafe string StillVisible()
    {
        var seen = new List<string>();
        foreach (var step in _steps)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(step.Addon, out var addon) && addon->IsVisible)
                seen.Add(step.Addon);
        }
        return seen.Count == 0 ? "none" : string.Join(", ", seen);
    }

    /// <summary>
    /// Leave a <c>SelectString</c> that might be the retainer menu. That one only
    /// returns control to <c>RetainerList</c> through its localised "Quit" entry
    /// (Addon sheet row 2383); an NPC's menu has no such entry and takes the
    /// ordinary cancel.
    /// </summary>
    public static unsafe bool LeaveSelectString(nint ptr)
    {
        var addon = (AtkUnitBase*)ptr;
        if (!GenericHelpers.IsAddonReady(addon))
            return false;

        var quit = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
            .GetRow(2383).Text.ToDalamudString().GetText();
        var ss = new AddonMaster.SelectString(addon);
        for (int i = 0; i < ss.Entries.Length; i++)
        {
            if (ss.Entries[i].Text == quit)
            {
                ss.Entries[i].Select();
                return true;
            }
        }
        // No Quit: an NPC menu, whose exit is its last entry ("Nothing"). The
        // cancel callback hides it without ending the event, which is what
        // leaves the NPC selected with Escape doing nothing.
        if (ss.Entries.Length > 0)
        {
            ss.Entries[^1].Select();
            return true;
        }
        Callback.Fire(addon, true, -1);
        return true;
    }

    /// <summary>
    /// Leave an NPC's icon menu through its last entry ("Nothing"), which is
    /// what actually ends the conversation. See <see cref="LeaveSelectString"/>.
    /// </summary>
    public static unsafe bool LeaveSelectIconString(nint ptr)
    {
        var addon = (AtkUnitBase*)ptr;
        if (!GenericHelpers.IsAddonReady(addon)) return false;
        var sis = new AddonMaster.SelectIconString(addon);
        if (sis.Entries.Length > 0)
        {
            sis.Entries[^1].Select();
            return true;
        }
        Callback.Fire(addon, true, -1);
        return true;
    }

    /// <summary>
    /// Every addon a vendor purchase can be sitting in, innermost first.
    /// </summary>
    public static IReadOnlyList<Step> ShopSteps { get; } =
    [
        new("SelectYesno"),
        new("Shop", Agent: AgentId.Shop),
        new("ShopExchangeCurrency", Agent: AgentId.Shop),
        new("SelectIconString", LeaveSelectIconString),
        new("SelectString", LeaveSelectString),
    ];
}
