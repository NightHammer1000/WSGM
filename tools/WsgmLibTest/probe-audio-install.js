// Probe: does the audio namespace the bootstrap defines actually satisfy Steam's own audio
// store? Installs a stand-in with the same shape, checks the store's availability flag and
// the device projection, then removes it and confirms the client is left as found.
(() => {
  const out = {};
  const system = window.SteamClient?.System;
  if (!system) return JSON.stringify({ error: "no SteamClient.System" });

  out.audioAbsentBefore = system.Audio === undefined;
  if (!out.audioAbsentBefore) return JSON.stringify({ ...out, skipped: "Audio already exists" });

  const toDevice = (entry) => ({
    id: entry.id,
    sName: entry.name,
    bHasOutput: entry.hasOutput === true,
    bHasInput: entry.hasInput === true,
    currentConfig: {},
    availableConfigs: [],
    eConnectorType: 0,
    eBus: 0,
    bSupportsHdmiCec: false,
    bHdmiCecEnabled: false,
    bHdmiCecActive: false,
  });
  const fake = [
    { id: 1, name: "Speakers", hasOutput: true, hasInput: false },
    { id: 2, name: "Headset", hasOutput: true, hasInput: true },
  ];
  const register = () => (cb) => ({ unregister: () => {} });

  Object.defineProperty(system, "Audio", {
    value: {
      GetDevices: () =>
        Promise.resolve({
          activeOutputDeviceId: 1,
          activeInputDeviceId: 2,
          overrideOutputDeviceId: "",
          overrideInputDeviceId: "",
          vecDevices: fake.map(toDevice),
        }),
      GetApps: () => Promise.resolve({ rgApps: [] }),
      SetDefaultDeviceOverride: () => Promise.resolve(),
      SetDeviceVolume: () => Promise.resolve(),
      SetAppVolume: () => Promise.resolve(),
      ClearDefaultDeviceOverride: () => Promise.resolve(),
      RegisterForServiceConnectionStateChanges: register(),
      RegisterForDeviceAdded: register(),
      RegisterForDeviceRemoved: register(),
      RegisterForDeviceVolumeChanged: register(),
      RegisterForVolumeButtonPressed: register(),
      RegisterForAppAdded: register(),
      RegisterForAppRemoved: register(),
      RegisterForAppVolumeChanged: register(),
    },
    configurable: true,
    enumerable: true,
    writable: false,
  });

  out.audioPresentAfter = system.Audio !== undefined;
  out.availabilityFlagWouldBeTrue = null != system.Audio;

  // Build a fresh store instance so its constructor runs against the namespace, exactly as it
  // would at client start with the bootstrap installed.
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_audioinstall_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);

  return new Promise((resolve) => {
    try {
      const mod = runtime("1409");
      const ctor = Object.values(mod).find(
        (v) => typeof v === "function" && /m_bAvailable/.test(String(v)),
      );
      out.storeConstructorFound = !!ctor;
      if (ctor) {
        const store = new ctor();
        out.storeReportsAvailable = store.m_bAvailable === true;
        setTimeout(() => {
          try {
            out.deviceCount = store.m_mapAudioDevices?.size ?? "n/a";
            out.activeOutput = store.m_activeOutputDeviceId;
            out.activeInput = store.m_activeInputDeviceId;
          } catch (e) {
            out.readError = String(e).slice(0, 200);
          }
          finish(resolve);
        }, 400);
        return;
      }
    } catch (e) {
      out.error = String(e).slice(0, 300);
    }
    finish(resolve);
  });

  function finish(resolve) {
    try {
      delete system.Audio;
    } catch (e) {
      out.removeError = String(e).slice(0, 200);
    }
    out.audioAbsentAfterRemoval = system.Audio === undefined;
    resolve(JSON.stringify(out));
  }
})();
