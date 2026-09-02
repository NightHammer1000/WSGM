using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>
/// A device capability rendered as a labelled slider — the control an integer capability with a
/// declared range asks for, instead of a value-cycling button. The <see cref="Slider"/> is the
/// focusable element on purpose: <c>GamepadNavigation</c> already routes Left/Right to a focused
/// slider and lets Up/Down leave the row, so pad, touch and keyboard all drive it with no extra
/// plumbing.
/// </summary>
/// <remarks>
/// Writes are debounced. A touch drag and a held Left/Right both stream value changes, and device
/// firmware persists some capabilities (charge limit) to non-volatile memory, so the row commits
/// once the value settles rather than on every tick. Uncertain hardware writes are never retried
/// here; the snapshot's next refresh reconciles the shown value.
/// </remarks>
internal sealed class DeviceSliderRow : Border
{
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(250);

    private readonly Slider _slider;
    private readonly TextBlock _value;
    private readonly CapabilityUnit _unit;
    private readonly DispatcherTimer _commit;
    private readonly Action<int> _onCommit;

    /// <summary>Builds the row for one capability.</summary>
    /// <param name="key">Stable focus key, mirrored onto the slider for focus restore.</param>
    /// <param name="title">Row heading.</param>
    /// <param name="description">Supporting line under the heading.</param>
    /// <param name="minimum">Inclusive lower bound.</param>
    /// <param name="maximum">Inclusive upper bound.</param>
    /// <param name="step">Legal step between values; coerced to at least 1.</param>
    /// <param name="unit">Unit for the live value label.</param>
    /// <param name="valueNow">The value to show initially.</param>
    /// <param name="enabled">Whether the slider accepts input.</param>
    /// <param name="onCommit">Invoked with the settled integer value to write.</param>
    internal DeviceSliderRow(
        string key,
        string title,
        string description,
        int minimum,
        int maximum,
        int step,
        CapabilityUnit unit,
        int valueNow,
        bool enabled,
        Action<int> onCommit)
    {
        ArgumentNullException.ThrowIfNull(onCommit);
        _unit = unit;
        _onCommit = onCommit;
        _commit = new DispatcherTimer { Interval = CommitDelay };
        _commit.Tick += OnCommitTick;

        int tick = Math.Max(1, step);
        int clamped = Math.Clamp(valueNow, minimum, maximum);

        Classes.Add("tile");
        Tag = key;

        var header = new TextBlock { Text = title };
        header.Classes.Add("setting-title");

        _value = new TextBlock
        {
            Text = Format(clamped),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _value.Classes.Add("setting-title");

        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(header, 0);
        Grid.SetColumn(_value, 1);
        titleRow.Children.Add(header);
        titleRow.Children.Add(_value);

        var caption = new TextBlock { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        caption.Classes.Add("caption");

        // Matches the color view's channel sliders, which are known to drive from the pad: the
        // SDL path in GamepadNavigation nudges a focused Slider by its TickFrequency, so that is
        // the only step property that matters, and snap/Focusable overrides are left at Avalonia's
        // focusable defaults rather than fighting them.
        _slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            TickFrequency = tick,
            Value = clamped,
            IsEnabled = enabled,
            Tag = key,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _slider.ValueChanged += OnSliderValueChanged;
        // The Fluent Slider template contains focusable inner RepeatButtons on either side of the
        // thumb. Directional (XY) focus lands on THOSE rather than the Slider, so the pad handler's
        // `target is Slider` check misses and Left/Right does nothing — the reported "focus lands
        // before and after the dot". Once templated, leave only the Slider itself focusable.
        _slider.AttachedToVisualTree += (_, _) =>
        {
            foreach (InputElement descendant in _slider.GetVisualDescendants().OfType<InputElement>())
            {
                if (!ReferenceEquals(descendant, _slider))
                {
                    descendant.Focusable = false;
                }
            }
        };

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(description))
        {
            body.Children.Add(caption);
        }

        body.Children.Add(_slider);
        Child = body;
    }

    /// <summary>The slider is the focus target so gamepad focus restore lands on the control.</summary>
    internal Control FocusTarget => _slider;

    private void OnSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _value.Text = Format((int)Math.Round(_slider.Value));
        // Restart the settle window on every change so a drag or a held press commits once.
        _commit.Stop();
        _commit.Start();
    }

    private void OnCommitTick(object? sender, EventArgs e)
    {
        _commit.Stop();
        _onCommit((int)Math.Round(_slider.Value));
    }

    private string Format(int value) =>
        $"{value.ToString(CultureInfo.CurrentCulture)}{Suffix(_unit)}";

    private static string Suffix(CapabilityUnit unit) => unit switch
    {
        CapabilityUnit.Watt => " W",
        CapabilityUnit.Percent => "%",
        CapabilityUnit.Celsius => " °C",
        CapabilityUnit.Rpm => " RPM",
        CapabilityUnit.Milliampere => " mA",
        _ => string.Empty,
    };
}
