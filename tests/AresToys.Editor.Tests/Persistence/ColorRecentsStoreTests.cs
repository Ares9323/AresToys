using System.Runtime.CompilerServices;
using AresToys.Editor.Model;
using AresToys.Editor.Persistence;
using AresToys.Storage.Settings;
using Xunit;

namespace AresToys.Editor.Tests.Persistence;

public sealed class ColorRecentsStoreTests
{
    [Fact]
    public async Task PushAsync_PutsColorAtFrontAndRaisesChanged()
    {
        var settings = new FakeSettingsStore();
        var store = new ColorRecentsStore(settings);
        IReadOnlyList<ShapeColor>? fromEvent = null;
        store.Changed += (_, list) => fromEvent = list;

        await store.PushAsync(new ShapeColor(255, 10, 20, 30), CancellationToken.None);
        await store.PushAsync(new ShapeColor(255, 40, 50, 60), CancellationToken.None);

        Assert.Equal(new ShapeColor(255, 40, 50, 60), store.Current[0]);
        Assert.Equal(new ShapeColor(255, 10, 20, 30), store.Current[1]);
        Assert.NotNull(fromEvent);
        Assert.Equal(store.Current, fromEvent);
    }

    [Fact]
    public async Task PushAsync_MovesExistingColorToFrontWithoutDuplicating()
    {
        var store = new ColorRecentsStore(new FakeSettingsStore());
        var a = new ShapeColor(255, 1, 2, 3);
        var b = new ShapeColor(255, 4, 5, 6);

        await store.PushAsync(a, CancellationToken.None);
        await store.PushAsync(b, CancellationToken.None);
        await store.PushAsync(a, CancellationToken.None);

        Assert.Equal(2, store.Current.Count);
        Assert.Equal(a, store.Current[0]);
        Assert.Equal(b, store.Current[1]);
    }

    [Fact]
    public async Task PushAsync_CapsAtMaxEntries()
    {
        var store = new ColorRecentsStore(new FakeSettingsStore());
        for (var i = 0; i < ColorRecentsStore.MaxEntries + 4; i++)
            await store.PushAsync(new ShapeColor(255, (byte)i, 0, 0), CancellationToken.None);

        Assert.Equal(ColorRecentsStore.MaxEntries, store.Current.Count);
        Assert.Equal((byte)(ColorRecentsStore.MaxEntries + 3), store.Current[0].R);
    }

    /// <summary>Concurrent pushes used to race: both read the pre-push list and the second write
    /// dropped the first colour. The store now serialises the read-modify-write.</summary>
    [Fact]
    public async Task PushAsync_ConcurrentPushesAllSurvive()
    {
        var store = new ColorRecentsStore(new FakeSettingsStore { DelayMs = 5 });
        var pushes = Enumerable.Range(1, 6)
            .Select(i => store.PushAsync(new ShapeColor(255, (byte)i, 0, 0), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(pushes);

        Assert.Equal(6, store.Current.Count);
        Assert.Equal(6, store.Current.Select(c => c.R).Distinct().Count());
    }

    [Fact]
    public async Task LoadAsync_RoundTripsPersistedEntries()
    {
        var settings = new FakeSettingsStore();
        var first = new ColorRecentsStore(settings);
        await first.PushAsync(new ShapeColor(200, 9, 8, 7), CancellationToken.None);

        var second = new ColorRecentsStore(settings);
        var loaded = await second.LoadAsync(CancellationToken.None);

        Assert.Equal(new ShapeColor(200, 9, 8, 7), Assert.Single(loaded));
        Assert.Equal(loaded, second.Current);
    }

    [Fact]
    public async Task LoadAsync_CorruptJsonYieldsEmptyList()
    {
        var settings = new FakeSettingsStore();
        await settings.SetAsync("editor.color.recents", "{not json", sensitive: false, CancellationToken.None);
        var store = new ColorRecentsStore(settings);

        Assert.Empty(await store.LoadAsync(CancellationToken.None));
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        /// <summary>Simulated write latency — widens the window a racing push would slip into.</summary>
        public int DelayMs { get; init; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

        public async Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken)
        {
            if (DelayMs > 0) await Task.Delay(DelayMs, cancellationToken);
            _values[key] = value;
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(_values.Remove(key));

        public async IAsyncEnumerable<SettingEntry> EnumerateAsync(
            bool includeSensitive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (k, v) in _values)
            {
                yield return new SettingEntry(k, v, false);
            }
            await Task.CompletedTask;
        }
    }
}
