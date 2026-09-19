using System.IO;
using System.Windows;
using AresToys.App.Services;
using AresToys.App.Services.Launcher;

namespace AresToys.App.Views;

/// <summary>Modal dialog used by the launcher overlay to edit a single cell. Returns the
/// updated <see cref="LauncherCell"/> via <see cref="Result"/> when DialogResult==true; on
/// "Clear" the result is an empty cell so the caller can persist deconfiguration.</summary>
public partial class LauncherCellEditDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _tabKey;
    private readonly string _keyChar;
    private readonly IconService _icons;

    /// <summary>Wraps an enum value with its localised display string for the WindowMode combo.
    /// ItemTemplate-less ComboBox renders ToString(), so we override that to the localised
    /// caption while keeping the enum tag retrievable via <see cref="Value"/>.</summary>
    private sealed record WindowModeOption(LauncherWindowMode Value, string Display)
    {
        public override string ToString() => Display;
    }

    public LauncherCellEditDialog(LauncherCell initial, IconService icons)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _tabKey = initial.TabKey;
        _keyChar = initial.KeyChar;
        _icons = icons;
        // Header includes the namespace so the user knows which slot they're editing — the
        // F-strip and the 10 tabs all use the same QWERTY letters, so "Cell Q" alone would be
        // ambiguous between (tab1, Q), (tab2, Q), …
        // Display the char that the user actually sees on their keyboard for this slot —
        // matches the cell label in the launcher, so editing "Tab 1 · Cell ò" on IT layout
        // matches what the user just right-clicked. Storage stays canonical underneath.
        var glyph = KeyboardLayoutMapper.GetDisplayChar(_keyChar);
        HeaderText.Text = _tabKey == LauncherTabs.FunctionStrip
            ? Loc("LauncherCellEdit_HeaderFunctionKey", glyph)
            : Loc("LauncherCellEdit_HeaderTabCell", _tabKey, glyph);
        LabelBox.Text = initial.Label;
        PathBox.Text  = initial.Path;
        ArgsBox.Text  = initial.Args;
        IconBox.Text  = initial.IconPath;
        IconIndexBox.Text = initial.IconIndex == 0 ? string.Empty : initial.IconIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

        RunAsAdminBox.IsChecked = initial.RunAsAdmin;
        // Localise enum values via WindowModeOption — the bare Enum.GetValues path renders
        // "Normal/Minimized/…" verbatim, ignoring the user's UI language.
        var modes = Enum.GetValues<LauncherWindowMode>()
            .Select(m => new WindowModeOption(m, m switch
            {
                LauncherWindowMode.Normal     => Loc("LauncherCellEdit_WindowModeNormal"),
                LauncherWindowMode.Minimized  => Loc("LauncherCellEdit_WindowModeMinimized"),
                LauncherWindowMode.Maximized  => Loc("LauncherCellEdit_WindowModeMaximized"),
                LauncherWindowMode.Hidden     => Loc("LauncherCellEdit_WindowModeHidden"),
                _ => m.ToString(),
            }))
            .ToList();
        WindowModeBox.ItemsSource = modes;
        WindowModeBox.SelectedItem = modes.FirstOrDefault(o => o.Value == initial.WindowMode) ?? modes[0];
        WindowTitleBox.Text = initial.WindowTitle;
        ProcessNameBox.Text = initial.ProcessName;
    }

    private static string Loc(string key, params object[] args)
    {
        var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture
                      ?? System.Globalization.CultureInfo.CurrentUICulture;
        var template = AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
        // CA1863 wants a cached CompositeFormat for "repeated" formatting; this helper runs
        // exactly twice per dialog open with runtime-loaded templates that vary per culture.
        // Caching per (key, culture) tuple is heavier than the work it saves — suppress.
#pragma warning disable CA1863
        return args.Length == 0 ? template : string.Format(culture, template, args);
#pragma warning restore CA1863
    }

    public LauncherCell? Result { get; private set; }

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick file for launcher cell",
            CheckFileExists = true,
            Multiselect = false,
        };
        SeedDirectory(s => dlg.InitialDirectory = s);
        if (dlg.ShowDialog() != true) return;

        // Same treatment a drop on the cell gets: a .lnk is unwrapped into the target it points
        // at plus the settings it carries, so the cell doesn't break when the shortcut moves and
        // its arguments stay visible and editable here.
        var target = LauncherDropTarget.Resolve(dlg.FileName);
        PathBox.Text = target.Path;

        // Auto-fill the Label from the filename if it's still empty — common case "I just
        // picked notepad.exe, the cell should say Notepad" without an extra typing step.
        if (string.IsNullOrWhiteSpace(LabelBox.Text)) LabelBox.Text = target.Label;

        // Only fill what the source actually declared: a shortcut without arguments mustn't wipe
        // arguments the user typed here a moment ago.
        if (!string.IsNullOrEmpty(target.Arguments)) ArgsBox.Text = target.Arguments;
        if (!string.IsNullOrEmpty(target.IconPath))
        {
            IconBox.Text = target.IconPath;
            IconIndexBox.Text = target.IconIndex == 0
                ? string.Empty
                : target.IconIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (target.RunAsAdmin) RunAsAdminBox.IsChecked = true;
        if (target.WindowMode != LauncherWindowMode.Normal
            && WindowModeBox.ItemsSource is IEnumerable<WindowModeOption> options)
        {
            WindowModeBox.SelectedItem = options.FirstOrDefault(o => o.Value == target.WindowMode)
                                         ?? WindowModeBox.SelectedItem;
        }
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick folder for launcher cell",
        };
        SeedDirectory(s => dlg.InitialDirectory = s);
        if (dlg.ShowDialog() == true)
        {
            PathBox.Text = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(LabelBox.Text))
            {
                LabelBox.Text = new DirectoryInfo(dlg.FolderName).Name;
            }
        }
    }

    private void SeedDirectory(Action<string> set)
    {
        var p = PathBox.Text;
        if (string.IsNullOrWhiteSpace(p)) return;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(p);
            var dir = Directory.Exists(expanded) ? expanded : Path.GetDirectoryName(expanded);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) set(dir);
        }
        catch { /* fall back to dialog default */ }
    }

    private void OnPickIcon(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick custom icon",
            Filter = "Image / icon files|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif|Icon (*.ico)|*.ico|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(IconBox.Text))
        {
            try
            {
                var dir = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(IconBox.Text));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            }
            catch { /* fall back to dialog default */ }
        }
        if (dlg.ShowDialog() == true) IconBox.Text = dlg.FileName;
    }

    private void OnClearIcon(object sender, RoutedEventArgs e) => IconBox.Text = string.Empty;

    private void OnPickWindow(object sender, RoutedEventArgs e)
    {
        var dlg = new WindowPickerDialog(_icons) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        // Populate both fields. Title is enough for window-title match; ProcessName is more
        // resilient to title changes (Chrome rewriting its title as you switch tabs, etc),
        // so we fill both — the launcher's TryActivate will pick whichever wins first.
        WindowTitleBox.Text = dlg.Result.Title;
        ProcessNameBox.Text = dlg.Result.ProcessName;
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        Result = LauncherCell.Empty(_tabKey, _keyChar);
        DialogResult = true;
        Close();
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        var mode = WindowModeBox.SelectedItem is WindowModeOption opt ? opt.Value : LauncherWindowMode.Normal;
        var iconIndex = int.TryParse(IconIndexBox.Text.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var ii) ? ii : 0;
        Result = new LauncherCell(_tabKey, _keyChar,
            LabelBox.Text.Trim(),
            PathBox.Text.Trim(),
            ArgsBox.Text.Trim(),
            RunAsAdmin: RunAsAdminBox.IsChecked == true,
            WindowMode: mode,
            WindowTitle: WindowTitleBox.Text.Trim(),
            ProcessName: ProcessNameBox.Text.Trim(),
            IconPath: IconBox.Text.Trim(),
            IconIndex: iconIndex);
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
