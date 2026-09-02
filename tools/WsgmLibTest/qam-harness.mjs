// Injects WSGM's native-QAM bootstrap into the RUNNING Steam client and plays the host's role, so
// CEF-side work can be exercised without building, installing and restarting anything.
//
// Why this exists: every fault in this area so far has lived entirely in injected JavaScript or in
// the config the host hands it — an allowlist entry, a namespace ownership check, a state field
// that was published by nobody. Each one cost a full verify + build + install + restart cycle to
// see, and each would have shown up here in seconds.
//
// It is a DIAGNOSTIC, not a second implementation. The bootstrap source, the asset hash, the
// allowlist and the config shape are all read from the repository rather than restated, so a drift
// between what this exercises and what WSGM ships is not possible in the direction that matters:
// if the harness passes and the product fails, the difference is the host, not the script.
//
// Usage:
//   node qam-harness.mjs status                 what is installed right now
//   node qam-harness.mjs install                inject the bridge and install every namespace
//   node qam-harness.mjs publish <file.json>    publish {patchId: state} to the bridge
//   node qam-harness.mjs remove                 remove the namespaces and dispose the bridge
//   node qam-harness.mjs screenshot [file.png]  capture the visible Big Picture window
//
// It never runs WSGM and never touches configuration. It talks to Steam's debug port only.
import { readFileSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = join(here, "..", "..");
const assetPath = join(
  repositoryRoot,
  "src",
  "WSGM",
  "Core",
  "SteamUiAssets",
  "NativeQamBootstrap.js",
);
const toolkitRoot = join(repositoryRoot, "external", "steam-ui-toolkit", "src", "SteamUiToolkit");
const bridgeIdentityPath = join(toolkitRoot, "SteamUiBridgeIdentity.cs");
const bridgeSourcePath = join(toolkitRoot, "SteamUiBridge.cs");
const sessionHostPath = join(repositoryRoot, "src", "WSGM", "Shell", "SteamUiSessionHost.cs");
const componentPatchesPath = join(
  repositoryRoot,
  "src",
  "WSGM",
  "Core",
  "NativeQamComponentPatches.cs",
);
const gatePatchesPath = join(repositoryRoot, "src", "WSGM", "Core", "SteamGatePatch.cs");
const bluetoothServicePath = join(
  repositoryRoot,
  "src",
  "WSGM",
  "Shell",
  "NativeQamBluetoothService.cs",
);

// These three are the host's, and are read from its source rather than copied. The allowlist in
// particular is what a new control forgets: a patch id missing here makes subscribe() throw
// "subscription not allowlisted" during render, which Steam's error boundary turns into a blank
// tab rather than a missing row.
const readSourceConstant = (path, pattern, what) => {
  const source = readFileSync(path, "utf8");
  const match = source.match(pattern);
  if (!match) throw new Error(`could not read ${what} from ${path}`);
  return match[1];
};

const readAllowlist = () => {
  const host = readFileSync(sessionHostPath, "utf8");
  const componentSource = readFileSync(componentPatchesPath, "utf8");
  const gateSource = readFileSync(gatePatchesPath, "utf8");
  const ids = new Map();
  for (const match of componentSource.matchAll(
    /internal static NativeQamComponentPatch\s+(\w+)\s*\{[^}]*\}\s*=\s*new\(\s*"([^"]+)"/g,
  )) {
    ids.set(`NativeQamComponentPatches.${match[1]}.Id`, match[2]);
  }
  for (const match of gateSource.matchAll(
    /internal static ISteamUiPatch\s+(\w+)\s*\{[^}]*\}\s*=\s*new SteamGatePatch\(\s*id:\s*"([^"]+)"/g,
  )) {
    ids.set(`SteamGatePatches.${match[1]}.Id`, match[2]);
  }
  ids.set(
    "ShellPatchId",
    readSourceConstant(
      sessionHostPath,
      /private const string ShellPatchId = "([^"]+)"/,
      "shell id",
    ),
  );

  const allowed = {};
  for (const match of host.matchAll(
    /new\(\s*((?:NativeQamComponentPatches|SteamGatePatches)\.\w+\.Id|ShellPatchId)\s*,\s*"([^"]+)"/g,
  )) {
    const patchId = ids.get(match[1]);
    if (!patchId) throw new Error(`could not resolve command owner ${match[1]}`);
    (allowed[patchId] ??= []).push(match[2]);
  }

  const bluetoothId = ids.get("SteamGatePatches.Bluetooth.Id");
  const bluetoothSource = readFileSync(bluetoothServicePath, "utf8");
  const bluetoothBlock = bluetoothSource.match(
    /internal static readonly string\[\] Commands\s*=\s*\[([\s\S]*?)\];/,
  );
  if (!bluetoothId || !bluetoothBlock) throw new Error("could not read Bluetooth commands");
  allowed[bluetoothId] = [...bluetoothBlock[1].matchAll(/"([^"]+)"/g)].map((match) => match[1]);

  if (!Object.keys(allowed).length) throw new Error("the allowlist parsed empty");
  return allowed;
};

const asset = readFileSync(assetPath, "utf8");
const configuration = {
  version: Number(
    readSourceConstant(
      bridgeSourcePath,
      /public const int SchemaVersion = (\d+)/,
      "schema version",
    ),
  ),
  namespace: readSourceConstant(
    bridgeIdentityPath,
    /public const string Namespace = "([^"]+)"/,
    "the bridge namespace",
  ),
  binding: readSourceConstant(
    bridgeIdentityPath,
    /public const string BindingName = "([^"]+)"/,
    "the binding name",
  ),
  // The product pins the asset's own hash so a changed script replaces a running bridge. The
  // harness does the same, for the same reason: without it an edit appears to do nothing.
  assetHash: createHash("sha256").update(asset).digest("hex").toUpperCase(),
  contextGeneration: 1,
  documentGeneration: 1,
  maximumPending: 32,
  timeoutMilliseconds: 5000,
  allowed: readAllowlist(),
};
const componentKinds = [
  "autoTdp",
  "frameLimit",
  "controllerTarget",
  "deviceControls",
  "resolution",
  "vrr",
  "valveProfileHeader",
  "valveReset",
  "valveRefreshRate",
  "valveOverlayLevel",
  "valveTdp",
];

const targets = async () => {
  const response = await fetch("http://127.0.0.1:8080/json/list");
  if (!response.ok) throw new Error(`Steam target discovery failed: HTTP ${response.status}`);
  return response.json();
};

const validatedSocket = (target, role) => {
  if (!target) throw new Error(`${role} is not open; is Steam running?`);
  const socket = new URL(target.webSocketDebuggerUrl);
  if (
    !["ws:", "wss:"].includes(socket.protocol) ||
    !["127.0.0.1", "localhost"].includes(socket.hostname) ||
    socket.port !== "8080"
  ) {
    throw new Error(`${role} reported a non-loopback DevTools socket`);
  }
  return socket.href;
};

const sharedTarget = async () => {
  const shared = (await targets()).find(
    (entry) =>
      entry.type === "page" &&
      entry.title === "SharedJSContext" &&
      entry.url.startsWith("https://steamloopback.host/"),
  );
  if (!shared) throw new Error("SharedJSContext is not open; is Steam running?");
  return validatedSocket(shared, "SharedJSContext");
};

const mainWindowTarget = async () => {
  const matches = (await targets()).filter(
    (entry) =>
      entry.type === "page" &&
      entry.url.startsWith("about:blank?") &&
      entry.url.includes("createflags") &&
      entry.url.includes("minwidth") &&
      !entry.url.includes("browserviewpopup") &&
      !entry.url.includes("openerid"),
  );
  if (matches.length !== 1) {
    throw new Error(`expected one MainWindow target, found ${matches.length}`);
  }
  return validatedSocket(matches[0], "MainWindow");
};

class Session {
  #socket;
  #next = 0;
  #pending = new Map();
  #onBinding;

  constructor(socket, onBinding) {
    this.#socket = socket;
    this.#onBinding = onBinding;
    socket.onmessage = (event) => {
      const message = JSON.parse(event.data);
      if (message.id !== undefined) {
        const entry = this.#pending.get(message.id);
        if (entry) {
          this.#pending.delete(message.id);
          if (message.error) entry.reject(new Error(JSON.stringify(message.error)));
          else entry.resolve(message.result);
        }
        return;
      }
      if (message.method === "Runtime.bindingCalled") this.#onBinding(message.params);
    };
  }

  send(method, params = {}) {
    const id = ++this.#next;
    return new Promise((resolve, reject) => {
      this.#pending.set(id, { resolve, reject });
      this.#socket.send(JSON.stringify({ id, method, params }));
    });
  }

  async evaluate(expression) {
    const result = await this.send("Runtime.evaluate", {
      expression,
      returnByValue: true,
      awaitPromise: true,
      // The injected page has a CSP the product's own bridge is exempted from; without this the
      // harness cannot inject the very script it exists to test.
      allowUnsafeEvalBlockedByCSP: true,
      userGesture: true,
    });
    if (result.exceptionDetails) {
      throw new Error(result.exceptionDetails.exception?.description ?? "evaluation threw");
    }
    return result.result?.value;
  }
}

// The host answers the bridge's requests. The harness answers them too, but only enough to prove
// the JS side asked the right thing: it echoes an empty success and prints the call, because what
// is being tested here is the injected half, not WSGM's services.
const respond = async (session, envelope) => {
  console.log(
    `  request  ${envelope.patchId} ${envelope.command}`,
    JSON.stringify(envelope.payload ?? null),
  );
  const response = {
    version: configuration.version,
    type: "response",
    patchId: envelope.patchId,
    command: envelope.command,
    sequence: envelope.sequence,
    contextGeneration: configuration.contextGeneration,
    documentGeneration: configuration.documentGeneration,
    ok: true,
    payload: null,
  };
  const accepted = await session.evaluate(
    `window[${JSON.stringify(configuration.namespace)}].deliver(${JSON.stringify(response)})`,
  );
  if (accepted !== true) throw new Error("bridge rejected the harness response envelope");
};

const connect = async () => {
  const socket = new WebSocket(await sharedTarget());
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = reject;
  });
  let session;
  session = new Session(socket, (params) => {
    if (params.name !== configuration.binding) return;
    try {
      void respond(session, JSON.parse(params.payload)).catch((error) => {
        console.log("  bridge response failed:", String(error));
      });
    } catch (error) {
      console.log("  binding payload was not readable:", String(error));
    }
  });
  await session.send("Runtime.enable");
  await session.send("Runtime.addBinding", { name: configuration.binding });
  return { session, socket };
};

const install = async (session) => {
  const source = asset.replace("__WSGM_CONFIGURATION_JSON__", JSON.stringify(configuration));
  const result = await session.evaluate(source);
  console.log("bootstrap:", result);

  const bridge = `window[${JSON.stringify(configuration.namespace)}]`;
  for (const gate of ["audio", "network", "bluetooth", "brightness", "perf", "steamOsManager"]) {
    const outcome = await session.evaluate(
      `(()=>{const b=${bridge};const g=b&&b.gate?b.gate(${JSON.stringify(gate)}):null;` +
        `if(!g)return 'absent';try{return JSON.stringify(g.install());}catch(e){return String(e);}})()`,
    );
    console.log(`  ${gate.padEnd(11)} ${outcome}`);
  }
  for (const component of componentKinds) {
    const outcome = await session.evaluate(
      `(()=>{const b=${bridge};const g=b&&b.gate?b.gate('nativeComponents'):null;` +
        `if(!g)return 'absent';try{return JSON.stringify(g.install(${JSON.stringify(component)}));}` +
        `catch(e){return String(e);}})()`,
    );
    console.log(`  ${component.padEnd(18)} ${outcome}`);
  }
};

const status = async (session) => {
  const bridge = `window[${JSON.stringify(configuration.namespace)}]`;
  const report = await session.evaluate(
    `(()=>{const b=${bridge};const s=window.SteamClient&&window.SteamClient.System;` +
      `const out={bridge:!!b,version:b&&b.version,` +
      `audioNamespace:!!(s&&s.Audio),audioOwned:!!(s&&s.Audio&&s.Audio.__wsgmOwnedNamespace===true),` +
      `perfNamespace:!!(s&&s.Perf),perfOwned:!!(s&&s.Perf&&s.Perf.__wsgmOwnedNamespace===true)};` +
      `if(b){for(const n of ['audio','network','bluetooth','brightness','perf','steamOsManager']){` +
      `try{const g=b.gate?b.gate(n):null;out[n]=g?g.status():'absent';}catch(e){out[n]='ERR '+e;}}` +
      // nativeComponents.status takes a KIND. Calling it bare reports registered:false for every
      // component, which reads as "nothing registered" and is purely an artefact of the call.
      `try{const c=b.gate?b.gate('nativeComponents'):null;if(!c)throw new Error('gate absent');` +
      `out.components={};for(const k of ${JSON.stringify(componentKinds)}){` +
      `const s=c.status(k);out.components[k]=s.registered;}` +
      `const any=c.status('frameLimit');out.lastAppend=any.lastAppend;` +
      `out.renderOutcomes=any.renderOutcomes;out.rootWrapped=any.performanceRootWrapped;}` +
      `catch(e){out.components='ERR '+e;}}` +
      `return JSON.stringify(out,null,1);})()`,
  );
  console.log(report);
};

const publish = async (session, file) => {
  const states = JSON.parse(readFileSync(file, "utf8"));
  const bridge = `window[${JSON.stringify(configuration.namespace)}]`;
  for (const [patchId, state] of Object.entries(states)) {
    // deliver() takes an OBJECT, and rejects any envelope whose generations do not match the config
    // it was installed with. Passing a JSON string, or omitting either generation, returns a bare
    // false with no reason — which is how this harness first reported "published: false".
    const envelope = {
      version: configuration.version,
      contextGeneration: configuration.contextGeneration,
      documentGeneration: configuration.documentGeneration,
      type: "state",
      patchId,
      payload: state,
    };
    const outcome = await session.evaluate(`${bridge}.deliver(${JSON.stringify(envelope)})`);
    console.log(`  published ${patchId}: ${outcome}`);
  }
};

const remove = async (session) => {
  const bridge = `window[${JSON.stringify(configuration.namespace)}]`;
  for (const gate of ["steamOsManager", "perf", "brightness", "bluetooth", "network", "audio"]) {
    const outcome = await session.evaluate(
      `(()=>{const b=${bridge};const g=b&&b.gate?b.gate(${JSON.stringify(gate)}):null;` +
        `if(!g)return 'absent';try{return JSON.stringify(g.remove());}catch(e){return String(e);}})()`,
    );
    console.log(`  ${gate.padEnd(11)} ${outcome}`);
  }
  await session.evaluate(
    `(()=>{const b=${bridge};if(b&&b.dispose)b.dispose('harness');return true;})()`,
  );
};

const screenshot = async (file) => {
  const socket = new WebSocket(await mainWindowTarget());
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = reject;
  });
  const session = new Session(socket, () => {});
  try {
    await session.send("Page.enable");
    const result = await session.send("Page.captureScreenshot", {
      format: "png",
      fromSurface: true,
    });
    if (typeof result.data !== "string" || result.data.length === 0) {
      throw new Error("Steam returned no screenshot data");
    }
    writeFileSync(file, Buffer.from(result.data, "base64"));
    console.log(`wrote ${file}`);
  } finally {
    socket.close();
  }
};

const [command, argument] = process.argv.slice(2);
if (command === "screenshot") {
  await screenshot(argument || "qam.png");
  process.exit(0);
}
const { session, socket } = await connect();
try {
  if (command === "install") await install(session);
  else if (command === "publish") await publish(session, argument);
  else if (command === "remove") await remove(session);
  else await status(session);

  // Requests arrive asynchronously after a control renders, so hold briefly to print them.
  if (command === "install" || command === "publish") {
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }
} finally {
  socket.close();
}
