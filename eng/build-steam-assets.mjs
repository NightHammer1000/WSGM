// Builds the injected Steam UI asset from its ordered TypeScript source fragments.
//
// The shipped asset is reviewable JavaScript, not a bundle: a maintainer reads it
// beside the page it is injected into, and the drift gate hashes it. So this
// compiles with type-stripping only — no bundling, no minification, no helpers —
// and formats the result with the repository's pinned Prettier so the output is
// byte-stable across machines.
//
//   node eng/build-steam-assets.mjs          regenerate the asset and its hash
//   node eng/build-steam-assets.mjs --check  fail if either is out of date
//
// The --check mode is what CI runs. It rebuilds into memory and compares, so a
// source edit that was never compiled cannot ship, and neither can a hand edit of
// the generated file.

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..");
const assetDirectory = join(repositoryRoot, "src", "WSGM", "Core", "SteamUiAssets");
const sourceDirectory = join(assetDirectory, "Source");

// The prelude comes from the toolkit submodule, WSGM's own fragments from here. One script either
// way: the whole thing is evaluated in a single CDP call, so it is compiled as one unit rather
// than shipped as separate assets.
const toolkitSourceDirectory = join(
  repositoryRoot,
  "external",
  "steam-ui-toolkit",
  "src",
  "SteamUiToolkit",
  "SteamUiAssets",
  "Source",
);

// The toolkit's fragments, in the only order that matters:
//
//   types.ts    declarations only. It sits above the bundle marker and is stripped from the
//               emitted asset entirely, so it exists to type the script and ship nothing.
//   bridge.ts   opens the IIFE and carries the orchestration — the reuse check, the request and
//               subscribe machinery, the gate registry, and the publication of the window property.
//
// EVERYTHING ELSE IS DISCOVERED, and its order does not matter. Gates are `function create…()`
// declarations, which hoist, so bridge.ts can call them before they appear textually; the shared
// helpers are consts referenced only from inside those functions, which run long after the whole
// bundle has been evaluated. That is why adding a gate is a new file and nothing else — this
// script holds no list of what exists.
const preludePaths = [
  join(toolkitSourceDirectory, "types.ts"),
  join(toolkitSourceDirectory, "bridge.ts"),
  join(toolkitSourceDirectory, "ownership.ts"),
  join(toolkitSourceDirectory, "rpc.ts"),
];

// The toolkit's closing fragment, and the one file whose position IS load-bearing: it returns the
// bridge's install result, so it has to follow every gate's top-level registration. Emitted last
// for that reason alone — see the file itself.
const epiloguePath = join(toolkitSourceDirectory, "epilogue.ts");

// components.ts is emitted last by convention rather than by necessity. Nothing depends on it
// being there — it is the UI layer, and reading the asset top-down as helpers, then gates, then
// the components they render is worth keeping.
const componentsPath = join(sourceDirectory, "components.ts");

// Order comes from the directory a fragment lives in, not from a list here: shared helpers beside
// the bridge, then gates, then components. Within each group, sorted, so the emitted asset is
// byte-stable no matter what order the filesystem reports.
async function discoverIn(root) {
  const entries = await readdir(root, { withFileTypes: true });
  return entries
    .filter((entry) => entry.isFile() && entry.name.endsWith(".ts"))
    .map((entry) => join(root, entry.name))
    .filter(
      (path) => !preludePaths.includes(path) && path !== componentsPath && path !== epiloguePath,
    )
    .sort();
}

const sourcePaths = [
  ...preludePaths,
  ...(await discoverIn(sourceDirectory)),
  ...(await discoverIn(join(sourceDirectory, "gates"))),
  componentsPath,
  epiloguePath,
];

// The builder closes the IIFE that bridge.ts opens. It used to be the last line of components.ts,
// which made that one fragment silently position-critical: moving it, or adding a fragment after
// it, emitted an asset that could not parse.
const bundleEpilogue = "})();\n";
const outputPath = join(assetDirectory, "NativeQamBootstrap.js");
const catalogPath = join(repositoryRoot, "src", "WSGM", "Core", "SteamUiAssetCatalog.cs");
const maximumAssetBytes = 256 * 1024;

// Everything above this marker is type declaration that exists only to type the
// injected script. The asset starts at the IIFE.
const bundleMarker = "// @wsgm-bundle-start";

const check = process.argv.includes("--check");

function run(command, args, options = {}) {
  // No shell: every invocation here is `node` with an explicit script path, and a
  // shell would only add quoting hazards on paths that already contain spaces.
  const result = spawnSync(command, args, {
    cwd: repositoryRoot,
    encoding: "utf8",
    ...options,
  });
  if (result.status !== 0) {
    const detail = `${result.stdout ?? ""}${result.stderr ?? ""}`.trim();
    throw new Error(`${command} ${args.join(" ")} failed:\n${detail}`);
  }
  return result.stdout ?? "";
}

const temporaryRoot = await mkdtemp(join(tmpdir(), "wsgm-steam-assets-"));
let compiled;
try {
  const inputDirectory = join(temporaryRoot, "input");
  const outputDirectory = join(temporaryRoot, "output");
  await mkdir(inputDirectory);
  await mkdir(outputDirectory);
  const combinedSourcePath = join(inputDirectory, "NativeQamBootstrap.ts");
  const source =
    (await Promise.all(sourcePaths.map((path) => readFile(path, "utf8")))).join("") +
    bundleEpilogue;
  await writeFile(combinedSourcePath, source, "utf8");
  const temporaryProject = join(temporaryRoot, "tsconfig.json");
  await writeFile(
    temporaryProject,
    JSON.stringify({
      extends: join(sourceDirectory, "tsconfig.json"),
      compilerOptions: { outDir: outputDirectory, rootDir: inputDirectory },
      files: [combinedSourcePath],
    }),
    "utf8",
  );
  run("node", [
    join(repositoryRoot, "node_modules", "typescript", "lib", "tsc.js"),
    "--project",
    temporaryProject,
  ]);
  compiled = await readFile(join(outputDirectory, "NativeQamBootstrap.js"), "utf8");
} finally {
  await rm(temporaryRoot, { recursive: true, force: true });
}

const markerIndex = compiled.indexOf(bundleMarker);
if (markerIndex < 0) {
  throw new Error(
    `${relative(repositoryRoot, sourcePaths[1])} must contain "${bundleMarker}" so the emitted asset has an exact start.`,
  );
}

// Format through the same Prettier the repository formats everything else with,
// so the generated file is stable no matter which machine emitted it and never
// fails the repository's own format check.
const unformattedPath = join(assetDirectory, "NativeQamBootstrap.generated.js");
await writeFile(
  unformattedPath,
  compiled.slice(markerIndex + bundleMarker.length).trimStart(),
  "utf8",
);
let formatted;
try {
  formatted = run("node", [
    join(repositoryRoot, "node_modules", "prettier", "bin", "prettier.cjs"),
    "--parser",
    "babel",
    unformattedPath,
  ]);
} finally {
  await rm(unformattedPath, { force: true });
}

const sha256 = createHash("sha256").update(formatted, "utf8").digest("hex").toUpperCase();
const catalog = await readFile(catalogPath, "utf8");
const hashPattern = /(NativeQamBootstrapSha256\s*=\s*\r?\n\s*")([0-9A-F]{64})(";)/u;
if (!hashPattern.test(catalog)) {
  throw new Error(
    `Could not find NativeQamBootstrapSha256 in ${relative(repositoryRoot, catalogPath)}.`,
  );
}

const currentAsset = await readFile(outputPath, "utf8").catch(() => null);
const currentHash = catalog.match(hashPattern)[2];

if (check) {
  const problems = [];
  if (currentAsset !== formatted) {
    problems.push(`${relative(repositoryRoot, outputPath)} does not match its TypeScript source.`);
  }
  if (currentHash !== sha256) {
    problems.push(
      `NativeQamBootstrapSha256 is ${currentHash}, but the built asset hashes to ${sha256}.`,
    );
  }

  // The shipped set is one reviewed file. Anything else under the asset directory is either a
  // stray build output or a new asset nobody decided to embed, and both must stop the gate.
  const shipped = (await readdir(assetDirectory, { withFileTypes: true }))
    .filter((entry) => entry.isFile() && entry.name.endsWith(".js"))
    .map((entry) => entry.name)
    .sort((left, right) => left.localeCompare(right, "en", { sensitivity: "variant" }));
  if (shipped.length !== 1 || shipped[0] !== "NativeQamBootstrap.js") {
    problems.push(
      `Steam UI assets must stay an explicit, reviewed set; found: ${shipped.join(", ") || "none"}.`,
    );
  }

  // The asset is embedded and evaluated in one CDP call, so its bytes are the contract: bounded,
  // UTF-8, and without a byte-order mark that would land inside the evaluated expression.
  const bytes = await readFile(outputPath).catch(() => null);
  if (bytes === null || bytes.length === 0 || bytes.length > maximumAssetBytes) {
    problems.push(
      `${relative(repositoryRoot, outputPath)} must be between 1 and ${maximumAssetBytes} bytes.`,
    );
  } else if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    problems.push(
      `${relative(repositoryRoot, outputPath)} must be UTF-8 without a byte-order mark.`,
    );
  } else {
    new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  }

  if (problems.length > 0) {
    throw new Error(`${problems.join("\n")}\nRun: npm run steam-assets:build`);
  }
  console.log(`Steam UI asset is current: SHA-256 ${sha256}`);
} else {
  await writeFile(outputPath, formatted, "utf8");
  await writeFile(catalogPath, catalog.replace(hashPattern, `$1${sha256}$3`), "utf8");
  console.log(`Steam UI asset built from TypeScript: SHA-256 ${sha256}`);
}
