using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One active Core Audio endpoint shown by the taskbar audio panel.</summary>
public sealed class AudioEndpointEntry : INotifyPropertyChanged
{
    /// <summary>Raised when the endpoint's presentation changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a visible endpoint row.</summary>
    /// <param name="id">The opaque Windows endpoint identifier.</param>
    /// <param name="name">The friendly device name.</param>
    internal AudioEndpointEntry(string id, string name)
    {
        Id = id;
        _name = name;
    }

    /// <summary>Gets the opaque Windows endpoint identifier.</summary>
    internal string Id { get; }

    private string _name;

    /// <summary>Gets the friendly device name.</summary>
    public string Name
    {
        get => _name;
        internal set
        {
            if (_name != value)
            {
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }
    }
}

/// <summary>Live master-volume and default audio-device state for the game-mode
/// taskbar. Potentially slow Core Audio enumeration runs away from the Avalonia
/// UI thread.</summary>
public sealed class AudioManager : INotifyPropertyChanged, IDisposable
{
    /// <summary>Raised after a bindable audio property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Revision bookkeeping for one data flow's default-endpoint writes:
    /// rapid selections each take a revision, only the newest may publish UI
    /// state, and the flow counts as pending until that newest revision
    /// completes. Pure, so the latest-wins rule is testable without Core Audio;
    /// the writes themselves are additionally serialized by a per-flow gate.</summary>
    internal struct EndpointSelectionTracker
    {
        private int _requested;
        private int _completed;

        /// <summary>Claims the next revision for a new selection.</summary>
        internal int Begin() => Interlocked.Increment(ref _requested);

        /// <summary>Whether this revision is still the newest selection.</summary>
        internal bool IsCurrent(int revision) => revision == Volatile.Read(ref _requested);

        /// <summary>Whether a selection is still in flight — the refresh path must
        /// not overwrite the user's choice with a stale default meanwhile.</summary>
        internal bool Pending =>
            Volatile.Read(ref _requested) != Volatile.Read(ref _completed);

        /// <summary>Records this revision's write as finished. A stale revision is
        /// ignored: the newer selection it lost to is still pending.</summary>
        internal void Complete(int revision)
        {
            if (IsCurrent(revision))
            {
                Volatile.Write(ref _completed, revision);
            }
        }
    }

    /// <summary>Gets the active playback endpoints.</summary>
    public ObservableCollection<AudioEndpointEntry> OutputEndpoints { get; } = [];

    /// <summary>Gets the active recording endpoints.</summary>
    public ObservableCollection<AudioEndpointEntry> InputEndpoints { get; } = [];

    private double _volumePercent;

    /// <summary>Gets or sets the default output's master volume, from 0 to 100.
    /// Setting it also queues the shared audible preview.</summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var normalized = NormalizeVolume(value);
            if (Math.Abs(_volumePercent - normalized) < 0.01)
            {
                return;
            }
            _volumePercent = normalized;
            Interlocked.Increment(ref _volumeRevision);
            if (normalized > 0)
            {
                Muted = false;
            }
            Raise(nameof(VolumePercent));
            Raise(nameof(VolumeText));
            QueueVolumeWrite(normalized);
        }
    }

    /// <summary>Gets the current master volume as display text.</summary>
    public string VolumeText => $"{(int)_volumePercent}%";

    private double? _inputVolumePercent;

    /// <summary>Gets or sets the default input's master volume, from 0 to 100.</summary>
    /// <remarks>Null means Windows currently has no readable default capture endpoint.</remarks>
    public double? InputVolumePercent
    {
        get => _inputVolumePercent;
        set
        {
            if (value is not { } requested)
            {
                return;
            }

            int normalized = NormalizeVolume(requested);
            if (_inputVolumePercent is { } current && Math.Abs(current - normalized) < 0.01)
            {
                return;
            }

            _inputVolumePercent = normalized;
            Interlocked.Increment(ref _inputVolumeRevision);
            if (normalized > 0)
            {
                InputMuted = false;
            }
            Raise(nameof(InputVolumePercent));
            QueueInputVolumeWrite(normalized);
        }
    }

    private bool _inputMuted;

    /// <summary>Gets whether the default input endpoint is muted.</summary>
    public bool InputMuted
    {
        get => _inputMuted;
        private set
        {
            if (_inputMuted != value)
            {
                _inputMuted = value;
                Raise(nameof(InputMuted));
            }
        }
    }

    private bool _muted;

    /// <summary>Gets whether the default output endpoint is muted.</summary>
    public bool Muted
    {
        get => _muted;
        private set
        {
            if (_muted != value)
            {
                _muted = value;
                Raise(nameof(Muted));
                Raise(nameof(VolumeText));
            }
        }
    }

    private AudioEndpointEntry? _selectedOutput;

    /// <summary>Gets or sets the default audio output.</summary>
    public AudioEndpointEntry? SelectedOutput
    {
        get => _selectedOutput;
        set => SelectEndpoint(value, output: true);
    }

    private AudioEndpointEntry? _selectedInput;

    /// <summary>Gets or sets the default audio input.</summary>
    public AudioEndpointEntry? SelectedInput
    {
        get => _selectedInput;
        set => SelectEndpoint(value, output: false);
    }

    private string _errorText = "";

    /// <summary>Gets a non-fatal audio error to show in the panel.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (_errorText != value)
            {
                _errorText = value;
                Raise(nameof(ErrorText));
                Raise(nameof(HasError));
            }
        }
    }

    /// <summary>Gets whether <see cref="ErrorText"/> should be visible.</summary>
    public bool HasError => ErrorText.Length > 0;

    private DispatcherTimer? _timer;
    private int _ticks;
    private int _refreshing;
    private int _volumeRevision;
    private int _inputVolumeRevision;
    private bool _disposed;
    private bool _stickyError;
    private string _endpointSummary = "";
    private readonly object _volumeGate = new();
    private readonly object _inputVolumeGate = new();
    private readonly SemaphoreSlim _outputSelectionGate = new(1, 1);
    private readonly SemaphoreSlim _inputSelectionGate = new(1, 1);
    private int? _pendingVolume;
    private bool _volumeWorkerRunning;
    private int? _pendingInputVolume;
    private bool _inputVolumeWorkerRunning;
    private EndpointSelectionTracker _outputSelection;
    private EndpointSelectionTracker _inputSelection;
    private bool _hasOutputSnapshot;

    /// <summary>Performs an immediate refresh and starts live audio updates.
    /// UI-thread callers only. Idempotent.</summary>
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }
        VolumeFeedback.Initialize();
        QueueRefresh(includeEndpoints: true);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Requests a fresh volume and device enumeration.</summary>
    public void Refresh()
    {
        _stickyError = false;
        QueueRefresh(includeEndpoints: true);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        QueueRefresh(includeEndpoints: _ticks % 5 == 0);
    }

    private void QueueRefresh(bool includeEndpoints)
    {
        if (_disposed || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        var volumeRevision = Volatile.Read(ref _volumeRevision);
        var inputVolumeRevision = Volatile.Read(ref _inputVolumeRevision);
        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = ReadSnapshot(includeEndpoints, volumeRevision, inputVolumeRevision);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed)
                    {
                        Apply(snapshot);
                    }
                });
            }
            catch (Exception ex)
            {
                PostFailure($"Audio refresh failed: {ex.Message}", sticky: true);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private sealed record Snapshot(
        int VolumeResult,
        int Volume,
        bool Muted,
        int VolumeRevision,
        int InputVolumeResult,
        int InputVolume,
        bool InputMuted,
        int InputVolumeRevision,
        bool IncludedEndpoints,
        int OutputResult,
        IReadOnlyList<CoreAudio.AudioEndpoint> Outputs,
        int InputResult,
        IReadOnlyList<CoreAudio.AudioEndpoint> Inputs);

    private static Snapshot ReadSnapshot(
        bool includeEndpoints,
        int volumeRevision,
        int inputVolumeRevision)
    {
        var volumeResult = CoreAudio.GetVolume(out var volume, out var muted);
        var inputVolumeResult = CoreAudio.GetVolume(
            CoreAudio.AudioDirection.Capture,
            out var inputVolume,
            out var inputMuted);
        if (!includeEndpoints)
        {
            return new Snapshot(
                volumeResult,
                volume,
                muted != 0,
                volumeRevision,
                inputVolumeResult,
                inputVolume,
                inputMuted != 0,
                inputVolumeRevision,
                false,
                0,
                [],
                0,
                []);
        }

        var outputResult = CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Render, out var outputs);
        var inputResult = CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Capture, out var inputs);
        return new Snapshot(
            volumeResult,
            volume,
            muted != 0,
            volumeRevision,
            inputVolumeResult,
            inputVolume,
            inputMuted != 0,
            inputVolumeRevision,
            true,
            outputResult,
            outputs,
            inputResult,
            inputs);
    }

    private void Apply(Snapshot snapshot)
    {
        if (snapshot.VolumeResult >= 0)
        {
            if (snapshot.VolumeRevision == Volatile.Read(ref _volumeRevision))
            {
                ApplyVolume(snapshot.Volume, snapshot.Muted);
                if (!_stickyError)
                {
                    ErrorText = "";
                }
            }
        }
        else
        {
            SetFailure("read volume", snapshot.VolumeResult);
        }

        if (snapshot.InputVolumeRevision == Volatile.Read(ref _inputVolumeRevision))
        {
            if (snapshot.InputVolumeResult >= 0)
            {
                ApplyInputVolume(snapshot.InputVolume, snapshot.InputMuted);
                Log.Change(
                    "audio.capture.volume",
                    $"Default microphone volume is {snapshot.InputVolume}% "
                        + $"(muted={snapshot.InputMuted}).");
            }
            else
            {
                ClearInputVolume();
                Log.Change(
                    "audio.capture.volume",
                    $"Default microphone volume is unavailable "
                        + $"(HRESULT 0x{snapshot.InputVolumeResult:X8}).");
            }
        }

        if (!snapshot.IncludedEndpoints)
        {
            return;
        }
        if (snapshot.OutputResult >= 0)
        {
            Reconcile(OutputEndpoints, snapshot.Outputs);
            if (!_outputSelection.Pending)
            {
                var previousOutputId = _selectedOutput?.Id;
                var defaultOutput = FindDefault(OutputEndpoints, snapshot.Outputs);
                SetSelected(output: true, defaultOutput);
                if (_hasOutputSnapshot
                    && !string.Equals(previousOutputId, defaultOutput?.Id, StringComparison.Ordinal))
                {
                    Log.Info("Default audio output changed outside WSGM; reopening the volume feedback stream.");
                    VolumeFeedback.Reinitialize();
                }
                _hasOutputSnapshot = true;
            }
        }
        else
        {
            SetFailure("list audio outputs", snapshot.OutputResult);
        }
        if (snapshot.InputResult >= 0)
        {
            Reconcile(InputEndpoints, snapshot.Inputs);
            if (!_inputSelection.Pending)
            {
                SetSelected(output: false, FindDefault(InputEndpoints, snapshot.Inputs));
            }
        }
        else
        {
            SetFailure("list audio inputs", snapshot.InputResult);
        }
        if (snapshot.OutputResult >= 0 && snapshot.InputResult >= 0)
        {
            var summary = $"Audio endpoints: {OutputEndpoints.Count} output(s), "
                + $"default='{SelectedOutput?.Name ?? "none"}'; {InputEndpoints.Count} input(s), "
                + $"default='{SelectedInput?.Name ?? "none"}'; volume={(int)VolumePercent}%, muted={Muted}; "
                + $"microphone={(InputVolumePercent is { } inputVolume
                    ? inputVolume.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
                    : "unavailable")}, "
                + $"muted={InputMuted}.";
            if (_endpointSummary != summary)
            {
                _endpointSummary = summary;
                Log.Info(summary);
            }
        }
    }

    private void ApplyVolume(int percentage, bool muted)
    {
        var normalized = NormalizeVolume(percentage);
        if (Math.Abs(_volumePercent - normalized) >= 0.01)
        {
            _volumePercent = normalized;
            Raise(nameof(VolumePercent));
            Raise(nameof(VolumeText));
        }
        Muted = muted;
    }

    private void ApplyInputVolume(int percentage, bool muted)
    {
        int normalized = NormalizeVolume(percentage);
        if (_inputVolumePercent is not { } current || Math.Abs(current - normalized) >= 0.01)
        {
            _inputVolumePercent = normalized;
            Raise(nameof(InputVolumePercent));
        }
        InputMuted = muted;
    }

    private void ClearInputVolume()
    {
        if (_inputVolumePercent is null)
        {
            return;
        }

        _inputVolumePercent = null;
        _inputMuted = false;
        Raise(nameof(InputVolumePercent));
        Raise(nameof(InputMuted));
    }

    private static AudioEndpointEntry? FindDefault(
        ObservableCollection<AudioEndpointEntry> entries,
        IReadOnlyList<CoreAudio.AudioEndpoint> snapshot)
    {
        string? defaultId = null;
        foreach (var endpoint in snapshot)
        {
            if (endpoint.IsDefault)
            {
                defaultId = endpoint.Id;
                break;
            }
        }
        if (defaultId is null)
        {
            return null;
        }
        foreach (var entry in entries)
        {
            if (entry.Id == defaultId)
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>Reconciles endpoint rows in place so a periodic refresh does not
    /// destroy an open combo box or its focused item.</summary>
    internal static void Reconcile(
        ObservableCollection<AudioEndpointEntry> entries,
        IReadOnlyList<CoreAudio.AudioEndpoint> fresh)
    {
        var remaining = new Dictionary<string, CoreAudio.AudioEndpoint>(StringComparer.Ordinal);
        foreach (var endpoint in fresh)
        {
            remaining.TryAdd(endpoint.Id, endpoint);
        }
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (remaining.Remove(entry.Id, out var endpoint))
            {
                entry.Name = endpoint.Name;
            }
            else
            {
                entries.RemoveAt(index);
            }
        }
        foreach (var endpoint in fresh)
        {
            if (remaining.Remove(endpoint.Id))
            {
                entries.Add(new AudioEndpointEntry(endpoint.Id, endpoint.Name));
            }
        }
    }

    private void SelectEndpoint(AudioEndpointEntry? value, bool output)
    {
        var current = output ? _selectedOutput : _selectedInput;
        if (value is null || ReferenceEquals(current, value) || current?.Id == value.Id)
        {
            return;
        }
        SetSelected(output, value);
        var kind = output ? "output" : "input";
        var revision = Tracker(output).Begin();
        Log.Info($"Audio {kind} selected: '{value.Name}'.");
        _ = Task.Run(() => ApplyEndpointSelection(value.Id, output, kind, revision));
    }

    /// <summary>Serializes default-device writes for one data flow. A stale
    /// queued request is skipped, and an already-running stale request cannot
    /// publish UI state after the user's newer choice.</summary>
    private void ApplyEndpointSelection(string endpointId, bool output, string kind, int revision)
    {
        var gate = output ? _outputSelectionGate : _inputSelectionGate;
        gate.Wait();
        try
        {
            if (_disposed || !Tracker(output).IsCurrent(revision))
            {
                return;
            }
            try
            {
                var result = CoreAudio.SetDefaultEndpoint(endpointId);
                if (result >= 0 && output)
                {
                    VolumeFeedback.Reinitialize();
                }
                Tracker(output).Complete(revision);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || !Tracker(output).IsCurrent(revision))
                    {
                        return;
                    }
                    if (result < 0)
                    {
                        PostFailure(
                            $"Could not select audio {kind} (HRESULT 0x{result:X8}).",
                            sticky: true);
                    }
                    else
                    {
                        _stickyError = false;
                        ErrorText = "";
                    }
                    QueueRefresh(includeEndpoints: true);
                });
            }
            catch (Exception ex)
            {
                if (Tracker(output).IsCurrent(revision))
                {
                    Tracker(output).Complete(revision);
                    PostFailure($"Audio {kind} selection failed: {ex.Message}", sticky: true);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private ref EndpointSelectionTracker Tracker(bool output)
        => ref (output ? ref _outputSelection : ref _inputSelection);

    private void SetSelected(bool output, AudioEndpointEntry? value)
    {
        if (output)
        {
            if (!ReferenceEquals(_selectedOutput, value))
            {
                _selectedOutput = value;
                Raise(nameof(SelectedOutput));
            }
        }
        else if (!ReferenceEquals(_selectedInput, value))
        {
            _selectedInput = value;
            Raise(nameof(SelectedInput));
        }
    }

    private void QueueVolumeWrite(int percentage)
    {
        lock (_volumeGate)
        {
            _pendingVolume = percentage;
            if (_volumeWorkerRunning)
            {
                return;
            }
            _volumeWorkerRunning = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int requested;
                    lock (_volumeGate)
                    {
                        if (_pendingVolume is not int pending || _disposed)
                        {
                            _volumeWorkerRunning = false;
                            return;
                        }
                        requested = pending;
                        _pendingVolume = null;
                    }

                    try
                    {
                        var result = CoreAudio.SetVolume(requested, out var muted);
                        if (result >= 0)
                        {
                            Log.Info($"Taskbar volume set to {requested}% (muted={muted != 0}).");
                            VolumeFeedback.Play();
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (!_disposed)
                                {
                                    ApplyVolume(requested, muted != 0);
                                    _stickyError = false;
                                    ErrorText = "";
                                }
                            });
                        }
                        else
                        {
                            PostFailure($"Set volume failed (HRESULT 0x{result:X8}).", sticky: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Report and carry on: unwinding the loop would strand
                        // _volumeWorkerRunning at true and silently drop every
                        // later slider move.
                        PostFailure($"Volume write failed: {ex.Message}", sticky: true);
                    }
                }
            }
            catch (Exception ex)
            {
                // The flag is the only thing that lets a later write start a
                // worker at all, so it must be cleared on the abnormal exit too.
                lock (_volumeGate)
                {
                    _volumeWorkerRunning = false;
                }
                Log.Warn($"Volume write worker stopped: {ex.Message}");
            }
        });
    }

    private void QueueInputVolumeWrite(int percentage)
    {
        lock (_inputVolumeGate)
        {
            _pendingInputVolume = percentage;
            if (_inputVolumeWorkerRunning)
            {
                return;
            }
            _inputVolumeWorkerRunning = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int requested;
                    lock (_inputVolumeGate)
                    {
                        if (_pendingInputVolume is not int pending || _disposed)
                        {
                            _inputVolumeWorkerRunning = false;
                            return;
                        }
                        requested = pending;
                        _pendingInputVolume = null;
                    }

                    try
                    {
                        int result = CoreAudio.SetVolume(
                            CoreAudio.AudioDirection.Capture,
                            requested,
                            out int muted);
                        if (result >= 0)
                        {
                            Log.Info(
                                $"Microphone volume set to {requested}% (muted={muted != 0}).");
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (!_disposed)
                                {
                                    ApplyInputVolume(requested, muted != 0);
                                    _stickyError = false;
                                    ErrorText = "";
                                }
                            });
                        }
                        else
                        {
                            PostFailure(
                                $"Set microphone volume failed (HRESULT 0x{result:X8}).",
                                sticky: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        PostFailure($"Microphone volume write failed: {ex.Message}", sticky: true);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_inputVolumeGate)
                {
                    _inputVolumeWorkerRunning = false;
                }
                Log.Warn($"Microphone volume write worker stopped: {ex.Message}");
            }
        });
    }

    /// <summary>Adopts a volume state another WSGM writer has ALREADY applied to
    /// Core Audio — the hardware volume buttons — so the taskbar slider does not
    /// lag one poll behind the OSD. Presentation only: nothing is written back,
    /// and the revision bump keeps an older in-flight snapshot from undoing the
    /// adopted value before the next poll confirms it.</summary>
    /// <param name="percent">The volume the writer landed on, 0-100.</param>
    /// <param name="muted">The mute state the writer landed on.</param>
    internal void NoteExternalVolume(int percent, bool muted)
    {
        Interlocked.Increment(ref _volumeRevision);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                ApplyVolume(percent, muted);
            }
        });
    }

    /// <summary>Rounds and bounds a slider value for Core Audio.</summary>
    /// <param name="value">The raw slider or endpoint value.</param>
    /// <returns>An integer from 0 through 100.</returns>
    internal static int NormalizeVolume(double value)
        => double.IsFinite(value) ? Math.Clamp((int)Math.Round(value), 0, 100) : 0;

    private void SetFailure(string operation, int result)
        => PostFailure($"Could not {operation} (HRESULT 0x{result:X8}).");

    private void PostFailure(string message, bool sticky = false)
    {
        Log.Warn(message);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                _stickyError |= sticky;
                ErrorText = message;
            }
        });
    }

    private void Raise(string property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>Stops refreshes and prevents pending native work from publishing
    /// into a closed taskbar.</summary>
    public void Dispose()
    {
        _disposed = true;
        // Invalidate every in-flight selection so its completion cannot publish.
        _outputSelection.Begin();
        _inputSelection.Begin();
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        lock (_volumeGate)
        {
            _pendingVolume = null;
        }
        lock (_inputVolumeGate)
        {
            _pendingInputVolume = null;
        }
    }
}
