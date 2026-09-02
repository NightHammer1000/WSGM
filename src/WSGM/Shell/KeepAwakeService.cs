using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Session-lifetime keep-awake coordinator ("standby wake lock"): a manual
/// hold toggled from the quick-access Power tab, plus an automatic hold while the
/// running Steam client reports an active download (polled over the CEF bridge, so a
/// disabled CEF integration simply leaves the automatic side inert). Each hold is its
/// own Windows power request, so <c>powercfg /requests</c> attributes them separately
/// on a device. Deliberately survives desktop/game mode switches — a download should
/// keep the handheld awake in both modes.</summary>
public sealed class KeepAwakeService : IDisposable
{
    /// <summary>How many consecutive inactive polls it takes to drop the download hold.</summary>
    internal const int ReleaseAfterInactivePolls = 2;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly WakeLock _manualStandbyLock =
        new("WSGM keep-awake (manual quick-access toggle)");
    private readonly WakeLock _manualDisplayLock =
        new("WSGM keep-display-on (manual quick-access toggle)",
            Interop.NativeMethods.PowerRequestDisplayRequired);
    private readonly WakeLock _downloadLock = new("WSGM keep-awake (Steam download in progress)");
    private readonly SteamMonitor? _monitor;
    private readonly Func<bool> _automaticCefReady;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _manualGate = new();
    // Guards every download-hold transition together with _autoEnabled and the
    // streak, so a config change and an in-flight poll cannot interleave.
    private readonly object _downloadGate = new();
    private ManualWakeMode _manualMode = ManualWakeMode.Off;
    private bool _autoEnabled;
    private bool _monitorDownloads;
    private bool _downloadActive;
    private bool _waitingForSteamUi;
    private int _inactiveStreak;

    /// <summary>Raised (on an arbitrary thread) whenever a hold engages or drops.</summary>
    public event Action? StateChanged;

    /// <summary>Raised on an arbitrary thread when a usable Steam sample changes
    /// whether a download is active.</summary>
    public event Action<bool>? DownloadActivityChanged;

    /// <summary>The user's current manual wake mode.</summary>
    public ManualWakeMode ManualMode
    {
        get
        {
            lock (_manualGate)
            {
                return _manualMode;
            }
        }
    }

    /// <summary>Whether the automatic download hold is active.</summary>
    public bool DownloadHold => _downloadLock.IsHeld;

    /// <summary>The last usable Steam download activity answer. A transient CEF
    /// failure does not clear it; a confirmed stopped Steam process does.</summary>
    public bool DownloadActive
    {
        get
        {
            lock (_downloadGate)
            {
                return _downloadActive;
            }
        }
    }

    private bool MonitorDownloads
    {
        get
        {
            lock (_downloadGate)
            {
                return _monitorDownloads;
            }
        }
    }

    private KeepAwakeService(
        SteamMonitor? monitor,
        bool autoEnabled,
        bool monitorDownloads,
        Func<bool> automaticCefReady)
    {
        _monitor = monitor;
        _autoEnabled = autoEnabled;
        _monitorDownloads = monitorDownloads;
        _automaticCefReady = automaticCefReady;
    }

    /// <summary>Starts the poll loop and returns the running service.</summary>
    /// <param name="monitor">The shared Steam lifecycle monitor; polls are skipped
    /// while it reports Steam dead. Null polls unconditionally.</param>
    /// <param name="autoEnabled">Initial <c>KeepAwakeDuringDownloads</c> setting.</param>
    /// <param name="monitorDownloads">Whether any session feature currently needs
    /// Steam's download activity signal.</param>
    /// <param name="automaticCefReady">Whether an autonomous CEF query is safe in
    /// the current session state. Desktop mode may return true without Big Picture;
    /// game-mode startup waits for its window.</param>
    public static KeepAwakeService StartNew(
        SteamMonitor? monitor,
        bool autoEnabled,
        bool monitorDownloads,
        Func<bool> automaticCefReady)
    {
        ArgumentNullException.ThrowIfNull(automaticCefReady);
        var service = new KeepAwakeService(
            monitor, autoEnabled, monitorDownloads, automaticCefReady);
        _ = Task.Run(service.RunAsync);
        return service;
    }

    /// <summary>Advances the manual mode one step: Off → Standby →
    /// Standby+Display → Off.</summary>
    public void CycleManualMode()
        => SetManualMode(ManualMode switch
        {
            ManualWakeMode.Off => ManualWakeMode.Standby,
            ManualWakeMode.Standby => ManualWakeMode.StandbyAndDisplay,
            _ => ManualWakeMode.Off,
        });

    /// <summary>Applies a manual wake mode (the quick-access cycle button).</summary>
    /// <param name="mode">The desired mode.</param>
    public void SetManualMode(ManualWakeMode mode)
    {
        lock (_manualGate)
        {
            if (mode == _manualMode)
            {
                return;
            }
            // Acquire before release so a Standby→Standby+Display step never has a
            // gap with no lock held. A failed acquire leaves the previous locks in
            // place and keeps the old mode — the UI stays truthful.
            if (mode != ManualWakeMode.Off && !_manualStandbyLock.Acquire())
            {
                return;
            }
            if (mode == ManualWakeMode.StandbyAndDisplay && !_manualDisplayLock.Acquire())
            {
                return;
            }
            if (mode != ManualWakeMode.StandbyAndDisplay)
            {
                _manualDisplayLock.Release();
            }
            if (mode == ManualWakeMode.Off)
            {
                _manualStandbyLock.Release();
            }
            _manualMode = mode;
            Log.Info($"Keep awake: manual mode {mode} (quick access).");
        }
        StateChanged?.Invoke();
    }

    /// <summary>Applies a reloaded configuration. Turning the automatic side off drops
    /// an engaged download hold immediately; turning all download consumers off also
    /// publishes an inactive state. The manual hold is unaffected.</summary>
    /// <param name="autoEnabled">The new <c>KeepAwakeDuringDownloads</c> setting.</param>
    /// <param name="monitorDownloads">Whether any enabled feature still consumes
    /// Steam download activity.</param>
    public void ApplyConfig(bool autoEnabled, bool monitorDownloads)
    {
        bool released;
        bool activityCleared;
        // Same gate as the poll's own decision: the flag write and the release must
        // not interleave with an in-flight poll's acquire, or a poll that started
        // before the disable could re-engage the hold behind it — and with the loop
        // then skipping polls entirely, nothing would ever release it again.
        lock (_downloadGate)
        {
            _autoEnabled = autoEnabled;
            _monitorDownloads = monitorDownloads;
            released = !autoEnabled && _downloadLock.IsHeld;
            if (released)
            {
                _downloadLock.Release();
                _inactiveStreak = 0;
            }
            activityCleared = !monitorDownloads && _downloadActive;
            if (activityCleared)
            {
                _downloadActive = false;
            }
        }
        if (released)
        {
            Log.Info("Keep awake: download hold released (disabled in settings).");
            StateChanged?.Invoke();
        }
        if (activityCleared)
        {
            DownloadActivityChanged?.Invoke(false);
        }
    }

    private async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (MonitorDownloads)
                {
                    await PollOnceAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Keep awake poll failed: {ex.Message}");
            }
            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        var steamAlive = _monitor is null || _monitor.IsAlive;
        DownloadOverview? overview = null;
        var detail = "Steam not running";
        if (steamAlive)
        {
            if (!_automaticCefReady())
            {
                if (!_waitingForSteamUi)
                {
                    _waitingForSteamUi = true;
                    Log.Info("Steam downloads: waiting for the Big Picture window before CEF polling.");
                }
                // This is not an inactive sample. Preserve the prior download state
                // and wake lock across a Steam restart while its UI is rebuilding.
                return;
            }
            if (_waitingForSteamUi)
            {
                _waitingForSteamUi = false;
                Log.Info("Steam downloads: Big Picture is ready; starting CEF polling.");
            }
            overview = await SteamDownloads.QueryAsync(token).ConfigureAwait(false);
            if (overview is { } o)
            {
                detail = o.Active
                    ? $"{o.State}, appid {o.AppId}, {o.NetworkBytesPerSecond / 1_000_000.0:0.0} MB/s"
                    : o.Paused ? $"{o.State}, paused" : o.State;
            }
            else
            {
                // Unreachable counts as an inactive sample: after the release streak
                // a closed/dead Steam drops the hold instead of pinning the device
                // awake forever.
                detail = "Steam client unreachable";
            }
        }

        // The whole decision runs under the gate ApplyConfig also takes, and
        // re-reads _autoEnabled inside it: the CEF query above can take seconds,
        // and a disable that lands during it must win over this (now stale) sample.
        string? change = null;
        bool? activityChange = null;
        lock (_downloadGate)
        {
            var activity = _monitorDownloads
                ? SteamDownloads.ResolveActivity(_downloadActive, steamAlive, overview)
                : false;
            if (activity != _downloadActive)
            {
                _downloadActive = activity;
                activityChange = activity;
            }
            var hadHold = _downloadLock.IsHeld;
            var sampleActive = overview?.Active == true && _autoEnabled;
            var (hold, streak) = NextDownloadHold(hadHold, _inactiveStreak, sampleActive);
            _inactiveStreak = streak;
            if (hold && !hadHold)
            {
                if (_downloadLock.Acquire())
                {
                    change = $"acquired ({detail})";
                }
            }
            else if (!hold && hadHold)
            {
                _downloadLock.Release();
                change = $"released ({detail})";
            }
        }
        if (change is not null)
        {
            Log.Info($"Keep awake: download hold {change}.");
            StateChanged?.Invoke();
        }
        if (activityChange is { } active)
        {
            Log.Info($"Steam downloads: {(active ? "active" : "inactive")} ({detail}).");
            DownloadActivityChanged?.Invoke(active);
        }
    }

    /// <summary>Pure hold/release policy for the automatic download wake lock: acquire on
    /// the first active sample, release only after a run of consecutive inactive polls so
    /// a brief gap between queued items (or one unreachable poll during a Steam client
    /// restart) does not flap the hold.</summary>
    /// <param name="currentHold">Whether the download hold is currently active.</param>
    /// <param name="inactiveStreak">Consecutive inactive polls seen so far.</param>
    /// <param name="sampleActive">Whether this poll saw an active transfer; an
    /// unreachable poll counts as inactive.</param>
    /// <returns>The desired hold state and the updated streak.</returns>
    internal static (bool Hold, int InactiveStreak) NextDownloadHold(
        bool currentHold, int inactiveStreak, bool sampleActive)
    {
        if (sampleActive)
        {
            return (true, 0);
        }
        var streak = Math.Min(inactiveStreak + 1, ReleaseAfterInactivePolls);
        return (currentHold && streak < ReleaseAfterInactivePolls, streak);
    }

    /// <summary>Stops the poll loop and drops both holds.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _manualStandbyLock.Dispose();
        _manualDisplayLock.Dispose();
        _downloadLock.Dispose();
    }
}
