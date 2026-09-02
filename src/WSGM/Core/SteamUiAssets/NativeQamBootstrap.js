(() => {
  "use strict";
  // What the whole bundle evaluates to, set at the end of this file and returned by epilogue.ts
  // after every fragment has registered. The early reuse return below is the one path that leaves
  // before the fragments run, and it returns its own result directly.
  let installResult;
  const config = __WSGM_CONFIGURATION_JSON__;
  const prior = window[config.namespace];
  if (
    prior &&
    prior.version === config.version &&
    // Neither generation changes when WSGM is updated, so without the asset hash a new build kept
    // running the previous build's script until Steam itself restarted.
    prior.assetHash === config.assetHash &&
    prior.contextGeneration === config.contextGeneration &&
    prior.documentGeneration === config.documentGeneration &&
    // A prior bridge that can still hand out gates is one this build can stand aside for. Asking
    // for a specific gate by name would tie the reuse check to whichever surfaces the consumer
    // happens to have.
    typeof prior.gate === "function"
  ) {
    return JSON.stringify({ ok: true, reused: true, version: prior.version });
  }
  if (prior) {
    // Older bridge versions disposed only the component host. Ask every exposed gate to unwind
    // while its closure still has the original methods/descriptors, then dispose the bridge. This
    // is the compatibility bridge that lets the new uniform ownership markers replace the old
    // per-gate ones without stacking on dead wrappers.
    for (const gateName of [
      "steamOsManager",
      "brightness",
      "bluetooth",
      "network",
      "audio",
      "perf",
    ]) {
      try {
        prior[gateName]?.remove?.();
      } catch {}
    }
    if (typeof prior.dispose === "function") prior.dispose("generation replaced");
  }
  const pending = new Map();
  const subscribers = new Map();
  const latestStates = new Map();
  let nextSequence = 0;
  let disposed = false;
  // One reviewed runtime tap for every gate. Capturing webpack's runtime by pushing an empty
  // chunk is the proven primitive; six private copies only made it possible for their safety and
  // diagnostics to drift. This helper captures the runtime but never evaluates an unknown module.
  const getWebpackRuntime = (scope) => {
    let runtime;
    window.webpackChunksteamui.push([
      [`wsgm_${scope}_${Date.now()}`],
      {},
      (value) => {
        runtime = value;
      },
    ]);
    return runtime;
  };
  const allowed = (patchId, command) => {
    const commands = config.allowed[patchId];
    return Array.isArray(commands) && commands.includes(command);
  };
  const send = (envelope) => {
    if (disposed) throw new Error("WSGM bridge disposed");
    const binding = window[config.binding];
    if (typeof binding !== "function") throw new Error("WSGM Runtime binding unavailable");
    binding(JSON.stringify(envelope));
  };
  // The host REJECTS an action generation of zero, and several gates were passing exactly that —
  // "sequence or action generation is invalid" against wsgm.native-qam.perf/updateSettings,
  // steam-network.gate/startScan and stopScan, and steam-bluetooth.service/setDiscovering, on the
  // reference device on 2026-08-30. Every Valve performance control's write, and every signal that
  // Steam's network page had started looking for networks, was dropped by the bridge before WSGM
  // ever saw it — which is why the Wi-Fi list never filled: WSGM was never told to scan.
  //
  // Zero was meant as "no user-initiated row action here", which is true of a gate. Rather than
  // repeat the counter at each such call site, an absent or non-positive generation is allocated
  // one here, so no caller can construct an invalid envelope at all.
  const actionGenerations = new Map();
  const nextActionGeneration = (patchId) => {
    const next = (actionGenerations.get(patchId) || 0) + 1;
    actionGenerations.set(patchId, next);
    return next;
  };
  const validActionGeneration = (patchId, actionGeneration) => {
    if (Number.isInteger(actionGeneration) && actionGeneration > 0) {
      actionGenerations.set(
        patchId,
        Math.max(actionGenerations.get(patchId) || 0, actionGeneration),
      );
      return actionGeneration;
    }
    return nextActionGeneration(patchId);
  };
  // The generation is optional: a gate has no user-initiated row action to number, and one is
  // allocated for it above. Row controls pass their own so an echo can be matched to the write.
  const request = (patchId, command, payload, requestedGeneration) => {
    if (!allowed(patchId, command)) return Promise.reject(new Error("command not allowlisted"));
    if (pending.size >= config.maximumPending) return Promise.reject(new Error("bridge busy"));
    const actionGeneration = validActionGeneration(patchId, requestedGeneration);
    const sequence = ++nextSequence;
    const envelope = {
      version: config.version,
      type: "request",
      patchId,
      command,
      sequence,
      actionGeneration,
      contextGeneration: config.contextGeneration,
      documentGeneration: config.documentGeneration,
      payload: payload ?? null,
    };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(sequence);
        try {
          send({ ...envelope, type: "cancel" });
        } catch {}
        reject(new Error("WSGM bridge request timed out"));
      }, config.timeoutMilliseconds);
      pending.set(sequence, { resolve, reject, timer, patchId, command });
      try {
        send(envelope);
      } catch (error) {
        clearTimeout(timer);
        pending.delete(sequence);
        reject(error);
      }
    });
  };
  const subscribe = (patchId, callback) => {
    if (!Object.hasOwn(config.allowed, patchId) || typeof callback !== "function")
      throw new Error("subscription not allowlisted");
    let set = subscribers.get(patchId);
    if (!set) subscribers.set(patchId, (set = new Set()));
    set.add(callback);
    if (latestStates.has(patchId)) callback(latestStates.get(patchId));
    return () => set.delete(callback);
  };
  const deliver = (envelope) => {
    if (
      !envelope ||
      envelope.version !== config.version ||
      envelope.contextGeneration !== config.contextGeneration ||
      envelope.documentGeneration !== config.documentGeneration
    )
      return false;
    if (envelope.type === "response") {
      const item = pending.get(envelope.sequence);
      if (!item || item.patchId !== envelope.patchId || item.command !== envelope.command)
        return false;
      clearTimeout(item.timer);
      pending.delete(envelope.sequence);
      if (envelope.ok) item.resolve(envelope.payload);
      else item.reject(new Error(String(envelope.error || "command rejected")));
      return true;
    }
    if (envelope.type === "state") {
      if (!Object.hasOwn(config.allowed, envelope.patchId)) return false;
      latestStates.set(envelope.patchId, envelope.payload);
      const set = subscribers.get(envelope.patchId);
      if (!set) return true;
      for (const callback of [...set]) {
        try {
          callback(envelope.payload);
        } catch {}
      }
      return true;
    }
    return false;
  };
  const dispose = (reason) => {
    if (disposed) return;
    disposed = true;
    // Resident gates own callbacks, service overlays and timers outside the bridge namespace.
    // Removing only the component host left the Manager gate polling every second after the bridge
    // that answered it had gone away, and left the other service wrappers calling dead closures.
    //
    // Every registered gate, not a list: a gate this file does not know about is exactly the case
    // a list gets wrong, and it is the normal case once a consumer adds one.
    for (const gate of gates.values()) {
      const owned = gate;
      // Both, where present. `remove` unwinds what the gate installed in the client; `dispose`
      // releases what it holds inside this bridge, and the component host has only the latter.
      try {
        owned.remove?.();
      } catch {}
      try {
        owned.dispose?.();
      } catch {}
    }
    for (const item of pending.values()) {
      clearTimeout(item.timer);
      item.reject(new Error(reason || "WSGM bridge disposed"));
    }
    pending.clear();
    subscribers.clear();
    latestStates.clear();
    actionGenerations.clear();
  };
  // Stamped on every namespace WSGM defines on SteamClient, so a later probe can tell OUR namespace
  // from a real backend. Without it the two are indistinguishable and the compatibility check reads
  // its own successful install as "a native backend exists", refuses, and tears the patch down —
  // which is exactly what left this client with an empty audio page and a crashing Performance tab.
  //
  // A string key rather than a Symbol: it has to survive being read back from a probe evaluated in
  // a separate CDP call, where a Symbol from this scope is not reachable.
  const ownedMarker = "__wsgmOwnedNamespace";
  // The same idea one level down: a method WSGM overlaid rather than a namespace it defined. The
  // second key carries the method that was replaced, so an overlay outliving the closure that made
  // it can still be unwound back to the client's own.
  const getState = {
    marker: "__wsgmOwnedGetState",
    original: "__wsgmOriginalGetState",
  };
  // Gates register themselves rather than being named here. The bridge used to construct each one
  // by name and publish it under a fixed property, which meant this file had to list every surface
  // its consumer happened to have — the one thing a reusable bridge cannot do.
  //
  // Registration is a top-level statement in each fragment, so it runs after this file and before
  // anything asks for a gate. It also inherits the reuse check for free: when this file returns
  // early because an identical bridge is already installed, the whole IIFE returns and no fragment
  // registers over it.
  const gates = new Map();
  const registerGate = (name, gate) => {
    gates.set(name, gate);
  };
  const bridge = Object.freeze({
    version: config.version,
    assetHash: config.assetHash,
    contextGeneration: config.contextGeneration,
    documentGeneration: config.documentGeneration,
    request,
    subscribe,
    deliver,
    dispose,
    // Looked up at call time, not captured: a gate registers after this object is frozen, and the
    // host asks for one long after that. Returning null for an unknown name rather than throwing
    // keeps a patch whose fragment failed to load reporting "gate absent" instead of an exception
    // with no name in it.
    gate: (name) => gates.get(name) ?? null,
  });
  Object.defineProperty(window, config.namespace, {
    value: bridge,
    configurable: true,
    enumerable: false,
    writable: false,
  });
  // NOT a return: every fragment after this file is concatenated into the same IIFE, so returning
  // the install result here would make each gate's top-level registerGate call unreachable and the
  // bridge would publish with an empty registry. epilogue.ts returns this once the bundle has run.
  installResult = JSON.stringify({ ok: true, reused: false, version: config.version });
  const defineHidden = (host, key, value) => {
    Object.defineProperty(host, key, {
      value,
      configurable: true,
      enumerable: false,
      writable: false,
    });
  };
  const claimed = (host, keys) => !!host && host[keys.marker] === true;
  const captureProperty = (host, property) => ({
    kind: "wsgm-property-snapshot-v1",
    hadOwn: Object.hasOwn(host, property),
    descriptor: Object.getOwnPropertyDescriptor(host, property),
    value: host[property],
  });
  const isPropertySnapshot = (value) =>
    !!value &&
    typeof value === "object" &&
    value.kind === "wsgm-property-snapshot-v1" &&
    typeof value.hadOwn === "boolean";
  // An accessor-backed field is one whose value lives BEHIND the property — a MobX observable, a
  // store's computed flag — and the only safe way to change it is through its own setter.
  // Redefining or deleting the accessor destroys the store's bookkeeping while leaving the getter in
  // place: Steam's settings message (a MobX object) then throws
  // `Cannot read properties of undefined (reading 'get')` on every later read, which crashed the
  // Quick Access Menu until the client restarted (device-reproduced 2026-09-01, brightness flag).
  const accessorSetter = (host, property) => {
    const current = Object.getOwnPropertyDescriptor(host, property);
    if (!current || "value" in current) return null;
    return typeof current.set === "function" ? current.set : undefined;
  };
  const restoreProperty = (host, property, snapshot) => {
    const setter = accessorSetter(host, property);
    if (setter !== null) {
      if (setter === undefined) {
        throw new TypeError("restore target is a read-only accessor");
      }
      if (host[property] !== snapshot.value) host[property] = snapshot.value;
      return;
    }
    if (snapshot.hadOwn && snapshot.descriptor) {
      Object.defineProperty(host, property, snapshot.descriptor);
    } else {
      delete host[property];
    }
  };
  const legacyValueSnapshot = (host, property, value, absentMeansMissing) => {
    const current = Object.getOwnPropertyDescriptor(host, property);
    const hadOwn = !(absentMeansMissing && value === undefined) && !!current;
    return {
      kind: "wsgm-property-snapshot-v1",
      hadOwn,
      descriptor: hadOwn && current && "value" in current ? { ...current, value } : undefined,
      value,
    };
  };
  const installDataValue = (host, property, value) => {
    const descriptor = Object.getOwnPropertyDescriptor(host, property);
    if (descriptor) {
      if (!("value" in descriptor)) {
        // Through the setter, never by redefinition — see accessorSetter. Read back because a
        // setter is free to ignore the write, and a claim that did not take must not be marked.
        if (typeof descriptor.set !== "function") {
          throw new TypeError("claim target is a read-only accessor");
        }
        host[property] = value;
        if (host[property] !== value) {
          throw new TypeError("claim target did not accept the value");
        }
        return;
      }
      Object.defineProperty(host, property, { ...descriptor, value });
    } else {
      Object.defineProperty(host, property, {
        value,
        configurable: true,
        enumerable: true,
        writable: true,
      });
    }
  };
  // Claims a plain data field — a flag or value the client set, that a gate replaces.
  //
  // `absent` is what the field reads as when nothing has claimed it. It is required rather than
  // inferred: reclaiming a previous bridge's work has to restore what THAT bridge displaced, and when
  // the stored original is missing the only honest answer is the value the client would have had.
  const claimValue = (host, field, keys, next, absent) => {
    if (!host || !(field in host)) {
      return { ok: false, error: "claim target unavailable" };
    }
    const reclaimed = claimed(host, keys);
    // Already at the target value and NOT marked means the client did this itself. Refusing is
    // correct: there is nothing to add, and restoring later would hand back a value we invented.
    if (!reclaimed && host[field] === next) {
      return { ok: false, error: "already set by the client" };
    }
    const fieldBefore = captureProperty(host, field);
    const markerBefore = Object.getOwnPropertyDescriptor(host, keys.marker);
    const originalBefore = Object.getOwnPropertyDescriptor(host, keys.original);
    try {
      const stored = Object.hasOwn(host, keys.original) ? host[keys.original] : absent;
      const original = reclaimed
        ? isPropertySnapshot(stored)
          ? stored
          : legacyValueSnapshot(host, field, stored, false)
        : fieldBefore;
      installDataValue(host, field, next);
      defineHidden(host, keys.marker, true);
      defineHidden(host, keys.original, original);
      return { ok: true, reclaimed };
    } catch (error) {
      try {
        restoreProperty(host, field, fieldBefore);
        if (markerBefore) Object.defineProperty(host, keys.marker, markerBefore);
        else delete host[keys.marker];
        if (originalBefore) Object.defineProperty(host, keys.original, originalBefore);
        else delete host[keys.original];
      } catch {
        // The primary error remains the useful diagnosis; a hostile Proxy can also refuse rollback.
      }
      return { ok: false, error: String(error) };
    }
  };
  // Hands a claimed field back. Releasing something never claimed is success, not an error: a gate
  // that failed halfway must be able to unwind without knowing how far it got.
  const releaseValue = (host, field, keys) => {
    if (!host || !claimed(host, keys)) return { ok: true };
    try {
      const stored = host[keys.original];
      const original = isPropertySnapshot(stored)
        ? stored
        : legacyValueSnapshot(host, field, stored, false);
      restoreProperty(host, field, original);
      delete host[keys.marker];
      delete host[keys.original];
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Claims a member — a method a gate overlays, or a namespace it supplies where the client has
  // none. The marker goes on the REPLACEMENT rather than the host, so `status` can ask the live
  // object whether what is installed is ours without consulting any closure.
  const claimMember = (host, member, keys, replacement) => {
    if (!host) {
      return { ok: false, error: "claim host unavailable" };
    }
    const current = host[member];
    const reclaimed = claimed(current, keys);
    try {
      const stored = reclaimed ? current[keys.original] : undefined;
      const original = reclaimed
        ? isPropertySnapshot(stored)
          ? stored
          : legacyValueSnapshot(host, member, stored, true)
        : captureProperty(host, member);
      const next = replacement(original.value);
      // Functions as well as objects: every member claim so far replaces a METHOD, and `typeof` a
      // function is "function", not "object". Excluding it left the replacement unmarked, so the
      // release found nothing of ours and handed nothing back — the overlay outlived its own
      // removal.
      if (!next || (typeof next !== "object" && typeof next !== "function")) {
        return { ok: false, error: "claim replacement cannot carry its marker" };
      }
      defineHidden(next, keys.marker, true);
      defineHidden(next, keys.original, original);
      installDataValue(host, member, next);
      return { ok: true, reclaimed };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Hands a claimed member back to whatever it displaced. A member that was absent before the claim
  // is deleted rather than set to undefined, so `member in host` reads as it did.
  const releaseMember = (host, member, keys) => {
    if (!host) return { ok: true };
    const current = host[member];
    if (!claimed(current, keys)) return { ok: true };
    try {
      const stored = current[keys.original];
      const original = isPropertySnapshot(stored)
        ? stored
        : legacyValueSnapshot(host, member, stored, true);
      restoreProperty(host, member, original);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  const memberClaimed = (host, member, keys) => claimed(host?.[member], keys);
  // Supplies a namespace the client does not have — the Performance and audio backends Valve's own
  // components were written against and the Windows client never defines.
  //
  // Distinct from claimMember, which overlays something that EXISTS. Three differences matter:
  //
  //   - Refusing a real backend is correct. A client that grows one must not be shadowed by a
  //     projection of a different machine's hardware.
  //   - Reclaiming our own is mandatory. A namespace outlives the bridge backing it — the bridge is a
  //     window property that dies with the JS context, SteamClient does not — so after a context
  //     reload an orphaned namespace is left whose methods call a bridge that is gone. Refusing there
  //     stranded the client permanently: the probe saw a namespace, called the patch incompatible,
  //     and Steam's audio page stayed empty until Steam itself restarted.
  //   - Removal DELETES rather than restores, because there was nothing there to hand back.
  //
  // Defined rather than assigned, and non-writable: assignment would throw against a previous
  // bridge's non-writable definition, under the "use strict" this whole asset runs in — turning a
  // reclaim into exactly the refusal above.
  // Takes a marker alone rather than a ClaimKeys pair, because nothing is displaced: there is no
  // original to remember, and removal deletes.
  const supplyNamespace = (host, name, marker, factory) => {
    if (!host) {
      return { ok: false, error: "namespace host unavailable" };
    }
    const current = host[name];
    if (current && !claimed(current, { marker, original: marker })) {
      return { ok: false, error: `${name} already exists` };
    }
    try {
      const api = factory();
      defineHidden(api, marker, true);
      Object.defineProperty(host, name, {
        value: api,
        configurable: true,
        enumerable: true,
        writable: false,
      });
      return { ok: true, reclaimed: !!current };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Withdraws a supplied namespace. Only ever deletes one this bridge marked, so a real backend that
  // appeared underneath is left alone.
  const withdrawNamespace = (host, name, marker) => {
    if (!host || !claimed(host[name], { marker, original: marker })) return { ok: true };
    try {
      delete host[name];
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Claims an accessor property — a getter the client computes, that a gate answers differently.
  //
  // Separate from claimMember because the write has to be defineProperty rather than assignment:
  // assigning to a getter-backed property either calls a setter that is not there or throws, and
  // defining the replacement on the INSTANCE instead of where the accessor lives would shadow rather
  // than replace, leaving the shadow behind after removal. The marker goes on the replacement getter
  // and carries the whole original descriptor, because that is what has to be handed back.
  //
  // Refuses a non-configurable property rather than throwing: a client that locked it is a client
  // this gate stands aside for.
  const claimAccessor = (host, property, keys, getter) => {
    if (!host) {
      return { ok: false, error: "claim host unavailable" };
    }
    const descriptor = Object.getOwnPropertyDescriptor(host, property);
    if (!descriptor || descriptor.configurable !== true) {
      return { ok: false, error: "property is not configurable" };
    }
    try {
      const reclaimed = claimed(descriptor.get, keys);
      const original = reclaimed ? descriptor.get[keys.original] : descriptor;
      defineHidden(getter, keys.marker, true);
      defineHidden(getter, keys.original, original);
      Object.defineProperty(host, property, { get: getter, configurable: true });
      return { ok: true, reclaimed };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Restores the descriptor a claimed accessor displaced.
  const releaseAccessor = (host, property, keys) => {
    if (!host) return { ok: true };
    try {
      const descriptor = Object.getOwnPropertyDescriptor(host, property);
      if (!claimed(descriptor?.get, keys)) return { ok: true };
      const original = descriptor.get[keys.original];
      if (original) {
        Object.defineProperty(host, property, original);
      }
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Answering what Steam asks.
  //
  // The client calls a service method and reads a transport reply, not a bare value. Two gates
  // answer such calls — the SteamOS Manager's GetState and the Bluetooth service's stubs — and both
  // had built the same reply shape and the same query invalidation by hand.
  //
  // Overlaying the method itself is an ownership claim (claimMember); what is here is the rest of
  // the job, which is the half that is easy to forget.
  // The shape Steam reads back from a service call. BSuccess decides whether the caller proceeds at
  // all, so a reply that omits it is discarded before its body is ever looked at; Body().toObject()
  // is what the store then consumes.
  const transportReply = (body) => ({
    BSuccess: () => true,
    BFailed: () => false,
    GetEResult: () => 1,
    Body: () => ({ ...body, toObject: () => body }),
  });
  // Replacing a stub is only half the job: react-query still holds the answer the stub gave, so the
  // UI keeps rendering the refusal until the query that cached it is invalidated. Live-verified that
  // the query client's invalidateQueries is reachable at module 21371.
  //
  // Failure is swallowed on purpose. A client whose query layer moved keeps the stale answer and the
  // row simply does not update — which is a degraded surface, not a broken one, and never a reason to
  // tear down a gate that is otherwise working.
  const invalidateQuery = (req, queryKey) => {
    try {
      req?.("21371")?.L?.invalidateQueries({ queryKey });
    } catch {
      // Intentionally ignored; see above.
    }
  };
  // Audio is supplied as the namespace Steam's own store looks for, rather than drawn as a row.
  // The store's availability flag is literally `null != SteamClient.System.Audio`, so defining this
  // object is the entire gate — there is nothing to patch and nothing to hide.
  function createAudioNamespace() {
    const patchId = "wsgm.native-qam.audio";
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    // Every registration Steam makes at construction. Held here so a state push can reach them and
    // so removal drops them all rather than leaving callbacks pointed at a torn-down bridge.
    const callbacks = {
      serviceConnection: null,
      deviceAdded: null,
      deviceRemoved: null,
      deviceVolumeChanged: null,
      volumeButtonPressed: null,
      appAdded: null,
      appRemoved: null,
      appVolumeChanged: null,
    };
    const register = (slot) => (callback) => {
      callbacks[slot] = typeof callback === "function" ? callback : null;
      // Steam expects an unregister handle from every RegisterFor* call and stores it.
      return { unregister: () => (callbacks[slot] = null) };
    };
    let known = [];
    let originalStoreState = null;
    // Steam's audio identities are NUMBERS: the live store keeps m_activeOutputDeviceId as a
    // uint32 with 0xFFFFFFFF for none (read off the running client, 2026-08-30). WSGM's endpoint
    // ids are Windows GUID strings, so devices listed by name but nothing could ever match as
    // active — which reads as "no default device" and disables the volume slider. Each GUID gets a
    // stable small number for Steam's side of the wire, translated back on every command.
    const NO_DEVICE = 4294967295;
    // The key m_mapVolumes is keyed by, and the second argument of both SetDeviceVolume and
    // OnAudioDeviceVolumeChanged. INPUT IS ZERO — read out of the client's own enum (module 74362:
    // Input=0, Output=1) on 2026-08-30, after assuming the opposite: with the values swapped the
    // output slider's writes were filtered out as "input" and the speaker volume was stored under
    // the input key, which put it on the microphone slider. Named because it has now been confused
    // with the volume itself AND mirrored, and neither mistake may recur silently.
    const AudioDirection = Object.freeze({ Input: 0, Output: 1 });
    // Below one step of a hardware volume button, so a genuine press always counts and float
    // round-tripping through a whole-number percent never does.
    const VolumeEpsilon = 0.004;
    const deviceNumbers = new Map();
    const deviceGuids = new Map();
    let nextDeviceNumber = 1;
    const numberFor = (guid) => {
      if (typeof guid !== "string" || !guid) return NO_DEVICE;
      let value = deviceNumbers.get(guid);
      if (value === undefined) {
        value = nextDeviceNumber++;
        deviceNumbers.set(guid, value);
        deviceGuids.set(value, guid);
      }
      return value;
    };
    const guidFor = (value) => deviceGuids.get(Number(value)) ?? null;
    // The store's device constructor ingests flOutputVolume/flInputVolume (0..1) into the map the
    // sliders bind — omit them and that direction renders a grey bar over undefined. WSGM observes
    // the two Windows defaults, so every endpoint of a direction carries that direction's current
    // default value; exposing a per-device number for an inactive endpoint would be invented.
    const toDevice = (entry, flOutputVolume, flInputVolume) => ({
      id: numberFor(entry.id),
      sName: entry.name,
      bHasOutput: entry.hasOutput === true,
      bHasInput: entry.hasInput === true,
      flOutputVolume: entry.hasOutput === true ? flOutputVolume : undefined,
      flInputVolume: entry.hasInput === true && flInputVolume !== null ? flInputVolume : undefined,
      // Speaker configuration and HDMI CEC reach a service WSGM does not supply. Reported empty and
      // false rather than invented, so those controls simply do not appear.
      currentConfig: {},
      availableConfigs: [],
      eConnectorType: 0,
      eBus: 0,
      bSupportsHdmiCec: false,
      bHdmiCecEnabled: false,
      bHdmiCecActive: false,
    });
    // The store that is already running. Defining the namespace is not enough on a live client:
    // `m_bAvailable` is computed once in the constructor, which ran at client start when
    // SteamClient.System.Audio did not exist, so the audio section would stay hidden forever.
    // Live-verified 2026-08-30: the flag is writable and RegisterOrUpdateDevice is the store's own
    // ingestion path, the same verified path the network gate now owns for the network store.
    const liveStore = () => {
      try {
        const req = getWebpackRuntime("audio-store");
        const store = req?.("1409")?.F5;
        return store && "m_bAvailable" in store ? store : null;
      } catch {
        return null;
      }
    };
    const flVolumeOf = (value) => {
      if (value === null || value === undefined || !Number.isFinite(Number(value))) return null;
      return Math.min(1, Math.max(0, Number(value) / 100));
    };
    // Volume-changed dispatches fire ONLY when the volume moved. Steam shows its volume OSD on
    // every dispatch, and firing one per publish made the OSD pop up over and over while nothing
    // had changed. Null means no volume has been reported yet, so the first publish never counts
    // as a change either — construction already carries it.
    let lastFlOutputVolume = null;
    let lastFlInputVolume = null;
    const onState = (state) => {
      if (!installed || !state || !Array.isArray(state.devices)) return;
      const flOutputVolume = flVolumeOf(state.volumePercent) ?? 0;
      const flInputVolume = flVolumeOf(state.inputVolumePercent);
      const outputVolumeChanged =
        lastFlOutputVolume !== null &&
        Math.abs(flOutputVolume - lastFlOutputVolume) > VolumeEpsilon;
      const inputVolumeChanged =
        lastFlInputVolume !== null &&
        flInputVolume !== null &&
        Math.abs(flInputVolume - lastFlInputVolume) > VolumeEpsilon;
      lastFlOutputVolume = flOutputVolume;
      lastFlInputVolume = flInputVolume;
      // Numeric, because these ids flow to the store and its callbacks, and Steam's side of the
      // wire is numeric everywhere.
      const seen = state.devices.map((device) => numberFor(device.id));
      const removed = known.filter((id) => !seen.includes(id));
      // Removals first: a device that has gone must leave the store before a re-read of the device
      // list can describe the set as complete, or the picker keeps an endpoint that is not there.
      for (const id of removed) {
        if (callbacks.deviceRemoved) callbacks.deviceRemoved(id);
      }
      for (const device of state.devices) {
        if (callbacks.deviceAdded) {
          callbacks.deviceAdded(toDevice(device, flOutputVolume, flInputVolume));
        }
        // (deviceId, DIRECTION, volume) — in that order. Read off the store's own methods
        // 2026-08-30: OnAudioDeviceVolumeChanged(e,t,r) forwards to OnVolumeUpdated(t,r), which is
        // m_mapVolumes.set(t, r). The direction is the KEY and the volume is the VALUE, and WSGM
        // was passing them the other way round — every entry it wrote was keyed by a float volume
        // with 1 or 0 as its value, so getDeviceVolume(direction) found nothing and the slider had
        // no number to sit on.
        //
        // Still gated on an actual change, unlike the direct path below, which also has to seed:
        // a store that registered these callbacks was constructed after the namespace existed and
        // therefore already read the volumes at construction.
        if (outputVolumeChanged && device.hasOutput === true && callbacks.deviceVolumeChanged) {
          const id = numberFor(device.id);
          callbacks.deviceVolumeChanged(id, AudioDirection.Output, flOutputVolume);
        }
        if (
          inputVolumeChanged &&
          flInputVolume !== null &&
          device.hasInput === true &&
          callbacks.deviceVolumeChanged
        ) {
          const id = numberFor(device.id);
          callbacks.deviceVolumeChanged(id, AudioDirection.Input, flInputVolume);
        }
      }
      known = seen;
      // The registrations above only reach a store constructed after the namespace existed. The
      // running one has to be fed through its own path, and told it is available at all.
      const store = liveStore();
      if (!store) return;
      try {
        originalStoreState ??= {
          available: store.m_bAvailable === true,
          output: Number(store.m_activeOutputDeviceId) || NO_DEVICE,
          input: Number(store.m_activeInputDeviceId) || NO_DEVICE,
        };
        store.m_bAvailable = true;
        for (const id of removed) {
          store.m_mapAudioDevices?.delete(id);
        }
        for (const device of state.devices) {
          store.RegisterOrUpdateDevice(toDevice(device, flOutputVolume, flInputVolume));
          // Update() copies the name, the directions and the CEC flags and nothing else — read
          // live 2026-08-30 — so registration never fills m_mapVolumes and this is its only path.
          //
          // But writing on every publish is wrong in both directions at once. It dispatches a
          // volume change once a second, which is Steam's OSD popping up forever; and while the
          // user is dragging, the store is already holding the value they chose, so pushing WSGM's
          // not-yet-observed one snaps the handle back under their thumb.
          //
          // So: seed a direction that has no value at all, and otherwise write only when WSGM's
          // OWN reading moved — something outside Steam changed the volume — and the store has not
          // already caught up. Both are suppressed, because neither is the user acting inside
          // Steam: a hardware button already shows WSGM's own overlay.
          const deviceId = numberFor(device.id);
          const entry = store.m_mapAudioDevices?.get(deviceId);
          const volumes = [];
          if (device.hasOutput === true) {
            volumes.push({
              direction: AudioDirection.Output,
              value: flOutputVolume,
              changed: outputVolumeChanged,
            });
          }
          if (device.hasInput === true && flInputVolume !== null) {
            volumes.push({
              direction: AudioDirection.Input,
              value: flInputVolume,
              changed: inputVolumeChanged,
            });
          } else if (device.hasInput === true) {
            entry?.m_mapVolumes?.delete?.(AudioDirection.Input);
          }
          for (const volume of volumes) {
            const { direction, value, changed } = volume;
            const held = entry?.getDeviceVolume?.(direction);
            const seeding = typeof held !== "number";
            if (!seeding && !(changed && Math.abs(held - value) > VolumeEpsilon)) {
              continue;
            }
            store.SuppressVolumeOverlay?.();
            try {
              store.OnAudioDeviceVolumeChanged?.(deviceId, direction, value);
            } finally {
              // Balanced whatever the dispatch does: the pair is a refcount, and leaking one would
              // suppress the user's own volume overlay for the rest of the session.
              store.UnSuppressVolumeOverlay?.();
            }
          }
        }
        // The running store learns the defaults from nothing else: a store constructed before the
        // namespace existed has 0xFFFFFFFF in both, which the settings page renders as "no default
        // device" and a disabled volume slider.
        store.m_activeOutputDeviceId = numberFor(state.activeOutputDeviceId ?? "");
        store.m_activeInputDeviceId = numberFor(state.activeInputDeviceId ?? "");
      } catch {
        // A store whose shape moved is a compatibility loss, not a fault: the namespace stays and
        // a client rebuilt around a different store simply shows no audio section.
      }
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const system = window.SteamClient?.System;
      if (!system) {
        lastError = "SteamClient.System unavailable";
        return { ok: false, error: lastError };
      }
      const buildApi = () => ({
        GetDevices: () =>
          request(patchId, "getDevices", null, 0).then((state) => ({
            activeOutputDeviceId: numberFor(state?.activeOutputDeviceId ?? ""),
            activeInputDeviceId: numberFor(state?.activeInputDeviceId ?? ""),
            overrideOutputDeviceId: NO_DEVICE,
            overrideInputDeviceId: NO_DEVICE,
            vecDevices: Array.isArray(state?.devices)
              ? state.devices.map((device) =>
                  toDevice(
                    device,
                    flVolumeOf(state?.volumePercent) ?? 0,
                    flVolumeOf(state?.inputVolumePercent),
                  ),
                )
              : [],
          })),
        // Empty until a session mixer exists. Steam then lists no per-application entries, which is
        // the honest outcome rather than inventing volumes it cannot move.
        GetApps: () => Promise.resolve({ rgApps: [] }),
        SetDefaultDeviceOverride: (id, direction) => {
          // Steam hands back the number this side minted; the host only knows the GUID.
          const guid = guidFor(id);
          if (!guid) return Promise.resolve();
          return request(patchId, "setDefaultDevice", {
            id: guid,
            input: direction === AudioDirection.Input,
          });
        },
        // (deviceId, DIRECTION, volume) — three arguments. Read off the store's own device class
        // 2026-08-30: setDeviceVolume(e,t) calls SetDeviceVolume(this.m_id, e, t). WSGM declared
        // two parameters and so read the DIRECTION as the volume: dragging the slider sent
        // Math.round(1 * 100) or Math.round(0 * 100), which is why every drag set 100% or 0% and
        // the log showed "Taskbar volume set to 100%" the moment the slider was touched.
        //
        SetDeviceVolume: (id, direction, volume) => {
          if (direction !== AudioDirection.Output && direction !== AudioDirection.Input) {
            return Promise.resolve();
          }
          return request(patchId, "setVolume", {
            percent: Math.round(Math.min(1, Math.max(0, Number(volume) || 0)) * 100),
            input: direction === AudioDirection.Input,
          });
        },
        SetAppVolume: () => Promise.resolve(),
        ClearDefaultDeviceOverride: () => Promise.resolve(),
        RegisterForServiceConnectionStateChanges: register("serviceConnection"),
        RegisterForDeviceAdded: register("deviceAdded"),
        RegisterForDeviceRemoved: register("deviceRemoved"),
        RegisterForDeviceVolumeChanged: register("deviceVolumeChanged"),
        RegisterForVolumeButtonPressed: register("volumeButtonPressed"),
        RegisterForAppAdded: register("appAdded"),
        RegisterForAppRemoved: register("appRemoved"),
        RegisterForAppVolumeChanged: register("appVolumeChanged"),
      });
      // Refusing a real backend and reclaiming our own orphan are both the primitive's job now; the
      // reasoning for each lives with it.
      const supplied = supplyNamespace(system, "Audio", ownedMarker, buildApi);
      if (!supplied.ok) {
        lastError = supplied.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      return { ok: true, installed: true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      for (const slot of Object.keys(callbacks)) callbacks[slot] = null;
      const store = liveStore();
      if (store) {
        try {
          for (const id of known) store.m_mapAudioDevices?.delete(id);
          store.m_bAvailable = originalStoreState?.available ?? false;
          store.m_activeOutputDeviceId = originalStoreState?.output ?? NO_DEVICE;
          store.m_activeInputDeviceId = originalStoreState?.input ?? NO_DEVICE;
        } catch (error) {
          lastError = "audio store cleanup failed: " + String(error);
        }
      }
      known = [];
      lastFlOutputVolume = null;
      lastFlInputVolume = null;
      originalStoreState = null;
      const withdrawn = withdrawNamespace(window.SteamClient?.System, "Audio", ownedMarker);
      if (!withdrawn.ok) {
        lastError = withdrawn.error ?? "audio namespace withdrawal failed";
        return { ok: false, error: lastError };
      }
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      namespacePresent: !!window.SteamClient?.System?.Audio,
      registrations: Object.keys(callbacks).filter((slot) => callbacks[slot] !== null),
      knownDevices: known.length,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("audio", createAudioNamespace());
  // Bluetooth is a WebUI transport service whose backend does not exist on Windows. The service,
  // its message shapes and every operation are present — GetState round-trips and answers
  // is_service_available:false with empty adapters and devices — so WSGM replaces the stub's
  // methods rather than implementing the service. `*Handler` exports are message descriptors,
  // not registration hooks, so implementing it is not on offer.
  //
  // The second gate matters here as much as the first: availability is read through react-query
  // with staleTime Infinity, so replacing the methods changes nothing until that cache is
  // invalidated. Live-verified 2026-08-30 that RF's methods are writable and configurable and that
  // the query client's invalidateQueries is reachable.
  function createBluetoothService() {
    const patchId = "wsgm.steam-bluetooth.service";
    const queryKey = ["BluetoothManagerService", "State"];
    const methodMarker = "__wsgmOwnedBluetoothService";
    const originalMethodField = "__wsgmOriginalBluetoothServiceMethod";
    const originals = new Map();
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    // Steam's own device and adapter shapes, which are not ours to describe: the store reads them
    // and WSGM only carries them through from the state it was given.
    let latest = { is_service_available: false, adapters: [], devices: [] };
    const modules = () => getWebpackRuntime("bluetooth-service");
    const reply = transportReply;
    const invalidate = (req) => invalidateQuery(req, queryKey);
    // WSGM sends its own field names and the mapping into Steam's lives here, so the client's
    // schema stays in the half that has to change when the client is rebuilt.
    const onState = (state) => {
      if (!installed || !state) return;
      const devices = Array.isArray(state.devices) ? state.devices : [];
      latest = {
        is_service_available: state.available === true,
        // One synthetic adapter, because the panel needs something to hang the radio toggle on and
        // Windows exposes no adapter identity WSGM could pass through truthfully.
        adapters:
          state.available === true
            ? [
                {
                  id: 1,
                  mac: "",
                  name: "Bluetooth",
                  is_enabled: state.enabled === true,
                  is_discovering: state.discovering === true,
                },
              ]
            : [],
        devices: devices.map((device) => ({
          id: device.id,
          mac: device.mac ?? "",
          name: device.name ?? device.id,
          etype: device.eType ?? 0,
          is_paired: device.isPaired === true,
          is_connected: device.isConnected === true,
          // Steam sorts by signal and shows a battery when one is reported. WSGM knows neither, and
          // a fabricated strength would order the list by a number that means nothing.
          strength_raw: 0,
          battery_percent: null,
          should_hide_hint: false,
        })),
      };
      invalidate(modules());
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const req = modules();
      const RF = req?.("60517")?.RF;
      if (!RF || typeof RF.GetState !== "function") {
        lastError = "BluetoothManagerService stub unavailable";
        return { ok: false, error: lastError };
      }
      const forward = (command) => (payload) =>
        request(patchId, command, payload ?? null).then(
          () => reply({ success: true }),
          () => reply({ success: false }),
        );
      const replace = (name, replacement) => {
        const current = RF[name];
        const original = current?.[methodMarker] === true ? current[originalMethodField] : current;
        originals.set(name, original);
        Object.defineProperty(replacement, methodMarker, {
          value: true,
          configurable: true,
          enumerable: false,
        });
        Object.defineProperty(replacement, originalMethodField, {
          value: original,
          configurable: true,
          enumerable: false,
        });
        RF[name] = replacement;
      };
      const restore = () => {
        for (const [name, original] of originals) {
          if (RF[name]?.[methodMarker] === true) RF[name] = original;
        }
      };
      try {
        replace("GetState", () => Promise.resolve(reply(latest)));
        replace("GetDeviceDetails", (payload) => {
          const id = payload?.id;
          const device = latest.devices.find((entry) => entry.id === id) ?? null;
          return Promise.resolve(reply({ device }));
        });
        replace("GetAdapterDetails", () =>
          Promise.resolve(reply({ adapter: latest.adapters[0] ?? null })),
        );
        replace("SetDiscovering", forward("setDiscovering"));
        replace("Pair", forward("pair"));
        replace("CancelPair", forward("cancelPair"));
        replace("Connect", forward("connect"));
        replace("Disconnect", forward("disconnect"));
        replace("Forget", forward("forget"));
        replace("SetTrusted", forward("setTrusted"));
        replace("SetWakeAllowed", forward("setWakeAllowed"));
      } catch (error) {
        lastError = String(error);
        restore();
        originals.clear();
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      invalidate(req);
      return { ok: true, installed: true, replaced: originals.size };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      const req = modules();
      const RF = req?.("60517")?.RF;
      if (RF) {
        for (const [name, original] of originals) {
          if (RF[name]?.[methodMarker] === true) RF[name] = original;
        }
      }
      originals.clear();
      latest = { is_service_available: false, adapters: [], devices: [] };
      invalidate(req);
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      replaced: originals.size,
      available: latest.is_service_available,
      devices: latest.devices.length,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("bluetooth", createBluetoothService());
  // Not availability-only, despite the founding comment that said Steam's own backend works on
  // Windows. It does not — device-disproved 2026-08-30: SetBrightness is a native stub and
  // RegisterForBrightnessChanges never fires, so the store's observable sits at its constructed 1
  // and the revealed slider moves nothing. WSGM is the backend: the gate forwards the slider's
  // writes over the bridge and feeds the store's observable from the published state, both through
  // the same \\.\LCD interface the host owns.
  function createBrightnessGate() {
    const patchId = "wsgm.steam-display.brightness";
    const field = "is_display_brightness_available";
    // A string key on the settings message, because the probe reads it from a separate CDP
    // evaluation where nothing from this scope is reachable. Without it this gate ran the
    // self-incompatibility teardown loop the audio namespace already paid for: the probe required
    // the flag to be hidden, a successful apply made it visible, and the patch manager tore down
    // its own work every poll — the row flickered in and out on a ~25-second cycle on the device.
    const availability = {
      marker: "__wsgmBrightnessRevealed",
      original: "__wsgmOriginalBrightnessAvailability",
    };
    const setter = {
      marker: "__wsgmOwnedSetBrightness",
      original: "__wsgmOriginalSetBrightness",
    };
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    let lastPercent = null;
    const displayStore = () => {
      try {
        const req = getWebpackRuntime("brightness-store");
        return req?.("59547")?.mG?.Get?.() ?? null;
      } catch {
        return null;
      }
    };
    const settings = () => displayStore()?.m_msgSettings ?? null;
    const onState = (state) => {
      if (!installed || !state) return;
      const percent = Number(state.percent);
      if (!Number.isInteger(percent) || percent < 0 || percent > 100) return;
      // Same rule as the volume: write only when WSGM's OWN reading moved, so a publish that
      // merely restates the level never fights a drag the store is already ahead on.
      if (percent === lastPercent) return;
      lastPercent = percent;
      try {
        const observable = displayStore()?.m_flDisplayBrightness;
        if (
          observable?.Set &&
          Math.abs((observable.m_currentValue ?? -1) - percent / 100) > 0.004
        ) {
          observable.Set(percent / 100);
        }
      } catch (error) {
        lastError = "brightness state apply failed: " + String(error);
      }
    };
    // The slider's writes, taken over at the one method it calls. Same replace-not-stack rule as
    // the Manager's GetState: the overlay carries the stub it replaced, so a bridge replaced in
    // place unwinds to the client's own method instead of wrapping a dead closure.
    const overrideSetter = () => {
      const display = window.SteamClient?.System?.Display;
      if (!display || typeof display.SetBrightness !== "function") {
        lastError = "SteamClient.System.Display.SetBrightness unavailable";
        return false;
      }
      const claim = claimMember(display, "SetBrightness", setter, () => (flBrightness) => {
        const percent = Math.round(Math.min(1, Math.max(0, Number(flBrightness) || 0)) * 100);
        // Remembered as ours so the echo of this very write coming back as state does not Set the
        // observable again underneath the drag.
        lastPercent = percent;
        return request(patchId, "setBrightness", { percent }).catch(() => {});
      });
      if (!claim.ok) {
        lastError = claim.error;
        return false;
      }
      return true;
    };
    const restoreSetter = () => {
      const released = releaseMember(
        window.SteamClient?.System?.Display ?? null,
        "SetBrightness",
        setter,
      );
      if (!released.ok) {
        lastError = released.error ?? "brightness setter release failed";
      }
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const message = settings();
      if (!message || !(field in message)) {
        lastError = "display settings message unavailable";
        return { ok: false, error: lastError };
      }
      // A client already reporting brightness available needs nothing from WSGM, and overwriting
      // the flag would mean restoring a value that was never ours to change. Available AND MARKED
      // is different: that is this gate's own earlier reveal, surviving a bridge replaced in
      // place, and refusing it is the teardown trap. Both cases are the claim primitive's job now.
      //
      // `false` is the absent value: a client that hides the row has the flag false, so a reclaim
      // whose stored original went missing hands back a hidden row rather than `undefined`, which
      // Steam's `?? true` hook would have read as available forever.
      const claim = claimValue(message, field, availability, true, false);
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      if (!overrideSetter()) {
        // Revealing a slider whose writes go into the stub is the broken state this gate shipped
        // with; the reveal is undone rather than left half-working.
        releaseValue(message, field, availability);
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      return { ok: true, installed: true, available: message[field] === true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const message = settings();
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      restoreSetter();
      if (!message) return { ok: true, removed: true, storeGone: true };
      const released = releaseValue(message, field, availability);
      if (!released.ok) {
        lastError = released.error ?? "brightness release failed";
        return { ok: false, error: lastError };
      }
      return { ok: true, removed: true };
    };
    const status = () => {
      const message = settings();
      return {
        ok: true,
        installed,
        available: message ? message[field] === true : false,
        setterOwned: memberClaimed(window.SteamClient?.System?.Display, "SetBrightness", setter),
        lastPercent,
        observable: displayStore()?.m_flDisplayBrightness?.m_currentValue ?? null,
        lastError,
      };
    };
    return { install, remove, status };
  }
  registerGate("brightness", createBrightnessGate());
  // Wi-Fi is hidden by one getter, not by an absent backend. Steam's Windows client genuinely
  // tracks the wireless device — hasWirelessDevice and isWifiEnabled are true here without any
  // help — and only `get networkManagementAvailable(){return TS.IS_STEAMOS}` keeps the UI away.
  //
  // Overriding that one property is narrow and reversible and affects one surface. Setting the
  // constant it reads would produce the same row while changing unrelated client behaviour
  // everywhere, which is the spoof D16 forbids. Live-verified 2026-08-30: the descriptor is
  // configurable, the override flips the value, and restoring the saved descriptor puts it back.
  function createNetworkGate() {
    const property = "networkManagementAvailable";
    const patchId = "wsgm.steam-network.gate";
    const availability = {
      marker: "__wsgmOwnedGetter",
      original: "__wsgmOriginalGetterDescriptor",
    };
    const scan = {
      marker: "__wsgmOwnedNetworkScan",
      original: "__wsgmOriginalNetworkScan",
    };
    let target = null;
    let lastError = "";
    let scanWrapped = false;
    let originalStart = null;
    let originalStop = null;
    let unsubscribe = null;
    let syntheticKeys = [];
    const store = () => {
      try {
        const req = getWebpackRuntime("network-store");
        return req?.("77347")?.OQ?.Get() ?? null;
      } catch {
        return null;
      }
    };
    const removeNetworkState = (refresh) => {
      const instance = store();
      if (instance) {
        const keys = new Set(syntheticKeys);
        // Compatibility cleanup for the retired standalone indicator, which used this exact
        // bounded id range but could not hand its closure-owned key list to the new gate.
        const deviceId = instance.m_WirelessDevice?.id;
        if (deviceId !== undefined) {
          for (let index = 0; index < 24; index += 1) keys.add(`${deviceId}:${990001 + index}`);
        }
        for (const key of keys) instance.m_mapNetworkAccessPoints?.delete(key);
        instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
        instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
      }
      syntheticKeys = [];
      if (refresh) {
        try {
          window.SteamClient?.System?.Network?.ForceRefresh?.();
        } catch {}
      }
    };
    // One resident owner now reveals AND feeds the network surface. The previous standalone
    // indicator installed a second script against this same store, with its own version sentinel
    // and retry timer; bridge state gives the generation-aware gate the same verified connected AP
    // for the header. Scan lifetime remains an observation of Steam's page, not an invented
    // connection protocol: its argument order has not been read from the client.
    const onState = (state) => {
      const instance = store();
      const networks = Array.isArray(state?.networks) ? state.networks.slice(0, 24) : [];
      if (!instance || !instance.m_WirelessDevice) {
        lastError = "network store has no wireless device";
        return;
      }
      if (networks.length === 0) {
        removeNetworkState(true);
        lastError = "";
        return;
      }
      try {
        const device = JSON.parse(JSON.stringify(instance.m_WirelessDevice));
        if (!device.wireless) device.wireless = { aps: [], esecurity_supported: 0 };
        const accessPoints = networks.map((network, index) => ({
          id: 990001 + index,
          esecurity: network.secured ? 16 : 0,
          estrength: Math.max(1, Math.min(4, Number(network.strength) || 1)),
          ssid: String(network.ssid || ""),
          is_active: network.connected === true,
          is_autoconnect: network.connected === true,
          is_hidden: false,
        }));
        const keys = accessPoints.map((accessPoint) => `${device.id}:${accessPoint.id}`);
        for (const key of syntheticKeys) {
          if (!keys.includes(key)) instance.m_mapNetworkAccessPoints.delete(key);
        }
        for (const key of keys) instance.m_mapNetworkAccessPoints.delete(key);
        device.estate = networks.some((network) => network.connected === true) ? 5 : device.estate;
        device.wireless.aps = accessPoints;
        accessPoints.forEach((accessPoint) => {
          instance.SetDeviceInfo(device, accessPoint.id);
          const entry = instance.m_mapNetworkAccessPoints.get(`${device.id}:${accessPoint.id}`);
          if (entry) entry.MarkAsNotPresent = () => {};
        });
        instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
        instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
        syntheticKeys = keys;
        lastError = "";
      } catch (error) {
        lastError = String(error);
      }
    };
    const install = () => {
      if (target) return { ok: true, alreadyInstalled: true };
      const instance = store();
      if (!instance) {
        lastError = "network store unavailable";
        return { ok: false, error: lastError };
      }
      // The getter lives on the prototype, so that is what is replaced and restored. Defining it
      // on the instance would shadow rather than replace, and removal would leave the shadow.
      //
      // Marked as ours for the same reason the namespaces are: the compatibility probe checks that
      // the getter currently reads false, and a successful override makes it read true. Left
      // unmarked, the patch reads its own success as "the client already reports this available,
      // stand aside", declares itself incompatible, and tears down — taking the network list with
      // it. The claim primitive is what keeps that from being re-derived here.
      const proto = Object.getPrototypeOf(instance);
      const claim = claimAccessor(proto, property, availability, () => true);
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      target = proto;
      lastError = "";
      wrapScanning();
      unsubscribe = subscribe(patchId, onState);
      return { ok: true, installed: true, available: instance[property] === true };
    };
    // Steam's own UI calls these when its network page opens and closes, so they are exactly the
    // signal for when a scan is worth running. WSGM's radio manager is otherwise driven by WSGM's
    // own panel, and a list refreshed only then would be stale on Steam's page — which is worse
    // than an empty one, because the user picks a network that is gone and the join fails silently.
    //
    // Both originals are always called through: this observes the lifetime, it does not take it
    // over, so a client that grows a working backend keeps behaving exactly as before.
    const wrapScanning = () => {
      const net = window.SteamClient?.System?.Network;
      if (!net || scanWrapped) return;
      const wrap = (name, command) => {
        // Checked before claiming, not inside the factory: a client without this method is one this
        // gate leaves alone entirely, and claiming would mark and reassign something that is not a
        // method at all.
        const current = net[name];
        const existing = claimed(current, scan) ? current[scan.original] : current;
        if (typeof existing !== "function") return null;
        let inner = null;
        const claim = claimMember(net, name, scan, (original) => {
          inner = original;
          return function (...args) {
            // A scan request that cannot reach WSGM must not stop Steam's own call. Promise
            // rejection is handled explicitly; a try/catch only sees synchronous construction.
            void request(patchId, command, null).catch(() => {});
            return inner.apply(this, args);
          };
        });
        return claim.ok ? inner : null;
      };
      originalStart = wrap("StartScanningForNetworks", "startScan");
      originalStop = wrap("StopScanningForNetworks", "stopScan");
      scanWrapped = !!(originalStart || originalStop);
    };
    const unwrapScanning = () => {
      const net = window.SteamClient?.System?.Network;
      if (!net || !scanWrapped) return;
      releaseMember(net, "StartScanningForNetworks", scan);
      releaseMember(net, "StopScanningForNetworks", scan);
      originalStart = null;
      originalStop = null;
      scanWrapped = false;
    };
    const remove = () => {
      unwrapScanning();
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      removeNetworkState(true);
      if (!target) return { ok: true, absent: true };
      const released = releaseAccessor(target, property, availability);
      if (!released.ok) {
        lastError = released.error ?? "network availability release failed";
        return { ok: false, error: lastError };
      }
      target = null;
      return { ok: true, removed: true };
    };
    const status = () => {
      const instance = store();
      return {
        ok: true,
        installed: !!target,
        available: instance ? instance[property] === true : false,
        // Reported because the row can be on while the list is empty: Steam's Windows backend
        // never populates wireless.aps, so an access point count of zero here means WSGM has not
        // supplied one, not that the machine cannot see any networks.
        accessPoints: Array.isArray(instance?.accessPoints) ? instance.accessPoints.length : -1,
        hasWirelessDevice: instance?.hasWirelessDevice === true,
        scanWrapped,
        lastError,
      };
    };
    return { install, remove, status };
  }
  registerGate("network", createNetworkGate());
  // The performance surface is the largest absent backend: SystemPerfStore's constructor
  // optional-chains through a SteamClient.System.Perf that does not exist on Windows, so its state
  // stays empty and every control renders null. Availability for each control is read out of that
  // same state, which is why supplying it also decides what appears — omit a limits field and
  // Valve's own wrapper renders nothing.
  //
  // State is written into m_msgState directly rather than pushed through OnStateChanged, which
  // would mean building a CMsgSystemPerfState protobuf in injected JavaScript to have the store
  // immediately decode it again. Live-verified 2026-08-30 that the direct write is observed through
  // every accessor the hooks use and restores cleanly.
  function createPerfNamespace() {
    const patchId = "wsgm.native-qam.perf";
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    const store = () => window.SystemPerfStore ?? null;
    // The message class is never named here — it is taken from an instance the store builds, so
    // this stays correct across minification and client updates. An object argument is still
    // accepted because that is what a caller other than the store would pass, and an
    // undecodable one is forwarded as-is so WSGM logs a readable rejection instead of nothing.
    const decodeSettingsUpdate = (payload) => {
      if (typeof payload !== "string") return payload?.toObject?.() ?? payload ?? {};
      try {
        const constructor = store()?.CreateSettingsUpdateRequest?.()?.constructor;
        if (typeof constructor?.deserializeBinary !== "function") {
          lastError = "settings update could not be decoded: no deserializeBinary";
          return {};
        }
        const binary = atob(payload);
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index += 1) {
          bytes[index] = binary.charCodeAt(index);
        }
        return constructor.deserializeBinary(bytes).toObject();
      } catch (error) {
        lastError = "settings update could not be decoded: " + String(error);
        return {};
      }
    };
    const onState = (state) => {
      if (!installed || !state) return;
      const target = store();
      if (!target || !target.m_msgState) return;
      try {
        target.m_msgState.limits = state.limits ?? {};
        target.m_msgState.settings = {
          global: state.global ?? {},
          per_app: state.perApp ?? {},
        };
        // Steam identifies the per-game profile by comparing these two: equal means the running
        // game's own profile is the one being edited. "No game" is 769 — the Steam client's own
        // pseudo-app, the id Valve's components compare against — never "0".
        target.m_msgState.current_game_id = state.currentGameId ?? "769";
        target.m_msgState.active_profile_game_id = state.activeProfileGameId ?? "769";
      } catch (error) {
        lastError = String(error);
      }
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const system = window.SteamClient?.System;
      if (!system) {
        lastError = "SteamClient.System unavailable";
        return { ok: false, error: lastError };
      }
      if (!store()) {
        lastError = "SystemPerfStore unavailable";
        return { ok: false, error: lastError };
      }
      // Every setter builds a protobuf delta and hands it to UpdateSettings, so that one method is
      // where all of them arrive. The delta is decoded on WSGM's side rather than here, because the
      // message shapes belong to the client and this half only forwards.
      const buildApi = () => ({
        // Decode first, always. SystemPerfStore's setters all end in
        // `UpdateSettings(request.serializeBase64String())`, so what arrives here is a BASE64
        // STRING, not the message — live-verified 2026-08-30 by round-tripping a request built by
        // the store itself. Forwarding it verbatim made WSGM's reader reject every write as
        // "carried no delta object", which is why no control on the Performance tab did anything:
        // the overlay-level selector snapped back to off, the frame cap never took, VRR never
        // toggled. Decoding through the message's OWN deserializeBinary keeps the wire format the
        // client's business; toObject() then emits snake_case field names, which is what WSGM reads.
        UpdateSettings: (payload) =>
          request(patchId, "updateSettings", { delta: decodeSettingsUpdate(payload) }, 0),
        RegisterForStateChanges: () => ({ unregister: () => {} }),
        RegisterForDiagnosticInfoChanges: () => ({ unregister: () => {} }),
      });
      // Stand aside for a real backend, reclaim one of our own — the primitive's rules, and it marks
      // the namespace before publishing it so nothing can observe an unmarked one. An orphaned Perf
      // namespace is worse than an orphaned audio one: it leaves SystemPerfStore holding half-written
      // state, which renders Valve's controls with no values behind them.
      const supplied = supplyNamespace(system, "Perf", ownedMarker, buildApi);
      if (!supplied.ok) {
        lastError = supplied.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      return { ok: true, installed: true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      const target = store();
      if (target?.m_msgState) {
        try {
          // Back to the empty state the Windows client leaves it in, so every control returns to
          // rendering nothing rather than keeping WSGM's last answer.
          target.m_msgState.limits = undefined;
          target.m_msgState.settings = undefined;
          target.m_msgState.current_game_id = undefined;
          target.m_msgState.active_profile_game_id = undefined;
        } catch (error) {
          lastError = String(error);
        }
      }
      // Marker-checked, which this path was not: it deleted whatever was at System.Perf, so a real
      // backend appearing under a still-installed gate would have been removed by WSGM's own cleanup.
      const withdrawn = withdrawNamespace(window.SteamClient?.System, "Perf", ownedMarker);
      if (!withdrawn.ok) {
        lastError = withdrawn.error ?? "perf namespace withdrawal failed";
        return { ok: false, error: lastError };
      }
      return { ok: true, removed: true };
    };
    const status = () => {
      const target = store();
      return {
        ok: true,
        installed,
        namespacePresent: !!window.SteamClient?.System?.Perf,
        limitsPresent: !!target?.msgLimits,
        // Which controls can draw at all, since each reads its own availability out of limits.
        frameLimitOptions: target?.msgLimits?.fps_limit_options?.length ?? 0,
        vrrSupported: target?.msgLimits?.is_vrr_supported === true,
        lastError,
      };
    };
    return { install, remove, status };
  }
  registerGate("perf", createPerfNamespace());
  // Brightness is one flag away, not a transport away. Steam already tracks the real panel
  // brightness on Windows and both SetBrightness and RegisterForBrightnessChanges exist; the system
  // settings message simply reports is_display_brightness_available as false, and the hook reads
  // `?? true` which never applies to an explicit false. Live-verified 2026-08-30: the flag is
  // writable, flips the answer, and restores.
  //
  // Nothing else is touched. This does not supply a backend, because there already is one.
  // The SteamOS Manager seam, which is what puts Valve's own TDP row on the Performance tab. The
  // row binds two CLIENT SETTINGS (steamos_tdp_limit_enabled, steamos_tdp_limit — Steam persists
  // them itself) and hides behind is_tdp_limit_available from the Manager service's GetState,
  // cached by react-query with staleTime Infinity under ["SteamOSService","State","Manager"].
  //
  // So the gate does three things and no more: overlay the Manager GetState answer with the TDP
  // fields, sourced from the same published state the hand-rolled row used; invalidate that query
  // key when the state changes; and watch the one setting Valve writes so the chosen watts reach
  // the device through the existing setPrimaryLimit command. Valve owns the row, the storage and
  // the write UI — WSGM answers one RPC and observes one number. Live-mapped 2026-08-30: stub
  // export Bd beside the Telemetry service, own-writable GetState, body nested under `state`.
  function createSteamOsManagerGate() {
    const patchId = "wsgm.native-qam.tdp";
    const queryKey = ["SteamOSService", "State", "Manager"];
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    let unsubscribeSettings = null;
    let originalGetState = null;
    let manager = null;
    let latest = {
      available: false,
      min: 0,
      max: 0,
    };
    let lastSentWatts = null;
    let lastSentEnabled = null;
    // One forward in flight at a time. The timer ticks every second and the host's own command
    // budget is longer than that, so without this a slow write would be re-sent underneath itself.
    let forwarding = false;
    const modules = () => getWebpackRuntime("steamos-manager");
    // The Manager service, found structurally: the export whose surface has GetState and the
    // screen-reader refresh no other service carries. Its sibling GV (Telemetry) also has
    // GetState, which is why a bare GetState match is not enough.
    const findManager = (req) => {
      for (const value of Object.values(req?.("90389") ?? {})) {
        if (
          value &&
          typeof value === "object" &&
          typeof value.GetState === "function" &&
          typeof value.RefreshScreenReaderAutoLocale === "function"
        ) {
          return value;
        }
      }
      return null;
    };
    const invalidate = (req) => invalidateQuery(req, queryKey);
    const onState = (state) => {
      if (!installed || !state) return;
      latest = {
        available: state.available === true && Number(state.minimumWatts) > 0,
        min: Number(state.minimumWatts) || 0,
        max: Number(state.maximumWatts) || 0,
      };
      invalidate(modules());
    };
    // Valve's TDP rows do not call a namespace. The toggle and the slider are bound to the
    // steamos_tdp_limit_enabled and steamos_tdp_limit CLIENT SETTINGS, Steam persists them, and
    // WSGM's job is to notice the number and route it to hardware.
    //
    // Read from the settings store rather than from a change payload. Live-verified 2026-08-30:
    // Valve's own hooks read (0,a.q3)(() => G.clientSettings[name]) off the store reachable as
    // window.settingsStore, so that IS the value the rows are showing. Guessing at the shape of
    // whatever RegisterForSettingsChanges hands back would have been a second, weaker source for
    // the same fact.
    const readSettings = () => {
      try {
        const settings = window.settingsStore?.clientSettings;
        if (!settings) return null;
        const watts = Number(settings.steamos_tdp_limit);
        return {
          watts: Number.isInteger(watts) && watts > 0 ? watts : null,
          enabled: settings.steamos_tdp_limit_enabled === true,
        };
      } catch {
        return null;
      }
    };
    const forwardSettings = () => {
      const now = readSettings();
      if (!now) return;
      if (now.enabled === lastSentEnabled && now.watts === lastSentWatts) return;
      if (forwarding) return;
      forwarding = true;
      // The enabled flag rides along: a limit switched off is not the same as a limit of zero
      // watts, and WSGM has to release the cap rather than try to apply one.
      request(patchId, "setPrimaryLimit", { watts: now.watts ?? 0, enabled: now.enabled }).then(
        () => {
          // Latched on SUCCESS, never on the attempt. Recording the value before the answer meant a
          // forward that failed — a host not ready yet, a bridge busy, a refusal — was remembered
          // as sent and never tried again, so the limit stayed where it was with the row showing
          // the number the user had chosen. The timer is what retries; this is what lets it.
          lastSentEnabled = now.enabled;
          lastSentWatts = now.watts;
          forwarding = false;
        },
        (error) => {
          lastError = "power limit forward failed: " + String(error);
          forwarding = false;
        },
      );
    };
    const watchSettings = () => {
      // Steam's own change notification is the trigger, and a slow timer is the safety net. The
      // notification fires on the settings Steam persists, but its payload shape is Steam's and a
      // release that changes it must not silently strand the power limit — which is the failure
      // this whole surface exists to avoid. Both ends call the same reader, and forwardSettings
      // only sends on an actual change, so the timer costs two property reads a second.
      try {
        const handle = window.SteamClient?.Settings?.RegisterForSettingsChanges?.(() =>
          forwardSettings(),
        );
        if (handle && typeof handle.unregister === "function") {
          unsubscribeSettings = () => handle.unregister();
        }
      } catch (error) {
        // The row still renders and Steam still persists the setting; only the routing to
        // hardware is lost, and the status says so.
        lastError = "settings watch unavailable: " + String(error);
      }
      const timer = setInterval(forwardSettings, 1000);
      const stopNotification = unsubscribeSettings;
      unsubscribeSettings = () => {
        clearInterval(timer);
        if (stopNotification) stopNotification();
      };
      // The rows show what Steam persisted, so the hardware has to be brought to it rather than
      // the other way round: without this a limit set in a previous session stays on screen and
      // off the device until the user happens to move the slider.
      forwardSettings();
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const req = modules();
      manager = findManager(req);
      if (!manager) {
        lastError = "SteamOS Manager service stub unavailable";
        return { ok: false, error: lastError };
      }
      // Never wrap our own wrapper. A bridge replaced in place — a new asset hash, a reinstall
      // after a probe — leaves the previous overlay on the service with its closure gone, and
      // nesting a second one would make removal restore a wrapper instead of Valve's method,
      // leaving Steam overlaid for the rest of its life. The overlay therefore carries Valve's
      // method on itself, so a fresh closure can unwrap back to it and replace rather than stack.
      //
      // Refusing instead would be the same self-incompatibility trap the Perf and Audio namespaces
      // already paid for: a successful install would make the next probe declare the patch
      // incompatible, tearing down what it had just done.
      const existing = manager.GetState;
      // The carried original is the claim primitive's property snapshot; a bridge older than the
      // snapshot stored the bare function.
      const carried = claimed(existing, getState) ? existing[getState.original] : existing;
      const recoverable =
        carried && typeof carried === "object" && "value" in carried ? carried.value : carried;
      if (typeof recoverable !== "function") {
        lastError = "SteamOS Manager GetState is not recoverable";
        return { ok: false, error: lastError };
      }
      originalGetState = recoverable;
      const overlaid = async (payload) => {
        // The original answer is kept and overlaid, never replaced: it carries real fields —
        // screen-reader support among them — that a fabricated reply would silently zero.
        const result = await originalGetState.call(manager, payload ?? {});
        try {
          const body = result?.Body?.()?.toObject?.();
          if (!body || !body.state) return result;
          const merged = {
            ...body,
            state: {
              ...body.state,
              is_tdp_limit_available: latest.available,
              tdp_limit_min: latest.min,
              tdp_limit_max: latest.max,
            },
          };
          return transportReply(merged);
        } catch {
          return result;
        }
      };
      const claim = claimMember(manager, "GetState", getState, () => overlaid);
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      watchSettings();
      invalidate(req);
      return { ok: true, installed: true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      if (unsubscribeSettings) {
        unsubscribeSettings();
        unsubscribeSettings = null;
      }
      const released = releaseMember(manager, "GetState", getState);
      if (!released.ok) {
        lastError = released.error ?? "GetState release failed";
        return { ok: false, error: lastError };
      }
      latest = { available: false, min: 0, max: 0 };
      invalidate(modules());
      manager = null;
      originalGetState = null;
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      managerFound: !!manager,
      // What the C# verify step checks. "installed" alone is this closure's own bookkeeping; this
      // is the client actually carrying the overlay.
      getStateOverlaid: memberClaimed(manager, "GetState", getState),
      settingsWatched: unsubscribeSettings !== null,
      available: latest.available,
      min: latest.min,
      max: latest.max,
      // What the host ACCEPTED, not what was attempted, and what Steam has stored beside it. The
      // pair is the diagnosis: two different numbers mean the forward is failing, and lastError
      // says how.
      lastSentWatts,
      lastSentEnabled,
      storedSettings: readSettings(),
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("steamOsManager", createSteamOsManagerGate());
  function createNativeComponentHost() {
    const registrations = new Map();
    const listeners = new Set();
    let runtime;
    let controlRuntime;
    let autoTdpControl;
    let frameLimitControl;
    let controllerControl;
    let resolutionControl;
    let vrrControl;
    let deviceControlsControl;
    // Valve's profile header and its per-game profile toggle. On the current client they are TWO
    // exports of the perf-components module — re-probed 2026-09-02 after the header rendered with
    // no way to enable a profile: the toggle's token resolves uniquely on its own, so each mounts
    // as its own row under the one valveProfileHeader kind. And Valve's reset button. All are
    // additive: WSGM built none of them.
    let valveProfileHeaderControl;
    let valveProfileToggleControl;
    let valveResetControl;
    let valveRefreshRateControl;
    let valveOverlayLevelControl;
    // Valve's power-limit pair. They arrive as two exports, not one row: the toggle reveals the
    // slider through the steamos_tdp_limit_enabled setting, which is how SteamOS models "off" for
    // this control and why the slider has no zero position.
    let valveTdpToggleControl;
    let valveTdpSliderControl;
    let performanceRoot;
    // The Quick Settings panel Steam rendered, captured at match time. S14 puts resolution and
    // refresh rate in Quick Settings, not Performance — but the panel is a LOCAL function of the
    // tabs module, not an export, so it is only ever known once the tab array passes through the
    // patched memo. Null means it has not been seen yet, which the status reports.
    let quickSettingsRoot = null;
    const quickSettingsWrapCache = new Map();
    let originalUseMemo;
    let patchedUseMemo;
    let disposedHost = false;
    let lastPatchError = "";
    // One entry per wrapped tab, because "the perf panel appended fine" and "Quick Settings never
    // rendered" are different facts that a single field could only report as one.
    const appendDiagnostics = {
      perf: null,
      quickSettings: null,
    };
    // Why each control did or did not draw. A control that renders null leaves no trace anywhere:
    // the row is built and appended, the panel simply has one fewer child, and every other signal
    // still reports success. This is the difference between "WSGM did not add it" and "WSGM added
    // it and the device had nothing to show".
    const renderOutcomes = {};
    const note = (kind, reason) => {
      // "no state" is what every render sees while a delivery is being rejected, and the wrapper
      // re-renders on each host notification, so the generic reason must not overwrite the precise
      // one the subscription recorded.
      if (
        reason === "no state" &&
        renderOutcomes[kind] === "state received but rejected by validation"
      ) {
        return null;
      }
      renderOutcomes[kind] = reason;
      return null;
    };
    const definitions = Object.freeze({
      autoTdp: Object.freeze({
        patchId: "wsgm.native-qam.auto-tdp",
        command: "setAutoTdp",
      }),
      // Two commands, because this is SteamOS's unified row: one slider that is the frame cap while
      // a cap is set and the refresh rate once it is switched off.
      frameLimit: Object.freeze({
        patchId: "wsgm.native-qam.frame-limit",
        command: "setFrameLimit",
        refreshCommand: "setRefreshRate",
      }),
      controllerTarget: Object.freeze({
        patchId: "wsgm.native-qam.controller-target",
        command: "setControllerTarget",
      }),
      // Hand-built for the same reason resolution is: Valve ships a component, and its gate is a
      // namespace this client does not have. See createVrrControl.
      vrr: Object.freeze({
        patchId: "wsgm.native-qam.vrr",
        command: "setVariableRefreshRate",
      }),
      // Hand-built, unlike the frame limit and VRR rows. SteamOS drives resolution through
      // gamescope and this client ships no component for it, so there is nothing to mount.
      resolution: Object.freeze({
        patchId: "wsgm.native-qam.resolution",
        command: "setResolution",
      }),
      deviceControls: Object.freeze({
        patchId: "wsgm.native-qam.device-controls",
        chargeCommand: "setChargeLimit",
        brightnessCommand: "setLightingBrightness",
        colorCommand: "setLightingColor",
      }),
      // Valve's own components. They carry no command because they never call WSGM directly: they
      // read SystemPerfStore and write through SteamClient.System.Perf.UpdateSettings, which is the
      // perf patch's vocabulary, not theirs. They still need an entry here — install() refuses any
      // kind that is not a declared definition.
      valveProfileHeader: Object.freeze({
        patchId: "wsgm.native-qam.valve-profile-header",
        command: "",
      }),
      valveReset: Object.freeze({
        patchId: "wsgm.native-qam.valve-reset",
        command: "",
      }),
      // Valve's own refresh-rate row, mounted into Quick Settings per S14. It reads
      // limits.display_refresh_manual_hz_* from SystemPerfStore, which the projection supplies only
      // under FrameLimitOnly — the strategy gate is the state, not a check here.
      valveRefreshRate: Object.freeze({
        patchId: "wsgm.native-qam.valve-refresh-rate",
        command: "",
      }),
      // Valve's performance-overlay selector replaces the retired hand-rolled imitation.
      valveOverlayLevel: Object.freeze({
        patchId: "wsgm.native-qam.valve-overlay-level",
        command: "",
      }),
      // Valve's own power-limit toggle and slider, in place of the hand-rolled row. They carry no
      // command for the same reason the rows above do not: they write the steamos_tdp_limit client
      // settings, which the SteamOS Manager gate watches and forwards.
      valveTdp: Object.freeze({
        patchId: "wsgm.native-qam.valve-tdp",
        command: "",
      }),
    });
    const notify = () => {
      for (const listener of [...listeners]) {
        try {
          listener();
        } catch {}
      }
    };
    const subscribeHost = (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    };
    const uniqueFactory = (requiredTokens) => {
      const matches = Object.entries(runtime.m).filter(([, factory]) => {
        const source = String(factory);
        return requiredTokens.every((token) => source.includes(token));
      });
      return matches.length === 1 ? matches[0] : null;
    };
    const uniqueFunction = (exports, requiredTokens) => {
      const matches = Object.values(exports).filter(
        (value) =>
          typeof value === "function" &&
          requiredTokens.every((token) => String(value).includes(token)),
      );
      return matches.length === 1 ? matches[0] : null;
    };
    const uniqueObject = (exports, predicate) => {
      const matches = Object.values(exports).filter(
        (value) => value && typeof value === "object" && predicate(value),
      );
      return matches.length === 1 ? matches[0] : null;
    };
    const createControlRuntime = () => {
      const reactFactory = uniqueFactory([
        "react.transitional.element",
        "useState",
        "cloneElement",
        "createElement",
      ]);
      const fieldsFactory = uniqueFactory([
        "DialogSlider_Container",
        "DropDownField",
        "SliderField",
      ]);
      const layoutFactory = uniqueFactory(["PanelSectionTitle", "PanelSectionRow", "spinner"]);
      const localizationFactory = uniqueFactory([
        "Attempting to localize token",
        "Unable to find localization token",
        "LocalizeString",
      ]);
      if (!reactFactory || !fieldsFactory || !layoutFactory || !localizationFactory) return null;
      const react = runtime(reactFactory[0]);
      const fields = runtime(fieldsFactory[0]);
      const layout = runtime(layoutFactory[0]);
      const localization = runtime(localizationFactory[0]);
      const slider = uniqueFunction(fields, [
        "onChangeComplete",
        "notchCount",
        "valueSuffix",
        "explainerTitle",
      ]);
      const dropdown = uniqueFunction(fields, [
        "contextMenuPositionOptions",
        "childrenContainerWidth",
        "menuLabel",
      ]);
      // Steam's own ToggleField, from the same module as the slider and dropdown above. Selected by
      // the two markers of its class body rather than by its export name, which is minified and
      // changes with every client build. Live-verified 2026-08-29: exactly one export matches, and
      // the provider that names the module's fields lists that same class as ToggleField.
      const toggle = uniqueFunction(fields, ["OnToggleChange", "this.Toggle()"]);
      const section = uniqueFunction(layout, ["PanelSectionTitle", "spinner"]);
      const row = uniqueObject(
        layout,
        (value) => value.$$typeof && typeof value.render === "function",
      );
      const localize = uniqueFunction(localization, ["LocalizeString(e)", "void 0===r?e"]);
      if (!slider || !dropdown || !section || !row || !localize) return null;
      // The toggle is deliberately not in that guard. It arrived after the other four, so a client
      // whose toggle cannot be found still gets every control that does not need one, rather than
      // losing the whole native surface.
      return { react, slider, dropdown, toggle, section, row, localize };
    };
    const normalizeText = (value) => (typeof value === "string" ? value.slice(0, 240) : "");
    // Deliberately small. Everything the row needs is a switch position and a reason, because the
    // device capability behind it answers in exactly those terms.
    const normalizeVrrState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (typeof value.enabled !== "boolean") return null;
      return Object.freeze({
        available: value.available,
        enabled: value.enabled,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeAutoTdpState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (typeof value.enabled !== "boolean" || typeof value.controlling !== "boolean") return null;
      // The watts figure is only ever a display detail beside the switch, so a value outside the
      // range any power limit uses is dropped rather than rejecting the whole state and taking the
      // switch away with it.
      const watts =
        typeof value.watts === "number" &&
        Number.isInteger(value.watts) &&
        value.watts >= 1 &&
        value.watts <= 200
          ? value.watts
          : null;
      return Object.freeze({
        available: value.available,
        enabled: value.enabled,
        controlling: value.controlling,
        watts,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeControllerState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (!Array.isArray(value.targets) || value.targets.length > 8) return null;
      const targets = [];
      const ids = new Set();
      for (const item of value.targets) {
        if (!item || typeof item !== "object") return null;
        const id = normalizeText(item.id);
        const label = normalizeText(item.label);
        // Uppercase is allowed because the ids WSGM actually sends are PascalCase —
        // SteamDeckComposite, Xbox360, DualShock4. A lowercase-only pattern rejected every one of
        // them, so the whole state normalised to null and the controller row never drew, with
        // nothing anywhere saying a state had been received and thrown away.
        if (!/^[A-Za-z0-9._-]{1,64}$/.test(id) || !label || ids.has(id)) return null;
        ids.add(id);
        targets.push(Object.freeze({ id, label, available: item.available !== false }));
      }
      const selectedTarget = normalizeText(value.selectedTarget);
      const observedTarget = normalizeText(value.observedTarget);
      if (
        (selectedTarget && !ids.has(selectedTarget)) ||
        (observedTarget && !ids.has(observedTarget))
      )
        return null;
      return Object.freeze({
        available: value.available,
        targets: Object.freeze(targets),
        selectedTarget,
        observedTarget,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
        applicationRestartRequired: value.applicationRestartRequired === true,
      });
    };
    const validEnum = (value, allowed) =>
      typeof value === "string" && allowed.includes(value) ? value : null;
    const normalizePerformanceCommon = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      // Only what a row actually reads. This validator once also demanded readbackQuality,
      // policyLayer and adapterAvailability — enums no component consumed and, after the review
      // simplification deleted their only publisher, no state carried: every frame-limit
      // delivery was rejected and the row silently vanished from the QAM (device-observed
      // 2026-09-02, the first dogfooding find).
      const progress = validEnum(value.progress, [
        "idle",
        "queued",
        "applying",
        "succeeded-verified",
        "applied-unverified",
        "rejected",
        "timed-out",
        "indeterminate",
        "failed",
        "external-change",
      ]);
      if (!progress) return null;
      return Object.freeze({
        available: value.available,
        progress,
        fault: normalizeText(value.fault),
        statusText: normalizeText(value.statusText),
      });
    };
    // Validated rather than trusted, like every other semantic state: this arrives over the bridge
    // and a malformed option list would render a dropdown whose entries select nothing.
    const normalizeResolutionState = (value) => {
      if (!value || typeof value !== "object") return null;
      const options = Array.isArray(value.options)
        ? value.options.filter(
            (option) =>
              typeof option === "string" && /^[1-9][0-9]{2,4}x[1-9][0-9]{2,4}$/.test(option),
          )
        : [];
      return {
        available: value.available === true,
        options: options.slice(0, 64),
        current: typeof value.current === "string" ? value.current : "",
        statusText: typeof value.statusText === "string" ? value.statusText : "",
      };
    };
    const normalizeDeviceRange = (value) => {
      if (value === null || value === undefined) return null;
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      const minimum = Number(value.minimum);
      const maximum = Number(value.maximum);
      const step = Number(value.step);
      const desired = value.desired === null ? null : Number(value.desired);
      const observed = value.observed === null ? null : Number(value.observed);
      if (
        !Number.isInteger(minimum) ||
        !Number.isInteger(maximum) ||
        !Number.isInteger(step) ||
        minimum < 0 ||
        maximum > 100 ||
        minimum >= maximum ||
        step < 1 ||
        step > maximum - minimum ||
        (desired !== null &&
          (!Number.isInteger(desired) ||
            desired < minimum ||
            desired > maximum ||
            (desired - minimum) % step !== 0)) ||
        (observed !== null &&
          (!Number.isInteger(observed) ||
            observed < minimum ||
            observed > maximum ||
            (observed - minimum) % step !== 0))
      )
        return null;
      return Object.freeze({
        available: value.available,
        minimum,
        maximum,
        step,
        desired,
        observed,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeDeviceControlsState = (value) => {
      if (!value || typeof value !== "object" || !Array.isArray(value.lightingZones)) return null;
      const chargeLimit = normalizeDeviceRange(value.chargeLimit);
      const lightingBrightness = normalizeDeviceRange(value.lightingBrightness);
      const lightingZones = [];
      const ids = new Set();
      for (const zone of value.lightingZones.slice(0, 16)) {
        if (!zone || typeof zone !== "object") return null;
        const id = normalizeText(zone.id);
        const label = normalizeText(zone.label);
        const desiredColor = zone.desiredColor === null ? null : Number(zone.desiredColor);
        const observedColor = zone.observedColor === null ? null : Number(zone.observedColor);
        if (
          id.length > 64 ||
          !id.trim() ||
          !label ||
          ids.has(id) ||
          (desiredColor !== null &&
            (!Number.isInteger(desiredColor) || desiredColor < 0 || desiredColor > 0xffffff)) ||
          (observedColor !== null &&
            (!Number.isInteger(observedColor) || observedColor < 0 || observedColor > 0xffffff))
        )
          return null;
        ids.add(id);
        lightingZones.push(
          Object.freeze({
            id,
            label,
            available: zone.available === true,
            desiredColor,
            observedColor,
            progress: normalizeText(zone.progress),
            statusText: normalizeText(zone.statusText),
          }),
        );
      }
      return Object.freeze({
        chargeLimit,
        lightingBrightness,
        lightingZones: Object.freeze(lightingZones),
      });
    };
    const normalizeFrameLimitState = (value) => {
      const common = normalizePerformanceCommon(value);
      if (!common) return null;
      const minimumFps = value.minimumFps === null ? null : Number(value.minimumFps);
      const maximumFps = value.maximumFps === null ? null : Number(value.maximumFps);
      const desiredFps = value.desiredFps === null ? null : Number(value.desiredFps);
      const observedFps = value.observedFps === null ? null : Number(value.observedFps);
      // The bounds are a pair: either both are present or neither is. Rejecting a
      // half-populated range here rather than inside the big test below is also what
      // lets the rest of it treat maximumFps as a number.
      if ((minimumFps === null) !== (maximumFps === null)) return null;
      if (
        (minimumFps !== null &&
          maximumFps !== null &&
          (!Number.isInteger(minimumFps) ||
            !Number.isInteger(maximumFps) ||
            minimumFps < 0 ||
            maximumFps < minimumFps ||
            maximumFps > 1000)) ||
        // Zero is OFF and is deliberately outside the slider's range, which now starts at a cap
        // worth playing at. Rejecting it here would have thrown away every state in which the user
        // has no cap set — which is the default one.
        (desiredFps !== null &&
          desiredFps !== 0 &&
          (!Number.isInteger(desiredFps) ||
            minimumFps === null ||
            maximumFps === null ||
            desiredFps < minimumFps ||
            desiredFps > maximumFps)) ||
        (observedFps !== null &&
          observedFps !== 0 &&
          (!Number.isInteger(observedFps) ||
            minimumFps === null ||
            maximumFps === null ||
            observedFps < minimumFps ||
            observedFps > maximumFps)) ||
        (common.available && minimumFps === null)
      )
        return null;
      // Cap to refresh rate, for the "(60 Hz)" half of the label. Absent under the uncoupled
      // strategy, where a cap moves no display mode and there is nothing to name.
      const refreshForCap = new Map();
      if (value.refreshForCap && typeof value.refreshForCap === "object") {
        for (const [cap, hz] of Object.entries(value.refreshForCap)) {
          const capValue = Number(cap);
          const hzValue = Number(hz);
          if (Number.isInteger(capValue) && Number.isInteger(hzValue) && hzValue > 0) {
            refreshForCap.set(capValue, hzValue);
          }
        }
      }
      const refreshMinHz = value.refreshMinHz === null ? null : Number(value.refreshMinHz);
      const refreshMaxHz = value.refreshMaxHz === null ? null : Number(value.refreshMaxHz);
      const currentRefreshHz =
        value.currentRefreshHz === null ? null : Number(value.currentRefreshHz);
      // The refresh half is a pair like the cap half, and it is OPTIONAL: a display that offers no
      // rates leaves the row with only its frame-limit mode rather than rejecting the state.
      // The stops the refresh mode slides between. Windows takes a MODE or refuses: a panel that
      // has 60 and 75 does not have 72, so this mode is notched to exactly what the display
      // accepted, unlike the frame cap, where the limiter really does hold any integer.
      const refreshRates = [];
      if (Array.isArray(value.refreshRates)) {
        for (const item of value.refreshRates) {
          const hz = Number(item);
          if (Number.isInteger(hz) && hz > 0 && !refreshRates.includes(hz)) refreshRates.push(hz);
        }
        refreshRates.sort((left, right) => left - right);
      }
      const refreshUsable =
        refreshRates.length > 0 &&
        refreshMinHz !== null &&
        refreshMaxHz !== null &&
        currentRefreshHz !== null &&
        Number.isInteger(refreshMinHz) &&
        Number.isInteger(refreshMaxHz) &&
        Number.isInteger(currentRefreshHz) &&
        refreshMinHz > 0 &&
        refreshMaxHz >= refreshMinHz;
      return Object.freeze({
        ...common,
        minimumFps,
        maximumFps,
        desiredFps,
        observedFps,
        limitEnabled: value.limitEnabled === true,
        refreshForCap,
        refreshMinHz: refreshUsable ? refreshMinHz : null,
        refreshMaxHz: refreshUsable ? refreshMaxHz : null,
        currentRefreshHz: refreshUsable ? currentRefreshHz : null,
        refreshRates: refreshUsable ? Object.freeze(refreshRates) : Object.freeze([]),
      });
    };
    const useSemanticState = (controlRuntime, kind, normalize) => {
      const definition = definitions[kind];
      const [state, setState] = controlRuntime.react.useState(null);
      controlRuntime.react.useEffect(
        () =>
          subscribe(definition.patchId, (value) => {
            const normalized = normalize(value);
            // A state that arrives and fails validation is not the same as one that never
            // arrived, and both used to end as a null the control returned on. The controller row
            // was invisible for exactly this reason: WSGM sends PascalCase target ids and the
            // validator only accepted lowercase, so every delivery was discarded in silence.
            if (normalized === null && value) {
              renderOutcomes[kind] = "state received but rejected by validation";
            }
            setState(normalized);
          }),
        [],
      );
      return state;
    };
    const isBusy = (progress) =>
      progress === "queued" || progress === "applying" || progress === "replacing";
    /// Lets a controlled slider follow the user's input before the hardware confirms it.
    ///
    /// These sliders are controlled by the observed hardware value, so with a no-op onChange the
    /// handle snapped back to that value on every render: dragging did nothing at all, and a single
    /// press moved exactly one step because only onChangeComplete ever committed. The echo holds
    /// what the user is pointing at until the release, then clears so the observed value governs
    /// again — including when the device refuses the write and the handle must spring back to what
    /// the hardware really is.
    const useEchoedValue = (controlRuntime, observed) => {
      const [echo, setEcho] = controlRuntime.react.useState(null);
      const [echoOf, setEchoOf] = controlRuntime.react.useState(observed);
      // A new observation supersedes an echo taken against the previous one; without this the
      // handle would keep showing a value the hardware had already moved away from.
      if (echoOf !== observed) {
        setEchoOf(observed);
        if (echo !== null) setEcho(null);
      }
      return {
        value: echo ?? observed,
        onChange: (next) => setEcho(typeof next === "number" ? next : null),
        onChangeComplete: (next, commit) => {
          setEcho(null);
          commit(next);
        },
      };
    };
    /// Coalesces expensive device-persistent writes while preserving the last value.
    /// A colour is edited through three sliders; committing each component separately can queue
    /// stale intermediate colours behind a firmware write-rate limit. The last edit replaces the
    /// pending one, and unmount flushes it so closing QAM cannot lose the user's final colour.
    const useTrailingCommit = (controlRuntime, delayMilliseconds, commit) => {
      const pending = controlRuntime.react.useRef(null);
      const timer = controlRuntime.react.useRef(null);
      const commitRef = controlRuntime.react.useRef(commit);
      commitRef.current = commit;
      const flush = () => {
        if (timer.current !== null) {
          globalThis.clearTimeout(timer.current);
          timer.current = null;
        }
        const value = pending.current;
        pending.current = null;
        if (value !== null) commitRef.current(value);
      };
      controlRuntime.react.useEffect(
        () => () => {
          flush();
        },
        [],
      );
      return (value) => {
        pending.current = value;
        if (timer.current !== null) globalThis.clearTimeout(timer.current);
        timer.current = globalThis.setTimeout(flush, delayMilliseconds);
      };
    };
    // Steam's localizer returns the token itself when it has no string for it, which is truthy and
    // would render "#QuickAccess_..." as a label. Live-verified 2026-08-29: a known token localizes,
    // an unknown one comes straight back.
    //
    // EVERY label goes through this, not only the WSGM-invented ones. With the rows finally
    // rendering on the reference Claw, "#QuickAccess_Tab_Perf_FramerateLimit" and
    // "#QuickAccess_Tab_Perf_PerfOverlayLevel" both came back raw and were shown to the user as
    // their token text. A bare localize() call here is a bug waiting for the next missing string.
    //
    // Live-probed 2026-08-30, which found the reason: neither token exists anywhere in the bundle.
    // They were never SteamOS strings absent from the Windows set — they were wrong names. The
    // client carries "#QuickAccess_Tab_Perf_LimitFrameRate" and "#QuickAccess_Tab_Perf_Overlay_Level",
    // and those localize. Both call sites now use the real names, so those two rows are translated
    // rather than permanently English.
    //
    // The fallback still earns its place, for the labels WSGM invents and Valve has no string for
    // (AutoTDP, the display-resolution row). Those pass no token at all rather than a plausible
    // one: a token that does not exist makes Steam log an unresolved string on every render and
    // still shows the English text.
    // Steam's localizer does not return a string. It returns a React element wrapping one, so
    // `typeof text === "string"` was false for every token and every WSGM label fell back to its
    // English default while Steam's own rows beside them were in the user's language. The element
    // is what should be handed to the field — only the "#" test needs the text inside it.
    const textOf = (value) => {
      if (typeof value === "string") return value;
      return value && typeof value === "object" && typeof value.props?.children === "string"
        ? value.props.children
        : null;
    };
    const localizeOr = (controlRuntime, token, fallback) => {
      const localized = controlRuntime.localize(token);
      const text = textOf(localized);
      return text && text.length > 0 && text[0] !== "#" ? localized : fallback;
    };
    // WSGM's own variable-refresh switch. Valve ships one, and it cannot be used: its component is
    // gated on a react-query over SteamClient.System.DisplayManager, whose GetState this client
    // does not define — the query never succeeds and the component returns null before it reads a
    // single field WSGM publishes (live-probed 2026-08-30). The device capability behind this row
    // is the one already verified on the reference unit through IGCL Arc Sync.
    const createVrrControl = (controlRuntime) =>
      function WsgmNativeVrrControl() {
        const state = useSemanticState(controlRuntime, "vrr", normalizeVrrState);
        if (!state) return note("vrr", "no state");
        if (!state.available)
          return note("vrr", "unavailable: " + (state.statusText || "no reason"));
        if (!controlRuntime.toggle) return note("vrr", "Steam ToggleField was not resolved");
        renderOutcomes.vrr = "rendered";
        const definition = definitions.vrr;
        return controlRuntime.react.createElement(controlRuntime.toggle, {
          // Valve's own token for the row, so the label matches the client's language even though
          // the component behind it is WSGM's.
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Perf_EnableVRR",
            "Variable refresh rate",
          ),
          description: state.statusText || undefined,
          checked: state.enabled,
          // Controlled: the switch shows what the device reports, so a write the panel refuses
          // leaves it where the hardware actually is rather than where it was clicked.
          controlled: true,
          disabled: isBusy(state.progress),
          onChange: (enabled) => {
            if (typeof enabled !== "boolean" || enabled === state.enabled) return;
            void request(
              definition.patchId,
              definition.command,
              { enabled },
              nextActionGeneration(definition.patchId),
            ).catch(() => {});
          },
        });
      };
    const createAutoTdpControl = (controlRuntime) =>
      function WsgmNativeAutoTdpControl() {
        const state = useSemanticState(controlRuntime, "autoTdp", normalizeAutoTdpState);
        if (!state) return note("autoTdp", "no state");
        if (!state.available)
          return note("autoTdp", "unavailable: " + (state.statusText || "no reason"));
        // Deliberately outside createControlRuntime's guard, so a client whose ToggleField cannot
        // be located loses only this row. That silence is exactly what needed a name.
        if (!controlRuntime.toggle) return note("autoTdp", "Steam ToggleField was not resolved");
        renderOutcomes.autoTdp = "rendered";
        const definition = definitions.autoTdp;
        const setEnabled = (enabled) => {
          if (typeof enabled !== "boolean" || enabled === state.enabled) return;
          void request(
            definition.patchId,
            definition.command,
            { enabled },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        // While controlling, the watts AutoTDP settled on go in the description: a user watching the
        // slider move needs to see that something is driving it, and what it decided.
        const description =
          state.controlling && state.watts !== null
            ? state.watts + " W · " + state.statusText
            : state.statusText;
        return controlRuntime.react.createElement(controlRuntime.toggle, {
          // WSGM's own control; Valve has no string for it, so no token is passed.
          label: "Automatic TDP",
          description: description || undefined,
          checked: state.enabled,
          // Controlled, so the switch shows the stored setting rather than its own click. A command
          // that does not land leaves the switch where the setting actually is instead of showing a
          // change that did not happen.
          controlled: true,
          disabled: isBusy(state.progress),
          onChange: setEnabled,
        });
      };
    const createControllerControl = (controlRuntime) =>
      function WsgmNativeControllerTargetControl() {
        const state = useSemanticState(
          controlRuntime,
          "controllerTarget",
          normalizeControllerState,
        );
        if (!state) return note("controllerTarget", "no state");
        if (!state.available)
          return note("controllerTarget", "unavailable: " + (state.statusText || "no reason"));
        const options = state.targets
          .filter((target) => target.available)
          .map((target) => ({ data: target.id, label: target.label }));
        const selected = state.observedTarget || state.selectedTarget;
        if (!options.some((option) => option.data === selected))
          return note(
            "controllerTarget",
            `selected '${selected}' is not among ${options.length} available target(s)`,
          );
        renderOutcomes.controllerTarget = "rendered";
        const definition = definitions.controllerTarget;
        const setTarget = (option) => {
          if (!option || !options.some((candidate) => candidate.data === option.data)) return;
          void request(
            definition.patchId,
            definition.command,
            { target: option.data },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        const restart = state.applicationRestartRequired
          ? " Restart the application to rebind."
          : "";
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Settings_Section_Controller_Title",
            "Controller",
          ),
          rgOptions: options,
          selectedOption: selected,
          onChange: setTarget,
          disabled: isBusy(state.progress) || options.length < 2,
          description: (state.statusText || "") + restart || undefined,
          layout: "below",
        });
      };
    const createResolutionControl = (controlRuntime) =>
      function WsgmNativeResolutionControl() {
        const state = useSemanticState(controlRuntime, "resolution", normalizeResolutionState);
        if (!state) return note("resolution", "no state");
        if (!state.available)
          return note("resolution", "unavailable: " + (state.statusText || "no reason"));
        if (state.options.length < 2)
          return note("resolution", `only ${state.options.length} option(s)`);
        renderOutcomes.resolution = "rendered";
        const definition = definitions.resolution;
        const options = state.options.map((option) => ({ data: option, label: option }));
        const setResolution = (option) => {
          // Checked against the offered list before sending. The row cannot be the only thing
          // standing between a stray value and a mode change, but it should not be the source of
          // one either.
          if (!option || !state.options.includes(option.data)) return;
          // "target" rather than "value": that is the payload shape every dropdown here uses, and
          // the host's reader rejects an object carrying anything else.
          void request(
            definition.patchId,
            definition.command,
            { target: option.data },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          // Not localized, deliberately. The client has no token meaning "display resolution":
          // #Settings_Display_GameResolution is a per-game override and would read wrongly in every
          // language but English. Passing a token that does not exist is worse than passing none —
          // it makes Steam log an unresolved token on every render and still shows this string.
          label: "Display resolution",
          rgOptions: options,
          // A current mode outside the offered list selects nothing rather than the first entry,
          // which would silently misreport what the display is doing.
          selectedOption: state.options.includes(state.current) ? state.current : undefined,
          onChange: setResolution,
          description: state.statusText || undefined,
          layout: "below",
        });
      };
    // Which notch the display is currently sitting on. A rate that is not one of the listed modes —
    // something else can leave the panel on one — takes the nearest notch at or below it rather
    // than snapping the handle to the start and reporting a rate the display is not at.
    const currentRefreshNotch = (state) => {
      if (!state || !state.refreshRates || state.refreshRates.length === 0) return null;
      const current = state.currentRefreshHz;
      if (!Number.isInteger(current)) return null;
      let notch = 0;
      for (let index = 0; index < state.refreshRates.length; index += 1) {
        if (state.refreshRates[index] <= current) notch = index;
      }
      return notch;
    };
    const createFrameLimitControl = (controlRuntime) =>
      function WsgmNativeFrameLimitControl() {
        const state = useSemanticState(controlRuntime, "frameLimit", normalizeFrameLimitState);
        const value = state ? (state.observedFps ?? state.desiredFps) : null;
        const echoed = useEchoedValue(controlRuntime, value);
        // Its own echo, because the two modes are two different numbers on one slider: reusing one
        // would make the handle jump to a frame cap the moment the rate mode took over. It echoes
        // the notch INDEX, which is what a notch slider reports while it is being dragged.
        // Unconditional, ahead of every early return — these are hooks.
        const refreshEchoed = useEchoedValue(controlRuntime, currentRefreshNotch(state));
        if (!state) return note("frameLimit", "no state");
        if (!state.available)
          return note("frameLimit", "unavailable: " + (state.statusText || "no reason"));
        if (value === null) return note("frameLimit", "no observed or desired fps");
        renderOutcomes.frameLimit = "rendered";
        const definition = definitions.frameLimit;
        const send = (command, nextValue) =>
          void request(
            definition.patchId,
            command,
            { value: nextValue, persistence: "automatic" },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        const setCap = (nextValue) => {
          if (
            !Number.isInteger(nextValue) ||
            nextValue < state.minimumFps ||
            nextValue > state.maximumFps
          )
            return;
          send(definition.command, nextValue);
        };
        // Takes a NOTCH INDEX, not a rate: the refresh mode is a notch slider, so what the control
        // hands back is a position in the accepted list.
        const setRefresh = (notchIndex) => {
          const hz = state.refreshRates[notchIndex];
          if (!Number.isInteger(hz)) return;
          send(definition.refreshCommand, hz);
        };
        // Off is zero, and the slider never shows it: the cap the user chose has to survive being
        // switched off and back on, so the switch below writes zero and the slider keeps sitting
        // where it was. That is how SteamOS's own "Disable Frame Limit" behaves next to its Frame
        // Limit slider, and it is why the slider can start at a cap worth playing at.
        const capped = state.limitEnabled && echoed.value > 0;
        const cappedValue = echoed.value > 0 ? echoed.value : (state.minimumFps ?? 0);
        // Recomputed every render, which is what makes it track a value still being dragged.
        const pairedHz = state.refreshForCap.get(cappedValue);
        // The row's second mode. With the cap off the slider IS the refresh rate — the whole reason
        // SteamOS merged the two rows is that they are one decision: the frame cap and the rate it
        // is presented at are the same frametime question, and vsync is what makes the pacing hold.
        // Switching the cap off does not leave a dead control behind, it hands the same slider over
        // to the rate.
        const refreshMode = !capped && state.refreshRates.length > 0;
        const sliderValue = refreshMode ? (refreshEchoed.value ?? 0) : cappedValue;
        // Guarded like the AutoTDP row: a client whose ToggleField cannot be located loses the
        // switch and keeps the slider, rather than losing the whole row silently.
        const disableSwitch = controlRuntime.toggle
          ? controlRuntime.react.createElement(controlRuntime.toggle, {
              // Not "#QuickAccess_Tab_Perf_LimitFrameRate_Off": that token is the notch slider's
              // first STOP and localizes to bare "Off" ("AUS"), which reads as a row with no
              // subject once it is a switch of its own. SteamOS names this switch outright.
              label: "Disable frame limit",
              description: refreshMode
                ? "The slider sets the refresh rate while the limit is off."
                : undefined,
              checked: !capped,
              controlled: true,
              disabled: isBusy(state.progress),
              // Turning it back on restores the cap the slider is already sitting on, so the
              // number the user was looking at is the one that takes effect.
              onChange: (next) => send(definition.command, next ? 0 : cappedValue),
            })
          : note("frameLimitSwitch", "Steam ToggleField was not resolved");
        const slider = controlRuntime.react.createElement(controlRuntime.slider, {
          // Live-verified 2026-08-30: these are tokens the client actually carries.
          // "#QuickAccess_Tab_Perf_FramerateLimit" appears nowhere in the bundle, so the row it was
          // written against fell back to English on every localized client.
          label: refreshMode
            ? localizeOr(controlRuntime, "#QuickAccess_Tab_Perf_RefreshRate", "Refresh rate")
            : localizeOr(
                controlRuntime,
                "#QuickAccess_Tab_Perf_LimitFrameRate",
                "Frame rate limit",
              ),
          // The two modes are two different sliders sharing one row. The frame cap is NOTCHLESS
          // under every strategy — the limiter holds any integer and the pairing is what snaps —
          // while the refresh rate is notched to exactly the modes the display accepted, because
          // Windows takes a mode or refuses and there is no continuum between 60 and 75.
          min: 0,
          max: refreshMode ? state.refreshRates.length - 1 : state.maximumFps,
          ...(refreshMode
            ? {
                notchCount: state.refreshRates.length,
                notchLabels: state.refreshRates.map((hz, notchIndex) => ({
                  notchIndex,
                  label: `${hz}`,
                  value: hz,
                })),
                notchTicksVisible: true,
              }
            : { min: state.minimumFps }),
          step: 1,
          value: sliderValue,
          // "60 FPS (60 Hz)" is how SteamOS's unified row names a cap and the rate it will be
          // presented at. In refresh mode the notch label already carries the number.
          valueSuffix: refreshMode ? " Hz" : pairedHz ? ` FPS (${pairedHz} Hz)` : " FPS",
          showValue: !refreshMode,
          showBookendLabels: !refreshMode,
          disabled: isBusy(state.progress),
          description: state.fault || state.statusText || undefined,
          onChange: refreshMode ? refreshEchoed.onChange : echoed.onChange,
          onChangeComplete: (next) =>
            refreshMode
              ? refreshEchoed.onChangeComplete(next, setRefresh)
              : echoed.onChangeComplete(next, setCap),
        });
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          slider,
          disableSwitch,
        );
      };
    const rgbToHsv = (color) => {
      const red = ((color >> 16) & 0xff) / 255;
      const green = ((color >> 8) & 0xff) / 255;
      const blue = (color & 0xff) / 255;
      const maximum = Math.max(red, green, blue);
      const minimum = Math.min(red, green, blue);
      const delta = maximum - minimum;
      let hue = 0;
      if (delta > 0) {
        if (maximum === red) hue = 60 * (((green - blue) / delta) % 6);
        else if (maximum === green) hue = 60 * ((blue - red) / delta + 2);
        else hue = 60 * ((red - green) / delta + 4);
      }
      if (hue < 0) hue += 360;
      return {
        hue: Math.round(hue),
        saturation: maximum === 0 ? 0 : Math.round((delta / maximum) * 100),
        brightness: Math.round(maximum * 100),
      };
    };
    const hsvToRgb = (hue, saturation, brightness) => {
      const h = ((Number(hue) % 360) + 360) % 360;
      const s = Math.min(100, Math.max(0, Number(saturation))) / 100;
      const v = Math.min(100, Math.max(0, Number(brightness))) / 100;
      const chroma = v * s;
      const x = chroma * (1 - Math.abs(((h / 60) % 2) - 1));
      const m = v - chroma;
      let red = 0;
      let green = 0;
      let blue = 0;
      if (h < 60) [red, green] = [chroma, x];
      else if (h < 120) [red, green] = [x, chroma];
      else if (h < 180) [green, blue] = [chroma, x];
      else if (h < 240) [green, blue] = [x, chroma];
      else if (h < 300) [red, blue] = [x, chroma];
      else [red, blue] = [chroma, x];
      return (
        (Math.round((red + m) * 255) << 16) |
        (Math.round((green + m) * 255) << 8) |
        Math.round((blue + m) * 255)
      );
    };
    const rgbCss = (color) => `#${Number(color).toString(16).padStart(6, "0")}`;
    const createDeviceControlsControl = (controlRuntime) =>
      function WsgmNativeDeviceControls() {
        const state = useSemanticState(
          controlRuntime,
          "deviceControls",
          normalizeDeviceControlsState,
        );
        const definition = definitions.deviceControls;
        const send = (command, payload) =>
          void request(
            definition.patchId,
            command,
            payload,
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        const queueColorCommit = useTrailingCommit(controlRuntime, 350, ({ zone, color }) =>
          send(definition.colorCommand, { zone, color }),
        );
        const [selectedZone, setSelectedZone] = controlRuntime.react.useState("");
        const chargeValue = state?.chargeLimit
          ? (state.chargeLimit.observed ?? state.chargeLimit.desired)
          : null;
        const brightnessValue = state?.lightingBrightness
          ? (state.lightingBrightness.observed ?? state.lightingBrightness.desired)
          : null;
        const zones = state?.lightingZones?.filter((zone) => zone.available) ?? [];
        const zone = zones.find((candidate) => candidate.id === selectedZone) ?? zones[0] ?? null;
        const color = zone ? (zone.observedColor ?? zone.desiredColor) : null;
        const hsv = color === null ? null : rgbToHsv(color);
        const chargeEcho = useEchoedValue(controlRuntime, chargeValue);
        const brightnessEcho = useEchoedValue(controlRuntime, brightnessValue);
        const hueEcho = useEchoedValue(controlRuntime, hsv?.hue ?? null);
        const saturationEcho = useEchoedValue(controlRuntime, hsv?.saturation ?? null);
        const colorBrightnessEcho = useEchoedValue(controlRuntime, hsv?.brightness ?? null);
        if (!state) return note("deviceControls", "no state");
        const rows = [];
        const appendSlider = (key, properties) => {
          rows.push(
            controlRuntime.react.createElement(
              controlRuntime.row,
              { key },
              controlRuntime.react.createElement(controlRuntime.slider, properties),
            ),
          );
        };
        if (state.chargeLimit?.available && chargeEcho.value !== null) {
          const range = state.chargeLimit;
          appendSlider("wsgm-native-qam-charge-limit", {
            label: "Battery charge limit",
            min: range.minimum,
            max: range.maximum,
            step: range.step,
            value: chargeEcho.value,
            valueSuffix: "%",
            showValue: true,
            showBookendLabels: true,
            disabled: isBusy(range.progress),
            description: range.statusText || undefined,
            onChange: chargeEcho.onChange,
            onChangeComplete: (next) =>
              chargeEcho.onChangeComplete(next, (percent) =>
                send(definition.chargeCommand, { percent }),
              ),
          });
        }
        if (state.lightingBrightness?.available && brightnessEcho.value !== null) {
          const range = state.lightingBrightness;
          appendSlider("wsgm-native-qam-lighting-brightness", {
            label: "Lighting brightness",
            min: range.minimum,
            max: range.maximum,
            step: range.step,
            value: brightnessEcho.value,
            valueSuffix: "%",
            showValue: true,
            showBookendLabels: true,
            disabled: isBusy(range.progress),
            description: range.statusText || undefined,
            onChange: brightnessEcho.onChange,
            onChangeComplete: (next) =>
              brightnessEcho.onChangeComplete(next, (percent) =>
                send(definition.brightnessCommand, { percent }),
              ),
          });
        }
        if (zone && hsv) {
          const options = zones.map((candidate) => ({
            data: candidate.id,
            label: candidate.label,
          }));
          rows.push(
            controlRuntime.react.createElement(
              controlRuntime.row,
              { key: "wsgm-native-qam-lighting-zone" },
              controlRuntime.react.createElement(controlRuntime.dropdown, {
                label: "Lighting zone",
                rgOptions: options,
                selectedOption: zone.id,
                onChange: (option) => {
                  if (option && zones.some((candidate) => candidate.id === option.data)) {
                    setSelectedZone(option.data);
                  }
                },
                disabled: options.length < 2,
                description: zone.statusText || undefined,
                layout: "below",
              }),
            ),
          );
          const stagedColor = hsvToRgb(
            hueEcho.value ?? hsv.hue,
            saturationEcho.value ?? hsv.saturation,
            colorBrightnessEcho.value ?? hsv.brightness,
          );
          rows.push(
            controlRuntime.react.createElement(
              controlRuntime.row,
              { key: "wsgm-native-qam-lighting-preview" },
              controlRuntime.react.createElement("div", {
                title: rgbCss(stagedColor),
                style: {
                  background: rgbCss(stagedColor),
                  border: "1px solid rgba(255,255,255,.7)",
                  borderRadius: "4px",
                  height: "32px",
                  width: "100%",
                },
              }),
            ),
          );
          const commitColor = (hue, saturation, brightness) =>
            queueColorCommit({
              zone: zone.id,
              color: hsvToRgb(hue, saturation, brightness),
            });
          appendSlider("wsgm-native-qam-lighting-hue", {
            label: localizeOr(controlRuntime, "#ColorPicker_Hue", "Hue"),
            min: 0,
            max: 360,
            step: 1,
            value: hueEcho.value,
            valueSuffix: "°",
            showValue: true,
            disabled: isBusy(zone.progress),
            trackStyleOverride: {
              background: "linear-gradient(to right,#f00,#ff0,#0f0,#0ff,#00f,#f0f,#f00)",
              "--left-track-color": "transparent",
            },
            onChange: hueEcho.onChange,
            onChangeComplete: (next) =>
              hueEcho.onChangeComplete(next, (hue) =>
                commitColor(
                  hue,
                  saturationEcho.value ?? hsv.saturation,
                  colorBrightnessEcho.value ?? hsv.brightness,
                ),
              ),
          });
          appendSlider("wsgm-native-qam-lighting-saturation", {
            label: localizeOr(controlRuntime, "#ColorPicker_Saturation", "Saturation"),
            min: 0,
            max: 100,
            step: 1,
            value: saturationEcho.value,
            valueSuffix: "%",
            showValue: true,
            disabled: isBusy(zone.progress),
            onChange: saturationEcho.onChange,
            onChangeComplete: (next) =>
              saturationEcho.onChangeComplete(next, (saturation) =>
                commitColor(
                  hueEcho.value ?? hsv.hue,
                  saturation,
                  colorBrightnessEcho.value ?? hsv.brightness,
                ),
              ),
          });
          appendSlider("wsgm-native-qam-lighting-color-brightness", {
            label: localizeOr(controlRuntime, "#ColorPicker_Brightness", "Brightness"),
            min: 0,
            max: 100,
            step: 1,
            value: colorBrightnessEcho.value,
            valueSuffix: "%",
            showValue: true,
            disabled: isBusy(zone.progress),
            onChange: colorBrightnessEcho.onChange,
            onChangeComplete: (next) =>
              colorBrightnessEcho.onChangeComplete(next, (brightness) =>
                commitColor(
                  hueEcho.value ?? hsv.hue,
                  saturationEcho.value ?? hsv.saturation,
                  brightness,
                ),
              ),
          });
        }
        if (!rows.length) return note("deviceControls", "no compatible charge or lighting rows");
        renderOutcomes.deviceControls = `rendered ${rows.length} row(s)`;
        return controlRuntime.react.createElement(controlRuntime.react.Fragment, null, ...rows);
      };
    // Steam's own FPS counter rows, which WSGM replaces with its RTSS-driven overlay. Identified by
    // localising the same tokens Steam did rather than by CSS class or visible text: the classes
    // are hashed per client build and the text changes with the user's language, while the token is
    // the one thing that is neither.
    const NativeFpsTokens = [
      "#QuickAccess_Tab_Perf_FPS_Corner",
      "#QuickAccess_Tab_Perf_FPS_Contrast",
    ];
    let filteredNative = null;
    let lastHidden = 0;
    // Wrappers that carry the filter into a component's own render output, cached against the
    // component so React keeps seeing one stable type per original and never remounts the subtree.
    const descendCache = new WeakMap();
    /// Removes the native rows whose label matches one of the tokens above.
    ///
    /// Descends through RENDERED output, not just props.children. The rows sit about ten levels
    /// inside Steam's panel behind component elements, and a component's children do not exist
    /// until React renders it — so a walk over props.children alone reaches nothing, which is why
    /// the filter previously ran and hid zero rows. Each function component met on the way down is
    /// replaced by a wrapper that renders the original and filters what it returns, which is the
    /// same mechanism Decky's createReactTreePatcher uses to reach into this panel.
    const hideNativeRows = (controlRuntime, element, labels, depth) => {
      if (depth > 12 || !controlRuntime.react.isValidElement(element)) return element;
      // Compared as text on both sides: a label is sometimes a localiser element and sometimes a
      // plain string, and matching the raw prop found nothing at all.
      const label = textOf(element.props && element.props.label);
      if (label !== null && labels.includes(label)) {
        lastHidden++;
        return null;
      }
      const type = element.type;
      if (typeof type === "function" && !type.prototype?.isReactComponent) {
        // A plain function component: render it through a wrapper so its output is filtered too.
        // Class components, memo and forwardRef objects are left alone — they cannot be called
        // directly, and wrapping them would change identity for refs.
        let wrapper = descendCache.get(type);
        if (!wrapper) {
          wrapper = function WsgmNativeQamDescend(props) {
            return hideNativeRows(controlRuntime, type(props), labels, 0);
          };
          descendCache.set(type, wrapper);
        }
        // The key rides along explicitly: it lives on the element, not in props, and dropping it
        // would re-key this node inside its parent's child list on every render.
        return controlRuntime.react.createElement(
          wrapper,
          element.key === null ? element.props : { ...element.props, key: element.key },
        );
      }
      const kids = controlRuntime.react.Children.toArray(element.props?.children);
      if (!kids.length) return element;
      let changed = false;
      const next = [];
      for (const kid of kids) {
        const replacement = hideNativeRows(controlRuntime, kid, labels, depth + 1);
        changed ||= replacement !== kid;
        if (replacement !== null) next.push(replacement);
      }
      return changed ? controlRuntime.react.cloneElement(element, {}, ...next) : element;
    };
    /// Wraps Steam's performance root so its OUTPUT can be filtered.
    ///
    /// The root returns a single component element with no static children, so its rows exist only
    /// once React renders it. Calling it from inside a component of our own is what puts its output
    /// in reach; the wrapper is cached against the inner component so React sees a stable type and
    /// does not remount the panel on every render.
    const withNativeRowsHidden = (controlRuntime, tree) => {
      const inner = tree && tree.type;
      if (typeof inner !== "function") return tree;
      const labels = NativeFpsTokens.map((token) => textOf(controlRuntime.localize(token))).filter(
        (text) => typeof text === "string" && text.length > 0 && text[0] !== "#",
      );
      if (!labels.length) return tree;
      if (!filteredNative || filteredNative.inner !== inner) {
        filteredNative = {
          inner,
          component: function WsgmNativeQamFilteredPerformance(props) {
            lastHidden = 0;
            const filtered = hideNativeRows(controlRuntime, inner(props), labels, 0);
            if (appendDiagnostics.perf) {
              appendDiagnostics.perf.nativeRowsHidden = lastHidden;
            }
            return filtered;
          },
        };
      }
      return controlRuntime.react.createElement(filteredNative.component, tree.props);
    };
    const appendControls = (controlRuntime, tree, placement = "perf") => {
      // Rendered React elements from Steam's own untyped runtime.
      const controls = [];
      // The one visible ordering table. It is the device-set order: profile identity, observation,
      // pacing, VRR, power, automatic power, display, controller, reset. A kind's registration,
      // component and placement are checked in one loop instead of three parallel structures and
      // ten almost-identical append branches.
      const rows = [
        [
          "valveProfileHeader",
          "wsgm-native-qam-valve-profile-header",
          valveProfileHeaderControl,
          "perf",
        ],
        [
          "valveProfileHeader",
          "wsgm-native-qam-valve-profile-toggle",
          valveProfileToggleControl,
          "perf",
        ],
        [
          "valveOverlayLevel",
          "wsgm-native-qam-valve-overlay-level",
          valveOverlayLevelControl,
          "perf",
        ],
        ["frameLimit", "wsgm-native-qam-frame-limit", frameLimitControl, "perf"],
        ["vrr", "wsgm-native-qam-vrr", vrrControl, "perf"],
        ["valveTdp", "wsgm-native-qam-valve-tdp-enabled", valveTdpToggleControl, "perf"],
        ["valveTdp", "wsgm-native-qam-valve-tdp", valveTdpSliderControl, "perf"],
        ["autoTdp", "wsgm-native-qam-auto-tdp", autoTdpControl, "perf"],
        ["resolution", "wsgm-native-qam-resolution", resolutionControl, "quickSettings"],
        [
          "valveRefreshRate",
          "wsgm-native-qam-valve-refresh-rate",
          valveRefreshRateControl,
          "quickSettings",
        ],
        ["controllerTarget", "wsgm-native-qam-controller-target", controllerControl, "perf"],
        ["valveReset", "wsgm-native-qam-valve-reset", valveResetControl, "perf"],
      ];
      for (const [kind, key, component, rowPlacement] of rows) {
        if (rowPlacement !== placement || !registrations.has(kind) || !component) continue;
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key },
            controlRuntime.react.createElement(component),
          ),
        );
      }
      if (
        placement === "quickSettings" &&
        registrations.has("deviceControls") &&
        deviceControlsControl
      ) {
        controls.push(
          controlRuntime.react.createElement(deviceControlsControl, {
            key: "wsgm-native-qam-device-controls",
          }),
        );
      }
      if (!controls.length) {
        appendDiagnostics[placement] = { controls: 0, inserted: false, ownSection: false };
        return tree;
      }
      // Quick Settings takes a plain appended section and nothing else. The native-row filtering
      // below is about Steam's FPS counter rows on the PERFORMANCE panel; running it against a
      // different tab's tree would be hiding rows this code has never even looked at.
      if (placement === "quickSettings") {
        const section = controlRuntime.react.createElement(
          controlRuntime.section,
          { key: "wsgm-native-qam-quick-settings-section" },
          ...controls,
        );
        appendDiagnostics[placement] = {
          controls: controls.length,
          inserted: true,
          ownSection: true,
        };
        // Display controls lead the tab rather than trailing it: brightness and the shortcut
        // toggles read below them naturally, and a dropdown at the bottom of a scrolling tab is
        // the control a user finds last.
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          section,
          tree,
        );
      }
      // WSGM's rows go into a PanelSection of their own, appended after whatever the native
      // performance panel rendered.
      //
      // The previous implementation searched the tree for a component identical to
      // controlRuntime.section and inserted into it. That could never work, on any OS: `tree` is
      // the ELEMENT returned by performanceRoot(props), and an element's props.children holds only
      // what was passed IN, never what its component produces when React renders it. Steam's
      // section exists only after that rendering, so the walk terminated on a root with no
      // children — measured on the reference Claw as depthReached 0, sectionSeen false, with the
      // section component itself resolved and all five rows built. It failed silently, which is
      // why an empty Quick Access panel survived so long: every other signal said success.
      //
      // Appending a section instead depends on nothing about Steam's internal tree shape, so it
      // cannot be broken by a Steam UI change or by the fields Windows hides.
      const own = controlRuntime.react.createElement(
        controlRuntime.section,
        { key: "wsgm-native-qam-section" },
        ...controls,
      );
      // Shape of what Steam's performance root returned, so the rows it renders can be identified
      // without guessing. Needed to suppress Steam's own FPS counter rows in favour of WSGM's
      // RTSS overlay: their DOM classes are hashed per client build and unusable as selectors.
      const describe = (element, depth) => {
        if (!controlRuntime.react.isValidElement(element)) return typeof element;
        const t = element.type;
        const name = typeof t === "string" ? t : t?.displayName || t?.name || "anonymous";
        const kids = controlRuntime.react.Children.toArray(element.props?.children);
        return depth >= 2 || !kids.length
          ? name
          : { [name]: kids.map((k) => describe(k, depth + 1)) };
      };
      // Steam's FPS rows are suppressed only on this path, which runs when WSGM has rows of its own
      // to put in their place. Hiding them and then rendering nothing would leave the user neither.
      const native = withNativeRowsHidden(controlRuntime, tree);
      appendDiagnostics.perf = {
        controls: controls.length,
        inserted: true,
        ownSection: true,
        tree: JSON.stringify(describe(tree, 0)).slice(0, 600),
        nativeFiltered: native !== tree,
      };
      return controlRuntime.react.createElement(controlRuntime.react.Fragment, null, native, own);
    };
    const ensurePatched = () => {
      if (
        controlRuntime &&
        performanceRoot &&
        patchedUseMemo &&
        controlRuntime.react.useMemo === patchedUseMemo
      )
        return true;
      runtime = getWebpackRuntime("native-components");
      if (!runtime || !runtime.m) {
        lastPatchError = "webpack runtime unavailable";
        return false;
      }
      const performanceFactory = uniqueFactory([
        "#QuickAccess_Tab_Perf_Common_Settings",
        "#QuickAccess_Tab_Perf_BatteryTimeRemaining",
        "TS.ON_FRAME",
      ]);
      controlRuntime = createControlRuntime();
      if (!performanceFactory) {
        lastPatchError = "performance panel factory was not a unique match";
        return false;
      }
      if (!controlRuntime) {
        lastPatchError = "React, fields, layout or localization runtime was not a unique match";
        return false;
      }
      performanceRoot = uniqueFunction(runtime(performanceFactory[0]), ["TS.ON_FRAME", "return"]);
      if (!performanceRoot) {
        lastPatchError = "performance panel root was not a unique match";
        return false;
      }
      autoTdpControl = createAutoTdpControl(controlRuntime);
      frameLimitControl = createFrameLimitControl(controlRuntime);
      controllerControl = createControllerControl(controlRuntime);
      resolutionControl = createResolutionControl(controlRuntime);
      vrrControl = createVrrControl(controlRuntime);
      deviceControlsControl = createDeviceControlsControl(controlRuntime);
      // Selected by the localization token it draws, never by a minified export name: the names are
      // right for today's build and are not guaranteed for the next. Live-probed 2026-08-30 that
      // this token matches exactly one export of the components module.
      const perfComponents = uniqueFactory([
        "#QuickAccess_Tab_Perf_EnableVRR",
        "#QuickAccess_Tab_Perf_LimitFrameRate",
      ]);
      const perfExports = perfComponents ? runtime(perfComponents[0]) : null;
      valveProfileHeaderControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_GameSpecificSettings"])
        : null;
      // The toggle reads current_game_id for availability, current==active for its checked state,
      // and writes through SetGameSpecificProfileEnabled — all state WSGM already backs. Without
      // this row nothing in the tab can enable a per-game profile.
      valveProfileToggleControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_ToggleGameSettings"])
        : null;
      valveResetControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_ResetToDefault"])
        : null;
      valveRefreshRateControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_RefreshRate"])
        : null;
      valveOverlayLevelControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_Overlay_Level"])
        : null;
      // A DIFFERENT module from the perf components above: the power-limit rows live with the
      // GPU-clock and charge-limit rows, next to the SteamOS Manager hooks they read. Selected by
      // the setting each one is bound to plus its own token, because both rows carry
      // #QuickAccess_Tab_Perf_TDPLimitEnabled — the toggle as its label, the slider as its
      // explainer title. Live-verified 2026-08-30 that each pair matches exactly one export.
      const tdpComponents = uniqueFactory([
        "#QuickAccess_Tab_Perf_TDPLimitEnabled",
        "#QuickAccess_Tab_Perf_TDPLimitUnits",
      ]);
      const tdpExports = tdpComponents ? runtime(tdpComponents[0]) : null;
      valveTdpToggleControl = tdpExports
        ? uniqueFunction(tdpExports, [
            '"steamos_tdp_limit_enabled"',
            "#QuickAccess_Tab_Perf_TDPLimitEnabled",
          ])
        : null;
      valveTdpSliderControl = tdpExports
        ? uniqueFunction(tdpExports, ["#QuickAccess_Tab_Perf_TDPLimitUnits"])
        : null;
      function WsgmNativeQamPerformanceRoot(props) {
        const [, setRevision] = controlRuntime.react.useState(0);
        controlRuntime.react.useEffect(
          () => subscribeHost(() => setRevision((value) => value + 1)),
          [],
        );
        return appendControls(controlRuntime, performanceRoot(props));
      }
      originalUseMemo = controlRuntime.react.useMemo;
      // One wrapper per wrapped tab, matched by root identity in the same memoized tab array.
      // Each root must match exactly once or it is left alone — the discipline that kept the
      // performance wrap honest, applied per root rather than to the array as a whole.
      // The performance panel is matched by export identity; the Quick Settings panel CANNOT be —
      // a tap on the tab array (2026-08-30) showed its type is a local function no module exports.
      // It is matched by its own source instead, on two Valve strings WSGM's gates never touch: the
      // Other-section title and the reorder-controllers button. Deliberately NOT the brightness
      // title, because that is the surface WSGM's own gate reveals, and a selector must not be
      // entangled with a thing this code changes.
      const wrappers = [
        {
          match: (type) => type === performanceRoot,
          component: () => WsgmNativeQamPerformanceRoot,
          fallbackKey: "wsgm-native-qam-performance-root",
        },
        {
          match: (type) => {
            if (typeof type !== "function" || type === performanceRoot) return false;
            const source = String(type);
            return (
              source.includes("#QuickAccess_Tab_Settings_Section_Other_Title") &&
              source.includes("#QuickAccess_ReorderControllers_Button")
            );
          },
          // The original is only known at match time, so the wrapper is built then — and cached by
          // original, because a fresh component identity on every memo pass would remount the whole
          // tab on each render.
          component: (original) => {
            let wrapped = quickSettingsWrapCache.get(original);
            if (!wrapped) {
              wrapped = function WsgmNativeQamQuickSettingsRoot(props) {
                const [, setRevision] = controlRuntime.react.useState(0);
                controlRuntime.react.useEffect(
                  () => subscribeHost(() => setRevision((value) => value + 1)),
                  [],
                );
                quickSettingsRoot = original;
                return appendControls(controlRuntime, original(props), "quickSettings");
              };
              quickSettingsWrapCache.set(original, wrapped);
            }
            return wrapped;
          },
          fallbackKey: "wsgm-native-qam-quick-settings-root",
        },
      ];
      patchedUseMemo = function WsgmNativeQamUseMemo(factory, dependencies) {
        const value = originalUseMemo(factory, dependencies);
        if (!Array.isArray(value)) return value;
        let result = value;
        for (const wrapper of wrappers) {
          const matches = result.filter(
            (item) =>
              item &&
              typeof item === "object" &&
              controlRuntime.react.isValidElement(item.panel) &&
              wrapper.match(item.panel.type),
          );
          if (matches.length !== 1) continue;
          result = result.map((item) => {
            if (item !== matches[0]) return item;
            const panel = controlRuntime.react.createElement(wrapper.component(item.panel.type), {
              ...item.panel.props,
              key: item.panel.key ?? wrapper.fallbackKey,
            });
            return { ...item, panel };
          });
        }
        return result;
      };
      controlRuntime.react.useMemo = patchedUseMemo;
      if (controlRuntime.react.useMemo !== patchedUseMemo) {
        lastPatchError = "React useMemo wrapper could not be installed";
        return false;
      }
      lastPatchError = "";
      return true;
    };
    const install = (kind) => {
      if (disposedHost || !Object.hasOwn(definitions, kind))
        return { ok: false, error: "component is not allowlisted" };
      if (!ensurePatched())
        return {
          ok: false,
          error: lastPatchError || "native performance root is incompatible",
        };
      registrations.set(kind, definitions[kind].patchId);
      notify();
      return { ok: true, kind, registered: true, hostVersion: 1 };
    };
    const remove = (kind) => {
      if (!Object.hasOwn(definitions, kind)) return { ok: true, absent: true };
      registrations.delete(kind);
      notify();
      if (
        !registrations.size &&
        controlRuntime &&
        originalUseMemo &&
        controlRuntime.react.useMemo === patchedUseMemo
      ) {
        controlRuntime.react.useMemo = originalUseMemo;
      }
      return { ok: true, kind, registered: false };
    };
    const status = (kind) => ({
      ok: Object.hasOwn(definitions, kind),
      kind,
      registered: registrations.has(kind),
      hostVersion: 1,
      performanceRootWrapped:
        !!controlRuntime && !!patchedUseMemo && controlRuntime.react.useMemo === patchedUseMemo,
      // Everything above can be true while the panel still shows nothing, because insertion
      // depends on the shape of the tree Steam renders. This is the part that says so.
      lastAppend: appendDiagnostics.perf,
      lastAppendQuickSettings: appendDiagnostics.quickSettings,
      quickSettingsRootResolved: !!quickSettingsRoot,
      // And this says which rows drew, and why the others did not.
      renderOutcomes,
      toggleResolved: !!(controlRuntime && controlRuntime.toggle),
      lastError: lastPatchError,
    });
    const disposeHostResources = () => {
      disposedHost = true;
      registrations.clear();
      notify();
      listeners.clear();
      if (controlRuntime && originalUseMemo && controlRuntime.react.useMemo === patchedUseMemo)
        controlRuntime.react.useMemo = originalUseMemo;
    };
    return { install, remove, status, dispose: disposeHostResources };
  }
  registerGate("nativeComponents", createNativeComponentHost());
  // The last fragment in the bundle, and the only thing in it.
  //
  // bridge.ts opens the IIFE and every other fragment is concatenated into it, so the value the
  // injected script evaluates to has to be returned AFTER the last of them — a gate registers with a
  // top-level call, and a return placed before those calls makes every one of them unreachable. That
  // is not a hypothetical: it shipped, and it published a bridge whose registry was empty while the
  // bootstrap patch still verified, so every gate reported "bridge unavailable" with nothing in the
  // log naming why.
  //
  // Keeping the return here rather than in the builder's epilogue string keeps the result shape the
  // bridge's own business; the builder only has to emit this file last and close the IIFE.
  return installResult;
})();
