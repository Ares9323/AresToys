using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AresToys.App.Native;
using AresToys.App.Services.KeySequences;

namespace AresToys.App.Views;

/// <summary>Non-activating, transparent, topmost popup that lists the candidate items for a
/// matched Replacer trigger. Pure presentation — selection / confirm / dismiss are exposed as
/// methods so the <see cref="KeySequenceTracker"/> can drive the UI from its atomic-binding
/// callbacks without the window ever gaining keyboard focus.</summary>
public partial class KeySequenceOverlay : Window
{
    private readonly List<OverlayRow> _rows = new();
    private int _selectedIndex;
    private Action<OverlayOption>? _onConfirm;
    private Action? _onDismiss;

    public KeySequenceOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Total option count. 0 when not showing.</summary>
    public int OptionCount => _rows.Count;

    public void SetOptions(IReadOnlyList<OverlayOption> options,
        Action<OverlayOption> onConfirm,
        Action onDismiss)
    {
        _onConfirm = onConfirm;
        _onDismiss = onDismiss;
        _rows.Clear();
        for (var i = 0; i < options.Count; i++)
            _rows.Add(new OverlayRow(options[i]) { IsSelected = i == 0 });
        _selectedIndex = 0;
        OptionsList.ItemsSource = null;
        OptionsList.ItemsSource = _rows;
    }

    public int SelectedIndex => _selectedIndex;
    public bool HasConfirmCallback => _onConfirm is not null;

    public void SelectNext() => ChangeSelection(+1);
    public void SelectPrevious() => ChangeSelection(-1);

    public void ConfirmCurrent()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _rows.Count) return;
        var chosen = _rows[_selectedIndex].Option;
        var cb = _onConfirm;
        // Null the callbacks before invoking so a callback that triggers Hide() doesn't recurse
        // into OnDismissCallback via the Deactivated/Closing handlers.
        _onConfirm = null;
        _onDismiss = null;
        cb?.Invoke(chosen);
    }

    public void RaiseDismiss()
    {
        var cb = _onDismiss;
        _onConfirm = null;
        _onDismiss = null;
        cb?.Invoke();
    }

    private void ChangeSelection(int delta)
    {
        if (_rows.Count == 0) return;
        _rows[_selectedIndex].IsSelected = false;
        _selectedIndex = (_selectedIndex + delta + _rows.Count) % _rows.Count;
        _rows[_selectedIndex].IsSelected = true;
        // Force the ItemsControl to re-bind so the row-template DataTrigger redraws.
        OptionsList.Items.Refresh();
    }

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OverlayRow row)
        {
            var idx = _rows.IndexOf(row);
            if (idx >= 0)
            {
                _selectedIndex = idx;
                ConfirmCurrent();
                e.Handled = true;
            }
        }
    }

    /// <summary>Drag-to-move handler on the outer border. Uses the
    /// <c>ReleaseCapture</c> + <c>WM_NCLBUTTONDOWN HTCAPTION</c> trick instead of WPF's
    /// <see cref="System.Windows.Window.DragMove"/> because the latter requires window
    /// activation — and the overlay is <c>WS_EX_NOACTIVATE</c> on purpose (must not steal focus
    /// from whichever app the user is typing into). The native move loop honours non-activating
    /// windows. Row clicks reach <see cref="Row_MouseLeftButtonDown"/> first and set Handled=true,
    /// so a click on a row item never triggers drag.</summary>
    private void OuterBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }
        catch { /* race in WPF mouse capture / invalid hwnd — swallow */ }
        e.Handled = true;
    }

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Apply WS_EX_NOACTIVATE so the window can be visible without ever stealing focus from
        // the foreground app the user is typing into.
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLong(IntPtr hwnd, int idx)
        => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, idx) : new IntPtr(GetWindowLong32(hwnd, idx));

    private static IntPtr SetWindowLong(IntPtr hwnd, int idx, IntPtr value)
        => IntPtr.Size == 8 ? SetWindowLongPtr(hwnd, idx, value) : new IntPtr(SetWindowLong32(hwnd, idx, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}

/// <summary>View-model row holding an <see cref="OverlayOption"/> + a mutable selected flag the
/// DataTrigger reads. Implements <see cref="INotifyPropertyChanged"/> so re-binds after a
/// SelectNext/SelectPrevious see the new highlight without rebuilding the entire ItemsSource.</summary>
internal sealed class OverlayRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public OverlayRow(OverlayOption option) { Option = option; }

    public OverlayOption Option { get; }
    public string Preview => Option.Preview;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
