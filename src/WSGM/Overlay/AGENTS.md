# Overlay

Overlay owns the quick access sheet (the one focus-taking surface: pinned Quick access root,
Session / Steam / Device / Tools / Power roots with their nested pages, status pills, Open apps
strip), its radio/audio/eject panels, and their shared Steam Input lease and focus handover. The
Power root exposes explicit Standby and Hibernate actions; both dismiss the overlay before asking
Windows to suspend.

- `OverlayController` is the lifetime owner of the sheet. Acquire the Steam Input lease before
  opening it and release it only after it closes.
- The sheet is deliberately not fullscreen: the game strip left below it is the tap-outside dismiss
  target. Do not grow it to the full display without adding another dismissal path.
- Every pinnable row carries its stable id in `Tag`; keep ids stable across releases — they are
  persisted in `AppConfig.QuickAccessPins`. Device rows are pinned by capability key and are
  re-rendered from the snapshot, never mirrored.
- Settings handoff transfers named ownership: Settings registers its claim before the deferred
  overlay close removes the overlay claim. Never abandon the old owner in the blocker's owner set,
  and acknowledge the close so Settings can end its temporary deactivation exemption.
- Preserve the 150 ms deferred close and touch-synthesized mouse filtering; removing either causes
  ghost clicks on controls behind the overlay.
- Raw-touch left/right gestures always emit Steam's Ctrl+1/Ctrl+2 Big Picture shortcuts, including
  while a game is foreground; bringing Steam's menu over the game is their purpose. Top and bottom
  are WSGM's.
- Peer keyboard focus is part of the active sub-view: include its bounds in tap hit-testing, keep
  only one navigation active, and invalidate asynchronous picker loads when navigation changes.
- Artwork operations snapshot both the target app and navigation generation across awaits; bound
  thumbnail concurrency, decode thumbnails scaled, and dispose replaced bitmap trees immediately.
- Dismissal may restore focus only under the existing game-mode and suppression gates. Next-app
  cycling deliberately suppresses restoration.
- Only one `GamepadNavigation` may be enabled at a time (the sheet's stands down while a status
  panel or the keyboard window owns focus), and never rebuild switcher/tray collections wholesale
  while a gamepad focus target exists.
- Keep visual styling in `Themes\` tokens and shared controls; consumer XAML must not add literal
  colours or a second focus-adornment mechanism.
- Plugin-declared Device sections are pages addressed by route (`OverlayPage.DevicePluginSection`
  plus its section id), never new enum values; the color editor's spectrum consumes only
  Left/Right, so Up/Down always escape to the sliders below it.
