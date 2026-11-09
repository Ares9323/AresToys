using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Items;
using AresToys.Storage.Rotation;

namespace AresToys.App.Services;

/// <summary>
/// Periodic background sweep that runs <see cref="CategoryRotationService.RunAsync"/> on a 30s
/// tick. Catches the time-based <c>auto_cleanup_after</c> cap (an item that's been sitting
/// untouched for N minutes won't trigger any add-time hook, so the timer is the only path
/// that ever soft-deletes it).
///
/// Timer cadence is conservative: 30s is enough to feel "live" in the UI without spamming
/// SQLite when the user has 10 categories with caps. The MaxItems cap is enforced earlier on
/// add (in <see cref="ItemStore.AddAsync"/>); this loop is the safety net.
///
/// On every successful sweep that actually deleted anything we also notify the item store so
/// any open popup re-queries its list immediately. ItemStore raises <c>ItemsChanged</c> for
/// other reasons too — re-using the same channel keeps the popup wiring single-handler.
/// </summary>
public sealed class CategoryRotationScheduler : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly CategoryRotationService _rotation;
    private readonly IItemStore _items;
    private readonly ILogger<CategoryRotationScheduler> _logger;

    public CategoryRotationScheduler(CategoryRotationService rotation, IItemStore items, ILogger<CategoryRotationScheduler> logger)
    {
        _rotation = rotation;
        _items = items;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First tick after a small delay so we don't fight startup contention with the seed
        // / migration step. Then steady cadence via PeriodicTimer (more accurate than Task.Delay
        // because it accounts for the previous tick's runtime).
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            try
            {
                var deleted = await _rotation.RunAsync(stoppingToken).ConfigureAwait(false);
                deleted += await _rotation.PurgeAsync(stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                {
                    _logger.LogDebug("CategoryRotationScheduler: deleted {Count} items (soft or purged)", deleted);
                    // Negative id signals "I touched many rows" — popup VMs ignore the id and
                    // refresh wholesale (matches how the other bulk deletes notify).
                    if (_items is ItemStore concrete)
                        concrete.RaiseItemsChanged(new ItemsChangedEventArgs(ItemsChangeKind.Deleted, -1));
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CategoryRotationScheduler: sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
