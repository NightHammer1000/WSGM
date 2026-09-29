// Wi-Fi is hidden by one getter, not by an absent backend. Steam's Windows client genuinely
// tracks the wireless device — hasWirelessDevice and isWifiEnabled are true here without any
// help — and only `get networkManagementAvailable(){return TS.IS_STEAMOS}` keeps the UI away.
//
// Overriding that one property is narrow and reversible and affects one surface. Setting the
// constant it reads would produce the same row while changing unrelated client behaviour
// everywhere, which is the spoof D16 forbids. Live-verified 2026-08-30: the descriptor is
// configurable, the override flips the value, and restoring the saved descriptor puts it back.
function createNetworkGate() {
  const property = "networkManagementAvailable";
  const patchId = "wsgm.steam-network.gate";
  const availability = {
    marker: "__wsgmOwnedGetter",
    original: "__wsgmOriginalGetterDescriptor",
  };
  const scan = {
    marker: "__wsgmOwnedNetworkScan",
    original: "__wsgmOriginalNetworkScan",
  };
  let target: object | null = null;
  let lastError = "";
  let scanWrapped = false;
  let originalStart: ((...args: unknown[]) => unknown) | null = null;
  let originalStop: ((...args: unknown[]) => unknown) | null = null;
  let unsubscribe: (() => void) | null = null;
  let syntheticKeys: string[] = [];

  const store = () => {
    try {
      const req = getWebpackRuntime("network-store");
      return req?.("77347")?.OQ?.Get() ?? null;
    } catch {
      return null;
    }
  };

  const removeNetworkState = (refresh: boolean) => {
    const instance = store();
    if (instance) {
      const keys = new Set(syntheticKeys);
      // Compatibility cleanup for the retired standalone indicator, which used this exact
      // bounded id range but could not hand its closure-owned key list to the new gate.
      const deviceId = instance.m_WirelessDevice?.id;
      if (deviceId !== undefined) {
        for (let index = 0; index < 24; index += 1) keys.add(`${deviceId}:${990001 + index}`);
      }
      for (const key of keys) instance.m_mapNetworkAccessPoints?.delete(key);
      instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
      instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
    }
    syntheticKeys = [];
    if (refresh) {
      try {
        window.SteamClient?.System?.Network?.ForceRefresh?.();
      } catch {}
    }
  };

  // One resident owner now reveals AND feeds the network surface. The previous standalone
  // indicator installed a second script against this same store, with its own version sentinel
  // and retry timer; bridge state gives the generation-aware gate the same verified connected AP
  // for the header. Scan lifetime remains an observation of Steam's page, not an invented
  // connection protocol: its argument order has not been read from the client.
  const onState = (state) => {
    const instance = store();
    const networks = Array.isArray(state?.networks) ? state.networks.slice(0, 24) : [];
    if (!instance || !instance.m_WirelessDevice) {
      lastError = "network store has no wireless device";
      return;
    }
    if (networks.length === 0) {
      removeNetworkState(true);
      lastError = "";
      return;
    }

    try {
      const device = JSON.parse(JSON.stringify(instance.m_WirelessDevice));
      if (!device.wireless) device.wireless = { aps: [], esecurity_supported: 0 };
      const accessPoints = networks.map((network, index) => ({
        id: 990001 + index,
        esecurity: network.secured ? 16 : 0,
        estrength: Math.max(1, Math.min(4, Number(network.strength) || 1)),
        ssid: String(network.ssid || ""),
        is_active: network.connected === true,
        is_autoconnect: network.connected === true,
        is_hidden: false,
      }));
      const keys = accessPoints.map((accessPoint) => `${device.id}:${accessPoint.id}`);
      for (const key of syntheticKeys) {
        if (!keys.includes(key)) instance.m_mapNetworkAccessPoints.delete(key);
      }
      for (const key of keys) instance.m_mapNetworkAccessPoints.delete(key);
      device.estate = networks.some((network) => network.connected === true) ? 5 : device.estate;
      device.wireless.aps = accessPoints;
      accessPoints.forEach((accessPoint) => {
        instance.SetDeviceInfo(device, accessPoint.id);
        const entry = instance.m_mapNetworkAccessPoints.get(`${device.id}:${accessPoint.id}`);
        if (entry) entry.MarkAsNotPresent = () => {};
      });
      instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
      instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
      syntheticKeys = keys;
      lastError = "";
    } catch (error) {
      lastError = String(error);
    }
  };

  const install = () => {
    if (target) return { ok: true, alreadyInstalled: true };
    const instance = store();
    if (!instance) {
      lastError = "network store unavailable";
      return { ok: false, error: lastError };
    }

    // The getter lives on the prototype, so that is what is replaced and restored. Defining it
    // on the instance would shadow rather than replace, and removal would leave the shadow.
    //
    // Marked as ours for the same reason the namespaces are: the compatibility probe checks that
    // the getter currently reads false, and a successful override makes it read true. Left
    // unmarked, the patch reads its own success as "the client already reports this available,
    // stand aside", declares itself incompatible, and tears down — taking the network list with
    // it. The claim primitive is what keeps that from being re-derived here.
    const proto = Object.getPrototypeOf(instance);
    const claim = claimAccessor(proto, property, availability, () => true);
    if (!claim.ok) {
      lastError = claim.error;
      return { ok: false, error: lastError };
    }
    target = proto;
    lastError = "";
    wrapScanning();
    unsubscribe = subscribe(patchId, onState);
    return { ok: true, installed: true, available: instance[property] === true };
  };

  // Steam's own UI calls these when its network page opens and closes, so they are exactly the
  // signal for when a scan is worth running. WSGM's radio manager is otherwise driven by WSGM's
  // own panel, and a list refreshed only then would be stale on Steam's page — which is worse
  // than an empty one, because the user picks a network that is gone and the join fails silently.
  //
  // Both originals are always called through: this observes the lifetime, it does not take it
  // over, so a client that grows a working backend keeps behaving exactly as before.
  const wrapScanning = () => {
    const net = window.SteamClient?.System?.Network;
    if (!net || scanWrapped) return;
    const wrap = (name: string, command: string) => {
      // Checked before claiming, not inside the factory: a client without this method is one this
      // gate leaves alone entirely, and claiming would mark and reassign something that is not a
      // method at all.
      const current = net[name];
      const existing = claimed(current, scan) ? current[scan.original] : current;
      if (typeof existing !== "function") return null;

      let inner: ((...a: unknown[]) => unknown) | null = null;
      const claim = claimMember(net, name, scan, (original) => {
        inner = original as (...a: unknown[]) => unknown;
        return function (this: unknown, ...args: unknown[]) {
          // A scan request that cannot reach WSGM must not stop Steam's own call. Promise
          // rejection is handled explicitly; a try/catch only sees synchronous construction.
          void request(patchId, command, null).catch(() => {});

          return inner!.apply(this, args);
        };
      });
      return claim.ok ? inner : null;
    };

    originalStart = wrap("StartScanningForNetworks", "startScan");
    originalStop = wrap("StopScanningForNetworks", "stopScan");
    scanWrapped = !!(originalStart || originalStop);
  };

  const unwrapScanning = () => {
    const net = window.SteamClient?.System?.Network;
    if (!net || !scanWrapped) return;
    releaseMember(net, "StartScanningForNetworks", scan);
    releaseMember(net, "StopScanningForNetworks", scan);
    originalStart = null;
    originalStop = null;
    scanWrapped = false;
  };

  const remove = () => {
    unwrapScanning();
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }
    removeNetworkState(true);
    if (!target) return { ok: true, absent: true };
    const released = releaseAccessor(target, property, availability);
    if (!released.ok) {
      lastError = released.error ?? "network availability release failed";
      return { ok: false, error: lastError };
    }

    target = null;
    return { ok: true, removed: true };
  };

  const status = () => {
    const instance = store();
    return {
      ok: true,
      installed: !!target,
      available: instance ? instance[property] === true : false,
      // Reported because the row can be on while the list is empty: Steam's Windows backend
      // never populates wireless.aps, so an access point count of zero here means WSGM has not
      // supplied one, not that the machine cannot see any networks.
      accessPoints: Array.isArray(instance?.accessPoints) ? instance.accessPoints.length : -1,
      hasWirelessDevice: instance?.hasWirelessDevice === true,
      scanWrapped,
      lastError,
    };
  };

  return { install, remove, status };
}

registerGate("network", createNetworkGate());
