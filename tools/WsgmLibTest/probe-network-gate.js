// Probe: what computes networkManagementAvailable, and whether Steam already holds live
// wireless device and access-point state on this Windows client. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_netgate_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const safe = (label, f) => {
    try {
      out[label] = f();
    } catch (e) {
      out[label] = "ERR " + String(e).slice(0, 200);
    }
  };
  const mod = runtime("77347");
  const proto = Object.getPrototypeOf(mod.OQ.Get());

  // Getter sources for the gates.
  for (const name of [
    "networkManagementAvailable",
    "hasWirelessDevice",
    "wirelessNetworkDevice",
    "userVisibleAccessPoints",
    "presentAccessPoints",
    "isWifiEnabled",
  ]) {
    safe("src_" + name, () => {
      const d = Object.getOwnPropertyDescriptor(proto, name);
      if (!d) return "no descriptor";
      return String(d.get || d.value).slice(0, 500);
    });
  }

  const store = mod.OQ.Get();
  safe("live_hasWirelessDevice", () => store.hasWirelessDevice);
  safe("live_isWifiEnabled", () => store.isWifiEnabled);
  safe("live_accessPointCount", () => {
    const a = store.accessPoints;
    return Array.isArray(a) ? a.length : typeof a;
  });
  safe("live_userVisibleCount", () => {
    const a = store.userVisibleAccessPoints;
    return Array.isArray(a) ? a.length : typeof a;
  });
  safe("live_wirelessDevice", () => {
    const d = store.wirelessNetworkDevice;
    if (!d) return null;
    const r = {};
    for (const k of Object.keys(d)) {
      const v = d[k];
      r[k] = v && typeof v === "object" ? typeof v : v;
    }
    return r;
  });
  safe("live_firstAccessPoints", () => {
    const a = store.userVisibleAccessPoints || store.accessPoints;
    if (!Array.isArray(a)) return typeof a;
    return a.slice(0, 5).map((p) => ({
      ssid: p?.strSSID ?? p?.ssid ?? "?",
      strength: p?.nStrength ?? p?.strength,
      sec: p?.eSecurity ?? p?.security,
    }));
  });
  return JSON.stringify(out);
})();
