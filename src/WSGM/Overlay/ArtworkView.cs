using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The gamepad-driven SteamGridDB artwork changer, hosted as a Tools sub-view
/// of the overlay (like <see cref="LibraryTabsView"/>). Flow: target the game the user
/// is viewing (<see cref="SteamPageBridge.GetCurrentAppIdAsync"/>) or pick one from the
/// library → choose an artwork slot → browse SteamGridDB thumbnails → apply. Applying
/// grid/hero/logo/wide is a robust Steam API call (<see cref="SteamArtwork"/>); the
/// image bytes are fetched and base64-encoded in C#. Self-drawing (no XAML), every
/// interactive element a <see cref="Button"/> so D-pad/A/B work with no extra
/// plumbing.</summary>
public sealed class ArtworkView : OverlaySubView
{
    private static readonly System.Threading.SemaphoreSlim ThumbnailGate = new(4, 4);

    private long _appId;
    private string _appName = "";
    private string _apiKey = "";
    private IReadOnlyList<SteamCollections.AppInfo>? _games;

    // When > 0, artwork is sourced from this SteamGridDB game id (a manual name search)
    // instead of the target's Steam app id — needed for non-Steam shortcuts / ROMs and
    // when the auto-detected game is wrong. The art still APPLIES to _appId.
    private int _sgdbGameId;

    // Remembered shortcut → SGDB game associations, snapshotted from config on open
    // and updated on every match pick, so a shortcut is clarified once, not per visit.
    private readonly Dictionary<long, (int Id, string Name)> _sgdbLinks = new();

    /// <inheritdoc />
    protected override string LogScope => "Artwork";

    /// <summary>Loads config, detects the current game, and opens the picker.</summary>
    public void Open() => _ = RunSafelyAsync(OpenAsync(), "open");

    private async Task OpenAsync()
    {
        var generation = ++_navigationGeneration;
        _stack.Clear();
        _current = null;
        _sgdbGameId = 0;
        var config = await Task.Run(LibraryTabManager.LoadConfig);
        if (generation != _navigationGeneration) { return; }
        _apiKey = SteamGridDb.ResolveKey(config);
        _sgdbLinks.Clear();
        foreach (var link in config.SgdbLinks.Where(l => l.SgdbGameId > 0))
        {
            _sgdbLinks[link.AppId] = (link.SgdbGameId, link.Name);
        }
        if (string.IsNullOrEmpty(_apiKey))
        {
            Navigate(RenderNoKey);
            return;
        }

        Navigate(() => RenderMessage("Change Artwork", "Detecting the game you're viewing…"));
        // Navigate invalidates the previous level, so re-snapshot for the awaits below.
        generation = _navigationGeneration;
        try
        {
            _appId = await SteamPageBridge.GetCurrentAppIdAsync();
            if (generation != _navigationGeneration)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: current-app detect failed: {ex.Message}");
            _appId = 0;
        }

        if (_appId > 0)
        {
            _games ??= await SafeGamesAsync();
            if (generation != _navigationGeneration)
            {
                return;
            }
            _appName = NameFor(_appId);
            if (IsShortcutApp(_appId))
            {
                // Clarify the SteamGridDB source game UP FRONT: a shortcut's id has
                // no Steam page, and springing a text box on the user after they
                // pick an art type reads as a broken flow. A remembered match skips
                // the question entirely; otherwise auto-search by the shortcut's
                // name and let them pick — typing only on explicit request.
                if (_sgdbLinks.TryGetValue(_appId, out var link))
                {
                    _sgdbGameId = link.Id;
                    _appName = link.Name;
                    Replace(RenderAssetTypes);
                    return;
                }
                DoSgdbSearch(_appName);
                return;
            }
            Replace(RenderAssetTypes);
        }
        else
        {
            Replace(RenderGameList);
        }
    }

    /// <summary>Invalidates outstanding work when the host hides this view, so an
    /// abandoned grid stops downloading and its decoded bitmaps are dropped instead of
    /// landing on a detached <see cref="Image"/>.</summary>
    public void Close() => _navigationGeneration++;

    private void RenderNoKey()
    {
        var stack = NewStack("Change Artwork");
        stack.Children.Add(Caption("This needs a free SteamGridDB API key. Add yours in "
            + "Settings → Steam, then reopen this."));
        stack.Children.Add(Caption($"Get one at {SteamGridDb.KeyPageUrl}"));
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Close", "Back to Tools", Icons.ExitFullscreen,
            RequestClose));
        SetContent(stack);
    }

    // ---- Level: pick a game ----

    // A full Steam library is rendered one CardButton per title into a
    // non-virtualizing host, so it is paged exactly like LibraryTabsView's
    // multi-select: on a 1000+ title account a single pass stalls the UI thread
    // of a focused overlay that is muting the game.
    private const int GamePageSize = 200;

    private void RenderGameList() => _ = RunSafelyAsync(RenderGameListAsync(), "game list");

    private async Task RenderGameListAsync()
    {
        var generation = _navigationGeneration;
        RenderMessage("Change Artwork", "Loading your games…");
        var games = await SafeGamesAsync();
        if (generation != _navigationGeneration) { return; }
        _games = games;
        RenderGamePage(0);
    }

    private void RenderGamePage(int page)
    {
        var games = _games ?? Array.Empty<SteamCollections.AppInfo>();
        var pageCount = Math.Max(1, (games.Count + GamePageSize - 1) / GamePageSize);
        page = Math.Clamp(page, 0, pageCount - 1);
        var current = page;
        var stack = NewStack("Change Artwork");
        stack.Children.Add(Caption("Choose a game (or open one in Steam and reopen this)."));
        if (pageCount > 1)
        {
            stack.Children.Add(Caption($"Page {page + 1} of {pageCount} · {games.Count} games"));
            if (page > 0)
            {
                stack.Children.Add(Row("Previous page", "", Icons.Restart,
                    () => Replace(() => RenderGamePage(current - 1))));
            }
        }
        foreach (var game in games.Skip(page * GamePageSize).Take(GamePageSize))
        {
            var g = game;
            stack.Children.Add(Row(g.Name, g.Shortcut ? "Non-Steam shortcut" : "", Icons.SteamLike, () =>
            {
                _appId = g.AppId;
                _appName = g.Name;
                _sgdbGameId = 0;
                if (g.Shortcut)
                {
                    // Same up-front clarification as the auto-detected case, with the
                    // same remembered-match short-circuit.
                    if (_sgdbLinks.TryGetValue(g.AppId, out var link))
                    {
                        _sgdbGameId = link.Id;
                        _appName = link.Name;
                        Navigate(RenderAssetTypes);
                        return;
                    }
                    DoSgdbSearch(g.Name);
                }
                else
                {
                    Navigate(RenderAssetTypes);
                }
            }));
        }
        stack.Children.Add(SectionLabel(""));
        if (page + 1 < pageCount)
        {
            stack.Children.Add(Row("Next page",
                $"Games {((page + 1) * GamePageSize) + 1}–{Math.Min(games.Count, (page + 2) * GamePageSize)}",
                Icons.Play, () => Replace(() => RenderGamePage(current + 1))));
        }
        stack.Children.Add(Row("Back", "Close", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    // ---- Level: pick an artwork slot ----

    private static readonly (ArtworkAsset Asset, string Label, string Desc)[] Assets =
    [
        (ArtworkAsset.Grid, "Capsule (portrait)", "The vertical library cover (600×900)"),
        (ArtworkAsset.Hero, "Hero banner", "The wide banner on the game page"),
        (ArtworkAsset.Logo, "Logo", "The transparent title logo"),
        (ArtworkAsset.Wide, "Wide capsule", "The horizontal cover (460×215)"),
        (ArtworkAsset.Icon, "Icon", "Small icon (Steam games only)"),
    ];

    private void RenderAssetTypes()
    {
        var stack = NewStack("Change Artwork");
        stack.Children.Add(Caption(_sgdbGameId > 0
            ? $"Applying to: {_appName}  ·  art from your search"
            : IsShortcutApp(_appId)
                ? $"Shortcut: {_appName} — picking any art type first searches "
                    + "SteamGridDB by name (a shortcut's id has no Steam page)."
                : $"Game: {_appName}"));
        foreach (var (asset, label, desc) in Assets)
        {
            var a = asset;
            stack.Children.Add(Row(label, desc, Icons.Palette, () => OpenArtGrid(a)));
            // Current CUSTOM art for the slot, shown under its row (nothing when the
            // slot uses Steam's official art). Probed and decoded off-thread.
            var preview = new Image
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 64,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Avalonia.Thickness(10, 0, 0, 4),
                IsVisible = false,
            };
            stack.Children.Add(preview);
            _ = LoadCurrentArtAsync(preview, _appId, a, _navigationGeneration);
        }
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Wrong game? Search by name", "For ROMs, shortcuts, or misdetections",
            Icons.CopyDoc, RenderNameSearch));
        stack.Children.Add(Row("Change game", "Target a different installed game", Icons.SteamLike,
            () => Navigate(RenderGameList)));
        stack.Children.Add(Row("Back", "Close", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    // ---- Level: search SteamGridDB by name (non-Steam / misdetected games) ----

    private void RenderNameSearch()
    {
        // Prefer the separate keyboard window; fall back to an inline keyboard screen.
        if (KeyboardService.Request("Search SteamGridDB by name", _appName, 100,
                term => DoSgdbSearch(term)))
        {
            return;
        }
        Navigate(() =>
        {
            var stack = NewStack("Search SteamGridDB");
            stack.Children.Add(Caption("Type the game's name — used to find art (applies to "
                + $"{_appName})."));
            var box = new TextBox { Text = _appName, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            stack.Children.Add(box);
            var keyboard = new OnScreenKeyboard { Target = box };
            keyboard.Accepted += (_, _) => DoSgdbSearch(box.Text ?? "", inlineKeyboardLevel: true);
            stack.Children.Add(keyboard);
            stack.Children.Add(PrimaryRow("Search", "Find matching games", Icons.Play,
                () => DoSgdbSearch(box.Text ?? "", inlineKeyboardLevel: true)));
            stack.Children.Add(Row("Cancel", "Back", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    // inlineKeyboardLevel: true only when the search was started from the inline
    // keyboard SCREEN, which is a navigation level of its own. The peer keyboard
    // window (the normal path) pushes nothing, so popping for it as well ate the
    // level the user came from — the game list.
    private void DoSgdbSearch(string term, bool inlineKeyboardLevel = false)
        => _ = RunSafelyAsync(DoSgdbSearchAsync(term, inlineKeyboardLevel), "search");

    private async Task DoSgdbSearchAsync(string term, bool inlineKeyboardLevel)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }
        Navigate(() => RenderMessage("Search SteamGridDB", $"Searching for \"{term}\"…"));
        // Navigate invalidates the previous level, so snapshot after it.
        var generation = _navigationGeneration;
        IReadOnlyList<SgdbGame> matches;
        string? failure = null;
        try
        {
            matches = await SteamGridDb.SearchGamesAsync(term, _apiKey);
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: SGDB search failed: {ex.Message}");
            matches = Array.Empty<SgdbGame>();
            failure = ex.Message;
        }
        if (generation != _navigationGeneration) { return; }
        Replace(() =>
        {
            var stack = NewStack("Pick a Match");
            if (failure is not null)
            {
                stack.Children.Add(Caption(failure));
            }
            else if (matches.Count == 0)
            {
                stack.Children.Add(Caption("No matches on SteamGridDB. Try a different name."));
            }
            foreach (var game in matches.Take(30))
            {
                var g = game;
                stack.Children.Add(Row(g.Name, "", Icons.Palette, () =>
                {
                    _sgdbGameId = g.Id;
                    _appName = g.Name;
                    RememberSgdbLink(g.Id, g.Name);
                    // Drop exactly what this flow pushed — the search level, plus
                    // the inline keyboard screen when that fallback was used —
                    // and land back on the asset types.
                    PopIfAny();
                    if (inlineKeyboardLevel)
                    {
                        PopIfAny();
                    }
                    Replace(RenderAssetTypes);
                }));
            }
            stack.Children.Add(SectionLabel(""));
            // Typing is an explicit choice, never a surprise: the text entry only
            // opens from this row (or when nothing matched and the user wants it).
            stack.Children.Add(Row("Type a different name", "Search SteamGridDB manually",
                Icons.CopyDoc, RenderNameSearch));
            stack.Children.Add(Row("Back", "Return", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    // ---- Level: browse SteamGridDB art ----

    // Safety net (clarification normally happens on entry): a shortcut's generated
    // app id means nothing to SteamGridDB, so a lookup by it can only 404 — run the
    // auto-search instead; picking a match sets _sgdbGameId and lands back on the
    // asset types, after which grids load normally. Never a surprise text box.
    private void OpenArtGrid(ArtworkAsset asset)
    {
        if (_sgdbGameId == 0 && IsShortcutApp(_appId))
        {
            DoSgdbSearch(_appName);
            return;
        }
        _ = RunSafelyAsync(OpenArtGridAsync(asset), "art list");
    }

    private async Task OpenArtGridAsync(ArtworkAsset asset)
    {
        var sourceGameId = _sgdbGameId;
        var targetAppId = _appId;
        Navigate(() => RenderMessage(AssetLabel(asset), "Loading artwork from SteamGridDB…"));
        // Navigate invalidates the previous level, so snapshot after it.
        var generation = _navigationGeneration;
        IReadOnlyList<SgdbAsset> assets;
        string? failure = null;
        try
        {
            assets = sourceGameId > 0
                ? await SteamGridDb.GetAssetsForGameAsync(asset, sourceGameId, _apiKey)
                : await SteamGridDb.GetAssetsForSteamAppAsync(asset, targetAppId, _apiKey);
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: SGDB fetch failed: {ex.Message}");
            assets = Array.Empty<SgdbAsset>();
            failure = ex.Message;
        }
        if (generation != _navigationGeneration || targetAppId != _appId || sourceGameId != _sgdbGameId)
        {
            return;
        }
        Replace(() => RenderArtGrid(asset, assets, failure));
    }

    private void RenderArtGrid(ArtworkAsset asset, IReadOnlyList<SgdbAsset> assets, string? failure)
    {
        var stack = NewStack(AssetLabel(asset));
        stack.Children.Add(Caption($"{_appName} — pick one to apply, or reset."));
        stack.Children.Add(PrimaryRow("Reset to official", "Remove the custom art", Icons.Restart,
            () => Apply(asset, null)));

        if (failure is not null)
        {
            stack.Children.Add(Caption(failure));
        }
        else if (assets.Count == 0)
        {
            stack.Children.Add(Caption("No artwork found for this game/slot on SteamGridDB."));
        }
        else
        {
            var (w, h) = ThumbSize(asset);
            var grid = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var art in assets.Where(a => ImageHeader.IsWithinLimits(a.Width, a.Height)).Take(30))
            {
                grid.Children.Add(ThumbButton(art, w, h, () => Apply(asset, art)));
            }
            stack.Children.Add(grid);
        }

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Back", "Choose another slot", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    private Button ThumbButton(SgdbAsset art, double w, double h, Action onClick)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var button = new Button
        {
            Content = image,
            Width = w,
            Height = h,
            Padding = new Avalonia.Thickness(2),
            Margin = new Avalonia.Thickness(3),
        };
        button.Click += (_, _) => onClick();
        _ = LoadThumbAsync(image, string.IsNullOrEmpty(art.Thumb) ? art.Url : art.Thumb, _navigationGeneration);
        return button;
    }

    // Mirrors SteamGridDb.DownloadImageAsync's 16 MB safety limit for formats whose
    // headers ImageHeader cannot read (webp previews must keep working).
    private const long CurrentArtMaxBytes = 16 * 1024 * 1024;

    // Shows the slot's current custom-art file (if any) in the given placeholder.
    // Disk-only; failures just leave the preview hidden.
    private async Task LoadCurrentArtAsync(Image image, long appId, ArtworkAsset asset, int generation)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                var path = SteamArtwork.FindCustomArtFile(appId, asset);
                if (path is null)
                {
                    return null;
                }
                // Grid files are written by Steam and third-party art tools, so they are
                // untrusted: refuse hostile declared dimensions for the formats ImageHeader
                // parses (PNG/JPEG/BMP), and byte-cap the ones it cannot (webp) so a tiny
                // file cannot commit an unbounded decode allocation.
                if (ImageHeader.TryReadSize(path, out var artWidth, out var artHeight))
                {
                    if (!ImageHeader.IsWithinLimits(artWidth, artHeight))
                    {
                        Log.Warn($"Artwork: current-art preview skipped, image declares "
                            + $"{artWidth}x{artHeight} px: {path}");
                        return null;
                    }
                }
                else if (new FileInfo(path).Length > CurrentArtMaxBytes)
                {
                    Log.Warn($"Artwork: current-art preview skipped, file exceeds "
                        + $"{CurrentArtMaxBytes / (1024 * 1024)} MB cap: {path}");
                    return null;
                }
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 200);
            });
            if (bitmap is null)
            {
                return;
            }
            if (generation != _navigationGeneration)
            {
                bitmap.Dispose();
                return;
            }
            (image.Source as IDisposable)?.Dispose();
            image.Source = bitmap;
            image.IsVisible = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: current-art preview failed: {ex.Message}");
        }
    }

    private async Task LoadThumbAsync(Image image, string url, int generation)
    {
        await ThumbnailGate.WaitAsync();
        try
        {
            // Checked before the download, not only after it: a queued thumbnail
            // whose screen the user already left is not worth fetching at all.
            if (generation != _navigationGeneration)
            {
                return;
            }
            var bytes = await SteamGridDb.DownloadImageAsync(url);
            if (generation != _navigationGeneration || bytes is null || bytes.Length == 0)
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(bytes);
                    var bitmap = Bitmap.DecodeToWidth(stream, 300);
                    if (generation == _navigationGeneration)
                    {
                        (image.Source as IDisposable)?.Dispose();
                        image.Source = bitmap;
                    }
                    else
                    {
                        bitmap.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Artwork: thumb decode failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: thumbnail load failed: {ex.Message}");
        }
        finally
        {
            ThumbnailGate.Release();
        }
    }

    // ---- Apply / reset ----

    private void Apply(ArtworkAsset asset, SgdbAsset? art) => _ = RunSafelyAsync(ApplyAsync(asset, art), "apply");

    private async Task ApplyAsync(ArtworkAsset asset, SgdbAsset? art)
    {
        var targetAppId = _appId;
        Navigate(() => RenderMessage(AssetLabel(asset),
            art is null ? "Resetting to official art…" : "Applying artwork…"));
        // Navigate invalidates the previous level, so snapshot after it.
        var generation = _navigationGeneration;

        ArtworkResult result;
        try
        {
            if (art is null)
            {
                result = await SteamArtwork.ClearAsync(targetAppId, asset);
            }
            else
            {
                var bytes = await SteamGridDb.DownloadImageAsync(art.Url);
                if (bytes is null || bytes.Length == 0)
                {
                    result = new ArtworkResult(false, "Could not download the image.");
                }
                else
                {
                    result = await SteamArtwork.ApplyAsync(targetAppId, asset, bytes, art.Extension);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Artwork apply failed.", ex);
            result = new ArtworkResult(false, "Something went wrong — see the log.");
        }

        if (generation != _navigationGeneration || targetAppId != _appId)
        {
            return;
        }
        // No continue screen: land straight back on the changer's overview with the
        // outcome as a one-line notice, ready for the next change or Back to leave.
        _notice = result.Detail;
        _stack.Clear();
        Replace(RenderAssetTypes);
    }

    private string NameFor(long appId)
        => _games?.FirstOrDefault(g => g.AppId == appId)?.Name ?? $"App {appId}";

    // Prefer Steam's own flag (live-verified BIsShortcut in the games list); the
    // numeric check covers an id missing from the list — shortcut ids carry the
    // high bit (>= 2^31), real store appids never do.
    private bool IsShortcutApp(long appId)
        => _games?.FirstOrDefault(g => g.AppId == appId)?.Shortcut ?? appId >= 0x80000000L;

    // Persist the association only for shortcuts: a normal game's id already IS its
    // SGDB lookup key, and pinning a manual-search override for it could silently
    // outlive a one-off misdetection workaround.
    private void RememberSgdbLink(int sgdbGameId, string name)
    {
        if (!IsShortcutApp(_appId))
        {
            return;
        }
        _sgdbLinks[_appId] = (sgdbGameId, name);
        var appId = _appId;
        // Observed, not fire-and-forget: the write takes the cross-process config
        // lock and can fail, and a dropped association silently asks the user to
        // search again on the next visit with nothing in the log to explain it.
        _ = RunSafelyAsync(LibraryTabManager.MutateConfigAsync<object?>(config =>
        {
            config.SgdbLinks.RemoveAll(l => l.AppId == appId);
            config.SgdbLinks.Add(new SgdbLinkConfig
            {
                AppId = appId,
                SgdbGameId = sgdbGameId,
                Name = name,
            });
            return null;
        }), "remember SGDB link");
    }

    private static string AssetLabel(ArtworkAsset asset)
        => Assets.FirstOrDefault(a => a.Asset == asset).Label ?? asset.ToString();

    private static (double W, double H) ThumbSize(ArtworkAsset asset) => asset switch
    {
        ArtworkAsset.Grid => (120, 180),
        ArtworkAsset.Hero => (260, 96),
        ArtworkAsset.Logo => (160, 96),
        ArtworkAsset.Wide => (200, 94),
        ArtworkAsset.Icon => (80, 80),
        _ => (120, 180),
    };

    // Replacing a level drops its decoded bitmaps immediately rather than waiting for a
    // collection: an artwork grid holds full-size thumbnails, and the overlay is resident.
    private protected override void SetContent(StackPanel stack)
    {
        if (Content is Control previous)
        {
            DisposeImages(previous);
        }
        base.SetContent(stack);
    }

    private static void DisposeImages(Control root)
    {
        if (root is Image image)
        {
            (image.Source as IDisposable)?.Dispose();
            image.Source = null;
        }
        if (root is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                DisposeImages(child);
            }
        }
        else if (root is ContentControl { Content: Control child })
        {
            DisposeImages(child);
        }
    }

}
