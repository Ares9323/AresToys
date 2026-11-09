using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AresToys.App.Views;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _autoCloseTimer;
    private readonly Action? _onClick;
    private bool _closing;

    public ToastWindow(string title, string message, TimeSpan duration, Action? onClick)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        _onClick = onClick;
        if (onClick is null) Root.Cursor = Cursors.Arrow;

        _autoCloseTimer = new DispatcherTimer { Interval = duration };
        _autoCloseTimer.Tick += (_, _) => BeginClose();
        Loaded += (_, _) =>
        {
            BeginIn();
            _autoCloseTimer.Start();
        };
    }

    public event EventHandler? Dismissed;

    private void BeginIn()
    {
        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _autoCloseTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { Dismissed?.Invoke(this, EventArgs.Empty); Close(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        if (_onClick is null) return;
        try { _onClick(); }
        catch { /* host's responsibility; don't crash the toast */ }
        BeginClose();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => BeginClose();
}
