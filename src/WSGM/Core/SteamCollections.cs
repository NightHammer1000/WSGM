using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One Steam user collection (which Steam renders as a library
/// category/tab).</summary>
/// <param name="Id">Steam's collection id (e.g. <c>uc-…</c>).</param>
/// <param name="Name">The display name.</param>
/// <param name="AppIds">The app ids currently in the collection.</param>
public sealed record SteamCollectionInfo(string Id, string Name, IReadOnlyList<long> AppIds);

/// <summary>Reads Steam library data and cleans up collection IDs created by the
/// retired collection-backed tab implementation. New tabs are injected by
/// <see cref="SteamLibraryTabs"/> and never create user collections.</summary>
public static class SteamCollections
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <summary>Lists the current user collections and their app ids.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<IReadOnlyList<SteamCollectionInfo>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        const string expression =
            "(()=>{try{const cs=collectionStore;" +
            "const cols=(cs.userCollections||[]).map(c=>({id:c.id,name:c.displayName," +
            "appids:(c.allApps||c.visibleApps||[]).map(a=>a.appid)}));" +
            "return JSON.stringify({ok:true,collections:cols});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return Array.Empty<SteamCollectionInfo>();
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("collections", out var cols))
            {
                return Array.Empty<SteamCollectionInfo>();
            }
            var list = new List<SteamCollectionInfo>();
            foreach (var col in cols.EnumerateArray())
            {
                var id = col.GetProperty("id").GetString() ?? "";
                var name = col.GetProperty("name").GetString() ?? "";
                var appIds = new List<long>();
                foreach (var appId in col.GetProperty("appids").EnumerateArray())
                {
                    if (appId.TryGetInt64(out var value))
                    {
                        appIds.Add(value);
                    }
                }
                list.Add(new SteamCollectionInfo(id, name, appIds));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam collections list parse failed: {ex.Message}");
            return Array.Empty<SteamCollectionInfo>();
        }
    }

    /// <summary>Outcome of evaluating a compiled filter over the library.</summary>
    /// <param name="Reachable">Whether Steam's debug port answered.</param>
    /// <param name="Ok">Whether Steam evaluated and returned a valid result.</param>
    /// <param name="AppIds">The matching app ids (empty is a valid successful result).</param>
    public readonly record struct FilterEvalResult(bool Reachable, bool Ok, IReadOnlyList<long> AppIds);

    /// <summary>Evaluates multiple compiled filters in one CEF exchange.</summary>
    /// <param name="filterExpressions">Self-contained filter IIFEs.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<IReadOnlyList<FilterEvalResult>> EvaluateFiltersAsync(
        IReadOnlyList<string> filterExpressions, CancellationToken cancellationToken = default)
    {
        if (filterExpressions.Count == 0)
        {
            return Array.Empty<FilterEvalResult>();
        }
        var expression = "(()=>JSON.stringify({values:[" + string.Join(",", filterExpressions
            .Select(static value => "JSON.parse((" + value + "))")) + "]}))()";
        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return Enumerable.Repeat(
                new FilterEvalResult(false, false, Array.Empty<long>()), filterExpressions.Count).ToList();
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var values = document.RootElement.GetProperty("values");
            var output = new List<FilterEvalResult>();
            foreach (var value in values.EnumerateArray())
            {
                if (!value.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                    || !value.TryGetProperty("appids", out var appids))
                {
                    output.Add(new FilterEvalResult(true, false, Array.Empty<long>()));
                    continue;
                }
                var ids = new List<long>();
                foreach (var appid in appids.EnumerateArray())
                {
                    if (appid.TryGetInt64(out var id))
                    {
                        ids.Add(id);
                    }
                }
                output.Add(new FilterEvalResult(true, true, ids));
            }
            while (output.Count < filterExpressions.Count)
            {
                output.Add(new FilterEvalResult(true, false, Array.Empty<long>()));
            }
            return output;
        }
        catch (Exception ex)
        {
            Log.Warn($"Batched filter evaluation parse failed: {ex.Message}");
            return Enumerable.Repeat(
                new FilterEvalResult(true, false, Array.Empty<long>()), filterExpressions.Count).ToList();
        }
    }

    /// <summary>One app's id and display name (for whitelist/blacklist pickers,
    /// card "view games" name resolution, and the artwork changer's target list).</summary>
    /// <param name="AppId">The Steam app id (a shortcut's generated id for shortcuts).</param>
    /// <param name="Name">The display name.</param>
    /// <param name="Shortcut">True for a non-Steam shortcut, whose id has no Steam
    /// store page (SteamGridDB lookups must go by name instead).</param>
    public sealed record AppInfo(long AppId, string Name, bool Shortcut = false);

    /// <summary>Lists the user's games AND non-Steam shortcuts (id + name), sorted by
    /// name — the source for the whitelist/blacklist app pickers, for resolving a
    /// card's installed ids to names, and for the artwork changer. Shortcuts come from
    /// the all-apps collection (the type-games collection excludes them) and are
    /// flagged, since their generated ids mean nothing outside this machine.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<IReadOnlyList<AppInfo>> GetGamesAsync(
        CancellationToken cancellationToken = default)
    {
        const string expression =
            "(()=>{try{const cs=collectionStore;" +
            "const g=cs.GetCollection('type-games');" +
            "const games=(g&&(g.allApps||g.visibleApps))||[];" +
            "const ids=new Set(games.map(a=>a.appid));" +
            "const ac=cs.allAppsCollection;" +
            "const all=(ac&&(ac.allApps||ac.visibleApps))||games;" +
            "const out=[];const seen=new Set();" +
            "for(const a of all){" +
            "const sc=typeof a.BIsShortcut==='function'?!!a.BIsShortcut():a.appid>=2147483648;" +
            "if(!ids.has(a.appid)&&!sc)continue;" +
            "if(seen.has(a.appid))continue;seen.add(a.appid);" +
            "out.push({id:a.appid,name:a.display_name||String(a.appid),sc:sc});}" +
            "return JSON.stringify({ok:true,apps:out});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return Array.Empty<AppInfo>();
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("apps", out var apps))
            {
                return Array.Empty<AppInfo>();
            }
            var list = new List<AppInfo>();
            foreach (var app in apps.EnumerateArray())
            {
                if (app.GetProperty("id").TryGetInt64(out var id))
                {
                    var shortcut = app.TryGetProperty("sc", out var sc)
                        && sc.ValueKind == JsonValueKind.True;
                    list.Add(new AppInfo(
                        id,
                        app.GetProperty("name").GetString() ?? id.ToString(CultureInfo.InvariantCulture),
                        shortcut));
                }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam games list parse failed: {ex.Message}");
            return Array.Empty<AppInfo>();
        }
    }

    /// <summary>One store tag (genre) present in the library.</summary>
    /// <param name="TagId">Steam's numeric tag id.</param>
    /// <param name="Name">The localized tag name.</param>
    /// <param name="Count">How many library games carry it.</param>
    public sealed record TagInfo(int TagId, string Name, int Count);

    /// <summary>Lists the store tags (genres) actually used in the library, with their
    /// localized names and game counts, most-used first — the source for the Tag
    /// filter's multi-select.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<IReadOnlyList<TagInfo>> GetLibraryTagsAsync(
        CancellationToken cancellationToken = default)
    {
        const string expression =
            "(()=>{try{const cs=collectionStore,as=appStore;" +
            "const g=cs.GetCollection('type-games');" +
            "const apps=(g&&(g.allApps||g.visibleApps))||[];" +
            "const m=as.m_mapStoreTagLocalization||{};const byTag={};" +
            "for(const a of apps)for(const t of (a.store_tag||[])){const nm=m[t];if(!nm)continue;" +
            "(byTag[t]=byTag[t]||{id:t,name:nm,count:0}).count++;}" +
            "const out=Object.values(byTag).sort((a,b)=>b.count-a.count);" +
            "return JSON.stringify({ok:true,tags:out});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return Array.Empty<TagInfo>();
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("tags", out var tags))
            {
                return Array.Empty<TagInfo>();
            }
            var list = new List<TagInfo>();
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.GetProperty("id").TryGetInt32(out var id))
                {
                    var name = tag.GetProperty("name").GetString() ?? "";
                    var count = tag.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
                    if (name.Length > 0)
                    {
                        list.Add(new TagInfo(id, name, count));
                    }
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam tags list parse failed: {ex.Message}");
            return Array.Empty<TagInfo>();
        }
    }

}
