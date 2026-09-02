using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Parses the undocumented-but-stable Shell_NotifyIcon wire format that
/// shell32 delivers to the Shell_TrayWnd window via WM_COPYDATA (dwData 1):
/// a TRAYNOTIFYDATA header (signature 0x34753423 + NIM_* message) followed by a
/// NOTIFYICONDATAW whose handle fields are 32-bit ON EVERY ARCHITECTURE — the
/// wire struct predates x64 and was never widened (verified against ReactOS
/// undocshell.h, LiteStep, ManagedShell, and zebar; widening the handles here
/// with sign/zero confusion silently corrupts every HWND and HICON).
///
/// Everything in this file is pure byte/state logic so the executable
/// specification lives in unit tests fed with captured blobs; the live window
/// and handle work stays in Shell\TrayHost.</summary>
public static class TrayProtocol
{
    /// <summary>TRAYNOTIFYDATA signature (ReactOS: NI_NOTIFY_SIG).</summary>
    public const uint Signature = 0x34753423;

    /// <summary>COPYDATASTRUCT.dwData for tray notifications.</summary>
    public const int CopyDataTray = 1;

    /// <summary>COPYDATASTRUCT.dwData for SHAppBarMessage traffic (stubbed).</summary>
    public const int CopyDataAppBar = 0;

    /// <summary>COPYDATASTRUCT.dwData for SHLoadInProc requests, which WSGM rejects because it
    /// owns the corresponding system surfaces instead of hosting Explorer extensions.</summary>
    public const int CopyDataLoadInProc = 2;

    /// <summary>COPYDATASTRUCT.dwData for Shell_NotifyIconGetRect queries.</summary>
    public const int CopyDataIconRect = 3;

    // NIM_* messages.
    /// <summary>NIM_ADD.</summary>
    public const uint NimAdd = 0;
    /// <summary>NIM_MODIFY.</summary>
    public const uint NimModify = 1;
    /// <summary>NIM_DELETE.</summary>
    public const uint NimDelete = 2;
    /// <summary>NIM_SETFOCUS.</summary>
    public const uint NimSetFocus = 3;
    /// <summary>NIM_SETVERSION.</summary>
    public const uint NimSetVersion = 4;

    // NIF_* validity flags.
    /// <summary>NIF_MESSAGE: uCallbackMessage is valid.</summary>
    public const uint NifMessage = 0x01;
    /// <summary>NIF_ICON: hIcon is valid.</summary>
    public const uint NifIcon = 0x02;
    /// <summary>NIF_TIP: szTip is valid.</summary>
    public const uint NifTip = 0x04;
    /// <summary>NIF_STATE: dwState/dwStateMask are valid.</summary>
    public const uint NifState = 0x08;
    /// <summary>NIF_GUID: guidItem identifies the icon.</summary>
    public const uint NifGuid = 0x20;

    /// <summary>NIS_HIDDEN state bit.</summary>
    public const uint NisHidden = 0x01;
    /// <summary>NIS_SHAREDICON state bit (hIcon belongs to another registration).</summary>
    public const uint NisSharedIcon = 0x02;

    /// <summary>WM_USER — the first message value Windows reserves for an
    /// application's own use. NOTIFYICONDATA.uCallbackMessage is documented as
    /// application-defined, and every real tray implementation allocates it from
    /// this range or above: WinForms NotifyIcon uses WM_USER + 1024, Qt's
    /// QSystemTrayIcon uses WM_APP + 101 (WM_APP is 0x8000), and apps wanting a
    /// process-unique value call RegisterWindowMessage, which returns 0xC000..0xFFFF.</summary>
    public const uint WmUser = 0x0400;

    /// <summary>The largest window message value (message numbers are 16-bit).</summary>
    public const uint MaxWindowMessage = 0xFFFF;

    /// <summary>Whether a registered callback is in Windows' application-defined message range.
    /// This governs activation only: registration remains successful for compatibility with
    /// shell32 retry behavior.</summary>
    /// <param name="callbackMessage">The registered uCallbackMessage value.</param>
    public static bool IsRelayableCallback(uint callbackMessage)
        => callbackMessage is >= WmUser and <= MaxWindowMessage;

    // Wire offsets. Header: signature 0, message 4, NOTIFYICONDATA32 at 8.
    private const int NidOffset = 8;
    private const int MinimumNidSize = 952; // v3 shape, ends after guidItem

    /// <summary>One parsed Shell_NotifyIcon request. Handle fields are already
    /// zero-extended from their 32-bit wire form.</summary>
    /// <param name="Message">The NIM_* request.</param>
    /// <param name="Hwnd">The registering app's callback window.</param>
    /// <param name="Uid">The app-chosen icon id (identity with <paramref name="Hwnd"/>).</param>
    /// <param name="Flags">NIF_* validity flags.</param>
    /// <param name="CallbackMessage">The app's callback message (valid with NIF_MESSAGE).</param>
    /// <param name="IconHandle">The 32-bit HICON value (valid with NIF_ICON).</param>
    /// <param name="Tip">The tooltip text (valid with NIF_TIP).</param>
    /// <param name="State">NIS_* bits (valid with NIF_STATE, masked by StateMask).</param>
    /// <param name="StateMask">Which NIS_* bits the request changes.</param>
    /// <param name="Version">The requested protocol version (meaningful for NIM_SETVERSION).</param>
    /// <param name="Guid">The icon GUID (identity when NIF_GUID is set).</param>
    public sealed record TrayNotification(
        uint Message,
        nint Hwnd,
        uint Uid,
        uint Flags,
        uint CallbackMessage,
        nint IconHandle,
        string Tip,
        uint State,
        uint StateMask,
        uint Version,
        Guid Guid);

    /// <summary>Parses a WM_COPYDATA dwData=1 payload. Returns false (and logs
    /// nothing — the caller decides) for payloads whose signature or size don't
    /// match the known TRAYNOTIFYDATA shapes.</summary>
    /// <param name="payload">The raw COPYDATASTRUCT.lpData bytes.</param>
    /// <param name="notification">The parsed request on success.</param>
    public static bool TryParse(ReadOnlySpan<byte> payload, out TrayNotification? notification)
    {
        notification = null;
        if (payload.Length < NidOffset + MinimumNidSize)
        {
            return false;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload) != Signature)
        {
            return false;
        }
        var message = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var nid = payload[NidOffset..];

        var cbSize = BinaryPrimitives.ReadUInt32LittleEndian(nid);
        // The wire NID is always the 32-bit layout; cbSize gates which trailing
        // fields exist (v3 = 952 ends at guidItem, v4 = 956 adds hBalloonIcon).
        // Accepted range is 952..968: at least the v3 shape, which is the whole
        // region this parser reads, plus slack for a padded or extended trailing
        // field a later Windows may append. Anything outside that range is either
        // hostile or a layout this parser doesn't know — reject so the caller
        // returns "not handled".
        if (cbSize < MinimumNidSize || cbSize > 968 || nid.Length < (int)cbSize)
        {
            return false;
        }

        var hwnd = (nint)BinaryPrimitives.ReadUInt32LittleEndian(nid[4..]);
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(nid[8..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(nid[12..]);
        var callback = BinaryPrimitives.ReadUInt32LittleEndian(nid[16..]);
        var icon = (nint)BinaryPrimitives.ReadUInt32LittleEndian(nid[20..]);
        var tip = ReadFixedString(nid[24..280]);
        var state = BinaryPrimitives.ReadUInt32LittleEndian(nid[280..]);
        var stateMask = BinaryPrimitives.ReadUInt32LittleEndian(nid[284..]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(nid[800..]);
        var guid = new Guid(nid.Slice(936, 16));

        notification = new TrayNotification(
            message, hwnd, uid, flags, callback, icon, tip, state, stateMask, version, guid);
        return true;
    }

    private static string ReadFixedString(ReadOnlySpan<byte> utf16Bytes)
    {
        var chars = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(utf16Bytes);
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }
}

/// <summary>How a tray request changed the icon table.</summary>
public enum TrayChange
{
    /// <summary>The request was rejected (unknown icon, duplicate add, no hwnd) —
    /// return 0 from WM_COPYDATA so shell32 reports failure and well-behaved apps
    /// re-add.</summary>
    Rejected,

    /// <summary>A new icon appeared.</summary>
    Added,

    /// <summary>An existing icon changed.</summary>
    Updated,

    /// <summary>An icon was removed.</summary>
    Removed,
}

/// <summary>The live set of registered tray icons: identity resolution
/// ((hwnd,uid) pair, or GUID when NIF_GUID is set), per-flag field updates, and
/// version negotiation — Explorer's semantics, pure and unit-tested.</summary>
public sealed class TrayIconTable
{
    /// <summary>One registered tray icon.</summary>
    public sealed class TrayIcon
    {
        internal TrayIcon(nint hwnd, uint uid, Guid guid)
        {
            Hwnd = hwnd;
            Uid = uid;
            Guid = guid;
        }

        /// <summary>Gets the owning app's callback window.</summary>
        public nint Hwnd { get; }

        /// <summary>Gets the app-chosen icon id.</summary>
        public uint Uid { get; }

        /// <summary>Gets the GUID identity (Guid.Empty when the app didn't use NIF_GUID).</summary>
        public Guid Guid { get; internal set; }

        /// <summary>Gets the message to send the app for icon interactions.</summary>
        public uint CallbackMessage { get; internal set; }

        /// <summary>Gets the negotiated protocol version (0/3 legacy, 4 = coords protocol).</summary>
        public uint Version { get; internal set; }

        /// <summary>Gets the tooltip text.</summary>
        public string Tip { get; internal set; } = "";

        /// <summary>Gets whether the app asked the icon to be hidden (NIS_HIDDEN).</summary>
        public bool IsHidden { get; internal set; }

        /// <summary>Gets or sets the host-rasterized icon image (opaque to this
        /// pure table; the tray host stores an Avalonia bitmap here). Ownership is
        /// per icon: the host disposes this image when the icon is removed and
        /// again at teardown, so one image instance must never be shared between
        /// two icons — a NIS_SHAREDICON registration has to rasterize its own from
        /// the shared handle.</summary>
        public object? IconImage { get; set; }
    }

    private readonly List<TrayIcon> _icons = [];

    /// <summary>Gets every registered icon, including hidden ones, registration order.</summary>
    public IReadOnlyList<TrayIcon> Icons => _icons;

    /// <summary>Finds the icon a request refers to, or null.</summary>
    /// <param name="n">The parsed request.</param>
    public TrayIcon? Find(TrayProtocol.TrayNotification n)
    {
        var byGuid = (n.Flags & TrayProtocol.NifGuid) != 0 && n.Guid != Guid.Empty;
        foreach (var icon in _icons)
        {
            if (byGuid && icon.Guid != Guid.Empty && icon.Guid == n.Guid)
            {
                return icon;
            }
            if (icon.Hwnd == n.Hwnd && icon.Uid == n.Uid)
            {
                return icon;
            }
        }
        return null;
    }

    /// <summary>Applies a parsed request with Explorer's semantics. The affected
    /// icon (for Added/Updated/Removed) is returned via <paramref name="affected"/>.</summary>
    /// <param name="n">The parsed request.</param>
    /// <param name="affected">The icon the request created, changed, or removed.</param>
    public TrayChange Apply(TrayProtocol.TrayNotification n, out TrayIcon? affected)
    {
        affected = null;
        var existing = Find(n);
        switch (n.Message)
        {
            case TrayProtocol.NimAdd:
                if (existing is not null)
                {
                    // Explorer fails a duplicate NIM_ADD; the app should modify.
                    return TrayChange.Rejected;
                }
                // A callback hwnd is required to ever deliver clicks (ManagedShell
                // rejects hwnd-less adds even with a GUID).
                if (n.Hwnd == 0)
                {
                    return TrayChange.Rejected;
                }
                var added = new TrayIcon(n.Hwnd, n.Uid, (n.Flags & TrayProtocol.NifGuid) != 0 ? n.Guid : Guid.Empty);
                ApplyFields(added, n);
                _icons.Add(added);
                affected = added;
                return TrayChange.Added;

            case TrayProtocol.NimModify:
                if (existing is null)
                {
                    // Rejecting makes shell32 report failure, prompting a re-add.
                    return TrayChange.Rejected;
                }
                ApplyFields(existing, n);
                affected = existing;
                return TrayChange.Updated;

            case TrayProtocol.NimDelete:
                if (existing is null)
                {
                    return TrayChange.Rejected;
                }
                _icons.Remove(existing);
                affected = existing;
                return TrayChange.Removed;

            case TrayProtocol.NimSetVersion:
                if (existing is null)
                {
                    return TrayChange.Rejected;
                }
                // Explorer accepts 0..4; anything else fails.
                if (n.Version > 4)
                {
                    return TrayChange.Rejected;
                }
                existing.Version = n.Version;
                affected = existing;
                return TrayChange.Updated;

            case TrayProtocol.NimSetFocus:
                // Focus return is a no-op for a bar that isn't a focus scope yet.
                return existing is null ? TrayChange.Rejected : TrayChange.Updated;

            default:
                return TrayChange.Rejected;
        }
    }

    /// <summary>Drops every icon (host teardown).</summary>
    public void Clear() => _icons.Clear();

    private static void ApplyFields(TrayIcon icon, TrayProtocol.TrayNotification n)
    {
        if ((n.Flags & TrayProtocol.NifMessage) != 0)
        {
            icon.CallbackMessage = n.CallbackMessage;
        }
        if ((n.Flags & TrayProtocol.NifTip) != 0)
        {
            icon.Tip = n.Tip;
        }
        if ((n.Flags & TrayProtocol.NifGuid) != 0 && n.Guid != Guid.Empty)
        {
            icon.Guid = n.Guid;
        }
        if ((n.Flags & TrayProtocol.NifState) != 0 && (n.StateMask & TrayProtocol.NisHidden) != 0)
        {
            icon.IsHidden = (n.State & TrayProtocol.NisHidden) != 0;
        }
        // NIF_ICON handle bookkeeping is deliberately NOT the table's job: the host
        // must rasterize synchronously while the foreign handle is alive, and it
        // rasterizes per icon even for NIS_SHAREDICON, so the table has no reason to
        // retain the wire handle.
    }
}
