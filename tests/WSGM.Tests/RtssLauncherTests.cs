using WSGM.Core;

namespace WSGM.Tests;

/// <summary>
/// When WSGM may start RTSS for itself, and when it must not.
/// </summary>
/// <remarks>
/// WSGM needs RTSS for the frame limit, the performance overlay and AutoTDP's frametimes, and on a
/// service boot WSGM runs before RTSS's own tray entry does — so a machine with RTSS installed and
/// working still came up with performance controls unavailable purely because of start order.
/// <para>
/// Every test here injects the start callback, so no test ever launches a process.
/// </para>
/// </remarks>
public sealed class RtssLauncherTests
{
    [Fact]
    public void NotRunningIsTheOneStateWorthStarting()
    {
        // Discovery has already accepted the installation and found no process. That is the only
        // unavailable state a launch actually fixes.
        Assert.True(RtssLauncher.ShouldStart(Probe(RtssAvailability.NotRunning), enabled: true));
    }

    [Fact]
    public void NoOtherStateStartsAnything()
    {
        // Starting a program because WSGM could not identify it would be exactly the wrong response
        // to "incompatible", and there is nothing to start when it is already ready.
        // A loop rather than [Theory] because the availability enum is internal.
        RtssAvailability[] others =
        [
            RtssAvailability.NotInstalled,
            RtssAvailability.Incompatible,
            RtssAvailability.Degraded,
            RtssAvailability.Unknown,
            RtssAvailability.AdapterUnavailable,
            RtssAvailability.Ready,
        ];
        foreach (RtssAvailability availability in others)
        {
            Assert.False(RtssLauncher.ShouldStart(Probe(availability), enabled: true));
        }
    }

    [Fact]
    public void PerformanceControlSwitchedOffStartsNothing()
    {
        // A user who turned the feature off has not asked WSGM to launch a background program.
        Assert.False(RtssLauncher.ShouldStart(Probe(RtssAvailability.NotRunning), enabled: false));
    }

    [Fact]
    public void AProbeWithNoVerifiedExecutableStartsNothing()
    {
        // The path comes from discovery, which only accepts a signed RTSS under a protected install
        // root. Without one there is nothing WSGM is willing to launch.
        RtssProbe probe = Probe(RtssAvailability.NotRunning) with { ExecutablePath = null };

        Assert.False(RtssLauncher.ShouldStart(probe, enabled: true));
    }

    [Fact]
    public async Task ItStartsTheExactExecutableDiscoveryVerified()
    {
        List<string> started = [];
        RtssLauncher launcher = new(path =>
        {
            started.Add(path);
            return Task.FromResult(true);
        });

        Assert.True(await launcher.TryStartAsync(
            Probe(RtssAvailability.NotRunning),
            enabled: true,
            Cancelled()));

        Assert.Equal([@"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe"], started);
    }

    [Fact]
    public async Task WithinTheCooldownItTriesOnceRatherThanEveryPoll()
    {
        // The probe runs on every poll. Immediate retries would mean launching a single-instance
        // program repeatedly, which at best wastes work and at worst produces the "multiple
        // processes match" case discovery already treats as degraded.
        int starts = 0;
        TestTimeProvider clock = new();
        RtssLauncher launcher = new(
            _ =>
            {
                starts++;
                return Task.FromResult(true);
            },
            clock);

        RtssProbe probe = Probe(RtssAvailability.NotRunning);
        Assert.True(await launcher.TryStartAsync(probe, enabled: true, Cancelled()));
        Assert.False(await launcher.TryStartAsync(probe, enabled: true, Cancelled()));
        clock.Now += RtssLauncher.RestartCooldown - TimeSpan.FromSeconds(1);
        Assert.False(await launcher.TryStartAsync(probe, enabled: true, Cancelled()));

        Assert.Equal(1, starts);
        Assert.True(launcher.Attempted);
    }

    [Fact]
    public async Task AnRtssClosedByTheUserIsStartedAgainAfterTheCooldown()
    {
        // RTSS's window has no close-to-tray, so one accidental X used to end the frame limit, OSD
        // and AutoTDP frametimes for the rest of the session. A later NotRunning probe past the
        // cooldown starts it again; the probe state already guarantees no second copy exists.
        int starts = 0;
        TestTimeProvider clock = new();
        RtssLauncher launcher = new(
            _ =>
            {
                starts++;
                return Task.FromResult(true);
            },
            clock);

        RtssProbe probe = Probe(RtssAvailability.NotRunning);
        Assert.True(await launcher.TryStartAsync(probe, enabled: true, Cancelled()));
        clock.Now += RtssLauncher.RestartCooldown;
        Assert.True(await launcher.TryStartAsync(probe, enabled: true, Cancelled()));

        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task AFailedStartIsReportedRatherThanThrown()
    {
        // RTSS is a feature WSGM uses, not one it is. A shell that failed to boot because a frame
        // limiter would not start would be a much worse outcome than one without a frame limit.
        RtssLauncher launcher = new(_ => throw new InvalidOperationException("access denied"));

        Assert.False(await launcher.TryStartAsync(
            Probe(RtssAvailability.NotRunning),
            enabled: true,
            Cancelled()));
    }

    [Fact]
    public async Task AStartThatCreatedNoProcessIsNotReportedAsSuccess()
    {
        RtssLauncher launcher = new(_ => Task.FromResult(false));

        Assert.False(await launcher.TryStartAsync(
            Probe(RtssAvailability.NotRunning),
            enabled: true,
            Cancelled()));
    }

    /// <summary>Already-cancelled, so the settle delay returns at once instead of waiting.</summary>
    private static CancellationToken Cancelled() => new(canceled: true);

    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-02T12:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static RtssProbe Probe(RtssAvailability availability) => new(
        availability,
        "7.3.7",
        @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe",
        0,
        null,
        "test");
}
