using WSGM.Core;

namespace WSGM.Tests;

/// <summary>Task XML generation for the de-elevating scheduled-task launcher.</summary>
public sealed class UnelevatedLauncherTests
{
    [Fact]
    public void DeElevationTaskEscapesExecutableAndArgumentsInXml()
    {
        var xml = UnelevatedLauncher.BuildTaskXml("C:\\A&B\\WSGM.exe", "--open-<wifi>-settings");

        Assert.Contains("<Command>C:\\A&amp;B\\WSGM.exe</Command>", xml);
        Assert.Contains("<Arguments>--open-&lt;wifi&gt;-settings</Arguments>", xml);
    }

    [Fact]
    public void DeElevationTaskUsesInteractiveTokenWithoutAnElevatedRunLevelInUtf16()
    {
        // The three properties invariant 5 rests on: an InteractiveToken principal with
        // NO RunLevel element yields the user's filtered medium-IL token (a RunLevel of
        // HighestAvailable would hand Explorer and the ms-settings one-shot back their
        // elevation), and schtasks rejects anything but the UTF-16 declaration with
        // "cannot switch encoding".
        var xml = UnelevatedLauncher.BuildTaskXml("C:\\WSGM\\WSGM.exe");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.DoesNotContain("<RunLevel>", xml);
    }

    [Fact]
    public async Task ScheduledTaskRunFailure_DeletesWithinTheSameAbsoluteDeadline()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        using var cancellation = new CancellationTokenSource();
        var calls = new List<(string Arguments, DateTimeOffset Deadline, CancellationToken Token)>();

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            calls.Add((arguments, commandDeadline, cancellationToken));
            return Task.FromResult(arguments.StartsWith("/Run", StringComparison.Ordinal)
                ? ConsoleToolRunOutcome.Failed
                : ConsoleToolRunOutcome.Succeeded);
        }

        ScheduledTaskLaunchDisposition disposition =
            await UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                cancellation.Token,
                RunCommand);

        Assert.Equal(ScheduledTaskLaunchDisposition.NotDispatched, disposition);
        Assert.Collection(
            calls,
            call => Assert.StartsWith("/Create", call.Arguments, StringComparison.Ordinal),
            call => Assert.StartsWith("/Run", call.Arguments, StringComparison.Ordinal),
            call => Assert.StartsWith("/Delete", call.Arguments, StringComparison.Ordinal));
        Assert.All(calls, call => Assert.Equal(deadline, call.Deadline));
        Assert.All(calls, call => Assert.Equal(cancellation.Token, call.Token));
    }

    [Fact]
    public async Task ScheduledTaskRunTimeout_PreservesUnknownDispatchBoundary()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        var calls = new List<string>();

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            _ = commandDeadline;
            _ = cancellationToken;
            calls.Add(arguments);
            return Task.FromResult(arguments.StartsWith("/Run", StringComparison.Ordinal)
                ? ConsoleToolRunOutcome.Unknown
                : ConsoleToolRunOutcome.Succeeded);
        }

        ScheduledTaskLaunchDisposition disposition =
            await UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                CancellationToken.None,
                RunCommand);

        Assert.Equal(ScheduledTaskLaunchDisposition.Unknown, disposition);
        Assert.Collection(
            calls,
            call => Assert.StartsWith("/Create", call, StringComparison.Ordinal),
            call => Assert.StartsWith("/Run", call, StringComparison.Ordinal),
            call => Assert.StartsWith("/Delete", call, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledTaskCreateUnknown_AttemptsCleanupWithoutDispatching()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        var calls = new List<string>();

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            _ = commandDeadline;
            _ = cancellationToken;
            calls.Add(arguments);
            return Task.FromResult(arguments.StartsWith("/Create", StringComparison.Ordinal)
                ? ConsoleToolRunOutcome.Unknown
                : ConsoleToolRunOutcome.Succeeded);
        }

        ScheduledTaskLaunchDisposition disposition =
            await UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                CancellationToken.None,
                RunCommand);

        Assert.Equal(ScheduledTaskLaunchDisposition.NotDispatched, disposition);
        Assert.Collection(
            calls,
            call => Assert.StartsWith("/Create", call, StringComparison.Ordinal),
            call => Assert.StartsWith("/Delete", call, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledTaskDeadlineClosesAfterCreate_SkipsRunAndCleanup()
    {
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset deadline = now + TimeSpan.FromSeconds(1);
        var calls = new List<string>();

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            _ = commandDeadline;
            _ = cancellationToken;
            calls.Add(arguments);
            now = deadline;
            return Task.FromResult(ConsoleToolRunOutcome.Succeeded);
        }

        ScheduledTaskLaunchDisposition disposition =
            await UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                CancellationToken.None,
                RunCommand,
                () => now);

        Assert.Equal(ScheduledTaskLaunchDisposition.NotDispatched, disposition);
        Assert.Collection(
            calls,
            call => Assert.StartsWith("/Create", call, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledTaskCancellationAfterCreate_SkipsRunAndCleanup()
    {
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset deadline = now + TimeSpan.FromSeconds(1);
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            _ = commandDeadline;
            Assert.Equal(cancellation.Token, cancellationToken);
            calls.Add(arguments);
            cancellation.Cancel();
            return Task.FromResult(ConsoleToolRunOutcome.Succeeded);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                cancellation.Token,
                RunCommand,
                () => now));

        Assert.Collection(
            calls,
            call => Assert.StartsWith("/Create", call, StringComparison.Ordinal));
    }
}
