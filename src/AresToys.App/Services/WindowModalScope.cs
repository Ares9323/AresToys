using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AresToys.App.Services;

/// <summary>Helpers around <see cref="Window.ShowDialog"/> that limit the modal block to the
/// dialog's <see cref="Window.Owner"/> instead of every top-level window in the app.
///
/// <para>
/// Default WPF behaviour: <c>ShowDialog</c> calls Win32 <c>EnableThreadWindows(false)</c> for the
/// entire UI thread, which disables every other top-level window — including unrelated editors
/// the user already had open. From the user's POV, opening "pick an icon" freezes a screenshot
/// editor opened ten seconds earlier; not what they expect.
/// </para>
///
/// <para>
/// Fix: after WPF wraps the dialog in its modal stack we re-call <c>EnableWindow</c> on every
/// other top-level window the app owns (skipping the dialog itself + its declared Owner). The
/// Owner stays disabled — that's the actual modal scope the dialog wants — but everything else
/// stays interactive. ShowDialog's normal teardown path still restores the Owner on close.
/// </para>
/// </summary>
public static class WindowModalScope
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    /// <summary>Drop-in replacement for <see cref="Window.ShowDialog"/> that keeps siblings
    /// interactive. Use whenever you want a modal scoped to a single owner — icon picker,
    /// uploader config, color picker invoked from somewhere other than the editor.</summary>
    public static bool? ShowOwnerScopedDialog(this Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var app = Application.Current;
        if (app is null) return dialog.ShowDialog();

        // Snapshot siblings BEFORE Show — Application.Windows can mutate as the dialog opens.
        // Windows that haven't been shown yet (Handle == 0) are skipped; we'll re-enable any
        // others on the dialog's Loaded event below.
        var siblings = app.Windows.OfType<Window>()
            .Where(w => !ReferenceEquals(w, dialog) && !ReferenceEquals(w, dialog.Owner))
            .ToList();

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            dialog.Loaded -= OnLoaded;
            foreach (var w in siblings)
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd != IntPtr.Zero) EnableWindow(hwnd, true);
            }
        }
        dialog.Loaded += OnLoaded;
        return dialog.ShowDialog();
    }
}
