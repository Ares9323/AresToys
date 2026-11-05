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
using AresToys.App.Services.Pins;
using AresToys.App.Services.Zones;
using AresToys.Capture;
using AresToys.Hotkeys;
using PixelRect = AresToys.App.Services.Pins.PixelRect;

namespace AresToys.App.Services.Peek;

/// <summary>The Peek preview window: one file of a list shown fitted to the window, with the file name and a
/// counter on the title row, the pixel and file size in the footer, and the arrow, Home, End, Enter and Esc keys.
/// The window is created once and re-used: <see cref="Show(IReadOnlyList{string}, int, IntPtr)"/> shows or
/// re-targets it, Esc and the close button only hide it, <see cref="Shutdown"/> really closes it.
/// Images are decoded on the thread pool (<see cref="PeekImageLoader"/>); text, code, Markdown and HTML go through one
/// web view (<see cref="PeekWebContent"/> builds the page); any other file with a registered Windows preview handler
/// is shown by that handler in <see cref="PeekShellPreview"/>. The window can be resized with the mouse and opens at
/// the bounds it had when it was last hidden (<see cref="BoundsRemembered"/>); with
/// <see cref="PeekWindowSettings.FitToContent"/> it sizes itself to each image instead. All the sizing is in physical
/// pixels on the window handle. UI thread only.</summary>
public partial class PeekWindow : FluentWindow
{
    private const double WorkAreaFraction = 0.7;
    private const double FitCapFraction = 0.9;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const int MonitorInfoExSize = 104;

    private IReadOnlyList<string> _files = Array.Empty<string>();
    private IReadOnlyList<string> _allFiles = Array.Empty<string>();
    private PeekBrowseMode _browse;
    private int _index;
    private bool _allowClose;
    private PeekNavOverlay? _nav;
    private int _targetPixelWidth;
    private int _version;
    private CancellationTokenSource? _loadCts;
    private Task<bool>? _webReady;
    private bool _webRuntimeMissing;
    private bool _navigationAllowed;
    private bool _videoShown;
    private string _videoInfo = string.Empty;

    // Sizing state. _saved: the bounds last stored (or handed to BoundsRemembered). _geometryIsFit: the window has
    // the size of an image, not the user's; _remembered is then the user's bounds from before (null when never used).
    private PixelRect? _saved;
    private PixelRect? _remembered;
    private bool _geometryIsFit;

    /// <summary>The window title until a file is shown (then the title is the file name).</summary>
    public const string WindowTitle = "Peek";

    /// <summary>Window property set on the Peek window's handle. The main app recognises Peek as the foreground window by
    /// it and the satellite's process id, so the toggle key can close it whatever child (web view, shell preview) holds
    /// the focus. The title cannot serve: it follows the file shown.</summary>
    public const string WindowMarker = "AresToys.Peek";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AresToys.App.Native.AppNativeMethods.SetProp(new WindowInteropHelper(this).Handle, WindowMarker, 1);
    }

    public PeekWindow()
    {
        InitializeComponent();
        Title = WindowTitle;
        _openInButton = new Wpf.Ui.Controls.Button
        {
            Content = AresToys.App.Resources.Strings.Peek_OpenIn,
            Icon = new SymbolIcon(SymbolRegular.ChevronDown24),
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        _openInButton.Click += OnOpenInClick;
        OpenInHost.Content = _openInButton;
        _showAsButton = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon(SymbolRegular.ChevronDown24),
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        _showAsButton.Click += OnShowAsClick;
        ShowAsPresenter.Content = _showAsButton;
        _browseButton = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon(SymbolRegular.ChevronDown24),
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        _browseButton.Click += OnBrowseClick;
        BrowsePresenter.Content = _browseButton;
    }

    private readonly Wpf.Ui.Controls.Button _browseButton;

    /// <summary>The "Browse" button names the mode; it shows only when the folder has more than one file.</summary>
    private void UpdateBrowseButton()
    {
        _browseButton.Content = AresToys.App.Resources.Strings.Peek_Nav + ": " + PeekBrowse.DisplayName(_browse);
        _browseButton.Visibility = _allFiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = _browseButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var mode in new[] { PeekBrowseMode.All, PeekBrowseMode.Family, PeekBrowseMode.Extension })
        {
            var item = new System.Windows.Controls.MenuItem { Header = PeekBrowse.DisplayName(mode), IsChecked = mode == _browse };
            item.Click += (_, _) => ChooseBrowse(mode);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>A mode picked in the footer: the list is filtered again around the current file (still shown, no reload),
    /// the counter and chevrons follow, and the choice is saved.</summary>
    private void ChooseBrowse(PeekBrowseMode mode)
    {
        if (mode == _browse) return;
        _browse = mode;
        ApplyBrowse(CurrentPath);
        CounterText.Text = _files.Count == 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture, "{0} / {1}", _index + 1, _files.Count);
        UpdateChevrons();
        UpdateBrowseButton();
        Settings.SaveBrowse?.Invoke(mode);
    }

    /// <summary>Sets <see cref="_files"/> and <see cref="_index"/> from the whole folder list by the browse mode, around
    /// <paramref name="current"/>.</summary>
    private void ApplyBrowse(string? current)
    {
        (_files, _index) = PeekBrowse.Filter(_allFiles, current, _browse);
    }

    private readonly Wpf.Ui.Controls.Button _openInButton;
    private readonly Wpf.Ui.Controls.Button _showAsButton;

    /// <summary>Raised when an entry of the "Show as" menu is chosen: the file and the pick id (<see cref="PeekShowAs"/>),
    /// null for Auto. The module saves the pick, then calls <see cref="ReloadCurrent"/>.</summary>
    public event Action<string, string?>? ShowAsRequested;

    /// <summary>Shows the current file again from scratch (a "Show as" pick changed).</summary>
    public void ReloadCurrent()
    {
        if (IsVisible && CurrentPath is not null)
        {
            ShowCurrent();
        }
    }

    /// <summary>The "Show as" button: hidden for a file with no extension or a video, else it names the current pick.</summary>
    private void UpdateShowAsButton(string? path, PeekPreviewKind kind)
    {
        if (path is null || Path.GetExtension(path).Length == 0 || kind == PeekPreviewKind.Video)
        {
            _showAsButton.Visibility = Visibility.Collapsed;
            return;
        }

        var pick = PeekShowAs.PickFor(path);
        _showAsButton.Content = AresToys.App.Resources.Strings.Peek_ShowAs + ": "
            + (pick is null ? AresToys.App.Resources.Strings.Peek_ShowAsAuto : PeekShowAs.DisplayName(pick));
        _showAsButton.Visibility = Visibility.Visible;
    }

    private void OnShowAsClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } path) return;
        var current = PeekShowAs.PickFor(path);
        var menu = new ContextMenu { PlacementTarget = _showAsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };

        System.Windows.Controls.MenuItem Entry(string header, string? pick, bool isChecked)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, IsChecked = isChecked };
            item.Click += (_, _) => ShowAsRequested?.Invoke(path, pick);
            return item;
        }

        menu.Items.Add(Entry($"{AresToys.App.Resources.Strings.Peek_ShowAsAuto} ({AutoKindName(path)})", null, current is null));
        menu.Items.Add(new Separator());
        foreach (var pick in PeekShowAs.BasicPicks)
        {
            menu.Items.Add(Entry(PeekShowAs.DisplayName(pick), pick, current == pick));
        }

        var code = new System.Windows.Controls.MenuItem
        {
            Header = AresToys.App.Resources.Strings.Peek_ShowAsCode,
            IsChecked = current?.StartsWith(PeekShowAs.CodePrefix, StringComparison.Ordinal) == true,
        };
        foreach (var language in PeekPreview.CodeLanguageChoices)
        {
            var pick = PeekShowAs.CodePrefix + language.Id;
            code.Items.Add(Entry(language.DisplayName, pick, current == pick));
        }

        menu.Items.Add(code);
        if (PeekPreview.PreviewHandlerFor(path) is not null)
        {
            menu.Items.Add(Entry(PeekShowAs.DisplayName(PeekShowAs.Shell), PeekShowAs.Shell, current == PeekShowAs.Shell));
        }

        if (PeekPreview.CanDecodeAsImage(path))
        {
            menu.Items.Add(Entry(PeekShowAs.DisplayName(PeekShowAs.Image), PeekShowAs.Image, current == PeekShowAs.Image));
        }

        menu.IsOpen = true;
    }

    /// <summary>What the built-in rules would show the file as, for the "Auto (...)" entry: the language for code.</summary>
    private static string AutoKindName(string path) => PeekPreview.AutoKindFor(path) switch
    {
        PeekPreviewKind.Code => PeekPreview.AutoLanguageFor(path).DisplayName,
        PeekPreviewKind.Markdown => "Markdown",
        PeekPreviewKind.Html => "HTML",
        PeekPreviewKind.Image => AresToys.App.Resources.Strings.Peek_ShowAsImage,
        PeekPreviewKind.Shell => AresToys.App.Resources.Strings.Peek_ShowAsShell,
        _ => AresToys.App.Resources.Strings.Peek_ShowAsNone,
    };

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

    /// <summary>The saved bounds and the fit option; the module sets them before each <see cref="Show"/>, so a change
    /// saved in Settings applies to the next Peek.</summary>
    public PeekWindowSettings Settings { get; set; } = PeekWindowSettings.Default;

    /// <summary>Raised with the window's bounds (physical pixels) when the window is hidden or shut down and they differ
    /// from the saved ones. Never raised for the size of an image the window was fitted to. The module saves them.</summary>
    public event Action<PixelRect>? BoundsRemembered;

    /// <summary>The file shown now, or null when the list is empty.</summary>
    public string? CurrentPath => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    /// <summary>The footer slot on the right where the "Open in" menu goes.</summary>
    public ContentPresenter OpenInHost => OpenInPresenter;

    /// <summary>Shows the window (or re-targets it when already visible) on <paramref name="files"/> at
    /// <paramref name="index"/>. A window that was hidden opens at its saved bounds (kept on a connected monitor), or,
    /// with none saved or when fitting to an image, at about 70% of the work area of the monitor holding
    /// <paramref name="anchorHwnd"/> (the primary monitor when it is zero), centered on it. A window already visible
    /// keeps its place and size.</summary>
    public void Show(IReadOnlyList<string> files, int index, IntPtr anchorHwnd)
    {
        _allFiles = files;
        _browse = Settings.Browse;
        ApplyBrowse(files.Count == 0 ? null : files[Math.Clamp(index, 0, files.Count - 1)]);

        Place(anchorHwnd);
        if (!IsVisible)
        {
            base.Show();
        }

        if (_nav is null)
        {
            _nav = new PeekNavOverlay(this, NavArea);
            _nav.PreviousClicked += () => MoveTo(_index - 1);
            _nav.NextClicked += () => MoveTo(_index + 1);
            LocationChanged += (_, _) => _nav?.Sync();
            SizeChanged += (_, _) => _nav?.Sync();
            StateChanged += (_, _) => _nav?.Sync();
        }

        _nav.Start();
        Activate();
        ShowCurrent();
    }

    /// <summary>Allows the window to close for real and closes it. The only way to end its life.</summary>
    public void Shutdown()
    {
        _allowClose = true;
        _nav?.Stop();
        _nav?.Close();
        _nav = null;
        RememberBounds();
        CancelLoad();
        PreviewWeb.Dispose();
        PreviewShell.Dispose();
        PeekShellPreview.ReleaseFactories();
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

    /// <summary>The one way Peek hides: remembers the bounds, cancels a load in flight and hides the window.</summary>
    public void HidePeek()
    {
        RememberBounds();
        CancelLoad();
        PeekShellPreview.StopHelper();
        StopVideo();
        _nav?.Stop();
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

    /// <summary>The open key: asks to open the file in its assigned program. The toggle shortcut is not handled here:
    /// the main app's global binding owns it (see PeekHotkey), also when the focus is in a native child. The settings
    /// refuse the navigation keys, so this never shadows the cases above. Auto-repeat is ignored. True when the key
    /// was the open key.</summary>
    private bool HandleConfiguredKey(Key key, bool isRepeat)
    {
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var modifiers = CurrentModifiers();
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

    private void UpdateChevrons() => _nav?.SetAvailability(_index > 0, _index < _files.Count - 1);

    /// <summary>The preview area on screen in physical pixels, for the chevron overlay; null while Peek is not shown.</summary>
    private PeekNavArea? NavArea()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || PresentationSource.FromVisual(Viewport) is not { } source)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(Viewport);
        var topLeft = Viewport.PointToScreen(new System.Windows.Point(0, 0));
        return new PeekNavArea(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(Viewport.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(Viewport.ActualHeight * dpi.DpiScaleY),
            source.CompositionTarget.TransformFromDevice);
    }

    /// <summary>Fills the title row for the current file at once and starts the decode; a decode still running for
    /// an earlier file is cancelled and, if it finishes anyway, its result is dropped by the version check.</summary>
    private void ShowCurrent()
    {
        CancelLoad();
        StopVideo();
        var path = CurrentPath;
        var name = path is null ? string.Empty : Path.GetFileName(path);

        Title = name;
        Bar.Title = name;
        CounterText.Text = _files.Count == 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture, "{0} / {1}", _index + 1, _files.Count);
        PreviewImage.Source = null;
        PreviewImage.StretchDirection = StretchDirection.DownOnly;
        FallbackText.Visibility = Visibility.Collapsed;
        InfoText.Text = string.Empty;
        UpdateChevrons();
        UpdateBrowseButton();
        UpdateOpenInButton();
        var kind = path is null ? PeekPreviewKind.None : PeekPreview.For(path);
        UpdateShowAsButton(path, kind);
        if (_geometryIsFit && !(Settings.FitToContent && kind == PeekPreviewKind.Image))
        {
            // The window still has the size of the last image and this file is not one to fit: the user's size is back.
            RestoreRemembered();
        }

        _targetPixelWidth = WorkAreaOf(new WindowInteropHelper(this).Handle, MonitorDefaultToNearest).Width;
        var isWeb = kind is PeekPreviewKind.Html or PeekPreviewKind.Markdown or PeekPreviewKind.Code or PeekPreviewKind.Video;
        PreviewWeb.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
        PreviewShell.Visibility = kind == PeekPreviewKind.Shell ? Visibility.Visible : Visibility.Collapsed;
        ReturnFocusToWindow();
        if (path is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        if (kind == PeekPreviewKind.Shell)
        {
            _ = LoadShellAsync(path, ++_version, cts.Token);
        }
        else
        {
            _ = isWeb ? LoadWebAsync(path, kind, ++_version, cts.Token) : LoadAsync(path, ++_version, cts.Token);
        }
    }

    /// <summary>A file a Windows preview handler shows: lets the window lay the host out and repaint first (the
    /// handler loads out of process and holds the UI thread while it does), then hosts the handler. A file that
    /// moved on meanwhile is dropped by the version check; a handler that fails gives the name only.</summary>
    private async Task LoadShellAsync(string path, int version, CancellationToken ct)
    {
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        if (!IsCurrent(version, ct))
        {
            return;
        }

        PreviewShell.BackgroundColor = CurrentBackground();
        if (!PreviewShell.Load(path))
        {
            ShowNameOnly(path);
            return;
        }

        ReturnFocusToWindow();
        InfoText.Text = PeekShellPreview.FormatInfo(path);
    }

    private bool IsCurrent(int version, CancellationToken ct) => version == _version && !ct.IsCancellationRequested;

    /// <summary>The web view is a native window: once the user clicks into a page it keeps the keyboard focus and
    /// the arrows, Home and End scroll the page. Moving to another file takes the focus back, so the keys of this
    /// window work again until the next click into a page.</summary>
    private void ReturnFocusToWindow()
    {
        if (PreviewWeb.IsKeyboardFocusWithin || PreviewShell.HasFocusWithin)
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
                page = await Task.Run(() => kind == PeekPreviewKind.Video
                    ? PeekWebContent.BuildVideo(path, theme, version)
                    : PeekWebContent.Build(path, kind, theme, ct), ct);
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
            _videoShown = kind == PeekPreviewKind.Video;
            _videoInfo = page.Info;
            core.NavigateToString(page.Html);
            InfoText.Text = page.Info;
        }
    }

    /// <summary>Stops the video of the last page and lets go of its file: the web view keeps playing (and keeps the
    /// file open) when it is hidden or when the next file is not a web page. No navigation, so the guard is untouched.</summary>
    private void StopVideo()
    {
        if (!_videoShown)
        {
            return;
        }

        _videoShown = false;
        if (PreviewWeb.CoreWebView2 is { } core)
        {
            _ = core.ExecuteScriptAsync("var v=document.getElementById('v');if(v){v.pause();v.removeAttribute('src');v.load();}");
        }
    }

    /// <summary>A message from the video page: <c>ok|width|height|seconds|version</c> adds the resolution and duration
    /// to the footer, <c>err|version</c> (the engine cannot play the file) gives the name only. Messages of a page
    /// that is not the current one are dropped.</summary>
    private void OnVideoMessage(string message)
    {
        var parts = message.Split('|');
        if (!_videoShown || parts.Length < 2 || CurrentPath is not { } path
            || !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version != _version)
        {
            return;
        }

        if (parts[0] == "err")
        {
            StopVideo();
            ShowNameOnly(path);
        }
        else if (parts[0] == "ok" && parts.Length == 5
                 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
                 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            InfoText.Text = PeekWebContent.DescribeVideo(_videoInfo, width, height, seconds);
        }
    }

    /// <summary>The file name over an empty area (what an image that cannot be decoded gets), or <paramref name="message"/>.</summary>
    private void ShowNameOnly(string path, string? message = null)
    {
        PreviewWeb.Visibility = Visibility.Collapsed;
        PreviewShell.Visibility = Visibility.Collapsed;
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
            // A video starts playing with sound when Peek opens: no click ever reaches the page first.
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" };
            var environment = await CoreWebView2Environment.CreateAsync(null, folder, options);
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
            core.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() is { } message)
                {
                    OnVideoMessage(message);
                }
            };
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

        // The footer text is final now, so the fixed part of the window (title row, footer, frame) is measured right.
        if (Settings.FitToContent && info.Bitmap is not null)
        {
            FitToImage(info.PixelWidth, info.PixelHeight);
        }
        else if (_geometryIsFit)
        {
            RestoreRemembered();
        }
    }

    private void CancelLoad()
    {
        _version++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        // The hosted handler goes with the load. Its window may hold the keyboard focus and is destroyed here:
        // take the focus back first, or the arrows and Esc would have nowhere to go.
        var shellHadFocus = PreviewShell.HasFocusWithin;
        PreviewShell.Unload();
        if (shellHadFocus)
        {
            Focus();
        }
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

    /// <summary>Puts a window that was hidden where it opens: at its saved bounds, kept on a connected monitor, or, with
    /// none saved or when the first file is an image to fit to, at 70% of the work area of the anchor's monitor,
    /// centered. A window already visible is left as it is. Works in physical pixels on the window handle, so the
    /// result is right on a monitor with another scale than the one the window was last on: the window is first moved
    /// onto the target monitor (which settles its DPI), then sized.</summary>
    private void Place(IntPtr anchorHwnd)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (IsVisible)
        {
            return;
        }

        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        var fitFirst = Settings.FitToContent && CurrentPath is { } first && PeekPreview.For(first) == PeekPreviewKind.Image;
        var fallback = DefaultBounds(WorkAreaOf(anchorHwnd, MonitorDefaultToPrimary));
        _saved = Settings.Bounds;
        _geometryIsFit = fitFirst;
        _remembered = fitFirst ? Settings.Bounds : null;

        var target = fallback;
        if (!fitFirst && Settings.Bounds is { } saved)
        {
            var monitors = MonitorEnumeration.Enumerate();
            var rects = monitors.Select(m => new PixelRect(m.X, m.Y, m.Width, m.Height)).ToList();
            target = PinPlacement.EnsureVisible(saved, rects, WorkAreaOf(0, MonitorDefaultToPrimary));
        }

        ApplyBounds(hwnd, target);
        if (PresentationSource.FromVisual(this) is { } source)
        {
            // Let WPF's Left/Top/Width/Height agree with the handle before the first Show, so it does not
            // move the window back to its own idea of the position.
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            var topLeft = fromDevice.Transform(new System.Windows.Point(target.X, target.Y));
            var size = fromDevice.Transform(new System.Windows.Point(target.Width, target.Height));
            Left = topLeft.X;
            Top = topLeft.Y;
            Width = size.X;
            Height = size.Y;
        }
    }

    /// <summary>The default bounds: 70% of <paramref name="work"/>, centered on it.</summary>
    private static PixelRect DefaultBounds(PixelRect work)
    {
        var width = (int)Math.Round(work.Width * WorkAreaFraction);
        var height = (int)Math.Round(work.Height * WorkAreaFraction);
        return new PixelRect(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
    }

    /// <summary>Moves the window onto the monitor of <paramref name="bounds"/> first (which settles its DPI), then sizes it.</summary>
    private static void ApplyBounds(nint hwnd, PixelRect bounds)
    {
        ZoneNative.SetWindowPos(hwnd, 0, bounds.X, bounds.Y, 0, 0, SwpNoSize | ZoneNative.SwpNoZOrder | ZoneNative.SwpNoActivate);
        ZoneNative.SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, ZoneNative.SwpNoZOrder | ZoneNative.SwpNoActivate);
    }

    /// <summary>The work area (taskbar excluded) of the monitor that holds <paramref name="hwnd"/>, or the one given
    /// by <paramref name="flags"/> when the handle is on none (the primary one for a zero handle).</summary>
    private static PixelRect WorkAreaOf(nint hwnd, uint flags)
    {
        var info = new ZoneNative.MonitorInfoEx { Size = MonitorInfoExSize };
        var monitor = MonitorFromWindow(hwnd, flags);
        var work = monitor != 0 && ZoneNative.GetMonitorInfo(monitor, ref info)
            ? info.Work
            : new ZoneNative.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        return new PixelRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    /// <summary>The window's bounds in physical pixels, or null when it is not in the normal state (minimized or
    /// maximized) or has no handle yet.</summary>
    private PixelRect? CurrentBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (WindowState != WindowState.Normal || hwnd == 0 || !ZoneNative.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        return new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Hands the bounds the user left the window with to <see cref="BoundsRemembered"/>, when they changed:
    /// the window's own bounds, or, while it has the size of an image, the ones it had before.</summary>
    private void RememberBounds()
    {
        if (!IsVisible)
        {
            return;
        }

        var bounds = _geometryIsFit ? _remembered : CurrentBounds();
        if (bounds is { } rect && rect != _saved)
        {
            _saved = rect;
            BoundsRemembered?.Invoke(rect);
        }
    }

    /// <summary>Sizes the window so the image shows at 100% (one image pixel is one physical pixel) in the viewport,
    /// plus whatever the window has around the viewport (title row, footer, frame). At most 90% of the work area of
    /// the monitor the window is on, at least the minimum size; centered where the window is now and kept inside the
    /// work area. The user's bounds are kept for the files that are not fitted.</summary>
    private void FitToImage(int imagePixelWidth, int imagePixelHeight)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (CurrentBounds() is not { } current)
        {
            return;
        }

        if (!_geometryIsFit)
        {
            _remembered = current;
            _geometryIsFit = true;
        }

        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var margin = PreviewImage.Margin;
        var work = WorkAreaOf(hwnd, MonitorDefaultToNearest);
        var needWidth = imagePixelWidth + (int)Math.Ceiling(
            current.Width - Viewport.ActualWidth * dpi.DpiScaleX + (margin.Left + margin.Right) * dpi.DpiScaleX);
        var needHeight = imagePixelHeight + (int)Math.Ceiling(
            current.Height - Viewport.ActualHeight * dpi.DpiScaleY + (margin.Top + margin.Bottom) * dpi.DpiScaleY);
        var minWidth = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        var minHeight = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
        var width = Math.Max(minWidth, Math.Min(needWidth, (int)(work.Width * FitCapFraction)));
        var height = Math.Max(minHeight, Math.Min(needHeight, (int)(work.Height * FitCapFraction)));

        // The image fills the viewport only when one side is not padded out to the minimum size; a small image in a
        // minimum size window stays at its own size instead of being blown up.
        PreviewImage.StretchDirection = needWidth >= minWidth || needHeight >= minHeight
            ? StretchDirection.Both
            : StretchDirection.DownOnly;
        ResizeAroundCenter(hwnd, current, width, height, work);
    }

    /// <summary>Gives the window back the size it had before it was fitted to an image (the default size when it
    /// never had one), centered where it is now.</summary>
    private void RestoreRemembered()
    {
        _geometryIsFit = false;
        PreviewImage.StretchDirection = StretchDirection.DownOnly;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (CurrentBounds() is not { } current)
        {
            return;
        }

        var work = WorkAreaOf(hwnd, MonitorDefaultToNearest);
        var size = _remembered ?? DefaultBounds(work);
        ResizeAroundCenter(hwnd, current, size.Width, size.Height, work);
    }

    /// <summary>Resizes the window around its center, then moves it back inside <paramref name="work"/> if that pushed
    /// it out (its top left corner wins when it is larger than the work area).</summary>
    private static void ResizeAroundCenter(nint hwnd, PixelRect current, int width, int height, PixelRect work)
    {
        var x = current.X + current.Width / 2 - width / 2;
        var y = current.Y + current.Height / 2 - height / 2;
        x = Math.Max(work.X, Math.Min(x, work.Right - width));
        y = Math.Max(work.Y, Math.Min(y, work.Bottom - height));
        ZoneNative.SetWindowPos(hwnd, 0, x, y, width, height, ZoneNative.SwpNoZOrder | ZoneNative.SwpNoActivate);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}
