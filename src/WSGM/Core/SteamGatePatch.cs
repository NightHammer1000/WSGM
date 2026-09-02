using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Shared JavaScript fragments for read-only webpack structural probes.</summary>
internal static class SteamUiProbeJs
{
    /// <summary>Opens a probe IIFE and captures webpack's require via a throwaway chunk push.</summary>
    /// <param name="chunkLabel">The stable chunk-label prefix, kept per probe for live diagnostics.</param>
    internal static string Preamble(string chunkLabel) => $$"""
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["{{chunkLabel}}"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
        """;

    /// <summary>The preamble plus the factory-source token counter structural probes share.</summary>
    /// <param name="chunkLabel">The stable chunk-label prefix, kept per probe for live diagnostics.</param>
    internal static string CountingPreamble(string chunkLabel) => $$"""
        {{Preamble(chunkLabel)}}
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
        """;
}

/// <summary>
/// One registered Steam service/store gate, driven entirely by data: a probe expression with a
/// compatibility predicate over its JSON, and the injected gate's install/status/remove surface.
/// </summary>
/// <remarks>
/// The behavior every gate shares lives here once: the probe skeleton, the
/// <c>bridge.install()</c> apply, and the status-checked verify/remove wrappers. What a gate
/// supplies (a namespace, an RPC answer, a revealed flag) lives in its injected fragment under
/// <c>Core\SteamUiAssets\Source\gates\</c>; what makes the client compatible lives in the probe
/// expression and predicate declared in <see cref="SteamGatePatches"/>. Every probe accepts
/// "already ours" as compatible — requiring the pre-patch shape alone made a successful apply
/// invalidate its own next probe and tear the gate down (see the inline probe comments).
/// </remarks>
internal sealed class SteamGatePatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;
    private readonly string _gateName;
    private readonly string _fingerprint;
    private readonly string _probeExpression;
    private readonly Func<JsonElement, bool> _compatible;
    private readonly string _verifyOk;
    private readonly string _removeOk;
    private readonly string _subject;

    /// <summary>Declares one gate.</summary>
    /// <param name="id">Stable patch id.</param>
    /// <param name="resourceKey">The owned client resource, serialized against conflicts.</param>
    /// <param name="gateName">The name the injected bridge registers this gate under.</param>
    /// <param name="fingerprint">Stable structural fingerprint reported on a positive probe.</param>
    /// <param name="probeExpression">Read-only probe naming literal modules only.</param>
    /// <param name="compatible">Reads the probe's JSON into a compatibility verdict.</param>
    /// <param name="verifyOk">JS predicate over the gate's <c>status</c> proving it holds.</param>
    /// <param name="removeOk">JS predicate over <c>status</c> proving removal left nothing.</param>
    /// <param name="subject">Diagnostic subject, e.g. "Audio namespace".</param>
    internal SteamGatePatch(
        string id,
        string resourceKey,
        string gateName,
        string fingerprint,
        string probeExpression,
        Func<JsonElement, bool> compatible,
        string verifyOk,
        string removeOk,
        string subject)
    {
        Id = id;
        ResourceKey = resourceKey;
        _gateName = gateName;
        _fingerprint = fingerprint;
        _probeExpression = probeExpression;
        _compatible = compatible;
        _verifyOk = verifyOk;
        _removeOk = removeOk;
        _subject = subject;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey { get; }

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <inheritdoc />
    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            _probeExpression,
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
            bool compatible = _compatible(document.RootElement);
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? _fingerprint : null,
                compatible ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "return JSON.stringify(bridge.install());",
            _subject + " installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const status=bridge.status();"
            + "return JSON.stringify({ok:" + _verifyOk + ",status});",
            _subject + " verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const removed=bridge.remove();const status=bridge.status();"
            + "return JSON.stringify({ok:removed.ok&&" + _removeOk + "});",
            _subject + " removal failed.",
            cancellationToken);

    private Task<SteamUiPatchOperationResult> EvaluateAsync(
        SteamUiPatchContext context,
        string body,
        string fallback,
        CancellationToken cancellationToken)
    {
        // `bridge` is bound to this patch's own gate, looked up in the registry the fragments
        // register into. A missing gate reads the same as a missing bridge, because from here they
        // are the same failure: nothing of ours is installed to talk to.
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate(" + SteamCef.JsString(_gateName) + "):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + body
            + "})()";
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            fallback,
            cancellationToken);
    }
}

/// <summary>
/// The six registered gates: each supplies or reveals one Valve surface whose backend the Windows
/// client lacks. Which gate kind responds to which absence — and the platform-constant spoof that
/// is never used — is documented in <c>docs\steam-cef.md</c>.
/// </summary>
internal static class SteamGatePatches
{
    /// <summary>The performance backend behind <c>SteamClient.System.Perf</c>.</summary>
    /// <remarks>
    /// Its own resource key, separate from the component patches that mount rows into the panel:
    /// this supplies data, they render, and a failure in one must not disable the other.
    /// </remarks>
    internal static ISteamUiPatch Perf { get; } = new SteamGatePatch(
        id: "wsgm.native-qam.perf",
        resourceKey: "wsgm.native-qam.perf-namespace",
        gateName: "perf",
        fingerprint: "native-qam-perf-v1:store+absent-namespace+reachable-singleton",
        // The store is counted by the source tokens that make it the perf store, never by module
        // id; the singleton is reached through the one export exposing a Get() returning a
        // state-carrying store, because the state is written into a client that is already running.
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("wsgm_native_perf_probe_")}}
              let singleton=false;
              try{
                const mod=req('74514');
                const holder=mod&&Object.values(mod).find(v=>v&&typeof v.Get==='function');
                const store=holder?holder.Get():null;
                singleton=!!(store&&'m_msgState' in store);
              }catch{}
              return JSON.stringify({
                perfStore:count(['SteamClient.System.Perf','RegisterForStateChanges','m_msgState']),
                perfNamespaceAbsent:(()=>{const p=window.SteamClient&&window.SteamClient.System&&window.SteamClient.System.Perf;
                  // Absent, or present and ours — see the audio probe. An orphaned Perf namespace
                  // is the worse case: it leaves SystemPerfStore holding half-written state, which
                  // is what crashed the whole Performance tab.
                  return !p||p.__wsgmOwnedNamespace===true;})(),
                storeSingletonReachable:singleton
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "perfStore")
            && Flag(root, "perfNamespaceAbsent")
            && Flag(root, "storeSingletonReachable"),
        verifyOk: "status.installed&&status.namespacePresent",
        removeOk: "!status.namespacePresent",
        subject: "Performance namespace");

    /// <summary>The audio backend behind <c>SteamClient.System.Audio</c>.</summary>
    /// <remarks>
    /// The store caches <c>m_bAvailable = null != SteamClient.System.Audio</c> at construction,
    /// which already ran; the singleton has to be reachable so it can be written to directly.
    /// </remarks>
    internal static ISteamUiPatch Audio { get; } = new SteamGatePatch(
        id: "wsgm.native-qam.audio",
        resourceKey: "wsgm.native-qam.audio-namespace",
        gateName: "audio",
        fingerprint: "native-qam-audio-v1:store+absent-namespace+reachable-singleton",
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("wsgm_native_audio_probe_")}}
              let singleton=false;
              try{const mod=req('1409');singleton=!!(mod&&mod.F5&&('m_bAvailable' in mod.F5));}catch{}
              return JSON.stringify({
                audioStore:count(['SteamClient.System.Audio','RegisterForDeviceAdded','m_bAvailable']),
                audioNamespaceAbsent:(()=>{const a=window.SteamClient&&window.SteamClient.System&&window.SteamClient.System.Audio;
                  // Absent, or present and OURS. A namespace WSGM installed is not evidence of a native
                  // backend, and treating it as one made this patch declare itself incompatible five
                  // seconds after a successful install, tear down, and orphan the namespace it had just
                  // defined — leaving Steam's audio page empty until Steam itself restarted.
                  return !a||a.__wsgmOwnedNamespace===true;})(),
                storeSingletonReachable:singleton
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "audioStore")
            && Flag(root, "audioNamespaceAbsent")
            && Flag(root, "storeSingletonReachable"),
        verifyOk: "status.installed&&status.namespacePresent",
        removeOk: "!status.namespacePresent",
        subject: "Audio namespace");

    /// <summary>The SteamOS Manager RPC answer Valve's TDP rows read availability and range from.</summary>
    /// <remarks>
    /// Shares the <c>wsgm.native-qam.tdp</c> id and its published state with the mounted TDP rows:
    /// the gate supplies the answer and watches the <c>steamos_tdp_limit</c> client settings the
    /// rows write, routing them to hardware. The original response is merged into, never replaced —
    /// it carries fields (screen-reader support among them) a fabricated reply would zero.
    /// Verification requires the overlay to be the method actually on the service; the settings
    /// watch is reported but not required, since losing it costs the write path, not the row.
    /// </remarks>
    internal static ISteamUiPatch SteamOsManager { get; } = new SteamGatePatch(
        id: "wsgm.native-qam.tdp",
        resourceKey: "wsgm.native-qam.steamos-manager-state",
        gateName: "steamOsManager",
        fingerprint: "native-qam-steamos-manager-v1:service+tdp-row+query-layer+own-getstate",
        // The service is matched by surface, not by export name: module 90389 exports both the
        // Manager and a Telemetry service and both have GetState, so the screen-reader method is
        // what separates them. The query layer must be reachable because the row's answer is cached
        // and a state change that cannot invalidate it never reaches the screen.
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("wsgm_steamos_manager_probe_")}}
              let manager=null;
              try{
                for(const value of Object.values(req('90389')||{})){
                  if(value&&typeof value==='object'
                    &&typeof value.GetState==='function'
                    &&typeof value.RefreshScreenReaderAutoLocale==='function'){manager=value;break;}
                }
              }catch{}
              let queryLayer=false;
              try{const q=req('21371');queryLayer=typeof q?.L?.invalidateQueries==='function';}catch{}
              return JSON.stringify({
                managerFound:!!manager,
                // Valve's own method, or one of WSGM's overlays that still carries it. Requiring the
                // PRE-patch shape here is the self-incompatibility trap this project has already paid
                // for twice: a successful apply would invalidate its own probe, and the next
                // compatibility pass would tear down what it had just installed.
                // The carried original is the claim primitive's property snapshot ({value}), or a
                // bare function from a bridge older than the snapshot. Accepting only the function
                // form re-created the loop: every successful apply read as irreplaceable two seconds
                // later and the row was torn down and rebuilt on a ~2-second cycle (device, 2026-09-01).
                getStateReplaceable:!!manager&&(typeof manager.GetState==='function')
                  &&(manager.GetState.__wsgmOwnedGetState!==true
                    ||typeof manager.GetState.__wsgmOriginalGetState==='function'
                    ||typeof (manager.GetState.__wsgmOriginalGetState||{}).value==='function'),
                queryLayer,
                tdpRow:count(['is_tdp_limit_available','tdp_limit_min','tdp_limit_max'])
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            Flag(root, "managerFound")
            && root.TryGetProperty("tdpRow", out JsonElement row)
            && row.TryGetInt32(out int rows)
            && rows > 0
            && Flag(root, "queryLayer")
            && Flag(root, "getStateReplaceable"),
        verifyOk: "status.installed&&status.getStateOverlaid",
        removeOk: "!status.getStateOverlaid",
        subject: "SteamOS Manager state");

    /// <summary>Reveals Steam's own brightness row, hidden by one settings boolean on Windows.</summary>
    /// <remarks>
    /// Supplies no backend — Steam's own works here — so the probe requires the backend to be
    /// present: revealing the row without it would produce a slider that moves and changes nothing.
    /// Verification includes <c>setterOwned</c> because a revealed slider whose writes still reach
    /// the native stub is the exact broken state this gate shipped with.
    /// </remarks>
    internal static ISteamUiPatch Brightness { get; } = new SteamGatePatch(
        id: "wsgm.steam-display.brightness",
        resourceKey: "wsgm.steam-display.brightness-availability",
        gateName: "brightness",
        fingerprint: "steam-brightness-v1:hidden-flag+present-backend",
        probeExpression: $$"""
            {{SteamUiProbeJs.Preamble("wsgm_brightness_probe_")}}
              const store=req('59547')&&req('59547').mG&&req('59547').mG.Get();
              const settings=store&&store.m_msgSettings;
              if(!settings)return JSON.stringify({error:'display settings unavailable'});
              const display=window.SteamClient&&SteamClient.System&&SteamClient.System.Display;
              return JSON.stringify({
                fieldPresent:'is_display_brightness_available' in settings,
                // Hidden, or visible because WSGM's own gate revealed it. Requiring hidden alone was
                // the self-incompatibility teardown loop: a successful apply made this false, the next
                // poll declared the patch incompatible, and the manager removed the reveal it had just
                // verified — the row flickered on a ~25-second cycle on the device (2026-08-30).
                revealable:settings.is_display_brightness_available!==true
                  ||settings.__wsgmBrightnessRevealed===true,
                backendPresent:!!display&&typeof display.SetBrightness==='function'
                  &&typeof display.RegisterForBrightnessChanges==='function'
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            Flag(root, "fieldPresent")
            && Flag(root, "revealable")
            && Flag(root, "backendPresent"),
        verifyOk: "status.installed&&status.available&&status.setterOwned",
        removeOk: "!status.available",
        subject: "Brightness gate");

    /// <summary>Replaces the stub methods behind Steam's own Bluetooth pairing UI.</summary>
    /// <remarks>
    /// The service cannot be implemented — its <c>*Handler</c> exports are message descriptors,
    /// not registration hooks — so the writable stub methods are swapped. The query cache must be
    /// reachable: availability rides a react-query with infinite stale time, so without an
    /// invalidation the row keeps reading the unavailable answer no matter what the methods return.
    /// </remarks>
    internal static ISteamUiPatch Bluetooth { get; } = new SteamGatePatch(
        id: "wsgm.steam-bluetooth.service",
        resourceKey: "wsgm.steam-bluetooth.manager-service",
        gateName: "bluetooth",
        fingerprint: "steam-bluetooth-v1:operations+writable-stub+reachable-cache",
        probeExpression: $$"""
            {{SteamUiProbeJs.Preamble("wsgm_bluetooth_probe_")}}
              const RF=req('60517')&&req('60517').RF;
              if(!RF)return JSON.stringify({error:'bluetooth service stub unavailable'});
              const ops=['GetState','SetDiscovering','Pair','CancelPair','Connect','Disconnect',
                'Forget','SetTrusted','SetWakeAllowed','GetDeviceDetails'];
              const missing=ops.filter(n=>typeof RF[n]!=='function');
              const d=Object.getOwnPropertyDescriptor(RF,'GetState');
              let cache=false;
              try{cache=typeof req('21371').L.invalidateQueries==='function';}catch{}
              return JSON.stringify({
                operationsPresent:missing.length===0,
                missing:missing,
                methodsWritable:!!d&&d.writable===true&&d.configurable===true,
                queryCacheReachable:cache
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            Flag(root, "operationsPresent")
            && Flag(root, "methodsWritable")
            && Flag(root, "queryCacheReachable"),
        verifyOk: "status.installed&&status.replaced>0",
        removeOk: "!status.installed",
        subject: "Bluetooth service");

    /// <summary>Reveals Steam's Wi-Fi surface by overriding one Deck-only store getter.</summary>
    /// <remarks>
    /// Reveals but does not populate: the Windows backend reports an empty access-point list, so
    /// verification reports the surface rather than treating a revealed row over no networks as
    /// success. The getter must currently read false — a client that already reports network
    /// management available is one WSGM must leave alone.
    /// </remarks>
    internal static ISteamUiPatch Network { get; } = new SteamGatePatch(
        id: "wsgm.steam-network.gate",
        resourceKey: "wsgm.steam-network.availability",
        gateName: "network",
        fingerprint: "steam-network-gate-v1:configurable-getter+currently-hidden",
        probeExpression: $$"""
            {{SteamUiProbeJs.Preamble("wsgm_network_gate_probe_")}}
              const store=req('77347')&&req('77347').OQ&&req('77347').OQ.Get();
              if(!store)return JSON.stringify({error:'network store unavailable'});
              const d=Object.getOwnPropertyDescriptor(
                Object.getPrototypeOf(store),'networkManagementAvailable');
              return JSON.stringify({
                getterConfigurable:!!d&&d.configurable===true&&typeof d.get==='function',
                // False, or already overridden by US. A getter WSGM installed is not evidence that the
                // client reports network management natively, and reading it that way made this patch
                // refuse itself after a successful apply and tear the network list down.
                currentlyHidden:store.networkManagementAvailable===false
                  ||(!!d&&!!d.get&&d.get.__wsgmOwnedGetter===true),
                hasWirelessDevice:store.hasWirelessDevice===true
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            Flag(root, "getterConfigurable")
            && Flag(root, "currentlyHidden"),
        verifyOk: "status.installed&&status.available",
        removeOk: "!status.available",
        subject: "Network gate");

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True;
}
