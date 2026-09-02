using WSGM.Core;

namespace WSGM.Tests;

public sealed class ConsoleToolTests
{
    [Fact]
    public async Task RunUntilAsync_WaitFaultAfterStart_KillsOwnedTreeAndReturnsUnknown()
    {
        var process = new FaultingConsoleToolProcess();

        Task<ConsoleToolRunOutcome> run = ConsoleTool.RunUntilAsync(
            "inert-test-tool.exe",
            "/Run",
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
            CancellationToken.None,
            _ => process);

        await process.KillRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(run.IsCompleted);
        process.CompleteExit();
        ConsoleToolRunOutcome outcome = await run.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(ConsoleToolRunOutcome.Unknown, outcome);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(2, process.WaitCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RunUntilAsync_CancellationWhileConfirmingKilledProcessExitIsPreserved()
    {
        var process = new FaultingConsoleToolProcess();
        using var cancellation = new CancellationTokenSource();

        Task<ConsoleToolRunOutcome> run = ConsoleTool.RunUntilAsync(
            "inert-test-tool.exe",
            "/Create",
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
            cancellation.Token,
            _ => process);

        await process.KillRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, process.KillCalls);
        Assert.True(process.Disposed);
    }

    private sealed class FaultingConsoleToolProcess : IConsoleToolProcess
    {
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExitCode => throw new InvalidOperationException("No exit code is available.");

        internal int KillCalls { get; private set; }

        internal int WaitCalls { get; private set; }

        internal bool Disposed { get; private set; }

        internal TaskCompletionSource KillRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalls++;
            return WaitCalls == 1
                ? Task.FromException(new InvalidOperationException("Injected wait failure."))
                : _exit.Task.WaitAsync(cancellationToken);
        }

        public void KillTree()
        {
            KillCalls++;
            KillRequested.TrySetResult();
        }

        internal void CompleteExit() => _exit.TrySetResult();

        public void Dispose() => Disposed = true;
    }
}
