using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Base for the self-drawing, gamepad-driven overlay sub-views (tab builder,
/// card manager, artwork changer, launch wrappers, wake locks): the render-thunk
/// navigation stack, the shared row/label builders, and text entry. Each navigation
/// level rebuilds <see cref="ContentControl.Content"/>, and every interactive element is
/// a <see cref="Button"/> so D-pad navigation and A/B work with no extra focus plumbing.
/// <para><see cref="_navigationGeneration"/> is also the invalidation token for
/// asynchronous work: leaving a level bumps it, so a load that completes afterwards
/// discards its result instead of drawing over the level the user moved to.</para></summary>
public abstract class OverlaySubView : UserControl
{
    // Navigation: a stack of render thunks. Push goes deeper; Back pops.
    private protected readonly Stack<Action> _stack = new();
    private protected Action? _current;
    private protected int _navigationGeneration;

    // One-shot message shown at the top of the next rendered level, then consumed.
    private protected string? _notice;

    /// <summary>Raised when the user backs out of the top level (the overlay then
    /// returns to the Tools list).</summary>
    public event Action? CloseRequested;

    /// <summary>Short name used to prefix log lines from this sub-view.</summary>
    protected abstract string LogScope { get; }

    /// <summary>Asks the host to close this sub-view, for the rows that offer an explicit
    /// way out rather than waiting for a Back press.</summary>
    private protected void RequestClose() => CloseRequested?.Invoke();

    /// <summary>Handles a Back/B press: pops one level, or requests close at the top.
    /// Returns true when it consumed the press.</summary>
    public bool Back()
    {
        _navigationGeneration++;
        if (_stack.Count == 0)
        {
            CloseRequested?.Invoke();
            return true;
        }
        _current = _stack.Pop();
        _current();
        return true;
    }

    private protected void Navigate(Action render)
    {
        _navigationGeneration++;
        if (_current is not null)
        {
            _stack.Push(_current);
        }
        _current = render;
        render();
    }

    private protected void Replace(Action render)
    {
        _current = render;
        render();
    }

    private protected void PopIfAny()
    {
        if (_stack.Count > 0)
        {
            _stack.Pop();
        }
    }

    private protected async Task RunSafelyAsync(Task task, string operation)
    {
        try { await task; }
        catch (Exception ex) { Log.Error($"{LogScope} {operation} failed.", ex); }
    }

    /// <summary>Lists the Steam library, degrading to an empty list so a picker renders
    /// "no games" instead of failing the whole sub-view when Steam cannot answer.</summary>
    private protected async Task<IReadOnlyList<SteamCollections.AppInfo>> SafeGamesAsync()
    {
        try
        {
            return await SteamCollections.GetGamesAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogScope}: could not list games: {ex.Message}");
            return [];
        }
    }

    private protected void Toast(string message)
    {
        Log.Info($"{LogScope}: {message}");
        _notice = message;
        _current?.Invoke();
    }

    // ---- Shared builders ----

    private protected StackPanel NewStack(string heading)
    {
        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrEmpty(heading))
        {
            stack.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
            });
        }
        if (!string.IsNullOrEmpty(_notice))
        {
            stack.Children.Add(Caption(_notice));
            _notice = null;
        }
        return stack;
    }

    private protected void RenderMessage(string heading, string message)
    {
        var stack = NewStack(heading);
        stack.Children.Add(Caption(message));
        SetContent(stack);
    }

    private protected void RenderLoading(string title) => RenderMessage(title, "Loading from Steam…");

    private protected CardButton Row(string title, string desc, Geometry? icon, Action? onClick)
    {
        var button = new CardButton { Title = title, Description = desc, IconGeometry = icon };
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }
        return button;
    }

    private protected CardButton PrimaryRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("primary");
        return button;
    }

    private protected CardButton DangerRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("danger");
        return button;
    }

    private protected CardButton CycleRow(string label, string value, Action onClick)
        => Row(label, value, Icons.Restart, onClick).Also(b => b.TrailingText = "↔");

    private protected TextBlock Caption(string text) => new()
    {
        Text = text,
        Classes = { "caption" },
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(2, 0, 2, 4),
    };

    private protected TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Classes = { "eyebrow" },
        Margin = new Avalonia.Thickness(2, 6, 2, 2),
    };

    // No inner ScrollViewer: the overlay's ContentScroller owns scrolling and its
    // GotFocus→BringIntoView keeps the focused control (incl. keyboard keys) on screen.
    // A nested scroller would swallow that scroll-into-view.
    private protected virtual void SetContent(StackPanel stack)
    {
        Content = stack;
        FocusFirst(stack);
    }

    // A row laid out inside a panel (a Grid of columns, a WrapPanel of thumbnails) is
    // still the first thing the user should land on, so the search descends one level.
    private protected void FocusFirst(StackPanel stack) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var child in stack.Children)
        {
            if (child is Button { IsEffectivelyEnabled: true } b)
            {
                b.Focus(NavigationMethod.Directional);
                return;
            }
            if (child is Panel panel)
            {
                foreach (var nested in panel.Children)
                {
                    if (nested is Button nestedButton)
                    {
                        nestedButton.Focus(NavigationMethod.Directional);
                        return;
                    }
                }
            }
        }
    });

    // ---- Text entry ----

    private protected void EditText(string title, string current, int maxLen, Action<string> onAccept)
    {
        // Accept ordering matters: the rows show values straight off the model, so the mutation
        // has to land BEFORE anything re-renders or the user sees the old text. The keyboard
        // window pushes no navigation level (it is a peer, not a screen), so this re-renders the
        // current level itself instead of relying on a pop to do it.
        if (KeyboardService.Request(title, current, maxLen, v =>
        {
            onAccept(v ?? "");
            _current?.Invoke();
        }))
        {
            return;
        }

        // No keyboard surface means there is no way to type at all, so say so. The alternative —
        // a screen carrying a bare TextBox — is unusable here by design: GamepadNavigation skips
        // TextBoxes so the Windows touch keyboard cannot pop, which means focus never lands on it
        // and nothing types. See "Text entry in the panel is a press-to-edit ROW" in
        // docs\overlay-and-input.md.
        Log.Warn($"{LogScope}: cannot edit '{title}' — no keyboard surface is available.");
        Toast("Text entry needs the overlay keyboard, which is not available.");
    }
}
