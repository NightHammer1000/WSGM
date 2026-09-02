using System;
using System.Buffers.Binary;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>
/// Packs a canonical sample into the Steam Deck's 64-byte controller frame.
/// </summary>
/// <remarks>
/// This is the wire format WSGM hands to VIIPER, which unmarshals it and re-emits it to the host as
/// the real device's <c>ID_CONTROLLER_DECK_STATE</c> report. The bit positions are settled
/// evidence, not a guess: VIIPER's own <c>device/steamdeck/const.go</c>, HandheldCompanion's
/// <c>SteamDeckTarget</c>, and <c>hhd</c>'s virtual Steam Deck agree exactly.
/// </remarks>
internal static class SteamDeckNeptuneReport
{
    /// <summary>Length of one Steam Deck controller frame.</summary>
    internal const int Length = 64;

    // Byte 8: face buttons, shoulders, and the digital edge of the triggers.
    private const byte Byte8A = 0x80;
    private const byte Byte8X = 0x40;
    private const byte Byte8B = 0x20;
    private const byte Byte8Y = 0x10;
    private const byte Byte8L1 = 0x08;
    private const byte Byte8R1 = 0x04;
    private const byte Byte8L2 = 0x02;
    private const byte Byte8R2 = 0x01;

    // Byte 9: the lower-left paddle, the menu cluster, and the d-pad.
    private const byte Byte9L5 = 0x80;
    private const byte Byte9Menu = 0x40;
    private const byte Byte9Steam = 0x20;
    private const byte Byte9Options = 0x10;
    private const byte Byte9DPadDown = 0x08;
    private const byte Byte9DPadLeft = 0x04;
    private const byte Byte9DPadRight = 0x02;
    private const byte Byte9DPadUp = 0x01;

    // Byte 10: left stick click, trackpad touch and click, and the lower-right paddle.
    private const byte Byte10L3 = 0x40;
    private const byte Byte10RPadTouch = 0x10;
    private const byte Byte10LPadTouch = 0x08;
    private const byte Byte10RPadPress = 0x04;
    private const byte Byte10LPadPress = 0x02;
    private const byte Byte10R5 = 0x01;

    // Byte 11: right stick click.
    private const byte Byte11R3 = 0x04;

    // Byte 13: capacitive stick touch and the two upper paddles.
    private const byte Byte13RStickTouch = 0x80;
    private const byte Byte13LStickTouch = 0x40;
    private const byte Byte13R4 = 0x04;
    private const byte Byte13L4 = 0x02;

    // Byte 14: the quick-access button.
    private const byte Byte14QuickAccess = 0x04;

    // The Deck IMU fields are signed 16-bit values over fixed physical ranges. Steam/SDL expose
    // their application-space axes as raw X, raw Z, -raw Y, so WSGM reverses that transform while
    // packing the canonical application-space sample.
    private const float GyroCountsPerDegreePerSecond = 16f;
    private const float AccelCountsPerG = 16384f;

    /// <summary>
    /// Writes one canonical sample into a Steam Deck frame.
    /// </summary>
    /// <param name="sample">The canonical sample to send.</param>
    /// <param name="destination">A buffer of exactly <see cref="Length"/> bytes.</param>
    /// <exception cref="ArgumentException">The destination is the wrong length.</exception>
    internal static void Write(CanonicalControllerSample sample, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (destination.Length != Length)
        {
            throw new ArgumentException(
                $"A Steam Deck frame is exactly {Length} bytes.",
                nameof(destination));
        }

        destination.Clear();

        // Byte 0 non-zero tells the decoder the frame carries its own counter. VIIPER stamps the
        // header and the packet number itself when it re-emits, so WSGM leaves the counter alone
        // rather than inventing a sequence the device would then contradict.
        CanonicalButtons buttons = sample.Buttons;
        destination[8] = (byte)(Mask(buttons, CanonicalButtons.A, Byte8A)
            | Mask(buttons, CanonicalButtons.X, Byte8X)
            | Mask(buttons, CanonicalButtons.B, Byte8B)
            | Mask(buttons, CanonicalButtons.Y, Byte8Y)
            | Mask(buttons, CanonicalButtons.LeftShoulder, Byte8L1)
            | Mask(buttons, CanonicalButtons.RightShoulder, Byte8R1)
            // The digital trigger edge must rise in the same frame the analogue value leaves rest
            // (HandheldCompanion's SteamDeckTarget uses the same > 0 rule). A mid-travel threshold
            // makes Steam Input register the edge as a second, later activation of the trigger:
            // in desktop mode every normal pull then double-clicks and a held drag is torn loose
            // (device-observed 2026-09-02).
            | (sample.LeftTrigger > 0 ? Byte8L2 : 0)
            | (sample.RightTrigger > 0 ? Byte8R2 : 0));

        destination[9] = (byte)(Mask(buttons, CanonicalButtons.RearPaddle3, Byte9L5)
            | Mask(buttons, CanonicalButtons.Menu, Byte9Menu)
            | Mask(buttons, CanonicalButtons.Guide, Byte9Steam)
            | Mask(buttons, CanonicalButtons.View, Byte9Options)
            | Mask(buttons, CanonicalButtons.DPadDown, Byte9DPadDown)
            | Mask(buttons, CanonicalButtons.DPadLeft, Byte9DPadLeft)
            | Mask(buttons, CanonicalButtons.DPadRight, Byte9DPadRight)
            | Mask(buttons, CanonicalButtons.DPadUp, Byte9DPadUp));

        destination[10] = (byte)(Mask(buttons, CanonicalButtons.LeftStick, Byte10L3)
            | Mask(buttons, CanonicalButtons.RightPadTouch, Byte10RPadTouch)
            | Mask(buttons, CanonicalButtons.LeftPadTouch, Byte10LPadTouch)
            | Mask(buttons, CanonicalButtons.RightPadClick, Byte10RPadPress)
            | Mask(buttons, CanonicalButtons.LeftPadClick, Byte10LPadPress)
            | Mask(buttons, CanonicalButtons.RearPaddle4, Byte10R5));

        destination[11] = Mask(buttons, CanonicalButtons.RightStick, Byte11R3);

        destination[13] = (byte)(Mask(buttons, CanonicalButtons.RightStickTouch, Byte13RStickTouch)
            | Mask(buttons, CanonicalButtons.LeftStickTouch, Byte13LStickTouch)
            | Mask(buttons, CanonicalButtons.RearPaddle2, Byte13R4)
            | Mask(buttons, CanonicalButtons.RearPaddle1, Byte13L4));

        destination[14] = Mask(buttons, CanonicalButtons.QuickAccess, Byte14QuickAccess);

        BinaryPrimitives.WriteInt16LittleEndian(destination[16..18], Axis(sample.LeftPadX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[18..20], Axis(sample.LeftPadY));
        BinaryPrimitives.WriteInt16LittleEndian(destination[20..22], Axis(sample.RightPadX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[22..24], Axis(sample.RightPadY));

        WriteMotion(sample.Motion, destination);

        // Triggers are signed 16-bit on the wire; the canonical model is a 0..1 unit.
        BinaryPrimitives.WriteUInt16LittleEndian(destination[44..46], Trigger(sample.LeftTrigger));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[46..48], Trigger(sample.RightTrigger));

        BinaryPrimitives.WriteInt16LittleEndian(destination[48..50], Axis(sample.LeftStickX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[50..52], Axis(sample.LeftStickY));
        BinaryPrimitives.WriteInt16LittleEndian(destination[52..54], Axis(sample.RightStickX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[54..56], Axis(sample.RightStickY));

        BinaryPrimitives.WriteUInt16LittleEndian(destination[56..58], Trigger(sample.LeftPadForce));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[58..60], Trigger(sample.RightPadForce));
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[60..62],
            Trigger(sample.LeftStickForce));
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[62..64],
            Trigger(sample.RightStickForce));
    }

    private static void WriteMotion(MotionSample? motion, Span<byte> destination)
    {
        if (motion is null)
        {
            return;
        }

        if (motion.HasAccelerometer)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[24..26],
                ScaledMotion(motion.AccelX, AccelCountsPerG));
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[26..28],
                ScaledMotion(-motion.AccelZ, AccelCountsPerG));
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[28..30],
                ScaledMotion(motion.AccelY, AccelCountsPerG));
        }

        if (motion.HasGyro)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[30..32],
                ScaledMotion(motion.GyroX, GyroCountsPerDegreePerSecond));
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[32..34],
                ScaledMotion(-motion.GyroZ, GyroCountsPerDegreePerSecond));
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[34..36],
                ScaledMotion(motion.GyroY, GyroCountsPerDegreePerSecond));
        }

        // The orientation quaternion at bytes 36..44 stays zero on purpose. WSGM publishes raw
        // angular velocity and never computes an orientation, and a frozen identity quaternion
        // makes Steam ignore the raw gyro and collapse gyro-to-stick to centre.
    }

    private static byte Mask(CanonicalButtons buttons, CanonicalButtons flag, byte bit) =>
        (buttons & flag) != 0 ? bit : (byte)0;

    /// <summary>Scales a 0..1 unit onto the wire's trigger/pressure range.</summary>
    /// <remarks>
    /// The trigger and pressure fields are signed 16-bit on the wire (Valve's
    /// <c>sTriggerRaw</c>/<c>sPressure</c> members; SDL3 doubles 0..32767 onto the full axis
    /// range), so full travel is 32767. Scaling to 65535 made every pull past half travel read
    /// as negative — Steam saw the trigger release mid-pull and press again on the way back,
    /// which double-clicked and tore held drags loose in desktop mode (device-observed
    /// 2026-09-02).
    /// </remarks>
    private static ushort Trigger(float value) =>
        (ushort)Math.Clamp(MathF.Round(value * short.MaxValue), 0, short.MaxValue);

    /// <summary>Scales a canonical -1..1 axis onto the wire's signed range.</summary>
    /// <remarks>
    /// The negative extreme is clamped one short of <see cref="short.MinValue"/>. SDL3's Deck driver
    /// negates stick Y with a plain unary minus, so -32768 wraps back to itself and a fully
    /// deflected stick reads as the opposite extreme; a real Deck's calibrated sticks never report
    /// it either.
    /// </remarks>
    private static short Axis(float value) =>
        (short)Math.Clamp(MathF.Round(value * short.MaxValue), short.MinValue + 1, short.MaxValue);

    private static short ScaledMotion(float value, float scale) =>
        (short)Math.Clamp(MathF.Round(value * scale), short.MinValue, short.MaxValue);
}
