using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.ImageEffects;
using AresToys.ImageEffects;
using AresToys.ImageEffects.Serialization;
using AresToys.Storage.ImageEffects;
using SkiaSharp;

namespace AresToys.App.ViewModels.ImageEffects;

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

    /// <summary>True when the selected entry has a TornEdge-style compass cluster — drives the
    /// "Sides" group's visibility in the property panel without relying on path-traversal
    /// fallback (a binding through <c>SelectedEntry.SideToggles</c> would feed UnsetValue to
    /// the converter when SelectedEntry is null, making the panel render with the last entry's
    /// state). Recomputed whenever SelectedEntry flips.</summary>
    public bool HasSideToggles => SelectedEntry?.SideToggles is not null;

    /// <summary>Localised header rendered above the property grid. Builds the
    /// "Properties — &lt;effect&gt;" or "Properties — (no selection)" string fresh for whatever
    /// language is active when the header re-evaluates.</summary>
    /// <summary>Pull a localised resource and substitute positional args. Centralises the
    /// "ResourceManager + LocalizedStrings.Culture" lookup chain we use everywhere else, so the
    /// status-line callsites stay terse.</summary>
    private static string Loc(string key, params object[] args)
    {
        var culture = Markup.LocalizedStrings.Instance.Culture ?? System.Globalization.CultureInfo.CurrentUICulture;
        var template = AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
        return args.Length == 0
            ? template
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);
    }

    public string PropertiesHeader => SelectedEntry is { } e
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
            AresToys.App.Resources.Strings.ResourceManager.GetString(
                "ImageEffects_PropertiesHeader",
                Markup.LocalizedStrings.Instance.Culture ?? System.Globalization.CultureInfo.CurrentUICulture)
                ?? "Properties — {0}",
            e.DisplayName)
        : AresToys.App.Resources.Strings.ResourceManager.GetString(
              "ImageEffects_PropertiesEmpty",
              Markup.LocalizedStrings.Instance.Culture ?? System.Globalization.CultureInfo.CurrentUICulture)
          ?? "Properties — (no selection)";

    partial void OnSelectedEntryChanged(EffectEntryViewModel? value)
    {
        OnPropertyChanged(nameof(HasSideToggles));
        OnPropertyChanged(nameof(PropertiesHeader));
    }

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
                StatusText = Loc("ImageEffects_StatusRenameOk", preset.Name);
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
            StatusText = Loc("ImageEffects_StatusLoadFail", ex.Message);
        }
        finally
        {
            _loadingFromStore = false;
        }
    }

    /// <summary>Suppress automatic preset persistence — used by the editor handoff so the
    /// user's slider / param tweaks during a "pick effects for THIS screenshot" session don't
    /// silently overwrite the saved preset they're previewing. The editor's "Apply to editor"
    /// button still works (renders the in-memory state), and an explicit "Override preset"
    /// button is shown in that mode for users who DO want to save the changes back.
    /// Setting this is one-way (the host turns it on once when launching the effects window
    /// in editor mode); flipping it off mid-session would re-enable the auto-save which is
    /// confusing UX.</summary>
    public bool SuppressAutoPersist { get; set; }

    private async Task PersistSelectedAsync()
    {
        if (_store is null || SelectedPreset is null) return;
        if (SuppressAutoPersist) return;
        try
        {
            // sortOrder = null → keep current ordering. Reorder is a separate explicit op once
            // we add drag-to-reorder UI.
            await _store.UpsertAsync(SelectedPreset.Preset, sortOrder: null, default).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusSaveFail", ex.Message);
        }
    }

    /// <summary>Force a save of the current preset state — bypasses <see cref="SuppressAutoPersist"/>.
    /// Wired to the "Override preset" button visible only in editor mode; it's the user's
    /// explicit "yes I really do want my edits to overwrite the saved preset" gesture.</summary>
    public async Task PersistSelectedExplicitlyAsync()
    {
        if (_store is null || SelectedPreset is null) return;
        try
        {
            await _store.UpsertAsync(SelectedPreset.Preset, sortOrder: null, default).ConfigureAwait(true);
            StatusText = Loc("ImageEffects_StatusOverrideSaved", SelectedPreset.Preset.Name);
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusSaveFail", ex.Message);
        }
    }

    private void RequestPersist()
    {
        if (_store is null || _loadingFromStore || SuppressAutoPersist) return;
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
            var (json, assetsDir) = await ReadSxiePackageAsync(path).ConfigureAwait(true);
            var preset = _serializer.Deserialize(json)
                ?? throw new InvalidDataException("Couldn't read preset payload.");

            // Patch path-bearing properties in two effects: DrawImage.ImageLocation (single
            // file) and DrawParticles.ImageFolder (a directory of sprite candidates).
            //  1. ShareX-specific token: "%ShareXImageEffects%" expands to the LocalAppData
            //     folder where ShareX stores downloadable effect packs (e.g. macOSBigSur
            //     border PNGs). If the user has ShareX installed the assets are there;
            //     otherwise the file/folder just doesn't exist and the effect no-ops.
            //  2. Standard env vars (%LOCALAPPDATA%, %USERPROFILE%, ...) get expanded too
            //     so a hand-authored preset can use those.
            //  3. Relative paths (no env vars, not rooted) resolve against the assets folder
            //     extracted from the .sxie package.
            foreach (var entry in preset.Effects)
            {
                switch (entry.Effect)
                {
                    case AresToys.ImageEffects.Drawings.DrawImageImageEffect di when !string.IsNullOrEmpty(di.ImageLocation):
                        di.ImageLocation = ExpandShareXAssetPath(di.ImageLocation, assetsDir, fileNotDir: true);
                        break;
                    case AresToys.ImageEffects.Drawings.DrawParticlesImageEffect dp when !string.IsNullOrEmpty(dp.ImageFolder):
                        dp.ImageFolder = ExpandShareXAssetPath(dp.ImageFolder, assetsDir, fileNotDir: false);
                        break;
                }
            }
            if (string.IsNullOrEmpty(preset.Name)) preset.Name = Path.GetFileNameWithoutExtension(path);
            preset.Id = Guid.NewGuid().ToString("N");
            var item = WrapPreset(preset);
            Presets.Add(item);
            SelectedPreset = item;
            if (_store is not null)
                await _store.UpsertAsync(preset, sortOrder: Presets.Count - 1, default).ConfigureAwait(true);
            StatusText = Loc("ImageEffects_StatusImported", preset.Effects.Count, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusImportFail", ex.Message);
        }
    }

    /// <summary>Expand a ShareX-style asset path. Tries, in order:
    ///  1. The literal expanded path (after rewriting <c>%ShareXImageEffects%</c> + env vars)
    ///     — this hits when the user has ShareX installed with the same effect pack.
    ///  2. The .sxie package's own extraction folder, looked up by the last 1–2 segments of
    ///     the original path. ShareX's <c>Packager</c> writes assets under
    ///     <c>&lt;PresetName&gt;\&lt;file.png&gt;</c> inside the ZIP, so a path like
    ///     <c>%ShareXImageEffects%\RTXON\rtx-on.png</c> matches the ZIP entry
    ///     <c>RTXON\rtx-on.png</c> when the .sxie was bundled with its assets.
    ///  3. <c>%LOCALAPPDATA%\AresToys\ImageEffects\&lt;PresetName&gt;\</c> — our own per-user
    ///     pack folder, so users can drop the missing PNGs there without needing ShareX.
    /// Falls back to the literal expanded path (which won't exist) so the effect cleanly no-ops.</summary>
    private static string ExpandShareXAssetPath(string raw, string? assetsDir, bool fileNotDir)
    {
        bool Exists(string p) => fileNotDir ? File.Exists(p) : Directory.Exists(p);

        var location = raw;
        if (location.Contains("%ShareXImageEffects%", StringComparison.OrdinalIgnoreCase))
        {
            var shareXBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShareX", "ImageEffects");
            location = location.Replace("%ShareXImageEffects%", shareXBase, StringComparison.OrdinalIgnoreCase);
        }
        location = Environment.ExpandEnvironmentVariables(location);

        // (1) literal path resolved.
        if (Path.IsPathRooted(location) && Exists(location)) return location;

        // (2) .sxie extraction folder — match by tail (preset\file or just file).
        if (assetsDir is not null)
        {
            // Relative: just join.
            if (!Path.IsPathRooted(location))
            {
                var candidate = Path.Combine(assetsDir, location);
                if (Exists(candidate)) return candidate;
            }
            // Absolute that didn't exist literally — try matching by the trailing two segments,
            // then by just the file/folder name. Example: ShareX path
            // "...\ImageEffects\RTXON\rtx-on.png" → look for "RTXON/rtx-on.png" or "rtx-on.png"
            // inside the package extraction folder.
            var normalized = location.Replace('/', Path.DirectorySeparatorChar);
            var segs = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var take = Math.Min(2, segs.Length); take >= 1; take--)
            {
                var tail = string.Join(Path.DirectorySeparatorChar, segs[^take..]);
                var candidate = Path.Combine(assetsDir, tail);
                if (Exists(candidate)) return candidate;
            }
        }

        // (3) AresToys's own per-user folder, mirroring ShareX's layout. We don't auto-create it
        // here because the user might not want a phantom folder for a no-op preset; they'll
        // see the empty-state message and create it themselves when they drop assets in.
        var sharedQBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AresToys", "ImageEffects");
        if (Path.IsPathRooted(location))
        {
            // Try to pick out "<...>\ImageEffects\<rest>" and re-root onto our base.
            var idx = location.IndexOf(@"ImageEffects" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var rest = location[(idx + ("ImageEffects" + Path.DirectorySeparatorChar).Length)..];
                var candidate = Path.Combine(sharedQBase, rest);
                if (Exists(candidate)) return candidate;
            }
        }

        return location;
    }

    public void ExportPresetTo(string path)
    {
        if (SelectedPreset is null) return;
        try
        {
            var json = _serializer.Serialize(SelectedPreset.Preset);
            File.WriteAllText(path, json);
            StatusText = Loc("ImageEffects_StatusExported", SelectedPreset.Preset.Name, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusExportFail", ex.Message);
        }
    }

    /// <summary>Export the selected preset as a <c>.sxie</c> ZIP package — same shape ShareX
    /// produces from its "Package" button: <c>Config.json</c> at the root + each DrawImage
    /// asset bundled as a sibling entry. Absolute paths in <see cref="DrawImageImageEffect.ImageLocation"/>
    /// are temporarily rewritten to the relative filename inside the archive so the importer
    /// (ours or anyone else's) can resolve them after extraction. The original ImageLocation
    /// is restored on the live preset after serialization so the editor keeps working.</summary>
    public void ExportPresetToSxie(string path)
    {
        if (SelectedPreset is null) return;
        var preset = SelectedPreset.Preset;
        var savedLocations = new List<(AresToys.ImageEffects.Drawings.DrawImageImageEffect Effect, string Original)>();
        var bundles = new List<(string EntryName, string SourcePath)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Walk every DrawImage and stage the bundle list. Rewrite ImageLocation to the
            // relative entry name so the serialized JSON references the asset as "logo.png"
            // rather than "C:\Users\Bob\..." — re-import resolves that back to an absolute
            // path after extraction.
            foreach (var entry in preset.Effects)
            {
                if (entry.Effect is not AresToys.ImageEffects.Drawings.DrawImageImageEffect di) continue;
                var src = di.ImageLocation;
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) continue;

                var baseName = Path.GetFileName(src);
                var entryName = baseName;
                var counter = 1;
                while (!usedNames.Add(entryName))
                {
                    entryName = $"{Path.GetFileNameWithoutExtension(baseName)}_{counter}{Path.GetExtension(baseName)}";
                    counter++;
                }
                bundles.Add((entryName, src));
                savedLocations.Add((di, src));
                di.ImageLocation = entryName;
            }

            var json = _serializer.Serialize(preset);

            using (var fs = File.Create(path))
            using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                var configEntry = archive.CreateEntry("Config.json", System.IO.Compression.CompressionLevel.Optimal);
                using (var es = configEntry.Open())
                using (var sw = new StreamWriter(es, System.Text.Encoding.UTF8))
                    sw.Write(json);

                foreach (var (entryName, srcPath) in bundles)
                {
                    var assetEntry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                    using var es = assetEntry.Open();
                    using var srcStream = File.OpenRead(srcPath);
                    srcStream.CopyTo(es);
                }
            }
            StatusText = Loc("ImageEffects_StatusExported", preset.Name, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusExportFail", ex.Message);
        }
        finally
        {
            // Always restore — the temporary mutation lives only as long as the serialize call.
            // Skipping this would leave the editor pointing at a relative filename that won't
            // resolve outside the .sxie package.
            foreach (var (effect, original) in savedLocations) effect.ImageLocation = original;
        }
    }

    /// <summary>Read the JSON payload out of a <c>.sxie</c> file. Modern ShareX wraps the
    /// preset in a ZIP package (PK magic; root entry is <c>Config.json</c> + optional asset
    /// files like watermark images); legacy <c>.sxie</c> and our own <c>.json</c> exports are
    /// bare JSON. ZIP packages also extract their asset files into a stable per-preset
    /// folder under %LOCALAPPDATA%/AresToys/effect-assets/ so DrawImage steps can resolve
    /// relative <c>ImageLocation</c> values to absolute paths after import.</summary>
    private static async Task<(string Json, string? AssetsDir)> ReadSxiePackageAsync(string path)
    {
        await using (var stream = File.OpenRead(path))
        {
            var header = new byte[2];
            var read = await stream.ReadAsync(header.AsMemory(0, 2)).ConfigureAwait(false);
            // ZIP magic bytes "PK" — every modern .sxie pushed by ShareX's packager.
            if (read == 2 && header[0] == 0x50 && header[1] == 0x4B)
            {
                stream.Position = 0;
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
                // ExtractToFile is an extension method in System.IO.Compression.ZipFileExtensions;
                // we'll use entry.Open() + manual copy below to avoid leaning on the using.

                // Extract every entry into a stable per-package folder. The folder name is
                // derived from the .sxie file name + a short hash of the path so reimporting
                // the same file overwrites the previous extraction instead of leaking copies.
                var packageName = SafeName(Path.GetFileNameWithoutExtension(path));
                var hash = Math.Abs(StringComparer.Ordinal.GetHashCode(path));
                var assetsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AresToys", "effect-assets", $"{packageName}-{hash:x8}");
                Directory.CreateDirectory(assetsDir);

                string? json = null;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // skip directory entries
                    var dest = Path.Combine(assetsDir, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using (var entryStream = entry.Open())
                    using (var outStream = File.Create(dest))
                    {
                        await entryStream.CopyToAsync(outStream).ConfigureAwait(false);
                    }
                    if (json is null && entry.FullName.Equals("Config.json", StringComparison.OrdinalIgnoreCase))
                        json = await File.ReadAllTextAsync(dest).ConfigureAwait(false);
                }
                json ??= archive.Entries
                    .FirstOrDefault(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) is { } fallback
                    ? await File.ReadAllTextAsync(Path.Combine(assetsDir, fallback.FullName)).ConfigureAwait(false)
                    : throw new InvalidDataException("Sxie package contains no Config.json entry.");
                return (json, assetsDir);
            }
        }
        // Bare JSON — no zip, no assets to extract.
        return (await File.ReadAllTextAsync(path).ConfigureAwait(false), null);
    }

    private static string SafeName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public void LoadPreviewImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var loaded = SKBitmap.Decode(stream);
            if (loaded is null)
            {
                StatusText = Loc("ImageEffects_StatusDecodeFail");
                return;
            }
            _sampleImage.Dispose();
            _sampleImage = loaded;
            StatusText = Loc("ImageEffects_StatusLoadedSample", Path.GetFileName(path));
            RequestRender();
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusLoadFailSample", ex.Message);
        }
    }

    /// <summary>Replace the in-memory source bitmap with raw PNG bytes — used by the editor's
    /// "Effects" tool to feed the actual screenshot into this VM instead of the placeholder
    /// sample image. Failures fall through to the existing sample (status text logged) so the
    /// window stays usable rather than rendering blank.</summary>
    public void LoadSourceFromBytes(byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return;
        try
        {
            using var stream = new MemoryStream(pngBytes);
            var loaded = SKBitmap.Decode(stream);
            if (loaded is null)
            {
                StatusText = Loc("ImageEffects_StatusDecodeFail");
                return;
            }
            _sampleImage.Dispose();
            _sampleImage = loaded;
            RequestRender();
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusLoadFailSample", ex.Message);
        }
    }

    /// <summary>Apply the currently-selected preset to the source bitmap and return the result
    /// as PNG bytes. Used by the editor handoff: the user picks effects in the live preview,
    /// clicks "Apply", we encode the rendered output and the editor swaps it in as its new
    /// source via an undoable command. Returns null when there's no selected preset (caller
    /// should fall back to the original input bytes).</summary>
    public byte[]? RenderCurrentToPng()
    {
        try
        {
            var preset = SelectedPreset?.Preset;
            using var output = preset is null ? _sampleImage.Copy() : preset.Apply(_sampleImage);
            using var image = SkiaSharp.SKImage.FromBitmap(output);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusRenderFail", ex.Message);
            return null;
        }
    }

    public void RebuildSampleImage()
    {
        var fresh = SampleImageGenerator.Build();
        _sampleImage.Dispose();
        _sampleImage = fresh;
        StatusText = Loc("ImageEffects_StatusSampleReset");
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
            StatusText = Loc("ImageEffects_StatusRendered",
                _sampleImage.Width,
                _sampleImage.Height,
                preset.Effects.Count(e => e.Enabled));
        }
        catch (Exception ex)
        {
            StatusText = Loc("ImageEffects_StatusRenderFail", ex.Message);
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
