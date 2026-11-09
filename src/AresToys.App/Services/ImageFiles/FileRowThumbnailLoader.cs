using System.Collections.Concurrent;

namespace AresToys.App.Services.ImageFiles;

/// <summary>Builds row thumbnails for Files items that have none (captured before Files
/// thumbnails existed, or whose ingestion couldn't read the file). One background worker drains
/// a queue, so a long history never decodes many files at once and the UI thread never decodes
/// at all. Each item is attempted once per session: a missing or broken file is not retried on
/// every list refresh.</summary>
public sealed class FileRowThumbnailLoader
{
    private readonly Func<IReadOnlyList<string>, byte[]?> _generate;
    private readonly Action<long, byte[]> _onReady;
    private readonly ConcurrentDictionary<long, byte> _attempted = new();
    private readonly ConcurrentQueue<(long Id, IReadOnlyList<string> Paths)> _queue = new();
    private int _draining;

    /// <param name="onReady">Invoked on a worker thread with the item id and its PNG thumbnail;
    /// the caller marshals to the UI and persists it.</param>
    /// <param name="generate">Thumbnail factory, <see cref="FileThumbnails.TryGenerate"/> by
    /// default (replaceable in tests).</param>
    public FileRowThumbnailLoader(Action<long, byte[]> onReady, Func<IReadOnlyList<string>, byte[]?>? generate = null)
    {
        _onReady = onReady ?? throw new ArgumentNullException(nameof(onReady));
        _generate = generate ?? (paths => FileThumbnails.TryGenerate(paths));
    }

    /// <summary>Queue a thumbnail for the item unless it was already attempted.</summary>
    public void Request(long itemId, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (FileThumbnails.FirstImagePath(paths) is null) return;
        if (!_attempted.TryAdd(itemId, 0)) return;
        _queue.Enqueue((itemId, paths));
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
            _ = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            try
            {
                while (_queue.TryDequeue(out var job))
                {
                    byte[]? thumb;
                    try { thumb = _generate(job.Paths); }
                    catch (Exception) { thumb = null; } // a thumbnail is cosmetic: skip the row, keep draining
                    if (thumb is { Length: > 0 }) _onReady(job.Id, thumb);
                }
            }
            finally
            {
                Volatile.Write(ref _draining, 0);
            }
            // A Request that enqueued after the inner loop ended but saw _draining == 1 relies on
            // this check to get its job picked up.
            if (_queue.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        }
    }
}
