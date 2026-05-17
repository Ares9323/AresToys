using AresToys.Core.Pipeline;
using AresToys.Pipeline.Tasks;

namespace AresToys.Pipeline.Profiles;

public static class DefaultPipelineProfiles
{
    // Profile / workflow ids
    public const string OnClipboardId       = "on-clipboard";
    public const string RegionCaptureId     = "region-capture";
    public const string MultiRegionCaptureId = "multi-region-capture";
    public const string ActiveWindowCaptureId = "active-window-capture";
    public const string ActiveMonitorCaptureId = "active-monitor-capture";
    public const string WebpageCaptureId       = "webpage-capture";
    public const string PinRegionToScreenId    = "pin-region-to-screen";
    public const string UploadSelectedFileId   = "upload-selected-file";
    public const string ManualUploadId      = "manual-upload";
    public const string UploadClipboardTextId = "upload-clipboard-text";
    public const string ShortenClipboardUrlId = "shorten-clipboard-url";
    public const string ShowPopupId         = "show-popup";
    public const string ToggleIncognitoId   = "toggle-incognito";
    public const string ColorSamplerId      = "color-sampler";
    public const string ColorPickerId       = "color-picker";
    public const string RecordScreenMp4Id   = "record-screen";
    public const string RecordScreenGifId   = "record-screen-gif";
    public const string OpenScreenshotFolderId = "open-screenshot-folder";
    public const string OpenLauncherId         = "open-launcher";
    public const string OpenSettingsId         = "open-settings";
    public const string QrReadFromRegionId     = "qr-read-from-region";
    public const string SaveQrToHistoryId      = "save-qr-to-history";

    // Wormholes built-in templates. All ship with no default hotkey — user binds the ones
    // they want from the Hotkeys tab. The Toggle templates carry THREE steps where only the
    // toggle is enabled at first; the two explicit on/off siblings are present-but-disabled
    // so the user discovers their existence and can flip between toggle / explicit modes
    // without rebuilding the workflow from scratch.
    public const string WormholesToggleHideId    = "wormholes-toggle-hide";
    public const string WormholesToggleLockId    = "wormholes-toggle-lock";
    public const string WormholesToggleCollapseId = "wormholes-toggle-collapse";
    public const string WormholesCreateId      = "wormholes-create";

    // Task IDs whose implementations live in AresToys.App (resolved at runtime by the registry).
    public const string AddFileTaskId              = "arestoys.add-file";
    public const string AddImageTaskId             = "arestoys.add-image";
    public const string AddTextTaskId              = "arestoys.add-text";
    public const string TextToQrPngTaskId          = "arestoys.text-to-qr-png";
    public const string TextToQrSvgTaskId          = "arestoys.text-to-qr-svg";
    public const string OpenEditorBeforeUploadTaskId = "arestoys.open-editor-before-upload";
    public const string ApplyImageEffectsPresetTaskId = "arestoys.apply-image-effects-preset";
    public const string OpenPopupTaskId            = "arestoys.open-popup";
    public const string ToggleIncognitoTaskId      = "arestoys.toggle-incognito";
    public const string ColorSamplerTaskId         = "arestoys.color-sampler";
    public const string ColorPickerTaskId          = "arestoys.color-picker";
    public const string ConvertColorTaskId         = "arestoys.convert-color";
    public const string CopyColorAsHexTaskId       = "arestoys.copy-color-hex";
    public const string CopyColorAsRgbTaskId       = "arestoys.copy-color-rgb";
    public const string CopyColorAsRgbaTaskId      = "arestoys.copy-color-rgba";
    public const string CopyColorAsHsbTaskId       = "arestoys.copy-color-hsb";
    public const string CopyColorAsCmykTaskId      = "arestoys.copy-color-cmyk";
    public const string CopyColorAsDecimalTaskId   = "arestoys.copy-color-decimal";
    public const string CopyColorAsLinearTaskId    = "arestoys.copy-color-linear";
    public const string CopyColorAsBgraTaskId      = "arestoys.copy-color-bgra";
    public const string CaptureRegionTaskId        = "arestoys.capture-region";
    public const string CaptureActiveWindowTaskId  = "arestoys.capture-active-window";
    public const string CaptureActiveMonitorTaskId = "arestoys.capture-active-monitor";
    public const string CaptureWebpageTaskId       = "arestoys.capture-webpage";
    public const string CaptureSelectedExplorerFileTaskId = "arestoys.capture-selected-explorer-file";
    public const string QrReadTaskId               = "arestoys.qr-read";
    public const string RecordScreenTaskId         = "arestoys.record-screen";
    public const string SaveVideoFileTaskId        = "arestoys.save-video-file";
    public const string RepeatTaskId               = "arestoys.repeat";
    public const string EndRepeatTaskId            = "arestoys.end-repeat";
    public const string OpenScreenshotFolderTaskId = "arestoys.open-screenshot-folder";
    public const string OpenLauncherMenuTaskId     = "arestoys.open-launcher-menu";
    public const string OpenSettingsTaskId         = "arestoys.open-settings";
    public const string UploadClipboardTextTaskId  = "arestoys.upload-clipboard-text";
    public const string PinToScreenTaskId          = "arestoys.pin-to-screen";
    public const string WormholeBatchOpTaskId      = "arestoys.wormhole-batch-op";
    public const string WormholeCreateTaskId       = "arestoys.wormhole-create";

    // Task IDs from AresToys.Plugins.
    public const string UploadTaskId = "arestoys.upload";

    // Hotkey modifier flag values (mirrors AresToys.Hotkeys.HotkeyModifiers; kept as raw ints so
    // AresToys.Pipeline doesn't pull in the Hotkeys assembly).
    private const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public static IReadOnlyList<PipelineProfile> All { get; } = BuildAll();

    /// <summary>Per-built-in category label used to group the Hotkeys list. Custom workflows
    /// (and any built-in not listed here) fall back to <see cref="DefaultCategory"/>. Kept in this
    /// file so the catalog and the categorisation stay literally next to each other — easier to
    /// remember to update both when adding a new built-in profile.</summary>
    public const string DefaultCategory = "Other";

    public static readonly IReadOnlyDictionary<string, string> CategoriesById = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RegionCaptureId]         = "Capture",
        [MultiRegionCaptureId]    = "Capture",
        [ActiveWindowCaptureId]   = "Capture",
        [ActiveMonitorCaptureId]  = "Capture",
        [WebpageCaptureId]        = "Capture",
        [PinRegionToScreenId]     = "Capture",
        [RecordScreenMp4Id]       = "Capture",
        [RecordScreenGifId]       = "Capture",

        [UploadClipboardTextId]   = "Upload",
        [ShortenClipboardUrlId]   = "Upload",
        [ManualUploadId]          = "Upload",
        [UploadSelectedFileId]    = "Upload",

        [OnClipboardId]           = "Clipboard",
        [ShowPopupId]             = "Clipboard",
        [ToggleIncognitoId]       = "Clipboard",

        [ColorSamplerId]          = "Tools",
        [ColorPickerId]           = "Tools",
        [OpenScreenshotFolderId]  = "Tools",
        [OpenLauncherId]          = "Tools",
        [OpenSettingsId]          = "Tools",
        [QrReadFromRegionId]      = "Tools",
        [SaveQrToHistoryId]       = "Tools",

        [WormholesToggleHideId]    = "Wormholes",
        [WormholesToggleLockId]    = "Wormholes",
        [WormholesToggleCollapseId] = "Wormholes",
        [WormholesCreateId]        = "Wormholes",
    };

    private static IReadOnlyList<PipelineProfile> BuildAll() =>
    [
        new PipelineProfile(
            Id: OnClipboardId,
            DisplayName: "On clipboard",
            Trigger: "event:clipboard",
            Steps:
            [
                new PipelineStep(AddToHistoryTask.TaskId)
            ],
            IsBuiltIn: true),

        // Region capture — single-shot. Auto-confirm ON so the first mouse-up commits the rect
        // (no Enter required). Editor opens after capture for quick annotation. Save + history +
        // clipboard + toast ON; upload + URL-copy OFF by default so the user only enables the
        // share leg when they actually want a public URL. Win+Shift+S keeps the muscle-memory
        // shortcut every Windows user already has on their finger.
        new PipelineProfile(
            Id: RegionCaptureId,
            DisplayName: "Capture region",
            Trigger: "hotkey:region",
            Steps:
            [
                new PipelineStep(CaptureRegionTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"autoConfirmOnFirstSelection\":true}"), Id: "capture-region"),
                new PipelineStep(OpenEditorBeforeUploadTaskId, Id: "open-editor"),
                // skipIfNotModified:true ⇒ this save only fires when the user actually edited the
                // image upstream in Open editor. showNotification:true gives the user the "saved"
                // confirmation only when an edit actually went to disk — clean captures with no
                // changes silently skip both the save and the toast.
                new PipelineStep(SaveToFileTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"showNotification\":true,\"skipIfNotModified\":true}"), Id: "save"),
                // First AddText sits AFTER save → bag.text = local_path, so this enters the AresToys
                // history as a Text item holding the saved file path. Push to Windows OFF + toast OFF
                // — purely an internal record next to the Image entry below.
                // AddText reads bag.text (= local_path set by SaveToFile) and inserts a Text row
                // pointing at the saved file. Terminal task (Inputs=[Text], Outputs=[]) — does
                // NOT touch bag.item_id / bag.new_item, so the next AddToHistory sees a clean
                // slot and commits the Image entry as the workflow's primary item.
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":false,\"showNotification\":false}"), Id: "add-text-path"),
                new PipelineStep(AddToHistoryTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"uploader\":\"\"}"), Enabled: false, Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                // Second AddText sits AFTER Upload → bag.text = upload URL. Disabled by default;
                // user flips Upload + this on when they actually want a public link.
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true,\"showNotification\":true}"), Enabled: false, Id: "add-text-url")            ],
            Hotkey: new HotkeyBinding(Win | Shift, 0x53),  // Win+Shift+S
            IsBuiltIn: true),

        // Multi-region capture — same shape as Region capture but auto-confirm OFF (the overlay
        // stays open, user drags multiple rects, presses Enter to commit) and Toast ON so the
        // user sees confirmation when the (potentially long) selection workflow finishes.
        new PipelineProfile(
            Id: MultiRegionCaptureId,
            DisplayName: "Capture multiple regions",
            Trigger: "hotkey:multi-region",
            Steps:
            [
                new PipelineStep(CaptureRegionTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"autoConfirmOnFirstSelection\":false}"), Id: "capture-region"),
                // Open editor disabled by default for multi-region: the user just dragged N rects,
                // popping the editor between drags would break the flow. They can enable it for a
                // post-batch annotation pass.
                new PipelineStep(OpenEditorBeforeUploadTaskId, Enabled: false, Id: "open-editor"),
                // Notification ON for multi-region: the gesture is long (drag → drag → … → Enter)
                // so an explicit "done, saved" toast confirms the workflow actually completed.
                new PipelineStep(SaveToFileTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"showNotification\":true}"), Id: "save"),
                // AddText reads bag.text (= local_path set by SaveToFile) and inserts a Text row
                // pointing at the saved file. Terminal task (Inputs=[Text], Outputs=[]) — does
                // NOT touch bag.item_id / bag.new_item, so the next AddToHistory sees a clean
                // slot and commits the Image entry as the workflow's primary item.
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":false,\"showNotification\":false}"), Id: "add-text-path"),
                new PipelineStep(AddToHistoryTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"uploader\":\"\"}"), Enabled: false, Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true,\"showNotification\":true}"), Enabled: false, Id: "add-text-url")            ],
            IsBuiltIn: true),

        // Active window / Active monitor share the same downstream shape as Capture multiple
        // regions (Save → AddText-path → AddToHistory → Upload[off] → UpdateUrl → AddText-url[off]),
        // only the source step differs. Keeps user customisations consistent across the three
        // single-shot captures.
        new PipelineProfile(
            Id: ActiveWindowCaptureId,
            DisplayName: "Capture active window",
            Trigger: "hotkey:active-window",
            Steps:
            [
                new PipelineStep(CaptureActiveWindowTaskId, Id: "capture-active-window"),
                new PipelineStep(OpenEditorBeforeUploadTaskId, Enabled: false, Id: "open-editor"),
                new PipelineStep(SaveToFileTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"showNotification\":true}"), Id: "save"),
                // AddText reads bag.text (= local_path set by SaveToFile) and inserts a Text row
                // pointing at the saved file. Terminal task (Inputs=[Text], Outputs=[]) — does
                // NOT touch bag.item_id / bag.new_item, so the next AddToHistory sees a clean
                // slot and commits the Image entry as the workflow's primary item.
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":false,\"showNotification\":false}"), Id: "add-text-path"),
                new PipelineStep(AddToHistoryTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"uploader\":\"\"}"), Enabled: false, Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true,\"showNotification\":true}"), Enabled: false, Id: "add-text-url")            ],
            // No default hotkey — Print Screen + Alt is Windows' built-in active-window screenshot
            // and we don't want to step on it. The user assigns one in Settings if they want it
            // routed through AresToys's pipeline (editor / upload / etc.) instead of just the clipboard.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: ActiveMonitorCaptureId,
            DisplayName: "Capture active monitor",
            Trigger: "hotkey:active-monitor",
            Steps:
            [
                // Active monitor = the screen the cursor is currently on. Useful as a hotkey on
                // multi-monitor setups so the user doesn't have to alt-tab into the right monitor's
                // app first; on single-monitor it degrades to a fullscreen of the only screen.
                new PipelineStep(CaptureActiveMonitorTaskId, Id: "capture-active-monitor"),
                new PipelineStep(OpenEditorBeforeUploadTaskId, Enabled: false, Id: "open-editor"),
                new PipelineStep(SaveToFileTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"showNotification\":true}"), Id: "save"),
                // AddText reads bag.text (= local_path set by SaveToFile) and inserts a Text row
                // pointing at the saved file. Terminal task (Inputs=[Text], Outputs=[]) — does
                // NOT touch bag.item_id / bag.new_item, so the next AddToHistory sees a clean
                // slot and commits the Image entry as the workflow's primary item.
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":false,\"showNotification\":false}"), Id: "add-text-path"),
                new PipelineStep(AddToHistoryTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"uploader\":\"\"}"), Enabled: false, Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true,\"showNotification\":true}"), Enabled: false, Id: "add-text-url")            ],
            // No default hotkey — leaves the keymap budget free; the user binds one in Settings
            // if they live with a multi-monitor rig.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: WebpageCaptureId,
            DisplayName: "Capture webpage",
            Trigger: "hotkey:webpage",
            Steps:
            [
                // The capture task either prompts for a URL (interactive, default) or reads it
                // from its own config (set in the workflow editor for "snapshot example.com on
                // demand" flows). Resulting PNG is full-page (CDP captureBeyondViewport).
                new PipelineStep(CaptureWebpageTaskId, Id: "capture-webpage"),
                new PipelineStep(OpenEditorBeforeUploadTaskId, Id: "open-editor"),
                new PipelineStep(SaveToFileTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"showNotification\":true}"), Id: "save"),
                // First AddText after save → bag.text = local_path. Adds a Text entry to the
                // history with the saved file path. Push to Windows OFF + toast OFF (the save
                // step already toasted).
                // AddText reads bag.text (= local_path set by SaveToFile) and inserts a Text row
                // pointing at the saved file. Terminal task (Inputs=[Text], Outputs=[]) — does
                // NOT touch bag.item_id / bag.new_item, so the next AddToHistory sees a clean
                // slot and commits the Image entry as the workflow's primary item.
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":false,\"showNotification\":false}"), Id: "add-text-path"),
                new PipelineStep(AddToHistoryTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"uploader\":\"\"}"), Enabled: false, Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                // Second AddText after Upload picks up bag.text = upload URL. Disabled by default;
                // user flips Upload + this on when they want a public link copied + toasted.
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true,\"showNotification\":true}"), Enabled: false, Id: "add-text-url")            ],
            // No default hotkey — webpage capture is mostly tray-driven (you've got the URL in
            // hand, you click "Webpage…"). The user binds one in Settings if they want it on
            // muscle memory.
            IsBuiltIn: true),

        // Two-step "screenshot a rect, leave it pinned on top". Mirrors the tray Tools → Pin to
        // screen → From screen flow but as a hotkey-bindable workflow. autoConfirm=true keeps
        // the picker single-shot (drag → release → pinned), matching the rest of the Capture
        // family. No save / history / upload steps — the only goal is the always-on-top pin.
        new PipelineProfile(
            Id: PinRegionToScreenId,
            DisplayName: "Pin region to screen",
            Trigger: "hotkey:pin-region",
            Steps:
            [
                new PipelineStep(CaptureRegionTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"autoConfirmOnFirstSelection\":true}"), Id: "capture-region"),
                new PipelineStep(PinToScreenTaskId, Id: "pin-to-screen"),
            ],
            IsBuiltIn: true),

        new PipelineProfile(
            Id: ManualUploadId,
            DisplayName: "Manual upload",
            Trigger: "menu:upload",
            Steps:
            [
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"category\":\"file\"}"), Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "copy-url")            ],
            IsBuiltIn: true),

        new PipelineProfile(
            Id: UploadClipboardTextId,
            DisplayName: "Upload clipboard text",
            Trigger: "hotkey:upload-clipboard-text",
            Steps:
            [
                // Mirrors region-capture but for text: pull text from clipboard → bag, upload to
                // text-category destinations (paste.rs / Pastebin / Gist / OneDrive / GoogleDrive
                // / Dropbox), copy resulting URL back to clipboard, toast. Lets the user grab any
                // selection (Ctrl+C anywhere) and turn it into a shareable link with one hotkey.
                new PipelineStep(UploadClipboardTextTaskId, Id: "read-clipboard-text"),
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"category\":\"text\"}"), Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "copy-url")            ],
            // No default hotkey — text upload is rarely a one-handed flow (you've usually got the
            // text already in the clipboard from elsewhere). User binds in Settings if desired.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: UploadSelectedFileId,
            DisplayName: "Upload selected file",
            Trigger: "hotkey:upload-selected-file",
            Steps:
            [
                // CaptureSelectedExplorerFile pulls the path from the foreground Explorer window
                // via Shell.Application COM, loads its bytes into the bag, and the rest of the
                // chain mirrors manual-upload. Hotkey-driven flow: user has a file selected,
                // hits the combo, file goes up, link lands on the clipboard.
                new PipelineStep(CaptureSelectedExplorerFileTaskId, Id: "capture-selected"),
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"category\":\"file\"}"), Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "copy-url")            ],
            // No default hotkey — user binds in Settings if they want it on muscle memory.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: ShortenClipboardUrlId,
            DisplayName: "Shorten clipboard URL",
            Trigger: "hotkey:shorten-clipboard-url",
            Steps:
            [
                // Same shape as upload-clipboard-text but routes through the Url category
                // (is.gd / v.gd) instead of Text. Reuses the read-clipboard-text task because
                // the input is still UTF-8 from the clipboard — the URL shortener uploaders
                // validate it's a real URL and fail loudly if not.
                new PipelineStep(UploadClipboardTextTaskId, Id: "read-clipboard-url"),
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"category\":\"url\"}"), Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "copy-url")            ],
            IsBuiltIn: true),

        new PipelineProfile(
            Id: ShowPopupId,
            DisplayName: "Show clipboard popup",
            Trigger: "hotkey:popup",
            Steps:
            [
                new PipelineStep(OpenPopupTaskId, Id: "show-popup")
            ],
            Hotkey: new HotkeyBinding(Win, 0x56),  // Win+V
            IsBuiltIn: true),

        new PipelineProfile(
            Id: ToggleIncognitoId,
            DisplayName: "Toggle incognito",
            Trigger: "hotkey:incognito",
            Steps:
            [
                new PipelineStep(ToggleIncognitoTaskId, Id: "toggle-incognito")
            ],
            Hotkey: new HotkeyBinding(Win | Shift, 0x4E),  // Win+Shift+N
            IsBuiltIn: true),

        // Colour-sampler workflow — 3-step composable chain that mirrors the image-capture
        // pipeline shape: Sampler emits bag.color → ConvertColor formats it as hex (default) and
        // writes bag.text → AddText pushes to the Windows clipboard AND adds to AresToys'
        // history. Format swap is one dropdown change in the editor; the old "8 muted CopyColorAs
        // steps" fan-out is gone.
        new PipelineProfile(
            Id: ColorSamplerId,
            DisplayName: "Color sampler",
            Trigger: "hotkey:color-sampler",
            Steps:
            [
                new PipelineStep(ColorSamplerTaskId, Id: "color-sampler"),
                new PipelineStep(ConvertColorTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"format\":\"hex\"}"), Id: "convert-color"),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true,\"showNotification\":false}"), Id: "add-text"),
            ],
            Hotkey: new HotkeyBinding(Ctrl, 0xDC),  // Ctrl+\
            IsBuiltIn: true),

        // Colour-picker workflow — same 3-step chain as Color sampler but driven by the
        // HSB/RGB/CMYK dialog instead of the screen sampler. Hotkey pairs Ctrl+Shift+\ with the
        // sampler's Ctrl+\.
        new PipelineProfile(
            Id: ColorPickerId,
            DisplayName: "Color picker",
            Trigger: "hotkey:color-picker",
            Steps:
            [
                new PipelineStep(ColorPickerTaskId, Id: "color-picker"),
                new PipelineStep(ConvertColorTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"format\":\"hex\"}"), Id: "convert-color"),
                new PipelineStep(AddTextTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true,\"showNotification\":false}"), Id: "add-text"),
            ],
            // Ctrl+Shift+\ — pairs with the existing Ctrl+\ Color sampler so the dialog launcher
            // and the screen sampler share the same root key. VK_OEM_5 (0xDC) = the \ / | key.
            Hotkey: new HotkeyBinding(Ctrl | Shift, 0xDC),
            IsBuiltIn: true),

        // Screen recording (mp4) — 3-step chain mirroring the image-capture pipeline shape:
        // Record screen (always emits mp4 bytes into the bag) → Save as Video file (writes the
        // mp4 verbatim to the user's capture folder, fires the toast) → Add Payload to AresToys
        // clipboard (commits the recording to AresToys' history with Windows-clipboard push).
        new PipelineProfile(
            Id: RecordScreenMp4Id,
            DisplayName: "Screen recording (mp4)",
            Trigger: "hotkey:record",
            Steps:
            [
                new PipelineStep(RecordScreenTaskId, Id: "record-screen"),
                new PipelineStep(SaveVideoFileTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"format\":\"mp4\",\"showNotification\":true}"), Id: "save-video"),
                new PipelineStep(AddToHistoryTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "add-to-history"),
            ],
            Hotkey: new HotkeyBinding(Shift, 0x2C),  // Shift+PrintScreen
            IsBuiltIn: true),

        // Screen recording (gif) — same chain as mp4 but Save Video file is configured for gif,
        // which kicks off an ffmpeg transcode (palette generation pass) on stop. Recording
        // itself is still mp4 — the format choice is purely a save-time decision.
        new PipelineProfile(
            Id: RecordScreenGifId,
            DisplayName: "Screen recording (gif)",
            Trigger: "hotkey:record-gif",
            Steps:
            [
                new PipelineStep(RecordScreenTaskId, Id: "record-screen-gif"),
                new PipelineStep(SaveVideoFileTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"format\":\"gif\",\"showNotification\":true}"), Id: "save-video-gif"),
                new PipelineStep(AddToHistoryTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "add-to-history-gif"),
            ],
            Hotkey: new HotkeyBinding(Ctrl | Shift, 0x2C),  // Ctrl+Shift+PrintScreen
            IsBuiltIn: true),

        new PipelineProfile(
            Id: OpenScreenshotFolderId,
            DisplayName: "Open screenshot folder",
            Trigger: "hotkey:open-screenshot-folder",
            Steps:
            [
                new PipelineStep(OpenScreenshotFolderTaskId, Id: "open-screenshot-folder")
            ],
            // No default hotkey — user assigns one if desired.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: QrReadFromRegionId,
            DisplayName: "Scan QR in region",
            Trigger: "hotkey:qr-read",
            Steps:
            [
                // Region picker → ZXing decode → AddToHistory commits the Text item (set by
                // QrRead which rewrites bag.new_item to Text kind) AND pushes to the Windows
                // clipboard AND shows the contextual toast. The toast's button set is picked
                // automatically: "Open URL" + "Copy URL" if the decoded text is an http(s) link,
                // "Copy text" + "Edit (system editor)" otherwise. QrRead aborts mid-pipeline
                // when no QR is found so the empty-payload trail doesn't poison anything.
                new PipelineStep(CaptureRegionTaskId, Id: "capture-region"),
                new PipelineStep(QrReadTaskId, Id: "qr-read"),
                new PipelineStep(AddToHistoryTask.TaskId,
                    Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true,\"showNotification\":true,\"notificationMessage\":\"QR: {bag.qr_text}\"}"),
                    Id: "add-to-history"),
            ],
            // No default hotkey — user binds in Settings if they want it on muscle memory.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: OpenLauncherId,
            DisplayName: "Open launcher",
            Trigger: "hotkey:launcher",
            Steps:
            [
                new PipelineStep(OpenLauncherMenuTaskId, Id: "open-launcher-menu")
            ],
            // Win+Z — picked because the Z position is unused by stock Windows shortcuts and
            // sits where the right-hand stays naturally in muscle-memory keyboard navigation.
            // The task itself toggles open/close so a second press dismisses the launcher.
            Hotkey: new HotkeyBinding(Win, 0x5A),
            IsBuiltIn: true),

        new PipelineProfile(
            Id: OpenSettingsId,
            DisplayName: "Open settings",
            Trigger: "hotkey:settings",
            Steps:
            [
                new PipelineStep(OpenSettingsTaskId, Id: "open-settings")
            ],
            // No default hotkey — Settings is mostly tray-driven. Exposed as a workflow so the
            // tray click picker (Settings tab) can route to it like any other action.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: SaveQrToHistoryId,
            DisplayName: "Save QR to history",
            Trigger: "menu:qr-save-to-history",
            Steps:
            [
                // Same shape as the screenshot capture profiles minus the capture step (we feed
                // bag.payload_bytes from outside). Save → history → clipboard → toast keeps the
                // QR alongside captures in the user's screenshot folder + Win+V history, with
                // the toast pointing them at the file path. Upload is intentionally not part of
                // the default; users who want it can clone this profile in Settings and tack on
                // an Upload step.
                new PipelineStep(SaveToFileTask.TaskId, Id: "save"),
                new PipelineStep(AddToHistoryTask.TaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"alsoCopyToWindows\":true}"), Id: "add-to-history"),
            ],
            IsBuiltIn: true),

        // Wormholes built-in templates. Each Toggle profile carries three steps where ONLY the
        // toggle is enabled — the two explicit on/off siblings are present-but-disabled so the
        // user can switch modes inline (mute the toggle, enable an explicit on or off) without
        // rebuilding the workflow. No default hotkeys.
        new PipelineProfile(
            Id: WormholesToggleHideId,
            DisplayName: "Toggle all Wormholes (Hidden/Shown)",
            Trigger: "hotkey:wormholes-toggle-hide",
            Steps:
            [
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"toggle-hide\"}"), Id: "toggle"),
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"hide-all\"}"),   Enabled: false, Id: "hide"),
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"show-all\"}"),   Enabled: false, Id: "show"),
            ],
            IsBuiltIn: true),
        new PipelineProfile(
            Id: WormholesToggleLockId,
            DisplayName: "Toggle all Wormholes (Locked/Unlocked)",
            Trigger: "hotkey:wormholes-toggle-lock",
            Steps:
            [
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"toggle-lock\"}"), Id: "toggle"),
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"lock-all\"}"),   Enabled: false, Id: "lock"),
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"unlock-all\"}"), Enabled: false, Id: "unlock"),
            ],
            IsBuiltIn: true),
        new PipelineProfile(
            Id: WormholesToggleCollapseId,
            DisplayName: "Toggle all Wormholes (Collapsed/Uncollapsed)",
            Trigger: "hotkey:wormholes-toggle-collapse",
            Steps:
            [
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"toggle-collapse\"}"), Id: "toggle"),
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"collapse-all\"}"),   Enabled: false, Id: "collapse"),
                new PipelineStep(WormholeBatchOpTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"op\":\"uncollapse-all\"}"), Enabled: false, Id: "uncollapse"),
            ],
            IsBuiltIn: true),
        new PipelineProfile(
            Id: WormholesCreateId,
            DisplayName: "Create Wormhole",
            Trigger: "hotkey:wormholes-create",
            Steps: [new PipelineStep(WormholeCreateTaskId, Id: "create")],
            IsBuiltIn: true),
    ];
}
