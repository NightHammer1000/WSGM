using System;
using System.Buffers.Binary;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Packs a canonical sample into VIIPER's DualShock 4 input-state wire format.</summary>
internal static class DualShock4Report
{
    /// <summary>Length of one VIIPER DualShock 4 input state.</summary>
    internal const int Length = 31;

    private const ushort Square = 0x0010;
    private const ushort Cross = 0x0020;
    private const ushort Circle = 0x0040;
    private const ushort Triangle = 0x0080;
    private const ushort L1 = 0x0100;
    private const ushort R1 = 0x0200;
    private const ushort L2 = 0x0400;
    private const ushort R2 = 0x0800;
    private const ushort Share = 0x1000;
    private const ushort Options = 0x2000;
    private const ushort L3 = 0x4000;
    private const ushort R3 = 0x8000;
    private const ushort Ps = 0x0001;
    private const ushort TouchpadClick = 0x0002;

    private const byte DPadUp = 0x01;
    private const byte DPadDown = 0x02;
    private const byte DPadLeft = 0x04;
    private const byte DPadRight = 0x08;
    private const float GyroCountsPerDegreePerSecond = 16f;
    private const float AccelCountsPerG = 512f * 9.81f;
    private const ushort TouchMaxX = 1920;
    private const ushort TouchMaxY = 942;

    /// <summary>Writes one canonical sample into a DualShock 4 input state.</summary>
    internal static void Write(CanonicalControllerSample sample, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (destination.Length != Length)
        {
            throw new ArgumentException(
                $"A VIIPER DualShock 4 input state is exactly {Length} bytes.",
                nameof(destination));
        }

        destination.Clear();
        CanonicalButtons buttons = sample.Buttons;
        destination[0] = unchecked((byte)Axis(sample.LeftStickX));
        destination[1] = unchecked((byte)Axis(-sample.LeftStickY));
        destination[2] = unchecked((byte)Axis(sample.RightStickX));
        destination[3] = unchecked((byte)Axis(-sample.RightStickY));

        ushort wireButtons = (ushort)(Mask(buttons, CanonicalButtons.X, Square)
            | Mask(buttons, CanonicalButtons.A, Cross)
            | Mask(buttons, CanonicalButtons.B, Circle)
            | Mask(buttons, CanonicalButtons.Y, Triangle)
            | Mask(buttons, CanonicalButtons.LeftShoulder, L1)
            | Mask(buttons, CanonicalButtons.RightShoulder, R1)
            // The digital bit rises with the first analogue movement, as on a real DualShock 4.
            // A mid-travel threshold splits the press into two Steam Input activations; the same
            // split double-clicked and broke drags on the Deck target (device-observed 2026-09-02).
            | (sample.LeftTrigger > 0 ? L2 : (ushort)0)
            | (sample.RightTrigger > 0 ? R2 : (ushort)0)
            | Mask(buttons, CanonicalButtons.View, Share)
            | Mask(buttons, CanonicalButtons.Menu, Options)
            | Mask(buttons, CanonicalButtons.LeftStick, L3)
            | Mask(buttons, CanonicalButtons.RightStick, R3)
            | Mask(buttons, CanonicalButtons.Guide, Ps)
            | (((buttons & (CanonicalButtons.LeftPadClick | CanonicalButtons.RightPadClick)) != 0)
                ? TouchpadClick
                : (ushort)0));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], wireButtons);

        destination[6] = (byte)(Mask(buttons, CanonicalButtons.DPadUp, DPadUp)
            | Mask(buttons, CanonicalButtons.DPadDown, DPadDown)
            | Mask(buttons, CanonicalButtons.DPadLeft, DPadLeft)
            | Mask(buttons, CanonicalButtons.DPadRight, DPadRight));
        destination[7] = Trigger(sample.LeftTrigger);
        destination[8] = Trigger(sample.RightTrigger);

        BinaryPrimitives.WriteUInt16LittleEndian(destination[9..11], Touch(sample.LeftPadX, TouchMaxX));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[11..13], Touch(-sample.LeftPadY, TouchMaxY));
        destination[13] = (buttons & CanonicalButtons.LeftPadTouch) != 0 ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..16], Touch(sample.RightPadX, TouchMaxX));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..18], Touch(-sample.RightPadY, TouchMaxY));
        destination[18] = (buttons & CanonicalButtons.RightPadTouch) != 0 ? (byte)1 : (byte)0;

        WriteMotion(sample.Motion, destination);
    }

    private static void WriteMotion(MotionSample? motion, Span<byte> destination)
    {
        if (motion?.HasGyro == true)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination[19..21],
                ScaledMotion(motion.GyroX, GyroCountsPerDegreePerSecond));
            BinaryPrimitives.WriteInt16LittleEndian(destination[21..23],
                ScaledMotion(motion.GyroY, GyroCountsPerDegreePerSecond));
            BinaryPrimitives.WriteInt16LittleEndian(destination[23..25],
                ScaledMotion(motion.GyroZ, GyroCountsPerDegreePerSecond));
        }

        if (motion?.HasAccelerometer == true)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination[25..27],
                ScaledMotion(motion.AccelX, AccelCountsPerG));
            BinaryPrimitives.WriteInt16LittleEndian(destination[27..29],
                ScaledMotion(motion.AccelY, AccelCountsPerG));
            BinaryPrimitives.WriteInt16LittleEndian(destination[29..31],
                ScaledMotion(motion.AccelZ, AccelCountsPerG));
        }
    }

    private static ushort Mask(CanonicalButtons buttons, CanonicalButtons flag, ushort bit) =>
        (buttons & flag) != 0 ? bit : (ushort)0;

    private static byte Mask(CanonicalButtons buttons, CanonicalButtons flag, byte bit) =>
        (buttons & flag) != 0 ? bit : (byte)0;

    private static sbyte Axis(float value) =>
        (sbyte)Math.Clamp(MathF.Round(value * sbyte.MaxValue), -sbyte.MaxValue, sbyte.MaxValue);

    private static byte Trigger(float value) =>
        (byte)Math.Clamp(MathF.Round(value * byte.MaxValue), 0, byte.MaxValue);

    private static ushort Touch(float value, ushort maximum) =>
        (ushort)Math.Clamp(
            MathF.Round(((Math.Clamp(value, -1f, 1f) + 1f) / 2f) * maximum),
            0,
            maximum);

    private static short ScaledMotion(float value, float scale) =>
        (short)Math.Clamp(MathF.Round(value * scale), short.MinValue, short.MaxValue);
}
