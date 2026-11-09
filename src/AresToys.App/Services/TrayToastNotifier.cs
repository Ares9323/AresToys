using System.Windows;

namespace AresToys.App.Services;

public sealed class TrayToastNotifier : IToastNotifier
{
    private readonly TrayIconService _tray;

    public TrayToastNotifier(TrayIconService tray)
    {
        _tray = tray;
    }

    public void Show(string title, string message, Action? onClick = null, string? imagePath = null,
                     IReadOnlyList<ToastButtonChoice>? buttons = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(message);
        // Tray balloon notifications (NOTIFYICONDATA) don't support inline images or action
        // buttons — the legacy Win32 API only carries title/message/icon. Both arguments are
        // accepted for interface compatibility and silently ignored when this notifier is
        // active; callers fall back to a text-only balloon.
        _ = buttons;

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _tray.ShowToast(title, message, onClick);
        });
    }
}
