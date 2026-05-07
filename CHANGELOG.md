# Changelog

All notable changes to ShareQ. Format loosely follows [Keep a Changelog](https://keepachangelog.com/),
versions follow [SemVer](https://semver.org/).

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
- Pipeline task `shareq.remove-background` exposes the same operation to workflows.

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
- ShareQ `Apply effects preset` pipeline task gains an editor-mode handoff (open effects
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
  presets round-trip between ShareX and ShareQ. Exports bundle DrawImage assets into the
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
  `%LOCALAPPDATA%\ShareQ\custom-uploaders\`.
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
