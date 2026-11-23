using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AresToys.App.Resources;
using AresToys.App.Services;
using AresToys.App.Services.FileLocks;
using AresToys.App.Services.Launcher;

namespace AresToys.App.Views;

/// <summary>"What's locking this file?": the programs that keep a file or folder open, LockHunter style (engine in
/// <see cref="LockFinder"/>, actions in <see cref="LockActions"/>). Unlock closes the handles and, when a program still
/// holds the item, ends it after asking (the owner's pick). Delete, rename and copy unlock first. Opened by the
/// "--unlock-file" Explorer verb in its own short lived process (App.OnStartup), so it works with AresToys closed.</summary>
public partial class FileLocksWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IconService _icons;
    private string _path;
    private IReadOnlyList<LockingProcess> _processes = [];
    private bool _busy;

    /// <summary>One row of the program list.</summary>
    public sealed record ProcessRow(LockingProcess Process, ImageSource? Icon)
    {
        public string Name => Process.Name;
        public int ProcessId => Process.ProcessId;
        public string User => Process.User ?? string.Empty;
        public string ExecutablePath => Process.ExecutablePath ?? string.Empty;
    }

    public FileLocksWindow(string path, IconService icons)
    {
        ArgumentNullException.ThrowIfNull(icons);
        _path = path;
        _icons = icons;
        InitializeComponent();
        DarkTitleBar.SuppressResizeFlicker(this);
        DarkTitleBar.EnlargeResizeHitZones(this);
        RestartAdminButton.Visibility = ElevationService.IsCurrentProcessElevated ? Visibility.Collapsed : Visibility.Visible;
        ShowItem();
        Loaded += async (_, _) => await SearchAsync();
    }

    private bool Exists => File.Exists(_path) || Directory.Exists(_path);

    private void ShowItem()
    {
        ItemName.Text = Path.GetFileName(_path.TrimEnd('\\')) is { Length: > 0 } name ? name : _path;
        ItemPath.Text = _path;
        ItemIcon.Source = _icons.GetIconAtSize(_path, 32);
    }

    private async Task SearchAsync()
    {
        if (_busy) return;
        if (!Exists)
        {
            _processes = [];
            ProcessList.ItemsSource = null;
            ItemList.ItemsSource = null;
            SetStatus(Format(Strings.FileLocks_NotFound, _path));
            UpdateButtons();
            return;
        }

        SetBusy(true);
        SetStatus(Strings.FileLocks_Searching);
        try
        {
            var path = _path;
            var result = await Task.Run(() => LockFinder.Find([path]));
            Show(result);
            SetStatus(result.Processes.Count == 0 ? Strings.FileLocks_NoneFound : Format(Strings.FileLocks_Found, result.Processes.Count));
        }
        catch (Exception ex)
        {
            SetStatus(Format(Strings.FileLocks_Failed, ex.Message));
        }
        finally { SetBusy(false); }
    }

    private void Show(LockSearchResult result)
    {
        _processes = result.Processes;
        PartialBar.Visibility = result.Partial ? Visibility.Visible : Visibility.Collapsed;
        ProcessList.ItemsSource = result.Processes
            .Select(p => new ProcessRow(p, p.ExecutablePath is { } exe ? _icons.GetIconAtSize(exe, 16) : null))
            .ToList();
        if (ProcessList.Items.Count > 0) ProcessList.SelectedIndex = 0;
        else ItemList.ItemsSource = null;
    }

    private void OnProcessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ItemList.ItemsSource = (ProcessList.SelectedItem as ProcessRow)?.Process.Items
            .Select(i => i.IsModule ? Format(Strings.FileLocks_Module, i.Path) : i.Path)
            .ToList();
        UpdateButtons();
    }

    /// <summary>Closes every handle on the item, searches again and, when programs still hold it, ends them after
    /// asking. True when nothing holds the item any more.</summary>
    private async Task<bool> UnlockAsync()
    {
        if (!Exists) return true;
        var path = _path;
        var processes = _processes;
        var remaining = await Task.Run(() =>
        {
            foreach (var process in processes) LockActions.Unlock(process);
            return LockFinder.Find([path]);
        });
        Show(remaining);
        if (remaining.Processes.Count == 0) return true;

        var names = string.Join("\n", remaining.Processes.Select(p => $"{p.Name} (PID {p.ProcessId})"));
        if (MessageBox.Show(this, Format(Strings.FileLocks_ConfirmKill, names), Strings.FileLocks_Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            SetStatus(Format(Strings.FileLocks_StillLocked, JoinNames(remaining.Processes)));
            return false;
        }

        var afterKill = await Task.Run(() =>
        {
            foreach (var process in remaining.Processes) LockActions.EndProcess(process.ProcessId);
            return LockFinder.Find([path]);
        });
        Show(afterKill);
        if (afterKill.Processes.Count == 0) return true;
        SetStatus(Format(Strings.FileLocks_StillLocked, JoinNames(afterKill.Processes)));
        return false;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        SetBusy(true);
        try { await action(); }
        catch (Exception ex) { SetStatus(Format(Strings.FileLocks_Failed, ex.Message)); }
        finally { SetBusy(false); }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnUnlockClicked(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (await UnlockAsync()) SetStatus(Strings.FileLocks_Unlocked);
    });

    private async void OnDeleteClicked(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!await UnlockAsync()) return;
        LockActions.Delete(_path);
        Show(new LockSearchResult([], Partial: false));
        SetStatus(Strings.FileLocks_Deleted);
    });

    private void OnOtherClicked(object sender, RoutedEventArgs e)
    {
        if (OtherButton.ContextMenu is not { } menu) return;
        EndProcessMenuItem.IsEnabled = ProcessList.SelectedItem is ProcessRow;
        CopyMenuItem.IsEnabled = File.Exists(_path);
        menu.PlacementTarget = OtherButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private async void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog(Strings.FileLocks_Rename.TrimEnd('…'), Strings.FileLocks_RenamePrompt, Path.GetFileName(_path)) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result)) return;
        var newName = dialog.Result.Trim();
        await RunAsync(async () =>
        {
            if (!await UnlockAsync()) return;
            _path = LockActions.Rename(_path, newName);
            ShowItem();
            SetStatus(Format(Strings.FileLocks_Renamed, Path.GetFileName(_path)));
        });
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(_path),
            InitialDirectory = Path.GetDirectoryName(_path),
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        await RunAsync(async () =>
        {
            if (!await UnlockAsync()) return;
            LockActions.Copy(_path, destination);
            SetStatus(Format(Strings.FileLocks_Copied, destination));
        });
    }

    private void OnDeleteAtRestartClicked(object sender, RoutedEventArgs e)
        => SetStatus(LockActions.DeleteAtRestart(_path) ? Strings.FileLocks_DeleteAtRestartDone : Strings.FileLocks_DeleteAtRestartFailed);

    private async void OnEndProcessClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessRow row) return;
        if (MessageBox.Show(this, Format(Strings.FileLocks_ConfirmEnd, row.Name, row.ProcessId), Strings.FileLocks_Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ended = await Task.Run(() => LockActions.EndProcess(row.ProcessId));
        await SearchAsync();
        if (!ended) SetStatus(Format(Strings.FileLocks_StillLocked, row.Name));
    }

    private void OnOpenLocationClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessRow { Process.ExecutablePath: { } exe }) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(Format(Strings.FileLocks_Failed, ex.Message)); }
    }

    /// <summary>Starts this window again as administrator (UAC), so programs running elevated or as another user are
    /// listed and can be unlocked, then closes this one.</summary>
    private void OnRestartAsAdminClicked(object sender, RoutedEventArgs e)
    {
        var exe = ExplorerContextMenuRegistration.CurrentExePath();
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"{FileLocksMenuRegistration.WindowCommandLineArg} \"{_path}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            });
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined: stay as we are.
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var exists = Exists;
        RefreshButton.IsEnabled = !_busy;
        UnlockButton.IsEnabled = !_busy && _processes.Count > 0;
        DeleteButton.IsEnabled = !_busy && exists;
        OtherButton.IsEnabled = !_busy && exists;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string JoinNames(IReadOnlyList<LockingProcess> processes)
        => string.Join(", ", processes.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
