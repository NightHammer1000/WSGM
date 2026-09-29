using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The quick access sheet: the controller-friendly, top-docked surface that
/// carries the pinned home root, the Session / Steam / Device / Tools / Power roots with
/// their nested pages, the header status pills and the Open apps strip. It covers
/// <see cref="SheetHeightFraction"/> of the display and leaves the game visible below.</summary>
public partial class OverlayWindow : Window
{
    /// <summary>Share of the display height the sheet covers. The rest stays the
    /// game's — a tap there is outside the window rectangle and dismisses the sheet
    /// through the raw-input hit test, which is why the sheet is deliberately NOT
    /// fullscreen.</summary>
    internal const double SheetHeightFraction = 0.8125;

    /// <summary>Raised when a nested page is torn down so auxiliary peer windows close too.</summary>
    public event Action? SubViewClosed;
    private bool _confirmRestart;
    private bool _confirmShutdown;
    private DispatcherTimer? _confirmResetTimer;
    private DispatcherTimer? _slideTimer;
    private PixelPoint _slideStart;
    private PixelPoint _slideEnd;
    private DateTime _slideStartedUtc;

    /// <summary>Raised when the user requests to start or focus the home application.</summary>
    public event Action? HomeAppRequested;

    /// <summary>Raised when the user requests a desktop/game-mode transition.</summary>
    public event Action? DesktopRequested;

    /// <summary>Raised when the user requests the Settings window.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the user requests to leave Steam Big Picture mode.</summary>
    public event Action? ExitBigPictureRequested;

    /// <summary>Raised after the user confirms closing the home application.</summary>
    public event Action? CloseLauncherRequested;

    /// <summary>Raised when the user requests Task Manager.</summary>
    public event Action? TaskManagerRequested;

    /// <summary>Raised when the keep-awake row is activated (toggle the manual hold).</summary>
    public event Action? KeepAwakeToggleRequested;

    /// <summary>Raised when an idle-timeout row is activated (cycle to the next preset).</summary>
    public event Action<Core.PowerTimeoutKind>? PowerTimeoutCycleRequested;

    /// <summary>Raised when the overlay is dismissed without another action.</summary>
    public event Action? Dismissed;

    /// <summary>Raised when the user picks an Open apps chip (or cycles with Y).</summary>
    public event Action<AppSwitcherEntry>? WindowPicked;

    /// <summary>Raised when the user activates a tray icon. Arguments: the entry,
    /// whether this is a context-menu (right-click / X) activation, and the screen
    /// pixel position the app should anchor any menu to.</summary>
    public event Action<TrayIconEntry, bool, PixelPoint>? TrayIconActivated;

    /// <summary>Raised when a radio pill is tapped. The flag selects the tab to
    /// open on: true for Bluetooth, false for Wi-Fi.</summary>
    public event Action<bool>? RadioPanelRequested;

    /// <summary>Raised when the audio pill is pressed.</summary>
    public event Action? AudioPanelRequested;

    /// <summary>Raised when the eject pill (or the Tools row) is pressed.</summary>
    public event Action? EjectPanelRequested;

    /// <summary>Raised with a row's id when the user pins or unpins it (X on the
    /// focused row, a touch hold, or a right click). The controller owns the
    /// persisted list and hands the new one back through <see cref="SetPins"/>.</summary>
    public event Action<string>? PinToggleRequested;

    /// <summary>Raised with <c>true</c> while a modal system dialog owns the screen,
    /// and <c>false</c> once it closes.</summary>
    /// <remarks>
    /// A system dialog is its own window OUTSIDE the bar's rectangle, so for its
    /// lifetime the controller must suspend tap-outside dismissal and gamepad
    /// navigation. Without this the first touch inside the file picker read as a tap
    /// outside the bar, closed it, and cancelled the whole flow (user-reproduced);
    /// a B press would likewise have driven the bar hidden behind the dialog.
    /// </remarks>
    public event Action<bool>? SystemDialogActive;

    private bool _confirmCloseLauncher;

    /// <summary>Set once this window instance is gone. Post-action feedback delays
    /// outlive the window they started on, and a dismissal raised from a dead window
    /// would close whatever panel is on screen by then.</summary>
    private bool _closed;

    // Guards the Device render that ShowDestination performs, which re-enters it via ConfigureTabs.
    private bool _showingDestination;
    private readonly CancellationTokenSource _deviceLifetime = new();
    private readonly OverlayNavigation _navigation = new();
    private static readonly OverlayFocusMemory FocusMemory = new();
    private IDeviceOverlaySource? _deviceBridge;

    /// <summary>Preview tiles by control, rebuilt with the Glyphs page and empty elsewhere.</summary>
    /// <remarks>
    /// Held so the input test can light a tile without re-rendering the page on every sample. The
    /// tiles are owned by the visual tree; this only points at them, and is cleared whenever the
    /// page that made them is replaced.
    /// </remarks>
    private readonly Dictionary<GlyphControlId, Border> _glyphTiles = [];

    private HashSet<GlyphControlId> _pressedGlyphControls = [];
    private IDisposable? _glyphInputObservation;
    private PerformanceOverlayBridge? _performanceSource;
    private IDisposable? _performanceObservation;

    private Shell.SdFormatManager? _format;
    private FormatTargetEntry? _pendingTarget;
    private readonly AppSwitcherViewModel _switcher;

    /// <summary>The launch fix waiting on the user to pick a game, and the button
    /// whose title reports the outcome.</summary>
    private (LaunchWrapperMode Mode, CardButton Button)? _pendingLaunchFix;

    /// <summary>Set while the peer keyboard owns activation so focus handoff does not
    /// look like a fresh overlay summons and discard the active workflow.</summary>
    internal bool KeyboardOwnsFocus { get; set; }

    /// <summary>One in-place nested page: what it pushes onto the navigation stack, the host it
    /// reveals, the destination panel it hides while it is up, and any state it owns.</summary>
    /// <param name="Page">The navigation page; also the identity of the open sub-view.</param>
    /// <param name="Host">The control revealed while the page is open.</param>
    /// <param name="Parent">The destination panel hidden behind it.</param>
    /// <param name="Destination">The destination that panel belongs to.</param>
    /// <param name="OnLeave">State the page owns, released before the peer keyboard is told
    /// to close so nothing re-reads a value the page has already abandoned.</param>
    private sealed record SubView(
        OverlayPage Page,
        Control Host,
        Control Parent,
        OverlayDestination Destination,
        Action? OnLeave = null);

    private SubView[]? _subViews;

    /// <summary>The nested pages, built once the XAML fields exist. The open one is identified by
    /// <see cref="OverlayNavigation.Page"/> rather than tracked in a parallel flag per page, which
    /// is what let the two disagree.</summary>
    private SubView[] SubViews => _subViews ??=
    [
        new(OverlayPage.SteamStorageFormat, PanelFormat, PanelSteam, OverlayDestination.Steam,
            () =>
            {
                _pendingTarget = null;
                _formatReturnsToCards = false;
            }),
        new(OverlayPage.SteamLibraryTabs, LibraryTabsHost, PanelSteam, OverlayDestination.Steam),
        new(OverlayPage.SteamCardManager, CardManagerHost, PanelSteam, OverlayDestination.Steam),
        new(OverlayPage.SteamArtwork, ArtworkHost, PanelSteam, OverlayDestination.Steam,
            () => ArtworkHost.Close()),
        new(OverlayPage.SteamLaunchConfiguration, LaunchWrapperHost, PanelSteam,
            OverlayDestination.Steam,
            () =>
            {
                _pendingLaunchFix = null;
                // Clears the "Asking Steam…" title left on whichever button opened the picker.
                // A pick re-writes it moments later with the real outcome.
                if (DataContext is OverlayViewModel viewModel)
                {
                    InitializeLaunchFixLabels(viewModel);
                }
            }),
        new(OverlayPage.PowerWakeLocks, WakeLockHost, PanelPower, OverlayDestination.Power),
        new(OverlayPage.DeviceColor, DeviceColorHost, PanelDevice, OverlayDestination.Device,
            RefreshDevicePanel),
    ];

    /// <summary>The nested page currently owning the surface, or null at a destination root.</summary>
    private SubView? ActiveSubView =>
        SubViews.FirstOrDefault(view => view.Page == _navigation.Page);

    /// <summary>Whether any nested page owns the surface. While one does, LB/RB destination
    /// switching is suppressed and B cancels the page rather than closing the overlay.</summary>
    private bool AnySubView => ActiveSubView is not null;

    private void EnterSubView(OverlayPage page)
    {
        SubView view = SubViews.First(candidate => candidate.Page == page);
        if (!_navigation.Push(page, CurrentSemanticFocusKey()))
        {
            return;
        }

        view.Parent.IsVisible = false;
        view.Host.IsVisible = true;
        FocusFirstControl(view.Host);
    }

    private void LeaveSubView(OverlayPage page)
    {
        if (_navigation.Page == page)
        {
            LeaveActiveSubView();
        }
    }

    private void LeaveActiveSubView()
    {
        if (ActiveSubView is not { } view)
        {
            return;
        }

        string? returnFocusKey = _navigation.Pop();
        view.OnLeave?.Invoke();
        // Closes any peer keyboard the page opened; without it the keyboard can outlive its
        // sub-view and keep writing back to a now-hidden field.
        SubViewClosed?.Invoke();
        view.Host.IsVisible = false;
        view.Parent.IsVisible = _navigation.Destination == view.Destination;
        if (view.Parent.IsVisible)
        {
            RestoreRootFocus(returnFocusKey);
        }
    }

    /// <summary>Gives the overlay the shared removable-storage format manager so
    /// its Steam storage page can drive it. Called by the controller right after
    /// construction (the manager outlives the window).</summary>
    /// <param name="format">The controller-owned format manager.</param>
    internal void AttachFormatManager(Shell.SdFormatManager format)
    {
        _format = format;
        PanelFormat.DataContext = format;
    }

    /// <summary>Attaches the semantic coordinator projection used by the optional Device tab.</summary>
    internal void AttachDeviceBridge(IDeviceOverlaySource? bridge)
    {
        if (ReferenceEquals(_deviceBridge, bridge))
        {
            return;
        }

        // Released against the outgoing bridge, before the field moves. Doing it after would leave
        // the old bridge holding a subscription and an observer count nothing can reach any more.
        UpdateGlyphInputObservation(false);
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed -= OnDeviceChanged;
        }
        _deviceBridge = bridge;
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed += OnDeviceChanged;
        }

        UpdateGlyphInputObservation(
            DeviceOverlaySectionPages.SectionFor(_navigation.Page) is DeviceOverlaySection.Glyphs);
        RefreshDevicePanel();
    }

    /// <summary>Attaches the shared performance projection without transferring its lifetime.</summary>
    internal void AttachPerformanceSource(PerformanceOverlayBridge? source)
    {
        if (ReferenceEquals(_performanceSource, source))
        {
            return;
        }

        if (_performanceSource is not null)
        {
            _performanceSource.Changed -= OnPerformanceChanged;
        }
        _performanceObservation?.Dispose();
        _performanceObservation = null;

        _performanceSource = source;
        if (_performanceSource is not null)
        {
            try
            {
                _performanceSource.Changed += OnPerformanceChanged;
                _performanceObservation = _performanceSource.AcquireObservation();
            }
            catch (Exception ex)
            {
                _performanceSource.Changed -= OnPerformanceChanged;
                _performanceSource = null;
                Log.Warn($"Performance overlay observation could not start: {ex.Message}");
            }
        }

        RefreshPerformancePanel();
    }

    /// <summary>Moves focus to Device when integration is enabled; otherwise leaves the current tab.</summary>
    internal void SelectDeviceDestination()
    {
        if (_deviceBridge?.Snapshot().Visible is true)
        {
            SelectDestination(OverlayDestination.Device);
        }
    }

    private void OnDeviceChanged() => Dispatcher.UIThread.Post(RefreshDevicePanel);

    /// <summary>True while focus is on an interactive value control inside the capability list — a
    /// slider, dropdown, toggle or textbox the user is adjusting. A telemetry-driven rebuild while
    /// one is focused would destroy it under the user, so the refresh is skipped until they leave.</summary>
    private bool IsEditingDeviceValue()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is not Control focused)
        {
            return false;
        }

        if (focused is not (Slider or ComboBox or ToggleSwitch or TextBox))
        {
            return false;
        }

        for (Visual? node = focused; node is not null; node = node.GetVisualParent())
        {
            if (ReferenceEquals(node, DeviceCapabilityList))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Redraws the written activation hints as the device's own buttons, where one resolved.
    /// </summary>
    /// <remarks>
    /// Written letters stay in the markup and remain the fallback, so this only ever adds. That
    /// matters on the two machines it will not resolve for — one with no glyph profile, and one
    /// where the input actually reaching WSGM is not the managed handheld's — because a hint showing
    /// a Claw button while the user holds an Xbox pad is worse than the letter it replaced.
    /// </remarks>
    private void RefreshNavigationHints()
    {
        // FaceSouth rather than "A": the glyph vocabulary is positional, so a device whose bottom
        // face button is printed with something else gets the button it actually has.
        HomeAppButton.TrailingGlyph = _deviceBridge?.NavigationHint(GlyphControlId.FaceSouth);
    }

    private void OnPerformanceChanged() => Dispatcher.UIThread.Post(() =>
    {
        RefreshPerformancePanel();
        if (_navigation.IsVisible(OverlayDestination.Device))
        {
            RefreshDevicePanel();
        }
    });

    private void RefreshDevicePanel()
    {
        if (_closed)
        {
            return;
        }

        DeviceOverlaySnapshot snapshot = _deviceBridge?.Snapshot()
            ?? new DeviceOverlaySnapshot(false, "Device integration off", string.Empty, null, []);
        PerformanceOverlaySnapshot? performance = _performanceSource?.Snapshot();
        RefreshNavigationHints();
        ConfigureTabs(snapshot.Visible);
        DeviceStatusTitle.Text = snapshot.Status;
        DeviceStatusDetail.Text = snapshot.Detail;

        // Do not tear the list down while the user is operating a value control on it. Read-only
        // telemetry (fan RPM, temperature) streams several samples a second and each one posts a
        // refresh; rebuilding would destroy the focused slider/dropdown mid-adjust — the pad
        // cannot hold Left/Right across it, and the row's debounced write timer would die with the
        // row before it commits. The next change after the user moves on rebuilds as normal.
        if (IsEditingDeviceValue())
        {
            return;
        }
        string? focusedKey = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is Control focused
            ? focused.Tag as string
            : null;
        DeviceCapabilityList.Children.Clear();

        // The tiles belong to the tree that was just cleared. Dropping the references here, before
        // anything can rebuild them, is what stops the input test writing to detached controls.
        _glyphTiles.Clear();
        if (DeviceOverlaySectionPages.Build(snapshot, performance).Count == 0)
        {
            DeviceCapabilityList.Children.Add(new TextBlock
            {
                Text = "No semantic capabilities are available yet.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(2, 4),
            });
            return;
        }

        DeviceOverlaySection? openSection = DeviceOverlaySectionPages.SectionFor(_navigation.Page);
        string? openPluginSection = _navigation.Page is OverlayPage.DevicePluginSection
            ? _navigation.SectionId
            : null;
        bool onMenu = openSection is null && openPluginSection is null;
        // Section pages are single columns of rows; uncapped they stretch across the
        // whole sheet, which is exactly the old-sidebar-scaled-up look. The root's
        // tile grid wants the full width.
        DeviceCapabilityList.MaxWidth = onMenu ? double.PositiveInfinity : 720;
        DeviceCapabilityList.HorizontalAlignment = onMenu
            ? Avalonia.Layout.HorizontalAlignment.Stretch
            : Avalonia.Layout.HorizontalAlignment.Left;
        DescriptorStatusRow? restoreFocus = openSection is { } section
            ? RenderDeviceSection(snapshot, section, focusedKey)
            : openPluginSection is { } pluginSectionId
                ? RenderDevicePluginSection(snapshot, pluginSectionId, focusedKey)
                : RenderDeviceSectionMenu(snapshot, performance, focusedKey);

        // A Device page that renders nothing is indistinguishable from a device that published
        // nothing, and the difference is the whole diagnosis. Reported on every render, not only
        // the empty ones, because "16 capabilities arrived and 5 rows were drawn" is the line that
        // separates a delivery problem from a rendering one — and an empty page with no line at
        // all cannot even prove the render ran.
        Log.Change(
            "overlay.device.render",
            $"Device page: page={_navigation.Page}, "
                + $"section={openSection?.ToString() ?? openPluginSection ?? "menu"}, "
                + $"rows={DeviceCapabilityList.Children.Count}, "
                + $"capabilities={snapshot.Capabilities.Count}, "
                + $"glyphSelection={snapshot.GlyphSelection is not null}, "
                + $"autoTdp={snapshot.AutoTdp is not null}, "
                + $"controller={snapshot.Controller is not null}, "
                + $"profile={snapshot.Profile is not null}, "
                + $"performanceProfiles={performance?.ProfileRows.Count ?? 0}, "
                + $"recovery={snapshot.Recovery is not null}",
            DeviceCapabilityList.Children.Count == 0 ? "warn " : "info ");

        restoreFocus?.Focus(NavigationMethod.Directional);
        RenderPins();
    }

    /// <summary>
    /// Renders the Device root: one card per section that currently has something in it.
    /// </summary>
    /// <remarks>
    /// A menu rather than one long list. The whole surface is a few rows tall on a handheld, and a
    /// list that needs scrolling is a list a controller cannot cross quickly. Each card carries the
    /// most serious status inside it, so a fault is visible without opening the page.
    /// </remarks>
    private DescriptorStatusRow? RenderDeviceSectionMenu(
        DeviceOverlaySnapshot snapshot,
        PerformanceOverlaySnapshot? performance,
        string? focusedKey)
    {
        DescriptorStatusRow? restoreFocus = null;

        // The per-application profile toggle is the headline of the Device root, the way Steam's own
        // per-game toggle heads the Performance tab: one control, on top of the page, that turns a
        // separate profile for the running application on or off. Its settings live on Power and
        // thermals; this is only the switch. Rendered before the section grid so it reads first.
        if (performance is { Visible: true }
            && performance.ProfileRows.FirstOrDefault(row => string.Equals(
                row.Id,
                DeviceOverlaySectionPages.ApplicationProfileRowId,
                StringComparison.Ordinal)) is { } applicationProfile)
        {
            const string toggleFocusKey = "device.application-profile";
            DescriptorStatusRow toggle = CreatePerformanceRow(applicationProfile, toggleFocusKey);
            toggle.Margin = new Thickness(0, 0, 0, 12);
            DeviceCapabilityList.Children.Add(toggle);
            if (string.Equals(toggleFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = toggle;
            }
        }

        // A grid of tile cards rather than a stretched stack: the sheet is wide, and
        // a full-width row per section read as the old sidebar scaled up.
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (DeviceOverlaySectionEntry entry in DeviceOverlaySectionPages.Build(
            snapshot,
            performance))
        {
            string key = DeviceOverlaySectionPages.FocusKey(entry);
            DescriptorStatusRow row = new();
            row.Classes.Add("tile");
            row.Margin = new Thickness(0, 0, 10, 10);
            row.Apply(new DescriptorRow(
                key,
                entry.Title,
                entry.Description,
                entry.Count.ToString(CultureInfo.InvariantCulture),
                CanInvoke: true,
                entry.Status));
            if (SectionIconFor(entry.Icon) is { } sectionIcon)
            {
                row.IconGeometry = sectionIcon;
            }

            DeviceOverlaySectionEntry captured = entry;
            row.Click += (_, _) =>
            {
                if (captured.PluginSectionId is { } pluginSection)
                {
                    EnterDevicePluginSection(pluginSection);
                }
                else
                {
                    EnterDeviceSection(captured.Section);
                }
            };
            grid.Children.Add(row);
            if (string.Equals(key, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        DeviceCapabilityList.Children.Add(grid);
        return restoreFocus;
    }

    /// <summary>Renders one Device section's rows.</summary>
    /// <summary>Renders one plugin-declared section page: lead rows, then category groups.</summary>
    private DescriptorStatusRow? RenderDevicePluginSection(
        DeviceOverlaySnapshot snapshot,
        string sectionId,
        string? focusedKey)
    {
        DeviceOverlayPluginSection? pluginSection = snapshot.PluginSections
            .FirstOrDefault(candidate => string.Equals(
                candidate.SectionId,
                sectionId,
                StringComparison.Ordinal));
        IReadOnlyList<DeviceOverlayCapability> capabilities =
            DeviceOverlaySectionPages.CapabilitiesInPluginSection(snapshot, sectionId);
        if (pluginSection is null || capabilities.Count == 0)
        {
            // The section vanished with a descriptor generation while its page was open. Saying so
            // beats rendering an empty page that cannot explain itself.
            DeviceCapabilityList.Children.Add(new TextBlock
            {
                Text = "This device section is no longer available.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(2, 4),
            });
            return null;
        }

        DescriptorStatusRow? restoreFocus = null;
        DeviceOverlayCapability? brightness = FindBrightness(snapshot);
        TextBlock sectionHeading = new()
        {
            Text = pluginSection.Title.ToUpperInvariant(),
            Margin = new Thickness(2, 2, 2, 2),
        };
        sectionHeading.Classes.Add("eyebrow");
        DeviceCapabilityList.Children.Add(sectionHeading);

        void AddRows(IEnumerable<DeviceOverlayCapability> rows)
        {
            var flow = new DeviceRowFlow(DeviceCapabilityList);
            foreach (DeviceOverlayCapability capability in rows)
            {
                string key = capability.InstanceId is { Length: > 0 }
                    ? $"{capability.CapabilityId}#{capability.InstanceId}"
                    : capability.CapabilityId;
                if (TryCreateDeviceControl(capability, key) is { } control)
                {
                    flow.Add(control, wide: control is DeviceSliderRow);
                    continue;
                }

                DescriptorStatusRow button = CreateDeviceCapabilityRow(capability, key, brightness);
                flow.Add(button, wide: false);
                if (string.Equals(key, focusedKey, StringComparison.Ordinal))
                {
                    restoreFocus = button;
                }
            }

            flow.Flush();
        }

        AddRows(capabilities.Where(capability => capability.CategoryId is null));
        foreach (DeviceOverlayCategory category in pluginSection.Categories)
        {
            List<DeviceOverlayCapability> rows = capabilities
                .Where(capability => string.Equals(
                    capability.CategoryId,
                    category.Id,
                    StringComparison.Ordinal))
                .ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            TextBlock label = new()
            {
                Text = category.Title.ToUpperInvariant(),
                Margin = new Thickness(2, 8, 2, 2),
            };
            label.Classes.Add("eyebrow");
            DeviceCapabilityList.Children.Add(label);
            AddRows(rows);
        }

        return restoreFocus;
    }

    /// <summary>The firmware brightness capability paired into the color editor, when one exists.</summary>
    private static DeviceOverlayCapability? FindBrightness(DeviceOverlaySnapshot snapshot) =>
        snapshot.Capabilities.FirstOrDefault(capability =>
            capability.Role is CapabilityRole.LightingBrightness);

    /// <summary>WSGM's geometry for a declared section icon, or null for the shared default.</summary>
    private static Avalonia.Media.Geometry? SectionIconFor(SectionIcon icon) => icon switch
    {
        SectionIcon.Power => Icons.Power,
        SectionIcon.Fan => Icons.Snowflake,
        SectionIcon.Battery => Icons.Battery,
        SectionIcon.Lighting => Icons.Palette,
        SectionIcon.Controller => Icons.Grid4,
        SectionIcon.Display => Icons.Monitor,
        SectionIcon.Gauge => Icons.ListLines,
        SectionIcon.Wrench => Icons.Wrench,
        _ => null,
    };

    private DescriptorStatusRow? RenderDeviceSection(
        DeviceOverlaySnapshot snapshot,
        DeviceOverlaySection section,
        string? focusedKey)
    {
        DescriptorStatusRow? restoreFocus = null;
        DeviceOverlayCapability? sectionBrightness = FindBrightness(snapshot);
        TextBlock heading = new()
        {
            Text = DeviceSectionLabel(section),
            Margin = new Thickness(2, 2, 2, 2),
        };
        heading.Classes.Add("eyebrow");
        DeviceCapabilityList.Children.Add(heading);

        var sectionFlow = new DeviceRowFlow(DeviceCapabilityList);
        foreach (DeviceOverlayCapability capability
            in DeviceOverlaySectionPages.CapabilitiesIn(snapshot, section))
        {
            string key = capability.InstanceId is { Length: > 0 }
                ? $"{capability.CapabilityId}#{capability.InstanceId}"
                : capability.CapabilityId;
            if (TryCreateDeviceControl(capability, key) is { } control)
            {
                sectionFlow.Add(control, wide: control is DeviceSliderRow);
                continue;
            }

            DescriptorStatusRow button = CreateDeviceCapabilityRow(
                capability,
                key,
                sectionBrightness);
            sectionFlow.Add(button, wide: false);
            if (string.Equals(key, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = button;
            }
        }

        sectionFlow.Flush();

        // AutoTDP moves the power limit rather than being one, so it sits with the limit it moves
        // instead of arriving through the capability list.
        if (section is DeviceOverlaySection.PowerAndThermals && snapshot.AutoTdp is { } autoTdp)
        {
            const string autoTdpFocusKey = "device.auto-tdp";
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                autoTdpFocusKey,
                autoTdp.Title,
                autoTdp.Description,
                autoTdp.TrailingText,
                autoTdp.CanInvoke,
                autoTdp.Status));
            row.Click += (_, _) => InvokeAutoTdpToggle();
            DeviceCapabilityList.Children.Add(row);
            if (string.Equals(autoTdpFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // The selected hardware profile is stored configuration rather than a device capability, so
        // it is a direct row for the same reason as the others on this surface. It sits with power
        // and thermals now that the per-application profile is the toggle on the Device root.
        if (section is DeviceOverlaySection.PowerAndThermals && snapshot.Profile is { } profile)
        {
            const string profileFocusKey = "device.hardware-profile";
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                profileFocusKey,
                profile.Title,
                profile.Description,
                profile.TrailingText,
                profile.CanInvoke,
                profile.Status));
            row.Click += (_, _) => InvokeHardwareProfileCycle();
            DeviceCapabilityList.Children.Add(row);
            if (string.Equals(profileFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // The authored fan profile, below the plugin's hardware profile. Two rows on one page
        // because they are genuinely different things: the hardware profile comes from the plugin
        // and switches its own values, while this chooses between curves the user drew in Settings.
        if (section is DeviceOverlaySection.PowerAndThermals && snapshot.AuthoredProfile is { } authored)
        {
            const string authoredFocusKey = "device.authored-profile";
            DescriptorStatusRow authoredRow = new();
            authoredRow.Apply(new DescriptorRow(
                authoredFocusKey,
                authored.Title,
                authored.Description,
                authored.TrailingText,
                authored.CanInvoke,
                authored.Status));
            authoredRow.Click += (_, _) => InvokeAuthoredProfileCycle();
            DeviceCapabilityList.Children.Add(authoredRow);
            if (string.Equals(authoredFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = authoredRow;
            }
        }

        // The controller target is WSGM's own setting, not a plugin capability, so it is placed on
        // its page directly for the same reason AutoTDP and glyph selection are.
        if (section is DeviceOverlaySection.ControllerAndMotion
            && snapshot.Controller is { } controller)
        {
            const string controllerFocusKey = "device.controller-target";
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                controllerFocusKey,
                controller.Title,
                controller.Description,
                controller.TrailingText,
                controller.CanInvoke,
                controller.Status));
            row.Click += (_, _) => InvokeControllerTargetCycle();
            DeviceCapabilityList.Children.Add(row);
            if (string.Equals(controllerFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // Recovery is an action on the device cycle itself rather than on the device, so it is not a
        // capability either. It appears only while there is something to recover.
        if (section is DeviceOverlaySection.Diagnostics && snapshot.Recovery is { } recovery)
        {
            const string recoveryFocusKey = "device.retry";
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                recoveryFocusKey,
                recovery.Title,
                recovery.Description,
                recovery.TrailingText,
                true,
                recovery.Status));
            row.Click += (_, _) => InvokeDeviceCycleRetry();
            DeviceCapabilityList.Children.Add(row);
            if (string.Equals(recoveryFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // Glyph selection is WSGM's own control rather than a plugin capability, so it is placed
        // here explicitly rather than arriving through the capability list.
        if (section is DeviceOverlaySection.Glyphs && snapshot.GlyphSelection is { } glyphSelection)
        {
            string glyphFocusKey = glyphSelection.Id;
            DescriptorStatusRow button = CreateGlyphSelectionRow(glyphSelection);
            DeviceCapabilityList.Children.Add(button);
            if (string.Equals(glyphFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = button;
            }
        }

        // After the selection row it is the result of, so changing the selection and seeing what it
        // produced reads top to bottom.
        if (section is DeviceOverlaySection.Glyphs && snapshot.GlyphPreview is { } preview)
        {
            RenderGlyphPreview(preview);
        }

        return restoreFocus;
    }

    /// <summary>
    /// Draws the plugin's own glyphs, and lights the one being pressed.
    /// </summary>
    /// <param name="preview">The resolved preview.</param>
    /// <remarks>
    /// The preview answers the two questions a glyph profile can fail at, and it answers them with
    /// the same picture: whether the artwork resolves at all, and whether pressing a control reaches
    /// WSGM as the control the artwork claims. Neither is answerable from a list of names.
    /// <para>
    /// The tiles are not focusable. This is something to look at while pressing buttons on the
    /// device, so making it a focus stop would put a wall of stops between the selection row above
    /// it and whatever follows, for controls that do nothing when activated.
    /// </para>
    /// </remarks>
    private void RenderGlyphPreview(DeviceOverlayGlyphPreview preview)
    {
        TextBlock caption = new()
        {
            Text = $"{preview.ProfileName} · {preview.Detail}",
            Margin = new Thickness(2, 6, 2, 2),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        caption.Classes.Add("caption");
        DeviceCapabilityList.Children.Add(caption);

        TextBlock hint = new()
        {
            Text = preview.InputTestAvailable
                ? "Press a control on the device to light it here."
                : "Input test unavailable · WSGM is not reading this device's controls.",
            Margin = new Thickness(2, 0, 2, 4),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Classes.Add("caption");
        DeviceCapabilityList.Children.Add(hint);

        WrapPanel tiles = new() { Margin = new Thickness(2, 0, 2, 4) };
        foreach (DeviceOverlayGlyphPreviewItem item in preview.Items)
        {
            Border tile = new()
            {
                Width = 64,
                Height = 72,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
            };
            tile.Classes.Add("glyph-tile");
            StackPanel stack = new() { Spacing = 2 };
            PhysicalGlyphImage image = new()
            {
                Plan = item.Plan,
                Width = 40,
                Height = 40,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            stack.Children.Add(image);
            TextBlock label = new()
            {
                Text = item.Label,
                FontSize = 10,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            stack.Children.Add(label);
            tile.Child = stack;
            tiles.Children.Add(tile);
            _glyphTiles[item.Control] = tile;
        }

        DeviceCapabilityList.Children.Add(tiles);
        ApplyGlyphInputTest();
    }

    /// <summary>Applies the last physical sample to the preview tiles.</summary>
    /// <remarks>
    /// Class-based rather than by setting a brush, so the lit appearance lives in the theme with
    /// every other visual state instead of as a literal colour here.
    /// </remarks>
    private void ApplyGlyphInputTest()
    {
        foreach ((GlyphControlId control, Border tile) in _glyphTiles)
        {
            tile.Classes.Set("pressed", _pressedGlyphControls.Contains(control));
        }
    }

    private void InvokeAutoTdpToggle() => _ = ToggleAutoTdpAsync();

    private void InvokeControllerTargetCycle() => _ = RunDeviceCommandAsync(
        "Controller target change",
        (bridge, token) => bridge.CycleControllerTargetAsync(token));

    private void InvokeDeviceCycleRetry() => _ = RunDeviceCommandAsync(
        "Device integration retry",
        (bridge, token) => bridge.RetryDeviceCycleAsync(token));

    private void InvokeHardwareProfileCycle() => _ = RunDeviceCommandAsync(
        "Hardware profile change",
        (bridge, token) => bridge.CycleHardwareProfileAsync(token));

    private void InvokeAuthoredProfileCycle() => _ = RunDeviceCommandAsync(
        "Fan profile change",
        (bridge, token) => bridge.CycleAuthoredProfileAsync(token));


    /// <summary>Runs one direct Device-surface command with the shared cancellation and logging.</summary>
    /// <param name="description">What the command is, for the log line if it fails.</param>
    /// <param name="command">The command to run against the current source.</param>
    /// <returns>A task completing once the command has run or failed.</returns>
    /// <remarks>
    /// These commands are WSGM's own rather than plugin capabilities, so they do not go through the
    /// capability invoke path. They still need its lifetime and failure handling: a device command
    /// that throws must never take the overlay with it, and one that is cancelled by the overlay
    /// closing is not a failure worth logging.
    /// </remarks>
    private async Task RunDeviceCommandAsync(
        string description,
        Func<IDeviceOverlaySource, CancellationToken, Task> command)
    {
        IDeviceOverlaySource? bridge = _deviceBridge;
        if (bridge is null || _closed)
        {
            return;
        }

        try
        {
            await command(bridge, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"{description} failed: {ex.Message}");
        }
    }

    private async Task ToggleAutoTdpAsync()
    {
        IDeviceOverlaySource? bridge = _deviceBridge;
        if (bridge is null || _closed)
        {
            return;
        }

        try
        {
            await bridge.ToggleAutoTdpAsync(_deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"AutoTDP switch failed: {ex.Message}");
        }
    }

    /// <summary>Opens one plugin-declared section as its own page.</summary>
    private void EnterDevicePluginSection(string sectionId)
    {
        if (!_navigation.Push(
            OverlayPage.DevicePluginSection,
            CurrentSemanticFocusKey(),
            sectionId))
        {
            return;
        }

        UpdateGlyphInputObservation(false);
        RefreshDevicePanel();
        RefreshPerformancePanel();
        FocusFirstControl(DeviceCapabilityList);
    }

    private void EnterDeviceSection(DeviceOverlaySection section)
    {
        if (!_navigation.Push(
            DeviceOverlaySectionPages.PageFor(section),
            CurrentSemanticFocusKey()))
        {
            return;
        }

        // The sample stream fires at input rate, so it is leased only for the one page that draws
        // it and released the moment that page is left.
        UpdateGlyphInputObservation(section is DeviceOverlaySection.Glyphs);
        RefreshDevicePanel();

        // The shared performance rows belong to one Device page, so entering or leaving any page
        // changes whether they are on screen.
        RefreshPerformancePanel();
        FocusFirstControl(DeviceCapabilityList);
    }

    private void LeaveDeviceSection(DeviceOverlaySection section)
    {
        UpdateGlyphInputObservation(false);
        string? returnFocusKey = _navigation.Pop()
            ?? DeviceOverlaySectionPages.FocusKey(section);
        RefreshDevicePanel();
        RefreshPerformancePanel();
        RestoreRootFocus(returnFocusKey);
    }

    /// <summary>Starts or stops the glyph input test's sample observation.</summary>
    /// <param name="observe">Whether the page that draws the samples is showing.</param>
    /// <remarks>
    /// Idempotent in both directions, because the page can be entered and left by several paths —
    /// the section card, Back, a destination change, and the overlay closing — and each of them
    /// calls this without knowing what the others did.
    /// </remarks>
    private void UpdateGlyphInputObservation(bool observe)
    {
        if (observe == (_glyphInputObservation is not null))
        {
            return;
        }

        if (!observe)
        {
            if (_deviceBridge is not null)
            {
                _deviceBridge.PhysicalSampleReceived -= OnPhysicalGlyphSample;
            }

            _glyphInputObservation?.Dispose();
            _glyphInputObservation = null;
            _pressedGlyphControls = [];
            return;
        }

        IDeviceOverlaySource? bridge = _deviceBridge;
        if (bridge is null || _closed)
        {
            return;
        }

        bridge.PhysicalSampleReceived += OnPhysicalGlyphSample;
        _glyphInputObservation = bridge.ObservePhysicalSamples();
    }

    /// <summary>Marshals one physical sample onto the UI thread and lights what it presses.</summary>
    /// <param name="sample">The unfiltered sample the plugin reported.</param>
    /// <remarks>
    /// The set is compared before posting, so a controller sitting still — which is most samples —
    /// costs one set comparison on the sampling thread and nothing on the UI thread. Without that,
    /// a 250 Hz stream would post 250 dispatcher items a second to change nothing.
    /// </remarks>
    private void OnPhysicalGlyphSample(CanonicalControllerSample sample)
    {
        HashSet<GlyphControlId> pressed = GlyphInputTestMap.Pressed(sample);
        if (pressed.SetEquals(_pressedGlyphControls))
        {
            return;
        }

        _pressedGlyphControls = pressed;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed)
            {
                return;
            }

            ApplyGlyphInputTest();
        });
    }

    /// <summary>True when a capability should render as a slider: a writable integer with a real
    /// declared range. Colour keeps its editor; everything else stays a row.</summary>
    private static bool RendersAsSlider(DeviceOverlayCapability capability) =>
        capability.ValueKind is CapabilityValueKind.Integer
        && capability.Writable
        && capability.Minimum is { } min
        && capability.Maximum is { } max
        && max > min;

    /// <summary>Builds the proper control for a writable capability — slider, toggle, dropdown or
    /// textbox — or null when it has no dedicated control and should render as a plain row (an
    /// action, a colour swatch, or a read-only value). Sets the row's focus target so gamepad
    /// focus restore lands on the interactive control.</summary>
    private Control? TryCreateDeviceControl(DeviceOverlayCapability capability, string key)
    {
        if (RendersAsSlider(capability))
        {
            return CreateDeviceSliderRow(capability, key);
        }

        if (!capability.Writable)
        {
            return null;
        }

        switch (capability.ValueKind)
        {
            case CapabilityValueKind.Boolean:
            {
                (Border row, _) = DeviceControlRows.Toggle(
                    key,
                    capability.Title,
                    capability.Description,
                    capability.CurrentValue?.BooleanValue ?? false,
                    capability.CanInvoke,
                    value => WriteDeviceValue(capability, new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Boolean,
                        BooleanValue = value,
                    }));
                return row;
            }

            case CapabilityValueKind.Choice when capability.Choices.Count > 0:
            {
                (Border row, _) = DeviceControlRows.Choice(
                    key,
                    capability.Title,
                    capability.Description,
                    capability.Choices,
                    capability.CurrentValue?.ChoiceValue,
                    capability.CanInvoke,
                    value => WriteDeviceValue(capability, new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Choice,
                        ChoiceValue = value,
                    }));
                return row;
            }

            case CapabilityValueKind.Text:
            {
                (Border row, _) = DeviceControlRows.Text(
                    key,
                    capability.Title,
                    capability.Description,
                    capability.CurrentValue?.TextValue,
                    capability.MaximumLength,
                    capability.CanInvoke,
                    value => WriteDeviceValue(capability, new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Text,
                        TextValue = value,
                    }));
                return row;
            }

            default:
                return null;
        }
    }

    /// <summary>Lays a group's rows into the wide sheet while respecting the pad: a slider (or any
    /// control that keeps Left/Right for its own value) spans the full width so Up/Down alone moves
    /// between rows, and the remaining compact rows — toggles, dropdowns, buttons, readings — pair
    /// two to a line. Order is preserved, so a wide row flushes the current pair before it.</summary>
    private sealed class DeviceRowFlow(StackPanel host)
    {
        private const double Gutter = 12;
        private Grid? _pair;

        public void Add(Control row, bool wide)
        {
            if (wide)
            {
                Flush();
                host.Children.Add(row);
                return;
            }

            if (_pair is null)
            {
                _pair = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                    ColumnSpacing = Gutter,
                };
                host.Children.Add(_pair);
            }

            Grid.SetColumn(row, _pair.Children.Count);
            _pair.Children.Add(row);
            if (_pair.Children.Count == 2)
            {
                _pair = null;
            }
        }

        public void Flush() => _pair = null;
    }

    private void WriteDeviceValue(DeviceOverlayCapability capability, CapabilityValue value)
    {
        IDeviceOverlaySource? bridge = _deviceBridge;
        if (bridge is null || _closed)
        {
            return;
        }

        _ = CommitDeviceValueAsync(bridge, capability with { NextValue = value });
    }

    /// <summary>Builds the labelled slider for an integer-range capability and wires its debounced
    /// commit to the device write path — the same <c>InvokeAsync(with NextValue)</c> the colour
    /// editor uses.</summary>
    private DeviceSliderRow CreateDeviceSliderRow(DeviceOverlayCapability capability, string key)
    {
        int min = capability.Minimum!.Value;
        int max = capability.Maximum!.Value;
        int current = capability.CurrentValue?.IntegerValue ?? min;
        var row = new DeviceSliderRow(
            key,
            capability.Title,
            capability.Description,
            min,
            max,
            capability.Step ?? 1,
            capability.Unit,
            current,
            capability.CanInvoke,
            value =>
            {
                IDeviceOverlaySource? bridge = _deviceBridge;
                if (bridge is null || _closed)
                {
                    return;
                }

                _ = CommitDeviceValueAsync(
                    bridge,
                    capability with
                    {
                        NextValue = new CapabilityValue
                        {
                            Kind = CapabilityValueKind.Integer,
                            IntegerValue = value,
                        },
                    });
            });
        return row;
    }

    private async System.Threading.Tasks.Task CommitDeviceValueAsync(
        IDeviceOverlaySource bridge,
        DeviceOverlayCapability capability)
    {
        try
        {
            await bridge.InvokeAsync(capability, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Device value write failed: {capability.CapabilityId}, {ex.Message}");
        }
    }

    private DescriptorStatusRow CreateDeviceCapabilityRow(
        DeviceOverlayCapability capability,
        string key,
        DeviceOverlayCapability? brightness = null)
    {
        DescriptorStatusRow button = new();
        button.Apply(new DescriptorRow(
            key,
            capability.Title,
            capability.Description,
            capability.TrailingText,
            capability.CanInvoke,
            capability.Status));
        if (capability.CurrentValue is { Kind: CapabilityValueKind.Color, ColorValue: { } packedColor })
        {
            // The row wears its current color: swatch instead of icon (mock detail).
            button.IconGeometry = null;
            button.SwatchBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(
                (byte)((packedColor >> 16) & 0xFF),
                (byte)((packedColor >> 8) & 0xFF),
                (byte)(packedColor & 0xFF)));
        }

        button.Click += async (_, _) =>
        {
            IDeviceOverlaySource? bridge = _deviceBridge;
            if (bridge is null || _closed)
            {
                return;
            }

            if (capability.CurrentValue is
                { Kind: CapabilityValueKind.Color, ColorValue: not null })
            {
                DeviceColorHost.Open(bridge, capability, brightness);
                EnterSubView(OverlayPage.DeviceColor);
                return;
            }

            button.IsEnabled = false;
            try
            {
                await bridge.InvokeAsync(capability, _deviceLifetime.Token);
            }
            catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Device overlay command failed: {capability.CapabilityId}, {ex.Message}");
            }
            finally
            {
                if (!_closed)
                {
                    button.IsEnabled = capability.CanInvoke;
                }
            }
        };
        return button;
    }

    private DescriptorStatusRow CreateGlyphSelectionRow(DescriptorRow glyphSelection)
    {
        DescriptorStatusRow button = new();
        button.Apply(glyphSelection);
        button.Click += async (_, _) =>
        {
            IDeviceOverlaySource? bridge = _deviceBridge;
            if (bridge is null || _closed)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                await bridge.CyclePhysicalGlyphSelectionAsync(_deviceLifetime.Token);
            }
            catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Physical glyph selection command failed: {ex.Message}");
            }
            finally
            {
                if (!_closed)
                {
                    button.IsEnabled = glyphSelection.CanInvoke;
                }
            }
        };
        return button;
    }

    private void RefreshPerformancePanel()
    {
        if (_closed)
        {
            return;
        }

        PlacePerformanceSection(_navigation.IsVisible(OverlayDestination.Device));
        PerformanceOverlaySnapshot? snapshot = _performanceSource?.Snapshot();
        PerformanceSection.IsVisible = snapshot?.Visible is true && PerformanceBelongsOnCurrentPage();
        PerformanceRows.Children.Clear();
        if (snapshot is not { Visible: true })
        {
            PerformanceStatus.Text = string.Empty;
            return;
        }

        PerformanceStatus.Text = snapshot.Status;
        string? focusedKey = CurrentSemanticFocusKey();
        DescriptorStatusRow? restoreFocus = null;
        // On Device the per-application enable toggle is promoted to the headline toggle on the root,
        // so the Power and thermals rows are the detail (detected application, active layer, reset)
        // plus the shared frame-limit and overlay rows. On System there is no Device root to host the
        // toggle, so it stays inline with the rest.
        IEnumerable<DescriptorRow> descriptors = _navigation.IsVisible(OverlayDestination.Device)
            ? snapshot.ProfileRows
                .Where(row => !string.Equals(
                    row.Id,
                    DeviceOverlaySectionPages.ApplicationProfileRowId,
                    StringComparison.Ordinal))
                .Concat(snapshot.Rows)
            : snapshot.ProfileRows.Concat(snapshot.Rows);
        foreach (DescriptorRow descriptor in descriptors)
        {
            DescriptorStatusRow button = CreatePerformanceRow(
                descriptor,
                $"performance.{descriptor.Id}");
            PerformanceRows.Children.Add(button);
            if (string.Equals(button.Tag as string, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = button;
            }
        }
        restoreFocus?.Focus(NavigationMethod.Directional);
    }

    private DescriptorStatusRow CreatePerformanceRow(DescriptorRow descriptor, string focusKey)
    {
        DescriptorStatusRow button = new();
        button.Apply(descriptor with { Id = focusKey });
        button.Click += async (_, _) =>
        {
            PerformanceOverlayBridge? source = _performanceSource;
            if (source is null || _closed || !descriptor.CanInvoke)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                await source.InvokeAsync(descriptor, _deviceLifetime.Token);
            }
            catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Performance overlay command failed: {descriptor.Id}, {ex.Message}");
            }
            finally
            {
                if (!_closed)
                {
                    button.IsEnabled = descriptor.CanInvoke;
                }
            }
        };
        return button;
    }

    /// <summary>Puts the shared performance rows where the user will look for them.</summary>
    /// <param name="deviceVisible">Whether the Device destination exists in this session.</param>
    /// <remarks>
    /// With no Device destination the rows live on System, which is where they have always been.
    /// With one, they belong on Device → Power and thermals, beside the power limit and AutoTDP:
    /// a frame limit and a power limit are one decision, and splitting them across two destinations
    /// makes the user reason about them separately.
    /// <para>
    /// The section is parented to Device but rendered only while that page is open, because Device
    /// is a menu of pages now. Leaving it parented and visible put it above the section cards on
    /// the root and kept it on screen inside every unrelated sub-page.
    /// </para>
    /// </remarks>
    private void PlacePerformanceSection(bool deviceVisible)
    {
        StackPanel target = deviceVisible ? PanelDevice : SystemPrimaryColumn;
        int targetIndex = deviceVisible ? 2 : 3;
        if (!target.Children.Contains(PerformanceSection))
        {
            PanelDevice.Children.Remove(PerformanceSection);
            SystemPrimaryColumn.Children.Remove(PerformanceSection);
            target.Children.Insert(Math.Min(targetIndex, target.Children.Count), PerformanceSection);
        }
    }

    /// <summary>Whether the performance rows belong on the page currently showing.</summary>
    /// <remarks>
    /// Always, when they live on System. On Device they belong to one page, so that everything on
    /// Power and thermals is what moves the same thing and nothing else carries them.
    /// </remarks>
    private bool PerformanceBelongsOnCurrentPage() =>
        !_navigation.IsVisible(OverlayDestination.Device)
        || DeviceOverlaySectionPages.SectionFor(_navigation.Page)
            is DeviceOverlaySection.PowerAndThermals;

    private static string DeviceSectionLabel(DeviceOverlaySection section) => section switch
    {
        DeviceOverlaySection.Overview => "OVERVIEW",
        DeviceOverlaySection.Profiles => "PROFILES",
        DeviceOverlaySection.PowerAndThermals => "POWER AND THERMALS",
        DeviceOverlaySection.ControllerAndMotion => "CONTROLLER AND MOTION",
        DeviceOverlaySection.Oem => "OEM BUTTONS",
        DeviceOverlaySection.LightingAndFeatures => "LIGHTING AND FEATURES",
        DeviceOverlaySection.Glyphs => "GLYPHS",
        DeviceOverlaySection.Diagnostics => "DIAGNOSTICS AND RECOVERY",
        _ => "DEVICE",
    };

    private void ConfigureTabs(bool showDevice)
    {
        if (!showDevice)
        {
            // A coordinator can retract Device while the Glyphs page is still selected. No tab
            // selection event is raised for that removal, so release the high-rate sample observer
            // here before the page and its tiles disappear.
            UpdateGlyphInputObservation(false);
        }

        OverlayDestination previous = _navigation.Destination;
        bool visibilityChanged = _navigation.SetDeviceVisible(showDevice);
        if (!visibilityChanged && Tabs.Tabs is not null)
        {
            return;
        }

        if (previous == OverlayDestination.Device && !showDevice)
        {
            RememberDestinationState(previous);
            _lastDestination = OverlayDestination.Home;
        }

        PlacePerformanceSection(showDevice);

        Tabs.Tabs = _navigation.VisibleDestinations.Select(CreateDestinationTab).ToList();
        int selectedIndex = DestinationIndex(_navigation.Destination);
        // Rebuilding a dynamic strip can change the meaning of an unchanged numeric
        // index (System 2 becomes Device 2). Force one descriptor-based selection.
        Tabs.SelectedIndex = -1;
        Tabs.SelectedIndex = selectedIndex;
        ShowDestination(_navigation.Destination, restoreFocus: false);
    }

    // Labels are uppercased for the sheet's tracked strip; DestinationLabel stays the
    // sentence-case name everything else (the eyebrow uppercases itself) uses.
    private static TabStripItem CreateDestinationTab(OverlayDestination destination) => destination switch
    {
        OverlayDestination.QuickAccess => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), Icons.Panel, (int)destination),
        OverlayDestination.Home => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), Icons.Play, (int)destination),
        OverlayDestination.Steam => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), Icons.SteamLike, (int)destination),
        OverlayDestination.Device => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), Icons.Gear, (int)destination),
        OverlayDestination.System => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), Icons.Wrench, (int)destination),
        OverlayDestination.Power => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), Icons.Power, (int)destination),
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };

    /// <summary>The user-facing name of a destination — the strip label and the header eyebrow.</summary>
    internal static string DestinationLabel(OverlayDestination destination) => destination switch
    {
        OverlayDestination.QuickAccess => "Quick access",
        OverlayDestination.Home => "Session",
        OverlayDestination.Steam => "Steam",
        OverlayDestination.Device => "Device",
        OverlayDestination.System => "Tools",
        OverlayDestination.Power => "Power",
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };

    private int DestinationIndex(OverlayDestination destination)
    {
        IReadOnlyList<TabStripItem>? tabs = Tabs.Tabs;
        if (tabs is null)
        {
            return 0;
        }
        for (int i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Tag == (int)destination)
            {
                return i;
            }
        }
        return 0;
    }

    // The library name the confirm step will format with. Held here rather than in a
    // TextBox: the row is press-to-edit (see the XAML), matching the tab editor and
    // card rename, and the peer keyboard window owns the typing.
    private string _formatName = "";

    /// <summary>Shows the name on its row so the value is visible without focusing
    /// anything, the way every other name row in the panel reads.</summary>
    private void SetFormatName(string value)
    {
        _formatName = value ?? "";
        FormatNameButton.Description = _formatName.Length > 0 ? _formatName : "(required)";
    }

    // Controller text entry for the library name goes through the peer keyboard
    // window (KeyboardService), like every other game-mode text field.
    private void OnFormatEditName(object? sender, RoutedEventArgs e)
    {
        if (!Core.KeyboardService.Request("Name (volume and Steam library)",
                _formatName, 32, SetFormatName))
        {
            // No keyboard window means no way to type on a controller; say so instead
            // of leaving a row that silently does nothing when pressed.
            Core.Log.Warn("Format: no on-screen keyboard available for the library name.");
        }
    }

    /// <summary>The control gamepad navigation should land on when the panel opens
    /// or when focus tracking is lost: the active destination's first row — HomeAppButton
    /// is invisible on other destinations and focusing it would fall through to
    /// the header close button.</summary>
    internal InputElement DefaultFocusTarget
    {
        get
        {
            // Nested pages retain focus ownership while one is open.
            if (ActiveSubView is { } nested)
            {
                foreach (var visual in nested.Host.GetVisualDescendants())
                {
                    if (visual is Button { Focusable: true, IsEffectivelyEnabled: true } b
                        && b.IsEffectivelyVisible)
                    {
                        return b;
                    }
                }
            }
            foreach (var visual in DestinationPanel().GetVisualDescendants())
            {
                if (visual is Button { Focusable: true, IsEffectivelyEnabled: true } button
                    && button.IsEffectivelyVisible)
                {
                    return button;
                }
            }
            // An empty Quick access root has no row: land on the first tab button so LB/RB
            // and the D-pad still lead somewhere visible.
            return FirstFocusable(Tabs) ?? HomeAppButton;
        }
    }

    // The destination the user last selected, restored on the next open. Static because
    // the overlay window is recreated per open; deliberately not persisted to config.
    private static OverlayDestination _lastDestination = OverlayDestination.QuickAccess;

    private readonly double _uiScale;
    private readonly PixelPoint? _preferredScreenPoint;

    /// <summary>The factor RootScale currently applies (1.0 = no transform). The
    /// sheet's inner layout happens in pre-transform units, so every budget derived
    /// from the window's width has to divide by this first.</summary>
    private double _contentScale = 1.0;

    /// <summary>Creates the sheet bound to the supplied state.</summary>
    /// <param name="viewModel">The state that drives labels, warnings and the rows.</param>
    /// <param name="switcher">The Open apps chips and tray icons (reconciled in place by the controller).</param>
    /// <param name="status">The live clock/battery/radio/audio status the header pills bind.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (e.g. 1.5
    /// for a 150% desktop; see DisplayScale.GetUiScalePercent).</param>
    /// <param name="preferredScreenPoint">A physical point in the foreground window that summoned
    /// the sheet. Null falls back to Avalonia's current window or primary-screen selection.</param>
    public OverlayWindow(
        OverlayViewModel viewModel,
        AppSwitcherViewModel switcher,
        SystemStatus status,
        double uiScale = 1.0,
        PixelPoint? preferredScreenPoint = null)
    {
        _uiScale = uiScale;
        _preferredScreenPoint = preferredScreenPoint;
        _switcher = switcher;
        InitializeComponent();
        DataContext = viewModel;
        // Two subtrees bind different objects than the window (compiled bindings:
        // x:DataType on the TrayScroller / AppsStrip and StatusZone subtrees).
        TrayScroller.DataContext = switcher;
        AppsStrip.DataContext = switcher;
        StatusZone.DataContext = status;
        IndexPinnableRows();

        // Controller navigation moves focus with InputElement.Focus(Directional),
        // which does NOT raise RequestBringIntoView on its own — a chip scrolled
        // out of its strip would take focus invisibly. Ask for it explicitly:
        // Control.BringIntoView() raises RequestBringIntoViewEvent, which the
        // ScrollViewer's presenter handles by scrolling. Arrow keys are safe to
        // leave to the ScrollViewer's own handler because GamepadNavigation marks
        // them handled from a TUNNEL handler on the window.
        TileScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        TrayScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        // Budget the tray against the XAML's declared width right away, so the
        // strip is bounded even on the path where DockToTopEdge bails out (no
        // primary screen); the dock recomputes it against the real display width.
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(Width, _contentScale);
        // Touch and mouse routes to pinning: a hold on a row, or a right click.
        AddHandler(InputElement.HoldingEvent, OnHolding, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnPointerReleasedForPin, RoutingStrategies.Bubble);

        ConfigureTabs(showDevice: false);
        Tabs.SelectionChanged += OnTabSelectionChanged;
        // The panel reopens on the destination the user last had selected (static: the
        // window is recreated per open). Activated covers both the fresh open and a
        // re-summon of a still-open panel. Any nested page is torn down with it.
        Activated += OnActivated;

        LibraryTabsHost.CloseRequested += LeaveLibraryTabsSubView;
        CardManagerHost.CloseRequested += LeaveCardManagerSubView;
        CardManagerHost.FormatRequested += OnFormatFromCardManager;
        ArtworkHost.CloseRequested += LeaveArtworkSubView;
        LaunchWrapperHost.CloseRequested += LeaveLaunchWrapperSubView;
        LaunchWrapperHost.Picked += OnLaunchFixGamePicked;
        LaunchWrapperHost.CustomPicked += OnCustomLaunchGamePicked;
        WakeLockHost.CloseRequested += LeaveWakeLockSubView;
        DeviceColorHost.CloseRequested += LeaveDeviceColorSubView;
        InitializeLaunchFixLabels(viewModel);

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += OnClosed;

        // The overlay takes focus Game-Bar-style: the game stops receiving input
        // while the panel is open. Viable because the Steam Input lease keeps the pad
        // readable even with a non-game window focused.
        //
        // Touch pass-through defense: Avalonia never marks touch raw events
        // handled, so WM_POINTER falls to DefWindowProc, which PROMOTES a tap into
        // a synthesized mouse click delivered AFTER the tap's dispatch. The
        // synthesized-message eater in WndProcHook consumes it — as long as this
        // window still exists when it arrives, which is why OverlayController
        // defers Close() by a beat. (The clean fix — consuming the raw touch
        // event — needs Avalonia's [PrivateApi] InputManager, which is stripped
        // from the published reference assemblies.)
        Win32Properties.AddWndProcHookCallback(
            this,
            Interop.NativeMethods.SwallowTouchSynthesizedMouse);
        Win32Properties.AddWndProcHookCallback(this, DeclineMouseActivationForPanels);
    }

    /// <summary>True while a status panel hangs from the header. A mouse click on the sheet then
    /// reaches its control without activating the sheet, so the click Windows synthesizes from
    /// the tap that opened the panel cannot raise the sheet over it. Set by
    /// <c>OverlayController.SyncSheetMouseActivation</c>; see its remarks for the mechanism.</summary>
    internal bool SuppressMouseActivation { get; set; }

    private nint DeclineMouseActivationForPanels(
        nint hWnd,
        uint msg,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (msg == Interop.NativeMethods.WmMouseActivate && SuppressMouseActivation)
        {
            handled = true;
            return Interop.NativeMethods.MaNoActivate;
        }

        return nint.Zero;
    }


    /// <summary>When set before the first show, the window primes the process-global render
    /// backend off-screen and then closes: <see cref="OnOpened"/> skips docking, focus and the
    /// CEF tab sync so nothing user-visible or Steam-touching happens during the warm pass.</summary>
    internal bool WarmingUp { get; set; }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (WarmingUp)
        {
            return;
        }

        DockToTopEdge();
        SelectDestination(_lastDestination);
        RestoreDestinationState(focus: true);
        MaybeAutoSyncTabs();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (KeyboardOwnsFocus)
        {
            return;
        }

        LeaveAllNestedPages();
        SelectDestination(_lastDestination);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        RememberDestinationState(_navigation.Destination);

        // Before _closed, because releasing reads the bridge, and after it the guard inside would
        // skip the release and leave the subscription attached to a dead window.
        UpdateGlyphInputObservation(false);
        _closed = true;
        _deviceLifetime.Cancel();
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed -= OnDeviceChanged;
        }
        if (_performanceSource is not null)
        {
            _performanceSource.Changed -= OnPerformanceChanged;
        }
        _performanceObservation?.Dispose();
        _performanceObservation = null;

        // These page controls are window-owned. Detach every cross-control callback and
        // invalidate asynchronous artwork loads at the same lifetime boundary.
        Tabs.SelectionChanged -= OnTabSelectionChanged;
        LibraryTabsHost.CloseRequested -= LeaveLibraryTabsSubView;
        CardManagerHost.CloseRequested -= LeaveCardManagerSubView;
        CardManagerHost.FormatRequested -= OnFormatFromCardManager;
        ArtworkHost.CloseRequested -= LeaveArtworkSubView;
        ArtworkHost.Close();
        LaunchWrapperHost.CloseRequested -= LeaveLaunchWrapperSubView;
        LaunchWrapperHost.Picked -= OnLaunchFixGamePicked;
        LaunchWrapperHost.CustomPicked -= OnCustomLaunchGamePicked;
        WakeLockHost.CloseRequested -= LeaveWakeLockSubView;
        DeviceColorHost.CloseRequested -= LeaveDeviceColorSubView;
        KeyDown -= OnKeyDown;
        Opened -= OnOpened;
        Activated -= OnActivated;
        Closed -= OnClosed;
        StopSlide();
        ResetConfirms();
        ReleasePinMirrors();
        _deviceLifetime.Dispose();
    }

    /// <summary>Selects the previous destination (LB), wrapping from the first to the
    /// last. Suppressed while a nested page owns the surface.</summary>
    internal void SelectPreviousTab()
    {
        if (!AnySubView)
        {
            Tabs.SelectPrevious();
        }
    }

    /// <summary>Selects the next destination (RB). Suppressed while a nested page is open.</summary>
    internal void SelectNextTab()
    {
        if (!AnySubView)
        {
            Tabs.SelectNext();
        }
    }

    /// <summary>Handles Back/B in strict dialog, nested-page, destination-root order.
    /// Returns false only when Home is already at its root and the controller should
    /// close the overlay. A format already running keeps running when its page closes.</summary>
    internal bool TryCancelSubView()
    {
        bool confirmationOpen = _confirmCloseLauncher || _confirmRestart || _confirmShutdown;
        switch (_navigation.BackAction(popupOpen: false, dialogOpen: confirmationOpen))
        {
            case OverlayBackAction.CloseDialog:
                ResetConfirms();
                return true;
            case OverlayBackAction.LeaveNestedPage:
                // A self-drawing sub-view handles its own deeper levels; at its root it raises
                // CloseRequested, which pops this window's page entry. The format panel is XAML
                // rather than a sub-view, so this window walks it back itself.
                if (ActiveSubView is { Host: OverlaySubView nested })
                {
                    return nested.Back();
                }
                if (AnySubView)
                {
                    LeaveFormatSubViewToOrigin();
                    return true;
                }
                if (DeviceOverlaySectionPages.SectionFor(_navigation.Page) is { } leaving)
                {
                    LeaveDeviceSection(leaving);
                    return true;
                }

                // Every branch above owns its own return focus. This is the fallback for a nested
                // page none of them claimed — a page added later, or a sub-view flag that went out
                // of step with the stack — and it has to restore focus like the rest of them.
                // Popping bare would leave the user at the top of the page they came back to, with
                // no indication of where they had been.
                RestoreRootFocus(_navigation.Pop());
                return true;
            case OverlayBackAction.ReturnHome:
                SelectDestination(OverlayDestination.QuickAccess);
                return true;
            case OverlayBackAction.ClosePopup:
                return true;
            default:
                return false;
        }
    }

    /// <summary>One selection path for touch, mouse and LB/RB: the strip carries stable
    /// destination IDs, while this window owns page visibility and semantic focus.</summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is null
            || !Enum.IsDefined((OverlayDestination)e.SelectedItem.Tag))
        {
            return;
        }

        OverlayDestination destination = (OverlayDestination)e.SelectedItem.Tag;
        RememberDestinationState(_navigation.Destination);
        LeaveAllNestedPages();
        if (!_navigation.Select(destination))
        {
            return;
        }

        _lastDestination = destination;
        ShowDestination(destination, restoreFocus: true);
    }

    private void SelectDestination(OverlayDestination destination)
    {
        if (!_navigation.IsVisible(destination))
        {
            destination = OverlayDestination.QuickAccess;
        }

        int index = DestinationIndex(destination);
        if (Tabs.SelectedIndex != index)
        {
            Tabs.SelectedIndex = index;
            return;
        }

        RememberDestinationState(_navigation.Destination);
        LeaveAllNestedPages();
        _navigation.Select(destination);
        _lastDestination = destination;
        ShowDestination(destination, restoreFocus: true);
    }

    private void ShowDestination(OverlayDestination destination, bool restoreFocus)
    {
        TabEyebrow.Text = DestinationLabel(destination).ToUpperInvariant();
        PanelQuickAccess.IsVisible = destination == OverlayDestination.QuickAccess;
        PanelHome.IsVisible = destination == OverlayDestination.Home;
        PanelSteam.IsVisible = destination == OverlayDestination.Steam;
        PanelDevice.IsVisible = destination == OverlayDestination.Device
            && _deviceBridge?.Snapshot().Visible is true;
        PanelSystem.IsVisible = destination == OverlayDestination.System;
        PanelPower.IsVisible = destination == OverlayDestination.Power;

        // The Device rows are built once per render into a panel that survives destination changes,
        // and until this call arriving here they were rebuilt only on attach and on a device-state
        // event. Selecting the destination resets the navigation stack to the Device root, so
        // without a render the panel kept rows belonging to whatever page was last drawn — showing
        // a section's contents under the root heading, or nothing at all when the attach-time
        // render had happened before the plugin published anything. The user reached an empty
        // "DEVICE CONTROLS" this way while all 16 capabilities were live.
        // RefreshDevicePanel calls ConfigureTabs, which calls back here. That terminates today only
        // because ConfigureTabs returns early on the second pass; an explicit guard is what keeps a
        // later change to either of them from turning this into a loop that hangs the UI thread.
        if (PanelDevice.IsVisible && !_showingDestination)
        {
            _showingDestination = true;
            try
            {
                RefreshDevicePanel();
                RefreshPerformancePanel();
            }
            finally
            {
                _showingDestination = false;
            }
        }

        RestoreDestinationState(restoreFocus);
    }

    private Control DestinationPanel() => _navigation.Destination switch
    {
        OverlayDestination.Home => PanelHome,
        OverlayDestination.Steam => PanelSteam,
        OverlayDestination.Device => PanelDevice,
        OverlayDestination.System => PanelSystem,
        OverlayDestination.Power => PanelPower,
        _ => PanelQuickAccess,
    };

    private void RememberDestinationState(OverlayDestination destination)
    {
        OverlayFocusState previous = FocusMemory.Recall(destination);
        string? semanticKey = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is Control { Tag: string key }
            ? key
            : previous.SemanticKey;
        FocusMemory.Remember(destination, semanticKey, ContentScroller.Offset.Y);
    }

    private string? CurrentSemanticFocusKey()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is Control { Tag: string key }
            ? key
            : null;

    private void RestoreRootFocus(string? semanticKey)
    {
        OverlayFocusState state = FocusMemory.Recall(_navigation.Destination);
        FocusMemory.Remember(
            _navigation.Destination,
            semanticKey ?? state.SemanticKey,
            state.ScrollOffset);
        RestoreDestinationState(focus: true);
    }

    private void RestoreDestinationState(bool focus)
    {
        OverlayFocusState state = FocusMemory.Recall(_navigation.Destination);
        ContentScroller.Offset = new Vector(0, state.ScrollOffset);
        if (!focus)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || AnySubView)
            {
                return;
            }

            Control panel = DestinationPanel();
            if (state.SemanticKey is not null)
            {
                foreach (var visual in panel.GetVisualDescendants())
                {
                    if (visual is Control
                        {
                            Tag: string key,
                            Focusable: true,
                            IsEffectivelyEnabled: true,
                            IsEffectivelyVisible: true,
                        } target
                        && string.Equals(key, state.SemanticKey, StringComparison.Ordinal))
                    {
                        target.Focus(NavigationMethod.Directional);
                        return;
                    }
                }
            }

            FocusFirstControl(panel);
        });
    }

    private void LeaveAllNestedPages()
    {
        LeaveFormatSubView();
        LeaveLibraryTabsSubView();
        LeaveCardManagerSubView();
        LeaveArtworkSubView();
        LeaveLaunchWrapperSubView();
        LeaveWakeLockSubView();
        LeaveDeviceColorSubView();

        // The Device sections are not sub-views with hosts of their own, so leaving them is only
        // this: dropping the one thing a section page holds beyond its rendered controls.
        UpdateGlyphInputObservation(false);
    }

    private static InputElement? FirstFocusable(Control panel)
    {
        foreach (var visual in panel.GetVisualDescendants())
        {
            // TextBoxes are excluded for the same reason D-pad traversal skips
            // them: focusing one pops the touch keyboard.
            if (visual is InputElement { Focusable: true, IsEffectivelyEnabled: true } element
                && element is not TextBox
                && element.IsEffectivelyVisible)
            {
                return element;
            }
        }
        return null;
    }

    private static void FocusFirstControl(Control panel)
        => FirstFocusable(panel)?.Focus(NavigationMethod.Directional);

    // ---- Quick access pins ----

    /// <summary>Every pinnable XAML row, by its stable id (the CardButton's Tag).
    /// Device rows are not here: they are rebuilt from the snapshot on every render.</summary>
    private readonly Dictionary<string, CardButton> _pinnable = new(StringComparer.Ordinal);

    /// <summary>Live mirrors on the Quick access root: each clone follows its source row's
    /// title, description, badge and visibility through the source's property changes, and
    /// presses through to the source's Click handlers.</summary>
    private readonly List<(CardButton Source, EventHandler<AvaloniaPropertyChangedEventArgs> Handler)> _pinMirrors = [];

    private IReadOnlyList<string> _pins = [];

    /// <summary>The Tag prefix that marks a Quick access clone; X on one unpins.</summary>
    private const string PinTagPrefix = "pin:";

    /// <summary>False until the first SetPins, so restoring stored pins never toasts.</summary>
    private bool _pinsInitialized;
    private DispatcherTimer? _pinToastTimer;

    private void IndexPinnableRows()
    {
        foreach (var button in this.GetLogicalDescendants().OfType<CardButton>())
        {
            if (button.Tag is string id && id.Length > 0 && !id.StartsWith(PinTagPrefix, StringComparison.Ordinal))
            {
                _pinnable[id] = button;
            }
        }
    }

    /// <summary>Rebuilds the Quick access root from the persisted pin list. Ids this
    /// build cannot resolve are skipped (kept in the config for the build or device that
    /// can).</summary>
    /// <param name="ids">The pinned row ids in display order.</param>
    internal void SetPins(IReadOnlyList<string> ids)
    {
        IReadOnlyList<string>? previous = _pinsInitialized ? _pins : null;
        _pins = ids;
        _pinsInitialized = true;
        RenderPins();
        if (previous is not null && ids.Count != previous.Count)
        {
            ShowPinToast(added: ids.Count > previous.Count);
        }
    }

    /// <summary>Transient confirmation above the Open apps strip after a pin toggle.</summary>
    private void ShowPinToast(bool added)
    {
        if (_closed)
        {
            return;
        }

        PinToastText.Text = added ? "Pinned to Quick access" : "Unpinned from Quick access";
        PinToastHint.Text = added ? "X again to unpin" : string.Empty;
        PinToast.IsVisible = true;
        if (_pinToastTimer is null)
        {
            DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(2400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                PinToast.IsVisible = false;
            };
            _pinToastTimer = timer;
        }

        _pinToastTimer.Stop();
        _pinToastTimer.Start();
    }

    private void RenderPins()
    {
        if (_closed)
        {
            return;
        }

        string? focusedKey = CurrentSemanticFocusKey();
        Control? restoreFocus = null;
        ReleasePinMirrors();
        PinnedGrid.Children.Clear();
        foreach (var id in _pins)
        {
            Control? row = _pinnable.TryGetValue(id, out var source)
                ? CreatePinMirror(id, source)
                : CreatePinnedDeviceRow(id);
            if (row is null)
            {
                continue;
            }
            row.Margin = new Thickness(0, 0, 10, 10);
            PinnedGrid.Children.Add(row);
            if (string.Equals(row.Tag as string, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }
        // The ghost cell is the permanent last slot: the pin affordance stays
        // discoverable whether the grid is empty or full (mock: empty-slot card).
        CardButton ghost = new()
        {
            IconGeometry = Icons.Pin,
            Title = "Pin an option",
            Description = "Hold X on any row in any tab; X again unpins",
            IsEnabled = false,
            Margin = new Thickness(0, 0, 10, 10),
        };
        ghost.Classes.Add("tile");
        ghost.Classes.Add("ghost");
        PinnedGrid.Children.Add(ghost);
        UpdatePinnedIndicators();
        Log.Change("overlay.pins", $"Quick access pins: {PinnedGrid.Children.Count} of {_pins.Count} rendered.");
        if (restoreFocus is not null)
        {
            restoreFocus.Focus(NavigationMethod.Directional);
        }
        else if (PanelQuickAccess.IsVisible && !AnySubView && focusedKey is not null
            && focusedKey.StartsWith(PinTagPrefix, StringComparison.Ordinal))
        {
            // The focused pin was just unpinned: land on whatever is left rather than on a
            // detached control.
            FocusFirstControl(PanelQuickAccess);
        }
    }

    /// <summary>Updates the pin marker on every row in its original destination.</summary>
    /// <remarks>
    /// Device and performance rows are rebuilt from snapshots, so this deliberately walks the
    /// current logical tree instead of retaining references to those short-lived controls. Quick
    /// access mirrors use a prefixed tag and remain unmarked: the icon is the immediate feedback at
    /// the source row, where the user pressed X or held the card.
    /// </remarks>
    private void UpdatePinnedIndicators()
    {
        var pinned = _pins.ToHashSet(StringComparer.Ordinal);
        foreach (CardButton button in this.GetLogicalDescendants().OfType<CardButton>())
        {
            button.IsPinned = IsOriginalPinnedRow(button.Tag, pinned);
        }
    }

    /// <summary>Determines whether a tagged card is an original row in the active pin set.</summary>
    internal static bool IsOriginalPinnedRow(object? tag, IReadOnlySet<string> pinned)
        => tag is string id
            && !id.StartsWith(PinTagPrefix, StringComparison.Ordinal)
            && pinned.Contains(id);

    private CardButton CreatePinMirror(string id, CardButton source)
    {
        var clone = new CardButton { Tag = PinTagPrefix + id };
        clone.Classes.Add("tile");
        foreach (var cls in source.Classes)
        {
            if (cls is "primary" or "danger")
            {
                clone.Classes.Add(cls);
            }
        }
        MirrorPinnedRow(clone, source);
        EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, _) => MirrorPinnedRow(clone, source);
        source.PropertyChanged += handler;
        // Press-through: the source's Click handlers run with the source as sender, so a
        // row that rewrites its own title ("Really?", "Applied to …") does so on the source
        // and the mirror follows.
        clone.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _pinMirrors.Add((source, handler));
        return clone;
    }

    private void ReleasePinMirrors()
    {
        foreach (var (source, handler) in _pinMirrors)
        {
            source.PropertyChanged -= handler;
        }
        _pinMirrors.Clear();
    }

    private static void MirrorPinnedRow(CardButton clone, CardButton source)
    {
        clone.Title = source.Title;
        clone.Description = source.Description;
        clone.IconGeometry = source.IconGeometry;
        clone.TrailingText = source.TrailingText;
        clone.TrailingGlyph = source.TrailingGlyph;
        clone.StatusBrush = source.StatusBrush;
        clone.SwatchBrush = source.SwatchBrush;
        // The source's OWN IsVisible (bound to a feature flag), not its effective one —
        // the source panel is hidden whenever the Quick access root shows.
        clone.IsVisible = source.IsVisible;
        clone.IsEnabled = source.IsEnabled;
    }

    /// <summary>A pinned Device row, rebuilt from the current snapshot: a plugin
    /// capability by its key, or one of WSGM's direct rows by its focus key. Null when
    /// the device is not showing it right now.</summary>
    private Control? CreatePinnedDeviceRow(string id)
    {
        DeviceOverlaySnapshot? snapshot = _deviceBridge?.Snapshot();
        if (snapshot is not { Visible: true })
        {
            return null;
        }

        DescriptorStatusRow? row = null;
        foreach (DeviceOverlayCapability capability in snapshot.Capabilities)
        {
            if (string.Equals(DeviceRowKey(capability), id, StringComparison.Ordinal))
            {
                row = CreateDeviceCapabilityRow(capability, id);
                break;
            }
        }
        if (row is null && DirectDeviceRow(snapshot, id) is { } direct)
        {
            row = new DescriptorStatusRow();
            row.Apply(direct.Row with { Id = id });
            Action invoke = direct.Invoke;
            row.Click += (_, _) => invoke();
        }
        if (row is null && snapshot.GlyphSelection is { } glyphSelection
            && string.Equals(glyphSelection.Id, id, StringComparison.Ordinal))
        {
            row = CreateGlyphSelectionRow(glyphSelection);
        }
        if (row is not null)
        {
            row.Tag = PinTagPrefix + id;
            row.Classes.Add("tile");
        }
        return row;
    }

    private static string DeviceRowKey(DeviceOverlayCapability capability)
        => capability.InstanceId is { Length: > 0 }
            ? $"{capability.CapabilityId}#{capability.InstanceId}"
            : capability.CapabilityId;

    /// <summary>WSGM's own Device rows by their focus key — the same table the section
    /// renderer draws from, so a pinned direct row invokes exactly what the page does.</summary>
    private (DescriptorRow Row, Action Invoke)? DirectDeviceRow(DeviceOverlaySnapshot snapshot, string id)
        => id switch
        {
            "device.auto-tdp" when snapshot.AutoTdp is { } row => (row, InvokeAutoTdpToggle),
            "device.hardware-profile" when snapshot.Profile is { } row => (row, InvokeHardwareProfileCycle),
            "device.authored-profile" when snapshot.AuthoredProfile is { } row => (row, InvokeAuthoredProfileCycle),
            "device.controller-target" when snapshot.Controller is { } row => (row, InvokeControllerTargetCycle),
            "device.retry" when snapshot.Recovery is { } row => (row, InvokeDeviceCycleRetry),
            _ => null,
        };

    /// <summary>Whether a row id can be pinned: a XAML row, or a Device row the snapshot
    /// currently shows.</summary>
    private bool IsPinnable(string id)
        => _pinnable.ContainsKey(id)
            || (_deviceBridge?.Snapshot() is { Visible: true } snapshot
                && (snapshot.Capabilities.Any(c => string.Equals(DeviceRowKey(c), id, StringComparison.Ordinal))
                    || DirectDeviceRow(snapshot, id) is not null
                    || string.Equals(snapshot.GlyphSelection?.Id, id, StringComparison.Ordinal)));

    /// <summary>Resolves the row id a row or its Quick access mirror stands for.</summary>
    private bool TryGetPinId(Control? control, out string id)
    {
        if (control is CardButton { Tag: string tag })
        {
            id = tag.StartsWith(PinTagPrefix, StringComparison.Ordinal) ? tag[PinTagPrefix.Length..] : tag;
            return IsPinnable(id);
        }
        id = "";
        return false;
    }

    /// <summary>Gamepad secondary action (X): the context menu of a focused tray
    /// icon, otherwise pin/unpin the focused row. Logged either way — this is
    /// remote-diagnosis territory.</summary>
    internal void RequestSecondaryAction(InputElement? focused)
    {
        if (focused is Control { DataContext: TrayIconEntry entry } control)
        {
            Log.Info($"Gamepad X: tray context menu for '{entry.Tip}'.");
            TrayIconActivated?.Invoke(entry, true, AnchorBelow(control));
            return;
        }
        if (TryGetPinId(focused as Control, out var id))
        {
            Log.Info($"Gamepad X: toggling pin '{id}'.");
            PinToggleRequested?.Invoke(id);
            return;
        }
        Log.Info($"Gamepad X: focused element is not pinnable ({focused?.GetType().Name ?? "none"}, tag {(focused as Control)?.Tag ?? "-"}).");
    }

    private void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started)
        {
            return;
        }
        var row = (e.Source as Visual)?.FindAncestorOfType<CardButton>(includeSelf: true);
        if (TryGetPinId(row, out var id))
        {
            e.Handled = true;
            Log.Info($"Touch hold: toggling pin '{id}'.");
            PinToggleRequested?.Invoke(id);
        }
    }

    private void OnPointerReleasedForPin(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }
        var row = (e.Source as Visual)?.FindAncestorOfType<CardButton>(includeSelf: true);
        if (TryGetPinId(row, out var id))
        {
            e.Handled = true;
            PinToggleRequested?.Invoke(id);
        }
    }

    // ---- Open apps strip and header pills ----

    /// <summary>Lands controller focus on the first Open apps chip — the bottom-swipe
    /// entry point, which exists to reach the running programs in one gesture.</summary>
    internal void FocusOpenApps() => FirstFocusable(AppTiles)?.Focus(NavigationMethod.Directional);

    /// <summary>Y: switch to the window after the foreground one in strip order,
    /// wrapping — an Alt+Tab step from the sheet. Nothing to do with fewer than two
    /// windows.</summary>
    internal void CycleNextApp()
    {
        var entries = _switcher.Entries;
        if (entries.Count < 2)
        {
            Log.Info("Next app: fewer than two windows open.");
            return;
        }
        var active = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsActive)
            {
                active = i;
                break;
            }
        }
        WindowPicked?.Invoke(entries[(active + 1) % entries.Count]);
    }

    private void OnPickWindow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AppSwitcherEntry entry)
        {
            WindowPicked?.Invoke(entry);
        }
    }

    /// <summary>Tap / A-button / left click → the icon's primary activation.</summary>
    private void OnTrayClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TrayIconEntry entry && sender is Control control)
        {
            TrayIconActivated?.Invoke(entry, false, AnchorBelow(control));
        }
    }

    /// <summary>Right mouse button → the icon's context menu (many tray apps only
    /// respond to this). Button.Click never fires for the right button, so this
    /// rides PointerReleased.</summary>
    private void OnTrayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right
            && (sender as Control)?.DataContext is TrayIconEntry entry
            && sender is Control control)
        {
            e.Handled = true;
            TrayIconActivated?.Invoke(entry, true, AnchorBelow(control));
        }
    }

    /// <summary>Screen position just below the pill's centre — where the app
    /// should anchor a popup menu (v4 coordinate protocol).</summary>
    private static PixelPoint AnchorBelow(Control control)
    {
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height));
        return new PixelPoint(point.X, point.Y);
    }

    private void OnRadioTileClicked(object? sender, RoutedEventArgs e)
    {
        // A flyout cannot hold a network list, and GamepadNavigation has no popup
        // awareness, so a list inside one would not be reachable with a controller
        // at all. The panel is a real window for both reasons.
        RadioPanelRequested?.Invoke((sender as Control)?.Tag as string == "bluetooth");
    }

    private void OnAudioTileClicked(object? sender, RoutedEventArgs e)
        => AudioPanelRequested?.Invoke();

    private void OnEjectTileClicked(object? sender, RoutedEventArgs e)
        => EjectPanelRequested?.Invoke();

    /// <summary>Scrolls a newly focused chip into its strip's viewport (app chips
    /// and tray icons share this handler). Bubbles from the buttons; the scroll
    /// viewers themselves are not focusable, and the call is a no-op when the chip
    /// is already fully visible.</summary>
    private void OnStripGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control && control is not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    /// <summary>The header's bottom edge in physical screen pixels, for the radio,
    /// audio and eject panels to hang from.</summary>
    internal int HeaderBottomScreenY
        => Position.Y
            + (int)Math.Ceiling(
                Header.Bounds.Height * _contentScale * StatusPanel.CurrentWindowScale(this));

    /// <summary>The sheet's physical right edge, used to keep peer panels on the same display.</summary>
    internal int RightScreenX
        => Position.X + (int)Math.Ceiling(Bounds.Width * StatusPanel.CurrentWindowScale(this));

    // ---- Geometry ----

    /// <summary>Share of the header's inner width the tray strip may claim before it
    /// starts scrolling. The tray is the only header content whose length WSGM does
    /// not control (the Shell_TrayWnd host takes whatever apps register), so it is
    /// the part that gets a budget; the wordmark and the status pills after it are
    /// fixed-size and always keep their space.</summary>
    private const double TrayWidthFraction = 0.30;

    /// <summary>Floor for the tray budget: one tray pill plus its spacing, so a
    /// single icon is never clipped even on an absurdly narrow display.</summary>
    private const double TrayMinWidth = 40;

    /// <summary>Horizontal padding the header adds inside the window (16 left + 16
    /// right — keep in sync with Padding="16,0" in the XAML).</summary>
    private const double HeaderHorizontalPadding = 32;

    /// <summary>The widest the tray strip may become before it scrolls, so that
    /// the fixed status pills always fit. Pure: the width budget is unit-tested
    /// against this method rather than against a live window.</summary>
    /// <param name="windowWidth">The sheet window's logical (DIP) width.</param>
    /// <param name="contentScale">The factor RootScale applies to the content
    /// (see <see cref="DockToTopEdge"/>); 1.0 when untransformed.</param>
    /// <returns>A MaxWidth in the header's pre-transform layout units.</returns>
    internal static double ComputeTrayMaxWidth(double windowWidth, double contentScale)
    {
        if (!double.IsFinite(windowWidth) || !double.IsFinite(contentScale) || contentScale <= 0)
        {
            return TrayMinWidth;
        }
        var inner = windowWidth / contentScale - HeaderHorizontalPadding;
        return Math.Max(TrayMinWidth, inner * TrayWidthFraction);
    }

    /// <summary>Spans the sheet across the summoning window's display top edge, sized to
    /// <see cref="SheetHeightFraction"/> of its height, and slides it down from
    /// above the screen. The window never covers the whole display: the strip left
    /// below is the game's, and the tap-outside dismissal.</summary>
    private void DockToTopEdge()
    {
        var screen = _preferredScreenPoint is { } point
            ? Screens?.ScreenFromPoint(point)
            : null;
        screen ??= Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        if (screen is null && Screens is { ScreenCount: > 0 })
        {
            screen = Screens.All[0];
        }
        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        // A new top-level starts on Windows' default monitor. Move its HWND onto the
        // selected display before reading its effective DPI; otherwise a secondary
        // display is sized using the primary display's scale. Do not use
        // screen.Scaling: Avalonia's screen cache can retain the pre-game-mode DPI
        // when no window existed to receive the display transition.
        Position = new PixelPoint(bounds.X, bounds.Y);
        var scaling = StatusPanel.CurrentWindowScale(this);
        // Render at the desktop's DPI: game mode forces displays to 100%, which
        // otherwise shrinks this DIP-sized sheet to millimeters on dense
        // handheld screens (device-reported). The content lays out in
        // desktop-DIP space (the factor divides the available size), the window
        // takes the scaled-up physical footprint.
        var factor = Math.Clamp(_uiScale / scaling, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Core.Log.Info($"Quick access UI scale {factor:0.##}x (desktop DPI over current {scaling:0.##}).");
            _contentScale = factor;
            RootScale.LayoutTransform = new Avalonia.Media.ScaleTransform(factor, factor);
        }
        Width = bounds.Width / scaling;
        Height = Math.Round(bounds.Height / scaling * SheetHeightFraction);
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(Width, _contentScale);

        var heightPx = (int)Math.Ceiling(Height * scaling);
        _slideEnd = new PixelPoint(bounds.X, bounds.Y);
        _slideStart = new PixelPoint(bounds.X, bounds.Y - heightPx);
        Position = _slideStart;

        StopSlide();
        _slideStartedUtc = DateTime.UtcNow;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        const double durationMs = 180;
        var progress = Math.Clamp((DateTime.UtcNow - _slideStartedUtc).TotalMilliseconds / durationMs, 0, 1);
        // Cubic ease-out keeps the movement quick without a sharp stop.
        var eased = 1 - Math.Pow(1 - progress, 3);
        Position = new PixelPoint(
            _slideEnd.X,
            (int)Math.Round(_slideStart.Y + (_slideEnd.Y - _slideStart.Y) * eased));

        if (progress >= 1)
        {
            StopSlide();
        }
    }

    private void StopSlide()
    {
        if (_slideTimer is null)
        {
            return;
        }
        _slideTimer.Stop();
        _slideTimer.Tick -= OnSlideTick;
        _slideTimer = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!TryCancelSubView())
            {
                Dismissed?.Invoke();
            }
            e.Handled = true;
        }
    }

    private void OnHomeApp(object? sender, RoutedEventArgs e) => HomeAppRequested?.Invoke();
    private void OnDesktop(object? sender, RoutedEventArgs e) => DesktopRequested?.Invoke();
    private void OnSettings(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnExitBigPicture(object? sender, RoutedEventArgs e) => ExitBigPictureRequested?.Invoke();
    private void OnTaskManager(object? sender, RoutedEventArgs e) => TaskManagerRequested?.Invoke();
    private void OnClose(object? sender, RoutedEventArgs e) => Dismissed?.Invoke();

    // ---- Per-game launch fixes ----

    /// <summary>Re-labels the launch-fix rows for the current CEF state. Called when
    /// a config reload flips live configuration on or off under an open panel.</summary>
    internal void RefreshLaunchFixLabels()
    {
        if (DataContext is OverlayViewModel viewModel)
        {
            InitializeLaunchFixLabels(viewModel);
        }
    }

    // Set from the view model, because the same buttons do two different things:
    // with CEF on they configure the game in the running Steam client, with CEF off
    // they fall back to copying the command for the user to paste.
    private void InitializeLaunchFixLabels(OverlayViewModel viewModel)
    {
        var live = viewModel.ConfigureLaunchOptionsLive;
        DeelevateFixButton.Title = live ? "Fix: run without admin" : "Copy de-elevation command";
        DeelevateFixButton.Description = live
            ? "For games that refuse to start under elevated Steam"
            : "Paste into a game's Steam launch options";
        InputLeaseFixButton.Title = live ? "Fix: give the game the controller" : "Copy Steam Input block command";
        InputLeaseFixButton.Description = "For games that read the controller themselves";
        BothFixesButton.Title = live ? "Fix: both of the above" : "Copy combined command";
        BothFixesButton.Description = "No admin, and the game owns the controller";
        RemoveFixesButton.Title = "Restore original launch action";
        RemoveFixesButton.Description = "Remove WSGM changes and restore the original";
    }

    private void OnApplyDeelevation(object? sender, RoutedEventArgs e)
        => StartLaunchFix(LaunchWrapperMode.Deelevate, DeelevateFixButton);

    private void OnApplySteamInputBlock(object? sender, RoutedEventArgs e)
        => StartLaunchFix(LaunchWrapperMode.InputLease, InputLeaseFixButton);

    private void OnApplyBothWrappers(object? sender, RoutedEventArgs e)
        => StartLaunchFix(LaunchWrapperMode.Both, BothFixesButton);

    private void OnRemoveLaunchWrappers(object? sender, RoutedEventArgs e)
        => StartLaunchFix(LaunchWrapperMode.None, RemoveFixesButton);

    private async void OnPickCustomLaunchAction(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<IStorageFile> files;
            // The picker is a separate top-level window, so every touch in it lands
            // outside the bar. Suspend the controller's tap-outside dismissal (and
            // the gamepad driving the bar behind the dialog) until it closes.
            SystemDialogActive?.Invoke(true);
            try
            {
                files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose a custom launch action",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Launch actions")
                        {
                            Patterns = ["*.exe", "*.cmd", "*.bat", "*.ps1"],
                        },
                    ],
                });
            }
            finally
            {
                SystemDialogActive?.Invoke(false);
            }
            if (_closed || files.Count == 0 || !files[0].Path.IsFile)
            {
                return;
            }
            var path = files[0].Path.LocalPath;
            if (!SteamCustomLaunchCommand.IsSupported(path))
            {
                CustomLaunchButton.Title = "Unsupported file type";
                return;
            }
            LaunchWrapperHost.OpenCustom(path, await ResolveCurrentGameAsync(CustomLaunchButton));
            if (_closed)
            {
                return;
            }
            EnterLaunchWrapperSubView();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                CustomLaunchButton.Title = "Couldn't choose a file";
            }
            Log.Error("Could not pick a custom launch action", ex);
        }
    }

    /// <summary>Resolves the game whose Steam page is on screen, so a custom action
    /// applies to it directly. Answers <c>null</c> for the library root and for a
    /// Steam that reported no current app — the caller then asks which game, exactly
    /// as <see cref="ApplyLaunchFixAsync"/> does for the wrapper buttons.</summary>
    private async Task<SteamCollections.AppInfo?> ResolveCurrentGameAsync(CardButton button)
    {
        button.Title = "Asking Steam…";
        var appId = await SteamPageBridge.GetCurrentAppIdAsync();
        if (_closed || appId <= 0)
        {
            return null;
        }
        var match = (await SafeGameLookupAsync()).FirstOrDefault(g => g.AppId == appId);
        // A game Steam knows about but the collection store did not list still
        // resolves: the id came from the page, and the shortcut flag from its range.
        return match ?? new SteamCollections.AppInfo(
            appId, appId.ToString(CultureInfo.InvariantCulture), appId >= 0x80000000L);
    }

    private void OnCustomLaunchGamePicked(
        string path, string arguments, SteamCollections.AppInfo game)
        => _ = ApplyCustomLaunchToAsync(path, arguments, game, CustomLaunchButton);

    private async System.Threading.Tasks.Task ApplyCustomLaunchToAsync(
        string path, string arguments, SteamCollections.AppInfo game, CardButton button)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                button.Title = "File is no longer available";
                return;
            }
            var details = await SteamLaunchConfig.ReadAsync(game.AppId);
            if (details is not { } current)
            {
                button.Title = "Steam didn't answer";
                return;
            }
            var existing = await LibraryTabManager.FindLaunchWrapperAsync(game.AppId);
            var originals = existing is null
                ? (current.ShortcutTarget,
                    game.Shortcut ? current.ShortcutArguments : current.LaunchOptions,
                    current.ShortcutStartDir)
                : (existing.OriginalTarget, existing.OriginalLaunchOptions, existing.OriginalStartDir);
            var snapshot = existing ?? new LaunchWrapperConfig
            {
                AppId = game.AppId,
                IsShortcut = game.Shortcut,
                OriginalTarget = originals.Item1,
                OriginalLaunchOptions = originals.Item2,
                OriginalStartDir = originals.Item3,
            };
            snapshot.Kind = LaunchConfigurationKind.CustomAction;
            snapshot.Mode = LaunchWrapperMode.None;
            snapshot.CustomActionPath = path;
            snapshot.CustomArguments = arguments;
            snapshot.Name = game.Name;
            if (existing is null)
            {
                // Persist the only restoration copy before Steam destroys a shortcut Target.
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);
            }
            var result = await SteamLaunchConfig.ApplyCustomAsync(
                game.AppId, game.Shortcut, path, arguments);
            if (!result.Ok && existing is null)
            {
                await LibraryTabManager.ForgetLaunchWrapperAsync(game.AppId);
            }
            else if (result.Ok && existing is not null)
            {
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);
            }
            button.Title = result.Ok ? $"Applied to {game.Name}" : result.Detail;
            if (result.Ok)
            {
                Log.Info($"Custom launch action written for {game.Name} ({game.AppId}).");
                LeaveLaunchWrapperSubView();
                await DismissAfterCopyFeedback();
            }
        }
        catch (Exception ex)
        {
            button.Title = "Couldn't configure launch action";
            Log.Error($"Could not configure custom launch action for {game.AppId}", ex);
        }
    }

    private void StartLaunchFix(LaunchWrapperMode mode, CardButton button)
    {
        // Resolve the lease route ONCE, here, before anything branches. The
        // clipboard text, the value written into Steam and the snapshot persisted
        // into config all flow from this, so deciding it in one place is what stops
        // them disagreeing about how a given game blocks Steam Input.
        mode = LaunchWrapperCommand.ForCurrentInputMode(
            mode, (DataContext as OverlayViewModel)?.InputLeaseUsesShim ?? true);
        var helperPath = LaunchWrapperCommand.HelperPathForCurrentDeployment();
        if (mode != LaunchWrapperMode.None && !System.IO.File.Exists(helperPath))
        {
            button.Title = "Launch wrapper missing";
            Log.Warn($"Cannot configure a launch fix; wrapper not found: {helperPath}");
            return;
        }

        if (DataContext is not OverlayViewModel { ConfigureLaunchOptionsLive: true })
        {
            _ = CopyLaunchCommandAsync(mode, button, helperPath);
            return;
        }
        _ = ApplyLaunchFixAsync(mode, button);
    }

    private async System.Threading.Tasks.Task CopyLaunchCommandAsync(
        LaunchWrapperMode mode, CardButton button, string helperPath)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            button.Title = "Clipboard unavailable";
            Log.Warn("Cannot copy a launch command; no clipboard is available.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(LaunchWrapperCommand.SteamLaunchOptions(helperPath, mode));
            button.Title = "Copied to clipboard";
            Log.Info($"Copied the {mode} launch-option command to clipboard.");
            await DismissAfterCopyFeedback();
        }
        catch (Exception ex)
        {
            button.Title = "Clipboard copy failed";
            Log.Error("Could not copy the launch command", ex);
        }
    }

    // A copied command means the user is heading to Steam to paste it: show the
    // "Copied" confirmation briefly, then dismiss the panel (which restores Steam
    // to the foreground). Same rule as the actions that open a window.
    private static async System.Threading.Tasks.Task FeedbackDelay()
        => await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(700));

    private async System.Threading.Tasks.Task DismissAfterCopyFeedback()
    {
        await FeedbackDelay();
        if (_closed)
        {
            // The panel was dismissed and re-opened while the confirmation showed:
            // the controller wires Dismissed per window instance, so this stale
            // window would close the live panel out from under the user.
            Log.Info("Launch-fix feedback dismissal skipped — its panel is already closed.");
            return;
        }
        Dismissed?.Invoke();
    }

    private async System.Threading.Tasks.Task ApplyLaunchFixAsync(
        LaunchWrapperMode mode, CardButton button)
    {
        button.Title = "Asking Steam…";
        var appId = await SteamPageBridge.GetCurrentAppIdAsync();
        if (appId <= 0)
        {
            // Nothing on screen identifies a game (the library root, or a Steam that
            // did not answer): ask which one instead of guessing.
            _pendingLaunchFix = (mode, button);
            LaunchWrapperHost.Open(mode == LaunchWrapperMode.None
                ? "Remove launch fixes"
                : "Apply launch fix");
            EnterLaunchWrapperSubView();
            return;
        }

        var games = await SafeGameLookupAsync();
        var match = games.FirstOrDefault(g => g.AppId == appId);
        await ApplyLaunchFixToAsync(
            mode, button, appId, match?.Name ?? appId.ToString(CultureInfo.InvariantCulture),
            match?.Shortcut ?? appId >= 0x80000000L);
    }

    private async System.Threading.Tasks.Task ApplyLaunchFixToAsync(
        LaunchWrapperMode mode, CardButton button, long appId, string name, bool isShortcut)
    {
        try
        {
            var current = await SteamLaunchConfig.ReadAsync(appId);
            if (current is not { } details)
            {
                button.Title = "Steam didn't answer";
                return;
            }

            LaunchConfigResult result;
            if (mode == LaunchWrapperMode.None)
            {
                var snapshot = await LibraryTabManager.FindLaunchWrapperAsync(appId);
                if (snapshot is null)
                {
                    button.Title = $"No fix applied to {name}";
                    return;
                }
                result = await SteamLaunchConfig.RestoreAsync(snapshot);
                if (result.Ok)
                {
                    await LibraryTabManager.ForgetLaunchWrapperAsync(appId);
                }
            }
            else
            {
                var existing = await LibraryTabManager.FindLaunchWrapperAsync(appId);
                // Snapshot BEFORE the write: configuring a shortcut overwrites its
                // Target, so this becomes the only record of the real program. When
                // the game is already wrapped (the user is switching modes) the
                // values on screen are WSGM's own — keep the first snapshot instead,
                // and when there is none (the command was pasted by hand, or the
                // config was reset) unwrap them rather than recording the wrapper as
                // the "original", which would make Remove restore the wrapper itself.
                var originals = SteamLaunchConfig.OriginalsFrom(isShortcut, details);
                var wrapped = SteamLaunchConfig.ModeFor(isShortcut, details) != LaunchWrapperMode.None;
                if (wrapped && existing is null && isShortcut
                    && string.IsNullOrWhiteSpace(originals.Target))
                {
                    // A wrapped shortcut whose real program cannot be recovered has
                    // no restorable state; writing WSGM's own values as the original
                    // would strand it permanently.
                    button.Title = "Can't read the original program";
                    Log.Warn($"Launch fix refused for {name} ({appId}): the shortcut is already "
                        + "wrapped and its original target could not be recovered.");
                    return;
                }
                var snapshot = existing ?? new LaunchWrapperConfig
                {
                    AppId = appId,
                    IsShortcut = isShortcut,
                    OriginalTarget = originals.Target,
                    OriginalLaunchOptions = originals.LaunchOptions,
                    OriginalStartDir = originals.StartDir,
                };
                snapshot.Kind = LaunchConfigurationKind.Wrapper;
                snapshot.Mode = mode;
                snapshot.CustomActionPath = "";
                snapshot.CustomArguments = "";
                snapshot.Name = name;
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);

                result = await SteamLaunchConfig.ApplyAsync(appId, isShortcut, mode, details);
                if (!result.Ok && existing is null)
                {
                    // Nothing was changed in Steam, so leave no snapshot behind
                    // claiming otherwise — unless one was already there.
                    await LibraryTabManager.ForgetLaunchWrapperAsync(appId);
                }
            }

            button.Title = result.Ok
                ? mode == LaunchWrapperMode.None ? $"Removed from {name}" : $"Applied to {name}"
                : result.Detail;
            if (result.Ok)
            {
                Log.Info($"Launch fix {mode} written for {name} ({appId}).");
                await DismissAfterCopyFeedback();
            }
        }
        catch (Exception ex)
        {
            button.Title = "Couldn't reach Steam";
            Log.Error($"Could not configure the launch fix for {appId}", ex);
        }
    }

    private static async System.Threading.Tasks.Task<IReadOnlyList<SteamCollections.AppInfo>>
        SafeGameLookupAsync()
    {
        try { return await SteamCollections.GetGamesAsync(); }
        catch (Exception ex)
        {
            Log.Warn($"Could not list games while configuring a launch fix: {ex.Message}");
            return [];
        }
    }

    private void OnLaunchFixGamePicked(SteamCollections.AppInfo game)
    {
        if (_pendingLaunchFix is not { } pending)
        {
            LeaveLaunchWrapperSubView();
            return;
        }
        LeaveLaunchWrapperSubView();
        _ = ApplyLaunchFixToAsync(pending.Mode, pending.Button, game.AppId, game.Name, game.Shortcut);
    }

    private void OnShowWakeLockHolders(object? sender, RoutedEventArgs e)
    {
        WakeLockHost.Open();
        EnterWakeLockSubView();
    }

    private void EnterWakeLockSubView() => EnterSubView(OverlayPage.PowerWakeLocks);

    private void LeaveWakeLockSubView() => LeaveSubView(OverlayPage.PowerWakeLocks);

    private void LeaveDeviceColorSubView() => LeaveSubView(OverlayPage.DeviceColor);

    private void EnterLaunchWrapperSubView() =>
        EnterSubView(OverlayPage.SteamLaunchConfiguration);

    private void LeaveLaunchWrapperSubView() =>
        LeaveSubView(OverlayPage.SteamLaunchConfiguration);

    private void OnCloseLauncher(object? sender, RoutedEventArgs e)
    {
        if (!_confirmCloseLauncher)
        {
            _confirmCloseLauncher = true;
            // Via the view model: the title is bound to CloseLauncherText, and a
            // direct Text write here would silently be overwritten by any
            // HomeAppName-triggered re-evaluation of that binding.
            if (DataContext is OverlayViewModel vm)
            {
                vm.ConfirmingCloseLauncher = true;
            }
            ArmConfirmReset();
            return;
        }
        ResetConfirms();
        CloseLauncherRequested?.Invoke();
    }

    // ---- Format SD Card / Add Steam Library sub-view ----

    private void OnFormatSdCard(object? sender, RoutedEventArgs e)
    {
        if (_format is null)
        {
            return;
        }
        FormatHeading.Text = "Format SD Card";
        ShowFormatState(pick: true, confirm: false, progress: false);
        EnterFormatSubView();
        _format.Refresh();
    }

    private void OnFormatRefresh(object? sender, RoutedEventArgs e) => _format?.Refresh();

    private void OnFormatTargetChosen(object? sender, RoutedEventArgs e)
    {
        if (_format is null || _format.Busy
            || (sender as Control)?.DataContext is not FormatTargetEntry entry)
        {
            return;
        }
        _pendingTarget = entry;
        FormatConfirmTarget.Text = $"Erase {entry.Name}?";
        FormatConfirmDetail.Text = entry.Detail;
        SetFormatName(Shell.SdFormatManager.DefaultLabel);
        ShowFormatState(pick: false, confirm: true, progress: false);
        FocusFirstControl(FormatConfirmView);
    }

    private void OnFormatConfirmed(object? sender, RoutedEventArgs e) =>
        Log.Observe(FormatConfirmedAsync(), "SD-card format");

    private async Task FormatConfirmedAsync()
    {
        if (_format is null || _pendingTarget is null)
        {
            return;
        }
        var target = _pendingTarget;
        var name = _formatName;
        ShowFormatState(pick: false, confirm: false, progress: true);
        ScrollFormatToTop();
        await _format.FormatAsync(target, name);
    }

    private void OnFormatCancel(object? sender, RoutedEventArgs e) => LeaveFormatSubViewToOrigin();

    private Shell.LibraryTabManager? _libraryTabs;
    private Shell.LibraryTabManager LibraryTabs => _libraryTabs ??= new Shell.LibraryTabManager();

    // Debounce for the on-open auto-sync, shared across overlay instances (the
    // window is recreated per open). Auto-sync keeps card and category tabs current
    // without the user pressing the button; the button forces an immediate sync.
    private static long _lastAutoTabSyncTicks;
    private static readonly TimeSpan AutoTabSyncInterval = TimeSpan.FromMinutes(10);

    /// <summary>Opens the Library Tabs builder sub-view (the gamepad-driven
    /// custom-tab UI). Its own "Sync now" materializes the tabs.</summary>
    private void OnLibraryTabs(object? sender, RoutedEventArgs e)
    {
        LibraryTabsHost.Open(LibraryTabs);
        EnterLibraryTabsSubView();
    }

    /// <summary>Opens the SD-card library manager sub-view.</summary>
    private void OnCardManager(object? sender, RoutedEventArgs e)
    {
        CardManagerHost.ShowFormat = _format is not null
            && DataContext is OverlayViewModel { ShowSdCard: true };
        CardManagerHost.Open(LibraryTabs);
        EnterCardManagerSubView();
    }

    /// <summary>Format picked from inside the Card Manager: hand the surface over to
    /// the format panel. Both are Steam nested pages, so the old one must be left first
    /// or two would claim the surface at once.</summary>
    private void OnFormatFromCardManager()
    {
        LeaveCardManagerSubView();
        OnFormatSdCard(this, new RoutedEventArgs());
        // Set AFTER entering: OnFormatSdCard runs the ordinary enter path, and
        // LeaveFormatSubView clears this on every exit.
        _formatReturnsToCards = true;
    }

    /// <summary>Whether leaving the format panel should land back in the Card Manager
    /// rather than the Steam root, because that is where the user opened it from.</summary>
    private bool _formatReturnsToCards;

    /// <summary>Cancel/Back out of the format panel, returning to whichever surface
    /// opened it. Re-opening the Card Manager also rescans, so a card that was just
    /// formatted shows up straight away.</summary>
    private void LeaveFormatSubViewToOrigin()
    {
        var toCards = _formatReturnsToCards;
        LeaveFormatSubView();
        if (toCards)
        {
            OnCardManager(this, new RoutedEventArgs());
        }
    }

    private void EnterCardManagerSubView() => EnterSubView(OverlayPage.SteamCardManager);

    private void LeaveCardManagerSubView() => LeaveSubView(OverlayPage.SteamCardManager);

    private void EnterLibraryTabsSubView() => EnterSubView(OverlayPage.SteamLibraryTabs);

    private void LeaveLibraryTabsSubView() => LeaveSubView(OverlayPage.SteamLibraryTabs);

    /// <summary>Opens the SteamGridDB artwork picker sub-view.</summary>
    private void OnChangeArtwork(object? sender, RoutedEventArgs e)
    {
        ArtworkHost.Open();
        EnterArtworkSubView();
    }

    private void EnterArtworkSubView() => EnterSubView(OverlayPage.SteamArtwork);

    private void LeaveArtworkSubView() => LeaveSubView(OverlayPage.SteamArtwork);

    /// <summary>Fire-and-forget background sync when the overlay opens, throttled so
    /// it runs at most once per <see cref="AutoTabSyncInterval"/>. Best-effort — a
    /// closed Steam simply leaves the tabs for the next open.</summary>
    private void MaybeAutoSyncTabs()
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAutoTabSyncTicks)
            < AutoTabSyncInterval.Ticks)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await LibraryTabs.SyncAllDetailedAsync();
                Log.Info($"Library tabs auto-sync: {result.Summary}");
                if (result.Success)
                {
                    Interlocked.Exchange(ref _lastAutoTabSyncTicks, DateTime.UtcNow.Ticks);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tabs auto-sync failed: {ex.Message}");
            }
        });
    }

    private void OnAddLibrary(object? sender, RoutedEventArgs e) =>
        Log.Observe(AddLibraryAsync(), "Steam library folder picker");

    private async Task AddLibraryAsync()
    {
        if (_format is null)
        {
            return;
        }
        // A native folder picker: for network shares / second internal drives on
        // DIY Steam machines, where the user has a pointer. Not gamepad-driven —
        // the format flow is the controller-only path.
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Choose a folder for the Steam library",
                AllowMultiple = false,
            });
        if (folders.Count == 0)
        {
            return;
        }
        var path = folders[0].Path.IsAbsoluteUri && folders[0].Path.IsFile
            ? folders[0].Path.LocalPath
            : null;
        if (string.IsNullOrEmpty(path))
        {
            Log.Warn("Add library: picked folder has no local path (a network location "
                + "without a mapped drive?).");
            return;
        }
        FormatHeading.Text = "Add Steam Library";
        ShowFormatState(pick: false, confirm: false, progress: true);
        EnterFormatSubView();
        await _format.AddLibraryAsync(path);
    }

    private void EnterFormatSubView() => EnterSubView(OverlayPage.SteamStorageFormat);

    private void LeaveFormatSubView() => LeaveSubView(OverlayPage.SteamStorageFormat);

    private void ShowFormatState(bool pick, bool confirm, bool progress)
    {
        FormatPickView.IsVisible = pick;
        FormatConfirmView.IsVisible = confirm;
        FormatProgressView.IsVisible = progress;
    }

    /// <summary>Brings a controller-focused control into the overlay viewport.
    /// Directional focus navigation does not raise this request on its own, so
    /// without it the lower keyboard rows could be focused off-screen.</summary>
    private void OnContentGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control && control is not ScrollViewer)
        {
            control.BringIntoView();
            if (!AnySubView && control.Tag is string semanticKey)
            {
                FocusMemory.Remember(
                    _navigation.Destination,
                    semanticKey,
                    ContentScroller.Offset.Y);
            }
        }
    }

    /// <summary>Returns the format flow to its heading when its state changes.
    /// The confirmation keyboard can leave the scroller at its bottom, where the
    /// terminal format message would otherwise be invisible.</summary>
    private void ScrollFormatToTop() => ContentScroller.Offset = new Vector(0, 0);

    private void OnStandby(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Standby();
    }

    private void OnHibernate(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Hibernate();
    }

    // Deliberately no dismiss: the row is a toggle, and the updated description/badge
    // are the immediate feedback the user is looking at.
    private void OnKeepAwakeToggle(object? sender, RoutedEventArgs e)
        => KeepAwakeToggleRequested?.Invoke();

    /// <summary>Paints the Keep Awake row's status dot in the WakeWatch color
    /// vocabulary: green free, yellow standby-blocked, red display-pinned, grey
    /// unknown. Brushes come from the palette tokens; set from the controller's
    /// indicator poll.</summary>
    /// <param name="state">The system-wide wake-lock state.</param>
    internal void SetKeepAwakeStatus(Core.WakeLockState state)
        => KeepAwakeButton.StatusBrush = this.FindResource(state switch
        {
            Core.WakeLockState.DisplayHeld => "HcDangerBrush",
            Core.WakeLockState.SystemHeld => "HcWarningBrush",
            Core.WakeLockState.Free => "HcSuccessBrush",
            _ => "HcTextMutedBrush",
        }) as Avalonia.Media.IBrush;

    private void OnCycleDisplayDc(object? sender, RoutedEventArgs e)
        => PowerTimeoutCycleRequested?.Invoke(Core.PowerTimeoutKind.DisplayDc);

    private void OnCycleDisplayAc(object? sender, RoutedEventArgs e)
        => PowerTimeoutCycleRequested?.Invoke(Core.PowerTimeoutKind.DisplayAc);

    private void OnCycleSleepDc(object? sender, RoutedEventArgs e)
        => PowerTimeoutCycleRequested?.Invoke(Core.PowerTimeoutKind.SleepDc);

    private void OnCycleSleepAc(object? sender, RoutedEventArgs e)
        => PowerTimeoutCycleRequested?.Invoke(Core.PowerTimeoutKind.SleepAc);

    private void OnRestart(object? sender, RoutedEventArgs e)
    {
        if (!_confirmRestart)
        {
            _confirmRestart = true;
            RestartButton.Title = "Really?";
            ArmConfirmReset();
            return;
        }
        Core.PowerActions.Restart();
    }

    private void OnShutdown(object? sender, RoutedEventArgs e)
    {
        if (!_confirmShutdown)
        {
            _confirmShutdown = true;
            ShutdownButton.Title = "Really?";
            ArmConfirmReset();
            return;
        }
        Core.PowerActions.Shutdown();
    }

    /// <summary>Armed "Really?" confirms revert on their own — after ~5 s and when
    /// the panel closes — so a stray second press minutes later cannot restart or
    /// shut down the device.</summary>
    private void ArmConfirmReset()
    {
        if (_confirmResetTimer is null)
        {
            // Parameterless ctor + explicit Start: Avalonia's 3-arg
            // DispatcherTimer ctor auto-starts, which silently defeats every
            // "start it if it isn't running" guard.
            _confirmResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _confirmResetTimer.Tick += (_, _) => ResetConfirms();
        }
        _confirmResetTimer.Stop();
        _confirmResetTimer.Start();
    }

    private void ResetConfirms()
    {
        _confirmResetTimer?.Stop();
        _confirmRestart = false;
        _confirmShutdown = false;
        _confirmCloseLauncher = false;
        if (DataContext is OverlayViewModel vm)
        {
            vm.ConfirmingCloseLauncher = false;
        }
        RestartButton.Title = "Restart";
        ShutdownButton.Title = "Shut down";
    }
}
