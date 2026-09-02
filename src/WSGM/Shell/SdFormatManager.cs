using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>The SteamOS-style "Format SD Card" engine: erase a removable drive,
/// give it a single NTFS volume tuned for game libraries, and put a ready
/// Steam library structure on it. Windows Steam has no such flow of its own.
///
/// The main input is a card straight out of a Steam Deck — GPT plus ext4, no
/// Windows drive letter — so the whole job runs at DISK level through diskpart
/// rather than on a drive letter. The mechanism is THREE separate diskpart runs
/// with a volume-arrival wait, every run re-verified on fresh DISK handles
/// first; the device evidence behind that shape is in <c>Shell\AGENTS.md</c>.
/// 128K allocation units mirror the user's proven reference card; quick format
/// only (a full format writes every sector of a wear-limited card for nothing).
///
/// Enumeration is disk-level too (the eject list only sees mounted volumes) and
/// runs off-thread on demand — no background polling. Rows reconcile in place
/// (gamepad-cursor discipline).</summary>
public sealed class SdFormatManager : INotifyPropertyChanged
{
    /// <summary>Raised after a status property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread when a format run finishes, with the
    /// terminal message — the controller surfaces it even when the overlay has
    /// been closed mid-format.</summary>
    public event Action<string, bool>? Finished;

    /// <summary>The volume/library label used when the user names nothing.</summary>
    internal const string DefaultLabel = "Games";

    /// <summary>Sanitizes a user-typed name into a value safe as both an NTFS
    /// volume label and a Steam library label: trims, keeps ASCII letters, digits,
    /// space, dash and underscore (so the diskpart script stays plain ASCII and no
    /// quote can break out of the label token), caps at 32 characters, and falls
    /// back to <see cref="DefaultLabel"/> when nothing usable remains.</summary>
    /// <param name="name">The raw name, or null.</param>
    internal static string SanitizeLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultLabel;
        }
        var kept = new string(name.Trim()
            .Where(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or ' ' or '-' or '_')
            .Take(32)
            .ToArray())
            .Trim();
        return kept.Length == 0 ? DefaultLabel : kept;
    }

    private int _refreshing;

    /// <summary>Serializes format runs: strictly one at a time.</summary>
    private readonly SemaphoreSlim _formatGate = new(1, 1);

    /// <summary>Gets the candidate drives, one row per physical disk.</summary>
    public ObservableCollection<FormatTargetEntry> Targets { get; } = [];

    private bool _hasTargets;
    /// <summary>Gets whether any formattable drive is present.</summary>
    public bool HasTargets
    {
        get => _hasTargets;
        private set
        {
            if (_hasTargets != value)
            {
                _hasTargets = value;
                Raise(nameof(HasTargets));
            }
        }
    }

    private bool _busy;
    /// <summary>Gets whether a format run is in flight. The flow's buttons and
    /// the target list disable while true.</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (_busy != value)
            {
                _busy = value;
                Raise(nameof(Busy));
                Raise(nameof(NotBusy));
            }
        }
    }

    /// <summary>Gets the inverse of <see cref="Busy"/>, for IsEnabled bindings.</summary>
    public bool NotBusy => !Busy;

    private string _statusText = "";
    /// <summary>Gets the current stage or terminal outcome of the format run.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText != value)
            {
                _statusText = value;
                Raise(nameof(StatusText));
                Raise(nameof(HasStatus));
            }
        }
    }

    /// <summary>Gets whether a status line should be shown.</summary>
    public bool HasStatus => StatusText.Length > 0;

    // ---- enumeration ----

    /// <summary>Re-enumerates the candidate disks off-thread and reconciles the
    /// bound list. Called when the flow opens and from its refresh button.</summary>
    public void Refresh()
    {
        if (Busy)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var targets = ReadTargets();
                Dispatcher.UIThread.Post(() => Apply(targets));
            }
            catch (Exception ex)
            {
                Log.Warn($"Format: enumeration failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>One formattable disk as the background snapshot reports it.</summary>
    /// <param name="Id">The row identity (device instance path or "disk:N").</param>
    /// <param name="DiskNumber">The physical disk number.</param>
    /// <param name="Name">Vendor/product identity.</param>
    /// <param name="SizeBytes">Total disk size.</param>
    /// <param name="BusType">The STORAGE_BUS_TYPE value.</param>
    /// <param name="Letters">Currently mounted letters on this disk, if any.</param>
    /// <param name="HasLinuxPartitions">Whether ext4-style partitions were found.</param>
    internal sealed record FormatTarget(
        string Id, int DiskNumber, string Name, long SizeBytes, int BusType,
        IReadOnlyList<char> Letters, bool HasLinuxPartitions);

    /// <summary>Reads the current candidate list. Worker thread only.</summary>
    private static List<FormatTarget> ReadTargets()
    {
        var systemDisks = RemovableDriveManager.ResolveSystemDisks();

        // Letters per disk, for the detail line (a letterless Deck card is the
        // normal case and simply shows none).
        var lettersByDisk = new Dictionary<int, List<char>>();
        foreach (var volume in NativeStorage.MountedVolumes())
        {
            if (volume.DeviceType == NativeStorage.FileDeviceDisk && volume.Disk >= 0)
            {
                (lettersByDisk.TryGetValue(volume.Disk, out var list)
                    ? list
                    : lettersByDisk[volume.Disk] = []).Add(volume.Letter);
            }
        }

        var result = new List<FormatTarget>();
        var seenDisks = new HashSet<int>();
        foreach (var path in NativeStorage.ListDiskInterfaces())
        {
            using var probe = NativeStorage.OpenVolumeForQueryPath(path);
            if (probe.IsInvalid
                || !NativeStorage.TryGetDeviceNumber(probe, out _, out var disk)
                || disk < 0 || !seenDisks.Add(disk)
                || RemovableDriveManager.ClassifyDisk(disk, systemDisks) is null)
            {
                continue;
            }
            using var handle = NativeStorage.OpenDiskForRead(disk);
            if (handle.IsInvalid)
            {
                continue;
            }
            var size = NativeStorage.GetDiskLength(handle);
            NativeStorage.TryGetDeviceDescriptor(handle, out var busType, out var product);
            var linux = NativeStorage.TryGetPartitionTypes(handle, out _, out var partitions)
                && partitions.Any(p => p.IsLinux);
            var id = NativeStorage.TryGetDevNode(path, out var devInst)
                ? NativeStorage.GetDeviceInstanceId(devInst)
                : "";
            result.Add(new FormatTarget(
                id.Length > 0 ? id : $"disk:{disk}",
                disk, product, size, busType,
                lettersByDisk.TryGetValue(disk, out var letters)
                    ? [.. letters.OrderBy(l => l)]
                    : [],
                linux));
        }
        return result;
    }

    /// <summary>The row's detail line: capacity — bus kind — letters — hint.</summary>
    /// <param name="target">The enumerated disk.</param>
    internal static string DescribeTarget(FormatTarget target)
    {
        var parts = new List<string>
        {
            RemovableDriveManager.FormatSize(target.SizeBytes),
            DescribeBus(target.BusType),
        };
        if (target.Letters.Count > 0)
        {
            parts.Add(RemovableDriveManager.FormatLetters([.. target.Letters]));
        }
        if (target.HasLinuxPartitions)
        {
            parts.Add("Linux partitions — looks like a Steam Deck card");
        }
        return string.Join(" — ", parts.Where(p => p.Length > 0));
    }

    /// <summary>Names the bus for the row and confirm views. USB stays generic:
    /// a USB-bridged internal card reader and a stick both say USB, and the
    /// product name is what tells them apart.</summary>
    /// <param name="busType">The STORAGE_BUS_TYPE value.</param>
    internal static string DescribeBus(int busType) => busType switch
    {
        NativeStorage.BusTypeSd or NativeStorage.BusTypeMmc => "SD card",
        NativeStorage.BusTypeUsb => "USB",
        _ => "",
    };

    /// <summary>Merges a fresh target list into the bound collection without
    /// replacing surviving rows.</summary>
    private void Apply(List<FormatTarget> fresh)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in fresh)
        {
            seen.Add(target.Id);
            var row = FindTarget(target.Id);
            if (row is null || row.DiskNumber != target.DiskNumber)
            {
                if (row is not null)
                {
                    // Same device, new disk number (re-enumerated by Windows):
                    // replace the row — the number is part of the safety check.
                    Targets.Remove(row);
                }
                row = new FormatTargetEntry(target.Id, target.DiskNumber);
                Targets.Add(row);
                Log.Info($"Format: candidate {target.Name} disk={target.DiskNumber} "
                    + $"bus={target.BusType} size={target.SizeBytes} "
                    + $"letters={string.Concat(target.Letters)} linux={target.HasLinuxPartitions}");
            }
            row.Name = target.Name;
            row.SizeBytes = target.SizeBytes;
            row.BusType = target.BusType;
            row.HasLinuxPartitions = target.HasLinuxPartitions;
            // The card's current letter, pinned so the format reassigns exactly
            // it — a card reader must keep its letter across reformats and swaps
            // (emulator/library paths depend on it). '\0' only for a card with no
            // letter at all (raw / ext4 Deck card being set up the first time).
            row.PreferredLetter = target.Letters.Count > 0 ? target.Letters[0] : '\0';
            row.Detail = DescribeTarget(target);
        }
        for (var i = Targets.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Targets[i].Id))
            {
                Targets.RemoveAt(i);
            }
        }
        HasTargets = Targets.Count > 0;
    }

    private FormatTargetEntry? FindTarget(string id)
    {
        foreach (var entry in Targets)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    // ---- the format run ----

    /// <summary>The first diskpart script for one target: erase and repartition.
    /// `clean` (never `clean all`) wipes any prior layout — GPT+ext4 Deck cards
    /// included — and MBR is the correct default for removable SD media.</summary>
    /// <param name="diskNumber">The physical disk number.</param>
    internal static string BuildDiskpartPartitionScript(int diskNumber) =>
        $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}\r\n"
        + "clean\r\n"
        + "create partition primary\r\n";

    /// <summary>The second diskpart script for one target: quick NTFS format with
    /// 128K allocation units (the proven game-library tuning).
    ///
    /// A SEPARATE run from <see cref="BuildDiskpartPartitionScript"/>, issued
    /// only after <see cref="WaitForVolume"/> — the device-observed volume race
    /// that forbids merging the scripts is recorded in <c>Shell\AGENTS.md</c>.
    ///
    /// The letter is deliberately NOT part of this script: by the time it runs,
    /// Windows automount has normally already given the new volume its letter,
    /// and <see cref="BuildDiskpartAssignScript"/> is only run when it has not
    /// handed out the one the card must keep.</summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <param name="label">The volume label (already sanitized); quoted so a name
    /// with spaces stays one token.</param>
    internal static string BuildDiskpartFormatScript(
        int diskNumber, string label = DefaultLabel) =>
        $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}\r\n"
        + "select partition 1\r\n"
        + $"format fs=ntfs quick unit=128k label=\"{label}\"\r\n";

    /// <summary>The third, conditional diskpart script: give the formatted volume
    /// its drive letter when automount did not.
    ///
    /// The letter is PINNED to the card's current one when it has it: a bare
    /// `assign` hands out the next free letter, which device-observed moved a
    /// card from E: to D: across a format and collided with another library's
    /// path. A card reader's letter must stay put across reformats and swaps.
    /// A letterless card (raw / ext4 Deck card) gets a bare `assign`.</summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <param name="preferredLetter">The letter to reassign, or '\0' for none.</param>
    internal static string BuildDiskpartAssignScript(int diskNumber, char preferredLetter) =>
        $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}\r\n"
        + "select partition 1\r\n"
        + (preferredLetter is >= 'A' and <= 'Z'
            ? $"assign letter={preferredLetter}\r\n"
            : "assign\r\n");

    /// <summary>How long the format waits for the freshly created partition's
    /// volume to be surfaced by the volume manager before the format run is
    /// attempted anyway.</summary>
    private const int VolumeWaitMs = 20_000;

    /// <summary>How many times the format run is attempted; each failure waits
    /// <see cref="FormatRetryDelayMs"/> before the next try.</summary>
    private const int FormatAttempts = 3;

    /// <summary>The pause between format-run attempts.</summary>
    private const int FormatRetryDelayMs = 2_000;

    /// <summary>Erases and formats one target and puts a Steam library on it.
    /// Serialized; progress lands in <see cref="StatusText"/>; the terminal
    /// message also fires <see cref="Finished"/>.</summary>
    /// <param name="entry">The target to format.</param>
    /// <param name="name">The user-chosen volume/library name, or null for the default.</param>
    public async Task FormatAsync(FormatTargetEntry entry, string? name = null)
    {
        if (Busy)
        {
            return;
        }
        var label = SanitizeLabel(name);
        // Declared out here so the catch can compensate: once the Steam registration is
        // removed, ANY later failure must try to put it back, not just a non-zero
        // diskpart exit. The restore no-ops when the card's marker is gone (the erase
        // did happen), so it is safe on every path. Only a registration that was
        // ACTUALLY removed lands here — restoring one the user had deleted themselves
        // would ADD a library the format never took away.
        string? removedContentId = null;
        var removedLabel = "";
        await _formatGate.WaitAsync();
        try
        {
            Busy = true;
            StatusText = $"Erasing {entry.Name}...";
            Log.Info($"Format: starting for {entry.Name} (disk {entry.DiskNumber}, "
                + $"{entry.SizeBytes} bytes, bus {entry.BusType}).");

            // Only a definite "not elevated" blocks; unknown proceeds and lets
            // diskpart's own error surface (shell mode is elevated in practice).
            if (ElevationCheck.IsCurrentProcessElevated() == false)
            {
                Finish("Formatting needs administrator rights, which WSGM does not have "
                    + "right now.", false);
                return;
            }

            var verify = await Task.Run(() => VerifyTarget(entry));
            if (verify is not null)
            {
                Finish(verify, false);
                return;
            }

            // Diskpart locks the volume; the ACF watcher's directory handle on the
            // card would make that lock fail. Stand it down (it resumes on its own).
            CardAcfWatcher.SuspendAll();

            // A card reader reuses its drive letter. If this is a WSGM-formatted
            // card, first remove its existing Steam library by the marker's stable
            // content id; otherwise the live client keeps the old app list as
            // ghost entries after diskpart has erased the manifests. The removal
            // also reports the marker's id for the post-erase card retirement, so
            // the marker is read once.
            var removal = await Task.Run(() => RemoveExistingLibrary(entry));
            if (removal.Failure is not null)
            {
                Finish(removal.Failure, false);
                return;
            }
            var retiredContentId = removal.MarkerContentId;
            removedContentId = removal.RemovedContentId;
            removedLabel = removal.RemovedLabel;

            // Stand the ACF watcher down again: the removal above can spend its whole
            // CEF budget, and the first suspension window would then lapse while
            // diskpart is still starting — WSGM would re-open a directory handle on the
            // very volume it is about to erase and veto its own format.
            CardAcfWatcher.SuspendAll();

            var keepLetter = entry.PreferredLetter;

            // Re-verify on fresh handles right before the only irreversible verb —
            // the removal above can outlast a card swap (Shell\AGENTS.md).
            var beforeErase = await Task.Run(() => ReadTargetIdentity(entry));
            LogReverification(entry, beforeErase, ReverifiedStages[0]);
            if (beforeErase.Identity == TargetIdentity.Changed)
            {
                // Nothing has been erased yet, so the registration this run removed
                // belongs to a card that is still intact: put it back, exactly as the
                // diskpart-failure branch below does.
                await Task.Run(() => RestoreRemovedLibraryIfCardSurvived(
                    entry, removedContentId, removedLabel));
                Finish("The drive changed since it was listed — refresh and pick it again.",
                    false);
                return;
            }

            var (partitionExit, partitionOutput) = await RunDiskpart(
                BuildDiskpartPartitionScript(entry.DiskNumber));
            if (partitionExit != 0)
            {
                await Task.Run(() => RestoreRemovedLibraryIfCardSurvived(
                    entry, removedContentId, removedLabel));
                Log.Warn($"Format: diskpart clean/partition failed (exit {partitionExit}). "
                    + $"Output:\n{partitionOutput}");
                Finish("Formatting failed — Windows could not rebuild the drive. "
                    + "Reinsert the card and try again.", false);
                return;
            }
            Log.Info($"Format: disk {entry.DiskNumber} erased and repartitioned.");

            // The erase destroyed the old library, so there is nothing left to
            // compensate: a later failure must not splice its identity back in beside
            // the fresh marker CreateSteamLibrary is about to write.
            removedContentId = null;
            removedLabel = "";

            // The old card provably no longer exists from here on, so retire it now:
            // a later failure (unmounted volume, lost drive letter) must not leave it
            // in the card database forever with an identity nothing can rediscover.
            if (!string.IsNullOrEmpty(retiredContentId))
            {
                await LibraryTabManager.MutateConfigAsync<object?>(config =>
                {
                    config.CardLibraries.RemoveAll(c => string.Equals(
                        c.ContentId, retiredContentId, StringComparison.Ordinal));
                    return null;
                });
            }

            // Give the volume manager time to surface the new partition's volume
            // before diskpart is asked to format it (see BuildDiskpartFormatScript);
            // a wait that runs out is logged and the format still attempted, since
            // diskpart itself may see the volume by then.
            StatusText = $"Formatting {entry.Name}...";
            var volumeWaitMs = await Task.Run(() => WaitForVolume(entry.DiskNumber));
            if (volumeWaitMs < 0)
            {
                Log.Warn($"Format: no volume appeared on disk {entry.DiskNumber} within "
                    + $"{VolumeWaitMs / 1000} s; attempting the format anyway.");
            }
            else
            {
                Log.Info($"Format: volume on disk {entry.DiskNumber} appeared after "
                    + $"{volumeWaitMs} ms.");
            }
            var formatScript = BuildDiskpartFormatScript(entry.DiskNumber, label);
            var (formatExit, formatOutput) = (-1, "");
            for (var attempt = 1; attempt <= FormatAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    Log.Warn($"Format: diskpart format attempt {attempt - 1} of {FormatAttempts} "
                        + $"failed (exit {formatExit}); retrying in {FormatRetryDelayMs} ms. "
                        + $"Output:\n{formatOutput}");
                    await Task.Delay(FormatRetryDelayMs);
                }
                // Re-verify per attempt: each is a fresh diskpart resolving
                // `select disk N` after waits long enough for a swap (Shell\AGENTS.md).
                var beforeFormat = await Task.Run(() => ReadTargetIdentity(entry));
                LogReverification(entry, beforeFormat, ReverifiedStages[1]);
                if (beforeFormat.Identity == TargetIdentity.Changed)
                {
                    // No compensation on this path: the erase already destroyed the old
                    // library, which is why removedContentId/removedLabel were cleared.
                    Finish(CardChangedMidRunMessage, false);
                    return;
                }
                (formatExit, formatOutput) = await RunDiskpart(formatScript);
                if (formatExit == 0)
                {
                    break;
                }
            }
            if (formatExit != 0)
            {
                Log.Warn($"Format: diskpart format failed after {FormatAttempts} attempts "
                    + $"(exit {formatExit}). Output:\n{formatOutput}");
                Finish("Formatting failed — Windows could not format the new drive. "
                    + "Reinsert the card and try again.", false);
                return;
            }
            Log.Info($"Format: diskpart formatted disk {entry.DiskNumber}.");

            // Automount normally hands the new volume its letter the moment it
            // arrives — usually the card's own, freed by the erase. Only when the
            // card is not sitting on that letter now does diskpart assign it.
            StatusText = "Waiting for the new drive...";
            var letter = await Task.Run(() => WaitForLetter(entry.DiskNumber, LetterProbeAttempts));
            if (letter is null || (keepLetter is >= 'A' and <= 'Z' && letter.Value != keepLetter))
            {
                Log.Info($"Format: disk {entry.DiskNumber} is on "
                    + (letter is null ? "no letter" : $"{letter}:")
                    + " after the format; assigning "
                    + (keepLetter is >= 'A' and <= 'Z' ? $"{keepLetter}:." : "a letter."));

                // Last re-verify before the assign pins the old card's letter onto
                // whatever is in the reader (Shell\AGENTS.md). No compensation here either.
                var beforeAssign = await Task.Run(() => ReadTargetIdentity(entry));
                LogReverification(entry, beforeAssign, ReverifiedStages[2]);
                if (beforeAssign.Identity == TargetIdentity.Changed)
                {
                    Finish(CardChangedMidRunMessage, false);
                    return;
                }

                var (assignExit, assignOutput) = await RunDiskpart(
                    BuildDiskpartAssignScript(entry.DiskNumber, keepLetter));
                if (assignExit != 0)
                {
                    Log.Warn($"Format: diskpart assign failed (exit {assignExit}). "
                        + $"Output:\n{assignOutput}");
                }
                letter = await Task.Run(() => WaitForLetter(entry.DiskNumber));
            }
            if (letter is null)
            {
                Finish("The drive was formatted, but Windows did not mount it. "
                    + "Reinsert the card.", false);
                return;
            }
            // The card reader must keep its letter. If diskpart could not put it
            // back (letter held by something else), stop rather than silently
            // leaving the card on a different one that would break every path
            // pointing at it (emulators, libraries).
            if (keepLetter is >= 'A' and <= 'Z' && letter.Value != keepLetter)
            {
                Log.Warn($"Format: expected to keep letter {keepLetter}: but disk "
                    + $"{entry.DiskNumber} mounted as {letter}:.");
                Finish($"Formatted, but Windows could not keep drive letter {keepLetter}:. "
                    + $"It is now {letter}: — free {keepLetter}: and reformat, or reassign "
                    + $"the letter in Disk Management.", false);
                return;
            }
            Log.Info($"Format: disk {entry.DiskNumber} mounted as {letter}: "
                + $"(letter preserved={keepLetter is >= 'A' and <= 'Z'}).");

            // TRIM the fresh (near-empty) volume so the flash controller learns
            // almost the whole card is free — proven to restore SD write speed.
            // Best-effort: a reader that does not pass TRIM just logs and moves on.
            StatusText = "Optimizing the card...";
            await RetrimVolume(letter.Value);

            StatusText = "Creating Steam library...";
            var summary = await Task.Run(
                () => CreateSteamLibrary(letter.Value, entry.SizeBytes, label));
            Finish(summary, true);
        }
        catch (Exception ex)
        {
            Log.Error("Format: run failed.", ex);
            try
            {
                await Task.Run(() => RestoreRemovedLibraryIfCardSurvived(
                    entry, removedContentId, removedLabel));
            }
            catch (Exception restoreEx)
            {
                Log.Error("Format: could not restore the removed library.", restoreEx);
            }
            Finish("Formatting failed unexpectedly — see the log.", false);
        }
        finally
        {
            Busy = false;
            _formatGate.Release();
        }
    }

    /// <summary>Verifies the target on fresh handles before the run starts: the
    /// disk number must still belong to a device with the same size and bus,
    /// still hot-pluggable, still not a system disk. Returns null when safe, else
    /// the refusal message. Deliberately STRICTER than the mid-run re-checks: at
    /// this point nothing has been erased, so an unopenable or unreadable disk
    /// aborts up front — the Unreadable-continues tolerance belongs only to the
    /// re-verifications after `clean` (see <see cref="CompareIdentity"/>).</summary>
    private static string? VerifyTarget(FormatTargetEntry entry)
    {
        var snapshot = ReadTargetIdentity(entry);
        if (snapshot.SystemDisk)
        {
            return "This drive hosts Windows or WSGM and cannot be formatted.";
        }
        if (!snapshot.HandleOpened)
        {
            return "The drive is no longer reachable. Reinsert it and try again.";
        }
        if (!snapshot.Removable)
        {
            return "The drive no longer reports as removable — not formatting it.";
        }
        if (snapshot.SizeBytes != entry.SizeBytes || snapshot.BusType != entry.BusType)
        {
            Log.Warn($"Format: disk {entry.DiskNumber} changed identity "
                + $"(size {entry.SizeBytes}->{snapshot.SizeBytes}, "
                + $"bus {entry.BusType}->{snapshot.BusType}).");
            return "The drive changed since it was listed — refresh and pick it again.";
        }
        return null;
    }

    /// <summary>What a fresh look at the target's disk number says about the media
    /// sitting there now, compared with the identity the run started from.</summary>
    internal enum TargetIdentity
    {
        /// <summary>Everything that could be read still matches the picked card.</summary>
        Same,

        /// <summary>The disk did not answer its identity queries. NOT a mismatch: a
        /// reader whose media is momentarily not ready reports exactly this, and the
        /// run must carry on so the existing waits and retries can still rescue it.</summary>
        Unreadable,

        /// <summary>The disk number now belongs to something else — a different
        /// capacity or bus, no longer removable media, or a system disk.</summary>
        Changed,
    }

    /// <summary>Decides whether the disk behind the target's number is still the card
    /// the user picked. Pure, so the ordering below is testable. Identity predicates
    /// only, a query failure is never a mismatch, and a same-capacity swap stays
    /// invisible — the device evidence is in <c>Shell\AGENTS.md</c>.</summary>
    /// <param name="opened">The disk handle opened and answered its hotplug query.</param>
    /// <param name="systemDisk">The disk number now hosts Windows or WSGM.</param>
    /// <param name="removable">It still classifies as hot-pluggable/removable media.</param>
    /// <param name="size">The disk length just read; 0 means the query failed.</param>
    /// <param name="busType">The STORAGE_BUS_TYPE just read; -1 means the query failed.</param>
    /// <param name="expectedSize">The size the run started from.</param>
    /// <param name="expectedBusType">The bus type the run started from.</param>
    internal static TargetIdentity CompareIdentity(
        bool opened, bool systemDisk, bool removable, long size, int busType,
        long expectedSize, int expectedBusType)
    {
        if (systemDisk)
        {
            // First and unconditional: a disk number that now points at system
            // storage must abort even when nothing else about it could be read.
            return TargetIdentity.Changed;
        }
        if (!opened || size <= 0)
        {
            return TargetIdentity.Unreadable;
        }
        // The sentinel tolerance is SYMMETRIC: size 0 / bus -1 are query-failure
        // values on BOTH sides (the enumeration baseline can carry them too), and
        // a fact we never had cannot contradict one we just read (Shell\AGENTS.md).
        return !removable
            || (size > 0 && expectedSize > 0 && size != expectedSize)
            || (busType >= 0 && expectedBusType >= 0 && busType != expectedBusType)
            ? TargetIdentity.Changed
            : TargetIdentity.Same;
    }

    /// <summary>The destructive diskpart runs, in the order FormatAsync issues them.
    /// Each one re-verifies the target's identity first; the array is what a test can
    /// pin, because the guards themselves sit on a device-only flow that is never
    /// automated. Dropping a stage here fails <c>SdFormatTests</c>.</summary>
    internal static readonly string[] ReverifiedStages = ["clean/partition", "format", "assign"];

    /// <summary>One (re-)verification pass: the verdict plus the raw facts behind
    /// it, so an abort can name what differed in a pasted log.</summary>
    /// <param name="Identity">The verdict.</param>
    /// <param name="SystemDisk">Whether the disk number now hosts Windows or WSGM.</param>
    /// <param name="HandleOpened">Whether the disk handle opened at all — the
    /// up-front check refuses on this where the mid-run checks tolerate it.</param>
    /// <param name="Removable">Whether it still reports as removable media.</param>
    /// <param name="SizeBytes">The size just read, 0 when the query failed.</param>
    /// <param name="BusType">The bus type just read, -1 when the query failed.</param>
    private readonly record struct TargetIdentitySnapshot(
        TargetIdentity Identity, bool SystemDisk, bool HandleOpened, bool Removable,
        long SizeBytes, int BusType);

    /// <summary>Re-reads the target's identity on FRESH handles immediately before one
    /// destructive diskpart run. Disk handle only — never a volume handle: an open
    /// volume handle is exactly what makes diskpart's own volume lock fail, which is
    /// why <see cref="CardAcfWatcher.SuspendAll"/> is called before the erase at all.
    /// Identity predicates only (see <see cref="CompareIdentity"/>); nothing here may
    /// read the filesystem, because `clean` legitimately erases it before the second
    /// and third runs. Worker thread.</summary>
    /// <param name="entry">The target being formatted.</param>
    private static TargetIdentitySnapshot ReadTargetIdentity(FormatTargetEntry entry)
    {
        var systemDisk = RemovableDriveManager.ResolveSystemDisks().Contains(entry.DiskNumber);
        // Declared up front: an out-var introduced in the right operand of && is not
        // definitely assigned afterwards (CS0165, an error under the Release gate).
        var opened = false;
        var removable = false;
        var size = 0L;
        var busType = -1;
        using var handle = NativeStorage.OpenDiskForRead(entry.DiskNumber);
        var handleOpened = !handle.IsInvalid;
        if (handleOpened
            && NativeStorage.TryGetHotplugInfo(handle, out var media, out var hotplug))
        {
            opened = true;
            removable = RemovableDriveManager.Classify(hotplug, media) is not null;
            size = NativeStorage.GetDiskLength(handle);
            NativeStorage.TryGetDeviceDescriptor(handle, out busType, out _);
        }
        return new TargetIdentitySnapshot(
            CompareIdentity(opened, systemDisk, removable, size, busType,
                entry.SizeBytes, entry.BusType),
            systemDisk, handleOpened, removable, size, busType);
    }

    /// <summary>Logs one re-verification verdict. A mismatch is an abort reason and
    /// names every fact that differs; an unreadable disk does NOT stop the run but is
    /// logged too, so a pasted log always says which of the two happened.</summary>
    /// <param name="entry">The target being formatted.</param>
    /// <param name="snapshot">What the re-verification saw.</param>
    /// <param name="stage">The diskpart run that was about to be issued.</param>
    private static void LogReverification(
        FormatTargetEntry entry, TargetIdentitySnapshot snapshot, string stage)
    {
        if (snapshot.Identity == TargetIdentity.Changed)
        {
            Log.Warn($"Format: disk {entry.DiskNumber} is not the card that was picked — "
                + $"aborting before the {stage} run (system disk {snapshot.SystemDisk}, "
                + $"removable {snapshot.Removable}, size {entry.SizeBytes}->{snapshot.SizeBytes}, "
                + $"bus {entry.BusType}->{snapshot.BusType}).");
        }
        else if (snapshot.Identity == TargetIdentity.Unreadable)
        {
            Log.Info($"Format: disk {entry.DiskNumber} did not answer the identity re-check "
                + $"before the {stage} run (size {snapshot.SizeBytes}, bus {snapshot.BusType}); "
                + "continuing — a reader that is not ready yet is not a swapped card.");
        }
    }

    /// <summary>The refusal when a re-verification BETWEEN the destructive runs finds a
    /// different card: the run stops where it is rather than quick-formatting media the
    /// user never picked, or pinning the old card's drive letter onto it.</summary>
    private const string CardChangedMidRunMessage =
        "The card changed while it was being formatted, so WSGM stopped. Reinsert the card "
        + "you want to format and start again.";

    /// <summary>What the pre-format removal did: the user-facing refusal when the
    /// old library cannot safely be removed, and — only when a registration was
    /// ACTUALLY taken out of Steam — its identity and label, so the failure
    /// compensation restores exactly what it removed and never invents a
    /// registration the user had deleted themselves. The marker id rides along
    /// so the post-erase retirement does not re-read the card's marker.</summary>
    /// <param name="Failure">The refusal message, or null when the run may proceed.</param>
    /// <param name="RemovedContentId">The removed registration's content id, or null.</param>
    /// <param name="RemovedLabel">The removed registration's label, empty when it had none.</param>
    /// <param name="MarkerContentId">The content id the card's own marker carried,
    /// whether or not Steam had it registered; null when no marker was found.</param>
    private readonly record struct LibraryRemoval(
        string? Failure, string? RemovedContentId, string RemovedLabel,
        string? MarkerContentId = null)
    {
        internal static LibraryRemoval Nothing(string? markerContentId = null) =>
            new(null, null, "", markerContentId);

        internal static LibraryRemoval Refused(string message) => new(message, null, "");
    }

    /// <summary>Removes the existing WSGM library on a card before it is erased.
    /// The card marker's content id selects the registration, so a fixed reader
    /// drive letter cannot remove the library belonging to another card. A live
    /// Steam is changed only through CEF; when Steam is closed its next-start
    /// configuration is cleaned directly.</summary>
    /// <param name="entry">The re-verified card selected for formatting.</param>
    /// <returns>The refusal and what was actually removed.</returns>
    private static LibraryRemoval RemoveExistingLibrary(FormatTargetEntry entry)
    {
        if (entry.PreferredLetter is < 'A' or > 'Z')
        {
            return LibraryRemoval.Nothing();
        }
        var marker = FindExistingMarker(entry);
        if (marker is null)
        {
            return LibraryRemoval.Nothing();
        }
        string contentId;
        try
        {
            var values = SteamLibraryVdf.ValuesOf(File.ReadAllText(marker), "contentid");
            if (values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
            {
                Log.Warn($"Format: existing marker at {marker} has no content id.");
                return LibraryRemoval.Refused(
                    "The existing Steam library marker has no content identity. "
                    + "Formatting was stopped to avoid leaving ghost games in Steam.");
            }
            contentId = values[0];
        }
        catch (Exception ex)
        {
            Log.Warn($"Format: could not read existing marker {marker}: {ex.Message}");
            return LibraryRemoval.Refused(
                "Could not verify the existing Steam library on this card. "
                + "Close Steam and try again.");
        }

        if (!Steam.IsInstalled)
        {
            Log.Info("Format: Steam is not installed; no registration needs removal.");
            return LibraryRemoval.Nothing(contentId);
        }
        if (!Steam.TryReadLibraryFolders(out var configPath, out var configText)
            || configPath is null || configText is null)
        {
            Log.Info($"Format: Steam has no libraryfolders config; {contentId} is not registered.");
            return LibraryRemoval.Nothing(contentId);
        }

        // Identity gate — runs for BOTH the live and the closed-Steam path. Removal is
        // keyed on the content id (a reused reader letter means several cards legitimately
        // share one path), but the registration that id points at must still live on the
        // card being erased: a card carrying a copied marker would otherwise de-register
        // an internal library. Never resolve this by moving a card to another letter —
        // the reader's letter is fixed, because emulator configs store absolute paths.
        var registeredPath = SteamLibraryVdf.PathForContentId(configText, contentId);
        if (registeredPath is null)
        {
            Log.Info($"Format: content id {contentId} is not registered with Steam; "
                + "nothing to remove.");
            return LibraryRemoval.Nothing(contentId);
        }
        var cardPath = FullPathOrNull(Path.GetDirectoryName(marker));
        var registeredFullPath = FullPathOrNull(registeredPath);
        if (cardPath is null || registeredFullPath is null
            || !string.Equals(registeredFullPath, cardPath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Format: content id {contentId} is registered at "
                + $"{registeredPath}, not at the card path {Path.GetDirectoryName(marker)}.");
            return LibraryRemoval.Refused(
                "The card marker does not match the Steam library on this drive. "
                + "Formatting was stopped to protect your other libraries.");
        }

        // Captured before the removal so a failed format can put the registration back
        // with the name the user gave it, not as an unnamed library.
        var registeredLabel = SteamLibraryVdf.LabelForContentId(configText, contentId) ?? "";

        if (Steam.IsRunning)
        {
            var pathMatches = SteamLibraryVdf.ValuesOf(configText, "path")
                .Count(path => string.Equals(Path.GetFullPath(path), cardPath,
                    StringComparison.OrdinalIgnoreCase));
            if (pathMatches > 1)
            {
                return LibraryRemoval.Refused(
                    "Several card libraries share this reader path. Close Steam and try again "
                    + "so WSGM can remove only this card's content identity.");
            }
            var result = SteamCdp.RemoveLibraryByContentIdAsync(contentId, configText)
                .GetAwaiter().GetResult();
            if (result.Status == SteamLibraryRemoveStatus.Removed)
            {
                Log.Info($"Format: removed existing live Steam library (content id {contentId}, "
                    + $"status {result.Status}).");
                return new LibraryRemoval(null, contentId, registeredLabel, contentId);
            }
            Log.Warn($"Format: could not remove existing live library {contentId} "
                + $"({result.Status}: {result.Detail ?? "no detail"}).");
            return LibraryRemoval.Refused(
                "Steam could not remove this card's existing library. "
                + "Close Steam completely and try again.");
        }

        // Reached only after the identity gate above proved this content id is registered
        // at the card's own path, so removing by content id cannot touch another library.
        if (!SteamLibraryVdf.TryRemoveContentId(configText, contentId, out var updated)
            || updated is null)
        {
            Log.Info($"Format: no closed-Steam registration found for content id {contentId}.");
            return LibraryRemoval.Nothing(contentId);
        }
        if (Steam.IsRunning)
        {
            return LibraryRemoval.Refused(
                "Steam started while the card library was being prepared for removal. "
                + "Close Steam and try formatting again.");
        }
        BackupOnce(configPath);
        WriteAtomically(configPath, updated);
        Log.Info($"Format: removed closed-Steam library registration for content id {contentId}.");
        return new LibraryRemoval(null, contentId, registeredLabel, contentId);
    }

    /// <summary>Normalizes a path for comparison, treating a malformed or empty one as
    /// unknown so the caller refuses rather than proceeding on a bad match.</summary>
    /// <param name="path">The path to normalize.</param>
    private static string? FullPathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Format: could not normalize path '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>The drive letters currently mounted on one physical disk, ascending.
    /// Read fresh instead of from the row's enumeration snapshot, and covering EVERY
    /// volume of a multi-partition disk: `clean` erases all of them, so a Steam
    /// library on any one of them has to be found before the erase — not only the
    /// one on the pinned letter. Worker thread.</summary>
    /// <param name="diskNumber">The physical disk number.</param>
    private static List<char> LettersOnDisk(int diskNumber)
    {
        var letters = new List<char>();
        foreach (var volume in NativeStorage.MountedVolumes())
        {
            if (volume.DeviceType == NativeStorage.FileDeviceDisk && volume.Disk == diskNumber)
            {
                letters.Add(volume.Letter);
            }
        }
        letters.Sort();
        return letters;
    }

    private static string? FindExistingMarker(FormatTargetEntry entry)
    {
        if (entry.PreferredLetter is < 'A' or > 'Z') { return null; }
        var letters = LettersOnDisk(entry.DiskNumber);
        if (letters.Count == 0)
        {
            letters.Add(entry.PreferredLetter);
        }
        var roots = letters.Select(letter => $@"{letter}:\").ToList();
        var candidates = roots
            .Select(root => Path.Combine(root, "SteamLibrary", "libraryfolder.vdf"))
            .ToList();
        if (Steam.TryReadLibraryFolders(out _, out var configText) && configText is not null)
        {
            foreach (var path in SteamLibraryVdf.ValuesOf(configText, "path"))
            {
                if (roots.Any(root => string.Equals(Path.GetPathRoot(path), root,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(Path.Combine(path, "libraryfolder.vdf"));
                }
            }
        }
        var marker = candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
        if (marker is not null
            && !string.Equals(Path.GetPathRoot(marker), $@"{entry.PreferredLetter}:\",
                StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Format: existing library marker found at {marker} — another volume of "
                + $"disk {entry.DiskNumber}, which the erase destroys as well.");
        }
        return marker;
    }

    /// <summary>Puts a removed library registration back after a failed format, with
    /// the label it carried — the card is still there (its marker survived), so it
    /// must come back exactly as it was and not as an unnamed library.</summary>
    /// <param name="entry">The card the format ran against.</param>
    /// <param name="contentId">The identity that was actually removed, or null.</param>
    /// <param name="label">The removed registration's label, empty for none.</param>
    private static void RestoreRemovedLibraryIfCardSurvived(
        FormatTargetEntry entry, string? contentId, string label)
    {
        if (string.IsNullOrEmpty(contentId)) { return; }
        var marker = FindExistingMarker(entry);
        if (marker is null) { return; }
        var libraryPath = Path.GetDirectoryName(marker)!;
        // FindExistingMarker resolves by LOCATION (the letters currently on this disk
        // number), not by identity. The pre-erase abort above reaches this after
        // PROVING the media behind that disk number is a different card, so without
        // this gate the compensation would register the swapped-in card under the
        // removed card's contentid, label and size. A no-op for the diskpart-failure
        // callers: their removedContentId was read out of this very marker.
        string? markerContentId = null;
        try
        {
            // The card can be gone by now — that is the scenario this guard exists for,
            // so an unreadable marker must refuse the restore rather than fault the run.
            SteamLibraryVdf.TryReadMarkerContentId(libraryPath, out markerContentId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Format: could not read {marker} to confirm the library identity: {ex.Message}");
        }
        if (!string.Equals(markerContentId, contentId, StringComparison.Ordinal))
        {
            Log.Warn(
                $"Format: not restoring library {contentId} — the marker now on disk "
                    + $"{entry.DiskNumber} reports contentid {markerContentId ?? "(none)"}.");
            return;
        }
        if (Steam.IsRunning)
        {
            var liveRestore = SteamCdp.AddLibrary(libraryPath, label);
            Log.Info($"Format: compensation after diskpart failure returned {liveRestore.Status}.");
            return;
        }
        if (!Steam.TryReadLibraryFolders(out var configPath, out var current)
            || configPath is null || current is null)
        {
            return;
        }
        if (!SteamLibraryVdf.IsContentIdRegistered(current, contentId)
            && SteamLibraryVdf.TrySplice(current, libraryPath, contentId, entry.SizeBytes,
                out var restored, label) && restored is not null)
        {
            WriteAtomically(configPath, restored);
            Log.Info($"Format: restored library registration {contentId} after diskpart failure.");
        }
    }

    /// <summary>Writes the script beside the log (an elevated diskpart consumes
    /// it — never %TEMP%, same rule as the de-elevation task XML), runs
    /// diskpart, deletes the script.</summary>
    /// <param name="script">The full diskpart script text.</param>
    private static async Task<(int ExitCode, string Output)> RunDiskpart(string script)
    {
        Log.Info($"Format: diskpart script:\n{script.TrimEnd()}");
        var scriptPath = Path.Combine(Log.Directory, $"format-disk-{Guid.NewGuid():N}.dp.txt");
        await using (var stream = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            await writer.WriteAsync(script);
            await writer.FlushAsync();
            stream.Flush(flushToDisk: true);
        }
        try
        {
            // Absolute System32 paths: this flow is elevated, and a bare exe name is
            // searched in the application directory (per-user install, user-writable)
            // before System32.
            var (aclExit, aclOutput) = await ConsoleTool.RunCapturedAsync(
                ConsoleTool.System32("icacls.exe"),
                $"\"{scriptPath}\" /setintegritylevel H", timeoutMs: 10_000);
            if (aclExit != 0)
            {
                throw new IOException($"Could not protect diskpart script ({aclExit}): {aclOutput}");
            }
            return await ConsoleTool.RunCapturedAsync(
                ConsoleTool.System32("diskpart.exe"), $"/s \"{scriptPath}\"", timeoutMs: 600_000);
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Issues TRIM (retrim) for the volume's free space via
    /// <c>Optimize-Volume -ReTrim</c> so the flash controller marks the freshly
    /// wiped blocks erasable — the proven SD write-speed win. Optimize-Volume is
    /// used over <c>defrag /L</c> because it retrims REMOVABLE media too (plain
    /// defrag skips it). Best-effort: a reader that does not pass TRIM just makes
    /// the cmdlet fail, which is logged and the format continues. Runs elevated
    /// (the format flow already is).</summary>
    /// <param name="letter">The just-mounted drive letter.</param>
    private static async Task RetrimVolume(char letter)
    {
        try
        {
            // Absolute path for the same reason the diskpart run uses one: an elevated
            // caller must never search the user-writable application directory.
            var powershell = Path.Combine(
                Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var (exitCode, output) = await ConsoleTool.RunCapturedAsync(
                powershell,
                "-NoProfile -NonInteractive -Command \"Optimize-Volume -DriveLetter "
                    + letter + " -ReTrim -ErrorAction Stop\"",
                timeoutMs: 300_000);
            if (exitCode == 0)
            {
                Log.Info($"Format: retrimmed {letter}: (TRIM issued for free space).");
            }
            else
            {
                Log.Info($"Format: retrim of {letter}: not applied (exit {exitCode}); "
                    + $"the reader may not pass TRIM. Output:\n{output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Format: retrim of {letter}: failed: {ex.Message}");
        }
    }

    /// <summary>Polls the volume manager's device interfaces until one of them
    /// maps back to the disk — i.e. the partition just created has a volume for
    /// diskpart's `format` to focus. Letter-agnostic on purpose: the volume has
    /// none yet. Worker thread; <see cref="VolumeWaitMs"/> cap.</summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <returns>Milliseconds until the volume was seen, or -1 on timeout.</returns>
    private static int WaitForVolume(int diskNumber)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            foreach (var path in NativeStorage.ListVolumeInterfaces())
            {
                using var volume = NativeStorage.OpenVolumeForQueryPath(path);
                if (!volume.IsInvalid
                    && NativeStorage.TryGetDeviceNumber(volume, out var type, out var disk)
                    && type == NativeStorage.FileDeviceDisk && disk == diskNumber)
                {
                    return (int)(Environment.TickCount64 - started);
                }
            }
            if (Environment.TickCount64 - started >= VolumeWaitMs)
            {
                return -1;
            }
            Thread.Sleep(250);
        }
    }

    /// <summary>How many 500 ms polls <see cref="WaitForLetter"/> spends when it
    /// is only checking whether automount already mounted the formatted volume,
    /// before diskpart is asked to assign the letter.</summary>
    private const int LetterProbeAttempts = 6;

    /// <summary>Polls for the freshly assigned drive letter by matching mounted
    /// volumes back to the disk number. Worker thread; ~15 s cap by default
    /// (typically 1-3 s after diskpart's assign).</summary>
    /// <param name="diskNumber">The physical disk number.</param>
    /// <param name="attempts">How many 500 ms polls to make before giving up.</param>
    private static char? WaitForLetter(int diskNumber, int attempts = 30)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            foreach (var volume in NativeStorage.MountedVolumes())
            {
                if (volume.Disk == diskNumber && volume.Ready)
                {
                    return volume.Letter;
                }
            }
            Thread.Sleep(500);
        }
        return null;
    }

    /// <summary>Creates the card-side Steam library (marker VDF, steamapps,
    /// steam.dll), registers it in Steam's config when possible, and pokes
    /// drive watchers with a synthetic volume-arrival broadcast — the real
    /// arrival fired when the volume was still empty, so a running Steam has
    /// already looked and found nothing. Returns the user-facing summary.
    /// Worker thread.</summary>
    private static string CreateSteamLibrary(char letter, long sizeBytes, string label)
    {
        var libraryPath = $@"{letter}:\SteamLibrary";
        Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps"));

        var steamExe = Steam.ExePath;
        Steam.TryReadLibraryFolders(out var configPath, out var configText);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (configText is not null)
        {
            foreach (var id in SteamLibraryVdf.ValuesOf(configText, "contentid"))
            {
                taken.Add(id);
            }
        }
        var contentId = SteamLibraryVdf.GenerateContentId(taken);

        // Steam drops a copy of its current client dll into every secondary
        // library root; version skew is tolerated, so this is create-time only.
        WriteMarkerAndClientDll(libraryPath, contentId, steamExe, label);

        var registration = RegisterLibrary(configPath, configText, libraryPath, contentId,
            sizeBytes, label);

        // Now that the library exists, make drive watchers look at the volume
        // again (best effort; harmless when nobody listens).
        NativeStorage.BroadcastVolumeArrival(letter);
        Log.Info($"Format: volume-arrival broadcast sent for {letter}:.");

        return $"{letter}: is ready as a Steam library. {registration}";
    }

    /// <summary>Registers the library with Steam. When Steam is RUNNING this
    /// drives Steam's own front-end API over its CEF debug port
    /// (<see cref="SteamCdp"/>) — Steam adopts, persists, mounts and scans it with
    /// no restart, which a file edit cannot do against a live client (Steam holds
    /// libraries in memory and rewrites the file on exit). When Steam is CLOSED
    /// (or its debug port is unreachable), the entry is spliced into
    /// config\libraryfolders.vdf so Steam reads it on next start; dedup there is by
    /// CONTENT ID (a card reader reuses its drive letter, so the path repeats per
    /// card). Returns the summary sentence.</summary>
    private static string RegisterLibrary(
        string? configPath, string? configText, string libraryPath, string contentId,
        long sizeBytes, string label)
    {
        // Live client: drive Steam's own front-end API over its CEF debug port so
        // Steam adds, persists, mounts and scans the library with no restart — the
        // only thing that makes a running Steam show a library live. Falls through
        // to the config write only when the debug channel cannot be reached.
        if (Steam.IsRunning)
        {
            // replaceExisting: the library at this path was created seconds ago on a
            // freshly wiped card, so any registration Steam still holds there belongs
            // to a card that is gone. Left in place, Steam lists the previous card's
            // games beside the new card's capacity until it is restarted.
            var live = SteamCdp.AddLibrary(libraryPath, label, replaceExisting: true);
            switch (live.Status)
            {
                case SteamLibraryAddStatus.Added:
                    return "Added to Steam.";
                case SteamLibraryAddStatus.AlreadyPresent:
                    return "This library is already in Steam.";
                case SteamLibraryAddStatus.Rejected:
                    Log.Warn($"Format: Steam refused the library add ({live.Detail}).");
                    return $"Steam did not accept it: {live.Detail}.";
                default:
                    Log.Warn("Format: Steam debug port unavailable — not editing its live config.");
                    return "Restart Steam, then add the library under Settings > Storage.";
            }
        }

        // Steam closed (or its debug port was unreachable): write the config; it is
        // read on next start.
        if (configPath is null || configText is null)
        {
            Log.Warn("Format: Steam config not found — skipping registration.");
            return "Add it in Steam under Settings > Storage.";
        }
        if (SteamLibraryVdf.IsContentIdRegistered(configText, contentId))
        {
            Log.Info($"Format: content id {contentId} already in libraryfolders.vdf.");
            return "This library is already in Steam.";
        }
        // Same staleness rule as the live path, applied to the file: dedup below is
        // by CONTENT ID, which cannot see a registration the previous card left at
        // this reader's drive letter under its own id. Splicing next to it would put
        // two entries at one path into the file Steam reads on next start.
        var stale = SteamLibraryVdf.TryRemovePath(configText, libraryPath, out var purged);
        if (stale > 0 && purged is not null)
        {
            Log.Info($"Format: dropped {stale} stale registration(s) at {libraryPath} "
                + "from libraryfolders.vdf before adding the new card.");
            configText = purged;
        }
        if (!SteamLibraryVdf.TrySplice(configText, libraryPath, contentId, sizeBytes,
                out var updated, label))
        {
            Log.Warn("Format: libraryfolders.vdf has an unexpected shape — not editing it.");
            return "Add it in Steam under Settings > Storage.";
        }
        BackupOnce(configPath);
        WriteAtomically(configPath, updated!);
        Log.Info($"Format: {libraryPath} registered in libraryfolders.vdf (backup written).");
        return "Added to Steam's library list (on next start).";
    }

    // ---- add an existing location as a library (no formatting) ----

    /// <summary>Turns a user-chosen folder into a registered Steam library
    /// WITHOUT formatting anything — for network shares, second internal drives
    /// (DIY Steam machines), and existing libraries. A drive root becomes
    /// <c>&lt;root&gt;SteamLibrary</c> (Steam's own layout); any other folder is
    /// used as the library root directly. An existing library (marker present)
    /// keeps its contentid untouched and is only registered.</summary>
    /// <param name="folderPath">The folder the user picked.</param>
    public async Task AddLibraryAsync(string folderPath)
    {
        if (Busy)
        {
            return;
        }
        await _formatGate.WaitAsync();
        try
        {
            Busy = true;
            StatusText = "Adding Steam library...";
            var summary = await Task.Run(() => AddLibrary(folderPath));
            Finish(summary.Message, summary.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Format: add-library failed.", ex);
            Finish("Could not add the library — see the log.", false);
        }
        finally
        {
            Busy = false;
            _formatGate.Release();
        }
    }

    /// <summary>Resolves the library root for a picked folder: drive roots get
    /// the conventional SteamLibrary subfolder, everything else is taken as-is.</summary>
    /// <param name="folderPath">The folder the user picked.</param>
    internal static string ResolveLibraryRoot(string folderPath)
    {
        var trimmed = folderPath.TrimEnd('\\', '/');
        // "D:" / "D:\" → the conventional <root>\SteamLibrary.
        return trimmed.Length == 2 && trimmed[1] == ':'
            ? $@"{trimmed}\SteamLibrary"
            : trimmed.Length == 0 ? folderPath : trimmed;
    }

    private static (string Message, bool Success) AddLibrary(string folderPath)
    {
        var libraryPath = ResolveLibraryRoot(folderPath);
        Log.Info($"Format: adding library at {libraryPath} (picked: {folderPath}).");
        try
        {
            Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Format: cannot create {libraryPath}: {ex.Message}");
            return ($"Could not create a library at {libraryPath}.", false);
        }

        var steamExe = Steam.ExePath;
        Steam.TryReadLibraryFolders(out var configPath, out var configText);

        // An existing library keeps its identity; only a fresh folder gets a
        // marker (and Steam's client dll) written.
        var markerPath = Path.Combine(libraryPath, "libraryfolder.vdf");
        string contentId;
        if (File.Exists(markerPath)
            && SteamLibraryVdf.ValuesOf(File.ReadAllText(markerPath), "contentid")
                is { Count: > 0 } existing
            && existing[0].Length > 0)
        {
            contentId = existing[0];
            Log.Info($"Format: existing library found (contentid {contentId}).");
        }
        else
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            if (configText is not null)
            {
                foreach (var id in SteamLibraryVdf.ValuesOf(configText, "contentid"))
                {
                    taken.Add(id);
                }
            }
            contentId = SteamLibraryVdf.GenerateContentId(taken);
            WriteMarkerAndClientDll(libraryPath, contentId, steamExe, label: "");
        }

        long totalSize = 0;
        try
        {
            var root = Path.GetPathRoot(libraryPath);
            if (root is { Length: > 0 } && root[0] != '\\')
            {
                totalSize = new DriveInfo(root).TotalSize;
            }
        }
        catch (Exception)
        {
            // Network shares have no DriveInfo; Steam fills totalsize itself.
        }

        // The Add-Library flow has no name field; an empty label leaves an existing
        // library's own label untouched and gives a fresh folder Steam's default.
        var registration = RegisterLibrary(configPath, configText, libraryPath, contentId,
            totalSize, label: "");
        return ($"{libraryPath} is set up as a Steam library. {registration}", true);
    }

    /// <summary>Writes the marker VDF (Steam's exact dialect: UTF-8 no BOM,
    /// LF-only) and copies Steam's client dll beside it.</summary>
    private static void WriteMarkerAndClientDll(
        string libraryPath, string contentId, string? steamExe, string label)
    {
        var utf8NoBom = new System.Text.UTF8Encoding(false);
        File.WriteAllText(
            Path.Combine(libraryPath, "libraryfolder.vdf"),
            SteamLibraryVdf.BuildMarker(contentId, steamExe ?? "", label),
            utf8NoBom);
        Log.Info($"Format: library marker written ({libraryPath}, contentid {contentId}).");
        if (steamExe is not null)
        {
            var sourceDll = Path.Combine(Path.GetDirectoryName(steamExe)!, "steam.dll");
            if (File.Exists(sourceDll))
            {
                File.Copy(sourceDll, Path.Combine(libraryPath, "steam.dll"), overwrite: true);
            }
            else
            {
                Log.Warn($"Format: steam.dll not found at {sourceDll} — library still mounts.");
            }
        }
    }

    private void Finish(string message, bool success)
    {
        StatusText = message;
        if (success)
        {
            Log.Info($"Format: done — {message}");
        }
        else
        {
            Log.Warn($"Format: failed — {message}");
        }
        Finished?.Invoke(message, success);
    }

    // Internal: LibraryTabManager's card rename reuses the same atomic replace for
    // its closed-Steam vdf label edits.
    internal static void WriteAtomically(string path, string content)
    {
        var temporary = path + $".wsgm-{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static void BackupOnce(string path)
    {
        var backup = path + ".wsgm-bak";
        if (!File.Exists(backup))
        {
            File.Copy(path, backup, overwrite: false);
        }
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
