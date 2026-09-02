using System;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Shell;

/// <summary>Boot-time "Please wait" cover: shown at shell start to hide startup-app
/// window flashes, dismissed when the Big Picture window is actually on screen
/// (short overlap + fade), on timeout, when quick access appears, or by its own
/// Switch-to-desktop button. Deliberately takes NO Steam Input lease: the splash dies
/// exactly when Steam's window takes the screen, and a held lease at that moment is
/// the device-verified state that breaks Big Picture's pad input (invariant 1).</summary>
public sealed class BootSplash
{
    // Tight poll: Big Picture's UI (steamwebhelper/CEF) SUSPENDS rendering while
    // fully occluded — a video that initializes under an opaque cover stays black
    // (device-observed; same behavior as BP under a game). The splash must drop
    // below full opacity the moment BP's window exists, so: detect fast, fade
    // immediately (first fade tick lifts the occlusion), no opaque overlap.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TouchCloseGrace = TimeSpan.FromMilliseconds(150);

    private readonly AppConfig _config;
    private readonly Action _switchToDesktop;
    private BootSplashWindow? _window;
    private GamepadService? _gamepad;
    private GamepadNavigation? _navigation;
    private DispatcherTimer? _pollTimer;
    private IDisposable? _pendingAction;
    private DateTime _shownUtc;
    private bool _dismissing;
    private bool _closeScheduled;

    /// <summary>Creates the startup splash coordinator.</summary>
    /// <param name="config">The shell configuration containing splash and display settings.</param>
    /// <param name="switchToDesktop">The session-owned action that cancels any
    /// active boot takeover and completes the desktop fallback.</param>
    public BootSplash(AppConfig config, Action switchToDesktop)
    {
        ArgumentNullException.ThrowIfNull(switchToDesktop);
        _config = config;
        _switchToDesktop = switchToDesktop;
    }

    /// <summary>UI thread only (ShellSession.Start is).</summary>
    public void Show()
    {
        _window = new BootSplashWindow(_config.Splash);
        _window.DesktopRequested += OnDesktopRequested;
        _window.Closed += (_, _) => OnWindowClosed();
        _window.Opened += (_, _) =>
        {
            // Own service instance (SettingsWindow pattern; single SDL pump).
            // Nothing is focused: the first D-pad press lands on the desktop
            // button via preferredFocus, and A alone activates nothing.
            _gamepad = new GamepadService();
            _navigation = new GamepadNavigation(_gamepad, _window!, back: static () => { },
                isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo,
                preferredFocus: () => _window?.DefaultFocusTarget);
            _gamepad.Start();
        };

        _shownUtc = DateTime.UtcNow;
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();

        _window.Show();
        Log.Info("Boot splash shown.");
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_dismissing)
        {
            return;
        }
        if (DateTime.UtcNow - _shownUtc > HardTimeout)
        {
            Log.Warn($"Boot splash timeout after {HardTimeout.TotalSeconds:0} s — closing (Big Picture window never appeared).");
            _dismissing = true;
            _pollTimer?.Stop();
            CloseAfter(TouchCloseGrace);
            return;
        }
        if (Steam.IsBigPictureVisible)
        {
            OnBigPictureDetected();
        }
    }

    private void OnBigPictureDetected()
    {
        Log.Info("Big Picture window detected — dismissing boot splash.");
        _dismissing = true;
        _pollTimer?.Stop();
        // Fade IMMEDIATELY (no opaque overlap — see PollInterval comment) and no
        // focus handoff afterwards: Steam takes the foreground itself, and a
        // protocol re-activation here kills the intro video (device-observed).
        _window?.BeginFadeOut(FadeDuration, () => _window?.Close());
    }

    private void OnDesktopRequested()
    {
        if (_dismissing)
        {
            return;
        }
        _dismissing = true;
        Log.Info("Boot splash: switching to desktop.");
        _pollTimer?.Stop();
        CloseAfter(TouchCloseGrace);
        _switchToDesktop();
    }

    /// <summary>External dismissal (quick access opened, Steam start warning).
    /// Idempotent; cancels a pending overlap fade so the splash can never fade in
    /// over the overlay later.</summary>
    public void Dismiss(string reason)
    {
        // _closeScheduled (not _dismissing) is the idempotence gate: a pending
        // overlap fade sets _dismissing but must still be cancellable here.
        if (_window is null || _closeScheduled)
        {
            return;
        }
        _pendingAction?.Dispose();
        _pendingAction = null;
        _dismissing = true;
        _pollTimer?.Stop();
        CloseAfter(TouchCloseGrace);
        Log.Info($"Boot splash dismissed ({reason}).");
    }

    /// <summary>Deferred close: a touch tap's promoted mouse click arrives after the
    /// tap — the window must still exist to eat it (same beat as the overlay).</summary>
    private void CloseAfter(TimeSpan delay)
    {
        _closeScheduled = true;
        _pendingAction = DispatcherTimer.RunOnce(() => _window?.Close(), delay);
    }

    private void OnWindowClosed()
    {
        // Idempotent — also runs when lifetime.Shutdown() closes the window
        // mid-boot (update flow). A dead splash leaves no persistent state, so
        // recovery paths need no knowledge of it.
        if (_pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= OnPollTick;
            _pollTimer = null;
        }
        _pendingAction?.Dispose();
        _pendingAction = null;
        _navigation?.Dispose();
        _navigation = null;
        _gamepad?.Stop();
        _gamepad = null;
        _window = null;
    }
}
