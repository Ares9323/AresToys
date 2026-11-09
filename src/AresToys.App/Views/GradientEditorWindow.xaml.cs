using System.Globalization;
using System.Windows;
using AresToys.App.ViewModels.ImageEffects;
using AresToys.ImageEffects.Drawing;
using AresToys.Storage.Settings;
using SkiaSharp;

namespace AresToys.App.Views;

/// <summary>Modal gradient editor — operates on a copy of the input <see cref="GradientInfo"/>
/// so cancelling discards the user's edits. On OK, the caller copies the final stops back.
/// Inherits FluentWindow chrome to match the rest of the app surface.</summary>
public partial class GradientEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly GradientEditorViewModel _viewModel;
    private readonly ISettingsStore? _settings;
    private bool _placementLoaded;

    private const string KeyX = "imageeffects.gradient.x";
    private const string KeyY = "imageeffects.gradient.y";
    private const string KeyWidth = "imageeffects.gradient.width";
    private const string KeyHeight = "imageeffects.gradient.height";
    private const string KeyMaximized = "imageeffects.gradient.maximized";

    /// <summary>Final state of the gradient when the user clicks OK; null if Cancel was used.</summary>
    public GradientInfo? Result { get; private set; }

    public GradientEditorWindow(GradientInfo source, ISettingsStore? settings = null)
    {
        _viewModel = new GradientEditorViewModel(source);
        _settings = settings;
        DataContext = _viewModel;
        InitializeComponent();
        AresToys.App.Services.DarkTitleBar.SuppressResizeFlicker(this);
        AresToys.App.Services.DarkTitleBar.EnlargeResizeHitZones(this);
        Loaded += OnLoaded;
        SizeChanged += OnPlacementChanged;
        LocationChanged += OnPlacementChanged;
        StateChanged += OnPlacementChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings is not null) await LoadPlacementAsync().ConfigureAwait(true);
        else _placementLoaded = true;
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
        catch { /* placement is cosmetic */ }
        finally
        {
            _placementLoaded = true;
        }
    }

    private async void OnPlacementChanged(object? sender, EventArgs e)
    {
        if (_settings is null || !_placementLoaded) return;
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

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        Result = _viewModel.WorkingCopy;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnPickStopColorClicked(object sender, RoutedEventArgs e)
    {
        PickColorFor(_viewModel.SelectedStop);
    }

    /// <summary>Click on a stop's colour swatch: open the picker for that specific stop
    /// without going through the "Pick color for selected stop" button. Single-click matches
    /// the picker affordance used in the property panel's Color/Gradient swatches.</summary>
    private void OnStopSwatchMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not GradientStopViewModel stop) return;
        e.Handled = true;
        PickColorFor(stop);
    }

    private void PickColorFor(GradientStopViewModel? target)
    {
        if (target is null) return;
        var c = target.ColorValue;
        var initial = new AresToys.Editor.Model.ShapeColor(c.Alpha, c.Red, c.Green, c.Blue);
        var dialog = new AresToys.Editor.Views.ColorPickerWindow(initial) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var picked = dialog.PickedColor;
        target.ColorValue = new SKColor(picked.R, picked.G, picked.B, picked.A);
    }

    private void OnPresetDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox lb) return;
        if (lb.SelectedItem is GradientPresetItemViewModel item) _viewModel.LoadPreset(item);
    }
}
