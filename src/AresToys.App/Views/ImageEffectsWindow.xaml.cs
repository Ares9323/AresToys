using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AresToys.App.ViewModels.ImageEffects;
using AresToys.Storage.Settings;

namespace AresToys.App.Views;

// Inherit FluentWindow (acrylic / Mica chrome + matched titlebar) instead of plain Window so
// the editor reads as part of the same app surface as MainWindow / Launcher.
public partial class ImageEffectsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ImageEffectsViewModel _viewModel;
    private readonly ISettingsStore? _settings;
    private bool _placementLoaded;

    /// <summary>PNG bytes of the user's "Apply to editor" pick — populated only when the
    /// window was launched in editor mode AND the user clicked Apply. Caller awaits the
    /// Closed event, reads this back, and swaps the editor's source. null = user dismissed
    /// the window without applying (close button / Esc / "X").</summary>
    public byte[]? ResultBytes { get; private set; }

    /// <summary>Flip the window into editor-callback mode:
    /// <list type="bullet">
    /// <item>"Apply to editor" button becomes visible (closes window with rendered PNG in
    /// <see cref="ResultBytes"/>).</item>
    /// <item>VM stops auto-persisting preset changes (<c>SuppressAutoPersist=true</c>) — slider
    /// tweaks during the session don't silently overwrite the saved preset.</item>
    /// <item>"Override preset" button becomes visible — the user's explicit save gesture if
    /// they DO want their tweaks persisted back.</item>
    /// <item>"Load image" + "Reset sample" buttons collapse — the source IS the editor's
    /// screenshot, those buttons would replace it in surprising ways.</item>
    /// </list>
    /// Called by <c>EditorLauncher</c> right after constructing the window with the editor's
    /// source image preloaded via <see cref="ImageEffectsViewModel.LoadSourceFromBytes"/>.</summary>
    public void EnableEditorMode()
    {
        ApplyToEditorBtn.Visibility = Visibility.Visible;
        OverridePresetBtn.Visibility = Visibility.Visible;
        LoadImageBtn.Visibility = Visibility.Collapsed;
        ResetSampleBtn.Visibility = Visibility.Collapsed;
        _viewModel.SuppressAutoPersist = true;
    }

    private const string KeyX = "imageeffects.x";
    private const string KeyY = "imageeffects.y";
    private const string KeyWidth = "imageeffects.width";
    private const string KeyHeight = "imageeffects.height";
    private const string KeyMaximized = "imageeffects.maximized";

    public ImageEffectsWindow(ImageEffectsViewModel? viewModel = null, ISettingsStore? settings = null)
    {
        _viewModel = viewModel ?? new ImageEffectsViewModel();
        _settings = settings;
        DataContext = _viewModel;
        InitializeComponent();
        AresToys.App.Services.DarkTitleBar.SuppressResizeFlicker(this);
        AresToys.App.Services.DarkTitleBar.EnlargeResizeHitZones(this);
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += OnPlacementChanged;
        LocationChanged += OnPlacementChanged;
        StateChanged += OnPlacementChanged;
        // Auto-fit the preview image to the pane whenever the VM swaps the BitmapImage (initial
        // load, sample reset, after each effect render). Same flow the clipboard window uses.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageEffectsViewModel.PreviewImage))
                Dispatcher.BeginInvoke(new Action(FitPreviewToPane), System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
    }

    // RMB-pan state for the preview ScrollViewer. Mirrors the clipboard preview pattern:
    // hold RMB, drag to translate scroll offsets; cursor flips to SizeAll while panning.
    private bool _isPreviewPanning;
    private System.Windows.Point _previewPanStart;
    private double _previewPanScrollH, _previewPanScrollV;
    private Cursor? _previewPanSavedCursor;

    /// <summary>Wheel: Ctrl+wheel zooms (0.1×..8×, anchor stays under cursor implicitly via
    /// LayoutTransform), Shift+wheel scrolls horizontally (Windows convention), bare wheel
    /// falls through to the ScrollViewer for vertical scroll.</summary>
    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (ctrl)
        {
            var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            var newScale = Math.Clamp(PreviewScale.ScaleX * factor, 0.1, 8.0);
            if (Math.Abs(newScale - PreviewScale.ScaleX) < 1e-4) return;
            PreviewScale.ScaleX = newScale;
            PreviewScale.ScaleY = newScale;
            e.Handled = true;
            return;
        }
        if (shift)
        {
            var step = SystemParameters.WheelScrollLines * 16;
            var dir = e.Delta > 0 ? -1 : 1;
            PreviewScroller.ScrollToHorizontalOffset(PreviewScroller.HorizontalOffset + dir * step);
            e.Handled = true;
        }
    }

    /// <summary>Pan the preview with the middle mouse button (ShareX-parity, was RMB pre-0.1.6).
    /// Wired through generic PreviewMouseDown + ChangedButton check since WPF lacks a
    /// dedicated MiddleButton variant.</summary>
    private void OnPreviewPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (PreviewScroller is null) return;
        _isPreviewPanning = true;
        _previewPanStart = e.GetPosition(PreviewScroller);
        _previewPanScrollH = PreviewScroller.HorizontalOffset;
        _previewPanScrollV = PreviewScroller.VerticalOffset;
        _previewPanSavedCursor = PreviewScroller.Cursor;
        PreviewScroller.Cursor = Cursors.SizeAll;
        PreviewScroller.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPreviewPanning) return;
        if (e.MiddleButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(PreviewScroller);
        PreviewScroller.ScrollToHorizontalOffset(_previewPanScrollH - (current.X - _previewPanStart.X));
        PreviewScroller.ScrollToVerticalOffset(_previewPanScrollV - (current.Y - _previewPanStart.Y));
        e.Handled = true;
    }

    private void OnPreviewPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!_isPreviewPanning) return;
        _isPreviewPanning = false;
        PreviewScroller.ReleaseMouseCapture();
        PreviewScroller.Cursor = _previewPanSavedCursor;
        _previewPanSavedCursor = null;
        e.Handled = true;
    }

    /// <summary>Compute the LayoutTransform scale that fits the current PreviewImage into
    /// the ScrollViewer viewport (no upscale beyond 1× — small images stay 1:1). Reset scroll
    /// offsets to 0 at the same time so a new render starts from the top-left corner.</summary>
    private void FitPreviewToPane()
    {
        if (_viewModel.PreviewImage is not { } img) return;
        if (PreviewScroller is null || PreviewScale is null) return;
        var w = img.PixelWidth; var h = img.PixelHeight;
        if (w <= 0 || h <= 0) return;
        var vw = PreviewScroller.ViewportWidth - 20;
        var vh = PreviewScroller.ViewportHeight - 20;
        if (vw <= 0 || vh <= 0) return;
        var fit = Math.Min(vw / w, vh / h);
        if (fit > 1.0) fit = 1.0;
        if (fit < 0.1) fit = 0.1;
        PreviewScale.ScaleX = fit;
        PreviewScale.ScaleY = fit;
        PreviewScroller.ScrollToHorizontalOffset(0);
        PreviewScroller.ScrollToVerticalOffset(0);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildAddEffectMenu();
        _viewModel.RequestRender();
        if (_settings is not null) await LoadPlacementAsync().ConfigureAwait(true);
        else _placementLoaded = true; // no settings store → no persistence, just unblock saves
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }

    /// <summary>Public entry point used by the file-association handler to load a .sxie into
    /// an already-open editor instead of spinning up a fresh one. Returns the imported preset's
    /// loading task so the caller can await it before bringing the window forward.</summary>
    public Task ImportSxieAsync(string path) => _viewModel.ImportSxieFileAsync(path);

    private async Task LoadPlacementAsync()
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
                // display etc.) — same heuristic as MainWindow.
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
        }
        catch { /* placement is cosmetic — never break the editor over a missing row */ }
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

    /// <summary>Build the cascade-by-category context menu attached to the "+ Add" button.
    /// Done in code rather than XAML because the catalogue is reflection-driven and changes
    /// every time we port a new effect — keeping the wiring in code-behind avoids a parallel
    /// resource dictionary that gets out of sync.</summary>
    private void BuildAddEffectMenu()
    {
        AddEffectMenu.Items.Clear();
        // Localise the category headers + leaf names. AvailableEffects is sorted by Category +
        // English Name in the VM; localising here means the visible order may not match the
        // alphabet of the active culture, but keeping the catalog stable across language flips
        // is more valuable than re-sorting every time CultureChanged fires.
        foreach (var group in _viewModel.AvailableEffects.GroupBy(d => d.Category))
        {
            var categoryItem = new MenuItem { Header = Services.ImageEffectLocalizer.LocalizeCategory(group.Key) };
            foreach (var effect in group)
            {
                var leaf = new MenuItem
                {
                    Header = Services.ImageEffectLocalizer.LocalizeEffect(effect.Id, effect.Name),
                    Tag = effect.Id,
                };
                leaf.Click += (_, _) => _viewModel.AddEffect(effect.Id);
                categoryItem.Items.Add(leaf);
            }
            AddEffectMenu.Items.Add(categoryItem);
        }
    }

    private void OnAddEffectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ContextMenu is not null)
        {
            fe.ContextMenu.PlacementTarget = fe;
            fe.ContextMenu.IsOpen = true;
        }
    }

    private async void OnImportSxieClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import effects preset",
            Filter = "ShareX / AresToys presets|*.sxie;*.json|ShareX preset|*.sxie|JSON|*.json|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        await _viewModel.ImportSxieFileAsync(dlg.FileName).ConfigureAwait(true);
    }

    private void OnExportPresetClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPreset is null) return;
        // Default to .sxie (ZIP package, same shape ShareX produces from its Package button) so
        // the file can be shared as a single artefact with bundled DrawImage assets. Plain JSON
        // is still offered as a fallback for users who want the raw config.
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export preset",
            Filter = "ShareX preset package|*.sxie|JSON|*.json",
            FileName = $"{SafeFileName(_viewModel.SelectedPreset.Name)}.sxie",
            DefaultExt = ".sxie",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        if (dlg.FileName.EndsWith(".sxie", StringComparison.OrdinalIgnoreCase))
            _viewModel.ExportPresetToSxie(dlg.FileName);
        else
            _viewModel.ExportPresetTo(dlg.FileName);
    }

    private void OnLoadImageClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load preview image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        _viewModel.LoadPreviewImage(dlg.FileName);
    }

    private void OnResetSampleClicked(object sender, RoutedEventArgs e)
    {
        // Sample image is rebuilt by the helper — push it back into the VM's pipeline by
        // re-rendering. We don't recreate the VM (would lose store wiring + drop the persist
        // debounce); instead we expose a helper that swaps the sample bitmap in place.
        _viewModel.RebuildSampleImage();
    }

    /// <summary>Editor-mode "Apply" handler — renders the currently-selected preset against
    /// the editor's source bitmap (preloaded via LoadSourceFromBytes), stows the encoded PNG
    /// in <see cref="ResultBytes"/>, and closes the window. The caller (EditorLauncher) hooks
    /// the Closed event and pushes the bytes back into the editor's <c>SourcePngBytes</c> via
    /// an undoable command.</summary>
    private void OnApplyToEditorClicked(object sender, RoutedEventArgs e)
    {
        var bytes = _viewModel.RenderCurrentToPng();
        if (bytes is null) return;
        ResultBytes = bytes;
        Close();
    }

    /// <summary>Editor-mode "Override preset" handler — explicitly saves the current preset
    /// state to the store, bypassing <see cref="ImageEffectsViewModel.SuppressAutoPersist"/>.
    /// The user clicks this when they DO want their slider tweaks persisted back instead of
    /// being treated as a one-shot for the current screenshot.</summary>
    private async void OnOverridePresetClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.PersistSelectedExplicitlyAsync();
    }

    /// <summary>Click in an unfocused TextBox: take focus immediately and swallow the click
    /// so the default caret-placement logic doesn't run — paired with
    /// <see cref="OnTextBoxGotFocus"/>, this gives "click → all contents selected" UX.</summary>
    private void OnTextBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.IsKeyboardFocusWithin) return;
        e.Handled = true;
        tb.Focus();
    }

    private void OnTextBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    /// <summary>Commit the manual-entry TextBox to its bound source on Enter, instead of
    /// waiting for LostFocus. Without this an Enter press just rings the bell — the user
    /// types a value, hits Enter expecting "apply", and nothing happens.</summary>
    private void OnParameterTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is TextBox tb)
        {
            var binding = BindingOperations.GetBindingExpression(tb, TextBox.TextProperty);
            binding?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>Auto-focus the rename TextBox the moment it becomes visible (i.e. when the
    /// user clicks the pencil button or presses F2). Selecting all the text matches what
    /// File Explorer / VS Code do for inline-rename, so a fresh keystroke replaces the name
    /// instead of inserting at position 0.</summary>
    private void OnRenameTextBoxVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsVisible) return;
        // Defer to the next dispatcher pass — the parent ListBoxItem may still be focusing
        // itself, which would steal focus right back from us if we grabbed it synchronously.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            tb.Focus();
            tb.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnRenameTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Enter)
        {
            // Push the typed value back to Name, then flip out of edit mode.
            BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateSource();
            if (tb.DataContext is PresetItemViewModel vm) vm.CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Roll back to the snapshot taken in BeginEdit. The TextBox refresh through the
            // binding picks up the reverted Name automatically.
            if (tb.DataContext is PresetItemViewModel vm) vm.CancelEdit();
            BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateTarget();
            e.Handled = true;
        }
    }

    private void OnRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // Click-away counts as "commit", same UX as File Explorer. The binding's
        // UpdateSourceTrigger=LostFocus already pushed the value, so we just close the editor.
        if (sender is TextBox tb && tb.DataContext is PresetItemViewModel vm) vm.CommitEdit();
    }

    /// <summary>Click on a Color swatch — opens the editor's HSV ColorPickerWindow seeded
    /// with the parameter's current SKColor; on OK pushes the picked colour back to the VM.
    /// The swatch is the only affordance — the surrounding "Pick…" button was removed in
    /// favour of clicking directly on the swatch itself.</summary>
    private void OnColorSwatchMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not EffectParameterViewModel vm) return;
        e.Handled = true;
        var c = vm.ColorValue;
        var initial = new AresToys.Editor.Model.ShapeColor(c.Alpha, c.Red, c.Green, c.Blue);
        var dialog = new AresToys.Editor.Views.ColorPickerWindow(initial) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var picked = dialog.PickedColor;
        vm.ColorValue = new SkiaSharp.SKColor(picked.R, picked.G, picked.B, picked.A);
    }

    /// <summary>Click on a Gradient swatch — opens GradientEditorWindow seeded with the
    /// parameter's current GradientInfo. On OK we copy the dialog's stop list back into
    /// the live GradientInfo (kept by reference) and notify the VM to refresh the swatch and
    /// kick a preview re-render. The Tag is bound to the gradient VM directly so it works
    /// both for standalone gradients and for the paired Color row's swap-in swatch.</summary>
    private void OnGradientSwatchMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not EffectParameterViewModel vm) return;
        e.Handled = true;
        if (vm.GradientValue is not { } current) return;
        var dialog = new GradientEditorWindow(current, _settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited) return;
        current.Type = edited.Type;
        current.Colors.Clear();
        foreach (var s in edited.Colors) current.Colors.Add(new AresToys.ImageEffects.Drawing.GradientStop(s.Color, s.Location));
        vm.NotifyGradientChanged();
    }

    /// <summary>Browse for an image file and store the chosen path on the parameter VM.
    /// Used by FilePath-kind rows (currently DrawImage's ImageLocation). The dialog filters
    /// common raster formats but keeps an "All files" entry so users can still pick something
    /// non-standard if they really need to.</summary>
    private void OnBrowseImageFileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not EffectParameterViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick an image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|All files|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(vm.StringValue) && System.IO.File.Exists(vm.StringValue))
            dlg.FileName = vm.StringValue;
        if (dlg.ShowDialog(this) != true) return;
        vm.StringValue = dlg.FileName;
    }

    /// <summary>Open the ShareX image-effects gallery in the user's default browser.
    /// Files downloaded from there are .sxie packages and can be loaded back through the
    /// existing Import… button without further translation.</summary>
    private void OnOpenEffectsGalleryClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://getsharex.com/image-effects",
                UseShellExecute = true,
            });
        }
        catch { /* defensive — bad shell hook, missing browser, etc. */ }
    }

    private static string SafeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "preset";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
