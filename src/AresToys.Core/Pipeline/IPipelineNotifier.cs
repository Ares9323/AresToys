namespace AresToys.Core.Pipeline;

/// <summary>
/// Cross-csproj abstraction for "show the user a toast about what just happened in this
/// pipeline step". The concrete WPF implementation lives in
/// <c>AresToys.App.Services.Notifications.ToastBuilderService</c> — Pipeline / Plugins tasks
/// consume this lightweight interface so they stay free of WPF / Win32 dependencies.
/// Implementations decide which buttons / image preview to attach by inspecting the bag.
/// </summary>
public interface IPipelineNotifier
{
    /// <summary>Show a contextual toast for the current pipeline step. Body is the pipeline's
    /// current text (<c>bag.text</c>) — set by the previous text-producing step (SaveToFile /
    /// Upload / QrRead / RecordScreen / etc.). No template expansion — single source of truth.</summary>
    /// <param name="context">Bag whose contents drive the body, button set, and image preview.</param>
    /// <param name="title">Optional toast title override. Null = "AresToys".</param>
    /// <param name="suppressEditorButton">When true, "Open in editor" is dropped from the toast
    /// buttons. Used by SaveToFile when its <c>skipIfNotModified</c> flag is set — that mode only
    /// fires after the user has already edited the image in an upstream Open-editor step, so a
    /// reopen-in-editor button would be redundant noise.</param>
    /// <param name="overrideItemId">When set, the toast pretends bag.item_id is this value
    /// without actually writing to the bag. Used by terminal Add* tasks (AddText / AddFile /
    /// AddImage) so they can surface their just-inserted DB row in the toast without claiming
    /// the bag's item_id slot — the slot belongs to the workflow's payload-primary item.</param>
    /// <param name="overrideItem">Companion to <paramref name="overrideItemId"/> — supplies the
    /// NewItem object for kind-based button routing.</param>
    void ShowFromBag(PipelineContext context, string? title = null, bool suppressEditorButton = false,
        long? overrideItemId = null, object? overrideItem = null);
}
