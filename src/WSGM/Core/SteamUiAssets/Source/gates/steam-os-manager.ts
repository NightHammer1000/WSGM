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
  let unsubscribe: (() => void) | null = null;
  let unsubscribeSettings: (() => void) | null = null;
  let originalGetState: any = null;
  let manager: any = null;
  let latest: { available: boolean; min: number; max: number } = {
    available: false,
    min: 0,
    max: 0,
  };
  let lastSentWatts: number | null = null;
  let lastSentEnabled: boolean | null = null;
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
        typeof (value as any).GetState === "function" &&
        typeof (value as any).RefreshScreenReaderAutoLocale === "function"
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
