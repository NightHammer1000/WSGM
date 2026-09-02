(async () => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_btafter_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const out = {};
  const st = req("21371").L.getQueryState(["BluetoothManagerService", "State"]);
  out.cached = st && st.data;
  const rf = Object.values(req("60517")).find((v) => v && typeof v === "object" && v.GetState);
  const r = await rf.GetState({});
  out.live = r.Body().toObject();
  return JSON.stringify(out);
})();
