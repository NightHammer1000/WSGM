using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Removable-storage state and safe eject for the game-mode taskbar.
/// Explorer's "Safely Remove Hardware" tray icon does not exist in game mode,
/// so this is how a card switcher gets a microSD or USB drive out safely.
///
/// Two eject paths, chosen per physical disk from IOCTL_STORAGE_GET_HOTPLUG_INFO:
/// hot-pluggable devices (USB sticks and drives) get the PnP device eject, which
/// removes all their volumes at once; removable media in a built-in reader
/// (microSD) gets the volume-level lock/dismount/eject — a device eject there
/// disables the reader itself until reboot.
///
/// Enumeration opens volume and disk handles, so nothing heavy runs on the UI
/// thread or on every tick: a 2 s timer computes a cheap drive-letter/readiness
/// signature and only a change (or an explicit refresh) triggers the full
/// re-enumeration, off-thread, publishing back through the dispatcher. Rows are
/// reconciled in place — rebuilding the collection would drop the control under
/// the gamepad cursor.</summary>
public sealed class RemovableDriveManager : INotifyPropertyChanged, IDisposable
{
    /// <summary>Raised after a status property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private DispatcherTimer? _timer;
    private int _refreshing;
    private string _lastSignature = "";

    /// <summary>Serializes eject attempts: one device at a time, so two rows
    /// cannot interleave their lock/eject sequences.</summary>
    private readonly SemaphoreSlim _ejectGate = new(1, 1);

    /// <summary>Disk numbers that must never be listed: the Windows volume's and
    /// WSGM's own. Resolved once on the first snapshot (worker thread only).</summary>
    private HashSet<int>? _systemDisks;

    /// <summary>Gets the ejectable devices: one row per hot-pluggable device
    /// (all its volumes together), one per removable-media volume.</summary>
    public ObservableCollection<RemovableDriveEntry> Drives { get; } = [];

    private bool _hasDrives;
    /// <summary>Gets whether anything ejectable is present — the taskbar shows
    /// its eject tile only while this is true.</summary>
    public bool HasDrives
    {
        get => _hasDrives;
        private set
        {
            if (_hasDrives != value)
            {
                _hasDrives = value;
                Raise(nameof(HasDrives));
            }
        }
    }

    private string _statusText = "";
    /// <summary>Gets the last thing that happened ("X is safe to remove", or why
    /// an eject was refused), for the panel's status line. Empty when there is
    /// nothing to report.</summary>
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

    /// <summary>Performs a first refresh and starts the 2 s change-detection
    /// timer. UI-thread callers only. Idempotent.</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }
        QueueRefresh(force: true);
        // Parameterless ctor + explicit Start: the 3-arg ctor auto-starts.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Stops the timer. Idempotent; bound values keep their last state.</summary>
    public void Dispose()
    {
        if (_timer is null)
        {
            return;
        }
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>Forces a full re-enumeration — bound to the panel's refresh
    /// button and run when the panel opens.</summary>
    public void Refresh() => QueueRefresh(force: true);

    private void OnTick(object? sender, EventArgs e) => QueueRefresh(force: false);

    /// <summary>Refreshes off the UI thread, at most one at a time. Without the
    /// force flag the full enumeration only runs when the cheap drive signature
    /// changed — an idle taskbar must not open device handles every 2 s.</summary>
    private void QueueRefresh(bool force)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var signature = ComputeSignature();
                if (!force && signature == _lastSignature)
                {
                    return;
                }
                var devices = ReadSnapshot();
                Dispatcher.UIThread.Post(() =>
                {
                    _lastSignature = signature;
                    Apply(devices);
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"Eject: refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>A cheap fingerprint of the mounted-drive landscape: letters,
    /// types and media readiness. Catches arrivals, removals AND a card slipped
    /// into an already-present reader slot (same letter, ready flips).</summary>
    private static string ComputeSignature()
    {
        var parts = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                parts.Add($"{drive.Name[0]}{(int)drive.DriveType}{(drive.IsReady ? 1 : 0)}");
            }
            catch (IOException)
            {
                // A drive vanishing mid-walk is itself a change next tick.
            }
        }
        return string.Join(";", parts);
    }

    /// <summary>One ejectable device as the background snapshot reports it.</summary>
    /// <param name="Id">The row identity (device instance path, or "media:X").</param>
    /// <param name="Name">The device display name.</param>
    /// <param name="Letters">The volume letters, formatted.</param>
    /// <param name="SizeBytes">Total capacity across the listed volumes.</param>
    /// <param name="Kind">Which eject path applies.</param>
    /// <param name="DevInst">The disk devnode (PnP eject rows).</param>
    /// <param name="VolumeLetter">The letter to lock (media rows).</param>
    internal sealed record EjectableDevice(
        string Id, string Name, string Letters, long SizeBytes, EjectKind Kind,
        uint DevInst, char VolumeLetter);

    /// <summary>Which eject path a disk's hotplug facts call for, or null when
    /// the disk is internal fixed storage and must not be listed at all.</summary>
    /// <param name="deviceHotplug">The device itself is hot-pluggable.</param>
    /// <param name="mediaRemovable">The media can leave the device.</param>
    internal static EjectKind? Classify(bool deviceHotplug, bool mediaRemovable) =>
        deviceHotplug ? EjectKind.UsbDevice
        : mediaRemovable ? EjectKind.Media
        : null;

    /// <summary>Classifies one disk number on a fresh handle: null for a
    /// system/app disk or anything that does not answer as external
    /// hot-pluggable/removable storage. Query access only — a read handle on
    /// <c>\\.\PhysicalDriveN</c> needs elevation, so this must work unelevated.</summary>
    /// <param name="disk">The physical disk number.</param>
    /// <param name="systemDisks">The guarded disks from <see cref="ResolveSystemDisks"/>.</param>
    internal static EjectKind? ClassifyDisk(int disk, HashSet<int> systemDisks)
    {
        if (systemDisks.Contains(disk))
        {
            return null;
        }
        using var handle = NativeStorage.OpenDiskForQuery(disk);
        return !handle.IsInvalid
            && NativeStorage.TryGetHotplugInfo(handle, out var media, out var hotplug)
            ? Classify(hotplug, media)
            : null;
    }

    /// <summary>Formats a capacity for the row's status line.</summary>
    /// <param name="bytes">The size in bytes; nothing is shown for 0.</param>
    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "";
        }
        // Decimal units, matching how storage is sold and labeled. Invariant:
        // the app publishes with InvariantGlobalization, and the tests must see
        // the same digits regardless of the machine locale.
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return bytes >= 1_000_000_000_000L
            ? (bytes / 1_000_000_000_000.0).ToString("0.#", invariant) + " TB"
            : bytes >= 1_000_000_000L
            ? (bytes / 1_000_000_000.0).ToString("0.#", invariant) + " GB"
            : Math.Max(1, bytes / 1_000_000L).ToString(invariant) + " MB";
    }

    /// <summary>Formats a device's drive letters ("E:" / "E:, F:").</summary>
    /// <param name="letters">The letters, in the order they were found.</param>
    internal static string FormatLetters(IReadOnlyList<char> letters) =>
        string.Join(", ", letters.Select(l => $"{l}:"));

    /// <summary>Reads the current ejectable-device list. Worker thread only:
    /// this opens volume and disk handles.</summary>
    private List<EjectableDevice> ReadSnapshot()
    {
        _systemDisks ??= ResolveSystemDisks();

        // Candidate volumes: mounted local disks. USB HDDs report Fixed, so the
        // type never filters — only network/optical/absent drives are skipped.
        var volumes = new List<(char Letter, int Disk, long Size)>();
        foreach (var volume in NativeStorage.MountedVolumes())
        {
            if (!volume.Ready
                || volume.DeviceType != NativeStorage.FileDeviceDisk
                || volume.Disk < 0)
            {
                continue;
            }
            volumes.Add((volume.Letter, volume.Disk, volume.SizeBytes));
        }
        if (volumes.Count == 0)
        {
            return [];
        }

        // Classify each disk once, skipping internal storage and the guarded
        // system/app disks regardless of what the hotplug flags claim.
        var kinds = new Dictionary<int, EjectKind>();
        foreach (var disk in volumes.Select(v => v.Disk).Distinct())
        {
            if (ClassifyDisk(disk, _systemDisks) is { } kind)
            {
                kinds[disk] = kind;
            }
        }
        if (kinds.Count == 0)
        {
            return [];
        }

        // Devnode and name per interesting disk, via the disk interface list.
        var nodes = new Dictionary<int, (uint DevInst, string Id, string Name)>();
        foreach (var path in NativeStorage.ListDiskInterfaces())
        {
            using var handle = NativeStorage.OpenVolumeForQueryPath(path);
            if (handle.IsInvalid
                || !NativeStorage.TryGetDeviceNumber(handle, out _, out var disk)
                || !kinds.ContainsKey(disk)
                || nodes.ContainsKey(disk)
                || !NativeStorage.TryGetDevNode(path, out var devInst))
            {
                continue;
            }
            nodes[disk] = (devInst,
                NativeStorage.GetDeviceInstanceId(devInst),
                NativeStorage.GetDeviceDisplayName(devInst));
        }

        var result = new List<EjectableDevice>();
        foreach (var group in volumes.GroupBy(v => v.Disk).OrderBy(g => g.Key))
        {
            if (!kinds.TryGetValue(group.Key, out var kind))
            {
                continue;
            }
            var hasNode = nodes.TryGetValue(group.Key, out var node);
            var name = hasNode ? node.Name : "";
            var devInst = hasNode ? node.DevInst : 0u;
            var letters = group.Select(v => v.Letter).OrderBy(l => l).ToArray();
            var size = group.Sum(v => v.Size);
            if (kind == EjectKind.UsbDevice)
            {
                // One row per DEVICE: the PnP eject takes every partition at
                // once, and per-partition rows would invite a doomed second try.
                var id = hasNode && node.Id.Length > 0 ? node.Id : $"disk:{group.Key}";
                result.Add(new EjectableDevice(
                    id, name, FormatLetters(letters), size, kind, devInst, letters[0]));
            }
            else
            {
                // Media rows stay per-volume: a multi-slot reader ejects each
                // card on its own.
                foreach (var volume in group)
                {
                    result.Add(new EjectableDevice(
                        $"media:{volume.Letter}", name, FormatLetters([volume.Letter]),
                        volume.Size, kind, devInst, volume.Letter));
                }
            }
        }
        return result;
    }

    /// <summary>The disks the eject list must never contain: whatever Windows
    /// itself and WSGM run from. Belt and braces — an internal disk already
    /// fails the hotplug classification, but a USB-attached boot drive would
    /// not. Shared with the Format flow's target list, which must never offer
    /// these either.</summary>
    internal static HashSet<int> ResolveSystemDisks()
    {
        var disks = new HashSet<int>();
        foreach (var root in new[]
        {
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            Path.GetPathRoot(AppContext.BaseDirectory),
        })
        {
            if (root is not { Length: > 0 } || !char.IsAsciiLetter(root[0]))
            {
                continue;
            }
            using var volume = NativeStorage.OpenVolumeForQuery(char.ToUpperInvariant(root[0]));
            if (!volume.IsInvalid
                && NativeStorage.TryGetDeviceNumber(volume, out _, out var disk))
            {
                disks.Add(disk);
            }
        }
        return disks;
    }

    /// <summary>Merges a fresh device list into the bound collection without
    /// replacing surviving rows (gamepad-cursor discipline). Internal for the
    /// reconcile tests; production callers reach it through the refresh path.</summary>
    /// <param name="fresh">The snapshot to merge.</param>
    internal void Apply(List<EjectableDevice> fresh)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        foreach (var device in fresh)
        {
            seen.Add(device.Id);
            var row = FindDrive(device.Id);
            if (row is null)
            {
                row = new RemovableDriveEntry(device.Id, device.Kind);
                Drives.Add(row);
                added++;
                Log.Info($"Eject: found {device.Name} ({device.Letters}) "
                    + $"{(device.Kind == EjectKind.UsbDevice ? "usb-device" : "media")} "
                    + $"id={device.Id}");
            }
            row.Name = device.Name;
            row.Letters = device.Letters;
            row.SizeText = FormatSize(device.SizeBytes);
            row.DevInst = device.DevInst;
            row.VolumeLetter = device.VolumeLetter;
            if (row.Ejected)
            {
                // Listed again after a successful eject = reinserted and mounted;
                // the row is back in ordinary service.
                row.Ejected = false;
                row.ResultText = "";
            }
        }
        var removed = 0;
        for (var i = Drives.Count - 1; i >= 0; i--)
        {
            var row = Drives[i];
            // A row mid-eject is never removed: its outcome message is about to
            // land on it.
            if (!row.Busy && !seen.Contains(row.Id))
            {
                Drives.RemoveAt(i);
                removed++;
            }
        }
        if (added > 0 || removed > 0)
        {
            Log.Info($"Eject: device list now {Drives.Count} row(s) "
                + $"(+{added}/-{removed}).");
        }
        HasDrives = Drives.Count > 0;
    }

    private RemovableDriveEntry? FindDrive(string id)
    {
        foreach (var entry in Drives)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    // ---- eject ----

    /// <summary>How often a refused eject is retried before giving up: transient
    /// handles (indexer, Defender) clear within a beat, real vetoes do not.</summary>
    private const int EjectAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Safely ejects one row's device or media, updating the row and
    /// <see cref="StatusText"/> with the outcome.</summary>
    /// <param name="entry">The row to eject.</param>
    public async Task EjectAsync(RemovableDriveEntry entry)
    {
        if (!entry.ActionEnabled)
        {
            return;
        }
        // Claim the row BEFORE queueing behind another row's eject: ActionEnabled is
        // the only thing that stops a second press, and while the gate is held for a
        // different device the row would still look idle — the duplicate run then
        // lands on an already-removed device and overwrites the success message.
        entry.Busy = true;
        await _ejectGate.WaitAsync();
        try
        {
            entry.ResultText = "";
            StatusText = $"Ejecting {entry.Name}...";
            // The ACF watcher holds directory handles on card volumes; a locked
            // volume with any other open handle vetoes the eject, so WSGM would
            // veto itself. Stand the watcher down first (it resumes on its own).
            CardAcfWatcher.SuspendAll();
            var devInst = entry.DevInst;
            var letter = entry.VolumeLetter;
            var name = entry.Name;
            var result = await Task.Run(() => entry.Kind == EjectKind.UsbDevice
                ? EjectDevice(devInst, name)
                : EjectMediaVolume(letter, name));
            if (result.Success)
            {
                entry.Ejected = true;
                entry.ResultText = "Safe to remove";
                StatusText = $"{name} is safe to remove.";
            }
            else
            {
                entry.ResultText = result.Message;
                StatusText = result.Message;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Eject: {entry.Name} failed unexpectedly: {ex.Message}");
            entry.ResultText = "Eject failed";
            StatusText = $"Could not eject {entry.Name}: {ex.Message}";
        }
        finally
        {
            entry.Busy = false;
            _ejectGate.Release();
        }
        Refresh();
    }

    private readonly record struct EjectResult(bool Success, string Message);

    /// <summary>The PnP device eject, with retries for transient holders.
    /// Worker thread only.</summary>
    private static EjectResult EjectDevice(uint diskDevInst, string name)
    {
        if (diskDevInst == 0)
        {
            Log.Warn($"Eject: no devnode for {name}.");
            return new EjectResult(false, "Windows could not identify this device.");
        }
        var target = NativeStorage.FindEjectTarget(diskDevInst);
        Log.Info($"Eject: requesting device eject for {name} "
            + $"(devnode {NativeStorage.GetDeviceInstanceId(target)}).");
        var vetoType = NativeStorage.PnpVetoType.TypeUnknown;
        var vetoName = "";
        for (var attempt = 1; attempt <= EjectAttempts; attempt++)
        {
            var code = NativeStorage.RequestDeviceEject(target, out vetoType, out vetoName);
            if (code == NativeStorage.CrSuccess)
            {
                Log.Info($"Eject: {name} ejected (attempt {attempt}).");
                return new EjectResult(true, "");
            }
            if (code != NativeStorage.CrRemoveVetoed)
            {
                Log.Warn($"Eject: {name} failed with CONFIGRET {code}.");
                return new EjectResult(false,
                    $"Windows could not remove this drive (error {code}).");
            }
            Log.Info($"Eject: {name} vetoed (attempt {attempt}, "
                + $"type {(int)vetoType} {vetoType}, by '{vetoName}').");
            if (attempt < EjectAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }
        return new EjectResult(false, DescribeVeto(vetoType, vetoName));
    }

    /// <summary>The media-level eject for a built-in reader's card. Worker
    /// thread only.</summary>
    private static EjectResult EjectMediaVolume(char letter, string name)
    {
        Log.Info($"Eject: dismounting media volume {letter}: ({name}).");
        using var volume = NativeStorage.OpenVolumeForEject(letter);
        if (volume.IsInvalid)
        {
            Log.Warn($"Eject: could not open {letter}: "
                + $"(Win32 {NativeStorage.LastWin32Error()}).");
            return new EjectResult(false, $"Could not open drive {letter}:.");
        }
        // The lock is the open-files check: it fails while anything else holds a
        // handle on the volume.
        var locked = false;
        for (var attempt = 1; attempt <= EjectAttempts; attempt++)
        {
            if (NativeStorage.LockVolume(volume))
            {
                locked = true;
                break;
            }
            Log.Info($"Eject: volume {letter}: still in use "
                + $"(attempt {attempt}, Win32 {NativeStorage.LastWin32Error()}).");
            if (attempt < EjectAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }
        if (!locked)
        {
            return new EjectResult(false,
                "Still in use — a running game or an active Steam download may have "
                + "files open on this card. Close it and try again.");
        }
        if (!NativeStorage.DismountVolume(volume))
        {
            Log.Warn($"Eject: dismount of {letter}: failed "
                + $"(Win32 {NativeStorage.LastWin32Error()}).");
            return new EjectResult(false, $"Could not dismount drive {letter}:.");
        }
        // Many readers have no motorized eject and fail this call; the lock and
        // dismount above are what makes the card safe to pull.
        if (!NativeStorage.EjectMedia(volume))
        {
            Log.Info($"Eject: media-eject call for {letter}: not supported "
                + $"(Win32 {NativeStorage.LastWin32Error()}); dismount succeeded.");
        }
        Log.Info($"Eject: {letter}: dismounted and safe to remove.");
        return new EjectResult(true, "");
    }

    /// <summary>Turns a PnP veto into something the user can act on. The open
    /// handles almost always belong to a running game or an active Steam
    /// download/install — Steam itself does not hold library volumes at idle.</summary>
    /// <param name="vetoType">The veto reason Windows reported.</param>
    /// <param name="vetoName">The vetoing module/service/path, possibly empty.</param>
    internal static string DescribeVeto(NativeStorage.PnpVetoType vetoType, string vetoName)
        => vetoType switch
        {
            NativeStorage.PnpVetoType.WindowsApp when vetoName.Length > 0 =>
                $"Still in use by {vetoName}. Close it and try again.",
            NativeStorage.PnpVetoType.WindowsApp
                or NativeStorage.PnpVetoType.OutstandingOpen
                or NativeStorage.PnpVetoType.PendingClose =>
                "Still in use — a running game or an active Steam download may have "
                + "files open on this drive. Close it and try again.",
            NativeStorage.PnpVetoType.WindowsService when vetoName.Length > 0 =>
                $"The Windows service '{vetoName}' is blocking removal.",
            NativeStorage.PnpVetoType.WindowsService =>
                "A Windows service is blocking removal.",
            NativeStorage.PnpVetoType.InsufficientRights =>
                "Windows denied the removal (insufficient rights).",
            _ => "Windows refused to remove this drive right now. Try again in a moment.",
        };

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
