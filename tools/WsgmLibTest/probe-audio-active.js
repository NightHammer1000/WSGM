(() => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_av4_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const s = String(req.m["1409"]);
  const at = s.indexOf("OnAudioDeviceVolumeChanged=");
  const at2 = at < 0 ? s.indexOf("OnAudioDeviceVolumeChanged(") : at;
  // Find the DEFINITION, not the registration: look for the arrow/method body.
  const defAt = s.indexOf(
    "OnAudioDeviceVolumeChanged",
    s.indexOf("OnAudioDeviceVolumeChanged") + 10,
  );
  return JSON.stringify({ def: s.slice(Math.max(0, defAt - 30), defAt + 280) });
})();
