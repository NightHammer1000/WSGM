using WSGM.Overlay;

namespace WSGM.Tests;

public sealed class DeviceColorViewTests
{
    [Theory]
    [InlineData("#FF8000", 0xFF8000)]
    [InlineData("00ff7f", 0x00FF7F)]
    [InlineData("  000000  ", 0x000000)]
    public void ExactColorAcceptsSixRgbHexDigits(string text, int expected)
    {
        Assert.True(DeviceColorView.TryParseColor(text, out int color));
        Assert.Equal(expected, color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#123")]
    [InlineData("#GG0000")]
    [InlineData("#FFFFFFFF")]
    public void ExactColorRejectsAnythingThatIsNotRgbHex(string text) =>
        Assert.False(DeviceColorView.TryParseColor(text, out _));

    [Theory]
    [InlineData(0, false, 351)]
    [InlineData(351, true, 0)]
    [InlineData(120, true, 129)]
    public void SpectrumHueStepWrapsAroundTheWheel(double hue, bool right, double expected)
    {
        WSGM.Controls.DeviceColorSpectrum spectrum = new()
        {
            HsvColor = new Avalonia.Media.HsvColor(1, hue, 1, 1),
        };

        spectrum.ApplyDirection(right
            ? Avalonia.Input.NavigationDirection.Right
            : Avalonia.Input.NavigationDirection.Left);

        Assert.Equal(expected, spectrum.HsvColor.H, 3);
    }
}
