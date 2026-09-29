// Probe: dump the QAM Bluetooth row module (66943) whole and follow its imports to the
// real Bluetooth store, then report that store's backend surface. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_bt3_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  out.row = String(runtime.m["66943"]);

  // Any module whose source declares bluetooth operations by name.
  const opRe =
    /(SetBluetoothEnabled|PairDevice|UnpairDevice|ConnectDevice|DisconnectDevice|ForgetDevice|StartScanning|BluetoothEnabled|bluetooth_)/;
  const owners = [];
  for (const id of Object.keys(runtime.m)) {
    let src;
    try {
      src = String(runtime.m[id]);
    } catch {
      continue;
    }
    if (!opRe.test(src)) continue;
    const ops = [
      ...new Set(
        src.match(
          /(SetBluetoothEnabled|PairDevice|UnpairDevice|ConnectDevice|DisconnectDevice|ForgetDevice|StartScanning\w*|bluetooth_\w+)/g,
        ) || [],
      ),
    ];
    owners.push({ id, len: src.length, ops: ops.slice(0, 14) });
  }
  out.owners = owners.sort((a, b) => a.len - b.len).slice(0, 10);
  return JSON.stringify(out);
})();
