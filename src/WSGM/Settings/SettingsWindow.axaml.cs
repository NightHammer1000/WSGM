using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Settings;

/// <summary>The interactive settings window for shell and game-mode configuration:
/// a bumper <see cref="TabStrip"/> over its always-alive pages (toggled by
/// visibility so their state survives switching) and a bottom status strip.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel = new();
    private readonly GamepadService _gamepad = new();
    private readonly Control[] _pages;
    private GamepadNavigation? _navigation;
    private OverlayController? _testOverlay;
    private BootSplashWindow? _splashPreview;
    private Window? _keyboardDialog;
    private bool _closed;

    // When Settings is the on-screen surface in game mode it must hold the Steam
    // Input lease, exactly like the overlay: without it Steam's desktop profile
    // stays live over this window, grabs the pad from SDL and injects its own
    // desktop bindings (invariant 1) — the ghost/double input.
    //
    // The lease is HANDED OVER from the sidebar, not re-taken: the overlay keeps
    // its (shared, static SteamInputBlocker) lease held across the open instead of
    // releasing it, so Steam's controller is never dropped and re-revoked in the
    // handoff — the churn the user saw as "controller gone again seconds later".
    // This window then owns that same lease and drives it via SteamInputBlocker.
    //
    // It tracks focus, not just lifetime: held only while this window (or the
    // splash preview it drives by pad) is the active, non-minimized foreground,
    // so unfocusing or minimizing Settings hands the controller straight back to
    // Big Picture. The reconciler keeps at most one inject/release in flight and
    // re-runs on completion, so rapid focus flips coalesce instead of thrashing.
    private readonly bool _gameModeSurface;
    private static int _nextLeaseOwnerId;
    // Owner-scoped, like OverlayController's: the lease is shared static state, so a
    // surface that merely observes IsApplied cannot tell "I hold it" from "someone
    // else does" — and its release then drops the block out from under whichever
    // surface is still on screen (invariant 1).
    private readonly string _leaseOwner =
        $"settings-window#{System.Threading.Interlocked.Increment(ref _nextLeaseOwnerId)}";
    private readonly object _leaseSync = new();
    private readonly SettingsLeaseReconciler _leaseReconciler = new();
    private bool _leaseEnabled;
    private bool _leaseHandoffPending;

    // In game mode WSGM hosts the only taskbar, and it excludes own-process windows
    // (the overlay/taskbar/tray chrome). This window opts in so it stays reachable
    // after it drops behind Big Picture.
    private nint _switchableHwnd;

    /// <summary>Creates the settings window, builds the tab strip and connects
    /// controller navigation and the shortcut recorders.</summary>
    /// <param name="gameModeSurface">True when opened as the on-screen surface in
    /// game mode (from the overlay), which makes the window hold a Steam Input
    /// lease for its lifetime. The desktop settings paths leave it false.</param>
    public SettingsWindow(bool gameModeSurface = false)
    {
        _gameModeSurface = gameModeSurface;
        _leaseHandoffPending = gameModeSurface;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // The one page table: it drives the tab strip, the visibility toggle and
        // the focus landing alike (the XAML hosts the same pages in this order).
        (string Title, Avalonia.Media.StreamGeometry Icon, Control Page)[] pages =
        [
            ("System", Icons.Monitor, PageSystem),
            ("Steam", Icons.SteamLike, PageSteam),
            ("Integration", Icons.Wrench, PageIntegration),
            ("Device setup", Icons.SteamLike, PageDevice),
            ("Startup", Icons.Rocket, PageStartup),
            ("Quick access", Icons.Panel, PageQuickAccess),
            ("Display", Icons.Monitor, PageDisplay),
            ("Appearance", Icons.Palette, PageAppearance),
            // Last, because its content belongs to whichever plugin is installed: WSGM's own pages
            // keep their positions on every machine rather than shifting around a tab that may not
            // be there.
            ("Plugin", Icons.Wrench, PagePluginSettings),
        ];
        _pages = [.. pages.Select(static entry => entry.Page)];
        Tabs.Tabs = [.. pages.Select((entry, index) => new TabStripItem(entry.Title, entry.Icon, index))];
        Tabs.SelectionChanged += OnTabSelectionChanged;

        // Controller navigation for the settings window itself. LB/RB cycle the
        // tab strip (which wraps at both ends).
        // Focus changes drive the lease; the opt-out is snapshotted once and stays
        // fixed for this window's life. Turning the lease off on the Quick access
        // page therefore takes effect at the NEXT surface open, not on this
        // one: dropping the lease the moment the user pressed "Save changes"
        // would hand the pad straight back to Steam's desktop profile, which swallows
        // it from SDL system-wide, and the controller user would be stranded in a
        // settings window they can no longer navigate. Same rule as
        // OverlayController.AcquireSteamInputLease (docs\steam-input.md).
        if (_gameModeSurface)
        {
            // From the view model, which already loaded config.json for this
            // window — a second ConfigStore.Load here takes the cross-process
            // mutex again on the UI thread for a value that is already in memory.
            _leaseEnabled = _viewModel.SteamInputLeaseEnabled;
            Activated += (_, _) => UpdateLeaseDesired();
            Deactivated += (_, _) => UpdateLeaseDesired();
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty)
                {
                    UpdateLeaseDesired();
                }
            };
        }
        // Every other GamepadNavigation host handles Escape itself; Settings did not,
        // and GamepadNavigation's keyboard-Escape branch arms its cross-source
        // suppression window whether or not anything acted on the key — so an Escape
        // arriving here swallowed the next controller B press instead of going back.
        KeyDown += OnWindowKeyDown;
        Opened += (_, _) =>
        {
            _navigation = CreateWindowNavigation();
            _gamepad.Start();
            InheritSteamInputLease();
            // Normally OverlayController acknowledges the handoff when its 150 ms
            // deferred close finishes. If that close was cancelled or its callback
            // was otherwise lost, never let the temporary focus exemption become a
            // permanent owner claim.
            Avalonia.Threading.DispatcherTimer.RunOnce(
                CompleteSteamInputLeaseHandoff,
                System.TimeSpan.FromSeconds(1));
            if (_gameModeSurface)
            {
                _switchableHwnd = TryGetPlatformHandle()?.Handle ?? 0;
                WindowFinder.IncludeOwnWindow(_switchableHwnd);
            }
            // Brackets the window's lifetime for splash-theme imports: an imported
            // theme's images live in a temp staging directory this process pins open
            // until the matching EndImportSession below, because an unsaved import must
            // stay materializable for as long as this window can still save it. Opening
            // the session also sweeps orphans left by earlier sessions. Paired with
            // Opened (not the constructor) so a window that is built but never shown
            // cannot leave a session — and therefore a pinned directory — behind.
            SplashTheme.BeginImportSession();
            _ = _viewModel.RefreshDeviceOwnerStatusAsync();
            MaybeShowQuickSetup();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _gamepad.Stop();
            WindowFinder.ExcludeOwnWindow(_switchableHwnd);
            // _closed makes the lease unwanted; the reconciler releases it.
            UpdateLeaseDesired();
            _navigation?.Dispose();
            _navigation = null;
            // The splash preview must not outlive Settings; its Closed handler
            // sees _closed and skips recreating window navigation.
            _splashPreview?.Close();
            _splashPreview = null;
            // Neither may the keyboard dialog; its own Closed handler restores
            // this window's navigation, which the line above already disposed.
            _keyboardDialog?.Close();
            _keyboardDialog = null;
            _testOverlay?.Dispose();
            _testOverlay = null;
            // The Appearance page live-applies accent picks to the running
            // Application as a preview. In the long-lived shell process an
            // unsaved close would otherwise leak that preview accent onto every
            // surface, so re-apply the persisted accent here (after a save this
            // re-applies the same color; after an abandoned preview it restores
            // the saved one).
            if (Avalonia.Application.Current is { } app)
            {
                Themes.AccentPalette.Apply(
                    app, Themes.AccentPalette.Parse(ConfigStore.Load().AccentColor));
            }
            // Recorder disposal keeps its historical slot and order (key recorder
            // first, chord second) so the hooks are gone on every close path.
            _keyRecorder?.Dispose();
            _keyRecorder = null;
            _chordRecorder?.Dispose();
            _chordRecorder = null;
            // LAST: nothing above may still read a staged import. Any save has long
            // committed the staged images into the stable splash assets by now, and an
            // abandoned import is exactly what this frees — up to ~128 MB of staged
            // images per import that used to stay pinned until the shell process
            // exited. Counted, so a second settings window's unsaved import (and any
            // other process's) survives this.
            SplashTheme.EndImportSession();
        };
    }

    /// <summary>One selection path for touch, mouse, keyboard and the LB/RB
    /// shoulder buttons: the TabStrip owns the index, this toggles the
    /// always-alive pages' visibility.</summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        for (var index = 0; index < _pages.Length; index++)
        {
            _pages[index].IsVisible = index == e.NewIndex;
        }

        // Land controller focus inside the newly shown page — without this the
        // next D-pad press falls back to the window's first focusable, which is
        // always the "System" tab button regardless of the active tab.
        FocusFirstControl(_pages[Math.Clamp(e.NewIndex, 0, _pages.Length - 1)]);
    }

    private static void FocusFirstControl(Control page)
    {
        foreach (var visual in page.GetVisualDescendants())
        {
            // TextBoxes are excluded for the same reason D-pad traversal skips
            // them: focusing one pops the touch keyboard.
            if (visual is InputElement { Focusable: true, IsEffectivelyEnabled: true } element
                && element is not TextBox
                && element.IsEffectivelyVisible)
            {
                element.Focus(NavigationMethod.Directional);
                return;
            }
        }
    }

    /// <summary>Shows the quick access panel for a local test (called by the
    /// Quick access page). Uses the real controller so behavior matches shell
    /// mode exactly; rebuilt for every test so unsaved glyph/input changes take
    /// effect immediately.</summary>
    internal void ShowTestOverlay()
    {
        _testOverlay?.Dispose();
        var config = _viewModel.SnapshotForPreview();
        _testOverlay = new OverlayController(config, monitor: null, new SessionModes(config, monitor: null),
            previewOnly: true);
        _testOverlay.ShowOverlay();
    }

    /// <summary>Raises Quick Setup over the window on a first run, or after a build
    /// adds a setting that needs an explicit decision.</summary>
    /// <remarks>
    /// The panel owns input while it is up: the pages behind it are disabled so
    /// gamepad focus cannot wander into them and answer nothing. Both integrations
    /// arrive pre-selected because both are what the product expects, but neither is
    /// applied until Continue - a skipped panel leaves Steam's directory untouched.
    /// </remarks>
    private void MaybeShowQuickSetup()
    {
        if (DataContext is not SettingsViewModel viewModel || !viewModel.QuickSetupPending)
        {
            return;
        }
        QuickSetupSteamInput.IsChecked = viewModel.SteamInputManagementEnabled;
        QuickSetupCef.IsChecked = viewModel.CefEnabled;
        QuickSetupOverlay.IsVisible = true;
        UpdateSettingsEnabled();
        QuickSetupContinueButton.Focus();
    }

    private void OnQuickSetupContinue(object? sender, RoutedEventArgs e) =>
        CompleteQuickSetup(
            QuickSetupSteamInput.IsChecked == true, QuickSetupCef.IsChecked == true);

    private void OnQuickSetupSkip(object? sender, RoutedEventArgs e) =>
        // Skipping is a decision, not a deferral: nothing gets written into Steam's
        // directory or its debug port until the user has actually said yes.
        CompleteQuickSetup(steamInput: false, cef: false);

    private void CompleteQuickSetup(bool steamInput, bool cef)
    {
        QuickSetupOverlay.IsVisible = false;
        UpdateSettingsEnabled();
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }
        viewModel.SteamInputManagementEnabled = steamInput;
        viewModel.CefEnabled = cef;
        viewModel.QuickSetupAnswered = true;
        Log.Info(
            $"Quick Setup completed (revision {QuickSetup.CurrentRevision}): " +
            $"steamInputManagement={steamInput}, cef={cef}.");
        viewModel.SaveCommand.Execute(null);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsSaving))
        {
            UpdateSettingsEnabled();
        }
    }

    /// <summary>Keeps the page controls inert while either Quick Setup owns input or a
    /// save is persisting its immutable snapshot. This prevents a post-capture edit
    /// from being followed by a misleading "Saved" acknowledgement.</summary>
    private void UpdateSettingsEnabled() =>
        SettingsRoot.IsEnabled = !_viewModel.IsSaving && !QuickSetupOverlay.IsVisible;

    /// <summary>Whether the unsaved glyph selection is the Nintendo family, whose
    /// A/B labels are swapped relative to Xbox — shared by every
    /// <see cref="GamepadNavigation"/> this window creates.</summary>
    private bool IsNintendoLayout() => _viewModel.GlyphStyleIndex == 2;

    /// <summary>Creates the controller navigation attached to this window
    /// (initial Opened wiring and restoration after a splash preview closes).</summary>
    private GamepadNavigation CreateWindowNavigation() => new(_gamepad, this, back: BackOrClose,
        isNintendoLayout: IsNintendoLayout,
        tabPrevious: Tabs.SelectPrevious,
        tabNext: Tabs.SelectNext);

    /// <summary>The controller Back action. A color-picker flyout the Appearance
    /// page has open takes B first: its content lives in a popup root that
    /// gamepad navigation cannot enter, so without this B would close the whole
    /// window and discard every unsaved edit on every page.</summary>
    private void BackOrClose()
    {
        if (PageAppearance.TryCloseColorFlyout())
        {
            return;
        }
        Close();
    }

    /// <summary>Routes a keyboard Escape through the same Back action the controller's
    /// B button uses, so an open colour flyout is closed first rather than the whole
    /// window with every unsaved edit on it.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BackOrClose();
        }
    }

    /// <summary>Opens the on-screen keyboard for a text box in its own dialog and
    /// moves controller navigation onto it (called by the Steam page for the
    /// SteamGridDB key). The window owns this because it owns the gamepad service
    /// and the navigation swap: the keyboard's keys are only reachable by pad once
    /// a <see cref="GamepadNavigation"/> is attached to THAT window, and this
    /// window's own navigation has to be parked meanwhile — Avalonia's modal
    /// dialog disables the owner at the Win32 level only, so its controls stay
    /// effectively enabled and a pad press would otherwise still act on the page
    /// behind the dialog (a machine-policy toggle sits there).</summary>
    /// <param name="target">The text box the keystrokes are typed into.</param>
    /// <param name="title">The dialog window title.</param>
    internal void ShowOnScreenKeyboard(TextBox target, string title)
    {
        ArgumentNullException.ThrowIfNull(target);
        OpenKeyboardEditor(
            target.Text ?? string.Empty,
            target.MaxLength,
            title,
            value =>
            {
                target.Text = value;
                target.CaretIndex = value.Length;
                target.SelectionStart = value.Length;
                target.SelectionEnd = value.Length;
                return null;
            });
    }

    /// <summary>Opens the controller keyboard for a value that has no fixed TextBox,
    /// such as a row created from a plugin manifest.</summary>
    /// <param name="initialValue">Initial text shown to the user.</param>
    /// <param name="maximumLength">Hard input bound.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="accept">Applies the result and returns an error to keep the dialog open, or null.</param>
    internal void ShowOnScreenKeyboard(
        string initialValue,
        int maximumLength,
        string title,
        Func<string, string?> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        OpenKeyboardEditor(initialValue, Math.Max(1, maximumLength), title, accept);
    }

    private void OpenKeyboardEditor(
        string initialValue,
        int maximumLength,
        string title,
        Func<string, string?> accept)
    {
        var editor = new TextBox
        {
            Text = initialValue ?? string.Empty,
            MaxLength = Math.Max(0, maximumLength),
            Margin = new Thickness(12, 12, 12, 0),
            MinHeight = 44,
        };
        editor.CaretIndex = editor.Text?.Length ?? 0;
        editor.SelectionStart = editor.CaretIndex;
        editor.SelectionEnd = editor.CaretIndex;
        var keyboard = new OnScreenKeyboard { Target = editor };
        var validation = new TextBlock
        {
            IsVisible = false,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12, 8, 12, 0),
        };
        var content = new StackPanel();
        content.Children.Add(editor);
        content.Children.Add(validation);
        content.Children.Add(keyboard);
        var window = new Window
        {
            Title = title,
            Width = 760,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };
        keyboard.Accepted += (_, _) =>
        {
            try
            {
                string? error = accept(editor.Text ?? string.Empty);
                if (!string.IsNullOrEmpty(error))
                {
                    validation.Text = error;
                    validation.IsVisible = true;
                    return;
                }
                window.Close();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"On-screen keyboard value apply failed: {ex.Message}");
                validation.Text = $"Could not apply that value: {ex.Message}";
                validation.IsVisible = true;
            }
        };
        GamepadNavigation? keyboardNavigation = null;
        window.Opened += (_, _) =>
        {
            if (_navigation is not null)
            {
                _navigation.IsEnabled = false;
            }
            keyboardNavigation = new GamepadNavigation(_gamepad, window, back: window.Close,
                isNintendoLayout: IsNintendoLayout);
        };
        window.Closed += (_, _) =>
        {
            keyboardNavigation?.Dispose();
            keyboardNavigation = null;
            if (_navigation is not null)
            {
                _navigation.IsEnabled = true;
            }
            if (ReferenceEquals(_keyboardDialog, window))
            {
                _keyboardDialog = null;
            }
            // Same re-evaluation the splash preview does on close, in case focus
            // did not return to this window.
            UpdateLeaseDesired();
        };
        // The dialog deactivates this window, and an unfocused Settings drops the
        // Steam Input lease — which in game mode hands the pad straight back to
        // Steam's desktop profile and makes the keyboard unusable by controller.
        // Tracked like the splash preview so the lease follows the child surface.
        _keyboardDialog = window;
        UpdateLeaseDesired();
        _ = window.ShowDialog(this);
    }

    /// <summary>Whether the lease should be held right now: only in game mode with
    /// the user opt-in, while this window is open, not minimized, and either active
    /// or driving one of its child surfaces (the splash preview, the on-screen
    /// keyboard dialog) by pad. Reads UI state — UI thread only.</summary>
    private bool ShouldHoldLease()
        => SettingsLeaseReconciler.ShouldHold(
            _gameModeSurface,
            _leaseEnabled,
            _closed,
            WindowState == WindowState.Minimized,
            IsActive,
            _splashPreview is not null || _keyboardDialog is not null,
            _leaseHandoffPending);

    /// <summary>Ends the short focus exemption used while the overlay's deferred
    /// close still overlaps this window. The overlay calls this after relinquishing
    /// its owner name; the Opened fallback also calls it if that close was cancelled.</summary>
    internal void CompleteSteamInputLeaseHandoff()
    {
        if (!_gameModeSurface)
        {
            return;
        }
        _leaseHandoffPending = false;
        UpdateLeaseDesired();
    }

    /// <summary>Takes over the lease the sidebar handed off. It is already held, so
    /// this is a no-op that avoids releasing/re-injecting (the churn); the reconcile
    /// only acts if the handoff lease was somehow absent. UI thread.</summary>
    private void InheritSteamInputLease()
    {
        if (!_gameModeSurface || !_leaseEnabled)
        {
            return;
        }
        // Register before the overlay's deferred close relinquishes its owner name.
        // ClaimFor is deliberately claim-only: a cold injection belongs on the
        // reconciler's worker, never the UI thread. With a live handoff, this name
        // keeps the same native lease continuously applied while the overlay lets go.
        SteamInputBlocker.ClaimFor(_leaseOwner);
        var held = SteamInputBlocker.IsApplied;
        SettingsLeaseAction action;
        lock (_leaseSync)
        {
            // Shown as the foreground surface — do not gate the initial state on
            // IsActive, which can still be false at Opened and would drop the lease.
            action = _leaseReconciler.InheritClaim(held);
        }
        RunLeaseAction(action);
    }

    /// <summary>Recomputes whether the lease is wanted and kicks the reconciler.
    /// Called on every focus, window-state and child-surface change (UI thread).</summary>
    private void UpdateLeaseDesired()
    {
        SettingsLeaseAction action;
        lock (_leaseSync)
        {
            action = _leaseReconciler.SetDesired(ShouldHoldLease());
        }
        RunLeaseAction(action);
    }

    /// <summary>Runs the next state-machine action. The reconciler marks the action
    /// busy before returning it, so scheduling outside the state lock cannot admit
    /// a second acquire or release.</summary>
    private void RunLeaseAction(SettingsLeaseAction action)
    {
        switch (action)
        {
            case SettingsLeaseAction.Acquire:
                _ = Task.Run(AcquireLeaseWork);
                break;
            case SettingsLeaseAction.Release:
                _ = Task.Run(ReleaseLeaseWork);
                break;
        }
    }

    private void AcquireLeaseWork()
    {
        // SteamInputBlocker is a no-op when the lease is already held (the handoff
        // case) and injects only on a real 0-held transition; it logs its own
        // outcome and never throws.
        SteamInputBlocker.AcquireFor(_leaseOwner);
        SettingsLeaseAction action;
        lock (_leaseSync)
        {
            // AcquireFor registered our owner even if Steam was not running and
            // the native acquire failed. CompleteAcquireFor preserves that claim so
            // a later deactivate/close always removes it.
            action = _leaseReconciler.CompleteAcquireFor();
        }
        RunLeaseAction(action);
    }

    private void ReleaseLeaseWork()
    {
        // ReleaseFor, not ReleaseBestEffort: the quick-access panel may have been
        // re-summoned over this window and still own the lease.
        SteamInputBlocker.ReleaseFor(_leaseOwner, "settings surface inactive");
        SettingsLeaseAction action;
        lock (_leaseSync)
        {
            action = _leaseReconciler.CompleteRelease();
        }
        // Focus may have returned during release — re-acquire if so.
        RunLeaseAction(action);
    }

    /// <summary>Shows the boot-splash preview (called by the Appearance page) and
    /// swaps controller navigation onto the preview window so B closes the preview
    /// instead of Settings; navigation returns here when the preview closes. The
    /// preview never outlives this window (see the Closed handler).</summary>
    internal void ShowSplashPreview(SplashConfig splash)
    {
        // Closing a previous preview restores window navigation via its Closed
        // handler before the swap below moves it to the new preview.
        _splashPreview?.Close();
        var preview = new BootSplashWindow(splash, preview: true);
        _splashPreview = preview;
        // The preview has no boot flow to hand off to — the desktop button just
        // dismisses it (otherwise the preview's most prominent, focused control
        // would be inert on a touch handheld).
        preview.DesktopRequested += preview.Close;
        preview.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_splashPreview, preview))
            {
                return;
            }
            _splashPreview = null;
            _navigation?.Dispose();
            _navigation = _closed ? null : CreateWindowNavigation();
            // The preview no longer needs the pad; re-evaluate in case focus did
            // not return to this window (so the lease is not held while unfocused).
            UpdateLeaseDesired();
        };
        // Show BEFORE the navigation swap: a Show() failure must leave Settings
        // fully controller-navigable (the page's catch reports the error).
        preview.Show();
        _navigation?.Dispose();
        _navigation = new GamepadNavigation(_gamepad, preview, back: preview.Close,
            isNintendoLayout: IsNintendoLayout,
            preferredFocus: () => preview.DefaultFocusTarget);
    }

    // --- Shortcut recorders (keyboard hotkey + controller chord) ---
    // The 200 ms arming delay keeps the press that STARTED recording out of the
    // recording, and the re-check after that delay prevents installing a
    // low-level keyboard hook with nothing left to dispose it — or one the user
    // already cancelled.
    private KeyRecorder? _keyRecorder;
    private GamepadChordRecorder? _chordRecorder;

    // Bumped by every arm AND every clear, so the continuation after the arming
    // delay can tell whether its own request is still the one the user wants.
    private int _hotkeyGeneration;
    private int _chordGeneration;

    /// <summary>Starts hotkey recording (called by the Quick access page).</summary>
    internal void RecordHotkey() => Observe(ArmHotkeyRecorder(), "Hotkey recording");

    /// <summary>Arms keyboard-shortcut recording (200 ms delayed, cancel- and
    /// closed-window safe).</summary>
    /// <returns>A task that completes once the recorder is armed, or once this
    /// request has been superseded.</returns>
    private async Task ArmHotkeyRecorder()
    {
        // Small delay so the key/controller press that started recording (Enter, A)
        // isn't the thing we record — same trick Handheld Companion uses.
        _viewModel.SetHotkeyRecording(true);
        var generation = ++_hotkeyGeneration;
        await Task.Delay(200);
        if (_closed || generation != _hotkeyGeneration)
        {
            // Window closed during the delay: creating the recorder now would
            // install a low-level keyboard hook with nothing left to dispose it.
            // A cleared/restarted recording is the same hazard from the other
            // side — the UI already says nothing is being recorded, so the hook
            // would swallow the user's next keystroke anywhere and silently make
            // it the hotkey (invariant 2: the hook exists only while recording).
            return;
        }

        _keyRecorder?.Dispose();
        _keyRecorder = new KeyRecorder();
        _keyRecorder.Recorded += hotkey =>
        {
            _viewModel.ApplyRecordedHotkey(hotkey);
            _keyRecorder?.Dispose();
            _keyRecorder = null;
        };
        _keyRecorder.Start();
    }

    /// <summary>Clears the recorded hotkey and stops any active recording
    /// (called by the Quick access page).</summary>
    internal void ClearHotkey()
    {
        _hotkeyGeneration++;
        _keyRecorder?.Dispose();
        _keyRecorder = null;
        _viewModel.ClearHotkey();
    }

    /// <summary>Starts controller-chord recording (called by the Quick access page).</summary>
    internal void RecordChord() => Observe(ArmChordRecorder(), "Chord recording");

    /// <summary>Arms controller-chord recording (200 ms delayed, cancel- and
    /// closed-window safe).</summary>
    /// <returns>A task that completes once the recorder is armed, or once this
    /// request has been superseded.</returns>
    private async Task ArmChordRecorder()
    {
        _viewModel.SetChordRecording(true);
        var generation = ++_chordGeneration;
        await Task.Delay(200);
        if (_closed || generation != _chordGeneration)
        {
            // Same races as the hotkey recorder: no recorder after the window is
            // gone, and none after the user cleared or restarted the recording.
            return;
        }

        _chordRecorder?.Dispose();
        // The window's own polling service — the chord recorder shares it rather
        // than running a second 16 ms SDL poller.
        _chordRecorder = new GamepadChordRecorder(_gamepad);
        _chordRecorder.Recorded += (buttons, hold) =>
        {
            _viewModel.ApplyRecordedChord(buttons, hold);
            _chordRecorder?.Dispose();
            _chordRecorder = null;
        };
        _chordRecorder.Start();
    }

    /// <summary>Observes an armed recorder: the recorders are manager operations,
    /// not framework event handlers, so a throw after their arming delay is logged
    /// here instead of reaching the dispatcher unobserved (which in the shell
    /// process is a crash rather than a reported failure).</summary>
    private static void Observe(Task task, string operation) =>
        task.ContinueWith(
            t => Log.Error($"{operation} failed", t.Exception!),
            System.Threading.CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    /// <summary>Clears the recorded chord and stops any active recording
    /// (called by the Quick access page).</summary>
    internal void ClearChord()
    {
        _chordGeneration++;
        _chordRecorder?.Dispose();
        _chordRecorder = null;
        _viewModel.ClearChord();
    }
}
