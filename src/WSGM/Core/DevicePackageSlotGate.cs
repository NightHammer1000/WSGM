using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Cross-process exclusion for the one protected Device Plugin slot. Runtime discovery
/// and host creation share this gate with package installation/removal so the slot cannot move
/// underneath a host that is entering its lifecycle.</summary>
internal sealed class DevicePackageSlotGate : IAsyncDisposable
{
    internal const string ProductionName = @"Global\WSGM.DevicePackageSlot";
    private readonly ManualResetEventSlim _releaseRequested = new(initialState: false);
    private readonly TaskCompletionSource _releaseCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DevicePackageSlotGate?> _acquisition = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _name;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _cancellationToken;
    private readonly Action? _waitStarted;
    private int _disposeState;

    private DevicePackageSlotGate(
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action? waitStarted)
    {
        _name = name;
        _timeout = timeout;
        _cancellationToken = cancellationToken;
        _waitStarted = waitStarted;
    }

    /// <summary>Acquires the production package-slot gate within one bounded wait.</summary>
    internal static Task<DevicePackageSlotGate?> TryAcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        TryAcquireAsync(ProductionName, timeout, cancellationToken);

    /// <summary>Acquires a named package-slot gate. The name seam keeps cross-process exclusion
    /// testable without touching the production object.</summary>
    internal static async Task<DevicePackageSlotGate?> TryAcquireAsync(
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action? waitStarted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var contender = new DevicePackageSlotGate(name, timeout, cancellationToken, waitStarted);
        var ownerThread = new Thread(contender.OwnMutex)
        {
            IsBackground = true,
            Name = "WSGM device package slot gate",
        };
        ownerThread.Start();
        return await contender._acquisition.Task.ConfigureAwait(false);
    }

    /// <summary>Runs one bounded operation under the production gate on the calling thread. This
    /// exists for recovery-first startup work that must preserve the process entry thread's STA
    /// apartment before Avalonia creates its dispatcher.</summary>
    internal static T? TryRunSynchronously<T>(TimeSpan timeout, Func<T> operation)
        where T : class =>
        TryRunSynchronously(ProductionName, timeout, operation);

    /// <summary>Runs one bounded operation under a named gate on the calling thread.</summary>
    internal static T? TryRunSynchronously<T>(
        string name,
        TimeSpan timeout,
        Func<T> operation,
        Action? waitStarted = null)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var mutex = new Mutex(initiallyOwned: false, name);
        waitStarted?.Invoke();
        if (WaitForSlot(mutex, timeout, CancellationToken.None) is not SlotWait.Acquired)
        {
            return null;
        }

        try
        {
            return operation();
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The operation and release stay on one thread. If Windows reports ownership
                // already lost, teardown is complete and the next waiter still recovers safely.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _releaseRequested.Set();
        }

        // Mutex ownership is thread-affine. The dedicated owner releases it and signals this
        // completion, which keeps disposal thread-independent without weakening crash recovery.
        await _releaseCompleted.Task.ConfigureAwait(false);
    }

    private void OwnMutex()
    {
        Mutex? mutex = null;
        bool ownsMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, _name);
            _waitStarted?.Invoke();
            SlotWait wait = WaitForSlot(mutex, _timeout, _cancellationToken);
            if (wait is SlotWait.TimedOut)
            {
                _acquisition.TrySetResult(null);
                return;
            }
            if (wait is SlotWait.Canceled || _cancellationToken.IsCancellationRequested)
            {
                ownsMutex = wait is SlotWait.Acquired;
                _acquisition.TrySetCanceled(_cancellationToken);
                return;
            }

            ownsMutex = true;
            _acquisition.TrySetResult(this);
            _releaseRequested.Wait();
        }
        catch (Exception ex)
        {
            _acquisition.TrySetException(ex);
        }
        finally
        {
            if (ownsMutex && mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The only release belongs to this owner thread. Treat an OS-reported loss as
                    // completed teardown so callers never hang while the process is already safe.
                }
            }
            mutex?.Dispose();
            _releaseCompleted.TrySetResult();
        }
    }

    private enum SlotWait
    {
        Acquired,
        TimedOut,
        Canceled,
    }

    /// <summary>One bounded wait on the slot mutex, shared by the async owner thread and the
    /// synchronous STA path. An abandoned mutex counts as acquired: the previous owner crashed and
    /// the slot must stay recoverable.</summary>
    private static SlotWait WaitForSlot(
        Mutex mutex,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        int signalled;
        try
        {
            signalled = WaitHandle.WaitAny(
                [mutex, cancellationToken.WaitHandle],
                timeout);
        }
        catch (AbandonedMutexException ex) when (ex.MutexIndex == 0)
        {
            return SlotWait.Acquired;
        }

        return signalled switch
        {
            WaitHandle.WaitTimeout => SlotWait.TimedOut,
            0 => SlotWait.Acquired,
            _ => SlotWait.Canceled,
        };
    }
}
