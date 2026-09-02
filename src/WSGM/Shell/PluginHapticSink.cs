using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Shell;

/// <summary>
/// The physical haptic return path from WSGM to the active plugin.
/// </summary>
/// <remarks>
/// Ownership is the plugin's, not this object's. The sink reports what the plugin published for the
/// controller it currently holds, and stops reporting ownership the moment that publication is
/// withdrawn — a frame written into a released controller would reach whatever owner took it next.
/// </remarks>
internal sealed class PluginHapticSink : IPhysicalHapticSink
{
    private readonly Func<HapticOutputFrame, CancellationToken, Task> _applyAsync;
    private readonly object _gate = new();
    private HapticCapabilities? _capabilities;
    private long _sourceGeneration;
    private int _framesInFlight;
    private TaskCompletionSource? _drained;

    internal PluginHapticSink(Func<HapticOutputFrame, CancellationToken, Task> applyAsync)
    {
        ArgumentNullException.ThrowIfNull(applyAsync);
        _applyAsync = applyAsync;
    }

    /// <inheritdoc/>
    public long SourceGeneration
    {
        get
        {
            lock (_gate)
            {
                return _sourceGeneration;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsOwned
    {
        get
        {
            lock (_gate)
            {
                return _capabilities is not null;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every channel unsupported while unowned, so a frame that races the withdrawal is clamped to
    /// silence rather than delivered at full strength.
    /// </remarks>
    public HapticCapabilities Capabilities
    {
        get
        {
            lock (_gate)
            {
                return _capabilities ?? new HapticCapabilities();
            }
        }
    }

    /// <summary>Records what the plugin published for the controller it now owns.</summary>
    /// <param name="capabilities">The published capabilities, or null when it drives no haptics.</param>
    /// <param name="sourceGeneration">Cycle generation the publication belongs to.</param>
    internal void Publish(HapticCapabilities? capabilities, long sourceGeneration)
    {
        lock (_gate)
        {
            _capabilities = capabilities;
            _sourceGeneration = sourceGeneration;
        }
    }

    /// <summary>Withdraws ownership and waits for every already-admitted frame to finish.</summary>
    /// <returns>A task completing once no frame can still reach the plugin.</returns>
    /// <remarks>
    /// Must complete before controller ownership is handed back: a late frame could reach the next
    /// owner or leave the plugin's last rumble value latched.
    /// </remarks>
    internal Task WithdrawAsync()
    {
        lock (_gate)
        {
            _capabilities = null;
            if (_framesInFlight == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    /// <inheritdoc/>
    public Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return TryAdmit() ? TrackAsync(_applyAsync(frame, cancellationToken)) : Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(long targetGeneration, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!TryAdmit())
        {
            return Task.CompletedTask;
        }

        // An explicit silent frame, not merely the absence of frames: the plugin latches the last
        // rumble values it was given, so stopping without one leaves the motors running.
        return TrackAsync(_applyAsync(
            HapticOutputFrame.Stop(targetGeneration, DateTimeOffset.UtcNow),
            cancellationToken));
    }

    /// <summary>Admits one frame while ownership holds, counting it as in flight.</summary>
    /// <returns><see langword="true"/> when the frame may be written.</returns>
    private bool TryAdmit()
    {
        lock (_gate)
        {
            if (_capabilities is null)
            {
                return false;
            }

            _framesInFlight++;
            return true;
        }
    }

    private async Task TrackAsync(Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (_gate)
            {
                if (--_framesInFlight == 0)
                {
                    drained = _drained;
                    _drained = null;
                }
            }

            drained?.TrySetResult();
        }
    }
}
