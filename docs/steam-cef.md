# Driving Steam through its CEF front-end

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

## Steam Input handheld-glyph selector

**Live-verified 2026-08-28 against the local Windows Steam client.** The controller selector is
owned by `SharedJSContext`, not by store/community pages or desktop Chromium targets. Current Steam
has exactly one positive structural match for each approved configuration summary, layout editor,
controller settings, controller input-test, binding-glyph URL builder, menu-prompt glyph selector,
semantic prompt map, and controller-image container. The inline controller-visualization shape is
pinned by its sanitized shape evidence (length 14, SHA-256
`52b961386cb4a9cb53cc2eb7baff0251ec7f8b7513efb035262c85bf71fb8d84`) in addition to its unique
component structure; a module id or class fragment alone is not compatibility evidence.

The P8.6 selector deliberately delivers no assets. Its exact route-kind gate also requires the
resolved subject `handheld`; external and unresolved controller subjects retain native Steam glyphs.
The live lifecycle probe left SharedJSContext head/body counts unchanged, created no owned nodes or
asset references, was absent from every store, community, supernav, menu, and desktop Steam target,
and removed its only namespace cleanly. Asset mapping and route-specific presentation remain
separate later patch tiers.

## Steam Input glyph mapping tiers

**Live-probed 2026-08-28 against the local Windows Steam client.** Stable resource mapping,
structural controller images, inline Valve SVG matching, and capability hiding have separate patch
IDs, resources, namespaces, probes, verification, removal, health, and kill switches. The stable
probe found exactly one binding-glyph builder, one menu-prompt selector, and one semantic prompt
map; the semantic map exposed 246 sanitized `/steaminputglyphs/...` resource basenames. The
structural probe again found exactly one layout-editor shape and one controller-image container. The
candidate cross-route capability-control set did not produce a unique positive result, so capability
hiding remains disabled. The one known inline shape still has no audited catalog mapping from its
Valve path hash to reviewed device artwork, so that tier also remains disabled.

### Physical glyphs are CSS, exactly like CSSLoader's Handheld Controller Glyphs

**Settled 2026-08-29 against the reference theme, checked out at `_ref/handheld-controller-glyphs`
(victor-borges/handheld-controller-glyphs), and the loader itself at `_ref/SDH-CssLoader`.** That
theme already does the whole job correctly on Decky and CSSLoader-Desktop, it covers the MSI Claw,
and WSGM matches its mechanism rather than inventing one.

Both remaining "tiers" are **presentation overrides written in CSS**. Nothing patches Steam's data
model, and nothing needs to:

- **Glyph replacement is `content:` on the image.** Valve renders each glyph as
  `<img src="/steaminputglyphs/<name>.svg">`, and the theme overrides it with
  `img[src="/steaminputglyphs/shared_color_button_a.svg"] { content: url(<asset>); }`. The Valve
  basename is the stable key; several Valve names map onto one physical control (`ps_button_x`,
  `shared_button_a`, and `shared_color_button_a` are all the south face button).
- **Inline Valve SVG is the same idea for the glyphs Valve draws as inline `<svg><path d="…">`
  instead of an `<img>`** — the Steam logo in particular. The theme matches
  `:has(svg path[d="M21.8011 11.5C…"])`, hides the inner `svg`, and paints the replacement as a
  `background` on the container. So this tier is real and needed; it is keyed by the `d` attribute,
  not by a webpack factory.
- **Capability hiding is `display: none` on the row.** For a control the handheld does not have, the
  theme hides the row that carries that control's glyph, for example
  `._2mL2HfT5AkDXRi1YBnRWKa:has(> div > img[src="/steaminputglyphs/shared_gyro.svg"])`, plus the
  long structural selectors for the configurator and layout screens. Every hiding rule is wrapped in
  `@container style(--hiding-enabled: 1)` against an `@property --hiding-enabled` declaration, which
  is how the theme makes hiding switchable without shipping two stylesheets.

The device-specific half of the theme is nothing but custom properties. `themes/msi/claw.css` is
seventeen lines that point `--controller-image`, `--button-guide-image`, `--button-l4-image`,
`--button-r4-image` and friends at assets; every selector lives in the shared stylesheets. That is
the shape WSGM's plugin-owned glyph package should produce: assets plus a control map, with the
selectors owned by WSGM.

Injection is equally plain, and worth copying exactly because it is what makes coexistence work.
CSSLoader attaches to CDP tabs matched by title/URL/`documentElement` class, appends
`<style id="…" class="css-loader-style">` to `document.head`, and removes by id or by that class
(`_ref/SDH-CssLoader/css_browserhook.py`). WSGM does the same with its **own** marker class
`wsgm-glyph-style`, never touches a `.css-loader-style` node, and removes only its own — a user
running both is the normal case, not an edge case.

**WSGM's implementation.** `Core/SteamGlyphCss.cs` emits the stylesheet and
`Core/SteamInputGlyphStylePatch.cs` installs it as the single owned `<style>` element. The ownership
split is the whole design and must not blur: WSGM owns the method — the Valve resource names, the
selectors, the stylesheet shape, and the injection — while the device plugin owns the glyphs. Every
image in the sheet comes from the active plugin's imported profile as a hash-checked data URI, and a
plugin never supplies a selector, a URL, or stylesheet text. WSGM ships no handheld artwork and no
per-device stylesheet, because maintaining either would put it back in the business of tracking
hardware it does not own.

Two of WSGM's selectors are Steam-generated class names (the inline-logo container and the
configurator control row) and are therefore coupled to a Steam build, exactly as the reference theme
is. The patch probe checks both before installing anything, so a Steam rebuild that renames them
disables glyph delivery and keeps native Valve rendering instead of installing rules that match
nothing.

**Live-verified 2026-08-29 on the reference Claw against the running client
(Chrome/126.0.6478.183).** The full install/verify/remove cycle was exercised with a stylesheet in
the exact shape the emitter produces:

- Both build-coupled classes are present in this build — `_2mL2HfT5AkDXRi1YBnRWKa` in 8 rules and
  `_3Jfd85nK4bKoNf_gCSTX6U` in 1 — found by scanning `document.styleSheets`.
- All five rules parsed, none silently dropped: both `:has()` selectors carrying the full Steam-logo
  `d` attribute, the grouped `img[src=…]` overrides, the `:root` block, and the row-hiding rule.
- The controller-image custom property resolved to its data URI through `getComputedStyle`.
- Removal left no owned node behind and touched no `.css-loader-style` node.

**Probe the stylesheets, not the DOM.** At the moment of the check the classes matched **zero live
elements** and there were **zero** `/steaminputglyphs/` images on screen, because those nodes exist
only while a controller settings or configurator view is open. A compatibility probe that looked for
live elements would therefore report the patch incompatible whenever the user happens not to be on
that screen, which is almost always. WSGM's probe reads the parsed CSS rules instead, which is why
it gives the same answer regardless of what the user is looking at.

What this does not yet cover is visual acceptance: correct artwork, orientation, and scale with a
real plugin profile on a controller settings screen. That remains the attended item.

The tier payload builder accepts only already-imported reviewed profiles, resolves Valve resource
names through WSGM's compiled semantic map, and re-emits only the importer's hash-checked SVG/PNG
bytes as bounded data references. It never accepts a plugin path, URL, stylesheet, selector, or
script. Each installed tier is a route/subject-gated mapping namespace only: it owns no DOM node and
performs no Steam UI mutation until the later route-specific delivery work has its own positive
result verification. Current production registration is deliberately fail-closed because the tree
contains no reviewed plugin-owned profile/assets or accepted A2VM glyph profile. All four tier kill
switches therefore initialize disabled and native Valve rendering remains unchanged. The live probe
was read-only; final cleanup confirmed the selector and all four tier namespaces were absent.

8. **Adding a library to a RUNNING Steam goes through Steam's own front-end, never its internals.**
   The shell's `PersistentSteamUiTransport` drives Steam's CEF remote-debugging port
   (localhost:8080) → WebSocket `Runtime.evaluate` →
   `SteamClient.InstallFolder.AddInstallFolder("<path>")`, so Steam adds, persists, mounts and scans
   on its own thread with no restart. Repository-owned one-shot operations borrow that same
   transport through `SteamUiTransportSession`; they cannot discover a target or open a second
   socket stack. The port only opens when Steam starts with the
   `<SteamDir>\.cef-enable-remote-debugging` flag present, so
   `SteamCef.EnsureRemoteDebuggingEnabled()` writes it before `Steam.LaunchBigPicture` cold-starts
   Steam (game mode always has the port). **Security posture of the CEF port (accepted, reviewed —
   do not "fix" without reading this).** The port is unauthenticated (Steam's CEF has no auth — a
   platform limitation, not ours) but **loopback-only** (`127.0.0.1`), and driving Steam's front-end
   is the only way to build the live-add / library-tab / artwork features; every comparable tool
   (CSSLoader-Desktop, Millennium, Decky-on-Windows) uses the same flag and port. WSGM's own
   hardening against a **local squatter**: `SteamCef.IsSteamPortOwner()` refuses port 8080 unless
   the listening PID is `steamwebhelper`/`steam` (native TCP table, loopback listener preferred over
   a wildcard one), and the returned `webSocketDebuggerUrl` is rejected unless it is `ws`/`wss` +
   host `127.0.0.1`/`localhost` + port 8080 — so a spoofed `/json/list` cannot redirect the CDP
   client (this is the answer to the Codex "unauthenticated DevTools" finding, which reviewed the
   pre-hardening commit `59fb357`; the checks landed in `4925494`). The residual is a loopback port
   any same-user process can drive — inherent, `medium`, not raised further. **Do NOT remove the
   `.cef-enable-remote-debugging` flag on uninstall (or anywhere):** it is shared Steam-wide state
   that CSSLoader-Desktop/Millennium also set and depend on, WSGM only writes it if absent and
   cannot know who created it, so deleting it would silently break a coexisting tool. This deletion
   was tried and deliberately reverted. **JSON-encode the path into the JS** (`JsonEncodedText`) — a
   raw path drops its backslashes and Steam rejects it as `NotWritableFolder`. Steam enforces one
   library per drive (`DriveAlreadyHasLibrary` = already present, not an error). Do NOT resurrect
   the in-process `CApplicationManager::AddLibraryFolder` call (removed from `steam_input_gate`):
   calling it from the injected thread clears+rebuilds the library array without Steam's lock and
   **destroys the library list** (device-verified: dropped D:/E:, persisted the loss to config).
   When Steam is closed (or the port is unreachable) `SdFormatManager` falls back to the
   `config\libraryfolders.vdf` splice, read on Steam's next start. Before a WSGM-format of a card
   that already has a library marker, WSGM reads that marker's `contentid`, removes the matching
   registered/live library first, and only then erases the disk; never identify the old library by
   its reused drive letter or path. **Steam allows SEVERAL install folders at ONE path and never
   dedupes them (live-verified 2026-08-20, and it is the cause of the "new card shows the previous
   card's games but the right capacity" report).** Steam keys install folders by PATH. A card pulled
   out of the reader leaves its registration behind — `bIsMounted:false`, still carrying its own
   `contentid`, app list and the capacity it had when last seen. `AddInstallFolder` on that same
   path does NOT adopt or replace it: it APPENDS a second entry, and `libraryfolders.vdf` is then
   written with two blocks at one path. Ejecting does not clear the phantom (it was never tied to
   the card) and `RefreshFolders()` does NOT dedupe — verified; only `RemoveInstallFolder(index)`
   drops it, and a Steam restart hides it by rebuilding the list from disk, which is why the bug
   "fixes itself" after a reboot. Two further measured facts: when a registration at the path IS
   mounted, a second add is refused with **`NotWritableFolder`** (not `DriveAlreadyHasLibrary`) even
   though the folder is writable, so that code means "already registered" here; and a registration
   stays `bIsMounted:true` with `nCapacity:0` when its folder is deleted while the volume is still
   present, so **mounted does not prove a registration is current**. The consequences are binding:
   `SteamCdp`'s add expression purges same-path registrations before adding (`replaceExisting: true`
   from the format flow purges even a mounted one, because a just-formatted card makes every prior
   registration there stale); the remove expression removes EVERY match, not the first; the relabel
   expression prefers the mounted match, because a phantom sorts FIRST and `find` would relabel it;
   the closed-Steam path calls `SteamLibraryVdf.TryRemovePath` before splicing, because dedup there
   is by content id and cannot see a registration the previous card left under its own id.
   `SteamLibraryVdf.NormalizePath` and `SteamCdp.NormalizePathJs` must stay equivalent — a mismatch
   silently skips the purge. **Card swaps are reconciled on the volume notification, not by polling
   (`Shell\CardVolumeMonitor.cs`, `Core\CardLibraryDecision.cs`).** The monitor must start for BOTH
   ways game mode becomes active: the initial direct/service boot and a later desktop-to-game
   transition. Initial boot does not raise `SessionModes.GameModeEntered`; relying on that event
   alone left the monitor absent for the whole boot session (device log, 2026-08-22: Safe Eject
   succeeded with no card-volume notification or reconcile). The signal is a
   `RegisterDeviceNotification` subscription to **`GUID_DEVINTERFACE_VOLUME`** on the process
   message-only window. It must be that and not the broadcast `DBT_DEVTYP_VOLUME` message, which
   Windows sends only to TOP-LEVEL windows and which a `HWND_MESSAGE` window therefore never
   receives. It must also not be WMI (`Win32_DiskDrive` + a model string): a model match only works
   for the one reader it was written against and does not provide the volume arrival identity this
   reconciliation needs. The notification arrives BEFORE the volume is mounted and lettered, so the
   reaction settles 3 s and rescans all drives rather than resolving the reported device path. The
   decision is `cardContentId` (from the card's own marker, the identity that travels with the card)
   against the ids registered for that path in `libraryfolders.vdf` — Steam's live folder API
   exposes no content id at all, which is why the file is the source. Gated on the CEF master switch
   and off in `--overlay-test`. **A running Steam process and a reachable SharedJSContext are NOT
   proof that a cold-start UI is ready for autonomous mutation** (device-observed 2026-08-22). On
   failed boot PID 12064, the input proxy had completely initialized in 2 ms with zero fallback
   calls, CEF accepted the download-sort injection, and the card monitor began replacing
   `D:\SteamLibrary` before a Big Picture window existed; that boot never produced the window.
   Manual Steam starts with the same proxy produced it. `SteamUiReadiness` therefore gates automatic
   card reconciliation, tab and card-manifest sync, and download-state polling on the process-owned
   `SDL_app` Big Picture window — and, since 2026-09-01, **the transport itself is closed** whenever
   game mode has no Big Picture window: `ShellSession` runs a one-second gate loop that keeps
   `SteamUiTransportSession.SetEnabled` equal to
   `SteamUiReadiness.TransportShouldBeOpen(cefMaster, inGameMode, bigPictureVisible)`, re-checked on
   every mode change and Steam lifecycle edge and always under the master-switch gate. That flag is
   the one choke point the patch host, the running-application probe and every static evaluator
   share, so nothing WSGM does can reach a cold-starting Steam's port before its window exists. **A
   SharedJSContext generation is NOT a readiness signal** (device-observed 2026-09-01): the 2.0
   patch host applied on the first `GenerationChanged`, and on a desktop-to-game transition that
   cold-started Steam (PID 6500, 19:14:22.028) it had the download-sort patch and the
   running-application probe on CEF at +2.9 s and the native-QAM bootstrap plus eighteen more
   patches Applied/Verified by +4 s; no `SDL_app` window ever appeared and Steam had to be ended
   from Task Manager. The one cold boot in the same log that succeeded (2026-08-31 15:53) had
   connected 80 ms AFTER `Big Picture window detected` — the race, won. Card-volume notification and
   scanning still start immediately so an already-present card and removals are not missed, but live
   Add/Remove is deferred and retried until the window exists. Desktop download polling and
   manual/overlay-driven operations remain immediate because they are not acting on a half-built
   game-mode session. `SteamCollections` remains only as the read/filter bridge and one-time cleanup
   for collection IDs created by pre-injection builds. New tabs never create collections. CEF
   unreachability must save the desired configuration but fail open with a retryable warning; it
   must not replace the last successfully injected definitions.

   **`nFolderIndex` is a STABLE ID, not an array position (live-measured 2026-08-23 against this
   machine's Steam).** Removing an install folder does not renumber the ones after it: removing
   index 2 of `[0,1,2,3]` left `0,1,3`, `libraryfolders.vdf` persisted the non-contiguous keys, and
   removing an index that is already gone is a harmless no-op. Steam's own store agrees —
   `steamui/chunk~2dcc5aaf7.js` has
   `GetInstallFolder(e){return this.m_InstallFolders.find(t=>t.nFolderIndex==e)}` while exposing
   array position separately as `findIndex`. `SteamCdp`'s purge and remove loops therefore stay as
   written: iterate ONE `GetInstallFolders()` snapshot and remove each match in order. No descending
   sort, and no re-fetch between removals — the index-shift concern that suggests them was measured
   and disproven.

9. **Custom filter tabs are INJECTED into Steam's tab strip — not collections (device-verified).**
   Collections render under the "Collections" tab, never as top-strip tabs; that was the wrong model
   and is fully removed. `Core\SteamLibraryTabs.cs` injects a resident script into `SharedJSContext`
   that replicates TabMaster without Decky: push a chunk to **`window.webpackChunksteamui`** to
   capture `__webpack_require__`, iterate `req.m` to `findModule` React (module with
   `createElement`+`useMemo`+`version`) **loading each candidate via `req(id)` — the captured
   require's `req.c` cache is EMPTY (live-verified), so a cache-only exports scan can never find
   React; a review once made that "safer" swap and broke all tab injection until the next device
   test** — then **hijack the current dispatcher slot**
   `React.__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE.H` so every `useMemo`
   result is passed through `patchTabs`, which rewrites the library tab array (found by a tab with
   `id==='AllGames'`) to append WSGM tabs. Each tab is a **fake in-memory collection** (a plain
   object of app overviews) rendered by Steam's own grid (found by the `Library_FilteredByHeader`
   source marker) — no real Steam collection is ever created. WSGM only supplies
   `window.__wsgm.tabs = [{id,title,appids}]`, plus `tabOrder` (full strip order as tab keys —
   native ids like `AllGames` mixed with `wsgm-…` ids; unlisted tabs keep natural order after the
   listed ones) and `hiddenTabs` (native ids to omit — hiding IS omission from the returned array,
   exactly TabMaster's model, and the tab reappears untouched when unhidden). `patchTabs` also
   records `W.nativeTabs` (id+title of Steam's own tabs) which the sync persists into
   `AppConfig.KnownNativeTabs` so the order UI shows real localized titles; app-ids come from
   `Core\LibraryFilter.cs` (a persisted `FilterNode` tree → **pure JS predicate** over `appStore`,
   unit-tested in `LibraryFilterTests` — keep it Steam-free; SD-card membership is baked in from
   WSGM's own card model). Card tabs and genre tabs use the same injection. It is **reactive**:
   `LibraryTabManager.SyncAllAsync` re-injects after every builder change (no manual "sync" button);
   interactive reordering uses the cheap `SteamLibraryTabs.PushOrderAsync` (order + hidden set only,
   no filter re-evaluation), debounced from the Tab Order UI. The boot sync waits for Big Picture
   plus `webpackChunksteamui`, `collectionStore`, and `appStore`. A reachable but failed filter
   evaluation retries the FULL tab sync even if the independent visible-page badge push succeeded;
   treating that badge success as completion was why custom tabs only appeared after opening WSGM's
   sidebar and triggering a later runtime sync (device-observed 2026-08-22). The two things that
   shift on a major Steam UI update — the dispatcher slot name and the `Library_FilteredByHeader`
   marker — are the accepted fragility (kill switch `window.__wsgm.disableTabs()`; a Steam restart
   also recovers). The builder UI is `Overlay\LibraryTabsView.cs` (self-drawing sub-view like
   `PanelFormat`; extend `AnySubView`). Prototype any change against live Steam via
   `tools/WsgmLibTest` (`run-file.mjs tabs-prod.js`) BEFORE editing the C#.

10. **Steam-page bridge (the VISIBLE window, not SharedJSContext).** `Core\SteamPageBridge.cs` reads
    the current game and injects the "On: <card>" badge into the **visible** Big-Picture/library
    window (`SteamUiTransportSession.EvaluateOnVisibleWindowAsync`) — SharedJSContext is HEADLESS
    (empty DOM, no images), it only holds the stores/React. The visible window is selected by shape,
    not localized title (a `page` whose url has `createflags` and lacks
    `openerid`/`browserviewpopup`). Current game = the appid of the **largest WIDE visible**
    `assets/<appid>/...` image (the hero banner) — device-verified robust across art naming (some
    games serve `library_hero`, others a hashed `assets/<id>/<hash>`; both put the appid in the
    path). Match by `width>=600 && width>height` so the portrait grid capsules are skipped and the
    badge CLEARS when leaving a game. NEVER match the `library_hero` filename alone — many games
    don't use it. The badge is a resident `MutationObserver` + fixed-position pill. `CurrentAppIdJs`
    resolves to **`{id,src}`**, not a bare number: it is ONE source string shared by the C# reader
    and the resident badge (so the center/visibility rules cannot drift between them), and `src`
    names the signal that matched — `focus` (the focused element's React fiber, tried first) or
    `hero image`. The badge's `curId()` unwraps `.id`. `Log` prints the signal, so a detection that
    silently shifts from one signal to the other is visible in a pasted `wsgm.log` instead of hiding
    behind a generic label. Bump `BadgeScriptVersion` whenever the resident script text changes, and
    re-probe both branches against a live Steam (`tools/WsgmLibTest`) before shipping a change here.
    Artwork apply (SteamGridDB feature) is the robust `SharedJSContext` API
    `SteamClient.Apps.Clear/SetCustomArtworkForApp(appid, base64, ext, assetType)` (grid=0/hero=1/
    logo=2/wide=3/icon=4; clear→~500ms→set; icons alone need FS writes) — data on SharedJSContext,
    DOM on the visible window, always. **Header Wi-Fi indicator (the registered network gate,
    live-verified):** Big Picture's header Wi-Fi icon is empty on Windows because Steam's backend
    sends device reports with an empty `wireless.aps` list, so `SystemNetworkStore`
    (SharedJSContext) never sees a connected access point. WSGM injects a synthetic AP (real SSID +
    signal from `WindowsRadio.GetWifiStatus`) through the store's own `SetDeviceInfo` ingestion
    (plain protobuf-toObject shape; estate 5=Connected, estrength 0-4 = filled arcs). Residency: do
    NOT wrap `OnNetworkDevicesChanged` — the backend holds the bound callback registered at init and
    a property wrap never fires (verified); instead the synthetic AP instance gets a no-op
    `MarkAsNotPresent`, which pins it across the backend's periodic reports. Removal = delete the
    map entry + `SteamClient.System.Network.ForceRefresh()`; disabled on desktop transitions like
    tabs/badge. **CSSLoader-Desktop coexistence (device- + source-verified):** Steam's CEF allows
    concurrent CDP clients, and CSSLoader only appends/removes `<style>` in `document.head`.
    Namespace everything under `window.__wsgm`, give injected nodes a unique `wsgm-badge` class
    (never `css-loader-style`, which CSSLoader bulk-removes), never touch `document.head`, and never
    disable the debug flag or port.

11. **Writing a game's launch configuration (`Core\SteamLaunchConfig.cs`, live-probed 2026-08-12).**
    The Tools tab's per-game launch fixes configure the RUNNING Steam client over SharedJSContext
    instead of copying a command for the user to paste; with `Cef.Enabled` off they fall back to the
    clipboard. Two APIs, because Steam treats the two kinds of entry differently: a real title takes
    `SteamClient.Apps.SetAppLaunchOptions(appid, str)`; a non-Steam shortcut takes
    `SetShortcutExe` + `SetShortcutLaunchOptions` (invariant 5d — a shortcut ignores an
    exe-replacement launch option). **Steam stores every one of these values VERBATIM** — it neither
    adds nor strips quotes and does not touch backslashes — and its own shortcut `Exe` is stored
    _quoted_ with single backslashes (`"C:\Games\…\game.exe"`), so WSGM supplies the quotes itself.
    Never use decky's `JSON.stringify(path)` form: it doubles backslashes and is only correct on
    Linux. Reads go through `RegisterForAppDetails` wrapped in a promise with a timeout and
    `unregister()` on both paths (it is a subscription, not a getter, and it re-fires after a
    write); `GetLaunchOptionsForApp` is the launch-_menu_ list, not the options string. Writes
    persist to `shortcuts.vdf`/`localconfig.vdf` immediately — **no Steam restart, and never
    hand-write those files**. `StartDir` is deliberately never written (the game's folder stays the
    CWD). A real title's **existing launch options are composed, never replaced** (`%command%`
    expands to the game's own command, so options the wrapper value overwrote would silently stop
    applying): plain options move after the placeholder, a user value that positions `%command%`
    itself keeps its prefix and suffix, and re-applying reads them back out with
    `LaunchWrapperCommand.OriginalLaunchOptions`. `%command%` is **real titles only** — a non-Steam
    shortcut ignores it (see 5d). Because configuring a shortcut destroys its original Target, the
    pre-change values are snapshotted into `AppConfig.LaunchWrappers` BEFORE the write — via
    `SteamLaunchConfig.OriginalsFrom`, which UNWRAPS an already-wrapped game (the command may have
    been pasted by hand, or the config reset) so the snapshot never records WSGM's own wrapper as
    the "original" and Remove cannot restore the wrapper itself — and re-applying an already-wrapped
    game keeps the first snapshot rather than recording WSGM's own values.

    A user prefix ahead of `%command%` keeps running at Steam's own integrity level, in front of the
    wrapper, and that is ACCEPTED: it runs there whether or not WSGM applies a fix, and applying one
    strictly REDUCES the elevated surface by moving the game itself to medium. The prefix is
    therefore never stripped, reordered, escaped or refused — doing so would revert 0751f86 and
    break `-dx11`/`-nolauncher` and profiler/RTSS shims. It is only reported:
    `LaunchWrapperCommand.PreservedPrefix` strips control characters and caps the length for a
    single `Log.Info` emitted BEFORE the write (`Log` interpolates its message raw, and a
    launch-option value is user text), and the string handed to `SetAppLaunchOptions` is
    byte-identical either way.

    The Tools tab's custom launch action is deliberately different from those fixes: it uses no WSGM
    wrapper and replaces the active launch fields with Steam-native syntax. A real title gets
    `"selected.exe" [arguments] %command%`; CMD/BAT and PS1 selections prefix that placeholder with
    an explicit `cmd.exe` or Windows PowerShell invocation. A non-Steam shortcut gets the selected
    EXE (or script host) in `Exe` and only the script plus custom arguments in Launch Arguments —
    `%command%` is never written there. The first pre-change snapshot is retained across edits so
    Restore returns every field verbatim.

12. **Download-queue sorting (`Core\SteamDownloadSort.cs`, live-verified 2026-08-12).**
    Name/Size/Type sort buttons injected into the header of Big Picture's "Up Next" download
    section, reordering the queue through Steam's own
    `SteamClient.Downloads.SetQueueIndex(appid, index, remoteClientId)`. Three findings are
    load-bearing and were each a real failure first: (a) **The buttons must be built from Steam's
    own `Focusable` component.** A plain DOM injection renders and clicks fine but is invisible to
    Big Picture's gamepad focus tree — device-confirmed ("its not navigateable with controller").
    With `Focusable` the controller reaches them and the footer shows the select hint. (b) **The
    injection point is the JSX runtime**, not the component. The section header rebuilds its own
    `children` array after spreading rest props, so it can only be WRAPPED; and the download-list
    section is a **MobX observer** whose `render` is a NON-configurable, NON-writable own property
    on every instance, so it cannot be patched, deleted, or shadowed by a prototype accessor.
    Wrapping `jsx`/`jsxs` and intercepting the header element at creation is what is left; the
    hot-path cost is one reference comparison. Some runtime modules re-export the same binding, so a
    wrapper must be skipped if it already carries the guard property — wrapping a wrapper renders
    the bar twice. (c) **The `Focusable` lookup must stay tight.** Matching "flow-children" +
    "onActivate" also hits three chat/friends CLASS components and the registry hands a text-area
    component back FIRST — which rendered a textbox into the download header. Require a plain
    function under 1500 chars that destructures the quoted `"flow-children"` key together with
    `onActivate:` / `focusClassName` / `focusWithinClassName`; that leaves exactly one match. Note
    webpack's ES exports are ACCESSOR properties — a value-only scan
    (`getOwnPropertyDescriptor(...).value`) finds neither React nor `Focusable`, which is also why
    they cannot be located the way a plain object's members would be. **Scope is the ENTIRE pending
    list** (maintainer-directed): `QueuedTransfers` + `UnqueuedTransfers`

- `ScheduledTransfers`, minus completed, renumbered from index 0. Index 0 is included — the item
  Steam is currently working on is part of the queue, and excluding it made a sort look broken;
  moving another app to index 0 only switches which one Steam works on, and per-app progress is
  retained. Including the scheduled entries **queues them** (their `queue_index` is -1 until a sort
  gives them one), which is exactly what dragging them into the queue does in Steam's own UI — so a
  sort empties the "Scheduled" section. **That is the point, not a side effect** (maintainer, on
  being offered the schedule-preserving alternative): when Wi-Fi drops mid-download Steam kicks the
  whole queue out to unqueued/scheduled, and one tap on a sort button is how fifty entries go back
  in. Do NOT "fix" this into sorting each section separately or preserving `deferred_time` — a
  reviewer re-raising it should be answered "deliberate, it is the bulk re-queue path". Never seed
  the renumbering from `items[0].queue_index`: with unqueued entries in the list that can be -1. The
  apply loop is one `SetQueueIndex` per item at 120 ms, so a fifty-entry re-queue takes ~6 s with
  the buttons dimmed; that pacing is deliberate and the list is deliberately not capped. **SIZE
  means bytes LEFT to download** (`bytes_total - bytes_in_progress`), not the total — the queue is
  about what is still coming down the wire. A freshly restarted client reports `bytes_total == 0`
  for queued-but-not-yet-planned apps; that is "unknown", NOT "smallest", and ranking it as zero is
  what made the first tap look like it did nothing while the second (reversed) tap looked correct —
  the reported "only works on the second tap" bug. Unknown-size items are parked at the END in BOTH
  directions, which is why each comparator takes the direction as an argument instead of the caller
  flipping the sign. WSGM never calls `EnableAllDownloads`, but a sort still **resumes a paused
  queue** (live-verified: paused → `Downloading`, even when the resulting order is unchanged)
  because Steam reacts to a `SetQueueIndex` at the head. That is accepted — it is what dragging an
  item to the top does in Steam's own UI — and must not be "fixed" by re-pausing afterwards.
  Displayed size is Steam's own formula: the sum of
  `progress[k_EAppUpdateProgress_Download].bytes_total` across every content type; taking the max
  over the progress array yields numbers that do not match the rows. `buildid == 0` = Install,
  otherwise Update. The queued section is identified by the locale-independent
  `#Downloads_Section_Current` title token plus a `count`+`labelId` shape check. Re-probe against a
  live Steam (`tools/WsgmLibTest/run-prod-sort.mjs`, which extracts the script verbatim from the C#)
  before shipping a change here.

**Turning a CEF feature off must RETRACT, not just stop pushing.** The injected tabs, badges,
synthetic Wi-Fi AP and download-sort buttons are resident in Steam's CEF session and survive until
Steam restarts. The master switch fails every evaluation closed — including WSGM's own removal calls
— so `ShellSession` awaits removal before closing the choke point. Wi-Fi is no longer a standalone
resident: the registered network gate owns its availability override, scan observation, store feed,
verification and cleanup as one generation-aware resource. Download sorting likewise uses the
SharedJSContext patch lifecycle around Steam's JSX runtime rather than a private readiness loop and
sentinel. The remaining legacy residents — tabs and the page badge — retain explicit removal until
their individual attended migrations land.

## Persistent Steam UI host and native Quick Access

WSGM owns exactly one CEF transport. `PersistentSteamUiTransport` is the sole target-discovery and
CDP-socket owner. `SteamCef` now owns only the remote-debugging opt-in and pure endpoint/JavaScript
validation helpers; it has no evaluation surface. Repository-owned one-shot callers use the
session-attached transport through the internal `SteamUiTransportSession`, so a settings reload can
close the same choke point without constructing a parallel connection stack. The transport reuses
the same loopback listener ownership, target URL/origin and Steam-process validation and does not
open a local listener. Connections are reference-counted by allowlisted target role and carry
browser, target, session, frame, execution-context and document generations. Runtime, Page and DOM
domains are enabled before a generation is announced ready, so in-place MainWindow replacement is
observable. Releasing the last lease invalidates that ownership generation; a concurrent replacement
subscriber cannot adopt the cancelled connection attempt. The CEF master also gates direct transport
consumers, including the running-application observer, while foreground executable observation
remains available.

The injected native-Quick-Access asset is authored as ordered TypeScript source fragments:
`types.ts`, `bridge.ts`, one file per gate, and `components.ts`. The asset builder concatenates the
fragments into one lexical scope before compiling because Steam receives one self-contained script;
this removes the 3,160-line editing surface without adding a runtime module loader or changing one
byte of the generated asset. The bridge owns the single webpack-runtime resolver and action
generation allocator, while the component file owns the visible row table and order.

`SteamUiSessionHost` is the sole state/publication and semantic-request owner. Its state projections
are a publication table, and its `(patchId, command)` dispatch is a handler table with payload
readers named for each wire shape. This keeps every refusal on the existing host-side diagnostic
path and makes patch coverage auditable without another orchestration layer or more files.

Controller-target ids are the exact projected `ManagedControllerTarget` names (`SteamDeckComposite`,
`Xbox360`, `DualShock4`) from state through the Valve dropdown and back to the host. The payload
boundary accepts ASCII uppercase for those PascalCase ids while retaining its length, character and
exact-object-shape checks. A lowercase-only reader let the row render and the dropdown select
normally but rejected every valid command before controller management ran; the live log exposed
that otherwise silent boundary mismatch.

The card badge and library tabs deliberately remain on their verified legacy resident scripts for
now. A read-only named-module probe can establish that their primitives still exist, but cannot
prove resident installation, SPA survival, current-game clearing, CSSLoader coexistence, native-tab
hiding, or rollback. The tab migration also retains the documented one-release legacy rollback
during its attended soak. Moving either path without those mutation checks would trade working,
device-verified behavior for an unverified source cleanup.

`tools/WsgmLibTest/qam-harness.mjs screenshot [file.png]` captures the uniquely matched MainWindow
through that target's `Page.captureScreenshot` command. It replaces the old standalone screenshot
script and uses the same literal target shape as the rest of the harness. It does not focus,
navigate, inject into, or otherwise operate the visible client.

The production bridge receives its exact state/command vocabulary from
`SteamUiModuleSet.AllowedCommands`; the toolkit contains no WSGM patch ids. The harness reads the
bridge identity and schema from `external/steam-ui-toolkit` and reconstructs that same vocabulary
from `SteamUiSessionHost`'s declarations because it does not run the managed host. This matters
after the repository split: the first device-controls harness pass rebuilt the current vocabulary
while the production toolkit still carried its older static dictionary, so the fixture rendered even
though an installed bridge would reject the new subscription. Toolkit commit `ebdb485` removed that
drift path, and a managed host test now inspects the emitted bridge configuration for all three
device-control commands. `qam-device-controls-fixture.json` supplies the bounded charge, lighting,
speaker and microphone state used for a non-hardware live render.

`SteamUiPatchManager` is the only persistent patch scheduler. Each patch has an independent stable
ID/version, target role, resource key, bounds, positive unique fingerprint, apply, functional
verification, owned-resource removal, health, and kill switch. Conflicting resource keys serialize;
one incompatible or degraded patch does not disable another. Every target generation queues the same
synchronization path, and a SharedJSContext generation change cancels semantic commands authorized
against the replaced document. Disabling the CEF master first removes the registered patches and
bridge, then retracts the remaining legacy residents, and only then closes the evaluation choke
point.

The native-QAM bootstrap is embedded, hash-locked repository JavaScript. Live probing on 2026-08-28
found exactly one current SharedJSContext module for each of the TDP availability gate, TDP
component, performance actions, and read-only performance-profile projection. Module build IDs are
deliberately not selectors. The first patch uses that four-part structural fingerprint and installs
only a collision-resistant versioned Runtime binding/namespace; it does not globally spoof SteamOS,
Steam Deck identity, or mutate unrelated performance, storage, update, shutdown, or device gates.

The independent frame-limit and RTSS own-statistics components were live-verified against the same
Steam client on 2026-08-28. Each registered and replayed retained state independently, emitted only
its exact semantic request payload (`value` plus persistence), survived removal of its peer, and
restored React's original `useMemo` only after the final component was removed. The probe binding
answered requests locally and did not write an RTSS profile; cleanup removed the temporary bridge
namespace and binding.

The per-application header is driven by identity, not by RTSS executable discovery. A live
2026-08-31 screenshot showed Valve's complete Performance tab but a blank "Use profile from" header
while WSGM's log had already observed Steam AppID 220: the managed projection discarded Steam's
identity-only state because no executable profile was available yet. `PerformanceService` now keeps
the canonical AppID separately from its optional RTSS profile. `current_game_id` therefore carries
the AppID as soon as Steam names one game; `active_profile_game_id` matches it only when that game's
persisted profile is enabled, and `per_app.is_game_perf_profile_enabled` reports that same policy
fact. Foreground observation later supplies the executable without changing the AppID. A delta that
names an AppID other than the currently projected one is refused as stale before reset or any value
write.

**Valve's "no game" is 769, never 0 — live-read from the components 2026-09-02.** The header, the
per-game toggle's availability, and the app-name lookup all compare game ids against the Steam
client's own pseudo-app 769 (`GetAppOverviewByGameID(active_profile_game_id)` renders the name):
`active_profile_game_id == 769` is the "Default settings" branch, anything else renders "Use profile
from &lt;name&gt;". Publishing "0" for the global case made the header take the game branch and look
up game id 0 — a blank name while HL2 ran — so the projection publishes 769 wherever it used to say
"0". The setters stamp their delta's `gameid` from the current or active profile id, so a
global-profile write arrives carrying 769 and the delta reader maps it to "global" rather than
treating it as a real AppID. The toggle itself (`#QuickAccess_Tab_Perf_ToggleGameSettings`) is a
separate export from the header on the current client — the 2026-08-30 "not separately mountable"
finding no longer holds — and is mounted as its own row under the `valveProfileHeader` kind:
available when `current_game_id != 769`, checked when `current_game_id == active_profile_game_id`,
writing through `SetGameSpecificProfileEnabled`.

Charge limit and device lighting are WSGM-owned Quick Settings rows selected by SDK semantic role,
not by a Claw package id. A 2026-08-31 live probe of literal module `30519` in `chunk~2dcc5aaf7.js`
found Steam's generic HSV implementation closed over by the module, but not exported. Its exported
controller-LED wrapper calls `SteamClient.Input.PreviewControllerLEDColor`; mounting that wrapper
for device lighting would add a Steam Input side effect unrelated to the plugin capability. WSGM
therefore builds the same HSV interaction from the already-resolved Valve slider, dropdown, row and
localization primitives. Hue, saturation and value remain local while dragging and a persistent
plugin write is requested only from `onChangeComplete`. The WSGM overlay follows the same rule with
one explicit Apply action.

The live harness fixture rendered seven device rows (charge, lighting brightness, zone, preview and
three HSV controls) and the Steam CEF MCP accessibility snapshot showed both speaker and microphone
sliders carrying independent values. The harness then removed every temporary gate and disposed its
bridge; it did not execute a device capability or Core Audio write.

Injected code can request only the compiled patch/command vocabulary. The managed bridge validates
schema, patch ID, command, payload size, monotonic request/action generations, current execution
context/document, and replay before dispatch. There is no generic evaluation, filesystem, shell,
device, plugin, or privileged-operation endpoint. Unsupported semantic services fail closed. On
generation replacement or disable, pending calls are rejected and WSGM removes only its binding and
namespace. `Cef.NativeQuickAccess` is an independent kill switch under the existing CEF master.

## Valve's own surfaces are present on Windows; only their backends are absent

**Live-probed 2026-08-30 against the local Windows Steam client.** This is the finding the QAM,
Quick Settings, Internet, Bluetooth and audio work all rest on, and it is the opposite of what the
shape of the client suggests: the components are not gated off, they are wired to nothing.

The performance store is the clearest case. `window.SystemPerfStore` is one MobX observable whose
entire state is `m_msgState`, and its constructor reads:

```js
(SteamClient.System.Perf?.RegisterForDiagnosticInfoChanges(this.OnDiagnosticInfoChanged),
  SteamClient.System.Perf?.RegisterForStateChanges(this.OnStateChanged));
```

`SteamClient.System` has no `Perf` namespace on Windows, so the optional chaining no-ops, the state
stays `{}`, and every control renders `null`. Writes are identical: each setter builds a protobuf
delta and hands it to `SteamClient.System.Perf?.UpdateSettings(...)`.

**Availability is read from that same absent state, so hiding is free.** For example the VRR hook is
`[limits?.is_vrr_supported ?? false, per_app?.is_vrr_enabled ?? false, SetVRREnabled]`. Omit a
`limits` field and Valve's own wrapper renders nothing — no CSS, no patching. Two layers are needed
even so, because some hooks hardcode `available: true` (both scaling ones do) and can never be
hidden by state; the first and primary layer is simply not mounting a component at all.

### Filling a store is not enough. Every revived item has a second gate

**This is the rule that catches every revival item, and it caught the audio work.** Supplying a
backend satisfies the _data_ gate. It does not satisfy the _render_ gate above it, and the two are
independent:

- **The store may cache availability at construction.** The audio store computes
  `m_bAvailable = null != SteamClient.System.Audio` **once, in its constructor**, which already ran
  at client start when the namespace did not exist. WSGM attaches to a client that is already
  running, so defining the namespace afterwards leaves the flag false forever and the section stays
  hidden. Live-verified 2026-08-30: with the namespace installed, the singleton still reported
  `bAvailable: false`. The running store has to be written to directly — its `m_bAvailable` is
  writable and `RegisterOrUpdateDevice` is its own ingestion path, the same shape the registered
  network gate uses for the network store.
- **A component may sit behind a platform constant no data can reach.** Night mode is
  `IN_GAMESCOPE`; several performance rows are wrapped in a gamescope feature gate; the Quick
  Settings audio section is `!IN_VR && bAvailable`. A row behind a pure platform constant cannot be
  revived by filling anything, and is a hide rather than a backend.
- **A wrapper may gate on `available` passed as a prop**, which comes from the state WSGM supplies —
  that one _is_ reachable, and is why omitting a `limits` field hides a row for free.

So each item needs three answers before it is called done: what supplies its data, whether the store
caches the availability it derives from that data, and whether anything above it gates on a platform
constant. Confirming only the first produces a working backend behind a control nobody can see.

**A platform-constant gate is sometimes final.** Where the constant is read through a store getter —
`networkManagementAvailable` returning `TS.IS_STEAMOS` — that getter is on a prototype, is
configurable, and can be overridden narrowly. Where it is read through a _module export_, it may not
be: night mode's support hook is `function(){return TS.IN_GAMESCOPE}` and its export descriptor is
**non-configurable**, so there is no narrow override and the only route left is the global constant,
which D16 forbids. Measured 2026-08-30.

That is the difference between "hidden" and "unreachable", and it is worth checking before planning
a revival: a non-configurable export means the row is a hide, and the feature has to be a WSGM-owned
control rather than Valve's.

### Four gates, and the one that must never be touched

| Gate                     | Example                                   | Response                          |
| ------------------------ | ----------------------------------------- | --------------------------------- |
| Absent JS namespace      | `SteamClient.System.Perf`, `System.Audio` | Supply it                         |
| Absent RPC response      | `SteamOSService/State/Manager`            | Supply it                         |
| RPC stub with no backend | `BluetoothManagerService`                 | Replace the stub's methods        |
| Deck-only store getter   | `networkManagementAvailable`              | Override that one getter          |
| Global platform constant | `TS.IS_STEAMOS`                           | **Never** — this is the D16 spoof |

The distinction is concrete, not academic: `networkManagementAvailable` is literally
`get networkManagementAvailable(){return TS.IS_STEAMOS}`, so overriding the getter and setting the
constant produce the same Wi-Fi row, while one touches a single store and the other changes
unrelated client behaviour everywhere.

### Three performance backend families, only one of which is reachable

Steam carries three generations of performance control and they are easy to confuse:

- **The perf store** (`CMsgSystemPerfState`, modules `74514`/`83571`) — per-app profiles,
  `fps_limit_options` notches, frame limit, overlay level, refresh, VRR, basic/advanced, reset.
  Backed by `SteamClient.System.Perf`, which is the absent namespace above. **This is the reachable
  one.** Its message shape is
  `{limits, settings:{global, per_app}, current_game_id, active_profile_game_id}`, and per-game
  profiles are exactly `current_game_id == active_profile_game_id` plus
  `per_app.is_game_perf_profile_enabled`, with 769 — the Steam client's own pseudo-app, not 0 — as
  the "no game / default settings" value both ids compare against. WSGM supplies the first from
  canonical Steam identity and the latter two from its persisted application-policy entry; RTSS
  executable resolution is not an availability gate for the header.
- **The SteamOS Manager family** (`steamos_tdp_limit*`, `steamos_manual_gpu_clock*`) — client
  settings whose availability comes from a WebUI transport RPC. This is where TDP and charge limit
  live; there is **no** TDP component in the perf store at all.
- **The gamescope family** (`gamescope_app_target_framerate` and friends) — behind a gamescope
  feature gate and not reachable on Windows at all.

Bluetooth is its own service, `BluetoothManagerService`, and does **not** share the SteamOS Manager
seam. Its `GetState` round-trips successfully on Windows and returns
`{is_service_available:false, adapters:[], devices:[]}` — transport and message shapes present, only
the backend missing. Its `*Handler` exports are message descriptors (`{name, request, response}`),
not registration hooks, so the service cannot be implemented; the stub's methods are replaced
instead.

**The full Bluetooth settings page ships in the Windows client, and opening it was proven live on
2026-08-30** (screenshot-confirmed: Valve's own page appears in the settings sidebar). Three facts a
future search must not re-derive wrongly — two earlier theories in the same session were wrong
because they tested invented token shapes:

- The page's strings are the `#QuickAccess_Tab_Bluetooth_*` family (AddDevice, Pair, Forget,
  Searching, No_Devices_Found, ToggleLabel, ShowAllDevices, …) in module `18931`; the settings page
  reuses the QAM panel. There is no `#Settings_Bluetooth_Title` — the sidebar's tokens are not
  shaped `#Settings_X_Title` for ANY page, so absence of such a token proves nothing.
- The nav gate is `is_service_available` read through
  `useQuery({queryKey:["BluetoothManagerService","State"], staleTime: 1/0})` (module `25467`; query
  client is export `L` of `21371`). `staleTime: Infinity` means replacing the stub changes nothing
  until that key is invalidated.
- The chain that opens it — replace stub methods, publish a state with `available:true`, `onState`
  rebuilds `latest` and invalidates the key — is exactly what the bootstrap already does. The
  earlier "page missing" failures were the self-incompatibility teardown loop killing the bridge
  before a bluetooth publication ever landed, not a missing mechanism.

Wi-Fi is the one to be careful about. Steam's Windows backend does push real
`CMsgNetworkDevicesData` reports — the store's `hasWirelessDevice` and `isWifiEnabled` are genuinely
true — but every report carries an **empty `wireless.aps`**, so it never enumerates networks. Any
access point visible in a live probe may be WSGM's synthetic one from the registered network gate
and is not evidence to the contrary.

Audio is the cheapest gate in the project: the store's flag is literally
`m_bAvailable = null != SteamClient.System.Audio`, so supplying that one namespace is the whole of
it.

### A probe must name the modules it touches (incident, 2026-08-30)

**Never iterate `webpackChunksteamui`'s module registry, and never call `new` on an export you did
not identify first.** A probe written to find three nested protobuf classes did both: it walked
every id in `runtime.m`, called `runtime(id)` on each — which executes that module's factory — and
then called `new value()` on every exported function it found, looking for one whose
`getClassName()` matched. It returned `{"found":{}}`, and it restarted the developer's machine and
cost them their Steam login. `DialogConfig.vdf` was rewritten four seconds into the run,
`loginusers.vdf` at 14:30:10 and `config.vdf` at 14:31:15 — the shutdown and restart — and the
client came up unauthenticated.

The power menu is the probable path: its actions are sign-out, restart, and shut down, and a single
`SignOutAndRestart` accounts for the reboot and the lost credentials together, which two unrelated
side effects would not. The exact constructor is not known and does not need to be. The client
bundle contains power, login, transport, and storage classes whose constructors and module factories
have real side effects, and they are not written to be instantiated speculatively by a stranger.
Forcing every module in the bundle to evaluate is not a read-only operation no matter what the probe
then does with the result.

A probe is read-only only when every module it resolves is named as a literal and every value it
constructs is one whose source it has already read. `probe-perf-components.js` is the shape to copy:
it reads the named `83571` factory as text and constructs nothing. When a class cannot be reached
that way, read its factory source as a string (`String(runtime.m[id])`) and stop; do not go looking
for it by construction. The three classes this probe wanted were never found anyway, so the entire
risk bought nothing.

**Do not set `force_deck_perf_tab`.** It is Valve's own gate override
(`U(e) = e || force_deck_perf_tab`) and a persisted client setting, and it force-shows every row
including the ones WSGM cannot back.
