using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;
using RadioPower = WindowsDeviceControl.WindowsRadio.Power;
using WifiSecurity = WindowsDeviceControl.WindowsRadio.WifiSecurity;

namespace WSGM.Overlay;

/// <summary>The game-mode Wi-Fi and Bluetooth panel.
///
/// A real window rather than a taskbar flyout for two reasons that both matter
/// on a handheld: a 240 px flyout cannot hold a network list, and
/// <see cref="Input.GamepadNavigation"/> has no popup awareness, so a list
/// inside a flyout would not be reachable with a controller at all.</summary>
public partial class RadioWindow : Window
{
    private readonly RadioManager _radios;
    private bool _applyingSwitch;

    /// <summary>What the prompt is currently collecting, so one input box can
    /// serve both a Wi-Fi password and a Bluetooth PIN.</summary>
    private enum PromptMode
    {
        None,
        WifiPassword,
        PairingPin,
        PairingConfirm,
    }

    private PromptMode _prompt;
    private string _promptSsid = "";
    private uint _promptToken;

    /// <summary>The window's design size in DIPs, before the touch scale.</summary>
    private const double BaseWidth = 500;
    private const double BaseHeight = 600;

    private readonly double _uiScale;

    /// <summary>Creates the panel.</summary>
    /// <param name="radios">The manager backing both tabs. Not owned: the sheet's
    /// status object outlives this window.</param>
    /// <param name="bluetooth">True to open on the Bluetooth tab.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (e.g. 1.5
    /// for a 150% desktop; see DisplayScale.GetUiScalePercent).</param>
    public RadioWindow(RadioManager radios, bool bluetooth, double uiScale = 1.0)
    {
        _radios = radios;
        _uiScale = uiScale;
        InitializeComponent();
        DataContext = radios;

        Tabs.Tabs = new List<TabStripItem>
        {
            new("Wi-Fi", Icons.WiFi),
            new("Bluetooth", Icons.Bluetooth),
        };
        Tabs.SelectionChanged += (_, e) => ShowTab(e.NewIndex);
        Tabs.SelectedIndex = bluetooth ? 1 : 0;
        ShowTab(Tabs.SelectedIndex);

        Keyboard.Accepted += (_, _) => OnPromptAccept(this, new RoutedEventArgs());
        Keyboard.PasteRequested += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    Keyboard.InsertExternalText(text);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Radio keyboard paste failed: {ex.Message}");
            }
        };
        _radios.PairingRequested += OnPairingRequested;
        _radios.PropertyChanged += OnRadiosPropertyChanged;
        Opened += (_, _) => _radios.StartScanning();
        Closed += (_, _) =>
        {
            // A prompt on screen when the panel goes means Windows is still
            // waiting on an answer. Unsubscribing alone left the deferral
            // pending until its 90 s timeout, with the row stuck on Working and
            // no way to start another pairing until it expired — so an
            // abandoned ceremony is declined, not just forgotten.
            if (_prompt is PromptMode.PairingPin or PromptMode.PairingConfirm)
            {
                Log.Info("Radio panel closed with a pairing question open — declining it.");
                _radios.RespondToPairing(_promptToken, accept: false, null);
                _prompt = PromptMode.None;
            }
            _radios.StopScanning();
            _radios.PairingRequested -= OnPairingRequested;
            _radios.PropertyChanged -= OnRadiosPropertyChanged;
        };
        StatusPanel.WirePanelBehaviour(this, ListScroller);
    }

    /// <summary>Places the panel just below the sheet header, at the right-hand end
    /// where its tiles are.
    ///
    /// Without this the window opens wherever Windows decides, which is the
    /// top-left corner — nowhere near the button that opened it. The bar's own
    /// height is measured rather than assumed, because it is content-sized and
    /// DPI-scaled.</summary>
    /// <param name="anchorBottom">The bar's top edge in physical screen pixels, or
    /// 0 when it is not on screen.</param>
    /// <param name="anchorRight">The bar's right edge in physical screen pixels, or 0.</param>
    internal void DockBelowHeader(int anchorBottom, int anchorRight) => StatusPanel.DockBelowHeader(
        this, RootScale, _uiScale, BaseWidth, BaseHeight, anchorBottom, anchorRight, "Radio");

    /// <summary>Scrolls a newly focused row (or its action button) into the
    /// viewport. A no-op when it is already fully visible.</summary>
    /// <summary>Shows the Wi-Fi or Bluetooth tab. Lets an already-open panel
    /// honour the tile that was tapped instead of staying on whichever tab it
    /// happened to open on.</summary>
    /// <param name="bluetooth">True for the Bluetooth tab.</param>
    internal void SelectTab(bool bluetooth) => Tabs.SelectedIndex = bluetooth ? 1 : 0;

    /// <summary>Moves to the previous tab (left shoulder).</summary>
    public void SelectPreviousTab() => Tabs.SelectPrevious();

    /// <summary>Moves to the next tab (right shoulder).</summary>
    public void SelectNextTab() => Tabs.SelectNext();

    private bool OnBluetoothTab => Tabs.SelectedIndex == 1;

    private void ShowTab(int index)
    {
        var bluetooth = index == 1;
        PanelWifi.IsVisible = !bluetooth && _prompt == PromptMode.None;
        PanelBluetooth.IsVisible = bluetooth && _prompt == PromptMode.None;
        PanelTitle.Text = bluetooth ? "Bluetooth" : "Wi-Fi";
        SyncSwitch();
    }

    private void OnRadiosPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RadioManager.WifiPower) or nameof(RadioManager.BluetoothPower))
        {
            SyncSwitch();
        }
    }

    /// <summary>Mirrors the radio's real state onto the switch without letting
    /// that write look like a user toggle.</summary>
    private void SyncSwitch()
    {
        _applyingSwitch = true;
        var power = OnBluetoothTab ? _radios.BluetoothPower : _radios.WifiPower;
        RadioSwitch.IsChecked = power == RadioPower.On;
        // A radio the machine does not have, or that policy has blocked, is not
        // something a switch can fix.
        RadioSwitch.IsEnabled = power is RadioPower.On or RadioPower.Off;
        _applyingSwitch = false;
    }

    // Invoke inside the try as well as awaiting inside it: WinRT-backed methods
    // can fail synchronously before they return a Task.
    private async Task RunRadioActionAsync(Func<Task> action, string operation)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio panel {operation} failed: {ex.Message}");
            _radios.ReportStatus($"{operation} failed: {ex.Message}");
        }
    }

    private void OnRadioSwitchToggled(object? sender, RoutedEventArgs e)
    {
        if (_applyingSwitch)
        {
            return;
        }
        var on = RadioSwitch.IsChecked == true;
        var bluetooth = OnBluetoothTab;
        _ = RunRadioActionAsync(
            () => _radios.SetRadioAsync(bluetooth, on),
            $"{(bluetooth ? "Bluetooth" : "Wi-Fi")} power {(on ? "on" : "off")}");
    }

    /// <summary>Selecting a network reveals its actions. It never connects or
    /// disconnects on its own: a stray tap must not drop the connection the user
    /// is currently browsing on.</summary>
    private void OnNetworkClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not WifiNetworkEntry entry)
        {
            return;
        }
        foreach (var other in _radios.Networks)
        {
            other.Expanded = ReferenceEquals(other, entry) && !entry.Expanded;
        }
    }

    private void OnNetworkAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not WifiNetworkEntry entry)
        {
            return;
        }
        if (entry.Connected)
        {
            _ = RunRadioActionAsync(() => _radios.DisconnectAsync(), "Wi-Fi disconnect");
            return;
        }
        if (entry.Security == WifiSecurity.Enterprise)
        {
            // 802.1X needs an EAP profile and a credential flow this panel has no
            // business guessing at; say so rather than failing obscurely later.
            Log.Info($"Wi-Fi connect: {entry.Ssid} skipped, enterprise networks are not supported.");
            _radios.ReportStatus(
                $"{entry.Ssid} uses enterprise Wi-Fi, which this panel cannot configure. "
                + "Connect from Windows Settings in desktop mode.");
            return;
        }
        if (entry.NeedsPassword)
        {
            _promptSsid = entry.Ssid;
            ShowPrompt(PromptMode.WifiPassword, $"Connect to {entry.Ssid}", "Enter the network password.");
            return;
        }
        _ = RunRadioActionAsync(
            () => _radios.ConnectAsync(entry.Ssid, null),
            $"Wi-Fi connect to {entry.Ssid}");
    }

    private void OnNetworkForget(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WifiNetworkEntry entry)
        {
            _ = RunRadioActionAsync(() => _radios.ForgetAsync(entry.Ssid),
                $"Wi-Fi forget {entry.Ssid}");
        }
    }

    private void OnDeviceClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not BluetoothDeviceEntry entry)
        {
            return;
        }
        foreach (var other in _radios.BluetoothDevices)
        {
            other.Expanded = ReferenceEquals(other, entry) && !entry.Expanded;
        }
    }

    /// <summary>The primary action: pair a stranger, or soft-connect/disconnect
    /// a paired audio device. Never unpairs — that is the Remove button's job,
    /// so a tap meant as "disconnect" can never destroy the pairing.</summary>
    private void OnDeviceAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not BluetoothDeviceEntry entry || entry.Busy)
        {
            return;
        }
        if (!entry.Paired)
        {
            _ = RunRadioActionAsync(() =>
            {
                _radios.BeginPairing(entry);
                return Task.CompletedTask;
            }, $"Bluetooth pairing for {entry.Name}");
            return;
        }
        if (entry.AudioConnectable)
        {
            _ = RunRadioActionAsync(
                () => _radios.SetAudioConnectionAsync(entry, connect: !entry.AudioActive),
                $"Bluetooth audio {(entry.AudioActive ? "disconnect" : "connect")} for {entry.Name}");
        }
    }

    private void OnDeviceRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BluetoothDeviceEntry { Busy: false } entry)
        {
            _ = RunRadioActionAsync(() => _radios.UnpairAsync(entry), $"Bluetooth unpair {entry.Name}");
        }
    }

    private void OnRescanClicked(object? sender, RoutedEventArgs e) => _radios.Rescan();

    /// <summary>Reveals what has been typed. A password field a user cannot read
    /// back is unusable on a keyboard they are tapping one character at a time.</summary>
    private void OnPromptReveal(object? sender, RoutedEventArgs e)
    {
        var hidden = PromptInput.PasswordChar != '\0';
        PromptInput.PasswordChar = hidden ? '\0' : '●';
        PromptReveal.Content = hidden ? "Hide" : "Show";
    }

    private void OnPairingRequested(RadioManager.PairingPrompt prompt)
    {
        _promptToken = prompt.Token;
        switch (prompt.Kind)
        {
            case WindowsRadio.PairingKind.ProvidePin:
                // The device shows a code, the user types it here.
                ShowPrompt(
                    PromptMode.PairingPin,
                    $"Pair with {prompt.DeviceName}",
                    "Enter the PIN shown on the device.");
                break;
            case WindowsRadio.PairingKind.DisplayPin:
            case WindowsRadio.PairingKind.ConfirmPinMatch:
                // Display-pin: we show it, the user types it on the device.
                // Confirm-pin-match: both sides show it, the user confirms.
                ShowPrompt(
                    PromptMode.PairingConfirm,
                    $"Pair with {prompt.DeviceName}",
                    prompt.Kind == WindowsRadio.PairingKind.DisplayPin
                        ? $"Enter this PIN on the device: {prompt.Pin}"
                        : $"Does the device show {prompt.Pin}?");
                break;
            default: // Confirm-only, and an unrecognized ceremony.
                ShowPrompt(
                    PromptMode.PairingConfirm,
                    $"Pair with {prompt.DeviceName}",
                    "Confirm to pair with this device.");
                break;
        }
    }

    private void ShowPrompt(PromptMode mode, string title, string detail)
    {
        _prompt = mode;
        PromptTitle.Text = title;
        PromptDetail.Text = detail;
        PromptInput.Text = "";
        // Only the two "type something" ceremonies get an input box; showing an
        // empty one for a confirmation would invite the user to type into it.
        var needsInput = mode is PromptMode.WifiPassword or PromptMode.PairingPin;
        PromptInput.IsVisible = needsInput;
        PromptReveal.IsVisible = needsInput;
        Keyboard.IsVisible = needsInput;
        Keyboard.Target = PromptInput;
        Keyboard.Reset();
        // Passwords start hidden; the reveal button is there for when a
        // one-character-at-a-time entry has gone wrong.
        PromptInput.PasswordChar = '●';
        PromptReveal.Content = "Show";
        PromptAccept.Content = mode == PromptMode.WifiPassword ? "Connect" : "Pair";
        PromptPanel.IsVisible = true;
        PanelWifi.IsVisible = false;
        PanelBluetooth.IsVisible = false;
        if (needsInput)
        {
            PromptInput.Focus();
        }
        else
        {
            PromptAccept.Focus();
        }
    }

    private void HidePrompt()
    {
        _prompt = PromptMode.None;
        PromptPanel.IsVisible = false;
        PromptInput.Text = "";
        ShowTab(Tabs.SelectedIndex);
    }

    private void OnPromptAccept(object? sender, RoutedEventArgs e)
    {
        var mode = _prompt;
        var text = PromptInput.Text ?? "";
        var ssid = _promptSsid;
        var token = _promptToken;
        // An empty PIN cannot answer the provide-pin ceremony. Keep the prompt
        // open instead of asking WinRT to accept with the wrong overload.
        if (mode == PromptMode.PairingPin && text.Length == 0)
        {
            PromptDetail.Text = "Enter the PIN shown on the device to continue.";
            PromptInput.Focus();
            return;
        }
        HidePrompt();
        switch (mode)
        {
            case PromptMode.WifiPassword:
                _ = RunRadioActionAsync(() => _radios.ConnectAsync(ssid, text),
                    $"Wi-Fi connect to {ssid}");
                break;
            case PromptMode.PairingPin:
                _radios.RespondToPairing(token, accept: true, text);
                break;
            case PromptMode.PairingConfirm:
                _radios.RespondToPairing(token, accept: true, null);
                break;
        }
    }

    private void OnPromptCancel(object? sender, RoutedEventArgs e)
    {
        var mode = _prompt;
        var token = _promptToken;
        HidePrompt();
        // A pairing ceremony that is simply abandoned stalls until Windows times
        // it out, so a cancel has to be reported rather than just dismissed.
        if (mode is PromptMode.PairingPin or PromptMode.PairingConfirm)
        {
            _radios.RespondToPairing(token, accept: false, null);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
