using System;
using System.Buffers.Binary;
using System.IO;

namespace WSGM.Core;

/// <summary>Reads the DECLARED pixel dimensions out of an image file's header
/// without decoding a single pixel, for exactly the formats WSGM accepts as
/// splash images (PNG, JPEG, BMP).
///
/// Why this exists: <c>.wsgmsplash</c> theme files are shared, therefore
/// untrusted, and the archive's byte caps bound only the ENCODED size. A few
/// kilobytes of PNG can declare 60000x60000 pixels, so a plain
/// <c>new Bitmap(path)</c> — thumbnail preview or boot splash — allocates a
/// multi-gigabyte pixel buffer before anything can reject it. Callers read the
/// header first, refuse absurd dimensions (<see cref="IsWithinLimits"/>), and
/// then decode SCALED.
///
/// Honest about its guarantees: these numbers are what the file CLAIMS, not
/// what it contains. A truncated or lying header simply fails the subsequent
/// decode, which every call site already catches — the point here is only to
/// avoid committing an unbounded allocation on a file's say-so. Nothing is
/// validated beyond plausibility (positive, in range).
///
/// Small and dependency-free: plain <see cref="FileStream"/> byte reads,
/// no reflection, no imaging stack, and it never throws (any I/O or format
/// surprise reports "unknown").</summary>
public static class ImageHeader
{
    /// <summary>Largest accepted edge length, in pixels. Above this a file is
    /// treated as hostile rather than as a real image: no display WSGM drives is
    /// anywhere near 20000 px, and both GPU texture limits and Skia would reject
    /// it anyway (just after allocating the buffer).</summary>
    public const int MaxDimension = 20_000;

    /// <summary>Largest accepted total pixel count (80 megapixels). This bounds
    /// the pathological aspect ratios <see cref="MaxDimension"/> alone lets
    /// through (e.g. 20000x20000 = 400 MP). It is a sanity bound, not a memory
    /// budget — 80 MP is still ~320 MB at 4 bytes per pixel, so call sites that
    /// can decode scaled should do so rather than lean on this limit.</summary>
    public const long MaxPixels = 80_000_000;

    /// <summary>True when the given dimensions are positive and within
    /// <see cref="MaxDimension"/> and <see cref="MaxPixels"/>.</summary>
    /// <param name="width">Declared width in pixels.</param>
    /// <param name="height">Declared height in pixels.</param>
    public static bool IsWithinLimits(int width, int height) =>
        width > 0
        && height > 0
        && width <= MaxDimension
        && height <= MaxDimension
        && (long)width * height <= MaxPixels;

    /// <summary>Reads the declared pixel dimensions from the file's header only.
    ///
    /// Formats and their quirks:
    /// <list type="bullet">
    /// <item>PNG — the 8-byte signature must be followed by the IHDR chunk
    /// (IHDR is mandatory and always first); width/height are big-endian
    /// uint32 and are rejected when they exceed <see cref="int.MaxValue"/>.</item>
    /// <item>JPEG — there is no fixed header, so the marker chain is walked from
    /// SOI to the first SOFn frame header (0xFFC0-0xFFCF except DHT 0xC4,
    /// JPG 0xC8 and DAC 0xCC, which are not frame headers); height comes BEFORE
    /// width in a SOFn. Fill bytes (runs of 0xFF) are skipped, standalone
    /// markers (TEM 0x01, RSTn 0xD0-0xD7, SOI 0xD8) carry no length field, and
    /// the walk stops at SOS (0xDA) where entropy-coded data begins or at
    /// EOI (0xD9) where the image ends.</item>
    /// <item>BMP — 'BM', then the DIB header size at offset 14 selects the
    /// layout: BITMAPINFOHEADER and later (>= 40) use signed 32-bit width/height
    /// where a NEGATIVE height means a top-down bitmap, so the absolute value is
    /// reported; the legacy 12-byte BITMAPCOREHEADER uses unsigned 16-bit
    /// fields.</item>
    /// </list>
    ///
    /// Anything else — another format, a truncated header, an unreadable or
    /// missing file, implausible values — reports false with zeroed outputs.</summary>
    /// <param name="path">Full path to the image file.</param>
    /// <param name="width">Declared width in pixels; 0 when unknown.</param>
    /// <param name="height">Declared height in pixels; 0 when unknown.</param>
    /// <returns>True when a size was read from a recognized header.</returns>
    public static bool TryReadSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 512, FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[8];
            if (!TryFill(stream, signature))
            {
                return false;
            }

            if (IsPng(signature))
            {
                return TryReadPng(stream, out width, out height);
            }
            if (signature[0] == 0xFF && signature[1] == 0xD8)
            {
                // The 8 signature bytes already consumed 6 bytes of the first
                // segment; restart the marker walk right after SOI.
                stream.Position = 2;
                return TryReadJpeg(stream, out width, out height);
            }
            if (signature[0] == (byte)'B' && signature[1] == (byte)'M')
            {
                return TryReadBmp(stream, out width, out height);
            }
            return false;
        }
        catch (Exception)
        {
            // Missing, locked, or otherwise unreadable: indistinguishable from an
            // unrecognized format for the caller's purposes.
            width = 0;
            height = 0;
            return false;
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> signature) =>
        signature[0] == 0x89
        && signature[1] == 0x50
        && signature[2] == 0x4E
        && signature[3] == 0x47
        && signature[4] == 0x0D
        && signature[5] == 0x0A
        && signature[6] == 0x1A
        && signature[7] == 0x0A;

    /// <summary>Reads IHDR, positioned directly after the PNG signature.</summary>
    private static bool TryReadPng(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> ihdr = stackalloc byte[16]; // length + type + width + height
        if (!TryFill(stream, ihdr))
        {
            return false;
        }
        if (ihdr[4] != (byte)'I' || ihdr[5] != (byte)'H' || ihdr[6] != (byte)'D' || ihdr[7] != (byte)'R')
        {
            return false;
        }
        var w = BinaryPrimitives.ReadUInt32BigEndian(ihdr[8..12]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(ihdr[12..16]);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
        {
            return false;
        }
        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>Walks JPEG markers from just after SOI to the first frame
    /// header.</summary>
    private static bool TryReadJpeg(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> pair = stackalloc byte[2];
        Span<byte> frame = stackalloc byte[5]; // precision + height + width
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                return false;
            }
            if (b != 0xFF)
            {
                // Not a marker boundary — the chain is broken; treat as unknown
                // rather than guessing.
                return false;
            }

            // Any number of 0xFF fill bytes may precede the marker code.
            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0)
                {
                    return false;
                }
            } while (marker == 0xFF);

            if (marker == 0xD9)
            {
                // End of image: the file ended without a frame header. Continuing
                // here would scan a crafted file byte by byte to EOF.
                return false;
            }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
            {
                // Standalone markers: no length field follows.
                continue;
            }
            if (marker == 0xDA)
            {
                // Start of scan: no frame header was found before the image data.
                return false;
            }
            if (!TryFill(stream, pair))
            {
                return false;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(pair);
            if (length < 2)
            {
                return false;
            }

            var isFrameHeader = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isFrameHeader)
            {
                if (length < 7 || !TryFill(stream, frame))
                {
                    return false;
                }
                var h = BinaryPrimitives.ReadUInt16BigEndian(frame[1..3]);
                var w = BinaryPrimitives.ReadUInt16BigEndian(frame[3..5]);
                if (w == 0 || h == 0)
                {
                    return false;
                }
                width = w;
                height = h;
                return true;
            }

            stream.Seek(length - 2, SeekOrigin.Current);
        }
    }

    /// <summary>Reads the DIB header, positioned after the first 8 file-header
    /// bytes.</summary>
    private static bool TryReadBmp(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        stream.Position = 14; // skip the rest of BITMAPFILEHEADER
        Span<byte> dib = stackalloc byte[12]; // header size + width + height
        if (!TryFill(stream, dib))
        {
            return false;
        }
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib[0..4]);
        int w, h;
        if (headerSize >= 40)
        {
            // BITMAPINFOHEADER / V4 / V5: signed 32-bit, negative height = top-down.
            w = BinaryPrimitives.ReadInt32LittleEndian(dib[4..8]);
            h = BinaryPrimitives.ReadInt32LittleEndian(dib[8..12]);
            if (h == int.MinValue)
            {
                return false; // no positive counterpart to report
            }
            h = Math.Abs(h);
        }
        else if (headerSize == 12)
        {
            // BITMAPCOREHEADER (OS/2 1.x): unsigned 16-bit, always bottom-up.
            w = BinaryPrimitives.ReadUInt16LittleEndian(dib[4..6]);
            h = BinaryPrimitives.ReadUInt16LittleEndian(dib[6..8]);
        }
        else
        {
            return false;
        }
        if (w <= 0 || h <= 0)
        {
            return false;
        }
        width = w;
        height = h;
        return true;
    }

    /// <summary>Fills the whole buffer or reports false (a short read means a
    /// truncated header).</summary>
    private static bool TryFill(Stream stream, Span<byte> buffer) =>
        stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
}
