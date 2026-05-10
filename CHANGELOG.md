# Changelog

All notable changes to AresToys. Format loosely follows [Keep a Changelog](https://keepachangelog.com/),
versions follow [SemVer](https://semver.org/).

## [0.1.7] — 2026-05-10

Line and Arrow tools unified into a single primitive with per-end cap toggles
(Start cap, End cap) and a pickable tip style — ShareX-style curved cap (default,
the V with concave base that integrates with the stroke) or solid filled triangle.
Same caps mechanism applied to Freehand. Toolbar keeps the two intent buttons (L
for Line, A for Arrow) and their muscle memory: clicking Arrow seeds a fresh
EndCap=true gesture, clicking Line seeds both off. Editor checkboxes now follow
the AresToys accent theme instead of the WPF-UI default teal.

### Editor — Line, Arrow, Freehand caps
- `ArrowShape` removed; `LineShape` is now the single primitive for line and
  arrow renders, carrying `StartCap`, `EndCap`, `TipStyle`. `FreehandShape`
  gains the same trio in place of the legacy single `EndArrow` bool. Persisted
  defaults migrate transparently — pre-0.1.7 payloads pick up `EndCap` from the
  old `FreehandEndArrow` value, the legacy field is then cleared on next save.
- Two toolbar buttons (Line / Arrow) remain as intent aliases for the same
  internal tool. Each persists its own pair of cap defaults (Line: both off,
  Arrow: EndCap on) so cycling between them always reseeds the expected look.
- New "Tip style" combobox in the per-shape properties panel for Line/Arrow and
  Freehand selections. Default `ShareX curve` reproduces ShareX's V with a
  concave curved base; `Filled triangle` is a heavier solid alternative.
- Cap rendering trims the underlying stroke parametrically (quadratic-bezier
  sub-curve for Line, arc-length walk for Freehand) by ~1.2× stroke width on
  each capped side so the round line cap no longer pokes past the V apex. The
  cap apex stays anchored at the user's actual endpoint.
- Short-line guard mirrors ShareX: when both caps are requested but the line
  is too short, only EndCap renders so the heads don't visually flip.
- Selection labels track intent: a `LineShape` with any cap toggled on reads
  as "Arrow" in the properties panel header; with no caps it reads "Line".

### Editor — UI polish
- Properties-panel checkboxes (Smooth stroke, Start cap, End cap, Bold, Italic
  in step / text sections) now follow the AresToys accent theme. Previously
  picked up WPF-UI's hard-coded teal.



ShareX-style step counter with draggable tail and right-click delete-renumber. Canvas
plus preview pan moved from right mouse button to middle mouse button across the app to
match ShareX. Alt+click becomes the placement-tool "select for quick-edit" gesture
(was Shift+click in 0.1.5), freeing Shift for the conventional multi-select toggle.
The editor remembers when an alt+click promoted you into Select and bounces you back
to the placement tool on deselect (the "modifica al volo" workflow). Settings tab
polish, plus update-available notifications routed through the Windows toast pipeline.

### Editor — step counter
- Step counter now carries a draggable tail handle (mint-green dot in Select mode),
  ShareX parity. Drag it to point the wedge at a feature in the underlying image; the
  triangular wedge renders only when the tip is outside the disc, so a freshly placed
  counter still looks like a bare disc until the user grabs the tail. The tail and the
  disc translate together when the whole counter is dragged, so the anchor stays
  consistent under move.
- Right-click on a step counter removes it AND decrements every counter that came
  after, so "1, 2, 3, 4" with #2 deleted becomes "1, 2, 3" instead of leaving a hole.
  Single undo step via `DeleteStepAndRenumberCommand`. The tool's running counter
  rolls back too so the next placement picks up max+1.
- Shortcut moved from N to S — matches ShareX's "Step" mnemonic family and frees N.

### Editor — alt+click "modifica al volo"
- Renamed the placement-tool "select existing shape" gesture from Shift+click to
  Alt+click. Shift now stacks: `Alt+Shift+click` toggles the hit shape into / out of
  the selection set (multi-select), matching the Select tool's own shift+click
  semantics. Settings key renamed `editor.shift_click_no_match` →
  `editor.alt_click_no_match`; existing toggles need to be re-set once.
- Alt+click auto-switches to the Select tool so grips, the new tail handle, and the
  properties row become editable in one motion — no V hop required.
- Return-to-placement: when an alt+click promoted you into Select, the next deselect
  action bounces you back to the placement tool you came from. Triggered by clicking
  the empty canvas, pressing Esc, pressing Delete on the selected shape(s), or
  right-clicking a step counter (the renumber-and-delete gesture also deselects).
  Manually picking a different tool (V, R, A, sidebar button…) wins over the queued
  auto-return — the user's explicit choice always trumps the breadcrumb.
- Esc is now layered: first tap clears selection plus auto-return; second tap (with
  no selection) cancels / closes the editor as before. The unsaved-changes confirm
  still fires when the user actually tries to leave, so nothing is lost by the extra
  step.

### Pan — RMB to MMB
- Canvas pan in the editor, image-preview pan in Clipboard, and preview pan in the
  Image effects window all moved from right mouse button to middle mouse button
  (ShareX parity). `BgRemoverWindow` already used MMB and stays as-is.
  `ScreenColorPickerOverlay`'s RMB stays as Cancel. The freed RMB powers the new
  step-counter quick-delete gesture above.
- Wired through generic `PreviewMouseDown` / `PreviewMouseUp` plus a
  `ChangedButton == MouseButton.Middle` filter, since WPF doesn't expose a dedicated
  middle-button event variant.

### Settings — App Settings tab
- Language card moved below Image effects (sits next to Backup as the other
  "rare configuration" card). Editor card stays near Image effects since they're both
  editor-adjacent surfaces.
- Uniform 16 px spacing between every card. The previous layout mixed
  `Margin="0,0,0,16"` on the top half with `Margin="0,8,0,0"` on Image effects and
  Backup, producing a 24 px gap above Image effects and only 8 px between Image
  effects and Backup.

### Self-update
- Update-available toast now routes through `IToastNotifier` (modern Windows toast
  pipeline) instead of the legacy tray balloon. The prompt lands in the Notification
  Center alongside every other app event (Color picked, Recording, Incognito,
  capture toasts) and persists past dismissal — the previous tray balloon faded after
  a few seconds and was easy to miss.

---

## [0.1.5] — 2026-05-10

Editor polish pass: toolbar icons, side-panel buttons, properties labels and window
titles now follow the theme's foreground colours instead of staying hard-coded white.
New shift+click smart-select on placement tools, double-click confirms pending crops
or capture regions. App Settings tab regrouped into "Windows integration" plus a
dedicated "Editor" card. Sliders pick up the theme's dim foreground for track and tick
marks. MainWindow sidebar buttons gain an accent-tinted hover.

### Editor
- Toolbar icons (Select, Rectangle, Ellipse, Line, Arrow, Freehand, Text, Step, Image,
  Blur, Pixelate, Spotlight, Erase, Crop, Resize, Effects, Trace, Magic Eraser) bind
  through `{DynamicResource AccentForegroundBrush}` so they retint live with the theme
  instead of staying baked white. Property panel section titles ("Crop properties",
  "Default properties", the dynamic Selection title) follow the same brush; subtitles
  and label rows (Outline, Fill, Stroke, Effect, Rotation, Font, Size, Alignment, etc.)
  use `AccentForegroundDarkBrush` so the value/label hierarchy reads correctly under
  any palette. The window title in the top-left and the three side-panel action button
  glyphs (Confirm/Copy, Save as, Cancel) follow `AccentForegroundBrush` too.
- New shift+click gesture on placement tools (Rectangle, Ellipse, Line, Arrow, Freehand,
  Text, Step counter, Blur, Pixelate, Spotlight, Smart Eraser): instead of starting a
  placement gesture, hit-test for an existing shape of the SAME type as the tool under
  the cursor and select it. Lets the user grab a previously-drawn rectangle to tweak
  its properties without leaving the Rectangle tool. The current tool stays active so
  the next plain click still places.
- Pending-crop and capture-region overlays now confirm on double-click inside any rect,
  same effect as Enter or the Apply button. The overlay help text and the editor's
  Apply tooltip mention both gestures.

### Settings
- Settings tab regrouped: "Run when Windows starts", "Start minimized" and the tray
  click-routing combos consolidate into a single "Windows integration" card. A new
  "Editor" card sits next to "Image effects" and hosts "Start editor maximized" plus a
  new toggle "Shift+click selects any shape (not only same type)" — controls the
  shift+click behaviour above when no same-type shape is under the cursor (off, the
  default, falls through to a normal placement; on, selects whatever shape is there).
  Persisted under `editor.shift_click_no_match` and `app.editor_start_maximized`.

### Theme
- Slider track, tick bar and outer thumb ring follow `AccentForegroundDarkBrush` instead
  of the WPF-UI default near-white. Affects every slider in the app: editor stroke,
  Trace knobs, BgRemover knobs, Image effects, Capture defaults. The inner thumb keeps
  the user's accent so the slider's identity colour still pops.
- MainWindow sidebar: non-selected category buttons now hover to a lighter shade of the
  user's accent (the same `AccentBackgroundLightBrush` used elsewhere) instead of a
  flat surface-coloured hover. The selected button keeps its dark-accent fill with the
  existing accent-light hover.
- Clipboard, Launcher, Hotkey-capture and Editor window titles in the title bar follow
  `AccentForegroundBrush` instead of a hard-coded `#FFF`, so they re-tint when the user
  picks a custom foreground colour.

---

## [0.1.4] — 2026-05-09

A custom palette mode for the SVG tracer, and a per-workflow toggle to skip the
multi-region overlay when a single capture is what you actually want.

### Image trace (raster → SVG)
- New "Custom (pick colours)" palette mode in the Trace window. The user samples 2 to 16
  colours from the source image with the on-screen eyedropper; every source pixel then
  maps to its nearest entry by Euclidean RGB distance, so related tones (white plus
  light grey, two near-identical reds, anti-alias halos) collapse into the nearest pick
  instead of getting an arbitrary auto-quantised palette. Falls back to Limited if the
  swatch list has fewer than 2 entries so the trace is always defined.
- Swatch row UI: click an empty slot to drop the colour picker, click an existing chip
  to replace it, right-click to remove. `MaxCustomSwatches = 16`, `MinCustomSwatches = 2`
  enforced in the VM. The "Add swatch" plus "Need more colours" affordances react live
  to collection mutations.
- `TraceOptions.CustomPalette` plumbs the picked list down to the potrace pipeline; the
  pipeline `Trace to SVG` task picks it up unchanged when a workflow drives the tracer.

### Region capture
- New per-workflow option `Auto-confirm on first selection (skip multi-region)` on the
  `Capture region` workflow action. When enabled, the overlay closes on the first valid
  mouse-up (drag rect or snap-to-window click) without waiting for Enter — single-shot
  semantics that match the pre-multi-region behaviour, useful for "rapid screenshot"
  workflows where the user never wants a second region. The multi-region toolbar is
  hidden in this mode (the affordances it exposes — region count, Apply, Cancel for
  accumulated rects — are meaningless when the overlay self-confirms). Esc still cancels.
- Default stays `false` so existing workflows keep the multi-region UX; the toggle lives
  in the workflow action's parameter strip in Settings → Hotkeys and workflows.

---

## [0.1.3] — 2026-05-08

Project rebrand from ShareQ to AresToys, new logo (Pigeon mark across icon, tray, title
bars and About).

### Branding
- Rebrand: project, solution, namespaces, assemblies, settings folder, installer
  artifacts and tray menu strings all migrated from "ShareQ" to "AresToys". Existing
  0.1.2 installs continue to read their data folder via the previous
  `%LocalAppData%\ShareQ-Data\` → `%LocalAppData%\AresToys-Data\` migration path so
  settings plus clipboard history carry over automatically.
- New logo (Pigeon mark) replaces the old green/grey leaf-pair across every surface:
  app icon (`.ico` rebuilt as a multi-frame PNG-encoded ICO at 16/24/32/48/64/128/256
  so it stays crisp on every Explorer view plus DPI), tray icon, title-bar `ImageIcon`
  on every window (MainWindow, BgRemover, Clipboard, HotkeyCapture, QrGenerator, Trace,
  Launcher, LauncherCellEdit, ImageEffects, EditorWindow, ColorPickerWindow), and the
  About panel. Vector `DrawingImage` resources collapsed to a single shared
  `BitmapImage` pointing at `Assets/AresToysLogo.png`; cross-assembly pack URIs hand
  the same PNG to the Editor assembly so there's only one logo file to swap in future
  redesigns.
- `tools/build-icon.ps1` ships alongside the source to regenerate `icon.ico` from a PNG
  on demand (multi-size, transparent, no external tooling required).

---

## [0.1.2] — 2026-05-08

Multi-region capture and multi-area crop, round ShareX-style pixel pickers across every
sampler surface, undoable pending crops, and a fix for the settings-wiped-on-update bug
that bit anyone who ran a 0.1.1 self-update.

### Region capture overlay
- Multi-region selection: drag accumulates rectangles instead of replacing the previous
  one, click on the hovered window snaps + adds, Enter applies them as a composite (bbox
  of all rects with everything outside any rect transparent — matches ShareX's multi-
  region capture).
- Snap-to-windows preview while idle: a yellow dashed outline tracks the window under the
  cursor so a click without drag captures that window exactly.
- Round pixel picker (was rectangular): Ellipse fed by a NearestNeighbor-scaled crop, red
  centre crosshair marking the target pixel, X / Y physical-pixel coordinates label.
- Wheel zoom on the magnifier (3 to 41 px sample window).
- Marching-ants screen-edge crosshair extending from cursor to canvas bounds, useful for
  aligning rect edges with distant features. Animated white + black dashes interlock at
  60 Hz.
- 8 resize grip handles per committed rect (corners + edge midpoints) with proper resize
  cursors. Drag inside a rect moves it.
- Single draggable popup at the bottom unifies the previous top hint and bottom toolbar
  (status + zoom + Apply + Cancel). Position is remembered across sessions.
- Dim-path rebuild during in-flight drags is throttled to a 60 Hz tick instead of running
  on every pointer event — eliminates crosshair and magnifier lag on high-poll mice.

### Editor
- Multi-area crop: every drag with the Crop tool appends a pending rect, the user can
  move / resize / delete any of them, then Apply All bakes them all as a single composite
  (matches the region overlay's behaviour).
- Pending crop borders are animated marching-ants dashes (yellow when selected, white
  otherwise) instead of a solid amber outline.
- 8 resize grip handles per pending crop (corners + edge midpoints), 30 percent smaller
  than the initial size, screen-space-sized so they stay constant when the canvas is
  zoomed.
- "Apply all crops" button in the Crop properties panel (the previous in-canvas floating
  green button has been removed — it dropped a square next to the rect that read like a
  per-rect confirm).
- Ctrl+Z removes the most recently placed pending crop (it's now recorded on the undo
  stack as a discrete step).
- Round crop magnifier (was square): Image element clipped to an ellipse with
  NearestNeighbor scaling, red centre marker. Pinned above effects + pending-crop
  visuals via Canvas.ZIndex so RedrawPendingCrop can no longer sandwich it underneath.

### Color picker (eyedropper)
- Round magnifier replaces the square one — same NearestNeighbor + clipped-Image pattern
  used by the region overlay and the editor crop tool.
- Wheel zoom on the picker (3 to 41 px sample window) with a live zoom indicator.
- Works on a frozen snapshot of the desktop captured before the overlay paints, instead
  of doing a fresh CopyFromScreen per frame. Two consequences: the picker no longer
  captures its own UI or the cursor sprite (was sampling the cursor colour on flat
  surfaces), and the per-frame GDI roundtrip + new BitmapSource allocation goes away.
- Dropped AllowsTransparency on the overlay window — WPF was forcing software rendering
  for a fullscreen transparent layered window, which on a multi-monitor virtual screen
  meant a long flash of white at open and constant frame stutter. The window now paints
  the snapshot opaquely as its background and runs hardware-accelerated.
- Pixel coordinates readout (X / Y in physical pixels) joins the existing hex + RGB row.

### Storage + update flow
- Settings folder migrated from %LocalAppData%\AresToys\ to %LocalAppData%\AresToys-Data\.
  The previous path collided with Velopack's install root, so every self-update wiped
  the user's settings and clipboard history. A one-shot migration on first launch copies
  the existing data over and updates the active path.

---

## [0.1.1] — 2026-05-07

Second alpha. Big editor refactor, full Italian localization, image trace (raster → SVG)
and AI background removal (Magic eraser), clipboard-first save flow, plus a sweep of UX
polish across capture, clipboard, launcher and effects.

### New — AI background removal ("Magic eraser")
- New eraser button in the editor toolbar opens a dedicated BgRemoverWindow with side-by-
  side source / cutout preview (cutout pane on a checker background to make alpha visible).
- U2NetP saliency model (~4.5 MB, Apache 2.0) embedded via ONNX Runtime. DirectML on Win10
  /11 (any DX12 GPU) with automatic CPU fallback. First call ~150-500 ms warm-up; subsequent
  calls ~100-500 ms inference-only.
- Post-processing pipeline (re-runs locally, no ONNX rerun on slider edits): Threshold,
  Feather (Gaussian), Edge offset (erode / dilate via blur+threshold), Background opacity
  (preview-only — see what was cut to paint it back).
- Brush tool (Add / Remove) with hardness control. Hardness drives both edge falloff AND
  per-stamp alpha cap so a soft brush genuinely builds up gradually instead of saturating
  instantly. Per-stroke buffer + Lighten blend keeps the falloff visible end-to-end across
  long continuous drags.
- Brush keyboard / mouse shortcuts: `X` toggle Add↔Remove, `Alt` invert mode mid-stroke,
  `Shift+Wheel` hardness, `Wheel` size, `Ctrl+Wheel` zoom (anchored at mouse, 0.1×–20×),
  middle-mouse-button pan, `Ctrl+Z` undo last stroke (20-level stack), `Reset brush`,
  `Reset view`. Apply via `Enter`, Cancel via `Esc`.
- Brush cursor visualizer: two concentric rings (outer = stamp radius, inner-dashed =
  hardness core). Stays at screen-size regardless of zoom level. Centres on the cursor
  even after slider tweaks (the ring re-flashes at the canvas centre when sliders are
  driven from outside the preview).
- Pipeline task `arestoys.remove-background` exposes the same operation to workflows.

### New — Image trace (raster → SVG)
- New "Trace" button in the editor toolbar opens an Illustrator-style preview window:
  source image on the left, live SVG preview on the right (WebView2-rendered against a
  checker background), parameter dock at the bottom.
- 12 stock presets matching Illustrator (`[Default]`, `High Fidelity Photo`, `Low Fidelity
  Photo`, `3 Colors`, `6 Colors`, `16 Colors`, `Shades of Gray`, `Black and White Logo`,
  `Sketched Art`, `Silhouettes`, `Line Art`, `Technical Drawing`) + user-saved custom
  presets persisted in settings.
- Modes: Color / Grayscale / Black and White. Palette: Automatic (elbow-prune) / Limited /
  Full Tone. Parameters: Colors (2-64), Threshold (0-255 for Black and White), Paths %,
  Corners %,
  Noise (despeckle), Snap Curves to Lines, Transparency, Auto Grouping.
- Quality knobs (exposed sliders 0-3): Smoothing iterations (majority filter on the
  per-pixel layer assignment — collapses 1-2 px boundary oscillations from anti-aliased
  edges), Pre-blur strength (3×3 box passes on the source — cleans AA noise before
  quantisation), Overlap radius (Overlapping-mode dilation in pixels — closes the seams
  between adjacent layers' potrace-smoothed paths).
- Ignore Color eyedropper drops a picked colour from the trace; tolerance slider widens
  the match. Toggling Ignore Color auto-enables Transparency so the effect is visible.
- View dropdown: Tracing Result / + Outlines / Outlines / + Source Image / Source Image —
  swaps the right pane without re-tracing.
- Preview toggle + explicit Trace button so the user can pause auto-rerun on slow inputs.
- Live preview supports `Ctrl+Wheel` zoom anchored at mouse (custom JS — vector-quality at
  any scale, no rasterisation) + drag-to-pan. Source image is displayed at WebView2's
  native scale; the checker bg stays stable while only the artwork zooms.
- State preservation across re-traces: zoom + pan survive every parameter tweak so the
  user can stay zoomed into a problem area while iterating sliders.
- Bundled `potrace.exe` 1.16 (BSD) under `Tools/`. Pipeline task `Trace to SVG` exposes the
  same tracer to workflows.

### Editor
- Total UX refactor — toolbar / canvas / properties panel rebuilt for clarity and density.
- Effects launcher: open the image-effects panel directly from the editor, applies the
  rendered result back as an undoable canvas swap.
- Multi-editor — open multiple editor windows in parallel without focus stealing.
- "Save as…" exports in PNG / JPEG / BMP / GIF (independent of the global capture format).
- Rotate effect added; tiling import fixed.
- Pan + zoom preview during region select.
- Numeric input nudge: wheel / Up / Down arrows on focused TextBox = ±1 (Shift = ±5),
  preserves decimal precision.
- Editor opens fullscreen by default (configurable). Removed always-on-top priority when
  the editor is opened from a toast.
- Editor selection bounding box no longer leaks into the captured / saved image.
- Crop + effect chain after editor confirmation now produces correctly-sized output
  (previously caused vertical-strip artifacts on high-DPI displays — DPI metadata was
  ignored by the canvas exporter).
- Clipboard-first save flow: the primary action button (formerly "Save", now a copy icon)
  creates a NEW history item with the edited bytes AND publishes them to the Windows
  clipboard, so the typical "screenshot → annotate → paste in Telegram" loop is one click.
  The original item is preserved untouched in history. "Save as…" (floppy icon) keeps its
  one-shot file-export semantics; "Cancel" (X icon, danger appearance) discards.
- Footer action buttons are now icon-only (Copy / Floppy / Dismiss via FontAwesome 7),
  with localised tooltips. Apply respects `Enter` (IsDefault), Cancel respects `Esc`
  (IsCancel).
- Blur / Pixelate over arrow shapes no longer erases them — the canvas renders effects
  before annotations regardless of insertion order.

### Capture
- Region capture: snapshot is now taken **before** the overlay window is constructed, so
  open dropdowns / hover popups / animated UI stay frozen in the captured image (matches
  ShareX's behaviour). Eliminates the gap that closed transient state before BitBlt fired.
- Capture webpage path: timing fix so the WebView2 renders the loaded page before the
  pipeline grabs bytes.

### Clipboard
- Pinned mode: keep the popup open through actions (multipaste, paste-and-keep-open) so
  the user can paste several items in a row without re-invoking Win+V.
- Multipaste without losing focus on the source app.
- Copy-path-to-clipboard button for items backed by a real file on disk.
- Process blacklist + incognito mode coverage tightened.
- Faster popup invocation (cached enumeration + warm DI).
- New filter chips and tighter padding; transparency removed in favour of an opaque
  Surface1 background (improves readability on busy desktops).
- "Generate QR code…" affordances (toolbar button + context menu) now hide on non-text
  rows (Image / Video / Files).
- Smart notifications: per-item tag/group so successive toasts don't replace or stack.
- Image copy-to-clipboard preserves alpha. The pipeline task, AutoPaster, editor's
  `Ctrl+C`, and pinned-image window now publish images via the registered "PNG" clipboard
  format — Telegram (send-as-file), Discord, Slack, browsers and modern Office paste with
  the alpha channel intact. The legacy CF_BITMAP path was overriding alpha and turning
  semi-transparent pixels (Shadow effect glow, Magic-eraser cutouts) into hard solid
  colour on paste; CF_BITMAP is now skipped for images that have alpha so well-behaved
  consumers always get the alpha-correct PNG. Fully-opaque captures still publish both
  formats so Paint / older Word still work.

### Launcher
- Faster invocation; resize handles polished; cell-edit dialog restyled to match the rest
  of the app (FluentWindow chrome, owner-scoped modal).
- Italian text fits properly in the cell-edit dialog (auto-width).

### QR codes
- QR generator window restyled to match the rest of the app (FluentWindow + accent border).

### Image effects
- ShareX `.sxie` round-trip: DrawTextEx placeholder expansion (`%y / %mo / %d / %h / %width
  / %height / %un / %hn`) — Polaroid-style date stamps now render correctly.
- AresToys `Apply effects preset` pipeline task gains an editor-mode handoff (open effects
  with the editor's bytes, return rendered result as an undoable swap).
- Color picker theme now follows the active app accent.

### Localization
- Italian translation (full coverage of UI surface — settings, editor, image effects,
  pipeline-action catalog, dialogs).

### Tray + lifecycle
- Start-minimized option (Settings → General).
- Dynamic tray-menu shortcuts: hotkey labels reflect the user's current bindings instead
  of hardcoded strings.
- Tray menu entries renamed for clarity.
- PrintScreen / Pause hotkey override fixed (low-level hook now correctly suppresses
  the keystroke from reaching the foreground app).

### Build + CI
- GitHub Actions bumped to `@v5` (Node 24) — `actions/checkout`, `actions/setup-dotnet`,
  `actions/cache`, `actions/upload-artifact`. Drops the Node 20 deprecation warning ahead
  of GitHub's 2026-09-16 removal deadline.
- Velopack build flow polished — `.md` ampersand parsing fix, token scope tightened.

---

## [0.1.0] — 2026-05-05

First public release. Alpha — feature-complete enough for daily clipboard + screenshot work
on Windows; OAuth uploaders only sign in when bundled credentials are configured.

### Capture
- Region capture (Win+Shift+S) with darkened overlay + window snap.
- Fullscreen / per-monitor / last-region (tray menu).
- Active window capture via DWM extended bounds (no resize-border padding).
- Screen recording → `.mp4` (Shift+PrtScn) and `.gif` (Ctrl+Shift+PrtScn) via FFmpeg.
- Webpage capture (full-page screenshot of any URL via WebView2). Affordances hide
  automatically on machines without WebView2 Runtime; the tray menu surfaces an
  "install runtime" entry that opens the Microsoft download page.
- Screen color picker / sampler (hex / RGB / HSB / CMYK / decimal / linear / BGRA copy formats).
- Pin-to-screen (always-on-top thumbnail with drag + wheel zoom).

### Clipboard
- Persistent SQLite history with FTS5 search + DPAPI-encrypted payloads.
- Win+V popup with custom categories, pin, search header, filter dropdown, vertical toolbar,
  geometry persistence, Ctrl+1-9 quick-paste.
- Process blacklist (KeePass / 1Password / etc.) + incognito mode hotkey.
- Auto-rotation: keep last N items / last N days, per-category overrides.

### Editor
- WPF-based, ShareX-inspired — Rectangle, Ellipse, Freehand (smooth + end-arrow),
  Text (drag-to-draw + wrap), Step counter, Image, Pixelate, Crop.
- Outer-aligned outlines (EvenOdd ring geometry).
- Color picker with palette + recents + live preview + eyedropper.
- Clipboard round-trip (Ctrl+C/X/V) preserves shapes as editable objects in-process,
  falls back to raster for other apps.
- "Save" commits to history in the global capture format; "Save as…" exports to a path
  + format the user picks (PNG / JPEG / BMP / GIF).

### Image effects
- 60+ effects ported from ShareX across Adjustments, Manipulations, Filters, Drawings.
- Multi-preset list, drag-reorderable effect chain, live preview on a sample image,
  property grid with sliders / colour swatches / paddings / gradients / fonts.
- Gradient picker (multi-stop with per-stop alpha + 9 ShareX presets) and font picker
  (filterable family list + size slider + bold/italic chips).
- ShareX-compatible `.sxie` import + export — preserves PascalCase + `$type` schema so
  presets round-trip between ShareX and AresToys. Exports bundle DrawImage assets into the
  `.sxie` ZIP.
- File-association toggle for `.sxie` (mirrors `.sxcu`).
- Pipeline task `Apply effects preset` lets the user attach any preset to capture / clipboard
  workflows.

### Image format
- Settings → Capture → Image format (PNG / JPEG / BMP / GIF).
- JPEG quality slider (default 90).
- Auto-fallback to JPEG when PNG output exceeds a configurable threshold (default 2 MB).
- Pipeline `Save to file` step accepts an optional per-step format override that re-encodes
  before writing.

### QR codes
- Generator with live preview window (multiline editor → real-time QR), error-correction
  picker (L/M/Q/H), module-size slider, copy-to-clipboard, save as PNG, save as SVG, save
  into clipboard history.
- Decoder via region select (Tools → "Read QR code" or as a pipeline task).
- Right-click on a clipboard text item → "Generate QR code…" pre-fills the generator.
- Pipeline tasks: Show QR code, Save QR as image, Save QR as SVG, Copy QR to clipboard.

### Notifications
- Modern Windows toasts (`ToastNotificationManagerCompat`) — persist in the Notification
  Center after the popup fades, with inline preview thumbnails for image-bearing toasts
  and unique tag/group per toast so successive notifications don't replace or collapse.

### Uploaders
- 10 bundled: Catbox, Uguu.se, paste.rs, Imgur (anonymous), ImgBB, Pastebin, GitHub Gist,
  OneDrive, Google Drive, Dropbox.
- `.sxcu` engine that loads ShareX-compatible custom uploader files dropped into
  `%LOCALAPPDATA%\AresToys\custom-uploaders\`.
- File-association toggle for `.sxcu`.

### Pipeline and workflows
- All capture / clipboard / upload flows run as composable pipelines (named "workflows").
- Steps user-editable in Settings → Hotkeys and workflows: add / remove / reorder / disable.
- Hotkey rebinder via low-level keyboard hook (handles Win+V, Win+Shift+S etc. that
  `RegisterHotKey` can't bind).
- Built-in profiles: region capture, screen recording, color picker/sampler, pin to screen,
  manual upload, upload clipboard text, open clipboard, open launcher.

### Other
- Categories (CopyQ-style) with Move/Copy/Delete and per-category clear.
- Settings backup / restore (JSON export / import).
- Themes with WPF-UI v4 brush overrides + custom Surface1/2/3 + Foreground/AccentForeground.
- Launcher overlay (MaxLauncher-inspired): F1-F10 strip + 10 numeric tabs × 30 QWERTY cells,
  drag-and-drop assignment, search-as-you-type.
- Autostart toggle (HKCU\Run, no admin).
- Tray menu: Capture submenu, Upload, Tools, Open clipboard, Open launcher, Toggle
  incognito, Open screenshot folder, Settings, Quit.

### Distribution
- Velopack packaging: installer + portable + delta updates.
- In-app updater (Settings → About → "Check for updates").
- Self-contained — ships .NET 10 runtime, no end-user prerequisites.

### Compatibility
- Targets Windows 10 / 11 (x64). The .NET runtime is bundled, no install required.
- Webpage capture additionally needs the **WebView2 Runtime** (preinstalled on Win11 and
  Win10 21H2+; older Win10 builds may need the user to install it). The feature gates
  itself when the runtime is missing — the rest of the app works unaffected.

### Known limitations
- OAuth uploaders (Imgur user-mode, OneDrive, Google Drive, Dropbox) need bundled
  Client IDs to sign in. Builds without `Secrets.Local.cs` show "isn't configured in this
  build" until the user supplies credentials in the Configure dialog.
- No SharedFolder / FTP / SFTP / S3 / Azure / B2 uploaders yet.
- No scrolling capture, image combiner, hash checker, metadata viewer.
- UI is English only (no i18n).
- No CLI / scripting interface.
