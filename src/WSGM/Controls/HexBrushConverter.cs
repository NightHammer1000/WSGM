using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace WSGM.Controls;

/// <summary>Converts a "#RRGGBB"/"#AARRGGBB" hex string into a solid brush for the
/// inline color-swatch previews on the Appearance page. Unparsable text (including
/// a half-typed value in the paired hex TextBox) yields a transparent brush, never
/// an error. It is shared through <c>x:Static</c>.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    /// <summary>Gets the shared stateless instance referenced from page XAML.</summary>
    public static readonly HexBrushConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && Color.TryParse(text, out var color)
            ? new ImmutableSolidColorBrush(color)
            : Brushes.Transparent;

    /// <summary>Swatch previews are one-way; converting back does nothing.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
