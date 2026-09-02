using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;

namespace WSGM.Tests;

/// <summary>Regression tests for value objects and pure presentation branches that
/// must remain runnable without touching the user's shell, hardware, or registry.</summary>
public sealed class RegressionCoverageTests
{
    [Theory]
    [InlineData((GamepadButtons)0, false, "None")]
    [InlineData(GamepadButtons.RightTrigger | GamepadButtons.L4 | GamepadButtons.QuickAccess, false, "R2 + L4 + Quick Access")]
    [InlineData(GamepadButtons.DPadUp | GamepadButtons.RightPadPress, true, "Hold D-Up + R-Pad")]
    public void GamepadDescriptionsCoverEmptyAndExtendedButtons(GamepadButtons buttons, bool hold, string expected)
        => Assert.Equal(expected, GamepadService.Describe(buttons, hold));

    [Theory]
    [InlineData("C:\\Tools\\", "C:\\Tools\\")]
    [InlineData("C:\\Program Files\\", "\"C:\\Program Files\\\\\"")]
    [InlineData("say \"hello\"", "\"say \\\"hello\\\"\"")]
    public void QuotePreservesTrailingBackslashesAndEmbeddedQuotes(string argument, string expected)
        => Assert.Equal(expected, SelfElevation.Quote(argument));

    [Fact]
    public void PadSnapshotKeepsItsControllerIdentityAndButtons()
    {
        var snapshot = new SdlGamepads.PadSnapshot(42, GamepadButtons.A | GamepadButtons.Start);

        Assert.Equal(42u, snapshot.Id);
        Assert.Equal(GamepadButtons.A | GamepadButtons.Start, snapshot.Buttons);
    }

    [Fact]
    public void LaunchResultIsAnImmutableValueSummary()
    {
        var result = new AppLauncher.LaunchResult(null, Started: false, ElevationDeclined: true);

        Assert.False(result.Started);
        Assert.True(result.ElevationDeclined);
        Assert.Null(result.Process);
    }

    [Fact]
    public void WindowEntryPreservesTheActivationTargetAndPresentationState()
    {
        var entry = new AppSwitcherEntry((nint)123, "Steam", isSteam: true, icon: null);

        Assert.Equal((nint)123, entry.Hwnd);
        Assert.Equal("Steam", entry.Title);
        Assert.True(entry.IsSteam);
        Assert.True(entry.HasNoIcon);
    }

    [Theory]
    [InlineData(true, false, 0, false, 0u, 5, true)]
    [InlineData(false, false, 0, false, 0u, 5, false)] // invisible
    [InlineData(true, true, 0, false, 0u, 5, false)] // Progman
    [InlineData(true, false, 0x0080, false, 0u, 5, false)] // WS_EX_TOOLWINDOW
    [InlineData(true, false, 0x0088, false, 0u, 5, false)] // tool window among other ex bits
    [InlineData(true, false, 0, true, 0u, 5, false)] // our own window
    [InlineData(true, false, 0, false, 2u, 5, false)] // DWM-cloaked UWP ghost
    [InlineData(true, false, 0, false, 0u, 0, false)] // untitled
    public void SwitchableWindowFilterAdmitsOnlyAltTabStyleWindows(
        bool isVisible, bool isShellWindow, int exStyle, bool isOwnProcess, uint cloaked, int titleLength, bool expected)
        => Assert.Equal(
            expected,
            WindowFinder.PassesSwitchableFilter(isVisible, isShellWindow, exStyle, isOwnProcess, cloaked, titleLength));

    [Fact]
    public void WindowSnapshotCarriesTheMinimizedStateForSwitcherPresentation()
    {
        var window = new WindowFinder.AppWindow((nint)456, "Game", 789) { IsMinimized = true };

        Assert.True(window.IsMinimized);
        Assert.False(new WindowFinder.AppWindow((nint)1, "A", 2).IsMinimized);
    }

    [Fact]
    public void RegistryAndWindowSnapshotsRetainTheirPositionalRecordContracts()
    {
        var uac = new UacSettings.UacState(true, 0, 1, 1);
        var window = new WindowFinder.AppWindow((nint)456, "Game", 789);

        var (readable, consentPrompt, secureDesktop, enableLua) = uac;
        var (hwnd, title, processId) = window;

        Assert.True(readable);
        Assert.Equal(0, consentPrompt);
        Assert.Equal(1, secureDesktop);
        Assert.Equal(1, enableLua);
        Assert.Equal((nint)456, hwnd);
        Assert.Equal("Game", title);
        Assert.Equal(789u, processId);
    }

    [Fact]
    public void NormalizeKeepsExistingNestedSectionsAndCollections()
    {
        var apps = new List<StartupAppConfig>();
        var hotkey = new HotkeyConfig { Enabled = true, VirtualKey = 0x41 };
        var chord = new GamepadChordConfig { Enabled = true, Buttons = (int)GamepadButtons.A };
        var gestures = new GestureConfig { BottomEdge = true };
        var textPlacement = new SplashElementPlacement { Anchor = SplashPlacementAnchor.BottomCenter };
        var spinnerPlacement = new SplashElementPlacement { Mode = SplashPlacementMode.Absolute, X = 10, Y = 20 };
        var logoPlacement = new SplashElementPlacement { Mode = SplashPlacementMode.Anchor };
        var splash = new SplashConfig
        {
            Text = "Custom",
            TextPlacement = textPlacement,
            SpinnerPlacement = spinnerPlacement,
            LogoPlacement = logoPlacement,
        };
        var config = new AppConfig
        {
            StartupApps = apps,
            Hotkey = hotkey,
            GamepadChord = chord,
            Gestures = gestures,
            Splash = splash,
            AccentColor = "#FF123456",
        };

        var normalized = ConfigStore.Normalize(config);

        Assert.Same(config, normalized);
        Assert.Same(apps, normalized.StartupApps);
        Assert.Same(hotkey, normalized.Hotkey);
        Assert.Same(chord, normalized.GamepadChord);
        Assert.Same(gestures, normalized.Gestures);
        Assert.Same(splash, normalized.Splash);
        Assert.Same(textPlacement, normalized.Splash.TextPlacement);
        Assert.Same(spinnerPlacement, normalized.Splash.SpinnerPlacement);
        Assert.Same(logoPlacement, normalized.Splash.LogoPlacement);
        Assert.Equal("Custom", normalized.Splash.Text);
        Assert.Equal("#FF123456", normalized.AccentColor);
    }

    [Theory]
    [InlineData("WSGM.exe", "WSGM.exe")]
    [InlineData("C:\\Tools\\app.exe\t--argument", "C:\\Tools\\app.exe\t--argument")]
    [InlineData("\"C:\\Tools\\app.exe\"", "C:\\Tools\\app.exe")]
    public void ShellCommandParserUsesWinlogonSpaceSemantics(string command, string expected)
        => Assert.Equal(expected, ShellRegistration.ExtractExecutablePath(command));
}
