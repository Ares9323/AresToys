using System.Globalization;
using AresToys.Capture.Recording;
using Xunit;

namespace AresToys.Capture.Tests;

/// <summary>Command-line shape and time clamping for the video trim. Pure string work, kept out of
/// the service so the part that's easy to get subtly wrong (time formatting under a comma-decimal
/// culture, out-of-range handles, an end before the start) is covered without invoking ffmpeg.</summary>
public sealed class VideoTrimArgsBuilderTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("clip.mp4", true)]
    [InlineData("clip.MP4", true)]
    [InlineData("clip.m4v", true)]
    [InlineData("clip.mov", true)]
    // Produced by the app, but each needs its own encoder rather than this command:
    // a GIF wants the palettegen/paletteuse pipeline, a WebM wants VP9.
    [InlineData("clip.gif", false)]
    [InlineData("clip.webm", false)]
    // Recognised by the preview, never produced by us.
    [InlineData("clip.mkv", false)]
    [InlineData("clip.avi", false)]
    [InlineData("notavideo.txt", false)]
    [InlineData("noextension", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheH264ContainersWeProduceAreTrimmable(string? path, bool expected)
    {
        Assert.Equal(expected, VideoTrimArgsBuilder.IsSupported(path));
    }

    [Fact]
    public void BuildsAReEncodingCommandWithBothTimesBeforeTheInput()
    {
        var args = VideoTrimArgsBuilder.Build(new VideoTrimOptions(
            @"C:\in\clip.mp4", @"C:\out\clip-trim.mp4",
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));

        // -ss/-to ahead of -i: that's the form measured as frame-accurate once re-encoding.
        Assert.Matches(@"-ss 5(\.\d+)? -to 15(\.\d+)? -i ", args);
        Assert.Contains(@"-i ""C:\in\clip.mp4""", args, StringComparison.Ordinal);
        Assert.EndsWith(@"""C:\out\clip-trim.mp4""", args, StringComparison.Ordinal);
        // Re-encode, never -c copy: a stream copy is only accurate thanks to the MP4 edit list,
        // and a player that ignores it replays up to a whole GOP the user thought was cut.
        Assert.DoesNotContain("-c copy", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:v copy", args, StringComparison.Ordinal);
        Assert.Contains("libx264", args, StringComparison.Ordinal);
        Assert.Contains("-pix_fmt yuv420p", args, StringComparison.Ordinal);
        Assert.Contains("-y", args, StringComparison.Ordinal);
    }

    [Fact]
    public void TimesAreWrittenWithADotEvenWhereTheCultureUsesAComma()
    {
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("it-IT");
        try
        {
            var args = VideoTrimArgsBuilder.Build(new VideoTrimOptions(
                "in.mp4", "out.mp4", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.25)));

            // "1,5" would make ffmpeg parse the time as 1 second and drop the rest.
            Assert.Contains("-ss 1.5 ", args, StringComparison.Ordinal);
            Assert.Contains("-to 2.25 ", args, StringComparison.Ordinal);
            Assert.DoesNotContain(",", args, StringComparison.Ordinal);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AnOrdinarySelectionIsLeftAlone()
    {
        var (start, end) = VideoTrimArgsBuilder.Clamp(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), Duration);

        Assert.Equal(TimeSpan.FromSeconds(5), start);
        Assert.Equal(TimeSpan.FromSeconds(20), end);
    }

    [Fact]
    public void HandlesDraggedOutsideTheClipAreBroughtBackInside()
    {
        var (start, end) = VideoTrimArgsBuilder.Clamp(
            TimeSpan.FromSeconds(-3), TimeSpan.FromSeconds(45), Duration);

        Assert.Equal(TimeSpan.Zero, start);
        Assert.Equal(Duration, end);
    }

    [Fact]
    public void AnEndBeforeTheStartIsSwapped()
    {
        // Reachable by setting the out point first and then the in point past it.
        var (start, end) = VideoTrimArgsBuilder.Clamp(
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(8), Duration);

        Assert.Equal(TimeSpan.FromSeconds(8), start);
        Assert.Equal(TimeSpan.FromSeconds(20), end);
    }

    [Fact]
    public void AZeroLengthSelectionIsWidenedToTheMinimumFrame()
    {
        var (start, end) = VideoTrimArgsBuilder.Clamp(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), Duration);

        Assert.True(end > start, "a zero-length trim would produce an empty file");
        Assert.True(end - start >= VideoTrimArgsBuilder.MinimumSelection);
    }

    [Fact]
    public void AMinimumSelectionAtTheVeryEndStaysInsideTheClip()
    {
        var (start, end) = VideoTrimArgsBuilder.Clamp(Duration, Duration, Duration);

        Assert.True(end <= Duration);
        Assert.True(end > start);
    }

    [Fact]
    public void TrimmingNothingIsRecognisedAsSuch()
    {
        // The dialog uses this to keep "Trim" disabled when the handles still span everything:
        // re-encoding a clip to produce the same clip only costs quality.
        Assert.True(VideoTrimArgsBuilder.IsWholeClip(TimeSpan.Zero, Duration, Duration));
        Assert.False(VideoTrimArgsBuilder.IsWholeClip(TimeSpan.FromSeconds(0.5), Duration, Duration));
        Assert.False(VideoTrimArgsBuilder.IsWholeClip(TimeSpan.Zero, TimeSpan.FromSeconds(29), Duration));
    }
}
