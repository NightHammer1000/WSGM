(async () => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_sc2_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const out = {};
  // The message class qt serializes with: `b.Ne` where b is an import of 33867.
  const src = String(req.m["33867"]);
  const importMatch = src.match(
    /([A-Za-z_$]+)=r\((\d+)\)[^;]*;?[\s\S]{0,600}?\1\.Ne\.serializeBinaryToWriter/,
  );
  const clsModule = importMatch ? importMatch[2] : null;
  out.clsModule = clsModule;
  const Ne = clsModule ? req(clsModule).Ne : null;
  out.clsName = Ne
    ? (() => {
        try {
          return new Ne().getClassName?.();
        } catch {
          return null;
        }
      })()
    : null;

  const captured = [];
  let handle = null;
  try {
    handle = window.SteamClient.Settings.RegisterForSettingsArrayChanges((...args) => {
      captured.push(args.map((a) => (typeof a === "string" ? a.slice(0, 80) : typeof a)));
      if (Ne && typeof args[0] === "string") {
        try {
          const bytes = Uint8Array.from(atob(args[0]), (c) => c.charCodeAt(0));
          const obj = Ne.deserializeBinary(bytes).toObject();
          out.decoded = Object.fromEntries(
            Object.entries(obj).filter(([, v]) => v !== undefined && v !== null),
          );
        } catch (e) {
          out.decodeErr = String(e);
        }
      }
    });
  } catch (e) {
    out.regErr = String(e);
  }
  try {
    req("33867").qt("steamos_tdp_limit", 21);
  } catch (e) {
    out.writeErr = String(e);
  }
  await new Promise((resolve) => setTimeout(resolve, 1200));
  out.captured = captured.slice(0, 2);
  try {
    handle?.unregister?.();
  } catch {}
  return JSON.stringify(out);
})();
