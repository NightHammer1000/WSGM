using System.Buffers.Binary;
using System.Text;
using WSGM.Core;

namespace WSGM.Tests;

/// <summary>The tray wire-format parser and icon-table semantics, exercised with
/// synthetic TRAYNOTIFYDATA blobs shaped exactly like shell32's WM_COPYDATA
/// payloads (32-bit handle fields, 956-byte v4 / 952-byte v3 NID).</summary>
public sealed class TrayProtocolTests
{
    private static byte[] Blob(
        uint message,
        uint cbSize = 956,
        uint hwnd = 0x1234,
        uint uid = 7,
        uint flags = 0,
        uint callback = 0,
        uint icon = 0,
        string tip = "",
        uint state = 0,
        uint stateMask = 0,
        uint version = 0,
        Guid guid = default,
        uint signature = TrayProtocol.Signature)
    {
        var payload = new byte[8 + 956];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, signature);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), message);
        var nid = payload.AsSpan(8);
        BinaryPrimitives.WriteUInt32LittleEndian(nid, cbSize);
        BinaryPrimitives.WriteUInt32LittleEndian(nid[4..], hwnd);
        BinaryPrimitives.WriteUInt32LittleEndian(nid[8..], uid);
        BinaryPrimitives.WriteUInt32LittleEndian(nid[12..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(nid[16..], callback);
        BinaryPrimitives.WriteUInt32LittleEndian(nid[20..], icon);
        Encoding.Unicode.GetBytes(tip).CopyTo(nid[24..280]);
        BinaryPrimitives.WriteUInt32LittleEndian(nid[280..], state);
        BinaryPrimitives.WriteUInt32LittleEndian(nid[284..], stateMask);
        BinaryPrimitives.WriteUInt32LittleEndian(nid[800..], version);
        guid.TryWriteBytes(nid.Slice(936, 16));
        return payload;
    }

    private static TrayProtocol.TrayNotification Parse(byte[] payload)
    {
        Assert.True(TrayProtocol.TryParse(payload, out var parsed));
        Assert.NotNull(parsed);
        return parsed!;
    }

    [Fact]
    public void ParserReadsEveryFieldAndZeroExtendsTheThirtyTwoBitHandles()
    {
        var guid = Guid.NewGuid();
        var parsed = Parse(Blob(
            TrayProtocol.NimAdd,
            hwnd: 0xFEDCBA98, // high bit set: sign-extension would corrupt this
            uid: 42,
            flags: TrayProtocol.NifMessage | TrayProtocol.NifIcon | TrayProtocol.NifTip | TrayProtocol.NifGuid,
            callback: 0x8001,
            icon: 0x80000001,
            tip: "Companion",
            version: 4,
            guid: guid));

        Assert.Equal(TrayProtocol.NimAdd, parsed.Message);
        Assert.Equal(unchecked((nint)0xFEDCBA98), parsed.Hwnd);
        Assert.True(parsed.Hwnd > 0); // zero-extended, never sign-extended
        Assert.Equal(42u, parsed.Uid);
        Assert.Equal(0x8001u, parsed.CallbackMessage);
        Assert.Equal(unchecked((nint)0x80000001), parsed.IconHandle);
        Assert.Equal("Companion", parsed.Tip);
        Assert.Equal(4u, parsed.Version);
        Assert.Equal(guid, parsed.Guid);
    }

    [Fact]
    public void ParserAcceptsTheOlderNineFiftyTwoByteLayout()
        => Assert.True(TrayProtocol.TryParse(Blob(TrayProtocol.NimAdd, cbSize: 952), out _));

    [Theory]
    [InlineData(0xDEADBEEFu, 956u)] // wrong signature
    [InlineData(TrayProtocol.Signature, 100u)] // impossible cbSize
    [InlineData(TrayProtocol.Signature, 5000u)] // larger than any known layout
    public void ParserRejectsUnknownShapes(uint signature, uint cbSize)
        => Assert.False(TrayProtocol.TryParse(Blob(TrayProtocol.NimAdd, cbSize: cbSize, signature: signature), out _));

    [Fact]
    public void ParserRejectsTruncatedPayloads()
        => Assert.False(TrayProtocol.TryParse(Blob(TrayProtocol.NimAdd).AsSpan(0, 200).ToArray(), out _));

    private static TrayIconTable.TrayIcon Added(TrayIconTable table, byte[] blob)
    {
        Assert.Equal(TrayChange.Added, table.Apply(Parse(blob), out var icon));
        Assert.NotNull(icon);
        return icon!;
    }

    [Fact]
    public void AddThenModifyUpdatesOnlyTheFlaggedFields()
    {
        var table = new TrayIconTable();
        var icon = Added(table, Blob(
            TrayProtocol.NimAdd,
            flags: TrayProtocol.NifMessage | TrayProtocol.NifTip,
            callback: 0x8001,
            tip: "Original"));

        var change = table.Apply(Parse(Blob(
            TrayProtocol.NimModify,
            flags: TrayProtocol.NifTip,
            callback: 0, // NIF_MESSAGE absent — must not clobber the callback
            tip: "Renamed")), out var modified);

        Assert.Equal(TrayChange.Updated, change);
        Assert.Same(icon, modified);
        Assert.Equal("Renamed", icon.Tip);
        Assert.Equal(0x8001u, icon.CallbackMessage);
    }

    [Fact]
    public void DuplicateAddAndUnknownModifyOrDeleteAreRejectedLikeExplorer()
    {
        var table = new TrayIconTable();
        Added(table, Blob(TrayProtocol.NimAdd));

        Assert.Equal(TrayChange.Rejected, table.Apply(Parse(Blob(TrayProtocol.NimAdd)), out _));
        Assert.Equal(TrayChange.Rejected, table.Apply(Parse(Blob(TrayProtocol.NimModify, uid: 99)), out _));
        Assert.Equal(TrayChange.Rejected, table.Apply(Parse(Blob(TrayProtocol.NimDelete, uid: 99)), out _));
        Assert.Single(table.Icons);
    }

    [Fact]
    public void AddWithoutACallbackWindowIsRejected()
    {
        var table = new TrayIconTable();
        Assert.Equal(TrayChange.Rejected, table.Apply(Parse(Blob(TrayProtocol.NimAdd, hwnd: 0)), out _));
    }

    [Fact]
    public void GuidIdentityOutranksTheWindowUidPairAcrossAppRestarts()
    {
        var guid = Guid.NewGuid();
        var table = new TrayIconTable();
        var icon = Added(table, Blob(TrayProtocol.NimAdd, hwnd: 0x1111, uid: 1, flags: TrayProtocol.NifGuid, guid: guid));

        // Restarted app: new hwnd/uid, same GUID — must resolve to the same icon.
        var change = table.Apply(Parse(Blob(
            TrayProtocol.NimModify, hwnd: 0x2222, uid: 9,
            flags: TrayProtocol.NifGuid | TrayProtocol.NifTip, tip: "Back", guid: guid)), out var modified);

        Assert.Equal(TrayChange.Updated, change);
        Assert.Same(icon, modified);
    }

    [Fact]
    public void SetVersionNegotiatesUpToFourAndRejectsBeyond()
    {
        var table = new TrayIconTable();
        var icon = Added(table, Blob(TrayProtocol.NimAdd));

        Assert.Equal(TrayChange.Updated, table.Apply(Parse(Blob(TrayProtocol.NimSetVersion, version: 4)), out _));
        Assert.Equal(4u, icon.Version);
        Assert.Equal(TrayChange.Rejected, table.Apply(Parse(Blob(TrayProtocol.NimSetVersion, version: 5)), out _));
        Assert.Equal(4u, icon.Version);
    }

    [Fact]
    public void HiddenStateFollowsTheStateMaskAndDeleteRemovesTheIcon()
    {
        var table = new TrayIconTable();
        var icon = Added(table, Blob(TrayProtocol.NimAdd));

        table.Apply(Parse(Blob(
            TrayProtocol.NimModify, flags: TrayProtocol.NifState,
            state: TrayProtocol.NisHidden, stateMask: TrayProtocol.NisHidden)), out _);
        Assert.True(icon.IsHidden);

        // Mask excludes NIS_HIDDEN — the state word alone must not unhide it.
        table.Apply(Parse(Blob(
            TrayProtocol.NimModify, flags: TrayProtocol.NifState,
            state: 0, stateMask: TrayProtocol.NisSharedIcon)), out _);
        Assert.True(icon.IsHidden);

        Assert.Equal(TrayChange.Removed, table.Apply(Parse(Blob(TrayProtocol.NimDelete)), out _));
        Assert.Empty(table.Icons);
    }

    [Fact]
    public void TrayTilesReconcileInPlaceAndFilterHiddenIcons()
    {
        var table = new TrayIconTable();
        var visible = Added(table, Blob(TrayProtocol.NimAdd, uid: 1));
        Added(table, Blob(TrayProtocol.NimAdd, uid: 2));
        table.Apply(Parse(Blob(
            TrayProtocol.NimModify, uid: 2, flags: TrayProtocol.NifState,
            state: TrayProtocol.NisHidden, stateMask: TrayProtocol.NisHidden)), out _);

        var vm = new WSGM.Overlay.AppSwitcherViewModel();
        vm.ReconcileTray(table.Icons);
        var tile = Assert.Single(vm.TrayIcons);
        Assert.Same(visible, tile.Icon);
        Assert.True(vm.HasTrayIcons);

        vm.ReconcileTray(table.Icons);
        Assert.Same(tile, Assert.Single(vm.TrayIcons)); // same instance across refreshes

        table.Apply(Parse(Blob(TrayProtocol.NimDelete, uid: 1)), out _);
        vm.ReconcileTray(table.Icons);
        Assert.Empty(vm.TrayIcons);
        Assert.False(vm.HasTrayIcons);
    }

    // The relay guard: TrayHost.SendClick refuses to forward a callback message
    // outside the application-defined range, because both the message and the
    // target hwnd come off an attacker-reachable wire and the relay leaves WSGM's
    // High IL where UIPI restricts nothing.
    [Theory]
    [InlineData(0u)] // NIF_MESSAGE never arrived
    [InlineData(0x000Cu)] // WM_SETTEXT
    [InlineData(0x0010u)] // WM_CLOSE
    [InlineData(0x0012u)] // WM_QUIT
    [InlineData(0x004Au)] // WM_COPYDATA
    [InlineData(0x0112u)] // WM_SYSCOMMAND
    [InlineData(0x03FFu)] // one below WM_USER
    [InlineData(0x10000u)] // wider than a 16-bit message number
    public void IsRelayableCallback_MessageOutsideTheApplicationRange_ReturnsFalse(uint callback)
        => Assert.False(TrayProtocol.IsRelayableCallback(callback));

    [Theory]
    [InlineData(0x0400u)] // WM_USER itself
    [InlineData(0x0800u)] // WinForms NotifyIcon: WM_USER + 1024
    [InlineData(0x8065u)] // Qt QSystemTrayIcon: WM_APP + 101
    [InlineData(0xBFFFu)] // top of the WM_APP range
    [InlineData(0xC123u)] // RegisterWindowMessage range
    [InlineData(0xFFFFu)] // highest window message
    public void IsRelayableCallback_ApplicationDefinedMessage_ReturnsTrue(uint callback)
        => Assert.True(TrayProtocol.IsRelayableCallback(callback));

    [Fact]
    public void Apply_AddWithNonRelayableCallback_StillRegistersTheIcon()
    {
        // The bound lives at the relay ONLY. Rejecting the registration would make
        // shell32 report failure and put well-behaved apps into an add/reject loop,
        // so the icon must still register (and keep its tooltip and callback value)
        // even though its click will never be forwarded.
        var table = new TrayIconTable();
        var icon = Added(table, Blob(
            TrayProtocol.NimAdd,
            flags: TrayProtocol.NifMessage | TrayProtocol.NifTip,
            callback: 0x0010, // WM_CLOSE
            tip: "Hostile"));

        Assert.Equal(0x0010u, icon.CallbackMessage);
        Assert.Equal("Hostile", icon.Tip);
        Assert.Single(table.Icons);
        Assert.False(TrayProtocol.IsRelayableCallback(icon.CallbackMessage));
    }
}
