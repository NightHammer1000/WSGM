// Probe: which export of the performance-component module renders which control, so mounting can
// select a component by the localization token it draws rather than by a minified export name that
// rotates on every Steam build.
//
// Resolves ONE named module id and reads its exports' SOURCES. It constructs nothing and calls
// nothing — see the rule in docs\steam-cef.md.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_perf_exports_probe_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);

  const tokens = {
    header: "#QuickAccess_Tab_Perf_PerformanceSettings",
    perGame: "#QuickAccess_Tab_Perf_GameSpecificSettings",
    view: "#Common_Advanced_View",
    reset: "#QuickAccess_Tab_Perf_ResetToDefault",
    frameLimit: "#QuickAccess_Tab_Perf_LimitFrameRate",
    overlayLevel: "#QuickAccess_Tab_Perf_Overlay_Level",
    refreshRate: "#QuickAccess_Tab_Perf_RefreshRate",
    vrr: "#QuickAccess_Tab_Perf_EnableVRR",
  };

  const out = {};
  try {
    const module = runtime("83571");
    out.exportCount = Object.keys(module).length;
    for (const [name, token] of Object.entries(tokens)) {
      const matches = Object.keys(module).filter((key) => {
        const value = module[key];
        return typeof value === "function" && String(value).includes(token);
      });
      // The count is what matters: exactly one means the token identifies a component uniquely and
      // is safe to select by. More than one means the selector needs another discriminator.
      out[name] = { count: matches.length, exports: matches.slice(0, 4) };
    }
  } catch (error) {
    out.error = String(error);
  }

  return JSON.stringify(out);
})();
