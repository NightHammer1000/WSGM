using WSGM.Core;

namespace WSGM.Tests;

public sealed class RtssDiscoveryTests
{
    [Fact]
    public void VerifiedRegistrationApiAndProcess_AreAcceptedWithoutLoadingRtssCode()
    {
        var environment = FakeDiscoveryEnvironment.Valid();

        RtssProbe probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.AdapterUnavailable, probe.Availability);
        Assert.Equal("7.3.7", probe.Version);
        Assert.NotEqual(0, probe.Generation);
    }

    [Fact]
    public void SimilarlyNamedProcessOutsideVerifiedInstall_IsIgnored()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.Processes =
        [
            new RtssProcessIdentity(
                999,
                @"C:\Users\player\Downloads\RTSS.exe",
                DateTimeOffset.UnixEpoch),
        ];

        RtssProbe probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.NotRunning, probe.Availability);
        Assert.Equal(0L, probe.Generation);
    }

    [Fact]
    public void OldRegistration_IsIncompatible()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.Records =
        [
            environment.Records[0] with { DisplayVersion = "7.2.3" },
        ];

        RtssProbe probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.Incompatible, probe.Availability);
    }

    [Fact]
    public void MissingProfileApiExport_IsIncompatible()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.ApiIdentity = environment.ApiIdentity with
        {
            Exports = new HashSet<string>(StringComparer.Ordinal)
            {
                "LoadProfile",
                "SaveProfile",
                "GetProfileProperty",
                "SetProfileProperty",
            },
        };

        RtssProbe probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.Incompatible, probe.Availability);
    }

    [Fact]
    public void UnprotectedRegistrationPath_IsIncompatible()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.Records =
        [
            environment.Records[0] with
            {
                InstallLocation = @"C:\Users\player\AppData\Local\RTSS",
                UninstallString = null,
                DisplayIcon = null,
            },
        ];

        RtssProbe probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.Incompatible, probe.Availability);
    }

    private sealed class FakeDiscoveryEnvironment : IRtssDiscoveryEnvironment
    {
        private const string InstallRoot = @"C:\Program Files (x86)\RivaTuner Statistics Server";

        public IReadOnlyList<RtssInstallRecord> Records { get; set; } = [];

        public RtssFileIdentity ExecutableIdentity { get; set; } = new(
            true,
            500_000,
            "RTSS",
            "7.3.5.28314",
            false,
            new HashSet<string>(StringComparer.Ordinal));

        public RtssFileIdentity ApiIdentity { get; set; } = new(
            true,
            1_400_000,
            null,
            null,
            Environment.Is64BitProcess,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "LoadProfile",
                "SaveProfile",
                "GetProfileProperty",
                "SetProfileProperty",
                "UpdateProfiles",
            });

        public IReadOnlyList<RtssProcessIdentity> Processes { get; set; } = [];

        public IReadOnlyList<string> ProtectedInstallRoots { get; } =
        [
            @"C:\Program Files",
            @"C:\Program Files (x86)",
        ];

        public IReadOnlyList<RtssInstallRecord> ReadInstallRecords() => Records;

        public RtssFileIdentity ReadFileIdentity(string path) => path.EndsWith(
            Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll",
            StringComparison.OrdinalIgnoreCase)
            ? ApiIdentity
            : ExecutableIdentity;

        public IReadOnlyList<RtssProcessIdentity> ReadProcesses() => Processes;

        public static FakeDiscoveryEnvironment Valid() => new()
        {
            Records =
            [
                new RtssInstallRecord(
                    "RivaTuner Statistics Server 7.3.7",
                    "7.3.7",
                    "Unwinder",
                    string.Empty,
                    $"\"{InstallRoot}\\uninstall.exe\"",
                    $"\"{InstallRoot}\\uninstall.exe\""),
            ],
            Processes =
            [
                new RtssProcessIdentity(
                    321,
                    $"{InstallRoot}\\RTSS.exe",
                    DateTimeOffset.UnixEpoch),
            ],
        };
    }
}

public sealed class PerformancePolicyResolverTests
{
    [Fact]
    public void ApplicationOverridesFallBackPerPropertyToGlobalValues()
    {
        var policy = new PerformancePolicy(
            new PerformanceValues(60, 1),
            [new PerformanceApplicationPolicy("steam:7", "game.exe", new PerformanceValues(null, 3))]);

        (
            PerformanceValues values,
            PerformancePolicyLayer frameLayer,
            PerformancePolicyLayer overlayLayer) = PerformancePolicyResolver.Resolve(
            policy,
            new PerformanceApplicationTarget("steam:7", 7, "game.exe"));

        Assert.Equal(new PerformanceValues(60, 3), values);
        Assert.Equal(PerformancePolicyLayer.Global, frameLayer);
        Assert.Equal(PerformancePolicyLayer.Application, overlayLayer);
    }

    [Fact]
    public void AutomaticEditUsesApplicationOnlyWhenAnOverrideAlreadyExists()
    {
        var target = new PerformanceApplicationTarget("steam:7", 7, "game.exe");
        var globalOnly = new PerformancePolicy(new PerformanceValues(60, 1), []);
        var withOverride = globalOnly with
        {
            Applications =
            [
                new PerformanceApplicationPolicy(
                    "steam:7",
                    "game.exe",
                    new PerformanceValues(45, null)),
            ],
        };

        Assert.Equal(
            PerformancePersistenceTarget.Global,
            PerformancePolicyResolver.ResolveEditTarget(globalOnly, target));
        Assert.Equal(
            PerformancePersistenceTarget.Application,
            PerformancePolicyResolver.ResolveEditTarget(withOverride, target));
    }

}

public sealed class PerformanceServiceTests
{
    [Fact]
    public void NonzeroOverlayOpensBothCurrentAndGlobalRtssPresentationGates()
    {
        Assert.Equal(
            ["game.exe", string.Empty],
            RtssNativeAdapter.OverlayActivationProfiles(3, "game.exe"));
    }

    [Fact]
    public void GlobalOverlayDoesNotWriteTheSameRtssProfileTwice()
    {
        Assert.Equal(
            [string.Empty],
            RtssNativeAdapter.OverlayActivationProfiles(1, string.Empty));
    }

    [Fact]
    public void OverlayOffDoesNotCloseAnyRtssPresentationGate()
    {
        Assert.Empty(RtssNativeAdapter.OverlayActivationProfiles(0, "game.exe"));
    }

    [Fact]
    public async Task VerifiedReadbackCompletesTheSingleSharedCommand()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            PerformancePersistenceTarget.Automatic,
            "overlay",
            "command-1");

        Assert.Equal(PerformanceCommandPhase.SucceededVerified, command.Phase);
        Assert.Equal(60, service.Current.Desired.FrameLimit);
        Assert.Equal(60, service.Current.Observed.FrameLimit);
        Assert.Equal(PerformanceReadbackQuality.Verified, service.Current.FrameLimitQuality);
        Assert.Single(adapter.Applies);
    }

    [Fact]
    public async Task AdapterBoundsRejectBeforeMutation()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            999,
            PerformancePersistenceTarget.Automatic,
            "qam",
            "command-2");

        Assert.Equal(PerformanceCommandPhase.Rejected, command.Phase);
        Assert.Empty(adapter.Applies);
    }

    [Fact]
    public async Task PersistenceFailureRollsBackDesiredStateBeforeRtssMutation()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = new PerformanceService(
            adapter,
            static (_, _) => Task.FromException(new IOException("disk unavailable")));

        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            PerformancePersistenceTarget.Automatic,
            "overlay",
            "persistence-failure");

        Assert.Equal(PerformanceCommandPhase.Failed, command.Phase);
        Assert.Null(service.Current.Desired.FrameLimit);
        Assert.Empty(adapter.Applies);
        Assert.Equal(RtssAvailability.Ready, service.Current.Probe.Availability);
    }

    [Fact]
    public async Task MissingRtssIsAnIsolatedRejectedFeature()
    {
        await using var adapter = new FakeRtssAdapter
        {
            Probe = FakeRtssAdapter.ReadyProbe with
            {
                Availability = RtssAvailability.NotInstalled,
                Capabilities = null,
                Diagnostic = "RTSS is absent.",
            },
        };
        await using var service = CreateService(adapter);

        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.OverlayLevel,
            2,
            PerformancePersistenceTarget.Automatic,
            "qam",
            "command-3");

        Assert.Equal(PerformanceCommandPhase.Rejected, command.Phase);
        Assert.Equal(RtssAvailability.NotInstalled, service.Current.Probe.Availability);
    }

    [Fact]
    public async Task UnprovenReadbackIsReportedAsAppliedUnverified()
    {
        await using var adapter = new FakeRtssAdapter
        {
            Probe = FakeRtssAdapter.ReadyProbe with
            {
                Capabilities = FakeRtssAdapter.ReadyProbe.Capabilities! with
                {
                    OverlayLevelReadback = false,
                },
            },
        };
        await using var service = CreateService(adapter);

        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.OverlayLevel,
            3,
            PerformancePersistenceTarget.Automatic,
            "overlay",
            "command-4");

        Assert.Equal(PerformanceCommandPhase.AppliedUnverified, command.Phase);
        Assert.Equal(PerformanceReadbackQuality.AppliedUnverified, service.Current.OverlayLevelQuality);
    }

    [Fact]
    public async Task RestartDuringMutationMakesOutcomeIndeterminate()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.OnApply = (request, _) =>
        {
            adapter.Write(request);
            adapter.Probe = adapter.Probe with { Generation = request.Generation + 1 };
            return Task.FromResult(new RtssApplyResult(true, null));
        };
        await using var service = CreateService(adapter);

        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            50,
            PerformancePersistenceTarget.Automatic,
            "overlay",
            "command-5");

        Assert.Equal(PerformanceCommandPhase.Indeterminate, command.Phase);
    }

    [Fact]
    public async Task AdapterTimeoutIsReportedWithoutEscaping()
    {
        await using var adapter = new FakeRtssAdapter
        {
            OnApply = static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new RtssApplyResult(true, null);
            },
        };
        await using var service = new PerformanceService(
            adapter,
            PersistAsync,
            commandTimeout: TimeSpan.FromMilliseconds(100));

        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            PerformancePersistenceTarget.Automatic,
            "qam",
            "command-6");

        Assert.Equal(PerformanceCommandPhase.TimedOut, command.Phase);
    }

    [Fact]
    public async Task ExternalEditIsPublishedAsExternalChange()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.Values[string.Empty] = new PerformanceValues(60, 1);
        await using var service = CreateService(adapter);
        await service.RefreshAsync();

        adapter.Values[string.Empty] = new PerformanceValues(45, 1);
        await service.RefreshAsync();

        Assert.Equal(45, service.Current.Observed.FrameLimit);
        Assert.Equal(PerformanceCommandPhase.ExternalChange, service.Current.Command.Phase);
    }

    [Fact]
    public async Task ReloadedPolicyReconcilesThroughTheSameAdapterPath()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        await service.UpdatePolicyAsync(new PerformancePolicy(new PerformanceValues(55, 2), []));

        Assert.Equal(new PerformanceValues(55, 2), service.Current.Desired);
        Assert.Equal(new PerformanceValues(55, 2), service.Current.Observed);
        Assert.Equal(2, adapter.Applies.Count);
    }

    [Fact]
    public async Task PollingStartsOnlyWhileAClientOwnsAnObservationLease()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = new PerformanceService(
            adapter,
            PersistAsync,
            pollInterval: TimeSpan.FromMilliseconds(250));
        await Task.Delay(50);
        Assert.Equal(0, adapter.ProbeCount);

        using (service.AcquireObservation())
        {
            await adapter.FirstProbe.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, service.ObserverCount);
        }

        Assert.Equal(0, service.ObserverCount);
        Assert.InRange(service.PollInterval, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ApplicationTransitionUsesPerPropertyProfilePrecedence()
    {
        var policy = new PerformancePolicy(
            new PerformanceValues(60, 1),
            [new PerformanceApplicationPolicy("steam:7", "game.exe", new PerformanceValues(null, 3))]);
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter, policy);

        await service.SetTargetAsync(new PerformanceApplicationTarget("steam:7", 7, "game.exe", 123));

        Assert.Equal(new PerformanceValues(60, 3), service.Current.Desired);
        Assert.Contains(adapter.Applies, request => request.RtssProfileName == "game.exe"
            && request.Control == PerformanceControl.FrameLimit
            && request.Value == 60);
        Assert.Contains(adapter.Applies, request => request.RtssProfileName == "game.exe"
            && request.Control == PerformanceControl.OverlayLevel
            && request.Value == 3);
    }

    [Fact]
    public async Task GlobalAndApplicationWritesHaveOnePersistenceSignalEach()
    {
        await using var adapter = new FakeRtssAdapter();
        var policy = new PerformancePolicy(
            new PerformanceValues(60, 1),
            [new PerformanceApplicationPolicy("steam:7", "game.exe", new PerformanceValues(40, 3))]);
        var policies = new List<PerformancePolicy>();
        await using var service = new PerformanceService(
            adapter,
            (persisted, _) =>
            {
                policies.Add(persisted);
                return Task.CompletedTask;
            },
            policy);

        await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            PerformancePersistenceTarget.Automatic,
            "overlay",
            "persistent");
        await service.SetTargetAsync(new PerformanceApplicationTarget("steam:7", 7, "game.exe", 123));
        await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            PerformancePersistenceTarget.Automatic,
            "overlay",
            "application");

        Assert.Equal(2, policies.Count);
        Assert.Equal(60, policies[0].Global.FrameLimit);
        Assert.Equal(45, policies[1].Applications[0].Values.FrameLimit);
        Assert.Equal(45, service.Current.Desired.FrameLimit);
    }

    [Fact]
    public async Task SimultaneousClientsAreSerializedThroughOneAdapterPath()
    {
        await using var adapter = new FakeRtssAdapter();
        adapter.OnApply = async (request, cancellationToken) =>
        {
            int active = Interlocked.Increment(ref adapter.ActiveApplies);
            adapter.MaximumActiveApplies = Math.Max(adapter.MaximumActiveApplies, active);
            try
            {
                await Task.Delay(20, cancellationToken);
                adapter.Write(request);
                return new RtssApplyResult(true, null);
            }
            finally
            {
                Interlocked.Decrement(ref adapter.ActiveApplies);
            }
        };
        await using var service = CreateService(adapter);

        Task<PerformanceCommandState> overlay = service.SetAsync(
            PerformanceControl.FrameLimit,
            50,
            PerformancePersistenceTarget.Automatic,
            "overlay",
            "overlay-command");
        Task<PerformanceCommandState> qam = service.SetAsync(
            PerformanceControl.FrameLimit,
            55,
            PerformancePersistenceTarget.Automatic,
            "qam",
            "qam-command");
        await Task.WhenAll(overlay, qam);

        Assert.Equal(1, adapter.MaximumActiveApplies);
        Assert.Equal(2, adapter.Applies.Count);
        Assert.Contains(service.Current.Observed.FrameLimit, new int?[] { 50, 55 });
        Assert.Equal("qam-command", service.Current.Command.CorrelationId);
    }

    [Fact]
    public async Task AnApplicationWithoutItsOwnProfileIsWrittenThroughTheGlobalProfile()
    {
        // Saving an RTSS profile that does not exist creates it, which sprayed a profile onto
        // every executable that ever took focus (device-observed 2026-09-02). Without a per-game
        // opt-in and without an existing RTSS profile, the global profile carries the value.
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(
            adapter,
            new PerformancePolicy(new PerformanceValues(null, null), []));

        await service.SetTargetAsync(
            new PerformanceApplicationTarget("process:hitman3.exe", null, "HITMAN3.exe"));
        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            PerformancePersistenceTarget.Automatic,
            "qam",
            "no-optin");

        Assert.Equal(PerformanceCommandPhase.SucceededVerified, command.Phase);
        Assert.All(adapter.Applies, request => Assert.Equal(string.Empty, request.RtssProfileName));
        Assert.DoesNotContain("HITMAN3.exe", adapter.Values.Keys);
    }

    [Fact]
    public async Task AnExistingRtssProfileStillReceivesTheEffectiveValues()
    {
        // An RTSS profile that already exists is the stronger RTSS layer: its explicit values
        // would silently override a global write, so the effective values go into it even without
        // a WSGM per-game entry.
        await using var adapter = new FakeRtssAdapter();
        adapter.ExistingProfiles.Add("game.exe");
        await using var service = CreateService(
            adapter,
            new PerformancePolicy(new PerformanceValues(null, null), []));

        await service.SetTargetAsync(
            new PerformanceApplicationTarget("process:game.exe", null, "game.exe"));
        PerformanceCommandState command = await service.SetAsync(
            PerformanceControl.FrameLimit,
            60,
            PerformancePersistenceTarget.Automatic,
            "qam",
            "existing-profile");

        Assert.Equal(PerformanceCommandPhase.SucceededVerified, command.Phase);
        Assert.Contains(adapter.Applies, request => request.RtssProfileName == "game.exe"
            && request.Control == PerformanceControl.FrameLimit
            && request.Value == 60);
    }

    [Fact]
    public async Task InvalidRtssProfileNameNeverReachesAdapter()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(adapter);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:7", 7, @"..\Global")));

        Assert.Empty(adapter.Applies);
    }

    [Fact]
    public async Task IdentityOnlyApplicationDefersRtssWritesUntilForegroundEnrichment()
    {
        await using var adapter = new FakeRtssAdapter();
        await using var service = CreateService(
            adapter,
            new PerformancePolicy(new PerformanceValues(60, 1), []));

        await service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:42", 42, null));
        Assert.Empty(adapter.Applies);
        Assert.Equal(PerformanceCommandPhase.Deferred, service.Current.Command.Phase);

        Assert.True(await service.SetApplicationProfileEnabledAsync(true));
        PerformanceCommandState deferred = await service.SetAsync(
            PerformanceControl.FrameLimit,
            45,
            PerformancePersistenceTarget.Automatic,
            "test",
            "identity-only");
        Assert.Equal(PerformanceCommandPhase.Deferred, deferred.Phase);
        Assert.True(service.Current.ApplicationProfileEnabled);
        Assert.Empty(adapter.Applies);

        await service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:42", 42, "game.exe"));

        Assert.Contains(adapter.Applies, request => request.RtssProfileName == "game.exe"
            && request.Control == PerformanceControl.FrameLimit
            && request.Value == 45);
        Assert.Contains(adapter.Applies, request => request.RtssProfileName == "game.exe"
            && request.Control == PerformanceControl.OverlayLevel
            && request.Value == 1);
    }

    private sealed class FakeRtssAdapter : IRtssAdapter
    {
        public void ApplyOsdCustomization(RtssOsdCustomSettings settings)
        {
            // The fake has no renderer; the service only forwards.
        }

        public static readonly RtssProbe ReadyProbe = new(
            RtssAvailability.Ready,
            "7.3.7",
            @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe",
            1,
            new RtssCapabilities(
                0,
                240,
                new HashSet<int> { 0, 1, 2, 3, 4 },
                true,
                true),
            null);

        public RtssProbe Probe { get; set; } = ReadyProbe;

        public Dictionary<string, PerformanceValues> Values { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = PerformanceValues.Empty,
        };

        /// <summary>Profiles RTSS already holds on disk, as the service would find them.</summary>
        public HashSet<string> ExistingProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool ProfileExists(string rtssProfileName) =>
            rtssProfileName.Length == 0
            || ExistingProfiles.Contains(rtssProfileName)
            || Values.ContainsKey(rtssProfileName);

        public List<RtssApplyRequest> Applies { get; } = [];

        public Func<RtssApplyRequest, CancellationToken, Task<RtssApplyResult>>? OnApply { get; set; }

        public int ActiveApplies;

        public int MaximumActiveApplies;

        public int ProbeCount;

        public TaskCompletionSource<bool> FirstProbe { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref ProbeCount);
            FirstProbe.TrySetResult(true);
            return Task.FromResult(Probe);
        }

        public Task<RtssReadback> ReadAsync(
            string rtssProfileName,
            long generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.TryGetValue(rtssProfileName, out PerformanceValues? values);
            return Task.FromResult(new RtssReadback(
                values ?? PerformanceValues.Empty,
                PerformanceReadbackQuality.Verified,
                PerformanceReadbackQuality.Verified,
                DateTimeOffset.UtcNow));
        }

        public Task<RtssApplyResult> ApplyAsync(
            RtssApplyRequest request,
            CancellationToken cancellationToken)
        {
            if (OnApply is not null)
            {
                return OnApply(request, cancellationToken);
            }

            Write(request);
            return Task.FromResult(new RtssApplyResult(true, null));
        }

        public void Write(RtssApplyRequest request)
        {
            Applies.Add(request);
            Values.TryGetValue(request.RtssProfileName, out PerformanceValues? current);
            Values[request.RtssProfileName] = (current ?? PerformanceValues.Empty).With(
                request.Control,
                request.Value);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static PerformanceService CreateService(
        IRtssAdapter adapter,
        PerformancePolicy? policy = null) => new(adapter, PersistAsync, policy);

    private static Task PersistAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
