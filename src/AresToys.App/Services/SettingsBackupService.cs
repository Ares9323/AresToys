using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Launcher;
using AresToys.Core.Domain;
using AresToys.Storage.Items;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>Reads / writes the entire AresToys settings store as a portable JSON document.
/// Sensitive values (OAuth tokens, credentials marked at write-time) are excluded from
/// exports — those are bound to the local user / machine via DPAPI and wouldn't decrypt on
/// another box anyway. Importing is non-destructive: existing keys are overwritten with the
/// imported values, missing keys are added, but keys present locally and absent from the
/// import file are left alone (so a partial backup doesn't wipe categories the user didn't
/// touch). Pinned items and user-defined categories are also bundled — pinned payloads
/// travel in the clear (base64) on the assumption the user doesn't pin sensitive data,
/// matching how the rest of the clipboard is treated. Tag definitions (v4) travel too, with the
/// tag names of every exported item; unpinned items that carry tags (only possible in
/// categories without retention) are exported in their own <c>TaggedItems</c> list so the
/// tags survive a restore. The version field lets future formats migrate forward.</summary>
public sealed class SettingsBackupService
{
    /// <summary>Backup schema history: v2 pinned items, v3 adds the per-item label, v4 adds tag
    /// definitions, per-item tag names and the <c>TaggedItems</c> list (issue #4). Older files
    /// import fine: every added field is optional.</summary>
    private const int CurrentVersion = 4;
    /// <summary>Cache one configured options instance — analyzer (CA1869) flags allocating a
    /// new one per call as a perf footgun. Indented output stays the default since users open
    /// these files in editors / diff tools. <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    /// disables the default HTML-safe escaping that turns embedded quotes into &quot; — our
    /// values are often nested JSON blobs (launcher state, hotkey config) where that produces
    /// unreadable noise. The "unsafe" tag is about HTML-context XSS, not filesystem use.</summary>
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ISettingsStore _settings;
    private readonly ICategoryStore _categories;
    private readonly IItemStore _items;
    private readonly ITagStore _tags;
    private readonly LauncherStore _launcher;
    private readonly ILogger<SettingsBackupService> _logger;

    public SettingsBackupService(
        ISettingsStore settings,
        ICategoryStore categories,
        IItemStore items,
        ITagStore tags,
        LauncherStore launcher,
        ILogger<SettingsBackupService> logger)
    {
        _settings = settings;
        _categories = categories;
        _items = items;
        _tags = tags;
        _launcher = launcher;
        _logger = logger;
    }

    public async Task ExportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var entry in _settings.EnumerateAsync(includeSensitive: false, cancellationToken)
                                              .ConfigureAwait(false))
        {
            entries[entry.Key] = entry.Value;
        }

        // Skip the seeded 'Clipboard' bucket — every install has it, exporting it just risks
        // overwriting the local user's customised default on import.
        var allCategories = await _categories.ListAsync(cancellationToken).ConfigureAwait(false);
        var customCategories = allCategories
            .Where(c => !string.Equals(c.Name, Category.Default, StringComparison.Ordinal))
            .Select(BackupCategory.From)
            .ToList();

        // Page through pinned rows — passing int.MaxValue as Limit blows up because ItemStore
        // pre-sizes a List<>(capacity) with that value (2B refs). 1000 / page is plenty: pinned
        // counts are tiny by definition (rarely more than a few dozen).
        var allTags = await _tags.ListAsync(cancellationToken).ConfigureAwait(false);
        var tagNames = allTags.ToDictionary(t => t.Id, t => t.Name);
        var pinnedItems = new List<BackupPinnedItem>();
        await foreach (var rec in EnumerateAllAsync(pinnedOnly: true, includePayload: true, cancellationToken).ConfigureAwait(false))
        {
            pinnedItems.Add(BackupPinnedItem.From(rec, tagNames));
        }
        // Unpinned items with tags: a separate list rather than PinnedItems so an older app
        // reading this file doesn't import them as pinned (it ignores the unknown field).
        var taggedItems = new List<BackupPinnedItem>();
        if (allTags.Count > 0)
        {
            await foreach (var rec in EnumerateAllAsync(pinnedOnly: false, includePayload: false, cancellationToken).ConfigureAwait(false))
            {
                if (rec.Pinned || rec.Tags.Count == 0) continue;
                // Second read with the payload only for the (few) rows that qualify, so the
                // sweep doesn't decrypt the whole history.
                var full = await _items.GetByIdAsync(rec.Id, includePayload: true, cancellationToken).ConfigureAwait(false);
                if (full is not null) taggedItems.Add(BackupPinnedItem.From(full, tagNames));
            }
        }

        var doc = new BackupDocument
        {
            Version = CurrentVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Settings = entries,
            Categories = customCategories,
            Tags = allTags.Select(BackupTag.From).ToList(),
            PinnedItems = pinnedItems,
            TaggedItems = taggedItems,
        };
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, doc, ExportOptions, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SettingsBackupService: exported {Settings} settings, {Categories} categories, {Tags} tags, {Pinned} pinned items, {Tagged} tagged items to {Path}",
            entries.Count, customCategories.Count, allTags.Count, pinnedItems.Count, taggedItems.Count, filePath);
    }

    public async Task<ImportResult> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        var doc = await JsonSerializer.DeserializeAsync<BackupDocument>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (doc is null)
        {
            _logger.LogWarning("SettingsBackupService: import failed — empty or malformed file {Path}", filePath);
            return ImportResult.Empty;
        }
        if (doc.Version > CurrentVersion)
        {
            // Forward-compat: a newer version of the app might add fields. We only know how
            // to re-apply the sections we recognise, so do that and warn the user that exotic
            // fields might be ignored. Don't outright refuse — most backups will be plain k/v.
            _logger.LogWarning("SettingsBackupService: import file is version {File}, app understands {App}; importing recognised sections only",
                doc.Version, CurrentVersion);
        }

        var settingsImported = 0;
        var launcherStateTouched = false;
        if (doc.Settings is not null)
        {
            foreach (var (key, value) in doc.Settings)
            {
                // Imported entries land as non-sensitive — sensitive values were never exported in
                // the first place, so any key found in the file is by construction non-sensitive.
                await _settings.SetAsync(key, value, sensitive: false, cancellationToken).ConfigureAwait(false);
                settingsImported++;
                if (key is "launcher.state" or "launcher.cells") launcherStateTouched = true;
            }
        }
        // The launcher pre-warms its in-memory view at app startup and uses a monotonic version
        // counter on LauncherStore to decide whether to reload on the next PrepareAsync. Direct
        // ISettingsStore writes above bypass LauncherStore.SaveAsync, so the counter stays put
        // and the pre-warmed window keeps showing the old (often empty) grid until the user
        // mutates a cell. Bump it explicitly here so the next open sees fresh data.
        if (launcherStateTouched) _launcher.BumpStateVersion();

        var categoriesImported = 0;
        if (doc.Categories is not null)
        {
            foreach (var bc in doc.Categories)
            {
                if (string.IsNullOrWhiteSpace(bc.Name)) continue;
                // Never overwrite the default bucket — it's seeded and shared across installs.
                if (string.Equals(bc.Name, Category.Default, StringComparison.Ordinal)) continue;
                var existing = await _categories.GetAsync(bc.Name, cancellationToken).ConfigureAwait(false);
                var category = new Category(bc.Name, bc.Icon, bc.SortOrder, bc.MaxItems, bc.AutoCleanupAfter);
                if (existing is null)
                    await _categories.AddAsync(category, cancellationToken).ConfigureAwait(false);
                else
                    await _categories.UpdateAsync(category, cancellationToken).ConfigureAwait(false);
                categoriesImported++;
            }
        }

        // Tag definitions first so the per-item names below resolve to them (and keep the
        // file's colours). An existing tag with the same name is reused; its colour is only
        // overwritten when the file carries one, mirroring how categories are updated.
        var tagIdsByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var tagsImported = 0;
        if (doc.Tags is not null)
        {
            foreach (var bt in doc.Tags)
            {
                if (TagRules.NormalizeName(bt.Name) is not { } name) continue;
                var tag = await _tags.GetOrCreateAsync(name, bt.Color, cancellationToken).ConfigureAwait(false);
                var color = TagRules.NormalizeColor(bt.Color);
                if (color is not null && !string.Equals(color, tag.Color, StringComparison.Ordinal))
                    await _tags.UpdateAsync(tag.Id, tag.Name, color, cancellationToken).ConfigureAwait(false);
                tagIdsByName[tag.Name] = tag.Id;
                tagsImported++;
            }
        }

        var pinnedImported = 0;
        var pinnedSkipped = 0;
        var fileItems = new List<(BackupPinnedItem Item, bool Pinned)>();
        if (doc.PinnedItems is not null) fileItems.AddRange(doc.PinnedItems.Select(p => (p, true)));
        if (doc.TaggedItems is not null) fileItems.AddRange(doc.TaggedItems.Select(p => (p, false)));
        if (fileItems.Count > 0)
        {
            // Build the dedup index up-front: for every existing non-deleted item, hash its
            // payload so imported entries with identical (kind, payload) are skipped. We hash
            // the *plaintext* payload exposed by ItemStore (DPAPI decryption already happened),
            // which matches what's in the backup file. Pagination keeps memory bounded even on
            // large libraries — passing int.MaxValue as Limit overflows ItemStore's
            // List<>(capacity) pre-allocation.
            // The index maps to the existing item id so a skipped duplicate still receives the
            // tags the file gives it.
            var existingHashes = new Dictionary<(ItemKind Kind, string Hash), long>();
            await foreach (var rec in EnumerateAllAsync(pinnedOnly: false, includePayload: true, cancellationToken).ConfigureAwait(false))
            {
                if (rec.Payload.IsEmpty) continue;
                existingHashes.TryAdd((rec.Kind, HashPayload(rec.Payload.Span)), rec.Id);
            }

            foreach (var (bp, pinned) in fileItems)
            {
                if (string.IsNullOrEmpty(bp.PayloadBase64)) continue;
                if (!Enum.TryParse<ItemKind>(bp.Kind, ignoreCase: false, out var kind)) continue;
                if (!Enum.TryParse<ItemSource>(bp.Source, ignoreCase: false, out var source)) source = ItemSource.Clipboard;

                byte[] payload;
                try { payload = Convert.FromBase64String(bp.PayloadBase64); }
                catch (FormatException) { continue; }

                var hash = HashPayload(payload);
                if (existingHashes.TryGetValue((kind, hash), out var existingId))
                {
                    await ApplyTagsAsync(existingId, bp.Tags, tagIdsByName, cancellationToken).ConfigureAwait(false);
                    pinnedSkipped++;
                    continue;
                }

                var newItem = new NewItem(
                    Kind: kind,
                    Source: source,
                    CreatedAt: bp.CreatedAt == default ? DateTimeOffset.UtcNow : bp.CreatedAt,
                    Payload: payload,
                    PayloadSize: payload.LongLength,
                    Pinned: pinned,
                    SourceProcess: bp.SourceProcess,
                    SourceWindow: bp.SourceWindow,
                    UploadedUrl: bp.UploadedUrl,
                    UploaderId: bp.UploaderId,
                    SearchText: bp.SearchText,
                    Category: string.IsNullOrEmpty(bp.Category) ? Category.Default : bp.Category,
                    Label: bp.Label);
                var newId = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
                existingHashes[(kind, hash)] = newId;
                await ApplyTagsAsync(newId, bp.Tags, tagIdsByName, cancellationToken).ConfigureAwait(false);
                pinnedImported++;
            }
        }

        _logger.LogInformation("SettingsBackupService: imported {Settings} settings, {Categories} categories, {Tags} tags, {Pinned} items ({Skipped} skipped) from {Path}",
            settingsImported, categoriesImported, tagsImported, pinnedImported, pinnedSkipped, filePath);
        return new ImportResult(settingsImported, categoriesImported, pinnedImported, pinnedSkipped, tagsImported);
    }

    /// <summary>Attach the tag names a backup item lists. A name missing from the file's tag
    /// definitions (hand-edited file) is created on the fly with the neutral colour.</summary>
    private async Task ApplyTagsAsync(long itemId, List<string>? names, Dictionary<string, long> tagIdsByName, CancellationToken ct)
    {
        if (names is not { Count: > 0 }) return;
        foreach (var raw in names)
        {
            if (TagRules.NormalizeName(raw) is not { } name) continue;
            if (!tagIdsByName.TryGetValue(name, out var tagId))
            {
                var created = await _tags.GetOrCreateAsync(name, null, ct).ConfigureAwait(false);
                tagId = created.Id;
                tagIdsByName[created.Name] = tagId;
            }
            await _items.AddTagAsync(itemId, tagId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Pages through ItemStore in 1000-row chunks. ItemStore.ListAsync allocates a
    /// List with capacity == Limit, so passing int.MaxValue triggers an overflow on the
    /// underlying array. Pagination also caps peak memory when payloads are large (images).</summary>
    private async IAsyncEnumerable<ItemRecord> EnumerateAllAsync(
        bool pinnedOnly,
        bool includePayload,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int PageSize = 1000;
        var offset = 0;
        while (true)
        {
            var page = await _items.ListAsync(
                new ItemQuery(
                    Limit: PageSize,
                    Offset: offset,
                    Pinned: pinnedOnly ? true : null,
                    IncludePayload: includePayload,
                    IncludeThumbnail: false),
                cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) yield break;
            foreach (var rec in page) yield return rec;
            if (page.Count < PageSize) yield break;
            offset += PageSize;
        }
    }

    private static string HashPayload(ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        return Convert.ToHexString(hash);
    }

    /// <summary><see cref="PinnedItems"/> / <see cref="PinnedSkipped"/> count every imported
    /// item, unpinned tagged ones included; <see cref="Tags"/> counts the tag definitions.</summary>
    public readonly record struct ImportResult(int Settings, int Categories, int PinnedItems, int PinnedSkipped, int Tags = 0)
    {
        public static ImportResult Empty => new(0, 0, 0, 0);
    }

    private sealed class BackupDocument
    {
        public int Version { get; set; }
        public DateTimeOffset ExportedAt { get; set; }
        public Dictionary<string, string>? Settings { get; set; }
        public List<BackupCategory>? Categories { get; set; }
        /// <summary>Tag definitions. Added in backup schema v4.</summary>
        public List<BackupTag>? Tags { get; set; }
        public List<BackupPinnedItem>? PinnedItems { get; set; }
        /// <summary>Unpinned items that carry at least one tag, imported unpinned. Added in
        /// backup schema v4.</summary>
        public List<BackupPinnedItem>? TaggedItems { get; set; }
    }

    private sealed class BackupTag
    {
        public string Name { get; set; } = string.Empty;
        public string? Color { get; set; }

        public static BackupTag From(Tag t) => new() { Name = t.Name, Color = t.Color };
    }

    private sealed class BackupCategory
    {
        public string Name { get; set; } = string.Empty;
        public string? Icon { get; set; }
        public int SortOrder { get; set; }
        public int MaxItems { get; set; }
        public int AutoCleanupAfter { get; set; }

        public static BackupCategory From(Category c) => new()
        {
            Name = c.Name,
            Icon = c.Icon,
            SortOrder = c.SortOrder,
            MaxItems = c.MaxItems,
            AutoCleanupAfter = c.AutoCleanupAfter,
        };
    }

    private sealed class BackupPinnedItem
    {
        public string Kind { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public string? SourceProcess { get; set; }
        public string? SourceWindow { get; set; }
        public string? UploadedUrl { get; set; }
        public string? UploaderId { get; set; }
        public string? SearchText { get; set; }
        public string? Category { get; set; }
        /// <summary>Optional per-item label (CopyQ "Notes" equivalent). v2 backups omit this
        /// field; <see cref="ImportAsync"/> imports them with a null label and the user can
        /// add one after the fact. Added in backup schema v3.</summary>
        public string? Label { get; set; }
        /// <summary>Names of the tags on the item (names, not ids: ids are local to one
        /// database). Absent in v2 / v3 backups. Added in backup schema v4.</summary>
        public List<string>? Tags { get; set; }
        /// <summary>Raw item payload, base64-encoded. Stored in the clear: pinned items are
        /// assumed not to contain secrets (the user explicitly chose to pin them), and the
        /// alternative — DPAPI ciphertext — wouldn't survive a move to another machine.</summary>
        public string PayloadBase64 { get; set; } = string.Empty;

        public static BackupPinnedItem From(ItemRecord r, IReadOnlyDictionary<long, string> tagNames) => new()
        {
            Kind = r.Kind.ToString(),
            Source = r.Source.ToString(),
            CreatedAt = r.CreatedAt,
            SourceProcess = r.SourceProcess,
            SourceWindow = r.SourceWindow,
            UploadedUrl = r.UploadedUrl,
            UploaderId = r.UploaderId,
            SearchText = r.SearchText,
            Category = r.Category,
            Label = r.Label,
            Tags = r.Tags.Count == 0
                ? null
                : r.Tags.Select(id => tagNames.TryGetValue(id, out var n) ? n : null).OfType<string>().ToList(),
            PayloadBase64 = r.Payload.IsEmpty ? string.Empty : Convert.ToBase64String(r.Payload.Span),
        };
    }
}
