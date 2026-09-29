using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
/// Picks which game a launch-wrapper action applies to, for the case where the
/// overlay could not tell what the user was looking at in Steam.
/// </summary>
/// <remarks>
/// The common path never reaches this view: opening the panel from a game's page
/// resolves that game directly. It exists for the panel opened from the library
/// root, and for a Steam that reports no current app.
/// </remarks>
public sealed class LaunchWrapperView : OverlaySubView
{
    private IReadOnlyList<SteamCollections.AppInfo> _games = [];

    /// <summary>Raised when the user chooses a game. The overlay then applies the
    /// pending action and leaves this sub-view.</summary>
    public event Action<SteamCollections.AppInfo>? Picked;

    /// <summary>Raised with the selected file, arguments, and game for a custom action.</summary>
    public event Action<string, string, SteamCollections.AppInfo>? CustomPicked;

    private string? _customPath;
    private string _customArguments = "";
    private SteamCollections.AppInfo? _customGame;

    /// <inheritdoc />
    protected override string LogScope => "Launch wrappers";

    /// <summary>Loads the library and shows the picker.</summary>
    /// <param name="heading">What the caller is about to do, as a title.</param>
    public void Open(string heading)
    {
        _customPath = null;
        _customGame = null;
        _stack.Clear();
        _current = null;
        _ = RunSafelyAsync(RenderGameListAsync(heading), "game list");
    }

    /// <summary>Asks for optional arguments before applying the custom action.</summary>
    /// <param name="path">The executable or script the user picked.</param>
    /// <param name="game">The game whose Steam page is on screen, or <c>null</c> to
    /// ask which game the action applies to.</param>
    public void OpenCustom(string path, SteamCollections.AppInfo? game = null)
    {
        _customPath = path;
        _customArguments = "";
        _customGame = game;
        _stack.Clear();
        _current = null;
        Navigate(RenderArgumentChoice);
    }

    private void RenderArgumentChoice()
    {
        var name = System.IO.Path.GetFileName(_customPath) ?? "";
        var stack = NewStack("Custom launch action");
        // Name the target when it is already known, so the flow that skips the
        // picker still says which game it is about to change.
        stack.Children.Add(Caption(_customGame is { } target ? $"{name} → {target.Name}" : name));
        stack.Children.Add(PrimaryRow("No arguments", "Continue without additional arguments",
            Icons.Play, ContinueCustomAction));
        stack.Children.Add(Row("Add arguments", "Enter command-line arguments", Icons.CopyDoc,
            () => EditText($"Arguments for {name}", _customArguments, 2048, value =>
            {
                _customArguments = value;
                Avalonia.Threading.Dispatcher.UIThread.Post(ContinueCustomAction);
            })));
        stack.Children.Add(Row("Cancel", "Do not change the game", Icons.ExitFullscreen,
            () => Back()));
        SetContent(stack);
    }

    // The game open in Steam is the target, the same rule the launch-fix buttons
    // follow. The picker below is only for the panel opened from the library root
    // and for a Steam that reported no current app.
    private void ContinueCustomAction()
    {
        _stack.Clear();
        _current = null;
        if (_customPath is { } path && _customGame is { } game)
        {
            CustomPicked?.Invoke(path, _customArguments, game);
            return;
        }
        _ = RunSafelyAsync(RenderGameListAsync("Replace launch action"), "game list");
    }

    private async Task RenderGameListAsync(string heading)
    {
        Navigate(() => RenderLoading(heading));
        var generation = _navigationGeneration;
        var games = await SafeGamesAsync();
        // The picker load is asynchronous, so a Back press (or a second open) while
        // Steam was answering must discard this result rather than redraw over it.
        if (generation != _navigationGeneration)
        {
            return;
        }

        _games = games;
        Replace(() => RenderGamePage(heading, 0));
    }

    // A full Steam library is one CardButton per title in a non-virtualizing
    // host, so it is paged the way LibraryTabsView's multi-select is: laying out
    // a 1000+ title account in one pass stalls the overlay's UI thread.
    private const int PageSize = 200;

    private void RenderGamePage(string heading, int page)
    {
        var pageCount = Math.Max(1, (_games.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pageCount - 1);
        var current = page;
        var stack = NewStack(heading);
        if (_games.Count == 0)
        {
            // GetGamesAsync answers empty for an unreachable Steam too, so this
            // says "could not read" rather than claiming the library is empty.
            stack.Children.Add(Caption("Couldn't read your library from Steam. Is it running?"));
        }
        else
        {
            stack.Children.Add(Caption(
                "Choose a game, or open one in Steam and use this panel from its page."));
            if (pageCount > 1)
            {
                stack.Children.Add(Caption($"Page {page + 1} of {pageCount} · {_games.Count} games"));
                if (page > 0)
                {
                    stack.Children.Add(Row("Previous page", "", Icons.Restart,
                        () => Replace(() => RenderGamePage(heading, current - 1))));
                }
            }
            foreach (var game in _games.Skip(page * PageSize).Take(PageSize))
            {
                var g = game;
                stack.Children.Add(Row(
                    g.Name,
                    g.Shortcut ? "Non-Steam shortcut" : "",
                    Icons.SteamLike,
                    () =>
                    {
                        if (_customPath is { } path)
                        {
                            CustomPicked?.Invoke(path, _customArguments, g);
                        }
                        else
                        {
                            Picked?.Invoke(g);
                        }
                    }));
            }
        }
        stack.Children.Add(SectionLabel(""));
        if (page + 1 < pageCount)
        {
            stack.Children.Add(Row("Next page",
                $"Games {((page + 1) * PageSize) + 1}–{Math.Min(_games.Count, (page + 2) * PageSize)}",
                Icons.Play, () => Replace(() => RenderGamePage(heading, current + 1))));
        }
        stack.Children.Add(Row("Back", "Cancel", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

}
