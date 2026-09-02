using System;
using System.Buffers.Binary;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Packs a canonical sample into VIIPER's Xbox 360 input-state wire format.</summary>
internal static class Xbox360Report
{
    /// <summary>Length of one VIIPER Xbox 360 input state.</summary>
    internal const int Length = 20;

    private const uint DPadUp = 0x0001;
    private const uint DPadDown = 0x0002;
    private const uint DPadLeft = 0x0004;
    private const uint DPadRight = 0x0008;
    private const uint Start = 0x0010;
    private const uint Back = 0x0020;
    private const uint LeftThumb = 0x0040;
    private const uint RightThumb = 0x0080;
    private const uint LeftShoulder = 0x0100;
    private const uint RightShoulder = 0x0200;
    private const uint Guide = 0x0400;
    private const uint A = 0x1000;
    private const uint B = 0x2000;
    private const uint X = 0x4000;
    private const uint Y = 0x8000;

    /// <summary>Writes one canonical sample into an Xbox 360 input state.</summary>
    internal static void Write(CanonicalControllerSample sample, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (destination.Length != Length)
        {
            throw new ArgumentException(
                $"A VIIPER Xbox 360 input state is exactly {Length} bytes.",
                nameof(destination));
        }

        destination.Clear();
        CanonicalButtons buttons = sample.Buttons;
        uint wireButtons = Mask(buttons, CanonicalButtons.DPadUp, DPadUp)
            | Mask(buttons, CanonicalButtons.DPadDown, DPadDown)
            | Mask(buttons, CanonicalButtons.DPadLeft, DPadLeft)
            | Mask(buttons, CanonicalButtons.DPadRight, DPadRight)
            | Mask(buttons, CanonicalButtons.Menu, Start)
            | Mask(buttons, CanonicalButtons.View, Back)
            | Mask(buttons, CanonicalButtons.LeftStick, LeftThumb)
            | Mask(buttons, CanonicalButtons.RightStick, RightThumb)
            | Mask(buttons, CanonicalButtons.LeftShoulder, LeftShoulder)
            | Mask(buttons, CanonicalButtons.RightShoulder, RightShoulder)
            | Mask(buttons, CanonicalButtons.Guide, Guide)
            | Mask(buttons, CanonicalButtons.A, A)
            | Mask(buttons, CanonicalButtons.B, B)
            | Mask(buttons, CanonicalButtons.X, X)
            | Mask(buttons, CanonicalButtons.Y, Y);

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], wireButtons);
        destination[4] = Trigger(sample.LeftTrigger);
        destination[5] = Trigger(sample.RightTrigger);
        BinaryPrimitives.WriteInt16LittleEndian(destination[6..8], Axis(sample.LeftStickX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[8..10], Axis(sample.LeftStickY));
        BinaryPrimitives.WriteInt16LittleEndian(destination[10..12], Axis(sample.RightStickX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[12..14], Axis(sample.RightStickY));
    }

    private static uint Mask(CanonicalButtons buttons, CanonicalButtons flag, uint bit) =>
        (buttons & flag) != 0 ? bit : 0;

    private static byte Trigger(float value) =>
        (byte)Math.Clamp(MathF.Round(value * byte.MaxValue), 0, byte.MaxValue);

    private static short Axis(float value) =>
        (short)Math.Clamp(MathF.Round(value * short.MaxValue), short.MinValue + 1, short.MaxValue);
}
