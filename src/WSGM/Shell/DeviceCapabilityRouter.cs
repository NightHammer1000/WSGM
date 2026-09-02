using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Shell;

/// <summary>Stable dictionary key for one semantic capability instance.</summary>
internal readonly record struct DeviceCapabilityKey(string CapabilityId, string? InstanceId)
{
    public override string ToString() => InstanceId is { Length: > 0 }
        ? $"{CapabilityId}#{InstanceId}"
        : CapabilityId;
}

/// <summary>One immutable router snapshot suitable for an overlay or diagnostics client.</summary>
internal sealed record DeviceCapabilityView(
    CapabilityDescriptor Descriptor,
    CapabilityProjection Projection,
    CapabilityCommandResult? LastResult);

/// <summary>
/// Validates and projects the semantic capability stream owned by one plugin generation.
/// </summary>
internal sealed class DeviceCapabilityRouter : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Action<Action> _postToUi;
    private readonly Dictionary<DeviceCapabilityKey, CapabilityDescriptor> _descriptors = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityCommandResult> _lastResults = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _pendingValues = [];

    /// <summary>Latest accepted state per capability.</summary>
    /// <remarks>
    /// The high-rate state channel does not promise ordering, and a delayed older sample overwriting
    /// a newer one is not cosmetic: it can restore a "fresh" reading the device has already moved
    /// past, and the UI would then command against it. Sequence numbers are per cycle generation, so
    /// stale-generation publications are refused by validation before they reach this map.
    /// </remarks>
    private readonly Dictionary<DeviceCapabilityKey, CapabilityStateDelta> _states = [];

    /// <summary>Last logged availability per capability, so only changes are written.</summary>
    private readonly Dictionary<DeviceCapabilityKey, bool> _availability = [];
    private readonly Dictionary<DeviceCapabilityKey, SemaphoreSlim> _commandGates = [];

    /// <summary>Overlay sections of the accepted descriptor set, replaced with each set.</summary>
    private IReadOnlyList<CapabilitySection> _sections = [];
    private DevicePluginRuntime? _client;
    private DeviceDesiredProfile? _desiredProfile;
    private string? _hardwareProfileId;
    private string? _applicationId;
    private long _descriptorGeneration;
    private long _cycleGeneration;
    private long _publishRevision;
    private bool _onAcPower = true;
    private bool _connected;
    private bool _disposed;

    internal DeviceCapabilityRouter(Action<Action> postToUi)
    {
        ArgumentNullException.ThrowIfNull(postToUi);
        _postToUi = postToUi;
    }

    /// <summary>Raised on the UI dispatcher with a complete immutable projection.</summary>
    internal event Action<IReadOnlyList<DeviceCapabilityView>>? Changed;

    internal void Attach(DevicePluginRuntime client, long cycleGeneration)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DetachUnderGate();
            _client = client;
            _cycleGeneration = cycleGeneration;
            _descriptorGeneration = 0;
            _descriptors.Clear();
            _states.Clear();
            _lastResults.Clear();
            _pendingValues.Clear();
            _availability.Clear();
            _commandGates.Clear();
            _sections = [];
            _connected = true;
            client.DescriptorSetReceived += OnDescriptorSet;
            client.CapabilityStateReceived += OnStateDelta;
        }

        Publish();
    }

    internal void UpdateDesiredContext(
        DeviceDesiredProfile? desiredProfile,
        bool onAcPower,
        string? hardwareProfileId,
        string? applicationId)
    {
        lock (_gate)
        {
            _desiredProfile = desiredProfile;
            _onAcPower = onAcPower;
            _hardwareProfileId = hardwareProfileId;
            _applicationId = applicationId;
        }

        Publish();
    }

    internal async Task<CapabilityCommandResult> ExecuteAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        DeviceCapabilityKey key = new(capabilityId, instanceId);
        SemaphoreSlim commandGate;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_commandGates.TryGetValue(key, out commandGate!))
            {
                commandGate = new SemaphoreSlim(1, 1);
                _commandGates.Add(key, commandGate);
            }
        }

        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CapabilityCommand command;
            DevicePluginRuntime client;
            CapabilityCommandResult? refusal = PrepareCommand(key, value, timeout, out command, out client);
            if (refusal is not null)
            {
                ReconcileResult(key, refusal);
                return refusal;
            }

            Publish();
            CapabilityCommandResult result;
            bool terminal = true;
            try
            {
                DeviceCommandDispatch dispatch = await client.ExecuteCommandAsync(
                    command,
                    cancellationToken).ConfigureAwait(false);
                result = dispatch.Immediate;
                if (result.CommandId != command.CommandId)
                {
                    result = Uncertain(command, "The plugin returned a different command ID.");
                }
                else if (dispatch.LateCompletion is not null)
                {
                    terminal = false;
                    _ = ObserveLateCommandAsync(
                        key,
                        command.CommandId,
                        command.ExpectedCycleGeneration,
                        client,
                        dispatch.LateCompletion);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = Uncertain(command, ex.Message);
            }

            ReconcileResult(key, result, terminal);
            return result;
        }
        finally
        {
            commandGate.Release();
        }
    }

    /// <summary>The declared overlay sections of the accepted descriptor set.</summary>
    internal IReadOnlyList<CapabilitySection> Sections
    {
        get
        {
            lock (_gate)
            {
                return _sections;
            }
        }
    }

    internal IReadOnlyList<DeviceCapabilityView> Snapshot()
    {
        lock (_gate)
        {
            return BuildSnapshotUnderGate(DateTimeOffset.UtcNow);
        }
    }

    internal void MarkCycleGenerationChanged(long cycleGeneration)
    {
        lock (_gate)
        {
            _cycleGeneration = cycleGeneration;
            _pendingValues.Clear();
            _lastResults.Clear();
        }

        Publish();
    }

    internal void CloseCommandAdmission()
    {
        lock (_gate)
        {
            _connected = false;
        }

        Publish();
    }

    internal void Detach()
    {
        lock (_gate)
        {
            DetachUnderGate();
            _pendingValues.Clear();
            _sections = [];
        }

        Publish();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            _disposed = true;
            DetachUnderGate();
            // An admitted ExecuteAsync releases its local gate in finally. Clearing the index
            // blocks reuse without disposing a semaphore an in-flight command still owns.
            _commandGates.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private CapabilityCommandResult? PrepareCommand(
        DeviceCapabilityKey key,
        CapabilityValue? value,
        TimeSpan timeout,
        out CapabilityCommand command,
        out DevicePluginRuntime client)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid commandId = Guid.NewGuid();
        lock (_gate)
        {
            command = new CapabilityCommand
            {
                CommandId = commandId,
                CapabilityId = key.CapabilityId,
                InstanceId = key.InstanceId,
                RequestedValue = value,
                ExpectedDescriptorGeneration = _descriptorGeneration,
                ExpectedCycleGeneration = _cycleGeneration,
                Deadline = now.Add(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5)),
            };

            if (!_connected || _client is null)
            {
                client = null!;
                return Reject(command, CapabilityReasonCode.HostUnavailable,
                    "The device plugin runtime is not connected.", retryable: true);
            }

            client = _client;
            if (!_descriptors.TryGetValue(key, out CapabilityDescriptor? descriptor))
            {
                return Reject(command, CapabilityReasonCode.Unsupported,
                    "The capability is not present in the current descriptor set.");
            }

            if (!_states.TryGetValue(key, out CapabilityStateDelta? rawState))
            {
                return Reject(command, CapabilityReasonCode.ObservationExpired,
                    "No current capability state has been observed.", retryable: true);
            }

            CapabilityState state = EvaluateFreshness(
                rawState.State,
                FreshnessFor(descriptor.Role),
                now,
                _cycleGeneration);
            if (!CanCommand(state))
            {
                return Reject(
                    command,
                    state.Reason?.Code ?? CapabilityReasonCode.ObservationExpired,
                    state.Reason?.Detail ?? "Capability state is not current.",
                    retryable: state.Reason?.Retryable ?? true);
            }

            CapabilityReason? refusal = null;
            if (_onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
            {
                refusal = new CapabilityReason(
                    CapabilityReasonCode.UnavailableOnPowerSource,
                    _onAcPower
                        ? "Capability is not available on AC power."
                        : "Capability is not available on battery.");
            }
            else if (value is null && !descriptor.SupportsAction)
            {
                refusal = new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    "Capability does not support being invoked as an action.");
            }
            else if (value is not null && !descriptor.SupportsWrite)
            {
                refusal = new CapabilityReason(CapabilityReasonCode.Unsupported, "Capability is read-only.");
            }
            else if (value is not null
                && !DeviceCapabilityValidation.ValueMatches(value, descriptor, out string? error))
            {
                refusal = new CapabilityReason(
                    CapabilityReasonCode.ValueOutOfRange,
                    error ?? "Capability value violates its descriptor.");
            }

            if (refusal is not null)
            {
                return Reject(
                    command,
                    refusal.Code,
                    refusal.Detail ?? "Command preflight failed.",
                    refusal.Retryable);
            }

            if (value is not null)
            {
                _pendingValues[key] = value;
            }

            return null;
        }
    }

    private void OnDescriptorSet(CapabilityDescriptorSet descriptors)
    {
        lock (_gate)
        {
            if (!DeviceCapabilityValidation.TryValidateDescriptorSet(
                descriptors,
                _cycleGeneration,
                _descriptorGeneration,
                out string? error))
            {
                Log.Warn($"Device descriptor set rejected: {error}");
                return;
            }

            _descriptorGeneration = descriptors.Generation;
            _sections = descriptors.Sections;
            _descriptors.Clear();
            foreach (CapabilityDescriptor descriptor in descriptors.Descriptors)
            {
                _descriptors.Add(Key(descriptor), descriptor);
            }

            _states.Clear();
            _pendingValues.Clear();
            _lastResults.Clear();
            _availability.Clear();
        }

        Publish();
    }

    private void OnStateDelta(CapabilityStateDelta delta)
    {
        lock (_gate)
        {
            DeviceCapabilityKey key = Key(delta.State);
            string? error = null;
            if (delta.Sequence <= 0
                || !_descriptors.TryGetValue(key, out CapabilityDescriptor? descriptor)
                || !DeviceCapabilityValidation.TryValidateState(
                    delta.State,
                    descriptor,
                    _descriptorGeneration,
                    _cycleGeneration,
                    out error))
            {
                Log.Change(
                    $"device-capability-state-rejected/{key}",
                    $"Device capability state rejected: key={key}, "
                        + $"{error ?? "invalid sequence or key"}");
                return;
            }

            if (_states.TryGetValue(key, out CapabilityStateDelta? existing)
                && delta.Sequence <= existing.Sequence)
            {
                Log.Change(
                    $"device-capability-delta-rejected/{key}",
                    $"Device capability delta rejected: key={key}, reason=OutOfOrder.");
                return;
            }

            _states[key] = delta;
            LogAvailabilityChange(key, delta.State);
        }

        Publish();
    }

    /// <summary>Logs a capability becoming available or unavailable, with the plugin's own reason.</summary>
    /// <param name="key">The capability that changed.</param>
    /// <param name="state">The state just applied.</param>
    /// <remarks>
    /// The plugin already says exactly why a capability is unavailable — a gated firmware revision,
    /// a missing prerequisite, a topology it could not match — and WSGM was throwing every one of
    /// those away. A device reporting itself "partly available" with no record of which parts or
    /// why cannot be diagnosed from a pasted log, which is the only way most of these devices are
    /// reachable. Logged on change so a capability that is simply unavailable does not repeat.
    /// <para>
    /// Called under <c>_gate</c>, after the delta is accepted, so what is logged is what was
    /// actually applied rather than what arrived.
    /// </para>
    /// </remarks>
    private void LogAvailabilityChange(DeviceCapabilityKey key, CapabilityState state)
    {
        bool previous = _availability.TryGetValue(key, out bool known) && known;
        bool first = !_availability.ContainsKey(key);
        _availability[key] = state.Available;
        if (!first && previous == state.Available)
        {
            return;
        }

        if (state.Available)
        {
            Log.Info($"Device capability available: {key}.");
            return;
        }

        string reason = state.Reason?.Detail is { Length: > 0 } detail
            ? $"{state.Reason.Code}: {detail}"
            : state.Reason?.Code.ToString() ?? "no reason given";
        Log.Warn($"Device capability unavailable: {key} — {reason}");
    }

    private async Task ObserveLateCommandAsync(
        DeviceCapabilityKey key,
        Guid commandId,
        long cycleGeneration,
        DevicePluginRuntime client,
        Task<CapabilityCommandResult> completion)
    {
        CapabilityCommandResult result = await completion.ConfigureAwait(false);
        lock (_gate)
        {
            if (!_connected
                || !ReferenceEquals(_client, client)
                || _cycleGeneration != cycleGeneration
                || result.CommandId != commandId)
            {
                Log.Warn(
                    $"Late device command result ignored: command={result.CommandId}, expected={commandId}, "
                        + $"resultGeneration={cycleGeneration}, activeGeneration={_cycleGeneration}, "
                        + $"connected={_connected}, sameRuntime={ReferenceEquals(_client, client)}.");
                return;
            }
        }

        Log.Info($"Late device command result reconciled: command={result.CommandId}, "
            + $"capability={key}, outcome={result.Outcome}.");
        ReconcileResult(key, result);
    }

    private void ReconcileResult(
        DeviceCapabilityKey key,
        CapabilityCommandResult result,
        bool terminal = true)
    {
        lock (_gate)
        {
            if (terminal)
            {
                _pendingValues.Remove(key);
            }
            _lastResults[key] = result;
        }

        Log.Info($"Device command: capability={key}, command={result.CommandId}, "
            + $"outcome={result.Outcome}, rollback={result.Rollback}.");
        Publish();
    }

    private IReadOnlyList<DeviceCapabilityView> BuildSnapshotUnderGate(DateTimeOffset now)
    {
        List<DeviceCapabilityView> views = [];
        foreach ((DeviceCapabilityKey key, CapabilityDescriptor descriptor) in _descriptors
            .OrderBy(item => item.Key.CapabilityId, StringComparer.Ordinal)
            .ThenBy(item => item.Key.InstanceId, StringComparer.Ordinal))
        {
            CapabilityState state = _states.TryGetValue(key, out CapabilityStateDelta? latest)
                ? latest.State
                : UnknownState(key);
            if (!_connected)
            {
                state = state with
                {
                    Available = false,
                    Quality = HardwareStateQuality.Stale,
                    Reason = new CapabilityReason(
                        CapabilityReasonCode.HostUnavailable,
                        "The device plugin is disconnected.",
                        Retryable: true),
                };
            }
            else
            {
                state = EvaluateFreshness(
                    state,
                    FreshnessFor(descriptor.Role),
                    now,
                    _cycleGeneration);
            }

            ResolvedDeviceDesiredValue desired = ResolveDesired(key);
            bool outOfRange = desired.Value is not null
                && !DeviceCapabilityValidation.ValueMatches(desired.Value, descriptor, out _);
            _pendingValues.TryGetValue(key, out CapabilityValue? pending);
            _lastResults.TryGetValue(key, out CapabilityCommandResult? result);
            views.Add(new DeviceCapabilityView(
                descriptor,
                new CapabilityProjection
                {
                    State = state,
                    DesiredValue = desired.Value,
                    DesiredSource = desired.Source,
                    PendingValue = pending,
                    Progress = Progress(pending, result),
                    DesiredValueOutOfRange = outOfRange,
                },
                result));
        }

        return views;
    }

    private ResolvedDeviceDesiredValue ResolveDesired(DeviceCapabilityKey key)
    {
        DeviceCapabilityPreference? preference = _desiredProfile?.Capabilities.FirstOrDefault(
            item => string.Equals(item.CapabilityId, key.CapabilityId, StringComparison.Ordinal)
                && string.Equals(item.InstanceId, key.InstanceId, StringComparison.Ordinal));
        return preference is null
            ? new ResolvedDeviceDesiredValue(null, DeviceDesiredValueSource.None)
            : DeviceDesiredStateResolver.Resolve(
                preference,
                _onAcPower,
                _hardwareProfileId,
                _applicationId);
    }

    private CapabilityState UnknownState(DeviceCapabilityKey key) => new()
    {
        CapabilityId = key.CapabilityId,
        InstanceId = key.InstanceId,
        Available = false,
        Quality = HardwareStateQuality.Unknown,
        DescriptorGeneration = _descriptorGeneration,
        CycleGeneration = _cycleGeneration,
        Reason = new CapabilityReason(
            CapabilityReasonCode.ObservationExpired,
            "No state has been published for this descriptor.",
            Retryable: true),
    };

    private void DetachUnderGate()
    {
        if (_client is not null)
        {
            _client.DescriptorSetReceived -= OnDescriptorSet;
            _client.CapabilityStateReceived -= OnStateDelta;
        }

        _client = null;
        _connected = false;
        _availability.Clear();
        _commandGates.Clear();
    }

    private void Publish()
    {
        IReadOnlyList<DeviceCapabilityView> snapshot;
        long revision;
        lock (_gate)
        {
            snapshot = BuildSnapshotUnderGate(DateTimeOffset.UtcNow);
            revision = ++_publishRevision;
        }

        _postToUi(() =>
        {
            lock (_gate)
            {
                if (_disposed || revision != _publishRevision)
                {
                    return;
                }
            }

            Changed?.Invoke(snapshot);
        });
    }

    private static CapabilityCommandResult Reject(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        bool retryable = false) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(code, detail, retryable),
            CompletedAt = DateTimeOffset.UtcNow,
        };

    private static CapabilityCommandResult Uncertain(CapabilityCommand command, string detail) => new()
    {
        CommandId = command.CommandId,
        Outcome = CommandOutcome.Indeterminate,
        Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable, detail, Retryable: true),
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static CommandProgress Progress(
        CapabilityValue? pending,
        CapabilityCommandResult? result)
    {
        if (pending is not null)
        {
            return CommandProgress.Pending;
        }

        return result?.Outcome switch
        {
            CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified =>
                CommandProgress.Completed,
            CommandOutcome.TimedOut or CommandOutcome.Indeterminate => CommandProgress.Uncertain,
            CommandOutcome.Rejected => CommandProgress.Failed,
            _ => CommandProgress.Idle,
        };
    }

    /// <summary>How long an observation stays usable, per capability role.</summary>
    /// <remarks>
    /// Per capability because the underlying facts age at wildly different rates: a fan RPM is stale
    /// within seconds, while a charge limit changes only when someone changes it. One global timeout
    /// would either spam a slow transport or leave a fast-moving reading looking current long after
    /// it stopped being so.
    /// </remarks>
    private static TimeSpan FreshnessFor(CapabilityRole role) => role switch
    {
        // A live reading, such as fan RPM or temperature.
        CapabilityRole.Telemetry or CapabilityRole.FanMeasuredRpm => TimeSpan.FromSeconds(5),
        // A value that only changes when something changes it, such as a charge limit.
        CapabilityRole.ChargeLimit
            or CapabilityRole.ChargeProtectionMode
            or CapabilityRole.ChargeBypass
            or CapabilityRole.LightingPower
            or CapabilityRole.LightingBrightness
            or CapabilityRole.LightingZoneColor
            or CapabilityRole.LightingEffect
            or CapabilityRole.LightingEffectSpeed => TimeSpan.FromMinutes(5),
        // A value that drifts on its own, such as a power limit under a scenario.
        _ => TimeSpan.FromSeconds(30),
    };

    /// <summary>Returns the state as it should be presented now, downgrading it to
    /// <see cref="HardwareStateQuality.Stale"/> when it can no longer be trusted.</summary>
    private static CapabilityState EvaluateFreshness(
        CapabilityState state,
        TimeSpan maxAge,
        DateTimeOffset now,
        long currentCycleGeneration)
    {
        // A faulted capability is already saying something stronger than "old". Downgrading it to
        // stale would lose the fault.
        if (state.Quality is HardwareStateQuality.Faulted or HardwareStateQuality.Unknown)
        {
            return state;
        }

        // A generation change invalidates the observation outright, regardless of age: the handles
        // and the hardware state it described belong to a device that no longer exists.
        if (state.CycleGeneration != currentCycleGeneration)
        {
            return Stale(state, CapabilityReasonCode.GenerationChanged,
                "Observed under a previous process/reconnect cycle.");
        }

        if (state.ObservedAt is not { } observedAt || now - observedAt > maxAge)
        {
            return Stale(state, CapabilityReasonCode.ObservationExpired,
                $"Observation is older than {maxAge}.");
        }

        return state;
    }

    /// <summary>Whether a command may be issued against this state.</summary>
    /// <remarks>
    /// Commanding from stale state is how a UI sends a value derived from a reading that no longer
    /// describes the device. The control is disabled until a fresh observation arrives.
    /// </remarks>
    private static bool CanCommand(CapabilityState state) =>
        state.Available
        && state.Quality is HardwareStateQuality.Observed or HardwareStateQuality.Verified;

    private static CapabilityState Stale(
        CapabilityState state,
        CapabilityReasonCode code,
        string detail) =>
        state with
        {
            Quality = HardwareStateQuality.Stale,
            Available = false,
            Reason = new CapabilityReason(code, detail, Retryable: true),
        };

    private static DeviceCapabilityKey Key(CapabilityDescriptor descriptor) =>
        new(descriptor.CapabilityId, descriptor.InstanceId);

    private static DeviceCapabilityKey Key(CapabilityState state) =>
        new(state.CapabilityId, state.InstanceId);
}

/// <summary>Structural and semantic validation applied before plugin data enters WSGM state.</summary>
internal static class DeviceCapabilityValidation
{
    private const int MaxDescriptors = 128;
    private const int MaxChoices = 64;

    /// <summary>Ceiling a text descriptor's own maximum length may declare.</summary>
    private const int MaxTextLength = 256;
    private const int MaxIdLength = 128;

    /// <summary>
    /// Matches <see cref="PluginSettingSection.MaxSectionIdLength"/>: a capability's section names
    /// the same declared section a setting does, so a longer id here would name nothing.
    /// </summary>
    private const int MaxSectionIdLength = PluginSettingSection.MaxSectionIdLength;

    internal static bool TryValidateDescriptorSet(
        CapabilityDescriptorSet set,
        long cycleGeneration,
        long previousGeneration,
        out string? error)
    {
        if (set.Generation <= previousGeneration || set.CycleGeneration != cycleGeneration)
        {
            error = "Descriptor or device generation is stale.";
            return false;
        }

        if (set.Descriptors.Count > MaxDescriptors)
        {
            error = $"Descriptor set exceeds {MaxDescriptors} entries.";
            return false;
        }

        if (set.Sections.Count > CapabilitySection.MaxSections)
        {
            error = $"Descriptor set declares more than {CapabilitySection.MaxSections} sections.";
            return false;
        }

        Dictionary<string, CapabilitySection> sections = new(StringComparer.Ordinal);
        foreach (CapabilitySection section in set.Sections)
        {
            if (section is null)
            {
                error = "Descriptor set contains a null section.";
                return false;
            }

            if (!section.TryValidate(out error))
            {
                return false;
            }

            if (!sections.TryAdd(section.SectionId, section))
            {
                error = $"Descriptor set declares section '{section.SectionId}' more than once.";
                return false;
            }
        }

        HashSet<DeviceCapabilityKey> keys = [];
        foreach (CapabilityDescriptor descriptor in set.Descriptors)
        {
            if (!TryValidateDescriptor(descriptor, out error)
                || !TryValidatePlacement(descriptor, sections, out error)
                || !keys.Add(new DeviceCapabilityKey(
                    descriptor.CapabilityId,
                    descriptor.InstanceId)))
            {
                error ??= "Descriptor keys are duplicated.";
                return false;
            }
        }

        error = null;
        return true;
    }

    internal static bool TryValidateState(
        CapabilityState state,
        CapabilityDescriptor descriptor,
        long descriptorGeneration,
        long cycleGeneration,
        out string? error)
    {
        if (state.DescriptorGeneration != descriptorGeneration
            || state.CycleGeneration != cycleGeneration)
        {
            error = "State generation does not match the current descriptor and cycle.";
            return false;
        }

        if (state.ObservedValue is not null
            && !ValueMatches(state.ObservedValue, descriptor, out error))
        {
            return false;
        }

        if (state.Quality is HardwareStateQuality.Verified && state.ObservedValue is null)
        {
            error = "Verified state must carry a readback value.";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool ValueMatches(
        CapabilityValue value,
        CapabilityDescriptor descriptor,
        out string? error)
    {
        if (value.Kind != descriptor.ValueKind)
        {
            error = "Capability value kind differs from its descriptor.";
            return false;
        }

        bool valid = value.Kind switch
        {
            CapabilityValueKind.Boolean => value.BooleanValue is not null,
            CapabilityValueKind.Integer => value.IntegerValue is { } integer
                && (descriptor.Minimum is null || integer >= descriptor.Minimum)
                && (descriptor.Maximum is null || integer <= descriptor.Maximum)
                && (descriptor.Step is null or <= 0
                    || (integer - (descriptor.Minimum ?? 0)) % descriptor.Step == 0),
            CapabilityValueKind.Choice => value.ChoiceValue is { Length: > 0 } choice
                && descriptor.Choices.Any(item => string.Equals(
                    item.Value,
                    choice,
                    StringComparison.Ordinal)),
            CapabilityValueKind.Color => value.ColorValue is >= 0 and <= 0xFFFFFF,
            CapabilityValueKind.Curve => CurveIsValid(value.CurveValue, descriptor),
            CapabilityValueKind.Text => PlainText.TryValidate(
                value.TextValue,
                descriptor.MaximumLength ?? 0,
                "text",
                out _),
            CapabilityValueKind.None => false,
            _ => false,
        };
        error = valid ? null : "Capability value violates its descriptor shape or bounds.";
        return valid;
    }

    /// <summary>Checks a descriptor's section and category references against the declared layout.</summary>
    /// <remarks>
    /// A section declared in the set is the plugin authoring its own overlay surface, so any role
    /// may be placed there. Outside that layout the old rule stands: a semantic role keeps the home
    /// WSGM gives it, and only a generic role may name a settings-manifest section — an unknown id
    /// there falls back to a WSGM-owned group instead of failing, which is why it is not an error.
    /// </remarks>
    private static bool TryValidatePlacement(
        CapabilityDescriptor descriptor,
        Dictionary<string, CapabilitySection> sections,
        out string? error)
    {
        CapabilitySection? home = null;
        if (descriptor.SectionId is { } sectionId
            && !sections.TryGetValue(sectionId, out home))
        {
            if (!descriptor.Role.IsGeneric())
            {
                // Named in the error, because from the plugin author's side this looks like a
                // section that was simply ignored.
                error =
                    $"Capability role {descriptor.Role} may not declare the undeclared section "
                    + $"'{sectionId}': a semantic role keeps the placement WSGM gives it on every "
                    + "device unless the descriptor set declares the layout.";
                return false;
            }

            home = null;
        }

        if (descriptor.CategoryId is { } categoryId
            && (home is null
                || !home.Categories.Any(category => string.Equals(
                    category.CategoryId,
                    categoryId,
                    StringComparison.Ordinal))))
        {
            error =
                $"Capability '{descriptor.CapabilityId}' names category '{categoryId}' that its "
                + "declared section does not carry.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateDescriptor(CapabilityDescriptor descriptor, out string? error)
    {
        if (!DeviceIdentifier.IsValid(descriptor.CapabilityId, MaxIdLength)
            || (descriptor.InstanceId is not null && !DeviceIdentifier.IsValid(descriptor.InstanceId, 64)))
        {
            error = "Capability or instance ID is invalid.";
            return false;
        }

        if (!descriptor.Display.TryValidate(out error))
        {
            return false;
        }

        if (descriptor.SectionId is { } sectionId
            && !DeviceIdentifier.IsValid(sectionId, MaxSectionIdLength))
        {
            error = "Capability section ID is invalid.";
            return false;
        }

        if (descriptor.CategoryId is { } categoryId
            && !DeviceIdentifier.IsValid(categoryId, CapabilityCategory.MaxCategoryIdLength))
        {
            error = "Capability category ID is invalid.";
            return false;
        }

        if (!descriptor.SupportsRead && !descriptor.SupportsWrite && !descriptor.SupportsAction)
        {
            error = "Descriptor exposes no readable, writable, or actionable operation.";
            return false;
        }

        if (descriptor.ValueKind is CapabilityValueKind.None != descriptor.SupportsAction
            || descriptor.ValueKind is CapabilityValueKind.None
                && (descriptor.SupportsRead || descriptor.SupportsWrite))
        {
            error = "Action and value-bearing descriptor shapes are inconsistent.";
            return false;
        }

        if (descriptor.ValueKind is CapabilityValueKind.Integer
            && (descriptor.Minimum is null
                || descriptor.Maximum is null
                || descriptor.Minimum > descriptor.Maximum
                || descriptor.Step is null or <= 0))
        {
            error = "Integer descriptors require an ordered range and positive step.";
            return false;
        }

        if (descriptor.ValueKind is CapabilityValueKind.Choice
            && (descriptor.Choices.Count is 0 or > MaxChoices
                || descriptor.Choices.Any(choice => !DeviceIdentifier.IsValid(choice.Value, 64))
                || descriptor.Choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal)
                    .Count() != descriptor.Choices.Count))
        {
            error = "Choice descriptor values are empty, invalid, oversized, or duplicated.";
            return false;
        }

        if (descriptor.ValueKind is not CapabilityValueKind.Choice && descriptor.Choices.Count != 0)
        {
            error = "Only choice descriptors may carry choices.";
            return false;
        }

        // Text is the one value shape with no natural bound, so the descriptor must supply one.
        // Without this a plugin could publish a text capability whose value is unbounded, which is
        // exactly the case PlainText exists to prevent.
        if (descriptor.ValueKind is CapabilityValueKind.Text
            && descriptor.MaximumLength is not (> 0 and <= MaxTextLength))
        {
            error = $"Text descriptors require a maximumLength between 1 and {MaxTextLength}.";
            return false;
        }

        if (descriptor.ValueKind is not CapabilityValueKind.Text && descriptor.MaximumLength is not null)
        {
            error = "Only text descriptors may carry a maximumLength.";
            return false;
        }

        if (!RoleMatchesValueKind(descriptor.Role, descriptor.ValueKind))
        {
            error = "Capability role and value kind are inconsistent.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool RoleMatchesValueKind(CapabilityRole role, CapabilityValueKind kind) => role switch
    {
        CapabilityRole.FanCurve => kind is CapabilityValueKind.Curve,
        CapabilityRole.GenericAction => kind is CapabilityValueKind.None,
        CapabilityRole.GenericToggle
            or CapabilityRole.LightingPower
            or CapabilityRole.VariableRefreshRate
            or CapabilityRole.ChargeBypass => kind is CapabilityValueKind.Boolean,
        CapabilityRole.GenericChoice
            or CapabilityRole.ScenarioMode
            or CapabilityRole.FanMode
            or CapabilityRole.ChargeProtectionMode
            or CapabilityRole.LightingEffect
            or CapabilityRole.ControllerSource
            or CapabilityRole.MotionSource => kind is CapabilityValueKind.Choice,
        CapabilityRole.LightingZoneColor => kind is CapabilityValueKind.Color,
        CapabilityRole.PowerSustainedLimit
            or CapabilityRole.PowerSlowLimit
            or CapabilityRole.PowerFastLimit
            or CapabilityRole.PowerPeakLimit
            or CapabilityRole.FanDuty
            or CapabilityRole.FanTargetRpm
            or CapabilityRole.FanMeasuredRpm
            or CapabilityRole.ChargeLimit
            or CapabilityRole.LightingBrightness
            or CapabilityRole.LightingEffectSpeed
            or CapabilityRole.GenericRange => kind is CapabilityValueKind.Integer,
        CapabilityRole.OemControl or CapabilityRole.HapticSink => kind is CapabilityValueKind.None,
        CapabilityRole.GenericText => kind is CapabilityValueKind.Text,
        CapabilityRole.Telemetry or CapabilityRole.GenericReadOnly =>
            kind is CapabilityValueKind.Boolean
                or CapabilityValueKind.Integer
                or CapabilityValueKind.Choice
                // A read-only string — a firmware revision, a mode name the device reports.
                or CapabilityValueKind.Text,
        _ => true,
    };

    /// <summary>Point count, strictly ascending inputs, and outputs inside whatever bounds the
    /// descriptor declared — the same three the authored-profile check applies.</summary>
    /// <remarks>
    /// The output bounds are checked here and not only in <see cref="DeviceProfileValidation"/>
    /// because a curve can also be written straight through <c>ExecuteCapabilityAsync</c>, without
    /// passing a profile. Every other numeric kind on this path is held to the declared minimum and
    /// maximum, and the refusal message promises "shape or bounds" for all of them. Only the bounds
    /// the device actually declared are enforced: a descriptor that leaves one unset is saying it
    /// has no limit there, and inventing one would refuse a curve the device would have accepted.
    /// </remarks>
    private static bool CurveIsValid(IReadOnlyList<CurvePoint> points, CapabilityDescriptor descriptor)
    {
        if (points.Count is 0 or > 64)
        {
            return false;
        }

        for (int index = 0; index < points.Count; index++)
        {
            CurvePoint point = points[index];
            if (index > 0 && point.Input <= points[index - 1].Input)
            {
                return false;
            }

            if ((descriptor.Minimum is { } minimum && point.Output < minimum)
                || (descriptor.Maximum is { } maximum && point.Output > maximum))
            {
                return false;
            }
        }

        return true;
    }
}
