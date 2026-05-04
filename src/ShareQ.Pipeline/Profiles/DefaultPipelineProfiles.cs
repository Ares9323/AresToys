using ShareQ.Core.Pipeline;
using ShareQ.Pipeline.Tasks;

namespace ShareQ.Pipeline.Profiles;

public static class DefaultPipelineProfiles
{
    // Profile / workflow ids
    public const string OnClipboardId       = "on-clipboard";
    public const string RegionCaptureId     = "region-capture";
    public const string ActiveWindowCaptureId = "active-window-capture";
    public const string ActiveMonitorCaptureId = "active-monitor-capture";
    public const string WebpageCaptureId       = "webpage-capture";
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

    // Task IDs whose implementations live in ShareQ.App (resolved at runtime by the registry).
    public const string CopyImageToClipboardTaskId = "shareq.copy-image-to-clipboard";
    public const string CopyTextToClipboardTaskId  = "shareq.copy-text-to-clipboard";
    public const string NotifyToastTaskId          = "shareq.notify-toast";
    public const string OpenEditorBeforeUploadTaskId = "shareq.open-editor-before-upload";
    public const string ApplyImageEffectsPresetTaskId = "shareq.apply-image-effects-preset";
    public const string OpenPopupTaskId            = "shareq.open-popup";
    public const string ToggleIncognitoTaskId      = "shareq.toggle-incognito";
    public const string ColorSamplerTaskId         = "shareq.color-sampler";
    public const string ColorPickerTaskId          = "shareq.color-picker";
    public const string CopyColorAsHexTaskId       = "shareq.copy-color-hex";
    public const string CaptureRegionTaskId        = "shareq.capture-region";
    public const string CaptureActiveWindowTaskId  = "shareq.capture-active-window";
    public const string CaptureActiveMonitorTaskId = "shareq.capture-active-monitor";
    public const string CaptureWebpageTaskId       = "shareq.capture-webpage";
    public const string CaptureSelectedExplorerFileTaskId = "shareq.capture-selected-explorer-file";
    public const string QrReadTaskId               = "shareq.qr-read";
    public const string RecordScreenTaskId         = "shareq.record-screen";
    public const string OpenScreenshotFolderTaskId = "shareq.open-screenshot-folder";
    public const string OpenLauncherMenuTaskId     = "shareq.open-launcher-menu";
    public const string OpenSettingsTaskId         = "shareq.open-settings";
    public const string UploadClipboardTextTaskId  = "shareq.upload-clipboard-text";

    // Task IDs from ShareQ.Plugins.
    public const string UploadTaskId = "shareq.upload";

    // Hotkey modifier flag values (mirrors ShareQ.Hotkeys.HotkeyModifiers; kept as raw ints so
    // ShareQ.Pipeline doesn't pull in the Hotkeys assembly).
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
        [ActiveWindowCaptureId]   = "Capture",
        [ActiveMonitorCaptureId]  = "Capture",
        [WebpageCaptureId]        = "Capture",
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

        new PipelineProfile(
            Id: RegionCaptureId,
            DisplayName: "Region capture",
            Trigger: "hotkey:region",
            Steps:
            [
                new PipelineStep(CaptureRegionTaskId, Id: "capture-region"),
                new PipelineStep(OpenEditorBeforeUploadTaskId, Enabled: false, Id: "open-editor"),
                new PipelineStep(SaveToFileTask.TaskId, Id: "save"),
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(CopyImageToClipboardTaskId, Id: "copy-image"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"category\":\"image\"}"), Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.upload_urls}\"}"), Id: "copy-url"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"Saved {bag.local_path}\"}"), Id: "toast")
            ],
            Hotkey: new HotkeyBinding(Win | Shift, 0x53),  // Win+Shift+S
            IsBuiltIn: true),

        new PipelineProfile(
            Id: ActiveWindowCaptureId,
            DisplayName: "Active window capture",
            Trigger: "hotkey:active-window",
            Steps:
            [
                // Same shape as region-capture but the first step picks the foreground window's
                // bounds (DWM extended-frame-bounds aware) instead of opening the overlay. Every
                // downstream step is identical so user customisations to either profile carry the
                // same intent.
                new PipelineStep(CaptureActiveWindowTaskId, Id: "capture-active-window"),
                new PipelineStep(OpenEditorBeforeUploadTaskId, Enabled: false, Id: "open-editor"),
                new PipelineStep(SaveToFileTask.TaskId, Id: "save"),
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(CopyImageToClipboardTaskId, Id: "copy-image"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"category\":\"image\"}"), Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.upload_urls}\"}"), Id: "copy-url"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"Saved {bag.local_path}\"}"), Id: "toast")
            ],
            // No default hotkey — Print Screen + Alt is Windows' built-in active-window screenshot
            // and we don't want to step on it. The user assigns one in Settings if they want it
            // routed through ShareQ's pipeline (editor / upload / etc.) instead of just the clipboard.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: ActiveMonitorCaptureId,
            DisplayName: "Active monitor capture",
            Trigger: "hotkey:active-monitor",
            Steps:
            [
                // Active monitor = the screen the cursor is currently on. Useful as a hotkey on
                // multi-monitor setups so the user doesn't have to alt-tab into the right monitor's
                // app first; on single-monitor it degrades to a fullscreen of the only screen.
                new PipelineStep(CaptureActiveMonitorTaskId, Id: "capture-active-monitor"),
                new PipelineStep(OpenEditorBeforeUploadTaskId, Enabled: false, Id: "open-editor"),
                new PipelineStep(SaveToFileTask.TaskId, Id: "save"),
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(CopyImageToClipboardTaskId, Id: "copy-image"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"category\":\"image\"}"), Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.upload_urls}\"}"), Id: "copy-url"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"Saved {bag.local_path}\"}"), Id: "toast")
            ],
            // No default hotkey — leaves the keymap budget free; the user binds one in Settings
            // if they live with a multi-monitor rig.
            IsBuiltIn: true),

        new PipelineProfile(
            Id: WebpageCaptureId,
            DisplayName: "Webpage capture",
            Trigger: "hotkey:webpage",
            Steps:
            [
                // The capture task either prompts for a URL (interactive, default) or reads it
                // from its own config (set in the workflow editor for "snapshot example.com on
                // demand" flows). Resulting PNG is full-page (CDP captureBeyondViewport).
                new PipelineStep(CaptureWebpageTaskId, Id: "capture-webpage"),
                new PipelineStep(OpenEditorBeforeUploadTaskId, Enabled: false, Id: "open-editor"),
                new PipelineStep(SaveToFileTask.TaskId, Id: "save"),
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(CopyImageToClipboardTaskId, Id: "copy-image"),
                new PipelineStep(UploadTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"category\":\"image\"}"), Id: "upload"),
                new PipelineStep(UpdateItemUrlTask.TaskId),
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.upload_urls}\"}"), Id: "copy-url"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"Captured {bag.window_title}\"}"), Id: "toast")
            ],
            // No default hotkey — webpage capture is mostly tray-driven (you've got the URL in
            // hand, you click "Webpage…"). The user binds one in Settings if they want it on
            // muscle memory.
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
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.upload_urls}\"}"), Id: "copy-url"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"Uploaded\"}"), Id: "toast")
            ],
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
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.upload_urls}\"}"), Id: "copy-url"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"Uploaded text → {bag.upload_url}\"}"), Id: "toast")
            ],
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
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.upload_urls}\"}"), Id: "copy-url"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"Uploaded → {bag.upload_url}\"}"), Id: "toast")
            ],
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
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.upload_urls}\"}"), Id: "copy-url"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"Shortened → {bag.upload_url}\"}"), Id: "toast")
            ],
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

        new PipelineProfile(
            Id: ColorSamplerId,
            DisplayName: "Color sampler",
            Trigger: "hotkey:color-sampler",
            Steps:
            [
                // Sample → emit hex. Composable: user can swap CopyColorAsHex for any other
                // CopyColorAs* (RGB, FLinearColor, CMYK, …) without touching the sampler.
                new PipelineStep(ColorSamplerTaskId, Id: "color-sampler"),
                new PipelineStep(CopyColorAsHexTaskId, Id: "copy-as-hex")
            ],
            Hotkey: new HotkeyBinding(Ctrl, 0xDC),  // Ctrl+\
            IsBuiltIn: true),

        new PipelineProfile(
            Id: ColorPickerId,
            DisplayName: "Color picker",
            Trigger: "hotkey:color-picker",
            Steps:
            [
                new PipelineStep(ColorPickerTaskId, Id: "color-picker"),
                new PipelineStep(CopyColorAsHexTaskId, Id: "copy-as-hex")
            ],
            // Ctrl+Shift+\ — pairs with the existing Ctrl+\ Color sampler so the dialog launcher
            // and the screen sampler share the same root key. VK_OEM_5 (0xDC) = the \ / | key.
            Hotkey: new HotkeyBinding(Ctrl | Shift, 0xDC),
            IsBuiltIn: true),

        new PipelineProfile(
            Id: RecordScreenMp4Id,
            DisplayName: "Screen recording (mp4)",
            Trigger: "hotkey:record",
            Steps:
            [
                new PipelineStep(RecordScreenTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"format\":\"mp4\"}"), Id: "record-screen")
            ],
            Hotkey: new HotkeyBinding(Shift, 0x2C),  // Shift+PrintScreen
            IsBuiltIn: true),

        new PipelineProfile(
            Id: RecordScreenGifId,
            DisplayName: "Screen recording (gif)",
            Trigger: "hotkey:record-gif",
            Steps:
            [
                new PipelineStep(RecordScreenTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"format\":\"gif\"}"), Id: "record-screen-gif")
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
            DisplayName: "QR — read from screen region",
            Trigger: "hotkey:qr-read",
            Steps:
            [
                // Region picker → ZXing decode → text replaces image payload → clipboard +
                // toast. Aborts mid-pipeline if no QR is found so the empty-payload trail
                // doesn't poison the history / clipboard with junk.
                new PipelineStep(CaptureRegionTaskId, Id: "capture-region"),
                new PipelineStep(QrReadTaskId, Id: "qr-read"),
                new PipelineStep(AddToHistoryTask.TaskId, Id: "add-to-history"),
                new PipelineStep(CopyTextToClipboardTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"template\":\"{bag.qr_text}\"}"), Id: "copy-text"),
                new PipelineStep(NotifyToastTaskId, Config: System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"ShareQ\",\"message\":\"QR → {bag.qr_text}\"}"), Id: "toast")
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
    ];
}
