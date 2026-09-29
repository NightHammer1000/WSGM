# Device plugin authoring

WSGM loads one administrator-installed device plugin through the public `WSGM.Device.Sdk` assembly.
A plugin owns exact device detection, hardware transports, semantic capabilities, input and output,
diagnostics, and restoration. It supplies no UI code and cannot use WSGM internals.

Both tools an author needs are MIT submodules: `external\WSGM.Device.Sdk` is the contract and
`external\WSGM.DeviceLab` is the tool. Their Git links pin exact source commits while still allowing
changes to be committed directly in either repository. Build Device Lab from its project in this
checkout; `wsgm-device` below means that build or an installed Device Lab.

## 1. Create and implement

Use Device Lab's Plugin Developer flow, or scaffold from a confirmed capture:

```powershell
wsgm-device scaffold --from <capture.wsgmcap> --out-dir <new-plugin-directory>
```

The generated project contains a minimal `IDevicePlugin`, a six-field `plugin.wsgm.json`, an
explicit x64 target, an MIT `LICENSE.txt` the author is expected to put their own name in, and its
package layout. A scaffolded plugin links only the MIT SDK, never WSGM, so the author picks its
licence freely — including a closed-source vendor plugin. The generated project keeps `LICENSE.txt`
beside both build and publish output. Inside a WSGM checkout it references the SDK project through
`external\WSGM.Device.Sdk`; installed Device Lab instead writes an explicit reference to the exact
`WSGM.Device.Sdk.dll` shipped beside the tool. That path is validated before any scaffold file is
written, so no undefined MSBuild property is emitted. Keep the reference on that exact API if the
scaffold is moved to another machine. Implement exact detection first, then add direct device-owned
services. Publish only semantic descriptors, state, input, and diagnostics through
`IPluginHostAdapter`; vendor addresses, packets, handles, and recovery state stay inside the plugin.

A plugin owns its Device-tab layout by declaring overlay sections inside every
`CapabilityDescriptorSet` (API version 2): up to 16 `CapabilitySection` entries with bounded
categories, each titled by a `SettingSectionKey` or bounded custom text and iconed from the closed
`SectionIcon` vocabulary, and `SectionId`/`CategoryId`/`SortOrder` on each descriptor placing it.
Any role may be placed in a declared section — the layout ships atomically with the capabilities it
lays out — while an unplaced capability keeps the semantic home WSGM derives from its role, and a
semantic role naming an undeclared section rejects the whole set. Layout is grouping only: WSGM
still owns every title string, icon geometry, and control shape it renders.

Every hardware write must recheck current identity and bounds, serialize its real transport, read
back when the hardware supports it, and restore the captured original state on failure or stop.
Unknown identity or ranges fail closed. A partial device is valid: publish the working capabilities
and a specific unavailable reason for the others.

## 2. Build and run safely

Build the plugin for 64-bit Windows and place the entry assembly plus package-local dependencies
beside the manifest:

```powershell
dotnet build <plugin.csproj> -c Release -r win-x64
wsgm-device validate <package-directory>
wsgm-device test sample
wsgm-device test plugin <package-directory> --from <inventory.json>
```

`validate` is offline and does not load plugin code. It rejects a missing, malformed, or non-x64
entry assembly and enforces the same entry, file, per-file, and aggregate-byte package budgets used
by protected staging. `test plugin` loads the package and runs exact detection only. Use a temporary
state directory for the attended hardware path:

```powershell
wsgm-device test hardware <package-directory> --from <inventory.json> --state-dir <new-directory> --action haptic
```

Use `--action controller` for the bounded controller-management check. A semantic capability write
uses `--action capability --capability <id> --value <semantic-value>` plus optional
`--instance <id>`. Each run accepts exactly one explicit action.

`--action haptic-sweep` is the interactive motor calibration that measures the two
`HapticCapabilities` values a plugin must declare from its motor technology:
`MinimumStartIntensity` (the weakest bounded haptic event the motors render) and `MinimumPulse`
(the shortest). The device's own controls pace it — A steps each descending sweep, B marks the
perception boundary — through three phases: continuous strength (informational; the host never
floors continuous rumble), 30 ms ticks (the start intensity), and full-strength pulses of
shrinking length (the minimum pulse). The report prints the values to declare verbatim. A voice
coil or LRA that renders everything keeps the zero defaults; the Claw's ERM motors measured
0.22 / 10 ms this way (2026-09-02).

The hardware command refuses redirected input or output, CI, `--yes`, a nonmatching device, an
active WSGM Device Integration owner, a process without elevation, or a reused state directory. It
requires a local confirmation immediately before activation. The state-path, owner, elevation,
attendance, CI, and confirmation checks complete before Device Lab loads the plugin assembly or runs
its constructor. Exact detection runs only after those checks and must match before activation.
Device Lab gives startup and cleanup 15-second cancellation budgets; the plugin must honor
cancellation so the in-process developer run can return. Never automate this command.

## 3. Test and diagnose

Use `WSGM.Device.Sdk.Testing.TestPluginHostAdapter` for deterministic lifecycle,
partial-availability, publication, cancellation, and cleanup tests without touching hardware. Keep
transport parsing and decision logic behind fakes; reserve real WMI/HID/controller checks for the
attended Device Lab path. The supporting read-only commands require their explicit inputs:

```powershell
wsgm-device doctor --out-dir <diagnostics-directory>
wsgm-device inventory --out-dir <inventory-directory> --shareable
wsgm-device inspect <capture.wsgmcap>
wsgm-device compare <first.wsgmcap> <second.wsgmcap>
wsgm-device correlate <capture.wsgmcap> --action <id> --sources <id,id>
```

They collect or inspect machine and capture evidence without granting mutation authority.

A plugin should leave enough bounded diagnostics to explain detection, service availability,
readback, restoration, and dependency failures. Do not log personal identifiers, raw secrets, or
unbounded device payloads.

## 4. Pack

Create the deterministic distribution archive only after offline validation passes:

```powershell
wsgm-device pack <package-directory> --out <plugin.wsgmpkg>
```

The archive contains only the validated package files in deterministic path and timestamp order.
Device Lab pins the source tree and regular-file handles before validation, then writes the archive
from those same handles so a link or file replacement cannot substitute different bytes after a
clean report. License and attribution notices required by shipped code or glyph assets remain
package files.

## 5. Install or replace the one slot

Close the WSGM shell. A package installed through this command becomes trusted hardware code and may
later inherit WSGM's elevation, so inspect and validate the exact directory you intend to install.
Expand the `.wsgmpkg` into a fresh directory, then ask the installed WSGM binary to replace the
protected slot:

```powershell
$expanded = '<new-expanded-directory>'
if (Test-Path -LiteralPath $expanded) { throw 'The expansion directory must be new.' }
New-Item -ItemType Directory -Path $expanded | Out-Null
tar -xf <plugin.wsgmpkg> -C $expanded
if ($LASTEXITCODE -ne 0) { throw 'Package extraction failed.' }
& "$env:LOCALAPPDATA\WSGM\bin\WSGM.exe" --install-device-plugin $expanded
if ($LASTEXITCODE -ne 0) { throw 'WSGM rejected the plugin installation; inspect wsgm.log.' }
```

The maintenance command requests elevation, copies into the fixed nondiscoverable `.staging`
sibling, revalidates its bounded paths, manifest/API version, and x64 entry point, atomically
reserves the machine-wide WSGM/Device Lab hardware owner, and replaces
`C:\Program Files\WSGM\DevicePlugins\installed`. It repairs an ambiguous old slot by replacing the
whole slot and never leaves a release and developer plugin side by side. The source directory must
not overlap the installed slot, `.staging`, `.previous`, `.installed.previous`, or an abandoned
`.installed.staging-*` namespace in either direction and must not traverse a link/reparse point;
these checks run before recovery reconciliation. Enable Device Integration in WSGM Settings only
after the install succeeds. Runtime discovery/loading and maintenance use the same machine-wide
package-slot gate; the owner reservation is held through every filesystem operation, closing the
startup race without loading plugin code in maintenance. To return to core-only WSGM, run the
maintenance removal; it also requests elevation and applies the same gate and owner refusal:

```powershell
& "$env:LOCALAPPDATA\WSGM\bin\WSGM.exe" --remove-device-plugin
if ($LASTEXITCODE -ne 0) { throw 'WSGM rejected the plugin removal; inspect wsgm.log.' }
```
