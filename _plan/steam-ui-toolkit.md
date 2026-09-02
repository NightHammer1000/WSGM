# steam-ui-toolkit — a framework for changing Steam's CEF front-end

**What it is:** a framework to add, hide and reorganize elements in Steam's Big Picture front-end,
plus the reconstructed SteamOS surfaces WSGM already built, plus a pipe so other developers can plug
their own backend behind them — and an Extensions tab that third-party plugins populate.

Decky Loader's territory, approached from the other side: not a plugin loader for a Deck, but the
mechanism for rebuilding SteamOS's front-end anywhere Steam runs. **There is no Decky for Windows**,
so this is not a port and has no ecosystem to be compatible with. It is the first one.

WSGM keeps **what it changes**. The surfaces are its product; the method is not.

This is the largest extraction so far and the only one needing real architectural change first. The
others moved code that was already a unit. This boundary runs *through* the existing code.

## The shape

```
steam-ui-toolkit
 ├ transport + lifecycle     CDP, probe/apply/verify/remove, ownership, kill switches
 │
 ├ THREE STABLE APIs
 │   Data constructs         feed a Steam store its own data shape
 │   RPC                     answer or overlay a call Steam makes
 │   Reveal                  unhide what SteamOS gates, without pretending to be SteamOS
 │
 ├ ELEMENT FRAMEWORK         add · hide · reorganize, with ownership and clean removal
 │
 ├ SURFACE MECHANISMS        Library · Downloads · Badges · QAM rows · Settings rows
 │
 ├ FEATURES                  things a consumer would otherwise reverse-engineer:
 │                           current-game detection, collections, artwork, launch
 │                           config, download queue, glyph CSS delivery
 │
 └ BACKEND PROVIDERS         a reconstructed surface declares what it needs answered

WSGM  — modules that consume the mechanisms and supply the policy
 SD-card libraries → Library    sort order → Downloads    card state → Badges
 TDP · frame limit · VRR · Wi-Fi · Bluetooth · audio · brightness → QAM + Data + RPC + Reveal
 and its Device Plugin is one backend behind those providers
```

The rule for every surface: **the toolkit provides the way to manipulate it; the consumer decides
what to say.** `SteamLibraryTabs.SyncTabsAsync`/`PushOrderAsync` is mechanism — *which* tabs is
WSGM's. `SteamDownloadSort.InstallExpression` is mechanism — the *sort order* is WSGM's.

## The three stable APIs

What someone else actually needs. Each was learned against a live client; each has a trap that
belongs in the API rather than in a consumer's future bug report.

### 1. Data constructs — feed a store its own shape

Supply an absent namespace, or push through a store's own ingestion path, so Valve's components find
the data they were written against. Used by `SystemPerfStore` (the Performance tab's entire
backend), the audio store, and the header Wi-Fi indicator via `SetDeviceInfo`.

- **Protobuf lives at the namespace boundary.** `UpdateSettings` receives a
  `serializeBase64String()`, not an object. Decode through the message class's own
  `deserializeBinary`; forwarding the string verbatim silently rejected every performance write.
- **The second gate.** Filling a store proves nothing until the render gate opens — a
  constructor-cached `m_bAvailable`, a `staleTime: Infinity`, a prototype getter. "Did it store" and
  "did it render" must be separate verifications.
- **Residency.** A synthetic entry needs a no-op `MarkAsNotPresent` to survive the backend's periodic
  reports. Do *not* wrap `OnNetworkDevicesChanged`: the backend holds the callback bound at init, so
  a property wrap never fires.

### 2. RPC — answer what Steam asks

Overlay one answer, or replace stub service methods and invalidate the query that cached the stub's
refusal. Used by SteamOS Manager `GetState` and the Bluetooth service methods.

- **`actionGeneration > 0` is mandatory.** Five gates silently rejected every write over a zero.
  This belongs inside `request()`, allocating a valid generation when handed an invalid one — no
  caller should be able to build a bad envelope.
- **Arity and enum values are facts to read, never assume.** `SetDeviceVolume(deviceId, direction,
  volume)`; `AudioDirection` is `Input = 0, Output = 1`. Guessing produced three live bugs, including
  a volume slider that only ever set 0% or 100%.
- **Replacing a stub is half the job.** React-query still holds the stub's answer.

### 3. Reveal — unhide what SteamOS gates

Override a store getter or flag, mount Valve components by localization token, watch a client
setting. Used by network availability, the brightness slider, QAM row mounting, `steamos_tdp_limit*`.

- **Never touch `TS.IS_STEAMOS`.** Spoofing the platform constant force-shows rows nothing can back
  (D16). Reveal exactly the surface you can serve.
- **The self-incompatibility teardown loop.** A probe requiring the pre-patch condition its own apply
  invalidates tears itself down forever — hit three times, on the audio namespace, network getter and
  brightness flag. Every reveal needs an ownership marker so "already ours" is distinct from "not
  applicable".
- **`force_deck_perf_tab` is not a reveal.** It is persisted client state that outlives the session.

### The rule underneath all three

**Never iterate the webpack module registry constructing exports.** It restarted the machine and
signed Steam out. Probes name literal module ids and inspect factory or prototype source. This is an
API constraint — the toolkit should offer no call that makes it easy.

Every patch fails **open** to Valve behaviour, carries an ownership marker, retains the original,
accepts "already ours", and removes only its own work. A successful patch must not invalidate its own
next probe.

## Backends are pluggable — the plugin is the pipe

A reconstructed SteamOS surface is useless without something behind it. The toolkit reconstructs the
*surface* and declares what it needs answered; the consumer supplies the backend.

For WSGM that backend is RTSS, AutoTDP, `windows-device-control`, and — for anything device-specific
— the Device Plugin. **`WSGM.Device.Sdk` is the pipe**: a device author implements one plugin and
gets the whole reconstructed SteamOS front-end above it, without touching CEF at all. Someone else
could plug in a different daemon, a different OS layer, or nothing at all and use only the element
framework.

**Open decision — does the toolkit depend on `WSGM.Device.Sdk`?**

Recommendation: **no.** The toolkit defines its own minimal provider interface per surface, and WSGM
adapts Device Plugin capabilities onto it. Reasons:

- Most surfaces have no device behind them at all — downloads, badges, libraries, collections.
- A Steam-UI framework that drags in a handheld device SDK is not usable for a kiosk or an HTPC.
- The Device SDK carries *WSGM's* lifecycle semantics — cycles, generations, make-safe ordering —
  which are the host's concern, not the front-end's.
- Independent pins mean either can move without the other.

The two should *rhyme* — semantic, honest about uncertainty, refusals carrying a reason — so wiring
one onto the other stays mechanical. They should not be the same type.

## Forking or basing on Decky Loader — assessed and rejected

Considered seriously, because the overlap looks large from outside. Two independent reasons say no,
and the first is decisive on its own.

**1. The licence forecloses it.** `_ref/decky-loader` is **GPL-2.0**, declared as `GPLv2` in both
`backend/pyproject.toml` and `frontend/package.json` with no "or later" election. (The
"any later version" wording in the LICENSE file is the standard GPL-2 appendix template, not the
project's election.) That means:

- A fork or derivative is GPL-2, so the toolkit could not be MIT. That kills the reason the whole
  org is permissive — the SDK is MIT specifically so a vendor or OEM can ship a closed backend, and
  a GPL-2 front-end framework above it would take that back.
- **GPL-2.0-only is incompatible with GPL-3.0.** WSGM is GPL-3.0-or-later, so a Decky-derived
  component could not legally be combined with WSGM's own code in one program. This is not a
  preference; it is the one combination the two licences forbid outright.

**2. The architectures point in opposite directions.** Decky is a **plugin loader for SteamOS**: it
assumes the SteamOS surfaces already exist and adds to them. This toolkit **reconstructs surfaces
that do not exist on Windows** — the three stable APIs exist precisely *because the backend is
missing*, and on a Deck none of them would be needed. Concretely:

- Decky's backend is Python (20 modules, poetry, PyInstaller). WSGM ships no Python and adding a
  Python runtime to a Windows shell is a large, permanent dependency for no gain.
- Decky assumes Linux — systemd, `/home/deck`, root, its own updater.
- Decky has no probe / verify / remove-with-ownership lifecycle of the kind built here. That
  discipline is what makes a Steam update degrade to Valve behaviour instead of breaking, and it is
  the most valuable thing this toolkit has.

**3. There is no Decky for Windows, so there is no ecosystem to be compatible with.** This is the
reason compatibility is not worth pursuing even setting the licence aside. Decky's plugins carry
Python backends and assume Linux paths, systemd and SteamOS binaries. A Windows loader that
implemented `@decky/api` and `@decky/ui` faithfully would inherit the API and almost none of the
plugins — the work of compatibility, without the payoff of an ecosystem.

**What is worth taking instead — coexistence, not code.** Never collide with Decky's or CSSLoader's
injected nodes, marker classes or namespaces. WSGM already does this for CSSLoader — its own
`wsgm-glyph-style` class, never touching a `.css-loader-style` node — and the rule generalizes to
anything else sharing SharedJSContext.

Read Decky for what it teaches about Steam's front-end. Do not link, vendor or fork it. Build the
extension model natively instead, which is the next section.

## Extensions — a tab fed by plugins

The requirement: **an Extensions tab that third-party plugins populate.** Decky's category, built for
Windows, rather than a port of Decky.

The pleasant part is that this is mostly assembly, not invention. Nearly every piece already exists:

| Need | Already built |
| --- | --- |
| mount a tab into the QAM | localization-token mounting, used by every current row |
| host → page state | `SteamUiStatePublication` |
| page → host commands | `SteamUiCommandHandler`, with `actionGeneration` handled centrally |
| load, verify and unload untrusted code | the Device Plugin runtime: collectible `AssemblyLoadContext`, one slot, validated manifest |
| package, validate, pack | Device Lab's `validate` / `pack`, already generalizable |
| survive a Steam update | probe / apply / verify / remove with ownership |

**An extension is a module loaded at runtime.** That is the unification worth aiming at: step 1 of
this plan defines `ISteamUiModule` as the internal contract for a surface. An extension is the same
contract, discovered from a package directory instead of compiled in. If those two things end up as
one mechanism, the framework is right; if they end up as two, something is wrong.

**Shape, mirroring the Device Plugin because that pattern is proven here:**

- `extension.wsgm.json` — id, name, version, exact API version, entry points.
- A frontend fragment: the extension's own TypeScript, compiled and hashed like any module fragment,
  contributing rows or a panel to the Extensions tab.
- An optional .NET backend implementing an extension contract, reached through the existing
  publication/command pair. **No Python, no second runtime** — the bridge WSGM already has is the
  extension↔host RPC.

**Honesty about isolation, same as the Device SDK.** An extension's backend loaded in-process has the
host's authority. Validate integrity — manifest, bounds, managed x64 entry, hashes — and say plainly
that this is not a sandbox. A collectible load context buys clean unload, not containment. The one
thing that must hold is that a broken or hostile extension cannot take down the shell: the same
fail-open rule every patch already follows.

**Ownership split.** The toolkit provides the extension host — discovery, load, lifecycle, the tab
mechanism, the package contract. The consumer decides policy: where packages live, whether extensions
are enabled at all, and what the tab is called. WSGM's answer is a Settings toggle and a package
directory; another consumer's may be neither.

This is deliberately **after** steps 1–5. An extension host built before modules are one declaration
would harden the five-places problem into a public contract.

## Features already built, worth shipping as features

These are why this is more than plumbing. Each cost real live-verification and none of it is
documented anywhere:

| Feature | What it does |
| --- | --- |
| `SteamPageBridge.GetCurrentAppIdAsync` | which game page is open — focused React fiber walk, with a largest-visible-hero fallback for mouse/touch |
| `UpdateCardBadgesAsync` / `DisableBadgeAsync` | attach and remove badges on library cards |
| `SteamLibraryTabs` | sync and reorder library tabs |
| `SteamCollections` | read and delete collections |
| `SteamArtwork`, `SteamGridDb` | apply and clear custom artwork |
| `SteamLaunchConfig` | read, apply and restore per-game launch configuration on the *running* client |
| `SteamDownloads.QueryAsync`, `SteamDownloadSort` | read and reorder the download queue |
| `SteamGlyphCss`, `SteamInputGlyphStylePatch` | physical controller glyphs as CSS, coexisting with CSSLoader |

The glyph work carries its own lesson worth keeping in the framework: **probe the parsed stylesheets,
not the DOM.** Those elements exist only while a controller settings view is open, so a DOM probe
reports "incompatible" almost always.

## What blocks extraction today

**A module is not a thing yet.** Adding one surface means touching five places:

1. a C# `ISteamUiPatch` class in `Core/`
2. a TypeScript fragment listed by hand in `eng/build-steam-assets.mjs`
3. a `StatePublication` row in `SteamUiSessionHost` (WSGM → page)
4. a `SemanticCommandKey` row in the same file (page → WSGM)
5. a patch-id constant, also in that file

A framework cannot host modules it must be edited to know about.

What is *already* clean sets the size of the job: `PersistentSteamUiTransport` (721 lines),
`SteamUiPatchManager` (530), `SteamCdp` (435) and `SteamUiCdpConnection` (407) carry no WSGM coupling
of substance, and every concrete patch compiles against nothing but `System` and the patch context.
`ISteamUiPatch` — probe/apply/verify/remove with bounds, resource key and fingerprint — is already
the right abstraction.

## Progress

Steps 1-5 are done and in `2.0`. Each landed with the gate green, and the notes below record what
changed against the plan rather than restating it.

- **1. A module is one declaration.** `ISteamUiModule` + `SteamUiModuleSet`, with duplicate module,
  patch and command identity refused at startup. Verified the surface was unchanged: 19 patch
  registrations, 11 publications, 31 command keys, 16 handlers, all identical sets. Publication
  ORDER changed, which is safe because the pump reads each independently under its own patch id.
- **2. The three APIs, named.** The ownership claim turned out to be the primitive underneath all
  three, hand-rolled five ways — which is where the self-incompatibility teardown loop kept coming
  from. `ownership.ts` now claims a value, a member, an accessor or a supplied namespace; every
  gate is ported and **no hand-rolled marker write remains**. `rpc.ts` deduplicates the transport
  reply and the query invalidation. **Three real defects were found in the process**, all of which
  passed every existing gate: a `typeof` check that excluded functions, so an overlaid method
  outlived its own removal; the Perf gate deleting `System.Perf` without checking the marker, so
  WSGM's cleanup would remove a real backend; and `GetState` read before it was validated.
  `eng/check-ownership-claims.mjs` now runs 26 scenarios against the **emitted** asset and is
  wired into the gate — it is not a test that passes by construction, and re-introducing the
  `typeof` defect fails four of its checks.
- **3. One bridge identity.** Nine copies of the namespace literal, including two inside JavaScript
  expression strings, now reference `SteamUiBridgeIdentity`. Both interpolated expressions verified
  byte-identical in the compiled assembly.
- **4. Fragments discovered, not listed.** The builder owns the IIFE close that `components.ts` used
  to own, which had silently made that one fragment position-critical. Order comes from the
  directory a fragment lives in. The emitted asset is purely reordered — sorted line multisets
  differ by zero lines. Verified a new gate file is picked up with no builder edit, and that
  removing it restores the previous hash exactly.
- **5. The traffic directions extracted.** `SteamUiModuleRuntime` takes the publication pump, the
  request router, in-flight cancellation and the refusal log; 324 lines out, host down to 1,611.
  The synchronize loop deliberately stayed: it is policy about which patches are on when, and
  extracting it would have meant a constructor of predicates describing one host's rules.

- **6, first half — the boundary is real.** The two remaining ties to WSGM are cut: the machinery
  writes through `ISteamUiLog`, which the host installs, and the bridge takes the script it injects
  rather than reaching for `SteamUiAssetCatalog`. `SteamUiToolkitBoundaryTests` reads the thirteen
  files being lifted and fails on either coming back — both would compile cleanly and, because the
  sink keeps lines landing in the same file, would look correct too.
- **7, first half — the extension host.** `SteamUiExtensionHost` discovers packages, validates
  them, and reports every one it refused with a reason. JavaScript extensions only in this version:
  they can already add, hide and reorganize through the three APIs, and in-process assembly loading
  is a separate decision. A script is confined to its package; every patch must be prefixed with
  the extension's own id.

- **6, second half — extracted.** `KillerPixelCrew/steam-ui-toolkit`, MIT, public, pinned at
  `external\steam-ui-toolkit`, with its own CI running the claim check against the emitted prelude.
  The composed asset rebuilds to the identical hash it had before the split.

  Compiling the prelude alone is what found the last coupling: `bridge.ts` would not build without
  the gates, because it constructed each by name and published it under a fixed property. Gates
  register themselves now, and the prelude build fails if that returns. Wiring WSGM back on also
  moved six types from `internal` to `public` — including the four transport seams a consumer needs
  to test against a fake wire — each of which the documentation gate caught as CS1591 first.

**Remaining.**

- **The Extensions tab.** Deferred: the host is built and tested, the surface is not. Picking it up
  means mounting a tab, rendering one row per loaded extension, and showing the refusals
  `SteamUiExtensionHost` already reports — it returns rejected extensions with their reason
  precisely so a tab can say why one is not there.
- **Whether extensions may carry a .NET backend.** Deliberately unanswered. A JavaScript extension
  already reaches the three APIs; adding in-process assembly loading is a separate decision with
  its own consequences, and it should not arrive as a side effect of building the tab.
- **The attended device pass.** Every asset change in steps 1-6 is proven by construction and by
  the automated gate — that the asset compiles, hashes, round-trips, and that its ownership claims
  behave. Whether the QAM renders is a device question.

## The work, in dependency order

**1. Make a module one declaration.**

```csharp
public interface ISteamUiModule
{
    string Id { get; }
    SteamUiModuleAssets Assets { get; }                    // its TS fragment + declared prelude need
    IReadOnlyList<ISteamUiPatch> Patches { get; }
    IReadOnlyList<SteamUiStatePublication> Publications { get; }
    IReadOnlyList<SteamUiCommandHandler> Commands { get; }
}
```

`StatePublication` and `SemanticCommandKey` are private records inside `SteamUiSessionHost` today.
They become public toolkit types — they *are* the module contract. Worth doing whether or not the
extraction happens: it removes the five-places problem from WSGM too.

**2. Name the three APIs and the element framework.** Today each gate re-derives its approach in
TypeScript and the taxonomy lives in `docs/steam-cef.md` rather than in code. Give each a named
surface with its traps enforced, then port the existing gates onto them one at a time. A gate that
will not express itself through one of the three is the signal that a fourth is real — not licence to
reach past them.

**3. Parameterize the bridge identity.** `__wsgmSteamUi_v1_28d7c54a` is a literal in nine files. It
becomes `SteamUiBridgeIdentity(Namespace, Version)`, supplied once by the host, along with the
`wsgm-` DOM marker classes and `__wsgm` globals. A framework with WSGM's namespace compiled in is not
a framework.

**4. Invert the asset pipeline.** `eng/build-steam-assets.mjs` holds a hardcoded ordered list of nine
fragments compiled into one asset with one drift hash. Instead: the toolkit ships the prelude
(`types.ts` + `bridge.ts`) as its own hashed asset; each module ships its own individually-hashed
fragment; the host composes them in declared-dependency order, and the injected script's identity is
the ordered composition of those hashes. This preserves all three properties the current design
exists for — the artifact stays reviewable JavaScript rather than a bundle, the drift gate still
fails on an uncompiled or hand edit, and `bridge.ts`'s re-injection check keeps working because the
composition hash changes exactly when any part does.

**5. Split `SteamUiSessionHost`** (1,812 lines, the largest entanglement). To the toolkit: transport
ownership, registration and synchronization, the publication pump and debounce, command routing, and
the universal refusal log (`Steam UI request {id}/{command} did nothing: …`) — verbatim, because it
is how a no-op is diagnosed from a pasted log. Stays in WSGM: the module declarations and the service
wiring behind them.

**6. Extract.** `filter-repo` with history, MIT, own CI. A **submodule**, not a pinned release: it is
a library WSGM compiles against with no second copy of anything, so the Device Lab / plugin two-SDK
conflict does not arise.

**7. The extension host and Extensions tab.** Last, and only once a module is one declaration — an
extension host built earlier would harden the five-places problem into a public contract that
third parties depend on. See the Extensions section above.

## Risks, stated plainly

- **This subsystem holds the most live-verified behaviour in the repository**, most of it
  re-derivable only from another attended session on the Claw against a running client.
- **Unattended tests cannot prove it.** They cover transport, bridge and session state. Whether the
  QAM still renders is a device question — so each step needs an attended pass before the next.
  Batching them and testing once would make a regression impossible to attribute.
- **Steam moves underneath.** Every module id, token and class name is coupled to a Steam build.
  Already true; the framework inherits the duty of saying so honestly to its consumers, and its
  probe-first design is what makes a Steam update degrade to Valve behaviour instead of breaking.

## Sequencing

Steps 1-5 are WSGM-internal refactors that stand on their own merit; step 6 is the extraction. **If
the toolkit never ships, 1-5 still leave WSGM better** — the property to protect. Do not let the
extraction goal justify a change that does not improve WSGM on its own.

Suggested: **1 → attended → 2 → attended → 3 + 4 → attended → 5 → attended → 6 → 7.**

Step 7 (extensions) is the point of the whole exercise from a user's perspective, and the most
tempting to start early. Starting it before step 1 would publish the current five-places module shape
as a third-party contract, and that is not a mistake this project could take back.

Nothing is started. `Q16` (the CEF simplification pass) overlaps step 1 substantially and should be
folded into it rather than run separately.
