using System.Windows;
using System.Windows.Interop;
using AresToys.App.Services;
using AresToys.Capture;

namespace AresToys.App.Views;

/// <summary>Tiny prompt that asks for a URL before the webpage-capture pipeline runs. Kept as a
/// separate dialog (not a generic input box) so the placeholder text + the explanation about
/// login-walls live next to the field instead of being passed in by every caller. Centers
/// itself on the monitor under the cursor in the Loaded event (manual placement because the
/// WPF CenterScreen + SizeToContent combo lands the window at 0,0 — measures after position).
///
/// Non-modal (<see cref="System.Windows.Window.Show"/>, not ShowDialog): the WPF modal lock
/// disables every other window in the app, which kills the AresToys clipboard popup the user
/// might want to paste a URL from. Result is plumbed back via <see cref="CompletionTask"/>.</summary>
public partial class WebpageUrlDialog : Window
{
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WebpageUrlDialog(string? initialUrl = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        if (!string.IsNullOrWhiteSpace(initialUrl)) UrlBox.Text = initialUrl;
        Loaded += OnLoaded;
        // Refocus UrlBox every time the window becomes the foreground. WPF doesn't auto-restore
        // keyboard focus on its controls when its window is re-activated by another app's
        // SetForegroundWindow — without this, when the user opens the AresToys clipboard popup
        // on top of this dialog, picks an entry, and AutoPaster brings us back to the front to
        // send Ctrl+V, UrlBox isn't keyboard-focused and the paste falls on deaf ears (caret
        // visibly gone). Activated fires for every re-foregrounding, which is exactly the
        // hook we need.
        Activated += (_, _) =>
        {
            // Guard: don't fight an in-progress drag or some other focused element the user is
            // deliberately interacting with on the dialog. UrlBox.Focus() is a no-op when the
            // box already has focus, so the only real cost is one redundant call right after
            // Loaded — fine.
            if (!UrlBox.IsKeyboardFocused) UrlBox.Focus();
        };
        // Default to cancel; OnOkClicked overrides with the captured URL right before Close().
        Closed += (_, _) => _completion.TrySetResult(Url.Length == 0 ? null : Url);
    }

    public string Url { get; private set; } = string.Empty;

    /// <summary>Awaits the user's choice without modal-locking the rest of the app. Resolves to
    /// the captured URL on Capture, or null on Cancel / Esc / Close (X).</summary>
    public Task<string?> CompletionTask => _completion.Task;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Recenter on the monitor under the cursor (the user's "current screen"). MonitorEnumeration
        // returns physical pixels; convert to DIPs through the window's PresentationSource so the
        // window lands centered on scaled monitors instead of drifting toward the corner. Fallback
        // to PrimaryScreen DIPs if the monitor enumeration / source aren't ready.
        var monitor = MonitorEnumeration.GetMonitorUnderCursor();
        if (monitor is not null && PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            var topLeft = target.TransformFromDevice.Transform(new Point(monitor.X, monitor.Y));
            var size    = target.TransformFromDevice.Transform(new Point(monitor.Width, monitor.Height));
            Left = topLeft.X + (size.X - ActualWidth) / 2.0;
            Top  = topLeft.Y + (size.Y - ActualHeight) / 2.0;
        }
        else
        {
            Left = (SystemParameters.PrimaryScreenWidth  - ActualWidth)  / 2.0;
            Top  = (SystemParameters.PrimaryScreenHeight - ActualHeight) / 2.0;
        }

        // Activate() pulls the window to foreground so the subsequent UrlBox.Focus() actually
        // routes the keyboard focus into the text box — without it, Loaded fires before the
        // window is the foreground HWND and Focus() is a no-op (paste / typing would land
        // wherever the previous foreground window was).
        Activate();
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        var raw = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(raw)) return;
        // Auto-prefix scheme so the user can type "example.com" — WebView2 rejects schemeless input.
        if (!raw.Contains("://", System.StringComparison.Ordinal)) raw = "https://" + raw;
        Url = raw;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Url = string.Empty;
        Close();
    }
}
