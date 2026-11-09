using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using AresToys.App.Services.PipelineTasks;
using AresToys.App.Services.Recording;
using AresToys.App.Tests.KeySequences.Fakes;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests;

/// <summary>Issue #28: the recording travels as a file (bag.local_path), never as bytes, and
/// only the recorder's own temp file is consumed. Same-format saves only: no ffmpeg needed.</summary>
public class SaveVideoFileTaskTests
{
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static (SaveVideoFileTask Task, string Destination) Build(string root)
    {
        var settings = new FakeSettingsStore();
        var destination = Path.Combine(root, "out");
        settings.Backing["capture.folder"] = destination;
        var task = new SaveVideoFileTask(settings, new FfmpegLocator(NullLogger<FfmpegLocator>.Instance),
            NullLogger<SaveVideoFileTask>.Instance);
        return (task, destination);
    }

    private static PipelineContext ContextFor(string sourcePath)
    {
        var ctx = new PipelineContext(new NoServices());
        ctx.Bag[PipelineBagKeys.LocalPath] = sourcePath;
        ctx.Bag[PipelineBagKeys.FileExtension] = "mp4";
        ctx.Bag[PipelineBagKeys.NewItem] = new NewItem(
            Kind: ItemKind.Video,
            Source: ItemSource.CaptureRegion,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: Encoding.UTF8.GetBytes(sourcePath),
            PayloadSize: new FileInfo(sourcePath).Length,
            BlobRef: sourcePath);
        return ctx;
    }

    [Fact]
    public async Task TempRecording_IsMovedToDestination_AndItemPointsAtIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "AresToys.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RecordingCoordinator.PipelineTempFolder);
        var temp = Path.Combine(RecordingCoordinator.PipelineTempFolder, $"test-{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllBytesAsync(temp, new byte[1234]);
            var (task, destination) = Build(root);
            var ctx = ContextFor(temp);

            await task.ExecuteAsync(ctx, JsonNode.Parse("{\"format\":\"mp4\"}"), CancellationToken.None);

            var saved = (string)ctx.Bag[PipelineBagKeys.LocalPath];
            Assert.StartsWith(destination, saved, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(saved));
            Assert.False(File.Exists(temp));
            Assert.False(ctx.Bag.ContainsKey(PipelineBagKeys.PayloadBytes));
            var item = (NewItem)ctx.Bag[PipelineBagKeys.NewItem];
            Assert.Equal(saved, item.BlobRef);
            Assert.Equal(saved, Encoding.UTF8.GetString(item.Payload.Span));
            Assert.Equal(1234, item.PayloadSize);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileOutsideTheTempFolder_IsCopied_AndLeftInPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), "AresToys.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var userFile = Path.Combine(root, "mine.mp4");
            await File.WriteAllBytesAsync(userFile, new byte[42]);
            var (task, _) = Build(root);
            var ctx = ContextFor(userFile);

            await task.ExecuteAsync(ctx, JsonNode.Parse("{\"format\":\"mp4\"}"), CancellationToken.None);

            var saved = (string)ctx.Bag[PipelineBagKeys.LocalPath];
            Assert.NotEqual(userFile, saved);
            Assert.True(File.Exists(saved));
            Assert.True(File.Exists(userFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
