using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Adds Name / Size / Type sort buttons to the header of Big Picture's
/// download queue ("Up Next"), reordering the queue through Steam's own
/// <c>SteamClient.Downloads.SetQueueIndex</c>. The device-verified findings this
/// script rests on — the <c>Focusable</c> requirement, the JSX-runtime injection
/// point, the tight component predicates, the whole-pending-list scope and the
/// unknown-size ranking — are in <c>docs\steam-cef.md</c> §12; re-probe with
/// <c>tools/WsgmLibTest/run-prod-sort.mjs</c> before shipping a change here.</summary>
internal static class SteamDownloadSort
{
    internal static string InstallExpression =>
        "(()=>{try{" + ResidentSetup
        + "return W.dlSortInstall();}"
        + "catch(e){return JSON.stringify({ok:false,err:String((e&&e.stack)||e)});}})()";

    internal const string RemoveExpression =
        "(()=>{try{var W=window.__wsgm;if(W&&W.dlSortRemove)W.dlSortRemove();"
        + "return JSON.stringify({ok:true});}"
        + "catch(e){return JSON.stringify({ok:false,err:String(e)});}})()";

    // The resident script. Guarded by dlSortVer so re-running only refreshes the
    // functions — bump BOTH literals below ("W.dlSortVer!==1" and "W.dlSortVer=1")
    // whenever this text changes, or a live Steam session keeps running the OLD
    // functions until the client restarts (same rule as the badge and Wi-Fi scripts).
    // Every shape decision in the script is a device-verified finding: docs\steam-cef.md §12.
    private const string ResidentSetup = """
        var W=window.__wsgm=window.__wsgm||{};
        if(W.dlSortVer!==1){
          W.dlSortVer=1;
          W.dlSortToken='#Downloads_Section_Current';
          W.dlSortState={key:null,dir:1,busy:false};
          W.dlSortReq=function(){
            if(!W._req){var r;window.webpackChunksteamui.push([[Symbol('wsgmdl')],{},function(q){r=q;}]);W._req=r;}
            return W._req;
          };
          W.dlSortSrc=function(v){try{var f=typeof v==='function'?v:(v&&v.render?v.render:null);return f?Function.prototype.toString.call(f):'';}catch(e){return '';}};
          W.dlSortScan=function(){
            var req=W.dlSortReq();
            if(!req)throw new Error('no webpack require');
            if(W._react&&W._focusable&&W._dlIdx!==undefined)return;
            for(var id of Object.keys(req.m)){
              var e;try{e=req(id);}catch(x){continue;}
              if(!e||typeof e!=='object')continue;
              if(!W._react&&e.createElement&&e.useMemo&&e.version)W._react=e;
              var ks;try{ks=Object.keys(e);}catch(x){continue;}
              for(var k of ks){
                var v;try{v=e[k];}catch(x){continue;}
                if(!v)continue;
                if(!W._focusable&&typeof v==='function'){
                  var s=W.dlSortSrc(v);
                  if(s.length<1500&&s.indexOf('class')!==0
                    &&s.indexOf('"flow-children"')!==-1&&s.indexOf('onActivate:')!==-1
                    &&s.indexOf('focusClassName')!==-1&&s.indexOf('focusWithinClassName')!==-1)W._focusable=v;
                }
                if(W._dlIdx===undefined&&typeof v==='object'&&v.k_EAppUpdateProgress_Download!==undefined)W._dlIdx=v.k_EAppUpdateProgress_Download;
              }
            }
            if(W._dlIdx===undefined)W._dlIdx=2;
          };
          // Bytes LEFT to download, not the total: the queue is about what is still
          // coming down the wire. Returns -1 for "Steam has not planned this app yet"
          // (every bytes_total still 0, which is what a freshly restarted client
          // reports for a queued-but-not-started app) so those can be parked instead
          // of being ranked as the smallest download.
          W.dlSortBytes=function(t){
            var i=W._dlIdx,total=0,done=0;
            for(var x of (t.update_type_info||[])){
              var p=x.progress&&x.progress[i];
              if(!p)continue;
              total+=p.bytes_total||0;done+=p.bytes_in_progress||0;
            }
            if(total<=0)return -1;
            return Math.max(0,total-done);
          };
          W.dlSortName=function(t){var o=window.appStore&&appStore.GetAppOverviewByAppID(t.appid);return (o&&o.display_name?o.display_name:String(t.appid)).toLocaleLowerCase();};
          W.dlSortKind=function(t){return t.buildid===0?0:1;};
          // Direction is applied INSIDE each comparator: items with an unknown size
          // must stay at the end in both directions, which an outer sign flip cannot
          // express.
          W.dlSortKeys=[
            {id:'name',label:'NAME',cmp:function(a,b,d){return d*W.dlSortName(a).localeCompare(W.dlSortName(b));}},
            {id:'size',label:'SIZE',cmp:function(a,b,d){
              var x=W.dlSortBytes(a),y=W.dlSortBytes(b);
              if(x<0&&y<0)return W.dlSortName(a).localeCompare(W.dlSortName(b));
              if(x<0)return 1;
              if(y<0)return -1;
              return d*(x-y)||W.dlSortName(a).localeCompare(W.dlSortName(b));
            }},
            {id:'type',label:'TYPE',cmp:function(a,b,d){return d*(W.dlSortKind(a)-W.dlSortKind(b))||W.dlSortName(a).localeCompare(W.dlSortName(b));}}
          ];
          // The whole pending list, not just the running queue: scheduled and
          // unqueued entries are part of what the user sees on the page, so they are
          // sorted in with everything else. Assigning them a queue index is what
          // dragging them into the queue does in Steam's own UI.
          W.dlSortQueue=function(){
            if(!window.downloadsStore)return [];
            var s=downloadsStore,seen={},out=[];
            var add=function(list){
              for(var t of (list||[])){
                if(!t||t.completed)continue;
                if(seen[t.appid])continue;
                seen[t.appid]=1;out.push(t);
              }
            };
            add(s.QueuedTransfers);add(s.UnqueuedTransfers);add(s.ScheduledTransfers);
            // Stable starting point: queued entries keep their order, everything
            // unqueued follows in the order Steam listed it.
            return out.sort(function(a,b){
              var x=a.queue_index<0?1e9:a.queue_index,y=b.queue_index<0?1e9:b.queue_index;
              return x-y;
            });
          };
          W.applyDownloadSort=function(keyId){
            var st=W.dlSortState;
            if(st.busy)return;
            st.dir=st.key===keyId?-st.dir:1;
            st.key=keyId;
            st.busy=true;
            W.dlSortRerender();
            var items=W.dlSortQueue();
            if(items.length<2){st.busy=false;W.dlSortRerender();return;}
            // Always renumber from 0: the list now includes unqueued/scheduled entries
            // whose queue_index is -1, so seeding from items[0] could hand
            // SetQueueIndex a negative index.
            var start=0;
            var def=W.dlSortKeys.filter(function(k){return k.id===keyId;})[0];
            var sorted=items.slice().sort(function(a,b){return def.cmp(a,b,st.dir);});
            var i=0;
            var step=function(){
              if(i>=sorted.length){st.busy=false;W.dlSortRerender();return;}
              try{
                SteamClient.Downloads.SetQueueIndex(sorted[i].appid,start+i,downloadsStore.CurrentViewingRemoteClientID);
              }catch(e){}
              i++;
              setTimeout(step,120);
            };
            step();
          };
          W.dlSortBar=function(){
            var R=W._react,F=W._focusable,st=W.dlSortState;
            var kids=[R.createElement('span',{key:'cap',style:{fontSize:'11px',letterSpacing:'.5px',color:'#8ba6b8',marginRight:'2px'}},'SORT:')];
            W.dlSortKeys.forEach(function(k){
              var on=st.key===k.id;
              kids.push(R.createElement(F,{
                key:k.id,
                onActivate:function(){W.applyDownloadSort(k.id);},
                style:{font:'inherit',fontSize:'11px',letterSpacing:'.5px',lineHeight:'1',padding:'5px 9px',
                  border:'1px solid '+(on?'rgba(103,193,245,.55)':'rgba(255,255,255,.18)'),borderRadius:'2px',
                  background:on?'rgba(103,193,245,.20)':'rgba(255,255,255,.07)',
                  color:on?'#67c1f5':'#c6d4df',cursor:'pointer',opacity:st.busy?0.5:1}
              },k.label+(on?(st.dir>0?' ↑':' ↓'):'')));
            });
            return R.createElement(F,{key:'wsgm-sort','flow-children':'row',
              style:{display:'flex',alignItems:'center',gap:'6px',flex:'0 0 auto',paddingLeft:'12px'}},kids);
          };
          W.dlSortWrap=function(orig){
            var w=function(type,props,key){
              if(props&&props.sectionTitle===W.dlSortToken&&props.count!==undefined&&props.labelId!==undefined){
                try{
                  var hdr=orig(type,Object.assign({},props,{style:Object.assign({},props.style,{flex:'1 1 auto',minWidth:0})}),key);
                  // paddingRight matches the header's own 16px gutter so the bar lines
                  // up with the right edge of the rows, not the window edge.
                  return W._react.createElement('div',
                    {style:{display:'flex',alignItems:'center',width:'100%',paddingRight:'16px',boxSizing:'border-box'}},
                    hdr,W.dlSortBar());
                }catch(e){}
              }
              return orig.apply(this,arguments);
            };
            w.__wsgmDlOrig=orig;
            return w;
          };
          W.dlSortRerender=function(){
            try{
              var mgr=window.g_PopupManager;
              if(!mgr)return;
              for(var p of Array.from(mgr.GetPopups())){
                var d=p.m_popup&&p.m_popup.document;
                if(!d)continue;
                var row=d.querySelector('[data-rbd-draggable-id]');
                if(!row)continue;
                var fk=Object.keys(row).filter(function(k){return k.indexOf('__reactFiber$')===0;})[0];
                if(!fk)continue;
                var f=row[fk];
                while(f.return)f=f.return;
                var seen=0;
                (function visit(n,depth){
                  if(!n||depth>500||seen>12)return;
                  var t=n.type;
                  if(n.stateNode&&typeof t==='function'&&t.prototype&&t.prototype.GetStorageKey){
                    try{n.stateNode.forceUpdate();seen++;}catch(e){}
                  }
                  visit(n.child,depth+1);visit(n.sibling,depth+1);
                })(f,0);
              }
            }catch(e){}
          };
          W.dlSortInstall=function(){
            W.dlSortScan();
            if(!W._react)return JSON.stringify({ok:false,err:'React not found'});
            if(!W._focusable)return JSON.stringify({ok:false,err:'Focusable not found'});
            if(!W.dlSortPatched){
              W.dlSortPatched=[];
              var req=W.dlSortReq();
              for(var id of Object.keys(req.m)){
                var e;try{e=req(id);}catch(x){continue;}
                if(!e||typeof e!=='object')continue;
                var jx,jxs;
                try{jx=e.jsx;jxs=e.jsxs;}catch(x){continue;}
                if(typeof jx!=='function'||typeof jxs!=='function')continue;
                try{
                  var did=false;
                  if(!jx.__wsgmDlOrig){e.jsx=W.dlSortWrap(jx);did=true;}
                  if(!jxs.__wsgmDlOrig){e.jsxs=W.dlSortWrap(jxs);did=true;}
                  if(did)W.dlSortPatched.push(e);
                }catch(x){}
              }
            }
            W.dlSortRerender();
            return JSON.stringify({ok:true,runtimes:(W.dlSortPatched||[]).length});
          };
          W.dlSortRemove=function(){
            var unwrap=function(f){while(f&&f.__wsgmDlOrig)f=f.__wsgmDlOrig;return f;};
            for(var e of (W.dlSortPatched||[])){
              try{e.jsx=unwrap(e.jsx);e.jsxs=unwrap(e.jsxs);}catch(x){}
            }
            W.dlSortPatched=null;
            W.dlSortState={key:null,dir:1,busy:false};
            W.dlSortRerender();
          };
        }
        """;
}

/// <summary>Owns the download-queue JSX wrapper through the shared patch lifecycle.</summary>
internal sealed class SteamDownloadSortPatch : ISteamUiPatch
{
    /// <summary>The download sorter's stable patch id.</summary>
    internal const string PatchId = "wsgm.download-sort";

    public string Id => PatchId;

    public int Version => 1;

    // The queue is rendered into the Big Picture document, but it is rendered BY SharedJSContext:
    // the jsx-runtime module this patch wraps, the module registry it comes from and the React
    // reconciler that re-renders the queue all live there, and the Big Picture window carries the
    // DOM and no webpack global at all. Addressing the window instead leaves the probe's runtime
    // check permanently false, so the sorter reports Incompatible and never installs.
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    public string ResourceKey => "steam.downloads.jsx-runtime";

    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            "(()=>{try{const W=window.__wsgm;return JSON.stringify({ok:true,"
                + "runtime:!!window.webpackChunksteamui,"
                + "owned:!!(W&&Array.isArray(W.dlSortPatched)&&W.dlSortPatched.length)});"
                + "}catch(e){return JSON.stringify({ok:false,error:String(e)});}})()",
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchProbeResult(
                false,
                false,
                false,
                null,
                result.Error ?? "SharedJSContext is unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            bool compatible = root.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("runtime", out JsonElement runtime)
                && runtime.ValueKind == JsonValueKind.True;
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? "download-sort-v1:jsx-runtime+focusable+queue-header" : null,
                compatible ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            SteamDownloadSort.InstallExpression,
            "Download queue sort installation failed.",
            cancellationToken);

    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            "(()=>{const W=window.__wsgm;return JSON.stringify({ok:!!(W"
                + "&&Array.isArray(W.dlSortPatched)&&W.dlSortPatched.length)});})()",
            "Download queue sort verification failed.",
            cancellationToken);

    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            SteamDownloadSort.RemoveExpression,
            "Download queue sort removal failed.",
            cancellationToken);

    private static Task<SteamUiPatchOperationResult> EvaluateAsync(
        SteamUiPatchContext context,
        string expression,
        string fallback,
        CancellationToken cancellationToken) => SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            fallback,
            cancellationToken);
}
