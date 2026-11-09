using System.Windows;
using AresToys.App.Views;

namespace AresToys.App.Services;

/// <summary>Custom WPF toast notifier. Stacks multiple toasts in the bottom-right of the primary
/// screen so notifications appear immediately without the Win32 BalloonTip queue delay.</summary>
public sealed class WpfToastNotifier : IToastNotifier
{
    private const double EdgeMargin = 16;
    private const double StackSpacing = 4;
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);

    private readonly List<ToastWindow> _active = [];

    public void Show(string title, string message, Action? onClick = null, string? imagePath = null,
                     IReadOnlyList<ToastButtonChoice>? buttons = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(message);
        // imagePath is the IToastNotifier API for inline image previews. The custom WPF
        // toast doesn't render images today; ignored silently — callers get a text-only toast.
        // buttons are similarly ignored — this notifier is a fallback for environments where
        // WinRT toasts can't run; the modern WindowsToastNotifier renders buttons properly.
        _ = buttons;

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var t = new ToastWindow(title, message, DefaultDuration, onClick);
            t.Dismissed += OnToastDismissed;
            t.Loaded += (_, _) => RepositionStack();
            _active.Add(t);
            t.Show();
            RepositionStack();
        });
    }

    private void OnToastDismissed(object? sender, EventArgs e)
    {
        if (sender is ToastWindow t)
        {
            _active.Remove(t);
            RepositionStack();
        }
    }

    /// <summary>Place toasts stacked from bottom-right upward on the primary screen.
    /// SystemParameters.WorkArea excludes the taskbar; convert pixel rect to DIPs via PresentationSource.</summary>
    private void RepositionStack()
    {
        var work = SystemParameters.WorkArea;
        // SystemParameters.WorkArea is already in DIPs.
        var bottom = work.Bottom - EdgeMargin;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var t = _active[i];
            // ActualHeight is 0 until first layout; fall back to a reasonable default.
            var h = t.ActualHeight > 0 ? t.ActualHeight : 80;
            t.Left = work.Right - t.Width - EdgeMargin + 8; // +8 compensates for the Border's outer Margin
            t.Top = bottom - h;
            bottom -= h + StackSpacing;
        }
    }
}
