using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One injected library tab: a title + the exact app ids it should contain.
/// WSGM computes the ids (from filters, cards, or genres); Steam renders them.</summary>
/// <param name="Id">Stable unique tab id (e.g. <c>wsgm-card-…</c>).</param>
/// <param name="Title">The tab's display title.</param>
/// <param name="AppIds">The app ids the tab shows.</param>
public sealed record InjectedTab(string Id, string Title, IReadOnlyList<long> AppIds);

/// <summary>Outcome of a tab sync: whether the injection succeeded, and the native
/// Steam tabs the resident script observed in the strip (empty when the library has
/// not rendered yet — callers must treat that as "unknown", not "none").</summary>
/// <param name="Ok">Whether the definitions reached Steam without a script error.</param>
/// <param name="NativeTabs">Native tabs observed in Steam's tab array.</param>
public sealed record TabSyncResult(bool Ok, List<NativeTabConfig> NativeTabs);

/// <summary>Adds real WSGM tabs to Steam's library tab strip — TabMaster's mechanism,
/// re-implemented without Decky and driven from an injected <c>SharedJSContext</c>
/// script (device-verified live). The script captures Steam's webpack registry
/// (<c>webpackChunksteamui</c>), finds React, and hijacks the current dispatcher's
/// <c>useMemo</c> so that whenever the library recomputes its tab array it appends our
/// tabs. Each tab renders a <b>fake in-memory collection</b> (a plain object of the
/// tab's app overviews) through Steam's own grid component — so NO real Steam
/// collection is created. WSGM only supplies <c>window.__wsgm.tabs</c> (id/title/appids),
/// <c>tabOrder</c> (full strip order as tab keys) and <c>hiddenTabs</c> (native ids to
/// omit); the resident script does the patching. Because order and hiding are applied
/// purely by rewriting the tab array (TabMaster's model), a hidden native tab is simply
/// absent from the returned array and reappears untouched once unhidden.
///
/// <para>Fragility is inherent (it rides Steam's minified React internals) and accepted:
/// the useMemo dispatcher slot and the grid component's <c>Library_FilteredByHeader</c>
/// marker are the two things that can shift on a major Steam UI update. A kill switch
/// (<c>window.__wsgm.disableTabs()</c>) and a Steam restart both fully recover.</para></summary>
public static class SteamLibraryTabs
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <summary>Installs the resident tab-injection script (idempotent) and sets the
    /// current tab list, strip order, and hidden native tabs. Passing an empty tab
    /// list clears WSGM's tabs on the next library render. Runs in
    /// <c>SharedJSContext</c>, where the webpack registry and React live.</summary>
    /// <param name="tabs">The tabs to show, in order.</param>
    /// <param name="order">Full strip order as tab keys (native + wsgm ids); tabs not
    /// listed keep their natural order after the listed ones.</param>
    /// <param name="hiddenNativeIds">Native Steam tab ids to omit from the strip.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<TabSyncResult> SyncTabsAsync(
        IReadOnlyList<InjectedTab> tabs,
        IReadOnlyList<string> order,
        IReadOnlyList<string> hiddenNativeIds,
        CancellationToken cancellationToken = default)
    {
        var expression =
            "(async()=>{try{" + ResidentSetup +
            "window.__wsgm.tabs=" + BuildDefs(tabs) + ";" +
            "window.__wsgm.tabOrder=" + BuildStrings(order) + ";" +
            "window.__wsgm.hiddenTabs=" + BuildStrings(hiddenNativeIds) + ";" +
            "window.__wsgm.lastTabError=null;" +
            "if(window.__wsgm.forceRerender)window.__wsgm.forceRerender();" +
            "await new Promise(r=>setTimeout(r,100));if(window.__wsgm.lastTabError)throw new Error(window.__wsgm.lastTabError);" +
            "return JSON.stringify({ok:true,installed:!!window.__wsgm.tabsInstalled," +
            "count:(window.__wsgm.tabs||[]).length," +
            "nativeTabs:(window.__wsgm.nativeTabs||[])});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.stack)||e)});}})()";

        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new TabSyncResult(false, []);
        }
        if (result.Value is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(result.Value);
                var root = document.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    var count = root.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                    Log.Info($"Library tabs injected: {count} tabs.");
                    return new TabSyncResult(true, ParseNativeTabs(root));
                }
                var err = root.TryGetProperty("err", out var e) ? e.GetString() : null;
                Log.Warn($"Library tab injection failed: {err}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tab injection parse failed: {ex.Message}");
            }
        }
        return new TabSyncResult(false, []);
    }

    /// <summary>Pushes only the strip order and hidden-native set into an already
    /// installed session and rerenders — cheap enough for interactive reordering (no
    /// filter re-evaluation). Returns false when the resident script is not installed
    /// yet; the caller should fall back to a full sync.</summary>
    /// <param name="order">Full strip order as tab keys.</param>
    /// <param name="hiddenNativeIds">Native Steam tab ids to omit from the strip.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<bool> PushOrderAsync(
        IReadOnlyList<string> order,
        IReadOnlyList<string> hiddenNativeIds,
        CancellationToken cancellationToken = default)
    {
        var expression =
            "(()=>{try{var W=window.__wsgm;" +
            "if(!W||!W.tabsInstalled)return JSON.stringify({ok:false,err:'not installed'});" +
            "W.tabOrder=" + BuildStrings(order) + ";" +
            "W.hiddenTabs=" + BuildStrings(hiddenNativeIds) + ";" +
            "W.forceRerender&&W.forceRerender();" +
            "return JSON.stringify({ok:true});}" +
            "catch(e){return JSON.stringify({ok:false,err:String(e)});}})()";
        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            return document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            Log.Warn($"Library tab order push parse failed: {ex.Message}");
            return false;
        }
    }

    private static List<NativeTabConfig> ParseNativeTabs(JsonElement root)
    {
        var natives = new List<NativeTabConfig>();
        if (root.TryGetProperty("nativeTabs", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                var id = element.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                var title = element.TryGetProperty("title", out var t) ? t.GetString() : null;
                natives.Add(new NativeTabConfig { Id = id, Title = title ?? "" });
            }
        }
        return natives;
    }

    /// <summary>Removes WSGM's dispatcher hook and tab definitions for the current
    /// Steam session. Best-effort; a Steam restart remains the outer recovery path.</summary>
    internal static Task<CefEvalResult> DisableAsync(CancellationToken cancellationToken = default)
        => SteamUiTransportSession.EvaluateAsync(
            "(async()=>{try{const W=window.__wsgm;if(W){W.tabs=[];W.tabOrder=[];W.hiddenTabs=[];W.forceRerender&&W.forceRerender();await new Promise(r=>setTimeout(r,100));W.suspendTabs&&W.suspendTabs();}return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false,err:String(e)});}})()",
            Budget, cancellationToken);

    private static string BuildDefs(IReadOnlyList<InjectedTab> tabs)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < tabs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            var t = tabs[i];
            sb.Append("{id:").Append(SteamCef.JsString(t.Id))
                .Append(",title:").Append(SteamCef.JsString(t.Title))
                .Append(",appids:[")
                .Append(string.Join(",", t.AppIds.Select(a => a.ToString(CultureInfo.InvariantCulture))))
                .Append("]}");
        }
        return sb.Append(']').ToString();
    }

    private static string BuildStrings(IReadOnlyList<string> values)
        => "[" + string.Join(",", values.Select(SteamCef.JsString)) + "]";

    // The resident setup: define window.__wsgm, capture React, install helpers and the
    // useMemo dispatcher hijack (once). Guarded by window.__wsgm.tabsInstalled so
    // re-running (each sync / after reconnect) only refreshes the functions. Namespaced
    // under window.__wsgm to coexist with CSSLoader. This is the exact script verified
    // live against Steam's Big Picture library.
    //
    // React discovery MUST load modules via req(id) over req.m: the require captured
    // from a pushed chunk exposes an EMPTY module cache (req.c has zero entries,
    // verified live), so a cache-only exports scan finds nothing — ever. A past review
    // "hardened" this into req.c and silently broke every injection ("React not
    // found") until the next device test. Do not switch this back to a cache scan.
    private const string ResidentSetup = """
        var W=window.__wsgm=window.__wsgm||{};
        if(W.tabsDisabled)throw new Error('WSGM library tabs are disabled for this Steam session');
        if(!W._react){
          if(!window.webpackChunksteamui)throw new Error('webpack not ready');
          var req;window.webpackChunksteamui.push([[Symbol('wsgm')],{},function(r){req=r;}]);
          if(!req)throw new Error('no require');
          for(var id of Object.keys(req.m)){var e;try{e=req(id);}catch(x){continue;}
            if(e&&e.createElement&&e.useMemo&&e.version){W._react=e;break;}}
          if(!W._react)throw new Error('React not found');
        }
        var React=W._react;
        W.findInTree=function(node,pred,depth){depth=depth||0;
          if(depth>40||node==null)return null;
          if(Array.isArray(node)){for(var n of node){var r=W.findInTree(n,pred,depth+1);if(r)return r;}return null;}
          if(typeof node!=='object')return null;
          try{if(pred(node))return node;}catch(e){}
          var kids=node.props&&node.props.children;
          return kids?W.findInTree(kids,pred,depth+1):null;};
        W.makeCollection=function(id,title,appids){var as=window.appStore;
          var apps=appids.map(function(a){return as.GetAppOverviewByAppID(a);}).filter(Boolean);
          var map=new Map();apps.forEach(function(a){map.set(a.appid,a);});
          return {id:id,displayName:title,allApps:apps,visibleApps:apps.slice(),apps:map,
            AsDeletableCollection:function(){return null;},AsDragDropCollection:function(){return null;},
            AsEditableCollection:function(){return null;},bAllowsDragAndDrop:false,bIsDeletable:false,
            bIsDynamic:false,bIsEditable:false,
            GetAppCountWithToolsFilter:function(f){return (f&&f.Matches)?apps.filter(function(x){return f.Matches(x);}).length:apps.length;}};};
        W.patchTabs=function(v){try{
          var isNested=Array.isArray(v)&&Array.isArray(v[0]);
          var tabs=isNested?v[0]:v;
          if(!Array.isArray(tabs))return v;
          tabs=tabs.filter(function(t){return !(t&&typeof t.id==='string'&&t.id.startsWith('wsgm-'));});
          var tmpl=tabs.find(function(t){return t&&t.id==='AllGames';});
          if(!tmpl)return v;
          W.nativeTabs=tabs.map(function(t){return {id:String(t&&t.id),title:(t&&typeof t.title==='string')?t.title:''};});
          var g=W.findInTree(tmpl.content,function(el){return el&&el.type&&el.type.toString&&el.type.toString().includes('Library_FilteredByHeader');});
          if(g){W._gridType=g.type;W._gridProps=g.props;}
          var existing=new Set(tabs.map(function(t){return t&&t.id;}));
          var add=[];
          for(var d of (W.tabs||[])){
            if(existing.has(d.id))continue;
            existing.add(d.id);
            var coll=W.makeCollection(d.id,d.title,d.appids||[]);
            var content=tmpl.content;
            if(!W._gridType||!React)throw new Error('Steam library grid component not found');
            content=React.createElement(W._gridType,Object.assign({},W._gridProps,{collection:coll}));
            (function(def,content){add.push({title:def.title,id:def.id,content:content,footer:tmpl.footer,
              renderTabAddon:function(){return React?React.createElement('span',null,String((def.appids||[]).length)):null;}});})(d,content);
          }
          var order=W.tabOrder||[];
          var hidden=new Set(W.hiddenTabs||[]);
          if(!add.length&&!order.length&&!hidden.size)return v;
          var all=tabs.concat(add);
          var pool=new Map();
          for(var p of all)pool.set(p.id,p);
          var out=[];var used=new Set();
          for(var oid of order){var ot=pool.get(oid);if(!ot)continue;used.add(oid);if(!hidden.has(oid))out.push(ot);}
          for(var rest of all){if(used.has(rest.id)||hidden.has(rest.id))continue;out.push(rest);}
          if(!out.length)out=tabs;
          return isNested?[out,v[1]]:out;
        }catch(e){W.lastTabError=String((e&&e.stack)||e);return v;}};
        if(!W.tabsInstalled){
          var internals=React.__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE;
          if(!internals||!('H' in internals))throw new Error('React dispatcher slot not found');
          var wrapped=new WeakMap(),unwrapped=new WeakMap();var cur=internals.H;
          Object.defineProperty(internals,'H',{configurable:true,
            get:function(){var c=cur;if(!c||typeof c!=='object'||typeof c.useMemo!=='function')return c;
              var w=wrapped.get(c);if(!w){var realUseMemo=c.useMemo;w=Object.create(c);
                w.useMemo=function(fn,deps){var d=Array.isArray(deps)?deps.concat(W.revision||0):deps;
                  return realUseMemo.call(c,function(){return W.patchTabs(fn());},d);};
                wrapped.set(c,w);unwrapped.set(w,c);}return w;},
            set:function(v){cur=unwrapped.get(v)||v;}});
          W.suspendTabs=function(){try{Object.defineProperty(internals,'H',{configurable:true,writable:true,value:cur});}catch(e){}
            W.tabs=[];W.tabsInstalled=false;};
          W.disableTabs=function(){W.suspendTabs();W.tabsDisabled=true;};
          W.forceRerender=function(){
            W.revision=(W.revision||0)+1;
            window.dispatchEvent(new Event('resize'));
          };
          W.tabsInstalled=true;
        }
        """;
}
