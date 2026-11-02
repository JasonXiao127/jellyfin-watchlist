using System.Threading.Channels;
using Jellyfin.Plugin.Watchlist.Logic;
using Jellyfin.Plugin.Watchlist.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlist.Services;

/// <summary>
/// Subscribes to user data changes and processes them on a background worker.
/// </summary>
public sealed class WatchlistHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReinitializeDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LibraryChangedDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LibraryQuietPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LibraryScanCooldown = TimeSpan.FromMinutes(1);

    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly WatchlistService _watchlistService;
    private readonly ILogger<WatchlistHostedService> _logger;
    private readonly Channel<WorkItem> _channel = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(5000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly object _configSync = new();
    private readonly object _scanSync = new();
    private Task? _worker;
    private Plugin? _subscribedPlugin;
    private bool _subscribedUserData;
    private bool _subscribedLibrary;
    private bool _disposed;
    private long _lastLibraryChangeTicks;
    private long _lastLibraryScanTicks;
    private bool _libraryScanRequested;
    private bool _scanPumpActive;
    private Task? _scanPumpTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistHostedService"/> class.
    /// </summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="watchlistService">The watchlist service.</param>
    /// <param name="logger">The logger.</param>
    public WatchlistHostedService(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        WatchlistService watchlistService,
        ILogger<WatchlistHostedService> logger)
    {
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _watchlistService = watchlistService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => ProcessAsync(_cancellationTokenSource.Token), cancellationToken);

        // Queue the startup scan before subscribing so events that arrive
        // after startup wait behind the baseline instead of racing it.
        // Events from before the plugin started are covered by the scan.
        _channel.Writer.TryWrite(WorkItem.Startup);

        _userDataManager.UserDataSaved += OnUserDataSaved;
        _subscribedUserData = true;

        // Refresh baselines after library scans add, update, or remove
        // episodes, movies, or series. The worker coalesces bursts into a
        // single delayed reconciliation.
        _libraryManager.ItemAdded += OnLibraryItemChanged;
        _libraryManager.ItemUpdated += OnLibraryItemChanged;
        _libraryManager.ItemRemoved += OnLibraryItemChanged;
        _subscribedLibrary = true;

        TrySubscribeToConfig();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribedUserData)
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
            _subscribedUserData = false;
        }

        if (_subscribedLibrary)
        {
            _libraryManager.ItemAdded -= OnLibraryItemChanged;
            _libraryManager.ItemUpdated -= OnLibraryItemChanged;
            _libraryManager.ItemRemoved -= OnLibraryItemChanged;
            _subscribedLibrary = false;
        }

        if (_subscribedPlugin is not null)
        {
            _subscribedPlugin.ConfigurationChanged -= OnConfigurationChanged;
            _subscribedPlugin = null;
        }
        else if (Plugin.Instance is { } current)
        {
            current.ConfigurationChanged -= OnConfigurationChanged;
        }

        // Drain queued work before cancelling so tracked completions are not
        // lost when reconciliation is disabled. Fall back to cancellation if
        // the drain takes too long.
        _channel.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The server is shutting down.
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Watchlist: background worker did not stop within the timeout; cancelling.");
                _cancellationTokenSource.Cancel();
                try
                {
                    await _worker.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Watchlist: background worker did not stop after cancellation.");
                }
            }
        }

        // A library scan may still be waiting out its quiet period. Give it a
        // chance to finish so a pending baseline refresh is not lost.
        Task? scanPump;
        lock (_scanSync)
        {
            scanPump = _scanPumpTask;
        }

        if (scanPump is not null)
        {
            try
            {
                await scanPump.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Watchlist: background library scan did not stop within the timeout; cancelling.");
                _cancellationTokenSource.Cancel();
                try
                {
                    await scanPump.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Watchlist: background library scan did not stop after cancellation.");
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_subscribedUserData)
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
            _subscribedUserData = false;
        }

        if (_subscribedLibrary)
        {
            _libraryManager.ItemAdded -= OnLibraryItemChanged;
            _libraryManager.ItemUpdated -= OnLibraryItemChanged;
            _libraryManager.ItemRemoved -= OnLibraryItemChanged;
            _subscribedLibrary = false;
        }

        if (_subscribedPlugin is not null)
        {
            _subscribedPlugin.ConfigurationChanged -= OnConfigurationChanged;
            _subscribedPlugin = null;
        }
        else if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        // Stop the worker even when the host disposes without stopping first.
        // There is nothing to await here; StopAsync still owns the drain.
        _channel.Writer.TryComplete();
        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Subscribes to plugin configuration changes when the plugin instance is
    /// available. Retried on every work batch because the instance may not
    /// exist yet when the hosted service starts.
    /// </summary>
    private void TrySubscribeToConfig()
    {
        lock (_configSync)
        {
            if (_subscribedPlugin is not null)
            {
                return;
            }

            if (Plugin.Instance is { } plugin)
            {
                plugin.ConfigurationChanged += OnConfigurationChanged;
                _subscribedPlugin = plugin;
            }
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        if (eventArgs.Item is null || eventArgs.UserData is null)
        {
            return;
        }

        // Playback start cannot complete an item. Progress updates are frequent,
        // but Jellyfin also flips Played to true from a progress update at the
        // completion threshold, so process progress only once it reports played.
        if (eventArgs.SaveReason == UserDataSaveReason.PlaybackStart)
        {
            return;
        }

        if (eventArgs.SaveReason == UserDataSaveReason.PlaybackProgress && !eventArgs.UserData.Played)
        {
            return;
        }

        // An episode's own favorite/rating change does not affect whether its
        // parent series belongs on the watchlist.
        if (eventArgs.Item is Episode && eventArgs.SaveReason == UserDataSaveReason.UpdateUserRating)
        {
            return;
        }

        var kind = eventArgs.Item switch
        {
            Movie => WatchlistItemKind.Movie,
            Series => WatchlistItemKind.Series,
            Season => WatchlistItemKind.Season,
            Episode => WatchlistItemKind.Episode,
            _ => WatchlistItemKind.Other
        };

        if (kind == WatchlistItemKind.Other)
        {
            return;
        }

        var seriesId = Guid.Empty;
        var seasonId = Guid.Empty;
        if (eventArgs.Item is Episode episode)
        {
            seriesId = episode.SeriesId;
            if (seriesId == Guid.Empty)
            {
                try
                {
                    seriesId = episode.FindSeriesId();
                }
                catch
                {
                    seriesId = Guid.Empty;
                }
            }

            seasonId = episode.SeasonId;
            if (seasonId == Guid.Empty)
            {
                try
                {
                    seasonId = episode.FindSeasonId();
                }
                catch
                {
                    seasonId = Guid.Empty;
                }
            }
        }

        var change = new UserDataChange(
            eventArgs.UserId,
            eventArgs.Item.Id,
            kind,
            seriesId,
            seasonId,
            eventArgs.UserData.IsFavorite,
            eventArgs.UserData.Played,
            eventArgs.SaveReason);

        if (!_channel.Writer.TryWrite(new WorkItem(change, TimeSpan.Zero, ScanKind.None)))
        {
            // TryWrite only fails once the writer is completed during shutdown.
            _logger.LogDebug("Watchlist: shutting down; dropping an event.");
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        // Rebuild the observations under the new completion rules. The service
        // also clears them when the options signature changes.
        _channel.Writer.TryWrite(WorkItem.Reinitialize);
    }

    private void OnLibraryItemChanged(object? sender, ItemChangeEventArgs eventArgs)
    {
        // New or updated episodes change what "complete" means for their
        // series, and removed items change fingerprints. Queue a delayed
        // reconciliation so baselines catch up even if no user-data event
        // follows. Library scans run on a separate pump so a quiet-period
        // wait never holds user-data events hostage.
        if (eventArgs.Item is Episode or Series or Season or Movie)
        {
            Volatile.Write(ref _lastLibraryChangeTicks, DateTime.UtcNow.Ticks);
            _channel.Writer.TryWrite(WorkItem.LibraryChanged);
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var first in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    // Coalesce everything already queued: one scan covers all
                    // pending scan requests. For events, keep the first and
                    // last rating event plus the latest non-rating event per
                    // item. Handlers re-read current state, but the
                    // favorite-while-unplayed baseline needs the rating event
                    // itself, and a later play must still be seen if another
                    // rating follows it. Grouping episode events per series
                    // happens inside the service through the single batched
                    // call.
                    var batch = new List<WorkItem> { first };
                    while (_channel.Reader.TryRead(out var extra))
                    {
                        batch.Add(extra);
                    }

                    TrySubscribeToConfig();

                    // Startup and reinitialization scans run inline: the startup
                    // scan must establish baselines before post-startup events
                    // are handled, and reinitialization is rare. Library scans
                    // are scheduled on the scan pump instead, so waiting out
                    // the library quiet period never holds user-data events
                    // hostage. Scans and events serialize inside the service.
                    TimeSpan? blockingDelay = null;
                    var hasLibraryScan = false;
                    foreach (var item in batch)
                    {
                        if (item.Change is null)
                        {
                            if (item.ScanKind == ScanKind.LibraryChanged)
                            {
                                hasLibraryScan = true;
                            }
                            else
                            {
                                blockingDelay = blockingDelay is null ? item.Delay : Max(blockingDelay.Value, item.Delay);
                            }
                        }
                    }

                    // Process user-data events before any delayed scan so a
                    // library refresh never holds favorite/play transitions
                    // hostage. Scans only re-baseline; evaluations read the
                    // current library state directly.
                    var changes = new List<UserDataChange>();
                    foreach (var item in batch)
                    {
                        var change = item.Change;
                        if (change is not null)
                        {
                            changes.Add(change);
                        }
                    }

                    var ordered = UserDataChangeBatchSelector.Select(changes);

                    if (ordered.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await _watchlistService
                            .HandleUserDataChangesAsync(ordered, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (blockingDelay is not null)
                    {
                        await Task.Delay(blockingDelay.Value, cancellationToken).ConfigureAwait(false);
                        await _watchlistService
                            .ReconcileAsync(progress: null, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (hasLibraryScan)
                    {
                        ScheduleLibraryScan();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Watchlist: background work item failed.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Schedules a library reconciliation on the scan pump without blocking
    /// the event loop. Bursts coalesce: at most one pump runs at a time and
    /// it re-checks for new requests after every scan, so no request is lost
    /// and user-data events are never stuck behind a quiet-period wait.
    /// </summary>
    private void ScheduleLibraryScan()
    {
        lock (_scanSync)
        {
            _libraryScanRequested = true;
            if (_scanPumpActive)
            {
                return;
            }

            _scanPumpActive = true;
            _scanPumpTask = Task.Run(() => LibraryScanPumpAsync(_cancellationTokenSource.Token));
        }
    }

    private async Task LibraryScanPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                lock (_scanSync)
                {
                    if (!_libraryScanRequested)
                    {
                        return;
                    }

                    _libraryScanRequested = false;
                }

                try
                {
                    await WaitForLibraryScanAsync(LibraryChangedDelay, cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _lastLibraryScanTicks, DateTime.UtcNow.Ticks);
                    await _watchlistService
                        .ReconcileAsync(progress: null, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Watchlist: background library scan failed.");
                }
            }
        }
        finally
        {
            // Close the race where a request lands after the final check:
            // re-check under the same lock the scheduler uses, and respawn
            // if needed. Never respawn while shutting down.
            bool restart;
            lock (_scanSync)
            {
                restart = _libraryScanRequested && !cancellationToken.IsCancellationRequested;
                if (!restart)
                {
                    _scanPumpActive = false;
                    _scanPumpTask = null;
                }
            }

            if (restart)
            {
                lock (_scanSync)
                {
                    _scanPumpActive = true;
                    _scanPumpTask = Task.Run(() => LibraryScanPumpAsync(cancellationToken));
                }
            }
        }
    }

    private async Task WaitForLibraryScanAsync(TimeSpan initialDelay, CancellationToken cancellationToken)
    {
        var delay = initialDelay;
        while (true)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var now = DateTime.UtcNow;

            // Wait for the library to go quiet so a scan does not run against
            // a partial listing while files are still being added or removed.
            var lastChangeTicks = Volatile.Read(ref _lastLibraryChangeTicks);
            if (lastChangeTicks != 0)
            {
                var quietRemaining = LibraryQuietPeriod - (now - new DateTime(lastChangeTicks, DateTimeKind.Utc));
                if (quietRemaining > TimeSpan.Zero)
                {
                    delay = quietRemaining;
                    continue;
                }
            }

            // Keep a minimum gap between library-triggered scans even when a
            // long scan keeps emitting events just outside the quiet window.
            var lastScanTicks = Volatile.Read(ref _lastLibraryScanTicks);
            if (lastScanTicks != 0)
            {
                var cooldownRemaining = LibraryScanCooldown - (now - new DateTime(lastScanTicks, DateTimeKind.Utc));
                if (cooldownRemaining > TimeSpan.Zero)
                {
                    delay = cooldownRemaining;
                    continue;
                }
            }

            return;
        }
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second)
    {
        return first >= second ? first : second;
    }

    private enum ScanKind
    {
        None = 0,
        LibraryChanged = 1,
        Reinitialize = 2,
        Startup = 3
    }

    private readonly record struct WorkItem(UserDataChange? Change, TimeSpan Delay, ScanKind ScanKind)
    {
        public static WorkItem Startup => new(null, StartupDelay, ScanKind.Startup);

        public static WorkItem Reinitialize => new(null, ReinitializeDelay, ScanKind.Reinitialize);

        public static WorkItem LibraryChanged => new(null, LibraryChangedDelay, ScanKind.LibraryChanged);
    }
}
