using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AresToys.Editor.Views;

/// <summary>Owner-scoped variant of <see cref="Window.ShowDialog"/>: keeps unrelated top-level
/// windows interactive while the dialog is up. Mirror of <c>AresToys.App.Services.WindowModalScope</c>;
/// duplicated here because <c>AresToys.Editor</c> doesn't reference the App project (the
/// dependency direction is App → Editor, not the other way).</summary>
internal static class WindowModalScope
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    public static bool? ShowOwnerScopedDialog(this Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var app = Application.Current;
        if (app is null) return dialog.ShowDialog();

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
