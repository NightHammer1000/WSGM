using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Structured outcome for tab synchronization and retry policy.</summary>
/// <param name="Summary">User-facing summary.</param>
/// <param name="Success">Whether definitions and badges synchronized.</param>
/// <param name="Reachable">Whether Steam's CEF target was reachable.</param>
/// <param name="BadgesPushed">Whether the in-page badge observer + map reached the
/// VISIBLE window. Distinct from Success because tabs live in SharedJSContext while
/// the badge lives in the visible page; direct runtime callers can reach one without
/// the other. The automatic boot path deliberately waits for Big Picture before
/// touching either context.</param>
public readonly record struct LibraryTabSyncResult(
    string Summary, bool Success, bool Reachable, bool BadgesPushed = false);

/// <summary>Builds Steam library tabs as injected in-memory definitions over CEF:
/// <list type="bullet">
/// <item>one tab per removable Steam library (MicroSD card / external drive),
/// keyed by its <c>libraryfolder.vdf</c> content id and remembered while ejected;</item>
/// <item>user-built filter tabs evaluated against Steam's app store.</item>
/// </list>
/// Steam renders fake in-memory collections through its own grid; no real collection
/// is created or modified except one-time cleanup of IDs from older WSGM builds.</summary>
public sealed class LibraryTabManager
{
    // Static so every trigger (boot, overlay open, each builder change) serializes even
    // across separate manager instances — concurrent syncs would race the config.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Recomputes every WSGM library tab and injects them into Steam's tab
    /// strip (see <see cref="SteamLibraryTabs"/>): custom filter tabs, then per-card
    /// tabs, then genre tabs. Reactive — called after any change in the builder and on
    /// overlay open. Returns a short user-facing summary; concurrent calls are
    /// serialized, not coalesced — every queued caller runs a full sync.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<string> SyncAllAsync(CancellationToken cancellationToken = default)
        => (await SyncAllDetailedAsync(cancellationToken).ConfigureAwait(false)).Summary;

    /// <summary>Synchronizes tabs and returns machine-readable retry state.</summary>
    public async Task<LibraryTabSyncResult> SyncAllDetailedAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discovered = await Task.Run(ScanLibraries, cancellationToken).ConfigureAwait(false);
            var config = await Task.Run(ConfigStore.Load, cancellationToken).ConfigureAwait(false);
            MergeDiscovery(config, discovered);

            var (tabs, reachable, filterFailed) = await BuildTabsAsync(config, discovered, cancellationToken)
                .ConfigureAwait(false);

            // CEF library-tabs feature gate (master + sub-toggle): when off, the tab
            // strip is never pushed. Discovery, badges, and config merge below still
            // run so the SD-card manager and its badges remain independent.
            var tabsEnabled = config.Cef.Enabled && config.Cef.LibraryTabs;
            TabSyncResult sync;
            if (reachable != false && !filterFailed && tabsEnabled)
            {
                sync = await SteamLibraryTabs.SyncTabsAsync(
                    tabs, config.LibraryTabOrder, config.HiddenNativeTabs, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                sync = new TabSyncResult(false, []);
                if (reachable != false && !tabsEnabled)
                {
                    // Turning the sub-toggle off has to retract, not merely stop
                    // pushing: the resident script keeps rendering the tabs that were
                    // already injected, so without this the setting appears to do
                    // nothing until a desktop trip or a Steam restart clears them.
                    var retraction = await SteamLibraryTabs.DisableAsync(cancellationToken)
                        .ConfigureAwait(false);
                    reachable ??= retraction.Reachable;
                }
            }
            var ok = sync.Ok;
            // BuildTabsAsync leaves reachability unknown when it evaluated no filter
            // (a card-tabs-only or empty configuration never talks to Steam), so the
            // reported value comes from whatever actually reached the CEF target.
            var reachedSteam = reachable ?? ok;

            // CEF work above may take seconds. Merge only this sync's discovery into a
            // freshly loaded config under the cross-process read-modify-write lock;
            // never save the stale snapshot.
            config = await MutateConfigAsync(fresh =>
            {
                MergeDiscovery(fresh, discovered);
                // Union, not replace: dynamic native tabs (Soundtracks, Favorites)
                // drop out of Steam's array while empty, and must keep their entry
                // so the order UI can still place and unhide them.
                foreach (var native in sync.NativeTabs)
                {
                    var known = fresh.KnownNativeTabs.FirstOrDefault(
                        k => string.Equals(k.Id, native.Id, StringComparison.Ordinal));
                    if (known is null)
                    {
                        fresh.KnownNativeTabs.Add(native);
                    }
                    else if (!string.IsNullOrEmpty(native.Title))
                    {
                        known.Title = native.Title;
                    }
                }
                return fresh;
            }, cancellationToken).ConfigureAwait(false);

            var badgePush = await PushCardBadgesAsync(config, cancellationToken)
                .ConfigureAwait(false);
            var badgesPushed = badgePush == BadgePush.Pushed;
            if (!tabsEnabled)
            {
                // The tab strip is switched off, so there is nothing left to push and
                // nothing pending: report success, or every caller keeps re-running a
                // full sync (the overlay only arms its auto-sync throttle on success).
                return new LibraryTabSyncResult(
                    "Library tabs are turned off.", true, reachedSteam, badgesPushed);
            }
            if (!reachedSteam || !ok)
            {
                if (filterFailed)
                {
                    return new LibraryTabSyncResult(
                        "Saved the tabs, but one filter failed in Steam; existing tabs were preserved.",
                        false, true, badgesPushed);
                }
                return new LibraryTabSyncResult(
                    "Saved the tabs — Steam isn't reachable yet; they'll appear when it's open.",
                    false, reachedSteam, badgesPushed);
            }

            Log.Info($"Library tabs: {tabs.Count} injected.");
            var summary = tabs.Count == 0
                ? "No library tabs yet — add a custom tab or insert a card library."
                : $"Synced {tabs.Count} library tabs.";
            return new LibraryTabSyncResult(summary, true, true, badgesPushed);
        }
        catch (OperationCanceledException)
        {
            // Expected: a desktop transition cancels the shared token mid-evaluation.
            // Not a failure, and it must not put a stack trace into the device log.
            Log.Info("Library tabs: sync cancelled.");
            return new LibraryTabSyncResult("Library tab sync cancelled.", false, false);
        }
        catch (Exception ex)
        {
            Log.Error("Library tabs: sync failed.", ex);
            return new LibraryTabSyncResult("Could not sync library tabs — see the log.", false, false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Polls (first probe after 3 s, then every 5 s) for Steam's Big Picture
    /// window and library stores to finish loading after a cold boot, then syncs — so
    /// tabs appear without the user opening the overlay.
    /// Best-effort and self-limiting; falls back to the on-open sync if Steam never
    /// becomes reachable.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task SyncOnBootAsync(CancellationToken cancellationToken = default)
    {
        _ = await SteamUiReadiness.RunWhenReadyAsync(
            "Library tabs (boot)",
            async token =>
        {
            CefEvalResult probe = await SteamUiTransportSession.EvaluateAsync(
                "JSON.stringify(!!window.webpackChunksteamui&&!!window.collectionStore"
                    + "&&!!window.appStore)",
                TimeSpan.FromSeconds(4),
                token).ConfigureAwait(false);
            if (!probe.Reachable || probe.Value != "true")
            {
                return false;
            }

            LibraryTabSyncResult result = await SyncAllDetailedAsync(token).ConfigureAwait(false);
            Log.Info($"Library tabs (boot): {result.Summary}");
            LibraryTabBootAction action = LibraryTabBootSyncPolicy.Decide(result);
            if (action == LibraryTabBootAction.RetryFullSync)
            {
                // A half-initialized appStore can be reachable but reject a filter. The badge
                // targets another context, so its success must never make us abandon the tabs.
                return false;
            }
            if (action == LibraryTabBootAction.Complete)
            {
                return true;
            }

            // The tabs succeeded but the visible-window badge did not. Retry only the badge; the
            // full filter evaluation must not run every five seconds.
            for (int attempt = 0; attempt < 30 && !token.IsCancellationRequested; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                AppConfig config = await Task.Run(ConfigStore.Load, token).ConfigureAwait(false);
                BadgePush push = await PushCardBadgesAsync(config, token).ConfigureAwait(false);
                if (push == BadgePush.Pushed)
                {
                    Log.Info("Library tabs (boot): card badge installed.");
                    return true;
                }
                if (push == BadgePush.Disabled)
                {
                    Log.Info("Library tabs (boot): card badges are turned off.");
                    return true;
                }
            }
            Log.Info("Library tabs (boot): badge target not reachable in time; "
                + "it will install on the next sync.");
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the ordered injected-tab list: custom filter tabs (evaluated over
    /// the library), then per-card tabs. Reachable is false when Steam was unreachable
    /// during filter evaluation and null when no filter was evaluated at all — nothing
    /// probed Steam then, so the caller must not read it as "reachable".</summary>
    private static async Task<(List<InjectedTab> Tabs, bool? Reachable, bool FilterFailed)> BuildTabsAsync(
        AppConfig config, List<Discovered> discovered, CancellationToken cancellationToken)
    {
        var tabs = new List<InjectedTab>();
        var resolver = new CardResolver(config, discovered);

        var customTabs = config.CustomTabs
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Position)
            .Where(tab =>
            {
                var valid = tab.FilterTree is not null && LibraryFilter.IsValid(tab.FilterTree);
                if (!valid)
                {
                    Log.Warn($"Library tabs: skipped invalid custom tab '{tab.Name}'.");
                }
                return valid;
            }).ToList();
        var expressions = customTabs.Select(tab => LibraryFilter.BuildEvaluation(
            tab.FilterTree!, tab.Categories == 0
                ? LibraryFilter.Categories.Games
                : (LibraryFilter.Categories)tab.Categories, resolver)).ToList();
        var evaluations = await SteamCollections.EvaluateFiltersAsync(expressions, cancellationToken)
            .ConfigureAwait(false);
        for (var i = 0; i < customTabs.Count; i++)
        {
            var tab = customTabs[i];
            var eval = evaluations[i];
            if (!eval.Reachable)
            {
                return (tabs, false, false);
            }
            if (!eval.Ok)
            {
                Log.Warn($"Library tabs: Steam filter evaluation failed for '{tab.Name}'.");
                return (tabs, true, true);
            }
            if (eval.AppIds.Count > 0)
            {
                tabs.Add(new InjectedTab($"wsgm-custom-{tab.Id}", tab.Name, eval.AppIds));
            }
        }

        // Only the cards the user has enabled — never auto-generated genre tabs. A user
        // who wants a genre tab makes a custom tab with a Tag filter (same engine).
        var present = new HashSet<string>(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);
        foreach (var card in config.CardLibraries.Where(c => c is { Enabled: true, Hidden: false }))
        {
            var keep = present.Contains(card.ContentId) || config.KeepEjectedCardTabs;
            if (keep && card.AppIds.Count > 0)
            {
                tabs.Add(new InjectedTab($"wsgm-card-{card.ContentId}", card.Name, card.AppIds));
            }
        }

        // Only a filter evaluation talks to Steam here; with none, reachability is
        // unknown rather than proven.
        bool? reachable = customTabs.Count > 0 ? true : null;
        return (tabs, reachable, false);
    }

    /// <summary>Outcome of a card-badge push: reached the visible window, missed it
    /// (boot paths retry), or the feature is switched off (nothing to retry).</summary>
    private enum BadgePush
    {
        NoTarget,
        Pushed,
        Disabled,
    }

    /// <summary>Pushes the per-game card badge map (app id → card name) into Steam's
    /// library page and (re)installs the resident badge observer. Best-effort — a badge
    /// failure never affects tab syncing.</summary>
    // Reports whether the observer + map actually reached the visible window —
    // callers on boot paths retry on NoTarget instead of assuming the badge exists.
    private static async Task<BadgePush> PushCardBadgesAsync(
        AppConfig config, CancellationToken cancellationToken)
    {
        // CEF SD-card-manager feature gate (master + sub-toggle): the "On: <card>"
        // badges are part of that feature, so retract them when it is off. Merely
        // skipping the push would leave the resident observer and the last map in
        // place, so the toggle would look ignored for the rest of the session.
        if (!(config.Cef.Enabled && config.Cef.CardManager))
        {
            if (config.Cef.Enabled)
            {
                await SteamPageBridge.DisableBadgeAsync(cancellationToken).ConfigureAwait(false);
            }
            return BadgePush.Disabled;
        }
        try
        {
            var map = new Dictionary<long, string>();
            foreach (var card in config.CardLibraries.Where(c => c is { Enabled: true, Hidden: false }))
            {
                foreach (var id in card.AppIds)
                {
                    map[id] = card.Name;
                }
            }
            var pushed = await SteamPageBridge.UpdateCardBadgesAsync(map, cancellationToken)
                .ConfigureAwait(false);
            return pushed ? BadgePush.Pushed : BadgePush.NoTarget;
        }
        catch (Exception ex)
        {
            Log.Warn($"Card badge push failed: {ex.Message}");
            return BadgePush.NoTarget;
        }
    }

    /// <summary>Resolves <see cref="FilterKind.SdCard"/> membership from WSGM's card
    /// model: "inserted" = union of currently-present cards, "any" = union of all
    /// tracked cards, "specific" = one card's remembered app ids.</summary>
    private sealed class CardResolver(AppConfig config, List<Discovered> discovered) : ISdCardResolver
    {
        private readonly HashSet<string> _present = new(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);

        public IReadOnlyCollection<long> Resolve(SdCardScope scope, string contentId)
        {
            IEnumerable<CardLibraryConfig> cards = scope switch
            {
                SdCardScope.Inserted => config.CardLibraries.Where(c => _present.Contains(c.ContentId)),
                SdCardScope.Any => config.CardLibraries,
                _ => config.CardLibraries.Where(
                    c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal)),
            };
            var ids = new HashSet<long>();
            foreach (var card in cards)
            {
                foreach (var id in card.AppIds)
                {
                    ids.Add(id);
                }
            }
            return ids;
        }
    }

    // ---- Card-manager API (drives the overlay card sub-view) ----

    /// <summary>A card as shown in the manager: identity, name, tab/hidden state, game
    /// count, and whether it is currently inserted.</summary>
    /// <param name="ContentId">Stable card identity (its library content id).</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Enabled">Whether a Steam tab is maintained.</param>
    /// <param name="Hidden">Whether it is hidden from tab creation.</param>
    /// <param name="GameCount">Remembered installed app count.</param>
    /// <param name="Inserted">Whether the card is currently mounted.</param>
    /// <param name="AppIds">Remembered installed app ids.</param>
    public sealed record CardView(
        string ContentId, string Name, bool Enabled, bool Hidden, int GameCount, bool Inserted,
        IReadOnlyList<long> AppIds);

    /// <summary>Scans drives, refreshes the card DB, and returns the current cards with
    /// live inserted state — the card manager's data source.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public async Task<IReadOnlyList<CardView>> ListCardsAsync(
        CancellationToken cancellationToken = default)
    {
        var discovered = await Task.Run(ScanLibraries, cancellationToken).ConfigureAwait(false);
        var present = new HashSet<string>(
            discovered.Select(d => d.ContentId), StringComparer.Ordinal);
        return await MutateConfigAsync(config =>
        {
            MergeDiscovery(config, discovered);
            return config.CardLibraries
                .Select(c => new CardView(
                    c.ContentId, c.Name, c.Enabled, c.Hidden, c.AppIds.Count,
                    present.Contains(c.ContentId), [.. c.AppIds]))
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames a tracked card everywhere it has a name: the WSGM tab, the
    /// Steam library label (live via CEF when Steam runs, else a byte-preserving
    /// vdf edit), and the Windows volume label when the card is inserted. Identity
    /// is always the content id — never the reader's (shared) drive letter.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>Null when every side applied; otherwise a short user-facing note
    /// describing what did not.</returns>
    public async Task<string?> RenameCardAsync(string contentId, string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "The name cannot be empty.";
        }
        await UpdateCardAsync(contentId, c => c.Name = trimmed, cancellationToken)
            .ConfigureAwait(false);

        var notes = new List<string>();
        var letter = await Task.Run(() => FindMountedLetter(contentId), cancellationToken)
            .ConfigureAwait(false);

        var steamNote = await PushLabelToSteamAsync(contentId, trimmed, letter, cancellationToken)
            .ConfigureAwait(false);
        if (steamNote is not null)
        {
            notes.Add(steamNote);
        }

        if (letter is char mounted)
        {
            var volumeNote = await Task.Run(
                () => TrySetVolumeLabel(mounted, trimmed), cancellationToken).ConfigureAwait(false);
            if (volumeNote is not null)
            {
                notes.Add(volumeNote);
            }
        }
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>The drive letter the card with this content id is currently mounted
    /// on, verified by reading the marker — never assumed from a remembered letter,
    /// because the reader letter is shared by every card ever inserted.</summary>
    private static char? FindMountedLetter(string contentId)
    {
        var systemDisks = RemovableDriveManager.ResolveSystemDisks();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || !IsExternalVolume(drive, systemDisks))
                {
                    continue;
                }
                var marker = Path.Combine(drive.Name, "SteamLibrary", "libraryfolder.vdf");
                if (!File.Exists(marker))
                {
                    continue;
                }
                var id = SteamLibraryVdf.ValuesOf(File.ReadAllText(marker), "contentid")
                    .FirstOrDefault();
                if (string.Equals(id, contentId, StringComparison.Ordinal))
                {
                    return char.ToUpperInvariant(drive.Name[0]);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Card rename: could not probe {drive.Name}: {ex.Message}");
            }
        }
        return null;
    }

    // Pushes the new label to Steam. Live client: SetFolderLabel over CEF (Steam
    // persists it itself; LastSteamLabel stays stale until Steam flushes its config,
    // and MergeDiscovery records the new agreement then). Closed client: edit the
    // config and marker vdf files directly and record the agreement immediately.
    private static async Task<string?> PushLabelToSteamAsync(
        string contentId, string label, char? letter, CancellationToken cancellationToken)
    {
        const string steamBehind = "Steam still shows the old name.";
        var steamExe = Steam.ExePath;
        if (steamExe is null)
        {
            return null;
        }
        var configPath = Path.Combine(
            Path.GetDirectoryName(steamExe)!, "config", "libraryfolders.vdf");

        if (Steam.IsRunning)
        {
            string configText;
            try
            {
                configText = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
            }
            catch (Exception ex)
            {
                Log.Warn($"Card rename: could not read Steam config: {ex.Message}");
                return steamBehind;
            }
            var result = await Core.SteamCdp.SetLibraryLabelByContentIdAsync(
                contentId, configText, label, cancellationToken).ConfigureAwait(false);
            if (result.Status is Core.SteamLibraryLabelStatus.Applied
                or Core.SteamLibraryLabelStatus.NotPresent)
            {
                return null;
            }
            Log.Warn($"Card rename: live relabel {result.Status} "
                + $"({result.Detail ?? "no detail"}).");
            return steamBehind;
        }

        var edited = await Task.Run(() =>
        {
            try
            {
                if (File.Exists(configPath)
                    && SteamLibraryVdf.TrySetLabel(
                        File.ReadAllText(configPath), contentId, label, out var updatedConfig)
                    && updatedConfig is not null)
                {
                    if (Steam.IsRunning)
                    {
                        // Started between the check and the write; its exit rewrite
                        // would clobber ours (and ours could corrupt its view).
                        return false;
                    }
                    SdFormatManager.WriteAtomically(configPath, updatedConfig);
                }
                if (letter is char mounted)
                {
                    var marker = Path.Combine($"{mounted}:\\", "SteamLibrary", "libraryfolder.vdf");
                    if (File.Exists(marker)
                        && SteamLibraryVdf.TrySetLabel(
                            File.ReadAllText(marker), contentId, label, out var updatedMarker)
                        && updatedMarker is not null)
                    {
                        SdFormatManager.WriteAtomically(marker, updatedMarker);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Card rename: vdf label edit failed: {ex.Message}");
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);
        if (!edited)
        {
            return steamBehind;
        }
        // The files now agree with Name; record the pair so discovery keeps
        // following future Steam-side renames.
        await UpdateCardAsync(contentId, c => c.LastSteamLabel = label, cancellationToken)
            .ConfigureAwait(false);
        return null;
    }

    // Windows volume labels are capped by filesystem: 32 chars on NTFS, 11 on
    // FAT32/exFAT — truncate rather than fail, and strip FAT-hostile characters.
    private static string? TrySetVolumeLabel(char letter, string name)
    {
        try
        {
            var drive = new DriveInfo($"{letter}:\\");
            var isNtfs = string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
            var invalid = "*?/\\|,;:+=<>[]\".".ToCharArray();
            var cleaned = new string([.. name.Where(c => Array.IndexOf(invalid, c) < 0)]).Trim();
            if (cleaned.Length == 0)
            {
                return null;
            }
            var capped = cleaned.Length > (isNtfs ? 32 : 11)
                ? cleaned[..(isNtfs ? 32 : 11)] : cleaned;
            if (!string.Equals(drive.VolumeLabel, capped, StringComparison.Ordinal))
            {
                drive.VolumeLabel = capped;
                Log.Info($"Card rename: volume {letter}: labeled '{capped}'.");
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Card rename: could not set volume label on {letter}:: {ex.Message}");
            return "The Windows volume label could not be changed.";
        }
    }

    /// <summary>Enables or disables a card's Steam tab.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="enabled">Whether to maintain a tab.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task SetCardEnabledAsync(string contentId, bool enabled,
        CancellationToken cancellationToken = default)
        => UpdateCardAsync(contentId, c => c.Enabled = enabled, cancellationToken);

    /// <summary>Hides or unhides a card in the manager.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task SetCardHiddenAsync(string contentId, bool hidden,
        CancellationToken cancellationToken = default)
        => UpdateCardAsync(contentId, c => c.Hidden = hidden, cancellationToken);

    /// <summary>Forgets a card: removes its tab (if any) and its DB entry. If the card
    /// is reinserted later it is rediscovered fresh.</summary>
    /// <param name="contentId">The card's content id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task ForgetCardAsync(string contentId,
        CancellationToken cancellationToken = default)
    {
        await MutateConfigAsync<object?>(config =>
        {
            var card = config.CardLibraries.FirstOrDefault(
                c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal));
            if (card is null)
            {
                return null;
            }
            config.CardLibraries.Remove(card);
            if (!config.ForgottenInsertedCardIds.Contains(contentId, StringComparer.Ordinal))
            {
                config.ForgottenInsertedCardIds.Add(contentId);
            }
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task UpdateCardAsync(string contentId, Action<CardLibraryConfig> apply,
        CancellationToken cancellationToken)
        => MutateConfigAsync<object?>(config =>
        {
            var card = config.CardLibraries.FirstOrDefault(
                c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal));
            if (card is not null)
            {
                apply(card);
            }
            return null;
        }, cancellationToken);

    /// <summary>Finds what a game's launch configuration looked like before WSGM
    /// pointed it at the launch wrapper.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    /// <returns>The snapshot, or <see langword="null"/> if the game has none.</returns>
    internal static Task<LaunchWrapperConfig?> FindLaunchWrapperAsync(
        long appId, CancellationToken cancellationToken = default)
        => MutateConfigAsync(
            config => config.LaunchWrappers.FirstOrDefault(w => w.AppId == appId),
            cancellationToken);

    /// <summary>Records (or updates) a game's pre-wrapper launch configuration.</summary>
    /// <param name="snapshot">What to remember; replaces any entry for the same game.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    internal static Task RememberLaunchWrapperAsync(
        LaunchWrapperConfig snapshot, CancellationToken cancellationToken = default)
        => MutateConfigAsync<object?>(config =>
        {
            config.LaunchWrappers.RemoveAll(w => w.AppId == snapshot.AppId);
            config.LaunchWrappers.Add(snapshot);
            return null;
        }, cancellationToken);

    /// <summary>Drops a game's snapshot once its launch configuration is restored.</summary>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    internal static Task ForgetLaunchWrapperAsync(
        long appId, CancellationToken cancellationToken = default)
        => MutateConfigAsync<object?>(config =>
        {
            config.LaunchWrappers.RemoveAll(w => w.AppId == appId);
            return null;
        }, cancellationToken);

    /// <summary>Loads the config, applies <paramref name="mutate"/>, and saves — the
    /// whole read-modify-write held under the cross-process config lock so a concurrent
    /// WSGM process (Settings window) can neither interleave nor lose fields.</summary>
    /// <typeparam name="T">The value the mutation returns to the caller.</typeparam>
    /// <param name="mutate">Applies changes and returns a snapshot value.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    internal static Task<T> MutateConfigAsync<T>(Func<AppConfig, T> mutate,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var _ = ConfigStore.AcquireLock();
            if (!ConfigStore.HasExclusiveLock)
            {
                throw new IOException("Could not acquire the configuration lock.");
            }
            var config = ConfigStore.LoadForMutation();
            var result = mutate(config);
            ConfigStore.Save(config);
            return result;
        }, cancellationToken);

    /// <summary>Loads the current custom tabs and cards for the builder UI (no scan;
    /// pair with <see cref="ListCardsAsync"/> for live inserted state).</summary>
    public static AppConfig LoadConfig() => ConfigStore.Load();

    /// <summary>One row of the tab-order UI: a tab key (a native Steam id or an
    /// injected <c>wsgm-…</c> id), its display title, and its visibility. Only native
    /// tabs can be hidden here — WSGM tabs are hidden by disabling them.</summary>
    /// <param name="Key">Native id (<c>AllGames</c>) or injected id (<c>wsgm-…</c>).</param>
    /// <param name="Title">Display title for the UI.</param>
    /// <param name="IsNative">Whether this is one of Steam's own tabs.</param>
    /// <param name="Hidden">Whether a native tab is currently hidden.</param>
    public sealed record TabOrderEntry(string Key, string Title, bool IsNative, bool Hidden);

    // Windows Big Picture's native tabs in Steam's default order — the pre-capture
    // fallback so the order UI works before the first sync has observed the strip.
    // Captured KnownNativeTabs entries override these titles and extend the list.
    private static readonly (string Id, string Title)[] DefaultNativeTabs =
    [
        ("AllGames", "All Games"),
        ("Installed", "Installed"),
        ("Favorites", "Favorites"),
        ("Collections", "Collections"),
        ("DesktopApps", "Non-Steam"),
        ("Soundtracks", "Soundtracks"),
    ];

    /// <summary>Builds the full tab-strip list the way Steam will render it: keys from
    /// <see cref="AppConfig.LibraryTabOrder"/> first, then unlisted tabs in natural
    /// order (native tabs, custom tabs by position, card tabs). Hidden native tabs
    /// stay in the list — marked hidden — so they can be moved and unhidden.</summary>
    /// <param name="config">The loaded configuration.</param>
    public static List<TabOrderEntry> BuildTabOrder(AppConfig config)
    {
        var hidden = new HashSet<string>(config.HiddenNativeTabs, StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, title) in DefaultNativeTabs)
        {
            titles[id] = title;
        }
        foreach (var native in config.KnownNativeTabs.Where(n => !string.IsNullOrEmpty(n.Title)))
        {
            titles[native.Id] = native.Title;
        }

        var pool = new List<TabOrderEntry>();
        var pooled = new HashSet<string>(StringComparer.Ordinal);
        void AddNative(string id)
        {
            if (!string.IsNullOrEmpty(id) && pooled.Add(id))
            {
                pool.Add(new TabOrderEntry(
                    id, titles.TryGetValue(id, out var title) ? title : id, true,
                    hidden.Contains(id)));
            }
        }
        foreach (var (id, _) in DefaultNativeTabs)
        {
            AddNative(id);
        }
        foreach (var native in config.KnownNativeTabs)
        {
            AddNative(native.Id);
        }
        foreach (var id in config.HiddenNativeTabs)
        {
            AddNative(id);
        }
        foreach (var tab in config.CustomTabs
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Position))
        {
            if (pooled.Add($"wsgm-custom-{tab.Id}"))
            {
                pool.Add(new TabOrderEntry($"wsgm-custom-{tab.Id}", tab.Name, false, false));
            }
        }
        foreach (var card in config.CardLibraries.Where(c => c is { Enabled: true, Hidden: false }))
        {
            if (pooled.Add($"wsgm-card-{card.ContentId}"))
            {
                pool.Add(new TabOrderEntry($"wsgm-card-{card.ContentId}", card.Name, false, false));
            }
        }

        var byKey = pool.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var result = new List<TabOrderEntry>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in config.LibraryTabOrder)
        {
            if (byKey.TryGetValue(key, out var entry) && used.Add(key))
            {
                result.Add(entry);
            }
        }
        result.AddRange(pool.Where(entry => used.Add(entry.Key)));
        return result;
    }

    /// <summary>A removable Steam library found on a mounted drive.
    /// <paramref name="SteamLabel"/> is the label Steam itself shows for it (config
    /// label, else marker label) — the value a Steam-side rename changes.</summary>
    private sealed record Discovered(
        string ContentId, string Name, List<long> AppIds, char Letter, string SteamLabel);

    /// <summary>Scans every ready drive for a <c>&lt;X&gt;:\SteamLibrary</c> marker and
    /// reads its identity, label and installed app ids. The primary Steam install
    /// has no such subfolder marker, so it is naturally excluded.</summary>
    private static List<Discovered> ScanLibraries()
    {
        var configLabels = ReadConfigLabels();
        // Resolved once per scan: each call opens two volume handles and issues two
        // IOCTLs, and the answer cannot change while a single scan runs.
        var systemDisks = RemovableDriveManager.ResolveSystemDisks();
        var found = new List<Discovered>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || !IsExternalVolume(drive, systemDisks))
                {
                    continue;
                }
                var root = Path.Combine(drive.Name, "SteamLibrary");
                var marker = Path.Combine(root, "libraryfolder.vdf");
                if (!File.Exists(marker))
                {
                    continue;
                }
                var text = File.ReadAllText(marker);
                var contentId = SteamLibraryVdf.ValuesOf(text, "contentid").FirstOrDefault();
                if (string.IsNullOrEmpty(contentId))
                {
                    continue;
                }
                var letter = char.ToUpperInvariant(drive.Name[0]);
                var label = SteamLibraryVdf.ValuesOf(text, "label").FirstOrDefault() ?? "";
                var name = ResolveName(label, contentId, configLabels, drive, letter);
                var appIds = ReadAcfAppIds(Path.Combine(root, "steamapps"));
                var steamLabel = configLabels.TryGetValue(contentId, out var configLabel)
                    && !string.IsNullOrWhiteSpace(configLabel)
                    ? configLabel.Trim() : label.Trim();
                found.Add(new Discovered(contentId, name, appIds, letter, steamLabel));
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tabs: could not read {drive.Name}: {ex.Message}");
            }
        }
        return found;
    }

    private static bool IsExternalVolume(DriveInfo drive, HashSet<int> systemDisks)
    {
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
        {
            return false;
        }
        var letter = char.ToUpperInvariant(drive.Name[0]);
        using var volume = NativeStorage.OpenVolumeForQuery(letter);
        if (volume.IsInvalid
            || !NativeStorage.TryGetDeviceNumber(volume, out var type, out var disk)
            || type != NativeStorage.FileDeviceDisk || disk < 0
            || systemDisks.Contains(disk))
        {
            return false;
        }
        // Query access only: GENERIC_READ on \\.\PhysicalDriveN requires elevation and WSGM
        // is asInvoker, so a read handle would be invalid for every disk in a desktop-launched
        // process and no card would ever be discovered. IOCTL_STORAGE_GET_HOTPLUG_INFO needs
        // no access rights, which is why the drive snapshot path opens the same way.
        using var physical = NativeStorage.OpenDiskForQuery(disk);
        return !physical.IsInvalid
            && NativeStorage.TryGetHotplugInfo(physical, out var media, out var hotplug)
            && RemovableDriveManager.Classify(hotplug, media) is not null;
    }

    /// <summary>Maps content id → the Steam-side library label from
    /// <c>config\libraryfolders.vdf</c> (each entry lists <c>label</c> then
    /// <c>contentid</c> in order, so the value lists align by entry). Lets a card
    /// whose on-disk marker has no label still get its real Steam name.</summary>
    private static Dictionary<string, string> ReadConfigLabels()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var steamExe = Steam.ExePath;
        if (steamExe is null)
        {
            return map;
        }
        var configPath = Path.Combine(
            Path.GetDirectoryName(steamExe)!, "config", "libraryfolders.vdf");
        if (!File.Exists(configPath))
        {
            return map;
        }
        try
        {
            var text = File.ReadAllText(configPath);
            var ids = SteamLibraryVdf.ValuesOf(text, "contentid");
            foreach (var id in ids)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    map[id] = SteamLibraryVdf.LabelForContentId(text, id) ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Library tabs: could not read config labels: {ex.Message}");
        }
        return map;
    }

    /// <summary>Picks a tab name: the library's own marker label, else its
    /// Steam-side label from config, else the volume label, else a drive-letter
    /// fallback.</summary>
    private static string ResolveName(
        string markerLabel, string contentId, Dictionary<string, string> configLabels,
        DriveInfo drive, char letter)
    {
        if (!string.IsNullOrWhiteSpace(markerLabel))
        {
            return markerLabel.Trim();
        }
        if (configLabels.TryGetValue(contentId, out var steamLabel)
            && !string.IsNullOrWhiteSpace(steamLabel))
        {
            return steamLabel.Trim();
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)
                && !string.Equals(drive.VolumeLabel, "Games", StringComparison.OrdinalIgnoreCase))
            {
                return drive.VolumeLabel.Trim();
            }
        }
        catch (IOException)
        {
            // No volume label available.
        }
        return $"Library ({letter}:)";
    }

    /// <summary>Reads app ids from <c>appmanifest_&lt;appid&gt;.acf</c> file names — the
    /// id is in the name, so no VDF parsing is needed for membership.</summary>
    private static List<long> ReadAcfAppIds(string steamAppsDir)
    {
        var ids = new List<long>();
        if (!Directory.Exists(steamAppsDir))
        {
            return ids;
        }
        foreach (var file in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var idText = stem["appmanifest_".Length..];
            if (long.TryParse(idText, out var id) && id > 0 && id != 228980)
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>Upserts the scan into the persisted card DB: a new card is added
    /// (enabled), a known one has its name, app ids, last-seen and letter refreshed.
    /// Cards not currently discovered are left untouched (remembered while ejected).</summary>
    private static void MergeDiscovery(AppConfig config, List<Discovered> discovered)
    {
        var db = config.CardLibraries;
        var presentIds = discovered.Select(static card => card.ContentId)
            .ToHashSet(StringComparer.Ordinal);
        config.ForgottenInsertedCardIds.RemoveAll(id => !presentIds.Contains(id));
        var now = DateTime.UtcNow.Ticks;
        foreach (var card in discovered)
        {
            if (config.ForgottenInsertedCardIds.Contains(card.ContentId, StringComparer.Ordinal))
            {
                continue;
            }
            var existing = db.FirstOrDefault(
                c => string.Equals(c.ContentId, card.ContentId, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new CardLibraryConfig { ContentId = card.ContentId, Enabled = true };
                db.Add(existing);
            }
            if (string.IsNullOrWhiteSpace(existing.Name))
            {
                existing.Name = card.Name;
            }
            // Two-way name sync with Steam, keyed by LastSteamLabel (the label last
            // seen in agreement with Name):
            //   - in sync (Name == LastSteamLabel) and Steam's label changed → the
            //     user renamed it in Steam; follow it here.
            //   - Steam's label caught up with a WSGM-side rename → record the new
            //     agreement. Until then a lagging libraryfolders.vdf (Steam flushes
            //     on exit) must NOT revert the WSGM rename, which is why an
            //     out-of-sync pair adopts nothing.
            if (!string.IsNullOrWhiteSpace(card.SteamLabel))
            {
                if (string.Equals(existing.Name, existing.LastSteamLabel, StringComparison.Ordinal)
                    && !string.Equals(card.SteamLabel, existing.LastSteamLabel, StringComparison.Ordinal))
                {
                    Log.Info($"Card {card.ContentId}: following Steam rename "
                        + $"'{existing.Name}' -> '{card.SteamLabel}'.");
                    existing.Name = card.SteamLabel;
                    existing.LastSteamLabel = card.SteamLabel;
                }
                else if (string.Equals(card.SteamLabel, existing.Name, StringComparison.Ordinal))
                {
                    existing.LastSteamLabel = card.SteamLabel;
                }
            }
            existing.AppIds = card.AppIds;
        }
    }
}
