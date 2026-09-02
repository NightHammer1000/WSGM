// Probe: the structural counts NativeQamResolutionPatch requires, so the row's fingerprint is
// checked against the live client rather than assumed from the frame-limit row it copies.
//
// This is the patch's OWN probe expression, verbatim. It reads module factory SOURCES as strings
// and never calls runtime(id) or constructs anything — see the rule in docs\steam-cef.md.
(() => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_native_resolution_probe_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  if (!req || !req.m) return JSON.stringify({ error: "webpack unavailable" });
  const count = (tokens) =>
    Object.values(req.m).reduce((total, factory) => {
      const source = String(factory);
      return total + (tokens.every((token) => source.includes(token)) ? 1 : 0);
    }, 0);
  return JSON.stringify({
    performanceActions: count([
      "SetFPSLimitEnabled",
      "SetFPSLimit",
      "SetPerfOverlayLevel",
      "SteamClient.System.Perf",
    ]),
    performanceRoot: count([
      "#QuickAccess_Tab_Perf_Common_Settings",
      "#QuickAccess_Tab_Perf_BatteryTimeRemaining",
      "TS.ON_FRAME",
    ]),
    nativeFields: count(["DialogSlider_Container", "DropDownField", "SliderField"]),
    nativeLayout: count(["PanelSectionTitle", "PanelSectionRow", "spinner"]),
    localization: count([
      "Attempting to localize token",
      "Unable to find localization token",
      "LocalizeString",
    ]),
    react: count(["react.transitional.element", "useState", "cloneElement", "createElement"]),
  });
})();
