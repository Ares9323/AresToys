namespace ShareQ.App.Services;

/// <summary>One actionable button to render at the bottom of a toast. Up to 5 buttons per toast
/// (Windows hard limit; the toolkit silently drops anything over). Label is rendered as-is —
/// callers should keep it short (≤ ~15 chars) so the OS doesn't truncate. Handler runs on the
/// WPF UI thread, marshalled by the notifier.</summary>
public sealed record ToastButtonChoice(string Label, Action OnClick);

public interface IToastNotifier
{
    /// <summary>Show a toast. The optional <paramref name="onClick"/> handler is invoked
    /// on the UI thread if the user clicks the balloon while it's visible.
    /// <paramref name="imagePath"/> is an absolute file path to a PNG/JPEG the toast should
    /// display alongside the text — implementations that don't support inline images (the
    /// legacy balloon, the custom WPF stack) silently ignore it.
    /// <paramref name="buttons"/> renders explicit action buttons under the body. When
    /// supplied, the body click (<paramref name="onClick"/>) is typically left null so the
    /// user is forced to pick an explicit action — this avoids the "tap-on-body does
    /// something different per scenario" confusion the pipeline task used to have.</summary>
    void Show(string title, string message, Action? onClick = null, string? imagePath = null,
              IReadOnlyList<ToastButtonChoice>? buttons = null);
}
