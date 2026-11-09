using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AresToys.App.Services.Rendering;

/// <summary>Gives back the video memory of windows that are hidden or closed.
/// A hidden-but-alive WPF window keeps the hardware render device, so the surfaces of closed windows
/// (capture overlays, the editor) are never freed. Every hidden HwndSource of the thread (windows never shown,
/// hidden windows and popup roots alike) is switched to software rendering, and when nothing is visible a tiny
/// cloaked helper window is shown for a moment: WPF frees the composition resources of closed windows only when
/// some window renders a frame afterwards. A source is back on the default (hardware) mode as soon as its HWND is
/// about to show (WM_SHOWWINDOW), before its first frame, because a transparent full screen overlay did not
/// appear in software rendering (decisions#capture-overlay-per-monitor).</summary>
internal static partial class VideoMemoryReleaser
{
    private const int WM_SHOWWINDOW = 0x0018;

    private static readonly TimeSpan ReleaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HelperLifetime = TimeSpan.FromMilliseconds(300);

    // Windows that already carry the trigger handlers: Loaded can fire again after an unload.
    private static readonly ConditionalWeakTable<Window, object> HookedWindows = new();

    // Sources that already carry the WM_SHOWWINDOW hook.
    private static readonly ConditionalWeakTable<HwndSource, object> HookedSources = new();

    private static Dispatcher? _dispatcher;
    private static DispatcherTimer? _releaseTimer;
    private static bool _installed;
    private static bool _helperActive;

    /// <summary>Registers the class handler once. Call on the UI thread before the first window is created.</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        _dispatcher = Dispatcher.CurrentDispatcher;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        // Catches the windows that are created hidden at start-up and never shown (the main window).
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ScheduleRelease));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded is a routed event: a child element's Loaded bubbles up here too, only the window itself counts.
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window)) return;
        if (window is ReleaseHelperWindow) return;
        if (HookedWindows.TryGetValue(window, out _)) return;
        HookedWindows.Add(window, new object());

        window.IsVisibleChanged += OnIsVisibleChanged;
        window.Closed += OnClosed;

        // The window can already be hidden by the time Loaded runs (hidden right after Show).
        if (!window.IsVisible) ScheduleRelease();
    }

    /// <summary>Only a trigger: the sources are switched at the release tick. A window that shows goes back to
    /// Default here as well, synchronously inside Show and before the render pass (the WM_SHOWWINDOW hook does the same).</summary>
    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Window window) return;
        if ((bool)e.NewValue)
        {
            if (PresentationSource.FromVisual(window) is HwndSource source) SetRenderMode(source, RenderMode.Default);
        }
        else
        {
            ScheduleRelease();
        }
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.IsVisibleChanged -= OnIsVisibleChanged;
            window.Closed -= OnClosed;
        }
        ScheduleRelease();
    }

    /// <summary>WM_SHOWWINDOW with wParam != 0 arrives before the window shows: hardware again before its first frame.</summary>
    private static IntPtr OnSourceMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SHOWWINDOW && wParam != IntPtr.Zero && HwndSource.FromHwnd(hwnd) is { } source)
            SetRenderMode(source, RenderMode.Default);
        return IntPtr.Zero;
    }

    private static void SetRenderMode(HwndSource source, RenderMode mode)
    {
        try
        {
            if (source.IsDisposed) return;
            var target = source.CompositionTarget;
            if (target is null || target.RenderMode == mode) return;
            // Never touch a visible window: a visible live window holds its memory regardless.
            if (mode == RenderMode.SoftwareOnly && IsWindowVisible(source.Handle)) return;
            target.RenderMode = mode;
        }
        catch (InvalidOperationException)
        {
            // The window is closing or already closed (ObjectDisposedException is an InvalidOperationException):
            // nothing left to switch.
        }
    }

    /// <summary>One release per burst of events: every hide or close restarts the delay.</summary>
    private static void ScheduleRelease()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _releaseTimer ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = ReleaseDelay };
        _releaseTimer.Tick -= OnReleaseTick;
        _releaseTimer.Tick += OnReleaseTick;
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    private static void OnReleaseTick(object? sender, EventArgs e)
    {
        _releaseTimer?.Stop();
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        // Walk every HwndSource of this thread: hidden ones go to software, a visible one means the helper is useless.
        var anyVisible = false;
        var sources = new List<HwndSource>();
        foreach (PresentationSource presentationSource in PresentationSource.CurrentSources)
        {
            if (presentationSource is HwndSource { IsDisposed: false } source && ReferenceEquals(source.Dispatcher, dispatcher))
                sources.Add(source);
        }

        foreach (var source in sources)
        {
            if (source.RootVisual is ReleaseHelperWindow) continue;
            try
            {
                if (source.CompositionTarget is null) continue;
                if (IsWindowVisible(source.Handle))
                {
                    anyVisible = true;
                    continue;
                }

                SetRenderMode(source, RenderMode.SoftwareOnly);
                if (!HookedSources.TryGetValue(source, out _))
                {
                    HookedSources.Add(source, new object());
                    source.AddHook(OnSourceMessage);
                }
            }
            catch (InvalidOperationException)
            {
                // The source was disposed meanwhile.
            }
        }

        // A visible window keeps the render device anyway, and while a capture is open its views are visible: the
        // helper must not appear then (it would also be one more window next to the overlay). The next hide or
        // close schedules again.
        if (anyVisible || _helperActive) return;
        ShowHelper(dispatcher);
    }

    /// <summary>Shows an 8x8 cloaked window for a moment so WPF renders a frame and frees what closed windows held.</summary>
    private static void ShowHelper(Dispatcher dispatcher)
    {
        ReleaseHelperWindow helper;
        try
        {
            helper = new ReleaseHelperWindow();
            helper.Show();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Win32Exception)
        {
            return;
        }

        _helperActive = true;
        var close = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = HelperLifetime };
        close.Tick += (_, _) =>
        {
            close.Stop();
            _helperActive = false;
            try { helper.Close(); }
            catch (InvalidOperationException) { /* already closed */ }
        };
        close.Start();
    }

    /// <summary>The render helper. A type of its own so the walk and the class handler can leave it out (no loop).</summary>
    private sealed class ReleaseHelperWindow : Window
    {
        private const int DWMWA_CLOAK = 13;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_BOTTOM = new(1);

        internal ReleaseHelperWindow()
        {
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            Width = 8;
            Height = 8;
            Content = new System.Windows.Shapes.Rectangle { Width = 8, Height = 8, Fill = Brushes.Black };
            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var one = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref one, sizeof(int));
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);
}
