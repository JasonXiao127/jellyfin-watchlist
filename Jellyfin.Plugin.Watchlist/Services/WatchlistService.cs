using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchlist.Logic;
using Jellyfin.Plugin.Watchlist.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlist.Services;

/// <summary>
/// Implements the watchlist rules against Jellyfin Favorites.
/// </summary>
public sealed class WatchlistService : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchlistStateStore _stateStore;
    private readonly ILogger<WatchlistService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="stateStore">The observation store.</param>
    /// <param name="logger">The logger.</param>
    public WatchlistService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        WatchlistStateStore stateStore,
        ILogger<WatchlistService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _stateStore = stateStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>
    /// Scans every user's favorites and removes completed items.
    /// </summary>
    /// <param name="progress">Optional progress reporting.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the scan.</returns>
    public async Task ReconcileAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("Watchlist: plugin configuration is unavailable; skipping reconciliation.");
                return;
            }

            var options = WatchlistOptions.From(config);
            _stateStore.EnsureOptions(options.Signature);

            var users = _userManager.GetUsers().ToList();
            var validUserIds = users.Select(user => user.Id).ToHashSet();

            if (!options.EnableReconciliation)
            {
                _logger.LogInformation("Watchlist: reconciliation is disabled by configuration; skipping scan. Re-enable it to run scans.");
                _stateStore.PruneUsers(validUserIds);
                return;
            }

            if (!options.AutoRemoveMovies && !options.AutoRemoveSeries)
            {
                _logger.LogDebug("Watchlist: both automatic removal options are disabled; skipping reconciliation.");
                _stateStore.PruneUsers(validUserIds);
                return;
            }

            var removed = 0;

            for (var index = 0; index < users.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var user = users[index];
                try
                {
                    removed += ReconcileUser(user, options, cancellationToken, out var aborted);
                    if (aborted)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Watchlist: failed to reconcile favorites for user '{UserName}'.",
                        user.Username);
                }

                progress?.Report((index + 1) * 100.0 / users.Count);
            }

            _stateStore.PruneUsers(validUserIds);

            _logger.LogInformation(
                "Watchlist: reconciliation finished. Users checked: {UserCount}; favorites removed: {RemovedCount}.",
                users.Count,
                removed);

            progress?.Report(100);
        }
        finally
        {
            _stateStore.Save();
            _gate.Release();
        }
    }

    /// <summary>
    /// Handles one copied user data event.
    /// </summary>
    /// <param name="change">The event data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the event handling.</returns>
    public Task HandleUserDataChangeAsync(
        UserDataChange change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        return HandleUserDataChangesAsync([change], cancellationToken);
    }

    /// <summary>
    /// Handles copied user data events, persisting state once for the batch.
    /// </summary>
    /// <param name="changes">The event data, in arrival order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the event handling.</returns>
    public async Task HandleUserDataChangesAsync(
        IReadOnlyList<UserDataChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return;
            }

            var options = WatchlistOptions.From(config);
            _stateStore.EnsureOptions(options.Signature);

            // Collapse episode events to the latest one per series: handling
            // an episode re-evaluates its whole series, so one pass per series
            // covers the batch. Movie and series changes are never collapsed;
            // the caller preserves the favorite/rating change they need.
            var deduped = new List<UserDataChange>(changes.Count);
            var seenEpisodeSeries = new HashSet<(Guid UserId, Guid SeriesId)>();
            for (var index = changes.Count - 1; index >= 0; index--)
            {
                var candidate = changes[index];
                if (candidate is null)
                {
                    continue;
                }

                if (candidate.Kind == WatchlistItemKind.Episode
                    && candidate.SeriesId != Guid.Empty
                    && !seenEpisodeSeries.Add((candidate.UserId, candidate.SeriesId)))
                {
                    continue;
                }

                deduped.Add(candidate);
            }

            deduped.Reverse();

            var users = new Dictionary<Guid, User?>();
            var dirty = false;

            foreach (var change in deduped)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (change is null)
                {
                    continue;
                }

                if (!users.TryGetValue(change.UserId, out var user))
                {
                    user = _userManager.GetUserById(change.UserId);
                    users[change.UserId] = user;
                    if (user is null)
                    {
                        _stateStore.RemoveUser(change.UserId);
                        dirty = true;
                        continue;
                    }
                }
                else if (user is null)
                {
                    continue;
                }

                try
                {
                    if (HandleChange(user, change, options, cancellationToken))
                    {
                        dirty = true;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Watchlist: failed to process a user data change for item {ItemId} and user '{UserName}'.",
                        change.ItemId,
                        user.Username);
                }
            }

            if (dirty)
            {
                _stateStore.Save();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private int ReconcileUser(
        User user,
        WatchlistOptions options,
        CancellationToken cancellationToken,
        out bool aborted)
    {
        aborted = false;
        var favorites = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IsFavorite = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true
        });

        var allMovies = favorites.OfType<Movie>().ToList();
        var allSeries = favorites.OfType<Series>().ToList();
        var removed = 0;

        foreach (var movie in allMovies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Re-read the configuration for every item so enabling test
                // mode or disabling reconciliation mid-scan takes effect for
                // the remaining items instead of only for removals.
                if (!RefreshOptionsForScan(ref options, out aborted))
                {
                    return removed;
                }

                // Iterate every favorite even when movies are disabled: the
                // setting may be enabled again while this scan is running.
                if (!options.AutoRemoveMovies)
                {
                    continue;
                }

                if (IsExempt(movie, options))
                {
                    _stateStore.RemoveMovie(user.Id, movie.Id);
                    continue;
                }

                var data = _userDataManager.GetUserData(user, movie);
                if (data is null)
                {
                    _logger.LogWarning(
                        "Watchlist: user data unavailable for movie '{MovieName}'; skipping without changing state.",
                        movie.Name);
                    continue;
                }

                var currentPlayed = data.Played;
                var observed = _stateStore.GetMoviePlayed(user.Id, movie.Id);

                if (RemovalPolicy.ShouldRemoveMovie(observed, currentPlayed))
                {
                    if (options.DryRun)
                    {
                        LogWouldRemove(user, movie);
                        continue;
                    }

                    if (!ConfirmRemovalStillWanted(user, movie, options))
                    {
                        continue;
                    }

                    if (Unfavorite(user, movie))
                    {
                        removed++;
                    }

                    _stateStore.RemoveMovie(user.Id, movie.Id);
                }
                else if (!options.DryRun)
                {
                    _stateStore.SetMoviePlayed(user.Id, movie.Id, currentPlayed);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Watchlist: failed to reconcile movie '{MovieName}' for user '{UserName}'.",
                    movie.Name,
                    user.Username);
            }
        }

        foreach (var show in allSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!RefreshOptionsForScan(ref options, out aborted))
                {
                    return removed;
                }

                // As with movies, keep iterating so enabling series removal
                // mid-scan takes effect for the remaining items.
                if (!options.AutoRemoveSeries)
                {
                    continue;
                }

                if (IsExempt(show, options))
                {
                    _stateStore.RemoveSeries(user.Id, show.Id);
                    continue;
                }

                var current = EvaluateSeries(user, show, options);
                var observed = _stateStore.GetSeriesObservation(user.Id, show.Id);

                if (RemovalPolicy.ShouldRemoveSeries(observed, current))
                {
                    if (options.DryRun)
                    {
                        LogWouldRemove(user, show);
                        continue;
                    }

                    if (!ConfirmRemovalStillWanted(user, show, options))
                    {
                        continue;
                    }

                    if (Unfavorite(user, show))
                    {
                        removed++;
                    }

                    _stateStore.RemoveSeries(user.Id, show.Id);
                }
                else if (!options.DryRun && RemovalPolicy.ShouldReplaceSeriesObservation(observed, current))
                {
                    _stateStore.SetSeriesObservation(
                        user.Id,
                        show.Id,
                        ToObservation(current));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Watchlist: failed to reconcile series '{SeriesName}' for user '{UserName}'.",
                    show.Name,
                    user.Username);
            }
        }

        _stateStore.PruneUser(
            user.Id,
            allMovies.Select(movie => movie.Id).ToHashSet(),
            allSeries.Select(show => show.Id).ToHashSet());

        return removed;
    }

    private bool HandleChange(
        User user,
        UserDataChange change,
        WatchlistOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var item = _libraryManager.GetItemById(change.ItemId);
        if (item is null)
        {
            _stateStore.RemoveItem(user.Id, change.ItemId);
            return true;
        }

        // An episode's own favorite flag is irrelevant. The watchlist entry is
        // the parent series, so episode play-state changes must still be handled
        // even when the episode itself was never favorited.
        if (item is Episode episode)
        {
            return HandleEpisode(user, episode, change, options, cancellationToken);
        }

        if (item is not (Movie or Series))
        {
            return false;
        }

        // Use the current favorite state plus the event snapshot, with a
        // fresh library query when they disagree or both say "not favorite".
        // Cached user data can stay stale after a library scan (single-item
        // lookups report IsFavorite=false while the list query and the
        // database report true); trusting the stale false would drop a valid
        // baseline and leave the favorite in place. Resolve it once here and
        // hand the result to the handler instead of querying again for movies.
        var data = _userDataManager.GetUserData(user, item);
        if (data is null)
        {
            return false;
        }

        if (!IsCurrentlyFavorite(user, item, data.IsFavorite, change.IsFavorite))
        {
            _stateStore.RemoveItem(user.Id, item.Id);
            return true;
        }

        return item switch
        {
            Movie movie => HandleMovie(user, movie, change, options, data),
            Series series => HandleSeries(user, series, change, options),
            _ => false
        };
    }

    private bool HandleMovie(
        User user,
        Movie movie,
        UserDataChange change,
        WatchlistOptions options,
        UserItemData data)
    {
        if (!options.AutoRemoveMovies || IsExempt(movie, options))
        {
            // A disabled type keeps its observations so re-enabling resumes
            // tracking; exempt items are still untracked while tagged.
            if (IsExempt(movie, options))
            {
                _stateStore.RemoveMovie(user.Id, movie.Id);
                return true;
            }

            return false;
        }

        // Favorite and rating updates share the same save reason, so a rating
        // change must never remove or advance tracking. Only a first sighting
        // or a changed play snapshot updates the baseline, using the event
        // snapshot: by the time a queued favorite event is handled the movie
        // may already be played, and re-reading the live state would lose the
        // favorite-while-unplayed transition.
        // Removals happen on playback, manual mark-played, and scans.
        if (change.SaveReason == UserDataSaveReason.UpdateUserRating)
        {
            if (options.DryRun)
            {
                return false;
            }

            var observedPlayed = _stateStore.GetMoviePlayed(user.Id, movie.Id);
            if (observedPlayed is null || change.Played != observedPlayed.Value)
            {
                _stateStore.SetMoviePlayed(user.Id, movie.Id, change.Played);
                return true;
            }

            return false;
        }

        var currentPlayed = data.Played;
        var observed = _stateStore.GetMoviePlayed(user.Id, movie.Id);

        if (observed is null)
        {
            if (options.DryRun)
            {
                return false;
            }

            _stateStore.SetMoviePlayed(user.Id, movie.Id, currentPlayed);
            return true;
        }

        if (!currentPlayed)
        {
            if (options.DryRun)
            {
                return false;
            }

            if (observed.Value)
            {
                _stateStore.SetMoviePlayed(user.Id, movie.Id, false);
                return true;
            }

            return false;
        }

        if (!RemovalPolicy.ShouldRemoveMovie(observed, true))
        {
            return false;
        }

        if (options.DryRun)
        {
            LogWouldRemove(user, movie);
            return false;
        }

        if (!ConfirmRemovalStillWanted(user, movie, options))
        {
            return false;
        }

        if (Unfavorite(user, movie, isKnownFavorite: true))
        {
            _stateStore.RemoveMovie(user.Id, movie.Id);
            return true;
        }

        _stateStore.RemoveMovie(user.Id, movie.Id);
        return true;
    }

    private bool HandleSeries(
        User user,
        Series series,
        UserDataChange change,
        WatchlistOptions options)
    {
        if (!options.AutoRemoveSeries || IsExempt(series, options))
        {
            // A disabled type keeps its observations so re-enabling resumes
            // tracking; exempt items are still untracked while tagged.
            if (IsExempt(series, options))
            {
                _stateStore.RemoveSeries(user.Id, series.Id);
                return true;
            }

            return false;
        }

        // Favorite and rating updates share the same save reason. Baseline on
        // those events instead of removing: re-starring a completed series
        // must keep it, and a rating change must not remove it. Playback,
        // manual mark-played, and scans handle removals. An existing baseline
        // is only replaced when the new result qualifies, so rating a series
        // during a rewatch or a library outage cannot re-arm removal or shrink
        // the baseline that protects a kept series.
        if (change.SaveReason == UserDataSaveReason.UpdateUserRating)
        {
            if (options.DryRun)
            {
                return false;
            }

            var rated = EvaluateSeries(user, series, options);
            var previous = _stateStore.GetSeriesObservation(user.Id, series.Id);
            if (previous is null || RemovalPolicy.ShouldReplaceSeriesObservation(previous, rated))
            {
                _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(rated));
                return true;
            }

            return false;
        }

        var current = EvaluateSeries(user, series, options);
        var observed = _stateStore.GetSeriesObservation(user.Id, series.Id);

        if (observed is null)
        {
            if (options.DryRun)
            {
                return false;
            }

            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(current));
            return true;
        }

        if (!RemovalPolicy.ShouldRemoveSeries(observed, current))
        {
            if (options.DryRun || !RemovalPolicy.ShouldReplaceSeriesObservation(observed, current))
            {
                return false;
            }

            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(current));
            return true;
        }

        if (options.DryRun)
        {
            LogWouldRemove(user, series);
            return false;
        }

        if (!ConfirmRemovalStillWanted(user, series, options))
        {
            return false;
        }

        if (Unfavorite(user, series, isKnownFavorite: true))
        {
            _stateStore.RemoveSeries(user.Id, series.Id);
            return true;
        }

        _stateStore.RemoveSeries(user.Id, series.Id);
        return true;
    }

    private bool HandleEpisode(
        User user,
        Episode episode,
        UserDataChange change,
        WatchlistOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var seriesId = change.SeriesId == Guid.Empty ? episode.FindSeriesId() : change.SeriesId;
        var series = seriesId == Guid.Empty
            ? null
            : _libraryManager.GetItemById(seriesId) as Series;

        if (series is null)
        {
            if (seriesId != Guid.Empty)
            {
                _stateStore.RemoveSeries(user.Id, seriesId);
                return true;
            }

            return false;
        }

        var seriesData = _userDataManager.GetUserData(user, series);
        if (seriesData is null)
        {
            return false;
        }

        // The episode's own favorite flag is irrelevant here; only the parent
        // series matters. Ignore the event snapshot and resolve the series
        // favorite with a fresh library query when the cached value says
        // "not favorite", since it can stay stale after a library scan.
        if (!IsCurrentlyFavorite(user, series, seriesData.IsFavorite, eventIsFavorite: null))
        {
            _stateStore.RemoveSeries(user.Id, series.Id);
            return true;
        }

        if (!options.AutoRemoveSeries || IsExempt(series, options))
        {
            // A disabled type keeps its observations so re-enabling resumes
            // tracking; exempt items are still untracked while tagged.
            if (IsExempt(series, options))
            {
                _stateStore.RemoveSeries(user.Id, series.Id);
                return true;
            }

            return false;
        }

        var current = EvaluateSeries(user, series, options);
        var observed = _stateStore.GetSeriesObservation(user.Id, series.Id);

        if (observed is null)
        {
            if (options.DryRun)
            {
                return false;
            }

            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(current));
            return true;
        }

        if (!RemovalPolicy.ShouldRemoveSeries(observed, current))
        {
            if (options.DryRun || !RemovalPolicy.ShouldReplaceSeriesObservation(observed, current))
            {
                return false;
            }

            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(current));
            return true;
        }

        if (options.DryRun)
        {
            LogWouldRemove(user, series);
            return false;
        }

        if (!ConfirmRemovalStillWanted(user, series, options))
        {
            return false;
        }

        if (Unfavorite(user, series, isKnownFavorite: true))
        {
            _stateStore.RemoveSeries(user.Id, series.Id);
            return true;
        }

        _stateStore.RemoveSeries(user.Id, series.Id);
        return true;
    }

    private CompletionResult EvaluateSeries(User user, Series series, WatchlistOptions options)
    {
        // Only latest-season mode needs missing and virtual episodes, so an
        // announced future season with no files yet stays visible to the
        // newest-season guard. Other modes ignore them entirely to avoid
        // evaluating placeholders that cannot be watched.
        var includeMissingEpisodes = options.Mode == CompletionMode.LatestSeasonWatched;
        var episodes = series
            .GetEpisodes(user, new DtoOptions(false), shouldIncludeMissingEpisodes: includeMissingEpisodes)
            .OfType<Episode>()
            .ToList();

        // Missing episodes have no meaningful user data to fetch.
        var presentEpisodes = episodes
            .Where(episode => !episode.IsMissingEpisode && !episode.IsVirtualItem)
            .Cast<BaseItem>()
            .ToList();
        var userData = presentEpisodes.Count == 0
            ? new Dictionary<Guid, UserItemData>()
            : _userDataManager.GetUserDataBatch(presentEpisodes, user);

        var snapshots = new List<EpisodeSnapshot>(episodes.Count);
        foreach (var episode in episodes)
        {
            var isMissing = episode.IsMissingEpisode || episode.IsVirtualItem;
            userData.TryGetValue(episode.Id, out var episodeData);
            snapshots.Add(new EpisodeSnapshot(
                episode.ParentIndexNumber,
                episode.PremiereDate,
                isMissing,
                episodeData?.Played == true,
                episode.Id));
        }

        var isEnded = SeriesStatusEvaluator.IsEnded(
            series.Status,
            options.TreatUnknownSeriesStatusAsEnded);

        return CompletionEvaluator.Evaluate(
            new SeriesSnapshot(isEnded, snapshots),
            options,
            DateTime.UtcNow);
    }

    /// <summary>
    /// Re-reads the current configuration before a removal and skips when a
    /// safety setting changed mid-scan. A long scan must not keep removing
    /// under stale options. Signature changes abort the scan separately.
    /// </summary>
    private bool ConfirmRemovalStillWanted(User user, BaseItem item, WatchlistOptions options)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return true;
        }

        var fresh = WatchlistOptions.From(config);
        if (fresh.DryRun != options.DryRun
            || fresh.AutoRemoveMovies != options.AutoRemoveMovies
            || fresh.AutoRemoveSeries != options.AutoRemoveSeries
            || !string.Equals(fresh.ExemptTag, options.ExemptTag, StringComparison.OrdinalIgnoreCase)
            || IsExempt(item, fresh))
        {
            _logger.LogInformation(
                "Watchlist: configuration changed during scan, or the item is now exempt; skipping removal of '{ItemName}' for user '{UserName}'.",
                item.Name,
                user.Username);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Re-reads the current configuration during a scan. Returns false when the
    /// scan must stop: completion rules changed (state was cleared) or
    /// reconciliation was disabled mid-scan. Otherwise refreshes the snapshot
    /// so test-mode and toggle changes take effect for the remaining items.
    /// </summary>
    private bool RefreshOptionsForScan(ref WatchlistOptions options, out bool aborted)
    {
        aborted = false;

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return true;
        }

        var fresh = WatchlistOptions.From(config);
        if (!string.Equals(fresh.Signature, options.Signature, StringComparison.Ordinal))
        {
            _logger.LogInformation("Watchlist: completion options changed during scan; stopping scan.");
            _stateStore.EnsureOptions(fresh.Signature);
            aborted = true;
            return false;
        }

        if (!fresh.EnableReconciliation)
        {
            _logger.LogInformation("Watchlist: reconciliation was disabled during scan; stopping scan.");
            aborted = true;
            return false;
        }

        options = fresh;
        return true;
    }

    private bool Unfavorite(User user, BaseItem item, bool isKnownFavorite = false)
    {
        var data = _userDataManager.GetUserData(user, item);
        if (data is null)
        {
            _logger.LogWarning(
                "Watchlist: user data unavailable for '{ItemName}'; skipping removal.",
                item.Name);
            return false;
        }

        if (!data.IsFavorite
            && !isKnownFavorite
            && !IsFavoriteInLibrary(user, item.Id))
        {
            return false;
        }

        // Only change the favorite flag. This avoids overwriting another
        // concurrent user-data update with a stale full snapshot.
        _userDataManager.SaveUserData(
            user,
            item,
            new UpdateUserItemDataDto { IsFavorite = false },
            UserDataSaveReason.UpdateUserRating);

        _logger.LogInformation(
            "Watchlist: removed {ItemType} '{ItemName}' from favorites for user '{UserName}'.",
            item.GetType().Name,
            item.Name,
            user.Username);

        return true;
    }

    private void LogWouldRemove(User user, BaseItem item)
    {
        _logger.LogInformation(
            "Watchlist dry run: would remove {ItemType} '{ItemName}' from favorites for user '{UserName}'.",
            item.GetType().Name,
            item.Name,
            user.Username);
    }

    /// <summary>
    /// Resolves whether an item should still be treated as a favorite.
    /// A fresh library favorites query decides when the cached user data and
    /// the event snapshot disagree or both say "not favorite", because the
    /// cached value can stay stale after a library scan.
    /// </summary>
    private bool IsCurrentlyFavorite(User user, BaseItem item, bool liveIsFavorite, bool? eventIsFavorite)
    {
        return FavoriteStatusResolver.ShouldTreatAsFavorite(
            liveIsFavorite,
            eventIsFavorite,
            () =>
            {
                try
                {
                    return IsFavoriteInLibrary(user, item.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Watchlist: favorites lookup failed for '{ItemName}'; preserving tracking.",
                        item.Name);
                    return null;
                }
            });
    }

    /// <summary>
    /// Checks the authoritative favorites listing for an item.
    /// </summary>
    private bool IsFavoriteInLibrary(User user, Guid itemId)
    {
        try
        {
            // Scope the authoritative query to the item being checked instead
            // of listing every favorite in the library.
            var favorites = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = [itemId],
                IsFavorite = true,
                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series],
                Recursive = true,
                Limit = 1
            });

            return favorites.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Watchlist: favorites query failed; preserving tracking instead of pruning.");
            return true;
        }
    }

    private static bool IsExempt(BaseItem item, WatchlistOptions options)
    {
        return ExemptTagMatcher.Matches(item.Tags, options.ExemptTag);
    }

    private static SeriesObservation ToObservation(CompletionResult result)
    {
        return new SeriesObservation(
            result.IsComplete,
            result.EpisodeCount,
            result.LatestSeasonNumber,
            result.ContentFingerprint ?? string.Empty);
    }
}
