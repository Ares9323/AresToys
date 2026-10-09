using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AresToys.App.Views;

/// <summary>Pins a video or animated GIF to the screen, the moving counterpart of
/// <see cref="PinnedImageWindow"/>. Borderless and topmost, loops forever by default.
/// <list type="bullet">
/// <item>Left click toggles play / pause, left drag moves the window, Space toggles too.</item>
/// <item>Mouse wheel scrubs (Left / Right arrows as well), Ctrl+wheel zooms around the cursor,
/// Shift+wheel changes the window opacity.</item>
/// <item>Hover shows the zoom readout and a transport bar with seek slider, timecode and mute.</item>
/// <item>Right click or Esc closes; closing stops playback and releases the file.</item>
/// </list>
/// Playback goes through the same <see cref="System.Windows.Controls.MediaElement"/> (Windows
/// Media Foundation) the clipboard panel preview uses, so what plays there plays here. The
/// file is streamed from disk: a big GIF is never decoded into memory frame by frame.</summary>
public partial class PinnedVideoWindow : Window
{
    /// <summary>Largest share of the work area a freshly pinned video may cover. Recordings are
    /// often full-screen sized: pinned at 1:1 they would bury the screen they're meant to float
    /// over, so they start scaled down to fit (Ctrl+wheel / reset zoom bring them back).</summary>
    private const double MaxInitialScreenFraction = 0.6;

    private readonly string _path;
    private readonly int _borderThickness;
    private readonly ILogger _logger;
    private readonly Action<Exception?>? _onFailed;
    private readonly DispatcherTimer _positionTimer;
    private bool _playing;
    private bool _muted;
    private bool _sliderUpdating;
    private bool _opened;
    private double _scale = 1.0;
    private double _opacity = 1.0;
    private double _naturalWidthDip;
    private double _naturalHeightDip;

    /// <param name="path">Video / GIF file on disk.</param>
    /// <param name="initialBorderThickness">Sticky pin border (same setting as the image pin).</param>
    /// <param name="muted">Initial mute state, normally the clipboard preview's preference.</param>
    /// <param name="onFailed">Invoked when the file can't be played (missing codec, file gone).
    /// The window closes itself right after.</param>
    public PinnedVideoWindow(
        string path,
        int initialBorderThickness = 0,
        bool muted = true,
        Action<Exception?>? onFailed = null,
        ILogger<PinnedVideoWindow>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        InitializeComponent();
        _path = path;
        _borderThickness = Math.Clamp(initialBorderThickness, 0, PinnedImageWindow.MaxBorderThickness);
        _muted = muted;
        _onFailed = onFailed;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        VideoBorder.BorderThickness = new Thickness(_borderThickness);

        // Placeholder size until MediaOpened reports the real frame size.
        Width = 320 + 2 * _borderThickness;
        Height = 180 + 2 * _borderThickness;

        _positionTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(150) };
        _positionTimer.Tick += (_, _) => SyncTransport();

        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => StartPlayback();
        Closed += (_, _) => ReleasePlayer();
        ApplyMuted();
    }

    private void StartPlayback()
    {
        Player.Source = new Uri(_path, UriKind.Absolute);
        Player.IsMuted = _muted;
        Player.Play();
        SetPlaying(true);
    }

    /// <summary>Stop, close and drop the source so the file handle is released immediately
    /// (the user may want to move / delete the recording while other pins stay open).</summary>
    private void ReleasePlayer()
    {
        _positionTimer.Stop();
        try
        {
            Player.Stop();
            Player.Close();
            Player.Source = null;
        }
        catch (InvalidOperationException) { /* already torn down */ }
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _opened = true;
        Player.IsMuted = _muted;
        var duration = Duration;
        _sliderUpdating = true;
        SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 0.001);
        SeekSlider.Value = 0;
        _sliderUpdating = false;
        UpdateTimeText(TimeSpan.Zero, duration);

        // Natural size is in video pixels: map 1 pixel to 1 physical pixel like the image pin,
        // then shrink to fit when the clip would cover most of the screen.
        var dpi = VisualTreeHelper.GetDpi(this);
        _naturalWidthDip = Math.Max(1, Player.NaturalVideoWidth) / dpi.DpiScaleX;
        _naturalHeightDip = Math.Max(1, Player.NaturalVideoHeight) / dpi.DpiScaleY;
        var work = SystemParameters.WorkArea;
        var fit = Math.Min(work.Width * MaxInitialScreenFraction / _naturalWidthDip,
                           work.Height * MaxInitialScreenFraction / _naturalHeightDip);
        _scale = Math.Clamp(Math.Min(1.0, fit), 0.1, 8.0);
        ResizeKeepingCenter();
        _positionTimer.Start();
        _logger.LogInformation("PinnedVideo: opened {Path} ({W}x{H} px, {Duration})",
            _path, Player.NaturalVideoWidth, Player.NaturalVideoHeight, duration);
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        // Loop: back to the start and keep going, unless the user paused right at the end.
        Player.Position = TimeSpan.Zero;
        if (_playing) Player.Play();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _logger.LogWarning(e.ErrorException, "PinnedVideo: cannot play {Path}", _path);
        _onFailed?.Invoke(e.ErrorException);
        Close();
    }

    private TimeSpan Duration => Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;

    private void SyncTransport()
    {
        var pos = Player.Position;
        _sliderUpdating = true;
        SeekSlider.Value = pos.TotalSeconds;
        _sliderUpdating = false;
        UpdateTimeText(pos, Duration);
    }

    private void OnSeekSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The timer writes the slider too (guarded); only user drags / clicks become seeks.
        if (_sliderUpdating || !_opened) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        UpdateTimeText(Player.Position, Duration);
    }

    private void SeekBy(double seconds)
    {
        if (!_opened) return;
        var duration = Duration;
        if (duration <= TimeSpan.Zero) return;
        var target = Player.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > duration) target = duration;
        Player.Position = target;
        SyncTransport();
    }

    /// <summary>One wheel notch / arrow press: 2% of the clip, between a quarter second and five
    /// seconds, so short GIFs and long recordings both scrub at a usable pace.</summary>
    private double SeekStepSeconds => Math.Clamp(Duration.TotalSeconds * 0.02, 0.25, 5.0);

    private void TogglePlayback()
    {
        if (_playing) Player.Pause();
        else Player.Play();
        SetPlaying(!_playing);
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        // Square = playing (click to pause), triangle = paused: same glyphs as the panel preview.
        PlayPauseButton.Content = playing ? "■" : "▶";
    }

    private void ApplyMuted()
    {
        Player.IsMuted = _muted;
        MuteButton.Content = _muted ? "🔇" : "🔊";
    }

    private void UpdateTimeText(TimeSpan position, TimeSpan duration)
        => TimeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private void ApplySize()
    {
        Width = _naturalWidthDip * _scale + 2 * _borderThickness;
        Height = _naturalHeightDip * _scale + 2 * _borderThickness;
        ZoomLabel.Text = $"{Math.Round(_scale * 100)}%";
    }

    private void ResizeKeepingCenter()
    {
        var centerX = Left + ActualWidth / 2;
        var centerY = Top + ActualHeight / 2;
        ApplySize();
        Left = centerX - Width / 2;
        Top = centerY - Height / 2;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayback();

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        ApplyMuted();
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        if (!_opened || Math.Abs(_scale - 1.0) < 1e-4) return;
        _scale = 1.0;
        ResizeKeepingCenter();
    }

    private void SetPinOpacity(double opacity)
    {
        _opacity = opacity;
        Opacity = opacity;
        OpacityLabel.Text = "◐ " + PinWindowOpacity.Format(opacity);
    }

    private void OnOpacityLabelClick(object sender, MouseButtonEventArgs e)
    {
        SetPinOpacity(1.0);
        e.Handled = true;
    }

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        TopBar.Visibility = Visibility.Visible;
        TransportBar.Visibility = Visibility.Visible;
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        TopBar.Visibility = Visibility.Collapsed;
        TransportBar.Visibility = Visibility.Collapsed;
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // Drag moves the window; a press that doesn't move it is a click and toggles playback.
        var (left, top) = (Left, Top);
        DragMove();
        if (Math.Abs(Left - left) < 1 && Math.Abs(Top - top) < 1) TogglePlayback();
        e.Handled = true;
    }

    private void OnSurfaceRightClick(object sender, MouseButtonEventArgs e) => Close();

    private void OnSurfaceWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            ZoomFromCursor(e);
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            SetPinOpacity(PinWindowOpacity.Next(_opacity, e.Delta));
        else
            SeekBy(e.Delta > 0 ? SeekStepSeconds : -SeekStepSeconds);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Space: TogglePlayback(); break;
            case Key.Left: SeekBy(-SeekStepSeconds); break;
            case Key.Right: SeekBy(SeekStepSeconds); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Zoom around the cursor, same math as <see cref="PinnedImageWindow"/>: the point
    /// under the mouse stays put while the window grows / shrinks.</summary>
    private void ZoomFromCursor(MouseWheelEventArgs e)
    {
        if (!_opened) return;
        var cursorScreen = PointToScreen(e.GetPosition(this));
        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        var newScale = Math.Clamp(_scale * factor, 0.1, 8.0);
        if (Math.Abs(newScale - _scale) < 1e-4) return;
        var actualFactor = newScale / _scale;
        _scale = newScale;
        ApplySize();

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var topLeftNow = PointToScreen(new Point(0, 0));
            var newTopLeft = new Point(
                cursorScreen.X - (cursorScreen.X - topLeftNow.X) * actualFactor,
                cursorScreen.Y - (cursorScreen.Y - topLeftNow.Y) * actualFactor);
            var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var dip = fromDevice.Transform(newTopLeft);
            Left = dip.X;
            Top = dip.Y;
        });
    }
}
