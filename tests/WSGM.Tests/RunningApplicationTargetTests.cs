using WSGM.Shell;

namespace WSGM.Tests;

public sealed class RunningApplicationTargetTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"wsgm-running-target-{Guid.NewGuid():N}");

    public RunningApplicationTargetTests() => Directory.CreateDirectory(_tempDirectory);

    [Fact]
    public void KnownSteamAppWithoutExecutableUsesIdentityButLeavesRtssGlobal()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        RunningApplicationTargetSnapshot initial = RunningApplicationTargetSnapshot.Initial(now);

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            initial,
            new SteamRunningAppObservation(true, [3280350], 7, null),
            new SteamRunningAppProfile(null, null, "Executable unavailable."),
            now);

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Equal("steam:3280350", target.ApplicationId);
        Assert.Equal((uint)3280350, target.SteamAppId);
        Assert.Null(target.RtssProfileName);
        Assert.Equal(1, target.Generation);
    }

    [Fact]
    public void ExitReturnsToGlobalWithoutInheritingPreviousApplication()
    {
        DateTimeOffset started = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        RunningApplicationTargetSnapshot active = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(started),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(@"D:\Games\game.exe", "game.exe", null),
            started);

        RunningApplicationTargetSnapshot exited = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(true, [], 3, null),
            null,
            started.AddMinutes(1));

        Assert.Equal(RunningApplicationTargetState.Global, exited.State);
        Assert.Null(exited.ApplicationId);
        Assert.Null(exited.SteamAppId);
        Assert.Null(exited.ExecutablePath);
        Assert.Null(exited.RtssProfileName);
        Assert.Equal(2, exited.Generation);
    }

    [Fact]
    public void UnreachableAndAmbiguousObservationsClearThePreviousTarget()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        RunningApplicationTargetSnapshot active = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(@"D:\Games\game.exe", "game.exe", null),
            now);

        RunningApplicationTargetSnapshot unavailable = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(false, [], 0, "CEF unavailable."),
            null,
            now.AddSeconds(1));
        RunningApplicationTargetSnapshot ambiguous = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(true, [42, 99], 3, null),
            null,
            now.AddSeconds(1));

        Assert.Equal(RunningApplicationTargetState.Unavailable, unavailable.State);
        Assert.Null(unavailable.ApplicationId);
        Assert.Null(unavailable.RtssProfileName);
        Assert.Equal(RunningApplicationTargetState.Ambiguous, ambiguous.State);
        Assert.Null(ambiguous.ApplicationId);
        Assert.Null(ambiguous.RtssProfileName);
    }

    [Fact]
    public void SourceGenerationReportsAStopStartEvenWhenTheAppIdIsTheSame()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        SteamRunningAppProfile profile = new(@"D:\Games\game.exe", "game.exe", null);
        RunningApplicationTargetSnapshot first = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42], 4, null),
            profile,
            now);

        RunningApplicationTargetSnapshot restarted = RunningApplicationTargetProjection.Apply(
            first,
            new SteamRunningAppObservation(true, [42], 6, null),
            profile,
            now.AddSeconds(1));

        Assert.Equal(first.Generation + 1, restarted.Generation);
        Assert.Equal(6, restarted.SourceGeneration);
    }

    [Fact]
    public void ExistingDirectShortcutYieldsOnlyItsExecutableProfileName()
    {
        string executable = Path.Combine(_tempDirectory, "shortcut-game.exe");
        File.WriteAllText(executable, "fixture");

        SteamRunningAppProfile profile = SteamRunningApplicationProbe.NormalizeShortcutTarget(
            $"\"{executable}\"");

        Assert.Equal(Path.GetFullPath(executable), profile.ExecutablePath);
        Assert.Equal("shortcut-game.exe", profile.RtssProfileName);
        Assert.Null(profile.Diagnostic);
    }

    [Fact]
    public void AnExistingAbsoluteInstallFolderBecomesPairingEvidence()
    {
        SteamRunningAppProfile profile =
            SteamRunningApplicationProbe.NormalizeInstallFolder(_tempDirectory);

        Assert.Equal(Path.GetFullPath(_tempDirectory), profile.InstallFolder);
        Assert.Null(profile.RtssProfileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"steamapps\common\Game")]
    [InlineData(@"Q:\definitely\not\present")]
    public void UntruthfulInstallFoldersProduceNoPairingEvidence(string folder)
    {
        SteamRunningAppProfile profile =
            SteamRunningApplicationProbe.NormalizeInstallFolder(folder);

        Assert.Null(profile.InstallFolder);
        Assert.NotNull(profile.Diagnostic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.exe")]
    [InlineData(@"C:\Games\not-a-profile.dll")]
    [InlineData(@"C:\Program Files\WSGM\WSGM.Launch.exe")]
    public void UntruthfulShortcutTargetsNeverBecomeRtssProfiles(string target)
    {
        SteamRunningAppProfile profile = SteamRunningApplicationProbe.NormalizeShortcutTarget(target);

        Assert.Null(profile.ExecutablePath);
        Assert.Null(profile.RtssProfileName);
        Assert.NotNull(profile.Diagnostic);
    }

    [Fact]
    public void UnresolvedShortcutProfileIsRetriedAfterItsBackoff()
    {
        uint shortcutAppId = 0x8000002A;
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        SteamRunningAppProfile unresolved = new(null, null, "Transient CEF failure.");

        Assert.False(RunningApplicationMonitor.ShouldResolveProfile(
            shortcutAppId,
            shortcutAppId,
            unresolved,
            now,
            now.AddSeconds(1)));
        Assert.True(RunningApplicationMonitor.ShouldResolveProfile(
            shortcutAppId,
            shortcutAppId,
            unresolved,
            now.AddSeconds(1),
            now.AddSeconds(1)));
    }

    [Fact]
    public void ForegroundApplicationSuppliesTheIdentitySteamDoesNotHave()
    {
        // The whole point of the second source: on the desktop, or for a title Steam never
        // launched, per-application policy still has something to key on.
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [], 3, null),
            null,
            now,
            new ForegroundApplicationObservation("Cyberpunk2077.exe"));

        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("process:cyberpunk2077.exe", target.ApplicationId);
        Assert.Equal("Cyberpunk2077.exe", target.RtssProfileName);
        Assert.Null(target.SteamAppId);
    }

    [Fact]
    public void SteamsIdentityOutranksTheForegroundWindow()
    {
        // Alt-tabbing out of a running Steam game must not retarget its profile: Steam's identity
        // is the one the launch went through and the one the RTSS profile was resolved from.
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(@"D:\Games\game.exe", "game.exe", null),
            now,
            new ForegroundApplicationObservation("chrome.exe"));

        Assert.Equal("steam:42", target.ApplicationId);
        Assert.Equal("game.exe", target.RtssProfileName);
    }

    [Fact]
    public void ForegroundSuppliesOrdinarySteamGamesMissingRtssProfile()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(
                null,
                null,
                "Steam exposes no executable.",
                @"D:\SteamLibrary\steamapps\common\Game"),
            now,
            new ForegroundApplicationObservation(
                "game.exe",
                @"D:\SteamLibrary\steamapps\common\Game\bin\game.exe"));

        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("steam:42", target.ApplicationId);
        Assert.Equal((uint)42, target.SteamAppId);
        Assert.Equal("game.exe", target.RtssProfileName);
        Assert.Equal(
            @"D:\SteamLibrary\steamapps\common\Game\bin\game.exe",
            target.ExecutablePath);
    }

    [Fact]
    public void AForegroundOutsideTheInstallFolderNeverBecomesTheGamesProfile()
    {
        // The bug this rule exists for: a terminal focused while a store title was resolving became
        // HITMAN 3's sticky RTSS target, and the frame limit landed on WindowsTerminal.exe
        // (device-observed 2026-09-02).
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(
                null,
                null,
                "Steam exposes no executable.",
                @"D:\SteamLibrary\steamapps\common\Game"),
            now,
            new ForegroundApplicationObservation(
                "WindowsTerminal.exe",
                @"C:\Program Files\WindowsApps\Terminal\WindowsTerminal.exe"));

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Null(target.RtssProfileName);
    }

    [Fact]
    public void AStoreTitleWithoutAKnownInstallFolderStaysIdentityOnly()
    {
        // No folder means no proof; a bare foreground name pairing here is how the wrong
        // application captured a game's profile for its whole run.
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(null, null, "Install folder still resolving."),
            now,
            new ForegroundApplicationObservation("game.exe", @"D:\Games\game.exe"));

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Null(target.RtssProfileName);
    }

    [Fact]
    public void AnUnresolvedShortcutStillTakesTheForegroundName()
    {
        // A shortcut has no install folder to check, and its target resolution normally names the
        // executable outright; the rare unresolved one keeps the name-based fill.
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [0x8000002A], 2, null),
            new SteamRunningAppProfile(null, null, "The shortcut target is a script."),
            now,
            new ForegroundApplicationObservation("game.exe"));

        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("game.exe", target.RtssProfileName);
    }

    [Fact]
    public void ForegroundResolvedSteamProfileSurvivesAltTab()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        SteamRunningAppObservation observation = new(true, [42], 2, null);
        SteamRunningAppProfile unresolved = new(
            null,
            null,
            "Steam exposes no executable.",
            @"D:\SteamLibrary\steamapps\common\Game");
        RunningApplicationTargetSnapshot game = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            observation,
            unresolved,
            now,
            new ForegroundApplicationObservation(
                "game.exe",
                @"D:\SteamLibrary\steamapps\common\Game\game.exe"));

        RunningApplicationTargetSnapshot altTabbed = RunningApplicationTargetProjection.Apply(
            game,
            observation,
            unresolved,
            now.AddSeconds(1),
            new ForegroundApplicationObservation(
                "chrome.exe",
                @"C:\Program Files\Google\Chrome\chrome.exe"));

        Assert.Equal("steam:42", altTabbed.ApplicationId);
        Assert.Equal("game.exe", altTabbed.RtssProfileName);
        Assert.Equal(game.Generation, altTabbed.Generation);
    }

    [Fact]
    public void ALauncherHandsThePairingToTheGameProcessFromTheSameFolder()
    {
        // A launcher takes focus first and validly pairs; when the game process from the same
        // install folder comes to the front, the profile follows it rather than staying on the
        // launcher for the whole run.
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        SteamRunningAppObservation observation = new(true, [42], 2, null);
        SteamRunningAppProfile unresolved = new(
            null,
            null,
            "Steam exposes no executable.",
            @"D:\SteamLibrary\steamapps\common\Game");
        RunningApplicationTargetSnapshot launcher = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            observation,
            unresolved,
            now,
            new ForegroundApplicationObservation(
                "launcher.exe",
                @"D:\SteamLibrary\steamapps\common\Game\launcher.exe"));

        RunningApplicationTargetSnapshot game = RunningApplicationTargetProjection.Apply(
            launcher,
            observation,
            unresolved,
            now.AddSeconds(5),
            new ForegroundApplicationObservation(
                "game.exe",
                @"D:\SteamLibrary\steamapps\common\Game\bin\game.exe"));

        Assert.Equal("launcher.exe", launcher.RtssProfileName);
        Assert.Equal("game.exe", game.RtssProfileName);
        Assert.Equal("steam:42", game.ApplicationId);
    }

    [Fact]
    public void AmbiguousSteamStateIsNotBrokenByTheForegroundWindow()
    {
        // The foreground says which window has focus, not which of two running games the user
        // means; choosing one here would write a power limit against the other.
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42, 43], 2, null),
            null,
            now,
            new ForegroundApplicationObservation("game.exe"));

        Assert.Equal(RunningApplicationTargetState.Ambiguous, target.State);
        Assert.Null(target.ApplicationId);
    }

    [Fact]
    public void AnUnreachableSteamStaysUnavailableRatherThanGuessingFromFocus()
    {
        // Unavailable means the observation failed. Publishing an identity from focus would claim
        // knowledge WSGM does not have about whether a game is running.
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(false, [], 0, "Steam is unreachable."),
            null,
            now,
            new ForegroundApplicationObservation("game.exe"));

        Assert.Equal(RunningApplicationTargetState.Unavailable, target.State);
        Assert.Null(target.RtssProfileName);
    }

    [Theory]
    [InlineData("wsgm.exe")]
    [InlineData("explorer.exe")]
    [InlineData("readme.txt")]
    [InlineData("")]
    public void ForegroundWindowsThatAreNotApplicationsLeavePolicyGlobal(string executable)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [], 3, null),
            null,
            now,
            new ForegroundApplicationObservation(executable));

        Assert.Equal(RunningApplicationTargetState.Global, target.State);
        Assert.Null(target.ApplicationId);
    }

    [Fact]
    public void ReturningToTheSameForegroundApplicationDoesNotChurnTheGeneration()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        SteamRunningAppObservation idle = new(true, [], 3, null);
        ForegroundApplicationObservation foreground = new("game.exe");

        RunningApplicationTargetSnapshot first = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            idle,
            null,
            now,
            foreground);
        RunningApplicationTargetSnapshot second = RunningApplicationTargetProjection.Apply(
            first,
            idle,
            null,
            now.AddSeconds(2),
            foreground);

        Assert.Equal(first.Generation, second.Generation);
    }

    [Fact]
    public async Task DeliberatelyDisabledCefStillAllowsForegroundApplicationPolicy()
    {
        await using var transport = new DisabledTransport();
        var probe = new SteamRunningApplicationProbe(transport);
        SteamRunningAppObservation observation = await probe.ObserveAsync(CancellationToken.None);

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(DateTimeOffset.UtcNow),
            observation,
            null,
            DateTimeOffset.UtcNow,
            new ForegroundApplicationObservation("game.exe"));

        Assert.True(observation.Reachable);
        Assert.Empty(observation.AppIds);
        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("game.exe", target.RtssProfileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class DisabledTransport : ISteamUiTransport
    {
        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged
        {
            add { }
            remove { }
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SteamUiEvaluationResult.Unavailable(
                "Steam CEF integration disabled in settings.",
                default));

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() => [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
