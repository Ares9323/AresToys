using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShareQ.App.Services.ImageEffects;
using ShareQ.ImageEffects;
using ShareQ.ImageEffects.Serialization;
using ShareQ.Storage.ImageEffects;
using SkiaSharp;

namespace ShareQ.App.ViewModels.ImageEffects;

/// <summary>Drives the entire <c>ImageEffectsWindow</c>. The viewmodel owns a sample image,
/// the in-memory preset list (wrapped in <see cref="PresetItemViewModel"/> for inline-rename
/// support), and a debounced preview-render pipeline. Changes are persisted to the SQLite
/// store via the optional <see cref="IImageEffectPresetStore"/>.</summary>
public sealed partial class ImageEffectsViewModel : ObservableObject, IDisposable
{
    private readonly ImageEffectRegistry _registry;
    private readonly EffectPresetSerializer _serializer;
    private readonly IImageEffectPresetStore? _store;
    private readonly DispatcherTimer _renderDebounce;
    private readonly DispatcherTimer _persistDebounce;
    private SKBitmap _sampleImage;
    private bool _disposed;
    private bool _loadingFromStore;

    public ObservableCollection<PresetItemViewModel> Presets { get; } = new();

    [ObservableProperty]
    private PresetItemViewModel? _selectedPreset;

    public ObservableCollection<EffectEntryViewModel> EffectEntries { get; } = new();

    [ObservableProperty]
    private EffectEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private BitmapImage? _previewImage;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isLivePreview;

    /// <summary>Toggling live preview swaps the render-debounce interval. 30 ms gives ~30 fps
    /// during a slider drag so the user sees the chain re-render in motion; 150 ms (the
    /// default) waits for the drag to settle, which keeps CPU near zero on heavy chains.</summary>
    partial void OnIsLivePreviewChanged(bool value)
    {
        _renderDebounce.Interval = TimeSpan.FromMilliseconds(value ? 30 : 150);
    }

    public IReadOnlyList<ImageEffectDescriptor> AvailableEffects { get; }

    public ImageEffectsViewModel(ImageEffectRegistry? registry = null, IImageEffectPresetStore? store = null)
    {
        _registry = registry ?? ImageEffectRegistry.Default;
        _serializer = new EffectPresetSerializer(_registry);
        _store = store;
        _sampleImage = SampleImageGenerator.Build();
        AvailableEffects = _registry.All.OrderBy(d => d.Category).ThenBy(d => d.Name).ToList();

        _renderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _renderDebounce.Tick += (_, _) =>
        {
            _renderDebounce.Stop();
            RenderPreview();
        };

        // Persist debounce: a slider sweep produces dozens of property changes; flushing a
        // SQLite UPSERT for each is wasteful (and causes WAL churn). 600 ms after the user
        // stops touching anything, the current preset gets serialised once.
        _persistDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _persistDebounce.Tick += async (_, _) =>
        {
            _persistDebounce.Stop();
            await PersistSelectedAsync().ConfigureAwait(true);
        };

        if (_store is not null)
        {
            _ = LoadFromStoreAsync();
        }
        else
        {
            var seed = new EffectPreset { Name = "New preset" };
            Presets.Add(WrapPreset(seed));
            SelectedPreset = Presets[0];
        }
    }

    private PresetItemViewModel WrapPreset(EffectPreset preset)
    {
        return new PresetItemViewModel(preset)
        {
            // Renamed = persist + status. The actual SQLite write goes through the regular
            // RequestPersist debounce so a quick rename + slider drag share one round-trip.
            Renamed = () =>
            {
                StatusText = $"Renamed to '{preset.Name}'.";
                RequestPersist();
            },
        };
    }

    private async Task LoadFromStoreAsync()
    {
        if (_store is null) return;
        _loadingFromStore = true;
        try
        {
            var loaded = await _store.ListAsync(default).ConfigureAwait(true);
            Presets.Clear();
            foreach (var p in loaded) Presets.Add(WrapPreset(p));
            if (Presets.Count == 0)
            {
                var seed = new EffectPreset { Name = "New preset" };
                var item = WrapPreset(seed);
                Presets.Add(item);
                SelectedPreset = item;
                await _store.UpsertAsync(seed, sortOrder: 0, default).ConfigureAwait(true);
            }
            else
            {
                SelectedPreset = Presets[0];
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load presets: {ex.Message}";
        }
        finally
        {
            _loadingFromStore = false;
        }
    }

    private async Task PersistSelectedAsync()
    {
        if (_store is null || SelectedPreset is null) return;
        try
        {
            // sortOrder = null → keep current ordering. Reorder is a separate explicit op once
            // we add drag-to-reorder UI.
            await _store.UpsertAsync(SelectedPreset.Preset, sortOrder: null, default).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    private void RequestPersist()
    {
        if (_store is null || _loadingFromStore) return;
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    partial void OnSelectedPresetChanged(PresetItemViewModel? value)
    {
        EffectEntries.Clear();
        if (value is not null)
        {
            foreach (var entry in value.Preset.Effects)
            {
                EffectEntries.Add(BindEntry(entry));
            }
        }
        SelectedEntry = EffectEntries.FirstOrDefault();
        RequestRender();
    }

    private EffectEntryViewModel BindEntry(EffectPresetEntry entry)
    {
        var vm = new EffectEntryViewModel(entry)
        {
            Changed = () => { RequestRender(); RequestPersist(); },
        };
        return vm;
    }

    [RelayCommand]
    private async Task AddPresetAsync()
    {
        var preset = new EffectPreset { Name = $"Preset {Presets.Count + 1}" };
        var item = WrapPreset(preset);
        Presets.Add(item);
        SelectedPreset = item;
        if (_store is not null)
            await _store.UpsertAsync(preset, sortOrder: Presets.Count - 1, default).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemovePresetAsync()
    {
        if (SelectedPreset is null) return;
        var removedId = SelectedPreset.Id;
        var idx = Presets.IndexOf(SelectedPreset);
        Presets.Remove(SelectedPreset);
        SelectedPreset = Presets.Count == 0
            ? null
            : Presets[Math.Min(idx, Presets.Count - 1)];
        if (_store is not null) await _store.DeleteAsync(removedId, default).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DuplicatePresetAsync()
    {
        if (SelectedPreset is null) return;
        // Round-trip through JSON for a deep clone — the preset graph holds live ImageEffect
        // instances with mutable state (gamma cache table, etc.) that we don't want to share
        // across the original and its copy.
        var json = _serializer.Serialize(SelectedPreset.Preset);
        var copy = _serializer.Deserialize(json);
        if (copy is null) return;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = SelectedPreset.Preset.Name + " (copy)";
        var item = WrapPreset(copy);
        Presets.Add(item);
        SelectedPreset = item;
        if (_store is not null)
            await _store.UpsertAsync(copy, sortOrder: Presets.Count - 1, default).ConfigureAwait(true);
    }

    /// <summary>Flip the selected preset into edit mode. Bound to the toolbar pencil button
    /// and the F2 key binding on the ListBox.</summary>
    [RelayCommand]
    private void BeginRenameSelected()
    {
        SelectedPreset?.BeginEdit();
    }

    public void AddEffect(string id)
    {
        if (SelectedPreset is null) return;
        var effect = _registry.Create(id);
        if (effect is null) return;
        var entry = new EffectPresetEntry(effect);
        SelectedPreset.Preset.Effects.Add(entry);
        var vm = BindEntry(entry);
        EffectEntries.Add(vm);
        SelectedEntry = vm;
        RequestRender();
        RequestPersist();
    }

    [RelayCommand]
    private void MoveEffectUp()
    {
        MoveSelectedEffect(-1);
    }

    [RelayCommand]
    private void MoveEffectDown()
    {
        MoveSelectedEffect(+1);
    }

    /// <summary>Swap the currently-selected effect with its neighbour. Order is significant
    /// (Brightness then Grayscale ≠ Grayscale then Brightness), so the picker UI exposes
    /// arrow buttons. Both the underlying preset and the bound EffectEntries collection are
    /// kept in sync — the latter drives the ListBox display, the former is what gets
    /// serialised on persist.</summary>
    private void MoveSelectedEffect(int direction)
    {
        if (SelectedPreset is null || SelectedEntry is null) return;
        var idx = EffectEntries.IndexOf(SelectedEntry);
        var newIdx = idx + direction;
        if (newIdx < 0 || newIdx >= EffectEntries.Count) return;

        EffectEntries.Move(idx, newIdx);
        SelectedPreset.Preset.Effects.RemoveAt(idx);
        SelectedPreset.Preset.Effects.Insert(newIdx, SelectedEntry.Entry);

        RequestRender();
        RequestPersist();
    }

    [RelayCommand]
    private void RemoveSelectedEffect()
    {
        if (SelectedPreset is null || SelectedEntry is null) return;
        var idx = EffectEntries.IndexOf(SelectedEntry);
        SelectedPreset.Preset.Effects.Remove(SelectedEntry.Entry);
        EffectEntries.Remove(SelectedEntry);
        SelectedEntry = EffectEntries.Count == 0
            ? null
            : EffectEntries[Math.Min(idx, EffectEntries.Count - 1)];
        RequestRender();
        RequestPersist();
    }

    [RelayCommand]
    private void ClearEffects()
    {
        if (SelectedPreset is null) return;
        SelectedPreset.Preset.Effects.Clear();
        EffectEntries.Clear();
        SelectedEntry = null;
        RequestRender();
        RequestPersist();
    }

    public async Task ImportSxieFileAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            var preset = SxiePresetImporter.Import(json, _registry);
            if (string.IsNullOrEmpty(preset.Name)) preset.Name = Path.GetFileNameWithoutExtension(path);
            preset.Id = Guid.NewGuid().ToString("N");
            var item = WrapPreset(preset);
            Presets.Add(item);
            SelectedPreset = item;
            if (_store is not null)
                await _store.UpsertAsync(preset, sortOrder: Presets.Count - 1, default).ConfigureAwait(true);
            StatusText = $"Imported {preset.Effects.Count} effect(s) from {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    public void ExportPresetTo(string path)
    {
        if (SelectedPreset is null) return;
        try
        {
            var json = _serializer.Serialize(SelectedPreset.Preset);
            File.WriteAllText(path, json);
            StatusText = $"Exported '{SelectedPreset.Preset.Name}' to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    public void LoadPreviewImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var loaded = SKBitmap.Decode(stream);
            if (loaded is null)
            {
                StatusText = "Couldn't decode image.";
                return;
            }
            _sampleImage.Dispose();
            _sampleImage = loaded;
            StatusText = $"Loaded {Path.GetFileName(path)} as preview source.";
            RequestRender();
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    public void RebuildSampleImage()
    {
        var fresh = SampleImageGenerator.Build();
        _sampleImage.Dispose();
        _sampleImage = fresh;
        StatusText = "Sample image reset.";
        RequestRender();
    }

    public void RequestRender()
    {
        // Reset on every change → the actual render fires once, 150 ms after the user stops
        // dragging a slider. Cheap effects render in a few ms, but back-to-back invocations
        // during a slider sweep at 60 Hz would still pile up if we rendered immediately.
        _renderDebounce.Stop();
        _renderDebounce.Start();
    }

    private void RenderPreview()
    {
        try
        {
            var preset = SelectedPreset?.Preset;
            if (preset is null)
            {
                PreviewImage = SkiaToWpfBitmap.Convert(_sampleImage);
                return;
            }
            using var output = preset.Apply(_sampleImage);
            PreviewImage = SkiaToWpfBitmap.Convert(output);
            StatusText = $"Rendered preview ({_sampleImage.Width}×{_sampleImage.Height}, {preset.Effects.Count(e => e.Enabled) } effect(s))";
        }
        catch (Exception ex)
        {
            StatusText = $"Render failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Flush any pending preset save synchronously — the user expects "close window =
        // changes saved" semantics, even if a slider sweep was the last thing they touched.
        if (_persistDebounce.IsEnabled)
        {
            _persistDebounce.Stop();
            try { PersistSelectedAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
        }
        _renderDebounce.Stop();
        _sampleImage.Dispose();
    }
}
