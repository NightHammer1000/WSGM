// Probe: the availability gates behind each Quick Settings section, and which SteamClient
// surfaces back them. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_gates_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const grab = (label, id, needle, before, after) => {
    try {
      const src = String(runtime.m[id]);
      const i = src.indexOf(needle);
      out[label] = i < 0 ? "NOT FOUND: " + needle : src.slice(Math.max(0, i - before), i + after);
    } catch (e) {
      out[label] = "ERR " + e;
    }
  };
  // Brightness / night mode / airplane store (59547)
  grab("brightness_store", "59547", "SetNightModeEnabled", 1400, 600);
  // Wi-Fi availability (77347) and the Wi-Fi row (89600)
  grab("wifi_gate", "77347", "function Ev", 0, 700);
  grab("wifi_row", "89600", "cV", 0, 900);
  // Bluetooth availability (25467) and row (66943)
  grab("bt_gate", "25467", "function Iz", 0, 700);
  grab("bt_row", "66943", "ToggleLabel", 900, 500);
  // Audio availability (1409)
  grab("audio_gate", "1409", "function In", 0, 700);
  // The unidentified section (17386)
  grab("unknown_section", "17386", "function DP", 0, 700);
  // What SteamClient surfaces exist for these?
  try {
    const sc = window.SteamClient || {};
    out.hasSystemAudio = typeof sc.System?.Audio;
    out.hasSystemBluetooth = typeof sc.System?.Bluetooth;
    out.systemNetwork = Object.keys(sc.System?.Network || {});
    out.systemDisplay = Object.keys(sc.System?.Display || {});
    out.systemPerf = typeof sc.System?.Perf;
  } catch (e) {
    out.scErr = String(e);
  }
  return JSON.stringify(out);
})();
