using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal interface IPhysicalHapticSink
{
    long SourceGeneration { get; }

    bool IsOwned { get; }

    HapticCapabilities Capabilities { get; }

    Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken);

    Task StopAsync(long targetGeneration, string reason, CancellationToken cancellationToken);
}

internal sealed class ControllerOutputRouter : IAsyncDisposable
{
    private static readonly TimeSpan MaxOutputAge = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private readonly IHidBackend _backend;
    private readonly IPhysicalHapticSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<HidTargetOutput> _queue = Channel.CreateBounded<HidTargetOutput>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sinkGate = new(1, 1);
    private readonly Task _worker;
    private HidTargetHandle? _target;
    private CancellationTokenSource? _pulseStopCancellation;
    private long _sourceGeneration;
    private long _routeGeneration;
    private long _pulseSequence;
    private long _lastDispatchTimestamp;
    private bool _outputFaulted;
    private bool _outputObserved;
    private bool _disposed;

    internal ControllerOutputRouter(
        IHidBackend backend,
        IPhysicalHapticSink sink,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _backend.OutputReceived += OnOutputReceived;
        _worker = RunAsync();
    }

    internal int DroppedFrames { get; private set; }

    internal void Attach(HidTargetHandle target, long sourceGeneration)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _target = target;
            _sourceGeneration = sourceGeneration;
            _routeGeneration++;
            _lastDispatchTimestamp = 0;
            _outputFaulted = false;
            _outputObserved = false;
            CancelPulseStopUnderGate();
            DrainUnderGate();
        }
    }

    internal async Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        HidTargetHandle? target;
        lock (_gate)
        {
            target = _target;
            _routeGeneration++;
            CancelPulseStopUnderGate();
            DrainUnderGate();
        }

        if (target is null)
        {
            return;
        }

        await _sinkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sink.StopAsync(target.Generation, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sinkGate.Release();
        }
    }

    internal void Detach(long targetGeneration)
    {
        lock (_gate)
        {
            if (_target?.Generation != targetGeneration)
            {
                return;
            }

            _target = null;
            _sourceGeneration = 0;
            _routeGeneration++;
            CancelPulseStopUnderGate();
            DrainUnderGate();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _backend.OutputReceived -= OnOutputReceived;
            _target = null;
            _routeGeneration++;
            CancelPulseStopUnderGate();
            DrainUnderGate();
        }

        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    private void OnOutputReceived(object? sender, HidTargetOutput output)
    {
        lock (_gate)
        {
            if (_disposed || !CanQueueUnderGate(output))
            {
                DroppedFrames++;
                return;
            }

            if (!_queue.Writer.TryWrite(output))
            {
                DroppedFrames++;
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (HidTargetOutput output in _queue.Reader.ReadAllAsync(_lifetime.Token)
                .ConfigureAwait(false))
            {
                HidTargetHandle? target;
                long sourceGeneration;
                long routeGeneration;
                lock (_gate)
                {
                    if (!CanQueueUnderGate(output))
                    {
                        DroppedFrames++;
                        continue;
                    }

                    target = _target;
                    sourceGeneration = _sourceGeneration;
                    routeGeneration = _routeGeneration;
                }

                if (target is null || sourceGeneration != _sink.SourceGeneration || !_sink.IsOwned)
                {
                    DroppedFrames++;
                    continue;
                }

                HapticOutputFrame frame = _sink.Capabilities.Clamp(output.Frame);
                TimeSpan? stopAfter = output.StopAfter;
                if (stopAfter is not null && !frame.IsSilent)
                {
                    // Bounded haptic events carry protocol intent (an LRA-grade click can be one
                    // millisecond at one percent); the plugin's declared motor physics decide how
                    // that renders. Continuous rumble envelopes pass through untouched — flooring
                    // them would make every quiet scene buzz.
                    frame = FloorForMotors(frame, _sink.Capabilities.MinimumStartIntensity);
                    if (_sink.Capabilities.MinimumPulse > stopAfter)
                    {
                        stopAfter = _sink.Capabilities.MinimumPulse;
                    }
                }
                int framesPerSecond = Math.Clamp(_sink.Capabilities.MaxFramesPerSecond, 1, 1000);
                TimeSpan minimumInterval = TimeSpan.FromSeconds(1d / framesPerSecond);
                if (_lastDispatchTimestamp != 0)
                {
                    TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastDispatchTimestamp);
                    if (elapsed < minimumInterval)
                    {
                        await Task.Delay(minimumInterval - elapsed, _timeProvider, _lifetime.Token)
                            .ConfigureAwait(false);
                    }
                }

                lock (_gate)
                {
                    if (_routeGeneration != routeGeneration || !MatchesRouteUnderGate(output))
                    {
                        DroppedFrames++;
                        continue;
                    }
                }

                await _sinkGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    // Rechecked inside the sink gate: a stop that won the race for it has already
                    // sent its silent frame, and this stale non-silent frame landing on top would
                    // leave the plugin's latched motors running after neutralization.
                    lock (_gate)
                    {
                        if (_routeGeneration != routeGeneration || !MatchesRouteUnderGate(output))
                        {
                            DroppedFrames++;
                            continue;
                        }
                    }

                    await _sink.ApplyAsync(frame, _lifetime.Token).ConfigureAwait(false);
                    _lastDispatchTimestamp = _timeProvider.GetTimestamp();
                    bool firstOutput;
                    lock (_gate)
                    {
                        firstOutput = !_outputObserved;
                        _outputObserved = true;
                        SchedulePulseStopUnderGate(stopAfter, target, routeGeneration);
                    }

                    if (firstOutput)
                    {
                        Log.Info(
                            $"Managed controller output active: target={target.Kind}, "
                                + $"generation={target.Generation}, timed={output.StopAfter is not null}.");
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _outputFaulted = true;
                    }

                    Log.Error("Managed controller output sink faulted; input remains active", ex);
                }
                finally
                {
                    _sinkGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private bool CanQueueUnderGate(HidTargetOutput output)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        return !_outputFaulted
            && MatchesRouteUnderGate(output)
            && output.Frame.Timestamp <= now.AddSeconds(1)
            && now - output.Frame.Timestamp <= MaxOutputAge
            && ManagedControllerSampleValidator.FiniteUnit(output.Frame.LowFrequency)
            && ManagedControllerSampleValidator.FiniteUnit(output.Frame.HighFrequency)
            && ManagedControllerSampleValidator.FiniteUnit(output.Frame.LeftTrigger)
            && ManagedControllerSampleValidator.FiniteUnit(output.Frame.RightTrigger)
            && (output.StopAfter is null || output.StopAfter > TimeSpan.Zero);
    }

    /// <summary>Maps a bounded event's nonzero channels onto the range the motors can start at.</summary>
    /// <param name="frame">The event frame after channel clamping.</param>
    /// <param name="minimumStartIntensity">The plugin-declared motor floor; zero passes through.</param>
    /// <returns>The frame with each nonzero channel compressed onto floor..1.</returns>
    /// <remarks>Zero channels stay zero so stop events still stop.</remarks>
    internal static HapticOutputFrame FloorForMotors(
        HapticOutputFrame frame,
        float minimumStartIntensity)
    {
        if (minimumStartIntensity <= 0f)
        {
            return frame;
        }

        float floor = Math.Min(1f, minimumStartIntensity);
        float Map(float value) => value <= 0f ? 0f : floor + ((1f - floor) * Math.Min(1f, value));
        return frame with
        {
            LowFrequency = Map(frame.LowFrequency),
            HighFrequency = Map(frame.HighFrequency),
            LeftTrigger = Map(frame.LeftTrigger),
            RightTrigger = Map(frame.RightTrigger),
        };
    }

    private bool MatchesRouteUnderGate(HidTargetOutput output) =>
        _target is { } target
        && output.Frame.TargetGeneration == target.Generation
        && output.SourceKind == target.Kind;

    private void DrainUnderGate()
    {
        while (_queue.Reader.TryRead(out _))
        {
            DroppedFrames++;
        }
    }

    private void SchedulePulseStopUnderGate(
        TimeSpan? pulseStop,
        HidTargetHandle target,
        long routeGeneration)
    {
        CancelPulseStopUnderGate();
        if (pulseStop is not { } stopAfter)
        {
            return;
        }

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        long pulseSequence = _pulseSequence;
        _pulseStopCancellation = cancellation;
        _ = StopPulseAfterAsync(
            stopAfter,
            target,
            routeGeneration,
            pulseSequence,
            cancellation);
    }

    private async Task StopPulseAfterAsync(
        TimeSpan delay,
        HidTargetHandle target,
        long routeGeneration,
        long pulseSequence,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellation.Token).ConfigureAwait(false);
            await _sinkGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_disposed
                        || _routeGeneration != routeGeneration
                        || _pulseSequence != pulseSequence
                        || _target != target)
                    {
                        return;
                    }
                }

                await _sink.StopAsync(
                    target.Generation,
                    "virtual-controller-pulse-complete",
                    cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _sinkGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate)
            {
                _outputFaulted = true;
            }

            Log.Error("Managed controller pulse stop faulted; input remains active", ex);
        }
        finally
        {
            lock (_gate)
            {
                if (_pulseSequence == pulseSequence)
                {
                    _pulseStopCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void CancelPulseStopUnderGate()
    {
        _pulseSequence++;
        CancellationTokenSource? cancellation = _pulseStopCancellation;
        _pulseStopCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed class ManagedControllerRouter : IAsyncDisposable
{
    private readonly IHidBackend _backend;
    private readonly ControllerOutputRouter _output;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private HidTargetHandle? _target;
    private long _sourceGeneration;
    private long _lastSequence = long.MinValue;
    private bool _neutral = true;
    private bool _disposed;

    internal ManagedControllerRouter(
        IHidBackend backend,
        IPhysicalHapticSink hapticSink,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _output = new(backend, hapticSink, _timeProvider);
        _backend.TargetLost += OnTargetLost;
    }

    /// <summary>Raised when the backend lost the target and this router faulted.</summary>
    /// <remarks>
    /// The owner needs this to stop reporting controller management as active: the target is gone,
    /// output has been stopped and the handle detached, so every further sample would be written
    /// into nothing while WSGM's surfaces waited on a source that had stopped delivering.
    /// </remarks>
    internal event Action<string>? TargetFaulted;

    internal ManagedTargetState State { get; private set; } = ManagedTargetState.Absent;

    internal HidTargetHandle? Target => _target;

    internal ControllerOutputRouter Output => _output;

    internal async Task<HidTargetHandle> CreateAsync(
        ManagedControllerTarget kind,
        long sourceGeneration,
        CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_target is not null)
            {
                throw new InvalidOperationException("A managed target already exists.");
            }

            return await CreateUnderGateAsync(kind, sourceGeneration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            State = ManagedTargetState.Faulted;
            if (_target is not null)
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(2));
                try
                {
                    await RemoveUnderGateAsync("create-failed", cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    Log.Error("Failed managed target creation also failed cleanup", cleanupException);
                }
            }

            State = ManagedTargetState.Faulted;
            throw;
        }
        finally
        {
            _transition.Release();
        }
    }

    internal void ActivateSource(long sourceGeneration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_target is null || State is not ManagedTargetState.Neutral)
        {
            throw new InvalidOperationException("A verified neutral target is required before routing.");
        }

        _sourceGeneration = sourceGeneration;
        _lastSequence = long.MinValue;
        _output.Attach(_target, sourceGeneration);
        // Activation means a source may affect the target. Even before the first accepted sample,
        // an invalid frame must publish an explicit neutral report rather than relying on the
        // creation-time packet still being current.
        _neutral = false;
        State = ManagedTargetState.Active;
    }

    internal async ValueTask<bool> RouteAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        HidTargetHandle? target = _target;
        if (target is null || State is not ManagedTargetState.Active)
        {
            return false;
        }

        if (!ManagedControllerSampleValidator.TryValidate(
            sample,
            _sourceGeneration,
            _lastSequence,
            _timeProvider.GetUtcNow(),
            out string refusal))
        {
            Log.Warn(
                $"Managed controller input was neutralized: reason={refusal}, "
                    + $"sampleGeneration={sample.CycleGeneration}, "
                    + $"activeGeneration={_sourceGeneration}, sequence={sample.Sequence}, "
                    + $"previousSequence={_lastSequence}, quality={sample.Quality}.");
            await NeutralizeAsync($"source-invalid:{refusal}", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        bool delivered = await _backend.PublishAsync(target, sample, cancellationToken)
            .ConfigureAwait(false);
        if (delivered)
        {
            _lastSequence = sample.Sequence;
            _neutral = ManagedControllerSampleValidator.IsNeutral(sample);
        }

        return delivered;
    }

    internal async Task NeutralizeAsync(string reason, CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await NeutralizeUnderGateAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task RemoveAsync(string reason, CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RemoveUnderGateAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<HidTargetHandle> ReplaceAsync(
        ManagedControllerTarget kind,
        long sourceGeneration,
        CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await RemoveUnderGateAsync("target-replacement", cancellationToken).ConfigureAwait(false);
            return await CreateUnderGateAsync(kind, sourceGeneration, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<HidTargetHandle> CreateUnderGateAsync(
        ManagedControllerTarget kind,
        long sourceGeneration,
        CancellationToken cancellationToken)
    {
        _sourceGeneration = sourceGeneration;
        _lastSequence = long.MinValue;
        CanonicalControllerSample neutral = NewNeutral(sourceGeneration);
        HidTargetHandle target = await _backend.CreateTargetAsync(kind, neutral, cancellationToken)
            .ConfigureAwait(false);
        _target = target;
        if (!await _backend.WaitForEnumerationAsync(target, cancellationToken).ConfigureAwait(false))
        {
            State = ManagedTargetState.Faulted;
            await RemoveUnderGateAsync("enumeration-failed", cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The virtual target did not enumerate.");
        }

        _neutral = true;
        State = ManagedTargetState.Neutral;
        _output.Attach(target, sourceGeneration);
        return target;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _backend.TargetLost -= OnTargetLost;
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(2));
            try
            {
                await RemoveUnderGateAsync("router-dispose", cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                State = ManagedTargetState.Faulted;
                Log.Error("Managed controller cleanup was not verified", ex);
            }
        }
        finally
        {
            _transition.Release();
        }

        await _output.DisposeAsync().ConfigureAwait(false);
        await _backend.DisposeAsync().ConfigureAwait(false);
        _transition.Dispose();
    }

    private async Task NeutralizeUnderGateAsync(string reason, CancellationToken cancellationToken)
    {
        if (_target is not { } target)
        {
            return;
        }

        await _output.StopAsync(reason, cancellationToken).ConfigureAwait(false);
        if (!_neutral)
        {
            await _backend.NeutralizeAsync(target, NewNeutral(_sourceGeneration), cancellationToken)
                .ConfigureAwait(false);
            _neutral = true;
        }

        State = ManagedTargetState.Neutral;
    }

    private async Task RemoveUnderGateAsync(string reason, CancellationToken cancellationToken)
    {
        if (_target is not { } target)
        {
            State = ManagedTargetState.Absent;
            return;
        }

        await NeutralizeUnderGateAsync(reason, cancellationToken).ConfigureAwait(false);
        // Close the managed route before native plugout: a host feedback packet already in flight
        // during removal must see no route to the physical controller or the replacement target.
        _output.Detach(target.Generation);
        await _backend.RemoveTargetAsync(target, cancellationToken).ConfigureAwait(false);
        if (!await _backend.WaitForRemovalAsync(target, cancellationToken).ConfigureAwait(false))
        {
            State = ManagedTargetState.Faulted;
            throw new InvalidOperationException("Virtual target removal was not observed.");
        }

        _target = null;
        _sourceGeneration = 0;
        _lastSequence = long.MinValue;
        _neutral = true;
        State = ManagedTargetState.Absent;
    }

    private CanonicalControllerSample NewNeutral(long sourceGeneration) =>
        CanonicalControllerSample.Neutral(
            _lastSequence == long.MaxValue ? long.MaxValue : Math.Max(0, _lastSequence + 1),
            sourceGeneration,
            _timeProvider.GetUtcNow());

    private void OnTargetLost(object? sender, long generation)
    {
        if (_target?.Generation != generation)
        {
            return;
        }

        State = ManagedTargetState.Faulted;
        Task stop = _output.StopAsync("target-lost", CancellationToken.None);
        _output.Detach(generation);
        _target = null;
        _neutral = true;
        _ = ObserveTargetLossStopAsync(stop);
        Log.Warn($"Managed controller target generation {generation} was lost; routing stopped.");
        TargetFaulted?.Invoke("The virtual controller target was lost.");
    }

    private static async Task ObserveTargetLossStopAsync(Task stop)
    {
        try
        {
            await stop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Managed target was lost and physical output stop was unverified", ex);
        }
    }
}
