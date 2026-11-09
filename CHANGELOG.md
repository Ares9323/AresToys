# Changelog

All notable changes to AresToys. Format loosely follows [Keep a Changelog](https://keepachangelog.com/),
versions follow [SemVer](https://semver.org/).

## [0.2.3] — 2026-11-09

First release of the published history. The repository history was rewritten
into one commit per module and the earlier releases were withdrawn, so this
entry describes what AresToys does today. The development that led here is
summarised by area in "Earlier development" below.

### Clipboard history
- One searchable timeline for text, images, files, videos and capture results,
  stored in SQLite with full text search and encrypted payloads. A popup opens
  on Win+V, with custom categories, pinning, a filter strip, a vertical
  toolbar, a remembered geometry and Ctrl+1 to 9 quick paste.
- Per-item labels (F2, right-click or the box above the preview) that take
  part in the search, coloured tags with an AND / OR filter strip, a Trigger
  chip for items with a trigger sequence, drag to a category tab, and a
  user-controlled order for pinned items.
- Image files copied in Explorer get thumbnails, transparent images keep their
  alpha, and recordings can be trimmed frame-accurately into a new entry.
- Process blacklist, incognito hotkey, and automatic rotation by item count or
  age with per-category overrides.
- Ctrl+C does not lag the PC: the hooks run on their own thread and the copy is
  read and saved off the UI thread.
- Deleted items are removed from the database for good 24 hours after their
  delete, with their orphan files, and the file gives the space back.

### Capture and recording
- Region capture (Win+Shift+S) with a darkened overlay, window snap preview,
  multi-region selection, eight grip handles per rectangle, a magnifier with
  wheel zoom, a marching-ants crosshair and a draggable toolbar.
- Fullscreen, per-monitor, last region and active window capture, and full page
  webpage capture through WebView2.
- Screen recording to MP4 (Shift+PrtScn) and GIF (Ctrl+Shift+PrtScn) through
  FFmpeg, built from composable workflow steps.
- Configurable file name (prefix and token pattern including window title and
  program name) and sub-folder pattern, image format with a JPEG quality and a
  size based fallback, and an option to hide the app's own toast before a shot.
- Region capture and the screen colour picker open one hardware-rendered window
  per monitor instead of one window over the whole virtual screen, which left
  about 288 MB of video memory behind at every open. A selection, a move or a
  toolbar drag still crosses monitors, the toolbar stays whole inside one
  monitor and its spot is saved in physical pixels.
- The video memory of a closed capture or editor is given back: windows that
  are only hidden (the main window, the clipboard popup, the launcher) kept
  the GPU render device alive, so about 300 MB stayed held after the first
  capture. Now the process drops back to a few MB within a second.

### Screen colour picker
- Round magnifier with wheel zoom, pixel coordinates and copy formats (hex,
  RGB, HSB, CMYK, decimal, linear, BGRA), shared with the editor eyedropper.
- Other programs can call it: `AresToys.exe --pick-color [--initial RRGGBB]
  [--owner HWND] [--format hex|rgb|hsl]` prints the chosen colour.

### Pin to screen
- Always-on-top images and videos with drag, wheel zoom, adjustable opacity,
  a border, a lock state and Normal, Multiply and Screen blend modes.
- Pins come back after a restart at the same place and look. Pinned videos
  loop and have a seek bar, timecode and mute.

### Image editor
- Rectangle, ellipse, line and arrow (one primitive with per-end caps and a
  curved or triangle tip), freehand, text with outline, step counter, image,
  pixelate, blur, spotlight, smart eraser and multi-area crop.
- Effects are true layers in creation order, export included. Undo, Redo,
  Save as, Copy, Cancel and zoom are in the title bar, with a vertical toolbar
  and one properties panel that keeps a current style per tool and a Recent
  list. Several editors can be open at once.
- Colour picker with palette, recents and eyedropper. Alt+click edits a shape
  on the fly. An image opened from a file asks on Save whether to overwrite or
  save a copy.
- Magic eraser (local AI background removal with brush touch-up) and Trace
  open from the same toolbar and come back as an undoable swap.
- A closed editor no longer stays in memory when a crop selection was still
  pending at close.

### Image effects
- More than 60 effects in Adjustments, Manipulations, Filters and Drawings,
  an editor with a multi-preset list, a drag-reorderable chain, a live preview
  and a property grid with gradient and font pickers.
- `.sxie` presets import and export with the exact ShareX schema, assets
  included, and a file association opens them in the editor.
- The Apply effects preset step attaches a preset to any capture or clipboard
  workflow.

### Trace and QR codes
- Trace converts a raster image to SVG with the native VectorCraft engine,
  presets (Auto Color, 1 to 6 Colors, Outline, Flat Logo), a live WebView2
  preview and a manual palette picked with the eyedropper.
- QR generator with a live preview, error correction picker and PNG or SVG
  export, a decoder that reads a region, and workflow steps for both.

### Upload
- Ten bundled uploaders: Catbox, Uguu.se, paste.rs, Imgur, ImgBB, Pastebin,
  GitHub Gist, OneDrive, Google Drive and Dropbox.
- A declarative `.sxcu` engine for custom uploaders, with an optional file
  association and an import confirmation dialog.

### Launcher
- Keyboard overlay with an F1 to F10 strip and ten numeric tabs of 30 QWERTY
  cells, drag and drop assignment (files, folders, shortcuts, Store apps),
  search as you type, a docked mode with tab swapping, and cells that run a
  workflow. Shortcuts keep their arguments, window state and elevation.

### Workflows
- Every capture, clipboard and upload action is a chain of steps the user
  edits, reorders or disables: add-step search, repeat loops, Send key, Read
  Windows clipboard, Launch application with window mode and elevation, Run
  now from the workflow page, and a rebinder for hotkeys and mouse buttons
  (including Win+V and PrintScreen).

### Wormholes
- Floating desktop windows that mirror a real folder, with lock, collapse,
  hide, opacity, per-wormhole accent colour, tab groups, inline search, F2
  rename, favicons for web links, the native shell context menu, cut, copy,
  paste and delete, and named layout presets that follow the monitor setup.

### Window tools
- Zone based window snapping with a card-based layout editor, free form and
  grid layouts, per-monitor layouts and zone memory by program and window title.
- Always on top: pin the foreground window with a coloured frame from a hotkey.
- Image Resizer tasks, an Explorer menu for images and a Custom dialog.
- New+ creates files and folders from your own templates in an Explorer
  context menu. Ctrl+Shift+N creates a folder in Explorer, on the desktop and in
  file dialogs (Firefox Save image, Save as), including dialogs that show a
  known folder by name. The built-in New menu is hidden only while AresToys
  runs: the process that hid it gives it back on exit and on a fatal crash, and
  the satellite gives it back when the main app dies without asking it to quit.
- Peek previews files from Explorer with a shortcut: text, code, Markdown,
  HTML, SVG, images, videos and Windows preview handlers, with zoom modes.

### Dictation
- Hold a hotkey, speak, release: the text is typed into the focused window.
  Recognition runs on this PC with a Whisper model in English, Italian,
  Spanish, French or German, with a small pill showing the microphone level.
  Off by default. The settings page notes that on the graphics card the model
  can hold a lot of video memory while loaded, which belongs to the model and
  not to the rest of AresToys.

### Settings and app
- Dictation, Window snapping, New+, Peek and Always on top have their switches
  in App Settings > Modules, and their sidebar entries follow the module
  Enabled flags. Their view models load with the settings instead of when
  their page opens. Always on top has its own switch (off releases the pinned
  windows) and a Shortcut chip. The satellite restarts when the module set
  changed during its start.
- Wormholes and the Launcher run in a separate non elevated process, so drag
  and drop from Explorer works when AresToys runs as administrator.
- Themes, a full Italian localization with a language picker, a JSON backup
  and restore, autostart through Task Scheduler, update checks and optional
  automatic installs, and crash logs in `%LocalAppData%\AresToys-Data\Logs`.
- Licensed under PolyForm Noncommercial 1.0.0. The code is written from
  scratch and ShareX, PowerToys and the other projects in the README are
  listed as inspiration.

## Earlier development (0.1.0 to 0.2.0, May to November 2026)

Notable changes merged across the pre-release versions, grouped by area.

### Clipboard
- 0.1.0 shipped the SQLite and FTS5 history with encrypted payloads, the
  Win+V popup, categories, pinning, the process blacklist and rotation.
- Per-item labels, tags (backup format v4), drag to a category tab, reorderable
  pins, a "+" button and a right-click menu on category tabs, selection of the
  newest copied entry when the window opens, and "Dock window" wording.
- Paste pipeline fixes: HTML and RTF no longer truncated, ASCII art whitespace
  kept, CF_HTML headers from some editors stripped, pasted content not
  re-ingested as a new item, file paste through CF_HDROP, and the first-run
  paste from a shortcut.
- Images keep alpha in the clipboard (CF_DIBV5 and PNG format), image rows
  paste on double-click, and a graceful fallback replaces video previews that
  Windows cannot decode (WebM, VP9).
- Video previews stop when the window closes and release their file, and the
  Delete button keeps a fixed place in the side toolbar.
- Recordings can be trimmed to a new entry (H.264 files, re-encoded for frame
  accuracy), and recordings in the history keep only the file path, so a large
  GIF no longer sits in memory dozens of times.
- Ctrl+C lag removed by moving the hooks to their own thread, and deleted
  items are purged after 24 hours with the database compacted.

### Capture and recording
- Region capture gained multi-region selection, window snap preview, a round
  magnifier with wheel zoom, grip handles, a single draggable toolbar and a
  throttled redraw. The snapshot is taken before the overlay is built.
- Fixes: a double-fire cooldown for the overlay, Discord paste of region
  captures, local time in file names, the PrintScreen and Pause hotkeys firing
  on every other press, twice, or missing quick taps, and a second record press
  no longer starting a second save with duplicate files.
- Recording split into composable steps (Record screen, Save as Video file),
  and the temporary recording is removed once saved.
- File name and sub-folder patterns with token chips, "Capture region" reuse of
  the last region, optional hiding of the app's own toast, and an image format
  setting with a JPEG fallback for large PNGs.
- The window detected under the cursor shows its X, Y, W and H, and the
  magnifier stays inside the monitor under the cursor.
- Closed editor windows are now freed (a day of screenshots could hold several
  GB), and a crash writes a log.

### Pin to screen
- Pinned thumbnails gained a Copy and Save button, a reset zoom that anchors on
  the window centre, multi-region pins, opacity, blend modes, video pins from
  the history, a lock state, restore after restart and transparent PNG or
  WebP support with click-through on transparent areas.

### Screen colour picker
- Round magnifier on a frozen snapshot of the desktop, wheel zoom, pixel
  coordinates, no software rendering fallback, and a magnifier that stays on
  screen near the edges. Colours picked land in the editor Recent list.
- Eight "copy as" steps merged into one Convert colour step.

### Editor
- 0.1.1 rebuilt the editor: new toolbar, canvas and properties panel, effects
  launcher, several editors in parallel, Save as in four formats, numeric
  nudging with the wheel and arrows, and a clipboard-first save flow.
- Shapes: Line and Arrow merged into one primitive with caps and tip styles,
  step counter with a draggable tail and renumbering, text outline, adjustable
  blur, pixelate and spotlight regions, multi-area crop with Apply all.
- Interaction: Alt+click edits on the fly, layered Esc, panning with the middle
  button, transparent shapes grabbed only near their outline at any zoom,
  moves and resizes committed to undo, and an eyedropper that feeds Recent.
- The toolbar moved into the title bar, then to a vertical layout with
  Save, Save as and Cancel pinned; effects became true layers; the window
  reopens at its closing size; images opened from a file can overwrite or save
  a copy.
- Fixes: selection box leaking into saved images, blur over arrows, a shifted
  copy, and a crop plus effect chain producing the wrong size.

### Image effects
- 60+ effects, a multi-preset editor with a drag-reorderable chain, gradient
  and font pickers, and `.sxie` round-trip with asset bundling and a file
  association. The Rotate effect, text placeholder expansion and an editor
  hand-off for the Apply effects preset step arrived in 0.1.1.

### Background removal and Trace
- Magic eraser: an embedded U2NetP model on ONNX Runtime with DirectML,
  threshold, feather and edge controls recomputed locally, an Add and Remove
  brush with hardness and shortcuts, and a workflow step.
- Trace began on Potrace with Illustrator-style presets, a live WebView2
  preview with zoom and pan kept across edits, an ignore-colour eyedropper and
  a custom palette picked by hand (2 to 16 colours).
- 0.1.41 replaced Potrace with the VectorCraft native engine: colours and fine
  details kept without halos, new controls (colour detail, shapes, gradients,
  fills, strokes), the Flat Logo mode and new presets.

### QR codes and notifications
- The QR generator, decoder and four workflow steps came with 0.1.0 and a
  restyled window in 0.1.1.
- Toasts go through the Windows notification pipeline with a unique tag per
  toast and inline thumbnails; the popup duration and the time spent in the
  Notification Center are configurable, and the update prompt uses the same
  path. Saving from a toast's "Open in editor" adds the edit to the history.

### Upload
- Ten uploaders and the `.sxcu` engine shipped in 0.1.0. OAuth client ids are
  not part of the public source, so builds from source configure their own.

### Launcher
- Faster invocation, docked mode with keyboard launching, tab swapping, drag
  of a cell to another page, Edit and Copy on every cell, a workflow picker on
  cells, Paste of Explorer files, and a trigger step that fires a cell.
- Dropped shortcuts are unwrapped into target, arguments, window state,
  elevation and icon, Control Panel shortcuts open the right page, and Store
  apps launch through their AppUserModelID.

### Workflows
- The step catalog was reorganised (Actions and Panels categories, shorter
  names), with a sticky header, a searchable add-step menu, port type stripes,
  inline warnings and per-uploader upload entries.
- New steps: Repeat and End repeat, Send key or shortcut, Read Windows
  clipboard, Trigger launcher key, Paste clipboard entry, Set snapping layout,
  and text input on Open file, Run command, Launch application and Open URL.
- Launch application gained Window, Run as administrator, Minimize after
  startup and failure notifications. The text bag was consolidated.
- Hotkeys: the Menu key is suppressed correctly, mouse buttons are triggers, a
  key picker covers media, browser, F13 to F24 and mouse buttons.

### Wormholes
- 0.1.9 introduced Wormholes: floating, transparent windows that mirror a
  folder, with batch workflow steps (hide, show, lock, collapse, topmost),
  an Explorer "Create Wormhole" verb and a settings grid.
- Interaction: drag out, drag in with per-tile routing and a right-button menu,
  keyboard Delete, Copy, Cut (faded tiles) and Paste, F2 rename, inline search,
  the native shell context menu, one-click open, selection cleared on empty
  clicks and focus loss, and a lasting custom icon.
- Appearance: icon size, tile padding, label font and lines, line spacing,
  background and border opacity, hidden extensions and system service files,
  an arrow on shortcut icons, favicons for web links and a per-wormhole accent.
- Layout: per-monitor-setup layouts with an immutable original, named presets,
  RDP and display-change handling, snapping to a grid and to other windows,
  folder rename tracking, tab groups and a "show collapsed on hover" mode.
- Topmost handling: a Toggle Topmost step with a default hotkey and an option
  to drop topmost after launching an app. Fixes for Show desktop, the taskbar
  button after an Explorer restart and a crash on multi-select delete.

### Window tools
- Window snapping started as zone based snapping and grew into a card
  based layout editor with free form zones, Split, Merge and Draw tools,
  padding and tolerance, and zone memory by window title.
- Always on top, Image Resizer, New+ and Peek (see 0.2.3) were added between
  0.1.37 and 0.2.0, with Peek zoom modes and Explorer SVG preview, and New+
  adjusted to act on the active Explorer tab and run when elevated.

### Dictation
- Added in 0.1.39 with a local Whisper model, a level pill, a language and
  microphone choice and a configurable time the model stays in memory. The
  microphone opens only after the model is loaded and the pill follows the
  monitor DPI.

### Settings, theme and localization
- App Settings gained a regrouped layout, update preferences, a Windows
  integration section, Task Scheduler autostart with no start delay, backup
  and restore (v4), a Modules page and a Wormhole settings page with
  drag-to-scrub number boxes.
- Theme: WPF-UI brush overrides, Outer and Inner border colours, a colour
  audit of about 50 hard-coded greys and an accent that follows every title.
- Full Italian localization arrived in 0.1.1 and was completed in later waves.

### Running as administrator
- Optional "Run as administrator" at startup, elevation detected from the
  process token, a tray entry to restart elevated or normally, and a warning
  that drag and drop from Explorer is limited while elevated. Version 0.1.41
  replaced this with a separate non elevated process for Wormholes and the
  Launcher, and programs started from them run with normal rights.

### Stability and security
- Fixes for memory held by closed editors, recordings and video previews, for
  updates that failed quietly when a file was locked, and for a WPF
  accessibility crash while a list updated.
- Native SQLite updated to 3.53.3 (CVE-2025-6965).

### Distribution and tooling
- Velopack installer, portable zip and delta updates through GitHub Releases,
  an in-app updater with startup checks, a local release script, a local pack
  script, a tag-driven release workflow, a local test script and CI on
  every push. The current logo arrived in
  0.1.3, and the settings folder moved to `%LocalAppData%\AresToys-Data`.
- Building from source needs the .NET 10 SDK and the Rust toolchain on PATH.
