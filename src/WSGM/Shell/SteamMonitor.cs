using System;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Watches Steam by process-name polling (the Big Picture window lives in
/// steamwebhelper.exe, so name polling is authoritative). Raises SteamExited on the
/// UI thread when it transitions alive → dead.</summary>
public sealed class SteamMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _wasAlive;

    /// <summary>Raised when Steam transitions from alive to absent while monitoring is active.</summary>
    public event Action? SteamExited;

    /// <summary>Raised when Steam transitions from absent back to alive (a fresh client
    /// start while WSGM keeps running — e.g. after an update restarts Steam). Not raised
    /// for a Steam that was already alive when monitoring began.</summary>
    public event Action? SteamStarted;

    private bool _seenDead;
    private bool _pollInFlight;

    /// <summary>Gets whether Steam was alive during the most recent poll.</summary>
    public bool IsAlive { get; private set; }

    /// <summary>While true (desktop mode, or after the user deliberately closed
    /// Steam) an alive→dead transition is swallowed instead of raising SteamExited,
    /// so nothing auto-relaunches or pops the overlay.</summary>
    public bool Paused { get; set; }

    /// <summary>Creates and starts a UI-thread Steam lifecycle monitor.</summary>
    public SteamMonitor()
    {
        // The convenience ctor taking a callback auto-starts the timer (see
        // GamepadService) — keep construction and Start() explicit.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        if (_pollInFlight)
        {
            return;
        }
        _pollInFlight = true;
        // Steam.IsRunning takes a full process snapshot per watched name — off the
        // UI thread, with only the resulting boolean marshalled back, so the 16 ms
        // gamepad poll and the overlay animations never wait on it. All monitor
        // state stays UI-thread owned in Apply.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            bool alive;
            try
            {
                alive = Steam.IsRunning;
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam liveness poll failed: {ex.Message}");
                Dispatcher.UIThread.Post(() => _pollInFlight = false);
                return;
            }
            Dispatcher.UIThread.Post(() => Apply(alive));
        });
    }

    private void Apply(bool alive)
    {
        _pollInFlight = false;
        IsAlive = alive;

        // _wasAlive can only be true after a poll saw Steam alive, so the
        // seen-alive-once requirement is implied.
        var exited = _wasAlive && !IsAlive;
        _wasAlive = IsAlive;
        if (exited)
        {
            if (Paused)
            {
                Log.Info("Steam exited (monitor paused, not reacting).");
            }
            else
            {
                Log.Info("Steam exited.");
                SteamExited?.Invoke();
            }
        }

        // Dead → alive: a fresh client start. Gated on having SEEN it dead so a
        // Steam already alive when monitoring began raises nothing.
        if (!IsAlive)
        {
            _seenDead = true;
        }
        else if (_seenDead)
        {
            _seenDead = false;
            if (!Paused)
            {
                Log.Info("Steam started.");
                SteamStarted?.Invoke();
            }
        }
    }

    /// <summary>Stops the lifecycle monitor.</summary>
    public void Dispose() => _timer.Stop();
}
