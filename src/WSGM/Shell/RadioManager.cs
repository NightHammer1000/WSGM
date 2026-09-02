using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using RadioPower = WindowsDeviceControl.WindowsRadio.Power;

namespace WSGM.Shell;

/// <summary>Wi-Fi and Bluetooth state and control for the game-mode UI.
///
/// Windows' own radio flyouts are unreachable in game mode — there is no
/// Explorer shell to host them, and `ms-settings:` cannot activate without one —
/// so this is the only way a user on a handheld can join a network or pair a
/// controller without leaving game mode.
///
/// Windows calls block (WinRT round trips, WLAN handles), so nothing here
/// runs on the UI thread: a background refresh publishes results back through
/// the dispatcher. Rows are reconciled in place, because rebuilding the
/// collections would drop the control under the gamepad cursor.</summary>
public sealed class RadioManager : INotifyPropertyChanged, IDisposable
{
    /// <summary>Raised after a status property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when Windows asks a pairing question and the UI must
    /// answer with <see cref="RespondToPairing"/>. Always on the UI thread.</summary>
    public event Action<PairingPrompt>? PairingRequested;

    /// <summary>Raised when a pairing attempt finishes, with a message to show.
    /// Always on the UI thread.</summary>
    public event Action<string>? PairingFinished;

    private DispatcherTimer? _timer;
    private int _refreshing;
    private bool _scanning;
    private bool _accessLogged;
    private volatile bool _disposed;

    /// <summary>Describes a pairing question for the UI to render.</summary>
    /// <param name="Token">Identifies the request when answering.</param>
    /// <param name="Kind">Which ceremony to present.
    /// <see cref="WindowsRadio.PairingKind.Unknown"/> is deliberately presented as confirm-only
    /// rather than declined: an accept is what Windows most often wants, and the log line records
    /// the raw kind so a device that really needs another ceremony is still diagnosable.</param>
    /// <param name="Pin">The PIN to show, for display-pin and confirm-pin-match.</param>
    /// <param name="DeviceName">The device being paired.</param>
    public readonly record struct PairingPrompt(
        uint Token,
        WindowsRadio.PairingKind Kind,
        string Pin,
        string DeviceName);

    /// <summary>Gets the Wi-Fi networks in range, strongest first.</summary>
    public ObservableCollection<WifiNetworkEntry> Networks { get; } = [];

    /// <summary>Gets the Bluetooth devices that are paired or visible.</summary>
    public ObservableCollection<BluetoothDeviceEntry> BluetoothDevices { get; } = [];

    private RadioPower _wifiPower = RadioPower.Unknown;
    /// <summary>Gets the Wi-Fi radio's power state.</summary>
    public RadioPower WifiPower
    {
        get => _wifiPower;
        private set
        {
            if (_wifiPower != value)
            {
                _wifiPower = value;
                Raise(nameof(WifiPower));
                Raise(nameof(WifiOn));
                Raise(nameof(WifiStateText));
                Raise(nameof(WifiUnavailableText));
                Raise(nameof(WifiIconState));
            }
        }
    }

    private RadioPower _bluetoothPower = RadioPower.Unknown;
    /// <summary>Gets the Bluetooth radio's power state.</summary>
    public RadioPower BluetoothPower
    {
        get => _bluetoothPower;
        private set
        {
            if (_bluetoothPower != value)
            {
                _bluetoothPower = value;
                Raise(nameof(BluetoothPower));
                Raise(nameof(BluetoothOn));
                Raise(nameof(BluetoothStateText));
                Raise(nameof(BluetoothUnavailableText));
                Raise(nameof(BluetoothIconState));
            }
        }
    }

    /// <summary>Gets whether the Wi-Fi radio is on.</summary>
    public bool WifiOn => WifiPower == RadioPower.On;

    /// <summary>Gets whether the Bluetooth radio is on.</summary>
    public bool BluetoothOn => BluetoothPower == RadioPower.On;

    /// <summary>Gets what to tell the user when the Wi-Fi list is empty because
    /// the radio is not usable. "Off" is only one of the reasons, and the least
    /// alarming: a blocked or missing adapter cannot be switched on at all, and
    /// saying "off" leaves the user pressing a dead switch.</summary>
    public string WifiUnavailableText => DescribeUnavailable(WifiPower, "Wi-Fi");

    /// <summary>Gets the same explanation for Bluetooth.</summary>
    public string BluetoothUnavailableText => DescribeUnavailable(BluetoothPower, "Bluetooth");

    /// <summary>The reason a radio is not usable, named rather than flattened
    /// into "off".</summary>
    /// <param name="power">The radio's power state.</param>
    /// <param name="label">The radio's display name.</param>
    internal static string DescribeUnavailable(RadioPower power, string label) => power switch
    {
        RadioPower.Off => $"{label} is off.",
        RadioPower.Disabled => $"{label} is blocked by Windows or a hardware switch.",
        RadioPower.Absent => $"This device has no {label} adapter.",
        RadioPower.Unknown => $"{label} state is unavailable.",
        _ => "",
    };

    /// <summary>Gets what the taskbar's Wi-Fi tile should show. Off and merely
    /// disconnected are different problems and must not look the same.</summary>
    public Controls.RadioIconState WifiIconState => WifiPower switch
    {
        RadioPower.On when WifiConnected => Controls.RadioIconState.Connected,
        RadioPower.On => Controls.RadioIconState.Disconnected,
        _ => Controls.RadioIconState.Off,
    };

    /// <summary>Gets what the taskbar's Bluetooth tile should show. Accent only
    /// when a device is actually connected — a lone powered radio is
    /// "disconnected", the same distinction the Wi-Fi tile draws.</summary>
    public Controls.RadioIconState BluetoothIconState => BluetoothPower switch
    {
        RadioPower.On when BluetoothConnectedCount > 0 => Controls.RadioIconState.Connected,
        RadioPower.On => Controls.RadioIconState.Disconnected,
        _ => Controls.RadioIconState.Off,
    };

    private int _bluetoothConnectedCount;
    /// <summary>Gets how many Bluetooth devices have a live connection. Read
    /// from PnP state every status tick, so the tile is correct whether or not
    /// the panel has ever been opened.</summary>
    public int BluetoothConnectedCount
    {
        get => _bluetoothConnectedCount;
        private set
        {
            if (_bluetoothConnectedCount != value)
            {
                _bluetoothConnectedCount = value;
                Raise(nameof(BluetoothConnectedCount));
                Raise(nameof(BluetoothIconState));
            }
        }
    }

    private bool _wifiConnected;
    /// <summary>Gets whether Wi-Fi is joined to a network — the only state that
    /// tints the taskbar's Wi-Fi tile with the accent color.</summary>
    public bool WifiConnected
    {
        get => _wifiConnected;
        private set
        {
            if (_wifiConnected != value)
            {
                _wifiConnected = value;
                Raise(nameof(WifiConnected));
                Raise(nameof(WifiIconState));
            }
        }
    }

    private int _wifiSignal;
    /// <summary>Gets the joined network's signal quality, 0-100. Drives the bars
    /// on the taskbar tile.</summary>
    public int WifiSignal
    {
        get => _wifiSignal;
        private set
        {
            if (_wifiSignal != value)
            {
                _wifiSignal = value;
                Raise(nameof(WifiSignal));
            }
        }
    }

    private string _connectedSsid = "";
    /// <summary>Gets the joined network's name, or an empty string.</summary>
    public string ConnectedSsid
    {
        get => _connectedSsid;
        private set => Set(ref _connectedSsid, value, nameof(ConnectedSsid));
    }

    private string _wifiStateText = "State unavailable";
    /// <summary>Gets the Wi-Fi state line for the taskbar tile's flyout.</summary>
    public string WifiStateText
    {
        get => _wifiStateText;
        private set => Set(ref _wifiStateText, value, nameof(WifiStateText));
    }

    private string _bluetoothStateText = "State unavailable";
    /// <summary>Gets the Bluetooth state line for the taskbar tile's flyout.</summary>
    public string BluetoothStateText
    {
        get => _bluetoothStateText;
        private set => Set(ref _bluetoothStateText, value, nameof(BluetoothStateText));
    }

    /// <summary>Whether <see cref="StatusText"/> currently holds a scan
    /// failure, and may therefore be cleared once scanning recovers.</summary>
    private bool _statusIsScanFailure;

    private string _statusText = "";
    /// <summary>Gets the last thing that happened, for the panel's status line.
    /// Empty when there is nothing to report.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            // Any writer takes ownership of the message; only Apply's own scan
            // branch re-claims it, so a connect or pairing result is never
            // cleared by an unrelated successful scan.
            _statusIsScanFailure = false;
            if (_statusText != value)
            {
                _statusText = value;
                Raise(nameof(StatusText));
                Raise(nameof(HasStatus));
            }
        }
    }

    /// <summary>Gets whether a status line should be shown.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Performs a first refresh and starts the update timer.
    /// UI-thread callers only. Idempotent.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer is not null)
        {
            return;
        }
        QueueRefresh();
        // Parameterless ctor + explicit Start: the 3-arg ctor auto-starts.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Stops the update timer. Idempotent; bound values keep their last
    /// state.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopScanning();
        if (_pairingInProgress)
        {
            if (_pairingToken != 0)
            {
                RespondToPairing(_pairingToken, accept: false, null);
            }
            FinishPairing();
        }
        PairingRequested = null;
        PairingFinished = null;
        if (_timer is null)
        {
            return;
        }
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>Begins actively scanning for networks and devices. Called when
    /// the radio panel opens: an idle taskbar must not pay for scans nobody is
    /// looking at, which on a handheld is battery.</summary>
    public void StartScanning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_scanning)
        {
            return;
        }
        _scanning = true;
        Log.Info("Radio panel: scanning started.");
        // Publish the cached scan list immediately — it is already there and
        // costs milliseconds — then ask for a fresh scan and let the live feeds
        // fill in the rest.
        QueueRefresh();
        StartFeeds();
        Rescan();
    }

    /// <summary>Stops actively scanning. Idempotent.</summary>
    public void StopScanning()
    {
        if (!_scanning)
        {
            return;
        }
        _scanning = false;
        StopFeeds();
        BluetoothScanning = false;
        Log.Info("Radio panel: scanning stopped.");
    }

    /// <summary>Asks for a fresh sweep of both radios.
    ///
    /// Bound to the panel's refresh button: without it the only way to look for
    /// a network or a device that appeared after opening was to close and reopen
    /// the panel.</summary>
    public void Rescan()
    {
        Log.Info("Radio panel: rescan requested.");
        StatusText = "";
        if (BluetoothPower == RadioPower.On)
        {
            BluetoothScanning = true;
            // A fresh sweep starts a fresh census; stale rows are dropped when
            // it completes.
            _seenThisSweep.Clear();
            // Restarting the watcher re-runs the initial enumeration, which is
            // what picks up a device that has only just been put into pairing
            // mode. Existing rows survive because they are matched by id.
            StopAndRestartBluetoothWatch();
        }
        _ = Task.Run(() =>
        {
            try
            {
                WindowsRadio.RequestWifiScan();
            }
            catch (Exception ex)
            {
                // Nothing awaits this task, so an unobserved throw would only
                // surface at an arbitrary later finalization — if ever.
                Log.Warn($"Wi-Fi scan request threw: {ex.Message}");
            }
        });
        QueueRefresh();
    }

    private void StopAndRestartBluetoothWatch()
    {
        if (!_feedsStarted)
        {
            return;
        }
        QueueFeedWork(() =>
        {
            try
            {
                WindowsRadio.StopBluetoothWatch();
                WindowsRadio.StartBluetoothWatch(OnBluetoothChanged);
            }
            catch
            {
                Dispatcher.UIThread.Post(() => BluetoothScanning = false);
                throw;
            }
        });
    }

    /// <summary>Serializes the Windows feed operations onto background threads.
    ///
    /// They BLOCK — a watcher can enumerate devices for seconds — and they
    /// must not interleave: a stop racing a start would leave the watcher in
    /// whichever state finished last. UI-thread callers only, so the field
    /// needs no lock.
    ///
    /// STATIC on purpose: the watchers are process-wide singletons, but
    /// managers are not — closing and reopening the taskbar builds a new one
    /// while the old is still tearing down. With a queue each, the old
    /// manager's stop could land after the new manager's start and silently
    /// leave the reopened panel with no discovery at all.</summary>
    private static Task _feedWork = Task.CompletedTask;

    private void QueueFeedWork(Action work)
    {
        _feedWork = _feedWork.ContinueWith(
            _ =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Radio feed operation failed: {ex.Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private bool _bluetoothScanning;
    /// <summary>Gets whether a Bluetooth sweep is still running, so the panel can
    /// show that more devices may still appear.</summary>
    public bool BluetoothScanning
    {
        get => _bluetoothScanning;
        private set => Set(ref _bluetoothScanning, value, nameof(BluetoothScanning));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // A safety net only: the live feeds carry every real change, so this is
        // here to catch a driver that stops reporting, not to drive the UI.
        QueueRefresh();
    }

    /// <summary>Refreshes state off the UI thread, at most one at a time. A slow
    /// Windows call must not queue up behind itself every tick.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = ReadSnapshot(_scanning);
                Dispatcher.UIThread.Post(() => Apply(snapshot));
            }
            catch (Exception ex)
            {
                Log.Warn($"Radio refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private sealed record Snapshot(
        RadioPower WifiPower,
        RadioPower BluetoothPower,
        int BluetoothConnected,
        WindowsRadio.WifiConnectionState WifiState,
        int WifiSignal,
        string WifiSsid,
        bool IncludedNetworks,
        IReadOnlyList<WindowsRadio.WifiNetwork> Networks,
        IReadOnlyList<CoreAudio.BluetoothAudioContainer>? AudioContainers,
        string? Failure);

    private static Snapshot ReadSnapshot(bool includeNetworks)
    {
        var wifiPower = ReadPower(WindowsRadio.RadioKind.WiFi);
        var bluetoothPower = ReadPower(WindowsRadio.RadioKind.Bluetooth);

        // Answered from PnP state, no inquiry — cheap enough for every tick,
        // and the only way the tile can distinguish "on" from "connected"
        // without the panel's watcher running.
        var bluetoothConnected = 0;
        if (bluetoothPower == RadioPower.On)
        {
            try
            {
                bluetoothConnected = WindowsRadio.ConnectedBluetoothCount();
            }
            catch (Exception ex)
            {
                Log.Change("radio-bluetooth-connected-query",
                    $"Bluetooth connected-device query unavailable: {ex.Message}");
            }
        }

        // State, signal and SSID together, every tick: reading the signal only
        // while the panel was open left the taskbar tile with no bars until the
        // panel had been opened once.
        var wifiState = WindowsRadio.WifiConnectionState.Unknown;
        var wifiSignal = 0;
        var wifiSsid = "";
        try
        {
            var status = WindowsRadio.GetWifiStatus();
            wifiState = status.State;
            wifiSignal = status.Signal;
            wifiSsid = status.Ssid;
        }
        catch (Exception ex)
        {
            Log.Change("radio-wifi-status-query",
                $"Wi-Fi status query unavailable: {ex.Message}");
        }

        IReadOnlyList<WindowsRadio.WifiNetwork> networks = [];
        string? failure = null;
        // Only a SUCCESSFUL listing counts as carrying a network list: a failed
        // one would make Apply reconcile against an empty collection and wipe
        // every row over a transient WLAN-service error.
        var listed = false;

        if (includeNetworks && wifiPower == RadioPower.On)
        {
            try
            {
                networks = WindowsRadio.ListWifiNetworks();
                listed = true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
        }

        // Only while the panel is open: the audio-endpoint set decides which
        // Bluetooth rows get a Connect action, and only the panel shows rows.
        // Local PnP enumeration, no radio traffic.
        IReadOnlyList<CoreAudio.BluetoothAudioContainer>? audio = null;
        if (includeNetworks)
        {
            try
            {
                audio = CoreAudio.ListBluetoothAudioContainers();
            }
            catch (Exception ex)
            {
                Log.Warn($"Bluetooth audio endpoint query failed: {ex.Message}");
            }
        }

        return new Snapshot(
            wifiPower,
            bluetoothPower,
            bluetoothConnected,
            wifiState,
            wifiSignal,
            wifiSsid,
            listed,
            networks,
            audio,
            failure);
    }

    private bool _feedsStarted;

    /// <summary>Ids reported during the current discovery sweep. Anything absent
    /// when the sweep completes is no longer there.</summary>
    private readonly HashSet<string> _seenThisSweep = new(StringComparer.Ordinal);

    /// <summary>Containers with Bluetooth audio endpoints, mapped to whether
    /// those endpoints are live. The devices whose rows get a
    /// Connect/Disconnect action, and which way round it reads. Refreshed with
    /// each panel-open snapshot.</summary>
    private readonly Dictionary<string, bool> _audioContainers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies the audio-endpoint facts to one row.</summary>
    private void ApplyAudioState(BluetoothDeviceEntry row)
    {
        var known = row.ContainerId.Length > 0
            && _audioContainers.TryGetValue(row.ContainerId, out var active);
        row.AudioConnectable = known;
        row.AudioActive = known && _audioContainers[row.ContainerId];
    }

    /// <summary>Starts the live Bluetooth and Wi-Fi feeds.
    ///
    /// Both are push, not poll, because that is the difference between a picker
    /// that feels dead and one that behaves like the Windows applet. The
    /// blocking Bluetooth enumeration takes ~30 s before showing anything; the
    /// watcher reports the first device in about 10 ms. Wi-Fi likewise: the
    /// driver refreshes its scan list when it feels like it, so an interval
    /// either wastes work or shows a network seconds late.</summary>
    private void StartFeeds()
    {
        if (_feedsStarted)
        {
            return;
        }
        _feedsStarted = true;
        QueueFeedWork(StartBluetoothFeed);
        QueueFeedWork(StartWifiFeed);
    }

    private void StartBluetoothFeed()
    {
        try
        {
            WindowsRadio.StartBluetoothWatch(OnBluetoothChanged);
        }
        catch
        {
            Dispatcher.UIThread.Post(() => BluetoothScanning = false);
            throw;
        }
    }

    private void StartWifiFeed() => WindowsRadio.StartWifiWatch(OnWifiEvent);

    private void StopFeeds()
    {
        if (!_feedsStarted)
        {
            return;
        }
        _feedsStarted = false;
        QueueFeedWork(() =>
        {
            WindowsRadio.StopBluetoothWatch();
            WindowsRadio.StopWifiWatch();
        });
    }

    private void OnBluetoothChanged(WindowsRadio.BluetoothChange change)
    {
        var device = change.Device;
        Dispatcher.UIThread.Post(() =>
            ApplyDeviceChange(
                change.Kind,
                device.Id,
                device.Name,
                device.Paired,
                device.CanPair,
                device.Connected,
                device.Container));
    }

    private void OnWifiEvent(WindowsRadio.WifiWatchEvent change)
    {
        // A scan-list refresh means new networks are visible right now; a
        // connection change means the "connected" marker moved.
        Dispatcher.UIThread.Post(QueueRefresh);
        if (change == WindowsRadio.WifiWatchEvent.ConnectionChanged)
        {
            Log.Info("Wi-Fi: connection state changed.");
        }
    }

    /// <summary>Applies one watcher event to the device list.</summary>
    /// <param name="change">What the watcher reported.</param>
    /// <param name="id">The device id.</param>
    /// <param name="name">The display name.</param>
    /// <param name="paired">Whether it is paired.</param>
    /// <param name="canPair">Whether it can be paired.</param>
    /// <param name="connected">Whether it has a live connection.</param>
    /// <param name="container">The device container id, or empty.</param>
    private void ApplyDeviceChange(
        WindowsRadio.BluetoothChangeKind change, string id, string name, bool paired,
        bool canPair, bool connected, string container)
    {
        if (change == WindowsRadio.BluetoothChangeKind.EnumerationCompleted)
        {
            BluetoothScanning = false;
            // Anything not seen during this sweep is gone. Windows keeps its
            // association-endpoint records long after a device stops
            // advertising, and the watcher does not always report a Removed for
            // them, so an unpaired device that has been switched off would
            // otherwise sit in the list forever. Paired devices stay: they are
            // legitimately known whether or not they are in range.
            var stale = 0;
            for (var i = BluetoothDevices.Count - 1; i >= 0; i--)
            {
                var candidate = BluetoothDevices[i];
                if (_seenThisSweep.Contains(candidate.Id) || candidate.Busy)
                {
                    continue;
                }
                if (candidate.Paired)
                {
                    // Retained as a known device, but a sweep that did not see
                    // it is the evidence that it is offline.
                    candidate.Connected = false;
                    candidate.AudioActive = false;
                    continue;
                }
                BluetoothDevices.RemoveAt(i);
                stale++;
            }
            Log.Info($"Bluetooth discovery complete ({BluetoothDevices.Count} device(s), "
                + $"{stale} stale dropped).");
            return;
        }
        if (id.Length == 0)
        {
            return;
        }
        var row = FindDevice(id);
        if (change == WindowsRadio.BluetoothChangeKind.Removed)
        {
            // A row mid-operation is never removed: a device dropping out of
            // range must not cancel the pairing the user just started. Nor is a
            // PAIRED one — it is legitimately known whether or not it is in
            // range, and dropping it would take its Paired status and Remove
            // button with it. Same rule the sweep cleanup applies.
            if (row is not null && !row.Busy && !row.Paired)
            {
                BluetoothDevices.Remove(row);
            }
            else if (row is not null && row.Paired)
            {
                // Kept, but no longer here: nothing else clears these, so the
                // row would go on claiming a live connection forever.
                row.Connected = false;
                row.AudioActive = false;
            }
            return;
        }
        _seenThisSweep.Add(id);
        if (row is null)
        {
            row = new BluetoothDeviceEntry(id);
            BluetoothDevices.Add(row);
        }
        row.Name = name;
        row.Paired = paired;
        row.CanPair = canPair;
        row.Connected = connected;
        row.ContainerId = container;
        ApplyAudioState(row);
        BluetoothStateText = DescribeBluetooth(BluetoothPower, BluetoothDevices.Count);
    }

    private static RadioPower ReadPower(WindowsRadio.RadioKind kind)
    {
        try
        {
            return WindowsRadio.GetPower(kind);
        }
        catch (Exception ex)
        {
            Log.Change($"radio-power-{kind}", $"Radio power query failed for kind {kind}: {ex.Message}");
            return RadioPower.Unknown;
        }
    }

    private void Apply(Snapshot snapshot)
    {
        WifiPower = snapshot.WifiPower;
        BluetoothPower = snapshot.BluetoothPower;
        BluetoothConnectedCount = snapshot.BluetoothConnected;
        WifiConnected = snapshot.WifiState == WindowsRadio.WifiConnectionState.Connected;
        // Straight from the interface, so the tile has bars whether or not the
        // panel has ever been opened.
        WifiSignal = snapshot.WifiSignal;
        ConnectedSsid = snapshot.WifiSsid;
        WifiStateText = DescribeWifi(snapshot.WifiPower, snapshot.WifiState);
        BluetoothStateText = DescribeBluetooth(snapshot.BluetoothPower, BluetoothDevices.Count);

        if (snapshot.Failure is { Length: > 0 } failure)
        {
            StatusText = DescribeScanFailure(failure);
            // Re-claimed AFTER the setter cleared it: this message is the one
            // that may be withdrawn when scanning recovers.
            _statusIsScanFailure = true;
        }
        else if (_statusIsScanFailure && snapshot.IncludedNetworks)
        {
            // The scan recovered, so its complaint goes. Tracked rather than
            // blanket-cleared: a connect or pairing result shown since must not
            // be wiped by an unrelated successful snapshot.
            _statusIsScanFailure = false;
            StatusText = "";
        }

        // Only when the snapshot actually carried a network list: reconciling
        // the always-empty closed-panel list would wipe the rows and zero the
        // signal that was just set.
        if (snapshot.IncludedNetworks)
        {
            ReconcileNetworks(snapshot.Networks);
        }

        if (snapshot.AudioContainers is { } audio)
        {
            _audioContainers.Clear();
            foreach (var container in audio)
            {
                // Active kept, not just the id: it is what the Connect button
                // actually toggles, and the row's broader AEP state can say
                // "connected" while the audio endpoints sit unplugged.
                _audioContainers[container.Container] = container.Active;
            }
            foreach (var row in BluetoothDevices)
            {
                ApplyAudioState(row);
            }
        }
    }

    /// <summary>Turns a scan failure into something actionable. The consent gate
    /// is the case worth naming: it is not a permissions problem the user can
    /// solve by elevating, and no amount of retrying will clear it.</summary>
    internal static string DescribeScanFailure(string message) =>
        message.Contains("Win32 5", StringComparison.Ordinal)
            ? "Windows is blocking the Wi-Fi scan until location access is allowed "
              + "(Settings > Privacy & security > Location)."
            : $"Wi-Fi scan failed: {message}";

    /// <summary>The state line for the Wi-Fi tile's flyout.</summary>
    internal static string DescribeWifi(
        RadioPower power,
        WindowsRadio.WifiConnectionState state) => power switch
        {
            RadioPower.Off => "Off",
            RadioPower.Disabled => "Blocked by Windows",
            RadioPower.Absent => "No Wi-Fi adapter",
            RadioPower.Unknown => "State unavailable",
            _ => state switch
            {
                WindowsRadio.WifiConnectionState.Connected => "Connected",
                WindowsRadio.WifiConnectionState.Connecting => "Connecting...",
                WindowsRadio.WifiConnectionState.Disconnected => "Not connected",
                _ => "On",
            },
        };

    /// <summary>The state line for the Bluetooth tile's flyout.</summary>
    internal static string DescribeBluetooth(RadioPower power, int deviceCount) => power switch
    {
        RadioPower.Off => "Off",
        RadioPower.Disabled => "Blocked by Windows",
        RadioPower.Absent => "No Bluetooth adapter",
        RadioPower.Unknown => "State unavailable",
        _ => deviceCount > 0 ? $"On, {deviceCount} device(s)" : "On",
    };

    /// <summary>Merges a fresh network list into the bound collection without
    /// replacing surviving rows — a wholesale rebuild would move focus out from
    /// under the gamepad cursor mid-scan.</summary>
    private void ReconcileNetworks(IReadOnlyList<WindowsRadio.WifiNetwork> fresh)
    {
        var connected = "";
        for (var i = 0; i < fresh.Count; i++)
        {
            var source = fresh[i];
            var row = FindNetwork(source.Ssid);
            if (row is null)
            {
                row = new WifiNetworkEntry(source.Ssid);
                Networks.Insert(Math.Min(i, Networks.Count), row);
            }
            else
            {
                var at = Networks.IndexOf(row);
                if (at != i && i < Networks.Count)
                {
                    Networks.Move(at, i);
                }
            }
            row.Signal = source.Signal;
            row.Security = source.Security;
            row.Saved = source.Saved;
            // Carried through rather than dropped: a network the driver has
            // already rejected must not show an enabled Connect that can only
            // fail.
            row.Connectable = source.Connectable;
            // Reported by the WLAN service, never guessed from list position:
            // the joined network is not always the strongest one visible.
            row.Connected = source.Connected;
            if (row.Connected)
            {
                connected = row.Ssid;
            }
        }
        for (var i = Networks.Count - 1; i >= fresh.Count; i--)
        {
            Networks.RemoveAt(i);
        }
        // Only when the scan positively named a joined network. The interface
        // status read in Apply is authoritative and already correct; no row
        // being marked connected (a hidden network, or a scan refresh
        // mid-flight) is not evidence of a disconnect.
        if (connected.Length > 0)
        {
            ConnectedSsid = connected;
        }
    }

    private WifiNetworkEntry? FindNetwork(string ssid)
    {
        foreach (var entry in Networks)
        {
            if (string.Equals(entry.Ssid, ssid, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    private BluetoothDeviceEntry? FindDevice(string id)
    {
        foreach (var entry in BluetoothDevices)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    // ---- commands ----

    /// <summary>Turns a radio on or off.</summary>
    /// <param name="bluetooth">True for the Bluetooth radio, false for Wi-Fi.</param>
    /// <param name="on">The state to switch to.</param>
    public async Task SetRadioAsync(bool bluetooth, bool on)
    {
        var kind = bluetooth ? WindowsRadio.RadioKind.Bluetooth : WindowsRadio.RadioKind.WiFi;
        var label = bluetooth ? "Bluetooth" : "Wi-Fi";
        // One power change at a time per radio. Two Task.Run delegates can
        // reach Windows in either order, so a quick Off-then-On
        // could settle with the radio OFF while the switch shows on — and the
        // two completions would overwrite each other's status besides.
        var gate = bluetooth ? _bluetoothPowerGate : _wifiPowerGate;
        await gate.WaitAsync();
        try
        {
            var access = await Task.Run(() => WindowsRadio.SetPower(kind, on));
            ApplyRadioResult(label, on, (int)access);
        }
        catch (Exception ex)
        {
            ReportCommandFailure($"turn {label} {(on ? "on" : "off")}", ex);
        }
        finally
        {
            gate.Release();
        }
        QueueRefresh();
    }

    // One gate per radio: a Wi-Fi toggle must not wait behind a Bluetooth one.
    private readonly SemaphoreSlim _wifiPowerGate = new(1, 1);
    private readonly SemaphoreSlim _bluetoothPowerGate = new(1, 1);

    /// <summary>Turns a failed radio command into recoverable feature state.
    ///
    /// Every enumeration path already degrades to "controls stay neutral", but a command's callers are async
    /// void UI handlers: an escaping exception reaches the process-wide
    /// unhandled hook and tears the game-mode session down over a button press.
    /// </summary>
    /// <param name="operation">What the user asked for, phrased for a status line.</param>
    /// <param name="ex">The failure the Windows call raised.</param>
    private void ReportCommandFailure(string operation, Exception ex)
    {
        Log.Warn($"Radio command failed ({operation}): {ex.Message}");
        StatusText = $"Could not {operation}.";
    }

    private void ApplyRadioResult(string label, bool on, int access)
    {
        if (access != 0)
        {
            // Access is refused by a privacy setting, not by anything we can fix.
            if (!_accessLogged)
            {
                _accessLogged = true;
                Log.Warn($"Radio control denied (access code {access}).");
            }
            StatusText = "Windows is not allowing apps to control the radios "
                + "(Settings > Privacy & security > Radios).";
        }
        else
        {
            Log.Info($"Radio set {label}={on}.");
            StatusText = "";
        }
    }

    /// <summary>Joins a network, installing a profile with the password first.</summary>
    /// <param name="ssid">The network to join.</param>
    /// <param name="password">The password, or null for an open or saved network.</param>
    /// <returns>True when the network was actually joined; false leaves a
    /// reason in <see cref="StatusText"/>.</returns>
    public async Task<bool> ConnectAsync(string ssid, string? password)
    {
        // One attempt at a time. The backend waits out the real verdict, so
        // a second Connect would run a concurrent attempt whose scoped watcher
        // sees the same process-wide WLAN events: the two would consume each
        // other's outcomes, report the wrong result, and roll back a profile
        // over a cancellation the user never asked for.
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
        {
            Log.Info($"Wi-Fi connect: {ssid} ignored, an attempt is already running.");
            StatusText = "Still working on the last connection attempt...";
            return false;
        }
        try
        {
            StatusText = $"Connecting to {ssid}...";
            var reason = await Task.Run(() => WindowsRadio.ConnectWifi(ssid, password));

            if (reason == 0)
            {
                Log.Info($"Wi-Fi connect: {ssid} connected.");
                StatusText = "";
                QueueRefresh();
                return true;
            }

            var verdict = WindowsRadio.GetReasonVerdict(reason);
            StatusText = DescribeConnectFailure(verdict, reason, "");
            Log.Warn(
                $"Wi-Fi connect: {ssid} failed (verdict {verdict}, reason {reason}).");
            QueueRefresh();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi connect: {ssid} threw: {ex.Message}");
            StatusText = DescribeConnectFailure(
                WindowsRadio.WifiFailureKind.Unknown, 0, ex.Message);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    /// <summary>Non-zero while a connection attempt is in flight.</summary>
    private int _connecting;

    /// <summary>The message for a failed join.
    ///
    /// Only a rejected key re-prompts for a password. Blaming the user's typing
    /// for an association timeout is worse than saying the network could not be
    /// reached, because they will retype a password that was already correct.</summary>
    internal static string DescribeConnectFailure(
        WindowsRadio.WifiFailureKind verdict,
        uint reasonCode,
        string fallback) => verdict switch
        {
            WindowsRadio.WifiFailureKind.KeyRejected =>
                "That password was not accepted. Check it and try again.",
            WindowsRadio.WifiFailureKind.SecurityMismatch => reasonCode != 0
                ? WindowsRadio.ReasonText(reasonCode)
                : "That password is not valid for this network.",
            WindowsRadio.WifiFailureKind.Unreachable =>
                "Could not reach that network. It may be out of range.",
            _ => reasonCode != 0
                ? WindowsRadio.ReasonText(reasonCode)
                : (fallback.Length > 0 ? fallback : "Could not connect."),
        };

    /// <summary>Leaves the current network.</summary>
    public async Task DisconnectAsync()
    {
        try
        {
            await Task.Run(WindowsRadio.DisconnectWifi);
            Log.Info("Wi-Fi disconnect: requested.");
            StatusText = "";
        }
        catch (Exception ex)
        {
            ReportCommandFailure("disconnect from this network", ex);
        }
        QueueRefresh();
    }

    /// <summary>Deletes a saved network, so it stops joining automatically.</summary>
    /// <param name="ssid">The network to forget.</param>
    public async Task ForgetAsync(string ssid)
    {
        try
        {
            await Task.Run(() => WindowsRadio.ForgetWifi(ssid));
            Log.Info($"Wi-Fi forget: {ssid}.");
            StatusText = "";
        }
        catch (Exception ex)
        {
            ReportCommandFailure($"forget {ssid}", ex);
        }
        QueueRefresh();
    }

    /// <summary>Connects or disconnects a paired Bluetooth audio device — the
    /// soft action, distinct from removing the pairing. Only meaningful for
    /// rows with <see cref="BluetoothDeviceEntry.AudioConnectable"/>: other
    /// device classes reconnect on their own initiative and Windows offers no
    /// general reconnect operation for them.</summary>
    /// <param name="entry">The device to connect or disconnect.</param>
    /// <param name="connect">True to connect, false to disconnect.</param>
    public async Task SetAudioConnectionAsync(BluetoothDeviceEntry entry, bool connect)
    {
        if (entry.ContainerId.Length == 0)
        {
            return;
        }
        entry.Busy = true;
        StatusText = $"{(connect ? "Connecting" : "Disconnecting")} {entry.Name}...";
        var container = entry.ContainerId;
        try
        {
            await Task.Run(() => CoreAudio.SetBluetoothAudioConnection(container, connect));
            Log.Info($"Bluetooth audio {(connect ? "connect" : "disconnect")}: {entry.Name}.");
            // Optimistic on the AUDIO state specifically — that is what this
            // one-shot moved. The next snapshot confirms it from the endpoints.
            entry.AudioActive = connect;
            StatusText = "";
        }
        catch (Exception ex)
        {
            Log.Warn($"Bluetooth audio {(connect ? "connect" : "disconnect")} failed for "
                + $"{entry.Name}: {ex.Message}");
            StatusText = connect
                ? $"Could not connect {entry.Name}. Make sure it is switched on and in range."
                : $"Could not disconnect {entry.Name}.";
        }
        finally
        {
            // Cleared on every path: a row left busy keeps its buttons disabled
            // for as long as the panel stays open.
            entry.Busy = false;
        }
        QueueRefresh();
    }

    /// <summary>Removes a Bluetooth pairing.</summary>
    /// <param name="entry">The device to unpair.</param>
    public async Task UnpairAsync(BluetoothDeviceEntry entry)
    {
        entry.Busy = true;
        var id = entry.Id;
        bool removed;
        try
        {
            removed = await Task.Run(() => WindowsRadio.UnpairBluetooth(id));
        }
        catch (Exception ex)
        {
            ReportCommandFailure($"remove {entry.Name}", ex);
            return;
        }
        finally
        {
            // Cleared on every path: a row left busy keeps its buttons disabled
            // for as long as the panel stays open.
            entry.Busy = false;
        }
        // Reflect the known outcome immediately. The background discovery would
        // confirm it eventually, but it performs a real inquiry and can take
        // half a minute — far too long for a button the user just pressed.
        if (removed)
        {
            entry.Paired = false;
        }
        Log.Info($"Bluetooth unpair: {entry.Name} -> {removed}.");
        StatusText = removed ? "" : $"Could not remove {entry.Name}.";
    }

    private bool _pairingInProgress;
    private BluetoothDeviceEntry? _pairingEntry;
    private uint _pairingToken;

    /// <summary>Starts pairing a device. Questions arrive on
    /// <see cref="PairingRequested"/> and must be answered with
    /// <see cref="RespondToPairing"/>.</summary>
    /// <param name="entry">The device to pair.</param>
    public void BeginPairing(BluetoothDeviceEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pairingInProgress)
        {
            StatusText = "Another pairing is already in progress.";
            return;
        }
        _pairingInProgress = true;
        entry.Busy = true;
        _pairingEntry = entry;
        StatusText = $"Pairing with {entry.Name}...";
        // Discovery keeps running through the whole ceremony ON PURPOSE, the
        // way the Windows applet does it: PairAsync needs the association
        // endpoint pair-ready, which for an advertising device is exactly what
        // the live scan maintains. Stopping the watcher before PairAsync made
        // every attempt fail instantly (device-observed 2026-08-09).
        Log.Info($"Bluetooth pairing: started for {entry.Name}.");

        try
        {
            WindowsRadio.PairBluetooth(entry.Id, OnPairingRequested, OnPairingDone);
        }
        catch (Exception ex)
        {
            FinishPairing();
            ReportCommandFailure($"pair with {entry.Name}", ex);
        }
    }

    /// <summary>Answers a pairing question raised on <see cref="PairingRequested"/>.</summary>
    /// <param name="token">The token from the prompt.</param>
    /// <param name="accept">Whether the user accepted.</param>
    /// <param name="pin">The PIN typed by the user, for the provide-pin ceremony.</param>
    public void RespondToPairing(uint token, bool accept, string? pin)
    {
        Log.Info($"Bluetooth pairing: answering token {token} with "
            + $"{(accept ? "accept" : "decline")}{(pin is { Length: > 0 } ? " and a PIN" : "")}.");
        // A pairing deferral completed on Avalonia's STA thread can wedge the
        // Device Association service; always answer from the MTA thread pool.
        _ = Task.Run(() =>
        {
            try
            {
                WindowsRadio.RespondToPairing(token, accept, pin);
            }
            catch (Exception ex)
            {
                // Unanswered, Windows' deferral sits until it times out and the
                // row stays on "Working..." — so the reason must reach the log
                // rather than an unobserved task.
                Log.Warn($"Bluetooth pairing: reply to token {token} threw: {ex.Message}");
            }
        });
    }

    private void FinishPairing()
    {
        if (_pairingEntry is not null)
        {
            _pairingEntry.Busy = false;
            _pairingEntry = null;
        }
        _pairingToken = 0;
        _pairingInProgress = false;
    }

    private void OnPairingRequested(WindowsRadio.PairingRequest request)
    {
        _pairingToken = request.Token;
        if (_disposed)
        {
            RespondToPairing(request.Token, accept: false, null);
            return;
        }
        Log.Info($"Bluetooth pairing: question received (token {request.Token}, "
            + $"kind {request.Kind}, pin '{request.Pin}') for {request.DeviceName}.");
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                RespondToPairing(request.Token, accept: false, null);
                return;
            }
            var handled = PairingRequested is not null;
            Log.Info($"Bluetooth pairing: prompting the user (token {request.Token}, "
                + $"handler attached: {handled}).");
            PairingRequested?.Invoke(new PairingPrompt(
                request.Token, request.Kind, request.Pin, request.DeviceName));
            if (!handled)
            {
                Log.Warn($"Bluetooth pairing: no UI attached, declining token {request.Token}.");
                RespondToPairing(request.Token, accept: false, null);
            }
        });
    }

    private void OnPairingDone(WindowsRadio.PairingResult? result, Exception? failure)
    {
        if (_disposed)
        {
            return;
        }
        var outcome = result?.Outcome;
        var text = failure?.Message
            ?? (result is { } completed ? $"Windows status {completed.RawStatus}" : string.Empty);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            var entry = _pairingEntry;
            var name = entry?.Name ?? "device";
            // Same reasoning as unpair: apply the outcome we already know rather
            // than leaving the row stale until the next inquiry finishes.
            var paired = outcome is WindowsRadio.PairingOutcome.Paired
                or WindowsRadio.PairingOutcome.AlreadyPaired;
            if (entry is not null && paired)
            {
                entry.Paired = true;
            }
            FinishPairing();
            var summary = DescribePairOutcome(outcome, name, text);
            // The raw status rides along: the grouped outcome deliberately
            // lumps rare statuses, and remote diagnosis needs the exact one.
            Log.Info($"Bluetooth pairing: finished for {name} (outcome {outcome}"
                + $"{(text.Length > 0 ? $", {text}" : "")}). {summary}");
            StatusText = paired ? "" : summary;
            PairingFinished?.Invoke(summary);
        });
    }

    /// <summary>Shows a non-transient panel decision that did not reach Windows.</summary>
    /// <param name="message">Short actionable text for the panel status line.</param>
    internal void ReportStatus(string message) => StatusText = message;

    /// <summary>The message for a finished pairing attempt.</summary>
    /// <param name="outcome">How it ended, or <see langword="null"/> when the attempt threw
    /// before Windows produced a result.</param>
    /// <param name="device">The device's display name.</param>
    /// <param name="message">The exception message or raw Windows status, used only when there
    /// is nothing better to say.</param>
    /// <returns>A line to show the user.</returns>
    internal static string DescribePairOutcome(
        WindowsRadio.PairingOutcome? outcome,
        string device,
        string message) => outcome switch
        {
            WindowsRadio.PairingOutcome.Paired => $"{device} is paired.",
            WindowsRadio.PairingOutcome.AlreadyPaired => $"{device} was already paired.",
            WindowsRadio.PairingOutcome.Cancelled => $"Pairing with {device} was cancelled.",
            WindowsRadio.PairingOutcome.Failed =>
                $"Could not pair with {device}. Make sure it is in pairing mode.",
            // The broker runs unelevated and may be unable to inspect an
            // elevated caller; that is a different problem from a sulky device.
            WindowsRadio.PairingOutcome.AccessDenied => $"Windows denied pairing with {device}.",
            // A hung earlier ceremony inside the Device Association service —
            // it survives WSGM, so only the radio (or a reboot) can clear it.
            WindowsRadio.PairingOutcome.AlreadyInProgress =>
                $"Windows is still busy with an earlier pairing attempt for {device}. "
                + "Turn Bluetooth off and on, then try again.",
            null => message.Length > 0 ? message : $"Pairing with {device} failed.",
            _ => $"Pairing with {device} did not complete.",
        };

    private void Set(ref string field, string value, string name)
    {
        if (field != value)
        {
            field = value;
            Raise(name);
        }
    }

    private void Set(ref bool field, bool value, string name)
    {
        if (field != value)
        {
            field = value;
            Raise(name);
        }
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
