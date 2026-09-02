using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class VolumeAppCommandsTests
{
    [Theory]
    [InlineData(CoreAudio.VolumeCommand.ToggleMute)]
    [InlineData(CoreAudio.VolumeCommand.StepDown)]
    [InlineData(CoreAudio.VolumeCommand.StepUp)]
    public void FromShellHookLParam_DecodesPackedVolumeCommand(CoreAudio.VolumeCommand command)
    {
        var packed = (nint)((int)command << 16);

        Assert.Equal(command, VolumeAppCommands.FromShellHookLParam(packed));
    }

    [Fact]
    public void FromShellHookLParam_AcceptsOemAlreadyExtractedCommand()
    {
        Assert.Equal(
            CoreAudio.VolumeCommand.StepUp,
            VolumeAppCommands.FromShellHookLParam((nint)CoreAudio.VolumeCommand.StepUp));
    }

    [Fact]
    public void FromShellHookLParam_IgnoresNonVolumeCommand()
    {
        Assert.Null(VolumeAppCommands.FromShellHookLParam((nint)(14 << 16)));
    }
}
