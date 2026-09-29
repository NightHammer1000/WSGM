using System.Text.Json;
using WSGM.Core;
using WSGM.Settings;

namespace WSGM.Tests;

// Every view model below is built through the injected-config constructor. The
// parameterless one calls ConfigStore.Load(), which reads the developer's real
// %LOCALAPPDATA%\WSGM\config.json — and, when that file is corrupt, WRITES
// config.bad.json beside it. Constructing it here must never reach that directory.
public sealed class SettingsViewModelSplashTests
{
    [Fact]
    public void OpeningSettingsDoesNotCountAsEditingTheRuntimeOwnedDeviceValues()
    {
        // AutoTDP, the controller target and the glyph policy are also persisted by the running
        // shell — the overlay and the native quick-access menu change all three while this window
        // is open. A save merges over a fresh load, so writing this window's startup snapshot back
        // unconditionally silently reverted whichever of them had changed in the meantime.
        AppConfig config = new();
        config.DeviceIntegration.AutoTdpEnabled = true;
        config.DeviceIntegration.ControllerTarget = ManagedControllerTarget.DualShock4;
        config.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.NativeSteam;

        SettingsViewModel viewModel = new(config);

        Assert.True(viewModel.DeviceAutoTdpEnabled);
        Assert.Equal((int)ManagedControllerTarget.DualShock4, viewModel.DeviceControllerTargetIndex);
        Assert.Equal((int)DeviceGlyphSelection.NativeSteam, viewModel.DeviceGlyphSelectionIndex);
        Assert.Equal((false, false, false), viewModel.DeviceEditsMade);
    }

    [Fact]
    public void ChangingOneRuntimeOwnedDeviceValueMarksOnlyThatOne()
    {
        SettingsViewModel viewModel = new(new AppConfig());

        viewModel.DeviceGlyphSelectionIndex = (int)DeviceGlyphSelection.ManualReviewedProfile;

        Assert.Equal((false, false, true), viewModel.DeviceEditsMade);
    }

    private static string Json(SplashConfig splash) =>
        JsonSerializer.Serialize(splash, ConfigJsonContext.Default.SplashConfig);

    [Fact]
    public void LoadSplashThenBuildSplashConfigRoundTripsEveryField()
    {
        var source = new SplashConfig
        {
            Text = "Custom title",
            TextEnabled = false,
            TextColor = "#123456",
            TitleFontSize = 48,
            Caption = "custom caption",
            CaptionColor = "#654321",
            CaptionFontSize = 15,
            SpinnerStyle = SplashSpinnerStyle.LiWave,
            SpinnerColor = "#ABCDEF",
            SpinnerSize = 72,
            SweepEdge = SweepEdge.Top,
            BackgroundColor = "#0A0B0C",
            VignetteEnabled = true,
            BackgroundImagePath = @"C:\pics\bg.png",
            LogoImagePath = @"C:\pics\logo.png",
            LogoMaxSize = 160,
            TextPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Anchor,
                Anchor = SplashPlacementAnchor.BottomCenter,
                PaddingX = 10,
                PaddingY = 210,
                X = 5,
                Y = 6,
            },
            SpinnerPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Absolute,
                Anchor = SplashPlacementAnchor.TopRight,
                PaddingX = 11,
                PaddingY = 12,
                X = 640,
                Y = 480,
            },
            LogoPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.WithText,
                Anchor = SplashPlacementAnchor.CenterLeft,
                PaddingX = 13,
                PaddingY = 14,
                X = 7,
                Y = 8,
            },
        };

        var viewModel = new SettingsViewModel(new AppConfig());
        viewModel.LoadSplash(source);
        var rebuilt = viewModel.BuildSplashConfig();

        Assert.Equal(Json(source), Json(rebuilt));
    }

    [Fact]
    public void EveryPresetSurvivesTheViewModelRoundTripUnchanged()
    {
        var viewModel = new SettingsViewModel(new AppConfig());
        foreach (var preset in SplashPresets.All)
        {
            var source = SplashPresets.Create(preset);
            viewModel.LoadSplash(source);
            Assert.Equal(Json(source), Json(viewModel.BuildSplashConfig()));
        }
    }

    // "With text" is a spinner/logo-only mode: the text element is what the others position
    // against, so it cannot itself be placed with the text. An imported theme may still carry it,
    // which is why the coercion lives in BuildSplashConfig rather than in the editor.
    [Fact]
    public void WithTextOnTheTextPlacementIsCoercedToAnchorOnBuild()
    {
        var viewModel = new SettingsViewModel(new AppConfig());
        viewModel.LoadSplash(new SplashConfig
        {
            TextPlacement = new SplashElementPlacement { Mode = SplashPlacementMode.WithText },
            LogoPlacement = new SplashElementPlacement { Mode = SplashPlacementMode.WithText },
        });

        var splash = viewModel.BuildSplashConfig();

        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        // Only the text placement is coerced; the logo legitimately rides with the text.
        Assert.Equal(SplashPlacementMode.WithText, splash.LogoPlacement.Mode);
    }

    [Fact]
    public void SelectorValueListsCoverEveryEnumMember()
    {
        Assert.Equal((int)SplashSpinnerStyle.Off + 1, SettingsViewModel.SpinnerStyleValues.Length);
        Assert.Equal((int)SplashPlacementMode.WithText + 1, SettingsViewModel.PlacementModeValues.Length);
        Assert.Equal(
            (int)SplashPlacementAnchor.BottomRight + 1,
            SettingsViewModel.PlacementAnchorValues.Length);
        // The text selector deliberately omits "with text" — see the coercion above.
        Assert.DoesNotContain(SplashPlacementMode.WithText, SettingsViewModel.TextPlacementModeValues);
    }

    // --- The failed-promotion repair step ---
    // Deliberately exercised through the pure repair method and the injected save
    // delegate: the save transaction's restore path used to end in an embedded
    // ConfigStore.Save, so testing it at all meant overwriting the developer's real
    // %LOCALAPPDATA%\WSGM\config.json. Nothing below touches the file system.

    private static AppConfig ConfigWith(string logo, string background) =>
        new() { Splash = new SplashConfig { LogoImagePath = logo, BackgroundImagePath = background } };

    [Fact]
    public void RepairWithNoFailedSlotsChangesNothingAndReportsNoFailure()
    {
        var config = ConfigWith("new-logo.png", "new-bg.png");

        var failure = SettingsViewModel.RepairSlotsThatFailedToPromote(
            config, [], "old-logo.png", "old-bg.png");

        Assert.Null(failure);
        Assert.Equal("new-logo.png", config.Splash.LogoImagePath);
        Assert.Equal("new-bg.png", config.Splash.BackgroundImagePath);
    }

    [Fact]
    public void RepairRevertsOnlyTheFailedSlotToThePreviouslyPersistedPath()
    {
        var config = ConfigWith("new-logo.png", "new-bg.png");

        var failure = SettingsViewModel.RepairSlotsThatFailedToPromote(
            config, [SplashAssets.LogoSlot], "old-logo.png", "old-bg.png");

        // The failed slot goes back to the image that is actually on disk; the slot
        // that DID go live keeps the path this save just persisted for it.
        Assert.Equal("old-logo.png", config.Splash.LogoImagePath);
        Assert.Equal("new-bg.png", config.Splash.BackgroundImagePath);
        Assert.NotNull(failure);
        Assert.Contains(SplashAssets.LogoSlot, failure);
    }

    [Fact]
    public void RepairRevertsBothSlotsAndTellsTheUserTheSaveCanBeRetried()
    {
        var config = ConfigWith("new-logo.png", "new-bg.png");

        var failure = SettingsViewModel.RepairSlotsThatFailedToPromote(
            config, [SplashAssets.LogoSlot, SplashAssets.BackgroundSlot], "old-logo.png", "old-bg.png");

        Assert.Equal("old-logo.png", config.Splash.LogoImagePath);
        Assert.Equal("old-bg.png", config.Splash.BackgroundImagePath);
        Assert.NotNull(failure);
        Assert.Contains(SplashAssets.LogoSlot, failure);
        Assert.Contains(SplashAssets.BackgroundSlot, failure);
        // The picked path stays in the view model, so the status line has to say the
        // save can simply be repeated once the file is free.
        Assert.Contains("retry", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestorePersistsTheRepairedConfigExactlyOnceThroughTheInjectedSave()
    {
        var config = ConfigWith("new-logo.png", "new-bg.png");
        var saved = new List<string>();

        var failure = SettingsViewModel.RestoreSlotsThatFailedToPromote(
            config, [SplashAssets.LogoSlot], "old-logo.png", "old-bg.png",
            c => saved.Add(c.Splash.LogoImagePath));

        Assert.NotNull(failure);
        // The write sees the REPAIRED state, not the one the first save persisted.
        Assert.Equal(new[] { "old-logo.png" }, saved);
    }

    [Fact]
    public void RestoreDoesNotWriteAtAllWhenEverySlotWentLive()
    {
        var config = ConfigWith("new-logo.png", "new-bg.png");
        var writes = 0;

        var failure = SettingsViewModel.RestoreSlotsThatFailedToPromote(
            config, [], "old-logo.png", "old-bg.png", _ => writes++);

        Assert.Null(failure);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void AFailingRepairWriteDoesNotMaskTheOriginalPromotionFailure()
    {
        // The promotion failure is the cause the user has to act on; letting the
        // secondary write's exception escape would replace it with an unrelated
        // message (and used to be able to).
        var config = ConfigWith("new-logo.png", "new-bg.png");

        var failure = SettingsViewModel.RestoreSlotsThatFailedToPromote(
            config, [SplashAssets.LogoSlot], "old-logo.png", "old-bg.png",
            _ => throw new IOException("config.json is read-only"));

        Assert.NotNull(failure);
        Assert.Contains(SplashAssets.LogoSlot, failure);
        Assert.DoesNotContain("read-only", failure);
    }

    [Fact]
    public void AFailedSlotKeepsTheUsersPickInTheEditorWhileTheConfigGoesBackToThePreviousImage()
    {
        // The whole point of materialization is that config.json never names a
        // volatile path (Downloads, a removable drive). When a slot could not be
        // materialized — staging failed, or the staged copy could not be promoted —
        // the persisted path goes back to the previous copy while the EDITOR keeps the
        // user's pick, so pressing Save again retries that image.
        const string picked = @"D:\Downloads\pick.png";
        var viewModel = new SettingsViewModel(new AppConfig());
        viewModel.SplashLogoPath = picked;
        viewModel.SplashBackgroundImagePath = @"D:\Downloads\bg.png";
        // What the save just persisted: the failed slot still names the picked file,
        // the healthy one already names its materialized copy.
        var config = ConfigWith(picked, @"C:\splash\background.png");
        var saved = new List<string>();

        var failure = SettingsViewModel.RestoreSlotsThatFailedToPromote(
            config, [SplashAssets.LogoSlot],
            @"C:\splash\logo.png", @"C:\splash\background.png",
            c => saved.Add(c.Splash.LogoImagePath));
        viewModel.AdoptMaterializedPaths(config.Splash, [SplashAssets.LogoSlot]);

        // Persisted: the previous copy, which is the file that is actually there.
        Assert.Equal(@"C:\splash\logo.png", config.Splash.LogoImagePath);
        Assert.Equal(new[] { @"C:\splash\logo.png" }, saved);
        // Editor: the user's pick, so a retry is one button press.
        Assert.Equal(picked, viewModel.SplashLogoPath);
        // The healthy slot adopts its materialized copy in both places.
        Assert.Equal(@"C:\splash\background.png", viewModel.SplashBackgroundImagePath);
        // And the save is REPORTED as failed rather than logged as "Settings saved."
        Assert.NotNull(failure);
        Assert.Contains(SplashAssets.LogoSlot, failure);
    }

    [Fact]
    public void EverySlotThatWentLiveAdoptsItsMaterializedPath()
    {
        var viewModel = new SettingsViewModel(new AppConfig());
        viewModel.SplashLogoPath = @"D:\Downloads\pick.png";
        viewModel.SplashBackgroundImagePath = @"E:\usb\bg.png";

        viewModel.AdoptMaterializedPaths(
            new SplashConfig
            {
                LogoImagePath = @"C:\splash\logo.png",
                BackgroundImagePath = @"C:\splash\background.png",
            },
            []);

        Assert.Equal(@"C:\splash\logo.png", viewModel.SplashLogoPath);
        Assert.Equal(@"C:\splash\background.png", viewModel.SplashBackgroundImagePath);
    }

    [Fact]
    public void SnapshotForPreviewCarriesSplashAndAccentAndStaysIsolatedFromLaterEdits()
    {
        var viewModel = new SettingsViewModel(new AppConfig());
        viewModel.AccentColorHex = "#112233";
        viewModel.Splash.Text = "Snapshot title";
        viewModel.Splash.SpinnerStyle = SplashSpinnerStyle.SweepLine;
        viewModel.SplashBackgroundColorHex = "#101010";

        var snapshot = viewModel.SnapshotForPreview();

        Assert.Equal("#112233", snapshot.AccentColor);
        Assert.Equal("Snapshot title", snapshot.Splash.Text);
        Assert.Equal(SplashSpinnerStyle.SweepLine, snapshot.Splash.SpinnerStyle);
        Assert.Equal("#101010", snapshot.Splash.BackgroundColor);

        // A later edit must not leak into the already-taken snapshot (deep copy).
        viewModel.Splash.Text = "Changed afterwards";
        viewModel.AccentColorHex = "#FFFFFF";
        Assert.Equal("Snapshot title", snapshot.Splash.Text);
        Assert.Equal("#112233", snapshot.AccentColor);
    }
}
