using WSGM.Core;

namespace WSGM.Tests;

public sealed class NativeQamOverlayLevelWireTests
{
    // Valve's EGraphicsPerfOverlayLevel: Hidden=0, Basic=1, Medium=2, Full=3, Minimal=4 — while
    // the selector presents OFF, Minimal, Basic, Medium, Full. The notch is WSGM's semantic level.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void WireAndNotchTranslateBothWays(int steamValue, int notch)
    {
        Assert.Equal(notch, NativeQamOverlayLevelWire.ToNotch(steamValue));
        Assert.Equal(steamValue, NativeQamOverlayLevelWire.ToSteam(notch));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void UnknownValuesReadAsOff(int value)
    {
        Assert.Equal(0, NativeQamOverlayLevelWire.ToNotch(value));
        Assert.Equal(0, NativeQamOverlayLevelWire.ToSteam(value));
    }
}
