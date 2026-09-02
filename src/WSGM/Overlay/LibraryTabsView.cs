using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The gamepad-driven custom-tab builder, hosted as a Tools sub-view of the
/// overlay (the <c>PanelFormat</c> idiom; scaffolding in <see cref="OverlaySubView"/>).
/// All Steam contact goes through <see cref="SteamCollections"/> /
/// <see cref="LibraryTabManager"/>; a tab is injected into Steam's own tab strip
/// by <see cref="SteamLibraryTabs"/> and its membership is a fake in-memory
/// collection — no Steam collection is ever created, and user/SRM ones are never
/// touched. Card libraries are managed by the separate
/// <see cref="CardManagerView"/>.</summary>
public sealed class LibraryTabsView : OverlaySubView
{
    private LibraryTabManager _manager = new();
    private AppConfig _config = new();

    // Lazily-loaded, cached Steam data for the pickers.
    private IReadOnlyList<SteamCollections.AppInfo>? _games;
    private IReadOnlyList<SteamCollections.TagInfo>? _tags;
    private IReadOnlyList<SteamCollectionInfo>? _collections;
    private HashSet<string> _openedTabIds = new(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override string LogScope => "Library tabs";

    /// <summary>Loads config and renders the root tab list. Called by the overlay when
    /// the sub-view opens.</summary>
    /// <param name="manager">The shared library-tab manager.</param>
    public void Open(LibraryTabManager manager)
    {
        _manager = manager;
        _stack.Clear();
        _current = null;
        _games = null;
        _tags = null;
        _collections = null;
        var generation = ++_navigationGeneration;
        RenderLoading("Library Tabs");
        _ = RunSafelyAsync(LoadAndRenderAsync(generation), "open");
    }

    private async Task LoadAndRenderAsync(int generation)
    {
        var config = await Task.Run(LibraryTabManager.LoadConfig);
        if (generation != _navigationGeneration) { return; }
        _config = config;
        _openedTabIds = _config.CustomTabs.Select(static tab => tab.Id)
            .ToHashSet(StringComparer.Ordinal);
        Navigate(RenderTabList);
    }

    // ---- Level: tab list ----

    private void RenderTabList()
    {
        var stack = NewStack("Library Tabs");
        stack.Children.Add(Caption("Tabs appear in Steam's library and update automatically as "
            + "you make changes."));

        stack.Children.Add(PrimaryRow("New Tab", "Build a tab from filters", Icons.FolderPlus,
            () => OpenTabEditor(null)));
        stack.Children.Add(Row("Tab Order & Steam Tabs", "Reorder the strip, hide Steam's own tabs",
            Icons.Reorder, OpenTabOrder));

        if (_config.CustomTabs.Count > 0)
        {
            stack.Children.Add(SectionLabel("YOUR TABS"));
            foreach (var tab in _config.CustomTabs.OrderBy(t => t.Position).ToList())
            {
                var t = tab;
                var state = t.Enabled ? $"{t.FilterTree?.Children.Count ?? 0} filters" : "disabled";
                stack.Children.Add(Row(string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name,
                    state, Icons.Wrench, () => OpenTabEditor(t)));
            }
        }

        SetContent(stack);
    }

    // ---- Level: tab order & native tabs ----

    private List<LibraryTabManager.TabOrderEntry> _orderEntries = [];
    private CancellationTokenSource? _orderPushDebounce;
    private Task _orderPersistChain = Task.CompletedTask;

    private void OpenTabOrder()
    {
        _orderEntries = LibraryTabManager.BuildTabOrder(_config);
        Navigate(RenderTabOrder);
    }

    private void RenderTabOrder()
    {
        var stack = NewStack("Tab Order");
        stack.Children.Add(Caption("Top to bottom here is left to right in Steam. Steam's own "
            + "tabs can be hidden; WSGM tabs disappear when disabled in their editors."));
        foreach (var entry in _orderEntries)
        {
            var e = entry;
            var kind = e.IsNative ? "Steam tab"
                : e.Key.StartsWith("wsgm-card-", StringComparison.Ordinal) ? "Card tab" : "Custom tab";
            stack.Children.Add(Row(e.Title, e.Hidden ? kind + " · hidden" : kind, Icons.Reorder,
                () => Navigate(() => RenderTabOrderActions(e.Key))));
        }
        SetContent(stack);
    }

    private void RenderTabOrderActions(string key, string? focusTitle = null)
    {
        var index = _orderEntries.FindIndex(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (index < 0)
        {
            Back();
            return;
        }
        var entry = _orderEntries[index];
        var stack = NewStack(entry.Title);
        stack.Children.Add(Caption($"Position {index + 1} of {_orderEntries.Count}"
            + (entry.Hidden ? " · hidden" : "")));
        stack.Children.Add(Row("Move up", "Earlier in the strip", Icons.ArrowUp,
            index > 0 ? () => MoveOrderEntry(key, -1) : null));
        stack.Children.Add(Row("Move down", "Later in the strip", Icons.ArrowDown,
            index + 1 < _orderEntries.Count ? () => MoveOrderEntry(key, +1) : null));
        if (entry.IsNative)
        {
            stack.Children.Add(entry.Hidden
                ? PrimaryRow("Show tab", "Put this Steam tab back in the strip", Icons.Play,
                    () => SetNativeHidden(key, false))
                : DangerRow("Hide tab", "Remove this Steam tab from the strip", Icons.Close,
                    () => SetNativeHidden(key, true)));
        }
        stack.Children.Add(Row("Done", "Back to the list", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
        if (focusTitle is not null)
        {
            // Posted after SetContent's own first-row focus post, so it wins — repeated
            // move presses keep the finger on the same action instead of jumping to the
            // top row every re-render.
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var child in stack.Children)
                {
                    if (child is CardButton { IsEffectivelyEnabled: true } button
                        && button.Title == focusTitle)
                    {
                        button.Focus(NavigationMethod.Directional);
                        return;
                    }
                }
            });
        }
    }

    private void MoveOrderEntry(string key, int delta)
    {
        var index = _orderEntries.FindIndex(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _orderEntries.Count)
        {
            return;
        }
        (_orderEntries[index], _orderEntries[target]) = (_orderEntries[target], _orderEntries[index]);
        PersistTabOrder();
        Replace(() => RenderTabOrderActions(key, delta < 0 ? "Move up" : "Move down"));
    }

    private void SetNativeHidden(string key, bool hidden)
    {
        var index = _orderEntries.FindIndex(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }
        _orderEntries[index] = _orderEntries[index] with { Hidden = hidden };
        PersistTabOrder();
        Replace(() => RenderTabOrderActions(key, hidden ? "Show tab" : "Hide tab"));
    }

    private void PersistTabOrder()
    {
        var order = _orderEntries.Select(e => e.Key).ToList();
        var hidden = _orderEntries.Where(e => e is { IsNative: true, Hidden: true })
            .Select(e => e.Key).ToList();
        _config.LibraryTabOrder = order;
        _config.HiddenNativeTabs = hidden;
        // Chained, not fired independently: rapid moves must commit in press order or a
        // slow earlier write could clobber a newer one.
        _orderPersistChain = _orderPersistChain
            .ContinueWith(_ => PersistTabOrderAsync(order, hidden), TaskScheduler.Default)
            .Unwrap();
        _ = RunSafelyAsync(_orderPersistChain, "order save");
    }

    private async Task PersistTabOrderAsync(List<string> order, List<string> hidden)
    {
        await LibraryTabManager.MutateConfigAsync<object?>(cfg =>
        {
            cfg.LibraryTabOrder = order;
            cfg.HiddenNativeTabs = hidden;
            return null;
        });
        ScheduleOrderPush(order, hidden);
    }

    // Debounced live push into the running Steam: cheap (no filter re-evaluation), so
    // the strip follows while the user is still tapping move. Falls back to a full
    // sync when the resident script is not installed in this Steam session yet.
    private void ScheduleOrderPush(List<string> order, List<string> hidden)
    {
        _orderPushDebounce?.Cancel();
        var cts = _orderPushDebounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, cts.Token);
                if (!await SteamLibraryTabs.PushOrderAsync(order, hidden, cts.Token))
                {
                    await SyncQuietly();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn($"Library tab order push failed: {ex.Message}");
            }
        });
    }

    // ---- Level: tab editor ----

    private CustomTabConfig? _editingOriginal;
    private CustomTabConfig _editing = new();

    private void OpenTabEditor(CustomTabConfig? existing)
    {
        _editingOriginal = existing;
        _editing = existing is null ? new CustomTabConfig() : Clone(existing);
        _editing.FilterTree ??= new FilterNode { Kind = FilterKind.Merge };
        Navigate(RenderTabEditor);
    }

    private void RenderTabEditor()
    {
        var stack = NewStack(_editingOriginal is null ? "New Tab" : "Edit Tab");

        stack.Children.Add(Row("Name", string.IsNullOrWhiteSpace(_editing.Name) ? "(required)" : _editing.Name,
            Icons.CopyDoc, () => EditText("Tab name", _editing.Name, 40, v =>
            {
                _editing.Name = v.Trim();
            })));

        stack.Children.Add(CycleRow("Match", _editing.FilterTree!.Mode == FilterMode.And
            ? "All filters (AND)" : "Any filter (OR)", () =>
        {
            _editing.FilterTree!.Mode = _editing.FilterTree.Mode == FilterMode.And
                ? FilterMode.Or : FilterMode.And;
            Replace(RenderTabEditor);
        }));

        stack.Children.Add(CycleRow("Include", CategoriesLabel((LibraryFilter.Categories)_editing.Categories),
            () =>
        {
            _editing.Categories = NextCategories(_editing.Categories);
            Replace(RenderTabEditor);
        }));

        stack.Children.Add(SectionLabel("FILTERS"));
        var filters = _editing.FilterTree!.Children;
        if (filters.Count == 0)
        {
            stack.Children.Add(Caption("No filters yet — add one below."));
        }
        else
        {
            foreach (var node in filters.ToList())
            {
                var n = node;
                var valid = LibraryFilter.IsValid(n) ? "" : "  ⚠ incomplete";
                stack.Children.Add(Row(DescribeFilter(n), FilterKindLabel(n.Kind) + valid,
                    Icons.Wrench, () => OpenFilterEditor(n)));
            }
        }
        stack.Children.Add(Row("Add filter", "Choose a filter type", Icons.FolderPlus,
            () => OpenFilterPicker(null)));

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(PrimaryRow("Save tab", "Materialize as a Steam tab", Icons.Play, SaveTab));
        if (_editingOriginal is not null)
        {
            stack.Children.Add(DangerRow("Delete tab", "Remove this tab from Steam's library",
                Icons.Close, DeleteTab));
        }
        stack.Children.Add(Row("Cancel", "Discard changes", Icons.ExitFullscreen, () => Back()));

        SetContent(stack);
    }

    private void SaveTab() => _ = RunSafelyAsync(SaveTabAsync(), "save");

    private async Task SaveTabAsync()
    {
        if (string.IsNullOrWhiteSpace(_editing.Name))
        {
            Toast("A tab needs a name.");
            return;
        }
        if (_editing.FilterTree!.Children.Count == 0 || !_editing.FilterTree.Children.All(LibraryFilter.IsValid))
        {
            Toast("Finish every filter first (no ⚠).");
            return;
        }

        if (_editingOriginal is null)
        {
            _editing.Position = _config.CustomTabs.Count == 0
                ? 0 : _config.CustomTabs.Max(t => t.Position) + 1;
            _config.CustomTabs.Add(_editing);
        }
        else
        {
            var index = _config.CustomTabs.IndexOf(_editingOriginal);
            _editing.Position = _editingOriginal.Position;
            if (index >= 0)
            {
                _config.CustomTabs[index] = _editing;
            }
        }

        if (!await TryPersistTabsAsync("save"))
        {
            return;
        }
        // Drop back to the list, then materialize in the background.
        _stack.Clear();
        Replace(RenderTabList);
        _ = SyncQuietly();
    }

    private void DeleteTab() => _ = RunSafelyAsync(DeleteTabAsync(), "delete");

    private async Task DeleteTabAsync()
    {
        if (_editingOriginal is null)
        {
            return;
        }
        _config.CustomTabs.Remove(_editingOriginal);
        if (!await TryPersistTabsAsync("delete"))
        {
            return;
        }
        _stack.Clear();
        Replace(RenderTabList);
        _ = SyncQuietly();
    }

    private Task PersistTabsAsync()
    {
        var tabs = _config.CustomTabs.Select(Clone).ToList();
        var baseline = _openedTabIds.ToHashSet(StringComparer.Ordinal);
        return LibraryTabManager.MutateConfigAsync<object?>(cfg =>
        {
            var wanted = tabs.Select(static tab => tab.Id).ToHashSet(StringComparer.Ordinal);
            cfg.CustomTabs.RemoveAll(tab => baseline.Contains(tab.Id) && !wanted.Contains(tab.Id));
            foreach (var tab in tabs)
            {
                var index = cfg.CustomTabs.FindIndex(existing => existing.Id == tab.Id);
                if (index >= 0)
                {
                    cfg.CustomTabs[index] = tab;
                }
                else
                {
                    cfg.CustomTabs.Add(tab);
                }
            }
            return null;
        });
    }

    private async Task<bool> TryPersistTabsAsync(string operation)
    {
        try
        {
            await PersistTabsAsync();
            _openedTabIds = _config.CustomTabs.Select(static tab => tab.Id)
                .ToHashSet(StringComparer.Ordinal);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Library tab {operation} failed: {ex.Message}");
            _config = await Task.Run(LibraryTabManager.LoadConfig);
            Toast($"Could not {operation} the tab. Try again.");
            _stack.Clear();
            Replace(RenderTabList);
            return false;
        }
    }

    private async Task SyncQuietly()
    {
        try
        {
            var summary = await _manager.SyncAllAsync();
            Log.Info($"Library tabs (builder): {summary}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Library-tab sync failed: {ex.Message}");
        }
    }

    // ---- Level: filter type picker ----

    private static readonly (FilterKind Kind, string Label, string Desc)[] FilterKinds =
    [
        (FilterKind.Tag, "Tag / Genre", "Games with a store tag"),
        (FilterKind.Installed, "Installed", "Installed or not"),
        (FilterKind.Collection, "Collection", "In a Steam collection"),
        (FilterKind.Regex, "Title", "Title matches a pattern"),
        (FilterKind.SdCard, "SD Card", "Installed on a card"),
        (FilterKind.TimePlayed, "Playtime", "Above/below hours played"),
        (FilterKind.SizeOnDisk, "Size", "Above/below install size"),
        (FilterKind.ReviewScore, "Review score", "Above/below a score"),
        (FilterKind.ReleaseDate, "Release date", "Before/after a date"),
        (FilterKind.LastPlayed, "Last played", "Before/after a date"),
        (FilterKind.Platform, "Platform", "Steam or non-Steam"),
        (FilterKind.Whitelist, "Whitelist", "Only these games"),
        (FilterKind.Blacklist, "Blacklist", "Exclude these games"),
        (FilterKind.Merge, "Merge group", "Nested AND/OR of filters"),
    ];

    private FilterNode? _replacingFilter;

    private void OpenFilterPicker(FilterNode? replacing)
    {
        _replacingFilter = replacing;
        Navigate(RenderFilterPicker);
    }

    private void RenderFilterPicker()
    {
        var stack = NewStack("Add Filter");
        foreach (var (kind, label, desc) in FilterKinds)
        {
            var k = kind;
            stack.Children.Add(Row(label, desc, Icons.Wrench, () => PickFilterKind(k)));
        }
        SetContent(stack);
    }

    private void PickFilterKind(FilterKind kind)
    {
        var node = new FilterNode { Kind = kind };
        if (kind == FilterKind.Merge)
        {
            node.Children.Add(new FilterNode { Kind = FilterKind.Installed });
        }
        if (_replacingFilter is not null)
        {
            var list = _editing.FilterTree!.Children;
            var idx = list.IndexOf(_replacingFilter);
            if (idx >= 0)
            {
                list[idx] = node;
            }
            PopIfAny();
        }
        else
        {
            _editing.FilterTree!.Children.Add(node);
        }
        // Replace the picker level with the editor for the new node.
        _current = () => RenderFilterEditor(node);
        RenderFilterEditor(node);
    }

    // ---- Level: filter editor ----

    private void OpenFilterEditor(FilterNode node) => Navigate(() => RenderFilterEditor(node));

    private void RenderFilterEditor(FilterNode node)
    {
        var stack = NewStack(FilterKindLabel(node.Kind));

        BuildFilterParams(stack, node);

        if (LibraryFilter.CanInvert(node.Kind))
        {
            stack.Children.Add(CycleRow("Result", node.Inverted ? "Inverted (NOT)" : "Normal", () =>
            {
                node.Inverted = !node.Inverted;
                Replace(() => RenderFilterEditor(node));
            }));
        }

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Change type", "Pick a different filter", Icons.Wrench,
            () => OpenFilterPicker(node)));
        stack.Children.Add(DangerRow("Remove filter", "Delete this filter", Icons.Close, () =>
        {
            RemoveNode(_editing.FilterTree!, node);
            Back();
        }));
        stack.Children.Add(PrimaryRow("Done", "Back to the tab", Icons.Play, () => Back()));

        SetContent(stack);
    }

    private void BuildFilterParams(StackPanel stack, FilterNode node)
    {
        switch (node.Kind)
        {
            case FilterKind.Installed:
                stack.Children.Add(CycleRow("State", node.BoolValue ? "Installed" : "Not installed",
                    () => { node.BoolValue = !node.BoolValue; Replace(() => RenderFilterEditor(node)); }));
                break;

            case FilterKind.Platform:
                stack.Children.Add(CycleRow("Platform", node.Platform == PlatformKind.Steam
                    ? "Steam" : "Non-Steam", () =>
                {
                    node.Platform = node.Platform == PlatformKind.Steam
                        ? PlatformKind.NonSteam : PlatformKind.Steam;
                    Replace(() => RenderFilterEditor(node));
                }));
                break;

            case FilterKind.Regex:
                stack.Children.Add(Row("Pattern", string.IsNullOrEmpty(node.Pattern)
                    ? "(required)" : node.Pattern, Icons.CopyDoc, () =>
                    EditText("Title pattern", node.Pattern, 64, v =>
                    {
                        node.Pattern = v;
                    })));
                break;

            case FilterKind.Tag:
                stack.Children.Add(Row("Tags", node.TagIds.Count == 0
                    ? "(choose one or more)" : $"{node.TagIds.Count} selected", Icons.Wrench,
                    () => OpenTagPicker(node)));
                stack.Children.Add(CycleRow("Match", node.Mode == FilterMode.And
                    ? "All tags (AND)" : "Any tag (OR)", () =>
                {
                    node.Mode = node.Mode == FilterMode.And ? FilterMode.Or : FilterMode.And;
                    Replace(() => RenderFilterEditor(node));
                }));
                break;

            case FilterKind.Collection:
                stack.Children.Add(Row("Collection", string.IsNullOrEmpty(node.CollectionId)
                    ? "(choose one)" : CollectionName(node.CollectionId), Icons.Wrench,
                    () => OpenCollectionPicker(node)));
                break;

            case FilterKind.Whitelist:
            case FilterKind.Blacklist:
                stack.Children.Add(Row("Games", node.AppIds.Count == 0
                    ? "(choose games)" : $"{node.AppIds.Count} selected", Icons.Wrench,
                    () => OpenGamePicker(node)));
                break;

            case FilterKind.ReviewScore:
                stack.Children.Add(CycleRow("Source", node.ScoreType == ReviewScoreType.SteamPercent
                    ? "Steam %" : "Metacritic", () =>
                {
                    node.ScoreType = node.ScoreType == ReviewScoreType.SteamPercent
                        ? ReviewScoreType.Metacritic : ReviewScoreType.SteamPercent;
                    Replace(() => RenderFilterEditor(node));
                }));
                AddCondition(stack, node);
                AddStepper(stack, "Score", node.Threshold, 0, 100, 5,
                    v => { node.Threshold = v; Replace(() => RenderFilterEditor(node)); });
                break;

            case FilterKind.TimePlayed:
                AddCondition(stack, node);
                stack.Children.Add(CycleRow("Units", node.Units switch
                {
                    TimeUnit.Minutes => "Minutes",
                    TimeUnit.Days => "Days",
                    _ => "Hours",
                }, () =>
                {
                    node.Units = node.Units switch
                    {
                        TimeUnit.Minutes => TimeUnit.Hours,
                        TimeUnit.Hours => TimeUnit.Days,
                        _ => TimeUnit.Minutes,
                    };
                    Replace(() => RenderFilterEditor(node));
                }));
                AddStepper(stack, "Amount", node.Threshold, 0, 1000, 1,
                    v => { node.Threshold = v; Replace(() => RenderFilterEditor(node)); });
                break;

            case FilterKind.SizeOnDisk:
                AddCondition(stack, node);
                AddStepper(stack, "Size (GB)", node.Threshold, 0, 2000, 5,
                    v => { node.Threshold = v; Replace(() => RenderFilterEditor(node)); });
                break;

            case FilterKind.ReleaseDate:
            case FilterKind.LastPlayed:
                AddCondition(stack, node);
                AddStepper(stack, "Days ago", node.DaysAgo, 0, 3650, 30,
                    v => { node.DaysAgo = (int)v; node.Year = 0; Replace(() => RenderFilterEditor(node)); });
                stack.Children.Add(Caption("0 days = use no date. (Absolute dates: edit config.)"));
                break;

            case FilterKind.SdCard:
                stack.Children.Add(CycleRow("Card", node.CardScope switch
                {
                    SdCardScope.Inserted => "Currently inserted",
                    SdCardScope.Any => "Any tracked card",
                    _ => CardName(node.ContentId),
                }, () => CycleCardScope(node)));
                break;

            case FilterKind.Merge:
                stack.Children.Add(CycleRow("Match", node.Mode == FilterMode.And
                    ? "All (AND)" : "Any (OR)", () =>
                {
                    node.Mode = node.Mode == FilterMode.And ? FilterMode.Or : FilterMode.And;
                    Replace(() => RenderFilterEditor(node));
                }));
                stack.Children.Add(SectionLabel("GROUP FILTERS"));
                foreach (var child in node.Children.ToList())
                {
                    var c = child;
                    stack.Children.Add(Row(DescribeFilter(c), FilterKindLabel(c.Kind), Icons.Wrench,
                        () => OpenChildEditor(node, c)));
                }
                stack.Children.Add(Row("Add to group", "Nested filter", Icons.FolderPlus,
                    () => OpenChildPicker(node)));
                break;
        }
    }

    private void AddCondition(StackPanel stack, FilterNode node)
        => stack.Children.Add(CycleRow("Condition", node.Condition == ThresholdCondition.Above
            ? "At or above" : "Below", () =>
        {
            node.Condition = node.Condition == ThresholdCondition.Above
                ? ThresholdCondition.Below : ThresholdCondition.Above;
            Replace(() => RenderFilterEditor(node));
        }));

    // ---- Merge sub-group editing (one nesting level; re-uses the same editor) ----

    private void OpenChildPicker(FilterNode group)
    {
        Navigate(() =>
        {
            var stack = NewStack("Add to Group");
            foreach (var (kind, label, desc) in FilterKinds.Where(f => f.Kind != FilterKind.Merge))
            {
                var k = kind;
                stack.Children.Add(Row(label, desc, Icons.Wrench, () =>
                {
                    var child = new FilterNode { Kind = k };
                    group.Children.Add(child);
                    _current = () => RenderChildEditor(group, child);
                    RenderChildEditor(group, child);
                }));
            }
            SetContent(stack);
        });
    }

    private void OpenChildEditor(FilterNode group, FilterNode child)
        => Navigate(() => RenderChildEditor(group, child));

    private void RenderChildEditor(FilterNode group, FilterNode child)
    {
        var stack = NewStack(FilterKindLabel(child.Kind));
        BuildFilterParams(stack, child);
        if (LibraryFilter.CanInvert(child.Kind))
        {
            stack.Children.Add(CycleRow("Result", child.Inverted ? "Inverted (NOT)" : "Normal", () =>
            {
                child.Inverted = !child.Inverted;
                Replace(() => RenderChildEditor(group, child));
            }));
        }
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(DangerRow("Remove", "Delete from group", Icons.Close, () =>
        {
            group.Children.Remove(child);
            Back();
        }));
        stack.Children.Add(PrimaryRow("Done", "Back to group", Icons.Play, () => Back()));
        SetContent(stack);
    }

    // ---- Pickers (async data) ----

    private void OpenTagPicker(FilterNode node) => _ = RunSafelyAsync(OpenTagPickerAsync(node), "tag picker");

    private async Task OpenTagPickerAsync(FilterNode node)
    {
        Navigate(() => RenderLoading("Tags"));
        var generation = _navigationGeneration;
        var loaded = await SteamCollections.GetLibraryTagsAsync();
        if (generation != _navigationGeneration)
        {
            return;
        }
        _tags = loaded;
        var selected = new HashSet<long>(node.TagIds.Select(static id => (long)id));
        Replace(() => RenderMultiSelect("Tags", _tags!.Select(t => ((long)t.TagId, $"{t.Name} ({t.Count})")),
            selected, () =>
        {
            node.TagIds = selected.Select(static id => checked((int)id)).ToList();
            Back();
        }));
    }

    private void OpenGamePicker(FilterNode node) => _ = RunSafelyAsync(OpenGamePickerAsync(node), "game picker");

    private async Task OpenGamePickerAsync(FilterNode node)
    {
        Navigate(() => RenderLoading("Games"));
        var generation = _navigationGeneration;
        var loaded = await SteamCollections.GetGamesAsync();
        if (generation != _navigationGeneration)
        {
            return;
        }
        _games = loaded;
        var selected = new HashSet<long>(node.AppIds);
        Replace(() => RenderMultiSelect("Games", _games!.Select(g => (g.AppId, g.Name)), selected, () =>
        {
            node.AppIds = selected.ToList();
            Back();
        }));
    }

    private void OpenCollectionPicker(FilterNode node) => _ = RunSafelyAsync(OpenCollectionPickerAsync(node), "collection picker");

    private async Task OpenCollectionPickerAsync(FilterNode node)
    {
        Navigate(() => RenderLoading("Collections"));
        var generation = _navigationGeneration;
        var loaded = await SteamCollections.ListAsync();
        if (generation != _navigationGeneration)
        {
            return;
        }
        _collections = loaded;
        Replace(() =>
        {
            var stack = NewStack("Collection");
            foreach (var col in _collections!)
            {
                var c = col;
                stack.Children.Add(Row(c.Name, $"{c.AppIds.Count} games", Icons.Wrench, () =>
                {
                    node.CollectionId = c.Id;
                    Back();
                }));
            }
            if (_collections!.Count == 0)
            {
                stack.Children.Add(Caption("No collections found — is Steam open?"));
            }
            SetContent(stack);
        });
    }

    private void RenderMultiSelect(string title, IEnumerable<(long Id, string Label)> items,
        HashSet<long> selected, Action onDone, int page = 0)
    {
        const int pageSize = 200;
        var all = items.ToList();
        var pageCount = Math.Max(1, (all.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 0, pageCount - 1);
        var stack = NewStack(title);
        stack.Children.Add(PrimaryRow("Done", $"{selected.Count} selected", Icons.Play, onDone));
        if (pageCount > 1)
        {
            var currentPage = page;
            stack.Children.Add(Row($"Page {page + 1} of {pageCount}", $"{all.Count} entries · previous",
                Icons.Restart, page > 0
                    ? () => Replace(() => RenderMultiSelect(title, all, selected, onDone, currentPage - 1))
                    : null));
            if (page + 1 < pageCount)
            {
                stack.Children.Add(Row("Next page", $"Entries {(page + 1) * pageSize + 1}–{Math.Min(all.Count, (page + 2) * pageSize)}",
                    Icons.Play, () => Replace(() => RenderMultiSelect(title, all, selected, onDone, currentPage + 1))));
            }
        }
        foreach (var (id, label) in all.Skip(page * pageSize).Take(pageSize))
        {
            var itemId = id;
            var check = selected.Contains(itemId) ? "✓ " : "";
            var row = Row(check + label, "", null, null);
            row.Click += (_, _) =>
            {
                if (!selected.Add(itemId))
                {
                    selected.Remove(itemId);
                }
                row.Title = (selected.Contains(itemId) ? "✓ " : "") + label;
            };
            stack.Children.Add(row);
        }
        SetContent(stack);
    }

    // ---- Tab-side builders ----

    private void AddStepper(StackPanel stack, string label, double value, double min, double max,
        double step, Action<double> onChange)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var text = new TextBlock
        {
            Text = $"{label}: {value.ToString("0.##", CultureInfo.InvariantCulture)}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(text, 0);
        var minus = new Button { Content = "−", Width = 46, Margin = new Avalonia.Thickness(4, 0, 4, 0) };
        Grid.SetColumn(minus, 1);
        minus.Click += (_, _) => onChange(Math.Clamp(value - step, min, max));
        var plus = new Button { Content = "+", Width = 46 };
        Grid.SetColumn(plus, 2);
        plus.Click += (_, _) => onChange(Math.Clamp(value + step, min, max));
        row.Children.Add(text);
        row.Children.Add(minus);
        row.Children.Add(plus);
        stack.Children.Add(row);
    }

    // ---- Value helpers ----

    private void CycleCardScope(FilterNode node)
    {
        var cards = _config.CardLibraries;
        // Inserted → Any → each specific card → Inserted.
        switch (node.CardScope)
        {
            case SdCardScope.Inserted:
                node.CardScope = SdCardScope.Any;
                break;
            case SdCardScope.Any:
                if (cards.Count > 0)
                {
                    node.CardScope = SdCardScope.Specific;
                    node.ContentId = cards[0].ContentId;
                }
                else
                {
                    node.CardScope = SdCardScope.Inserted;
                }
                break;
            default:
                var idx = cards.FindIndex(c => c.ContentId == node.ContentId);
                if (idx < 0 || idx + 1 >= cards.Count)
                {
                    node.CardScope = SdCardScope.Inserted;
                    node.ContentId = "";
                }
                else
                {
                    node.ContentId = cards[idx + 1].ContentId;
                }
                break;
        }
        Replace(() => RenderFilterEditor(node));
    }

    private static void RemoveNode(FilterNode group, FilterNode node) => group.Children.Remove(node);

    private static int NextCategories(int current)
    {
        // Cycle a few useful presets rather than exposing the full bitfield.
        var g = (int)LibraryFilter.Categories.Games;
        var gs = g | (int)LibraryFilter.Categories.Software;
        var gsh = gs | (int)LibraryFilter.Categories.Hidden;
        if (current == g)
        {
            return gs;
        }
        return current == gs ? gsh : g;
    }

    private static string CategoriesLabel(LibraryFilter.Categories c)
    {
        var parts = new List<string>();
        if (c.HasFlag(LibraryFilter.Categories.Games))
        {
            parts.Add("Games");
        }
        if (c.HasFlag(LibraryFilter.Categories.Software))
        {
            parts.Add("Software");
        }
        if (c.HasFlag(LibraryFilter.Categories.Music))
        {
            parts.Add("Music");
        }
        if (parts.Count == 0)
        {
            parts.Add("Games");
        }
        if (c.HasFlag(LibraryFilter.Categories.Hidden))
        {
            parts.Add("+Hidden");
        }
        return string.Join(", ", parts);
    }

    private string CollectionName(string id)
        => _collections?.FirstOrDefault(c => c.Id == id)?.Name ?? "selected";

    private string CardName(string contentId)
        => _config.CardLibraries.FirstOrDefault(c => c.ContentId == contentId)?.Name ?? "a card";

    private static string FilterKindLabel(FilterKind kind)
        => FilterKinds.FirstOrDefault(f => f.Kind == kind).Label ?? kind.ToString();

    private string DescribeFilter(FilterNode node)
    {
        var prefix = node.Inverted && LibraryFilter.CanInvert(node.Kind) ? "NOT " : "";
        return prefix + node.Kind switch
        {
            FilterKind.Tag => node.TagIds.Count == 1 && _tags is not null
                ? _tags.FirstOrDefault(t => t.TagId == node.TagIds[0])?.Name ?? "Tag"
                : $"Tags ({node.TagIds.Count})",
            FilterKind.Installed => node.BoolValue ? "Installed" : "Not installed",
            FilterKind.Collection => CollectionName(node.CollectionId),
            FilterKind.Regex => $"Title ~ {node.Pattern}",
            FilterKind.SdCard => node.CardScope == SdCardScope.Specific
                ? $"On {CardName(node.ContentId)}"
                : node.CardScope == SdCardScope.Any ? "On any card" : "On inserted card",
            FilterKind.TimePlayed => $"Playtime {Cond(node)} {node.Threshold:0.##}",
            FilterKind.SizeOnDisk => $"Size {Cond(node)} {node.Threshold:0.##} GB",
            FilterKind.ReviewScore => $"Score {Cond(node)} {node.Threshold:0}",
            FilterKind.ReleaseDate => $"Released {(node.DaysAgo > 0 ? $"< {node.DaysAgo}d ago" : "date")}",
            FilterKind.LastPlayed => $"Played {(node.DaysAgo > 0 ? $"< {node.DaysAgo}d ago" : "date")}",
            FilterKind.Platform => node.Platform == PlatformKind.Steam ? "Steam" : "Non-Steam",
            FilterKind.Whitelist => $"Whitelist ({node.AppIds.Count})",
            FilterKind.Blacklist => $"Blacklist ({node.AppIds.Count})",
            FilterKind.Merge => $"Group ({node.Children.Count})",
            _ => node.Kind.ToString(),
        };
    }

    private static string Cond(FilterNode node) => node.Condition == ThresholdCondition.Above ? "≥" : "<";

    private static CustomTabConfig Clone(CustomTabConfig t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Enabled = t.Enabled,
        Position = t.Position,
        Categories = t.Categories,
        FilterTree = t.FilterTree?.Clone() ?? new FilterNode { Kind = FilterKind.Merge },
    };
}

/// <summary>Tiny fluent helper so builders can configure-and-return in one expression.</summary>
internal static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
