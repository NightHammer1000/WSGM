using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What the AutoTDP controller decided to do with the primary power limit.</summary>
internal enum AutoTdpAction
{
    /// <summary>Leave the current limit alone.</summary>
    Hold,

    /// <summary>Raise the limit one step because frames are missing their deadline.</summary>
    Raise,

    /// <summary>Try one step lower because delivery has been comfortable.</summary>
    Probe,

    /// <summary>Go back to the last limit that delivered, because the probe hurt.</summary>
    Restore,

    /// <summary>Hand the limit back to whoever set it manually.</summary>
    Release,
}

/// <summary>The plugin-published bounds of the primary power capability.</summary>
/// <param name="Minimum">Lowest limit the device accepts.</param>
/// <param name="Maximum">Highest limit the device accepts.</param>
/// <param name="Step">Smallest change the device accepts.</param>
internal sealed record AutoTdpLimits(int Minimum, int Maximum, int Step)
{
    /// <summary>Clamps a candidate limit onto the device grid.</summary>
    /// <param name="value">The candidate limit.</param>
    /// <returns>A limit the device accepts.</returns>
    internal int Clamp(int value) => Math.Clamp(value, Minimum, Maximum);

    /// <summary>Whether the bounds describe a usable control.</summary>
    internal bool IsUsable => Step > 0 && Minimum > 0 && Maximum >= Minimum + Step;
}

/// <summary>One observation window of frame delivery for the foreground application.</summary>
/// <param name="FrametimeMs">Mean frametime over the window.</param>
/// <param name="TargetFrametimeMs">The deadline that window was supposed to meet.</param>
/// <param name="Capped">Whether a frame limiter or vsync was holding delivery back.</param>
/// <param name="ContextKey">Application plus display/target context the sample belongs to.</param>
internal sealed record AutoTdpSample(
    double FrametimeMs,
    double TargetFrametimeMs,
    bool Capped,
    string ContextKey)
{
    /// <summary>Whether the window has usable numbers at all.</summary>
    internal bool IsMeasured =>
        double.IsFinite(FrametimeMs)
        && double.IsFinite(TargetFrametimeMs)
        && FrametimeMs > 0
        && TargetFrametimeMs > 0;
}

/// <summary>What the controller decided for one observation window.</summary>
/// <param name="Action">The kind of change.</param>
/// <param name="Watts">The limit that should now be in effect.</param>
/// <param name="Reason">Stable diagnostic token, for logs and replay assertions.</param>
internal sealed record AutoTdpDecision(AutoTdpAction Action, int Watts, string Reason)
{
    /// <summary>Whether this decision requires a hardware write.</summary>
    internal bool RequiresWrite => Action is not AutoTdpAction.Hold;
}

/// <summary>
/// The one deterministic AutoTDP control policy.
/// </summary>
/// <remarks>
/// Frametime behaviour only. Utilization counters are deliberately not consulted: a game that is
/// GPU-bound at 60% reported utilization and one that is CPU-bound both miss the same deadline, and
/// the only thing AutoTDP can do about either is move the power limit and watch what happens.
/// <para>
/// Pure and single-threaded on purpose. Every input arrives as an argument, every decision is a
/// return value, and the whole controller replays exactly from a recorded trace — which is how a
/// reported oscillation gets reproduced without the hardware that produced it.
/// </para>
/// </remarks>
internal sealed class AutoTdpController
{
    /// <summary>How far past its deadline a window has to land before it counts as a miss.</summary>
    /// <remarks>
    /// Not zero tolerance. A frametime mean sits a little above the deadline on a perfectly healthy
    /// capped game simply because the cap is enforced by sleeping, and raising power at every such
    /// window would walk straight to maximum and stay there.
    /// </remarks>
    internal const double MissRatio = 1.05;

    /// <summary>How comfortably a window has to beat its deadline before it counts as headroom.</summary>
    internal const double ComfortRatio = 0.92;

    /// <summary>Consecutive missed windows before power is raised.</summary>
    internal const int SustainedMisses = 3;

    /// <summary>Consecutive comfortable windows before a downward probe.</summary>
    /// <remarks>
    /// Deliberately much longer than <see cref="SustainedMisses"/>: raising power costs battery and
    /// fixes stutter, lowering it saves battery and risks stutter, so the two directions are not
    /// symmetric and must not share a threshold.
    /// </remarks>
    internal const int SettledWindows = 8;

    /// <summary>Windows ignored after a write, while the limit takes effect.</summary>
    internal const int SettleWindows = 2;

    /// <summary>Windows a downward probe is judged over before it is accepted.</summary>
    internal const int ProbeWindows = 6;

    private readonly Dictionary<string, int> _learnedFloor = new(StringComparer.Ordinal);
    private string _contextKey = string.Empty;
    private int _watts;
    private int _lastGood;
    private int _settling;
    private int _misses;
    private int _comfortable;
    private int _probeElapsed;
    private bool _probing;
    private bool _paused;

    /// <summary>The limit the controller believes is in effect.</summary>
    internal int Watts => _watts;

    /// <summary>Whether a manual change has suspended automatic control.</summary>
    internal bool IsPaused => _paused;

    /// <summary>Whether a downward probe is currently being judged.</summary>
    internal bool IsProbing => _probing;

    /// <summary>The limit AutoTDP found before the current probe.</summary>
    internal int LastGood => _lastGood;

    /// <summary>Starts control from the limit currently in effect.</summary>
    /// <param name="watts">The limit AutoTDP is taking over from.</param>
    /// <param name="limits">The device bounds.</param>
    /// <param name="contextKey">Application plus display context to start in.</param>
    /// <returns>The limit to begin at, which may be a learned floor for a known context.</returns>
    /// <remarks>
    /// A remembered floor is a starting point, not a promise. The controller still raises from it the
    /// moment the deadline is missed, so a game that got heavier since it was learned recovers on the
    /// same three windows as an unknown one.
    /// </remarks>
    internal int Start(int watts, AutoTdpLimits limits, string contextKey)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _contextKey = contextKey ?? string.Empty;
        _watts = limits.Clamp(watts);
        _lastGood = _watts;
        ResetWindows();
        _settling = 0;
        _paused = false;
        _probing = false;
        if (_learnedFloor.TryGetValue(_contextKey, out int floor))
        {
            _watts = limits.Clamp(Math.Max(floor, limits.Minimum));
            _lastGood = _watts;
        }

        return _watts;
    }

    /// <summary>Suspends automatic control because the limit was changed by hand.</summary>
    /// <param name="watts">The limit the user or a profile just set.</param>
    /// <remarks>
    /// The pause lasts until AutoTDP is switched off and on again. A user who moved the slider is
    /// telling the controller its answer was wrong, and silently taking the limit back a few seconds
    /// later is the single most confusing thing this feature could do.
    /// </remarks>
    internal void PauseForManualChange(int watts)
    {
        _paused = true;
        _probing = false;
        _watts = watts;
        _lastGood = watts;
        ResetWindows();
        _settling = 0;
    }

    /// <summary>Lifts a manual pause so automatic control judges the next window again.</summary>
    /// <remarks>
    /// The counterpart to <see cref="PauseForManualChange"/> for a <em>scoped</em> override: a limit
    /// set for one application pauses control while that application runs, and leaving the application
    /// must return control rather than leave it paused forever. This does not itself pick a wattage —
    /// the caller re-bases the controller on the value actually on the device next window, exactly as
    /// it does after an unapplied write — so the pause simply ends.
    /// <para>
    /// It is deliberately distinct from a user's global manual change, which still pauses until
    /// AutoTDP is switched off and on: that is the user overriding the controller, not a per-game
    /// profile expiring.
    /// </para>
    /// </remarks>
    internal void ResumeAutomaticControl()
    {
        _paused = false;
        _probing = false;
        ResetWindows();
        _settling = 0;
    }

    /// <summary>Evaluates one observation window.</summary>
    /// <param name="sample">The window to judge.</param>
    /// <param name="limits">Current device bounds.</param>
    /// <returns>What should happen to the power limit.</returns>
    internal AutoTdpDecision Evaluate(AutoTdpSample sample, AutoTdpLimits limits)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(limits);
        if (_paused)
        {
            return Hold("paused-manual");
        }

        if (!limits.IsUsable)
        {
            return Hold("limits-unusable");
        }

        if (!sample.IsMeasured)
        {
            // No telemetry is not evidence of comfort. The streak counters are dropped so a gap
            // cannot be stitched onto the windows either side of it and read as a settled period.
            ResetWindows();
            _settling = 0;
            return Hold("no-telemetry");
        }

        if (!string.Equals(sample.ContextKey, _contextKey, StringComparison.Ordinal))
        {
            // A different game or display mode is a different problem. Carrying streaks across the
            // boundary would judge the new context on the old one's evidence.
            _contextKey = sample.ContextKey;
            ResetWindows();
            _settling = 0;
            _probing = false;
            _lastGood = _watts;
            return Hold("context-changed");
        }

        if (_settling > 0)
        {
            _settling--;
            return Hold("settling");
        }

        double ratio = sample.FrametimeMs / sample.TargetFrametimeMs;
        bool missed = ratio > MissRatio;
        bool comfortable = ratio <= ComfortRatio || (sample.Capped && !missed);

        if (_probing)
        {
            return JudgeProbe(missed);
        }

        if (missed)
        {
            _comfortable = 0;
            _misses++;
            if (_misses < SustainedMisses)
            {
                return Hold("miss-unconfirmed");
            }

            return Raise(limits);
        }

        _misses = 0;
        if (!comfortable)
        {
            // Meeting the deadline without headroom is the state AutoTDP is aiming for. Probing
            // down from here would cost the frames it just secured.
            _comfortable = 0;
            return Hold("on-target");
        }

        _comfortable++;
        if (_comfortable < SettledWindows)
        {
            return Hold("settling-headroom");
        }

        return BeginProbe(limits);
    }

    /// <summary>Ends automatic control and reports the limit to restore.</summary>
    /// <param name="restoreTo">The manual or profile limit AutoTDP took over from.</param>
    /// <returns>The release decision.</returns>
    internal AutoTdpDecision Stop(int restoreTo)
    {
        _probing = false;
        _paused = false;
        ResetWindows();
        _settling = 0;
        _watts = restoreTo;
        _lastGood = restoreTo;
        return new AutoTdpDecision(AutoTdpAction.Release, restoreTo, "stopped");
    }

    /// <summary>The limit remembered as sufficient for a context, when one is known.</summary>
    /// <param name="contextKey">The context to look up.</param>
    /// <returns>The learned floor, or null.</returns>
    internal int? LearnedFloor(string contextKey) =>
        _learnedFloor.TryGetValue(contextKey, out int watts) ? watts : null;

    private AutoTdpDecision JudgeProbe(bool missed)
    {
        if (missed)
        {
            // The step we just removed was load-bearing. Going back is not enough on its own: the
            // context has to remember this floor, or the next settled period probes into the same
            // stutter again and the limit oscillates for as long as the game runs.
            _probing = false;
            _learnedFloor[_contextKey] = _lastGood;
            _watts = _lastGood;
            ResetWindows();
            _settling = SettleWindows;
            return new AutoTdpDecision(AutoTdpAction.Restore, _watts, "probe-rejected");
        }

        _probeElapsed++;
        if (_probeElapsed < ProbeWindows)
        {
            return Hold("probe-pending");
        }

        _probing = false;
        _lastGood = _watts;
        _learnedFloor[_contextKey] = _watts;
        ResetWindows();
        return Hold("probe-accepted");
    }

    private AutoTdpDecision BeginProbe(AutoTdpLimits limits)
    {
        int candidate = limits.Clamp(_watts - limits.Step);
        if (candidate >= _watts)
        {
            _comfortable = 0;
            return Hold("at-minimum");
        }

        if (_learnedFloor.TryGetValue(_contextKey, out int floor) && candidate < floor)
        {
            // Already known to be too little for this context. Re-probing it every settled period
            // would spend the rest of the session rediscovering the same answer.
            _comfortable = 0;
            return Hold("below-learned-floor");
        }

        _lastGood = _watts;
        _watts = candidate;
        _probing = true;
        _probeElapsed = 0;
        _settling = SettleWindows;
        _comfortable = 0;
        return new AutoTdpDecision(AutoTdpAction.Probe, _watts, "probe-down");
    }

    private AutoTdpDecision Raise(AutoTdpLimits limits)
    {
        int candidate = limits.Clamp(_watts + limits.Step);
        ResetWindows();
        if (candidate <= _watts)
        {
            return Hold("at-maximum");
        }

        _watts = candidate;
        _lastGood = candidate;
        _settling = SettleWindows;
        // A context that needed more power than the learned floor has outgrown it.
        _learnedFloor[_contextKey] = candidate;
        return new AutoTdpDecision(AutoTdpAction.Raise, _watts, "sustained-miss");
    }

    private AutoTdpDecision Hold(string reason) => new(AutoTdpAction.Hold, _watts, reason);

    private void ResetWindows()
    {
        _misses = 0;
        _comfortable = 0;
        _probeElapsed = 0;
    }
}
