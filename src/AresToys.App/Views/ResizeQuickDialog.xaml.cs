using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using AresToys.App.Resources;
using AresToys.App.Services.ImageResize;
using Wpf.Ui.Controls;

namespace AresToys.App.Views;

/// <summary>One entry of the preset dropdown: a display name and the resize step config the
/// preset fills the fields from. A null <see cref="Config"/> is the "Custom" entry.</summary>
public sealed record ResizePresetItem(string Name, JsonObject? Config);

/// <summary>
/// Quick configurator opened by the Explorer "Custom" resize verb (the AresToys take on the
/// PowerToys Image Resizer dialog). It only collects parameters: on Resize it exposes them as
/// <see cref="ResultConfig"/>, a JsonObject with the keys the "Resize image files" task reads,
/// and the caller runs the task's batch code. Cancel leaves <see cref="ResultConfig"/> null.
/// </summary>
public partial class ResizeQuickDialog : FluentWindow
{
    private sealed record Option(string Raw, string Label);

    private readonly ResizePresetItem _customItem;
    private bool _loading = true;

    private readonly Func<string, JsonObject, Task<string>>? _savePreset;
    private readonly List<ResizePresetItem> _items = new();

    /// <param name="savePreset">Saves the current values as a preset: (name, config) to the final
    /// preset name. Null hides the "Save as preset" button.</param>
    public ResizeQuickDialog(IReadOnlyList<string> files, IReadOnlyList<ResizePresetItem> presets, JsonObject lastValues,
        Func<string, JsonObject, Task<string>>? savePreset = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(lastValues);

        InitializeComponent();

        FileCountText.Text = Loc("ResizeQuick_FileCount")
            .Replace("{0}", files.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);

        _customItem = new ResizePresetItem(Loc("ResizeQuick_PresetCustom"), null);
        _savePreset = savePreset;
        SavePresetButton.Visibility = savePreset is null ? Visibility.Collapsed : Visibility.Visible;
        _items.Add(_customItem);
        _items.AddRange(presets);
        PresetBox.ItemsSource = _items;

        ModeBox.ItemsSource = Options("fit", "fill", "stretch");
        UnitBox.ItemsSource = Options("px", "percent", "inch", "cm");
        FormatBox.ItemsSource = Options("keep", "png", "jpg", "webp", "bmp", "tiff", "gif");

        // No stored values yet: start from a sensible Full HD fit.
        ApplyConfig(lastValues.Count == 0 ? DefaultValues() : lastValues);
        PresetBox.SelectedIndex = 0;
        _loading = false;

        // The window is launched from Explorer while another app owns the foreground, so a plain
        // Show leaves it behind. A Topmost flip is the reliable way to bring it forward.
        Loaded += (_, _) =>
        {
            Activate();
            Topmost = true;
            Topmost = false;
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    /// <summary>The chosen parameters after Resize was pressed, null when the dialog was
    /// cancelled or closed. Keys are the ones <c>ResizeImageFilesTask</c> reads.</summary>
    public JsonObject? ResultConfig { get; private set; }

    /// <summary>Values used when nothing has been stored from a previous run.</summary>
    public static JsonObject DefaultValues() => new()
    {
        ["mode"] = "fit",
        ["unit"] = "px",
        ["width"] = 1920d,
        ["height"] = 1080d,
        ["ignore_orientation"] = true,
    };

    private static string Loc(string key)
    {
        var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        return Strings.ResourceManager.GetString(key, culture) ?? key;
    }

    private static List<Option> Options(params string[] raws)
        => raws.Select(r => new Option(r, Loc("EnumValue_" + r))).ToList();

    private static void Select(System.Windows.Controls.ComboBox box, string? raw)
    {
        var list = (List<Option>)box.ItemsSource;
        var wanted = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (wanted == "jpeg") wanted = "jpg";
        if (wanted == "tif") wanted = "tiff";
        box.SelectedItem = list.FirstOrDefault(o => o.Raw == wanted) ?? list[0];
    }

    private static string FormatSize(double? value)
        => value is { } v && double.IsFinite(v) && v > 0
            ? v.ToString("0.##", CultureInfo.CurrentCulture)
            : "0";

    /// <summary>Fills every field from a resize step config. Missing keys take the same
    /// defaults the task uses.</summary>
    private void ApplyConfig(JsonNode? config)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            Select(ModeBox, ConfigReader.GetString(config, "mode"));
            Select(UnitBox, ConfigReader.GetString(config, "unit"));
            WidthBox.Text = FormatSize(ConfigReader.GetDouble(config, "width"));
            HeightBox.Text = FormatSize(ConfigReader.GetDouble(config, "height"));
            ShrinkOnlyBox.IsChecked = ConfigReader.GetBool(config, "shrink_only") ?? false;
            IgnoreOrientationBox.IsChecked = ConfigReader.GetBool(config, "ignore_orientation") ?? true;
            OverwriteBox.IsChecked = ConfigReader.GetBool(config, "replace_original") ?? false;
            StripMetadataBox.IsChecked = ConfigReader.GetBool(config, "strip_metadata") ?? false;
            Select(FormatBox, ConfigReader.GetString(config, "format"));
            var quality = ConfigReader.GetDouble(config, "quality");
            QualitySlider.Value = quality is { } q && double.IsFinite(q) ? Math.Clamp(Math.Round(q), 1, 100) : 90;
            QualityText.Text = ((int)QualitySlider.Value).ToString(CultureInfo.CurrentCulture);
            var template = ConfigReader.GetString(config, "file_name_template");
            TemplateBox.Text = string.IsNullOrWhiteSpace(template) ? "{name}_{h}" : template;
            OutputFolderBox.Text = ConfigReader.GetString(config, "output_folder") ?? string.Empty;
            UpdateQualityEnabled();
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void UpdateQualityEnabled()
    {
        var raw = (FormatBox.SelectedItem as Option)?.Raw;
        QualitySlider.IsEnabled = raw is "jpg" or "webp";
    }

    private void OnPresetChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (PresetBox.SelectedItem is ResizePresetItem { Config: { } config })
            ApplyConfig(config);
    }

    /// <summary>Any manual edit turns the preset box back to "Custom".</summary>
    private void OnParamChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateQualityEnabled();
        if (!ReferenceEquals(PresetBox.SelectedItem, _customItem))
        {
            _loading = true;
            try { PresetBox.SelectedItem = _customItem; }
            finally { _loading = false; }
        }
    }

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during InitializeComponent, before the text block exists.
        if (QualityText is null) return;
        QualityText.Text = ((int)e.NewValue).ToString(CultureInfo.CurrentCulture);
        OnParamChanged(sender, e);
    }

    /// <summary>Accepts the current culture decimal separator and, as a fallback, the invariant
    /// one. An empty box counts as 0 (automatic).</summary>
    private static bool TryParseSize(string? text, out double value)
    {
        value = 0;
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return true;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        return double.IsFinite(value) && value >= 0;
    }

    /// <summary>Builds the config from the fields, or warns and returns null when the size is
    /// invalid (both width and height 0).</summary>
    private JsonObject? BuildConfig()
    {
        if (!TryParseSize(WidthBox.Text, out var width) || !TryParseSize(HeightBox.Text, out var height)
            || (width <= 0 && height <= 0))
        {
            System.Windows.MessageBox.Show(this, Loc("ResizeQuick_InvalidSize"), Loc("ResizeQuick_Title"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            WidthBox.Focus();
            return null;
        }

        return new JsonObject
        {
            ["mode"] = (ModeBox.SelectedItem as Option)?.Raw ?? "fit",
            ["unit"] = (UnitBox.SelectedItem as Option)?.Raw ?? "px",
            ["width"] = width,
            ["height"] = height,
            ["shrink_only"] = ShrinkOnlyBox.IsChecked == true,
            ["ignore_orientation"] = IgnoreOrientationBox.IsChecked == true,
            ["format"] = (FormatBox.SelectedItem as Option)?.Raw ?? "keep",
            ["quality"] = (int)QualitySlider.Value,
            ["strip_metadata"] = StripMetadataBox.IsChecked == true,
            ["output_folder"] = (OutputFolderBox.Text ?? string.Empty).Trim(),
            ["file_name_template"] = (TemplateBox.Text ?? string.Empty).Trim(),
            // A picked preset keeps its own name for the {preset} file name token; any manual
            // edit already switched the box back to the Custom entry.
            ["preset_name"] = PresetBox.SelectedItem is ResizePresetItem { Config: not null } picked ? picked.Name : "Custom",
            ["replace_original"] = OverwriteBox.IsChecked == true,
            ["keep_date_modified"] = false,
            ["on_collision"] = "uniquify",
            ["show_notification"] = true,
        };
    }

    private void OnResizeClicked(object sender, RoutedEventArgs e)
    {
        if (BuildConfig() is not { } config) return;
        ResultConfig = config;
        Close();
    }

    private async void OnSavePresetClicked(object sender, RoutedEventArgs e)
    {
        if (_savePreset is null) return;
        if (BuildConfig() is not { } config) return;

        var prompt = new ResizePresetNameDialog { Owner = this };
        if (prompt.ShowDialog() != true || prompt.PresetName is not { } name) return;

        SavePresetButton.IsEnabled = false;
        try
        {
            var finalName = await _savePreset(name, config);
            var itemConfig = (JsonObject)config.DeepClone();
            itemConfig["replace_original"] = false;
            var item = new ResizePresetItem(finalName, itemConfig);
            _items.Add(item);
            _loading = true;
            try
            {
                PresetBox.ItemsSource = null;
                PresetBox.ItemsSource = _items;
                PresetBox.SelectedItem = item;
            }
            finally { _loading = false; }
            SavedText.Text = Loc("ResizeQuick_SavedAs").Replace("{0}", finalName, StringComparison.Ordinal);
            SavedText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Loc("ResizeQuick_SaveAsPreset"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            SavePresetButton.IsEnabled = true;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
