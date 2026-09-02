using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>
/// The performance state WSGM supplies in place of Steam's absent <c>SteamClient.System.Perf</c>.
/// </summary>
/// <remarks>
/// The field names are Valve's, taken from the generated protobuf metadata in the client's own
/// bundle (<c>CMsgSystemPerfLimits</c>, <c>CMsgSystemPerfSettingsGlobal</c>,
/// <c>CMsgSystemPerfSettingsPerApp</c>), because the store's controls read them by name. They are
/// spelled out here rather than derived from a naming policy: the outer object is camelCase for the
/// injected bootstrap and the inner objects are the protobuf's snake_case, so one policy cannot
/// serve both and a wrong name is silently a missing control.
/// <para>
/// <b>Every field is nullable and omitted when null, and that is the safety property.</b> Control
/// availability is read straight out of this state — <c>msgLimits?.is_vrr_supported ?? false</c> —
/// so a field WSGM cannot back is left out and Valve's own wrapper renders nothing. Hiding costs no
/// CSS and no patching; adding a field is what makes a control appear.
/// </para>
/// </remarks>
internal sealed record NativeQamPerfState
{
    /// <summary>Bounds and support flags. Omitted fields hide their controls.</summary>
    [JsonPropertyName("limits")]
    public NativeQamPerfLimits? Limits { get; init; }

    /// <summary>Settings that apply to every application.</summary>
    [JsonPropertyName("global")]
    public NativeQamPerfGlobalSettings? Global { get; init; }

    /// <summary>Settings for the application the profile is currently being edited for.</summary>
    [JsonPropertyName("perApp")]
    public NativeQamPerfApplicationSettings? PerApp { get; init; }

    /// <summary>
    /// The running application's Steam AppID as a string, or <c>"769"</c> for none.
    /// </summary>
    /// <remarks>
    /// Steam decides the per-game profile is in use by comparing this with
    /// <see cref="ActiveProfileGameId"/>: equal, and not the Steam client's own pseudo-app, means
    /// the running game's own profile is the one on screen. The no-game value is 769 — see
    /// <see cref="NativeQamPerfProjection"/> — and never "0".
    /// </remarks>
    [JsonPropertyName("currentGameId")]
    public string CurrentGameId { get; init; } = "769";

    /// <summary>The AppID whose profile is being edited, or <c>"769"</c> for the global profile.</summary>
    [JsonPropertyName("activeProfileGameId")]
    public string ActiveProfileGameId { get; init; } = "769";
}

/// <summary>Bounds and support flags for the performance controls WSGM can back.</summary>
/// <remarks>
/// Deliberately partial. The message also carries CPU governor bounds, FSR sharpness bounds, split
/// scaling filters and scalers, external-display refresh bounds, and
/// <c>is_dynamic_refresh_rate_in_steam_supported</c>; none is supplied, so none of those controls
/// renders. The two-layer ownership and hiding contract is documented in
/// <c>docs\steam-cef.md</c>.
/// <para>
/// <c>tdp_limit_min</c>/<c>tdp_limit_max</c> exist in this message and are still not supplied: no
/// component in the performance bundle renders a TDP control, so the fields would be read by
/// nothing. Valve's TDP row comes from the SteamOS Manager RPC seam instead, which is a separate
/// patch.
/// </para>
/// </remarks>
internal sealed record NativeQamPerfLimits
{
    /// <summary>The frame caps the slider offers, as its notches, in ascending order.</summary>
    /// <remarks>
    /// The slider's labels are <c>value.toString()</c> over this array, so the notches and the
    /// options are the same list; there is no separate label channel to fill.
    /// </remarks>
    [JsonPropertyName("fps_limit_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? FpsLimitOptions { get; init; }

    /// <summary>The same notches, for a display Steam considers external.</summary>
    /// <remarks>
    /// THE external-twin rule, referenced by every other twin here and by the delta reader: EVERY
    /// display field in this message has an <c>_external</c> twin, and Valve's controls read and
    /// write whichever side their own display test selects — the Claw's built-in panel reports
    /// <c>bDisplayIsExternal: true</c>, so on this hardware the external twin is the one that
    /// renders. Supplying only the internal fields left the frame-limit slider a grey bar with a
    /// label: the component rendered with an empty notch list.
    /// <para>
    /// WSGM manages one display and cannot tell the two cases apart usefully, so both twins carry
    /// the same values rather than guessing which Steam will read.
    /// </para>
    /// </remarks>
    [JsonPropertyName("fps_limit_options_external")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? FpsLimitOptionsExternal { get; init; }

    /// <summary>Whether the panel supports variable refresh rate.</summary>
    [JsonPropertyName("is_vrr_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsVrrSupported { get; init; }

    /// <summary>Whether a refresh rate can be chosen by hand.</summary>
    [JsonPropertyName("is_manual_display_refresh_rate_available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsManualDisplayRefreshRateAvailable { get; init; }

    /// <summary>Lowest selectable refresh rate in Hz.</summary>
    [JsonPropertyName("display_refresh_manual_hz_min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayRefreshManualHzMin { get; init; }

    /// <summary>Lowest selectable refresh rate, for an externally-reported display.</summary>
    [JsonPropertyName("display_external_refresh_manual_hz_min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayExternalRefreshManualHzMin { get; init; }

    /// <summary>Highest selectable refresh rate, for an externally-reported display.</summary>
    [JsonPropertyName("display_external_refresh_manual_hz_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayExternalRefreshManualHzMax { get; init; }

    /// <summary>Highest selectable refresh rate in Hz.</summary>
    [JsonPropertyName("display_refresh_manual_hz_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayRefreshManualHzMax { get; init; }
}

/// <summary>Performance settings that are not per-application.</summary>
internal sealed record NativeQamPerfGlobalSettings
{
    /// <summary>The performance overlay level, which WSGM maps onto RTSS.</summary>
    [JsonPropertyName("perf_overlay_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerfOverlayLevel { get; init; }

    /// <summary>Whether the panel shows its advanced rows.</summary>
    [JsonPropertyName("is_advanced_settings_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsAdvancedSettingsEnabled { get; init; }

    /// <summary>The second gate on the refresh-rate row for an externally-classified display.</summary>
    /// <remarks>
    /// Live-read from the refresh-rate hook 2026-08-30: availability is
    /// <c>external ? (is_manual_display_refresh_rate_available &amp;&amp;
    /// allow_external_display_refresh_control) : (is_manual_display_refresh_rate_available &amp;&amp;
    /// !disable_refresh_rate_management)</c>. The Claw's built-in panel reports as external, so the
    /// availability flag alone leaves the row hidden — this is the half that was missing.
    /// </remarks>
    [JsonPropertyName("allow_external_display_refresh_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowExternalDisplayRefreshControl { get; init; }
}

/// <summary>Performance settings for one application, or for the global profile.</summary>
internal sealed record NativeQamPerfApplicationSettings
{
    /// <summary>The frame cap in FPS.</summary>
    [JsonPropertyName("fps_limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FpsLimit { get; init; }

    /// <summary>The same cap, for a display Steam considers external. See the limits twin.</summary>
    [JsonPropertyName("fps_limit_external")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FpsLimitExternal { get; init; }

    /// <summary>Whether the frame cap is applied at all.</summary>
    [JsonPropertyName("is_fps_limit_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFpsLimitEnabled { get; init; }

    /// <summary>Whether variable refresh rate is on.</summary>
    [JsonPropertyName("is_vrr_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsVrrEnabled { get; init; }

    /// <summary>The chosen refresh rate in Hz.</summary>
    [JsonPropertyName("display_refresh_manual_hz")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayRefreshManualHz { get; init; }

    /// <summary>The same rate, for a display Steam considers external.</summary>
    [JsonPropertyName("display_external_refresh_manual_hz")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayExternalRefreshManualHz { get; init; }

    /// <summary>Whether this application keeps its own profile rather than using the global one.</summary>
    [JsonPropertyName("is_game_perf_profile_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsGamePerfProfileEnabled { get; init; }
}

/// <summary>What the device can currently back, as far as the performance panel is concerned.</summary>
/// <param name="FrameLimitOptions">Frame caps to offer, or empty to hide the slider.</param>
/// <param name="VariableRefreshRateSupported">Whether the panel supports VRR.</param>
/// <param name="VariableRefreshRateEnabled">
/// Whether VRR is on now, read from the same capability that reports support so the toggle cannot
/// show a state the device disagrees with.
/// </param>
/// <param name="RefreshRatesSelectable">
/// Whether the user may choose a refresh rate by hand. False under the pairing strategies, where
/// WSGM owns the refresh rate and a manual row would fight it.
/// </param>
/// <param name="RefreshRateMinHz">Lowest selectable refresh rate.</param>
/// <param name="RefreshRateMaxHz">Highest selectable refresh rate.</param>
/// <param name="CurrentRefreshRateHz">
/// The rate in force, which the manual refresh row needs a concrete value for. Null only when that
/// row is not offered.
/// </param>
/// <param name="RefreshForCap">
/// The refresh rate each cap will be presented at, keyed by cap. Empty under
/// <c>FrameLimitOnly</c>, where the cap changes no display state and there is nothing to name.
/// <para>
/// Sent as a map rather than as a rule the injected half re-derives: the pairing policy is one
/// decision and it belongs in one place. The slider reads it to label a cap the way SteamOS does —
/// "60 FPS (60 Hz)" — while the user is still dragging, before anything has been applied.
/// </para>
/// </param>
/// <param name="RefreshRates">
/// Every rate the display actually accepted, ascending. Windows takes a mode or it does not —
/// there is no continuum to slide along — so the unified row's refresh mode is NOTCHED to exactly
/// these, unlike its frame-cap mode, where the limiter really does hold any integer.
/// </param>
internal readonly record struct NativeQamPerfSupport(
    IReadOnlyList<int> FrameLimitOptions,
    bool VariableRefreshRateSupported,
    bool RefreshRatesSelectable,
    int? RefreshRateMinHz,
    int? RefreshRateMaxHz,
    bool VariableRefreshRateEnabled = false,
    int? CurrentRefreshRateHz = null,
    IReadOnlyDictionary<int, int>? RefreshForCap = null,
    IReadOnlyList<int>? RefreshRates = null);

/// <summary>Builds the performance state from what WSGM knows, supplying only backed fields.</summary>
internal static class NativeQamPerfProjection
{
    /// <summary>The game id Valve uses to mean "no game": the Steam client's own pseudo-app.</summary>
    /// <remarks>
    /// 769, not "0" — live-read from the client's own components 2026-09-02. The profile header,
    /// the per-game toggle's availability, and the name lookup all compare game ids against 769;
    /// publishing "0" made the header take the game-specific branch, look up game id 0, and render
    /// "Use profile from" with an empty name while HL2 was running.
    /// </remarks>
    private const string NoGame = "769";

    /// <summary>Highest level Steam's overlay-level selector has a notch for.</summary>
    /// <remarks>
    /// Read off the selector itself: it builds five entries, OFF plus 1 to 4, and resolves the
    /// current value against them without a fallback.
    /// </remarks>
    private const int MaximumOverlayLevel = 4;

    /// <summary>Projects WSGM's performance state into Valve's state message shape.</summary>
    /// <param name="values">The resolved frame limit and overlay level for the active profile.</param>
    /// <param name="support">What the device can back.</param>
    /// <param name="steamAppId">The running Steam AppID, or null when none is running.</param>
    /// <param name="perApplicationProfileEnabled">
    /// Whether the running application keeps its own profile.
    /// </param>
    /// <param name="advancedSettingsEnabled">Whether the advanced rows are shown.</param>
    /// <param name="variableRefreshRateEnabled">Current VRR state, or null when unsupported.</param>
    /// <param name="refreshRateHz">Current refresh rate, or null when WSGM owns it.</param>
    /// <returns>The state to publish to the injected shim.</returns>
    /// <remarks>
    /// Pure, and the only place that decides what the panel shows. A field is supplied when WSGM can
    /// both report and honour it; anything else is left null so the control does not render at all,
    /// which is safer than rendering a control whose writes go nowhere.
    /// </remarks>
    internal static NativeQamPerfState Project(
        PerformanceValues values,
        NativeQamPerfSupport support,
        uint? steamAppId,
        bool perApplicationProfileEnabled,
        bool advancedSettingsEnabled,
        bool? variableRefreshRateEnabled,
        int? refreshRateHz)
    {
        // A foreground-only identity has no AppID, and Steam's per-game header is built entirely
        // from one. The profile still applies — it is simply presented as the global one, because
        // claiming an AppID WSGM does not have would put the wrong game's name in Valve's header.
        string gameId = steamAppId is { } appId ? appId.ToString() : NoGame;
        bool perGame = perApplicationProfileEnabled && gameId != NoGame;

        IReadOnlyList<int>? frameLimitOptions = support.FrameLimitOptions.Count > 0
            ? [.. support.FrameLimitOptions.Where(option => option > 0).Distinct().Order()]
            : null;
        int? frameLimit = frameLimitOptions is not null
            ? values.FrameLimit ?? LowestOption(support.FrameLimitOptions)
            : null;
        bool? frameLimitEnabled = frameLimitOptions is not null ? values.FrameLimit is > 0 : null;
        int? manualRefreshHz = support.RefreshRatesSelectable
            ? refreshRateHz ?? support.RefreshRateMaxHz ?? 0
            : null;

        return new NativeQamPerfState
        {
            Limits = new NativeQamPerfLimits
            {
                // Both twins carry the same values; see the external-twin rule on
                // NativeQamPerfLimits.FpsLimitOptionsExternal.
                FpsLimitOptions = frameLimitOptions,
                FpsLimitOptionsExternal = frameLimitOptions,
                IsVrrSupported = support.VariableRefreshRateSupported ? true : null,
                IsManualDisplayRefreshRateAvailable = support.RefreshRatesSelectable ? true : null,
                DisplayRefreshManualHzMin = support.RefreshRatesSelectable
                    ? support.RefreshRateMinHz
                    : null,
                DisplayRefreshManualHzMax = support.RefreshRatesSelectable
                    ? support.RefreshRateMaxHz
                    : null,
                DisplayExternalRefreshManualHzMin = support.RefreshRatesSelectable
                    ? support.RefreshRateMinHz
                    : null,
                DisplayExternalRefreshManualHzMax = support.RefreshRatesSelectable
                    ? support.RefreshRateMaxHz
                    : null,
            },
            Global = new NativeQamPerfGlobalSettings
            {
                // Always a number, and always one of the five the selector knows. It resolves the
                // notch with `levels.find(l => l.value === current).notchIndex` and does not guard
                // the miss, so a level outside 0-4 throws inside the render and Steam's error
                // boundary blanks the whole Performance tab. Clamping here is the cheap side of
                // that trade — the control is always mounted, so an absent value is not an option
                // either. The wire value is Valve's enum, not the notch index — see
                // NativeQamOverlayLevelWire.
                PerfOverlayLevel = NativeQamOverlayLevelWire.ToSteam(
                    Math.Clamp(values.OverlayLevel ?? 0, 0, MaximumOverlayLevel)),
                IsAdvancedSettingsEnabled = advancedSettingsEnabled,
                AllowExternalDisplayRefreshControl = support.RefreshRatesSelectable ? true : null,
            },
            PerApp = new NativeQamPerfApplicationSettings
            {
                // LIMITS AND SETTINGS ARE A PAIR, and getting this wrong crashed the whole
                // Performance tab on 2026-08-30. Hiding a control by omitting its `limits` field is
                // safe; advertising it in `limits` and then omitting its `settings` value is not —
                // Valve's component renders, finds no value, and throws inside Steam's error
                // boundary, taking the tab with it. So every field here is supplied exactly when
                // the limits field that reveals its control is, and carries a concrete value: the
                // lowest offered notch when no cap is set, never 0, because zero is filtered out of
                // the options above and "off" is carried by the flag below.
                //
                // The `_external` twins follow the rule on FpsLimitOptionsExternal.
                FpsLimit = frameLimit,
                FpsLimitExternal = frameLimit,
                // Steam draws the cap and its on/off state from two fields. Without the flag the
                // slider renders at the cap but reads as disabled, so an unset cap is off and any
                // cap at all is on.
                IsFpsLimitEnabled = frameLimitEnabled,
                IsVrrEnabled = support.VariableRefreshRateSupported
                    ? variableRefreshRateEnabled ?? false
                    : null,
                DisplayRefreshManualHz = manualRefreshHz,
                DisplayExternalRefreshManualHz = manualRefreshHz,
                IsGamePerfProfileEnabled = gameId == NoGame ? null : perApplicationProfileEnabled,
            },
            CurrentGameId = gameId,
            ActiveProfileGameId = perGame ? gameId : NoGame,
        };
    }

    /// <summary>The lowest cap actually offered, or zero when none is.</summary>
    /// <remarks>
    /// Mirrors the filter applied to <c>fps_limit_options</c> above, so the value reported can never
    /// be one the slider does not have a notch for. Shared with the frame-limit projection and the
    /// enable-toggle default, which need the same "lowest playable cap" answer.
    /// </remarks>
    internal static int LowestOption(IReadOnlyList<int> options)
    {
        int lowest = 0;
        foreach (int option in options)
        {
            if (option > 0 && (lowest == 0 || option < lowest))
            {
                lowest = option;
            }
        }

        return lowest;
    }
}

