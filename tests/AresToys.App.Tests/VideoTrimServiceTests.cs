using System.Diagnostics;
using System.IO;
using AresToys.App.Services.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests;

/// <summary>End-to-end trims through the real FFmpeg. The source clip is generated on the spot with
/// <c>testsrc</c>, so these depend on the binary being present but on nothing else, and they check
/// the property that matters and that a unit test can't reach: the output really is the requested
/// length. Skipped when ffmpeg isn't installed.</summary>
public sealed class VideoTrimServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _ffmpeg;

    public VideoTrimServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "arestoys-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ffmpeg = new FfmpegLocator(NullLogger<FfmpegLocator>.Instance).Find();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir, best effort */ }
    }

    private VideoTrimService Service() =>
        new(new FfmpegLocator(NullLogger<FfmpegLocator>.Instance), NullLogger<VideoTrimService>.Instance);

    /// <summary>Generate a clip of a known length, encoded the way the recorder encodes.</summary>
    private string MakeClip(string name, int seconds)
    {
        var path = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg!,
            Arguments = $"-y -f lavfi -i testsrc=duration={seconds}:size=320x240:rate=30 "
                      + $"-c:v libx264 -preset veryfast -pix_fmt yuv420p \"{path}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return path;
    }

    /// <summary>Duration in seconds, read back through ffmpeg's own report.</summary>
    private double DurationOf(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg!,
            Arguments = $"-hide_banner -i \"{path}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var m = System.Text.RegularExpressions.Regex.Match(err, @"Duration: (\d+):(\d+):(\d+\.\d+)");
        Assert.True(m.Success, "could not read a duration from:\n" + err);
        return (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 3600)
             + (int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 60)
             + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task ProducesAClipOfExactlyTheRequestedLength()
    {
        if (_ffmpeg is null) return;
        var src = MakeClip("src.mp4", 10);

        var result = await Service().TrimAsync(src, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7),
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.NotNull(result);
        // Frame-accurate: a stream copy would land on the nearest keyframe instead.
        Assert.Equal(4.0, DurationOf(result!), precision: 1);
    }

    [Fact]
    public async Task LeavesTheOriginalUntouched()
    {
        if (_ffmpeg is null) return;
        var src = MakeClip("keep.mp4", 6);
        var before = new FileInfo(src).Length;

        var result = await Service().TrimAsync(src, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(6), CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEqual(Path.GetFullPath(src), Path.GetFullPath(result!));
        Assert.True(File.Exists(src));
        Assert.Equal(before, new FileInfo(src).Length);
    }

    [Fact]
    public async Task TrimmingTheSameRecordingTwiceKeepsBothExcerpts()
    {
        if (_ffmpeg is null) return;
        var src = MakeClip("twice.mp4", 8);
        var svc = Service();

        var first = await svc.TrimAsync(src, TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), CancellationToken.None);
        var second = await svc.TrimAsync(src, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first!));
        Assert.True(File.Exists(second!));
    }

    [Fact]
    public async Task OutOfRangeHandlesStillProduceAValidClip()
    {
        if (_ffmpeg is null) return;
        var src = MakeClip("clamped.mp4", 5);

        // Asks for more than there is: clamping should reduce it to the clip itself.
        var result = await Service().TrimAsync(src, TimeSpan.FromSeconds(-2), TimeSpan.FromSeconds(99),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5.0, DurationOf(result!), precision: 1);
    }

    [Fact]
    public async Task UnsupportedContainersAreRefusedWithoutRunningAnything()
    {
        var gif = Path.Combine(_dir, "anim.gif");
        await File.WriteAllTextAsync(gif, "not really a gif");

        Assert.Null(await Service().TrimAsync(gif, TimeSpan.Zero, TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task AMissingFileIsReportedRatherThanThrown()
    {
        Assert.Null(await Service().TrimAsync(Path.Combine(_dir, "ghost.mp4"),
            TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
