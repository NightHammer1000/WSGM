# UI layer and splash engine

This file records the UI mechanisms whose behavior depends on Avalonia layout or imported assets.
Device-verified constraints should be changed only with equivalent live verification.

## Shared UI

All styling lives in `Themes\`: `Palette.axaml`, `Typography.axaml`, `Shared.axaml`, and the control
themes. `App.axaml` only includes them. Consumer XAML uses palette tokens rather than literal
colours. The runtime accent family (`HcAccentBrush`, `HcOnAccentBrush`, and
`HcOnAccentCaptionBrush`) uses `DynamicResource`; stable tokens use `StaticResource`.

Focus uses one mechanism: `FocusAdorner={x:Null}` plus a constant two-pixel border whose brush
changes on `:focus`. Recreating Avalonia's adorner during focus movement loses it on activation
transitions. Shared controls live under `Controls\`: `TabStrip` supplies the LB/RB tab bar,
`CardButton` supplies card actions, and `Icons` supplies stroke-style `StreamGeometry`. Stroke icons
use `Fill={x:Null}` so their interior detail remains visible.

Descriptor rows keep semantic IDs independent of placement. The performance projection is rendered
both as the Device -> Profiles workflow and, for its value controls, beside Device power; the window
adds a placement-specific focus prefix when it creates each `DescriptorStatusRow`. Do not clone the
state or command logic to place the same control twice—the descriptor and its bridge remain the one
owner, while each rendered row retains a stable focus key.

Settings keeps its page controls alive and switches `IsVisible`, preserving scroll position and
recorder lifetime. The layout floor is 1280x800 for the shell and 1024x640 for Settings.

Avalonia's `Shape` scales `Stretch=Uniform` geometry and aligns it at the geometry origin rather
than centering unused space. A wide, short glyph in a square path box therefore sits at the top.
Give such paths only their dominant dimension and let the containing layout size the other axis.

## Splash engine

The splash is a customization engine over `SplashConfig`, `SplashStyle`, `SplashPresets`,
`SplashAssets`, `SplashTheme`, and `ImageHeader`. Presets prefill editable fields; rendering never
branches on the selected preset.

Imported `.wsgmsplash` files follow these contracts:

- Archive entries must be simple contained file names. Extraction enforces per-entry and aggregate
  byte budgets, and configuration paths are replaced with the files actually extracted.
- `ImageHeader` checks declared PNG, JPEG, and BMP dimensions before decode. Logo and background
  decode also have output-area budgets. WebP preview input is limited by the existing 16 MB encoded
  byte cap because `ImageHeader` does not parse WebP dimensions.
- `ConfigStore.NormalizeSplash` bounds text and colour strings and clamps numeric fields for both
  ordinary configuration load and theme import.
- Imports remain in an owned temporary directory for the Settings-window lifetime, so another window
  cannot collect an unsaved import.
- `SplashAssets` stages sidecars and promotes them only after configuration save succeeds. A failed
  promotion leaves the previous persisted path intact and keeps the picked source available for a
  retry.

Path-based image validation and decode use separate streams. Callers therefore keep both byte and
decode-size limits and handle decode failure locally; a stricter identity guarantee would require a
single open-handle decode API shared by every call site.
