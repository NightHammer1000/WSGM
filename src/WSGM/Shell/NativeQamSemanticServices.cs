using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>The display-resolution row's state, for the native quick-access menu.</summary>
/// <param name="Available">Whether the row can be drawn at all.</param>
/// <param name="Options">
/// Resolutions to offer, as <c>WIDTHxHEIGHT</c>. Empty hides the row: a picker with nothing to pick
/// is worse than no picker.
/// </param>
/// <param name="Current">The resolution in force, or empty when it cannot be read.</param>
/// <param name="StatusText">Why the row is unavailable, when it is.</param>
/// <remarks>
/// Hand-built rather than reactivated, unlike the frame limit and VRR rows: SteamOS drives
/// resolution through gamescope and this client ships no component for it, so there is nothing to
/// mount and the row is WSGM's own.
/// </remarks>
internal sealed record NativeQamResolutionState(
    bool Available,
    IReadOnlyList<string> Options,
    string Current,
    string StatusText);

/// <summary>The unified frame-limit row, shaped like SteamOS's own.</summary>
/// <remarks>
/// One continuous slider bookended by the panel's limits, plus a separate switch for off — verified
/// against a Steam Deck showing "60 FPS (60 Hz)" between bookends 10 and 60. There are no notches
/// under any strategy: the cap is a free number and the PAIRING is what snaps to a mode the panel
/// can hold, which is exactly the merge Valve made when it unified the two rows.
/// </remarks>
internal sealed record NativeQamFrameLimitState(
    bool Available,
    int? MinimumFps,
    int? MaximumFps,
    int? DesiredFps,
    int? ObservedFps,
    string Progress,
    string Fault,
    string StatusText,
    bool LimitEnabled = false,
    IReadOnlyDictionary<int, int>? RefreshForCap = null,
    int? RefreshMinHz = null,
    int? RefreshMaxHz = null,
    int? CurrentRefreshHz = null,
    IReadOnlyList<int>? RefreshRates = null);

internal sealed record NativeQamTdpState(
    bool Available,
    int? MinimumWatts,
    int? MaximumWatts,
    int? StepWatts,
    int? DesiredWatts,
    int? ObservedWatts,
    string Progress,
    string StatusText);

/// <summary>One bounded integer device control projected into Quick Settings.</summary>
internal sealed record NativeQamDeviceRangeState(
    bool Available,
    int Minimum,
    int Maximum,
    int Step,
    int? Desired,
    int? Observed,
    string Progress,
    string StatusText);

/// <summary>One independently writable lighting zone.</summary>
internal sealed record NativeQamLightingZoneState(
    string Id,
    string Label,
    bool Available,
    int? DesiredColor,
    int? ObservedColor,
    string Progress,
    string StatusText);

/// <summary>Device charging and lighting controls shown in Steam Quick Settings.</summary>
internal sealed record NativeQamDeviceControlsState(
    NativeQamDeviceRangeState? ChargeLimit,
    NativeQamDeviceRangeState? LightingBrightness,
    IReadOnlyList<NativeQamLightingZoneState> LightingZones);

/// <summary>The variable-refresh switch as WSGM's own row renders it.</summary>
/// <param name="Available">Whether a device capability backs the switch at all.</param>
/// <param name="Enabled">What the device reports now, not what was last asked for.</param>
/// <param name="Progress">Command progress in the shared vocabulary.</param>
/// <param name="StatusText">One line describing the state, or why the row cannot be operated.</param>
internal sealed record NativeQamVrrState(
    bool Available,
    bool Enabled,
    string Progress,
    string StatusText);

/// <summary>AutoTDP as Steam's own menu renders it.</summary>
/// <remarks>
/// Deliberately more than a boolean. A switch that only says "on" leaves a user watching the power
/// limit move with no way to tell control from a fault, so the state carries what AutoTDP is
/// actually doing: the watts it settled on, whether it is controlling, waiting, paused or unable to
/// run, and why.
/// </remarks>
/// <param name="Available">Whether the switch may be operated at all.</param>
/// <param name="Enabled">The stored setting, which is what the switch shows.</param>
/// <param name="Controlling">Whether AutoTDP is currently moving the power limit.</param>
/// <param name="Watts">The limit AutoTDP settled on, when it has one.</param>
/// <param name="Progress">Command progress in the shared vocabulary.</param>
/// <param name="StatusText">One line describing what it is doing, or why it cannot.</param>
internal sealed record NativeQamAutoTdpState(
    bool Available,
    bool Enabled,
    bool Controlling,
    int? Watts,
    string Progress,
    string StatusText);

internal sealed record NativeQamControllerTargetOption(
    string Id,
    string Label,
    bool Available);

internal sealed record NativeQamControllerTargetState(
    bool Available,
    IReadOnlyList<NativeQamControllerTargetOption> Targets,
    string SelectedTarget,
    string ObservedTarget,
    string Progress,
    string StatusText,
    bool ApplicationRestartRequired);

/// <summary>Shared shaping for the text these projections hand to Steam's page.</summary>
internal static class NativeQamText
{
    /// <summary>Longest status text a projection sends.</summary>
    /// <remarks>
    /// Plugin and driver messages have no useful display length guarantee. The page has one line,
    /// so longer text is truncated before delivery.
    /// </remarks>
    private const int MaximumLength = 240;

    /// <summary>Normalizes an optional detail into bounded, renderable text.</summary>
    /// <param name="value">The detail, which may be null, blank, or arbitrarily long.</param>
    /// <returns>The empty string for nothing to say, otherwise the text within the bound.</returns>
    internal static string Bound(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= MaximumLength ? value : value[..MaximumLength];
}

/// <summary>UI-thread marshalling for services whose backing managers own observable UI state.</summary>
/// <remarks>
/// The radio and audio managers reconcile observable collections the taskbar binds to, so their
/// calls are UI-thread owned while bridge requests arrive off the bridge's own thread.
/// </remarks>
internal static class NativeQamUi
{
    /// <summary>Runs one manager call on the UI thread.</summary>
    /// <param name="action">The call to make.</param>
    /// <param name="cancellationToken">Checked before dispatch; the call itself is not cancelled.</param>
    /// <returns>A task completing after it ran.</returns>
    internal static Task RunAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}

/// <summary>Readers for the exact wire shapes the injected page sends.</summary>
/// <remarks>
/// Exact rather than lenient: the page is WSGM's own script, so anything else arriving here is
/// either a defect or something that is not WSGM, and neither should reach a setting.
/// </remarks>
internal static class NativeQamPayload
{
    /// <summary>Reads one required integer within a range, without an object-arity rule.</summary>
    internal static bool TryReadInt(
        JsonElement payload,
        string propertyName,
        int minimum,
        int maximum,
        out int value)
    {
        value = default;
        return payload.ValueKind is JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind is JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value >= minimum
            && value <= maximum;
    }

    /// <summary>Reads a payload that is exactly one boolean named <c>enabled</c>.</summary>
    internal static bool TryReadEnabled(JsonElement payload, out bool enabled)
    {
        enabled = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("enabled", out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !HasExactly(payload, 1))
        {
            return false;
        }

        enabled = property.GetBoolean();
        return true;
    }

    /// <summary>Reads a payload that is exactly one bounded identifier named <c>target</c>.</summary>
    internal static bool TryReadTarget(JsonElement payload, out string target)
    {
        target = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("target", out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = property.GetString();
        if (!HasExactly(payload, 1)
            || candidate is not { Length: >= 1 and <= 64 }
            || !ValidTargetId(candidate))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    /// <summary>Reads one required non-blank string property within a length bound.</summary>
    internal static bool TryReadBoundedString(
        JsonElement payload,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > maximumLength)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    /// <summary>Whether the payload object carries exactly this many properties.</summary>
    internal static bool HasExactly(JsonElement payload, int propertyCount)
    {
        int count = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            count++;
        }

        return count == propertyCount;
    }

    private static bool ValidTargetId(string target)
    {
        foreach (char character in target)
        {
            if (!(character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class PerformanceServiceNativeQamAdapter
{
    private readonly PerformanceService _service;

    internal PerformanceServiceNativeQamAdapter(PerformanceService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    internal NativeQamFrameLimitState FrameLimit => ProjectFrameLimit(
        _service.Current,
        _service.Enabled,
        PerfSupport?.Invoke());

    /// <summary>The variable-refresh switch, straight from the device capability.</summary>
    /// <remarks>
    /// Availability follows the plugin's published capability and nothing else: a machine whose
    /// device publishes no VRR capability has no switch, rather than one that refuses every press.
    /// </remarks>
    internal NativeQamVrrState Vrr
    {
        get
        {
            NativeQamPerfSupport? support = PerfSupport?.Invoke();
            bool available = support?.VariableRefreshRateSupported == true
                && ApplyVariableRefreshRate is not null;
            bool enabled = support?.VariableRefreshRateEnabled == true;
            return new NativeQamVrrState(
                available,
                enabled,
                "idle",
                available
                    ? enabled
                        ? "The panel follows the frame rate."
                        : "The panel holds a fixed refresh rate."
                    : "This device publishes no variable-refresh capability.");
        }
    }

    /// <summary>
    /// Supplies what the device can currently back, for the reactivated performance panel.
    /// </summary>
    /// <remarks>
    /// Injected rather than read here because the frame-limit options come from display-mode
    /// discovery and the VRR flag from the device plugin, neither of which this adapter owns. The
    /// default reports nothing supported, which hides every control rather than showing one that
    /// writes nowhere.
    /// </remarks>
    internal Func<NativeQamPerfSupport>? PerfSupport { get; set; }

    /// <summary>Applies a manually chosen refresh rate, when the session allows one.</summary>
    /// <remarks>
    /// Set only where a manual refresh rate is meaningful. Under the pairing strategies the frame
    /// cap owns the refresh rate, so this stays unset and a write is refused by name rather than
    /// fighting the pairing on the user's behalf — the row is hidden there anyway, because the
    /// projection omits its limits.
    /// </remarks>
    internal Func<int, bool>? ApplyRefreshRate { get; set; }

    /// <summary>Turns variable refresh rate on or off, when a device publishes it.</summary>
    /// <remarks>
    /// Unset on a machine whose plugin publishes no VRR capability, which is also when the
    /// projection omits <c>is_vrr_supported</c> and Valve's own row does not render. Both follow the
    /// same fact, from the same source, so the row cannot appear without a way to act on it.
    /// </remarks>
    internal Func<bool, CancellationToken, Task<bool>>? ApplyVariableRefreshRate { get; set; }

    /// <summary>Answers the unified row's <c>setFrameLimit</c> command.</summary>
    internal async Task<SteamUiCommandResult> HandleFrameLimitAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadPerformancePayload(
            request.Payload,
            out int value,
            out PerformancePersistenceTarget persistence))
        {
            return new(false, "The frame-limit payload is invalid.");
        }
        return await SetAsync(
            PerformanceControl.FrameLimit,
            value,
            persistence,
            CorrelationId(request),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers the unified row's <c>setRefreshRate</c> command.</summary>
    internal async Task<SteamUiCommandResult> HandleRefreshRateAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadPerformancePayload(
            request.Payload,
            out int hz,
            out PerformancePersistenceTarget _))
        {
            return new(false, "The refresh-rate payload is invalid.");
        }
        return await ApplyRefreshRateAsync(hz, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers the VRR switch's <c>setVariableRefreshRate</c> command.</summary>
    internal async Task<SteamUiCommandResult> HandleVrrAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPayload.TryReadEnabled(request.Payload, out bool enabled))
        {
            return new(false, "The variable-refresh payload is invalid.");
        }
        return await ApplyVariableRefreshRateAsync(enabled, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Turns variable refresh on or off through the device capability.</summary>
    /// <param name="enabled">The wanted state.</param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>Whether the device took it.</returns>
    internal Task<SteamUiCommandResult> ApplyVariableRefreshRateAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (ApplyVariableRefreshRate is not { } apply)
        {
            const string reason = "This device publishes no variable-refresh capability.";
            Log.Warn($"Native QAM variable refresh {(enabled ? "on" : "off")} refused: {reason}");
            return Task.FromResult(new SteamUiCommandResult(false, reason));
        }

        return ApplyFlagAsync(apply, enabled, "variable refresh rate", cancellationToken);
    }

    /// <summary>Applies a refresh rate the user chose directly.</summary>
    /// <param name="hz">The rate to apply.</param>
    /// <param name="cancellationToken">Unused; the display call is synchronous.</param>
    /// <returns>Whether the display took it.</returns>
    /// <remarks>
    /// The unified row's other mode. With the frame limit off there is no cap to pair a rate to, so
    /// the slider becomes the refresh rate itself and writes here — which is why this is available
    /// under every strategy, unlike the manual-refresh row, whose whole problem was fighting a
    /// pairing that was still active.
    /// </remarks>
    internal Task<SteamUiCommandResult> ApplyRefreshRateAsync(
        int hz,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (ApplyRefreshRate is not { } apply)
        {
            const string reason = "This session cannot change the refresh rate.";
            Log.Warn($"Native QAM refresh rate {hz} Hz refused: {reason}");
            return Task.FromResult(new SteamUiCommandResult(false, reason));
        }

        return Task.FromResult(apply(hz)
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(false, $"The display refused {hz} Hz."));
    }

    /// <summary>The cap the enable toggle applies when no cap is set yet.</summary>
    /// <remarks>
    /// The last cap the service still holds when there is one, else the lowest offered notch —
    /// which is also the value the projection shows on the disabled slider, so the cap that takes
    /// effect is the number the user was already looking at.
    /// </remarks>
    private int EnableFrameLimitWatts()
    {
        int desired = _service.Current.Desired.FrameLimit ?? 0;
        return desired > 0
            ? desired
            : NativeQamPerfProjection.LowestOption(PerfSupport?.Invoke().FrameLimitOptions ?? []);
    }

    /// <summary>The state Steam's own performance panel reads every control's value out of.</summary>
    internal NativeQamPerfState PerfState
    {
        get
        {
            PerformanceState current = _service.Current;
            NativeQamPerfSupport support = PerfSupport?.Invoke()
                ?? new NativeQamPerfSupport([], false, false, null, null);

            return NativeQamPerfProjection.Project(
                current.Desired,
                support,
                current.Target?.SteamAppId,
                perApplicationProfileEnabled: current.ApplicationProfileEnabled,
                advancedSettingsEnabled: true,
                variableRefreshRateEnabled: support.VariableRefreshRateEnabled,
                // Was hardcoded null, which advertised the manual refresh row in `limits` while
                // giving it no value in `settings` — half of what crashed the Performance tab.
                refreshRateHz: support.CurrentRefreshRateHz);
        }
    }

    /// <summary>Applies one <c>UpdateSettings</c> call from Steam's own performance panel.</summary>
    /// <param name="request">The forwarded request.</param>
    /// <param name="cancellationToken">Cancels the applies.</param>
    /// <returns>Whether every recognized change applied, and the first failure if not.</returns>
    /// <remarks>
    /// Every setter in Valve's store funnels through the one <c>UpdateSettings</c> method, so a
    /// single call can carry several changes and each is applied in the order it arrived — a delta
    /// that turns the cap on and sets it in one message must not apply the two out of order.
    /// <para>
    /// Failures are collected rather than aborting: refusing the rest of a delta because one field
    /// has no backend would drop settings WSGM can honour, and the panel's own state would then
    /// disagree with the device until the next publish.
    /// </para>
    /// </remarks>
    internal async Task<SteamUiCommandResult> HandlePerformanceDeltaAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPerfDeltaReader.TryRead(
                request.Payload,
                out NativeQamPerfDelta delta,
                out string? readError))
        {
            Log.Warn($"Native QAM performance delta refused: {readError}");
            return new(false, readError);
        }

        if (delta.Unsupported.Count > 0)
        {
            // Named, because the alternative is a control the user operates that quietly does
            // nothing and cannot be diagnosed from a pasted log.
            Log.Warn(
                "Native QAM performance delta carried fields with no WSGM backend: "
                + string.Join(", ", delta.Unsupported));
        }

        if (delta.SteamAppId is { } requestedAppId
            && _service.Current.Target?.SteamAppId != requestedAppId)
        {
            string current = _service.Current.Target?.SteamAppId is { } currentAppId
                ? $"AppID {currentAppId}"
                : "no Steam application";
            string error = $"The performance delta targets stale AppID {requestedAppId}; {current} is current.";
            Log.Warn($"Native QAM performance delta refused: {error}");
            return new(false, error);
        }

        if (delta.ResetToDefault)
        {
            Log.Info(
                "Native QAM performance reset requested for "
                + $"{(delta.SteamAppId is { } id ? $"AppID {id}" : "the global profile")}.");

            // A reset arrives on its own, not alongside value changes: Valve's button sends only
            // this flag. Returning here rather than falling through keeps that explicit.
            return await ResetProfileAsync(cancellationToken).ConfigureAwait(false);
        }

        if (delta.Recognized.Count == 0)
        {
            Log.Info(
                "Native QAM performance delta contained nothing WSGM backs; no change was made.");
            return new(false, "The performance delta carried no supported change.");
        }

        string? failure = null;
        foreach (NativeQamPerfChange received in delta.Recognized)
        {
            // The overlay level travels as Valve's enum value, not the notch the user picked —
            // see NativeQamOverlayLevelWire. Everything downstream speaks notches.
            NativeQamPerfChange change = received.Kind is NativeQamPerfSetting.OverlayLevel
                ? received with { Value = NativeQamOverlayLevelWire.ToNotch(received.Value) }
                : received;

            // Echo suppression, the volume/brightness rule: Steam's side re-sends values it did
            // not originate — a control committing its computed value after WSGM's own state
            // publication moved it, or a settings replay restating the store. Applying a
            // restatement made the level ping-pong between two writers at the poll cadence
            // (device log 2026-09-01, 21:42: OverlayLevel alternating 4/0 every ~2 s with the
            // user idle). A value that already equals WSGM's desired one changes nothing and is
            // dropped before it can re-enter the loop; a genuine user change always differs.
            if (RestatesDesired(change))
            {
                Log.Change(
                    $"native-qam-echo-{change.Kind}",
                    $"Native QAM delta restated {change.Kind}={change.Value}; already desired — skipped.");
                continue;
            }

            SteamUiCommandResult result = await ApplyPerfChangeAsync(
                change,
                CorrelationId(request),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                continue;
            }

            Log.Warn(
                $"Native QAM performance change {change.Kind}={change.Value} failed: "
                + (result.Error ?? "no reason reported"));
            failure ??= result.Error;
        }

        return new(failure is null, failure);
    }

    /// <summary>Whether a delta field only restates the value WSGM already wants.</summary>
    /// <param name="change">The decoded change.</param>
    /// <returns>True to drop the change as an echo.</returns>
    /// <remarks>Only the RTSS-backed settings are judged here: their desired values live in the
    /// performance service and are what the state publication told Steam in the first place. The
    /// display-owned settings pass through; their owners are idempotent.</remarks>
    private bool RestatesDesired(NativeQamPerfChange change)
    {
        PerformanceValues desired = _service.Current.Desired;
        return change.Kind switch
        {
            NativeQamPerfSetting.OverlayLevel => desired.OverlayLevel == change.Value,
            NativeQamPerfSetting.FrameLimit => desired.FrameLimit == change.Value,
            NativeQamPerfSetting.FrameLimitEnabled =>
                change.AsFlag == (desired.FrameLimit is > 0),
            _ => false,
        };
    }

    /// <summary>Applies one change from Steam's own performance panel.</summary>
    /// <param name="change">The decoded change.</param>
    /// <param name="correlationId">Correlates the command across the log.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>Whether the change was applied.</returns>
    /// <remarks>
    /// Only the settings behind a control WSGM mounts and can honour. Anything else is refused with
    /// its name, never accepted-and-dropped: a control that appears to work and does nothing is
    /// worse than one that never rendered.
    /// </remarks>
    internal Task<SteamUiCommandResult> ApplyPerfChangeAsync(
        NativeQamPerfChange change,
        string correlationId,
        CancellationToken cancellationToken) => change.Kind switch
        {
            NativeQamPerfSetting.FrameLimit => SetAsync(
                PerformanceControl.FrameLimit,
                change.Value,
                PerformancePersistenceTarget.Automatic,
                correlationId,
                cancellationToken),

            // Steam models the cap and its switch separately; RTSS has one value where zero is off.
            // Disabling writes zero. Enabling must WRITE A CAP: Valve's toggle sends only the flag,
            // and treating it as a no-op left the slider grey with a switch that snapped straight
            // back — there is no "enabled with no value" state on the RTSS side for it to mean.
            NativeQamPerfSetting.FrameLimitEnabled when !change.AsFlag => SetAsync(
                PerformanceControl.FrameLimit,
                0,
                PerformancePersistenceTarget.Automatic,
                correlationId,
                cancellationToken),
            NativeQamPerfSetting.FrameLimitEnabled => SetAsync(
                PerformanceControl.FrameLimit,
                EnableFrameLimitWatts(),
                PerformancePersistenceTarget.Automatic,
                correlationId,
                cancellationToken),

            NativeQamPerfSetting.OverlayLevel => SetAsync(
                PerformanceControl.OverlayLevel,
                change.Value,
                PerformancePersistenceTarget.Automatic,
                correlationId,
                cancellationToken),

            // Straight to the service that owns the policy: creating or removing the application
            // layer is policy, not a value write, and routing it through SetAsync would need a
            // control that does not exist.
            NativeQamPerfSetting.PerApplicationProfileEnabled => ApplyProfileToggleAsync(
                change.AsFlag,
                cancellationToken),

            NativeQamPerfSetting.VariableRefreshRate when
                ApplyVariableRefreshRate is { } applyVrr =>
                ApplyFlagAsync(applyVrr, change.AsFlag, "variable refresh rate", cancellationToken),

            NativeQamPerfSetting.RefreshRateHz when ApplyRefreshRate is { } applyRefresh =>
                Task.FromResult(applyRefresh(change.Value)
                    ? new SteamUiCommandResult(true, null)
                    : new SteamUiCommandResult(
                        false,
                        $"The display refused {change.Value} Hz.")),

            _ => Task.FromResult(
                new SteamUiCommandResult(
                    false,
                    $"The performance setting {change.Kind} has no WSGM backend yet.")),
        };

    internal async Task<SteamUiCommandResult> SetAsync(
        PerformanceControl control,
        int value,
        PerformancePersistenceTarget persistence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        PerformanceCommandState result = await _service.SetAsync(
            control,
            value,
            persistence,
            "native-qam",
            correlationId,
            cancellationToken).ConfigureAwait(false);
        bool succeeded = result.Phase is
            PerformanceCommandPhase.Deferred
            or PerformanceCommandPhase.SucceededVerified
            or PerformanceCommandPhase.AppliedUnverified;
        return new SteamUiCommandResult(
            succeeded,
            succeeded
                ? null
                : NativeQamText.Bound(result.Diagnostic ?? PhaseFailure(result.Phase)));
    }

    /// <summary>Resets the profile in force to its defaults.</summary>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// A reset that changes nothing because the profile is already at defaults is reported as a
    /// success, unlike the toggle: the user asked for a state and that state is what they have.
    /// </remarks>
    internal async Task<SteamUiCommandResult> ResetProfileAsync(
        CancellationToken cancellationToken)
    {
        await _service.ResetProfileAsync(cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, null);
    }

    /// <remarks>
    /// A refusal is reported rather than swallowed. The toggle is controlled, so an unreported
    /// failure shows it moved and then snaps it back on the next publish with no explanation — and
    /// "no application is running" is exactly the case a user hits by opening the menu on the
    /// desktop.
    /// </remarks>
    private async Task<SteamUiCommandResult> ApplyProfileToggleAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (_service.Current.Target is null)
        {
            return new SteamUiCommandResult(
                false,
                "The per-application profile could not be changed; no identifiable application is "
                    + "running.");
        }

        bool changed = await _service.SetApplicationProfileEnabledAsync(enabled, cancellationToken)
            .ConfigureAwait(false);
        return changed || _service.Current.ApplicationProfileEnabled == enabled
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(
                false,
                "The per-application profile could not be changed; no identifiable application is "
                + "running.");
    }

    /// <remarks>
    /// The device write is awaited rather than fired and forgotten: Steam's toggle is controlled, so
    /// reporting success before the device answered would show it moved and then snap it back on the
    /// next publish.
    /// </remarks>
    private static async Task<SteamUiCommandResult> ApplyFlagAsync(
        Func<bool, CancellationToken, Task<bool>> apply,
        bool enabled,
        string what,
        CancellationToken cancellationToken)
    {
        bool applied = await apply(enabled, cancellationToken).ConfigureAwait(false);
        return applied
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(
                false,
                $"The device refused to turn {what} {(enabled ? "on" : "off")}.");
    }

    /// <param name="state">The performance service's current state.</param>
    /// <param name="enabled">Whether RTSS control is switched on at all.</param>
    /// <param name="support">
    /// What the panel can hold. Its option list bookends the slider, because RTSS's own range is
    /// 0-1000 and a slider spanning that is not a control anyone can aim — the display decides
    /// what a cap can usefully be, not the limiter.
    /// </param>
    /// <returns>The row's state.</returns>
    internal static NativeQamFrameLimitState ProjectFrameLimit(
        PerformanceState state,
        bool enabled,
        NativeQamPerfSupport? support = null)
    {
        RtssCapabilities? capabilities = state.Probe.Capabilities;
        bool supported = capabilities?.Supports(PerformanceControl.FrameLimit) == true;
        bool available = enabled
            && state.Probe.Availability == RtssAvailability.Ready
            && supported;

        // Zero is "off" and is never a slider position, so it is filtered out of both bookends.
        IReadOnlyList<int> caps = support?.FrameLimitOptions ?? [];
        int panelMinimum = NativeQamPerfProjection.LowestOption(caps);
        int panelMaximum = 0;
        foreach (int cap in caps)
        {
            if (cap > panelMaximum)
            {
                panelMaximum = cap;
            }
        }

        int? minimum = panelMinimum > 0 ? panelMinimum : supported ? capabilities!.MinimumFrameLimit : null;
        int? maximum = panelMaximum > 0 ? panelMaximum : supported ? capabilities!.MaximumFrameLimit : null;
        return new NativeQamFrameLimitState(
            available,
            minimum,
            maximum,
            ValidValue(state.Desired.FrameLimit, capabilities, PerformanceControl.FrameLimit),
            ValidValue(state.Observed.FrameLimit, capabilities, PerformanceControl.FrameLimit),
            ProgressText(state.Command, PerformanceControl.FrameLimit),
            FaultText(state.Command, PerformanceControl.FrameLimit),
            StatusText(state, PerformanceControl.FrameLimit, available),
            // Off is a switch of its own, the way SteamOS's "Disable Frame Limit" is, so the
            // slider never has to spend a position on it and the cap the user last chose survives
            // being switched off and back on.
            state.Desired.FrameLimit is > 0,
            support?.RefreshForCap,
            // The bounds of the row's OTHER mode. Present whenever the display has rates to offer,
            // independent of RefreshRatesSelectable — that flag governs Valve's separate manual
            // row, which must stay hidden while a cap owns the rate. Here the cap is off, so there
            // is nothing to fight.
            support?.RefreshRateMinHz,
            support?.RefreshRateMaxHz,
            support?.CurrentRefreshRateHz,
            // The stops that mode slides between. Windows accepts a MODE, not a rate: it either
            // has 75 Hz or it does not, and asking for 72 gets a refusal, not the nearest thing.
            support?.RefreshRates);
    }

    private static bool TryReadPerformancePayload(
        JsonElement payload,
        out int value,
        out PerformancePersistenceTarget persistence)
    {
        value = default;
        persistence = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("value", out JsonElement valueProperty)
            || valueProperty.ValueKind != JsonValueKind.Number
            || !valueProperty.TryGetInt32(out value)
            || !payload.TryGetProperty("persistence", out JsonElement persistenceProperty)
            || persistenceProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        persistence = persistenceProperty.GetString() switch
        {
            "automatic" => PerformancePersistenceTarget.Automatic,
            "global" => PerformancePersistenceTarget.Global,
            "application" => PerformancePersistenceTarget.Application,
            _ => (PerformancePersistenceTarget)(-1),
        };
        return NativeQamPayload.HasExactly(payload, 2)
            && persistence is PerformancePersistenceTarget.Automatic
                or PerformancePersistenceTarget.Global
                or PerformancePersistenceTarget.Application;
    }

    internal static string CorrelationId(SteamUiBridgeRequest request) =>
        $"native-qam:{request.ContextGeneration}:{request.DocumentGeneration}:"
        + $"{request.Sequence}:{request.ActionGeneration}";

    private static int? ValidValue(
        int? value,
        RtssCapabilities? capabilities,
        PerformanceControl control) => value is int integer
        && capabilities?.IsValid(control, integer) == true
            ? integer
            : null;

    private static string ProgressText(
        PerformanceCommandState command,
        PerformanceControl control)
    {
        if (command.Phase != PerformanceCommandPhase.Idle && command.Control != control)
        {
            return "idle";
        }

        return command.Phase switch
        {
            PerformanceCommandPhase.Queued => "queued",
            PerformanceCommandPhase.Applying => "applying",
            PerformanceCommandPhase.Deferred => "deferred",
            PerformanceCommandPhase.SucceededVerified => "succeeded-verified",
            PerformanceCommandPhase.AppliedUnverified => "applied-unverified",
            PerformanceCommandPhase.Rejected => "rejected",
            PerformanceCommandPhase.TimedOut => "timed-out",
            PerformanceCommandPhase.Indeterminate => "indeterminate",
            PerformanceCommandPhase.Failed => "failed",
            PerformanceCommandPhase.ExternalChange => "external-change",
            _ => "idle",
        };
    }

    private static string FaultText(
        PerformanceCommandState command,
        PerformanceControl control) => command.Control == control
        && command.Phase is PerformanceCommandPhase.Rejected
            or PerformanceCommandPhase.TimedOut
            or PerformanceCommandPhase.Indeterminate
            or PerformanceCommandPhase.Failed
                ? NativeQamText.Bound(command.Diagnostic ?? PhaseFailure(command.Phase))
                : string.Empty;

    private static string StatusText(
        PerformanceState state,
        PerformanceControl control,
        bool available)
    {
        string fault = FaultText(state.Command, control);
        if (!string.IsNullOrEmpty(fault))
        {
            return fault;
        }

        if (!available)
        {
            return NativeQamText.Bound(state.Probe.Diagnostic ?? (state.Probe.Availability switch
            {
                RtssAvailability.NotInstalled => "RTSS is not installed.",
                RtssAvailability.NotRunning => "RTSS is not running.",
                RtssAvailability.Incompatible => "The installed RTSS version is incompatible.",
                RtssAvailability.AdapterUnavailable => "The RTSS profile adapter is unavailable.",
                _ => "RTSS performance control is not currently available.",
            }));
        }

        return state.Target switch
        {
            null => "RTSS global profile",
            { RtssProfileName: { Length: > 0 } profile } =>
                NativeQamText.Bound($"RTSS application profile: {profile}"),
            { SteamAppId: { } appId } => NativeQamText.Bound(
                $"Steam AppID {appId}; waiting for its foreground executable."),
            _ => "Waiting for the foreground application's executable profile.",
        };
    }

    private static string PhaseFailure(PerformanceCommandPhase phase) => phase switch
    {
        PerformanceCommandPhase.Rejected => "The RTSS command was rejected.",
        PerformanceCommandPhase.TimedOut => "The RTSS command timed out.",
        PerformanceCommandPhase.Indeterminate => "The RTSS command result is indeterminate.",
        PerformanceCommandPhase.Failed => "The RTSS command failed.",
        _ => "The RTSS command did not complete.",
    };
}

/// <summary>Projects the primary power limit into Steam's TDP surface.</summary>
/// <remarks>
/// With a null coordinator (device integration not active this session) the state is the constant
/// unavailable one and every write is refused with its reason, so the surface stays honest without
/// a separate stand-in implementation.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamTdpService : IDisposable
{
    private const string CapabilityId = "power.primary-limit";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly NativeQamTdpState UnavailableState = new(
        false,
        null,
        null,
        null,
        null,
        null,
        string.Empty,
        "Device Integration is not active in this session.");
    private readonly DeviceCoordinator? _coordinator;
    private bool _disposed;

    internal DeviceCoordinatorNativeQamTdpService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
        if (_coordinator is not null)
        {
            _coordinator.Capabilities.Changed += OnCapabilityViewsChanged;
        }
    }

    public event Action? StateChanged;

    public NativeQamTdpState Current => _coordinator is null
        ? UnavailableState
        : Project(_coordinator.Capabilities.Snapshot()).State;

    /// <summary>Answers Steam's <c>setPrimaryLimit</c> command: the watts and the switch.</summary>
    /// <remarks>
    /// The switch is not optional: a limit switched off still carries the watts the slider holds,
    /// and reading only the number would apply a cap the user had just turned off. Releasing the
    /// limit applies the device ceiling, because the hardware has no "no limit" write.
    /// </remarks>
    internal async Task<SteamUiCommandResult> HandleSetPrimaryLimitAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadPowerLimitPayload(request.Payload, out int watts, out bool enabled))
        {
            return new(false, "The primary power-limit payload is invalid.");
        }
        if (enabled)
        {
            return await SetPrimaryLimitAsync(watts, cancellationToken).ConfigureAwait(false);
        }
        if (Current.MaximumWatts is not int ceiling)
        {
            const string error = "The device does not report a power-limit ceiling to release to.";
            Log.Warn($"Native QAM power limit release refused: {error}");
            return new(false, error);
        }

        Log.Info(
            "Native QAM power limit released to the device ceiling "
            + $"{ceiling} W: Steam's TDP toggle is off (slider holds {watts} W).");
        return await SetPrimaryLimitAsync(ceiling, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SteamUiCommandResult> SetPrimaryLimitAsync(
        int watts,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, UnavailableState.StatusText);
        }

        TdpProjection projection = Project(_coordinator.Capabilities.Snapshot());
        if (!projection.State.Available
            || projection.State.MinimumWatts is not int minimum
            || projection.State.MaximumWatts is not int maximum
            || projection.State.StepWatts is not int step
            || watts < minimum
            || watts > maximum
            || (watts - minimum) % step != 0)
        {
            return new SteamUiCommandResult(false,
                "The primary power limit is unavailable or outside its current descriptor.");
        }

        CapabilityCommandResult result = await _coordinator.ExecuteCapabilityAsync(
            CapabilityId,
            projection.InstanceId,
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = watts,
            },
            CommandTimeout,
            // A person moved the TDP control in the Steam menu, so AutoTDP steps aside for it.
            CapabilityCommandOrigin.User,
            cancellationToken).ConfigureAwait(false);
        bool succeeded = result.Outcome is
            CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;
        return new SteamUiCommandResult(
            succeeded,
            succeeded ? null : result.Reason?.Detail ?? OutcomeText(result.Outcome));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is not null)
        {
            _coordinator.Capabilities.Changed -= OnCapabilityViewsChanged;
        }
    }

    internal static TdpProjection Project(IReadOnlyList<DeviceCapabilityView> views)
    {
        DeviceCapabilityView[] matches = views
            .Where(view => string.Equals(
                view.Descriptor.CapabilityId,
                CapabilityId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            string detail = matches.Length == 0
                ? "The active device does not publish a primary power limit."
                : "The active device published an ambiguous primary power limit.";
            return new TdpProjection(Unavailable(detail), null);
        }

        DeviceCapabilityView view = matches[0];
        CapabilityDescriptor descriptor = view.Descriptor;
        CapabilityProjection projection = view.Projection;
        CapabilityState state = projection.State;
        if (descriptor.Role is not CapabilityRole.PowerSustainedLimit
            || descriptor.ValueKind is not CapabilityValueKind.Integer
            || descriptor.Unit is not CapabilityUnit.Watt
            || !descriptor.SupportsRead
            || !descriptor.SupportsWrite
            || descriptor.Minimum is not int minimum
            || descriptor.Maximum is not int maximum
            || descriptor.Step is not int step
            || minimum < 1
            || maximum > 200
            || minimum >= maximum
            || step < 1
            || step > maximum - minimum)
        {
            return new TdpProjection(
                Unavailable("The primary power-limit descriptor is incompatible."),
                descriptor.InstanceId);
        }

        int? desired = ValidInteger(projection.DesiredValue, minimum, maximum, step);
        int? observed = ValidInteger(state.ObservedValue, minimum, maximum, step);
        bool available = state.Available
            && state.Quality is HardwareStateQuality.Observed or HardwareStateQuality.Verified
            && (desired.HasValue || observed.HasValue);
        string status = StatusText(view, available);
        return new TdpProjection(
            new NativeQamTdpState(
                available,
                minimum,
                maximum,
                step,
                desired,
                observed,
                ProgressText(projection.Progress),
                status),
            descriptor.InstanceId);
    }

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> views) =>
        StateChanged?.Invoke();

    /// <summary>Reads the power-limit payload: the watts and the switch beside them.</summary>
    private static bool TryReadPowerLimitPayload(
        JsonElement payload,
        out int watts,
        out bool enabled)
    {
        watts = default;
        enabled = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("watts", out JsonElement wattsProperty)
            || wattsProperty.ValueKind != JsonValueKind.Number
            || !wattsProperty.TryGetInt32(out watts)
            || !payload.TryGetProperty("enabled", out JsonElement enabledProperty)
            || enabledProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        enabled = enabledProperty.ValueKind is JsonValueKind.True;
        return true;
    }

    private static int? ValidInteger(
        CapabilityValue? value,
        int minimum,
        int maximum,
        int step)
    {
        if (value?.Kind is not CapabilityValueKind.Integer
            || value.IntegerValue is not int integer
            || integer < minimum
            || integer > maximum
            || (integer - minimum) % step != 0)
        {
            return null;
        }

        return integer;
    }

    private static string ProgressText(CommandProgress progress) => progress switch
    {
        CommandProgress.Pending => "applying",
        CommandProgress.Completed => "completed",
        CommandProgress.Failed => "failed",
        CommandProgress.Uncertain => "uncertain",
        _ => string.Empty,
    };

    private static string StatusText(DeviceCapabilityView view, bool available)
    {
        string? detail = view.LastResult?.Reason?.Detail
            ?? view.Projection.State.Reason?.Detail;
        if (!available && string.IsNullOrWhiteSpace(detail))
        {
            detail = "The primary power limit is not currently available.";
        }
        else if (view.Projection.DesiredValueOutOfRange)
        {
            detail = "The desired power limit is outside the current descriptor.";
        }

        return NativeQamText.Bound(detail);
    }

    private static NativeQamTdpState Unavailable(string detail) => new(
        false,
        null,
        null,
        null,
        null,
        null,
        string.Empty,
        NativeQamText.Bound(detail));

    private static string OutcomeText(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Rejected => "The primary power-limit command was rejected.",
        CommandOutcome.TimedOut => "The primary power-limit command timed out.",
        CommandOutcome.Indeterminate => "The primary power-limit result is indeterminate.",
        _ => "The primary power-limit command did not complete.",
    };

    internal sealed record TdpProjection(NativeQamTdpState State, string? InstanceId);
}

/// <summary>Projects charge-limit and persistent lighting capabilities into Quick Settings.</summary>
/// <remarks>
/// The projection selects capabilities by their SDK semantic roles, never by a device package's
/// private ids. Commands re-resolve the descriptor at execution time and pass through the same
/// coordinator validation and readback path as the overlay and profiles.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamDeviceControlsService : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private readonly DeviceCoordinator? _coordinator;
    private bool _disposed;

    internal DeviceCoordinatorNativeQamDeviceControlsService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
        if (_coordinator is not null)
        {
            _coordinator.Capabilities.Changed += OnCapabilityViewsChanged;
        }
    }

    public event Action? StateChanged;

    public NativeQamDeviceControlsState Current => Project(
        _coordinator?.Capabilities.Snapshot() ?? []);

    internal async Task<SteamUiCommandResult> HandleSetChargeLimitAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPayload.TryReadInt(request.Payload, "percent", 0, 100, out int percent)
            || !NativeQamPayload.HasExactly(request.Payload, 1))
        {
            return new(false, "The charge-limit payload is invalid.");
        }

        return await SetIntegerAsync(
            CapabilityRole.ChargeLimit,
            percent,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SteamUiCommandResult> HandleSetLightingBrightnessAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPayload.TryReadInt(request.Payload, "percent", 0, 100, out int percent)
            || !NativeQamPayload.HasExactly(request.Payload, 1))
        {
            return new(false, "The lighting-brightness payload is invalid.");
        }

        return await SetIntegerAsync(
            CapabilityRole.LightingBrightness,
            percent,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SteamUiCommandResult> HandleSetLightingColorAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPayload.TryReadBoundedString(request.Payload, "zone", 64, out string zone)
            || !NativeQamPayload.TryReadInt(
                request.Payload,
                "color",
                0,
                0xFFFFFF,
                out int color)
            || !NativeQamPayload.HasExactly(request.Payload, 2))
        {
            return new(false, "The lighting-color payload is invalid.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(false, "Device Integration is not active in this session.");
        }

        DeviceCapabilityView[] matches = _coordinator.Capabilities.Snapshot()
            .Where(view => view.Descriptor.Role is CapabilityRole.LightingZoneColor
                && string.Equals(view.Descriptor.InstanceId, zone, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 || !WritableColor(matches[0]))
        {
            Log.Warn($"Native QAM lighting color refused: zone='{zone}', matches={matches.Length}.");
            return new(false, "That lighting zone is unavailable or incompatible.");
        }

        return await ExecuteAsync(
            matches[0],
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Color,
                ColorValue = color,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SteamUiCommandResult> SetIntegerAsync(
        CapabilityRole role,
        int value,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(false, "Device Integration is not active in this session.");
        }

        DeviceCapabilityView[] matches = _coordinator.Capabilities.Snapshot()
            .Where(view => view.Descriptor.Role == role)
            .ToArray();
        if (matches.Length != 1 || !WritableRange(matches[0], role, out _, out _, out _))
        {
            Log.Warn($"Native QAM device range refused: role={role}, matches={matches.Length}.");
            return new(false, "That device control is unavailable or incompatible.");
        }

        CapabilityDescriptor descriptor = matches[0].Descriptor;
        if (descriptor.Minimum is not int minimum
            || descriptor.Maximum is not int maximum
            || descriptor.Step is not int step
            || value < minimum
            || value > maximum
            || (value - minimum) % step != 0)
        {
            return new(false, "The value is outside the device's current descriptor.");
        }

        return await ExecuteAsync(
            matches[0],
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = value,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SteamUiCommandResult> ExecuteAsync(
        DeviceCapabilityView view,
        CapabilityValue value,
        CancellationToken cancellationToken)
    {
        CapabilityCommandResult result = await _coordinator!.ExecuteCapabilityAsync(
            view.Descriptor.CapabilityId,
            view.Descriptor.InstanceId,
            value,
            CommandTimeout,
            CapabilityCommandOrigin.User,
            cancellationToken).ConfigureAwait(false);
        bool succeeded = result.Outcome is
            CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;
        return new SteamUiCommandResult(
            succeeded,
            succeeded
                ? null
                : result.Reason?.Detail ?? $"The device command ended as {result.Outcome}.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is not null)
        {
            _coordinator.Capabilities.Changed -= OnCapabilityViewsChanged;
        }
    }

    internal static NativeQamDeviceControlsState Project(
        IReadOnlyList<DeviceCapabilityView> views)
    {
        NativeQamDeviceRangeState? charge = ProjectUniqueRange(
            views,
            CapabilityRole.ChargeLimit);
        NativeQamDeviceRangeState? brightness = ProjectUniqueRange(
            views,
            CapabilityRole.LightingBrightness);
        List<NativeQamLightingZoneState> zones = [];
        IEnumerable<IGrouping<string, DeviceCapabilityView>> zoneGroups = views
            .Where(candidate => candidate.Descriptor.Role is CapabilityRole.LightingZoneColor
                && !string.IsNullOrWhiteSpace(candidate.Descriptor.InstanceId)
                && candidate.Descriptor.InstanceId.Length <= 64)
            .GroupBy(
                candidate => candidate.Descriptor.InstanceId!,
                StringComparer.Ordinal);
        foreach (IGrouping<string, DeviceCapabilityView> group in zoneGroups
            .Where(candidate => candidate.Count() == 1)
            .Take(16))
        {
            DeviceCapabilityView view = group.Single();
            CapabilityDescriptor descriptor = view.Descriptor;
            string instanceId = group.Key;
            bool compatible = WritableColor(view);
            zones.Add(new NativeQamLightingZoneState(
                instanceId,
                descriptor.Display.Key is DisplayKey.Custom
                    && !string.IsNullOrWhiteSpace(descriptor.Display.CustomLabel)
                        ? NativeQamText.Bound(descriptor.Display.CustomLabel)
                        : instanceId,
                compatible,
                ValidColor(view.Projection.DesiredValue),
                ValidColor(view.Projection.State.ObservedValue),
                ProgressText(view.Projection.Progress),
                StatusText(view, compatible)));
        }

        return new NativeQamDeviceControlsState(charge, brightness, zones);
    }

    private static NativeQamDeviceRangeState? ProjectUniqueRange(
        IReadOnlyList<DeviceCapabilityView> views,
        CapabilityRole role)
    {
        DeviceCapabilityView[] matches = views
            .Where(view => view.Descriptor.Role == role)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }
        if (matches.Length != 1
            || !WritableRange(matches[0], role, out int minimum, out int maximum, out int step))
        {
            return new NativeQamDeviceRangeState(
                false, 0, 100, 1, null, null, string.Empty,
                $"The device published an incompatible or ambiguous {role} control.");
        }

        DeviceCapabilityView view = matches[0];
        return new NativeQamDeviceRangeState(
            true,
            minimum,
            maximum,
            step,
            ValidInteger(view.Projection.DesiredValue, minimum, maximum, step),
            ValidInteger(view.Projection.State.ObservedValue, minimum, maximum, step),
            ProgressText(view.Projection.Progress),
            StatusText(view, available: true));
    }

    private static bool WritableRange(
        DeviceCapabilityView view,
        CapabilityRole expectedRole,
        out int minimum,
        out int maximum,
        out int step)
    {
        CapabilityDescriptor descriptor = view.Descriptor;
        minimum = descriptor.Minimum ?? 0;
        maximum = descriptor.Maximum ?? 0;
        step = descriptor.Step ?? 0;
        CapabilityState state = view.Projection.State;
        return descriptor.Role == expectedRole
            && descriptor.ValueKind is CapabilityValueKind.Integer
            && descriptor.Unit is CapabilityUnit.Percent
            && descriptor.SupportsRead
            && descriptor.SupportsWrite
            && minimum >= 0
            && maximum <= 100
            && minimum < maximum
            && step >= 1
            && step <= maximum - minimum
            && state.Available
            && state.Quality is HardwareStateQuality.Observed or HardwareStateQuality.Verified;
    }

    private static bool WritableColor(DeviceCapabilityView view)
    {
        CapabilityDescriptor descriptor = view.Descriptor;
        CapabilityState state = view.Projection.State;
        return descriptor.Role is CapabilityRole.LightingZoneColor
            && descriptor.ValueKind is CapabilityValueKind.Color
            && descriptor.SupportsRead
            && descriptor.SupportsWrite
            && state.Available
            && state.Quality is HardwareStateQuality.Observed or HardwareStateQuality.Verified
            && (ValidColor(view.Projection.DesiredValue).HasValue
                || ValidColor(state.ObservedValue).HasValue);
    }

    private static int? ValidInteger(
        CapabilityValue? value,
        int minimum,
        int maximum,
        int step) => value is
        {
            Kind: CapabilityValueKind.Integer,
            IntegerValue: { } integer,
        }
        && integer >= minimum
        && integer <= maximum
        && (integer - minimum) % step == 0
            ? integer
            : null;

    private static int? ValidColor(CapabilityValue? value) => value is
    {
        Kind: CapabilityValueKind.Color,
        ColorValue: >= 0 and <= 0xFFFFFF,
    }
            ? value.ColorValue
            : null;

    private static string ProgressText(CommandProgress progress) => progress switch
    {
        CommandProgress.Pending => "applying",
        CommandProgress.Completed => "completed",
        CommandProgress.Failed => "failed",
        CommandProgress.Uncertain => "uncertain",
        _ => string.Empty,
    };

    private static string StatusText(DeviceCapabilityView view, bool available)
    {
        string? detail = view.LastResult?.Reason?.Detail
            ?? view.Projection.State.Reason?.Detail;
        if (!available && string.IsNullOrWhiteSpace(detail))
        {
            detail = "The device control is not currently available.";
        }
        else if (view.Projection.DesiredValueOutOfRange)
        {
            detail = "The desired value is outside the current descriptor.";
        }

        return NativeQamText.Bound(detail);
    }

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> views) =>
        StateChanged?.Invoke();
}

/// <summary>
/// Projects WSGM's AutoTDP into Steam's native quick-access menu, beside the limit it moves.
/// </summary>
/// <remarks>
/// AutoTDP is a WSGM setting driving a plugin capability, not a capability of its own, so this reads
/// the coordinator directly rather than looking for a descriptor. One owner: this switch, the
/// overlay's Power and thermals row, and the Settings checkbox all move
/// <c>DeviceIntegration.AutoTdpEnabled</c> through the same method, and none of them holds a copy.
/// A null coordinator projects the constant unavailable state and refuses every write.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamAutoTdpService : IDisposable
{
    private static readonly NativeQamAutoTdpState UnavailableState = new(
        false,
        false,
        false,
        null,
        string.Empty,
        "Device Integration is not active in this session.");
    private readonly DeviceCoordinator? _coordinator;
    private readonly AutoTdpService? _autoTdp;
    private bool _disposed;

    /// <summary>Creates the projection over a running coordinator, or the unavailable one.</summary>
    /// <param name="coordinator">The coordinator owning the AutoTDP setting, or null.</param>
    /// <param name="autoTdp">The session's AutoTDP service, or null when it is not running.</param>
    internal DeviceCoordinatorNativeQamAutoTdpService(
        DeviceCoordinator? coordinator,
        AutoTdpService? autoTdp)
    {
        _coordinator = coordinator;
        _autoTdp = autoTdp;
        if (_coordinator is not null)
        {
            _coordinator.ConfigurationChanged += OnChanged;
            _coordinator.Capabilities.Changed += OnCapabilityViewsChanged;
        }
        if (_autoTdp is not null)
        {
            // The setting is not the state: AutoTDP moves between idle, controlling and paused, and
            // its wattage and frametime detail change, with the stored setting and every capability
            // view untouched. Without this the row rendered whatever it last saw.
            _autoTdp.StatusChanged += OnAutoTdpStatusChanged;
        }
    }

    /// <summary>Raised when the projected state changes.</summary>
    public event Action? StateChanged;

    /// <summary>The state Steam should currently be rendering.</summary>
    public NativeQamAutoTdpState Current => _coordinator is null
        ? UnavailableState
        : Project(
            _coordinator.AutoTdpEnabled,
            _autoTdp?.Status,
            DeviceCoordinatorNativeQamTdpService.Project(_coordinator.Capabilities.Snapshot())
                .State.Available);

    /// <summary>Answers Steam's <c>setAutoTdp</c> command.</summary>
    internal async Task<SteamUiCommandResult> HandleSetAutoTdpAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPayload.TryReadEnabled(request.Payload, out bool enabled))
        {
            return new(false, "The AutoTDP payload is invalid.");
        }
        return await SetEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the AutoTDP setting through its one owner.</summary>
    public async Task<SteamUiCommandResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeQamAutoTdpState state = Current;
        if (_coordinator is null || !state.Available)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, state.StatusText);
        }

        // Idempotent rather than an error: the page and the store can disagree for one frame after
        // a change made somewhere else, and re-sending the value it already has is the harmless way
        // that resolves. The coordinator compares and sets under its own transition gate, so the
        // requested value is what lands even when another surface changed it in between — a toggle
        // decided from the snapshot above would invert that newer value instead.
        await _coordinator.SetAutoTdpEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is not null)
        {
            _coordinator.ConfigurationChanged -= OnChanged;
            _coordinator.Capabilities.Changed -= OnCapabilityViewsChanged;
        }
        if (_autoTdp is not null)
        {
            _autoTdp.StatusChanged -= OnAutoTdpStatusChanged;
        }
    }

    /// <summary>Projects the stored setting and live status into the menu's vocabulary.</summary>
    /// <param name="enabled">The stored setting.</param>
    /// <param name="status">The running service's state, or null when it is not running.</param>
    /// <param name="powerLimitAvailable">Whether a primary power limit exists to drive.</param>
    /// <returns>The state the menu renders.</returns>
    internal static NativeQamAutoTdpState Project(
        bool enabled,
        AutoTdpStatus? status,
        bool powerLimitAvailable)
    {
        // Without a power limit there is nothing to control, so the switch is not offered rather
        // than offered and then silently ineffective.
        if (!powerLimitAvailable)
        {
            return new NativeQamAutoTdpState(
                false,
                enabled,
                false,
                null,
                string.Empty,
                NativeQamText.Bound("No primary power limit is available to control."));
        }

        if (status is null)
        {
            return new NativeQamAutoTdpState(
                true,
                enabled,
                false,
                null,
                enabled ? "applying" : string.Empty,
                NativeQamText.Bound(enabled ? "Starting." : string.Empty));
        }

        bool controlling = status.State is AutoTdpState.Controlling;
        return new NativeQamAutoTdpState(
            // Unavailable is the one state where the switch must not be operable: it means AutoTDP
            // cannot run on this device however the setting is left.
            status.State is not AutoTdpState.Unavailable,
            enabled,
            controlling,
            status.Watts,
            status.State switch
            {
                AutoTdpState.Controlling => "completed",
                AutoTdpState.Unavailable => "failed",
                _ => string.Empty,
            },
            NativeQamText.Bound(status.Detail));
    }

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> views) => OnChanged();

    // Raised from AutoTDP's own tick loop; the consumer rebuilds UI-owned state, so marshal first.
    private void OnAutoTdpStatusChanged(AutoTdpStatus status) => Dispatcher.UIThread.Post(OnChanged);

    private void OnChanged() => StateChanged?.Invoke();
}

/// <summary>
/// Projects WSGM's own controller management into Steam's native quick-access menu.
/// </summary>
/// <remarks>
/// The controller target is WSGM's setting, not a plugin capability, so this reads
/// <see cref="ControllerManager"/> through the coordinator instead of looking for a capability
/// descriptor. That keeps one owner: the QAM control and the overlay's controller page move the same
/// stored default through the same method, and neither holds a copy of the target. A null
/// coordinator projects the constant unavailable state and refuses every write.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamControllerTargetService : IDisposable
{
    /// <summary>Why the row is inert, stated as the one cause that can still produce it: the
    /// session is running without device integration. Every build ships the component.</summary>
    internal const string UnavailableDetail =
        "Controller management is unavailable: this session is running without device integration.";

    private static readonly NativeQamControllerTargetState UnavailableState = new(
        false,
        Array.Empty<NativeQamControllerTargetOption>(),
        string.Empty,
        string.Empty,
        string.Empty,
        UnavailableDetail,
        false);
    private readonly DeviceCoordinator? _coordinator;
    private bool _disposed;

    /// <summary>Creates the projection over a running coordinator, or the unavailable one.</summary>
    /// <param name="coordinator">The coordinator owning controller management, or null.</param>
    internal DeviceCoordinatorNativeQamControllerTargetService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
        if (_coordinator is not null)
        {
            _coordinator.Controllers.StatusChanged += OnControllerStatusChanged;
        }
    }

    /// <summary>Raised when the projected state changes.</summary>
    public event Action? StateChanged;

    /// <summary>The state Steam should currently be rendering.</summary>
    public NativeQamControllerTargetState Current => _coordinator is null
        ? UnavailableState
        : Project(
            _coordinator.ControllerManagementEnabled,
            _coordinator.Controllers.Snapshot(),
            _coordinator.InstalledPackage is not null,
            _coordinator.Controllers.SupportedTargets);

    /// <summary>Answers Steam's <c>setControllerTarget</c> command.</summary>
    internal async Task<SteamUiCommandResult> HandleSetControllerTargetAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPayload.TryReadTarget(request.Payload, out string target))
        {
            return new(false, "The controller-target payload is invalid.");
        }
        return await SetTargetAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores and applies the chosen managed-controller target.</summary>
    public async Task<SteamUiCommandResult> SetTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeQamControllerTargetState state = Current;
        if (_coordinator is null || !state.Available)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, state.StatusText);
        }

        if (!TryParseTarget(target, out ManagedControllerTarget parsed))
        {
            return new SteamUiCommandResult(false, $"'{target}' is not a controller target.");
        }

        ControllerManagerStatus status = await _coordinator
            .SetControllerTargetAsync(parsed, cancellationToken)
            .ConfigureAwait(false);

        // Truthful rather than optimistic: the setting is stored either way, but a manager that
        // could not bring the new target up is not a success the menu should show as one.
        bool succeeded = status.State is not
            (ControllerManagementState.Faulted or ControllerManagementState.Unavailable);
        return new SteamUiCommandResult(succeeded, succeeded ? null : status.Detail);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is not null)
        {
            _coordinator.Controllers.StatusChanged -= OnControllerStatusChanged;
        }
    }

    /// <summary>Projects controller state into the menu's closed vocabulary.</summary>
    /// <param name="enabled">Whether controller management may run at all.</param>
    /// <param name="status">The manager's current truthful state.</param>
    /// <param name="packageInstalled">Whether a device package is installed.</param>
    /// <param name="supportedTargets">Targets the backend on this machine can create.</param>
    /// <returns>The state the menu renders.</returns>
    internal static NativeQamControllerTargetState Project(
        bool enabled,
        ControllerManagerStatus status,
        bool packageInstalled,
        IReadOnlyList<ManagedControllerTarget> supportedTargets)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(supportedTargets);
        if (!enabled)
        {
            return new NativeQamControllerTargetState(
                false,
                Array.Empty<NativeQamControllerTargetOption>(),
                string.Empty,
                string.Empty,
                string.Empty,
                NativeQamText.Bound(status.Detail),
                false);
        }

        // Only what the backend can actually build. These are WSGM's own virtual devices rather
        // than hardware, but a target still needs an encoder behind it: offering one that has none
        // meant the selection persisted, target creation was refused, and controller management
        // reported itself unavailable until the user found their way back to the setting.
        NativeQamControllerTargetOption[] targets =
        [
            .. new[]
            {
                (Target: ManagedControllerTarget.SteamDeckComposite, Label: "Steam Deck"),
                (Target: ManagedControllerTarget.Xbox360, Label: "Xbox 360"),
                (Target: ManagedControllerTarget.DualShock4, Label: "DualShock 4"),
            }
                .Where(option => supportedTargets.Contains(option.Target))
                .Select(option => new NativeQamControllerTargetOption(
                    option.Target.ToString(),
                    option.Label,
                    true)),
        ];

        bool available = status.State is
            ControllerManagementState.Idle or ControllerManagementState.Active;
        string selected = status.Target is { } target ? target.ToString() : string.Empty;

        // Observed is what a target actually exists for right now, which is only true while Active.
        // Reporting the selection back as if it were observed would hide a target that was chosen
        // but never came up.
        string observed = status.State is ControllerManagementState.Active ? selected : string.Empty;
        string detail = status.Detail;
        if (available && string.IsNullOrWhiteSpace(detail) && !packageInstalled)
        {
            detail = "No device package is installed, so no physical controller is being captured.";
        }

        return new NativeQamControllerTargetState(
            available,
            targets,
            selected,
            observed,
            ProgressFor(status.State),
            NativeQamText.Bound(detail),
            // A running game holds the target it was launched with, so a change reaches it only on
            // the next launch. Saying so is the difference between a control that looks broken and
            // one the user understands.
            ApplicationRestartRequired: status.ApplicationId is not null);
    }

    /// <summary>Maps a stored target name back onto the enumeration.</summary>
    /// <param name="target">The name the menu sent.</param>
    /// <param name="parsed">Receives the parsed target.</param>
    /// <returns>Whether the name named a target.</returns>
    /// <remarks>
    /// Ordinal and case-sensitive on purpose: the menu is sent these names from
    /// <see cref="Project"/>, so anything else is a caller defect rather than user input to be
    /// forgiving about.
    /// </remarks>
    internal static bool TryParseTarget(string target, out ManagedControllerTarget parsed) =>
        Enum.TryParse(target, ignoreCase: false, out parsed)
            && Enum.IsDefined(parsed);

    private static string ProgressFor(ControllerManagementState state) => state switch
    {
        ControllerManagementState.Active => "completed",
        ControllerManagementState.Idle => string.Empty,
        ControllerManagementState.Faulted => "failed",
        _ => string.Empty,
    };

    private void OnControllerStatusChanged(ControllerManagerStatus status) =>
        StateChanged?.Invoke();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamBrightnessState))]
[JsonSerializable(typeof(SteamNetworkState))]
[JsonSerializable(typeof(SteamNetworkAccessPointState))]
[JsonSerializable(typeof(NativeQamTdpState))]
[JsonSerializable(typeof(NativeQamDeviceControlsState))]
[JsonSerializable(typeof(NativeQamDeviceRangeState))]
[JsonSerializable(typeof(NativeQamLightingZoneState))]
[JsonSerializable(typeof(NativeQamAutoTdpState))]
[JsonSerializable(typeof(NativeQamControllerTargetState))]
[JsonSerializable(typeof(NativeQamFrameLimitState))]
[JsonSerializable(typeof(NativeQamResolutionState))]
[JsonSerializable(typeof(NativeQamVrrState))]
[JsonSerializable(typeof(SteamBluetoothState))]
[JsonSerializable(typeof(SteamBluetoothDevice))]
[JsonSerializable(typeof(NativeQamAudioState))]
[JsonSerializable(typeof(NativeQamAudioDevice))]
[JsonSerializable(typeof(NativeQamPerfState))]
internal sealed partial class NativeQamSemanticJsonContext : JsonSerializerContext;
