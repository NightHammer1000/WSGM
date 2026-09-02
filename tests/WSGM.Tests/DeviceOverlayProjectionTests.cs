using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>Pure final-overlay projection coverage; no device package or host is started.</summary>
public sealed class DeviceOverlayProjectionTests
{
    [Fact]
    public void SimulatedDeviceGroupsEveryCapabilityIntoAStableSemanticSection()
    {
        using SimulatedDeviceOverlaySource source = new();

        DeviceOverlaySnapshot snapshot = source.Snapshot();

        Assert.True(snapshot.Visible);
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.PowerAndThermals);
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.ControllerAndMotion);
        Assert.Contains(snapshot.Capabilities,
            capability => capability.Section == DeviceOverlaySection.LightingAndFeatures);
        Assert.All(snapshot.Capabilities,
            capability => Assert.NotEqual(DescriptorStatus.None, capability.Status));
        Assert.NotNull(snapshot.GlyphSelection);
        Assert.DoesNotContain(snapshot.Capabilities,
            capability => capability.CapabilityId == "wsgm.glyph.selection");
    }

    [Fact]
    public void AvailableActionOnlyCapabilityIsRunnableWithoutInventingReadback()
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = "haptic.rumble",
            Role = CapabilityRole.HapticSink,
            ValueKind = CapabilityValueKind.None,
            Display = new CapabilityDisplay { Key = DisplayKey.Rumble },
            SupportsAction = true,
            Persistence = CapabilityPersistence.Volatile,
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId,
            Available = true,
            Quality = HardwareStateQuality.Unknown,
            DescriptorGeneration = 4,
            CycleGeneration = 3,
        };

        DeviceOverlayCapability capability = DeviceOverlayBridge.ToOverlayCapability(
            new DeviceCapabilityView(
                descriptor,
                new CapabilityProjection { State = state },
                LastResult: null),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(DescriptorStatus.Available, capability.Status);
        Assert.True(capability.CanInvoke);
        Assert.Equal("RUN", capability.TrailingText);
        Assert.Equal("Ready · action has no readback", capability.Description);
    }

    [Fact]
    public async Task SimulatedDeviceMutationRaisesOneSharedChangeAndUpdatesReadback()
    {
        using SimulatedDeviceOverlaySource source = new();
        int changes = 0;
        source.Changed += () => changes++;
        DeviceOverlayCapability tdp = source.Snapshot().Capabilities.Single(
            capability => capability.CapabilityId == "preview.power.tdp");

        await source.InvokeAsync(tdp);

        Assert.Equal(1, changes);
        Assert.Equal("16 W", source.Snapshot().Capabilities.Single(
            capability => capability.CapabilityId == "preview.power.tdp").TrailingText);
    }

    [Fact]
    public async Task SimulatedGlyphSelectionUsesItsDedicatedCommandPath()
    {
        using SimulatedDeviceOverlaySource source = new();
        int changes = 0;
        source.Changed += () => changes++;

        DescriptorRow before = Assert.IsType<DescriptorRow>(
            source.Snapshot().GlyphSelection);
        await source.CyclePhysicalGlyphSelectionAsync();
        DescriptorRow after = Assert.IsType<DescriptorRow>(
            source.Snapshot().GlyphSelection);

        Assert.Equal("AUTO", before.TrailingText);
        Assert.Equal("STEAM", after.TrailingText);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SimulatedDeviceDeclaresThePluginLayout()
    {
        using SimulatedDeviceOverlaySource source = new();

        DeviceOverlaySnapshot snapshot = source.Snapshot();

        Assert.Equal(
            ["power", "cooling", "lighting"],
            snapshot.PluginSections.Select(section => section.SectionId));
        Assert.Contains(snapshot.Capabilities, capability =>
            capability.PluginSectionId == "lighting"
            && capability.Role == CapabilityRole.LightingZoneColor);
        Assert.Contains(snapshot.Capabilities, capability =>
            capability.Role == CapabilityRole.LightingBrightness);
        // One row stays unplaced so the WSGM fallback grouping keeps working beside the layout.
        Assert.Contains(snapshot.Capabilities, capability => capability.PluginSectionId is null);
    }

    [Fact]
    public async Task SimulatedColorAndBrightnessAcceptStagedValues()
    {
        using SimulatedDeviceOverlaySource source = new();
        DeviceOverlaySnapshot snapshot = source.Snapshot();
        DeviceOverlayCapability rings = snapshot.Capabilities.Single(
            capability => capability.CapabilityId == "preview.lighting.rings");
        DeviceOverlayCapability brightness = snapshot.Capabilities.Single(
            capability => capability.CapabilityId == "preview.lighting.brightness");

        await source.InvokeAsync(rings with
        {
            NextValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Color,
                ColorValue = 0x123456,
            },
        });
        await source.InvokeAsync(brightness with
        {
            NextValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = 55,
            },
        });

        DeviceOverlaySnapshot after = source.Snapshot();
        Assert.Equal("#123456", after.Capabilities.Single(
            capability => capability.CapabilityId == "preview.lighting.rings").TrailingText);
        Assert.Equal("55%", after.Capabilities.Single(
            capability => capability.CapabilityId == "preview.lighting.brightness").TrailingText);
    }
}
