// Probe: that the selector the shim uses to mount Valve's VRR control resolves to exactly one
// module and one export. This is the selector verbatim — module by structural tokens, then export
// by the localization token it draws — so a pass here is evidence for the real code path and not
// for a lookalike.
//
// Resolves ONE module id, found by reading factory sources. Nothing is constructed and no module is
// resolved by loop — see the rule in docs\steam-cef.md.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_valve_vrr_probe_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);

  const uniqueFactory = (requiredTokens) => {
    const matches = Object.entries(runtime.m).filter(([, factory]) => {
      const source = String(factory);
      return requiredTokens.every((token) => source.includes(token));
    });
    return matches.length === 1 ? matches[0] : null;
  };

  const out = {};
  try {
    const matches = Object.entries(runtime.m).filter(([, factory]) => {
      const source = String(factory);
      return (
        source.includes("#QuickAccess_Tab_Perf_EnableVRR") &&
        source.includes("#QuickAccess_Tab_Perf_LimitFrameRate")
      );
    });
    out.moduleMatches = matches.length;

    const factory = uniqueFactory([
      "#QuickAccess_Tab_Perf_EnableVRR",
      "#QuickAccess_Tab_Perf_LimitFrameRate",
    ]);
    if (!factory) return JSON.stringify({ ...out, error: "module not unique" });
    out.moduleId = factory[0];

    const exports = runtime(factory[0]);
    const components = Object.values(exports).filter(
      (value) =>
        typeof value === "function" && String(value).includes("#QuickAccess_Tab_Perf_EnableVRR"),
    );
    out.exportMatches = components.length;
    out.isFunction = components.length === 1 && typeof components[0] === "function";
  } catch (error) {
    out.error = String(error);
  }

  return JSON.stringify(out);
})();
