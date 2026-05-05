# Changelog

All notable changes to ShareQ. Format loosely follows [Keep a Changelog](https://keepachangelog.com/),
versions follow [SemVer](https://semver.org/).

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

### Pipeline & workflows
- All capture / clipboard / upload flows run as composable pipelines (named "workflows").
- Steps user-editable in Settings → Hotkeys & workflows: add / remove / reorder / disable.
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
