using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Resources;
using AresToys.App.Services.Hotkeys;
using AresToys.App.Services.Peek;
using AresToys.Core.Pipeline;
using AresToys.Hotkeys;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AresToys.App.ViewModels;

/// <summary>A browse mode and its name in the settings list.</summary>
public sealed record PeekBrowseChoice(PeekBrowseMode Mode, string Name);

/// <summary>Backs the Peek sidebar section: the "Open in" programs and the extension to program associations.
/// The module switch itself stays on <see cref="SettingsViewModel"/> (it persists and applies the satellite).
/// Every change is saved at once through <see cref="PeekOpenWithStore"/>; the satellite reads the config fresh
/// at each Peek open, so saving is enough.</summary>
public sealed partial class PeekSettingsViewModel : ObservableObject
{
    private readonly PeekOpenWithStore _store;
    private readonly PeekKeysStore _keysStore;
    private readonly PeekWindowStore _windowStore;
    private readonly PeekShowAsStore _showAsStore;
    private readonly ILogger<PeekSettingsViewModel> _logger;
    private bool _showingFit;
    private PeekOpenWithConfig _config = new();
    private PeekKeys _keys = PeekKeys.Default;
    private bool _loading;
    private string? _editingId;

    public PeekSettingsViewModel(PeekOpenWithStore store, PeekKeysStore keysStore, PeekWindowStore windowStore, PeekShowAsStore showAsStore, AresToys.App.Services.Launcher.IconService icons, ILogger<PeekSettingsViewModel> logger)
    {
        Icons = icons;
        _store = store;
        _keysStore = keysStore;
        _windowStore = windowStore;
        _showAsStore = showAsStore;
        _logger = logger;
        ProgramChoices.Add(DefaultChoice);
        ShowKeys();
    }

    // ---- the keys ----

    /// <summary>The shortcut that opens and closes Peek, as text (<c>Ctrl + Space</c>).</summary>
    [ObservableProperty] private string _toggleText = string.Empty;

    /// <summary>The key that opens the file in its assigned program while Peek is open.</summary>
    [ObservableProperty] private string _openKeyText = string.Empty;

    /// <summary>The key that opens an image in the editor while Peek is open.</summary>
    [ObservableProperty] private string _editKeyText = string.Empty;

    /// <summary>Why the last key was refused; empty when there is nothing to say.</summary>
    [ObservableProperty] private string _keyErrorText = string.Empty;

    private void ShowKeys()
    {
        ToggleText = PeekKeysStore.Format(_keys.Toggle);
        OpenKeyText = PeekKeysStore.Format(_keys.OpenAssigned);
        EditKeyText = PeekKeysStore.Format(_keys.Edit);
    }

    // ---- file types (the "Show as" picks) ----

    /// <summary>The extensions that have a "Show as" pick, for the File types list.</summary>
    public ObservableCollection<PeekFileTypeRow> FileTypes { get; } = new();

    /// <summary>True when no extension has a pick (shows the empty line).</summary>
    [ObservableProperty] private bool _noFileTypes = true;

    private async Task LoadFileTypesAsync()
    {
        try
        {
            ShowFileTypes(await _showAsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not load the file types");
            ShowFileTypes(new Dictionary<string, string>());
        }
    }

    private void ShowFileTypes(IReadOnlyDictionary<string, string> picks)
    {
        FileTypes.Clear();
        foreach (var pair in picks.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (PeekShowAs.Resolve(pair.Value) is null) continue;
            FileTypes.Add(new PeekFileTypeRow(this, pair.Key, PeekShowAs.DisplayName(pair.Value)));
        }

        NoFileTypes = FileTypes.Count == 0;
    }

    internal async Task RemoveFileTypeAsync(PeekFileTypeRow row)
    {
        try
        {
            ShowFileTypes(await _showAsStore.SetAsync(row.Extension, null, CancellationToken.None).ConfigureAwait(true));
            FileTypesErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not remove the file type");
            FileTypesErrorText = SaveErrorText(ex);
            await LoadFileTypesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Why a file type could not be removed; empty when there is nothing to say.</summary>
    [ObservableProperty] private string _fileTypesErrorText = string.Empty;

    // ---- the window ----

    /// <summary>Size the Peek window to the image it shows. Saved at once; the satellite reads it at each Peek.</summary>
    [ObservableProperty] private bool _fitToContent;

    /// <summary>Why the fit option could not be saved or read; empty when there is nothing to say.</summary>
    [ObservableProperty] private string _fitErrorText = string.Empty;

    partial void OnFitToContentChanged(bool value)
    {
        if (_showingFit) return;
        _ = SaveFitAsync(value);
    }

    private async Task SaveFitAsync(bool value)
    {
        try
        {
            await _windowStore.SaveFitAsync(value, CancellationToken.None).ConfigureAwait(true);
            FitErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not save the fit option");
            FitErrorText = SaveErrorText(ex);
        }
    }

    /// <summary>The three browse modes with their names, for the Browse choice.</summary>
    public IReadOnlyList<PeekBrowseChoice> BrowseChoices { get; } = new[]
    {
        new PeekBrowseChoice(PeekBrowseMode.All, AresToys.App.Resources.Strings.Peek_NavAll),
        new PeekBrowseChoice(PeekBrowseMode.Family, AresToys.App.Resources.Strings.Peek_NavFamily),
        new PeekBrowseChoice(PeekBrowseMode.Extension, AresToys.App.Resources.Strings.Peek_NavExtension),
    };

    /// <summary>Which files of the folder Peek's previous and next go through. Saved at once; the satellite reads it at each Peek.</summary>
    [ObservableProperty] private PeekBrowseMode _browseMode;

    partial void OnBrowseModeChanged(PeekBrowseMode value)
    {
        if (_showingFit) return;
        _ = SaveBrowseAsync(value);
    }

    private async Task SaveBrowseAsync(PeekBrowseMode value)
    {
        try
        {
            await _windowStore.SaveBrowseAsync(value, CancellationToken.None).ConfigureAwait(true);
            FitErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not save the browse mode");
            FitErrorText = SaveErrorText(ex);
        }
    }

    private async Task LoadFitAsync()
    {
        try
        {
            var window = await _windowStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            _showingFit = true;
            FitToContent = window.FitToContent;
            BrowseMode = window.Browse;
            FitErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not load the window settings");
            FitErrorText = SaveErrorText(ex);
        }
        finally
        {
            _showingFit = false;
        }
    }

    [RelayCommand]
    private Task ChangeToggleAsync() => ChangeKeyAsync(PeekKey.Toggle);

    [RelayCommand]
    private Task ChangeOpenKeyAsync() => ChangeKeyAsync(PeekKey.OpenAssigned);

    [RelayCommand]
    private Task ChangeEditKeyAsync() => ChangeKeyAsync(PeekKey.Edit);

    private enum PeekKey { Toggle, OpenAssigned, Edit }

    /// <summary>Opens the capture window of the Hotkeys editor, checks the key and saves it at once. Reset and Clear
    /// both give the default key; Cancel changes nothing.</summary>
    private async Task ChangeKeyAsync(PeekKey which)
    {
        var dialog = new Views.HotkeyCaptureWindow(canReset: true);
        if (System.Windows.Application.Current?.MainWindow is { } owner && owner.IsVisible)
            dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        HotkeyBinding binding;
        if (dialog.ResetRequested || dialog.ClearRequested)
            binding = which switch
            {
                PeekKey.Toggle => PeekKeys.DefaultToggle,
                PeekKey.OpenAssigned => PeekKeys.DefaultOpenAssigned,
                _ => PeekKeys.DefaultEdit,
            };
        else if (dialog.CapturedVirtualKey == 0)
            return;
        else
            binding = new HotkeyBinding((int)dialog.CapturedModifiers, dialog.CapturedVirtualKey);

        var others = which switch
        {
            PeekKey.Toggle => new[] { _keys.OpenAssigned, _keys.Edit },
            PeekKey.OpenAssigned => new[] { _keys.Toggle, _keys.Edit },
            _ => new[] { _keys.Toggle, _keys.OpenAssigned },
        };
        var refusal = Refusal(binding, others);
        if (refusal is not null)
        {
            KeyErrorText = refusal;
            return;
        }

        KeyErrorText = string.Empty;
        var next = which switch
        {
            PeekKey.Toggle => _keys with { Toggle = binding },
            PeekKey.OpenAssigned => _keys with { OpenAssigned = binding },
            _ => _keys with { Edit = binding },
        };
        try
        {
            await _keysStore.SaveAsync(next, CancellationToken.None).ConfigureAwait(true);
            _keys = next;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not save the Peek keys");
            KeyErrorText = SaveErrorText(ex);
        }
        ShowKeys();
    }

    /// <summary>The message for a key Peek cannot use, or null. The navigation keys and the mouse buttons are refused
    /// whatever the modifiers; the Peek keys must all differ.</summary>
    internal static string? Refusal(HotkeyBinding binding, IEnumerable<HotkeyBinding> others)
    {
        var vk = binding.VirtualKey;
        if (vk is 0x1B or 0x0D or >= 0x23 and <= 0x28) return Strings.Peek_ErrorKeyNavigation;
        if (HotkeyDisplay.IsMouseButton(vk)) return Strings.Peek_ErrorKeyMouse;
        if (others.Any(other => PeekKeys.Matches(other, (HotkeyModifiers)binding.Modifiers, vk))) return Strings.Peek_ErrorKeysSame;
        return null;
    }

    private static PeekProgram DefaultChoice => new("", Strings.Peek_OpenInDefault, "", "");

    /// <summary>The programs, for the list.</summary>
    public ObservableCollection<PeekProgram> Programs { get; } = new();

    /// <summary>Shell icons for the program list (the shared cached service the Launcher uses).</summary>
    public AresToys.App.Services.Launcher.IconService Icons { get; }

    /// <summary>"System default app" followed by the programs: the items of every association combo box.</summary>
    public ObservableCollection<PeekProgram> ProgramChoices { get; } = new();

    public ObservableCollection<PeekAssociationRow> Associations { get; } = new();

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EditProgramCommand), nameof(RemoveProgramCommand))]
    private PeekProgram? _selectedProgram;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editExe = string.Empty;
    [ObservableProperty] private string _editArgs = string.Empty;

    [ObservableProperty] private string _newExtension = string.Empty;
    [ObservableProperty] private string _newProgramId = string.Empty;

    [ObservableProperty] private string _errorText = string.Empty;

    public async Task LoadAsync()
    {
        try
        {
            _keys = await _keysStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            ShowKeys();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not load the Peek keys");
        }

        await LoadFitAsync().ConfigureAwait(true);
        await LoadFileTypesAsync().ConfigureAwait(true);

        try
        {
            _config = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            ErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not load the open-with config");
            _config = new PeekOpenWithConfig();
            ErrorText = SaveErrorText(ex);
        }
        Rebuild();
    }

    /// <summary>Rebuilds the three collections from <see cref="_config"/>; the combo boxes re-select their value
    /// while <see cref="_loading"/> is set, so that no change is written back.</summary>
    private void Rebuild()
    {
        _loading = true;
        try
        {
            var previous = SelectedProgram?.Id;
            Programs.Clear();
            ProgramChoices.Clear();
            ProgramChoices.Add(DefaultChoice);
            foreach (var p in _config.Programs)
            {
                Programs.Add(p);
                ProgramChoices.Add(p);
            }
            SelectedProgram = Programs.FirstOrDefault(p => p.Id == previous);

            Associations.Clear();
            foreach (var pair in _config.Associations
                         .OrderBy(a => a.Key == ".*" ? 1 : 0)
                         .ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
            {
                var id = _config.Programs.Any(p => string.Equals(p.Id, pair.Value, StringComparison.OrdinalIgnoreCase))
                    ? _config.Programs.First(p => string.Equals(p.Id, pair.Value, StringComparison.OrdinalIgnoreCase)).Id
                    : "";
                Associations.Add(new PeekAssociationRow(this, pair.Key, id));
            }
            if (NewProgramId is { Length: > 0 } && Programs.All(p => p.Id != NewProgramId)) NewProgramId = "";
        }
        finally { _loading = false; }
    }

    /// <summary>Saves <see cref="_config"/>. On failure the error is logged and shown, and the config is reloaded
    /// from the store so that the screen shows what is really saved.</summary>
    private async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(_config, CancellationToken.None).ConfigureAwait(true);
            ErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Peek settings: could not save the open-with config");
            var message = SaveErrorText(ex);
            await LoadAsync().ConfigureAwait(true);
            ErrorText = message;
        }
    }

    private static string SaveErrorText(Exception ex) =>
        Strings.Peek_ErrorSave.Replace("{0}", ex.Message, StringComparison.Ordinal);

    // ---- programs ----

    [RelayCommand]
    private void AddProgram()
    {
        _editingId = null;
        EditName = string.Empty;
        EditExe = string.Empty;
        EditArgs = string.Empty;
        ErrorText = string.Empty;
        IsEditing = true;
        // Saves a click: the file picker opens at once; cancelling it leaves the empty fields to fill by hand.
        BrowseProgram();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProgram))]
    private void EditProgram()
    {
        if (SelectedProgram is not { } p) return;
        _editingId = p.Id;
        EditName = p.Name;
        EditExe = p.Exe;
        EditArgs = p.Args;
        ErrorText = string.Empty;
        IsEditing = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProgram))]
    private async Task RemoveProgramAsync()
    {
        if (SelectedProgram is not { } p) return;
        _config.Programs.RemoveAll(x => x.Id == p.Id);
        foreach (var key in _config.Associations
                     .Where(a => string.Equals(a.Value, p.Id, StringComparison.OrdinalIgnoreCase))
                     .Select(a => a.Key).ToList())
            _config.Associations.Remove(key);
        if (_editingId == p.Id) IsEditing = false;
        Rebuild();
        await SaveAsync().ConfigureAwait(true);
    }

    private bool HasSelectedProgram() => SelectedProgram is not null;

    [RelayCommand]
    private void BrowseProgram()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.Peek_BrowseFilter,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        EditExe = dialog.FileName;
        EditName = PascalCaseName(Path.GetFileNameWithoutExtension(dialog.FileName));
    }

    /// <summary>"vectorcraft" -> "Vectorcraft", "pdf-craft_tool" -> "PdfCraftTool": every run of letters and
    /// digits starts upper case, the rest of each run is kept as written, separators are dropped.</summary>
    private static string PascalCaseName(string fileName)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(fileName, @"[^\p{L}\p{N}]+");
        return string.Concat(parts.Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    [RelayCommand]
    private async Task SaveProgramAsync()
    {
        var name = EditName.Trim();
        var exe = EditExe.Trim().Trim('"');
        if (name.Length == 0 || exe.Length == 0)
        {
            ErrorText = Strings.Peek_ErrorNeedNameAndProgram;
            return;
        }

        var args = EditArgs.Trim();
        if (_editingId is null)
        {
            _config.Programs.Add(new PeekProgram(Guid.NewGuid().ToString(), name, exe, args));
        }
        else
        {
            var index = _config.Programs.FindIndex(x => x.Id == _editingId);
            if (index >= 0) _config.Programs[index] = new PeekProgram(_editingId, name, exe, args);
            else _config.Programs.Add(new PeekProgram(_editingId, name, exe, args));
        }
        IsEditing = false;
        _editingId = null;
        Rebuild();
        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelProgram()
    {
        IsEditing = false;
        _editingId = null;
        ErrorText = string.Empty;
    }

    // ---- associations ----

    [RelayCommand]
    private async Task AddAssociationAsync()
    {
        var key = NormalizeExtension(NewExtension);
        if (key is null)
        {
            ErrorText = Strings.Peek_ErrorExtension;
            return;
        }
        _config.Associations[key] = NewProgramId ?? "";
        NewExtension = string.Empty;
        Rebuild();
        await SaveAsync().ConfigureAwait(true);
    }

    internal async Task RemoveAssociationAsync(PeekAssociationRow row)
    {
        _config.Associations.Remove(row.Extension);
        Rebuild();
        await SaveAsync().ConfigureAwait(true);
    }

    internal async Task AssociationChangedAsync(PeekAssociationRow row)
    {
        if (_loading) return;
        _config.Associations[row.Extension] = row.ProgramId ?? "";
        await SaveAsync().ConfigureAwait(true);
    }

    /// <summary>".JPG", "jpg", " .jpg " become ".jpg"; "*" and ".*" become ".*"; null when it is not an extension.</summary>
    internal static string? NormalizeExtension(string? text)
    {
        var s = (text ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        if (s == "*" || s == ".*") return ".*";
        if (!s.StartsWith('.')) s = "." + s;
        if (s.Length < 2) return null;
        // Only the extension itself: no spaces, wildcards, path separators or further dots.
        var rest = s.AsSpan(1);
        if (rest.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || rest.IndexOfAny(' ', '*', '.') >= 0) return null;
        return s;
    }
}

/// <summary>One File types row: an extension and the name of the view picked for it with "Show as".</summary>
public sealed partial class PeekFileTypeRow : ObservableObject
{
    private readonly PeekSettingsViewModel _owner;

    internal PeekFileTypeRow(PeekSettingsViewModel owner, string extension, string pickName)
    {
        _owner = owner;
        Extension = extension;
        PickName = pickName;
    }

    public string Extension { get; }

    public string PickName { get; }

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveFileTypeAsync(this);
}

/// <summary>One extension to program row. <see cref="ProgramId"/> is "" for the system default app.</summary>
public sealed partial class PeekAssociationRow : ObservableObject
{
    private readonly PeekSettingsViewModel _owner;

    internal PeekAssociationRow(PeekSettingsViewModel owner, string extension, string programId)
    {
        _owner = owner;
        Extension = extension;
        _programId = programId;
    }

    public string Extension { get; }

    public ObservableCollection<PeekProgram> Choices => _owner.ProgramChoices;

    [ObservableProperty] private string? _programId;

    partial void OnProgramIdChanged(string? value) => _ = _owner.AssociationChangedAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAssociationAsync(this);
}
