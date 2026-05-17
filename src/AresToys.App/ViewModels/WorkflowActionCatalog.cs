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
    /// <summary>⌨ Click-to-capture button: opens <c>HotkeyCaptureWindow</c> and writes the
    /// pressed combo (e.g. <c>"Ctrl + Shift + T"</c>) back as the parameter value. Used by
    /// PressKeyTask's <c>combo</c> parameter so the workflow editor reuses the same capture UI
    /// the settings → hotkeys list uses, instead of asking the user to type a combo string.</summary>
    HotkeyCapture,
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
    bool LocalizeOptionsAsEnum = false,
    /// <summary>When true, dropdown labels are passed through
    /// <see cref="Services.Launcher.KeyboardLayoutMapper.GetDisplayChar"/> so the user sees
    /// the glyph their physical keyboard prints (italian ";"→"Ò", "/"→"-", etc.) while the
    /// Raw value persisted in step.Config stays the US-canonical storage key — same trick
    /// the launcher uses for cell labels, so storage portability is preserved across
    /// keyboard-layout changes.</summary>
    bool LocalizeOptionsAsLauncherKey = false,
    /// <summary>When true, dropdown labels render as colour-format preview strings via
    /// <see cref="Services.ColorFormatLabels.LabelFor"/>: <c>"hex-hash-alpha"</c> shows as
    /// <c>"Hex with # and alpha (#RRGGBBAA)"</c> so the user can see the output shape before
    /// committing. Raw value persisted in step.Config stays the compact lookup key so the
    /// ConvertColorTask format switch keeps working without a parse step.</summary>
    bool LocalizeOptionsAsColorFormat = false,
    /// <summary>When true, dropdown labels render as Settings sidebar section names via
    /// <see cref="Services.SettingsTabLabels.LabelFor"/>: <c>"hotkeys"</c> shows as
    /// <c>"Hotkeys &amp; workflows"</c>, matching the sidebar entries. Used by OpenSettingsTask's
    /// tab dropdown — raw enum value persists in step.Config, display string honours the same
    /// labels the user sees in the sidebar so the dropdown reads as a direct picker for those
    /// sections.</summary>
    bool LocalizeOptionsAsSettingsTab = false);

/// <summary>The three semantic "data types" that flow through a pipeline's bag. Rendered as
/// coloured pills above (Inputs) and below (Outputs) each step row so the user can see at a
/// glance whether two steps speak the same language. Mapping to bag keys:
/// <list type="bullet">
///   <item><see cref="Payload"/>: <c>bag.payload_bytes</c> + <c>bag.file_extension</c> — the
///   current binary blob (image / video / file bytes).</item>
///   <item><see cref="Text"/>: <c>bag.text</c> — the canonical text channel (URL, file path,
///   decoded QR string, raw text — last writer wins).</item>
///   <item><see cref="Color"/>: <c>bag.color</c> — the sampled / picked colour.</item>
/// </list>
/// Item / history-entry side-channels are deliberately NOT exposed as ports — they're internal
/// state, not something the user composes against.</summary>
public enum WorkflowPort { Payload, Text, Color }

/// <summary>One entry in the "+ Add step" picker for workflows. Maps a pipeline task id to
/// human-readable metadata + a default config to apply when the user adds the action.</summary>
public sealed record WorkflowActionDescriptor(
    string TaskId,
    string DisplayName,
    string Description,
    string Category,
    /// <summary>Optional inline warning rendered on the step card under the description, with a
    /// FontAwesome ⚠ glyph in accent yellow. Use for tasks whose misconfiguration can have
    /// destructive side effects (Repeat with high count, Run command, etc.). Null = no banner.
    /// Localisation key is <c>WorkflowActionWarning_&lt;sanitised_task_id&gt;</c> (or
    /// <c>WorkflowActionWarning_&lt;LocalizationKey&gt;</c> when set) — falls back to this
    /// English string when the resx key is missing.</summary>
    string? WarningMessage = null,
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
    string? LocalizationKey = null,
    /// <summary>Bag "types" this step reads. Rendered as pills ABOVE the step row in the editor.
    /// Empty = pure source (no upstream dependency).</summary>
    IReadOnlyList<WorkflowPort>? Inputs = null,
    /// <summary>Bag "types" this step writes. Rendered as pills BELOW the step row in the editor.
    /// Empty = terminal step (writes to clipboard / disk / window — nothing downstream consumes).</summary>
    IReadOnlyList<WorkflowPort>? Outputs = null);

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
            },
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.capture-active-window",
            "Capture active window",
            "Snapshot the currently-foreground window using DWM extended-frame-bounds (no resize-border padding). Honours the global capture delay; own-process windows are skipped so Settings / popup never become the target.",
            "Capture",
            IntParameter: new IntParameter("delay_seconds", "Delay (s)", 0, 0, 30),
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.capture-active-monitor",
            "Capture active monitor",
            "Snapshot the monitor currently under the mouse cursor — useful as a hotkey on multi-monitor setups. On single-monitor it just captures the whole screen.",
            "Capture",
            IntParameter: new IntParameter("delay_seconds", "Delay (s)", 0, 0, 30),
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.capture-webpage",
            "Capture webpage",
            "Render a URL in a hidden WebView2 and grab a full-page PNG (everything below the fold included). URL resolution: (1) the URL field below, (2) bag.text from an upstream step (e.g. Scan QR in region → Capture webpage), (3) interactive prompt when both are empty. Login-walled pages won't render protected content.",
            "Capture",
            StringParameters: [new StringParameter("url", "URL", string.Empty, Placeholder: "https://example.com (empty = use bag.text, then prompt)")],
            // Optional Text input: when the workflow has an upstream step that produces bag.text
            // (Scan QR in region, Read text from clipboard via shorten, etc.) Capture webpage will
            // pick that up automatically. Empty bag.text + empty config → prompt.
            Inputs: new[] { WorkflowPort.Text },
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.record-screen",
            "Record screen area",
            "Toggle FFmpeg-driven screen recording. First invocation prompts for a region and starts; second invocation stops. Always records MP4 into a temp file — pair with 'Save Video file' downstream to write the final output to disk (and pick gif / mp4 / webm / mov there) and 'Add Payload to AresToys clipboard' to commit the video to AresToys' history.",
            "Capture",
            DefaultConfigJson: "{}",
            LocalizationKey: "arestoys_record_screen",
            // The bag emits MP4 bytes + new_item. Outputs Payload so the workflow editor draws
            // the proper port; no Text output here since the saved path comes from the
            // downstream Save Video file step.
            Outputs: new[] { WorkflowPort.Payload }),

        // Wormholes batch ops — one task class (WormholeBatchOpTask) routed by the "op" config
        // value; six rows here so the workflow editor's "+ Add" menu lists each as a discrete
        // entry. Same pattern as record-screen mp4 / gif.
        new("arestoys.wormhole-batch-op",
            "Hide all",
            "Set IsHidden=true on every wormhole — closes their live windows but keeps the records.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"hide-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Show all",
            "Set IsHidden=false on every wormhole — re-spawns the live windows for any that were hidden.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"show-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Lock all",
            "Lock geometry on every wormhole — drag and resize disabled until unlocked.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"lock-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Unlock all",
            "Unlock every wormhole so they're draggable / resizable again.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"unlock-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Collapse all",
            "Roll up every wormhole to its header strip only — quick way to reclaim desktop space without hiding.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"collapse-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Uncollapse all",
            "Restore every rolled-up wormhole to its previous height.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"uncollapse-all\"}"),
        new("arestoys.wormhole-batch-op",
            "Toggle hide / show",
            "If any wormhole is currently visible, hide them all; otherwise show them all. Single-key 'clean up the desktop / bring them back' gesture.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"toggle-hide\"}"),
        new("arestoys.wormhole-batch-op",
            "Toggle lock / unlock",
            "If any wormhole is unlocked, lock them all; otherwise unlock them all.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"toggle-lock\"}"),
        new("arestoys.wormhole-batch-op",
            "Toggle collapse / uncollapse",
            "If any wormhole is uncollapsed, collapse them all; otherwise uncollapse them all.",
            "Wormholes",
            DefaultConfigJson: "{\"op\":\"toggle-collapse\"}"),
        new("arestoys.wormhole-create",
            "Create wormhole",
            "Create a new wormhole. If a single folder is selected in the foreground Explorer window, uses that folder automatically. Otherwise opens the New Wormhole dialog so the user picks a folder.",
            "Wormholes"),

        new("arestoys.color-sampler",
            "Color sampler",
            "Open the magnifier-style sampler at the cursor — picks a pixel from anywhere on screen. The sampled color is copied to the clipboard.",
            "Tools",
            Outputs: new[] { WorkflowPort.Color }),

        new("arestoys.color-picker",
            "Color picker",
            "Open the dialog-style HSB/RGB/CMYK colour picker (wheel + numeric inputs). The chosen color is stashed in the bag — pair with a Copy color as … step to write it to the clipboard.",
            "Tools",
            Outputs: new[] { WorkflowPort.Color }),

        // Convert color — single unified converter replacing the 0.1.16 fan-out of 8 separate
        // CopyColorAs* tasks. Reads bag.color from an upstream sampler/picker, writes the
        // formatted string into bag.text + bag.payload_bytes. Compose with downstream
        // "Add text to AresToys clipboard" (alsoCopyToWindows=true) to push the colour to the
        // Windows clipboard — same chain shape image-capture workflows use (Save → AddToHistory).
        new("arestoys.convert-color",
            "Convert color",
            "Format the colour produced by an upstream Color sampler / Color picker into text. The output goes into bag.text so a downstream Add text step can push it to the Windows clipboard and/or add it to AresToys' history. Format picks the syntax — hex variants, rgb / rgba, hsb, cmyk, decimal (ARGB), Unreal Engine FLinearColor / FColor.",
            "I/O",
            DefaultConfigJson: "{\"format\":\"hex\"}",
            StringParameters: [new StringParameter("format", "Format", "hex",
                OptionsKey: "color_formats", IsEditable: false, LocalizeOptionsAsColorFormat: true)],
            LocalizationKey: "arestoys_convert_color",
            Inputs: new[] { WorkflowPort.Color },
            Outputs: new[] { WorkflowPort.Text }),

        new("arestoys.open-editor-before-upload",
            "Open editor",
            "Pause the pipeline and open the annotation editor on the captured bytes. On save, subsequent steps see the edited image.",
            "Panels",
            // No default tool — empty string = "use last-used", which is what the user expects
            // for an "open editor" step that doesn't dictate a starting mode. Specific presets
            // (e.g. a quick-crop pipeline) can pin "Crop" / "Rectangle" / etc. via DefaultConfigJson.
            DefaultConfigJson: "{\"fullscreen\":false,\"default_tool\":\"\",\"abortOnCancel\":false}",
            BoolParameters: new[]
            {
                // Fullscreen on the active monitor (the one currently under the cursor) +
                // force fit-to-viewport so the image fills the window regardless of size. Off
                // by default to keep the legacy windowed behaviour for existing presets.
                new BoolParameter("fullscreen", "Open fullscreen on active monitor (fit to screen)", false),
                // When ON, Esc / Cancel in the editor aborts the rest of the workflow — no
                // save, no history entry, no upload. Useful for "decide then commit" pipelines
                // (Capture region → Open editor → Save) where the user wants to throw away the
                // capture if they change their mind. OFF preserves the legacy behaviour
                // (pipeline proceeds with the unedited capture).
                new BoolParameter("abortOnCancel", "Interrupt workflow if canceled", false),
            },
            StringParameters: [new StringParameter("default_tool", "Default tool", string.Empty,
                Placeholder: "(use last)", OptionsKey: "editor_tools",
                IsEditable: false, LocalizeOptionsAsEnum: true)],
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Payload }),

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
                IsEditable: false)],
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.trace-to-svg",
            "Trace to SVG",
            "Convert the captured raster to an SVG vector via potrace. Result lands in the pipeline bag under 'svg_output' for downstream Save-SVG / Copy-SVG steps. The 'Preset' dropdown lists the same stock presets as the standalone trace window ([Default], High Fidelity Photo, 3 Colors, Black and White Logo, Sketched Art, Silhouettes, Line Art, Technical Drawing, …) plus any custom presets you've saved there.",
            "Editor",
            DefaultConfigJson: "{\"preset\":\"[Default]\"}",
            StringParameters: [new StringParameter("preset", "Preset", "[Default]",
                Placeholder: "[Default]", OptionsKey: "trace_presets",
                IsEditable: false)],
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.remove-background",
            "Remove background",
            "Run AI background removal on the captured image (U2NetP ONNX model). Replaces the in-flight bytes with a transparent-background PNG. Best on portraits / objects with clear edges; subtle / fluffy edges may show artefacts. Falls through silently when the model fails to load. First call costs ~150-500 ms session warmup; subsequent calls are ~100-500 ms.",
            "Editor",
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.save-to-file",
            "Save as Image file",
            "Write the current raster bytes to disk under the configured capture folder (Settings → Capture). 'Format' is optional: leave empty to keep whatever's already in the bag (the global capture format), or pick one to force a re-encode for this step. Toggle 'Show notification' for a post-save toast. Toggle 'Only save if image was edited' to skip when the upstream editor step ran but the user didn't modify anything — useful for placing a second Save AFTER an Open-editor step to get a before/after pair without duplicates.",
            "I/O",
            DefaultConfigJson: "{\"showNotification\":false,\"skipIfNotModified\":false}",
            BoolParameters: new[]
            {
                new BoolParameter("showNotification", "Show notification", false),
                new BoolParameter("skipIfNotModified", "Only save if image was edited (since Open editor)", false),
            },
            StringParameters: [new StringParameter("format", "Format", string.Empty,
                Placeholder: "(use bag format)", OptionsKey: "image_formats",
                IsEditable: false)],
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Text }),

        new("arestoys.save-svg",
            "Save as SVG",
            "Pair with Trace to SVG / Convert text to QR (SVG): writes bag.svg_output to disk as a .svg file in the capture folder. Toggle 'Show notification' for a post-save toast with Copy-path / Show-in-folder buttons.",
            "I/O",
            DefaultConfigJson: "{\"showNotification\":false}",
            BoolParameters: new[] { new BoolParameter("showNotification", "Show notification", false) },
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Text }),

        new("arestoys.save-video-file",
            "Save as Video file",
            "Pair with Record screen: writes the recorded video bytes to disk under the configured capture folder (Settings → Capture). 'Format' picks the output container — mp4 keeps the original recording verbatim (fast), gif / webm / mov trigger an ffmpeg transcode (slower; requires ffmpeg.exe in PATH or %APPDATA%\\AresToys\\Tools). Toggle 'Show notification' for a post-save toast with Copy-path / Show-in-folder buttons.",
            "I/O",
            DefaultConfigJson: "{\"format\":\"mp4\",\"showNotification\":false}",
            BoolParameters: new[] { new BoolParameter("showNotification", "Show notification", false) },
            StringParameters: [new StringParameter("format", "Format", "mp4",
                Placeholder: "mp4", OptionsKey: "video_formats",
                IsEditable: false)],
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Text }),

        new("arestoys.add-to-history",
            "Add Payload to AresToys clipboard",
            "Save whatever the upstream capture / generator step staged (image bytes, file path, text — read from bag.new_item) to AresToys' history (the popup you open with Win+V). For composing your own additions, see the more specific 'Add file / image / text' variants below. Toggles add Windows-clipboard push + a contextual toast (Open URL / Copy / Editor buttons chosen from the bag).",
            "Clipboard",
            DefaultConfigJson: "{\"alsoCopyToWindows\":false,\"showNotification\":false}",
            BoolParameters: new[]
            {
                new BoolParameter("alsoCopyToWindows", "Also push to Windows clipboard", false),
                new BoolParameter("showNotification", "Show notification", false),
            },
            Inputs: new[] { WorkflowPort.Payload }),

        new("arestoys.add-file",
            "Add file path to AresToys clipboard",
            "Add the file at bag.local_path (set by Save as Image file / Record screen + Save as Video file / Save as SVG / Save as…) to AresToys' history as a Files entry. Reads the PATH from the bag (Text input), not the file's bytes — chain it after any save step. The resulting clipboard entry behaves like a real file (preview, paste-as-file). Toggles enable Windows-clipboard CF_HDROP push and a contextual toast with Copy-path / Show-in-folder buttons.",
            "Clipboard",
            DefaultConfigJson: "{\"alsoCopyToWindows\":false,\"showNotification\":false}",
            BoolParameters: new[]
            {
                new BoolParameter("alsoCopyToWindows", "Also push to Windows clipboard (paste-as-file)", false),
                new BoolParameter("showNotification", "Show notification", false),
            },
            Inputs: new[] { WorkflowPort.Text }),

        new("arestoys.add-image",
            "Add image to AresToys clipboard",
            "Add the image staged in bag.payload_bytes (set by any Capture step) to AresToys' history as an Image entry. Toggles enable Windows-clipboard PNG push (alpha-preserving) and a contextual toast with Open-in-editor / Show-in-folder buttons.",
            "Clipboard",
            DefaultConfigJson: "{\"alsoCopyToWindows\":false,\"showNotification\":false}",
            BoolParameters: new[]
            {
                new BoolParameter("alsoCopyToWindows", "Also push to Windows clipboard", false),
                new BoolParameter("showNotification", "Show notification", false),
            },
            Inputs: new[] { WorkflowPort.Payload }),

        new("arestoys.add-text",
            "Add text to AresToys clipboard",
            "Add the pipeline's current text (bag.text — overwritten by Upload / Scan QR / Save as Image file / etc.) to AresToys' history as a Text entry. Zero config — chain it after the step that produces the text you want. Toggles enable Windows-clipboard push and a contextual toast (Open URL if the text is a link, Copy / Edit with system editor otherwise).",
            "Clipboard",
            DefaultConfigJson: "{\"alsoCopyToWindows\":false,\"showNotification\":false}",
            BoolParameters: new[]
            {
                new BoolParameter("alsoCopyToWindows", "Also push to Windows clipboard", false),
                new BoolParameter("showNotification", "Show notification", false),
            },
            Inputs: new[] { WorkflowPort.Text }),

        new("arestoys.paste-history-item",
            "Paste history item",
            "Auto-paste the N-th most recent clipboard history item into the foreground window. Index is 1-based (1 = most recent), same ordering as the popup's Ctrl+1..9 shortcuts. The history snapshot is frozen at the start of the workflow run so chained paste steps target the items the user expects, not whatever just got re-ingested.",
            "Clipboard",
            DefaultConfigJson: "{\"index\":1}",
            IntParameter: new IntParameter(Key: "index", Label: "Index (1 = most recent)", DefaultValue: 1, Min: 1, Max: 99)),

        new("arestoys.press-key",
            "Send key / shortcut",
            "Send an arbitrary key or modifier+key combo (Ctrl+Shift+T, F12, Win+R, …) to the foreground window. Click the Combo field to capture — the dialog records whatever you press, including OS-reserved combos. Pair with a hotkey-triggered workflow for a PowerToys KBM-style remap (bind a side-button hotkey → workflow sending Ctrl+W = close tab).",
            "Actions",
            DefaultConfigJson: "{\"combo\":\"\"}",
            StringParameters: [new StringParameter("combo", "Combo", string.Empty,
                Placeholder: "Click ⌨ to capture",
                Picker: StringPickerKind.HotkeyCapture)],
            LocalizationKey: "arestoys_press_key_combo"),

        new("arestoys.delay",
            "Delay",
            "Pause the workflow for the configured number of milliseconds. Useful between paste / press-key steps when the target window is slow to process keystrokes.",
            "Flow",
            DefaultConfigJson: "{\"ms\":250}",
            IntParameter: new IntParameter(Key: "ms", Label: "Milliseconds", DefaultValue: 250, Min: 0, Max: 60000)),

        new("arestoys.end-repeat",
            "End repeat",
            "Closes the scope opened by the nearest preceding Repeat task. Steps below this marker render back at the outer indent and run ONCE per workflow execution instead of being part of the loop body. Omit this marker entirely if you want the Repeat to loop the whole tail of the workflow (back-compat behaviour). An orphan End Repeat (no Repeat above) is a runtime no-op and shows a warning banner.",
            "Flow",
            LocalizationKey: "arestoys_end_repeat"),

        new("arestoys.repeat",
            "Repeat next steps",
            "Re-execute every step BELOW this one N times. The visual indent on the workflow editor highlights which steps are inside the loop. Optional 'Cancel hotkey' breaks out early (capture a combo like Ctrl+Shift+X — useful safety net for high counts). Pair with Delay for spaced spamming. Nested Repeat tasks are not supported. Hard-capped at 1000 iterations to avoid foot-guns.",
            "Flow",
            WarningMessage: "WARNING: every step below runs N times. Set a Cancel hotkey before bumping Count past 20, and double-check that the body doesn't run anything destructive (Run command, Launch app, Send key combos to active windows).",
            // delayMs is a StringParameter (no IntParameters-list support in the descriptor yet),
            // so the default JSON value MUST be a string — a JsonNode Number can't be cast to
            // string when the editor hydrates the parameter row, and would throw InvalidOperation
            // ("An element of type 'Number' cannot be converted to System.String") on first open.
            DefaultConfigJson: "{\"count\":2,\"delayMs\":\"0\",\"cancelCombo\":\"\"}",
            IntParameter: new IntParameter(Key: "count", Label: "Count", DefaultValue: 2, Min: 1, Max: 1000),
            StringParameters: new[]
            {
                new StringParameter("delayMs", "Delay between iterations (ms)", "0", Placeholder: "0"),
                new StringParameter("cancelCombo", "Cancel hotkey", string.Empty,
                    Placeholder: "Click ⌨ to capture",
                    Picker: StringPickerKind.HotkeyCapture),
            },
            LocalizationKey: "arestoys_repeat"),

        // Consolidated upload: one entry, dropdown of every enabled uploader (auto-populated
        // from the plugin registry via OptionsProviders["uploader_ids"]). Empty selection =
        // auto-detect category from the bag's file extension and pick the first matching
        // uploader. For multi-uploader fan-out the user adds the step twice with different
        // uploader ids — no special "category" path needed in the descriptor.
        new("arestoys.upload",
            "Upload to cloud service",
            "Upload the current bytes to one of the configured uploaders. Pick a destination from the dropdown; leave it empty to auto-pick based on the file extension (image → image uploader, video → video uploader, etc.) — the first uploader the user has selected for that category wins. Toggle 'Show notification' for a post-upload toast with Open / Copy URL buttons.",
            "Upload",
            DefaultConfigJson: "{\"uploader\":\"\",\"showNotification\":false}",
            BoolParameters: new[] { new BoolParameter("showNotification", "Show notification", false) },
            StringParameters: new[]
            {
                new StringParameter("uploader", "Uploader", string.Empty,
                    Placeholder: "(auto-detect from file type)",
                    OptionsKey: "uploader_ids",
                    IsEditable: false),
            },
            LocalizationKey: "arestoys_upload_cloud",
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Text }),

        new("arestoys.upload",
            "Shorten URL",
            "Shorten the current text (typically chained after 'Read Windows clipboard' when the clipboard holds a URL) via a URL shortener plugin. Pick a shortener from the dropdown; empty = first available. Input must be a valid http(s) URL — the shortener rejects anything else. Writes the shortened URL into bag.text so a downstream 'Add text to clipboard' captures the result.",
            "Upload",
            // category:"url" is carried as a discriminator so LookupForStep can distinguish this
            // descriptor from "Upload to cloud service" (both share TaskId "arestoys.upload"). UploadTask
            // ignores `category` whenever `uploader` is set, so this does not affect runtime resolution.
            DefaultConfigJson: "{\"uploader\":\"\",\"category\":\"url\",\"showNotification\":false}",
            BoolParameters: new[] { new BoolParameter("showNotification", "Show notification", false) },
            StringParameters: new[]
            {
                new StringParameter("uploader", "Shortener", string.Empty,
                    Placeholder: "(first available)",
                    OptionsKey: "shortener_ids",
                    IsEditable: false),
            },
            LocalizationKey: "arestoys_shorten_url",
            // Reads bag.text when present (or bag.payload_bytes as fallback for back-compat with
            // chains that pre-encode UTF-8). Writes the shortened URL into bag.text.
            Inputs: new[] { WorkflowPort.Text },
            Outputs: new[] { WorkflowPort.Text }),

        new("arestoys.upload-clipboard-text",
            "Read Windows clipboard",
            "Pulls whatever is currently on the system clipboard — files, image, or text — and stages it into the bag. Detection order Files → Image → Text picks the most specific shape (Explorer ships both files and paths-as-text; we prefer files). Files populate bag.local_path; images populate bag.payload_bytes as PNG; text populates both bag.text and bag.payload_bytes (UTF-8). Skips silently when the clipboard is empty; downstream tasks each validate their own preconditions, so a mismatch (image clipboard + text-only Upload downstream, etc.) is a quiet no-op instead of a mid-pipeline failure.",
            "I/O",
            DefaultConfigJson: null,
            // Output is conditional on clipboard content: Files / Image → Payload, Text → both
            // Payload and Text. We advertise both ports so the editor's port-strip visualisation
            // doesn't lie about either of the realistic chains (Upload reads Payload, Shorten URL
            // / Add text read Text).
            Outputs: new[] { WorkflowPort.Payload, WorkflowPort.Text }),

        new("arestoys.capture-selected-explorer-file",
            "Capture selected Explorer file",
            "Reads the file currently selected in the foreground Explorer window (via Shell.Application COM) and stages its bytes as the workflow's payload. Aborts silently when no Explorer is foreground or nothing is selected; only takes the first file on multi-selection.",
            "Capture",
            DefaultConfigJson: null,
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.qr-read",
            "QR — read code from image",
            "Decodes the first QR code found in the current image payload (ZXing.Net, QR_CODE only, AutoRotate + TryHarder enabled). Replaces the payload with the decoded UTF-8 text and flips the extension to .txt. Aborts the workflow when no QR is found — pair with capture-region to build a 'screenshot QR → clipboard text' flow. Toggle 'Show notification' for a post-decode toast: Open URL if the text is a link, Copy text + Edit (system editor) otherwise.",
            "Tools",
            DefaultConfigJson: "{\"showNotification\":false}",
            BoolParameters: new[] { new BoolParameter("showNotification", "Show notification", false) },
            Inputs: new[] { WorkflowPort.Payload },
            // After decode: text = the decoded string AND payload_bytes = UTF-8 bytes of the same
            // text (ext flipped to .txt). Lets the user chain either an Add text step (Text) or
            // an Upload step that treats the decoded text as a .txt file (Payload).
            Outputs: new[] { WorkflowPort.Payload, WorkflowPort.Text }),

        new("arestoys.update-item-url",
            "Update item URL",
            "Persists the upload URL on the history item so the popup shows it. Auto-injected after upload — not user-picked.",
            "I/O",
            IsPlumbing: true),

        new("arestoys.open-popup",
            "Show clipboard window",
            "Open the AresToys clipboard window (Win+V replacement). Pressing the same shortcut again while it's up dismisses it.",
            "Panels"),

        new("arestoys.toggle-incognito",
            "Toggle incognito mode",
            "Flip incognito on/off — when on, clipboard items aren't captured into history.",
            "Actions"),

        new("arestoys.open-screenshot-folder",
            "Open screenshot folder",
            "Open the configured capture folder (Settings → Capture) in Windows Explorer.",
            "Actions"),

        new("arestoys.show-in-explorer",
            "Show file in Explorer",
            "Open Windows Explorer with the just-saved file pre-selected. Requires a preceding Save to file step.",
            "I/O",
            Inputs: new[] { WorkflowPort.Text }),

        new("arestoys.save-as",
            "Save image as…",
            "Open a Save File dialog so the user picks the destination + filename. The chosen path becomes the new local_path for subsequent steps. Toggle 'Show notification' for a post-save toast with Copy-path / Show-in-folder buttons.",
            "I/O",
            DefaultConfigJson: "{\"showNotification\":false}",
            BoolParameters: new[] { new BoolParameter("showNotification", "Show notification", false) },
            Inputs: new[] { WorkflowPort.Payload },
            Outputs: new[] { WorkflowPort.Text }),

        new("arestoys.open-url",
            "Open URL in browser",
            "Launch the default browser on a URL. Resolution order: hardcoded URL field (highest priority) → bag.text (set by an upstream Upload / Shorten URL / Read clipboard / etc.). Leave the URL field empty to consume whatever the bag carries; type a value to pin a specific destination regardless of upstream.",
            "Actions",
            DefaultConfigJson: "{\"url\":\"\"}",
            StringParameters: [new StringParameter("url", "URL", string.Empty,
                Placeholder: "(uses bag.text when empty)")],
            Inputs: new[] { WorkflowPort.Text }),

        new("arestoys.show-qr-code",
            "Show QR code",
            "Generate a QR code from the upload URL (or explicit text) and pop a small window. Handy for scanning the link on a phone. Also writes the rendered PNG to bag.payload_bytes so Save image as… / Copy image to clipboard / Add to history can chain after.",
            "Tools",
            Inputs: new[] { WorkflowPort.Text },
            Outputs: new[] { WorkflowPort.Payload }),
        new("arestoys.text-to-qr-png",
            "Convert text to QR (PNG)",
            "Render a QR code PNG from the pipeline's current text (bag.text — set by the previous Upload / Scan QR / Save step). Writes the image into bag.payload_bytes + bag.file_extension=png so downstream sinks ('Add image', 'Save as Image file') treat it like a fresh screenshot. Doesn't touch any clipboard on its own — pure converter.",
            "Tools",
            Inputs: new[] { WorkflowPort.Text },
            Outputs: new[] { WorkflowPort.Payload }),
        new("arestoys.text-to-qr-svg",
            "Convert text to QR (SVG)",
            "Render a QR code as an SVG vector (sharp at any zoom) from the pipeline's current text (bag.text) into bag.svg_output. Compose with 'Save as SVG file' downstream to write it to disk. Doesn't touch any clipboard on its own.",
            "Tools",
            Inputs: new[] { WorkflowPort.Text },
            Outputs: new[] { WorkflowPort.Payload }),

        new("arestoys.pin-to-screen",
            "Pin image to screen",
            "Show the captured image in an always-on-top window. Drag to move, wheel to zoom, right-click or Esc to close.",
            "Tools",
            Inputs: new[] { WorkflowPort.Payload }),

        // Launch family — MaxLaunchpad-style "press a shortcut, run a thing". Composable into any
        // workflow so a single AresToys shortcut can capture, save, AND launch an app/file/command
        // in sequence. Use Launch app for .exe / .lnk / .bat targets; Open file for "treat me as
        // a document and let Windows pick the handler"; Run command for shell pipelines.
        new("arestoys.launch-app",
            "Launch app",
            "Start an executable, shortcut, or batch file. Path supports %ENV% expansion. Args are passed verbatim to the target. Working dir defaults to the path's folder. Leave the Path field empty to consume bag.text from an upstream step (e.g. 'Read clipboard' → 'Launch app'); a value in Path always wins so a workflow with a pinned target isn't redirected by stray bag content.",
            "Actions",
            DefaultConfigJson: "{\"path\":\"\",\"args\":\"\",\"workingDir\":\"\"}",
            StringParameters: new[]
            {
                new StringParameter("path",       "Path",        "", "(uses bag.text when empty)", StringPickerKind.File),
                new StringParameter("args",       "Args",        "", "--flag value"),
                new StringParameter("workingDir", "Working dir", "", "(defaults to app's folder)",   StringPickerKind.Folder),
            },
            Inputs: new[] { WorkflowPort.Text }),

        new("arestoys.open-file",
            "Open file / folder",
            "Open a file or folder with its default OS-registered handler — same as double-clicking in Explorer. PDFs land in the PDF viewer, .txt in Notepad, folders in an Explorer window. Leave the Path field empty to consume bag.text from an upstream step (e.g. 'Read clipboard' → 'Open file / folder' to open whatever path was copied); a value in Path always wins.",
            "Actions",
            DefaultConfigJson: "{\"path\":\"\"}",
            StringParameters: new[]
            {
                new StringParameter("path", "Path", "", "(uses bag.text when empty)", StringPickerKind.FileOrFolder),
            },
            Inputs: new[] { WorkflowPort.Text }),

        new("arestoys.run-command",
            "Run command",
            "Run a shell command line via cmd /c — supports PATH lookups, pipes, redirects, chained commands. Fire-and-forget: workflow doesn't block on completion. For interactive console use Launch app with cmd.exe + /k … Leave the Command field empty to consume bag.text from an upstream step (e.g. 'Read clipboard' → 'Run command'); a value in Command always wins.",
            "Actions",
            WarningMessage: "WARNING: executes arbitrary shell commands. If you leave Command empty, whatever text is in the bag at runtime (clipboard contents, decoded QR, etc.) becomes the command line — review the upstream chain before enabling this in workflows that ingest untrusted input.",
            DefaultConfigJson: "{\"command\":\"\"}",
            StringParameters: new[]
            {
                new StringParameter("command", "Command", "", "(uses bag.text when empty)"),
            },
            Inputs: new[] { WorkflowPort.Text }),

        new("arestoys.open-launcher-menu",
            "Open launcher menu",
            "Show the launcher overlay — a 3×10 keyboard grid where every printable key fires a path / shortcut / shell target. Press a key to launch, Esc to dismiss, right-click a cell to map it to something. Wire this behind a global shortcut and you have a MaxLaunchpad-style panel inside AresToys.",
            "Panels"),

        new("arestoys.open-settings",
            "Open settings window",
            "Show + activate the main AresToys Settings window, optionally jumping to a specific sidebar section on open. Useful as a tray-click action, or as the entry step of a workflow that opens settings then runs something else. Default tab = App settings; pick another from the dropdown to land directly on Uploaders / Hotkeys & workflows / Capture / Theme / Clipboard categories / Wormholes / Debug / About.",
            "Panels",
            DefaultConfigJson: "{\"tab\":\"settings\"}",
            StringParameters: [new StringParameter("tab", "Tab", "settings",
                OptionsKey: "settings_tabs", IsEditable: false, LocalizeOptionsAsSettingsTab: true)],
            LocalizationKey: "arestoys_open_settings"),

        new("arestoys.open-launcher-drag-mode",
            "Open launcher (drag mode)",
            "Show the launcher overlay already in drag-and-drop mode: the panel stays open while you drag files / folders / shortcuts from Explorer onto cells to map them. Esc exits drag mode (the launcher stays open in normal mode); a second Esc closes it.",
            "Panels"),

        new("arestoys.launcher.trigger-key",
            "Trigger launcher key",
            "Fire a launcher cell programmatically — same as opening the launcher and pressing the key, but without the overlay. Pick a tab (1-9 / 0) and a key (Q-P, A-;, Z-/, or F1-F10). F1-F10 are global: the tab is ignored when the key is a function key. Toast appears if the slot is empty.",
            "Actions",
            DefaultConfigJson: "{\"tab\":\"1\",\"key\":\"Q\"}",
            StringParameters: new[]
            {
                new StringParameter("tab", "Tab", "1",
                    Placeholder: "1", OptionsKey: "launcher_tabs", IsEditable: false),
                new StringParameter("key", "Key", "Q",
                    Placeholder: "Q", OptionsKey: "launcher_keys", IsEditable: false,
                    LocalizeOptionsAsLauncherKey: true),
            }),

        new("arestoys.clipboard.paste-entry",
            "Paste clipboard entry",
            "Paste the N-th entry from a clipboard category — same gesture as opening the popup on that category and pressing Ctrl+N. Leave Category empty for the global 'All' view. Toast appears if the category is empty or has fewer entries than requested.",
            "Clipboard",
            DefaultConfigJson: "{\"category\":\"\",\"entry\":1}",
            IntParameter: new IntParameter("entry", "Entry #", 1, 1, 9),
            StringParameters: new[]
            {
                new StringParameter("category", "Category", string.Empty,
                    Placeholder: "(all)", OptionsKey: "clipboard_categories", IsEditable: false),
            }),

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
        // Key-PRESENCE discrimination — for variants distinguished by which optional config field
        // they ship with rather than by a value. Example: "Send key / shortcut" defaults to
        // {"combo":""} (empty value, key present) and shares TaskId with "Press Enter"
        // ({"key":"enter"}). Empty values fail the value-match loop above, so we fall through to
        // here and pick the descriptor whose DefaultConfigJson declares the same optional field.
        foreach (var key in new[] { "combo" })
        {
            var stepObj = step.Config?.AsObject();
            if (stepObj is null || !stepObj.ContainsKey(key)) continue;
            foreach (var candidate in matches)
            {
                if (string.IsNullOrEmpty(candidate.DefaultConfigJson)) continue;
                if (JsonNode.Parse(candidate.DefaultConfigJson)?.AsObject().ContainsKey(key) == true)
                    return candidate;
            }
        }
        return matches[0];
    }

    /// <summary>Convenience overload that uses the static catalog (used by call sites that don't
    /// need the per-uploader dynamic entries).</summary>
    public static WorkflowActionDescriptor? LookupForStep(PipelineStep step) => LookupForStep(All, step);
}
