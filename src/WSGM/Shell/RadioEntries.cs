using System.ComponentModel;
using WifiSecurity = WindowsDeviceControl.WindowsRadio.WifiSecurity;

namespace WSGM.Shell;

/// <summary>One row in the Wi-Fi list. A row instance survives refreshes so the
/// gamepad cursor keeps its place; only its values are updated.</summary>
public sealed class WifiNetworkEntry : INotifyPropertyChanged
{
    /// <summary>Raised after a displayed value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a row for a network.</summary>
    /// <param name="ssid">The network name, which also identifies the row.</param>
    public WifiNetworkEntry(string ssid) => Ssid = ssid;

    /// <summary>Gets the network name. Immutable: it is the row's identity.</summary>
    public string Ssid { get; }

    private int _signal;
    /// <summary>Gets the signal quality, 0-100.</summary>
    public int Signal
    {
        get => _signal;
        internal set => Set(ref _signal, value, nameof(Signal));
    }

    private WifiSecurity _security;
    /// <summary>Gets how the network is protected.</summary>
    public WifiSecurity Security
    {
        get => _security;
        internal set
        {
            if (_security != value)
            {
                _security = value;
                Raise(nameof(Security));
                Raise(nameof(NeedsPassword));
                Raise(nameof(Secured));
                Raise(nameof(StatusLine));
                Raise(nameof(ActionEnabled));
            }
        }
    }

    private bool _saved;
    /// <summary>Gets whether a saved profile exists, so joining needs no password.</summary>
    public bool Saved
    {
        get => _saved;
        internal set
        {
            if (_saved != value)
            {
                _saved = value;
                Raise(nameof(Saved));
                Raise(nameof(NeedsPassword));
                Raise(nameof(StatusLine));
            }
        }
    }

    private bool _connectable = true;
    /// <summary>Gets whether the driver believes this network can be joined at
    /// all. False leaves the row visible but its action disabled: offering a
    /// Connect the driver has already rejected produces a doomed attempt with
    /// nothing to explain it.</summary>
    public bool Connectable
    {
        get => _connectable;
        internal set
        {
            if (_connectable != value)
            {
                _connectable = value;
                Raise(nameof(Connectable));
                Raise(nameof(ActionEnabled));
                Raise(nameof(StatusLine));
            }
        }
    }

    /// <summary>Gets whether the row's action button may be pressed. A joined
    /// network can always be disconnected, whatever the scan says about
    /// joining it again — but an enterprise network is never joinable here (it
    /// needs an EAP flow this panel does not offer), and an enabled button that
    /// silently does nothing is worse than a disabled one. WEP
    /// (<see cref="WifiSecurity.Unsupported"/>) is listed but not offered: its
    /// open-system authentication otherwise looks exactly like an unsecured
    /// network, so it would skip the password prompt and then fail.</summary>
    public bool ActionEnabled => Connected
        || (Connectable
            && Security != WifiSecurity.Enterprise
            && Security != WifiSecurity.Unsupported);

    private bool _connected;
    /// <summary>Gets whether this is the network currently joined.</summary>
    public bool Connected
    {
        get => _connected;
        internal set
        {
            if (_connected != value)
            {
                _connected = value;
                Raise(nameof(Connected));
                Raise(nameof(IconState));
                Raise(nameof(StatusLine));
                Raise(nameof(ActionText));
                Raise(nameof(ActionEnabled));
            }
        }
    }

    /// <summary>Gets whether joining this network needs a password prompt: it is
    /// secured, and no saved profile already carries the key. Enhanced Open is
    /// encrypted but keyless, so it never prompts.</summary>
    public bool NeedsPassword => Security == WifiSecurity.PersonalPsk && !Saved;

    /// <summary>Gets whether the network is protected at all.</summary>
    public bool Secured => Security != WifiSecurity.Open;

    private bool _expanded;
    /// <summary>Gets whether this row is showing its actions. Selecting a row
    /// reveals what can be done with it rather than acting immediately — a tap
    /// must never disconnect the network the user is using.</summary>
    public bool Expanded
    {
        get => _expanded;
        internal set => Set(ref _expanded, value, nameof(Expanded));
    }

    /// <summary>Gets the icon state: off is never used here (a listed network
    /// implies a live radio), so this is connected or merely visible.</summary>
    public Controls.RadioIconState IconState => Connected
        ? Controls.RadioIconState.Connected
        : Controls.RadioIconState.Disconnected;

    /// <summary>Gets the second line under the name.</summary>
    public string StatusLine => Connected
        ? "Connected"
        : !Connectable
        ? "Not available right now"
        : Security switch
        {
            WifiSecurity.Enterprise => "Enterprise network (not supported here)",
            WifiSecurity.Unsupported => "WEP network (not supported here)",
            WifiSecurity.Open => Saved ? "Open, saved" : "Open",
            WifiSecurity.EnhancedOpen => Saved ? "Open (encrypted), saved" : "Open (encrypted)",
            _ => Saved ? "Saved" : "Secured",
        };

    /// <summary>Gets the label for this row's action button.</summary>
    public string ActionText => Connected ? "Disconnect" : "Connect";

    private void Set(ref int field, int value, string name)
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

/// <summary>One row in the Bluetooth list. Same in-place refresh discipline as
/// <see cref="WifiNetworkEntry"/>.</summary>
public sealed class BluetoothDeviceEntry : INotifyPropertyChanged
{
    /// <summary>Raised after a displayed value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a row for a device.</summary>
    /// <param name="id">The WinRT device id, which identifies the row.</param>
    public BluetoothDeviceEntry(string id) => Id = id;

    /// <summary>Gets the WinRT device id. Immutable: it is the row's identity.</summary>
    public string Id { get; }

    private string _name = "";
    /// <summary>Gets the display name, or a placeholder when the device has not
    /// advertised one yet.</summary>
    public string Name
    {
        get => _name.Length == 0 ? "Unnamed device" : _name;
        internal set
        {
            if (_name != value)
            {
                _name = value;
                Raise(nameof(Name));
            }
        }
    }

    private bool _paired;
    /// <summary>Gets whether the device is paired.</summary>
    public bool Paired
    {
        get => _paired;
        internal set
        {
            if (_paired != value)
            {
                _paired = value;
                Raise(nameof(Paired));
                Raise(nameof(ActionText));
                Raise(nameof(IconState));
                Raise(nameof(StatusLine));
                Raise(nameof(PrimaryActionVisible));
                Raise(nameof(RemoveVisible));
            }
        }
    }

    private bool _canPair;
    /// <summary>Gets whether Windows believes pairing is currently possible.</summary>
    public bool CanPair
    {
        get => _canPair;
        internal set
        {
            if (_canPair != value)
            {
                _canPair = value;
                Raise(nameof(CanPair));
                Raise(nameof(StatusLine));
                // A device that enters pairing mode later must reveal its Pair
                // button without the row being rebuilt.
                Raise(nameof(PrimaryActionVisible));
            }
        }
    }

    private bool _connected;
    /// <summary>Gets whether the device has a live connection right now. Paired
    /// and connected are different states: a paired headset that is switched
    /// off must not read as "connected".</summary>
    public bool Connected
    {
        get => _connected;
        internal set
        {
            if (_connected != value)
            {
                _connected = value;
                Raise(nameof(Connected));
                Raise(nameof(IconState));
                Raise(nameof(StatusLine));
                Raise(nameof(ActionText));
            }
        }
    }

    private string _containerId = "";
    /// <summary>Gets the device container id, which ties the device to its
    /// audio endpoints. Empty when Windows reported none.</summary>
    public string ContainerId
    {
        get => _containerId;
        internal set
        {
            if (_containerId != value)
            {
                _containerId = value;
                Raise(nameof(ContainerId));
            }
        }
    }

    private bool _audioConnectable;
    /// <summary>Gets whether this device can be connected/disconnected on
    /// demand — true only for devices with audio endpoints. Everything else
    /// (mice, gamepads) reconnects on its own initiative when used, and
    /// Windows offers no general reconnect operation for them; the row then shows only
    /// Pair or Remove, the same choice the Settings app makes.</summary>
    public bool AudioConnectable
    {
        get => _audioConnectable;
        internal set
        {
            if (_audioConnectable != value)
            {
                _audioConnectable = value;
                Raise(nameof(AudioConnectable));
                Raise(nameof(ActionText));
                Raise(nameof(PrimaryActionVisible));
            }
        }
    }

    private bool _audioActive;
    /// <summary>Gets whether this device's AUDIO endpoints are live, which is
    /// what the connect action actually toggles. Deliberately separate from
    /// <see cref="Connected"/>: a headset can hold an association for another
    /// profile while its audio endpoints sit unplugged, and reading the broader
    /// state there would label the button Disconnect and then send the opposite
    /// one-shot.</summary>
    public bool AudioActive
    {
        get => _audioActive;
        internal set
        {
            if (_audioActive != value)
            {
                _audioActive = value;
                Raise(nameof(AudioActive));
                Raise(nameof(ActionText));
            }
        }
    }

    private bool _busy;
    /// <summary>Gets whether an operation is in flight for this device.</summary>
    public bool Busy
    {
        get => _busy;
        internal set
        {
            if (_busy != value)
            {
                _busy = value;
                Raise(nameof(Busy));
                Raise(nameof(ActionText));
                Raise(nameof(StatusLine));
            }
        }
    }

    /// <summary>Gets the label for this row's primary button: Pair for a
    /// stranger; Connect/Disconnect for a paired audio device. The pairing
    /// itself is only ever touched by the separate Remove button.</summary>
    public string ActionText => Busy
        ? "Working..."
        : !Paired ? "Pair"
        : AudioActive ? "Disconnect" : "Connect";

    /// <summary>Gets whether the primary button is shown at all. A paired
    /// non-audio device has no on-demand connect (it reconnects itself when
    /// used), so its only action is Remove — and an unpaired device Windows
    /// says cannot be paired offers nothing at all rather than a Pair button
    /// that is guaranteed to fail.</summary>
    public bool PrimaryActionVisible => Paired ? AudioConnectable : CanPair;

    /// <summary>Gets whether the Remove (unpair) button is shown.</summary>
    public bool RemoveVisible => Paired;

    private bool _expanded;
    /// <summary>Gets whether this row is showing its actions. Same reasoning as
    /// the Wi-Fi rows: a tap reveals the choice, it does not take it.</summary>
    public bool Expanded
    {
        get => _expanded;
        internal set => Set(ref _expanded, value, nameof(Expanded));
    }

    /// <summary>Gets the icon state: accent only for a live connection, muted
    /// for everything else — the same rule as the taskbar tile.</summary>
    public Controls.RadioIconState IconState => Connected
        ? Controls.RadioIconState.Connected
        : Controls.RadioIconState.Disconnected;

    /// <summary>Gets the second line under the name.</summary>
    public string StatusLine => Busy
        ? "Working..."
        : Connected ? "Connected"
        : Paired ? "Paired" : CanPair ? "Available" : "Not available";

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
