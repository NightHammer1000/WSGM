using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The one process-level shutdown policy selected before Avalonia teardown begins.</summary>
internal enum ApplicationShutdownReason
{
    Normal,
    Update,
    SessionEnd,
    Uninstall,
}

/// <summary>Bounded outcome of the process-owned graceful shutdown attempt.</summary>
internal enum ApplicationShutdownOutcome
{
    Clean,
    Unverified,
    TimedOut,
    Failed,
}

/// <summary>Cross-bootstrap marker used by one-shot exit sources before lifetime shutdown.</summary>
internal static class ApplicationShutdownRequest
{
    private static int _reason;

    internal static void Request(ApplicationShutdownReason reason)
    {
        while (true)
        {
            int current = Volatile.Read(ref _reason);
            var currentReason = (ApplicationShutdownReason)current;
            if (PriorityFor(currentReason) >= PriorityFor(reason)
                || Interlocked.CompareExchange(ref _reason, (int)reason, current) == current)
            {
                return;
            }
        }
    }

    internal static ApplicationShutdownReason Consume() =>
        (ApplicationShutdownReason)Interlocked.Exchange(
            ref _reason,
            (int)ApplicationShutdownReason.Normal);

    /// <summary>Stops the Avalonia classic desktop lifetime when one is running — the one exit
    /// door shared by installer exit requests and the session-end path.</summary>
    internal static void ShutdownLifetime()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }

    private static int PriorityFor(ApplicationShutdownReason reason) => reason switch
    {
        ApplicationShutdownReason.Uninstall => 3,
        ApplicationShutdownReason.Update => 2,
        ApplicationShutdownReason.SessionEnd => 1,
        _ => 0,
    };
}

/// <summary>
/// Enforces the single outer process-shutdown deadline. Subsystems retain their protocol phase
/// budgets; this owner prevents any collection of cleanup failures from holding installer or
/// session termination indefinitely.
/// </summary>
internal static class ApplicationShutdownCoordinator
{
    internal static int ExitCodeFor(ApplicationShutdownOutcome outcome) =>
        outcome is ApplicationShutdownOutcome.Clean ? 0 : 1;

    internal static TimeSpan BudgetFor(ApplicationShutdownReason reason) => reason switch
    {
        ApplicationShutdownReason.Update => TimeSpan.FromSeconds(10),
        ApplicationShutdownReason.SessionEnd => TimeSpan.FromSeconds(5),
        ApplicationShutdownReason.Uninstall => TimeSpan.FromSeconds(20),
        _ => TimeSpan.FromSeconds(15),
    };

    internal static Task<ApplicationShutdownOutcome> ShutdownAsync(
        Func<DateTimeOffset, ValueTask> shutdownAsync,
        ApplicationShutdownReason reason,
        TimeSpan? budgetOverride = null)
        => ShutdownAsync(
            shutdownAsync,
            reason,
            budgetOverride,
            static () => DateTimeOffset.UtcNow,
            static timeout => Task.Delay(timeout));

    /// <summary>Test seam for the process deadline clock and timer. Production always supplies
    /// UTC and <see cref="Task.Delay(TimeSpan)"/> through the overload above.</summary>
    internal static async Task<ApplicationShutdownOutcome> ShutdownAsync(
        Func<DateTimeOffset, ValueTask> shutdownAsync,
        ApplicationShutdownReason reason,
        TimeSpan? budgetOverride,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(shutdownAsync);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(delayAsync);
        TimeSpan budget = budgetOverride ?? BudgetFor(reason);
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetOverride));
        }

        DateTimeOffset deadline = utcNow().Add(budget);
        Task cleanup;
        try
        {
            cleanup = shutdownAsync(deadline).AsTask();
        }
        catch (Exception ex)
        {
            Log.Error($"Application shutdown could not start ({reason})", ex);
            return ApplicationShutdownOutcome.Failed;
        }

        try
        {
            TimeSpan remaining = deadline - utcNow();
            if (cleanup.IsCompleted)
            {
                // Observe a completed cleanup before classifying the outer deadline. A subsystem
                // TimeoutException is still an unverified cleanup result, while a successful
                // synchronous cleanup that consumed the complete owner budget is an outer timeout.
                await cleanup.ConfigureAwait(false);
                if (remaining <= TimeSpan.Zero)
                {
                    ReportTimeout(reason, budget);
                    return ApplicationShutdownOutcome.TimedOut;
                }

                return ApplicationShutdownOutcome.Clean;
            }

            if (remaining <= TimeSpan.Zero)
            {
                ObserveLateCleanup(cleanup, reason);
                ReportTimeout(reason, budget);
                return ApplicationShutdownOutcome.TimedOut;
            }

            Task timeout = delayAsync(remaining);
            Task completed = await Task.WhenAny(cleanup, timeout).ConfigureAwait(false);
            if (!ReferenceEquals(completed, cleanup))
            {
                ObserveLateCleanup(cleanup, reason);
                ReportTimeout(reason, budget);
                return ApplicationShutdownOutcome.TimedOut;
            }

            // Await the cleanup task itself after it wins. In particular, a cleanup task that
            // faults with TimeoutException is an unverified subsystem result, not proof that this
            // process owner's outer timer elapsed.
            await cleanup.ConfigureAwait(false);
            return ApplicationShutdownOutcome.Clean;
        }
        catch (Exception ex)
        {
            Log.Error($"Application shutdown was incomplete ({reason})", ex);
            return ApplicationShutdownOutcome.Unverified;
        }
    }

    private static void ReportTimeout(ApplicationShutdownReason reason, TimeSpan budget) =>
        Log.Warn(
            $"Application shutdown exceeded the {budget.TotalSeconds:0.#} s {reason} budget; "
            + "process exit will release process-owned resources and recovery will reconcile next start.");

    private static void ObserveLateCleanup(Task cleanup, ApplicationShutdownReason reason) =>
        _ = ObserveAsync(cleanup, reason);

    private static async Task ObserveAsync(Task cleanup, ApplicationShutdownReason reason)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"Application shutdown failed after its outer deadline ({reason})", ex);
        }
    }
}
