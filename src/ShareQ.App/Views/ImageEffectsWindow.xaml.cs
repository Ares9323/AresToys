using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ShareQ.App.ViewModels.ImageEffects;
using ShareQ.Storage.Settings;

namespace ShareQ.App.Views;

// Inherit FluentWindow (acrylic / Mica chrome + matched titlebar) instead of plain Window so
// the editor reads as part of the same app surface as MainWindow / Launcher.
public partial class ImageEffectsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ImageEffectsViewModel _viewModel;
    private readonly ISettingsStore? _settings;
    private bool _placementLoaded;

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
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += OnPlacementChanged;
        LocationChanged += OnPlacementChanged;
        StateChanged += OnPlacementChanged;
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
        foreach (var group in _viewModel.AvailableEffects.GroupBy(d => d.Category))
        {
            var categoryItem = new MenuItem { Header = group.Key.ToString() };
            foreach (var effect in group)
            {
                var leaf = new MenuItem { Header = effect.Name, Tag = effect.Id };
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
            Filter = "ShareX / ShareQ presets|*.sxie;*.json|ShareX preset|*.sxie|JSON|*.json|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        await _viewModel.ImportSxieFileAsync(dlg.FileName).ConfigureAwait(true);
    }

    private void OnExportPresetClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPreset is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export preset",
            Filter = "JSON|*.json",
            FileName = $"{SafeFileName(_viewModel.SelectedPreset.Name)}.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
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

    private static string SafeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "preset";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
