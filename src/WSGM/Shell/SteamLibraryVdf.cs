using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WSGM.Shell;

/// <summary>Generates the two Valve KeyValues files a Steam library needs, in
/// Steam's exact on-disk dialect (byte-verified against real libraries,
/// 2026-08-10): UTF-8 without BOM, LF-only line endings even on Windows, one TAB
/// per nesting level, TWO TABs between key and value, backslashes in paths
/// escaped as <c>\\</c>, file ends with the closing brace plus LF.
///
/// The library id ("contentid") is a random unsigned 64-bit chosen at creation —
/// Steam's own client stores it as <c>m_ulContentID</c> with no derivation from
/// path/volume/machine, accepts third-party-invented ids, and self-heals empty
/// ones. The same value goes into the card marker and the config registration.
///
/// Everything here is pure string work so the exact bytes are unit-testable;
/// file I/O lives in <see cref="SdFormatManager"/>, except the one shared card-marker
/// read (<see cref="TryReadMarkerContentId"/>).</summary>
public static class SteamLibraryVdf
{
    /// <summary>Generates a fresh library content id: a uniformly random integer
    /// in [1, 2^63), decimal-formatted — the value shape of every Steam-created
    /// id observed in the wild.</summary>
    /// <param name="taken">Ids already present in the config; collisions retry.</param>
    public static string GenerateContentId(IReadOnlySet<string> taken)
    {
        Span<byte> bytes = stackalloc byte[8];
        while (true)
        {
            // A uniformly random positive int64 in [1, 2^63), the value shape of
            // every Steam-created id. RandomNumberGenerator has no int64 range
            // helper, so draw 8 bytes and clear the sign bit.
            RandomNumberGenerator.Fill(bytes);
            var raw = BitConverter.ToUInt64(bytes) & 0x7FFF_FFFF_FFFF_FFFFUL;
            if (raw == 0)
            {
                continue;
            }
            var value = raw.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(value))
            {
                return value;
            }
        }
    }

    /// <summary>Escapes a Windows path for a VDF string value.</summary>
    /// <param name="path">The plain path, e.g. <c>E:\SteamLibrary</c>.</param>
    internal static string EscapePath(string path) => path.Replace("\\", "\\\\");

    /// <summary>Escapes a VDF string value (backslash then double-quote), for the
    /// user-chosen library label.</summary>
    /// <param name="value">The raw value.</param>
    internal static string EscapeValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Builds the card marker — <c>&lt;X&gt;:\SteamLibrary\libraryfolder.vdf</c>.</summary>
    /// <param name="contentId">The generated library id.</param>
    /// <param name="steamExePath">The plain steam.exe path (escaped here).</param>
    /// <param name="label">The user-chosen library label, or empty for none.</param>
    public static string BuildMarker(string contentId, string steamExePath, string label = "") =>
        "\"libraryfolder\"\n"
        + "{\n"
        + $"\t\"contentid\"\t\t\"{contentId}\"\n"
        + $"\t\"label\"\t\t\"{EscapeValue(label)}\"\n"
        + $"\t\"launcher\"\t\t\"{EscapePath(steamExePath)}\"\n"
        + "}\n";

    /// <summary>Builds one numbered registration block for
    /// <c>config\libraryfolders.vdf</c>, field order matching what Steam writes;
    /// <c>apps</c> stays empty for Steam to fill.</summary>
    /// <param name="index">The zero-based entry index.</param>
    /// <param name="libraryPath">The plain library path (escaped here).</param>
    /// <param name="contentId">The library id, matching the card marker.</param>
    /// <param name="totalSize">The volume size in bytes.</param>
    /// <param name="label">The user-chosen library label, or empty for none.</param>
    internal static string BuildConfigEntry(
        int index, string libraryPath, string contentId, long totalSize, string label = "") =>
        $"\t\"{index}\"\n"
        + "\t{\n"
        + $"\t\t\"path\"\t\t\"{EscapePath(libraryPath)}\"\n"
        + $"\t\t\"label\"\t\t\"{EscapeValue(label)}\"\n"
        + $"\t\t\"contentid\"\t\t\"{contentId}\"\n"
        + $"\t\t\"totalsize\"\t\t\"{totalSize.ToString(CultureInfo.InvariantCulture)}\"\n"
        + "\t\t\"update_clean_bytes_tally\"\t\t\"0\"\n"
        + "\t\t\"time_last_update_verified\"\t\t\"0\"\n"
        + "\t\t\"apps\"\n"
        + "\t\t{\n"
        + "\t\t}\n"
        + "\t}\n";

    /// <summary>All quoted values following a given key anywhere in the file,
    /// UNESCAPED (the inverse of <see cref="EscapeValue"/>/<see cref="EscapePath"/>) —
    /// used for registered-path, label and content-id lookups. Line-based on
    /// purpose: existing content is never reserialized, only inspected.</summary>
    /// <param name="vdf">The file text.</param>
    /// <param name="key">The bare key name, e.g. "path".</param>
    public static List<string> ValuesOf(string vdf, string key)
    {
        var results = new List<string>();
        var marker = $"\"{key}\"";
        foreach (var rawLine in vdf.Split('\n'))
        {
            var line = rawLine.TrimStart('\t', ' ');
            if (!line.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }
            if (TryReadQuoted(line[marker.Length..].TrimStart('\t', ' '), out var value))
            {
                results.Add(value);
            }
        }
        return results;
    }

    /// <summary>Whether the config already contains an entry with this content id —
    /// the library's stable identity, which is what dedup keys on. Path is the
    /// wrong key: a reformatted card reuses the reader's drive letter but is a
    /// NEW library (fresh content id), and Steam permits several libraries at one
    /// path, so a reused path must not suppress the new card's entry.</summary>
    /// <param name="vdf">The config file text.</param>
    /// <param name="contentId">The library content id.</param>
    public static bool IsContentIdRegistered(string vdf, string contentId)
    {
        foreach (var value in ValuesOf(vdf, "contentid"))
        {
            if (string.Equals(value, contentId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Finds the registered library path for a stable content id. This
    /// deliberately selects the registration by content id, not by path: a card
    /// reader can assign the same letter to many different cards.</summary>
    /// <param name="vdf">The current libraryfolders configuration text.</param>
    /// <param name="contentId">The library identity from its card marker.</param>
    /// <returns>The unescaped registered path, or null when the id is absent.</returns>
    public static string? PathForContentId(string vdf, string contentId) =>
        ValueForContentId(vdf, "path", contentId);

    /// <summary>Finds the label belonging to a specific content-id registration.</summary>
    /// <param name="vdf">The current libraryfolders configuration text.</param>
    /// <param name="contentId">The stable library identity.</param>
    /// <returns>The matching label, or null when the identity is absent.</returns>
    public static string? LabelForContentId(string vdf, string contentId) =>
        ValueForContentId(vdf, "label", contentId);

    /// <summary>The value of one key inside the block whose contentid matches, or
    /// null when the identity is absent (or the block carries no such key).</summary>
    private static string? ValueForContentId(string vdf, string key, string contentId)
    {
        string? value = null;
        string? currentId = null;
        foreach (var rawLine in vdf.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (IsTopLevelEntry(line))
            {
                if (string.Equals(currentId, contentId, StringComparison.Ordinal))
                {
                    return value;
                }
                value = null;
                currentId = null;
                continue;
            }
            if (TryReadValue(line, key, out var candidate))
            {
                value = candidate;
            }
            else if (TryReadValue(line, "contentid", out var candidateId))
            {
                currentId = candidateId;
            }
        }
        return string.Equals(currentId, contentId, StringComparison.Ordinal) ? value : null;
    }

    /// <summary>Rewrites the <c>label</c> of the library block whose content id
    /// matches, preserving every other byte. Works on both file shapes: the
    /// numbered blocks of <c>config\libraryfolders.vdf</c> and the single-block
    /// card marker <c>libraryfolder.vdf</c>. A block without a label line gets one
    /// inserted after its contentid line. Used only while Steam is closed; a
    /// running client is renamed through its CEF API instead.</summary>
    /// <param name="vdf">The current file text.</param>
    /// <param name="contentId">The stable library identity to rename.</param>
    /// <param name="label">The new raw label (escaped here).</param>
    /// <param name="updated">The text with the new label on success.</param>
    /// <returns>True when a matching block was updated.</returns>
    public static bool TrySetLabel(string vdf, string contentId, string label, out string? updated)
    {
        updated = null;
        var starts = TopLevelEntryStarts(vdf);

        int blockStart = -1, blockEnd = -1;
        if (starts.Count == 0)
        {
            // Marker file: the whole file is one "libraryfolder" block.
            if (!IsContentIdRegistered(vdf, contentId))
            {
                return false;
            }
            blockStart = 0;
            blockEnd = vdf.Length;
        }
        else
        {
            var rootClose = vdf.LastIndexOf('}');
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1] : rootClose;
                if (end > starts[i] && IsContentIdRegistered(vdf[starts[i]..end], contentId))
                {
                    blockStart = starts[i];
                    blockEnd = end;
                    break;
                }
            }
            if (blockStart < 0)
            {
                return false;
            }
        }

        int labelStart = -1, labelEnd = -1, idStart = -1, idEnd = -1;
        var position = blockStart;
        while (position < blockEnd)
        {
            var end = vdf.IndexOf('\n', position);
            if (end < 0 || end > blockEnd)
            {
                end = blockEnd;
            }
            var line = vdf[position..end].TrimEnd('\r');
            if (TryReadValue(line, "label", out _))
            {
                labelStart = position;
                labelEnd = end;
            }
            else if (TryReadValue(line, "contentid", out _))
            {
                idStart = position;
                idEnd = end;
            }
            position = end + 1;
        }

        var escaped = EscapeValue(label);
        if (labelStart >= 0)
        {
            var raw = vdf[labelStart..labelEnd];
            var line = raw.TrimEnd('\r');
            var leading = line[..(line.Length - line.TrimStart('\t', ' ').Length)];
            var replacement = leading + "\"label\"\t\t\"" + escaped + "\""
                + (raw.EndsWith('\r') ? "\r" : "");
            updated = vdf.Remove(labelStart, labelEnd - labelStart)
                .Insert(labelStart, replacement);
            return true;
        }
        if (idStart >= 0)
        {
            var idLine = vdf[idStart..idEnd].TrimEnd('\r');
            var leading = idLine[..(idLine.Length - idLine.TrimStart('\t', ' ').Length)];
            updated = vdf.Insert(idEnd, "\n" + leading + "\"label\"\t\t\"" + escaped + "\"");
            return true;
        }
        return false;
    }

    /// <summary>Removes exactly one top-level library registration selected by
    /// content id, preserving all other configuration bytes. Used only while
    /// Steam is closed; a running client is changed through its CEF API instead.</summary>
    /// <param name="vdf">The current libraryfolders configuration text.</param>
    /// <param name="contentId">The stable library identity to remove.</param>
    /// <param name="updated">The text without the matching entry on success.</param>
    /// <returns>True when a matching registration was removed.</returns>
    public static bool TryRemoveContentId(string vdf, string contentId, out string? updated)
    {
        updated = null;
        if (!vdf.TrimStart('\uFEFF', ' ', '\r', '\n', '\t')
                .StartsWith("\"libraryfolders\"", StringComparison.Ordinal)
            || vdf.LastIndexOf('}') < 0)
        {
            return false;
        }
        var starts = TopLevelEntryStarts(vdf);
        var rootClose = vdf.LastIndexOf('}');
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : rootClose;
            if (end <= starts[i]
                || !IsContentIdRegistered(vdf[starts[i]..end], contentId))
            {
                continue;
            }
            updated = RenumberEntries(vdf.Remove(starts[i], end - starts[i]));
            return true;
        }
        return false;
    }

    /// <summary>Removes EVERY top-level registration whose <c>path</c> is
    /// <paramref name="libraryPath"/>, whatever content id each one carries.</summary>
    /// <remarks>
    /// The closed-Steam counterpart of the live purge in
    /// <see cref="Core.SteamCdp"/>. A card reader reuses its drive letter, so
    /// <c>E:\SteamLibrary</c> names a different card every time one is swapped, and
    /// a registration left behind by the previous card keeps its own content id.
    /// Dedup by content id therefore does NOT see it, the freshly formatted card is
    /// appended as a second entry at the same path, and Steam ends up listing the
    /// old card's games next to the new card's capacity. Removal is by PATH here
    /// for exactly that reason; identity-based removal stays
    /// <see cref="TryRemoveContentId"/>, which is what the pre-format step uses
    /// when the card's own marker is still readable.
    /// </remarks>
    /// <param name="vdf">The current libraryfolders configuration text.</param>
    /// <param name="libraryPath">The plain library path, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="updated">The text without those entries, when any were removed.</param>
    /// <returns>How many registrations were removed.</returns>
    public static int TryRemovePath(string vdf, string libraryPath, out string? updated)
    {
        updated = null;
        if (!vdf.TrimStart('\uFEFF', ' ', '\r', '\n', '\t')
                .StartsWith("\"libraryfolders\"", StringComparison.Ordinal)
            || vdf.LastIndexOf('}') < 0)
        {
            return 0;
        }
        var target = NormalizePath(libraryPath);
        if (target.Length == 0)
        {
            return 0;
        }
        var removed = 0;
        var current = vdf;
        // Re-scan after each removal: the offsets of every later block move, and
        // renumbering rewrites the entry keys. Cards are few, so the repeated scan
        // costs nothing and keeps the index bookkeeping impossible to get wrong.
        while (true)
        {
            var starts = TopLevelEntryStarts(current);
            var rootClose = current.LastIndexOf('}');
            var cut = -1;
            var cutEnd = -1;
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1] : rootClose;
                if (end <= starts[i])
                {
                    continue;
                }
                if (ValuesOf(current[starts[i]..end], "path")
                    .Any(path => string.Equals(NormalizePath(path), target, StringComparison.Ordinal)))
                {
                    cut = starts[i];
                    cutEnd = end;
                    break;
                }
            }
            if (cut < 0)
            {
                break;
            }
            current = RenumberEntries(current.Remove(cut, cutEnd - cut));
            removed++;
        }
        if (removed > 0)
        {
            updated = current;
        }
        return removed;
    }

    /// <summary>One top-level registration's key facts, as
    /// <see cref="ReadEntries"/> reports them. Null means the block carries no
    /// such line.</summary>
    /// <param name="Index">The numbered block's key.</param>
    /// <param name="Path">The unescaped library path.</param>
    /// <param name="Label">The unescaped label.</param>
    /// <param name="ContentId">The library's stable identity.</param>
    public readonly record struct ConfigEntry(
        int Index, string? Path, string? Label, string? ContentId);

    /// <summary>Reads every top-level registration's path, label and content id,
    /// each taken from ITS OWN block — index-zipping separate
    /// <see cref="ValuesOf"/> lists silently mispairs them when a block lacks a
    /// key.</summary>
    /// <param name="vdf">The current libraryfolders configuration text.</param>
    public static List<ConfigEntry> ReadEntries(string vdf)
    {
        var entries = new List<ConfigEntry>();
        var inEntry = false;
        var index = 0;
        string? path = null, label = null, contentId = null;
        foreach (var rawLine in vdf.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (IsTopLevelEntry(line))
            {
                if (inEntry)
                {
                    entries.Add(new ConfigEntry(index, path, label, contentId));
                }
                inEntry = true;
                var end = line.IndexOf('"', 2);
                index = int.Parse(line[2..end], NumberStyles.None, CultureInfo.InvariantCulture);
                path = label = contentId = null;
                continue;
            }
            if (!inEntry)
            {
                continue;
            }
            if (TryReadValue(line, "path", out var candidatePath))
            {
                path = candidatePath;
            }
            else if (TryReadValue(line, "label", out var candidateLabel))
            {
                label = candidateLabel;
            }
            else if (TryReadValue(line, "contentid", out var candidateId))
            {
                contentId = candidateId;
            }
        }
        if (inEntry)
        {
            entries.Add(new ConfigEntry(index, path, label, contentId));
        }
        return entries;
    }

    /// <summary>Reads the content id a card's <c>libraryfolder.vdf</c> marker
    /// carries — the one file read shared by the card features. False when the
    /// marker is absent or holds no usable id. Deliberately does NOT catch IO
    /// failures: the callers' policies for an unreadable marker differ (skip the
    /// volume, refuse a restore), so the exception is theirs to handle.</summary>
    /// <param name="libraryPath">The library root, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="contentId">The first non-whitespace content id, or null.</param>
    public static bool TryReadMarkerContentId(string libraryPath, out string? contentId)
    {
        contentId = null;
        var marker = Path.Combine(libraryPath, "libraryfolder.vdf");
        if (!File.Exists(marker))
        {
            return false;
        }
        contentId = ValuesOf(File.ReadAllText(marker), "contentid")
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        return contentId is not null;
    }

    /// <summary>Canonical form used to decide whether two registrations name the
    /// same folder: separator direction unified, trailing separators dropped, case
    /// folded. Mirrors the normalizer the injected CEF expressions use, so the
    /// closed-Steam and live paths can never disagree about "the same folder".
    /// </summary>
    /// <param name="path">A library path as stored or as supplied.</param>
    /// <returns>The comparable form, or an empty string for an empty input.</returns>
    public static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    /// <summary>Offsets of every top-level numbered entry line, in file order.</summary>
    private static List<int> TopLevelEntryStarts(string vdf)
    {
        var starts = new List<int>();
        var lineStart = 0;
        while (lineStart < vdf.Length)
        {
            var lineEnd = vdf.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = vdf.Length;
            }
            if (IsTopLevelEntry(vdf[lineStart..lineEnd].TrimEnd('\r')))
            {
                starts.Add(lineStart);
            }
            lineStart = lineEnd + 1;
        }
        return starts;
    }

    private static string RenumberEntries(string vdf)
    {
        var lines = vdf.Split('\n');
        var index = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd('\r');
            if (!IsTopLevelEntry(line))
            {
                continue;
            }
            var suffix = raw.EndsWith('\r') ? "\r" : "";
            lines[i] = $"\t\"{index++}\"{suffix}";
        }
        return string.Join("\n", lines);
    }

    private static bool IsTopLevelEntry(string line)
    {
        if (line.Length < 4 || line[0] != '\t' || line[1] != '"'
            || line.StartsWith("\t\t", StringComparison.Ordinal))
        {
            return false;
        }
        var end = line.IndexOf('"', 2);
        return end > 2 && int.TryParse(line[2..end], NumberStyles.None,
            CultureInfo.InvariantCulture, out _);
    }

    private static bool TryReadValue(string line, string key, out string value)
    {
        value = "";
        var trimmed = line.TrimStart('\t', ' ');
        var marker = $"\"{key}\"";
        if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }
        return TryReadQuoted(trimmed[marker.Length..].TrimStart('\t', ' '), out value);
    }

    /// <summary>Reads one quoted VDF value and returns it UNESCAPED. A backslash
    /// escapes the next character, so an escaped quote does not terminate the
    /// value: a label may legitimately contain <c>"</c> or <c>\</c> (both are
    /// written escaped by <see cref="EscapeValue"/>) and a path is stored with its
    /// backslashes doubled. Scanning to the first raw quote instead truncated such
    /// a value, and returning it still escaped doubled it on the next write.</summary>
    private static bool TryReadQuoted(string rest, out string value)
    {
        value = "";
        if (rest.Length < 2 || rest[0] != '"')
        {
            return false;
        }
        var builder = new StringBuilder(rest.Length - 1);
        for (var i = 1; i < rest.Length; i++)
        {
            var current = rest[i];
            if (current == '\\' && i + 1 < rest.Length)
            {
                builder.Append(rest[++i]);
                continue;
            }
            if (current == '"')
            {
                value = builder.ToString();
                return true;
            }
            builder.Append(current);
        }
        return false;
    }

    /// <summary>The next free top-level entry index: highest existing numbered
    /// block + 1. Line-based scan for <c>\t"N"</c> at nesting depth one.</summary>
    /// <param name="vdf">The config file text.</param>
    internal static int NextIndex(string vdf)
    {
        var highest = -1;
        foreach (var rawLine in vdf.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!IsTopLevelEntry(line))
            {
                continue;
            }
            var end = line.IndexOf('"', 2);
            if (int.TryParse(line[2..end], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index)
                && index > highest)
            {
                highest = index;
            }
        }
        return highest + 1;
    }

    /// <summary>Splices a new registration block into
    /// <c>config\libraryfolders.vdf</c>, preserving every existing byte — the
    /// block is inserted immediately before the file's final closing brace.
    /// Returns false (with <paramref name="updated"/> = null) when the file does
    /// not look like a libraryfolders file or an entry with this content id is
    /// already present. Dedup is by content id, NOT path: a reused drive letter
    /// (card reader) is expected and Steam allows several libraries at one path.</summary>
    /// <param name="vdf">The current file text (LF line endings).</param>
    /// <param name="libraryPath">The plain library path, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="contentId">The library id (must match the card marker).</param>
    /// <param name="totalSize">The volume size in bytes.</param>
    /// <param name="updated">The new file text on success.</param>
    /// <param name="label">The user-chosen library label, or empty for none.</param>
    public static bool TrySplice(
        string vdf, string libraryPath, string contentId, long totalSize,
        out string? updated, string label = "")
    {
        updated = null;
        if (!vdf.StartsWith("\"libraryfolders\"\n{\n", StringComparison.Ordinal)
            || IsContentIdRegistered(vdf, contentId))
        {
            return false;
        }
        // The root block's closing brace is the last '}' in the file; everything
        // after it (a trailing LF, per Steam's own writes) is preserved.
        var close = vdf.LastIndexOf('}');
        if (close <= 0)
        {
            return false;
        }
        var entry = BuildConfigEntry(NextIndex(vdf), libraryPath, contentId, totalSize, label);
        updated = vdf[..close] + entry + vdf[close..];
        return true;
    }
}
