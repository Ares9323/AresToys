using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QRCoder;
using AresToys.App.Services;
using AresToys.App.Services.Qr;
using AresToys.Core.Domain;
using AresToys.Pipeline.Profiles;
using AresToys.Storage.Settings;

namespace AresToys.App.Views;

/// <summary>Live QR generator. Type / paste anything in the multiline editor, the preview
/// updates with a 150 ms debounce. Options for ECC level + module size feed both the preview
/// and the export buttons. Exporters: copy PNG to clipboard, save PNG, save SVG. Window
/// position/size + last-used options round-trip through <see cref="ISettingsStore"/>.</summary>
public partial class QrGeneratorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly QrCodeService _qr;
    private readonly ISettingsStore? _settings;
    private readonly ManualUploadService? _ingestion;
    private readonly DispatcherTimer _renderDebounce;
    private bool _placementLoaded;
    /// <summary>True while LoadPlacementAsync is restoring the saved options into the controls;
    /// blocks the change handlers from looping back into the settings store.</summary>
    private bool _suppressOptionsPersist;

    private const string KeyX = "qr.window.x";
    private const string KeyY = "qr.window.y";
    private const string KeyWidth = "qr.window.width";
    private const string KeyHeight = "qr.window.height";
    private const string KeyMaximized = "qr.window.maximized";
    private const string KeyEcc = "qr.options.ecc";
    private const string KeyDensity = "qr.options.density";

    public QrGeneratorWindow(QrCodeService qrService, string? initialText = null, ISettingsStore? settings = null, ManualUploadService? ingestion = null)
    {
        ArgumentNullException.ThrowIfNull(qrService);
        _qr = qrService;
        _settings = settings;
        _ingestion = ingestion;

        // Timer goes BEFORE InitializeComponent because the Slider's ValueChanged fires
        // mid-XAML-parse (when Minimum/Maximum/Value attributes are set), and our handler
        // touches _renderDebounce — without this ordering we'd null-ref before the window
        // has even rendered.
        _renderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _renderDebounce.Tick += (_, _) => { _renderDebounce.Stop(); RefreshPreview(); };

        InitializeComponent();
        AresToys.App.Services.DarkTitleBar.SuppressResizeFlicker(this);
        AresToys.App.Services.DarkTitleBar.EnlargeResizeHitZones(this);

        InputText.TextChanged += (_, _) => Schedule();
        if (!string.IsNullOrEmpty(initialText))
        {
            InputText.Text = initialText;
        }

        Loaded += OnLoaded;
        SizeChanged += OnPlacementChanged;
        LocationChanged += OnPlacementChanged;
        StateChanged += OnPlacementChanged;

        // Hide the history button when the host didn't wire up the ingestion service —
        // pop-up callers (e.g. a future test harness) shouldn't see a non-functional button.
        if (_ingestion is null) SaveToHistoryButton.Visibility = Visibility.Collapsed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings is not null) await LoadPlacementAndOptionsAsync().ConfigureAwait(true);
        else _placementLoaded = true;
        InputText.Focus();
    }

    /// <summary>Restore window x/y/w/h + last-used ECC level + last-used module size from the
    /// settings store. Wraps option restore in <see cref="_suppressOptionsPersist"/> so the
    /// resulting SelectionChanged / ValueChanged events don't loop back through PersistOptions.</summary>
    private async Task LoadPlacementAndOptionsAsync()
    {
        if (_settings is null) return;
        try
        {
            var ct = System.Threading.CancellationToken.None;
            var x = await _settings.GetAsync(KeyX, ct).ConfigureAwait(true);
            var y = await _settings.GetAsync(KeyY, ct).ConfigureAwait(true);
            var w = await _settings.GetAsync(KeyWidth, ct).ConfigureAwait(true);
            var h = await _settings.GetAsync(KeyHeight, ct).ConfigureAwait(true);
            var max = await _settings.GetAsync(KeyMaximized, ct).ConfigureAwait(true);
            var ecc = await _settings.GetAsync(KeyEcc, ct).ConfigureAwait(true);
            var density = await _settings.GetAsync(KeyDensity, ct).ConfigureAwait(true);

            if (double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                && width >= MinWidth && height >= MinHeight)
            {
                Width = width;
                Height = height;
            }
            if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var top))
            {
                // Reject coordinates that would land off any monitor (unplugged secondary
                // display etc.) — same heuristic as MainWindow / ImageEffectsWindow.
                var virtualLeft = SystemParameters.VirtualScreenLeft;
                var virtualTop = SystemParameters.VirtualScreenTop;
                var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
                var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
                if (left + 50 < virtualRight && top + 50 < virtualBottom
                    && left + Width - 50 > virtualLeft && top + Height - 50 > virtualTop)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = left;
                    Top = top;
                }
            }
            if (string.Equals(max, "1", StringComparison.Ordinal))
                WindowState = WindowState.Maximized;

            // Options — wrap in suppress so the resulting Combo/Slider events don't try to
            // save what we just loaded.
            _suppressOptionsPersist = true;
            try
            {
                if (!string.IsNullOrEmpty(ecc))
                {
                    foreach (ComboBoxItem item in EccCombo.Items)
                    {
                        if (item.Tag is string tag && tag == ecc) { item.IsSelected = true; break; }
                    }
                }
                if (!string.IsNullOrEmpty(density)
                    && double.TryParse(density, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    && d >= DensitySlider.Minimum && d <= DensitySlider.Maximum)
                {
                    DensitySlider.Value = d;
                }
            }
            finally
            {
                _suppressOptionsPersist = false;
            }
        }
        catch { /* placement / options are cosmetic — never break the editor over a missing row */ }
        finally
        {
            _placementLoaded = true;
        }
    }

    private async void OnPlacementChanged(object? sender, EventArgs e)
    {
        if (_settings is null || !_placementLoaded) return;
        // RestoreBounds gives us the pre-maximise rect, which is what we want to restore on
        // next launch (saving the maximised geometry as the "preferred" size would lock the
        // user into full-screen on every relaunch).
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;
        try
        {
            var ct = System.Threading.CancellationToken.None;
            await _settings.SetAsync(KeyX, bounds.X.ToString(CultureInfo.InvariantCulture), false, ct).ConfigureAwait(true);
            await _settings.SetAsync(KeyY, bounds.Y.ToString(CultureInfo.InvariantCulture), false, ct).ConfigureAwait(true);
            await _settings.SetAsync(KeyWidth, bounds.Width.ToString(CultureInfo.InvariantCulture), false, ct).ConfigureAwait(true);
            await _settings.SetAsync(KeyHeight, bounds.Height.ToString(CultureInfo.InvariantCulture), false, ct).ConfigureAwait(true);
            await _settings.SetAsync(KeyMaximized, WindowState == WindowState.Maximized ? "1" : "0", false, ct).ConfigureAwait(true);
        }
        catch { /* best-effort */ }
    }

    private async void PersistOptions()
    {
        if (_settings is null || !_placementLoaded || _suppressOptionsPersist) return;
        try
        {
            var ct = System.Threading.CancellationToken.None;
            var ecc = (EccCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag) ? tag : "Q";
            await _settings.SetAsync(KeyEcc, ecc, false, ct).ConfigureAwait(true);
            await _settings.SetAsync(KeyDensity, ((int)DensitySlider.Value).ToString(CultureInfo.InvariantCulture), false, ct).ConfigureAwait(true);
        }
        catch { /* best-effort */ }
    }

    private static string Loc(string key, params object[] args)
    {
        var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture
                      ?? System.Globalization.CultureInfo.CurrentUICulture;
        // Fully qualified — the bare "Resources" identifier collides with FrameworkElement.Resources
        // (this Window inherits from FluentWindow → FrameworkElement) and the compiler picks the
        // instance member over the namespace.
        var template = AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
        return args.Length == 0 ? template : string.Format(culture, template, args);
    }

    private void Schedule()
    {
        // Guard against InitializeComponent-time invocations: the Slider raises ValueChanged
        // before the rest of the visual tree is instantiated, so InputText / StatusText may
        // still be null when this fires the first time.
        if (InputText is null || _renderDebounce is null) return;
        if (StatusText is not null) StatusText.Text = Loc("QrGenerator_CharactersFormat", InputText.Text.Length);
        _renderDebounce.Stop();
        _renderDebounce.Start();
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        // Same XAML-init-time guard as Schedule.
        if (DensityValueText is null || DensitySlider is null) return;
        DensityValueText.Text = Loc("QrGenerator_PixelsFormat", (int)DensitySlider.Value);
        Schedule();
        PersistOptions();
    }

    private void RefreshPreview()
    {
        var text = InputText.Text;
        if (string.IsNullOrEmpty(text))
        {
            QrPreview.Source = null;
            return;
        }
        var bmp = _qr.TryRenderBitmap(text, (int)DensitySlider.Value, GetEccLevel());
        QrPreview.Source = bmp;
        if (bmp is null) StatusText.Text = Loc("QrGenerator_StatusFail");
    }

    /// <summary>Read the ECC dropdown's selected Tag back into the QRCoder enum. Tag is a
    /// single-letter string (L/M/Q/H) so the XAML stays readable; falling back to Q matches
    /// QRCoder's typical default.</summary>
    private QRCodeGenerator.ECCLevel GetEccLevel()
    {
        if (EccCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag switch
            {
                "L" => QRCodeGenerator.ECCLevel.L,
                "M" => QRCodeGenerator.ECCLevel.M,
                "H" => QRCodeGenerator.ECCLevel.H,
                _ => QRCodeGenerator.ECCLevel.Q,
            };
        }
        return QRCodeGenerator.ECCLevel.Q;
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InputText.Text)) return;
        var bmp = _qr.TryRenderBitmap(InputText.Text, (int)DensitySlider.Value, GetEccLevel());
        if (bmp is null) { StatusText.Text = Loc("QrGenerator_StatusRenderFailCopy"); return; }
        try
        {
            System.Windows.Clipboard.SetImage(bmp);
            StatusText.Text = Loc("QrGenerator_StatusCopied");
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc("QrGenerator_StatusCopyFail", ex.Message);
        }
    }

    private void OnSavePngClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InputText.Text)) return;
        var bytes = _qr.TryRenderPng(InputText.Text, (int)DensitySlider.Value, GetEccLevel());
        if (bytes is null) { StatusText.Text = Loc("QrGenerator_StatusRenderFailSave"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc("QrGenerator_SavePngTitle"),
            Filter = Loc("QrGenerator_FilterPng"),
            FileName = "qr.png",
            DefaultExt = ".png",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, bytes);
            StatusText.Text = Loc("QrGenerator_StatusSaved", Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc("QrGenerator_StatusSaveFail", ex.Message);
        }
    }

    private void OnSaveSvgClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InputText.Text)) return;
        var svg = _qr.TryRenderSvg(InputText.Text, (int)DensitySlider.Value, GetEccLevel());
        if (string.IsNullOrEmpty(svg)) { StatusText.Text = Loc("QrGenerator_StatusRenderFailSave"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc("QrGenerator_SaveSvgTitle"),
            Filter = Loc("QrGenerator_FilterSvg"),
            FileName = "qr.svg",
            DefaultExt = ".svg",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, svg);
            StatusText.Text = Loc("QrGenerator_StatusSaved", Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc("QrGenerator_StatusSaveFail", ex.Message);
        }
    }

    private async void OnSaveToHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (_ingestion is null) return;
        if (string.IsNullOrEmpty(InputText.Text)) return;
        var bytes = _qr.TryRenderPng(InputText.Text, (int)DensitySlider.Value, GetEccLevel());
        if (bytes is null) { StatusText.Text = Loc("QrGenerator_StatusRenderFailSave"); return; }
        try
        {
            // First 80 chars of the encoded payload form the search-text — same shape Win+V's
            // history list uses for its row preview, so the QR shows up labelled with its content.
            var searchText = InputText.Text.Length <= 80 ? InputText.Text : InputText.Text[..80] + "…";
            await _ingestion.IngestBytesAsync(bytes, "png", ItemKind.Image, $"QR · {searchText}",
                DefaultPipelineProfiles.SaveQrToHistoryId, System.Threading.CancellationToken.None);
            StatusText.Text = Loc("QrGenerator_StatusSavedHistory");
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc("QrGenerator_StatusHistoryFail", ex.Message);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
