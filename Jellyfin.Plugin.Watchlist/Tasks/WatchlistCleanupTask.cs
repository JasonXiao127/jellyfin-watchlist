using Jellyfin.Plugin.Watchlist.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Watchlist.Tasks;

/// <summary>
/// Scheduled reconciliation for missed user data events.
/// </summary>
public sealed class WatchlistCleanupTask : IScheduledTask
{
    private readonly WatchlistService _watchlistService;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistCleanupTask"/> class.
    /// </summary>
    /// <param name="watchlistService">The watchlist service.</param>
    public WatchlistCleanupTask(WatchlistService watchlistService)
    {
        _watchlistService = watchlistService;
    }

    /// <inheritdoc />
    public string Name => "Watchlist cleanup";

    /// <inheritdoc />
    public string Key => "WatchlistCleanup";

    /// <inheritdoc />
    public string Description =>
        "Removes completed movies and series from user Favorites according to the Watchlist settings.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Jellyfin does not distinguish scheduled from manual runs here, so the
        // task honors the reconciliation setting in both cases. Re-enable the
        // setting to run the cleanup on demand.
        return _watchlistService.ReconcileAsync(progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
