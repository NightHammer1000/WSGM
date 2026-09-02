using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Serialization;

namespace WSGM.Tests;

public sealed class SteamGlyphCssTests
{
    [Fact]
    public void ImportedProfileProducesOnlyCatalogOwnedExactMappings()
    {
        ImportedGlyphProfile profile = ImportProfile();

        SteamInputGlyphPresentation? presentation = SteamInputGlyphPresentation.Create(profile);

        Assert.NotNull(presentation);
        Assert.Equal("example.handheld", presentation.ProfileId);
        Assert.All(presentation.StableResources, mapping =>
        {
            Assert.Equal(GlyphControlId.FaceSouth, mapping.Control);
            Assert.StartsWith("/steaminputglyphs/", mapping.ValvePath, StringComparison.Ordinal);
            Assert.StartsWith(
                "data:image/svg+xml;base64,",
                mapping.Asset.DataUri,
                StringComparison.Ordinal);
        });
        Assert.Equal("full", Assert.Single(presentation.ControllerImages).Slot);

        // Absence is the default: the profile declares what the device HAS, and everything it does
        // not name is absent. Requiring each missing control to be declared meant the one nobody
        // remembered to list was the one left on screen.
        Assert.Contains(GlyphControlId.LeftTrackpad, presentation.AbsentControls);
        Assert.DoesNotContain(GlyphControlId.FaceSouth, presentation.AbsentControls);
    }

    [Fact]
    public void EveryValveResourceForAControlIsOverriddenWithTheOnePluginAsset()
    {
        SteamInputGlyphPresentation presentation = Presentation();

        string css = SteamGlyphCss.Build(presentation, hideAbsentControls: false);

        // The south face button is drawn from several Valve resources depending on the controller
        // family; all of them have to resolve to the handheld's own artwork.
        Assert.Contains("img[src=\"/steaminputglyphs/shared_button_a.svg\"]", css, StringComparison.Ordinal);
        Assert.Contains("img[src=\"/steaminputglyphs/shared_color_button_a.svg\"]", css, StringComparison.Ordinal);
        Assert.Contains("img[src=\"/steaminputglyphs/ps_button_x.svg\"]", css, StringComparison.Ordinal);
        Assert.Contains("content: url(\"data:image/svg+xml;base64,", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasedLogicalControlIsPresentWhenItsPhysicalArtworkExists()
    {
        SteamInputGlyphPresentation? presentation = SteamInputGlyphPresentation.Create(
            ImportProfile(aliasEastToSouth: true));

        Assert.NotNull(presentation);
        Assert.DoesNotContain(GlyphControlId.FaceEast, presentation.AbsentControls);
        Assert.Contains(
            presentation.StableResources,
            mapping => mapping.Control is GlyphControlId.FaceEast);
    }

    [Fact]
    public void OnlyControlsThePluginSuppliesAppearInTheStylesheet()
    {
        SteamInputGlyphPresentation presentation = Presentation();

        string css = SteamGlyphCss.Build(presentation, hideAbsentControls: false);

        // WSGM ships no artwork of its own, so a control the profile does not supply must simply be
        // absent from the sheet and keep Valve's own glyph.
        Assert.DoesNotContain("shared_dpad_up.svg", css, StringComparison.Ordinal);
        Assert.DoesNotContain("xbox_button_start.svg", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerImagesArePublishedAsCustomProperties()
    {
        SteamInputGlyphPresentation presentation = Presentation();

        string css = SteamGlyphCss.Build(presentation, hideAbsentControls: false);

        Assert.Contains("--wsgm-controller-full-image: url(\"data:", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentControlHidesItsRowOnlyWhenHidingIsRequested()
    {
        SteamInputGlyphPresentation presentation = Presentation();

        string hidden = SteamGlyphCss.Build(presentation, hideAbsentControls: true);
        string shown = SteamGlyphCss.Build(presentation, hideAbsentControls: false);

        // The glyph names are the ones the client actually draws. The table previously named
        // sd_ltrackpad_swipe.svg, which this build renders nowhere, so every hide rule matched
        // nothing and a device with no trackpads still showed both trackpad sections.
        Assert.Contains("sd_ltrackpad_up.svg", hidden, StringComparison.Ordinal);
        Assert.Contains("display: none;", hidden, StringComparison.Ordinal);

        // Anchored on the section container, so a control the device lacks takes its heading and
        // its bindings with it rather than leaving an empty group behind.
        Assert.Contains($".{SteamGlyphCss.ControlSectionClass}:has(", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("sd_ltrackpad_up.svg", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInlineSteamLogoIsReplacedFromTheGuideArtwork()
    {
        SteamInputGlyphPresentation presentation = Presentation(guide: true);

        string css = SteamGlyphCss.Build(presentation, hideAbsentControls: false);

        // Valve draws this one as an inline path, so a content override cannot reach it: the inner
        // svg is hidden and the container is painted instead.
        Assert.Contains(SteamGlyphCss.SteamLogoPathData, css, StringComparison.Ordinal);
        Assert.Contains($".{SteamGlyphCss.InlineLogoContainerClass}", css, StringComparison.Ordinal);
        Assert.Contains("background: url(\"data:", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileWithNothingToDrawProducesNoStylesheetAtAll()
    {
        SteamInputGlyphPresentation empty = new("example.handheld", 1, [], [], []);

        Assert.Equal(string.Empty, SteamGlyphCss.Build(empty, hideAbsentControls: true));
    }

    [Fact]
    public void OnlyABoundedDataUriMayReachTheStylesheet()
    {
        Assert.Throws<ArgumentException>(() =>
            SteamGlyphCss.Url("https://example.invalid/glyph.svg"));
        Assert.Throws<ArgumentException>(() =>
            SteamGlyphCss.Url("data:image/svg+xml,<svg onload=\"alert(1)\"/>"));
        Assert.Throws<ArgumentException>(() =>
            SteamGlyphCss.Url("data:image/svg+xml;base64,AAA\") ; body { display:none } a{content:url(\""));
    }

    [Fact]
    public void AttributeValuesAreEscapedForTheSelector()
    {
        Assert.Equal("a\\\"b", SteamGlyphCss.Attribute("a\"b"));
        Assert.Equal("a\\\\b", SteamGlyphCss.Attribute("a\\b"));
    }

    private static SteamInputGlyphPresentation Presentation(bool guide = false)
    {
        SteamInputGlyphPresentation? presentation =
            SteamInputGlyphPresentation.Create(ImportProfile(guide));
        Assert.NotNull(presentation);
        return presentation;
    }

    private static ImportedGlyphProfile ImportProfile(
        bool guide = false,
        bool aliasEastToSouth = false)
    {
        byte[] controlSvg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
            + "<path d=\"M 0 0 L 64 64 Z\"/></svg>");
        byte[] guideSvg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
            + "<path d=\"M 0 0 L 32 64 Z\"/></svg>");
        byte[] controllerSvg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 128 64\">"
            + "<path d=\"M 0 0 L 128 64 Z\"/></svg>");
        string controlHash = Hash(controlSvg);
        string guideHash = Hash(guideSvg);
        string controllerHash = Hash(controllerSvg);
        GlyphAssetLockEntry controlAsset = Asset(
            controlHash,
            controlSvg.Length,
            GlyphAssetRole.Control,
            new GlyphViewBox(0, 0, 64, 64));
        GlyphAssetLockEntry guideAsset = Asset(
            guideHash,
            guideSvg.Length,
            GlyphAssetRole.Control,
            new GlyphViewBox(0, 0, 64, 64));
        GlyphAssetLockEntry controllerAsset = Asset(
            controllerHash,
            controllerSvg.Length,
            GlyphAssetRole.FullController,
            new GlyphViewBox(0, 0, 128, 64));
        List<GlyphControlMapping> controls =
        [
            new GlyphControlMapping
            {
                Control = GlyphControlId.FaceSouth,
                Presence = GlyphControlPresence.Present,
                AssetSha256 = controlHash,
            },
            new GlyphControlMapping
            {
                Control = GlyphControlId.LeftTrackpad,
                Presence = GlyphControlPresence.Absent,
            },
        ];
        if (guide)
        {
            controls.Add(new GlyphControlMapping
            {
                Control = GlyphControlId.Guide,
                Presence = GlyphControlPresence.Present,
                AssetSha256 = guideHash,
            });
        }

        GlyphProfileManifest manifest = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = "example.handheld",
            DisplayName = "Example handheld",
            Revision = 4,
            ExactDeviceIds = ["example-device"],
            SourceRevision = "revision-1",
            NoticePath = "THIRD_PARTY_NOTICES.md",
            Assets = guide
                ? [controlAsset, guideAsset, controllerAsset]
                : [controlAsset, controllerAsset],
            ControllerImages = new GlyphControllerImages { FullSha256 = controllerHash },
            Controls = controls,
            Aliases = aliasEastToSouth
                ?
                [
                    new GlyphControlAlias
                    {
                        LogicalControl = GlyphControlId.FaceEast,
                        PhysicalControl = GlyphControlId.FaceSouth,
                    },
                ]
                : [],
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [GlyphPackageLayout.ProfileManifest(manifest.ProfileId)] =
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    DeviceJsonContext.Default.GlyphProfileManifest),
            [manifest.NoticePath] = Encoding.UTF8.GetBytes("Example glyph notice\n"),
            [GlyphPackageLayout.Asset(controlHash, GlyphAssetFormat.Svg)] = controlSvg,
            [GlyphPackageLayout.Asset(guideHash, GlyphAssetFormat.Svg)] = guideSvg,
            [GlyphPackageLayout.Asset(controllerHash, GlyphAssetFormat.Svg)] = controllerSvg,
        };
        GlyphPackageImportResult result = GlyphPackageImporter.Import(
            new GlyphTestPackageSource(manifest.ProfileId, files));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return Assert.Single(result.Profiles);
    }

    private static GlyphAssetLockEntry Asset(
        string hash,
        int byteCount,
        GlyphAssetRole role,
        GlyphViewBox viewBox) => new()
        {
            Sha256 = hash,
            Format = GlyphAssetFormat.Svg,
            ByteCount = byteCount,
            Role = role,
            ViewBox = viewBox,
        };

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

}
