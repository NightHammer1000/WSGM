using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>A bounded, controller-driven editor for one device lighting-zone color.</summary>
/// <remarks>
/// The editor stages every change locally and writes only when Apply is pressed. That is required
/// for device lighting whose firmware persists every commit: navigating a picker must not stream
/// writes into non-volatile profile memory. The full-spectrum field, the three channel sliders,
/// and the firmware brightness slider all edit the same staged state; the overlay keyboard remains
/// available for an exact hexadecimal value.
/// </remarks>
public sealed class DeviceColorView : OverlaySubView
{
    private IDeviceOverlaySource? _source;
    private DeviceOverlayCapability? _capability;
    private DeviceOverlayCapability? _brightnessCapability;
    private int _initialColor;
    private int _color;
    private int _initialBrightness;
    private int _brightness;
    private bool _applying;

    /// <summary>Guards the control↔state sync so one edit cannot echo through the others.</summary>
    private bool _updating;

    private Border? _swatch;
    private TextBlock? _hexCaption;
    private DeviceColorSpectrum? _spectrum;
    private readonly Slider?[] _channels = new Slider?[3];
    private readonly TextBlock?[] _channelValues = new TextBlock?[3];
    private TextBlock? _brightnessValue;

    /// <inheritdoc />
    protected override string LogScope => "Device color";

    /// <summary>Stages the capability's observed color and opens its editor.</summary>
    /// <param name="source">The device source that owns command execution.</param>
    /// <param name="capability">A writable color capability.</param>
    /// <param name="brightness">
    /// The device's firmware brightness capability from the same snapshot, or null when it has
    /// none. Staged and applied with the color, because "how bright are the rings" is part of the
    /// one question this editor answers.
    /// </param>
    internal void Open(
        IDeviceOverlaySource source,
        DeviceOverlayCapability capability,
        DeviceOverlayCapability? brightness = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capability);
        if (capability.CurrentValue is not
            { Kind: CapabilityValueKind.Color, ColorValue: { } color })
        {
            throw new ArgumentException("The capability has no observed color.", nameof(capability));
        }

        _source = source;
        _capability = capability;
        _brightnessCapability = brightness is
        { CanInvoke: true, CurrentValue: { Kind: CapabilityValueKind.Integer, IntegerValue: not null } }
            ? brightness
            : null;
        _initialBrightness = Math.Clamp(
            _brightnessCapability?.CurrentValue?.IntegerValue ?? 0, 0, 100);
        _brightness = _initialBrightness;
        _initialColor = color & 0xFFFFFF;
        _color = _initialColor;
        _applying = false;
        _stack.Clear();
        _current = null;
        Navigate(Render);
    }

    private void Render()
    {
        DeviceOverlayCapability? capability = _capability;
        if (capability is null)
        {
            RenderMessage("Lighting color", "The device color is no longer available.");
            return;
        }

        var stack = NewStack(capability.Title);

        // Two columns so the editor fills the wide sheet instead of scrolling a tall single
        // column: the preview and spectrum on the left, the precise controls and actions on the
        // right.
        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 16,
        };
        var left = new StackPanel { Spacing = 4 };
        var right = new StackPanel { Spacing = 4 };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);

        _swatch = new Border
        {
            Height = 40,
            Margin = new Thickness(2, 0, 2, 4),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(ToAvaloniaColor(_color)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
        };
        left.Children.Add(_swatch);
        _hexCaption = Caption(HexCaptionText());
        left.Children.Add(_hexCaption);

        left.Children.Add(SectionLabel("SPECTRUM"));
        _spectrum = new DeviceColorSpectrum
        {
            Height = 220,
            Margin = new Thickness(2, 0, 2, 4),
            CornerRadius = new CornerRadius(6),
            Color = ToAvaloniaColor(_color),
        };
        _spectrum.ColorChanged += (_, e) =>
        {
            if (!_updating && !_applying)
            {
                SetColor(
                    (e.NewColor.R << 16) | (e.NewColor.G << 8) | e.NewColor.B,
                    source: _spectrum);
            }
        };
        left.Children.Add(_spectrum);

        right.Children.Add(SectionLabel("CHANNELS"));
        right.Children.Add(ChannelRow(0, "Red", 16));
        right.Children.Add(ChannelRow(1, "Green", 8));
        right.Children.Add(ChannelRow(2, "Blue", 0));
        right.Children.Add(Row(
            "Exact hexadecimal color",
            $"#{_color:X6}",
            Icons.Wrench,
            _applying ? null : EditHex));

        if (_brightnessCapability is not null)
        {
            right.Children.Add(SectionLabel("BRIGHTNESS"));
            right.Children.Add(BrightnessRow());
        }

        right.Children.Add(SectionLabel(""));
        right.Children.Add(PrimaryRow(
            _applying ? "Applying…" : "Apply",
            _brightnessCapability is null
                ? "Commit this color to the device"
                : "Commit this color and brightness to the device",
            Icons.Play,
            () =>
            {
                if (!_applying)
                {
                    _ = RunSafelyAsync(ApplyAsync(), "apply");
                }
            }));
        right.Children.Add(Row("Cancel", "Discard the staged changes", Icons.ExitFullscreen,
            _applying ? null : () => Back()));

        stack.Children.Add(columns);
        SetContent(stack);
    }

    /// <summary>One channel slider row: label, 0–255 slider, live value.</summary>
    private Grid ChannelRow(int index, string label, int shift)
    {
        Slider slider = new()
        {
            Minimum = 0,
            Maximum = 255,
            TickFrequency = 5,
            Value = Channel(shift),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        TextBlock value = SliderValueText(Channel(shift).ToString(CultureInfo.CurrentCulture));
        _channels[index] = slider;
        _channelValues[index] = value;
        slider.ValueChanged += (_, _) =>
        {
            if (_updating || _applying)
            {
                return;
            }

            int mask = 0xFF << shift;
            int next = Math.Clamp((int)Math.Round(slider.Value), 0, 255);
            SetColor((_color & ~mask) | (next << shift), source: slider);
        };
        return SliderRow(label, slider, value);
    }

    private Grid BrightnessRow()
    {
        Slider slider = new()
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 5,
            Value = _brightness,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        TextBlock value = SliderValueText($"{_brightness}%");
        _brightnessValue = value;
        slider.ValueChanged += (_, _) =>
        {
            if (_updating || _applying)
            {
                return;
            }

            _brightness = Math.Clamp((int)Math.Round(slider.Value), 0, 100);
            value.Text = $"{_brightness}%";
        };
        return SliderRow("Brightness", slider, value);
    }

    private static Grid SliderRow(string label, Slider slider, TextBlock value)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("72,*,52"),
            Margin = new Thickness(2, 0, 2, 0),
        };
        TextBlock caption = new()
        {
            Text = label,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(caption);
        row.Children.Add(slider);
        row.Children.Add(value);
        return row;
    }

    private static TextBlock SliderValueText(string text) => new()
    {
        Text = text,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
    };

    private string HexCaptionText() =>
        $"#{_color:X6} · changes are written only when Apply is pressed.";

    private int Channel(int shift) => (_color >> shift) & 0xFF;

    /// <summary>Moves the staged color and syncs every control except the one that changed it.</summary>
    /// <param name="value">The new packed RGB value.</param>
    /// <param name="source">The control driving the change, skipped during sync.</param>
    private void SetColor(int value, object? source = null)
    {
        _color = value & 0xFFFFFF;
        _updating = true;
        try
        {
            if (_swatch is not null)
            {
                _swatch.Background = new SolidColorBrush(ToAvaloniaColor(_color));
            }

            if (_hexCaption is not null)
            {
                _hexCaption.Text = HexCaptionText();
            }

            if (_spectrum is not null && !ReferenceEquals(_spectrum, source))
            {
                _spectrum.Color = ToAvaloniaColor(_color);
            }

            int[] shifts = [16, 8, 0];
            for (int index = 0; index < 3; index++)
            {
                if (_channelValues[index] is { } text)
                {
                    text.Text = Channel(shifts[index]).ToString(CultureInfo.CurrentCulture);
                }

                if (_channels[index] is { } slider && !ReferenceEquals(slider, source))
                {
                    slider.Value = Channel(shifts[index]);
                }
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void EditHex() => EditText(
        "Lighting color (#RRGGBB)",
        $"#{_color:X6}",
        7,
        value =>
        {
            if (TryParseColor(value, out int color))
            {
                SetColor(color);
                Replace(Render);
            }
            else
            {
                Toast("Enter six hexadecimal digits, for example #FF8000.");
            }
        });

    private async Task ApplyAsync()
    {
        IDeviceOverlaySource? source = _source;
        DeviceOverlayCapability? capability = _capability;
        if (source is null || capability is null)
        {
            return;
        }

        bool colorChanged = _color != _initialColor;
        bool brightnessChanged = _brightnessCapability is not null
            && _brightness != _initialBrightness;
        if (!colorChanged && !brightnessChanged)
        {
            RequestClose();
            return;
        }

        _applying = true;
        Replace(Render);
        bool applied = false;
        try
        {
            if (colorChanged)
            {
                await source.InvokeAsync(capability with
                {
                    NextValue = new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Color,
                        ColorValue = _color,
                    },
                }).ConfigureAwait(true);
            }

            if (brightnessChanged && _brightnessCapability is { } brightness)
            {
                await source.InvokeAsync(brightness with
                {
                    NextValue = new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = _brightness,
                    },
                }).ConfigureAwait(true);
            }

            applied = true;
        }
        finally
        {
            _applying = false;
            if (applied)
            {
                RequestClose();
            }
            else
            {
                Replace(Render);
            }
        }
    }

    internal static bool TryParseColor(string? text, out int color)
    {
        color = 0;
        string candidate = (text ?? string.Empty).Trim();
        if (candidate.StartsWith('#'))
        {
            candidate = candidate[1..];
        }

        return candidate.Length == 6
            && int.TryParse(candidate, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out color)
            && color is >= 0 and <= 0xFFFFFF;
    }

    private static Color ToAvaloniaColor(int color) => Color.FromRgb(
        (byte)((color >> 16) & 0xFF),
        (byte)((color >> 8) & 0xFF),
        (byte)(color & 0xFF));
}
