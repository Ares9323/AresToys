using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Tasks;
using AresToys.Storage.Settings;
using Xunit;

namespace AresToys.Pipeline.Tests.Tasks;

public class SaveToFileTaskTests
{
    private static SaveToFileTask Create() => new(new NullSettingsStore(), NullLogger<SaveToFileTask>.Instance);

    /// <summary>Empty settings store used when the test exercises the explicit folder config path
    /// rather than the settings fallback.</summary>
    private sealed class NullSettingsStore : ISettingsStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken) => Task.FromResult(false);
        public async IAsyncEnumerable<SettingEntry> EnumerateAsync(bool includeSensitive = false,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;   // satisfy async signature without yielding anything
            yield break;
        }
    }

    private static PipelineContext NewContext() => new(new ServiceCollection().BuildServiceProvider());

    [Fact]
    public async Task ExecuteAsync_WritesPayloadToFolderAndPopulatesBag()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AresToys.SaveTask", Guid.NewGuid().ToString("N"));
        try
        {
            var task = Create();
            var ctx = NewContext();
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            ctx.Bag[PipelineBagKeys.PayloadBytes] = bytes;
            ctx.Bag[PipelineBagKeys.FileExtension] = "bin";
            var config = JsonNode.Parse($"{{\"folder\":\"{folder.Replace("\\", "\\\\")}\"}}");

            await task.ExecuteAsync(ctx, config, CancellationToken.None);

            var path = (string)ctx.Bag[PipelineBagKeys.LocalPath];
            Assert.True(File.Exists(path));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.StartsWith(folder, path);
            Assert.EndsWith(".bin", path);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoPayloadInBag_Skips()
    {
        var task = Create();
        var ctx = NewContext();

        await task.ExecuteAsync(ctx, config: null, CancellationToken.None);

        Assert.False(ctx.Bag.ContainsKey(PipelineBagKeys.LocalPath));
    }

    [Fact]
    public async Task ExecuteAsync_DefaultExtension_IsBin_WhenNotInBag()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AresToys.SaveTask.NoExt", Guid.NewGuid().ToString("N"));
        try
        {
            var task = Create();
            var ctx = NewContext();
            ctx.Bag[PipelineBagKeys.PayloadBytes] = new byte[] { 0xAA };
            var config = JsonNode.Parse($"{{\"folder\":\"{folder.Replace("\\", "\\\\")}\"}}");

            await task.ExecuteAsync(ctx, config, CancellationToken.None);

            var path = (string)ctx.Bag[PipelineBagKeys.LocalPath];
            Assert.EndsWith(".bin", path);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
