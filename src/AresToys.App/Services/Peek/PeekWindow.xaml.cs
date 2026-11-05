using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using AresToys.App.Services.Zones;
using AresToys.Hotkeys;

namespace AresToys.App.Services.Peek;

/// <summary>The Peek preview window: one file of a list shown fitted to the window, with the file name and a
/// counter on the title row, the pixel and file size in the footer, and the arrow, Home, End, Enter and Esc keys.
/// The window is created once and re-used: <see cref="Show(IReadOnlyList{string}, int, IntPtr)"/> shows or
/// re-targets it, Esc and the close button only hide it, <see cref="Shutdown"/> really closes it.
/// Images are decoded on the thread pool (<see cref="PeekImageLoader"/>); text, code, Markdown and HTML go through one
/// web view (<see cref="PeekWebContent"/> builds the page). UI thread only.</summary>
public partial class PeekWindow : FluentWindow
{
    private const double WorkAreaFraction = 0.7;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint SwpNoSize = 0x0001;
    private const int MonitorInfoExSize = 104;

    private IReadOnlyList<string> _files = Array.Empty<string>();
    private int _index;
    private bool _allowClose;
    private bool _mouseOver;
    private int _targetPixelWidth;
    private int _version;
    private CancellationTokenSource? _loadCts;
    private Task<bool>? _webReady;
    private bool _webRuntimeMissing;
    private bool _navigationAllowed;

    public PeekWindow()
    {
        InitializeComponent();
        _openInButton = new Wpf.Ui.Controls.Button
        {
            Content = AresToys.App.Resources.Strings.Peek_OpenIn,
            Icon = new SymbolIcon(SymbolRegular.ChevronDown24),
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        _openInButton.Click += OnOpenInClick;
        OpenInHost.Content = _openInButton;
    }

    private readonly Wpf.Ui.Controls.Button _openInButton;

    /// <summary>Gives the programs of the "Open in" menu for a file path (the assigned one first). The window knows
    /// nothing about where they come from. Null or an empty list hides the button.</summary>
    public Func<string, IReadOnlyList<PeekProgram>>? ProgramsProvider { get; set; }

    /// <summary>Raised when a menu item is chosen: the program, or null for the system default app, and the file.</summary>
    public event Action<PeekProgram?, string>? OpenInRequested;

    /// <summary>Raised with the current file path when Enter is pressed (open with the Windows default app).</summary>
    public event Action<string>? OpenRequested;

    /// <summary>Raised with the current file path when the open key (<see cref="PeekKeys.OpenAssigned"/>) is pressed
    /// (open with the program assigned to the extension, else the default app).</summary>
    public event Action<string>? OpenAssignedRequested;

    /// <summary>The configurable keys; the module sets them before each <see cref="Show"/>, so a change saved in
    /// Settings applies to the next Peek.</summary>
    public PeekKeys Keys { get; set; } = PeekKeys.Default;

    /// <summary>The file shown now, or null when the list is empty.</summary>
    public string? CurrentPath => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    /// <summary>The footer slot on the right where the "Open in" menu goes.</summary>
    public ContentPresenter OpenInHost => OpenInPresenter;

    /// <summary>Shows the window (or re-targets it when already visible) on <paramref name="files"/> at
    /// <paramref name="index"/>, sized to about 70% of the work area of the monitor holding
    /// <paramref name="anchorHwnd"/> (the primary monitor when it is zero) and centered on it.</summary>
    public void Show(IReadOnlyList<string> files, int index, IntPtr anchorHwnd)
    {
        _files = files;
        _index = files.Count == 0 ? 0 : Math.Clamp(index, 0, files.Count - 1);

        Place(anchorHwnd);
        if (!IsVisible)
        {
            base.Show();
        }

        Activate();
        ShowCurrent();
    }

    /// <summary>Allows the window to close for real and closes it. The only way to end its life.</summary>
    public void Shutdown()
    {
        _allowClose = true;
        CancelLoad();
        PreviewWeb.Dispose();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HidePeek();
            return;
        }

        base.OnClosing(e);
    }

    private void HidePeek()
    {
        CancelLoad();
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt combinations arrive as Key.System.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                HidePeek();
                break;
            case Key.Left:
                MoveTo(_index - 1);
                break;
            case Key.Right:
                MoveTo(_index + 1);
                break;
            case Key.Home:
                MoveTo(0);
                break;
            case Key.End:
                MoveTo(_files.Count - 1);
                break;
            case Key.Enter:
                if (CurrentPath is { } path)
                {
                    OpenRequested?.Invoke(path);
                }

                break;
            default:
                if (!HandleConfiguredKey(key, e.IsRepeat))
                {
                    return;
                }

                break;
        }

        e.Handled = true;
    }

    /// <summary>The two configurable keys. The window has the focus, so the main app's hook never sees them: the
    /// toggle shortcut closes the window, the open key asks to open the file in its assigned program. The settings
    /// refuse the navigation keys, so these never shadow the cases above. Auto-repeat is ignored: a key still held
    /// from the press that opened Peek must not close it at once. True when the key was one of them.</summary>
    private bool HandleConfiguredKey(Key key, bool isRepeat)
    {
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var modifiers = CurrentModifiers();
        if (PeekKeys.Matches(Keys.Toggle, modifiers, vk))
        {
            if (!isRepeat)
            {
                HidePeek();
            }

            return true;
        }

        if (PeekKeys.Matches(Keys.OpenAssigned, modifiers, vk))
        {
            if (!isRepeat && CurrentPath is { } path)
            {
                OpenAssignedRequested?.Invoke(path);
            }

            return true;
        }

        return false;
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var keyboard = Keyboard.Modifiers;
        var result = HotkeyModifiers.None;
        if ((keyboard & ModifierKeys.Control) != 0) result |= HotkeyModifiers.Control;
        if ((keyboard & ModifierKeys.Alt) != 0) result |= HotkeyModifiers.Alt;
        if ((keyboard & ModifierKeys.Shift) != 0) result |= HotkeyModifiers.Shift;
        if ((keyboard & ModifierKeys.Windows) != 0) result |= HotkeyModifiers.Win;
        return result;
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => MoveTo(_index - 1);

    private void OnNextClick(object sender, RoutedEventArgs e) => MoveTo(_index + 1);

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        _mouseOver = true;
        UpdateChevrons();
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        _mouseOver = false;
        UpdateChevrons();
    }

    /// <summary>Moves to another file; no wrap-around, so a move past either end does nothing.</summary>
    private void MoveTo(int index)
    {
        if (_files.Count == 0 || index < 0 || index >= _files.Count || index == _index)
        {
            return;
        }

        _index = index;
        ShowCurrent();
    }

    private IReadOnlyList<PeekProgram> GetPrograms()
    {
        if (CurrentPath is not { } path || ProgramsProvider is null) return Array.Empty<PeekProgram>();
        return ProgramsProvider(path);
    }

    private void UpdateOpenInButton()
        => _openInButton.Visibility = GetPrograms().Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnOpenInClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } path) return;
        var programs = GetPrograms();
        if (programs.Count == 0) return;

        var menu = new ContextMenu { PlacementTarget = _openInButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var defaultItem = new System.Windows.Controls.MenuItem { Header = AresToys.App.Resources.Strings.Peek_OpenInDefault };
        defaultItem.Click += (_, _) => OpenInRequested?.Invoke(null, path);
        menu.Items.Add(defaultItem);
        menu.Items.Add(new Separator());
        for (var i = 0; i < programs.Count; i++)
        {
            var program = programs[i];
            var item = new System.Windows.Controls.MenuItem { Header = program.Name };
            // The assigned program is the first one only when an assignment exists; the provider puts it first.
            if (i == 0 && PeekAssigned(path) == program) item.FontWeight = FontWeights.Bold;
            item.Click += (_, _) => OpenInRequested?.Invoke(program, path);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Assigned program: the provider lists it first, the module tells through <see cref="AssignedProvider"/>.</summary>
    private PeekProgram? PeekAssigned(string path) => AssignedProvider?.Invoke(path);

    /// <summary>The program assigned to the file's extension, or null; shown in bold in the menu.</summary>
    public Func<string, PeekProgram?>? AssignedProvider { get; set; }

    private void UpdateChevrons()
    {
        PrevButton.Visibility = _mouseOver && _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _mouseOver && _index < _files.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Fills the title row for the current file at once and starts the decode; a decode still running for
    /// an earlier file is cancelled and, if it finishes anyway, its result is dropped by the version check.</summary>
    private void ShowCurrent()
    {
        CancelLoad();
        var path = CurrentPath;
        var name = path is null ? string.Empty : Path.GetFileName(path);

        Title = name;
        Bar.Title = name;
        CounterText.Text = _files.Count == 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture, "{0} / {1}", _index + 1, _files.Count);
        PreviewImage.Source = null;
        FallbackText.Visibility = Visibility.Collapsed;
        InfoText.Text = string.Empty;
        UpdateChevrons();
        UpdateOpenInButton();
        var kind = path is null ? PeekPreviewKind.None : PeekPreview.For(path);
        var isWeb = kind is PeekPreviewKind.Html or PeekPreviewKind.Markdown or PeekPreviewKind.Code;
        PreviewWeb.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
        ReturnFocusToWindow();
        if (path is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _ = isWeb ? LoadWebAsync(path, kind, ++_version, cts.Token) : LoadAsync(path, ++_version, cts.Token);
    }

    private bool IsCurrent(int version, CancellationToken ct) => version == _version && !ct.IsCancellationRequested;

    /// <summary>The web view is a native window: once the user clicks into a page it keeps the keyboard focus and
    /// the arrows, Home and End scroll the page. Moving to another file takes the focus back, so the keys of this
    /// window work again until the next click into a page.</summary>
    private void ReturnFocusToWindow()
    {
        if (PreviewWeb.IsKeyboardFocusWithin)
        {
            Focus();
        }
    }

    /// <summary>Text, code, Markdown or HTML: builds the page off the UI thread (HTML is served from its own file),
    /// then navigates the web view. A page that finishes after another file was requested is dropped.</summary>
    private async Task LoadWebAsync(string path, PeekPreviewKind kind, int version, CancellationToken ct)
    {
        // Start the web view while the page is built; creating the browser is the slow part of the first preview.
        var readyTask = EnsureWebViewAsync();
        PeekWebPage? page = null;
        if (kind != PeekPreviewKind.Html)
        {
            var theme = CurrentPageTheme();
            try
            {
                page = await Task.Run(() => PeekWebContent.Build(path, kind, theme, ct), ct);
            }
            catch (OperationCanceledException)
            {
                // Another file was requested meanwhile: nothing to show for this one.
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file vanished, is locked or is unreadable: like an undecodable image, show its name only.
                if (IsCurrent(version, ct))
                {
                    ShowNameOnly(path);
                }

                return;
            }
        }

        var ready = await readyTask;
        if (!ready)
        {
            _webReady = null; // Try again at the next web preview (the runtime may have been installed meanwhile).
        }

        if (!IsCurrent(version, ct))
        {
            return;
        }

        if (!ready)
        {
            ShowNameOnly(path, _webRuntimeMissing ? AresToys.App.Resources.Strings.Peek_NoWebView : null);
            return;
        }

        var core = PreviewWeb.CoreWebView2;
        var isHtml = kind == PeekPreviewKind.Html;
        var folder = Path.GetDirectoryName(path);
        core.ClearVirtualHostNameToFolderMapping(PeekWebContent.FileHost);
        if (!string.IsNullOrEmpty(folder))
        {
            core.SetVirtualHostNameToFolderMapping(PeekWebContent.FileHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        }

        // An HTML file runs no scripts. Our own pages for Markdown and code need theirs (highlight.js).
        core.Settings.IsScriptEnabled = !isHtml;
        PreviewWeb.DefaultBackgroundColor = isHtml ? System.Drawing.Color.White : ToDrawingColor(CurrentBackground());
        _navigationAllowed = true;
        if (page is null)
        {
            // The query keeps an edited file from coming out of the browser cache.
            core.Navigate("https://" + PeekWebContent.FileHost + "/" + Uri.EscapeDataString(Path.GetFileName(path))
                + "?v=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            InfoText.Text = PeekWebContent.DescribeHtml(path);
        }
        else
        {
            core.NavigateToString(page.Html);
            InfoText.Text = page.Info;
        }
    }

    /// <summary>The file name over an empty area (what an image that cannot be decoded gets), or <paramref name="message"/>.</summary>
    private void ShowNameOnly(string path, string? message = null)
    {
        PreviewWeb.Visibility = Visibility.Collapsed;
        FallbackText.Text = message ?? Path.GetFileName(path);
        FallbackText.Visibility = Visibility.Visible;
    }

    /// <summary>Creates the web view's browser once, on the first web preview, and keeps it for the window's life.
    /// It has its own user data folder: the main app's folder cannot be shared with this non elevated process. The
    /// task is False when the runtime is missing or the browser could not start.</summary>
    private Task<bool> EnsureWebViewAsync() => _webReady ??= InitWebViewAsync();

    private async Task<bool> InitWebViewAsync()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AresToys-Data", "WebView2", "Peek");
            Directory.CreateDirectory(folder);
            var environment = await CoreWebView2Environment.CreateAsync(null, folder);
            await PreviewWeb.EnsureCoreWebView2Async(environment);

            var core = PreviewWeb.CoreWebView2;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping(PeekWebContent.AssetsHost,
                Path.Combine(AppContext.BaseDirectory, "Peek", "Web"), CoreWebView2HostResourceAccessKind.DenyCors);

            // Only the navigation this window started goes through: links, redirects and window.open do nothing.
            core.NavigationStarting += (_, e) =>
            {
                if (_navigationAllowed)
                {
                    _navigationAllowed = false;
                }
                else
                {
                    e.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += (_, _) => ReturnFocusToWindow();
            _webRuntimeMissing = false;
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _webRuntimeMissing = true;
            return false;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            // The browser process could not start (blocked user data folder, missing Web assets folder): the
            // window shows the file name, like an image that cannot be decoded.
            _webRuntimeMissing = false;
            return false;
        }
    }

    /// <summary>The page colours from the app theme: the Surface1 background of this window and its text colour; dark
    /// when that background is dark (the app theme manager's answer when the resource is missing).</summary>
    private static PeekPageTheme CurrentPageTheme()
    {
        var background = CurrentBackground();
        var hasResource = Application.Current?.TryFindResource("Surface1Color") is Color;
        var luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255;
        var dark = hasResource ? luminance < 0.5 : ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var foreground = (Application.Current?.TryFindResource("AccentForegroundBrush") as SolidColorBrush)?.Color
            ?? (dark ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Color.FromRgb(0x20, 0x20, 0x20));
        return new PeekPageTheme(dark, ToCss(background), ToCss(foreground));
    }

    private static Color CurrentBackground()
        => Application.Current?.TryFindResource("Surface1Color") is Color color
            ? color
            : ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White;

    private static string ToCss(Color color) => string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    private static System.Drawing.Color ToDrawingColor(Color color) => System.Drawing.Color.FromArgb(color.R, color.G, color.B);

    private async Task LoadAsync(string path, int version, CancellationToken ct)
    {
        PeekImageInfo info;
        try
        {
            info = await PeekImageLoader.LoadWithInfoAsync(path, _targetPixelWidth, ct);
        }
        catch (OperationCanceledException)
        {
            // Another file was requested meanwhile: nothing to show for this one.
            return;
        }

        // Back on the UI thread. A newer request, Esc or Shutdown makes this result stale.
        if (version != _version || ct.IsCancellationRequested)
        {
            return;
        }

        if (info.Bitmap is { } bitmap)
        {
            PreviewImage.Source = bitmap;
            FallbackText.Visibility = Visibility.Collapsed;
        }
        else
        {
            FallbackText.Text = Path.GetFileName(path);
            FallbackText.Visibility = Visibility.Visible;
        }

        InfoText.Text = FormatInfo(info);
    }

    private void CancelLoad()
    {
        _version++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private static string FormatInfo(PeekImageInfo info)
    {
        var size = info.FileBytes >= 0 ? PeekWebContent.FormatBytes(info.FileBytes) : string.Empty;
        if (info.Bitmap is null)
        {
            return size;
        }

        var pixels = string.Format(CultureInfo.CurrentCulture, "{0} x {1}", info.PixelWidth, info.PixelHeight);
        return size.Length == 0 ? pixels : pixels + "  ·  " + size;
    }

    /// <summary>Sizes the window to 70% of the work area of the anchor's monitor and centers it there. Works in
    /// physical pixels on the window handle, so the result is right on a monitor with another scale than the one
    /// the window was last on: the window is first moved onto the target monitor (which settles its DPI), then sized.</summary>
    private void Place(IntPtr anchorHwnd)
    {
        var info = new ZoneNative.MonitorInfoEx { Size = MonitorInfoExSize };
        var monitor = MonitorFromWindow(anchorHwnd, MonitorDefaultToPrimary);
        var work = monitor != 0 && ZoneNative.GetMonitorInfo(monitor, ref info)
            ? info.Work
            : new ZoneNative.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };

        var width = (int)Math.Round((work.Right - work.Left) * WorkAreaFraction);
        var height = (int)Math.Round((work.Bottom - work.Top) * WorkAreaFraction);
        var x = work.Left + (work.Right - work.Left - width) / 2;
        var y = work.Top + (work.Bottom - work.Top - height) / 2;
        _targetPixelWidth = width;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        ZoneNative.SetWindowPos(hwnd, 0, x, y, 0, 0, SwpNoSize | ZoneNative.SwpNoZOrder | ZoneNative.SwpNoActivate);
        ZoneNative.SetWindowPos(hwnd, 0, x, y, width, height, ZoneNative.SwpNoZOrder | ZoneNative.SwpNoActivate);

        if (!IsVisible && PresentationSource.FromVisual(this) is { } source)
        {
            // Let WPF's Left/Top/Width/Height agree with the handle before the first Show, so it does not
            // move the window back to its own idea of the position.
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            var topLeft = fromDevice.Transform(new System.Windows.Point(x, y));
            var size = fromDevice.Transform(new System.Windows.Point(width, height));
            Left = topLeft.X;
            Top = topLeft.Y;
            Width = size.X;
            Height = size.Y;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}
