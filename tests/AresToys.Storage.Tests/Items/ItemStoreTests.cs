using System.Text;
using AresToys.Core.Domain;
using AresToys.Storage.Items;
using AresToys.Storage.Protection;
using AresToys.Storage.Tests.Fixtures;
using Xunit;

namespace AresToys.Storage.Tests.Items;

public class ItemStoreTests
{
    private static IItemStore CreateStore(TempDatabaseFixture fx)
        => new ItemStore(fx.Database, new ItemSerializer(new DpapiPayloadProtector()));

    private static NewItem TextItem(string text, bool pinned = false)
        => new(
            Kind: ItemKind.Text,
            Source: ItemSource.Clipboard,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: Encoding.UTF8.GetBytes(text),
            PayloadSize: Encoding.UTF8.GetByteCount(text),
            Pinned: pinned,
            SearchText: text);

    [Fact]
    public async Task Add_Then_GetById_RoundTripsPayloadAndMetadata()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var id = await store.AddAsync(TextItem("hello"), CancellationToken.None);
        var loaded = await store.GetByIdAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(ItemKind.Text, loaded!.Kind);
        Assert.Equal(ItemSource.Clipboard, loaded.Source);
        Assert.Equal("hello", Encoding.UTF8.GetString(loaded.Payload.Span));
        Assert.Equal("hello", loaded.SearchText);
    }

    [Fact]
    public async Task List_OrdersByCreatedAtDescending()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var older = await store.AddAsync(TextItem("older") with { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10) }, CancellationToken.None);
        var newer = await store.AddAsync(TextItem("newer") with { CreatedAt = DateTimeOffset.UtcNow }, CancellationToken.None);

        var list = await store.ListAsync(new ItemQuery(Limit: 10), CancellationToken.None);

        Assert.Equal(new[] { newer, older }, list.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task List_FiltersByKind()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        await store.AddAsync(TextItem("a text"), CancellationToken.None);
        await store.AddAsync(TextItem("another text") with { Kind = ItemKind.Image }, CancellationToken.None);

        var textOnly = await store.ListAsync(new ItemQuery(Kind: ItemKind.Text), CancellationToken.None);

        Assert.Single(textOnly);
        Assert.Equal(ItemKind.Text, textOnly[0].Kind);
    }

    [Fact]
    public async Task SetPinned_PersistsTrueAndFalse()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var id = await store.AddAsync(TextItem("p"), CancellationToken.None);
        Assert.True(await store.SetPinnedAsync(id, true, CancellationToken.None));
        Assert.True((await store.GetByIdAsync(id, CancellationToken.None))!.Pinned);

        Assert.True(await store.SetPinnedAsync(id, false, CancellationToken.None));
        Assert.False((await store.GetByIdAsync(id, CancellationToken.None))!.Pinned);
    }

    [Fact]
    public async Task SoftDelete_HidesByDefault_AndIsListableWithIncludeDeleted()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var id = await store.AddAsync(TextItem("victim"), CancellationToken.None);
        await store.SoftDeleteAsync(id, CancellationToken.None);

        var visible = await store.ListAsync(new ItemQuery(), CancellationToken.None);
        Assert.Empty(visible);

        var includingDeleted = await store.ListAsync(new ItemQuery(IncludeDeleted: true), CancellationToken.None);
        Assert.Single(includingDeleted);
        Assert.NotNull(includingDeleted[0].DeletedAt);
    }

    [Fact]
    public async Task SetUploadedUrl_PopulatesUrlAndUploaderId()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var id = await store.AddAsync(TextItem("img placeholder"), CancellationToken.None);
        await store.SetUploadedUrlAsync(id, uploaderId: "imgur", url: "https://i.imgur.com/abc.png", CancellationToken.None);

        var loaded = (await store.GetByIdAsync(id, CancellationToken.None))!;
        Assert.Equal("imgur", loaded.UploaderId);
        Assert.Equal("https://i.imgur.com/abc.png", loaded.UploadedUrl);
    }

    [Fact]
    public async Task List_WithSearch_ReturnsFtsMatches()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        await store.AddAsync(TextItem("the quick brown fox"), CancellationToken.None);
        await store.AddAsync(TextItem("a slow purple cat"), CancellationToken.None);
        await store.AddAsync(TextItem("brown bear in the woods"), CancellationToken.None);

        var matches = await store.ListAsync(new ItemQuery(Search: "brown"), CancellationToken.None);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public async Task List_WithSearch_IsDiacriticInsensitive()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        await store.AddAsync(TextItem("naïve résumé"), CancellationToken.None);

        var matches = await store.ListAsync(new ItemQuery(Search: "naive"), CancellationToken.None);

        Assert.Single(matches);
    }

    [Fact]
    public async Task List_WithSearch_ExcludesSoftDeleted()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var id = await store.AddAsync(TextItem("hidden treasure"), CancellationToken.None);
        await store.SoftDeleteAsync(id, CancellationToken.None);

        var matches = await store.ListAsync(new ItemQuery(Search: "treasure"), CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task HardDeleteOlderThan_RemovesOnlySoftDeletedNonPinnedItemsBeforeCutoff()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var pinnedAndSoftDeleted = await store.AddAsync(TextItem("pin", pinned: true), CancellationToken.None);
        await store.SoftDeleteAsync(pinnedAndSoftDeleted, CancellationToken.None);

        var ordinary = await store.AddAsync(TextItem("ord"), CancellationToken.None);
        await store.SoftDeleteAsync(ordinary, CancellationToken.None);

        var alive = await store.AddAsync(TextItem("alive"), CancellationToken.None);

        var deletedCount = await store.HardDeleteOlderThanAsync(DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);

        Assert.Equal(1, deletedCount);
        Assert.NotNull(await store.GetByIdAsync(pinnedAndSoftDeleted, CancellationToken.None));
        Assert.Null(await store.GetByIdAsync(ordinary, CancellationToken.None));
        Assert.NotNull(await store.GetByIdAsync(alive, CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePayloadAsync_Existing_OverwritesContent()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var id = await store.AddAsync(TextItem("original"), CancellationToken.None);

        var newBytes = System.Text.Encoding.UTF8.GetBytes("rewritten");
        Assert.True(await store.UpdatePayloadAsync(id, newBytes, newBytes.LongLength, newKind: null, CancellationToken.None));

        var loaded = (await store.GetByIdAsync(id, CancellationToken.None))!;
        Assert.Equal("rewritten", System.Text.Encoding.UTF8.GetString(loaded.Payload.Span));
        Assert.Equal(newBytes.LongLength, loaded.PayloadSize);
    }

    [Fact]
    public async Task UpdatePayloadAsync_TextRow_RefreshesSearchTextSnippet()
    {
        // Regression: editing a text entry (external editor sync-back) used to leave search_text
        // stale, so the popup's left-hand list kept the pre-edit snippet while the preview pane
        // showed the new content. UpdatePayloadAsync must refresh search_text from the new payload.
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var id = await store.AddAsync(TextItem("original"), CancellationToken.None);

        var newBytes = Encoding.UTF8.GetBytes("rewritten body");
        Assert.True(await store.UpdatePayloadAsync(id, newBytes, newBytes.LongLength, newKind: null, CancellationToken.None));

        var loaded = (await store.GetByIdAsync(id, CancellationToken.None))!;
        Assert.Equal("rewritten body", loaded.SearchText);
    }

    [Fact]
    public async Task UpdatePayloadAsync_WithKindFlip_StoresKindAsName()
    {
        // The kind column is stored as the enum NAME everywhere else; UpdatePayloadAsync must
        // follow suit so a flipped row still matches the kind = $kind filter in ListAsync.
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var id = await store.AddAsync(
            new NewItem(
                Kind: ItemKind.Html,
                Source: ItemSource.Clipboard,
                CreatedAt: DateTimeOffset.UtcNow,
                Payload: Encoding.UTF8.GetBytes("<b>hi</b>"),
                PayloadSize: 9,
                SearchText: "hi"),
            CancellationToken.None);

        var plain = Encoding.UTF8.GetBytes("hi");
        Assert.True(await store.UpdatePayloadAsync(id, plain, plain.LongLength, ItemKind.Text, CancellationToken.None));

        var loaded = (await store.GetByIdAsync(id, CancellationToken.None))!;
        Assert.Equal(ItemKind.Text, loaded.Kind);
        // The flipped item is findable under the Text kind filter (would fail if kind were "0").
        var textRows = await store.ListAsync(new ItemQuery(Kind: ItemKind.Text), CancellationToken.None);
        Assert.Contains(textRows, r => r.Id == id);
    }

    [Fact]
    public async Task UpdatePayloadAsync_Missing_ReturnsFalse()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        Assert.False(await store.UpdatePayloadAsync(99999, new byte[] { 0x01 }, 1, newKind: null, CancellationToken.None));
    }

    [Fact]
    public async Task SetTriggerAsync_OnExistingItem_PersistsAndRaisesUpdated()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var id = await store.AddAsync(TextItem("payload"), CancellationToken.None);

        ItemsChangedEventArgs? captured = null;
        store.ItemsChanged += (_, e) => { if (e.Kind == ItemsChangeKind.Updated) captured = e; };

        var ok = await store.SetTriggerAsync(id, "mail", CancellationToken.None);

        Assert.True(ok);
        Assert.NotNull(captured);
        Assert.Equal(ItemsChangeKind.Updated, captured!.Kind);
        Assert.Equal(id, captured.ItemId);

        var loaded = (await store.GetByIdAsync(id, CancellationToken.None))!;
        Assert.Equal("mail", loaded.Trigger);
    }

    [Fact]
    public async Task SetTriggerAsync_WithNull_ClearsTrigger()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var id = await store.AddAsync(TextItem("payload") with { Trigger = "gh" }, CancellationToken.None);

        Assert.Equal("gh", (await store.GetByIdAsync(id, CancellationToken.None))!.Trigger);

        Assert.True(await store.SetTriggerAsync(id, null, CancellationToken.None));
        Assert.Null((await store.GetByIdAsync(id, CancellationToken.None))!.Trigger);
    }

    [Fact]
    public async Task SetTriggerAsync_WithWhitespace_NormalisesToNull()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var id = await store.AddAsync(TextItem("payload") with { Trigger = "gh" }, CancellationToken.None);

        Assert.True(await store.SetTriggerAsync(id, "   ", CancellationToken.None));
        Assert.Null((await store.GetByIdAsync(id, CancellationToken.None))!.Trigger);
    }

    [Fact]
    public async Task SetTriggerAsync_OnMissingId_ReturnsFalse_AndDoesNotRaise()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var raised = false;
        store.ItemsChanged += (_, _) => raised = true;

        var ok = await store.SetTriggerAsync(99999, "missing", CancellationToken.None);

        Assert.False(ok);
        Assert.False(raised);
    }

    [Fact]
    public async Task AddAsync_WithTrigger_PersistsAndRoundTrips()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var id = await store.AddAsync(TextItem("snippet") with { Trigger = "insta" }, CancellationToken.None);

        var loaded = (await store.GetByIdAsync(id, CancellationToken.None))!;
        Assert.Equal("insta", loaded.Trigger);
    }

    [Fact]
    public async Task ListAsync_HydratesTriggerColumn()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        await store.AddAsync(TextItem("with") with { Trigger = "hello" }, CancellationToken.None);
        await store.AddAsync(TextItem("without"), CancellationToken.None);

        var rows = await store.ListAsync(new ItemQuery(Limit: 10), CancellationToken.None);

        // Newest first → row[0] is "without" (no trigger), row[1] is "with" trigger=hello.
        Assert.Null(rows[0].Trigger);
        Assert.Equal("hello", rows[1].Trigger);
    }

    private static NewItem VideoItem(string path, byte[] payload)
        => new(
            Kind: ItemKind.Video,
            Source: ItemSource.CaptureRecording,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: payload,
            PayloadSize: payload.LongLength,
            BlobRef: path,
            SearchText: "Recording");

    [Fact]
    public async Task AddAsync_Video_StoresPathInsteadOfFileBytes()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var fileBytes = new byte[200_000];

        var id = await store.AddAsync(VideoItem(@"C:\captures\rec.mp4", fileBytes), CancellationToken.None);

        var loaded = (await store.GetByIdAsync(id, CancellationToken.None))!;
        Assert.Equal(@"C:\captures\rec.mp4", Encoding.UTF8.GetString(loaded.Payload.Span));
        Assert.Equal(@"C:\captures\rec.mp4", loaded.BlobRef);
        // The size column keeps describing the file (the toast and the row show it).
        Assert.Equal(fileBytes.LongLength, loaded.PayloadSize);
    }

    [Fact]
    public async Task AddAsync_Video_NeverDedupsAgainstPreviousRecording()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var first = await store.AddAsync(VideoItem(@"C:\captures\rec.mp4", new byte[10]), CancellationToken.None);
        var second = await store.AddAsync(VideoItem(@"C:\captures\rec.mp4", new byte[10]), CancellationToken.None);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task GetById_LegacyVideoRowWithHugePayload_ReturnsEmptyPayload()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var serializer = new ItemSerializer(new DpapiPayloadProtector());
        var store = new ItemStore(fx.Database, serializer);

        // Rows written before issue #28 hold the whole recording in the payload column.
        var conn = fx.Database.GetOpenConnection();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO items (kind, source, created_at, pinned, payload, payload_size, blob_ref)
                VALUES ('Video', 'CaptureRecording', $created, 0, $payload, $size, $blob);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$payload", serializer.Encode(new byte[300_000]));
            cmd.Parameters.AddWithValue("$size", 300_000L);
            cmd.Parameters.AddWithValue("$blob", @"C:\captures\old.mp4");
            var id = (long)(await cmd.ExecuteScalarAsync())!;

            var loaded = (await store.GetByIdAsync(id, CancellationToken.None))!;
            Assert.True(loaded.Payload.IsEmpty);
            Assert.Equal(@"C:\captures\old.mp4", loaded.BlobRef);

            var listed = await store.ListAsync(new ItemQuery(Limit: 10, IncludePayload: true), CancellationToken.None);
            Assert.True(listed.Single(r => r.Id == id).Payload.IsEmpty);
        }
    }

    [Fact]
    public async Task GetById_WithoutPayload_SkipsPayloadButKeepsMetadata()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var id = await store.AddAsync(TextItem("hello") with { BlobRef = @"C:\x.txt" }, CancellationToken.None);

        var loaded = (await store.GetByIdAsync(id, includePayload: false, CancellationToken.None))!;

        Assert.True(loaded.Payload.IsEmpty);
        Assert.Equal(ItemKind.Text, loaded.Kind);
        Assert.Equal(@"C:\x.txt", loaded.BlobRef);
        Assert.Equal("hello", loaded.SearchText);
    }

    private static NewItem FilesItem(string paths, byte[]? thumbnail = null)
        => new(
            Kind: ItemKind.Files,
            Source: ItemSource.Clipboard,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: Encoding.UTF8.GetBytes(paths),
            PayloadSize: paths.Length,
            SearchText: paths,
            Thumbnail: thumbnail);

    [Fact]
    public async Task AddAsync_FilesWithThumbnail_StoresTheProvidedThumbnail()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        var id = await store.AddAsync(FilesItem(@"C:\pic.png", [1, 2, 3]), CancellationToken.None);

        var listed = await store.ListAsync(new ItemQuery(IncludePayload: false), CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, listed.Single(r => r.Id == id).Thumbnail!.Value.ToArray());
    }

    [Fact]
    public async Task SetThumbnailIfMissing_FillsEmptyColumnOnly_AndRaisesNoEvent()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var bare = await store.AddAsync(FilesItem(@"C:\a.png"), CancellationToken.None);
        var withThumb = await store.AddAsync(FilesItem(@"C:\b.png", [9]), CancellationToken.None);
        var events = 0;
        store.ItemsChanged += (_, _) => events++;

        Assert.True(await store.SetThumbnailIfMissingAsync(bare, [7, 7], CancellationToken.None));
        Assert.False(await store.SetThumbnailIfMissingAsync(withThumb, [8], CancellationToken.None));

        var listed = await store.ListAsync(new ItemQuery(IncludePayload: false), CancellationToken.None);
        Assert.Equal(new byte[] { 7, 7 }, listed.Single(r => r.Id == bare).Thumbnail!.Value.ToArray());
        Assert.Equal(new byte[] { 9 }, listed.Single(r => r.Id == withThumb).Thumbnail!.Value.ToArray());
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task AddAsync_DuplicateFilesCopy_FillsMissingThumbnailOfExistingRow()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);
        var first = await store.AddAsync(FilesItem(@"C:\pic.png"), CancellationToken.None);

        var second = await store.AddAsync(FilesItem(@"C:\pic.png", [5]), CancellationToken.None);

        Assert.Equal(first, second);
        var loaded = await store.GetByIdAsync(first, includePayload: false, CancellationToken.None);
        Assert.Equal(new byte[] { 5 }, loaded!.Thumbnail!.Value.ToArray());
    }
}
