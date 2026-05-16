# Changelog

All notable changes to AresToys. Format loosely follows [Keep a Changelog](https://keepachangelog.com/),
versions follow [SemVer](https://semver.org/).

## [0.1.15] — 2026-05-16

Wormhole settings expanded with independent typography / opacity / overlap
knobs, F2 inline rename on items, smarter label wrap, single-wormhole selection
(Explorer-style), row reorder + focused-row highlight in the Settings panel,
and the UI hydration fix that made the "Background opacity" field always show
95 % on launch even when the persisted value was different.

### Wormhole — F2 inline rename on items
- `F2` on the selected tile pops a TextBox overlay positioned over the label
  via `TranslatePoint(0,0, ContentGrid)`, pre-selects the basename only
  (extension stays untouched, Explorer-style), `Enter` commits via
  `File.Move` / `Directory.Move`, `Esc` cancels, `LostFocus` commits. The
  hosting wormhole's `FolderWatcher` picks up the rename and refreshes the
  tiles automatically — no manual reload path needed.
- Single shared `ItemRenameEditor` TextBox lives inside `ContentGrid` as a
  sibling of `ItemsHost`; positioning by `Margin` instead of bloating the
  per-item `DataTemplate` with a per-tile TextBox.

### Wormhole — smarter label wrap
- `DisplayNameWrappable` (new) injects U+200B (zero-width space) only at
  meaningful boundaries so the wrap engine prefers real word breaks: kept
  whitespace + `.` / `_` / `-` / `/` / `\` natural breaks intact, ZWSP at
  CamelCase boundaries (`lower→upper`) and letter↔digit transitions
  (`Gen1Pokemon` → break at the digit), with an 8-char fallback so an
  uninterrupted blob doesn't end up on a single line with ellipsis.
- `DisplayName` itself is preserved verbatim — search filter, rename, and
  clipboard still operate on the raw filename.

### Wormhole — new appearance knobs
- **Label font size (px)**, range 8–20 (default 12). Drives the TextBlock's
  FontSize AND, via the heuristic `lineHeight = ceil(fontSize × 1.36)`, the
  reserved label-area height in `TileHeight` — wider font automatically
  gets a taller text area, no manual tuning per font.
- **Label lines (max)**, range 1–3 (default 2). Binds `TextBlock.MaxHeight`
  to `lineHeight × maxLines`; 1 = Explorer single-line + ellipsis, 3 =
  generous multi-line wrap.
- **Line spacing (overlap, px)**, range -32…+32 (default -4). Applied as a
  CSS-style negative bottom `Margin` on the hosting `ListBoxItem` via the
  `ItemContainerStyle` setter — negative pulls the next row's tile UP over
  this tile's label area (visual overlap, no glyph clipping) without
  changing the label area itself. Positive expands the row gap.
- **Background opacity (%)**, range 1–100 (default 70, was 95). 1 % floor
  because a fully alpha-0 header strip would lose its double-click → roll-up
  gesture: alpha-0 pixels are click-through to whatever's below the wormhole.
- **Border opacity (%)**, range 0–100 (default 100). Independent fade of the
  `OuterFrame`'s 1 px accent ring + drop shadow. `ApplyAppearance` clones
  the theme's `OuterBorderBrush` into a per-window `SolidColorBrush` with
  the user's alpha (touching the shared resource would fade every wormhole
  at once), and modulates the `DropShadowEffect.Opacity = 0.45 × borderOp`
  so the shadow disappears with the ring instead of telegraphing the
  invisible outline.

### Wormhole — single-wormhole item selection
- Clicking an item in wormhole A used to leave selection highlights in
  wormhole B because each `ListBox` was per-control. Added
  `IWormholeWindowManager.NotifyItemSelectionTaken(source)`: the manager
  iterates `_live`, calls a new `WormholeWindow.ClearItemSelection()` on
  every sibling, guarded by `_suppressSelectionBroadcast` so the chained
  `SelectionChanged` events don't ping-pong back and re-broadcast.
- Programmatic `UnselectAll` lands in `OnItemsHostSelectionChanged` with
  empty `AddedItems` — those don't refire the broadcast, only user-driven
  `AddedItems > 0` does.

### Wormhole settings — reorder + focused-row highlight
- `IWormholeStore.MoveAsync(id, delta, ct)` shifts a record by ±N positions
  in the persisted JSON, clamps at list bounds, flushes atomically.
  `WormholeRowViewModel.MoveUp/MoveDown` commands mirror the result on the
  visible `ObservableCollection.Move` so the grid updates without a full
  reload. UI: stacked ↑/↓ chevrons (14 px each, vertical `StackPanel`) in
  a single 32 px column so the folder / trash icons sit at a fixed X.
- `IWormholeWindowManager.WormholeFocused` event + `NotifyWormholeFocused`
  fired from `OnHeaderMouseDown`, `OnContentAreaMouseDown`, and
  `NotifyItemSelectionTaken` — covers chrome-click, content-area-click, and
  item-click. `WormholesViewModel.SelectedWormholeId` listens and refreshes
  every row's `IsSelected` flag; the row Border's Style trigger swaps its
  Background to `AccentBackgroundDarkBrush` on selection.

### Wormhole settings — UI hydration fix
- `WormholesViewModel` is built eagerly during DI (`SettingsViewModel`
  depends on it), BEFORE the async `WormholeDefaultsService.LoadAsync` runs
  at `DispatcherPriority.Loaded`. The ctor's hydration therefore captured
  fallback values (e.g. 95 % opacity), never refreshed — the live wormhole
  windows correctly used the persisted 70 %, but the Settings panel
  reported 95 %, looking like a mismatch.
- Fix: `ReloadAsync` (already called on every tab activation) now
  re-reads every default from the service and re-assigns the observables
  with the persist-suppress flag set, so navigating to the panel always
  shows what's actually on disk.

### Wormhole settings — UI polish
- Page heading + sidebar entry renamed `Wormholes` → `Wormhole settings`.
- All numeric defaults migrated from `Slider` to `ui:NumberBox` (Default
  icon size, Tile padding, Label font size, Label lines, Line spacing,
  Background opacity, Border opacity) — easier to dial exact values vs.
  scrubbing a slider.
- "+ New wormhole" button + selected-row highlight use
  `AccentBackgroundDarkBrush` (was the light variant which read as washed
  out against the page surface).

### Icon extraction — tiny-icon fix for `.url` and some `.lnk` files
- Some shortcuts (especially `.url` web shortcuts whose `IconFile` points to a
  small favicon, plus a handful of `.lnk` targets) rendered as a 16-px glyph
  floating in the top-left of an otherwise empty 48-px tile slot. Root cause:
  `IconService.ExtractIconAtSize` resolved icons via `SHGetImageList` +
  `ImageList_GetIcon`, which preserves the source icon's NATIVE pixel size
  inside the fixed-size imagelist slot — small favicons stay small with
  transparent padding around them, and WPF rendered the bitmap at its true
  dimensions inside the tile.
- New `ExtractViaShellItemImageFactory(path, sizePx)` path tried first:
  `IShellItemImageFactory::GetImage` with `SIIGBF_RESIZETOFIT | SIIGBF_IconOnly`
  is the only shell API that actually upscales the source icon to the
  requested pixel size (same call Explorer uses for "Large / Extra Large
  icons" views). `IconOnly` keeps the file-type icon for media files instead
  of returning a content thumbnail. The imagelist path remains as a fallback
  for shells where the modern API fails.

### Internals
- `WormholeItemViewModel` ctor now takes `lineSpacingPx`, `labelFontSizePx`,
  `labelMaxLines` (defaults 0, 11, 2 for backward compat). `TileHeight`
  decomposed into `IconSize + 4 + TilePadding + 4 (textMargin) + LabelAreaHeight`
  where `LabelAreaHeight = lineHeight × maxLines` — no more single magic
  baseline constant.
- `WormholeWindow` accepts a `IWormholeWindowManager? manager` ctor param;
  the manager passes itself at spawn time so the window can call back into
  the focus / selection-fan-out methods.
- `WormholeWindowManager` subscribes the new `BorderOpacityChanged`,
  `LabelFontSizeChanged`, `LabelMaxLinesChanged`, `LineSpacingChanged` events
  on the defaults service → triggers a live `RefreshAllLiveIconSize` or
  `RefreshAllLiveOpacity` pass without restart.

## [0.1.14] — 2026-05-15

Fix: dragging a file or folder *out* of a wormhole now works as expected.

### Wormhole — drag-out gesture
- `WormholeWindow` did not implement an outbound `DoDragDrop`, so a left-button
  click-and-drag from a tile fell through to the inner `ListBox` and started
  its Extended-selection rubber-band: no file-cursor preview, no drop accepted
  outside the window. Added `Preview*` mouse handlers on each tile that arm on
  Down + promote to a real `DragDrop.DoDragDrop` once the OS drag threshold
  (`SystemParameters.MinimumHorizontal/VerticalDragDistance`) is exceeded.
- Payload is `DataFormats.FileDrop` with absolute paths — same format Explorer
  produces, so Windows shows the file-icon preview cursor and the drop is
  accepted by Explorer, the desktop, and other wormholes / per-tile folder
  drops.
- Multi-drag matches Explorer: if the armed tile is part of the live selection
  every selected path is dragged; otherwise just the armed one. Locked
  wormholes restrict allowed effects to `Copy | Link` so a Move drop can't
  mutate the source folder.

## [0.1.13] — 2026-05-15

Two new pipeline tasks (Trigger launcher key, Paste clipboard entry), per-item
drop routing on wormhole tiles, a handful of paste-pipeline fixes that surfaced
together (HTML truncation, ASCII-art whitespace, Rider's headers-only CF_HTML,
echo-back duplicates, first-run target capture) and a Menu-key hotkey
suppression fix.

### Pipeline / workflows — new tasks
- **Trigger launcher key** — workflow step that fires a specific launcher cell
  by tab + key, same effect as opening the launcher and pressing the key
  manually. F1-F10 force the function-strip namespace regardless of the
  selected tab. Toast "Launcher cell X:Y is empty." surfaces when the cell
  has no binding so the workflow doesn't silently no-op.
- **Paste clipboard entry** — workflow counterpart of the popup's Ctrl+1..9
  quick-paste: category + 1-based entry index. Empty category string targets
  the unified history (popup's "All" tab). Toast `"{Category} is empty."` /
  `"{Category} has only N entries — can't paste #X."` for the missing cases.
- Extracted `LauncherActionService` from `LauncherWindow.FireCell` so the new
  pipeline task can drive a launcher cell headlessly without instantiating
  the overlay window.
- Workflow editor's `key` dropdown for Trigger launcher key shows
  layout-localized glyphs (Italian `;` → `Ò`, etc.) via the existing
  `KeyboardLayoutMapper.GetDisplayChar` used by the launcher window — new
  `LocalizeOptionsAsLauncherKey` flag on the StringParameter descriptor.

### Wormhole — per-item drop routing
- Dragging a file directly onto a wormhole **tile** now routes by the tile's
  target type:
  - Folder (or `.lnk` pointing at a folder) → drop **inside** that folder
    via the existing shell copy/move flow, with the right-button drag menu
    still applying for Copy/Move/Shortcut choice.
  - Executable / script (`.exe`, `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js`,
    `.wsf`, `.com`) or a `.lnk` to one → launch the target with the dropped
    file as a quoted argument (Photoshop opens a dropped image, a `.bat`
    receives the file as `%1`, etc.).
  - Anything else → bubble to the wormhole container so the existing drop-
    into-the-wormhole-folder behaviour kicks in unchanged.
- `.lnk` resolution uses `IShellLinkW` + `IPersistFile` to walk the shortcut
  to its real target before deciding the routing, so a wormhole tile that is
  a shortcut to a folder behaves like the folder itself.

### Hotkeys — Menu key (VK_APPS) suppression
- Using the Application / Menu key (`VK_APPS = 0x5D`) as a hotkey used to
  still open the Windows context menu under the cursor: the low-level hook
  was suppressing the KEYDOWN but `CallNextHookEx`-forwarding the KEYUP, and
  Windows opens the menu on KEYUP, not KEYDOWN.
- `KeyboardHook` now tracks a per-vk "suppressed KEYUPs" set: when a KEYDOWN
  is matched + swallowed by a hotkey, the corresponding KEYUP for that vk is
  also swallowed on the way back through the hook. Generic enough to fix any
  future KEYUP-triggered system gesture, but the immediate target is VK_APPS.

### Clipboard — paste pipeline fixes
- **HTML / RTF truncation on paste** — `AutoPaster` was sourcing the paste
  text from `record.SearchText`, which is the 256-char + "…" preview written
  by `Win32ClipboardReader.TruncatePreview()` for the FTS index. Long HTML
  or RTF payloads (ASCII art, long pre-blocks) arrived at the target app cut
  short with a trailing ellipsis. AutoPaster now runs the live HTML/RTF
  stripper on the full `record.Payload` and only falls back to SearchText
  when the live extraction returns empty.
- **ASCII-art whitespace** — `ClipboardCleaning.HtmlToPlain` used to replace
  every tag with a space and then collapse `\s+` to a single space, which
  flattened both the newlines and the in-line spaces of any pre-formatted
  block (the user's "ARES9323" ASCII banana from Code.exe came through as a
  single line of single-spaced characters). Block-level tags
  (`<br>`/`<p>`/`<div>`/`<tr>`/`<li>`/`<h1..6>`/`<pre>`/`<blockquote>`/
  `<article>`/`<section>`/`<header>`/`<footer>`/`<nav>`/`<aside>`/`<table>`/
  `<thead>`/`<tbody>`/`<tfoot>`/`<ul>`/`<ol>`/`<dl>`/`<dt>`/`<dd>`/`<figure>`/
  `<figcaption>`) now convert to `\n` before the generic tag-strip, inline
  tags are removed with empty replacement (no spurious space between adjacent
  characters) and the only whitespace collapse left is `\n{3,}` → `\n\n`.
- **CF_HTML header strip** — some producers (Rider64.exe in particular) write
  CF_HTML with only the standard `Version:1.0 / StartHTML:N / EndHTML:N /
  StartFragment:N / EndFragment:N / SourceURL:...` header lines followed by
  raw body text, **without** the `<!--StartFragment-->` / `<!--EndFragment-->`
  comment markers `HtmlToPlain` relied on. The headers were pasting through
  verbatim ("Version:1.0\nStartHTML:0000000128\n…Whitelist"). New preamble
  pass skips header lines (`Word:Value\r\n` with no `<` before the colon)
  whenever the payload starts with `Version:`, so the marker-less case lands
  at the body just like the well-formed one.
- **Re-ingestion of pasted content as a new history item** — `AutoPaster`'s
  own `SetText` / `SetPng` call to publish the chosen item to the system
  clipboard fired `WM_CLIPBOARDUPDATE`, which the clipboard listener picked
  up and stored as a fresh history entry (a `Text` row labelled with the
  paste target's process name). Result: every workflow paste produced a
  visible duplicate of the source item. AutoPaster now takes an optional
  `IClipboardListener` dependency and calls `SuppressNext()` immediately
  before `SetText`/`SetPng`; the listener drops exactly one upcoming
  clipboard update event and the next genuine copy is captured normally.
  Dependency is nullable to keep the paste path working when the clipboard
  module is disabled via Settings → Modules (listener singleton not
  registered in that case).
- **First-run paste-from-shortcut no-op** — `TargetWindowTracker._captured`
  starts at `IntPtr.Zero` and is only primed when the clipboard popup opens.
  Workflows triggered by a hotkey without ever showing the popup hit
  `TryRestoreCaptured() → false` on the first invocation and AutoPaster
  bailed before sending Ctrl+V. `PasteClipboardEntryTask` now mirrors
  `PasteHistoryItemTask`: capture the current foreground window at task
  entry (the keyboard hook didn't change focus, so the foreground at that
  point IS the user's intended paste target) before delegating to AutoPaster.

## [0.1.12] — 2026-05-15

Three pieces of wormhole polish: Explorer-style right-button drag menu, the
crash fix for the Del shortcut on shortcut (.lnk) items, and Cut visual
feedback that fades selected items at 50 % opacity until the clipboard moves
on.

### Wormhole — right-button drag menu
- Dragging a file into a wormhole with the **right** mouse button now opens
  the Explorer-style choice menu on drop: **Copy here / Move here / Create
  shortcut here / Cancel**. Left-button drag keeps the existing implicit
  Move (same volume) / Copy (cross volume) heuristic — same as Explorer.
- `DragDropKeyStates.RightMouseButton` is latched during `OnDragOver` because
  by the time `OnDrop` fires the right button has already been released and
  the flag in the final event has cleared.
- Shortcut creation uses `IShellLinkW` + `IPersistFile` to write a proper
  `.lnk` file named `<name> - Shortcut.lnk` (Explorer's default for this
  gesture), with the target's icon inherited and the working directory set
  to the target's parent folder.

### Wormhole — Del / multi-select crash fix
- Pressing Del on a `.lnk` item (or any multi-selection) inside a wormhole
  used to crash the app with `0xC0000005` inside `SHFileOperation`. The
  `LPWStr` marshaller was truncating the wide-string path list at the first
  embedded NUL — exactly the byte the API uses to separate paths in the
  multi-file list AND to mark the end of the list (double NUL). The kernel
  received only the first path plus a single NUL, then scanned into
  uninitialised memory looking for the terminator.
- Fix: `pFrom` / `pTo` switched from `string` (LPWStr marshalled) to
  `IntPtr`, with manual `Marshal.AllocHGlobal` of a properly
  double-NUL-terminated UTF-16 buffer. Also removed the `Pack = 1` attribute
  on `SHFILEOPSTRUCT` since modern shellapi.h ships the struct with natural
  alignment on x64 — `Pack = 1` was forcing misaligned pointer fields and
  contributing to the access violation.
- Ctrl+V paste path (which also goes through `SHFileOperation` via
  `ShellCopyOrMove`) gets the same treatment as a side benefit.

### Settings — Auto-install error fallback
- The "Automatically install updates when available" path used to swallow
  every download/apply failure silently (offline, GitHub rate-limit, Velopack
  staging-folder ACL, …) because the call was a fire-and-forget Task with no
  exception handler. The user saw no toast, no install, no log entry — the
  failure was invisible.
- The auto-install handler now wraps the `DownloadAndRestartAsync` call in
  `Task.Run` + try/catch. Any exception is logged through `ILogger<App>` and
  falls back to the regular update toast labelled "auto-install failed —
  click to retry", so the user can at least retry manually instead of
  staring at a dead update notification.

### CI / Release infrastructure
- `ci.yml` skip marker renamed from `[skip ci]` to `[skip-ci-only]`. The
  original `[skip ci]` is a GitHub Actions reserved keyword that skips
  EVERY workflow on the push — including `release.yml` when a tag points
  at a commit carrying that marker, which is exactly how the v0.1.12 first
  attempt failed to fire its Release run. The new marker is custom (not on
  the GitHub reserved list) so it only opts out of our own `ci.yml` job
  while letting Release fire normally.

### Wormhole — Cut fade-out
- `Ctrl+X` on a wormhole selection now drops the selected tiles to 40 %
  opacity, matching Explorer's "cut" visual. Cleared automatically when:
  - the user pastes the items somewhere (the receiving app clears the
    clipboard after a Cut+Paste, our `WM_CLIPBOARDUPDATE` listener detects
    the takeover and unsets every IsCutMarked tile);
  - the clipboard's content changes to something that isn't our path list
    (another app's Ctrl+C, the user pressing Ctrl+X again on a different
    selection, etc.).
- New `WormholeItemViewModel.IsCutMarked` bound to the tile Border's
  `Opacity` via the existing `BoolToOpacity` converter; the path set is
  cached on the window (HashSet<string>) so the FileSystemWatcher-driven
  VM rebuild can re-apply the flag without losing the visual state.
- Wired up via `AddClipboardFormatListener` + a window-local
  `WM_CLIPBOARDUPDATE` hook installed in `SourceInitialized` and torn down
  on `Closed`.

## [0.1.11] — 2026-05-15

### Settings — Self-update preferences
- New "Look for updates at startup" checkbox in Settings → App settings →
  Windows integration (default ON). Persisted under `app.updates.check_at_startup`;
  read by `App.OnStartup` to gate the silent `UpdaterService.CheckSilentlyAsync`
  call fired on launch. Turning it off skips the GitHub API roundtrip entirely
  — the manual "Check for updates" button in About stays available.
- New "Automatically install updates when available" checkbox (default OFF).
  Persisted under `app.updates.auto_install`. When ON and the silent check
  surfaces a new release, the updater downloads + applies + restarts without
  the toast prompt; when OFF (the safe default) the existing toast appears so
  the user picks the timing themselves. Auto-install is gated behind the
  startup check toggle — if the startup check is OFF, the auto-install flag
  has no effect.

### Clipboard & Launcher — "Dock window" terminology
- The Clipboard window's `Pinned mode` toggle and the Launcher window's
  `Drag mode` toggle both renamed to `Dock window` (when off) /
  `Undock window` (when on). Same action — keep the window visible past Esc /
  click-out, treat it as a docked surface rather than a transient popup — but
  the label is now consistent between the two windows and reads as a verb the
  user can act on instead of a state label. Italian: "Aggancia finestra" /
  "Sgancia finestra".
- Emoji prefixes (📌 / 📥 / ✓) dropped from the toggle labels — they were
  visual noise on a one-word verb.

### Wormhole — keyboard shortcuts on the items list
- **Del** / **Shift+Del** on a multi-select recycles or permanently deletes the
  selected files via `SHFileOperation` (the same call Explorer uses). Multi
  selection joined with NUL separators feeds the API in one batch; Shift+Del
  raises the shell's "permanently delete?" prompt to match Explorer's gesture.
- **Ctrl+C** copies the selected paths onto the Windows clipboard in
  CF_HDROP format with a `Preferred DropEffect` hint of `1` (Copy). Pasteable
  into Explorer / any other shell-aware app the same way Explorer's own
  Ctrl+C is.
- **Ctrl+X** writes the same CF_HDROP payload with `Preferred DropEffect = 2`
  (Move) so a downstream paste treats it as a cut. AresToys doesn't currently
  render the "fading" cut visual Explorer shows on cut items — left as polish.
- **Ctrl+V** reads the clipboard, sniffs the `Preferred DropEffect`, and
  runs SHFileOperation `FO_COPY` or `FO_MOVE` into the wormhole's source
  folder. The shell handles the conflict-rename prompt + the progress bar
  automatically; on a successful Cut+Paste the clipboard is cleared, matching
  Explorer's behaviour.
- Locked wormholes refuse the mutating gestures (Cut / Paste / Delete) but
  still allow Copy — read-only mode stays a one-way membrane.

### Launcher — context menu on empty cells
- Right-click on an empty launcher cell now hides the entries that have nothing
  to operate on (Open file location, Edit, Copy, Delete) plus the surrounding
  separators. Only Paste stays visible, since it's the one action that makes
  sense on a blank slot (drop a previously-copied cell into the empty spot).
  `BoolToVisibility` converter bound to `HasPath` on every relevant MenuItem.

### Hotkey display — extended virtual-key map
- The HotkeyDisplay used by the Settings → Hotkeys rebind chip + the
  HotkeyCaptureWindow now names a bunch of keys that previously fell through
  to the generic `VK 0xXX` hex fallback:
  - `Num 0` … `Num 9` (VK_NUMPAD0..9, 0x60..0x69)
  - `Num *` / `Num +` / `Num Separator` / `Num -` / `Num .` / `Num /`
    (VK_MULTIPLY..VK_DIVIDE, 0x6A..0x6F)
  - `NumLock` (0x90), `CapsLock` (0x14)
  - `Menu` (VK_APPS = 0x5D, the context-menu key between Ctrl-right and the
    Win-right key on most layouts)
  - `< / >` (VK_OEM_102 = 0xE2, the ISO 102nd-key found between Shift-left
    and Z on italian / german / french ISO layouts — absent on US ANSI)

## [0.1.10]

Inner / outer border split, editor toolbar that survives narrow windows, and
Wormhole inline search. The Wormhole feature shipped in 0.1.9 has its right-
click menu replaced by the native Windows shell menu (with dark theming via
the undocumented uxtheme ordinals 133/135/136) — every third-party verb the
user has installed (7-Zip, Send to, Open with…, Properties, …) is reachable
in a single right-click instead of the curated 6-entry menu.

### Theme — Outer / Inner border palette
- The single `BorderBrush` key introduced in 0.1.9 splits into
  `OuterBorderBrush` (window-frame accent telaio, sidebar separator,
  prominent surface edges) and `InnerBorderBrush` (1 px swatch frames,
  divider lines between sub-sections in the same card, list-row separators
  in clipboard / wormhole / launcher panels). Defaults: outer `#4A4A4A`,
  inner `#2D2D2D` (matches Surface3 so an inner edge reads as a one-step-up
  tone, never a hard accent line).
- Both colours are tunable from Settings → App theme. Every preset gets an
  outer + inner hex pair tuned to the preset's accent family.
- 50+ usage sites moved to the new keys: launcher F-row / tab strip /
  QWERTY cells (inner), wormhole window telaio (outer), bg-remover sub
  cards (inner), clipboard history + preview panes (inner), 8 theme tab
  swatch borders (inner), gradient editor stops + presets (inner), icon
  picker cells (inner).

### Editor — toolbar in the titlebar
- Tool palette stays inline in the titlebar header but the ScrollViewer
  now has its `MaxWidth` recomputed on every `SizeChanged` (`titleBar
  ActualWidth - 314 px` of reserved chrome for icon + title + caption
  buttons). Min/Max/Close stay reachable at any window width — no more
  hidden caption buttons on narrow editors.
- Vertical wheel ticks inside the toolbar map to horizontal scroll
  (~48 px per tick = exactly one tool button). The OS scroll bar is hidden
  in the title chrome but the wheel still works.
- Selecting a tool (mouse click OR keyboard shortcut R / A / T / V / …)
  brings the matching button into view via `BringIntoView`, so the active
  tool can never sit behind the overflow.
- Save / Save as / Cancel are now pinned at the top of the property
  column via `DockPanel.Dock=Top` — a tall properties section can't push
  the primary commit/cancel controls off-screen any more.

### Wormhole — inline search
- New magnifying-glass button in the wormhole header expands into an
  inline TextBox; typing filters the visible icons case-insensitively
  against `DisplayName`. 250 ms debounce on every keystroke so the
  ItemsControl re-filter doesn't lag on folders with thousands of items.
- Esc collapses the search and restores the full grid; Enter commits +
  drops focus while keeping the textbox visible so the user sees the
  active filter and can clear it.
- Filter pipes through `CollectionViewSource.GetDefaultView(_items)` with
  a `Filter` predicate — re-using the same view the ListBox iterates so a
  refresh costs one `view.Refresh()` call instead of rebuilding the
  ObservableCollection.

### Wormhole — Windows shell context menu
- Right-click on an item shows the native Windows shell context menu (the
  same menu Explorer would show), with full support for third-party verbs:
  Open with…, Send to, 7-Zip, Properties, Git GUI, Pin to Quick Access,
  every shell extension the user has installed. Wired via
  `IContextMenu` + `IContextMenu2` / `IContextMenu3` so the owner-drawn
  submenus (Send to, Open with) actually render — the WM_INITMENUPOPUP /
  WM_DRAWITEM / WM_MEASUREITEM / WM_MENUCHAR messages are forwarded
  through an HwndSource hook to the COM object.
- Dark-themed menu chrome on Windows 10 1903+ via the undocumented
  uxtheme ordinals `SetPreferredAppMode(2)` + `AllowDarkModeForWindow` +
  `FlushMenuThemes` (same trick Notepad / Calculator use). Older Windows
  silently falls back to the system default (light) menu.
- Removes the curated 6-entry menu (Open / Open file location / Copy
  path / Move to / Rename / Delete) since every entry exists in the
  native menu and a few more (Cut, copy path, properties, third-party).
  -270 lines of UI plumbing trimmed.

### Capture region — double-fire cooldown
- 400 ms cooldown between consecutive region-overlay opens absorbs the
  cold-start "double trigger" the OS keyboard hook sometimes delivers on
  the very first hotkey press after launch. Without this the first
  Win+Shift+S used to open the overlay twice — the second on top of the
  first, showing a phantom "all desktop" selection the user had to Esc
  through. Mirror guard in `CaptureCoordinator.CaptureRegionAsync` for
  the tray-driven path.

### Wormhole geometry — drag-to-scrub
- (Promoted from 0.1.9 polish.) X / Y / W / H cells in the Settings →
  Wormholes grid now act like Unreal / Blender drag fields: press the
  left button and drag horizontally to scrub the integer value (1 px of
  drag = 1 unit). A plain click still focuses the cell for keyboard
  editing — the scrub only kicks in past a 4 px movement threshold so
  the gestures don't collide. Hover shows the SizeWE cursor as a
  discoverability cue. Live binding so the wormhole window moves /
  resizes frame-by-frame, not on LostFocus.

### Bug fixes
- Localised month name (`%mon` token in the sub-folder pattern) now
  honours the app UI culture instead of always emitting English. Italian
  user typing `%mon` gets `Maggio`, not `May`. Numeric tokens stay on
  `InvariantCulture` so paths remain portable.

## [0.1.9] — 2026-05-15

The Wormholes feature lands. Stardock-Fences-style floating windows that
mirror a real filesystem folder: drop a folder onto the desktop, get a
draggable / lockable / collapsible grid of icons that stays in sync with the
folder via FileSystemWatcher. Persistent JSON store, multi-window, all-sides
resize, Ctrl+Wheel zoom per-wormhole, opacity + tile padding settings,
Explorer right-click "Create Wormhole" verb, error state + relink dialog
when the source folder goes offline. Triggered as a workflow step (Hide all
/ Show all / Lock / Unlock / Collapse / Uncollapse / Toggle / Create) so the
existing pipeline + hotkey machinery drives every batch operation.

Beyond Wormholes: editor opens at native 1:1 with DPI awareness, sub-folder
pattern now reaches SVG saves + screen recordings (was screenshot-only),
Trace-to-SVG picks from the same preset list as the standalone trace window,
50+ hardcoded greys throughout the app moved onto themed brushes, and the
workflow editor gets per-uploader Upload entries + 3 discrete Copy-text
variants (URL / file path / SVG path).

### Wormholes
- New floating-window subsystem: `WormholeWindow` (transparent FluentWindow,
  custom chrome with chevron + lock + hamburger menu + close, all-sides
  resize via WM_NCHITTEST hook, DragMove on header, double-click rolls to
  header-only height), `WormholeWindowManager` (singleton DI service that
  owns the live windows, persists geometry on every drag/resize/lock,
  enforces multi-monitor work-area clamps), `WormholeStoreJson`
  (atomic-rename JSON store at `%LOCALAPPDATA%\AresToys\wormholes.json`),
  `FolderWatcher` (FileSystemWatcher per portal, debounced refresh of the
  ListBox).
- Icons come from `IconService.ExtractIconAtSize` using `SHGetImageList` +
  `ImageList_GetIcon` (no IImageList COM marshalling) with overlay
  composition (`INDEXTOOVERLAYMASK`) so .lnk shortcuts get the corner arrow
  the rest of Explorer shows. Icon size is per-wormhole (Ctrl+Wheel zoom
  saves into the record) with a global default in Settings.
- Text wraps to 2 lines in a Grid (row 0 Auto = icon, row 1 * = label
  TextWrapping=Wrap with explicit Width=TileWidth) so the WrapPanel layout
  doesn't shrink-wrap and break wrap measurement.
- Per-wormhole tile padding setting → controls density (0 = Portals-style
  dense, 32 = airy). Per-wormhole opacity (30 - 100 %) sets `OuterFrame.
  Opacity`; the `BodyBackdrop` + `HeaderBackdrop` carry the opacity so
  icons stay full-strength.
- 3-phase batch operations for "Hide all" / "Show all" / "Lock all" / etc:
  mutate the in-memory records, run a tight per-record UI spawn / close
  loop, then a single `FlushAsync` to persist — the old per-record
  SaveAsync+ReconcileAsync ran serially through a SemaphoreSlim and made
  "Show all" appear as a gradual cascade.
- New workflow tasks: `arestoys.wormholes.hide-all`, `show-all`, `lock-all`,
  `unlock-all`, `collapse-all`, `uncollapse-all`, `toggle-hide`,
  `toggle-lock`, `toggle-collapse`, `create-wormhole`. Bound to user
  hotkeys via the existing pipeline-trigger machinery.
- Explorer integration:
  - **Folder right-click "Create Wormhole"** — `HKCU\Software\Classes\
    Directory\shell\` registration. Single-instance pipe IPC forwards the
    folder path to the running AresToys when applicable, else cold-start
    via `--create-wormhole` CLI flag.
  - **Background-click verb** opens the NewWormhole dialog instead of
    auto-creating, so the user can pick a different folder than the one
    they right-clicked on.
- Error state: wormhole window with missing source folder paints a
  warning chrome and surfaces a "Relink…" button (`OpenFolderDialog`) so
  the user can repoint to the moved folder without recreating the
  wormhole + losing geometry.
- Module gate in `app.modules.wormholes` (defaults OFF) so the feature is
  opt-in until 0.1.10's polish lands; no startup cost when disabled.
- Wormhole types unified into a single "folder portal" — the
  Data/Shortcuts variant introduced in early prototypes is gone entirely
  (the user requested it: a Shortcuts wormhole was just a Folder portal
  pointing at a hidden internal folder full of .lnk files, so the second
  type was redundant). Persisted records without a `Portal` block are
  migrated transparently.
- Drag-to-scrub on X / Y / W / H in the Settings → Wormholes grid: press
  the left button and drag horizontally to scrub the value Unreal /
  Blender style. A plain click still focuses for keyboard editing.

### Editor
- **Open editor — always 1:1**, regardless of fullscreen mode. The
  workflow task's `fullscreen` flag now only controls window size
  (maximise on active monitor vs. fit-to-image), the canvas zoom is
  fixed at native pixel density. DPI-aware: `_zoom = 1 / dpiScaleX`
  so one image pixel maps to one physical screen pixel on
  125 % / 150 % / 200 % displays.
- Non-fullscreen `Open editor` pre-sizes the window to the captured
  image dimensions + chrome budget (~300 px wide + ~120 px tall, clamped
  to the work area + a 800 × 600 floor) so a small screenshot opens in
  a tight window instead of the legacy fixed 1200 × 820.
- Recording coordinator now writes `.mp4` + `.gif` into the same folder
  + sub-folder pattern as screenshots (was hardcoded to `%USERPROFILE%
  \Pictures\AresToys`). SaveSvgTask too: when a Save-as-Image step ran
  first, the `.svg` sits next to the `.png`; otherwise the sub-folder
  pattern is resolved fresh from `capture.subfolder_pattern`.

### Workflow editor — UX
- **Sticky header**: Back / workflow name / hotkey / "Pipeline steps"
  caption stay pinned at the top while the steps list scrolls — long
  pipelines with 8 + steps no longer hide the rebind chip and the save
  button.
- **Hotkey chip** aligned vertically to the workflow-name TextBox height
  (Style template gained `VerticalAlignment="Center"`).
- **"Copy text to clipboard" split into 3 discrete catalog entries** with
  distinct `LocalizationKey` per row so the catalog picker shows the
  three variants by their actual purpose: Copy URL to clipboard, Copy
  file path to clipboard, Copy SVG path to clipboard. All three route to
  the same `arestoys.copy-text-to-clipboard` task with different bag-key
  templates (`{bag.upload_urls}` / `{bag.local_path}` /
  `{bag.svg_local_path}`).
- **Per-uploader Upload entries**: each uploader gets its own catalog row
  with `LocalizationKey="arestoys_upload_to_<id>"` so the picker shows
  "Upload to Imgur", "Upload to Catbox", … instead of N rows labelled
  identically "Upload".
- New Wormholes category in Hotkeys & Workflows tab; templates for
  3 toggle workflows (Hide / Lock / Collapse all) + Create Wormhole.

### Capture
- New `arestoys.save-svg` task (Save as SVG): pairs with Trace-to-SVG to
  drop the `.svg` into the same folder as the raster Save-as-Image. Bag
  key `svg_local_path` so a downstream Copy-text step can `{bag.
  svg_local_path}` into the clipboard.
- `arestoys.save-to-file` renamed `Save as Image file` in the catalog to
  pair visually with Save as SVG.
- `arestoys.trace-to-svg` task takes a preset string (from `TracePresets.
  Stock` + user-saved `TracePresetStore`) instead of the old `colors`
  int — matches the picker in the standalone Trace window. Legacy
  `colors` field kept readable for back-compat.
- Region capture default workflow gets `Win+Shift+S` baked into the
  preset; Multi-region capture preset added with `autoConfirm=false`
  and no default hotkey.
- Sub-folder pattern field in Capture Settings: token chips (Year /
  Month / MonthName / Day / Hour / …) replace the wall-of-text token
  reference; clicking a chip inserts the token at the caret without
  stealing focus from the TextBox.
- Sub-folder pattern field validates / sanitises input on keystroke +
  paste: invalid file-name chars (`<>:"/\\|?*` + `Path.GetInvalidFile
  NameChars()`) blocked on typing, stripped on paste.
- **`%mon`** (full month name) extracted into a shared
  `DatePatternExpander.Expand(pattern, now)` helper consumed by
  `SaveToFileTask`, `SaveSvgTask`, and `RecordingCoordinator` — three
  copy-pasted implementations collapsed into one. Token order now matters
  (longest-first) so `%mon` doesn't get eaten by the `%mo` replace and
  emit `05n` instead of `May`.

### Theme — colour audit
- ~50 hardcoded greys / borders across the app moved to themed
  DynamicResource bindings. Targets:
  - `MainWindow.xaml`: `#666` / `#777` / `#888` / `#999` / `#AAA` →
    `AccentForegroundDarkBrush`; `#DDD` → `AccentForegroundBrush`;
    `#333` / `#444` → `Surface3Brush`; `#0F0F0F` → `Surface1Brush`.
  - 8 dialog windows (`PinSourceChooser`, `SxcuImport`, `TabTitle`,
    `WindowPicker`, `UploaderConfig`, `QrCode`, `IconPicker`,
    `ResizeDialog`): `Background="#1E1E1E"` / `#252525` →
    `Surface2Brush`; `BorderBrush="#3A3A3A"` / `#444` /
    `#555` → `Surface3Brush`.
  - `EditorWindow.xaml`: canvas backdrop `#1A1A1A` → `Surface1Brush`;
    3 sidebar dividers `#333` → `Surface3Brush`.
  - `ToastWindow.xaml` + `PinnedImageWindow.xaml`: text greys onto
    `AccentForegroundBrush`.
- Excluded by design (need to read against arbitrary colours): the colour
  picker primitives (`ColorChannelSlider`, `ColorSquareControl`,
  `ColorWheelControl`, `ColorSwatchButton`), `RegionOverlay` and
  `RecordingOverlay` semaphore colours (red REC, green Confirm, blue
  region selection).

### Capture region — quality of life
- "Copy to clipboard" task split into 3 discrete entries in the workflow
  catalog (URL / file path / SVG path) — see the Workflow editor section
  above.
- Subfolder pattern fix: `SaveSvgTask` + `RecordingCoordinator` were
  hardcoded to `%USERPROFILE%\Pictures\AresToys`, ignoring the user's
  `capture.subfolder_pattern`. Both now resolve the same way
  `SaveToFileTask` does (`capture.folder` + `capture.subfolder_pattern`
  + token expansion via the shared `DatePatternExpander`).

### Release flow + tooling
- New `tools/release.ps1` + `release.bat` shim drive the whole tag-and-push
  flow in one go: reads `<Version>` from `AresToys.App.csproj` (strips
  `-dev`), refuses to run on a dirty working tree, refuses if the tag
  already exists locally or on origin, runs `test-local.ps1`, asks for
  confirmation, then creates the annotated tag and pushes it (which fires
  the on-tag trigger added in 0.1.8's `release.yml`). Double-click
  `release.bat` from Explorer or run `pwsh tools/release.ps1` from a
  terminal. Flags: `-SkipTests`, `-SkipConfirm`, `-Version X.Y.Z`
  (override), `-Force` (allow dirty tree). If the push fails the local tag
  is removed automatically so the next run starts clean.

## [0.1.8] — 2026-05-13

Clipboard window gets three CopyQ-flavoured upgrades: optional per-item labels
that replace the auto-derived snippet in the row title, drag-from-row-to-
category-tab as a shortcut for the right-click Move-to menu, and user-controlled
reorder of the pinned strip via both drag-drop and on-hover chevron buttons.
Two additive schema migrations ship together — `Migration002` adds the `label`
column and rebuilds the FTS5 index so search matches across content + label,
`Migration003` adds `pin_sort_order` for the reorder gesture. Both run once on
first launch under the existing schema_version gate. Settings backup format
bumps to v3 to carry the new label field; v2 backups (including the legacy
`shareq-settings-*.json` snapshots) continue to import unchanged.

### Release flow + tooling
- `release.yml` now accepts a second trigger alongside the existing manual
  `workflow_dispatch`: pushing a tag matching `v*` (e.g. `git tag v0.1.8 &&
  git push origin v0.1.8`) cuts the release with the tag's version. The
  leading `v` is stripped; a SemVer pre-release suffix (`v0.1.8-rc1`) auto-
  marks the GitHub Release as pre-release so no extra flag is needed. Both
  paths share the same vpk pack → GitHub Release draft pipeline via a single
  "Resolve release version" step that normalises the version + prerelease
  flag into env vars consumed by every downstream step.
- New `tools/test-local.ps1` mirrors the test step of `ci.yml` so you can
  validate before pushing the tag. Default is Release configuration
  (matches CI), `-Project Storage` filters to a single test project,
  `-SkipBuild` reuses the previous build output. Exit code is non-zero on
  any failure so it can gate a release script.

### Clipboard — pinned reorder
- Pinned items now keep an explicit per-item sort order (new `pin_sort_order`
  column added in Migration 003). Order is
  user-controlled: new pins land at the bottom of the pinned strip
  (MAX(pin_sort_order)+1) instead of jumping to the top via the created_at
  tiebreaker, and existing pins can be rearranged freely.
- Two gestures:
  - Drag a pinned row onto another pinned row → insert-before. Source moves
    to the target's slot, target and everything below shifts down by one.
    Same `arestoys.clipboard.item` drag payload as the category-tab drop,
    different drop target (the row Border). Mixed pinned↔unpinned drops are
    silently ignored.
  - Hover a pinned row → two chevron buttons (FA chevron-up / chevron-down)
    appear on the right of the row. Click swaps with the adjacent pinned
    neighbour. MultiDataTrigger on `Pinned + IsMouseOver(RowBorder)` so the
    chevrons stay invisible at rest and don't add ingombro to non-pinned rows.
- Chevron clicks suppress drag-arming on the row Border (visual-tree walk for
  `ButtonBase` ancestor) so a quick mouse-jiggle while clicking a chevron
  doesn't get reinterpreted as a drag.
- `IItemStore.ReorderPinnedAsync(IReadOnlyList<long> orderedIds, ct)` applies
  the new sequence in a single transaction with parameterised UPDATEs and
  fires one broadcast `Updated(-1)` event so the popup re-queries once.

### Clipboard — drag-to-category
- Drag any row from the history list onto a category tab header to move the
  item into that category. Equivalent to right-click → "Move to → <name>",
  but doesn't require opening the menu. Custom drag format
  `arestoys.clipboard.item` keeps the payload distinct from Explorer file
  drops or text drags so an accidental drop from another app is ignored.
  Drop onto the currently-active category is a silent no-op (no UPDATE, no
  refresh). Honours the OS drag threshold (`SystemParameters.Minimum*DragDistance`)
  so a plain click never gets reinterpreted as a drag.

### Clipboard — per-item labels (CopyQ "Notes" equivalent)
- New optional `Label` field on every clipboard item. When set, the label
  replaces the auto-derived snippet in the row title and a small `📎` glyph
  marks the row as labelled. Useful when the list fills up with similar-
  looking blobs (Unreal node graphs, JSON dumps, hex hashes) that are
  indistinguishable by their first 200 chars.
- Three input gestures, all wired to the same persist path: right-click →
  "Rename label", F2 on the selected row, or the always-on TextBox above the
  preview pane on the right. Inline rename commits on Enter, cancels on Esc
  / LostFocus; the preview pane TextBox commits on LostFocus or Enter
  (asymmetry is intentional — the inline editor is an explicit mode, the
  preview pane is the natural resting target for the selected row).
- New App Settings → Clipboard → "Show content snippet under label"
  checkbox. Default off — the label replaces the snippet entirely, matching
  CopyQ. When on, labelled rows render a dimmer secondary line with the
  original content snippet so the user keeps both at a glance.
- Search (Ctrl+F) matches across label AND content. The FTS5 virtual table
  was rebuilt with the new `label` column so labelled rows stay findable by
  their original content snippet alongside the user-typed name.
- v3 backups loaded by hypothetical older builds drop the label field via
  standard "ignore unknown JSON properties" semantics — no crash, just a lost
  label.

## [0.1.7] — 2026-05-13

Line and Arrow tools unified into a single primitive with per-end cap toggles
(Start cap, End cap) and a pickable tip style — ShareX-style curved cap (default,
the V with concave base that integrates with the stroke) or solid filled triangle.
Same caps mechanism applied to Freehand. Toolbar keeps the two intent buttons (L
for Line, A for Arrow) and their muscle memory: clicking Arrow seeds a fresh
EndCap=true gesture, clicking Line seeds both off. Editor checkboxes now follow
the AresToys accent theme instead of the WPF-UI default teal. Three first-wave
bug reports also fixed in this release — see the Bug fixes section below.

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

### Bug fixes
- Background removal: brush preview ring now scales with the zoom factor in
  addition to the fit-to-window scale, so the on-screen ring keeps matching
  the actual painted footprint when zoomed in or out. The ring also refreshes
  immediately on Ctrl+Wheel zoom and on "Reset view" instead of waiting for
  the next mouse-move.
- Color picker: opening the eyedropper from a swatch inside the editor now
  uses the same full-screen `ScreenColorPickerOverlay` magnifier the tray
  sampler / wheel / trace tool / image-effects swatches show, instead of
  silently switching the canvas to a crosshair cursor. The previous
  hide-the-picker-and-sample-the-canvas dance also crashed the picker on the
  next OK click because `.NET 10` resets `Window._showingAsDialog` when
  `Show()` is called on a hidden `ShowDialog`'d window, so the eventual
  `DialogResult = true` threw "DialogResult can be set only after Window is
  created and shown as dialog". The overlay opens nested-modal on top of the
  picker — the picker's own modal pump stays untouched.
- Editor: the Copy (Confirm) button no longer produces a horizontally shifted
  PNG with transparent pixels on one side. `CanvasPngExporter` was running
  `UpdateLayout()` after its direct `Measure` / `Arrange` calls, which let the
  hosting `ScrollViewer`'s pending layout work re-Arrange the canvas at a
  parent-driven offset. Settling the layout queue before the direct calls
  keeps the export consistent regardless of whether a modal dialog ran in
  between (which is why Save-as appeared to "just work").
- Launcher: importing a backup that overwrites `launcher.state` no longer
  leaves the launcher grid empty until the user edits a cell. The app pre-warms
  a `LauncherWindow` at startup and gates `PrepareAsync` reloads behind a
  monotonic `LauncherStore.StateVersion` counter; direct `ISettingsStore`
  writes in `SettingsBackupService.ImportAsync` bypassed `SaveAsync` and left
  the counter stale, so the next open kept rendering the pre-import (empty)
  snapshot. Import now bumps the version explicitly when it touches
  `launcher.state` / `launcher.cells`.
- Launcher: switching tabs after importing a large set of cells no longer
  freezes the UI for several seconds per first visit. `RebuildActiveTab`
  resolves each cell's icon synchronously on the dispatcher, and the imported
  cells point at slow paths (OneDrive online-only, `.lnk` Start-Menu entries,
  unreachable network shares) where `SHGetFileInfo` blocks. `PrepareAsync` now
  pre-warms `IconService`'s cache for every tab on a background thread after
  loading state, so the per-cell extraction cost is paid once off-thread
  instead of 30× synchronously on each first tab switch.



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
