using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal enum HidBackendHealthState
{
    Unavailable,
    Incompatible,
    Ready,
    Faulted,
}

internal enum ManagedTargetState
{
    Absent,
    Neutral,
    Active,
    Faulted,
}

internal sealed record HidBackendCapabilities(
    IReadOnlyList<ManagedControllerTarget> SupportedTargets);

internal sealed record HidBackendHealth(
    HidBackendHealthState State,
    string Detail,
    HidBackendCapabilities? Capabilities = null);

internal sealed record HidTargetHandle(
    ManagedControllerTarget Kind,
    long Generation);

internal sealed record HidTargetOutput(
    HapticOutputFrame Frame,
    ManagedControllerTarget SourceKind,
    TimeSpan? StopAfter = null);

internal interface IHidBackend : IAsyncDisposable
{
    event EventHandler<HidTargetOutput>? OutputReceived;

    event EventHandler<long>? TargetLost;

    Task<HidBackendHealth> DiscoverAsync(CancellationToken cancellationToken);

    Task<HidTargetHandle> CreateTargetAsync(
        ManagedControllerTarget kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken);

    Task<bool> WaitForEnumerationAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken);

    ValueTask<bool> PublishAsync(
        HidTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken);

    Task NeutralizeAsync(
        HidTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken);

    Task RemoveTargetAsync(HidTargetHandle target, CancellationToken cancellationToken);

    Task<bool> WaitForRemovalAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken);
}

internal static class ManagedControllerSampleValidator
{
    internal static bool TryValidate(
        CanonicalControllerSample sample,
        long sourceGeneration,
        long previousSequence,
        DateTimeOffset now,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.CycleGeneration != sourceGeneration)
        {
            reason = "stale-source-generation";
            return false;
        }

        if (sample.Sequence <= previousSequence)
        {
            reason = "non-monotonic-sequence";
            return false;
        }

        if (sample.Timestamp > now.AddSeconds(1) || now - sample.Timestamp > TimeSpan.FromSeconds(1))
        {
            reason = "stale-or-future-timestamp";
            return false;
        }

        if (sample.Quality is not SampleQuality.Good
            || !Axis(sample.LeftStickX)
            || !Axis(sample.LeftStickY)
            || !Axis(sample.RightStickX)
            || !Axis(sample.RightStickY)
            || !FiniteUnit(sample.LeftTrigger)
            || !FiniteUnit(sample.RightTrigger)
            || !Motion(sample.Motion))
        {
            reason = "invalid-or-discontinuous-sample";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static bool IsNeutral(CanonicalControllerSample sample) =>
        sample.Buttons == CanonicalButtons.None
        && sample.LeftStickX == 0
        && sample.LeftStickY == 0
        && sample.RightStickX == 0
        && sample.RightStickY == 0
        && sample.LeftTrigger == 0
        && sample.RightTrigger == 0
        && sample.Motion is null;

    private static bool Axis(float value) => float.IsFinite(value) && value is >= -1 and <= 1;

    /// <summary>Whether the value is a finite 0..1 unit, as triggers and haptic channels require.</summary>
    internal static bool FiniteUnit(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

    private static bool Motion(MotionSample? motion) => motion is null
        || ((!motion.HasGyro
            || (float.IsFinite(motion.GyroX)
                && float.IsFinite(motion.GyroY)
                && float.IsFinite(motion.GyroZ)))
            && (!motion.HasAccelerometer
                || (float.IsFinite(motion.AccelX)
                    && float.IsFinite(motion.AccelY)
                    && float.IsFinite(motion.AccelZ))));
}
