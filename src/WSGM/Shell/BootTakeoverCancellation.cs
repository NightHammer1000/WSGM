using System;
using System.Threading;

namespace WSGM.Shell;

/// <summary>Coordinates the splash's desktop recovery request with the service-boot
/// takeover running on a worker thread. A request accepted while active is sticky;
/// after completion, the caller must use the ordinary desktop transition instead.</summary>
internal sealed class BootTakeoverCancellation : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private BootTakeoverState _state;

    /// <summary>Cancellation token observed at every reversible takeover boundary.</summary>
    internal CancellationToken Token => _source.Token;

    /// <summary>Whether the splash requested desktop mode while this takeover was active.</summary>
    internal bool DesktopRequested
    {
        get
        {
            lock (_gate)
            {
                return _state == BootTakeoverState.DesktopRequested;
            }
        }
    }

    /// <summary>Whether application teardown cancelled this takeover without requesting a new
    /// desktop transition from the splash.</summary>
    internal bool ShutdownRequested
    {
        get
        {
            lock (_gate)
            {
                return _state == BootTakeoverState.ShutdownRequested;
            }
        }
    }

    /// <summary>Requests cancellation of the active takeover.</summary>
    /// <returns>True when this coordinator accepted the request; false when the
    /// takeover had already completed and the ordinary desktop transition owns it.</returns>
    internal bool RequestDesktop()
    {
        lock (_gate)
        {
            if (_state != BootTakeoverState.Active)
            {
                return false;
            }
            _state = BootTakeoverState.DesktopRequested;
            _source.Cancel();
            return true;
        }
    }

    /// <summary>Cancels an active takeover for application teardown. Unlike a splash request, this
    /// never starts another session transition; the shutdown owner decides whether recovery is
    /// needed after every in-flight transition has settled.</summary>
    internal bool RequestShutdown()
    {
        lock (_gate)
        {
            if (_state is BootTakeoverState.Completed or BootTakeoverState.ShutdownRequested)
            {
                return false;
            }
            _state = BootTakeoverState.ShutdownRequested;
            _source.Cancel();
            return true;
        }
    }

    /// <summary>Closes the cancellation window without overwriting an accepted request.</summary>
    internal void Complete()
    {
        lock (_gate)
        {
            if (_state == BootTakeoverState.Active)
            {
                _state = BootTakeoverState.Completed;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _state = BootTakeoverState.Completed;
            _source.Dispose();
        }
    }

    private enum BootTakeoverState
    {
        Active,
        DesktopRequested,
        ShutdownRequested,
        Completed,
    }
}
