using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.App.Services;
using AresToys.App.Services.Launcher;
using AresToys.Core.Domain;
using AresToys.Storage.Database;
using AresToys.Storage.Database.Migrations;
using AresToys.Storage.Items;
using AresToys.Storage.Options;
using AresToys.Storage.Paths;
using AresToys.Storage.Protection;
using AresToys.Storage.Settings;
using Xunit;

namespace AresToys.App.Tests;

/// <summary>Backup schema v4 (issue #4): tag definitions and per-item tags survive an export /
/// import round trip, unpinned tagged items come back unpinned, and v3 (label-only) files still
/// import.</summary>
public sealed class SettingsBackupTagsTests
{
    /// <summary>One throwaway database with the real stores on top. Deletes its folder on dispose.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly string _root;
        public AresToysDatabase Database { get; }
        public IItemStore Items { get; private set; } = null!;
        public ITagStore Tags { get; private set; } = null!;
        public SettingsBackupService Backup { get; private set; } = null!;

        public Env(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            var paths = new StoragePathResolver(Microsoft.Extensions.Options.Options.Create(new StorageOptions { RootDirectoryOverride = root }));
            var migrations = new IMigration[]
            {
                new Migration001InitialSchema(), new Migration002AddItemLabel(), new Migration003AddPinSortOrder(),
                new Migration004AddItemTrigger(), new Migration005AddTags(),
            };
            Database = new AresToysDatabase(paths, new MigrationRunner(migrations), NullLogger<AresToysDatabase>.Instance);
        }

        public static async Task<Env> CreateAsync(string scratch)
        {
            var env = new Env(Path.Combine(scratch, Guid.NewGuid().ToString("N")));
            await env.Database.InitializeAsync(CancellationToken.None);
            var protector = new DpapiPayloadProtector();
            env.Items = new ItemStore(env.Database, new ItemSerializer(protector));
            env.Tags = new SqliteTagStore(env.Database);
            var settings = new SqliteSettingsStore(env.Database, protector);
            env.Backup = new SettingsBackupService(settings, new SqliteCategoryStore(env.Database), env.Items, env.Tags,
                new LauncherStore(settings), NullLogger<SettingsBackupService>.Instance);
            return env;
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* lingering handle, best effort */ }
        }
    }

    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "AresToys.Tests", "backup-tags");

    private static NewItem TextItem(string text, bool pinned, string? label = null)
        => new(ItemKind.Text, ItemSource.Clipboard, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes(text),
            Encoding.UTF8.GetByteCount(text), Pinned: pinned, SearchText: text, Label: label);

    private static async Task<List<ItemRecord>> AllItemsAsync(IItemStore items)
        => [.. await items.ListAsync(new ItemQuery(Limit: 100), CancellationToken.None)];

    private static async Task<string[]> TagNamesAsync(ITagStore tags, ItemRecord record)
    {
        var all = (await tags.ListAsync(CancellationToken.None)).ToDictionary(t => t.Id, t => t.Name);
        return [.. record.Tags.Select(id => all[id]).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task ExportImport_RoundTripsTagDefinitionsAndItemTags()
    {
        var file = Path.Combine(Scratch, Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Scratch);
        try
        {
            await using (var source = await Env.CreateAsync(Scratch))
            {
                var licenses = await source.Tags.GetOrCreateAsync("licenses", "#FF0000", CancellationToken.None);
                var regex = await source.Tags.GetOrCreateAsync("regex", null, CancellationToken.None);
                var pinned = await source.Items.AddAsync(TextItem("MIT text", pinned: true, label: "MIT"), CancellationToken.None);
                var tagged = await source.Items.AddAsync(TextItem("^\\d+$", pinned: false), CancellationToken.None);
                await source.Items.AddAsync(TextItem("plain history", pinned: false), CancellationToken.None);
                await source.Items.AddTagAsync(pinned, licenses.Id, CancellationToken.None);
                await source.Items.AddTagAsync(pinned, regex.Id, CancellationToken.None);
                await source.Items.AddTagAsync(tagged, regex.Id, CancellationToken.None);

                await source.Backup.ExportAsync(file);
            }

            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(file)))
            {
                Assert.Equal(4, json.RootElement.GetProperty("Version").GetInt32());
                Assert.Equal(2, json.RootElement.GetProperty("Tags").GetArrayLength());
                Assert.Equal(1, json.RootElement.GetProperty("TaggedItems").GetArrayLength());
            }

            await using var target = await Env.CreateAsync(Scratch);
            var result = await target.Backup.ImportAsync(file);

            Assert.Equal(2, result.Tags);
            Assert.Equal(2, result.PinnedItems);
            var tags = await target.Tags.ListAsync(CancellationToken.None);
            Assert.Equal("#FF0000", tags.Single(t => t.Name == "licenses").Color);
            Assert.Null(tags.Single(t => t.Name == "regex").Color);

            var items = await AllItemsAsync(target.Items);
            // The untagged unpinned item was never exported.
            Assert.Equal(2, items.Count);
            var mit = items.Single(i => i.Label == "MIT");
            Assert.True(mit.Pinned);
            Assert.Equal(new[] { "licenses", "regex" }, await TagNamesAsync(target.Tags, mit));
            var re = items.Single(i => i.SearchText == "^\\d+$");
            Assert.False(re.Pinned);
            Assert.Equal(new[] { "regex" }, await TagNamesAsync(target.Tags, re));
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Import_V3LabelOnlyBackup_StillWorks()
    {
        var file = Path.Combine(Scratch, Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Scratch);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("legacy pinned"));
        await File.WriteAllTextAsync(file, $$"""
            {
              "Version": 3,
              "ExportedAt": "2026-01-01T00:00:00+00:00",
              "Settings": {},
              "Categories": [],
              "PinnedItems": [
                { "Kind": "Text", "Source": "Clipboard", "CreatedAt": "2026-01-01T00:00:00+00:00",
                  "SearchText": "legacy pinned", "Category": "Clipboard", "Label": "Old", "PayloadBase64": "{{payload}}" }
              ]
            }
            """);
        try
        {
            await using var target = await Env.CreateAsync(Scratch);
            var result = await target.Backup.ImportAsync(file);

            Assert.Equal(1, result.PinnedItems);
            Assert.Equal(0, result.Tags);
            var item = Assert.Single(await AllItemsAsync(target.Items));
            Assert.True(item.Pinned);
            Assert.Equal("Old", item.Label);
            Assert.Empty(item.Tags);
            Assert.Empty(await target.Tags.ListAsync(CancellationToken.None));
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Import_DuplicateItem_ReceivesTheFileTags()
    {
        var file = Path.Combine(Scratch, Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Scratch);
        try
        {
            await using (var source = await Env.CreateAsync(Scratch))
            {
                var tag = await source.Tags.GetOrCreateAsync("boilerplate", "#00FF00", CancellationToken.None);
                var id = await source.Items.AddAsync(TextItem("shared snippet", pinned: true), CancellationToken.None);
                await source.Items.AddTagAsync(id, tag.Id, CancellationToken.None);
                await source.Backup.ExportAsync(file);
            }

            await using var target = await Env.CreateAsync(Scratch);
            // Same content already present locally, without tags.
            await target.Items.AddAsync(TextItem("shared snippet", pinned: true), CancellationToken.None);

            var result = await target.Backup.ImportAsync(file);

            Assert.Equal(0, result.PinnedItems);
            Assert.Equal(1, result.PinnedSkipped);
            var item = Assert.Single(await AllItemsAsync(target.Items));
            Assert.Equal(new[] { "boilerplate" }, await TagNamesAsync(target.Tags, item));
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { /* best effort */ }
        }
    }
}
