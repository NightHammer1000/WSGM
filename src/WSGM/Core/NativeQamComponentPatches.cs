using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Shared bounded lifecycle for one independently versioned native-QAM semantic component.
/// </summary>
/// <remarks>
/// Every component differs only in its declaration: id, compiled component kind, fingerprint,
/// probe chunk label, and — for the two components that do not ride the performance-actions
/// module — the factory tokens that uniquely identify their own module. The declarations live in
/// <see cref="NativeQamComponentPatches"/>.
/// </remarks>
internal sealed class NativeQamComponentPatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;
    private static readonly string[] CommonRequiredCounts =
    [
        "performanceRoot",
        "nativeFields",
        "nativeLayout",
        "localization",
        "react",
    ];
    private static readonly string[] PerformanceActionTokens =
    [
        "SetFPSLimitEnabled",
        "SetFPSLimit",
        "SetPerfOverlayLevel",
        "SteamClient.System.Perf",
    ];

    private readonly string _componentKind;
    private readonly string _fingerprint;
    private readonly string _chunkLabel;
    private readonly string _primaryCountName;
    private readonly IReadOnlyList<string> _primaryTokens;

    /// <summary>Declares one component.</summary>
    /// <param name="id">Stable patch id.</param>
    /// <param name="componentKind">Compiled component kind accepted by the embedded bootstrap.</param>
    /// <param name="fingerprint">Stable structural fingerprint describing the exact positive match.</param>
    /// <param name="chunkLabel">Stable webpack chunk label, kept for live diagnostics and probe tooling.</param>
    /// <param name="primaryCountName">The component-specific probe result property.</param>
    /// <param name="primaryTokens">Tokens that uniquely identify the component-specific factory.</param>
    internal NativeQamComponentPatch(
        string id,
        string componentKind,
        string fingerprint,
        string chunkLabel,
        string primaryCountName = "performanceActions",
        IReadOnlyList<string>? primaryTokens = null)
    {
        Id = id;
        _componentKind = componentKind;
        _fingerprint = fingerprint;
        _chunkLabel = chunkLabel;
        _primaryCountName = primaryCountName;
        _primaryTokens = primaryTokens ?? PerformanceActionTokens;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.native-qam.performance-root";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <summary>Read-only structural probe shared by every native-QAM component.</summary>
    private string ProbeExpression => $$"""
        {{SteamUiProbeJs.CountingPreamble(_chunkLabel)}}
          return JSON.stringify({
            {{_primaryCountName}}:count({{JsonSerializer.Serialize(_primaryTokens)}}),
            performanceRoot:count(['#QuickAccess_Tab_Perf_Common_Settings','#QuickAccess_Tab_Perf_BatteryTimeRemaining','TS.ON_FRAME']),
            nativeFields:count(['DialogSlider_Container','DropDownField','SliderField']),
            nativeLayout:count(['PanelSectionTitle','PanelSectionRow','spinner']),
            localization:count(['Attempting to localize token','Unable to find localization token','LocalizeString']),
            react:count(['react.transitional.element','useState','cloneElement','createElement'])
          });
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """;

    /// <inheritdoc />
    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            ProbeExpression,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchProbeResult(
                false,
                false,
                false,
                null,
                result.Error ?? "SharedJSContext is unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            bool unique = SteamUiPatchEvaluation.IsOne(root, _primaryCountName);
            foreach (string property in CommonRequiredCounts)
            {
                unique &= SteamUiPatchEvaluation.IsOne(root, property);
            }

            return new SteamUiPatchProbeResult(
                true,
                unique,
                unique,
                unique ? _fingerprint : null,
                unique ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + "return JSON.stringify(bridge.install("
            + SteamCef.JsString(_componentKind)
            + "));})()";
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            "Native-QAM component installation failed.",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + "const status=bridge.status("
            + SteamCef.JsString(_componentKind)
            + ");return JSON.stringify({ok:status.ok&&status.registered"
            + "&&status.hostVersion===1&&status.performanceRootWrapped,status});})()";
        SteamUiPatchOperationResult result = await SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            "Native-QAM component verification failed.",
            cancellationToken).ConfigureAwait(false);

        // Verification asks whether the component registered and the performance root is wrapped.
        // Both can be true while the Quick Access panel shows nothing, because the rows are only
        // inserted if the tree Steam renders contains the section they attach to — and on Windows
        // Steam does not render the SteamOS-gated performance blocks at all. Reporting the append
        // outcome is what separates "WSGM did not run" from "WSGM ran and found nowhere to put it".
        await LogAppendOutcomeAsync(context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:true,absent:true});"
            + "const removed=bridge.remove("
            + SteamCef.JsString(_componentKind)
            + ");const status=bridge.status("
            + SteamCef.JsString(_componentKind)
            + ");return JSON.stringify({ok:removed.ok&&!status.registered});})()";
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            "Native-QAM component removal failed.",
            cancellationToken);
    }

    /// <summary>Reports what the last row-insertion attempt actually achieved.</summary>
    /// <param name="context">The live patch context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// Read-only, and deliberately best-effort: a diagnostic that could fail verification would
    /// make the log a liability. Keyed per component through <see cref="Log.Change"/>, so a steady
    /// outcome is stated once and a change in it is stated again.
    /// </remarks>
    private async Task LogAppendOutcomeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            string expression = "(()=>{const b=window[" + SteamCef.JsString(BridgeNamespace)
                + "];const g=b&&b.gate?b.gate('nativeComponents'):null;"
                + "if(!g)return JSON.stringify({error:'bridge unavailable'});"
                + "const s=g.status(" + SteamCef.JsString(_componentKind) + ");"
                + "return JSON.stringify({append:s.lastAppend||{never:true},"
                + "rows:s.renderOutcomes,toggle:s.toggleResolved});})()";
            SteamUiEvaluationResult evaluation = await context.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                expression,
                cancellationToken).ConfigureAwait(false);
            if (!evaluation.Reachable || evaluation.Value is null)
            {
                return;
            }

            Log.Change(
                "steam.ui.append." + Id,
                $"Native-QAM rows for {Id}: {evaluation.Value}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Change("steam.ui.append.error." + Id, $"Native-QAM row report failed: {ex.Message}");
        }
    }
}

/// <summary>The eleven mounted native-QAM components, declared as data.</summary>
/// <remarks>
/// Stateless, so one shared instance per component serves every registry. Which components are
/// Valve's own reactivated exports and which are hand-built on Valve's field primitives — and
/// why — is decided in <c>components.ts</c>, where each kind's factory carries that rationale.
/// </remarks>
internal static class NativeQamComponentPatches
{
    /// <summary>WSGM's unified frame-limit row (deliberately not Valve's notch slider).</summary>
    internal static NativeQamComponentPatch FrameLimit { get; } = new(
        "wsgm.native-qam.frame-limit",
        "frameLimit",
        "native-qam-frame-limit-v1:performance-actions+performance-root+valve-slider",
        "wsgm_native_frame_limit_probe_");

    /// <summary>Valve's profile header and the per-game profile toggle, as one component kind.</summary>
    /// <remarks>
    /// Two separate exports of the perf-components module on the current client — re-probed
    /// 2026-09-02 after the header rendered with no way to enable a profile — mounted as two rows
    /// under this one kind because they are halves of one feature: the header names whose profile
    /// is on screen, the toggle is the only control that can change that.
    /// </remarks>
    internal static NativeQamComponentPatch ValveProfileHeader { get; } = new(
        "wsgm.native-qam.valve-profile-header",
        "valveProfileHeader",
        "native-qam-valve-profile-header-v1:performance-actions+performance-root+valve-header",
        "wsgm_native_valve_header_probe_");

    /// <summary>Valve's reset-to-default button, rendered last because it undoes everything above it.</summary>
    internal static NativeQamComponentPatch ValveReset { get; } = new(
        "wsgm.native-qam.valve-reset",
        "valveReset",
        "native-qam-valve-reset-v1:performance-actions+performance-root+valve-reset",
        "wsgm_native_valve_reset_probe_");

    /// <summary>Valve's own performance-overlay selector.</summary>
    internal static NativeQamComponentPatch ValveOverlayLevel { get; } = new(
        "wsgm.native-qam.valve-overlay-level",
        "valveOverlayLevel",
        "native-qam-valve-overlay-level-v1:performance-actions+performance-root+valve-selector",
        "wsgm_native_valve_overlay_probe_");

    /// <summary>Valve's refresh-rate row, mounted into Quick Settings per S14.</summary>
    internal static NativeQamComponentPatch ValveRefreshRate { get; } = new(
        "wsgm.native-qam.valve-refresh-rate",
        "valveRefreshRate",
        "native-qam-valve-refresh-rate-v1:performance-actions+performance-root+valve-refresh",
        "wsgm_native_valve_refresh_probe_");

    /// <summary>The hand-built display-resolution row, which this client has no component for.</summary>
    internal static NativeQamComponentPatch Resolution { get; } = new(
        "wsgm.native-qam.resolution",
        "resolution",
        "native-qam-resolution-v1:performance-actions+performance-root+valve-dropdown",
        "wsgm_native_resolution_probe_");

    /// <summary>Charge-limit and persistent device-lighting controls in Quick Settings.</summary>
    internal static NativeQamComponentPatch DeviceControls { get; } = new(
        "wsgm.native-qam.device-controls",
        "deviceControls",
        "native-qam-device-controls-v1:performance-root+valve-slider+valve-dropdown",
        "wsgm_native_device_controls_probe_");

    /// <summary>Valve's power-limit toggle and slider pair.</summary>
    /// <remarks>
    /// Not gated by <c>SystemPerfStore</c> at all: both halves read availability and the watt range
    /// out of the SteamOS Manager RPC and write the <c>steamos_tdp_limit</c> client settings, so
    /// this and <see cref="SteamGatePatches.SteamOsManager"/> are one mechanism in two halves.
    /// </remarks>
    internal static NativeQamComponentPatch ValveTdp { get; } = new(
        "wsgm.native-qam.valve-tdp",
        "valveTdp",
        "native-qam-valve-tdp-v1:performance-actions+performance-root+valve-tdp-pair",
        "wsgm_native_valve_tdp_probe_");

    /// <summary>The hand-built variable-refresh switch (Valve's is gated on an absent namespace).</summary>
    internal static NativeQamComponentPatch Vrr { get; } = new(
        "wsgm.native-qam.vrr",
        "vrr",
        "native-qam-vrr-v1:performance-actions+performance-root+valve-toggle",
        "wsgm_native_vrr_probe_");

    /// <summary>The approved controller-target projection on Valve's dropdown primitives.</summary>
    internal static NativeQamComponentPatch ControllerTarget { get; } = new(
        "wsgm.native-qam.controller-target",
        "controllerTarget",
        "native-qam-controller-target-v1:controller-presentation+performance-root+valve-dropdown",
        "wsgm_native_controller_target_probe_",
        "controllerPresentation",
        [
            "#QuickAccess_Tab_Settings_Section_Controller_Title",
            "#QuickAccess_ReorderControllers_Button",
            "#QuickAccess_Tab_Perf_Title",
        ]);

    /// <summary>WSGM's AutoTDP switch, placed with the power limit it moves.</summary>
    /// <remarks>
    /// It requires the same TDP presentation the power-limit patch does: with no native power limit
    /// there is nothing for AutoTDP to sit beside, and nothing for it to drive.
    /// </remarks>
    internal static NativeQamComponentPatch AutoTdp { get; } = new(
        "wsgm.native-qam.auto-tdp",
        "autoTdp",
        "native-qam-auto-tdp-v1:presentation+performance-root+valve-toggle",
        "wsgm_native_auto_tdp_probe_",
        "tdpPresentation",
        [
            "#QuickAccess_Tab_Perf_TDPLimitEnabled",
            "steamos_tdp_limit",
            "showBookendLabels",
        ]);
}
