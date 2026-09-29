using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Decodes the volume-related APPCOMMAND values from a shell-hook
/// notification. Keeping this parsing isolated gives the device-only message
/// path a small, executable specification.</summary>
internal static class VolumeAppCommands
{
    private const int AppCommandMask = 0x0FFF;

    /// <summary>Gets the supported volume command carried by a shell-hook lParam,
    /// or <see langword="null"/> when the command belongs to another subsystem.</summary>
    internal static CoreAudio.VolumeCommand? FromShellHookLParam(nint lParam)
    {
        // GET_APPCOMMAND_LPARAM(lParam): HIWORD(lParam) without the device bits.
        var raw = unchecked((int)(long)lParam);
        var command = ((raw >> 16) & 0xFFFF) & AppCommandMask;
        if (Supported(command) is { } packed)
        {
            return packed;
        }

        // Some OEM shell implementations relay the already-extracted command
        // rather than the original WM_APPCOMMAND lParam. Accept that shape too.
        return Supported(raw);
    }

    private static CoreAudio.VolumeCommand? Supported(int command)
        => (CoreAudio.VolumeCommand)command is var value
            && value is CoreAudio.VolumeCommand.ToggleMute
                or CoreAudio.VolumeCommand.StepDown
                or CoreAudio.VolumeCommand.StepUp
            ? value
            : null;
}
