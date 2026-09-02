# AGENTS.md

Source of truth for coding agents in this repository.

Every `AGENTS.md` and `CLAUDE.md` is tracked deliberately. Each `CLAUDE.md` must be a symlink
(mode `120000`) to the `AGENTS.md` beside it. If a checkout turned one into a regular file, recreate
the symlink; never put guidance directly in `CLAUDE.md`.

This file contains working rules, not a design manifesto. Put short, mechanism-specific rationale
beside the code it governs. Put device findings, disproven approaches, and explanations spanning
several files in one focused `docs\` topic. When code is collapsed or deleted, move rationale that
still matters into the surviving code or its topic doc and delete commentary about machinery that
no longer exists.

## Product and current runtime

WSGM reconstructs SteamOS Game Mode on Windows 11 handhelds. Explorer remains the registered shell.
At sign-in, the SYSTEM logon service starts WSGM's splash; WSGM lets Explorer complete the
one-per-session initialization that preserves touch features, exits it through Explorer's own
shutdown path, and starts Steam Big Picture with WSGM's overlay. Steam is the only launcher and is
detected from the registry.

The implementation is intentionally direct:

```text
WSGM.exe (CoreCLR)
  ├─ one in-process collectible Device Plugin runtime
  ├─ controller management (VIIPER + HidHide)
  ├─ RTSS performance control + AutoTDP
  ├─ one persistent Steam CEF transport and patch/session host
  ├─ Wi-Fi, Bluetooth, audio and touch-keyboard integration
  └─ overlay, settings and shell/session coordination

Separate artifacts that still have a real boundary:
  external/WSGM.Device.Sdk           public plugin/package contract (submodule)
  external/WSGM.DeviceLab             diagnostic and authoring tool (submodule)
  external/WSGM.Device.Msi.Claw8A2Vm built-in device package source (submodule)
  WSGM.LogonService                  SYSTEM logon/watchdog process
  WSGM.Launch                        per-game medium-integrity wrapper
  native/SteamInput                  native Steam Input lease and proxy (submodule)
  external/windows-device-control    radio/Wi-Fi/audio/brightness library (submodule)
  external/steam-ui-toolkit          Steam CEF transport and patch toolkit (submodule)
  VIIPER                             native virtual-controller backend
```

The main application is not NativeAOT. Managed COM/WinRT, reflection needed for the one package-local
plugin assembly, and ordinary CoreCLR libraries are allowed. Do not reintroduce helper processes,
native shims, IPC protocols, mirror projects, or abstraction layers unless a demonstrated OS,
lifetime, packaging, or public-contract boundary requires them. Prefer extending or merging into an
existing owner over creating another manager, policy, projection, or transport.

Exactly one installed Device Plugin package may exist. Normal startup observes:

- zero packages: WSGM runs with device integration unavailable;
- one package: validate and load it in-process when integration is enabled;
- more than one package: refuse normal startup and list every package root.

Package maintenance serializes the package slot and reserves the same machine-wide owner marker as
the runtime. It never loads device code while replacing it. The plugin owns device-specific
transport, protocol and policy; WSGM owns session/UI policy. A missing or rejected plugin must not
take down the shell.

## Read before changing verified behavior

The current code represents working, device-tested behavior. Simplification is welcome; changing an
outcome without re-verifying it is not. Read the relevant topic before editing:

| Topic | Read before touching |
| --- | --- |
| `docs\boot-and-shell.md` | logon service, boot splash, Explorer, desktop/game transitions, taskbar/tray |
| `docs\steam-input.md` | Steam Input lease, proxy DLL, controller blocking |
| `docs\elevation.md` | elevation, de-elevation, scheduled tasks, `WSGM.Launch` |
| `docs\steam-cef.md` | CEF transport, QAM/Steam UI patches, tabs, glyphs, downloads, live probes |
| `docs\rtss.md` | RTSS discovery/profiles, performance state and AutoTDP |
| `docs\sd-cards.md` | card identity, formatting, libraries and manager |
| `docs\overlay-and-input.md` | overlay, SDL/gamepad ownership, touch and raw input |
| `docs\power-and-display.md` | display/HDR, screen-off mute, keep-awake |
| `docs\radios.md` | Wi-Fi, Bluetooth, audio and touch keyboard — and the library that owns them |
| `docs\ui.md` | themes, controls, Settings and splash presentation |
| `docs\device-integration.md` | one-plugin slot and in-process runtime lifecycle |
| `docs\device-plugin-authoring.md` | plugin implementation, testing, packaging and installation |
| `docs\device-security.md` | concrete runtime/package boundaries that remain |
| `docs\decisions.md` | standing product decisions and accepted posture |
| `_plan\2.0-decisions.md` | authoritative 2.0 product decisions |

Steam CEF, boot/takeover, controller ownership and hardware writes reveal constraints only on live
systems. Preserve their observable behavior and diagnostic coverage while simplifying their shape.

## Maintainer workflow

- Work on the current branch. Do not create a branch unless the maintainer asks.
- Preserve unrelated working-tree edits. In particular, never stage or rewrite a user-owned change
  merely because it shares the tree with this task.
- `_plan\implementation-todo.md` is the only progress tracker. Update it rather than creating a
  second checklist.
- Commit after a completed task and push periodically. Tagging and publishing require an explicit
  instruction.
- Batch local gates. Do not run builds/tests/verification after every edit. Run them when a coherent
  worklist slice is complete, for an installer hand-off, or when asked. CI runs the same verification
  on every push.
- Iteration on the reference device uses `eng\dev-deploy.ps1`; an installer is for milestone
  hand-off. Never run dev deploy without the machine and attendance checks below.
- A milestone hand-off runs `build.ps1`, then copies the exact installer named by the checked-in
  project version to `Z:\` and verifies the copied hash.

Version numbers are maintainer-owned. `src\WSGM\WSGM.csproj` `<Version>` is the single source of
truth for application metadata, the companion executables published by `build.ps1`, Inno Setup's
`AppVersion`, and `publish\WSGM-Setup-<version>.exe`. The 2.0 line currently uses `2.0.0`, producing
`WSGM-Setup-2.0.0.exe`. Change it only when the maintainer explicitly asks to advance the product or
installer version, or to prepare a named release. A version change does not authorize a Git tag or
GitHub release; tagging and publishing still require their own explicit instruction.

## Commands and live-system boundary

```powershell
dotnet build src\WSGM\WSGM.csproj
dotnet test WSGM.slnx
./eng/verify.ps1
./eng/verify.ps1 -Fix
./build.ps1
./eng/dev-deploy.ps1
src\WSGM\bin\...\WSGM.exe --settings
src\WSGM\bin\...\WSGM.exe --overlay-test
node tools\WsgmLibTest\run-file.mjs <file.js>
node tools\WsgmLibTest\qam-harness.mjs

# Device Lab is built directly from its pinned submodule.
dotnet run --project external\WSGM.DeviceLab\src\WSGM.DeviceLab\WSGM.DeviceLab.csproj -- doctor|inventory|candidates
dotnet run --project external\WSGM.DeviceLab\src\WSGM.DeviceLab\WSGM.DeviceLab.csproj -- inspect|compare|correlate <file>
dotnet run --project external\WSGM.DeviceLab\src\WSGM.DeviceLab\WSGM.DeviceLab.csproj -- validate <plugin>|pack <plugin>
```

The configured Steam CEF MCP attaches to the live client's existing CDP endpoint. Target by role:
`SharedJSContext` owns stores, webpack modules and WSGM's bridge/registry; `Big-Picture-Modus` owns
visible DOM and screenshots. Listing targets and bounded read-only evaluation are observation.
Navigation, focus, clicks, or capture need the maintainer to direct that live interaction;
`close_page` closes Steam's real window and is never a cleanup command. The MCP does not relax the
literal-module probe rule below: never sweep the registry, invoke unknown exports, or instantiate
modules while exploring.

`--settings` and `--overlay-test` are safe local UI modes. Read-only Device Lab observation and
offline validation commands are safe. Change Device Lab inside `external\WSGM.DeviceLab`, commit
and push there, then commit the moved submodule pin in WSGM.

`--shell` may run only through `eng\dev-deploy.ps1`, only after
`Get-CimInstance Win32_BaseBoard` reports product `MS-1T52`, only when the maintainer explicitly
directs the loop, and only while they are present. It exits Explorer and owns the session.

Never run without an explicit maintainer instruction:

- `--boot`;
- `--install-device-plugin` or other plugin activation/replacement;
- `WSGM.LogonService.exe --install`;
- `wsgm-device test hardware`;
- live capture or any attended hardware-mutation path.

Do not dismiss work as “needs hardware” before checking the current machine. The development machine
may be the reference MSI Claw (`MS-1T52`), where enumeration and read-only observation are available.
Being on the Claw does not authorize shell takeover, writes, or plugin activation.

## Native and packaged dependencies

**The first-party dependencies below are submodules, not vendored directories.** `WSGM.slnx` and
the PowerShell build/staging scripts consume those checked-out projects directly at their pinned
Git links. Do not reintroduce release downloads, extracted source caches, copied project mirrors, or
separate version/digest lock files for them. They are separate `KillerPixelCrew` repositories
because they are useful on their own and can be developed directly from this recursive checkout:

| Path | Repository | Empty checkout looks like |
| --- | --- | --- |
| `native\SteamInput` | `steam-input-lease` | a missing Rust toolchain |
| `external\windows-device-control` | `windows-device-control` | an unresolvable csproj path |
| `external\WSGM.Device.Sdk` | `WSGM.Device.Sdk` | an unresolvable csproj path |
| `external\steam-ui-toolkit` | `steam-ui-toolkit` | a csproj path AND a Steam asset that will not compile |
| `external\WSGM.DeviceLab` | `WSGM.DeviceLab` | an unresolvable tool project path |
| `external\WSGM.Device.Msi.Claw8A2Vm` | `WSGM.Device.Msi.Claw8A2Vm` | a missing built-in package project |

**Clone this repository with `--recursive`**, or run `git submodule update --init --recursive` after
cloning. A dependency change is committed and pushed from inside that submodule first; then commit
the updated Git link here. The Git link is the dependency lock.

`external\windows-device-control` is an ordinary project reference and holds every Windows call
behind Wi-Fi, Bluetooth, pairing, Core Audio endpoints, panel brightness and the volume cue. WSGM
owns policy and wording on top of it; see `docs\radios.md` for that split. Its public surface is
documented for IntelliSense and its build fails on an undocumented member, so a change there costs
a documentation pass — that is deliberate.

`external\steam-ui-toolkit` is the CDP transport, the probe/apply/verify/remove patch lifecycle,
the bridge, the module contract and the extension host. **WSGM keeps every surface** — the gates,
the QAM rows, the components, and the policy about which patches are applied when. The test for
where something belongs: does it name a Steam module id, a localization token, or a WSGM service?
Then it is WSGM's. Does it describe how to find and own such a thing safely? Then it is the
toolkit's.

The injected asset spans both and is still ONE script, because it is evaluated in one CDP call:
`eng\build-steam-assets.mjs` takes the prelude from the submodule and WSGM's fragments from
`Core\SteamUiAssets\Source`, and compiles them together. A gate is a new file in `gates\` and
nothing else — the builder holds no list. WSGM supplies the three things the toolkit refuses to
assume: where to log, what script to inject, and where Steam is installed.

`external\WSGM.Device.Sdk` is the plugin contract, and it is **MIT while WSGM is GPL-3.0-or-later**.
That is deliberate: the assembly is linked into every plugin, so the application's copyleft there
would make every plugin GPL-3, including a vendor or OEM one. Do not "fix" the licence mismatch.
It stays a zero-dependency leaf — anything it references, every plugin inherits and cannot give
back — and its build fails on an undocumented public member. Both properties are guarded by tests
in that repository and re-checked here against the pin.

WSGM is no longer its only consumer, so **the ABI is a public compatibility promise, not an
internal handshake.** Do not change the C ABI, `include\steam_input_lease.h`, or
`bindings\SteamInterop.Net` from this repository — change them in the library's own repository,
bump `sil_abi_version()`, then move this submodule's pin. WSGM and Launch link the same managed
source from the submodule; do not create a second binding mirror.

`eng\build-steam-input-lease.ps1` builds and stages the two shipped DLLs and licenses.
`steam-input-lease.exe` is diagnostic only and is not installed. The generated staging directory is
gitignored and must never be hand-populated. Rust validation runs through the verification gate; do
not reformat untouched Rust simply to add a fmt gate.

VIIPER and its controller drivers are pinned under `third_party\controller`. Build scripts acquire
and validate the pinned source; the installer scopes VIIPER, usbip-win2 and HidHide to the controller
component. Do not silently substitute a newer driver: the pin records a device-specific regression.

Device Lab and the built-in Claw package are MIT submodules and ordinary projects in `WSGM.slnx`.
That is a build/test relationship only: `WSGM.csproj` still references just the SDK and discovers the
installed package dynamically. `eng\stage-device-components.ps1` publishes Device Lab and invokes
the package repository's own packer from those pinned sources; no release download or digest lock is
involved. The package's staged glyph count is compared with its source tree so losing artwork cannot
silently pass validation. No generated binary is checked in.

The retired `native\Radio`, `native\VolumeControl`, DeviceHost, IPC transport and managed binding
mirror must stay retired. Upgrade cleanup may still name old artifacts solely to remove them.

## Implementation and documentation style

- Use the simplest direct implementation that preserves behavior. Delete zero-consumer seams and
  test doubles from production. Collapse duplicate polling, serialization, policy and state paths.
- A type/project/process must justify its existence with a current consumer or a real boundary.
  “Future flexibility”, theoretical isolation and architecture diagrams are not justification.
- Keep OS declarations and handle/marshalling details at the existing interop edge, but do not build
  facade layers that only rename one call.
- Prefer immutable records/value types for data and sealed classes for stateful owners. Represent
  finite states explicitly rather than with unrelated booleans.
- Avalonia controls and observable UI state are UI-thread owned. Perform blocking process, file,
  Steam, display and device calls off-thread, then marshal results back.
- `async void` is for framework event handlers only. Do not use `.Wait()` or `.Result()` in
  asynchronous library code. Long-lived callbacks, timers, watchers and handles need a rooted owner
  and an explicit dispose/unsubscribe path.
- Catch only when the caller receives a usable fallback or recovery continues. Log normal feature
  failures with the operation and decisive values.
- Every branch ending in “nothing happened” logs why. Poll loops use `Log.Change(key, message)` so a
  transition is visible without repeating unchanged state thousands of times.
- Plugins log through `PluginTrace.Info/Warn/Error/Failure`. Never log per 125 Hz sample.
- Public production APIs need meaningful XML documentation. Documentation explains contract,
  lifetime, ownership, non-obvious side effects and failure behavior; it does not restate a member's
  name. Test names are the executable specification and do not need XML docs.
- Comments explain why a surviving constraint exists. Delete chronology, review conversation,
  scaffolding narration and claims about removed architecture. When a rationale spans files or
  records device evidence, move it to the focused doc and leave a short pointer at the mechanism.
- Follow `.editorconfig`: UTF-8, CRLF, final newline, trimmed whitespace; four spaces for C# and
  PowerShell-style code, two for AXAML/XML/project/JSON/workflow files. Let `eng\verify.ps1 -Fix`
  apply repository formatting at a milestone.

## Operational invariants

- `Program.Main` handles recovery and fixed command modes before logging/Avalonia. `--restore-shell`
  must remain usable even when configuration, graphics or logging initialization is broken.
- A normal shell session creates one long-lived session root and one instance of each service. Do
  not duplicate transition policy in overlay or view models.
- Configuration reload replaces `AppConfig`; services retain their runtime state and receive the new
  values through their existing apply path.
- Device integration off means no plugin lifecycle, controller target, hardware write or AutoTDP
  activity. Enabling starts one fresh cycle; suspend/resume and faults preserve make-safe ordering.
- Capability writes validate the live cycle/identity and return uncertainty honestly. Never retry an
  uncertain hardware write automatically.
- CEF patches fail open to Valve behavior. Every injected replacement has an ownership marker,
  retains the original value, accepts “already ours”, and removes only its own work. A successful
  patch must not invalidate its next probe.
- Live CEF probes name literal module IDs and inspect factory/prototype source. Never iterate the
  webpack module registry, call every module, or instantiate exports speculatively; that has restarted
  the machine and signed Steam out before.
- The restored Explorer must be initialized, medium-integrity, current-session and jobless. Scheduled
  task launch is degraded recovery, not the normal handoff.
- Remote diagnosis depends on `%LOCALAPPDATA%\WSGM\wsgm.log`. Preserve established controller,
  Steam Input, Explorer and transition diagnostics and extend them at new no-op decisions.

## Tests and delivery gates

No test, benchmark or throwaway probe may touch `%LOCALAPPDATA%\WSGM`. Use temporary directories and
the existing injected seams. Never call `ConfigStore.Save/Load` or the parameterless
`SettingsViewModel` constructor from a test. `Log` stays uninitialized in tests.

Unattended tests must not run shell takeover, Steam navigation, hardware mutation, display changes,
lock-screen flows or plugin activation. Test their pure state, serialization, ordering and failure
decisions; retain the attended verification boundary for the live outcome.

At a major milestone:

1. Run `./eng/verify.ps1 -Fix`.
2. Run `./build.ps1`.
3. Resolve the checked-in version, require that exact installer, copy it to `Z:\`, and compare the
   source and destination hashes:

```powershell
$project = Get-Content -LiteralPath .\src\WSGM\WSGM.csproj -Raw
if ($project -notmatch '<Version>([^<]+)</Version>') {
    throw 'WSGM.csproj has no Version.'
}
$version = $Matches[1]
$setupPath = ".\publish\WSGM-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Expected installer was not produced: $setupPath"
}
$setup = Get-Item -LiteralPath $setupPath
$destination = Join-Path 'Z:\' $setup.Name
$sourceHash = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash
Copy-Item -LiteralPath $setup.FullName -Destination $destination -Force
$destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash) {
    throw "Copied installer hash mismatch: $destination"
}
```

Report automated/build evidence separately from live or attended acceptance that has not run.

## Requested reviews

Only enter review mode when the user asks for a review/audit. Review the specified changeset, not the
entire long-running branch unless requested. Read every changed hunk, trace changed behavior through
entry point, state, side effect, failure and cleanup, and follow cross-boundary contracts including
native ABI, config/boot service, shell/Steam, installer/recovery and themes/consumers. Findings must
be demonstrable defects with severity and a concrete trigger; do not report architecture preference
or security theater as correctness findings.
