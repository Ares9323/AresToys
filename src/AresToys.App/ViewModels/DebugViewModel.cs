using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.Logging;

namespace AresToys.App.ViewModels;

/// <summary>Backs the Debug settings tab. Exposes the live entry collection for direct binding +
/// Clear / Copy / Filter commands. AutoScroll lets the user lock the view to the latest entry —
/// turned off automatically (in code-behind) when the user manually scrolls up so they can read
/// older lines without the list yanking back to the bottom. <see cref="Filter"/> is a free-text
/// substring filter applied case-insensitively to category + message; <see cref="CopyAll"/> copies
/// only the filtered subset so the user can grab a focused slice for a bug report.</summary>
public sealed partial class DebugViewModel : ObservableObject
{
    private readonly DebugLogService _service;
    private readonly ICollectionView _view;

    public DebugViewModel(DebugLogService service)
    {
        _service = service;
        _view = CollectionViewSource.GetDefaultView(_service.Entries);
        _view.Filter = MatchesFilter;
    }

    public ObservableCollection<DebugLogEntry> Entries => _service.Entries;

    /// <summary>Public view-collection that respects <see cref="Filter"/>. WPF ItemsControls bound
    /// to <see cref="Entries"/> already see filtered output because there's only one default
    /// CollectionView per source — exposing this is purely for explicit-binding scenarios.</summary>
    public ICollectionView View => _view;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _filter = string.Empty;

    partial void OnFilterChanged(string value) => _view.Refresh();

    private bool MatchesFilter(object obj)
    {
        if (string.IsNullOrEmpty(Filter)) return true;
        if (obj is not DebugLogEntry entry) return false;
        return entry.Category.Contains(Filter, StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains(Filter, StringComparison.OrdinalIgnoreCase)
            || entry.LevelDisplay.Contains(Filter, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void Clear() => _service.Clear();

    [RelayCommand]
    private void CopyAll()
    {
        // Copy = "what the user is currently looking at": if a filter is active, copy only the
        // filtered subset; otherwise everything. The Debug tab's primary purpose is grabbing a
        // focused slice for a bug report, so copying around the filter would be surprising.
        var sb = new StringBuilder();
        foreach (var obj in _view)
        {
            if (obj is not DebugLogEntry entry) continue;
            sb.Append(entry.Format()).Append('\n');
        }
        var text = sb.ToString();
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard may be locked by another app — silent retry isn't worth it here */ }
    }
}
