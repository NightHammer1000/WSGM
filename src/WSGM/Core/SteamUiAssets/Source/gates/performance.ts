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
  let unsubscribe: (() => void) | null = null;

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
