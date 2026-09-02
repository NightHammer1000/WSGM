using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace WSGM.Controls;

/// <summary>One tab of a <see cref="TabStrip"/>: a label, an optional vector icon and a
/// caller-defined integer tag (typically the index or id of the page the tab selects).
/// Items are immutable; assign a new list to <see cref="TabStrip.Tabs"/> to change the
/// tab set.</summary>
public sealed class TabStripItem
{
    /// <summary>Creates a tab descriptor.</summary>
    /// <param name="label">Text shown on the tab button.</param>
    /// <param name="iconGeometry">Optional vector icon rendered left of the label.</param>
    /// <param name="tag">Caller-defined value carried through to selection handling.</param>
    public TabStripItem(string label, Geometry? iconGeometry = null, int tag = 0)
    {
        Label = label;
        IconGeometry = iconGeometry;
        Tag = tag;
    }

    /// <summary>Gets the text shown on the tab button.</summary>
    public string Label { get; }

    /// <summary>Gets the vector icon rendered left of the label, or null for a text-only tab.</summary>
    public Geometry? IconGeometry { get; }

    /// <summary>Gets the caller-defined value carried through to selection handling.</summary>
    public int Tag { get; }
}

/// <summary>Event data for <see cref="TabStrip.SelectionChanged"/>.</summary>
public sealed class TabStripSelectionChangedEventArgs : EventArgs
{
    internal TabStripSelectionChangedEventArgs(int newIndex, TabStripItem? selectedItem)
    {
        NewIndex = newIndex;
        SelectedItem = selectedItem;
    }

    /// <summary>Gets the newly selected tab index.</summary>
    public int NewIndex { get; }

    /// <summary>Gets the newly selected tab item, or null when <see cref="NewIndex"/> is
    /// outside the current <see cref="TabStrip.Tabs"/> list.</summary>
    public TabStripItem? SelectedItem { get; }
}

/// <summary>The shared bumper tab bar used by the quick access overlay and the Settings
/// window: LB/RB hint chips at the ends and one flex-equal icon+label button per tab,
/// with an accent underline marking the active tab. The tabs are real focusable
/// <see cref="Button"/>s, so gamepad navigation (tab-order traversal + synthesized
/// Enter) and touch both work without special handling. Visuals live in
/// Themes\TabStripTheme.axaml.</summary>
public sealed class TabStrip : TemplatedControl
{
    /// <summary>Rendered size of a tab icon along its longer axis, in DIPs.</summary>
    private const double IconExtent = 15;

    /// <summary>Defines the Avalonia property holding the list of tabs. Assigning a new
    /// list rebuilds the tab buttons; mutating a previously assigned list is not observed.</summary>
    public static readonly StyledProperty<IReadOnlyList<TabStripItem>?> TabsProperty =
        AvaloniaProperty.Register<TabStrip, IReadOnlyList<TabStripItem>?>(nameof(Tabs));

    /// <summary>Defines the Avalonia property holding the selected tab index.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<TabStrip, int>(nameof(SelectedIndex));

    private readonly List<Button> _tabButtons = new();
    private Panel? _tabsHost;

    /// <summary>Gets or sets the list of tabs. Assigning a new list rebuilds the tab
    /// buttons; mutating a previously assigned list is not observed.</summary>
    public IReadOnlyList<TabStripItem>? Tabs
    {
        get => GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    /// <summary>Gets or sets the selected tab index. Setting it moves the accent
    /// underline and raises <see cref="SelectionChanged"/>.</summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Raised after <see cref="SelectedIndex"/> changes, whether by tab button
    /// activation or programmatically (e.g. shoulder-button cycling).</summary>
    public event EventHandler<TabStripSelectionChangedEventArgs>? SelectionChanged;

    /// <summary>Selects the next tab, wrapping from the last to the first. Intended for
    /// the RB shoulder button.</summary>
    public void SelectNext() => MoveSelection(1);

    /// <summary>Selects the previous tab, wrapping from the first to the last. Intended
    /// for the LB shoulder button.</summary>
    public void SelectPrevious() => MoveSelection(-1);

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _tabsHost = e.NameScope.Find<Panel>("PART_TabsHost");
        RebuildTabs();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TabsProperty)
        {
            RebuildTabs();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            ApplySelectionClasses();
            var newIndex = change.GetNewValue<int>();
            var tabs = Tabs;
            var selected = tabs is not null && newIndex >= 0 && newIndex < tabs.Count ? tabs[newIndex] : null;
            SelectionChanged?.Invoke(this, new TabStripSelectionChangedEventArgs(newIndex, selected));
        }
    }

    private void MoveSelection(int delta)
    {
        var count = Tabs?.Count ?? 0;
        if (count == 0)
        {
            return;
        }
        SelectedIndex = ((SelectedIndex + delta) % count + count) % count;
    }

    private void RebuildTabs()
    {
        if (_tabsHost is null)
        {
            return;
        }
        foreach (var button in _tabButtons)
        {
            button.Click -= OnTabButtonClick;
        }
        _tabButtons.Clear();
        _tabsHost.Children.Clear();

        var tabs = Tabs;
        if (tabs is null)
        {
            return;
        }
        for (var i = 0; i < tabs.Count; i++)
        {
            var button = CreateTabButton(tabs[i], i);
            _tabButtons.Add(button);
            _tabsHost.Children.Add(button);
        }
        ApplySelectionClasses();
    }

    private Button CreateTabButton(TabStripItem item, int index)
    {
        var face = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Bottom-heavy so the accent underline below never crowds the label.
            Margin = new Thickness(14, 8, 14, 11),
        };
        if (item.IconGeometry is not null)
        {
            // Only the dominant dimension is sized: Avalonia scales a Uniform
            // geometry and then aligns it TOP-LEFT inside the element box, so a
            // square 15x15 box around a wide-and-short glyph (the gamepad, the
            // panel) parks it against the top while a square glyph fills the box,
            // and the icons in one strip lose their shared baseline. Sizing the
            // long side only keeps the identical scale (uniform = side / longest
            // extent either way) and lets the box hug the drawn glyph, so the
            // centering below actually applies.
            var bounds = item.IconGeometry.Bounds;
            var icon = new Path
            {
                Data = item.IconGeometry,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (bounds.Width >= bounds.Height)
            {
                icon.Width = IconExtent;
            }
            else
            {
                icon.Height = IconExtent;
            }
            icon.Classes.Add("tab-strip-icon");
            face.Children.Add(icon);
        }
        face.Children.Add(new TextBlock
        {
            Text = item.Label,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Visibility and brush come from the .active style rules, never local values
        // (a local IsVisible would out-prioritize the style setters).
        var underline = new Border();
        underline.Classes.Add("tab-strip-underline");

        var root = new Panel();
        root.Children.Add(face);
        root.Children.Add(underline);

        var button = new Button { Content = root, Tag = index };
        button.Classes.Add("tab-strip-tab");
        button.Click += OnTabButtonClick;
        return button;
    }

    private void OnTabButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            SelectedIndex = index;
        }
    }

    private void ApplySelectionClasses()
    {
        for (var i = 0; i < _tabButtons.Count; i++)
        {
            var classes = _tabButtons[i].Classes;
            if (i == SelectedIndex)
            {
                if (!classes.Contains("active"))
                {
                    classes.Add("active");
                }
            }
            else
            {
                classes.Remove("active");
            }
        }
    }
}
