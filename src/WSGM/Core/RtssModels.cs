using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Availability of the optional external RTSS integration.</summary>
internal enum RtssAvailability
{
    Unknown,
    NotInstalled,
    Incompatible,
    NotRunning,
    AdapterUnavailable,
    Ready,
    Degraded,
}

/// <summary>The two bounded RTSS controls exposed through shared performance state.</summary>
internal enum PerformanceControl
{
    FrameLimit,
    OverlayLevel,
}

/// <summary>Where a performance edit is stored.</summary>
internal enum PerformancePersistenceTarget
{
    Automatic,
    Global,
    Application,
}

/// <summary>Persistent policy layer supplying one effective RTSS value.</summary>
internal enum PerformancePolicyLayer
{
    None,
    Global,
    Application,
}

/// <summary>Truthful lifecycle of the last semantic performance command.</summary>
internal enum PerformanceCommandPhase
{
    Idle,
    Queued,
    Applying,
    Deferred,
    SucceededVerified,
    AppliedUnverified,
    Rejected,
    TimedOut,
    Indeterminate,
    Failed,
    ExternalChange,
}

/// <summary>Quality of values read back from RTSS.</summary>
internal enum PerformanceReadbackQuality
{
    Unavailable,
    Verified,
    AppliedUnverified,
}

/// <summary>Canonical WSGM application identity plus optional Steam and RTSS enrichment.</summary>
/// <remarks>
/// <see cref="ApplicationId"/> is authoritative whenever this record exists. Steam can name a game
/// before Windows exposes its foreground executable, so <see cref="RtssProfileName"/> is optional:
/// policy remains per-application while RTSS writes wait for that enrichment.
/// </remarks>
internal sealed record PerformanceApplicationTarget(
    string ApplicationId,
    uint? SteamAppId,
    string? RtssProfileName,
    int? ProcessId = null);

/// <summary>Desired or observed values. Null means the corresponding control has no value.</summary>
internal sealed record PerformanceValues(int? FrameLimit, int? OverlayLevel)
{
    internal static readonly PerformanceValues Empty = new(null, null);

    internal int? ValueFor(PerformanceControl control) => control switch
    {
        PerformanceControl.FrameLimit => FrameLimit,
        PerformanceControl.OverlayLevel => OverlayLevel,
        _ => null,
    };

    internal PerformanceValues With(PerformanceControl control, int value) => control switch
    {
        PerformanceControl.FrameLimit => this with { FrameLimit = value },
        PerformanceControl.OverlayLevel => this with { OverlayLevel = value },
        _ => this,
    };
}

/// <summary>One persistent per-application override.</summary>
internal sealed record PerformanceApplicationPolicy(
    string ApplicationId,
    string RtssProfileName,
    PerformanceValues Values);

/// <summary>Persistent global and per-application RTSS policy.</summary>
internal sealed record PerformancePolicy(
    PerformanceValues Global,
    IReadOnlyList<PerformanceApplicationPolicy> Applications,
    bool Enabled = true)
{
    internal static readonly PerformancePolicy Empty = new(
        PerformanceValues.Empty,
        Array.Empty<PerformanceApplicationPolicy>());
}

/// <summary>Bounds and truthful query support reported by a concrete RTSS adapter.</summary>
internal sealed record RtssCapabilities(
    int MinimumFrameLimit,
    int MaximumFrameLimit,
    IReadOnlySet<int> OverlayLevels,
    bool FrameLimitReadback,
    bool OverlayLevelReadback)
{
    internal bool Supports(PerformanceControl control) => control switch
    {
        PerformanceControl.FrameLimit => MinimumFrameLimit >= 0
            && MaximumFrameLimit >= MinimumFrameLimit,
        PerformanceControl.OverlayLevel => OverlayLevels.Count > 0,
        _ => false,
    };

    internal bool IsValid(PerformanceControl control, int value) => control switch
    {
        PerformanceControl.FrameLimit => value >= MinimumFrameLimit && value <= MaximumFrameLimit,
        PerformanceControl.OverlayLevel => OverlayLevels.Contains(value),
        _ => false,
    };

    internal bool HasVerifiedReadback(PerformanceControl control) => control switch
    {
        PerformanceControl.FrameLimit => FrameLimitReadback,
        PerformanceControl.OverlayLevel => OverlayLevelReadback,
        _ => false,
    };
}

/// <summary>One bounded adapter discovery result. Process identity is folded into Generation.</summary>
internal sealed record RtssProbe(
    RtssAvailability Availability,
    string? Version,
    string? ExecutablePath,
    long Generation,
    RtssCapabilities? Capabilities,
    string? Diagnostic);

/// <summary>Result of querying the active global or application profile.</summary>
internal sealed record RtssReadback(
    PerformanceValues Values,
    PerformanceReadbackQuality FrameLimitQuality,
    PerformanceReadbackQuality OverlayLevelQuality,
    DateTimeOffset Timestamp);

/// <summary>Narrow property update sent to the adapter.</summary>
internal sealed record RtssApplyRequest(
    string RtssProfileName,
    PerformanceControl Control,
    int Value,
    long Generation);

/// <summary>Truthful result of an adapter mutation attempt.</summary>
internal sealed record RtssApplyResult(bool Applied, string? Diagnostic);

/// <summary>Adapter boundary used by the shared service and deterministic tests.</summary>
internal interface IRtssAdapter : IAsyncDisposable
{
    /// <summary>Applies the Custom overlay's configuration (selector level 4). A cheap handoff
    /// to the adapter's renderer; adapters without one ignore it.</summary>
    /// <param name="settings">The widget order and per-widget detail.</param>
    void ApplyOsdCustomization(RtssOsdCustomSettings settings);

    /// <summary>Whether RTSS already holds a profile with this exact name.</summary>
    /// <param name="rtssProfileName">The application profile name; empty means the global profile.</param>
    /// <remarks>
    /// Saving an RTSS profile that does not exist creates it, so the service asks first: a
    /// per-application profile is only written when the user opted the application in or RTSS
    /// already carries one whose explicit values would otherwise override the global write.
    /// Without this check every focused executable grew a profile
    /// (device-observed 2026-09-02).
    /// </remarks>
    bool ProfileExists(string rtssProfileName);

    Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken);

    Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        long generation,
        CancellationToken cancellationToken);

    Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Immutable command status shared by every performance UI client.</summary>
internal sealed record PerformanceCommandState(
    long Sequence,
    string Origin,
    string CorrelationId,
    PerformanceControl Control,
    int? RequestedValue,
    PerformanceCommandPhase Phase,
    string? Diagnostic)
{
    internal static readonly PerformanceCommandState Idle = new(
        0,
        string.Empty,
        string.Empty,
        PerformanceControl.FrameLimit,
        null,
        PerformanceCommandPhase.Idle,
        null);
}

/// <summary>Immutable RTSS state projected into the overlay and native QAM.</summary>
internal sealed record PerformanceState(
    RtssProbe Probe,
    PerformanceApplicationTarget? Target,
    bool ApplicationProfileEnabled,
    PerformancePolicyLayer FrameLimitLayer,
    PerformancePolicyLayer OverlayLevelLayer,
    PerformanceValues Desired,
    PerformanceValues Observed,
    PerformanceReadbackQuality FrameLimitQuality,
    PerformanceReadbackQuality OverlayLevelQuality,
    DateTimeOffset? RefreshedAt,
    PerformanceCommandState Command);
