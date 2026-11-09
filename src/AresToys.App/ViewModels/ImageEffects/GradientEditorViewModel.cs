using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.ImageEffects;
using AresToys.ImageEffects.Drawing;
using SkiaSharp;
// Disambiguate from System.Windows.Media.GradientStop pulled in via System.Windows.Media.
using GradientStop = AresToys.ImageEffects.Drawing.GradientStop;

namespace AresToys.App.ViewModels.ImageEffects;

/// <summary>Edits a copy of a <see cref="GradientInfo"/>. The dialog operates on
/// <see cref="WorkingCopy"/> so cancelling discards the user's changes; OK pushes
/// <see cref="WorkingCopy"/> back into the source via the host.</summary>
public sealed partial class GradientEditorViewModel : ObservableObject
{
    public GradientInfo WorkingCopy { get; }
    public ObservableCollection<GradientStopViewModel> Stops { get; } = new();
    public IReadOnlyList<GradientPresetItemViewModel> Presets { get; } =
        GradientPresets.All.Select(p => new GradientPresetItemViewModel(p)).ToList();

    [ObservableProperty]
    private LinearGradientMode _direction;

    [ObservableProperty]
    private GradientStopViewModel? _selectedStop;

    [ObservableProperty]
    private Brush _previewBrush = Brushes.Transparent;

    public GradientEditorViewModel(GradientInfo source)
    {
        WorkingCopy = Clone(source);
        _direction = WorkingCopy.Type;
        foreach (var stop in WorkingCopy.Colors) AppendStop(stop);
        SelectedStop = Stops.FirstOrDefault();
        UpdatePreview();
    }

    private static GradientInfo Clone(GradientInfo source) => new(source.Type,
        source.Colors.Select(s => new GradientStop(s.Color, s.Location)).ToArray());

    private void AppendStop(GradientStop stop)
    {
        var vm = new GradientStopViewModel(stop) { Changed = UpdatePreview };
        Stops.Add(vm);
    }

    partial void OnDirectionChanged(LinearGradientMode value)
    {
        WorkingCopy.Type = value;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        // The preview brush mirrors the WorkingCopy state — recomputed on every stop tweak
        // or direction change so the rectangle on the right of the editor stays current.
        PreviewBrush = EffectParameterViewModel.ToWpfBrush(WorkingCopy);
    }

    [RelayCommand]
    private void AddStop()
    {
        // New stops drop midway between the selected stop and its neighbour (or at 50% when
        // none is selected). Colour copies from the selection so the addition is visually
        // unobtrusive — the user typically tweaks the new colour right after.
        var location = SelectedStop is null ? 50f : Math.Clamp(SelectedStop.Location + 10f, 0f, 100f);
        var color = SelectedStop?.ColorValue ?? new SKColor(128, 128, 128);
        var newStop = new GradientStop(color, location);
        WorkingCopy.Colors.Add(newStop);
        AppendStop(newStop);
        SelectedStop = Stops[^1];
        UpdatePreview();
    }

    [RelayCommand]
    private void RemoveStop()
    {
        if (SelectedStop is null || Stops.Count <= 2) return; // need at least two stops
        var idx = Stops.IndexOf(SelectedStop);
        WorkingCopy.Colors.Remove(SelectedStop.Stop);
        Stops.RemoveAt(idx);
        SelectedStop = Stops.Count == 0 ? null : Stops[Math.Min(idx, Stops.Count - 1)];
        UpdatePreview();
    }

    [RelayCommand]
    private void DistributeEvenly()
    {
        // Re-spaces every stop linearly across 0..100 — useful after adding several stops
        // so they aren't all clumped together.
        if (Stops.Count <= 1) return;
        var step = 100f / (Stops.Count - 1);
        for (var i = 0; i < Stops.Count; i++) Stops[i].Location = i * step;
        UpdatePreview();
    }

    [RelayCommand]
    private void Reverse()
    {
        // Mirror locations 0↔100 — flips the direction visually without changing the Type
        // (e.g. swap "blue → red" for "red → blue" while keeping vertical orientation).
        foreach (var stop in Stops) stop.Location = 100f - stop.Location;
        UpdatePreview();
    }

    public void LoadPreset(GradientPresetItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var preset = item.Preset;
        WorkingCopy.Type = preset.Gradient.Type;
        Direction = preset.Gradient.Type;
        WorkingCopy.Colors.Clear();
        Stops.Clear();
        foreach (var s in preset.Gradient.Colors)
        {
            var copy = new GradientStop(s.Color, s.Location);
            WorkingCopy.Colors.Add(copy);
            AppendStop(copy);
        }
        SelectedStop = Stops.FirstOrDefault();
        UpdatePreview();
    }
}
