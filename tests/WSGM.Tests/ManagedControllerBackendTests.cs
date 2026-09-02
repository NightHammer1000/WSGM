using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Tests;

public sealed class ManagedControllerBackendTests
{
    [Fact]
    public async Task FakeBackendRequiresNeutralFirstStateAndOwnsOnlyOneTarget()
    {
        DeterministicFakeHidBackend backend = new(ManagedControllerTarget.Xbox360);
        CanonicalControllerSample neutral = CanonicalControllerSample.Neutral(
            0,
            7,
            DateTimeOffset.UtcNow);

        HidTargetHandle target = await backend.CreateTargetAsync(
            ManagedControllerTarget.Xbox360,
            neutral,
            CancellationToken.None);

        Assert.Equal(1, target.Generation);
        Assert.Contains("create:1:neutral", backend.Operations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.CreateTargetAsync(
            ManagedControllerTarget.Xbox360,
            neutral,
            CancellationToken.None));
        await backend.DisposeAsync();
    }

    [Fact]
    public async Task RouterReplacesByStoppingOutputNeutralizingAndRemovingBeforeCreate()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(42);
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(
            ManagedControllerTarget.Xbox360,
            42,
            CancellationToken.None);
        router.ActivateSource(42);
        Assert.True(await router.RouteAsync(LiveSample(1, 42),
            CancellationToken.None));

        HidTargetHandle replacement = await router.ReplaceAsync(
            ManagedControllerTarget.DualShock4,
            43,
            CancellationToken.None);

        Assert.Equal(2, replacement.Generation);
        string[] operations = backend.Operations.ToArray();
        Assert.True(Array.IndexOf(operations, "neutralize:1")
            < Array.IndexOf(operations, "remove:1"));
        Assert.True(Array.IndexOf(operations, "remove:1")
            < Array.IndexOf(operations, "create:2:neutral"));
        Assert.Contains("target-replacement", sink.StopReasons);
        Assert.Equal(ManagedTargetState.Neutral, router.State);
    }

    [Fact]
    public async Task RouterDetachesFeedbackRouteBeforeBackendRemovalStarts()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(42);
        await using ManagedControllerRouter router = new(backend, sink);
        HidTargetHandle target = await router.CreateAsync(
            ManagedControllerTarget.SteamDeckComposite,
            42,
            CancellationToken.None);
        int droppedBeforeRemoval = router.Output.DroppedFrames;
        backend.Removing = _ =>
        {
            backend.EmitOutput(new()
            {
                TargetGeneration = target.Generation,
                Timestamp = DateTimeOffset.UtcNow,
                LowFrequency = 1,
                HighFrequency = 1,
            });
            Assert.Equal(droppedBeforeRemoval + 1, router.Output.DroppedFrames);
        };

        await router.ReplaceAsync(
            ManagedControllerTarget.Xbox360,
            43,
            CancellationToken.None);

        Assert.DoesNotContain(sink.Frames, frame =>
            frame.TargetGeneration == target.Generation && frame.LowFrequency > 0);
    }

    [Fact]
    public async Task InvalidSourceSamplePublishesNeutralAndStopsForwarding()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(11);
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(
            ManagedControllerTarget.SteamDeckComposite,
            11,
            CancellationToken.None);
        router.ActivateSource(11);

        CanonicalControllerSample invalid = LiveSample(1, 11) with
        {
            LeftStickX = float.NaN,
        };
        bool delivered = await router.RouteAsync(invalid, CancellationToken.None);

        Assert.False(delivered);
        Assert.Equal(ManagedTargetState.Neutral, router.State);
        Assert.Contains("neutralize:1", backend.Operations);
        Assert.Contains("source-invalid:invalid-or-discontinuous-sample", sink.StopReasons);
    }

    [Fact]
    public async Task OutputRouterDropsStaleGenerationAndClampsUnsupportedChannels()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(5, new()
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Unsupported,
            MaxFramesPerSecond = 1000,
        });
        await using ManagedControllerRouter router = new(backend, sink);
        HidTargetHandle target = await router.CreateAsync(
            ManagedControllerTarget.Xbox360,
            5,
            CancellationToken.None);

        backend.EmitOutput(new()
        {
            TargetGeneration = target.Generation - 1,
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 1,
            HighFrequency = 1,
        });
        backend.EmitOutput(new()
        {
            TargetGeneration = target.Generation,
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 0.75f,
            HighFrequency = 1,
        });

        Assert.True(SpinWait.SpinUntil(() => sink.Frames.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(0.75f, sink.Frames[0].LowFrequency);
        Assert.Equal(0, sink.Frames[0].HighFrequency);
        Assert.True(router.Output.DroppedFrames >= 1);
    }

    [Fact]
    public async Task FakeBackendReleasesDelayedOutputDeterministically()
    {
        DeterministicFakeHidBackend backend = new()
        {
            DelayOutput = true,
        };
        DeterministicFakeHapticSink sink = new(6);
        await using ManagedControllerRouter router = new(backend, sink);
        HidTargetHandle target = await router.CreateAsync(
            ManagedControllerTarget.Xbox360,
            6,
            CancellationToken.None);
        backend.EmitOutput(new()
        {
            TargetGeneration = target.Generation,
            Timestamp = DateTimeOffset.UtcNow,
            LowFrequency = 1,
        });

        Assert.Empty(sink.Frames);
        backend.ReleaseDelayedOutput();
        Assert.True(SpinWait.SpinUntil(() => sink.Frames.Count == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task OutputRouterStopsTimedHapticPulseWithoutLatchingThePhysicalMotors()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(7, new()
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Native,
            MaxFramesPerSecond = 1000,
        });
        await using ManagedControllerRouter router = new(backend, sink);
        HidTargetHandle target = await router.CreateAsync(
            ManagedControllerTarget.SteamDeckComposite,
            7,
            CancellationToken.None);

        backend.EmitOutput(
            new HapticOutputFrame
            {
                TargetGeneration = target.Generation,
                Timestamp = DateTimeOffset.UtcNow,
                LowFrequency = 0.5f,
                HighFrequency = 0.5f,
            },
            stopAfter: TimeSpan.FromMilliseconds(5));

        Assert.True(SpinWait.SpinUntil(
            () => sink.StopReasons.Contains("virtual-controller-pulse-complete"),
            TimeSpan.FromSeconds(1)));
        Assert.Equal(0.5f, sink.Frames[0].LowFrequency);
        Assert.Equal(0, sink.Frames[^1].LowFrequency);
        Assert.Equal(target.Generation, sink.Frames[^1].TargetGeneration);
    }

    [Fact]
    public async Task BackendTargetLossFaultsInputWithoutReusingGeneration()
    {
        DeterministicFakeHidBackend backend = new();
        DeterministicFakeHapticSink sink = new(3);
        await using ManagedControllerRouter router = new(backend, sink);
        await router.CreateAsync(
            ManagedControllerTarget.DualShock4,
            3,
            CancellationToken.None);

        backend.LoseTarget();

        Assert.Equal(ManagedTargetState.Faulted, router.State);
        Assert.Null(router.Target);
    }

    private static CanonicalControllerSample LiveSample(long sequence, long generation) => new()
    {
        Sequence = sequence,
        CycleGeneration = generation,
        Timestamp = DateTimeOffset.UtcNow,
        Buttons = CanonicalButtons.A,
        LeftStickX = 0.25f,
        LeftStickY = -0.25f,
        LeftTrigger = 0.5f,
    };
}

internal sealed class DeterministicFakeHapticSink : IPhysicalHapticSink
{
    private readonly object _gate = new();
    private readonly List<HapticOutputFrame> _frames = [];
    private readonly List<string> _stopReasons = [];

    internal DeterministicFakeHapticSink(
        long sourceGeneration,
        HapticCapabilities? capabilities = null)
    {
        SourceGeneration = sourceGeneration;
        Capabilities = capabilities ?? new()
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Native,
            MaxFramesPerSecond = 60,
        };
    }

    public long SourceGeneration { get; set; }

    public bool IsOwned { get; set; } = true;

    public HapticCapabilities Capabilities { get; set; }

    internal Exception? NextFailure { get; set; }

    internal IReadOnlyList<HapticOutputFrame> Frames
    {
        get
        {
            lock (_gate)
            {
                return _frames.ToArray();
            }
        }
    }

    internal IReadOnlyList<string> StopReasons
    {
        get
        {
            lock (_gate)
            {
                return _stopReasons.ToArray();
            }
        }
    }

    public Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfFaultRequested();
            _frames.Add(frame);
            return Task.CompletedTask;
        }
    }

    public Task StopAsync(
        long targetGeneration,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfFaultRequested();
            _stopReasons.Add(reason);
            _frames.Add(HapticOutputFrame.Stop(targetGeneration, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }

    private void ThrowIfFaultRequested()
    {
        if (NextFailure is not { } failure)
        {
            return;
        }

        NextFailure = null;
        throw failure;
    }
}

internal sealed class DeterministicFakeHidBackend : IHidBackend
{
    private readonly object _gate = new();
    private readonly List<string> _operations = [];
    private readonly Dictionary<long, TaskCompletionSource<bool>> _enumeration = [];
    private readonly Dictionary<long, TaskCompletionSource<bool>> _removal = [];
    private readonly Queue<HidTargetOutput> _delayedOutput = [];
    private long _nextGeneration;
    private HidTargetHandle? _target;
    private bool _disposed;

    internal DeterministicFakeHidBackend(params ManagedControllerTarget[] supportedTargets)
    {
        IReadOnlyList<ManagedControllerTarget> targets = supportedTargets.Length == 0
            ? Enum.GetValues<ManagedControllerTarget>()
            : supportedTargets.ToArray();
        Health = new(
            HidBackendHealthState.Ready,
            "Deterministic fake backend is ready.",
            new(targets));
    }

    public event EventHandler<HidTargetOutput>? OutputReceived;

    public event EventHandler<long>? TargetLost;

    internal HidBackendHealth Health { get; set; }

    internal bool AutoEnumerate { get; set; } = true;

    internal bool AutoRemove { get; set; } = true;

    internal bool DelayOutput { get; set; }

    internal Exception? NextCreateFailure { get; set; }

    internal Exception? NextPublishFailure { get; set; }

    internal Exception? NextRemoveFailure { get; set; }

    internal Action<HidTargetHandle>? Removing { get; set; }

    internal HidTargetHandle? CurrentTarget
    {
        get
        {
            lock (_gate)
            {
                return _target;
            }
        }
    }

    internal IReadOnlyList<string> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.ToArray();
            }
        }
    }

    public Task<HidBackendHealth> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            _operations.Add("discover");
            return Task.FromResult(Health);
        }
    }

    public Task<HidTargetHandle> CreateTargetAsync(
        ManagedControllerTarget kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialNeutralState);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is not null)
            {
                throw new InvalidOperationException("The fake backend already owns a target.");
            }

            if (NextCreateFailure is { } failure)
            {
                NextCreateFailure = null;
                throw failure;
            }

            if (Health.State is not HidBackendHealthState.Ready
                || Health.Capabilities is null
                || !Health.Capabilities.SupportedTargets.Contains(kind))
            {
                throw new InvalidOperationException($"Target {kind} is unavailable.");
            }

            if (!ManagedControllerSampleValidator.IsNeutral(initialNeutralState))
            {
                throw new ArgumentException(
                    "The first target state must be neutral.",
                    nameof(initialNeutralState));
            }

            long generation = ++_nextGeneration;
            _target = new(kind, generation);
            _operations.Add($"create:{generation}:neutral");
            _enumeration.Add(generation, NewCompletionSource(AutoEnumerate));
            _removal.Add(generation, NewCompletionSource(completed: false));
            return Task.FromResult(_target);
        }
    }

    public Task<bool> WaitForEnumerationAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken)
    {
        Task<bool> completion;
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            _operations.Add($"wait-enumeration:{target.Generation}");
            completion = _enumeration[target.Generation].Task;
        }

        return completion.WaitAsync(cancellationToken);
    }

    public ValueTask<bool> PublishAsync(
        HidTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (NextPublishFailure is { } failure)
            {
                NextPublishFailure = null;
                throw failure;
            }

            _operations.Add(ManagedControllerSampleValidator.IsNeutral(sample)
                ? $"publish:{target.Generation}:neutral"
                : $"publish:{target.Generation}:live");
            return ValueTask.FromResult(true);
        }
    }

    public Task NeutralizeAsync(
        HidTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(neutralState);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (!ManagedControllerSampleValidator.IsNeutral(neutralState))
            {
                throw new ArgumentException(
                    "Neutralization requires a neutral sample.",
                    nameof(neutralState));
            }

            _operations.Add($"neutralize:{target.Generation}");
            return Task.CompletedTask;
        }
    }

    public Task RemoveTargetAsync(HidTargetHandle target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Action<HidTargetHandle>? removing;
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (NextRemoveFailure is { } failure)
            {
                NextRemoveFailure = null;
                throw failure;
            }

            _operations.Add($"remove:{target.Generation}");
            removing = Removing;
        }

        removing?.Invoke(target);

        lock (_gate)
        {
            if (AutoRemove)
            {
                CompleteRemovalUnderGate(target.Generation);
            }

            return Task.CompletedTask;
        }
    }

    public Task<bool> WaitForRemovalAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken)
    {
        Task<bool> completion;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_removal.TryGetValue(target.Generation, out TaskCompletionSource<bool>? source))
            {
                throw new InvalidOperationException("The target generation is unknown.");
            }

            _operations.Add($"wait-removal:{target.Generation}");
            completion = source.Task;
        }

        return completion.WaitAsync(cancellationToken);
    }

    internal void CompleteEnumeration(long generation, bool enumerated = true)
    {
        lock (_gate)
        {
            if (!_enumeration.TryGetValue(generation, out TaskCompletionSource<bool>? source))
            {
                throw new InvalidOperationException("The target generation is unknown.");
            }

            source.TrySetResult(enumerated);
        }
    }

    internal void CompleteRemoval(long generation, bool removed = true)
    {
        lock (_gate)
        {
            if (!removed)
            {
                _removal[generation].TrySetResult(false);
                return;
            }

            CompleteRemovalUnderGate(generation);
        }
    }

    internal void EmitOutput(
        HapticOutputFrame frame,
        ManagedControllerTarget? sourceKind = null,
        TimeSpan? stopAfter = null)
    {
        HidTargetOutput output;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is null)
            {
                throw new InvalidOperationException("No target exists.");
            }

            output = new(frame, sourceKind ?? _target.Kind, stopAfter);
            _operations.Add($"output:{frame.TargetGeneration}");
            if (DelayOutput)
            {
                _delayedOutput.Enqueue(output);
                return;
            }
        }

        OutputReceived?.Invoke(this, output);
    }

    internal void ReleaseDelayedOutput()
    {
        while (true)
        {
            HidTargetOutput? output;
            lock (_gate)
            {
                if (!_delayedOutput.TryDequeue(out output))
                {
                    return;
                }
            }

            OutputReceived?.Invoke(this, output);
        }
    }

    internal void LoseTarget()
    {
        long generation;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is null)
            {
                return;
            }

            generation = _target.Generation;
            _target = null;
            _removal[generation].TrySetResult(true);
            _operations.Add($"lost:{generation}");
        }

        TargetLost?.Invoke(this, generation);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _target = null;
            _delayedOutput.Clear();
            foreach (TaskCompletionSource<bool> completion in _enumeration.Values)
            {
                completion.TrySetCanceled();
            }

            foreach (TaskCompletionSource<bool> completion in _removal.Values)
            {
                completion.TrySetCanceled();
            }

            _operations.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    private static TaskCompletionSource<bool> NewCompletionSource(bool completed)
    {
        TaskCompletionSource<bool> source = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            source.SetResult(true);
        }

        return source;
    }

    private void CompleteRemovalUnderGate(long generation)
    {
        _removal[generation].TrySetResult(true);
        if (_target?.Generation == generation)
        {
            _target = null;
        }
    }

    private void ValidateTarget(HidTargetHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_target is null || _target != target)
        {
            throw new InvalidOperationException("The target generation is stale.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
