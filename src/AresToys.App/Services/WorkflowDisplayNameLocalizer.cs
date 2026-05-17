using AresToys.App.Resources;
using AresToys.Pipeline.Profiles;

namespace AresToys.App.Services;

/// <summary>UI-side translator for built-in pipeline profile names + categories. The seeder
/// writes <c>profile.DisplayName</c> in English to the DB on first run; we don't rewrite the DB
/// every culture flip (that would clobber any user rename). Instead the UI calls
/// <see cref="Localize"/> on every render, which returns the live translation for built-ins
/// and the stored value as-is for user customs / unknown ids.</summary>
public static class WorkflowDisplayNameLocalizer
{
    /// <summary>Map a built-in profile id → resx key. Misses fall through to the persisted
    /// DisplayName (custom workflows + any new built-in we forget to register here).</summary>
    private static readonly IReadOnlyDictionary<string, string> WorkflowKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DefaultPipelineProfiles.OnClipboardId]            = nameof(Strings.Workflow_OnClipboard),
        [DefaultPipelineProfiles.RegionCaptureId]          = nameof(Strings.Workflow_RegionCapture),
        [DefaultPipelineProfiles.ActiveWindowCaptureId]    = nameof(Strings.Workflow_ActiveWindowCapture),
        [DefaultPipelineProfiles.ActiveMonitorCaptureId]   = nameof(Strings.Workflow_ActiveMonitorCapture),
        [DefaultPipelineProfiles.WebpageCaptureId]         = nameof(Strings.Workflow_WebpageCapture),
        [DefaultPipelineProfiles.PinRegionToScreenId]      = nameof(Strings.Workflow_PinRegionToScreen),
        [DefaultPipelineProfiles.ManualUploadId]           = nameof(Strings.Workflow_ManualUpload),
        [DefaultPipelineProfiles.UploadClipboardTextId]    = nameof(Strings.Workflow_UploadClipboardText),
        [DefaultPipelineProfiles.UploadSelectedFileId]     = nameof(Strings.Workflow_UploadSelectedFile),
        [DefaultPipelineProfiles.ShortenClipboardUrlId]    = nameof(Strings.Workflow_ShortenClipboardUrl),
        [DefaultPipelineProfiles.ShowPopupId]              = nameof(Strings.Workflow_ShowPopup),
        [DefaultPipelineProfiles.ToggleIncognitoId]        = nameof(Strings.Workflow_ToggleIncognito),
        [DefaultPipelineProfiles.ColorSamplerId]           = nameof(Strings.Workflow_ColorSampler),
        [DefaultPipelineProfiles.ColorPickerId]            = nameof(Strings.Workflow_ColorPicker),
        [DefaultPipelineProfiles.RecordScreenMp4Id]        = nameof(Strings.Workflow_RecordScreenMp4),
        [DefaultPipelineProfiles.RecordScreenGifId]        = nameof(Strings.Workflow_RecordScreenGif),
        [DefaultPipelineProfiles.OpenScreenshotFolderId]   = nameof(Strings.Workflow_OpenScreenshotFolder),
        [DefaultPipelineProfiles.QrReadFromRegionId]       = nameof(Strings.Workflow_QrReadFromRegion),
        [DefaultPipelineProfiles.OpenLauncherId]           = nameof(Strings.Workflow_OpenLauncher),
        [DefaultPipelineProfiles.OpenSettingsId]           = nameof(Strings.Workflow_OpenSettings),
        [DefaultPipelineProfiles.SaveQrToHistoryId]        = nameof(Strings.Workflow_SaveQrToHistory),
    };

    /// <summary>Category buckets defined as bare strings in <see cref="DefaultPipelineProfiles.CategoriesById"/>;
    /// the same five values rendered as Hotkeys-list group headers. Anything else (custom user-
    /// invented category) falls through unchanged.</summary>
    private static readonly IReadOnlyDictionary<string, string> CategoryKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Capture"]   = nameof(Strings.WorkflowCategory_Capture),
        ["Upload"]    = nameof(Strings.WorkflowCategory_Upload),
        ["Clipboard"] = nameof(Strings.WorkflowCategory_Clipboard),
        ["Tools"]     = nameof(Strings.WorkflowCategory_Tools),
        ["Other"]     = nameof(Strings.WorkflowCategory_Other),
    };

    /// <summary>Cultura di lookup. Same precedence the LocExtension indexer uses:
    /// the static override pinned by LocalizationService.ApplyToThread wins; CurrentUICulture
    /// is the fallback for early callsites running before the service has loaded.</summary>
    // Read culture from the LocalizedStrings singleton (instance field, survives the WPF /
    // Hosting reset of static culture state we discovered during the i18n bring-up). Strings.
    // Culture is unreliable as a primary source here.
    private static System.Globalization.CultureInfo Culture =>
        Markup.LocalizedStrings.Instance.Culture ?? System.Globalization.CultureInfo.CurrentUICulture;

    public static string Localize(string profileId, string fallback)
    {
        if (string.IsNullOrEmpty(profileId)) return fallback;
        if (!WorkflowKeys.TryGetValue(profileId, out var key)) return fallback;
        return Strings.ResourceManager.GetString(key, Culture) ?? fallback;
    }

    public static string LocalizeCategory(string category)
    {
        if (!CategoryKeys.TryGetValue(category, out var key)) return category;
        return Strings.ResourceManager.GetString(key, Culture) ?? category;
    }
}
