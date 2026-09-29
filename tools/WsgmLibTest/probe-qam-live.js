// Probe: the live value of each Quick Settings availability gate on this Windows client,
// so we know which rows would render if mounted. Reads only; toggles nothing.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_qamlive_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const tryGet = (label, f) => {
    try {
      out[label] = f();
    } catch (e) {
      out[label] = "ERR " + String(e).slice(0, 160);
    }
  };
  // Wi-Fi / network management store (77347).
  tryGet("network_module_exports", () => Object.keys(runtime("77347")));
  tryGet("wifi_available", () => {
    const m = runtime("77347");
    return typeof m.Ev === "function" ? m.Ev() : "no Ev";
  });
  // Audio store (1409).
  tryGet("audio_module_exports", () => Object.keys(runtime("1409")));
  // Brightness / system manager settings store (59547).
  tryGet("brightness_module_exports", () => Object.keys(runtime("59547")));
  // Bluetooth availability comes from the SteamOS manager state (33706).
  tryGet("steamos_manager_state", () => {
    const m = runtime("33706");
    return Object.keys(m);
  });
  // Global stores Steam exposes on window, which are readable without hooks.
  tryGet("windowStores", () =>
    Object.keys(window)
      .filter((k) => /Store$|Manager$/.test(k))
      .slice(0, 60),
  );
  return JSON.stringify(out);
})();
