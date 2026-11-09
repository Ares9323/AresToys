using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AresToys.App.Services.Launcher;

namespace AresToys.App.Services.Peek;

/// <summary>The row of thumbnails under the preview: one per file of the list the arrows walk, the current one framed in
/// the accent colour and kept in the centre, a click on one raises <see cref="FileChosen"/>. The list is virtualised
/// (recycling) and an item asks for its thumbnail only when it is realised on screen; the request goes to two STA
/// worker threads that read the shell thumbnail cache through <see cref="IconService.GetThumbnail"/>, and it is
/// cancelled when the item scrolls away. Decoded thumbnails are kept by path (at most <see cref="CacheLimit"/>) for the
/// control's life. The control never takes the keyboard focus. UI thread only.</summary>
public partial class PeekThumbnailStrip : UserControl
{
    /// <summary>The side of a thumbnail in DIPs.</summary>
    private const double ThumbSize = 56;
    private const int CacheLimit = 300;
    private const int Workers = 2;

    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private BlockingCollection<Job>? _jobs;
    private ThumbItem[] _items = [];
    private ScrollViewer? _scroll;

    public PeekThumbnailStrip()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Center();
        SizeChanged += (_, _) => Center();
    }

    /// <summary>Raised with the index (in the list given to <see cref="SetFiles"/>) of the thumbnail the user clicked.</summary>
    public event Action<int>? FileChosen;

    /// <summary>Gives the strip the files to show and the current one; outstanding loads of the earlier list are cancelled.</summary>
    public void SetFiles(IReadOnlyList<string> files, int index)
    {
        foreach (var item in _items) Cancel(item);
        _items = files.Select(p => new ThumbItem(p)).ToArray();
        List.ItemsSource = _items;
        List.SelectedIndex = index >= 0 && index < _items.Length ? index : -1;
        Center();
    }

    /// <summary>Moves the frame to <paramref name="index"/> and scrolls it into the centre.</summary>
    public void SetCurrent(int index)
    {
        if (index == List.SelectedIndex) return;
        List.SelectedIndex = index >= 0 && index < _items.Length ? index : -1;
        Center();
    }

    /// <summary>The file at <paramref name="path"/> changed on disk (Peek saved a turned image): its cached thumbnail is
    /// dropped and, when its cell is on screen, loaded again.</summary>
    public void Refresh(string path)
    {
        if (_cache.Remove(path))
        {
            var kept = _cacheOrder.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            _cacheOrder.Clear();
            foreach (var p in kept) _cacheOrder.Enqueue(p);
        }

        foreach (var item in _items)
        {
            if (!string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
            Cancel(item);
            item.Source = null;
            if (List.ItemContainerGenerator.ContainerFromItem(item) is not null) Request(item);
        }
    }

    /// <summary>A click on a thumbnail: the window moves to that file, which moves the frame through <see cref="SetCurrent"/>.</summary>
    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        var index = List.ItemContainerGenerator.IndexFromContainer((DependencyObject)sender);
        if (index < 0) return;
        e.Handled = true;
        FileChosen?.Invoke(index);
    }

    private ScrollViewer? FindScroll()
    {
        if (_scroll is not null) return _scroll;
        List.ApplyTemplate();
        _scroll = Descendant<ScrollViewer>(List);
        return _scroll;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }

        return null;
    }

    /// <summary>Scrolls so the current item is in the middle (the scroll offset counts items while content scrolling is on).</summary>
    private void Center()
    {
        if (!IsVisible || List.SelectedIndex < 0 || FindScroll() is not { } scroll) return;
        List.UpdateLayout();
        var viewport = scroll.ViewportWidth;
        if (viewport <= 0) return;
        scroll.ScrollToHorizontalOffset(Math.Max(0, Math.Round(List.SelectedIndex - (viewport - 1) / 2)));
    }

    /// <summary>The wheel scrolls the strip sideways, three items a notch.</summary>
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScroll() is not { } scroll) return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - Math.Sign(e.Delta) * 3);
        e.Handled = true;
    }

    private void OnCellLoaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ThumbItem item) Request(item);
    }

    private void OnCellUnloaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ThumbItem item) Cancel(item);
    }

    /// <summary>A recycled container gets another item: the old one stops loading, the new one starts.</summary>
    private void OnCellDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ThumbItem old) Cancel(old);
        if (e.NewValue is ThumbItem item && ((FrameworkElement)sender).IsLoaded) Request(item);
    }

    private void Request(ThumbItem item)
    {
        if (item.Source is not null || item.Load is not null) return;
        if (_cache.TryGetValue(item.Path, out var cached))
        {
            item.Source = cached;
            return;
        }

        var cts = new CancellationTokenSource();
        item.Load = cts;
        var size = (int)Math.Ceiling(ThumbSize * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var path = item.Path;
        Jobs().Add(new Job(path, size, bitmap => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (cts.IsCancellationRequested) return;
            item.Load = null;
            cts.Dispose();
            if (bitmap is null) return;
            Remember(path, bitmap);
            item.Source = bitmap;
        }), cts.Token));
    }

    private static void Cancel(ThumbItem item)
    {
        if (item.Load is not { } cts) return;
        item.Load = null;
        cts.Cancel();
    }

    private void Remember(string path, BitmapSource bitmap)
    {
        if (_cache.TryAdd(path, bitmap)) _cacheOrder.Enqueue(path);
        while (_cache.Count > CacheLimit && _cacheOrder.TryDequeue(out var oldest)) _cache.Remove(oldest);
    }

    /// <summary>The job stack and its worker threads, made on the first request. Newest first: what the user just
    /// scrolled to is wanted before what scrolled past.</summary>
    private BlockingCollection<Job> Jobs()
    {
        if (_jobs is not null) return _jobs;
        var icons = ((App)Application.Current).Services.GetService(typeof(IconService)) as IconService;
        var jobs = new BlockingCollection<Job>(new ConcurrentStack<Job>());
        for (var i = 0; i < Workers; i++)
        {
            var thread = new Thread(() => Work(jobs, icons)) { IsBackground = true, Name = "PeekThumbnails" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        _jobs = jobs;
        return jobs;
    }

    private static void Work(BlockingCollection<Job> jobs, IconService? icons)
    {
        foreach (var job in jobs.GetConsumingEnumerable())
        {
            if (job.Token.IsCancellationRequested) continue;
            BitmapSource? bitmap = null;
            try
            {
                if (icons is not null && File.Exists(job.Path)) bitmap = icons.GetThumbnail(job.Path, job.SizePx);
            }
            catch
            {
                // A handler that throws leaves the cell empty.
            }

            if (!job.Token.IsCancellationRequested) job.Done(bitmap);
        }
    }

    private sealed record Job(string Path, int SizePx, Action<BitmapSource?> Done, CancellationToken Token);

    private sealed class ThumbItem(string path) : INotifyPropertyChanged
    {
        private BitmapSource? _source;

        public string Path { get; } = path;

        public string Name { get; } = System.IO.Path.GetFileName(path);

        /// <summary>The cancellation of the load in flight, null when none.</summary>
        public CancellationTokenSource? Load { get; set; }

        public BitmapSource? Source
        {
            get => _source;
            set
            {
                _source = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
