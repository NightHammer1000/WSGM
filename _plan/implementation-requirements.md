# WSGM 2.0 outcome requirements

This file records product outcomes that simplification must preserve. It is not an implementation
architecture. Current work and acceptance status live only in `implementation-todo.md`; mechanisms
and device evidence live in focused `docs\` topics.

## Product invariants

1. **Core independence.** With Device Integration off, Game Mode, Steam Big Picture, the overlay,
   radios/audio, display control, RTSS and the Steam Input fallback continue to work. No plugin code,
   controller target, hardware capability write or AutoTDP loop runs.
2. **One optional plugin.** Zero installed packages is valid, one may load, and more than one refuses
   normal startup with every root named. WSGM never ranks or auto-selects packages.
3. **One lifecycle.** Enabling integration creates one in-process collectible runtime for the sole
   package. The session owns detection, activation, suspend/resume, fault recovery, make-safe stop
   and unload.
4. **Plugin hardware ownership.** A plugin owns device identities, transports, protocols, ranges,
   hardware profiles and exact restore behavior. WSGM owns session, UI, Steam and persistence policy.
   There is no generic raw hardware broker.
5. **Trusted-code honesty.** An installed plugin is administrator-approved hardware code with the
   same practical authority as WSGM. Package checks protect deterministic loading and correctness;
   they are not described as a sandbox.
6. **Thin public SDK.** Community plugins implement one documented API version and lifecycle, publish
   semantic capabilities/settings/input/glyphs, and can be validated, packaged and exercised through
   Device Lab without referencing WSGM UI or internals.
7. **Device Lab remains complete.** Its GUI and CLI share inventory, capture, diagnosis, scaffolding,
   testing and packaging operations. Hardware mutation remains attended and cannot be made unattended
   with a command-line acknowledgement.
8. **External ownership is respected.** WSGM removes or restores only state it owns or explicitly
   acquired. It does not kill or silently race another device manager. Uncertainty is reported rather
   than converted into success or automatic retry.
9. **Controller targets remain.** Steam Deck Composite, Xbox 360 and DualShock 4 remain selectable
   where the active VIIPER backend supports them, globally and per application. HidHide owned-delta,
   UI capture, neutral output, haptics, source arbitration and Steam Input fallback remain one
   coordinated path.
10. **No general remapper.** OEM buttons may map to WSGM actions or canonical controls; WSGM does not
    become a desktop macro/remapping engine.
11. **Native Steam UI stays narrow.** Patches restore or augment individual Valve surfaces without
    setting global SteamOS/gamescope identities. Each patch is independently removable and failure
    falls back to Valve behavior.
12. **One CEF system.** One persistent CDP transport, one session host, one bridge protocol and one
    ownership-marker/generation model own every resident Steam UI feature. Page-specific modules may
    keep their business logic but not a parallel connection/residency stack.
13. **Performance is shared.** RTSS discovery, global/per-application frame limit, overlay level,
    refresh pairing, Steam UI and WSGM overlay operate on one performance policy and one serialized
    adapter. RTSS remains useful without a Device Plugin.
14. **AutoTDP is frametime-driven.** It uses observed RTSS frametimes and a plugin-published power
    capability, learns per application/display context, pauses on manual override, and restores the
    baseline it actually took over from.
15. **Glyph ownership stays split.** Plugins own bounded static physical artwork and mappings; WSGM
    owns selectors, CSS/injection, presentation and input-test routing. Missing artwork means absence,
    not a fabricated generic device.
16. **Overlay actions have one owner.** Home, Steam, Device and System surfaces project the same
    session services used elsewhere. Settings edits durable configuration; it does not become a
    second live hardware controller.
17. **Feature-local failure.** Device, controller, CEF, RTSS, radio/audio, AutoTDP and glyph failures
    degrade their own feature and remain diagnosable without taking down the shell or desktop recovery.
18. **One bounded shutdown.** Application shutdown has one outer deadline. Input admission closes
    first; AutoTDP and device ownership make safe before shell/Steam teardown; Explorer recovery and
    installer handoff report verified, timed-out or unverified outcomes honestly.
19. **Explicit installation.** WSGM does not download plugins or controller drivers at runtime.
    Installer components are explicit, pinned and removable; uninstall restores WSGM-owned state and
    leaves externally-owned state intact.
20. **Measured resident work.** Polling is cancellable, bounded and transition-logged. Input hot paths
    do not allocate or log per sample. A repeating task without a current consumer is removed.

## Required automated evidence

- Configuration and source-generated serialization repair malformed/forward enum values without
  discarding unrelated settings or recovery snapshots.
- Package cardinality, path confinement, in-process load/unload, dependency resolution, lifecycle
  cancellation/faults and package replacement are covered with temporary directories only.
- Device capability routing covers generation, timeout, late result, desired-state ordering,
  suspend/resume and disposal races.
- Controller tests cover target replacement, UI capture, neutral output, HidHide exact delta and
  recovery, sample generation, haptic faults and supported target encoders.
- CEF tests cover transport reference counts/generation/reconnect, session resynchronization,
  ownership markers, command correlation, size bounds and patch-independent failure.
- RTSS/performance tests cover global/application precedence, policy persistence, external edits,
  tick rollover, refresh pairing, adapter serialization and disposal.
- AutoTDP trace replay covers startup, steady state, menus, context changes, telemetry gaps, manual
  override, cancellation and baseline restoration.
- Device Lab tests cover untrusted capture rejection, output-path decisions, worker cleanup, CLI
  option rejection, attended cancellation and GUI-close cleanup.
- Installer/build tests assert component scoping, old-artifact cleanup, atomic package promotion,
  rollback, controller pins/notices, shutdown handoff and produced artifacts.

No automated test may touch `%LOCALAPPDATA%\WSGM`, start the shell, navigate live Steam, mutate
hardware, install a plugin/driver, change display state or exercise the lock screen.

## Required live and attended evidence

- Explorer takeover/recovery produces an initialized medium-integrity current-session jobless
  desktop across normal, failure, update and uninstall flows.
- Steam CEF survives client/document generations and exercises each mounted surface against the live
  client without leaving replacements behind.
- The reference Claw verifies plugin lifecycle, capabilities, controller targets, HidHide, OEM input,
  suspend/resume, AutoTDP, glyphs and external-manager coexistence.
- RTSS is exercised against a rendering game, including external edits/restart and every frame-limit
  strategy.
- Overlay and Settings are exercised with controller, touch, keyboard, scaling, themes,
  accessibility, cancellation and shutdown.

Attended evidence is a release gate, not permission to leave testable source behavior unfinished.
