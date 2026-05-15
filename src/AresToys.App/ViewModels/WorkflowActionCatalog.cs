using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;

namespace AresToys.App.ViewModels;

/// <summary>Declarative description of a single integer-valued config parameter the user can edit
/// inline on a step row.</summary>
public sealed record IntParameter(string Key, string Label, int DefaultValue, int Min, int Max);

/// <summary>Declarative description of a boolean config parameter — rendered as a checkbox on the
/// step row, persisted under <see cref="Key"/> in the step's JSON config.</summary>
public sealed record BoolParameter(string Key, string Label, bool DefaultValue);

/// <summary>Hint to the workflow editor about which (if any) system picker buttons to render
/// next to a <see cref="StringParameter"/>'s text box. The text box itself remains editable
/// — pickers just populate it from a dialog so the user doesn't have to type long paths.</summary>
public enum StringPickerKind
{
    /// <summary>No picker — plain text box.</summary>
    None,
    /// <summary>📄 Browse… opens a file-open dialog and writes the chosen path back.</summary>
    File,
    /// <summary>📁 Browse… opens a folder-open dialog and writes the chosen path back.</summary>
    Folder,
    /// <summary>Both 📄 and 📁 buttons — for parameters that may target either.</summary>
    FileOrFolder,
}

/// <summary>Declarative description of a free-form string config parameter — rendered as a text
/// box on the step row. <see cref="Placeholder"/> shows as a hint when the value is empty
/// (paths, args, commands). <see cref="Picker"/> requests file/folder browse buttons
/// alongside the text box. The text is persisted verbatim under <see cref="Key"/> in the
/// step's JSON config. When <see cref="OptionsKey"/> is set the editor renders a ComboBox
/// instead of a TextBox, populated by the matching entry in
/// <see cref="WorkflowActionCatalog.OptionsProviders"/> (e.g. <c>"image_effect_presets"</c>).</summary>
public sealed record StringParameter(
    string Key,
    string Label,
    string DefaultValue,
    string? Placeholder = null,
    StringPickerKind Picker = StringPickerKind.None,
    string? OptionsKey = null,
    /// <summary>When false the editor renders a pure selector (no typing). Defaults to true
    /// for the legacy "preset_name"-style parameters where the user can name a preset that
    /// doesn't exist yet. Pure selectors (image format, editor tool) set this to false.</summary>
    bool IsEditable = true,
    /// <summary>When true, dropdown labels are pulled from the EnumValue_&lt;raw&gt; resx
    /// keys via <see cref="Services.ImageEffectLocalizer.LocalizeEnumValue"/>. The Value
    /// stored in step.Config remains the raw enum name so .sxie / DB round-trip stays
    /// stable.</summary>
    bool LocalizeOptionsAsEnum = false);

/// <summary>One entry in the "+ Add step" picker for workflows. Maps a pipeline task id to
/// human-readable metadata + a default config to apply when the user adds the action.</summary>
public sealed record WorkflowActionDescriptor(
    string TaskId,
    string DisplayName,
    string Description,
    string Category,
    /// <summary>True for steps the executor needs to run but aren't user-pickable (e.g. URL persistence
    /// after upload). They're hidden from the workflow editor and from the Add picker.</summary>
    bool IsPlumbing = false,
    /// <summary>JSON config string applied as default when the user adds this action via the picker.</summary>
    string? DefaultConfigJson = null,
    /// <summary>If set, the editor renders an inline integer input on the step row, bound to the
    /// matching key in <see cref="System.Text.Json.Nodes.JsonNode"/> step config.</summary>
    IntParameter? IntParameter = null,
    /// <summary>Optional list of boolean toggles rendered as checkboxes on the step row. Each
    /// entry's <see cref="BoolParameter.Key"/> is the JSON property the checkbox writes to.</summary>
    IReadOnlyList<BoolParameter>? BoolParameters = null,
    /// <summary>Optional list of free-form text inputs rendered on the step row (paths, args,
    /// shell commands). Each entry's <see cref="StringParameter.Key"/> is the JSON property
    /// the text box writes to.</summary>
    IReadOnlyList<StringParameter>? StringParameters = null,
    /// <summary>Override key for resx lookups (Title + Description). Use when multiple
    /// descriptors share the same <see cref="TaskId"/> (e.g. upload-by-category, press-key
    /// Enter/Tab, record-screen mp4/gif) so each variant can have a distinct translation.
    /// When null the helper falls back to a sanitised TaskId.</summary>
    string? LocalizationKey = null);

public static class WorkflowActionCatalog
{
    /// <summary>Dynamic-options resolvers keyed by <see cref="StringParameter.OptionsKey"/>.
    /// Populated at app startup (<c>App.xaml.cs</c>) so the catalog itself stays free of
    /// service-locator / DI plumbing. The provider is invoked once per step row at the
    /// moment the row is materialised — a fresh open of Settings → Workflows reflects any
    /// new entries (preset list, etc.). Callers that need live updates have to rebuild the
    /// row.</summary>
    public static readonly Dictionary<string, Func<IReadOnlyList<string>>> OptionsProviders =
        new(StringComparer.Ordinal);

    public static readonly IReadOnlyList<WorkflowActionDescriptor> All =
    [
        new("arestoys.capture-region",
            "Capture region",
            "Show the region selection overlay and capture the chosen rectangle. Skipped automatically when a payload is already in the bag (e.g. fullscreen / monitor entry-points).",
            "Capture",
            // Default true: the multi-region workflow (drag-drag-drag-Enter) is the exception,
            // not the rule — most users want a one-drag screenshot. Existing workflows that
            // were saved with autoConfirmOnFirstSelection=false keep their explicit value.
            DefaultConfigJson: "{\"autoConfirmOnFirstSelection\":true}",
            BoolParameters: new[]
            {
                new BoolParameter("autoConfirmOnFirstSelection", "Auto-confirm on first selection (skip multi-region)", true),
            }),

        new("arestoys.capture-active-window",
            "Capture active window",
            "Snapshot the currently-foreground window using DWM extended-frame-bounds (no resize-border padding). Honours the global capture delay; own-process windows are skipped so Settings / popup never become the target.",
            "Capture",
            IntParameter: new IntParameter("delay_seconds", "Delay (s)", 0, 0, 30)),

        new("arestoys.capture-active-monitor",
            "Capture active monitor",
            "Snapshot the monitor currently under the mouse cursor — useful as a hotkey on multi-monitor setups. On single-monitor it just captures the whole screen.",
            "Capture",
            IntParameter: new IntParameter("delay_seconds", "Delay (s)", 0, 0, 30)),

        new("arestoys.capture-webpage",
            "Capture webpage",
            "Render a URL in a hidden WebView2 and grab a full-page PNG (everything below the fold included). Leave URL empty to be prompted at runtime; set it for fully-automated 'snapshot example.com' workflows. Login-walled pages won't render protected content.",
            "Capture",
            StringParameters: [new StringParameter("url", "URL", string.Empty, Placeholder: "https://example.com (leave empty to prompt)")]),

        new("arestoys.record-screen",
            "Start/stop screen recording (mp4)",
            "Toggle FFmpeg-driven screen recording in mp4 format. First invocation starts, second stops and produces the file.",
            "Capture",
            DefaultConfigJson: "{\"format\":\"mp4\"}",
            LocalizationKey: "arestoys_record_screen_mp4"),

        new("arestoys.record-screen",
            "Start/stop screen recording (gif)",
            "Toggle FFmpeg-driven screen recording in animated GIF format. First invocation starts, second stops and produces the file.",
            "Capture",
            DefaultConfigJson: "{\"format\":\"gif\"}",
            LocalizationKey: "arestoys_record_screen_gif"),

        // Wormholes batch ops — one task class (WormholeBatchOpTask) routed by the "op" config
        // value; six rows here so the workflow editor's "+ Add" menu lists each as a discrete
        // entry. Same pattern as record-screen mp4 / gif.
        new("arestoys.wormhole-batch-op",
            "Wormholes: hide all",
            "Set IsHidden=true on every wormhole — closes their live windows but keeps the records.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"hide-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Wormholes: show all",
            "Set IsHidden=false on every wormhole — re-spawns the live windows for any that were hidden.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"show-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Wormholes: lock all",
            "Lock geometry on every wormhole — drag and resize disabled until unlocked.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"lock-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Wormholes: unlock all",
            "Unlock every wormhole so they're draggable / resizable again.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"unlock-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Wormholes: collapse all",
            "Roll up every wormhole to its header strip only — quick way to reclaim desktop space without hiding.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"collapse-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Wormholes: uncollapse all",
            "Restore every rolled-up wormhole to its previous height.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"uncollapse-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Wormholes: toggle hide / show",
            "If any wormhole is currently visible, hide them all; otherwise show them all. Single-key 'clean up the desktop / bring them back' gesture.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"toggle-hide\"}"),
        new("arestoys.wormhole-batch-op",
            "Wormholes: toggle lock / unlock",
            "If any wormhole is unlocked, lock them all; otherwise unlock them all.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"toggle-lock\"}"),
        new("arestoys.wormhole-batch-op",
            "Wormholes: toggle collapse / uncollapse",
            "If any wormhole is uncollapsed, collapse them all; otherwise uncollapse them all.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"toggle-collapse\"}"),
        new("arestoys.wormhole-create",
            "Wormholes: create wormhole",
            "Create a new wormhole. If a single folder is selected in the foreground Explorer window, uses that folder automatically. Otherwise opens the New Wormhole dialog so the user picks a folder.",
            "Wormholes"),

        new("arestoys.color-sampler",
            "Color sampler",
            "Open the magnifier-style sampler at the cursor — picks a pixel from anywhere on screen. The sampled color is copied to the clipboard.",
            "Capture"),

        new("arestoys.color-picker",
            "Color picker",
            "Open the dialog-style HSB/RGB/CMYK colour picker (wheel + numeric inputs). The chosen color is stashed in the bag — pair with a Copy color as … step to write it to the clipboard.",
            "Capture"),

        // Copy-color-as family: read the bag colour produced by Color sampler / Color picker and
        // emit it in a specific format. One step per format keeps the workflow editor's "+ Add"
        // menu friendly — no JSON config dropdowns needed.
        new("arestoys.copy-color-hex",
            "Copy color as Hex",
            "Emit the bag colour as RRGGBB (or RRGGBBAA with alpha). Toggles below choose whether to include alpha and whether to prefix with #.",
            "Color",
            DefaultConfigJson: "{\"alpha\":false,\"hash\":false}",
            BoolParameters: new[]
            {
                new BoolParameter("alpha", "Include alpha (RRGGBBAA)", false),
                new BoolParameter("hash",  "Prefix with #",            false),
            }),
        new("arestoys.copy-color-rgb",     "Copy color as RGB",     "Emit the bag colour as rgb(R, G, B) to the clipboard.",                          "Color"),
        new("arestoys.copy-color-rgba",    "Copy color as RGBA",    "Emit the bag colour as rgba(R, G, B, A) with alpha 0–1 to the clipboard.",       "Color"),
        new("arestoys.copy-color-hsb",     "Copy color as HSB",     "Emit the bag colour as hsb(H°, S%, B%) to the clipboard.",                       "Color"),
        new("arestoys.copy-color-cmyk",    "Copy color as CMYK",    "Emit the bag colour as cmyk(C%, M%, Y%, K%) to the clipboard.",                  "Color"),
        new("arestoys.copy-color-decimal", "Copy color as Decimal", "Emit the bag colour as the packed AARRGGBB integer to the clipboard.",           "Color"),
        new("arestoys.copy-color-linear",  "Copy color as Linear",  "Emit (R=…,G=…,B=…,A=…) — Unreal Engine FLinearColor stringification — to the clipboard.", "Color"),
        new("arestoys.copy-color-bgra",    "Copy color as BGRA",    "Emit (B=…,G=…,R=…,A=…) — Unreal Engine FColor stringification — to the clipboard.", "Color"),

        new("arestoys.open-editor-before-upload",
            "Open editor",
            "Pause the pipeline and open the annotation editor on the captured bytes. On save, subsequent steps see the edited image.",
            "Editor",
            // No default tool — empty string = "use last-used", which is what the user expects
            // for an "open editor" step that doesn't dictate a starting mode. Specific presets
            // (e.g. a quick-crop pipeline) can pin "Crop" / "Rectangle" / etc. via DefaultConfigJson.
            DefaultConfigJson: "{\"fullscreen\":false,\"default_tool\":\"\"}",
            BoolParameters: new[]
            {
                // Fullscreen on the active monitor (the one currently under the cursor) +
                // force fit-to-viewport so the image fills the window regardless of size. Off
                // by default to keep the legacy windowed behaviour for existing presets.
                new BoolParameter("fullscreen", "Open fullscreen on active monitor (fit to screen)", false),
            },
            StringParameters: [new StringParameter("default_tool", "Default tool", string.Empty,
                Placeholder: "(use last)", OptionsKey: "editor_tools",
                IsEditable: false, LocalizeOptionsAsEnum: true)]),

        new("arestoys.apply-image-effects-preset",
            "Apply image effects preset",
            "Run a saved chain of adjustments / filters on the captured image (e.g. add a border, watermark, vignette). Configure presets in Settings → Image effects. Pick the preset from the dropdown; leave empty to skip. Toggle 'Keep original in history' to save both pre- and post-effect entries with a single Add-to-history step downstream.",
            "Editor",
            BoolParameters: new[]
            {
                new BoolParameter("keep_original", "Keep original in clipboard history", false),
            },
            StringParameters: [new StringParameter("preset_name", "Preset", string.Empty,
                Placeholder: "(none)", OptionsKey: "image_effect_presets",
                IsEditable: false)]),

        new("arestoys.trace-to-svg",
            "Trace to SVG",
            "Convert the captured raster to an SVG vector via potrace. Result lands in the pipeline bag under 'svg_output' for downstream Save-SVG / Copy-SVG steps. The 'Preset' dropdown lists the same stock presets as the standalone trace window ([Default], High Fidelity Photo, 3 Colors, Black and White Logo, Sketched Art, Silhouettes, Line Art, Technical Drawing, …) plus any custom presets you've saved there.",
            "Editor",
            DefaultConfigJson: "{\"preset\":\"[Default]\"}",
            StringParameters: [new StringParameter("preset", "Preset", "[Default]",
                Placeholder: "[Default]", OptionsKey: "trace_presets",
                IsEditable: false)]),

        new("arestoys.remove-background",
            "Remove background",
            "Run AI background removal on the captured image (U2NetP ONNX model). Replaces the in-flight bytes with a transparent-background PNG. Best on portraits / objects with clear edges; subtle / fluffy edges may show artefacts. Falls through silently when the model fails to load. First call costs ~150-500 ms session warmup; subsequent calls are ~100-500 ms.",
            "Editor"),

        new("arestoys.save-to-file",
            "Save as Image file",
            "Write the current raster bytes to disk under the configured capture folder (Settings → Capture). 'Format' is optional: leave empty to keep whatever's already in the bag (the global capture format), or pick one to force a re-encode for this step.",
            "I/O",
            StringParameters: [new StringParameter("format", "Format", string.Empty,
                Placeholder: "(use bag format)", OptionsKey: "image_formats",
                IsEditable: false)]),

        new("arestoys.save-svg",
            "Save as SVG",
            "Pair with Trace to SVG: writes the SVG string from the bag's 'svg_output' to disk as a .svg file in the capture folder. Reuses the raster's filename stem when Save-as-Image ran first, so {name}.png and {name}.svg sit side-by-side. Silent no-op when no SVG is in the bag (workflow without Trace-to-SVG).",
            "I/O"),

        new("arestoys.add-to-history",
            "Add to clipboard history",
            "Index the item in AresToys's history so it shows up in Win+V.",
            "Clipboard"),

        new("arestoys.copy-image-to-clipboard",
            "Copy image to Windows clipboard",
            "Place the bitmap on the Windows clipboard (Ctrl+V in any other app pastes it). Overwritten by any later text-to-clipboard step in the same workflow. Distinct from 'Add to clipboard history', which saves the item to AresToys's persistent popup list.",
            "Clipboard"),

        new("arestoys.paste-history-item",
            "Paste history item",
            "Auto-paste the N-th most recent clipboard history item into the foreground window. Index is 1-based (1 = most recent), same ordering as the popup's Ctrl+1..9 shortcuts. The history snapshot is frozen at the start of the workflow run so chained paste steps target the items the user expects, not whatever just got re-ingested.",
            "Clipboard",
            DefaultConfigJson: "{\"index\":1}",
            IntParameter: new IntParameter(Key: "index", Label: "Index (1 = most recent)", DefaultValue: 1, Min: 1, Max: 99)),

        new("arestoys.press-key",
            "Press Enter",
            "Send a single Enter keystroke to the foreground window. Useful as a separator between paste steps when chaining clipboard items onto consecutive lines.",
            "Clipboard",
            DefaultConfigJson: "{\"key\":\"enter\"}",
            LocalizationKey: "arestoys_press_key_enter"),

        new("arestoys.press-key",
            "Press Tab",
            "Send a single Tab keystroke to the foreground window — handy for moving between fields between paste steps.",
            "Clipboard",
            DefaultConfigJson: "{\"key\":\"tab\"}",
            LocalizationKey: "arestoys_press_key_tab"),

        new("arestoys.delay",
            "Delay",
            "Pause the workflow for the configured number of milliseconds. Useful between paste / press-key steps when the target window is slow to process keystrokes.",
            "Flow",
            DefaultConfigJson: "{\"ms\":250}",
            IntParameter: new IntParameter(Key: "ms", Label: "Milliseconds", DefaultValue: 250, Min: 0, Max: 60000)),

        // Three discrete rows for the most common destinations, all backed by the same
        // CopyTextToClipboardTask but pre-configured with a fixed template each. Disambiguation
        // in LookupForStep is via the "template" key in DefaultConfigJson — so a step that has
        // template="{bag.upload_urls}" reads back as "Copy URL", template="{bag.local_path}"
        // as "Copy file path", etc. No editable template field on these rows: power users who
        // want a custom format can drop in the workflow JSON directly (rare).
        // LocalizationKey is unique-per-variant so the resx lookup misses (we don't ship per-
        // variant translations of these labels) and the localizer falls back to DisplayName
        // above. Without this, all three rows would resolve to the catch-all
        // WorkflowAction_arestoys_copy_text_to_clipboard = "Copy text to clipboard" and the
        // picker showed three identical entries.
        new("arestoys.copy-text-to-clipboard",
            "Copy URL to clipboard",
            "Put the upload URL(s) on the OS clipboard (Ctrl+V target) — the standard 'capture → upload → paste link' tail. Joined by newline when multiple uploaders ran. Empty if no upload step preceded.",
            "Clipboard",
            DefaultConfigJson: "{\"template\":\"{bag.upload_urls}\"}",
            LocalizationKey: "arestoys_copy_text_url"),

        new("arestoys.copy-text-to-clipboard",
            "Copy file path to clipboard",
            "Put the absolute path of the saved file on the OS clipboard. Requires a preceding 'Save as Image file' step (which sets bag.local_path). Useful for pasting the screenshot path into Discord / Slack / file dialogs.",
            "Clipboard",
            DefaultConfigJson: "{\"template\":\"{bag.local_path}\"}",
            LocalizationKey: "arestoys_copy_text_local_path"),

        new("arestoys.copy-text-to-clipboard",
            "Copy SVG path to clipboard",
            "Put the absolute path of the saved .svg on the OS clipboard. Requires a preceding 'Save as SVG' step (which sets bag.svg_local_path).",
            "Clipboard",
            DefaultConfigJson: "{\"template\":\"{bag.svg_local_path}\"}",
            LocalizationKey: "arestoys_copy_text_svg_path"),

        new("arestoys.upload",
            "Upload to selected image uploaders",
            "Run every uploader the user has selected for the image category (Settings → Plugins → image).",
            "Upload",
            DefaultConfigJson: "{\"category\":\"image\"}",
            LocalizationKey: "arestoys_upload_image"),

        new("arestoys.upload",
            "Upload to selected file uploaders",
            "Run every uploader the user has selected for the file category (Settings → Plugins → file).",
            "Upload",
            DefaultConfigJson: "{\"category\":\"file\"}",
            LocalizationKey: "arestoys_upload_file"),

        new("arestoys.upload",
            "Upload to selected text uploaders",
            "Run every uploader the user has selected for the text category (Settings → Plugins → text). paste.rs, Pastebin, Gist, plus any AnyFile destination.",
            "Upload",
            DefaultConfigJson: "{\"category\":\"text\"}",
            LocalizationKey: "arestoys_upload_text"),

        new("arestoys.upload",
            "Upload to selected video uploaders",
            "Run every uploader the user has selected for the video category (Settings → Plugins → video).",
            "Upload",
            DefaultConfigJson: "{\"category\":\"video\"}",
            LocalizationKey: "arestoys_upload_video"),

        new("arestoys.upload",
            "Shorten URL via selected URL shorteners",
            "Run every URL shortener the user has selected (Settings → Uploaders → URL). is.gd / v.gd are bundled. Input must be a valid absolute URL — the shorteners reject anything else.",
            "Upload",
            DefaultConfigJson: "{\"category\":\"url\"}",
            LocalizationKey: "arestoys_upload_url"),

        new("arestoys.upload-clipboard-text",
            "Read text from clipboard",
            "Pulls the current text from the system clipboard and stages it as the workflow's payload (UTF-8 bytes, .txt extension). Pair with an Upload step (text category) to publish the content. Skips silently if the clipboard has no text.",
            "Capture",
            DefaultConfigJson: null),

        new("arestoys.capture-selected-explorer-file",
            "Capture selected Explorer file",
            "Reads the file currently selected in the foreground Explorer window (via Shell.Application COM) and stages its bytes as the workflow's payload. Aborts silently when no Explorer is foreground or nothing is selected; only takes the first file on multi-selection.",
            "Capture",
            DefaultConfigJson: null),

        new("arestoys.qr-read",
            "QR — read code from image",
            "Decodes the first QR code found in the current image payload (ZXing.Net, QR_CODE only, AutoRotate + TryHarder enabled). Replaces the payload with the decoded UTF-8 text and flips the extension to .txt. Aborts the workflow when no QR is found — pair with capture-region to build a 'screenshot QR → clipboard text' flow.",
            "Tools",
            DefaultConfigJson: null),

        new("arestoys.update-item-url",
            "Update item URL",
            "Persists the upload URL on the history item so the popup shows it. Auto-injected after upload — not user-picked.",
            "I/O",
            IsPlumbing: true),

        new("arestoys.notify-toast",
            "Show toast notification",
            "Display a Windows toast confirming the operation. Click opens the URL when present.",
            "Notify",
            DefaultConfigJson: "{\"title\":\"AresToys\",\"message\":\"Done.\"}"),

        new("arestoys.open-popup",
            "Show clipboard window",
            "Open the AresToys clipboard window (Win+V replacement). Pressing the same shortcut again while it's up dismisses it.",
            "Clipboard"),

        new("arestoys.toggle-incognito",
            "Toggle incognito mode",
            "Flip incognito on/off — when on, clipboard items aren't captured into history.",
            "Tools"),

        new("arestoys.open-screenshot-folder",
            "Open screenshot folder",
            "Open the configured capture folder (Settings → Capture) in Windows Explorer.",
            "Tools"),

        new("arestoys.show-in-explorer",
            "Show file in Explorer",
            "Open Windows Explorer with the just-saved file pre-selected. Requires a preceding Save to file step.",
            "I/O"),

        new("arestoys.save-as",
            "Save image as…",
            "Open a Save File dialog so the user picks the destination + filename. The chosen path becomes the new local_path for subsequent steps.",
            "I/O"),

        new("arestoys.open-url",
            "Open URL in browser",
            "Launch the default browser on the upload URL (or an explicit URL via config). Useful right after an Upload step.",
            "Notify"),

        new("arestoys.show-qr-code",
            "Show QR code",
            "Generate a QR code from the upload URL (or explicit text) and pop a small window. Handy for scanning the link on a phone. Also writes the rendered PNG to bag.payload_bytes so Save image as… / Copy image to clipboard / Add to history can chain after.",
            "Notify"),
        new("arestoys.save-qr-as-image",
            "Save QR as image",
            "Generate a QR PNG and write it to disk. Auto-saves to config.path when set, otherwise prompts with a Save dialog. Source text: config.text → bag.upload_url → bag.payload_bytes (UTF-8).",
            "Notify"),
        new("arestoys.save-qr-as-svg",
            "Save QR as SVG",
            "Same as Save QR as image but produces a scalable SVG document — sharp at any size, ideal for print or slides.",
            "Notify"),
        new("arestoys.copy-qr-to-clipboard",
            "Copy QR to clipboard",
            "Generate a QR PNG and place it on the Windows clipboard. Paste-ready in any app that accepts images. (SVG-to-clipboard isn't supported on Windows.)",
            "Notify"),

        new("arestoys.pin-to-screen",
            "Pin image to screen",
            "Show the captured image in an always-on-top window. Drag to move, wheel to zoom, right-click or Esc to close.",
            "Tools"),

        // Launch family — MaxLaunchpad-style "press a shortcut, run a thing". Composable into any
        // workflow so a single AresToys shortcut can capture, save, AND launch an app/file/command
        // in sequence. Use Launch app for .exe / .lnk / .bat targets; Open file for "treat me as
        // a document and let Windows pick the handler"; Run command for shell pipelines.
        new("arestoys.launch-app",
            "Launch app",
            "Start an executable, shortcut, or batch file. Path supports %ENV% expansion. Args are passed verbatim to the target. Working dir defaults to the path's folder.",
            "Launch",
            DefaultConfigJson: "{\"path\":\"\",\"args\":\"\",\"workingDir\":\"\"}",
            StringParameters: new[]
            {
                new StringParameter("path",       "Path",        "", "C:\\Program Files\\…\\app.exe", StringPickerKind.File),
                new StringParameter("args",       "Args",        "", "--flag value"),
                new StringParameter("workingDir", "Working dir", "", "(defaults to app's folder)",   StringPickerKind.Folder),
            }),

        new("arestoys.open-file",
            "Open file or folder",
            "Open a file or folder with its default OS-registered handler — same as double-clicking in Explorer. PDFs land in the PDF viewer, .txt in Notepad, folders in an Explorer window.",
            "Launch",
            DefaultConfigJson: "{\"path\":\"\"}",
            StringParameters: new[]
            {
                new StringParameter("path", "Path", "", "C:\\Users\\…\\file.pdf", StringPickerKind.FileOrFolder),
            }),

        new("arestoys.run-command",
            "Run command",
            "Run a shell command line via cmd /c — supports PATH lookups, pipes, redirects, chained commands. Fire-and-forget: workflow doesn't block on completion. For interactive console use Launch app with cmd.exe + /k …",
            "Launch",
            DefaultConfigJson: "{\"command\":\"\"}",
            StringParameters: new[]
            {
                new StringParameter("command", "Command", "", "git pull && npm test"),
            }),

        new("arestoys.open-launcher-menu",
            "Open launcher menu",
            "Show the launcher overlay — a 3×10 keyboard grid where every printable key fires a path / shortcut / shell target. Press a key to launch, Esc to dismiss, right-click a cell to map it to something. Wire this behind a global shortcut and you have a MaxLaunchpad-style panel inside AresToys.",
            "Launch"),

        new("arestoys.open-settings",
            "Open settings window",
            "Show + activate the main AresToys Settings window. Useful as a tray-click action, or as the entry step of a workflow that opens settings then runs something else.",
            "Tools"),

        new("arestoys.open-launcher-drag-mode",
            "Open launcher (drag mode)",
            "Show the launcher overlay already in drag-and-drop mode: the panel stays open while you drag files / folders / shortcuts from Explorer onto cells to map them. Esc exits drag mode (the launcher stays open in normal mode); a second Esc closes it.",
            "Launch"),

    ];

    /// <summary>Only the user-pickable subset, grouped by category. Drives the "+ Add step" menu.</summary>
    public static IEnumerable<IGrouping<string, WorkflowActionDescriptor>> Pickable()
        => All.Where(a => !a.IsPlumbing).GroupBy(a => a.Category);

    /// <summary>Find the descriptor for a task id, ignoring config. When multiple descriptors share
    /// the same task id (variants like record-screen mp4 / gif) returns the first; for render-time
    /// disambiguation use <see cref="LookupForStep"/>.</summary>
    public static WorkflowActionDescriptor? Lookup(string taskId)
        => All.FirstOrDefault(a => a.TaskId == taskId);

    /// <summary>Find the descriptor that best matches an existing step within a given catalog
    /// snapshot. When multiple descriptors share the task id (variants like record-screen mp4/gif
    /// or upload-to-onedrive vs upload-to-catbox) prefer the one whose
    /// <see cref="WorkflowActionDescriptor.DefaultConfigJson"/> matches the step's config on a
    /// disambiguation key — checked in order: <c>uploader</c>, <c>category</c>, <c>format</c>.
    /// Falls back to the first match by task id.</summary>
    public static WorkflowActionDescriptor? LookupForStep(IReadOnlyList<WorkflowActionDescriptor> catalog, PipelineStep step)
    {
        var matches = catalog.Where(a => a.TaskId == step.TaskId).ToList();
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];
        foreach (var key in new[] { "uploader", "category", "format", "key", "op", "template" })
        {
            var stepValue = (string?)step.Config?[key];
            if (string.IsNullOrEmpty(stepValue)) continue;
            foreach (var candidate in matches)
            {
                if (string.IsNullOrEmpty(candidate.DefaultConfigJson)) continue;
                var candidateValue = (string?)JsonNode.Parse(candidate.DefaultConfigJson)?[key];
                if (string.Equals(stepValue, candidateValue, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        return matches[0];
    }

    /// <summary>Convenience overload that uses the static catalog (used by call sites that don't
    /// need the per-uploader dynamic entries).</summary>
    public static WorkflowActionDescriptor? LookupForStep(PipelineStep step) => LookupForStep(All, step);
}
