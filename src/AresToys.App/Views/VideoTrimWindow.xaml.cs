using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AresToys.App.Services;
using AresToys.App.Services.Recording;
using AresToys.Capture.Recording;

namespace AresToys.App.Views;

/// <summary>Picks the window to keep out of a recording, then runs the trim. The two cut points are
/// taken from the player's current position rather than from draggable handles on a timeline: the
/// player already scrubs frame by frame, so "move there, mark it" gives frame precision without
/// building a second timeline control. <see cref="ResultPath"/> holds the new file on success.</summary>
public partial class VideoTrimWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _sourcePath;
    private readonly VideoTrimService _trimmer;
    private readonly DispatcherTimer _tick;

    private TimeSpan _duration;
    private TimeSpan _start;
    private TimeSpan _end;
    private bool _isPlaying;
    private bool _busy;

    /// <summary>Path of the trimmed file, or null when the user cancelled or the encode failed.</summary>
    public string? ResultPath { get; private set; }

    public VideoTrimWindow(string sourcePath, VideoTrimService trimmer)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _sourcePath = sourcePath;
        _trimmer = trimmer;

        // 200 ms: fast enough that the timecode doesn't visibly lag the picture, slow enough not to
        // spend a dispatcher pass on every frame.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => SyncFromPlayer();

        Loaded += (_, _) =>
        {
            Player.Source = new Uri(_sourcePath);
            // Pause at the first frame: ScrubbingEnabled renders it, so the window opens showing
            // the clip instead of a black rectangle.
            Player.Play();
            Player.Pause();
            UpdateSelectionText();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            Player.Stop();
            Player.Source = null;   // releases the file handle
        };
    }

    private static string Loc(string key, params object[] args)
    {
        var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        var template = AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
#pragma warning disable CA1863 // a handful of formats per dialog, caching a CompositeFormat costs more than it saves
        return args.Length == 0 ? template : string.Format(culture, template, args);
#pragma warning restore CA1863
    }

    private static string Format(TimeSpan t) =>
        t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Player.NaturalDuration.HasTimeSpan
            ? Player.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        _start = TimeSpan.Zero;
        _end = _duration;

        UpdateSelectionText();
        _tick.Start();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // Windows can't decode every container it will happily hand us a path to. Nothing to pick
        // cut points on, so the dialog can only say so and let the user out.
        _tick.Stop();
        StatusText.Text = Loc("VideoTrim_PlaybackFailed");
        TrimButton.IsEnabled = false;
        SetStartButton.IsEnabled = false;
        SetEndButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        _isPlaying = false;
        PlayPauseButton.Content = "▶";
    }

    private void SyncFromPlayer()
    {
        if (_duration <= TimeSpan.Zero) return;
        // Playback stops at the out-point: while trimming, the end of the selection is the end of
        // the clip you're making, so running past it only shows footage you've already discarded.
        if (_isPlaying && Player.Position >= _end)
        {
            Player.Pause();
            Player.Position = _end;
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
        }
        if (_isPlaying) UpdateTimecode();
        UpdatePlayhead();
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimeline();

    /// <summary>Click anywhere on the track to seek there. The handles sit above and swallow their
    /// own clicks, so this only fires on the bare track.</summary>
    private void OnTimelineClicked(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= TimeSpan.Zero) return;
        var x = e.GetPosition(Timeline).X;
        Player.Position = TrimTimelineGeometry.XToTime(x, _duration, Timeline.ActualWidth);
        UpdateTimecode();
        UpdatePlayhead();
    }

    private void OnStartHandleDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_duration <= TimeSpan.Zero) return;
        var x = TrimTimelineGeometry.TimeToX(_start, _duration, Timeline.ActualWidth) + e.HorizontalChange;
        _start = TrimTimelineGeometry.XToTime(x, _duration, Timeline.ActualWidth);
        // Follow the handle with the picture: that's the frame the cut will start on, and seeing
        // it is the whole point of dragging rather than typing a number.
        Player.Position = _start;
        UpdateSelectionText();
    }

    private void OnEndHandleDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_duration <= TimeSpan.Zero) return;
        var x = TrimTimelineGeometry.TimeToX(_end, _duration, Timeline.ActualWidth) + e.HorizontalChange;
        _end = TrimTimelineGeometry.XToTime(x, _duration, Timeline.ActualWidth);
        Player.Position = _end;
        UpdateSelectionText();
    }

    /// <summary>Repaint the track: red where footage is being discarded, accent where it's kept,
    /// handles centred on the two cut points.</summary>
    private void UpdateTimeline()
    {
        var w = Timeline.ActualWidth;
        if (w <= 0) return;

        TrackBase.Width = w;
        var (headWidth, keepX, keepWidth) = TrimTimelineGeometry.Bands(_start, _end, _duration, w);

        System.Windows.Controls.Canvas.SetLeft(HeadCut, 0);
        HeadCut.Width = headWidth;

        System.Windows.Controls.Canvas.SetLeft(KeepSpan, keepX);
        KeepSpan.Width = keepWidth;

        var tailX = keepX + keepWidth;
        System.Windows.Controls.Canvas.SetLeft(TailCut, tailX);
        TailCut.Width = Math.Max(0, w - tailX);

        // Handles are 14 px wide, so centre them on their time rather than starting there.
        System.Windows.Controls.Canvas.SetLeft(StartHandle, keepX - (StartHandle.Width / 2));
        System.Windows.Controls.Canvas.SetLeft(EndHandle, tailX - (EndHandle.Width / 2));

        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        var w = Timeline.ActualWidth;
        if (w <= 0) return;
        System.Windows.Controls.Canvas.SetLeft(
            Playhead, TrimTimelineGeometry.TimeToX(Player.Position, _duration, w) - (Playhead.Width / 2));
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e) => TogglePlayback();

    /// <summary>Back to the start of the trim, not of the video: after marking an in-point, that's
    /// where the clip being made begins, and rewinding to 0 would only replay discarded footage.</summary>
    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        _isPlaying = false;
        PlayPauseButton.Content = "▶";
        Player.Position = _start;
        UpdateTimecode();
        UpdatePlayhead();
    }

    private void OnSurfaceClick(object sender, MouseButtonEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_isPlaying) { Player.Pause(); PlayPauseButton.Content = "▶"; }
        else { Player.Play(); PlayPauseButton.Content = "■"; }
        _isPlaying = !_isPlaying;
    }

    private void OnSetStartClicked(object sender, RoutedEventArgs e)
    {
        _start = Player.Position;
        UpdateSelectionText();
    }

    private void OnSetEndClicked(object sender, RoutedEventArgs e)
    {
        _end = Player.Position;
        UpdateSelectionText();
    }

    private void UpdateTimecode() =>
        TimecodeText.Text = $"{Format(Player.Position)} / {Format(_duration)}";

    private void UpdateSelectionText()
    {
        var (from, to) = VideoTrimArgsBuilder.Clamp(_start, _end, _duration);
        _start = from;
        _end = to;
        SelectionText.Text = Loc("VideoTrim_Selection", Format(from), Format(to), Format(to - from));
        UpdateTimeline();

        // Nothing to cut yet: re-encoding the clip into the same clip would only spend quality.
        var nothingToDo = VideoTrimArgsBuilder.IsWholeClip(from, to, _duration);
        TrimButton.IsEnabled = !nothingToDo && !_busy && _duration > TimeSpan.Zero;
        StatusText.Text = nothingToDo && _duration > TimeSpan.Zero ? Loc("VideoTrim_NothingSelected") : string.Empty;
        UpdateTimecode();
    }

    private async void OnTrimClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        TrimButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusText.Text = Loc("VideoTrim_Working");
        Player.Pause();
        _isPlaying = false;
        PlayPauseButton.Content = "▶";

        var result = await _trimmer.TrimAsync(_sourcePath, _start, _end, _duration, CancellationToken.None)
            .ConfigureAwait(true);

        _busy = false;
        CancelButton.IsEnabled = true;
        if (result is null)
        {
            StatusText.Text = Loc("VideoTrim_Failed");
            TrimButton.IsEnabled = true;
            return;
        }

        ResultPath = result;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
