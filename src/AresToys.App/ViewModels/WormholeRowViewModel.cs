using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.Wormholes;

namespace AresToys.App.ViewModels;

/// <summary>One row in the Settings → Wormholes list. Wraps a <see cref="WormholeRecord"/>
/// with INPC properties so two-way bindings on Lock / Hidden CheckBoxes round-trip through the
/// store and the live <c>WormholeWindow</c> (via <see cref="IWormholeWindowManager.ReconcileAsync"/>).
/// Display-only geometry / kind / folder strings derive from the wrapped record.</summary>
public sealed partial class WormholeRowViewModel : ObservableObject
{
    private readonly IWormholeStore _store;
    private readonly IWormholeWindowManager _manager;
    private readonly WormholesViewModel _owner;
    private bool _suppressPersist;

    public WormholeRecord Record { get; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _isHidden;

    /// <summary>True when this row's wormhole is the one the user last interacted with on the
    /// desktop. Drives the row's "highlighted" surface in the Settings panel — bound to a
    /// DataTrigger that swaps the row Border's Background. Updated by
    /// <see cref="RefreshIsSelected"/>, which the owner calls after every
    /// <see cref="IWormholeWindowManager.WormholeFocused"/> event.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Re-read the selected state from the owner's
    /// <see cref="WormholesViewModel.SelectedWormholeId"/>. Cheap — guarded against no-op writes
    /// so the DataTrigger doesn't churn.</summary>
    public void RefreshIsSelected()
    {
        var selected = _owner.SelectedWormholeId == Record.Id;
        if (IsSelected != selected) IsSelected = selected;
    }
    // Geometry fields are strings (not doubles) so the WPF TextBox bindings can hold partial /
    // intermediate user input without immediately reverting. Commit happens on LostFocus (the
    // default UpdateSourceTrigger), the partial-method setter parses + applies via the manager.
    [ObservableProperty] private string _positionX = string.Empty;
    [ObservableProperty] private string _positionY = string.Empty;
    [ObservableProperty] private string _sizeW = string.Empty;
    [ObservableProperty] private string _sizeH = string.Empty;

    public WormholeRowViewModel(
        WormholeRecord record,
        IWormholeStore store,
        IWormholeWindowManager manager,
        WormholesViewModel owner)
    {
        Record = record;
        _store = store;
        _manager = manager;
        _owner = owner;
        // Initial property assignments must not echo back to the store — guard with a flag the
        // OnXxxChanged partials check before persisting.
        _suppressPersist = true;
        try
        {
            Title = record.Title;
            IsLocked = record.IsLocked;
            IsHidden = record.IsHidden;
            PositionX = FormatCoord(record.Geometry.X);
            PositionY = FormatCoord(record.Geometry.Y);
            SizeW    = FormatCoord(record.Geometry.Width);
            SizeH    = FormatCoord(record.Geometry.Height);
        }
        finally { _suppressPersist = false; }
    }

    public Guid Id => Record.Id;

    private static string FormatCoord(double v) => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Re-read every displayed property from the underlying record. Called by
    /// <see cref="WormholesViewModel"/> in response to <see cref="IWormholeWindowManager.RecordChanged"/>,
    /// which fires after the live chrome saves a drag/resize. Guarded by <see cref="_suppressPersist"/>
    /// so the property setters don't re-persist the values we just read.</summary>
    public void RefreshDisplay()
    {
        _suppressPersist = true;
        try
        {
            if (Title != Record.Title) Title = Record.Title;
            if (IsLocked != Record.IsLocked) IsLocked = Record.IsLocked;
            if (IsHidden != Record.IsHidden) IsHidden = Record.IsHidden;
            var x = FormatCoord(Record.Geometry.X);
            var y = FormatCoord(Record.Geometry.Y);
            var w = FormatCoord(Record.Geometry.Width);
            var h = FormatCoord(Record.Geometry.Height);
            if (PositionX != x) PositionX = x;
            if (PositionY != y) PositionY = y;
            if (SizeW    != w) SizeW    = w;
            if (SizeH    != h) SizeH    = h;
        }
        finally { _suppressPersist = false; }
    }

    /// <summary>Tooltip shown over the Title cell — full source folder path.</summary>
    public string Tooltip => Record.Portal?.SourcePath ?? "(no source)";

    partial void OnIsLockedChanged(bool value)
    {
        if (_suppressPersist) return;
        Record.IsLocked = value;
        Persist();
    }

    partial void OnIsHiddenChanged(bool value)
    {
        if (_suppressPersist) return;
        Record.IsHidden = value;
        Persist();
    }

    partial void OnPositionXChanged(string value) => CommitGeometryField(value, () => Record.Geometry.X, v => Record.Geometry.X = v);
    partial void OnPositionYChanged(string value) => CommitGeometryField(value, () => Record.Geometry.Y, v => Record.Geometry.Y = v);
    partial void OnSizeWChanged(string value)     => CommitGeometryField(value, () => Record.Geometry.Width, v =>
    {
        Record.Geometry.Width = v;
    }, minimum: 80);
    partial void OnSizeHChanged(string value)     => CommitGeometryField(value, () => Record.Geometry.Height, v =>
    {
        Record.Geometry.Height = v;
        // Editing the size from the panel implies the user wants this height to stick — keep
        // UnrolledHeight in sync so a future roll-up / unroll restores to this value.
        if (!Record.IsRolled) Record.Geometry.UnrolledHeight = v;
    }, minimum: 40);

    /// <summary>Parse + clamp + apply a geometry textbox value. No-op when the parsed value
    /// matches the current record state, or when the input doesn't parse (bad text just reverts
    /// to the persisted value on next RefreshDisplay).</summary>
    private void CommitGeometryField(string raw, Func<double> getCurrent, Action<double> apply, double minimum = 0)
    {
        if (_suppressPersist) return;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var v))
            return;
        if (minimum > 0 && v < minimum) v = minimum;
        if (Math.Abs(getCurrent() - v) < 0.5) return;
        apply(v);
        Persist();
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        // Lightweight rename via the InputBox-equivalent — we don't have a generic prompt dialog
        // in AresToys, so this command is wired in but currently unused. Inline rename lives in
        // the chrome's hamburger menu; the panel will get its own inline-edit gesture once the
        // list rows learn click-to-edit. Kept as a hook for that follow-up.
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Record.Portal?.SourcePath;
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't open the folder:\n" + ex.Message,
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task MoveUpAsync() => await MoveAsync(-1).ConfigureAwait(true);

    [RelayCommand]
    private async Task MoveDownAsync() => await MoveAsync(+1).ConfigureAwait(true);

    /// <summary>Push the record up (delta=-1) or down (delta=+1) in the persisted list, then
    /// mirror the change in <see cref="WormholesViewModel.Rows"/> so the panel reflects the new
    /// order without a full <see cref="WormholesViewModel.ReloadAsync"/>. Re-ordering only
    /// affects display order — the live windows on the desktop don't move.</summary>
    private async Task MoveAsync(int delta)
    {
        var newIdx = await _store.MoveAsync(Record.Id, delta, CancellationToken.None).ConfigureAwait(true);
        if (newIdx < 0) return;
        _owner.MoveRow(this, newIdx);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var confirm = MessageBox.Show(
            $"Delete wormhole \"{Record.Title}\"?\n\nThe source folder on disk is NOT touched.",
            "AresToys",
            MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;
        await _manager.DeleteAsync(Record.Id, CancellationToken.None).ConfigureAwait(true);
        _owner.Remove(this);
    }

    private void Persist()
    {
        _ = PersistAsync();
        async Task PersistAsync()
        {
            try
            {
                await _store.SaveAsync(Record, CancellationToken.None).ConfigureAwait(true);
                await _manager.ReconcileAsync(Record, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't apply the change:\n" + ex.Message,
                    "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
