using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace WSGM.Controls;

/// <summary>An on-screen keyboard drawn by WSGM itself.
///
/// Windows' own touch keyboard is not an option in game mode. It is rendered by
/// TextInputHost, part of the same immersive-shell AppX family as `ms-settings`,
/// and that cannot activate with no Explorer in the session — the same wall the
/// settings-activation work already hit. Starting TabTip.exe does nothing either:
/// on Windows 11 it is already running, so a second launch just exits.
///
/// So the only text entry that can be relied on for a Wi-Fi password or a
/// Bluetooth PIN is one this process draws. Being ours has a second benefit: the
/// keys are ordinary buttons, so controller navigation works on them for free.
/// </summary>
public sealed class OnScreenKeyboard : Decorator
{
    /// <summary>Defines the <see cref="Target"/> property.</summary>
    public static readonly StyledProperty<TextBox?> TargetProperty =
        AvaloniaProperty.Register<OnScreenKeyboard, TextBox?>(nameof(Target));

    /// <summary>Raised when the user presses the accept key.</summary>
    public event EventHandler? Accepted;

    /// <summary>Raised when the user asks the owning window to paste clipboard text.</summary>
    public event EventHandler? PasteRequested;

    private readonly Panel _root = new StackPanel { Spacing = 4 };
    private bool _shift;

    /// <summary>Which key layer is showing: 0 letters, 1 symbols, 2 the rest of
    /// the symbols. Three layers because a WPA passphrase may contain any
    /// printable ASCII character and this keyboard is the only way to type one
    /// in game mode — a character it cannot reach is a network that cannot be
    /// joined.</summary>
    private int _layer;

    private const int LayerLetters = 0;
    private const int LayerSymbols = 1;
    private const int LayerMoreSymbols = 2;

    /// <summary>Creates the keyboard.</summary>
    public OnScreenKeyboard()
    {
        Child = _root;
        Build();
    }

    /// <summary>Gets or sets the text box that receives the keystrokes.</summary>
    public TextBox? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    // Rows are the standard phone layout rather than a full PC one: a password
    // field does not need function keys, and wider keys are what a thumb needs.
    private static readonly string[] LettersLower = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];
    private static readonly string[] LettersUpper = ["QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM"];
    private static readonly string[] Symbols = ["1234567890", "-/:;()$&@\"", ".,?!'#%*+="];

    /// <summary>The printable ASCII the first symbol page has no room for.
    /// Together with the letters, digits, space and <see cref="Symbols"/> this
    /// completes the set a WPA passphrase is allowed to contain.</summary>
    private static readonly string[] MoreSymbols = ["[]{}<>", "\\|~`^_"];

    private void Build()
    {
        _root.Children.Clear();
        var rows = _layer switch
        {
            LayerSymbols => Symbols,
            LayerMoreSymbols => MoreSymbols,
            _ => _shift ? LettersUpper : LettersLower,
        };
        foreach (var row in rows)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            foreach (var key in row)
            {
                panel.Children.Add(KeyButton(key.ToString(), () => Insert(key.ToString())));
            }
            _root.Children.Add(panel);
        }

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // One cycling key rather than two: the label names the layer it leads
        // to, so every character is reachable without a second modifier the
        // gamepad cursor would have to hunt for.
        controls.Children.Add(KeyButton(
            _layer switch
            {
                LayerSymbols => "#+=",
                LayerMoreSymbols => "abc",
                _ => "?123",
            },
            () =>
            {
                _layer = (_layer + 1) % 3;
                Build();
                FocusControl(_layer == LayerSymbols ? "#+=" : _layer == LayerMoreSymbols ? "abc" : "?123");
            },
            width: 58));
        controls.Children.Add(KeyButton("Shift", () =>
        {
            _shift = !_shift;
            _layer = LayerLetters;
            Build();
            FocusControl("Shift");
        }, width: 62));
        controls.Children.Add(KeyButton("Space", () => Insert(" "), width: 96));
        controls.Children.Add(KeyButton("Paste", () => PasteRequested?.Invoke(this, EventArgs.Empty), width: 62));
        controls.Children.Add(KeyButton("Back", Backspace, width: 58));
        controls.Children.Add(KeyButton("Enter", () => Accepted?.Invoke(this, EventArgs.Empty),
            width: 62));
        _root.Children.Add(controls);
    }

    private void FocusControl(string label) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var row in _root.Children.OfType<Panel>())
        {
            foreach (var button in row.Children.OfType<Button>())
            {
                if (string.Equals(button.Content?.ToString(), label, StringComparison.Ordinal))
                {
                    button.Focus();
                    return;
                }
            }
        }
    });

    private Button KeyButton(string label, Action action, double width = 44)
    {
        var button = new Button
        {
            Content = label,
            Width = width,
            Height = 44,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            // Constant border, no adorner: the repo's focus discipline, so a
            // controller cursor never changes a key's size as it moves.
            BorderThickness = new Thickness(2),
            FocusAdorner = null,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void Insert(string text)
    {
        InsertExternalText(text);
        if (_shift && _layer == LayerLetters)
        {
            _shift = false;
            Build();
            FocusControl(text == " " ? "Space" : text.ToLowerInvariant());
        }
    }

    /// <summary>Inserts pasted text at the selection or caret, respecting the target limit.</summary>
    /// <param name="text">Clipboard text to insert.</param>
    public void InsertExternalText(string text)
    {
        if (Target is not { } target)
        {
            return;
        }
        var current = target.Text ?? "";
        // Respect the caret rather than always appending: a mistyped character
        // in the middle of a long password is otherwise unfixable.
        var start = Math.Clamp(Math.Min(target.SelectionStart, target.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(target.SelectionStart, target.SelectionEnd), start, current.Length);
        var available = target.MaxLength > 0
            ? Math.Max(0, target.MaxLength - (current.Length - (end - start)))
            : text.Length;
        var inserted = text[..Math.Min(text.Length, available)];
        target.Text = current[..start] + inserted + current[end..];
        target.CaretIndex = start + inserted.Length;
        target.SelectionStart = target.CaretIndex;
        target.SelectionEnd = target.CaretIndex;
    }

    internal void Backspace()
    {
        if (Target is not { } target)
        {
            return;
        }
        var current = target.Text ?? "";
        var start = Math.Clamp(Math.Min(target.SelectionStart, target.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(target.SelectionStart, target.SelectionEnd), start, current.Length);
        if (end > start)
        {
            target.Text = current[..start] + current[end..];
            target.CaretIndex = start;
            target.SelectionStart = start;
            target.SelectionEnd = start;
            return;
        }
        var caret = Math.Clamp(target.CaretIndex, 0, current.Length);
        if (caret == 0 || current.Length == 0)
        {
            return;
        }
        target.Text = current[..(caret - 1)] + current[caret..];
        target.CaretIndex = caret - 1;
        target.SelectionStart = target.CaretIndex;
        target.SelectionEnd = target.CaretIndex;
    }

    /// <summary>Resets to the lower-case letter layer.</summary>
    public void Reset()
    {
        if (_shift || _layer != LayerLetters)
        {
            _shift = false;
            _layer = LayerLetters;
            Build();
        }
    }

    /// <summary>The key rows, exposed so a test can assert the layout covers
    /// what a WPA passphrase is allowed to contain. The space bar is a control
    /// key rather than a row, so it is included here explicitly.</summary>
    internal static IReadOnlyList<string> AllKeys() =>
        [.. LettersLower, .. LettersUpper, .. Symbols, .. MoreSymbols, " "];
}
