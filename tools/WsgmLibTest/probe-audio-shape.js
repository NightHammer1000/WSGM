// Probe: the exact shapes Steam's audio store consumes from SteamClient.System.Audio --
// the GetDevices response and the device/app objects the registrations receive.
// Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_audioshape_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const src = String(runtime.m["1409"]);
  const out = {};
  const grab = (label, needle, before, after) => {
    const i = src.indexOf(needle);
    out[label] = i < 0 ? "NOT FOUND" : src.slice(Math.max(0, i - before), i + after);
  };

  // The GetDevices consumer, which names every field of the response.
  grab("getDevices", "GetDevices()", 60, 1400);
  // The device-added handler, which names the device object's fields.
  grab("onDeviceAdded", "OnAudioDeviceAdded", 40, 900);
  // The volume-changed handler.
  grab("onVolumeChanged", "OnAudioDeviceVolumeChanged", 40, 600);
  // The app-added handler, for the per-app mixer shape.
  grab("onAppAdded", "OnAudioAppAdded", 40, 700);
  return JSON.stringify(out);
})();
