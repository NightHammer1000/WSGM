(() => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_vol3_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const s = String(req.m["1409"]);
  const out = [];
  for (const name of [
    "flOutputVolume",
    "flInputVolume",
    "RegisterOrUpdateDevice",
    "OnVolumeUpdated",
  ]) {
    const at = s.indexOf(name);
    out.push({ name, slice: at < 0 ? null : s.slice(Math.max(0, at - 260), at + 200) });
  }
  return JSON.stringify(out);
})();
