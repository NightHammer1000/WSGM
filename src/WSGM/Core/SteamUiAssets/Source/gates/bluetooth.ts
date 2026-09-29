// Bluetooth is a WebUI transport service whose backend does not exist on Windows. The service,
// its message shapes and every operation are present — GetState round-trips and answers
// is_service_available:false with empty adapters and devices — so WSGM replaces the stub's
// methods rather than implementing the service. `*Handler` exports are message descriptors,
// not registration hooks, so implementing it is not on offer.
//
// The second gate matters here as much as the first: availability is read through react-query
// with staleTime Infinity, so replacing the methods changes nothing until that cache is
// invalidated. Live-verified 2026-08-30 that RF's methods are writable and configurable and that
// the query client's invalidateQueries is reachable.
function createBluetoothService() {
  const patchId = "wsgm.steam-bluetooth.service";
  const queryKey = ["BluetoothManagerService", "State"];
  const methodMarker = "__wsgmOwnedBluetoothService";
  const originalMethodField = "__wsgmOriginalBluetoothServiceMethod";
  const originals = new Map<string, unknown>();
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;
  // Steam's own device and adapter shapes, which are not ours to describe: the store reads them
  // and WSGM only carries them through from the state it was given.
  let latest: {
    is_service_available: boolean;
    adapters: any[];
    devices: any[];
  } = { is_service_available: false, adapters: [], devices: [] };

  const modules = () => getWebpackRuntime("bluetooth-service");

  const reply = transportReply;
  const invalidate = (req) => invalidateQuery(req, queryKey);

  // WSGM sends its own field names and the mapping into Steam's lives here, so the client's
  // schema stays in the half that has to change when the client is rebuilt.
  const onState = (state) => {
    if (!installed || !state) return;
    const devices = Array.isArray(state.devices) ? state.devices : [];
    latest = {
      is_service_available: state.available === true,
      // One synthetic adapter, because the panel needs something to hang the radio toggle on and
      // Windows exposes no adapter identity WSGM could pass through truthfully.
      adapters:
        state.available === true
          ? [
              {
                id: 1,
                mac: "",
                name: "Bluetooth",
                is_enabled: state.enabled === true,
                is_discovering: state.discovering === true,
              },
            ]
          : [],
      devices: devices.map((device) => ({
        id: device.id,
        mac: device.mac ?? "",
        name: device.name ?? device.id,
        etype: device.eType ?? 0,
        is_paired: device.isPaired === true,
        is_connected: device.isConnected === true,
        // Steam sorts by signal and shows a battery when one is reported. WSGM knows neither, and
        // a fabricated strength would order the list by a number that means nothing.
        strength_raw: 0,
        battery_percent: null,
        should_hide_hint: false,
      })),
    };
    invalidate(modules());
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    const req = modules();
    const RF = req?.("60517")?.RF;
    if (!RF || typeof RF.GetState !== "function") {
      lastError = "BluetoothManagerService stub unavailable";
      return { ok: false, error: lastError };
    }

    const forward = (command) => (payload) =>
      request(patchId, command, payload ?? null).then(
        () => reply({ success: true }),
        () => reply({ success: false }),
      );
    const replace = (name, replacement) => {
      const current = RF[name];
      const original = current?.[methodMarker] === true ? current[originalMethodField] : current;
      originals.set(name, original);
      Object.defineProperty(replacement, methodMarker, {
        value: true,
        configurable: true,
        enumerable: false,
      });
      Object.defineProperty(replacement, originalMethodField, {
        value: original,
        configurable: true,
        enumerable: false,
      });
      RF[name] = replacement;
    };
    const restore = () => {
      for (const [name, original] of originals) {
        if (RF[name]?.[methodMarker] === true) RF[name] = original;
      }
    };

    try {
      replace("GetState", () => Promise.resolve(reply(latest)));
      replace("GetDeviceDetails", (payload) => {
        const id = payload?.id;
        const device = latest.devices.find((entry) => entry.id === id) ?? null;
        return Promise.resolve(reply({ device }));
      });
      replace("GetAdapterDetails", () =>
        Promise.resolve(reply({ adapter: latest.adapters[0] ?? null })),
      );
      replace("SetDiscovering", forward("setDiscovering"));
      replace("Pair", forward("pair"));
      replace("CancelPair", forward("cancelPair"));
      replace("Connect", forward("connect"));
      replace("Disconnect", forward("disconnect"));
      replace("Forget", forward("forget"));
      replace("SetTrusted", forward("setTrusted"));
      replace("SetWakeAllowed", forward("setWakeAllowed"));
    } catch (error) {
      lastError = String(error);
      restore();
      originals.clear();
      return { ok: false, error: lastError };
    }

    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, onState);
    invalidate(req);
    return { ok: true, installed: true, replaced: originals.size };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }

    const req = modules();
    const RF = req?.("60517")?.RF;
    if (RF) {
      for (const [name, original] of originals) {
        if (RF[name]?.[methodMarker] === true) RF[name] = original;
      }
    }

    originals.clear();
    latest = { is_service_available: false, adapters: [], devices: [] };
    invalidate(req);
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    replaced: originals.size,
    available: latest.is_service_available,
    devices: latest.devices.length,
    lastError,
  });

  return { install, remove, status };
}

registerGate("bluetooth", createBluetoothService());
