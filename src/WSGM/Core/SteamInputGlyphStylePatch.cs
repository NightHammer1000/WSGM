using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Installs the handheld glyph stylesheet into a Steam document.
/// </summary>
/// <remarks>
/// One patch, one stylesheet, one owned node. This replaces the four mapping-namespace tiers that
/// installed JavaScript resolver objects nothing in Steam consulted, so nothing ever changed on
/// screen. Glyphs are a presentation override and CSS is the entire mechanism; see
/// <c>docs/steam-cef.md</c> and the reference theme at <c>_ref/handheld-controller-glyphs</c>.
/// <para>
/// Coexistence with CSSLoader is a first-class requirement rather than an edge case: WSGM appends a
/// <c>&lt;style&gt;</c> carrying its own id and marker class and removes only that, exactly as
/// CSSLoader does with its own. Neither tool touches the other's nodes, so a user can run both.
/// </para>
/// </remarks>
internal sealed class SteamInputGlyphStylePatch(SteamInputGlyphDeliveryState state) : ISteamUiPatch
{
    /// <summary>Stable id of the one glyph delivery patch.</summary>
    internal const string PatchId = "wsgm.steam-input.glyph-style";

    private readonly SteamInputGlyphDeliveryState _state = state;

    /// <inheritdoc/>
    public string Id => PatchId;

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    /// <remarks>
    /// The window the user is looking at, not SharedJSContext. A stylesheet only affects the
    /// document it is installed in, and SharedJSContext has essentially no DOM — measured at 218
    /// bytes of body on the reference Claw with the Steam Input page open, against 29,555 bytes
    /// here, along with every Valve glyph image the rules key off. Half a megabyte of correct CSS
    /// installed there, verified there, and changed nothing the user could see.
    /// </remarks>
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.MainWindow;

    /// <inheritdoc/>
    public string ResourceKey => "wsgm.steam-input.glyph-style";

    /// <inheritdoc/>
    /// <remarks>
    /// A wider payload bound than the default: the stylesheet inlines every glyph as a data URI, so
    /// its size is set by the artwork rather than by the expression. The importer already caps
    /// individual assets; this is the ceiling on the whole sheet.
    /// </remarks>
    public SteamUiPatchBounds Bounds { get; } = new(
        TimeSpan.FromSeconds(8),
        MaximumExpressionCharacters: 2 * 1024 * 1024,
        MaximumDiagnosticCharacters: 2048);

    /// <inheritdoc/>
    /// <remarks>
    /// The structure this patch depends on is Valve's glyph resource naming, which is stable, plus
    /// two generated Steam class names used for the inline logo container and the configurator row.
    /// Both are checked here so a Steam rebuild that renames them disables the patch instead of
    /// installing rules that silently match nothing.
    /// </remarks>
    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_state.Current is null)
        {
            return new SteamUiPatchProbeResult(
                true,
                false,
                false,
                null,
                "No reviewed handheld glyph profile is selected.");
        }

        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            $$"""
            (()=>{try{
              const rowClass={{SteamCef.JsString(SteamGlyphCss.ControlRowClass)}};
              const logoClass={{SteamCef.JsString(SteamGlyphCss.InlineLogoContainerClass)}};
              const styles=[...document.styleSheets].length;
              let rowSeen=false,logoSeen=false;
              for(const sheet of document.styleSheets){
                let rules;
                try{rules=sheet.cssRules;}catch{continue;}
                if(!rules)continue;
                for(const rule of rules){
                  const text=rule.selectorText;
                  if(!text)continue;
                  rowSeen=rowSeen||text.includes(rowClass);
                  logoSeen=logoSeen||text.includes(logoClass);
                  if(rowSeen&&logoSeen)break;
                }
                if(rowSeen&&logoSeen)break;
              }
              return JSON.stringify({ok:!!document.head,styleSheets:styles,rowClass:rowSeen,logoClass:logoSeen});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchProbeResult(
                false,
                false,
                false,
                null,
                result.Error ?? "Steam MainWindow is unavailable.");
        }

        // Both selector classes are required, not just ok. They are build-coupled: the rules this
        // patch installs are written against them, so a client that renamed either one would accept
        // a stylesheet that matches nothing while the patch reported itself compatible and unique.
        bool compatible = SteamUiPatchEvaluation.IsSuccessful(result.Value, "rowClass", "logoClass");
        return new SteamUiPatchProbeResult(
            true,
            compatible,
            compatible,
            compatible ? $"wsgm-glyph-style-v1:{_state.Current.ProfileId}:{_state.Current.Revision}" : null,
            compatible ? null : SteamUiPatchEvaluation.Bounded(result.Value));
    }

    /// <inheritdoc/>
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_state.Current is not { } presentation)
        {
            return Task.FromResult(new SteamUiPatchOperationResult(
                false,
                "No reviewed handheld glyph profile is selected."));
        }

        string css = SteamGlyphCss.Build(presentation, hideAbsentControls: true);
        if (css.Length == 0)
        {
            return Task.FromResult(new SteamUiPatchOperationResult(
                false,
                "The selected profile produced no glyph rules."));
        }

        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            TargetRole,
            $$"""
            (()=>{try{
              const id={{SteamCef.JsString(SteamGlyphCss.ElementId)}};
              const owned={{SteamCef.JsString(SteamGlyphCss.OwnedClass)}};
              const css={{SteamCef.JsString(css)}};
              if(!document.head)return JSON.stringify({ok:false,error:'document head is absent'});
              const prior=document.getElementById(id);
              if(prior&&!prior.classList.contains(owned))
                return JSON.stringify({ok:false,error:'the glyph style id is owned by something else'});
              const style=prior??document.createElement('style');
              style.id=id;
              style.classList.add(owned);

              // The controller illustration is a background on one div whose class is generated by
              // Steam's build, so the selector is read from Valve's own rules instead of being
              // hardcoded: every rule painting /images/controller/controller_config_controller_*
              // ends in that same class, whatever this build happens to call it. A rebuild that
              // rehashes it is followed automatically, and a build that stops using it simply
              // yields nothing rather than a stale rule.
              let illustration='';
              try{
                const classes=new Set();
                for(const sheet of document.styleSheets){
                  let rules;
                  try{rules=sheet.cssRules;}catch{continue;}
                  if(!rules)continue;
                  for(const rule of rules){
                    const text=rule.cssText||'';
                    if(!text.includes('/images/controller/controller_config_controller'))continue;
                    for(const part of (rule.selectorText||'').split(',')){
                      const last=part.trim().split(/\s+/).pop()||'';
                      if(last.startsWith('.')&&!last.includes(':'))classes.add(last);
                    }
                  }
                }
                // !important because Steam's own rule qualifies the same element with a
                // controller-type ancestor — ".controller_steamcontroller_neptune .rlz-…" — and two
                // classes beat one. Without it the override installs, verifies, and loses the
                // cascade in silence, which is exactly what it did.
                if(classes.size)
                  illustration='\n'+[...classes].join(',\n')
                    +' {\n  background-image: var(--wsgm-controller-full-image) !important;\n}\n';
              }catch{}
              style.textContent=css+illustration;
              if(!prior)document.head.append(style);
              const installed=document.getElementById(id)?.textContent??'';
              return JSON.stringify({
                ok:installed.startsWith(css),
                reused:!!prior,
                illustration:illustration.length>0,
              });
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """,
            "Handheld glyph stylesheet installation failed.",
            cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Verified by asking the browser whether a rule actually matches, not merely whether the node
    /// exists: an installed stylesheet whose selectors match nothing looks identical to a working
    /// one from the outside, and that is exactly the failure a Steam rebuild produces.
    /// </remarks>
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            TargetRole,
            $$"""
            (()=>{try{
              const id={{SteamCef.JsString(SteamGlyphCss.ElementId)}};
              const owned={{SteamCef.JsString(SteamGlyphCss.OwnedClass)}};
              const style=document.getElementById(id);
              if(!style||!style.classList.contains(owned))
                return JSON.stringify({ok:false,error:'the WSGM glyph stylesheet is absent'});
              const sheet=style.sheet;
              const ruleCount=sheet?sheet.cssRules.length:0;
              const foreign=document.querySelectorAll('.css-loader-style').length;
              return JSON.stringify({ok:ruleCount>0,ruleCount,cssLoaderStyles:foreign});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """,
            "Handheld glyph stylesheet verification failed.",
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            TargetRole,
            $$"""
            (()=>{try{
              const owned={{SteamCef.JsString(SteamGlyphCss.OwnedClass)}};
              let removed=0;
              for(const style of [...document.querySelectorAll('style.'+owned)]){
                style.remove();
                removed++;
              }
              return JSON.stringify({ok:document.querySelectorAll('style.'+owned).length===0,removed});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """,
            "Handheld glyph stylesheet removal failed.",
            cancellationToken);
    }
}
