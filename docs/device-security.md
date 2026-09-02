# Device runtime boundaries

This document is the short boundary checklist for device integration. Runtime behavior, controller
ordering, profiles, and recovery are described in `device-integration.md`; package construction and
installation are described in `device-plugin-authoring.md`.

## Ownership

The installed plugin owns exact-device detection, device transports, hardware writes and readback,
restoration, physical-controller acquisition, and device diagnostics. WSGM owns session policy, the
virtual controller, its own HidHide changes, Steam integration, and UI. Device Lab owns offline
diagnosis and one explicitly attended hardware action.

An installed plugin is administrator-selected managed hardware code running inside WSGM. The
collectible load context resolves package-local dependencies and supports unload after cleanup; it
does not contain process-fatal plugin failures. Plugins publish through the semantic SDK and do not
supply WSGM UI, Steam patches, URLs, or generic shell commands.

## Package and runtime

Production accepts zero or one installed package. More than one package root refuses normal startup
before plugin code runs. Replacement uses the fixed `.staging` and `.previous` siblings while the
package-slot mutex is held; path containment, reparse-point, file-identity, manifest, entry-count,
and byte-size checks make the transaction deterministic.

The exact `Global\WSGM.DeviceOwner` object serializes the production runtime, package maintenance,
setup/uninstall, and attended Device Lab hardware access. The package-slot mutex separately prevents
loading and replacement from observing a half-published package.

`DevicePluginRuntime` makes lifecycle calls, capability commands, state publication, controller
samples, haptics, OEM events, and fault publication directly in-process. Cycle and descriptor
generations reject stale commands and publications. Shutdown closes admission, drains in-flight
work, releases controller ownership, stops and disposes the plugin, then unloads the context when
cleanup is verified. WSGM still removes its own virtual target and HidHide changes after a plugin
timeout or failure.

## HidHide findings (device-observed 2026-08-29, MSI Claw)

Two findings recorded from `Shell\HidHideOwnership.cs`:

- **Another tool's hide blinds discovery before WSGM's own transaction runs.** HandheldCompanion had
  hidden the Claw's pad in both modes with an allowlist naming only itself: SDL reported no gamepad,
  the plugin's HID enumeration could not see the pad it had just switched the device into, and
  nothing anywhere mentioned HidHide. `EnsureReadableAsync` therefore allowlists WSGM before the
  plugin's cycle starts — after discovery has failed it is too late for that cycle.
- **HidHide stores application entries as NT device paths.** A ledger whose preexisting list already
  contained `\Device\HarddiskVolume3\…\WSGM.exe` recorded a delta adding `C:\…\WSGM.exe`: the
  allowlist grew on every activation, and because cleanup matches what it wrote, the duplicate in
  the other notation was left behind on restore. `Contains`/`NormalizePath` compare both notations
  for that reason.

## Imported data and Device Lab

Capture, manifest, package, and request parsers accept external files, so they bound sizes and
shapes and require explicit output paths. Shareable capture output is redacted, and tools never use
live `%LOCALAPPDATA%\WSGM` state. Offline commands do not load plugin code.

The only Device Lab mutation path is a locally attended plugin action. It has no `--yes`, bulk, CI,
remembered-consent, or imported-operation route. It validates the package and live machine, reserves
the production owner object, and retains that reservation through plugin cleanup and disposal.

## Verification boundary

Automated tests cover package cardinality and containment, lifecycle ordering, stale-generation
rejection, controller cleanup, atomic replacement, and unresolved restoration with isolated fakes.
Hardware writes, live shell takeover, live Steam, and attended Device Lab work remain device
verification and must record the exact build, device, observed result, and cleanup.
