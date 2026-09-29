# Device integration

Device Integration is an optional, process-long WSGM subsystem. It is independent from Steam and
Desktop/Game Mode transitions. Turning it off leaves the shell, overlay, Steam Input lease, storage,
artwork, launch features, RTSS, and core recovery usable.

## One protected plugin slot

Normal startup counts package roots before manifest validation, device matching, elevation, Explorer
exit, Avalonia initialization, `ShellSession`, plugin loading, HidHide, or virtual-controller
creation.

- Zero packages starts core WSGM with Device Integration unavailable.
- One package is validated and asked to detect the current machine. A malformed or nonmatching
  package faults only Device Integration and reports the exact package error.
- Two or more package roots refuse normal WSGM UI and shell startup before device code runs. The
  error lists every package name and absolute path. WSGM never ranks, selects, disables, or prefers
  one package.
- Recovery/setup/update/uninstall, `--restore-shell`, and the dedicated plugin-removal maintenance
  path bypass this refusal without starting device code. `--overlay-test` remains simulated.

The release installer owns one administrator-protected slot under `%ProgramFiles%\WSGM`. Installing
a different device package replaces the existing package; a developer package occupies the same
logical slot. Managed maintenance and setup both use the fixed, nondiscoverable `.staging` sibling,
park the old slot at `.previous`, and atomically publish `installed`. They reconcile the older
`.installed.previous` managed-maintenance name and remove abandoned GUID staging siblings while the
package-slot gate is held. A maintenance source that overlaps `installed`, either fixed sibling, the
legacy recovery sibling, or a legacy staging namespace is rejected by lexical path and existing
filesystem identity before reconciliation mutates any of them. A source that traverses a
link/reparse point is rejected at the same pre-reconciliation boundary, including when its leaf is
missing. Existing path components are then held against replacement, and every enumerated file or
directory is opened without following reparse points before its handle is copied or traversed. Slot,
recovery, and current or legacy staging attributes are inspected exactly before cleanup or
replacement; access and I/O failures refuse mutation instead of being treated as absent paths. An
unrelated missing source still reconciles a parked package before failing. WSGM never loads plugin
code from a user-writable discovery root.

The minimal `plugin.wsgm.json` identifies the package ID, name, version, exact API version, entry
assembly, and entry type. Hardware identity, dependencies, capabilities, and operational policy are
published by plugin code instead of duplicated in the manifest. Runtime validation accepts only an
AMD64 managed assembly with a readable CLR header and assembly metadata, and bounds all files plus
directories before sorting or traversing them.

## Runtime topology

`ShellSession` creates at most one `DeviceCoordinator` for the interactive session. The exact
`Global\WSGM.DeviceOwner` marker prevents any session, setup, maintenance command, or attended
Device Lab run from starting a second machine hardware cycle. The admitted coordinator validates the
sole package and loads its public entry type into one package-local collectible assembly-load
context inside WSGM. That one runtime stays alive across Steam restarts, games, and desktop/game
transitions. Runtime discovery and elevated install/removal share the exact crash-recovering
`Global\WSGM.DevicePackageSlot` mutex. Maintenance reserves the hardware marker before taking that
gate and keeps both reservations through filesystem replacement, so package bytes cannot change
under a loaded plugin. Uninstall holds the same objects through package deletion.

When setup or uninstall refuses before file mutation, it restores the initially observed
shell/settings mode and restarts the logon service through its installer-tagged start only when that
service was initially present and running, so startup catch-up cannot launch a second boot process.
The restored shell or settings process opens the installer's existing unowned global marker,
acknowledges the second handle, and retains it for the rest of its process lifetime before setup
releases its copy. This preserves the session without allowing package maintenance and a new
hardware cycle to overlap.

`DevicePluginRuntime` loads only the validated package entry assembly and package-local
dependencies. Lifecycle calls and semantic publications are direct managed calls. A bounded one-slot
sample pump in `ControllerManager` coalesces high-rate controller state while preserving the newest
accepted sample.

The installed plugin is explicit administrator-installed hardware code and inherits WSGM's required
authority. The collectible load context isolates package dependency resolution and permits a clean
unload after verified cleanup, but it is not process-crash containment: a process-fatal managed or
native plugin failure now terminates WSGM with the plugin. Existing WSGM/session recovery and the
plugin's bounded next-start recovery record are the remaining recovery boundary. This is the
explicit maintenance-cost tradeoff of the in-process design, not a claim of equivalent isolation.

Dependency resolution inside that context is **host-first**. Package authors cannot be expected to
trim what their SDK copies beside the plugin: any `-windows10.0.x` build ships `WinRT.Runtime.dll`
and `Microsoft.Windows.SDK.NET.dll`, and a second `WinRT.Runtime` in the process makes whichever
side initializes second fail its process-global `ComWrappers` registration for good — on the Claw
the plugin touched WinRT first and WSGM's own Wi-Fi and Bluetooth queries were the side that died
(2026-09-01). `PluginLoadContext.Load` therefore pins the SDK and the WinRT pair to the host's
loaded assemblies by name, asks the default context for every other dependency before consulting the
package, and uses the package copy only for assemblies the host does not have or cannot satisfy by
version (that duplicate is logged once). This is the parent-first rule plugin hosts converge on
(`PluginLoader.PreferSharedTypes`, Java class loading): sharing what the host already owns costs
nothing the isolation was buying, while a duplicate of anything with process-wide state is a fault
no later cleanup can undo. There are no runtime trust tiers, publisher grants, signer
rotation/revocation, package ranking, quarantine catalog, or de-elevated plugin class.

Plugins publish only the public semantic SDK. WMI, HID, sensors, lighting, firmware, controller, and
recovery implementation stays inside the plugin. A plugin cannot supply XAML, JavaScript, URLs,
Steam selectors, arbitrary shell/file operations, or a raw hardware broker.

## Lifecycle and recovery

The runtime has one serialized lifecycle: detect, start, suspend, resume, stop, and diagnostics.
Suspend/lock quiesces the current plugin; resume advances one cycle generation before new state or
commands are accepted. Full release first closes command admission, cancels and quiesces in-flight
commands, performs the controller handoff, stops the plugin, detaches direct publications, disposes
the plugin, and unloads the collectible context only when cleanup was verified. A command canceled
at its caller deadline retains its stable late-completion task so an eventual hardware outcome is
still observed instead of being misattributed to a later command.

Controller management is an optional child policy, not a plugin-health requirement. A plugin whose
other services are healthy remains `Active` when that child is deliberately off; its controller and
haptic capabilities separately publish `ResourceReleased`. A requested controller acquisition that
fails is still a degraded service and must not be disguised as the disabled case.

Startup cancellation after acquisition gets a fresh bounded controller handoff and plugin stop,
while process-lifetime cancellation preserves the runtime for the outer shutdown owner to stop under
its application deadline. Generation-bearing direct publications validate cycle and descriptor
generations before reaching WSGM consumers; stale samples and state are refused rather than allowed
to cross a resume or controller-reacquisition boundary.

One process shutdown deadline covers normal exit, update, session logoff, and uninstall. The same
deadline is passed through controller release and plugin restoration; WSGM does not stack a second
set of phase budgets. WSGM-owned virtual-target and HidHide cleanup still runs after an unverified
plugin response, and the compact result is logged as clean, unverified, timed-out, or failed.

A background plugin service reports a runtime fault through the direct host adapter. That completion
closes command admission and drives the same bounded make-safe, stop, detach, dispose, and restart
policy. WSGM retries at most twice, then faults Device Integration for the run with a clear manual
retry. The fail-open path restores usable input and removes only WSGM-owned state. It never starts,
stops, kills, or reconfigures MSI Center, Handheld Companion, or another external manager.

Recovery records only temporary plugin-owned state that was actually changed and could not be
restored. Persistent desired RGB/profile state remains separate. An indeterminate hardware write is
reported to the plugin owner and is never blindly retried.

## Public SDK and glyph data

`WSGM.Device.Sdk` is the one public API shared by WSGM, plugins, and Device Lab. It lives in its own
repository (`KillerPixelCrew/WSGM.Device.Sdk`, MIT) and is pinned here as the
`external\WSGM.Device.Sdk` submodule; see `AGENTS.md` for why its licence differs from WSGM's. It
contains the exact plugin API version, one plugin lifecycle, practical semantic capability
descriptors/state/commands/results, canonical controller and motion samples, haptic output, OEM
events, glyph data/control maps, and a publication sink.

The SDK does not contain implementation modules, generic resource leases, WSGM UI policy,
source-arbitration projections, evidence IDs/locks, source generators, Steam selectors, or CDP
patches. Add an abstraction only when the Claw plugin and a materially different plugin both need
it.

Glyph artwork and semantic control maps are static plugin data. WSGM validates local paths, IDs,
formats, dimensions, sizes, and references, then owns every Avalonia and Steam adaptation. Missing,
ambiguous, or mismatched profiles retain native Steam/generic WSGM presentation.

## Controller management

`ControllerManager` is the one WSGM-side owner of controller management for a session: the virtual
target and its replacement, the haptic return path, WSGM's own HidHide delta, the local UI capture,
the source WSGM's own surfaces navigate from, and the make-safe handoff. `DeviceCoordinator` keeps
the plugin conversation; the manager orders both halves. Nothing else creates a target, mutates
HidHide, or decides where UI input comes from.

The target is chosen by exactly two stored layers: one global default plus per-application
overrides, both kept directly under device integration rather than under a per-device profile.
Overrides are keyed by the canonical running-application identity from the one
`RunningApplicationMonitor`, which also resolves the RTSS profile, so the controller target and the
performance profile can never disagree about which application is running.

That identity has two sources, and only one of them is Steam. The monitor also takes the foreground
application — a WinEvent hook plus a two-second poll, because a hook alone misses focus changes
across a lock or an elevation transition, and a UWP window is resolved through
`ApplicationFrameWindow` to the process that actually owns a child window, or every UWP application
would share one profile. It is an input to the same projection rather than a second observer, which
is what keeps the one-monitor rule above intact.

Steam wins whenever it names exactly one running application: that identity is the one its launch
went through and the one the shortcut's executable was resolved from, so alt-tabbing out of a
running game does not retarget its profile. The foreground fills only the case where Steam names
nothing — the desktop, another launcher, a title started outside Steam — which is what makes the
overlay's per-application rows mean anything outside a Steam game. It deliberately does not break a
tie: when Steam reports two running applications the state stays ambiguous, because focus says which
window the user is looking at, not which of two games they meant to configure, and when the
observation itself failed the state stays unavailable rather than claiming an application is
running.

A foreground window that is not an application — WSGM's own surfaces included, since the overlay
takes focus by design at exactly the moment the user is most likely editing that profile — leaves
the previous application in force rather than dropping to the global profile. An unreadable process,
which is ordinary for anything elevated or protected, is treated the same way.

The semantic capabilities keep their five desired-state layers because hardware limits genuinely
differ on battery and per profile; a controller target does not.

Only one target exists at a time. A per-application change is one replacement operation that
neutralizes and removes the old target before creating the new one, so the two are never enumerated
together. Any unavailable prerequisite — closed release gate, missing or incompatible backend,
unhealthy HidHide, a target that does not enumerate — fails open: the shell, SDL input, and the
Steam Input lease continue unchanged, global HidHide state is untouched, and WSGM's own surfaces
stay on the SDL-plus-Steam-lease source.

Capture by a WSGM surface is reference counted and never reaches the virtual target. Controls held
when a surface opens are suppressed until released, and forwarding resumes only on the first sample
in which every control the UI used is up, so the press that opened or closed a surface never arrives
in the game as a fresh input.

The make-safe handoff is stated in the shared `ControllerHandoffStep` vocabulary rather than a
second WSGM-local one, so a pasted log settles how far the handoff got. WSGM's half collapses into
two of those steps and keeps the two orderings that prevent a defect as explicit guards: the virtual
target may not be removed until the physical release has concluded either way, and WSGM's HidHide
entries may not be removed until the target is gone. Removing them earlier would expose a device the
plugin is still holding, which is the duplicate-input state the single-target rule exists to
prevent. An unverified or failed plugin answer still runs WSGM's removal; the result records
`ReleasedUnverified` rather than presenting a timeout as a clean release.

Controller management uses VIIPER directly. Its Steam Deck target carries all four rear controls and
stick-touch fields through usbip-win2's pinned signed driver; WSGM's encoder supplies the complete
Neptune frame. Motion is converted from the SDK's application axes back to the Deck report's raw
`X, Z, -Y` axes at 16 gyro counts per degree/second and 16384 accelerometer counts per g; leaving
the values as normalized axes was why Steam saw a motion source but no usable gyro movement. Xbox
360 and DualShock 4 now have their own VIIPER wire encoders and are advertised as selectable
targets: X360 maps the standard buttons, byte triggers and signed sticks; DS4 additionally maps
touch contacts, gyro and acceleration. The shell never installs or repairs a driver at runtime.
`third_party/controller/viiper/README.md` records the live-device evidence and exact pins.

The Steam Deck target's return path accepts all three feedback shapes Steam sends: ordinary
sixteen-bit `0xEB` rumble, continuous `0xEA` trackpad haptics approximated symmetrically on the
physical motors, and `0x8F` haptic pulses. A pulse carries a bounded, route-generation-checked stop
back through the serialized haptic sink; an old pulse can neither stop a replacement target nor
leave the Claw's latched motors running. The first physical output admitted for each target is
logged once, never at report cadence.

An action-only haptic sink has availability but deliberately has no readback. The overlay treats an
available, unreadable action as `Ready`, shows `RUN`, and permits its bounded preview instead of
mislabeling the absent value as `Unknown` and disabling the only direct hardware test.

The optional installer task owns initial usbip-win2/HidHide installation. Its USB/IP helper remains
nonfatal, but publishes an atomic bounded status under `%ProgramData%\WSGM`; setup reads that status
instead of treating exit code zero as proof that the signed driver registered. A new installation
requests a reboot, an already-present driver does not, and a failed, newer-unreviewed, missing, or
malformed result is shown without rolling back WSGM itself.

A target replacement also owns the usbip-win2 client attachment, not just VIIPER's server-side
device. Attach records the driver-assigned port and removal issues `IOCTL_PLUGOUT_HARDWARE` for that
port before deleting the server device; otherwise the closed old stream remains as a stale Windows
attachment and the next target is not a true live replacement. This is the focused backport from
Handheld Companion's bundled VIIPER commit `679f7e0`, layered on WSGM's pinned
`corando98/VIIPER@024aef3a` `viiper-controller` performance/Steam Deck baseline. The managed
feedback route closes and the backend target becomes unavailable before plugout. VIIPER then removes
its reverse callback registration, drains callbacks already in flight, and releases its global C-API
mutex across the blocking driver request. That ordering keeps a final host output packet from
re-entering WSGM during synchronous removal or reaching the physical controller or replacement
target.

## Authored profiles

A **setting** is one value WSGM keeps and hands the plugin. A **profile** is a named shape the user
builds and then applies. They are different records with different homes on purpose, and a curve is
refused as a setting (`PluginSettingDescriptor.TryValidate`) precisely so it cannot acquire two.

Authoring is Settings' job and selection is the overlay's (D22b), which is why
`DeviceProfileSelectionStore` writes only _which_ profile is chosen and never a profile's contents:
the two surfaces cannot fight over one record.

The chain, and what each link exists to prevent:

| Step    | Owner                                                       | Prevents                                                                                                                   |
| ------- | ----------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| Author  | `Settings\Pages\PluginSettingsPage`, `Controls\CurveEditor` | A gesture producing a curve the router refuses — every edit goes through `CurveEditing`, so an invalid one cannot be built |
| Store   | `DeviceAuthoredProfile`, `ConfigStore` normalization        | A profile that keys nothing or whose inputs do not ascend surviving to be chosen                                           |
| Select  | `DeviceProfileSelection`, `DeviceProfileSelectionStore`     | A per-game change silently widening to every game; an override stranded on a stale copy of a curve                         |
| Resolve | `DeviceProfileSelectionResolver`                            | A deleted profile quietly falling back to someone else's curve                                                             |
| Check   | `DeviceProfileValidation`                                   | A curve authored against bounds the device no longer has                                                                   |
| Apply   | `Shell\DeviceProfileApplier`, `ShellSession`                | The fan curve and the controller target disagreeing about what is running                                                  |

**Selections reference a profile by id, never by copy.** Editing a profile has to change every
application already using it; copying the curve at selection time would strand every override on the
shape the profile happened to have that day.

**The pre-apply check is not redundant with storage normalization.** Normalization sees only a
profile's internal shape. Profiles are authored with no plugin running — `--settings` starts no
device runtime — so a curve is built against the last known bounds and the device can be updated,
swapped, or downgraded before it is applied. The descriptor is therefore read at apply time and
never cached: a plugin republishes its capabilities across a cycle.

**A bound the descriptor leaves unset is not invented.** An absent minimum means the device declared
no limit there, and supplying one would refuse a curve it would have accepted.

Two refusals are deliberately _not_ symmetrical with the rest. A selection naming a deleted profile
is **kept** by normalization rather than pruned, because the resolver reports it by name and pruning
would turn a diagnosable mistake into a per-application override that vanished without explanation.
And it resolves to nothing rather than falling back to the global choice, because falling back hides
that the user's intent for that application is gone while the fans quietly run another curve.

Applying counts `AppliedUnverified` as success: many EC writes have no readback, and treating absent
confirmation as failure would report every one of them as broken. A timeout does not count — whether
it was written is unknown, and claiming success there is the one answer that misleads.

A profile carries a curve **or** a colour, never both. The capability being authored decides which,
and a profile holding an unused half would let a capability change resurrect a value the user set
for something else. Colours are masked to 24 bits on the way in: the picker returns an alpha channel
WSGM has no use for, and a stored value carrying one reads as a wildly different colour when it is
later unpacked as RGB.

The overlay's row states the **scope** of the current choice, not only its name — "Quiet, for this
game" and "Quiet, for everything" read identically otherwise, and that difference is what the row is
opened mid-game to check. Pressing it scopes the change to the running application when there is one
and globally otherwise, persisting before applying so a failed save cannot leave the device on a
profile the configuration does not name. Cycling wraps through "none", and a selection whose profile
was deleted reads `MISSING` and stays cyclable, because pressing out of that state is faster than
opening Settings mid-game.

## Device Lab and UI ownership

Device Lab is one optional developer-tools application with GUI and CLI modes over the same internal
operations: doctor, inventory, capture, inspect/compare/correlate, fixture extraction, scaffold,
glyph import, local plugin run, validate/test, and pack.

It lives in `KillerPixelCrew/WSGM.DeviceLab` and is pinned here as `external\WSGM.DeviceLab`. The
main solution builds that project, and the installer's optional `devicelab` component publishes it
from the same commit. Change and commit its behavior inside the submodule, then commit the moved Git
link in WSGM. The ownership rules below still bind it, and its own repository is where they are
enforced.

Read-only is the default. One explicit attended action may invoke plugin-owned
snapshot/readback/restore code; it has no `--yes`, bulk, CI, imported recipe, trial-hash, receipt,
evidence-promotion, or remembered-consent route. Every output path is explicit, privacy redaction is
mandatory, and tools never read or write live `%LOCALAPPDATA%\WSGM` data.

Device Lab validates the package as data and the new state path, then atomically creates the same
machine-wide owner-mutex object used by WSGM. It keeps that unowned handle open through plugin
cleanup and disposal, so owner absence cannot become stale between detection and activation. If
construction starts but cannot return a disposable instance, or later plugin disposal fails, Device
Lab still unloads the collectible plugin context but retains the owner handle until process exit.
The still-running Device Lab therefore cannot overlap those unverified resources with a competing
WSGM cycle. Elevation, local attendance, CI refusal, and immediate confirmation are also checked
before the selected plugin assembly or its constructor loads. Only then may the plugin perform exact
read-only detection; a mismatch still refuses activation.

Settings owns startup/integration/controller-ownership/logging/update configuration and
owner-process requests. Live power, fan, controller, motion, OEM, lighting, glyph, performance, and
recovery state belongs on the overlay's Device destination. Overlay, Settings, native QAM, and
diagnostics consume the same runtime services rather than parallel policy/projection stacks.
