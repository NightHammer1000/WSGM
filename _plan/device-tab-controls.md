# Device tab: proper control vocabulary (HC-style)

Goal: the Device overlay pages must render real controls per capability — toggle, slider,
dropdown, textbox — instead of a `DescriptorStatusRow` button for everything. Confirmed root
cause: TDP declares `Minimum 8, Maximum 37, Unit Watt` (plugin `IntegerDescriptor`) but the
renderer emits a cycle-button. The semantic model is sufficient; the RENDERER is the gap. Do NOT
give plugins Avalonia — keep the declarative boundary (AGENTS.md / docs/device-security.md).

Reference: HandheldCompanion Quick Access (`_ref/HandheldCompanion/HandheldCompanion/Views/QuickPages/`).
It uses WinUI `SettingsCard` (icon + header + description + one trailing control: ToggleSwitch /
ComboBox / Slider-with-value-label) and `SettingsExpander` (header row with a master control that
expands to reveal cards — e.g. TDP: master toggle in header, slider in the expanded card).
QuickDevicePage.xaml and QuickPerformancePage.xaml are the exemplars.

## Descriptor → control mapping (keyed off what the descriptor already carries)
- `CapabilityValueKind.Boolean` → toggle row
- `CapabilityValueKind.Integer` + Minimum/Maximum/Step/Unit → **slider row** + live value+unit label
- `CapabilityValueKind.Choice` (choices) → dropdown row
- `CapabilityValueKind.Text` + MaximumLength → textbox row (reuse OnScreenKeyboard path for gamepad)
- `CapabilityValueKind.Color` → existing swatch → `DeviceColorView` (widen for real overlay width)
- action (no value / not writable numeric) → button row (current `DescriptorStatusRow`)

## Key code facts (verified 2026-09-02)
- Projection: `src/WSGM/Shell/DeviceOverlayBridge.cs` record `DeviceOverlayCapability` (~line 55).
  Currently carries CapabilityId, InstanceId, Section, Status, Title, Description, TrailingText,
  CanInvoke, CurrentValue, NextValue, Role, PluginSectionId, CategoryId, SortOrder.
  **MISSING and must be added from the descriptor: ValueKind, Minimum, Maximum, Step, Unit,
  Choices (IReadOnlyList<string>), Writable.** Populate where the snapshot is projected from the
  coordinator descriptor set (same file; find where DeviceOverlayCapability is constructed).
- Descriptor source fields: `external/WSGM.Device.Sdk/.../Capabilities/CapabilityDescriptor.cs`
  Minimum/Maximum/Step (int?), Unit (CapabilityUnit), MaximumLength (int?); ValueKind enum in
  `Capabilities/CapabilityRole.cs` (Boolean, Integer, Choice, Text, Color, …); choices live on the
  descriptor (ChoiceDescriptor builds them — see plugin `Claw8A2VmPlugin.cs`).
- Write path: `source.InvokeAsync(capability with { NextValue = CapabilityValue.<kind>(v) }, token)`.
  Model on `DeviceColorView.ApplyAsync` (line ~306) which does `InvokeAsync(capability with { … })`.
  Bridge `InvokeAsync` (line ~737) → `_coordinator.ExecuteCapabilityAsync` (~747). Uncertain writes
  never auto-retry (per AGENTS.md capability-write invariant) — keep that.
- Current control building: `src/WSGM/Overlay/OverlayWindow.axaml.cs`
  `CreateDeviceCapabilityRow` (~line 1037) — builds `DescriptorStatusRow` for all; color special-
  cased to open `DeviceColorHost`. Callers ~531, ~608, ~2050.
- Section menu + pages: `src/WSGM/Overlay/DeviceOverlaySectionPages.cs` (menu of pages, keeps
  controller nav fast). Section enum `DeviceOverlaySection`; pages `OverlayPage.Device*` in
  `OverlayNavigation.cs`. Keep the menu-of-pages structure; put the card list inside each page.
- Controls to reuse/create: WSGM already has sliders (RTSS curve editor, audio panel), toggles,
  dropdowns in Settings/QAM. Prefer a small set of reusable device control rows in
  `src/WSGM/Overlay` themed via `Themes/` tokens (no literal colors — CLAUDE.md).

## Gamepad/touch (WSGM-owned — the reason plugins don't get Avalonia)
- Slider: Left/Right adjusts by Step (model on the curve editor's Left/Right semantics,
  `docs/overlay-and-input.md`). Dropdown: Left/Right or A cycles. Textbox: A opens OnScreenKeyboard.
- Only one `GamepadNavigation` active at a time; rows are focusable; keep focus keys stable.

## Section regroup (plugin descriptor set, `Claw8A2VmPlugin.cs` OverlaySections ~1604)
- Currently: Power(limits), Cooling(fans/thermals), Lighting, Input, Display, plus WSGM-side
  ControllerAndMotion/Glyphs split. Target: one **Power** (limits + thermals + fans via Categories)
  and one **Controller** (target + motion + glyphs via Categories). Use `CapabilityCategory` for
  sub-grouping within a section rather than separate pages. This is a submodule change → commit in
  `external/WSGM.Device.Msi.Claw8A2Vm` first, bump pin. Package apiVersion already 2, v1.2.0.

## Layout
- Card list flows into MULTIPLE COLUMNS across the wide overlay (sheet is 1280 DIP) instead of one
  scrolling column. Categories become column groups / headers.
- Widen `DeviceColorView` for the real overlay width (it was built for the old sidebar).

## Slices (each builds + `dev-deploy.ps1` + verify on its own)
1. Extend `DeviceOverlayCapability` projection with ValueKind/Min/Max/Step/Unit/Choices/Writable;
   populate at construction. Build a **slider row** control; route Integer+range capabilities to it
   (fixes TDP, boost, charge limit, brightness, fan %). Write via InvokeAsync(NextValue). Gamepad
   Left/Right. THIS IS THE HIGHEST-VALUE SLICE — do first.
2. Dropdown row (Choice) + toggle row (Boolean) + textbox row (Text).
3. Multi-column card layout across the wide overlay; category headers.
4. Section regroup in the plugin submodule (Power, Controller); bump pin; refresh via dev-deploy.
5. Widen the color editor.

## Deploy note
`eng/dev-deploy.ps1` now rebuilds + installs the device plugin (elevated slot swap) unless
`-SkipPlugin`. Plugin submodule builds clean as of the CS1573/IDE0055 fixes (uncommitted in the
submodule working tree — commit + push + bump pin per AGENTS.md submodule rule).

## Status
- [ ] Slice 1 (slider)  [ ] Slice 2 (dropdown/toggle/textbox)  [ ] Slice 3 (layout)
- [ ] Slice 4 (section regroup)  [ ] Slice 5 (color width)
- [ ] Plugin submodule build fixes committed + pin bumped
