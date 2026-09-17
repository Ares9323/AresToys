using System.Runtime.CompilerServices;
using AresToys.Editor.Persistence;
using AresToys.Storage.Settings;
using Xunit;

namespace AresToys.Editor.Tests.Persistence;

/// <summary>The effect defaults have to survive a restart, and — more delicately — a settings
/// payload written before they existed has to load with the values the tools used to hard-code,
/// otherwise everyone's next blur would silently come out at radius 0.</summary>
public sealed class EditorDefaultsStoreEffectsTests
{
    private const string Key = "editor.defaults";

    [Fact]
    public async Task EffectDefaultsRoundTrip()
    {
        var settings = new FakeSettingsStore();
        var store = new EditorDefaultsStore(settings);
        var saved = EditorDefaultsStore.Initial with
        {
            BlurRadius = 33,
            PixelateBlockSize = 21,
            SpotlightDim = 0.75,
            SpotlightBlur = 9,
        };

        await store.SaveAsync(saved, CancellationToken.None);
        var loaded = await new EditorDefaultsStore(settings).LoadAsync(CancellationToken.None);

        Assert.Equal(33, loaded.BlurRadius);
        Assert.Equal(21, loaded.PixelateBlockSize);
        Assert.Equal(0.75, loaded.SpotlightDim);
        Assert.Equal(9, loaded.SpotlightBlur);
    }

    [Fact]
    public async Task ADimOfZeroIsPreservedRatherThanTreatedAsMissing()
    {
        var settings = new FakeSettingsStore();
        var store = new EditorDefaultsStore(settings);

        await store.SaveAsync(EditorDefaultsStore.Initial with { SpotlightDim = 0 }, CancellationToken.None);
        var loaded = await new EditorDefaultsStore(settings).LoadAsync(CancellationToken.None);

        Assert.Equal(0, loaded.SpotlightDim);
    }

    [Fact]
    public async Task APayloadWrittenBeforeEffectsExistedLoadsWithTheOldHardCodedValues()
    {
        var settings = new FakeSettingsStore();
        // Shape of the payload from before the effect fields were added.
        await settings.SetAsync(Key, """
        {"OutlineA":255,"OutlineR":255,"OutlineG":0,"OutlineB":0,
         "FillA":0,"FillR":0,"FillG":0,"FillB":0,
         "StrokeWidth":2,"Tool":1,"FontFamily":"Segoe UI","FontSize":24,"Bold":false,"Italic":false}
        """, sensitive: false, CancellationToken.None);

        var loaded = await new EditorDefaultsStore(settings).LoadAsync(CancellationToken.None);

        Assert.Equal(12, loaded.BlurRadius);
        Assert.Equal(8, loaded.PixelateBlockSize);
        Assert.Equal(0.5, loaded.SpotlightDim);
        Assert.Equal(0, loaded.SpotlightBlur);
    }

    [Fact]
    public async Task OutOfRangeValuesAreClampedOnLoad()
    {
        var settings = new FakeSettingsStore();
        await settings.SetAsync(Key, """
        {"OutlineA":255,"OutlineR":255,"OutlineG":0,"OutlineB":0,
         "FillA":0,"FillR":0,"FillG":0,"FillB":0,
         "StrokeWidth":2,"Tool":1,"FontFamily":"Segoe UI","FontSize":24,"Bold":false,"Italic":false,
         "BlurRadius":100000,"PixelateBlockSize":0,"SpotlightDim":4,"SpotlightBlur":-20}
        """, sensitive: false, CancellationToken.None);

        var loaded = await new EditorDefaultsStore(settings).LoadAsync(CancellationToken.None);

        Assert.Equal(60, loaded.BlurRadius);
        Assert.Equal(2, loaded.PixelateBlockSize);
        Assert.Equal(1, loaded.SpotlightDim);
        Assert.Equal(0, loaded.SpotlightBlur);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(_values.Remove(key));

        public async IAsyncEnumerable<SettingEntry> EnumerateAsync(
            bool includeSensitive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (k, v) in _values) yield return new SettingEntry(k, v, false);
            await Task.CompletedTask;
        }
    }
}
