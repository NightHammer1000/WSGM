using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>
/// Reads the refresh rates a panel itself advertises out of its EDID.
/// </summary>
/// <remarks>
/// Needed because enumeration cannot tell an advertised mode from one the driver synthesized. The
/// reference Claw enumerates 30/48/60/75/100/120 and accepts every one, while its EDID advertises
/// only 60 and 120 — the rest exist because the panel's adaptive-sync range lets the driver make
/// them up. That distinction is the whole difference between the `NativeModes` and `FrameDoubling`
/// frame-limit strategies, and without it the two would silently be the same thing.
/// <para>
/// Only the detailed timing descriptors are read. Established and standard timings describe old
/// low-resolution modes no handheld panel uses, and reading them would add rates the panel does not
/// actually run at its native resolution.
/// </para>
/// </remarks>
internal static class EdidModes
{
    /// <summary>Length of a complete EDID base block.</summary>
    private const int BlockLength = 128;

    /// <summary>Offset of the first of the four 18-byte descriptors.</summary>
    private const int FirstDescriptor = 54;

    /// <summary>Number of descriptors in the base block.</summary>
    private const int DescriptorCount = 4;

    /// <summary>Length of one descriptor.</summary>
    private const int DescriptorLength = 18;

    /// <summary>
    /// The vertical refresh rates a panel advertises as detailed timings.
    /// </summary>
    /// <param name="edid">A complete EDID base block, or more.</param>
    /// <returns>Advertised rates in Hz, ascending and deduplicated; empty when none can be read.</returns>
    /// <remarks>
    /// Rates are rounded to whole hertz because that is how Windows reports and accepts them; a
    /// panel's 59.95 Hz timing is the 60 Hz mode everywhere else in the system.
    /// </remarks>
    internal static IReadOnlyList<int> ReadAdvertisedRefreshRates(byte[]? edid)
    {
        if (edid is null || edid.Length < BlockLength || !HasValidHeader(edid))
        {
            return [];
        }

        SortedSet<int> rates = [];
        for (int index = 0; index < DescriptorCount; index++)
        {
            int offset = FirstDescriptor + (index * DescriptorLength);
            if (TryReadDetailedTiming(edid, offset, out int refreshHz))
            {
                rates.Add(refreshHz);
            }
        }

        return [.. rates];
    }

    private static bool HasValidHeader(byte[] edid) =>
        edid[0] == 0x00
        && edid[1] == 0xFF
        && edid[2] == 0xFF
        && edid[3] == 0xFF
        && edid[4] == 0xFF
        && edid[5] == 0xFF
        && edid[6] == 0xFF
        && edid[7] == 0x00;

    private static bool TryReadDetailedTiming(byte[] edid, int offset, out int refreshHz)
    {
        refreshHz = 0;

        // Pixel clock in 10 kHz units, little endian. Zero marks a non-timing descriptor.
        int pixelClock = edid[offset] | (edid[offset + 1] << 8);
        if (pixelClock == 0)
        {
            return false;
        }

        // Active and blanking are split: the low eight bits sit in their own byte and the high four
        // share a nibble byte, which is why these cannot simply be read as 16-bit values.
        int horizontalActive = edid[offset + 2] | ((edid[offset + 4] & 0xF0) << 4);
        int horizontalBlank = edid[offset + 3] | ((edid[offset + 4] & 0x0F) << 8);
        int verticalActive = edid[offset + 5] | ((edid[offset + 7] & 0xF0) << 4);
        int verticalBlank = edid[offset + 6] | ((edid[offset + 7] & 0x0F) << 8);

        long horizontalTotal = horizontalActive + horizontalBlank;
        long verticalTotal = verticalActive + verticalBlank;
        if (horizontalTotal <= 0 || verticalTotal <= 0)
        {
            return false;
        }

        long dotsPerSecond = pixelClock * 10_000L;
        double rate = dotsPerSecond / (double)(horizontalTotal * verticalTotal);

        // A panel outside this band is a misparse rather than a real mode.
        if (rate is < 20 or > 1000)
        {
            return false;
        }

        refreshHz = (int)System.Math.Round(rate);
        return true;
    }
}
