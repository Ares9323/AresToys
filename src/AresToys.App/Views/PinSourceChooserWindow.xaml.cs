using System.Windows;
using System.Windows.Input;

namespace AresToys.App.Views;

public enum PinSource { Cancelled, Screen, Clipboard, File }

public partial class PinSourceChooserWindow : Window
{
    private readonly TaskCompletionSource<PinSource> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PinSource Result { get; private set; } = PinSource.Cancelled;

    /// <summary>Awaitable that resolves with the user's pick (or Cancelled) when the window
    /// closes. Lets callers <c>Show()</c> the window modelessly — the rest of the app stays
    /// interactive while the chooser is up — and still get a single-await result.</summary>
    public Task<PinSource> CompletionTask => _completion.Task;

    public PinSourceChooserWindow()
    {
        InitializeComponent();
        FromScreenButton.Click    += (_, _) => Pick(PinSource.Screen);
        FromClipboardButton.Click += (_, _) => Pick(PinSource.Clipboard);
        FromFileButton.Click      += (_, _) => Pick(PinSource.File);
        CancelButton.Click        += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // Any path that ends with Close() — including the user clicking the OS-level X (none
        // here since WindowStyle=None, but kept for safety) or pressing Esc — falls through
        // here. Pick() pre-sets Result before Close() so the TCS sees the right value; the
        // default (Cancelled) wins when no button was clicked.
        Closed += (_, _) => _completion.TrySetResult(Result);
    }

    private void Pick(PinSource s) { Result = s; Close(); }
}
