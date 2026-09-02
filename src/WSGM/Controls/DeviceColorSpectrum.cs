using System;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace WSGM.Controls;

/// <summary>
/// The overlay's hue/saturation field: Avalonia's <see cref="ColorSpectrum"/> with deterministic
/// left/right hue stepping so a controller can sweep the square without being trapped in it.
/// </summary>
/// <remarks>
/// The control behaves like a horizontal <see cref="Avalonia.Controls.Slider"/> to navigation:
/// Left/Right sweep the hue and are consumed, Up/Down leave the control so the d-pad can reach the
/// channel sliders below. Touch and mouse still get the full two-dimensional field. Owning the
/// arrow handling here keeps the keyboard path and the SDL gamepad path applying the identical
/// step, the same contract <see cref="CurveEditor"/> follows.
/// </remarks>
internal sealed class DeviceColorSpectrum : ColorSpectrum
{
    /// <summary>Hue degrees moved per step; a full sweep is 40 presses.</summary>
    private const double HueStep = 9;

    /// <summary>The base control's theme still applies; only the input contract is WSGM's.</summary>
    protected override Type StyleKeyOverride => typeof(ColorSpectrum);

    internal DeviceColorSpectrum()
    {
        Focusable = true;
    }

    /// <summary>Applies one controller or keyboard step to the selected hue.</summary>
    /// <param name="direction">The direction pressed. Only Left and Right change anything.</param>
    internal void ApplyDirection(NavigationDirection direction)
    {
        double delta = direction switch
        {
            NavigationDirection.Left => -HueStep,
            NavigationDirection.Right => HueStep,
            _ => 0,
        };
        if (delta == 0)
        {
            return;
        }

        HsvColor current = HsvColor;
        double hue = (current.H + delta + 360) % 360;
        HsvColor = new HsvColor(current.A, hue, current.S, current.V);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Arrows bypass the base control entirely: Left/Right apply WSGM's hue step, Up/Down stay
    /// unhandled so window navigation moves focus. Letting the base see them would add a second,
    /// differently-sized step on the same physical press.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                ApplyDirection(NavigationDirection.Left);
                e.Handled = true;
                return;
            case Key.Right:
                ApplyDirection(NavigationDirection.Right);
                e.Handled = true;
                return;
            case Key.Up:
            case Key.Down:
                return;
            default:
                base.OnKeyDown(e);
                return;
        }
    }
}
