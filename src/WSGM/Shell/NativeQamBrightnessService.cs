using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The panel backlight level Steam's brightness slider shows.</summary>
/// <param name="Percent">The level, 0 to 100, read from the panel itself.</param>
internal sealed record SteamBrightnessState(int Percent);

/// <summary>
/// The backend behind the revealed brightness row: reads and writes the panel backlight, and
/// watches it for changes made outside Steam so the slider follows them.
/// </summary>
internal sealed class NativeQamBrightnessService : IDisposable
{
    /// <summary>
    /// Field-rooted for its lifetime and disposed with this service, per the long-lived-callback
    /// rule; the read is one ioctl on a handle opened and closed per poll.
    /// </summary>
    private readonly Timer _poll;
    private readonly Func<bool> _active;
    private readonly Action _publish;
    private int _lastPolled = -1;

    /// <summary>Creates the service and starts the backlight poll.</summary>
    /// <param name="active">Whether the session currently publishes brightness at all.</param>
    /// <param name="publish">Queues a state publication toward Steam.</param>
    internal NativeQamBrightnessService(Func<bool> active, Action publish)
    {
        _active = active;
        _publish = publish;
        _poll = new Timer(OnPoll, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Reads the published brightness state, or nothing when the panel refuses.</summary>
    internal static ValueTask<JsonElement?> ReadPublication() =>
        Backlight.TryReadBrightness(out int percent)
            ? ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(
                new SteamBrightnessState(percent),
                NativeQamSemanticJsonContext.Default.SteamBrightnessState))
            : ValueTask.FromResult<JsonElement?>(null);

    /// <summary>Answers Steam's <c>setBrightness</c> command.</summary>
    internal Task<SteamUiCommandResult> HandleSetBrightnessAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPayload.TryReadInt(request.Payload, "percent", 0, 100, out int percent))
        {
            return Task.FromResult(new SteamUiCommandResult(
                false,
                "The brightness payload is invalid."));
        }
        return Task.FromResult(Backlight.TrySetBrightness(percent)
            ? SteamUiCommandResult.Applied
            : new SteamUiCommandResult(false, "The panel backlight refused the write."));
    }

    /// <inheritdoc />
    public void Dispose() => _poll.Dispose();

    /// <remarks>
    /// Queues a publication only on an actual level change, so a stable backlight costs one ioctl
    /// every two seconds and no bridge traffic at all. Steam's slider writes land back here too —
    /// that is one transition per drag, which keeps the slider and the panel agreeing without a
    /// second mechanism.
    /// </remarks>
    private void OnPoll(object? state)
    {
        if (!_active())
        {
            return;
        }

        if (!Backlight.TryReadBrightness(out int percent)
            || percent == Interlocked.Exchange(ref _lastPolled, percent))
        {
            return;
        }

        Log.Change("display.backlight", $"Panel backlight at {percent}%.");
        _publish();
    }
}
