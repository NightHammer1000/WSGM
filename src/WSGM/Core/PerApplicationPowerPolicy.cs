namespace WSGM.Core;

/// <summary>What to do with the device power limit when the running application changes.</summary>
internal enum PerAppPowerAction
{
    /// <summary>Write a concrete watt value the resolved layer specifies.</summary>
    Apply,

    /// <summary>Hand control back to AutoTDP, which the outgoing application's own limit had paused.</summary>
    ResumeAutomatic,

    /// <summary>Release the limit to the device ceiling: nothing is preferred and AutoTDP is off.</summary>
    ReleaseToCeiling,

    /// <summary>Change nothing. WSGM never imposed a limit, so it has none to take back.</summary>
    Leave,
}

/// <summary>One resolved power decision for an application transition.</summary>
/// <param name="Action">What the caller should do.</param>
/// <param name="Watts">The value for <see cref="PerAppPowerAction.Apply"/> and
/// <see cref="PerAppPowerAction.ReleaseToCeiling"/>; ignored otherwise.</param>
internal readonly record struct PerAppPowerDecision(PerAppPowerAction Action, int Watts);

/// <summary>
/// Pure per-application power-limit policy: which layer owns the limit, and what a transition to a
/// new application must do to the device so a limit set for one game does not leak onto the next
/// application or the desktop.
/// </summary>
/// <remarks>
/// The bug this exists to prevent: a limit set inside a game stayed on the device after the game
/// closed, silently becoming the global limit. The persisted model already carries a per-application
/// <c>TdpWatts</c> and a global one; this decides how they resolve and, crucially, what to do when
/// the resolved value is <em>absent</em> — which is where the leak happened, because "no preference"
/// was read as "keep whatever is currently on the device".
/// </remarks>
internal static class PerApplicationPowerPolicy
{
    /// <summary>The watt limit in force for an application, or null when none is preferred.</summary>
    /// <param name="globalWatts">The global limit preference, or null for none.</param>
    /// <param name="applicationWatts">The application's own limit preference, or null for none.</param>
    /// <param name="perGameProfileActive">
    /// Whether the application keeps its own profile. The per-game switch governs every performance
    /// value, so an application's limit applies only while its profile is enabled; otherwise the
    /// application inherits the global limit exactly as its frame limit and overlay level do.
    /// </param>
    /// <returns>The effective limit, or null when neither layer prefers one.</returns>
    internal static int? ResolveEffective(
        int? globalWatts,
        int? applicationWatts,
        bool perGameProfileActive) =>
        perGameProfileActive && applicationWatts is { } watts ? watts : globalWatts;

    /// <summary>Decides the device action for a transition to the resolved limit.</summary>
    /// <param name="effectiveWatts">The limit resolved for the new application, or null for none.</param>
    /// <param name="powerCurrentlyImposed">
    /// Whether WSGM's per-application feature is the reason the device currently holds a limit. Only
    /// then is there something to take back: a limit WSGM never set is not WSGM's to release.
    /// </param>
    /// <param name="autoTdpEnabled">Whether automatic control is switched on.</param>
    /// <param name="ceilingWatts">The device's maximum limit, used only for a release.</param>
    /// <returns>The action and, where relevant, the watts it carries.</returns>
    /// <remarks>
    /// A concrete preference is always applied, whether it came from the application or the global
    /// layer, because an explicit limit overrides automatic control the same way moving the slider
    /// does. When no preference exists, the outgoing application's limit is undone rather than left
    /// on the device: automatic control resumes if it is on, and otherwise the limit is released to
    /// the ceiling — but only when WSGM actually imposed the current one, so a session that never
    /// used the feature is never touched.
    /// </remarks>
    internal static PerAppPowerDecision DecideOnTargetChange(
        int? effectiveWatts,
        bool powerCurrentlyImposed,
        bool autoTdpEnabled,
        int ceilingWatts)
    {
        if (effectiveWatts is { } watts)
        {
            return new PerAppPowerDecision(PerAppPowerAction.Apply, watts);
        }

        if (!powerCurrentlyImposed)
        {
            return new PerAppPowerDecision(PerAppPowerAction.Leave, 0);
        }

        return autoTdpEnabled
            ? new PerAppPowerDecision(PerAppPowerAction.ResumeAutomatic, 0)
            : new PerAppPowerDecision(PerAppPowerAction.ReleaseToCeiling, ceilingWatts);
    }
}

/// <summary>What to do with the variable-refresh state when the running application changes.</summary>
internal enum PerAppVrrAction
{
    /// <summary>Write a concrete on/off state.</summary>
    Apply,

    /// <summary>Change nothing. WSGM never set a state, so it has none to take back.</summary>
    Leave,
}

/// <summary>One resolved variable-refresh decision for an application transition.</summary>
/// <param name="Action">What the caller should do.</param>
/// <param name="Enabled">The state to write for <see cref="PerAppVrrAction.Apply"/>.</param>
internal readonly record struct PerAppVrrDecision(PerAppVrrAction Action, bool Enabled);

/// <summary>
/// Pure per-application variable-refresh policy, the display twin of
/// <see cref="PerApplicationPowerPolicy"/>: which layer owns the VRR state and what a transition must
/// do so a state set for one game does not leak onto the next application or the desktop.
/// </summary>
/// <remarks>
/// Simpler than the power twin because there is no automatic controller to coordinate with. The one
/// judgement it makes is the restore baseline: when no state is preferred but WSGM had set one,
/// variable refresh returns to off — the state Steam's own model treats as the default and the one a
/// fixed-refresh desktop expects — rather than being left on because a game enabled it.
/// </remarks>
internal static class PerApplicationVrrPolicy
{
    /// <summary>The variable-refresh state in force for an application, or null when none is preferred.</summary>
    /// <param name="globalState">The global preference, or null for none.</param>
    /// <param name="applicationState">The application's own preference, or null for none.</param>
    /// <param name="perGameProfileActive">Whether the application keeps its own profile.</param>
    /// <returns>The effective state, or null when neither layer prefers one.</returns>
    internal static bool? ResolveEffective(
        bool? globalState,
        bool? applicationState,
        bool perGameProfileActive) =>
        perGameProfileActive && applicationState is { } state ? state : globalState;

    /// <summary>Decides the display action for a transition to the resolved state.</summary>
    /// <param name="effectiveState">The state resolved for the new application, or null for none.</param>
    /// <param name="stateCurrentlyImposed">
    /// Whether WSGM's per-application feature is the reason variable refresh currently holds a state.
    /// Only then is there something to take back to the default.
    /// </param>
    /// <returns>The action and, where relevant, the state it carries.</returns>
    internal static PerAppVrrDecision DecideOnTargetChange(
        bool? effectiveState,
        bool stateCurrentlyImposed) =>
        effectiveState is { } state
            ? new PerAppVrrDecision(PerAppVrrAction.Apply, state)
            : stateCurrentlyImposed
                ? new PerAppVrrDecision(PerAppVrrAction.Apply, false)
                : new PerAppVrrDecision(PerAppVrrAction.Leave, false);
}
