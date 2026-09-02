using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Shell;

/// <summary>WSGM-owned typed action surface available to the OEM router.</summary>
internal sealed record DeviceOemActionServices
{
    internal required Func<CancellationToken, Task<bool>> ToggleOverlayAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleSteamQuickAccessAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleDevicePageAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleOpenAppsAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleDesktopGameModeAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleOnScreenKeyboardAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> CyclePerformanceProfileAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> CyclePerformanceOverlayLevelAsync { get; init; }

    internal required Func<int, CancellationToken, Task<bool>> SetRearButtonAsync { get; init; }
}

/// <summary>WSGM-owned assignment and runtime-availability policy for physical OEM controls.</summary>
internal static class OemActionRules
{
    internal static bool IsAssignable(OemAction action, OemControlPlacement placement) =>
        !IsVirtualTargetButton(action) || placement is OemControlPlacement.Rear;

    internal static bool IsVirtualTargetButton(OemAction action) => action
        is OemAction.VirtualTargetRearButton1
        or OemAction.VirtualTargetRearButton2;

    internal static bool IsAvailable(OemAction action, bool targetHasRearButtons) =>
        !IsVirtualTargetButton(action) || targetHasRearButtons;
}

/// <summary>Maps canonical OEM events to the closed WSGM-owned action vocabulary.</summary>
internal sealed class DeviceOemActionRouter : IDisposable
{
    private const int MaxControls = 16;
    private const int MaxDeduplicationEntries = 256;
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, OemControlDescriptor> _controls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _recentEvents = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private DevicePluginRuntime? _client;
    private DeviceOemActionServices? _actions;
    private DeviceDesiredProfile? _profile;
    private long _cycleGeneration;
    private long _actionGeneration;
    private bool _controllerManagementEnabled;
    private bool _targetHasRearButtons;
    private bool _disposed;

    internal void ConfigureActions(DeviceOemActionServices actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        lock (_gate)
        {
            _actions = actions;
        }
    }

    internal void Attach(DevicePluginRuntime client, long cycleGeneration)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DetachUnderGate();
            _client = client;
            _cycleGeneration = cycleGeneration;
            ResetUnderGate();
            client.OemControlsReceived += OnControls;
            client.OemEventReceived += OnEvent;
        }
    }

    internal void UpdateConfiguration(
        DeviceDesiredProfile? profile,
        bool controllerManagementEnabled,
        ManagedControllerTarget target)
    {
        lock (_gate)
        {
            _profile = profile;
            _controllerManagementEnabled = controllerManagementEnabled;
            _targetHasRearButtons = target is ManagedControllerTarget.SteamDeckComposite;
            _actionGeneration++;
            ResetUnderGate();
        }
    }

    internal void Reset(long? cycleGeneration = null)
    {
        lock (_gate)
        {
            if (cycleGeneration is { } generation)
            {
                _cycleGeneration = generation;
            }

            ResetUnderGate();
        }
    }

    internal void Detach()
    {
        lock (_gate)
        {
            DetachUnderGate();
            ResetUnderGate();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _disposed = true;
            DetachUnderGate();
            ResetUnderGate();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnControls(IReadOnlyList<OemControlDescriptor> controls)
    {
        lock (_gate)
        {
            if (controls.Count > MaxControls
                || controls.Any(control => !ValidControl(control))
                || controls.Select(control => control.ControlId)
                    .Distinct(StringComparer.Ordinal).Count() != controls.Count)
            {
                Log.Warn("Device OEM control set rejected as malformed or duplicated.");
                return;
            }

            _controls.Clear();
            foreach (OemControlDescriptor control in controls)
            {
                _controls.Add(control.ControlId, control);
            }

            _actionGeneration++;
            ResetUnderGate();
        }
    }

    private void OnEvent(OemControlEvent input)
    {
        OemAction action;
        DeviceOemActionServices? actions;
        lock (_gate)
        {
            if (input.SourceGeneration != _cycleGeneration
                || !_controls.TryGetValue(input.ControlId, out OemControlDescriptor? control)
                || string.IsNullOrWhiteSpace(input.DeduplicationId)
                || input.DeduplicationId.Length > 128
                || input.Timestamp > DateTimeOffset.UtcNow.AddSeconds(5)
                || DateTimeOffset.UtcNow - input.Timestamp > DeduplicationWindow)
            {
                Log.Warn($"Device OEM event rejected: control={input.ControlId}, "
                    + $"generation={input.SourceGeneration}.");
                return;
            }

            if (input.Edge is OemControlEdge.Released)
            {
                Log.Info($"Device OEM release observed: control={input.ControlId}; actions run on press only.");
                return;
            }

            string deduplicationKey = $"{_actionGeneration}:{input.SourceGeneration}:"
                + $"{input.ControlId}:{input.Press}:{input.Edge}:{input.DeduplicationId}";
            ExpireDeduplicationUnderGate(DateTimeOffset.UtcNow);
            if (!_recentEvents.TryAdd(deduplicationKey, input.Timestamp))
            {
                Log.Info($"Device OEM duplicate suppressed: control={input.ControlId}.");
                return;
            }

            action = ResolveActionUnderGate(control);
            if (!OemActionRules.IsAssignable(action, control.Placement)
                || !OemActionRules.IsAvailable(action, _targetHasRearButtons)
                || control.RequiresControllerAcquisition && !_controllerManagementEnabled)
            {
                Log.Warn($"Device OEM action unavailable: control={control.ControlId}, action={action}.");
                return;
            }

            actions = _actions;
        }

        if (action is OemAction.Disabled)
        {
            return;
        }

        if (actions is null)
        {
            Log.Warn($"Device OEM action unavailable before UI services attach: action={action}.");
            return;
        }

        _ = DispatchAsync(actions, action, input, _lifetime.Token);
    }

    private static async Task DispatchAsync(
        DeviceOemActionServices actions,
        OemAction action,
        OemControlEvent input,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(3));
            bool completed = action switch
            {
                OemAction.ToggleWsgmOverlay => await actions.ToggleOverlayAsync(bounded.Token)
                    .ConfigureAwait(false),
                OemAction.ToggleSteamQuickAccess =>
                    await actions.ToggleSteamQuickAccessAsync(bounded.Token).ConfigureAwait(false),
                OemAction.ShowWsgmDevicePage => await actions.ToggleDevicePageAsync(bounded.Token)
                    .ConfigureAwait(false),
                OemAction.ToggleWsgmTaskbar => await actions.ToggleOpenAppsAsync(bounded.Token)
                    .ConfigureAwait(false),
                OemAction.ToggleDesktopGameMode =>
                    await actions.ToggleDesktopGameModeAsync(bounded.Token).ConfigureAwait(false),
                OemAction.ToggleOnScreenKeyboard =>
                    await actions.ToggleOnScreenKeyboardAsync(bounded.Token).ConfigureAwait(false),
                OemAction.CyclePerformanceProfile =>
                    await actions.CyclePerformanceProfileAsync(bounded.Token).ConfigureAwait(false),
                OemAction.CyclePerformanceOverlayLevel =>
                    await actions.CyclePerformanceOverlayLevelAsync(bounded.Token).ConfigureAwait(false),
                OemAction.VirtualTargetRearButton1 =>
                    await actions.SetRearButtonAsync(1, bounded.Token).ConfigureAwait(false),
                OemAction.VirtualTargetRearButton2 =>
                    await actions.SetRearButtonAsync(2, bounded.Token).ConfigureAwait(false),
                _ => true,
            };
            Log.Info($"Device OEM action: control={input.ControlId}, action={action}, "
                + $"completed={completed}.");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Device OEM action timed out: control={input.ControlId}, action={action}.");
        }
        catch (Exception ex)
        {
            Log.Error($"Device OEM action failed: control={input.ControlId}, action={action}", ex);
        }
    }

    private OemAction ResolveActionUnderGate(OemControlDescriptor control)
    {
        DeviceOemAssignment? assignment = _profile?.OemAssignments.FirstOrDefault(item =>
            string.Equals(item.ControlId, control.ControlId, StringComparison.Ordinal));
        if (assignment is not null)
        {
            return assignment.Action;
        }

        // WSGM claims no physical button by default. The handheld's OEM buttons reach Steam as the
        // virtual target's own Steam and Quick Access buttons — the plugin puts them in the
        // controller sample, and Steam responds to its controller natively. WSGM neither intercepts
        // them nor synthesizes anything on their behalf.
        // Putting a WSGM surface on a hardware button is an explicit Settings assignment; an
        // unassigned button does nothing here.
        return OemAction.Disabled;
    }

    private void ExpireDeduplicationUnderGate(DateTimeOffset now)
    {
        foreach (string key in _recentEvents
            .Where(item => now - item.Value > DeduplicationWindow)
            .Select(item => item.Key)
            .ToArray())
        {
            _recentEvents.Remove(key);
        }

        if (_recentEvents.Count >= MaxDeduplicationEntries)
        {
            foreach (string key in _recentEvents.OrderBy(item => item.Value)
                .Take(_recentEvents.Count - MaxDeduplicationEntries + 1)
                .Select(item => item.Key)
                .ToArray())
            {
                _recentEvents.Remove(key);
            }
        }
    }

    private void ResetUnderGate()
    {
        _recentEvents.Clear();
    }

    private void DetachUnderGate()
    {
        if (_client is not null)
        {
            _client.OemControlsReceived -= OnControls;
            _client.OemEventReceived -= OnEvent;
        }

        _client = null;
        _controls.Clear();
    }

    private static bool ValidControl(OemControlDescriptor control) =>
        DeviceIdentifier.IsValid(control.ControlId, 64)
        && control.Display.TryValidate(out _);
}
