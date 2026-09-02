(() => {
  const store = window.SystemPerfStore;
  const system = window.SteamClient?.System;
  if (!store || !system) return JSON.stringify({ error: "missing store/system" });
  if (system.Perf) return JSON.stringify({ skipped: "Perf already present" });
  const out = {};
  Object.defineProperty(system, "Perf", {
    configurable: true,
    enumerable: true,
    value: {
      UpdateSettings: () => Promise.resolve(),
      RegisterForStateChanges: () => ({ unregister: () => {} }),
      RegisterForDiagnosticInfoChanges: () => ({ unregister: () => {} }),
    },
  });
  store.m_msgState.limits = {
    fps_limit_options: [0, 30, 40, 60, 120],
    tdp_limit_min: 8,
    tdp_limit_max: 37,
    is_vrr_supported: true,
    disable_refresh_rate_management: false,
  };
  store.m_msgState.settings = {
    global: { perf_overlay_level: 2 },
    per_app: {
      fps_limit: 60,
      is_fps_limit_enabled: true,
      is_vrr_enabled: true,
      is_game_perf_profile_enabled: true,
    },
  };
  store.m_msgState.current_game_id = "12345";
  store.m_msgState.active_profile_game_id = "12345";
  // Read back through exactly the accessors the hooks use.
  out.namespacePresent = system.Perf != null;
  out.limits = !!store.msgLimits;
  out.fpsOptions = store.msgLimits.fps_limit_options;
  out.frameLimitAvailable = !store.msgLimits.disable_refresh_rate_management;
  out.vrrSupported = store.msgLimits.is_vrr_supported;
  out.overlayLevel = store.msgSettingsGlobal.perf_overlay_level;
  out.perGameProfileOn = store.msgSettingsPerApp.is_game_perf_profile_enabled;
  out.perGameActive = store.nCurrentGameID === store.nActiveProfileGameID;
  store.m_msgState.limits = undefined;
  store.m_msgState.settings = undefined;
  store.m_msgState.current_game_id = undefined;
  store.m_msgState.active_profile_game_id = undefined;
  delete system.Perf;
  out.restored = !store.msgLimits && system.Perf === undefined;
  return JSON.stringify(out);
})();
