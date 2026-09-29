using System.IO.Pipes;

namespace WSGM.Tests;

/// <summary>
/// The disposal behaviour the game-mode transition depends on.
/// </summary>
/// <remarks>
/// Entering game mode installs a fresh shell anchor and retires the previous one. Retiring it flows
/// through <c>StreamWriter.Dispose</c>, which flushes, and a flush to a pipe whose peer has already
/// exited throws <c>IOException: IO_PipeBroken</c>. A dead peer is the ordinary state for an anchor
/// being retired, so that throw escaping disposal aborted the entire transition: WSGM logged
/// "Game-mode transition failed", rolled back, and closed the Big Picture it had just started —
/// leaving the user on the desktop with no way back for the rest of the session.
/// <para>
/// These tests pin the primitive rather than the anchor, because the anchor owns a live child
/// process and cannot be constructed in a unit test. What broke was the assumption about flushing,
/// and that is exactly what is asserted here.
/// </para>
/// </remarks>
public sealed class ShellAnchorDisposalTests
{
    [Fact]
    public async Task DisposingAWriterOverAPipeWhosePeerHasGoneStillThrows()
    {
        // The premise. If this ever stops throwing the guard is redundant, and whoever removes it
        // should have to delete this test deliberately rather than discover the behaviour by
        // shipping it.
        (NamedPipeServerStream server, NamedPipeClientStream client) = await ConnectedPairAsync();
        StreamWriter writer = new(client) { AutoFlush = false };
        await writer.WriteLineAsync("queued");
        await server.DisposeAsync();

        Assert.Throws<IOException>(writer.Dispose);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task TheBrokenPipeFamilyIsWhatDisposalHasToTolerate()
    {
        // The guard catches IOException and ObjectDisposedException and nothing else. Both are
        // reachable from releasing a pipe-backed resource whose peer has gone; anything else is a
        // real defect and must still surface.
        (NamedPipeServerStream server, NamedPipeClientStream client) = await ConnectedPairAsync();
        StreamWriter writer = new(client) { AutoFlush = false };
        await writer.WriteLineAsync("queued");
        await server.DisposeAsync();

        Exception thrown = Record.Exception(writer.Dispose)!;
        Assert.NotNull(thrown);
        Assert.True(
            thrown is IOException or ObjectDisposedException,
            $"disposal threw {thrown.GetType().Name}, which the anchor's guard would not catch.");
        await client.DisposeAsync();
    }

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)>
        ConnectedPairAsync()
    {
        string name = "WSGM.Tests.AnchorDisposal." + Guid.NewGuid().ToString("N");
        NamedPipeServerStream server = new(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        NamedPipeClientStream client = new(
            ".",
            name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        Task waiting = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await waiting;
        return (server, client);
    }
}
