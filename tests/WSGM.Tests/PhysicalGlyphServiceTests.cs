using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Serialization;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class PhysicalGlyphServiceTests
{
    [Fact]
    public void Automatic_WithNoActiveDevice_ReportsTheMismatchRatherThanAProfile()
    {
        // The shape of the bug this replaced: the catalog held the package's profile and every
        // glyph surface still fell back, because the one production call site passed no device at
        // all. These tests passed throughout — they supplied a device id the coordinator never did.
        ImportedGlyphProfile profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        PhysicalGlyphSelectionResult result = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        Assert.Null(result.Profile);
        Assert.Equal(PhysicalGlyphFallbackReason.ExactDeviceMismatch, result.FallbackReason);
    }

    [Fact]
    public void SettingTheActiveDevice_AnnouncesTheChangeSoSurfacesReselect()
    {
        // The device definition and the profiles arrive from different places and in either order.
        // Whichever lands second has to announce itself, or every surface keeps the answer it
        // computed while the pair was still incomplete.
        ImportedGlyphProfile profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        int changes = 0;
        catalog.Changed += () => changes++;

        catalog.ReplacePackageProfiles([profile]);
        catalog.SetActiveDevice("device-a");

        Assert.Equal(2, changes);
        Assert.Same(
            profile,
            catalog.SelectProfile(true, DeviceGlyphSelection.Automatic, null).Profile);

        // Setting the same device again is not a change.
        catalog.SetActiveDevice("device-a");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Automatic_RequiresAnImportedProfileForTheExactDevice()
    {
        ImportedGlyphProfile profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        catalog.SetActiveDevice("device-a");
        PhysicalGlyphSelectionResult exact = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);
        catalog.SetActiveDevice("device-b");
        PhysicalGlyphSelectionResult otherDevice = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        Assert.Same(profile, exact.Profile);
        Assert.Null(otherDevice.Profile);
        Assert.Equal(PhysicalGlyphFallbackReason.ExactDeviceMismatch, otherDevice.FallbackReason);
    }

    [Fact]
    public void MissingManualProfile_FallsBackThroughAutomaticAndReportsMissing()
    {
        ImportedGlyphProfile profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        catalog.SetActiveDevice("device-a");
        PhysicalGlyphSelectionResult result = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.ManualReviewedProfile,
            "removed.profile");

        Assert.Same(profile, result.Profile);
        Assert.True(result.FellBackFromMissingManualProfile);
    }

    [Fact]
    public void DeviceIntegrationOff_AlwaysReturnsGenericOrNativeFallback()
    {
        ImportedGlyphProfile profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        catalog.SetActiveDevice("device-a");
        PhysicalGlyphSelectionResult result = catalog.SelectProfile(
            false,
            DeviceGlyphSelection.ManualReviewedProfile,
            "example.handheld");

        Assert.Null(result.Profile);
        Assert.Equal(
            PhysicalGlyphFallbackReason.DeviceIntegrationDisabled,
            result.FallbackReason);
    }

    [Fact]
    public void GlyphSelectionViewReportsGenericFallbackWithoutClaimingDeviceArtwork()
    {
        DescriptorRow row = DeviceOverlayBridge.PhysicalGlyphSelectionView(
            DeviceGlyphSelection.Automatic,
            new PhysicalGlyphSelectionResult(
                null,
                PhysicalGlyphFallbackReason.ExactDeviceMismatch,
                false));

        Assert.Equal(DescriptorStatus.Warning, row.Status);
        Assert.Equal("AUTO", row.TrailingText);
        Assert.Contains("generic glyphs", row.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(row.CanInvoke);
    }

    [Fact]
    public void DeviceDescriptionSurvivesControllerManagementOffButNavigationDoesNotMislabelExternalInput()
    {
        ImportedGlyphProfile profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphService service = new(catalog);
        catalog.ReplacePackageProfiles([profile]);
        catalog.SetActiveDevice("device-a");
        PhysicalGlyphSelectionResult selected = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        // Controller-management state is deliberately not an input to profile selection. Only the
        // surface authority decides whether an active external source may display it.
        PhysicalGlyphRenderPlan device = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription,
            activeInputSourceIsManagedHandheld: false,
            PhysicalGlyphTheme.Dark,
            1);
        PhysicalGlyphRenderPlan externalNavigation = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.NavigationHint,
            activeInputSourceIsManagedHandheld: false,
            PhysicalGlyphTheme.Dark,
            1);

        Assert.True(device.UsesDeviceArtwork);
        Assert.False(externalNavigation.UsesDeviceArtwork);
        Assert.Equal(
            PhysicalGlyphFallbackReason.SourceNotHandheld,
            externalNavigation.FallbackReason);
    }

    [Fact]
    public void Cache_IsBoundedAndReleasedWhenPackageProfileChanges()
    {
        ImportedGlyphProfile profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphService service = new(
            catalog,
            maximumCacheEntries: 1,
            maximumCacheBytes: 4096);
        catalog.ReplacePackageProfiles([profile]);
        catalog.SetActiveDevice("device-a");
        PhysicalGlyphSelectionResult selected = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        _ = service.Resolve(selected, GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription, true, PhysicalGlyphTheme.Light, 1);
        _ = service.Resolve(selected, GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription, true, PhysicalGlyphTheme.Dark, 1.5);

        Assert.Equal(1, service.CachedEntryCount);
        Assert.InRange(service.CachedBytes, 1, 4096);

        catalog.ReplacePackageProfiles([]);
        Assert.Equal(0, service.CachedEntryCount);
        Assert.Equal(0, service.CachedBytes);
    }

    [Fact]
    public void PresentControlWithoutReviewedArtwork_UsesGenericFallback()
    {
        ImportedGlyphProfile profile = ImportProfile(["device-a"], includeArtwork: false);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphService service = new(catalog);
        catalog.ReplacePackageProfiles([profile]);
        catalog.SetActiveDevice("device-a");
        PhysicalGlyphSelectionResult selected = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        PhysicalGlyphRenderPlan result = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription,
            true,
            PhysicalGlyphTheme.HighContrast,
            2);

        Assert.False(result.UsesDeviceArtwork);
        Assert.Equal(PhysicalGlyphFallbackReason.ArtworkMissing, result.FallbackReason);
    }

    private static ImportedGlyphProfile ImportProfile(
        IReadOnlyList<string> exactDeviceIds,
        bool includeArtwork = true)
    {
        byte[] artwork = OnePixelPng();
        string hash = Convert.ToHexString(SHA256.HashData(artwork)).ToLowerInvariant();
        GlyphAssetLockEntry asset = new()
        {
            Sha256 = hash,
            Format = GlyphAssetFormat.Png,
            ByteCount = artwork.Length,
            Role = GlyphAssetRole.Control,
            PixelWidth = 1,
            PixelHeight = 1,
        };
        GlyphProfileManifest manifest = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = "example.handheld",
            DisplayName = "Example handheld",
            Revision = 1,
            ExactDeviceIds = exactDeviceIds,
            SourceRevision = "revision-1",
            NoticePath = "THIRD_PARTY_NOTICES.md",
            Assets = includeArtwork ? [asset] : [],
            Controls =
            [
                new GlyphControlMapping
                {
                    Control = GlyphControlId.FaceSouth,
                    Presence = GlyphControlPresence.Present,
                    AssetSha256 = includeArtwork ? hash : null,
                },
            ],
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [GlyphPackageLayout.ProfileManifest(manifest.ProfileId)] =
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    DeviceJsonContext.Default.GlyphProfileManifest),
            [manifest.NoticePath] = Encoding.UTF8.GetBytes("Example glyph notice\n"),
        };
        if (includeArtwork)
        {
            files[GlyphPackageLayout.Asset(hash, GlyphAssetFormat.Png)] = artwork;
        }

        GlyphPackageImportResult result = GlyphPackageImporter.Import(
            new GlyphTestPackageSource(manifest.ProfileId, files));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return Assert.Single(result.Profiles);
    }

    private static byte[] OnePixelPng()
    {
        using MemoryStream output = new();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR", header);

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write([0, 255, 0, 0, 255]);
        }
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        byte[] crcInput = [.. typeBytes, .. data];
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xedb88320u & mask);
            }
        }
        return ~crc;
    }

}
