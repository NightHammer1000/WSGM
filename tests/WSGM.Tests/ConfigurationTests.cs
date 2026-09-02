using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void JsonNullIsRejectedInsteadOfBecomingSilentDefaults()
    {
        Assert.Throws<JsonException>(() => ConfigStore.DeserializeConfig("null"));
    }

    [Fact]
    public void AnUnknownDeviceEnumNameIsRepairedInsteadOfDiscardingTheWholeFile()
    {
        // Every enum here is written by name, so an unrecognised one throws before Normalize can
        // apply its Enum.IsDefined fallbacks. If the repair pass does not cover it, the retry
        // throws too and Load moves the entire file aside — taking the registry recovery snapshots
        // and every unrelated setting with it. The values below are what a hand edit, or a
        // configuration written by a build that knows more names, looks like.
        const string json = """
        {
          "AccentColor": "#FF00AA",
          "DeviceIntegration": {
            "Enabled": true,
            "ControllerTarget": "NintendoSwitchPro",
            "GlyphSelection": "SomethingElse",
            "DiagnosticLevel": "Verbose",
            "ControllerTargets": [
              { "ApplicationId": "steam:70", "Target": "NotATarget" }
            ],
            "Profiles": [
              {
                "DeviceIdentityKey": "device",
                "OemAssignments": [ { "ControlId": "oem1", "Action": "LaunchAnything" } ],
                "Capabilities": [
                  {
                    "CapabilityId": "power.primary-limit",
                    "GlobalDefault": { "Kind": "Wattage", "IntegerValue": 15 }
                  }
                ]
              }
            ]
          }
        }
        """;

        AppConfig? config = ConfigStore.DeserializeConfig(json);

        Assert.NotNull(config);
        // The unrelated setting survived, which is the point of repairing rather than discarding.
        Assert.Equal("#FF00AA", config.AccentColor);
        Assert.True(config.DeviceIntegration.Enabled);
        Assert.Equal(
            ManagedControllerTarget.SteamDeckComposite,
            config.DeviceIntegration.ControllerTarget);
        Assert.Equal(DeviceGlyphSelection.Automatic, config.DeviceIntegration.GlyphSelection);
        Assert.Equal(
            ManagedControllerTarget.SteamDeckComposite,
            Assert.Single(config.DeviceIntegration.ControllerTargets).Target);
        DeviceDesiredProfile profile = Assert.Single(config.DeviceIntegration.Profiles);
        Assert.Equal(OemAction.Disabled, Assert.Single(profile.OemAssignments).Action);
        Assert.Equal(
            WSGM.Device.Sdk.Capabilities.CapabilityValueKind.None,
            Assert.Single(profile.Capabilities).GlobalDefault!.Kind);
    }

    [Fact]
    public void UnknownPerformanceAndCachedDeclarationEnumsDoNotDiscardTheConfig()
    {
        const string json = """
        {
          "AccentColor": "#FF00AA",
          "Performance": { "FrameLimitStrategy": "FutureStrategy" },
          "DeviceIntegration": {
            "PluginSettings": [
              {
                "DeviceDefinitionId": "device",
                "PluginId": "plugin",
                "Declaration": {
                  "Sections": [ { "SectionId": "general", "Key": "FutureSection" } ],
                  "Settings": [
                    {
                      "SettingId": "future",
                      "ValueKind": "FutureValue",
                      "Display": { "Key": "FutureLabel" },
                      "Default": { "Kind": "FutureValue" },
                      "Unit": "FutureUnit"
                    }
                  ]
                }
              }
            ]
          }
        }
        """;

        AppConfig? config = ConfigStore.DeserializeConfig(json);

        Assert.NotNull(config);
        Assert.Equal("#FF00AA", config.AccentColor);
        Assert.Equal(FrameLimitStrategy.FrameLimitOnly, config.Performance.FrameLimitStrategy);
        Assert.Equal(
            CapabilityValueKind.None,
            Assert.Single(Assert.Single(config.DeviceIntegration.PluginSettings)
                .Declaration!.Settings).ValueKind);
    }

    [Fact]
    public void NormalizeRepairsAnExplicitNullRtssProfileName()
    {
        var config = new AppConfig
        {
            Performance = new PerformanceConfig
            {
                Applications =
                [
                    new PerformanceApplicationConfig
                    {
                        ApplicationId = "steam:10",
                        RtssProfileName = null!,
                        UsePerGameProfile = true,
                    },
                ],
            },
        };

        PerformanceApplicationConfig application = Assert.Single(
            ConfigStore.Normalize(config).Performance.Applications);

        Assert.Equal(string.Empty, application.RtssProfileName);
        Assert.True(application.UsePerGameProfile);
    }

    [Fact]
    public void NormalizeRepairsEveryNullableCollectionAndNestedSection()
    {
        var config = new AppConfig
        {
            StartupApps = null!,
            Hotkey = null!,
            GamepadChord = null!,
            Gestures = null!,
            SavedDisplayScaleEntries = null!,
            DisplayProfiles = null!,
            PreviousConsoleLockSchemeValues = null!,
            CardLibraries = null!,
            ForgottenInsertedCardIds = null!,
            CustomTabs = null!,
            LaunchWrappers = null!,
            SgdbLinks = null!,
            LibraryTabOrder = null!,
            HiddenNativeTabs = null!,
            KnownNativeTabs = null!,
            SteamGridDbApiKey = null!,
            AccentColor = null!,
            Splash = null!,
        };

        var normalized = ConfigStore.Normalize(config);

        Assert.NotNull(normalized.StartupApps);
        Assert.NotNull(normalized.Hotkey);
        Assert.NotNull(normalized.GamepadChord);
        Assert.NotNull(normalized.Gestures);
        Assert.NotNull(normalized.SavedDisplayScaleEntries);
        Assert.NotNull(normalized.DisplayProfiles);
        Assert.NotNull(normalized.PreviousConsoleLockSchemeValues);
        Assert.NotNull(normalized.CardLibraries);
        Assert.NotNull(normalized.ForgottenInsertedCardIds);
        Assert.NotNull(normalized.CustomTabs);
        Assert.NotNull(normalized.LaunchWrappers);
        Assert.NotNull(normalized.SgdbLinks);
        Assert.NotNull(normalized.LibraryTabOrder);
        Assert.NotNull(normalized.HiddenNativeTabs);
        Assert.NotNull(normalized.KnownNativeTabs);
        Assert.Equal("", normalized.SteamGridDbApiKey);
        Assert.Equal("#FFFF9D3D", normalized.AccentColor);
        Assert.NotNull(normalized.Splash);
    }

    /// A null ELEMENT ("StartupApps": [null]) survives the list-level null repair. It
    /// used to NRE in SelfElevation BEFORE the crash-loop breaker records a start, so
    /// the shell died at every sign-in with nothing left to disarm the boot.
    [Fact]
    public void NormalizeDropsNullElementsFromEveryListAndRepairsTheirStrings()
    {
        var config = new AppConfig
        {
            StartupApps = [null!, new StartupAppConfig { Path = null!, Args = null! }],
            SavedDisplayScaleEntries = [null!, new DisplayScaleEntry { DeviceName = null! }],
            DisplayProfiles = [null!, new MonitorDisplayProfile { MonitorId = null!, DeviceName = null!, DisplayName = null!, Desktop = null!, Game = null! }],
            PreviousConsoleLockSchemeValues = [null!, new PowerSchemeConsoleLock { SchemeGuid = null! }],
            SgdbLinks = [null!, new SgdbLinkConfig { Name = null! }],
        };

        var normalized = ConfigStore.Normalize(config);

        var app = Assert.Single(normalized.StartupApps);
        Assert.Equal("", app.Path);
        Assert.Equal("", app.Args);
        Assert.Equal("", Assert.Single(normalized.SavedDisplayScaleEntries).DeviceName);
        var display = Assert.Single(normalized.DisplayProfiles);
        Assert.Equal("", display.MonitorId);
        Assert.Equal("", display.DeviceName);
        Assert.NotNull(display.Desktop);
        Assert.NotNull(display.Game);
        Assert.Equal("", Assert.Single(normalized.PreviousConsoleLockSchemeValues).SchemeGuid);
        Assert.Equal("", Assert.Single(normalized.SgdbLinks).Name);
    }

    [Fact]
    public void NormalizeRepairsExplicitNullsInsideAnExistingSplashSection()
    {
        var config = new AppConfig
        {
            Splash = new SplashConfig
            {
                Text = null!,
                TextColor = null!,
                Caption = null!,
                CaptionColor = null!,
                SpinnerColor = null!,
                BackgroundColor = null!,
                BackgroundImagePath = null!,
                LogoImagePath = null!,
                TextPlacement = null!,
                SpinnerPlacement = null!,
                LogoPlacement = null!,
            },
        };

        var splash = ConfigStore.Normalize(config).Splash;

        Assert.Equal("Please wait", splash.Text);
        Assert.Equal("#FFFFFF", splash.TextColor);
        Assert.Equal("", splash.Caption);
        Assert.Equal("#666666", splash.CaptionColor);
        Assert.Equal("#FFFFFF", splash.SpinnerColor);
        Assert.Equal("#000000", splash.BackgroundColor);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.Equal("", splash.LogoImagePath);
        Assert.NotNull(splash.TextPlacement);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.NotNull(splash.SpinnerPlacement);
        Assert.Equal(SplashPlacementMode.WithText, splash.SpinnerPlacement.Mode);
        Assert.NotNull(splash.LogoPlacement);
        Assert.Equal(SplashPlacementMode.WithText, splash.LogoPlacement.Mode);
    }

    [Fact]
    public void NormalizeRepairsInvalidPersistedFilterEnums()
    {
        var tab = new CustomTabConfig
        {
            FilterTree = new FilterNode
            {
                Kind = (FilterKind)999,
                Mode = (FilterMode)999,
                CardScope = (SdCardScope)999,
            },
        };

        var normalized = ConfigStore.Normalize(new AppConfig { CustomTabs = [tab] });

        Assert.Equal(FilterKind.Installed, normalized.CustomTabs[0].FilterTree!.Kind);
        Assert.Equal(FilterMode.And, normalized.CustomTabs[0].FilterTree!.Mode);
        Assert.Equal(SdCardScope.Inserted, normalized.CustomTabs[0].FilterTree!.CardScope);
    }

    [Fact]
    public void NormalizeSplashClampsEveryNumericFieldIntoTheAppearanceEditorBounds()
    {
        // What a shared .wsgmsplash theme or a hand-edited config.json can carry and
        // the Appearance editor's NumericUpDowns can not.
        var splash = new SplashConfig
        {
            TitleFontSize = int.MaxValue,
            CaptionFontSize = 0,
            SpinnerSize = int.MaxValue,
            LogoMaxSize = -12,
            TextPlacement = new SplashElementPlacement
            {
                PaddingX = int.MaxValue,
                PaddingY = int.MinValue,
                X = -5,
                Y = int.MaxValue,
            },
            SpinnerPlacement = new SplashElementPlacement { PaddingX = 8192, PaddingY = -1, X = 40000, Y = -40000 },
            LogoPlacement = new SplashElementPlacement { PaddingX = -3, PaddingY = 100000, X = int.MinValue, Y = 20000 },
        };

        ConfigStore.NormalizeSplash(splash);

        Assert.Equal(400, splash.TitleFontSize);
        Assert.Equal(1, splash.CaptionFontSize);
        Assert.Equal(1024, splash.SpinnerSize);
        Assert.Equal(1, splash.LogoMaxSize);
        Assert.Equal(4096, splash.TextPlacement.PaddingX);
        Assert.Equal(0, splash.TextPlacement.PaddingY);
        Assert.Equal(0, splash.TextPlacement.X);
        Assert.Equal(16384, splash.TextPlacement.Y);
        Assert.Equal(4096, splash.SpinnerPlacement.PaddingX);
        Assert.Equal(0, splash.SpinnerPlacement.PaddingY);
        Assert.Equal(16384, splash.SpinnerPlacement.X);
        Assert.Equal(0, splash.SpinnerPlacement.Y);
        Assert.Equal(0, splash.LogoPlacement.PaddingX);
        Assert.Equal(4096, splash.LogoPlacement.PaddingY);
        Assert.Equal(0, splash.LogoPlacement.X);
        Assert.Equal(16384, splash.LogoPlacement.Y);
    }

    [Fact]
    public void NormalizeSplashLeavesInRangeNumbersAndKnownEnumsUntouched()
    {
        var splash = new SplashConfig
        {
            TitleFontSize = 26,
            CaptionFontSize = 12,
            SpinnerSize = 36,
            LogoMaxSize = 200,
            SpinnerStyle = SplashSpinnerStyle.LiWave,
            SweepEdge = SweepEdge.Top,
            TextPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Absolute,
                Anchor = SplashPlacementAnchor.BottomRight,
                PaddingX = 64,
                PaddingY = 4096,
                X = 0,
                Y = 16384,
            },
        };

        ConfigStore.NormalizeSplash(splash);

        Assert.Equal(26, splash.TitleFontSize);
        Assert.Equal(12, splash.CaptionFontSize);
        Assert.Equal(36, splash.SpinnerSize);
        Assert.Equal(200, splash.LogoMaxSize);
        Assert.Equal(SplashSpinnerStyle.LiWave, splash.SpinnerStyle);
        Assert.Equal(SweepEdge.Top, splash.SweepEdge);
        Assert.Equal(SplashPlacementMode.Absolute, splash.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.BottomRight, splash.TextPlacement.Anchor);
        Assert.Equal(64, splash.TextPlacement.PaddingX);
        Assert.Equal(4096, splash.TextPlacement.PaddingY);
        Assert.Equal(0, splash.TextPlacement.X);
        Assert.Equal(16384, splash.TextPlacement.Y);
    }

    [Fact]
    public void NormalizeSplashDropsEnumMembersThatDoNotExistBackToTheirDefaults()
    {
        // "SpinnerStyle": 999 in the JSON deserializes unchecked into the enum.
        var splash = new SplashConfig
        {
            SpinnerStyle = (SplashSpinnerStyle)999,
            SweepEdge = (SweepEdge)(-4),
            TextPlacement = new SplashElementPlacement
            {
                Mode = (SplashPlacementMode)42,
                Anchor = (SplashPlacementAnchor)(-1),
            },
        };

        ConfigStore.NormalizeSplash(splash);

        Assert.Equal(SplashSpinnerStyle.Ring, splash.SpinnerStyle);
        Assert.Equal(SweepEdge.Bottom, splash.SweepEdge);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.Center, splash.TextPlacement.Anchor);
    }

    [Fact]
    public void NormalizeSplashTurnsWhitespaceOnlyImagePathsIntoTheSingleNoImageValue()
    {
        // Every consumer reads these with IsNullOrWhiteSpace, so "   " already MEANS
        // no image — persisting it verbatim (config.json, an exported theme) only
        // spreads a second spelling of the same state that nothing can act on.
        var splash = new SplashConfig { LogoImagePath = "   ", BackgroundImagePath = "\t\n" };

        ConfigStore.NormalizeSplash(splash);

        Assert.Equal("", splash.LogoImagePath);
        Assert.Equal("", splash.BackgroundImagePath);
    }

    [Fact]
    public void NormalizeSplashKeepsRealImagePathsExactlyAsTheyAre()
    {
        // Leading and trailing spaces are legal inside Windows path components, so
        // nothing but the all-whitespace case may be rewritten.
        var splash = new SplashConfig
        {
            LogoImagePath = @"C:\pictures\ spaced logo .png",
            BackgroundImagePath = @"\\server\share\bg.jpg",
        };

        ConfigStore.NormalizeSplash(splash);

        Assert.Equal(@"C:\pictures\ spaced logo .png", splash.LogoImagePath);
        Assert.Equal(@"\\server\share\bg.jpg", splash.BackgroundImagePath);
    }

    [Fact]
    public void NormalizeSplashTruncatesDisplayStringsThatCouldNotBeLaidOut()
    {
        // A shared .wsgmsplash may spend most of its 1 MiB splash.json allowance on
        // one string (tiny once compressed). Both the Settings text box it is bound
        // into on import and the boot splash's single unwrapped TextBlock would then
        // have to lay out that whole run.
        var splash = new SplashConfig
        {
            Text = new string('A', 300_000),
            Caption = new string('B', 4_000),
            TextColor = "#" + new string('F', 500),
            CaptionColor = new string('c', 33),
            SpinnerColor = new string('d', 64),
            BackgroundColor = new string('e', 1_000_000),
        };

        ConfigStore.NormalizeSplash(splash);

        Assert.Equal(200, splash.Text.Length);
        Assert.Equal(new string('A', 200), splash.Text);
        Assert.Equal(200, splash.Caption.Length);
        Assert.Equal(new string('B', 200), splash.Caption);
        Assert.Equal(32, splash.TextColor.Length);
        Assert.Equal(32, splash.CaptionColor.Length);
        Assert.Equal(32, splash.SpinnerColor.Length);
        Assert.Equal(32, splash.BackgroundColor.Length);
    }

    [Fact]
    public void NormalizeSplashLeavesOrdinarySplashTextAndColorsExactlyAsTheyAre()
    {
        // The caps may never cut a real splash line: a title is a few words, and the
        // longest color string that can parse is "#AARRGGBB" or a color name.
        var splash = new SplashConfig
        {
            Text = "Starting Steam Big Picture…",
            Caption = "Please wait while the handheld finishes waking up — this takes a moment",
            TextColor = "#FFFFFF",
            CaptionColor = "#80FF9D3D",
            SpinnerColor = "LightGoldenrodYellow",
            BackgroundColor = "#0B0B0D",
        };

        ConfigStore.NormalizeSplash(splash);

        Assert.Equal("Starting Steam Big Picture…", splash.Text);
        Assert.Equal("Please wait while the handheld finishes waking up — this takes a moment", splash.Caption);
        Assert.Equal("#FFFFFF", splash.TextColor);
        Assert.Equal("#80FF9D3D", splash.CaptionColor);
        Assert.Equal("LightGoldenrodYellow", splash.SpinnerColor);
        Assert.Equal("#0B0B0D", splash.BackgroundColor);
    }

    [Fact]
    public void NormalizeSplashNeverTruncatesBetweenTheHalvesOfASurrogatePair()
    {
        // The leading ASCII char puts every emoji's high surrogate on an odd index,
        // so a blind cut at 200 would keep the first half of the 100th emoji and
        // render a replacement glyph at the end of the line.
        var splash = new SplashConfig { Text = "A" + string.Concat(Enumerable.Repeat("😀", 300)) };

        ConfigStore.NormalizeSplash(splash);

        Assert.Equal("A" + string.Concat(Enumerable.Repeat("😀", 99)), splash.Text);
        Assert.Equal(199, splash.Text.Length);
    }

    [Fact]
    public void ImportingASplashThemeTruncatesItsOverLongTextBeforeItReachesTheEditor()
    {
        // The import path deserializes the same contract from an untrusted archive
        // and runs it through NormalizeSplash — the cap has to apply there too, since
        // that config is bound into the Appearance text boxes immediately.
        var root = Path.Combine(Path.GetTempPath(), "wsgm-splash-length-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var themePath = Path.Combine(root, "huge.wsgmsplash");
            var oversized = new SplashConfig
            {
                Text = new string('T', 250_000),
                Caption = new string('C', 250_000),
                BackgroundColor = new string('#', 900),
            };
            // Export does not normalize, so this writes exactly the archive a
            // malicious sharer would hand out.
            Assert.True(SplashTheme.Export(oversized, themePath));

            var imported = SplashTheme.Import(themePath, Path.Combine(root, "staged"));

            Assert.NotNull(imported);
            Assert.Equal(200, imported.Text.Length);
            Assert.Equal(200, imported.Caption.Length);
            Assert.Equal(32, imported.BackgroundColor.Length);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Temp cleanup is best effort.
            }
        }
    }

    [Fact]
    public void LoadingAConfigWithAnAbsurdSplashSizeClampsItOnTheLoadPath()
    {
        // Normalize is what ConfigStore.Load runs over a persisted config.json.
        var config = new AppConfig
        {
            Splash = new SplashConfig { SpinnerSize = 2147483647, LogoMaxSize = 999999 },
        };

        var splash = ConfigStore.Normalize(config).Splash;

        Assert.Equal(1024, splash.SpinnerSize);
        Assert.Equal(4096, splash.LogoMaxSize);
    }

    [Fact]
    public void TheConfigLockIsReentrantOnTheSameThreadSoNestedLoadAndSaveStillBalance()
    {
        // The Settings save transaction holds this scope across Mutate → Commit
        // while ConfigStore.Load/Save re-acquire the same named mutex inside it.
        using var outer = ConfigStore.AcquireLock();
        // Write scopes fail closed when another process owns the mutex. Keep the
        // assertion as an explicit statement that the following checks exercise the
        // real kernel lock rather than only the thread-local recursion counter.
        Assert.True(ConfigStore.HasExclusiveLock, "the config mutex was held elsewhere");

        var nested = System.Diagnostics.Stopwatch.StartNew();
        using (ConfigStore.AcquireLock())
        {
            // The nested acquisition is granted immediately (per-thread recursion
            // count), not degraded to the 2 s lock-less timeout path.
        }
        nested.Stop();
        Assert.True(nested.ElapsedMilliseconds < 1000, $"nested acquire took {nested.ElapsedMilliseconds} ms");

        // The inner release only decremented the recursion count: the outer scope
        // still owns the mutex, so another acquisition is still immediate.
        var afterInnerRelease = System.Diagnostics.Stopwatch.StartNew();
        using (ConfigStore.AcquireLock())
        {
        }
        afterInnerRelease.Stop();
        Assert.True(
            afterInnerRelease.ElapsedMilliseconds < 1000,
            $"acquire after the inner release took {afterInnerRelease.ElapsedMilliseconds} ms"
        );

        // …and the lock is still EXCLUSIVE while the outer scope lives: the nested
        // release must not have handed the mutex to another saver mid-transaction.
        var acquiredElsewhere = true;
        var probeThread = new System.Threading.Thread(() =>
        {
            using var probe = new System.Threading.Mutex(false, @"Local\WSGM.Config");
            acquiredElsewhere = probe.WaitOne(200);
            if (acquiredElsewhere)
            {
                probe.ReleaseMutex();
            }
        });
        probeThread.Start();
        probeThread.Join();
        Assert.False(acquiredElsewhere);
    }

    [Fact]
    public void NestedConfigLockScopesBalanceTheirDepthOnEveryPath()
    {
        // Nested acquisition is short-circuited by a thread-local depth counter (it
        // must not pay the 2 s timeout once per nested call under contention), so that
        // counter is what decides whether the OUTERMOST scope ever releases the
        // cross-process mutex. It has to come back to zero on every path.
        Assert.Equal(0, ConfigStore.LockDepth);

        using (ConfigStore.AcquireLock())
        {
            Assert.Equal(1, ConfigStore.LockDepth);
            using (ConfigStore.AcquireLock())
            {
                Assert.Equal(2, ConfigStore.LockDepth);
                using (ConfigStore.AcquireLock())
                {
                    Assert.Equal(3, ConfigStore.LockDepth);
                }
                Assert.Equal(2, ConfigStore.LockDepth);
            }
            Assert.Equal(1, ConfigStore.LockDepth);

            // A nested scope left through an exception still pops exactly one level.
            Action nestedStepThatThrows = () =>
            {
                using (ConfigStore.AcquireLock())
                {
                    throw new InvalidOperationException("nested step blew up");
                }
            };
            Assert.Throws<InvalidOperationException>(nestedStepThatThrows);
            Assert.Equal(1, ConfigStore.LockDepth);

            // Disposing the same scope twice must not pop a level it never pushed.
            var scope = ConfigStore.AcquireLock();
            Assert.Equal(2, ConfigStore.LockDepth);
            scope.Dispose();
            scope.Dispose();
            Assert.Equal(1, ConfigStore.LockDepth);
        }

        Assert.Equal(0, ConfigStore.LockDepth);
    }

    [Fact]
    public void ConfigLockScopesDisposedOutOfOrderKeepTheDepthAndTheMutexSound()
    {
        // Scopes are `using` blocks everywhere today, but the counter must not be one
        // mis-ordered dispose away from nonsense: the outermost scope used to assign 0
        // while a nested scope was still live, and that nested scope's later Dispose
        // then decremented to -1 — a depth no acquisition can ever come back from
        // cleanly, and one a later stale scope could pop off an unrelated acquisition.
        Assert.Equal(0, ConfigStore.LockDepth);
        var outer = ConfigStore.AcquireLock();
        Assert.True(ConfigStore.HasExclusiveLock, "the config mutex was held elsewhere");
        var stale = ConfigStore.AcquireLock();
        Assert.Equal(2, ConfigStore.LockDepth);

        outer.Dispose();
        Assert.Equal(0, ConfigStore.LockDepth);

        // A fresh, real acquisition on this thread — which the stale scope's late
        // Dispose must leave completely alone.
        var reacquired = ConfigStore.AcquireLock();
        Assert.True(ConfigStore.HasExclusiveLock, "the config mutex was held elsewhere");
        Assert.Equal(1, ConfigStore.LockDepth);
        stale.Dispose();
        Assert.Equal(1, ConfigStore.LockDepth);
        stale.Dispose();
        Assert.Equal(1, ConfigStore.LockDepth);

        // Still exclusive across processes while that fresh scope lives…
        Assert.False(MutexTakenOnAnotherThread(200));
        reacquired.Dispose();
        Assert.Equal(0, ConfigStore.LockDepth);
        // …and released exactly once, so the named mutex is free again.
        Assert.True(MutexTakenOnAnotherThread(2000));
    }

    /// Takes the real config mutex from a foreign thread, the only way to observe
    /// whether the cross-process lock is actually held (the depth counter is
    /// thread-local and says nothing about the kernel object).
    private static bool MutexTakenOnAnotherThread(int timeoutMs)
    {
        var acquired = false;
        var probeThread = new System.Threading.Thread(() =>
        {
            using var probe = new System.Threading.Mutex(false, @"Local\WSGM.Config");
            acquired = probe.WaitOne(timeoutMs);
            if (acquired)
            {
                probe.ReleaseMutex();
            }
        });
        probeThread.Start();
        probeThread.Join();
        return acquired;
    }

    [Fact]
    public void AnOutermostConfigLockScopeReleasesEvenAfterItsNestedScopesAreGone()
    {
        // The outermost scope owns the kernel mutex; a double dispose of it must leave
        // the depth at zero so the next acquisition on this thread is a real one.
        var outer = ConfigStore.AcquireLock();
        Assert.True(ConfigStore.HasExclusiveLock, "the config mutex was held elsewhere");
        using (ConfigStore.AcquireLock()) { }
        outer.Dispose();
        outer.Dispose();

        Assert.Equal(0, ConfigStore.LockDepth);

        // Another thread can take it again — the mutex really was released.
        var acquiredElsewhere = false;
        var probeThread = new System.Threading.Thread(() =>
        {
            using var probe = new System.Threading.Mutex(false, @"Local\WSGM.Config");
            acquiredElsewhere = probe.WaitOne(2000);
            if (acquiredElsewhere)
            {
                probe.ReleaseMutex();
            }
        });
        probeThread.Start();
        probeThread.Join();
        Assert.True(acquiredElsewhere);
    }

    [Fact]
    public void SplashDefaultsReproduceTheClassicBootSplashLook()
    {
        var splash = new SplashConfig();

        Assert.Equal("#000000", splash.BackgroundColor);
        Assert.False(splash.VignetteEnabled);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.True(splash.TextEnabled);
        Assert.Equal("Please wait", splash.Text);
        Assert.Equal("#FFFFFF", splash.TextColor);
        Assert.Equal(26, splash.TitleFontSize);
        Assert.Equal("", splash.Caption);
        Assert.Equal("#666666", splash.CaptionColor);
        Assert.Equal(12, splash.CaptionFontSize);
        Assert.Equal(SplashSpinnerStyle.Ring, splash.SpinnerStyle);
        Assert.Equal("#FFFFFF", splash.SpinnerColor);
        Assert.Equal(36, splash.SpinnerSize);
        Assert.Equal(SweepEdge.Bottom, splash.SweepEdge);
        Assert.Equal("", splash.LogoImagePath);
        Assert.Equal(200, splash.LogoMaxSize);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.Center, splash.TextPlacement.Anchor);
        Assert.Equal(SplashPlacementMode.WithText, splash.SpinnerPlacement.Mode);
        Assert.Equal(SplashPlacementMode.WithText, splash.LogoPlacement.Mode);
    }

    [Fact]
    public void FullyCustomizedSplashConfigRoundTripsWithStringEnums()
    {
        var original = new AppConfig
        {
            Splash = new SplashConfig
            {
                Text = "WSGM",
                TextEnabled = false,
                TextColor = "#FF9D3D",
                TitleFontSize = 48,
                Caption = "STARTING STEAM",
                CaptionColor = "#AAAAAA",
                CaptionFontSize = 14,
                SpinnerStyle = SplashSpinnerStyle.SweepLine,
                SpinnerColor = "#00FF00",
                SpinnerSize = 72,
                SweepEdge = SweepEdge.Top,
                BackgroundColor = "#101010",
                VignetteEnabled = true,
                BackgroundImagePath = "C:\\Images\\bg.png",
                LogoImagePath = "C:\\Images\\logo.png",
                LogoMaxSize = 320,
                TextPlacement = new SplashElementPlacement
                {
                    Mode = SplashPlacementMode.Anchor,
                    Anchor = SplashPlacementAnchor.BottomLeft,
                    PaddingX = 32,
                    PaddingY = 160,
                },
                SpinnerPlacement = new SplashElementPlacement
                {
                    Mode = SplashPlacementMode.Absolute,
                    X = 640,
                    Y = 360,
                },
                LogoPlacement = new SplashElementPlacement
                {
                    Mode = SplashPlacementMode.Anchor,
                    Anchor = SplashPlacementAnchor.TopRight,
                },
            },
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.Contains("\"SpinnerStyle\": \"SweepLine\"", json);
        Assert.NotNull(restored);
        var splash = restored.Splash;
        Assert.Equal("WSGM", splash.Text);
        Assert.False(splash.TextEnabled);
        Assert.Equal("#FF9D3D", splash.TextColor);
        Assert.Equal(48, splash.TitleFontSize);
        Assert.Equal("STARTING STEAM", splash.Caption);
        Assert.Equal("#AAAAAA", splash.CaptionColor);
        Assert.Equal(14, splash.CaptionFontSize);
        Assert.Equal(SplashSpinnerStyle.SweepLine, splash.SpinnerStyle);
        Assert.Equal("#00FF00", splash.SpinnerColor);
        Assert.Equal(72, splash.SpinnerSize);
        Assert.Equal(SweepEdge.Top, splash.SweepEdge);
        Assert.Equal("#101010", splash.BackgroundColor);
        Assert.True(splash.VignetteEnabled);
        Assert.Equal("C:\\Images\\bg.png", splash.BackgroundImagePath);
        Assert.Equal("C:\\Images\\logo.png", splash.LogoImagePath);
        Assert.Equal(320, splash.LogoMaxSize);
        Assert.Equal(SplashPlacementAnchor.BottomLeft, splash.TextPlacement.Anchor);
        Assert.Equal(32, splash.TextPlacement.PaddingX);
        Assert.Equal(160, splash.TextPlacement.PaddingY);
        Assert.Equal(SplashPlacementMode.Absolute, splash.SpinnerPlacement.Mode);
        Assert.Equal(640, splash.SpinnerPlacement.X);
        Assert.Equal(360, splash.SpinnerPlacement.Y);
        Assert.Equal(SplashPlacementMode.Anchor, splash.LogoPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.TopRight, splash.LogoPlacement.Anchor);
    }

    [Fact]
    public void EveryCefSubToggleDefaultsOnAndRoundTripsOff()
    {
        // Default-on matters as much as the round trip: an older config.json has no
        // Cef section at all, and a sub-toggle that deserialized to false would
        // silently disable a shipped feature on upgrade.
        var defaults = new AppConfig().Cef;
        Assert.True(defaults.Enabled);
        Assert.True(defaults.LibraryTabs);
        Assert.True(defaults.CardManager);
        Assert.True(defaults.SdFormat);
        Assert.True(defaults.Artwork);
        Assert.True(defaults.WifiIndicator);
        Assert.True(defaults.DownloadKeepAwake);
        Assert.True(defaults.DownloadQueueSort);

        var original = new AppConfig();
        original.Cef.DownloadQueueSort = false;
        original.Cef.DownloadKeepAwake = false;

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.False(restored.Cef.DownloadQueueSort);
        Assert.False(restored.Cef.DownloadKeepAwake);
        Assert.True(restored.Cef.WifiIndicator);
    }

    [Fact]
    public void ACefSectionMissingFromAnOlderConfigStillEnablesTheNewSubToggles()
    {
        // Exactly what an upgrade from a build that predates the sub-toggle sees.
        var restored = JsonSerializer.Deserialize(
            "{\"SteamAutoRelaunch\":true}", ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.NotNull(restored.Cef);
        Assert.True(restored.Cef.DownloadQueueSort);
    }

    [Fact]
    public void AccentColorRoundTripsAndDefaultsToTheWsgmOrange()
    {
        Assert.Equal("#FFFF9D3D", new AppConfig().AccentColor);

        var original = new AppConfig { AccentColor = "#FF2266CC" };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.Equal("#FF2266CC", restored.AccentColor);
    }

    [Fact]
    public void NormalizeBoundsAHandEditedAccentColorTheSameWayTheSplashColorsAreBounded()
    {
        // Same shape as the splash color strings: hand-editable in config.json, bound
        // to a TextBox, and Color.TryParse'd over its whole length on every keystroke
        // to repaint the swatches and the picker. A 1 MiB value used to survive here.
        var config = ConfigStore.Normalize(new AppConfig { AccentColor = new string('e', 1_000_000) });

        Assert.Equal(32, config.AccentColor.Length);
    }

    [Fact]
    public void NormalizeLeavesEveryAccentColorAUserCanActuallyPickUntouched()
    {
        // The cap may never cut a real value: the longest that can parse is
        // "#AARRGGBB" or Avalonia's longest known-color name.
        Assert.Equal(
            "#FF9D3D", ConfigStore.Normalize(new AppConfig { AccentColor = "#FF9D3D" }).AccentColor);
        Assert.Equal(
            "#FFFF9D3D", ConfigStore.Normalize(new AppConfig { AccentColor = "#FFFF9D3D" }).AccentColor);
        Assert.Equal(
            "LightGoldenrodYellow",
            ConfigStore.Normalize(new AppConfig { AccentColor = "LightGoldenrodYellow" }).AccentColor);
    }

    [Fact]
    public void SourceGeneratedConfigJsonRoundTripsSettingsAndSnapshots()
    {
        var original = new AppConfig
        {
            SteamAutoRelaunch = true,
            StartupDelayMs = 1234,
            GlyphStyle = GlyphStyle.Nintendo,
            PreviousShellSnapshotCaptured = true,
            PreviousShellValueExists = true,
            PreviousShellValue = "explorer.exe",
            StartupApps =
            [
                new StartupAppConfig { Path = "C:\\Tools\\companion.exe", Args = "--silent", Elevated = true },
            ],
            SavedDisplayScaleEntries =
            [
                new DisplayScaleEntry { DeviceName = "\\\\.\\DISPLAY1", Percent = 150 },
            ],
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.Contains("\"GlyphStyle\": \"Nintendo\"", json);
        Assert.NotNull(restored);
        Assert.True(restored.SteamAutoRelaunch);
        Assert.Equal(1234, restored.StartupDelayMs);
        Assert.Equal(GlyphStyle.Nintendo, restored.GlyphStyle);
        Assert.Equal("explorer.exe", restored.PreviousShellValue);
        Assert.Single(restored.StartupApps);
        Assert.True(restored.StartupApps[0].Elevated);
        Assert.Equal(150, Assert.Single(restored.SavedDisplayScaleEntries).Percent);
    }

    [Fact]
    public void GameModeBootDefaultsMatchTheInstallerIntent()
    {
        var config = new AppConfig();

        Assert.True(config.GameModeBootEnabled);
        Assert.Equal(5000, config.ExplorerLogonSettleMs);
    }

    [Fact]
    public void DisplayManagementDefaultsToLegacyDpiOnlyBehavior()
        => Assert.Equal(DisplayManagementMode.DpiOnly, new AppConfig().DisplayManagement);

    [Fact]
    public void NormalizeRepairsAnOutOfRangeDisplayManagementValue()
    {
        var config = new AppConfig { DisplayManagement = (DisplayManagementMode)99 };

        ConfigStore.Normalize(config);

        Assert.Equal(DisplayManagementMode.DpiOnly, config.DisplayManagement);
    }

    [Fact]
    public void PerMonitorDesktopAndGameProfilesRoundTrip()
    {
        var original = new AppConfig
        {
            DisplayManagement = DisplayManagementMode.FixedProfiles,
            DisplayProfiles = [new MonitorDisplayProfile
            {
                MonitorId = "MONITOR\\AUO1234",
                DeviceName = @"\\.\DISPLAY1",
                DisplayName = "Internal panel",
                HdrAvailable = true,
                Desktop = new DisplayModeValues { Width = 1920, Height = 1080, RefreshRate = 120, DpiPercent = 150, HdrEnabled = false },
                Game = new DisplayModeValues { Width = 1280, Height = 720, RefreshRate = 120, DpiPercent = 100, HdrEnabled = true },
            }],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

        Assert.Equal(DisplayManagementMode.FixedProfiles, restored.DisplayManagement);
        var profile = Assert.Single(restored.DisplayProfiles);
        Assert.Equal("MONITOR\\AUO1234", profile.MonitorId);
        Assert.True(profile.HdrAvailable);
        Assert.Equal((1920, 1080, 120, 150), (profile.Desktop.Width, profile.Desktop.Height, profile.Desktop.RefreshRate, profile.Desktop.DpiPercent));
        Assert.Equal((1280, 720, 120, 100), (profile.Game.Width, profile.Game.Height, profile.Game.RefreshRate, profile.Game.DpiPercent));
        Assert.False(profile.Desktop.HdrEnabled);
        Assert.True(profile.Game.HdrEnabled);
    }

    [Theory]
    [InlineData(DisplayManagementMode.AutomaticProfiles, DisplayManagementMode.AutomaticProfiles, false)]
    [InlineData(DisplayManagementMode.DpiOnly, DisplayManagementMode.AutomaticProfiles, true)]
    [InlineData(DisplayManagementMode.AutomaticProfiles, DisplayManagementMode.FixedProfiles, true)]
    [InlineData(DisplayManagementMode.FixedProfiles, DisplayManagementMode.Off, false)]
    public void SettingsDoesNotOverwriteRuntimeOwnedAutomaticSnapshots(
        DisplayManagementMode initial,
        DisplayManagementMode selected,
        bool expected)
        => Assert.Equal(expected, WSGM.Settings.SettingsViewModel.ShouldWriteDisplayProfiles(initial, selected));

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void HdrChangesOnlyWhenTheActiveTargetSupportsIt(
        bool available,
        bool current,
        bool requested,
        bool expected)
        => Assert.Equal(expected, DisplayScale.ShouldChange(available, current, requested));

    [Fact]
    public void GameModeBootFieldsRoundTripThroughSourceGeneratedJson()
    {
        var original = new AppConfig { GameModeBootEnabled = false, ExplorerLogonSettleMs = 250 };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.False(restored.GameModeBootEnabled);
        Assert.Equal(250, restored.ExplorerLogonSettleMs);
    }

    [Fact]
    public void NormalizeDropsNullLaunchWrapperEntriesAndRepairsTheirStrings()
    {
        var config = new AppConfig
        {
            LaunchWrappers =
            [
                null!,
                new LaunchWrapperConfig
                {
                    AppId = 7,
                    OriginalTarget = null!,
                    OriginalLaunchOptions = null!,
                    OriginalStartDir = null!,
                    Name = null!,
                    CustomActionPath = null!,
                    CustomArguments = null!,
                },
            ],
        };

        var normalized = ConfigStore.Normalize(config);

        var wrapper = Assert.Single(normalized.LaunchWrappers);
        Assert.Equal(7, wrapper.AppId);
        Assert.Equal("", wrapper.OriginalTarget);
        Assert.Equal("", wrapper.OriginalLaunchOptions);
        Assert.Equal("", wrapper.OriginalStartDir);
        Assert.Equal("", wrapper.Name);
        Assert.Equal("", wrapper.CustomActionPath);
        Assert.Equal("", wrapper.CustomArguments);
    }

    /// <summary>The upgrade guarantee. Every config.json written before Steam Input
    /// Management existed omits the property, and those devices must come up with the
    /// shim deploying - otherwise an upgrade silently costs them controller
    /// navigation in the overlay.</summary>
    [Fact]
    public void AConfigWrittenBeforeSteamInputManagementDeserializesWithItOn()
    {
        var restored = JsonSerializer.Deserialize(
            """{"SteamInputLeaseEnabled":true}""", ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.True(restored!.SteamInputManagementEnabled);
        Assert.Equal(0, restored.QuickSetupRevision);
    }

    [Fact]
    public void AnExplicitlyDisabledSteamInputManagementSurvivesARoundTrip()
    {
        var original = new AppConfig { SteamInputManagementEnabled = false };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.False(restored!.SteamInputManagementEnabled);
    }

    [Fact]
    public void QuickSetupIsOfferedUntilItsCurrentRevisionHasBeenAnswered()
    {
        var config = new AppConfig();
        Assert.True(QuickSetup.ShouldShow(config));

        QuickSetup.MarkCompleted(config);

        Assert.False(QuickSetup.ShouldShow(config));
        Assert.Equal(QuickSetup.CurrentRevision, config.QuickSetupRevision);
    }

    /// <summary>A device that answered an older revision is asked once more, which is
    /// the whole reason the stamp is an int rather than a bool.</summary>
    [Fact]
    public void QuickSetupIsOfferedAgainWhenANewerRevisionAddsSettings()
    {
        var config = new AppConfig { QuickSetupRevision = QuickSetup.CurrentRevision - 1 };

        Assert.True(QuickSetup.ShouldShow(config));
    }

    /// <summary>The opt-out has to survive a round trip on its own: it is read once
    /// when a focused surface opens, so a value that failed to persist would silently
    /// re-enable the lease at the next overlay open rather than at some visible moment.
    /// </summary>
    [Fact]
    public void AnExplicitlyDisabledSteamInputLeaseSurvivesARoundTrip()
    {
        var original = new AppConfig { SteamInputLeaseEnabled = false };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.False(restored!.SteamInputLeaseEnabled);
        Assert.True(restored.SteamInputManagementEnabled);
    }
}
