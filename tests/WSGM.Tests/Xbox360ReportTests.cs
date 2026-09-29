using System.Buffers.Binary;
using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Tests;

public sealed class Xbox360ReportTests
{
    [Theory]
    [InlineData(CanonicalButtons.DPadUp, 0x0001u)]
    [InlineData(CanonicalButtons.DPadDown, 0x0002u)]
    [InlineData(CanonicalButtons.DPadLeft, 0x0004u)]
    [InlineData(CanonicalButtons.DPadRight, 0x0008u)]
    [InlineData(CanonicalButtons.Menu, 0x0010u)]
    [InlineData(CanonicalButtons.View, 0x0020u)]
    [InlineData(CanonicalButtons.LeftStick, 0x0040u)]
    [InlineData(CanonicalButtons.RightStick, 0x0080u)]
    [InlineData(CanonicalButtons.LeftShoulder, 0x0100u)]
    [InlineData(CanonicalButtons.RightShoulder, 0x0200u)]
    [InlineData(CanonicalButtons.Guide, 0x0400u)]
    [InlineData(CanonicalButtons.A, 0x1000u)]
    [InlineData(CanonicalButtons.B, 0x2000u)]
    [InlineData(CanonicalButtons.X, 0x4000u)]
    [InlineData(CanonicalButtons.Y, 0x8000u)]
    public void EachSupportedButtonUsesTheXInputBit(CanonicalButtons button, uint expected)
    {
        byte[] frame = Frame(Sample(button));

        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(frame));
    }

    [Fact]
    public void SticksAndTriggersUseTheFullXInputRanges()
    {
        byte[] frame = Frame(Sample(CanonicalButtons.None) with
        {
            LeftTrigger = 0.5f,
            RightTrigger = 1f,
            LeftStickX = -1f,
            LeftStickY = 1f,
            RightStickX = 0.5f,
            RightStickY = -0.5f,
        });

        Assert.Equal(128, frame[4]);
        Assert.Equal(byte.MaxValue, frame[5]);
        Assert.Equal(short.MinValue + 1, BinaryPrimitives.ReadInt16LittleEndian(frame[6..8]));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(frame[8..10]));
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(frame[10..12]));
        Assert.Equal(-16384, BinaryPrimitives.ReadInt16LittleEndian(frame[12..14]));
        Assert.Equal(new byte[6], frame[14..20]);
    }

    [Fact]
    public void ControlsTheXboxTargetCannotRepresentAreDropped()
    {
        byte[] frame = Frame(Sample(
            CanonicalButtons.RearPaddle1
            | CanonicalButtons.LeftPadTouch
            | CanonicalButtons.QuickAccess));

        Assert.Equal(new byte[Xbox360Report.Length], frame);
    }

    [Fact]
    public void AWrongSizedDestinationIsRefused() => Assert.Throws<ArgumentException>(() =>
        Xbox360Report.Write(Sample(CanonicalButtons.None), new byte[Xbox360Report.Length - 1]));

    private static byte[] Frame(CanonicalControllerSample sample)
    {
        byte[] frame = new byte[Xbox360Report.Length];
        Xbox360Report.Write(sample, frame);
        return frame;
    }

    private static CanonicalControllerSample Sample(CanonicalButtons buttons) => new()
    {
        Sequence = 1,
        CycleGeneration = 1,
        Timestamp = DateTimeOffset.UnixEpoch,
        Buttons = buttons,
    };
}
