using WSGM.Core;
using WSGM.Overlay;
using WSGM.Settings;

namespace WSGM.Tests;

/// <summary>Pure quick access sheet logic: edge-swipe routing, the header tray budget
/// and the in-place Open apps reconciliation that keeps the focused chip alive across
/// refreshes.</summary>
public sealed class QuickAccessSheetTests
{
    // The SteamOS map: left/right are Steam's, top/bottom are WSGM's. The bottom
    // edge is explorer's in desktop mode and stays ignored there.
    [Theory]
    [InlineData(ScreenEdge.Top, false, OverlayController.SwipeAction.QuickAccess)]
    [InlineData(ScreenEdge.Top, true, OverlayController.SwipeAction.QuickAccess)]
    [InlineData(ScreenEdge.Bottom, false, OverlayController.SwipeAction.QuickAccessApps)]
    [InlineData(ScreenEdge.Bottom, true, OverlayController.SwipeAction.None)] // desktop: explorer owns the edge
    [InlineData(ScreenEdge.Left, false, OverlayController.SwipeAction.SteamMenu)]
    [InlineData(ScreenEdge.Left, true, OverlayController.SwipeAction.SteamMenu)]
    [InlineData(ScreenEdge.Right, false, OverlayController.SwipeAction.SteamQuickAccess)]
    [InlineData(ScreenEdge.Right, true, OverlayController.SwipeAction.SteamQuickAccess)]
    public void EdgeSwipeRoutesToTheSteamOsLayout(
        ScreenEdge edge, bool explorerRunning, OverlayController.SwipeAction expected)
        => Assert.Equal(expected, OverlayController.DecideSwipe(edge, explorerRunning));

    [Fact]
    public void NewConfigurationsEnableEveryEdge()
    {
        var gestures = new GestureConfig();

        Assert.True(gestures.BottomEdge);
        Assert.True(gestures.TopEdge);
        Assert.True(gestures.LeftEdgeSteamMenu);
        Assert.True(gestures.RightEdgeSteamQuickAccess);
    }

    [Fact]
    public void NormalizeDropsBlankAndDuplicatePins()
    {
        var config = new AppConfig { QuickAccessPins = ["system.keep-awake", "", " ", "system.keep-awake", "home.steam"] };

        ConfigStore.Normalize(config);

        Assert.Equal(["system.keep-awake", "home.steam"], config.QuickAccessPins);
    }

    [Fact]
    public void NormalizeRepairsANullPinList()
    {
        var config = new AppConfig { QuickAccessPins = null! };

        ConfigStore.Normalize(config);

        Assert.NotNull(config.QuickAccessPins);
        Assert.Empty(config.QuickAccessPins);
    }

    [Theory]
    [InlineData("steam.artwork", true)]
    [InlineData("steam.card-manager", false)]
    [InlineData("pin:steam.artwork", false)]
    [InlineData(null, false)]
    public void PinMarkerAppearsOnlyOnThePinnedRowsOriginalLocation(string? tag, bool expected)
    {
        IReadOnlySet<string> pins = new HashSet<string>(["steam.artwork"], StringComparer.Ordinal);

        Assert.Equal(expected, OverlayWindow.IsOriginalPinnedRow(tag, pins));
    }

    [Theory]
    [InlineData(ScreenEdge.Bottom, 100, 100, 125, 35, 65)]
    [InlineData(ScreenEdge.Right, 100, 100, 35, 125, 65)]
    [InlineData(ScreenEdge.Left, 100, 100, 165, 35, 65)]
    [InlineData(ScreenEdge.Top, 100, 100, 35, 165, 65)]
    public void InwardDistanceUsesTheDirectionOppositeEachScreenEdge(
        ScreenEdge edge, int startX, int startY, int x, int y, int expected)
        => Assert.Equal(expected, TouchSwipeMonitor.InwardDistance(edge, startX, startY, x, y));

    [Theory]
    [InlineData(true, false, true, false, 100, 100, 165, 100, ScreenEdge.Left)]
    [InlineData(true, false, true, false, 100, 100, 100, 35, ScreenEdge.Bottom)]
    [InlineData(false, true, false, true, 100, 100, 35, 100, ScreenEdge.Right)]
    [InlineData(false, true, false, true, 100, 100, 100, 165, ScreenEdge.Top)]
    public void CornerSwipeUsesTheEdgeMatchingTheContactsDirection(
        bool bottom, bool right, bool left, bool top,
        int startX, int startY, int x, int y, ScreenEdge expected)
        => Assert.Equal(
            expected,
            TouchSwipeMonitor.PickTriggeredEdge(
                bottom, right, left, top, startX, startY, x, y, triggerDistance: 48));

    [Fact]
    public void CornerSwipeWaitsUntilOneDirectionCrossesTheTriggerDistance()
        => Assert.Null(
            TouchSwipeMonitor.PickTriggeredEdge(
                bottomCandidate: true,
                rightCandidate: false,
                leftCandidate: true,
                topCandidate: false,
                startX: 100,
                startY: 100,
                x: 140,
                y: 70,
                triggerDistance: 48));

    // Each switch is exercised at its NON-default value in one of the two cases:
    // both default to true, so asserting a true round trip would also pass if the
    // snapshot never read the view model at all.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SettingsSnapshotPersistsLeftAndRightSteamGestureSwitchesIndependently(
        bool left, bool right)
    {
        var viewModel = new SettingsViewModel(new AppConfig());
        viewModel.GestureLeftSteamMenu = left;
        viewModel.GestureRightSteamQuickAccess = right;

        var snapshot = viewModel.SnapshotForPreview();

        Assert.Equal(left, snapshot.Gestures.LeftEdgeSteamMenu);
        Assert.Equal(right, snapshot.Gestures.RightEdgeSteamQuickAccess);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SettingsSnapshotPersistsTopAndBottomSheetGestureSwitchesIndependently(
        bool top, bool bottom)
    {
        var viewModel = new SettingsViewModel(new AppConfig());
        viewModel.GestureTop = top;
        viewModel.GestureBottom = bottom;

        var snapshot = viewModel.SnapshotForPreview();

        Assert.Equal(top, snapshot.Gestures.TopEdge);
        Assert.Equal(bottom, snapshot.Gestures.BottomEdge);
    }

    [Theory]
    [InlineData(BigPictureShortcut.SteamMenu, 0x31)]
    [InlineData(BigPictureShortcut.QuickAccess, 0x32)]
    public void BigPictureMenuShortcutsMatchSteamsKeyboardSimulator(
        BigPictureShortcut shortcut, ushort expected)
        => Assert.Equal(expected, Steam.ShortcutVirtualKey(shortcut));

    [Theory]
    [InlineData(150, 100u, 100u, 150u)] // saved desktop scaling wins
    [InlineData(null, 100u, 150u, 150u)] // desktop already ran 100% → panel's recommended
    [InlineData(null, 175u, 150u, 175u)] // live desktop scaling beats recommended
    [InlineData(null, 100u, 100u, 100u)] // nothing known → no upscale
    [InlineData(99, 100u, 150u, 150u)] // garbage snapshot value is ignored
    [InlineData(600, 100u, 150u, 150u)]
    public void UiScaleUsesTheSavedDesktopScalingElseTheRecommendedPanelScale(
        int? saved, uint current, uint recommended, uint expected)
        => Assert.Equal(expected, DisplayScale.PickUiScalePercent(saved, current, recommended));

    [Theory]
    [InlineData(100, 100)]
    [InlineData(113, 125)]
    [InlineData(275, 250)]
    [InlineData(490, 500)]
    public void ConfiguredDpiUsesAValueSupportedByTheDisplayConfigPacket(int requested, int expected)
        => Assert.Equal(expected, DisplayScale.NormalizeConfiguredPercent(requested));

    [Fact]
    public void ANewDockDisplayIsNotLoweredWhileAnotherDisplaysRecoverySnapshotSurvives()
        => Assert.False(DisplayScale.ShouldLowerDisplay(
            freshCapture: false,
            [new DisplayScaleEntry { DeviceName = @"\\.\DISPLAY1", Percent = 150 }],
            @"\\.\DISPLAY2"));

    [Fact]
    public void ADisplayAlreadyOwnedByTheRecoverySnapshotCanBeLoweredAgain()
        => Assert.True(DisplayScale.ShouldLowerDisplay(
            freshCapture: false,
            [new DisplayScaleEntry { DeviceName = @"\\.\DISPLAY1", Percent = 150 }],
            @"\\.\display1"));

    [Fact]
    public void AFreshCaptureCanLowerEveryIdentifiedDisplay()
        => Assert.True(DisplayScale.ShouldLowerDisplay(
            freshCapture: true,
            [],
            @"\\.\DISPLAY2"));

    // ---- Tray width budget: the header's pill zone must never grow past the sheet ----

    [Theory]
    [InlineData(1280.0, 1.0, 374.4)] // 1280 px sheet, no touch transform
    [InlineData(1920.0, 1.0, 566.4)]
    [InlineData(1280.0, 1.5, 246.4)] // RootScale 1.5x shrinks the inner layout width
    [InlineData(0.0, 1.0, 40.0)] // degenerate inputs never fall below one tray pill
    [InlineData(1280.0, 0.0, 40.0)]
    [InlineData(double.NaN, 1.0, 40.0)]
    public void TheTrayStripIsCappedAtAFractionOfTheHeadersInnerWidth(double width, double scale, double expected)
        => Assert.Equal(expected, OverlayWindow.ComputeTrayMaxWidth(width, scale), 3);

    [Fact]
    public void TheCappedTrayLeavesTheFixedStatusPillsAndTheWordmark()
    {
        // Fixed header cost at 1280 px logical, added up from the XAML: eject 34 +
        // audio 34 + Wi-Fi 34 + Bluetooth 34 + battery ~70 + clock ~64 + close 34 +
        // 7x4 spacing + the 9 px separator = ~341; the wordmark and eyebrow ~160;
        // the header's 2x16 padding 32.
        const double sheet = 1280;
        const double pills = 341;
        const double wordmark = 160;
        const double padding = 32;

        var tray = OverlayWindow.ComputeTrayMaxWidth(sheet, 1.0);
        var slack = sheet - padding - wordmark - pills - tray;

        Assert.True(slack > 0, $"status pills do not fit (slack {slack:0.#} px)");
    }

    [Fact]
    public void EveryDestinationHasAUserFacingLabel()
    {
        foreach (OverlayDestination destination in Enum.GetValues<OverlayDestination>())
        {
            Assert.False(string.IsNullOrWhiteSpace(OverlayWindow.DestinationLabel(destination)));
        }
        Assert.Equal("Quick access", OverlayWindow.DestinationLabel(OverlayDestination.QuickAccess));
        Assert.Equal("Session", OverlayWindow.DestinationLabel(OverlayDestination.Home));
        Assert.Equal("Tools", OverlayWindow.DestinationLabel(OverlayDestination.System));
    }

    private static WindowFinder.AppWindow Window(nint hwnd, string title, bool minimized = false)
        => new(hwnd, title, (uint)hwnd) { IsMinimized = minimized };

    private static AppSwitcherEntry Create(WindowFinder.AppWindow window)
        => new(window.Hwnd, window.Title, isSteam: false, icon: null);

    [Fact]
    public void ReconcileKeepsSurvivingChipInstancesAndUpdatesTheirStateInPlace()
    {
        var vm = new AppSwitcherViewModel();
        vm.Reconcile([Window(1, "Game"), Window(2, "Tool")], activeHwnd: 1, Create);
        var game = vm.Entries[0];
        var tool = vm.Entries[1];

        vm.Reconcile([Window(2, "Tool v2", minimized: true), Window(1, "Game")], activeHwnd: 2, Create);

        // Same instances (a rebuild would destroy the focused button), same stable
        // order (first-seen, not Z-order), fresh presentation state.
        Assert.Same(game, vm.Entries[0]);
        Assert.Same(tool, vm.Entries[1]);
        Assert.Equal("Tool v2", tool.Title);
        Assert.True(tool.IsMinimized);
        Assert.True(tool.IsActive);
        Assert.False(game.IsActive);
    }

    [Fact]
    public void ReconcileRemovesClosedWindowsAndAppendsNewOnesInEnumerationOrder()
    {
        var vm = new AppSwitcherViewModel();
        vm.Reconcile([Window(1, "A"), Window(2, "B")], activeHwnd: 0, Create);

        vm.Reconcile([Window(3, "C"), Window(2, "B"), Window(4, "D")], activeHwnd: 0, Create);

        Assert.Equal(3, vm.Entries.Count);
        Assert.Equal((nint)2, vm.Entries[0].Hwnd); // survivor keeps its slot
        Assert.Equal((nint)3, vm.Entries[1].Hwnd); // new windows append in order
        Assert.Equal((nint)4, vm.Entries[2].Hwnd);
        Assert.True(vm.HasEntries);
    }

    [Fact]
    public void ReconcileWithNoWindowsEmptiesTheStripAndFlagsTheEmptyState()
    {
        var vm = new AppSwitcherViewModel();
        vm.Reconcile([Window(1, "A")], activeHwnd: 1, Create);

        vm.Reconcile([], activeHwnd: 0, Create);

        Assert.Empty(vm.Entries);
        Assert.False(vm.HasEntries);
    }

    [Fact]
    public void SwitcherEntryRaisesChangeNotificationsOnlyWhenValuesActuallyChange()
    {
        var entry = new AppSwitcherEntry(1, "Title", isSteam: false, icon: null);
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        entry.Title = "Title";
        entry.IsMinimized = false;
        Assert.Empty(changed);

        entry.Title = "New";
        entry.IsMinimized = true;
        entry.IsActive = true;
        Assert.Equal([nameof(AppSwitcherEntry.Title), nameof(AppSwitcherEntry.IsMinimized), nameof(AppSwitcherEntry.IsActive)], changed);
    }
}
