using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Live system status for the game-mode taskbar's right zone: clock, date
/// and battery level (GetSystemPowerStatus). Refreshes on a 1 s UI-thread timer
/// while started; the taskbar binds its status cluster to this object.
///
/// Radio and audio state are not read here. They live on <see cref="Radios"/>
/// and <see cref="Audio"/>, which this object owns and starts; the same manager
/// instances back the taskbar tiles and their panels, so each pair stays in sync.</summary>
public sealed class SystemStatus : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// Creates a status cluster, optionally over an audio manager owned by someone else.
    /// </summary>
    /// <param name="audio">
    /// A session-scoped audio manager to share, or null to create and own one.
    /// </param>
    /// <param name="radios">
    /// A session-scoped radio manager to share, or null to create and own one.
    /// </param>
    /// <remarks>
    /// The taskbar comes and goes while a session lasts, so anything that must answer for the whole
    /// session — Steam's audio namespace, in particular — cannot depend on a manager this object
    /// disposes when the taskbar closes. Sharing one instance rather than creating a second is the
    /// point: two managers would enumerate endpoints twice and could disagree about which device is
    /// default.
    /// </remarks>
    public SystemStatus(AudioManager? audio = null, RadioManager? radios = null)
    {
        _ownsAudio = audio is null;
        Audio = audio ?? new AudioManager();
        _ownsRadios = radios is null;
        Radios = radios ?? new RadioManager();
    }

    /// <summary>Raised after a status property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly bool _ownsAudio;
    private readonly bool _ownsRadios;
    private DispatcherTimer? _timer;
    private bool _disposed;

    private string _clockText = "";
    /// <summary>Gets the current time of day, e.g. "21:37".</summary>
    public string ClockText
    {
        get => _clockText;
        private set => Set(ref _clockText, value, nameof(ClockText));
    }

    private string _dateText = "";
    /// <summary>Gets the current date, e.g. "Fri 08 Aug" (localized day/month names).</summary>
    public string DateText
    {
        get => _dateText;
        private set => Set(ref _dateText, value, nameof(DateText));
    }

    private bool _hasBattery;
    /// <summary>Gets whether a system battery with a known charge level exists; the
    /// taskbar hides the battery indicator entirely when false (desktop PCs, or a
    /// driver reporting the 255 unknown markers).</summary>
    public bool HasBattery
    {
        get => _hasBattery;
        private set => Set(ref _hasBattery, value, nameof(HasBattery));
    }

    private int _batteryPercent;
    /// <summary>Gets the battery charge in percent (0–100; 0 while <see cref="HasBattery"/> is false).</summary>
    public int BatteryPercent
    {
        get => _batteryPercent;
        private set
        {
            if (_batteryPercent != value)
            {
                _batteryPercent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryPercent)));
            }
        }
    }

    private string _batteryText = "";
    /// <summary>Gets the battery charge as display text, e.g. "87%" (empty without a battery).</summary>
    public string BatteryText
    {
        get => _batteryText;
        private set => Set(ref _batteryText, value, nameof(BatteryText));
    }

    /// <summary>Gets the Wi-Fi and Bluetooth manager backing the taskbar's radio
    /// tiles and the radio panel. Disposed with this object only when this object
    /// created it — a manager supplied by the session outlives every taskbar.</summary>
    public RadioManager Radios { get; }

    /// <summary>Gets the master-volume and endpoint manager backing the taskbar's
    /// audio tile and audio panel. Disposed with this object only when this object
    /// created it — a manager supplied by the session outlives every taskbar.</summary>
    public AudioManager Audio { get; }

    /// <summary>Gets the removable-storage manager backing the taskbar's eject
    /// tile and the Safe Eject panel. Owned and disposed with this object.</summary>
    public RemovableDriveManager Drives { get; } = new();

    /// <summary>Performs an immediate refresh and starts the 1 s update timer.
    /// UI-thread callers only (the timer is a DispatcherTimer). Idempotent.
    /// Refused (and logged) after <see cref="Dispose"/> — the owned managers are
    /// gone by then and a restarted timer would tick a dead status cluster.</summary>
    public void Start()
    {
        if (_disposed)
        {
            Log.Warn("System status Start() ignored: the instance was already disposed.");
            return;
        }
        if (_timer is not null)
        {
            return;
        }
        Refresh();
        Radios.Start();
        Audio.Start();
        Drives.Start();
        Log.Info($"System status started (battery: {(HasBattery ? BatteryText : "none")}).");
        // Parameterless ctor + explicit Start: the 3-arg ctor auto-starts.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Ends this object's life: stops the update timer AND disposes the
    /// owned radio, audio and removable-drive managers, which cannot be recreated —
    /// create a fresh <see cref="SystemStatus"/> instead of restarting this one.
    /// Idempotent; bound values keep their last state.</summary>
    public void Dispose()
    {
        _disposed = true;

        // Only when this object created them. Disposing a session-scoped manager here would take
        // audio or the radios away from everything else holding them the moment the taskbar closes.
        if (_ownsRadios)
        {
            Radios.Dispose();
        }

        if (_ownsAudio)
        {
            Audio.Dispose();
        }

        Drives.Dispose();
        if (_timer is null)
        {
            return;
        }
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var now = DateTime.Now;
        ClockText = FormatClock(now);
        DateText = FormatDate(now, CultureInfo.CurrentCulture);

        var ok = NativeMethods.GetSystemPowerStatus(out var power);
        var (hasBattery, percent, text) = InterpretBattery(ok, power.BatteryFlag, power.BatteryLifePercent);
        HasBattery = hasBattery;
        BatteryPercent = percent;
        BatteryText = text;
    }

    /// <summary>Formats the taskbar clock ("21:37"). 24-hour, culture-independent.</summary>
    internal static string FormatClock(DateTime now)
        => now.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Formats the taskbar date ("Fri 08 Aug") with the culture's day/month names.</summary>
    internal static string FormatDate(DateTime now, CultureInfo culture)
        => now.ToString("ddd dd MMM", culture);

    /// <summary>Maps a GetSystemPowerStatus result to the indicator state: hidden
    /// (no battery / unknown markers) or a percent with display text.</summary>
    internal static (bool HasBattery, int Percent, string Text) InterpretBattery(
        bool callSucceeded, byte batteryFlag, byte lifePercent)
    {
        // The 0x80 mask covers both unknown markers (128 = no system battery,
        // 255 = unknown flag); 255 percent = unknown level.
        if (!callSucceeded || (batteryFlag & 0x80) != 0 || lifePercent > 100)
        {
            return (false, 0, "");
        }
        return (true, lifePercent, lifePercent + "%");
    }

    private void Set(ref string field, string value, string name)
    {
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void Set(ref bool field, bool value, string name)
    {
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
