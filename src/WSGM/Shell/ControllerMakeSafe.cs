using System;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>
/// The one make-safe sequence WSGM runs when it stops driving the physical controller.
/// </summary>
/// <remarks>
/// Stated in the shared plugin vocabulary (<see cref="ControllerHandoffStep"/>) deliberately. The plugin
/// reports its half of the handoff in those steps, so a second WSGM-local vocabulary would need
/// translating at every boundary and could disagree with the plugin about how far the handoff got —
/// which is exactly the state a remote log has to be able to settle.
/// <para>
/// WSGM's own half collapses into two of those steps: everything before the plugin is asked to let
/// go is <see cref="ControllerHandoffStep.VirtualTargetNeutralized"/>, and everything after it has
/// let go is <see cref="ControllerHandoffStep.WsgmStateRemoved"/>. The two orderings that actually
/// prevent a defect stay as explicit guards rather than as extra steps: the virtual target may not
/// be removed until the physical release has concluded, and WSGM's HidHide entries may not be
/// removed until the virtual target is gone. Removing HidHide earlier exposes a device the plugin is
/// still holding, and Steam then sees the physical controller and the virtual target at once.
/// </para>
/// </remarks>
internal sealed class ControllerMakeSafeSequence
{
    private bool _pluginReleaseObserved;
    private bool _targetRemoved;
    private bool _hidHideRemoved;
    private bool _unverified;

    /// <summary>How far the handoff has progressed in the shared plugin vocabulary.</summary>
    internal ControllerHandoffStep Step { get; private set; } = ControllerHandoffStep.NotStarted;

    /// <summary>How the handoff turned out, once it is complete.</summary>
    internal ControllerHandoffResult Result { get; private set; } =
        ControllerHandoffResult.InProgress;

    /// <summary>Whether WSGM's virtual target has been removed.</summary>
    internal bool TargetRemoved => _targetRemoved;

    /// <summary>Whether WSGM's own HidHide entries have been removed.</summary>
    internal bool HidHideRemoved => _hidHideRemoved;

    /// <summary>Whether WSGM's HidHide entries must stay in place for now.</summary>
    internal bool HidHideMustRemain =>
        Step is not ControllerHandoffStep.NotStarted && !_targetRemoved;

    /// <summary>Whether the virtual target may be removed yet.</summary>
    /// <remarks>
    /// True once the physical release concluded either way. A timed-out release still permits
    /// removal: the user asked WSGM to stop, and leaving a virtual target behind because the plugin
    /// was slow would leave duplicate input rather than prevent it.
    /// </remarks>
    internal bool CanRemoveTarget => _pluginReleaseObserved;

    /// <summary>Whether WSGM's HidHide entries may be removed yet.</summary>
    internal bool CanRemoveHidHide => _targetRemoved;

    /// <summary>Records that routing, output, and the virtual target are all quiet.</summary>
    /// <param name="verified">Whether the neutral state was actually written and confirmed.</param>
    /// <remarks>
    /// The step advances either way — the handoff has to keep going, and the plugin must still be
    /// asked to let go — but an unverified neutralization is carried into the result. A target that
    /// could not be quietened may still be holding a control.
    /// </remarks>
    internal void RecordNeutralized(bool verified)
    {
        Require(
            Step is ControllerHandoffStep.NotStarted,
            $"Make-safe cannot neutralize from {Step}.");
        Step = ControllerHandoffStep.VirtualTargetNeutralized;
        _unverified |= !verified;
    }

    /// <summary>Records the plugin's own release acknowledgment.</summary>
    /// <param name="step">Furthest step the plugin reported.</param>
    /// <param name="result">The plugin's own verification outcome.</param>
    internal void RecordPluginRelease(ControllerHandoffStep step, ControllerHandoffResult result)
    {
        Require(
            Step is ControllerHandoffStep.VirtualTargetNeutralized && !_pluginReleaseObserved,
            $"Make-safe cannot accept a plugin release from {Step}.");
        Require(
            step is not (ControllerHandoffStep.NotStarted
                or ControllerHandoffStep.VirtualTargetNeutralized),
            $"{step} is not a plugin-owned handoff step.");
        _pluginReleaseObserved = true;
        Step = step;
        _unverified |= step is not ControllerHandoffStep.TopologyVerified
            || result is not ControllerHandoffResult.ReleasedVerified;
    }

    /// <summary>Records that the plugin never completed its release within the deadline.</summary>
    internal void RecordPluginReleaseUnobserved()
    {
        Require(
            Step is ControllerHandoffStep.VirtualTargetNeutralized && !_pluginReleaseObserved,
            $"Make-safe cannot time out a plugin release from {Step}.");
        _pluginReleaseObserved = true;
        _unverified = true;
        Step = ControllerHandoffStep.TopologyUnverified;
    }

    /// <summary>Records removal of the virtual target.</summary>
    /// <param name="verified">Whether the removal completed without a reported failure.</param>
    /// <remarks>
    /// The flag exists so the sequence may continue to HidHide cleanup after a failed removal —
    /// leaving WSGM's entries in place would hide the physical controller with nothing driving it —
    /// while the result still says the removal was not verified.
    /// </remarks>
    internal void RecordTargetRemoved(bool verified)
    {
        Require(CanRemoveTarget, "The virtual target cannot be removed before the physical release.");
        _targetRemoved = true;
        _unverified |= !verified;
    }

    /// <summary>Records removal of WSGM's own HidHide entries.</summary>
    /// <param name="verified">Whether removal was read back and confirmed.</param>
    internal void RecordHidHideRemoved(bool verified)
    {
        Require(CanRemoveHidHide, "HidHide entries cannot be removed while the target still exists.");
        _hidHideRemoved = true;
        _unverified |= !verified;
    }

    /// <summary>Completes the sequence and settles its result.</summary>
    internal ControllerHandoffResult Complete()
    {
        Require(
            _targetRemoved && _hidHideRemoved,
            "Make-safe cannot complete before WSGM state is removed.");
        Step = ControllerHandoffStep.WsgmStateRemoved;
        Result = _unverified
            ? ControllerHandoffResult.ReleasedUnverified
            : ControllerHandoffResult.ReleasedVerified;
        return Result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
