using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>
/// The non-slider device capability controls — toggle, dropdown, textbox — that a capability's
/// value kind asks for, so a boolean is a switch and a choice is a dropdown instead of a
/// value-cycling button. Each is a themed tile whose single interactive control is the focus
/// target; <c>GamepadNavigation</c> already routes A/Left/Right to a focused ToggleSwitch,
/// ComboBox and TextBox, so pad, touch and keyboard drive them with no extra plumbing.
/// </summary>
internal static class DeviceControlRows
{
    /// <summary>Builds the shared tile skeleton: heading, optional caption, and a right-aligned
    /// interactive control on the header line.</summary>
    private static Border Tile(string key, string title, string description, Control control)
    {
        control.Tag = key;
        var header = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        header.Classes.Add("setting-title");

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(header, 0);
        Grid.SetColumn(control, 1);
        headerRow.Children.Add(header);
        headerRow.Children.Add(control);

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(headerRow);
        if (!string.IsNullOrWhiteSpace(description))
        {
            var caption = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
            caption.Classes.Add("caption");
            body.Children.Add(caption);
        }

        return new Border { Classes = { "tile" }, Tag = key, Child = body };
    }

    /// <summary>A boolean capability as a switch.</summary>
    /// <param name="key">Stable focus key.</param>
    /// <param name="title">Row heading.</param>
    /// <param name="description">Supporting line.</param>
    /// <param name="isOn">Current state.</param>
    /// <param name="enabled">Whether input is accepted.</param>
    /// <param name="onChanged">Invoked with the new state.</param>
    /// <returns>The tile and its focus target.</returns>
    internal static (Border Row, Control Focus) Toggle(
        string key,
        string title,
        string description,
        bool isOn,
        bool enabled,
        Action<bool> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        var toggle = new ToggleSwitch
        {
            IsChecked = isOn,
            IsEnabled = enabled,
            Focusable = enabled,
            HorizontalAlignment = HorizontalAlignment.Right,
            OffContent = null,
            OnContent = null,
        };
        bool syncing = true;
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (syncing)
            {
                return;
            }

            onChanged(toggle.IsChecked ?? false);
        };
        syncing = false;
        return (Tile(key, title, description, toggle), toggle);
    }

    /// <summary>A choice capability as a dropdown.</summary>
    /// <param name="key">Stable focus key.</param>
    /// <param name="title">Row heading.</param>
    /// <param name="description">Supporting line.</param>
    /// <param name="choices">Legal values with their display labels.</param>
    /// <param name="selected">Currently selected value, or null.</param>
    /// <param name="enabled">Whether input is accepted.</param>
    /// <param name="onChanged">Invoked with the chosen value.</param>
    /// <returns>The tile and its focus target.</returns>
    internal static (Border Row, Control Focus) Choice(
        string key,
        string title,
        string description,
        IReadOnlyList<CapabilityChoice> choices,
        string? selected,
        bool enabled,
        Action<string> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        List<ChoiceItem> items = choices
            .Select(choice => new ChoiceItem(choice.Value, LabelFor(choice)))
            .ToList();
        var combo = new ComboBox
        {
            ItemsSource = items,
            DisplayMemberBinding = new Avalonia.Data.Binding(nameof(ChoiceItem.Label)),
            SelectedIndex = Math.Max(0, items.FindIndex(item =>
                string.Equals(item.Value, selected, StringComparison.Ordinal))),
            IsEnabled = enabled,
            Focusable = enabled,
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        bool syncing = true;
        combo.SelectionChanged += (_, _) =>
        {
            if (syncing || combo.SelectedItem is not ChoiceItem item)
            {
                return;
            }

            onChanged(item.Value);
        };
        syncing = false;
        return (Tile(key, title, description, combo), combo);
    }

    /// <summary>A text capability as a textbox that commits on Enter or when focus leaves.</summary>
    /// <param name="key">Stable focus key.</param>
    /// <param name="title">Row heading.</param>
    /// <param name="description">Supporting line.</param>
    /// <param name="text">Current text.</param>
    /// <param name="maximumLength">Maximum accepted length, or null.</param>
    /// <param name="enabled">Whether input is accepted.</param>
    /// <param name="onCommit">Invoked with the committed text.</param>
    /// <returns>The tile and its focus target.</returns>
    internal static (Border Row, Control Focus) Text(
        string key,
        string title,
        string description,
        string? text,
        int? maximumLength,
        bool enabled,
        Action<string> onCommit)
    {
        ArgumentNullException.ThrowIfNull(onCommit);
        var box = new TextBox
        {
            Text = text ?? string.Empty,
            IsEnabled = enabled,
            Focusable = enabled,
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (maximumLength is { } max and > 0)
        {
            box.MaxLength = max;
        }

        void Commit()
        {
            onCommit(box.Text ?? string.Empty);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();
        return (Tile(key, title, description, box), box);
    }

    private static string LabelFor(CapabilityChoice choice) =>
        choice.Display.Key == DisplayKey.Custom && !string.IsNullOrWhiteSpace(choice.Display.CustomLabel)
            ? choice.Display.CustomLabel!
            : choice.Value;

    private sealed record ChoiceItem(string Value, string Label);
}
