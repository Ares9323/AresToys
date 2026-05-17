namespace AresToys.App.Services.KeySequences;

/// <summary>UX-facing contract for the overlay window. Abstracted out of the concrete WPF
/// <see cref="SequenceOverlayHost"/> so the tracker / dispatcher can be unit-tested without
/// instantiating WPF dependencies. All members must be safe to call from the UI thread only —
/// callers (the tracker) marshal via <see cref="System.Windows.Application.Current.Dispatcher"/>.</summary>
public interface ISequenceOverlay
{
    bool IsVisible { get; }

    /// <summary>Show the overlay with the supplied candidate list. <paramref name="onConfirm"/> is
    /// called with the chosen item when the user confirms; <paramref name="onDismiss"/> is called
    /// when the overlay closes without a selection (cancel / typed-past-match). Both callbacks
    /// fire on the UI thread.</summary>
    void Show(
        IReadOnlyList<OverlayOption> options,
        Action<OverlayOption> onConfirm,
        Action onDismiss);

    void Close();

    void SelectNext();
    void SelectPrevious();

    /// <summary>Trigger confirm on the currently-selected item. Equivalent to the user pressing
    /// the configured confirm key.</summary>
    void ConfirmCurrent();
}

/// <summary>One row in the overlay list. <paramref name="ItemId"/> is what the dispatcher uses to
/// resolve and paste; <paramref name="Preview"/> is the rendered label (truncated body text or
/// item label).</summary>
public sealed record OverlayOption(long ItemId, string Preview);
