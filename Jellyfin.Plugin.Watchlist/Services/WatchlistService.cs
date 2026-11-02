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

            if (!options.AutoRemoveMovies && !options.AutoRemoveSeries && !options.AutoRemoveSeasons)
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

            // Collapse episode events to the latest one per series+season:
            // handling an episode re-evaluates its series and its season, so
            // one pass per season covers the batch. Movie, series, and season
            // changes are never collapsed; the caller preserves the
            // favorite/rating change they need.
            var deduped = new List<UserDataChange>(changes.Count);
            var seenEpisodeScope = new HashSet<(Guid UserId, Guid SeriesId, Guid SeasonId)>();
            for (var index = changes.Count - 1; index >= 0; index--)
            {
                var candidate = changes[index];
                if (candidate is null)
                {
                    continue;
                }

                if (candidate.Kind == WatchlistItemKind.Episode
                    && candidate.SeriesId != Guid.Empty
                    && !seenEpisodeScope.Add((candidate.UserId, candidate.SeriesId, candidate.SeasonId)))
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
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season],
            Recursive = true
        });

        var allMovies = favorites.OfType<Movie>().ToList();
        var allSeries = favorites.OfType<Series>().ToList();
        var allSeasons = favorites.OfType<Season>().ToList();
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

                // Retry a deferred rewatch reset before normal handling.
                // Offline re-adds (tombstone without pending flag) are not reset.
                if (TryResetPendingMovieInScan(user, movie, options))
                {
                    continue;
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
                    _stateStore.RemoveRemoved(user.Id, movie.Id);
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
                    _stateStore.SetRemoved(user.Id, movie.Id, nameof(WatchlistItemKind.Movie));
                }
                else if (observed is null || !options.DryRun)
                {
                    // Persist first sightings even in dry-run so a later scan
                    // can remove. Never persist a completion transition in dry-run.
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

        foreach (var season in allSeasons)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!RefreshOptionsForScan(ref options, out aborted))
                {
                    return removed;
                }

                if (TryResetPendingSeasonInScan(user, season, options))
                {
                    continue;
                }

                if (!options.AutoRemoveSeasons)
                {
                    continue;
                }

                if (IsExempt(season, options))
                {
                    _stateStore.RemoveSeason(user.Id, season.Id);
                    _stateStore.RemoveRemoved(user.Id, season.Id);
                    continue;
                }

                var current = EvaluateSeason(user, season, options);
                var observed = _stateStore.GetSeasonObservation(user.Id, season.Id);

                if (RemovalPolicy.ShouldRemoveSeason(observed, current))
                {
                    if (options.DryRun)
                    {
                        LogWouldRemove(user, season);
                        continue;
                    }

                    if (!ConfirmRemovalStillWanted(user, season, options))
                    {
                        continue;
                    }

                    if (Unfavorite(user, season))
                    {
                        removed++;
                    }

                    _stateStore.RemoveSeason(user.Id, season.Id);
                    _stateStore.SetRemoved(user.Id, season.Id, nameof(WatchlistItemKind.Season));
                }
                else if (observed is null || (!options.DryRun && RemovalPolicy.ShouldReplaceSeasonObservation(observed, current)))
                {
                    // First sightings persist even in dry-run; transitions do not.
                    _stateStore.SetSeasonObservation(
                        user.Id,
                        season.Id,
                        ToSeasonObservation(current));
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
                    "Watchlist: failed to reconcile season '{SeasonName}' for user '{UserName}'.",
                    season.Name,
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

                if (TryResetPendingSeriesInScan(user, show, options))
                {
                    continue;
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
                    _stateStore.RemoveRemoved(user.Id, show.Id);
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
                    _stateStore.SetRemoved(user.Id, show.Id, nameof(WatchlistItemKind.Series));
                }
                else if (observed is null || (!options.DryRun && RemovalPolicy.ShouldReplaceSeriesObservation(observed, current)))
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
            allSeries.Select(show => show.Id).ToHashSet(),
            allSeasons.Select(season => season.Id).ToHashSet());

        PruneDeletedTombstones(user);

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
            _stateStore.RemoveDeletedItem(user.Id, change.ItemId);
            return true;
        }

        // An episode's own favorite flag is irrelevant. The watchlist entry is
        // the parent series or season, so episode play-state changes must still
        // be handled even when the episode itself was never favorited.
        if (item is Episode episode)
        {
            return HandleEpisode(user, episode, change, options, cancellationToken);
        }

        if (item is Season seasonItem)
        {
            return HandleSeasonEvent(user, seasonItem, change, options);
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
                // Dropping the tombstone: an exempt re-add must not reset later.
                _stateStore.RemoveRemoved(user.Id, movie.Id);
                return true;
            }

            return false;
        }

        // Rewatch reset: a re-added movie the plugin removed restarts playback.
        if (TryResetMovieOnReadd(user, movie, options, data.Played))
        {
            return true;
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
            var observedPlayed = _stateStore.GetMoviePlayed(user.Id, movie.Id);
            if (observedPlayed is null)
            {
                // First sighting persists even in dry-run so a later scan can remove.
                _stateStore.SetMoviePlayed(user.Id, movie.Id, change.Played);
                return true;
            }

            if (options.DryRun)
            {
                return false;
            }

            if (change.Played != observedPlayed.Value)
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
            // First sighting persists even in dry-run.
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
            _stateStore.SetRemoved(user.Id, movie.Id, nameof(WatchlistItemKind.Movie));
            return true;
        }

        _stateStore.RemoveMovie(user.Id, movie.Id);
        _stateStore.SetRemoved(user.Id, movie.Id, nameof(WatchlistItemKind.Movie));
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
                _stateStore.RemoveRemoved(user.Id, series.Id);
                return true;
            }

            return false;
        }

        if (TryResetSeriesOnReadd(user, series, options))
        {
            return true;
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
            var rated = EvaluateSeries(user, series, options);
            var previous = _stateStore.GetSeriesObservation(user.Id, series.Id);
            if (previous is null)
            {
                _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(rated));
                return true;
            }

            if (options.DryRun)
            {
                return false;
            }

            if (RemovalPolicy.ShouldReplaceSeriesObservation(previous, rated))
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
            _stateStore.SetRemoved(user.Id, series.Id, nameof(WatchlistItemKind.Series));
            return true;
        }

        _stateStore.RemoveSeries(user.Id, series.Id);
        _stateStore.SetRemoved(user.Id, series.Id, nameof(WatchlistItemKind.Series));
        return true;
    }

    private bool HandleSeasonEvent(
        User user,
        Season season,
        UserDataChange change,
        WatchlistOptions options)
    {
        var data = _userDataManager.GetUserData(user, season);
        if (data is null)
        {
            return false;
        }

        if (!IsCurrentlyFavorite(user, season, data.IsFavorite, change.IsFavorite))
        {
            _stateStore.RemoveItem(user.Id, season.Id);
            return true;
        }

        if (!options.AutoRemoveSeasons || IsExempt(season, options))
        {
            if (IsExempt(season, options))
            {
                _stateStore.RemoveSeason(user.Id, season.Id);
                _stateStore.RemoveRemoved(user.Id, season.Id);
                return true;
            }

            return false;
        }

        if (TryResetSeasonOnReadd(user, season, options))
        {
            return true;
        }

        // Favorite/rating events only baseline; playback and scans remove.
        if (change.SaveReason == UserDataSaveReason.UpdateUserRating)
        {
            var rated = EvaluateSeason(user, season, options);
            var previous = _stateStore.GetSeasonObservation(user.Id, season.Id);
            if (previous is null)
            {
                _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(rated));
                return true;
            }

            if (options.DryRun)
            {
                return false;
            }

            if (RemovalPolicy.ShouldReplaceSeasonObservation(previous, rated))
            {
                _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(rated));
                return true;
            }

            return false;
        }

        var current = EvaluateSeason(user, season, options);
        var observed = _stateStore.GetSeasonObservation(user.Id, season.Id);

        if (observed is null)
        {
            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(current));
            return true;
        }

        if (!RemovalPolicy.ShouldRemoveSeason(observed, current))
        {
            if (options.DryRun || !RemovalPolicy.ShouldReplaceSeasonObservation(observed, current))
            {
                return false;
            }

            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(current));
            return true;
        }

        if (options.DryRun)
        {
            LogWouldRemove(user, season);
            return false;
        }

        if (!ConfirmRemovalStillWanted(user, season, options))
        {
            return false;
        }

        if (Unfavorite(user, season, isKnownFavorite: true))
        {
            _stateStore.RemoveSeason(user.Id, season.Id);
            _stateStore.SetRemoved(user.Id, season.Id, nameof(WatchlistItemKind.Season));
            return true;
        }

        _stateStore.RemoveSeason(user.Id, season.Id);
        _stateStore.SetRemoved(user.Id, season.Id, nameof(WatchlistItemKind.Season));
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

        var seriesId = change.SeriesId != Guid.Empty ? change.SeriesId : SafeFindSeriesId(episode);
        var seasonId = change.SeasonId != Guid.Empty ? change.SeasonId : SafeFindSeasonId(episode);

        var dirty = false;

        // Evaluate a favorited parent season first so a season completion is
        // not missed when the series itself is not favorited.
        if (seasonId != Guid.Empty)
        {
            var season = _libraryManager.GetItemById(seasonId) as Season;
            if (season is null)
            {
                _stateStore.RemoveSeason(user.Id, seasonId);
                _stateStore.RemoveDeletedItem(user.Id, seasonId);
                dirty = true;
            }
            else
            {
                var seasonData = _userDataManager.GetUserData(user, season);
                if (seasonData is not null && IsCurrentlyFavorite(user, season, seasonData.IsFavorite, eventIsFavorite: null))
                {
                    if (HandleSeasonFromEpisode(user, season, options))
                    {
                        dirty = true;
                    }
                }
                else if (seasonData is not null)
                {
                    // Not a favorite: drop stale observation but keep tombstones.
                    _stateStore.RemoveSeason(user.Id, season.Id);
                    dirty = true;
                }
            }
        }

        var series = seriesId == Guid.Empty
            ? null
            : _libraryManager.GetItemById(seriesId) as Series;

        if (series is null)
        {
            if (seriesId != Guid.Empty)
            {
                _stateStore.RemoveSeries(user.Id, seriesId);
                dirty = true;
            }

            return dirty;
        }

        var seriesData = _userDataManager.GetUserData(user, series);
        if (seriesData is null)
        {
            return dirty;
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
                _stateStore.RemoveRemoved(user.Id, series.Id);
                return true;
            }

            return dirty;
        }

        var current = EvaluateSeries(user, series, options);
        var observed = _stateStore.GetSeriesObservation(user.Id, series.Id);

        if (observed is null)
        {
            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(current));
            return true;
        }

        if (!RemovalPolicy.ShouldRemoveSeries(observed, current))
        {
            if (options.DryRun || !RemovalPolicy.ShouldReplaceSeriesObservation(observed, current))
            {
                return dirty;
            }

            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(current));
            return true;
        }

        if (options.DryRun)
        {
            LogWouldRemove(user, series);
            return dirty;
        }

        if (!ConfirmRemovalStillWanted(user, series, options))
        {
            return dirty;
        }

        if (Unfavorite(user, series, isKnownFavorite: true))
        {
            _stateStore.RemoveSeries(user.Id, series.Id);
            _stateStore.SetRemoved(user.Id, series.Id, nameof(WatchlistItemKind.Series));
            return true;
        }

        _stateStore.RemoveSeries(user.Id, series.Id);
        _stateStore.SetRemoved(user.Id, series.Id, nameof(WatchlistItemKind.Series));
        return true;
    }

    private bool HandleSeasonFromEpisode(User user, Season season, WatchlistOptions options)
    {
        if (!options.AutoRemoveSeasons || IsExempt(season, options))
        {
            if (IsExempt(season, options))
            {
                _stateStore.RemoveSeason(user.Id, season.Id);
                _stateStore.RemoveRemoved(user.Id, season.Id);
                return true;
            }

            return false;
        }

        var current = EvaluateSeason(user, season, options);
        var observed = _stateStore.GetSeasonObservation(user.Id, season.Id);

        if (observed is null)
        {
            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(current));
            return true;
        }

        if (!RemovalPolicy.ShouldRemoveSeason(observed, current))
        {
            if (options.DryRun || !RemovalPolicy.ShouldReplaceSeasonObservation(observed, current))
            {
                return false;
            }

            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(current));
            return true;
        }

        if (options.DryRun)
        {
            LogWouldRemove(user, season);
            return false;
        }

        if (!ConfirmRemovalStillWanted(user, season, options))
        {
            return false;
        }

        if (Unfavorite(user, season, isKnownFavorite: true))
        {
            _stateStore.RemoveSeason(user.Id, season.Id);
            _stateStore.SetRemoved(user.Id, season.Id, nameof(WatchlistItemKind.Season));
            return true;
        }

        _stateStore.RemoveSeason(user.Id, season.Id);
        _stateStore.SetRemoved(user.Id, season.Id, nameof(WatchlistItemKind.Season));
        return true;
    }

    private Guid SafeFindSeriesId(Episode episode)
    {
        try
        {
            return episode.FindSeriesId();
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private Guid SafeFindSeasonId(Episode episode)
    {
        try
        {
            return episode.FindSeasonId();
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private CompletionResult EvaluateSeason(User user, Season season, WatchlistOptions options)
    {
        var seasonNumber = season.IndexNumber ?? 1;
        List<Episode> episodes;
        try
        {
            episodes = season.GetEpisodes(user, new DtoOptions(false), shouldIncludeMissingEpisodes: false)
                .OfType<Episode>()
                .ToList();
        }
        catch
        {
            episodes = [];
        }

        var present = episodes
            .Where(e => !e.IsMissingEpisode && !e.IsVirtualItem)
            .Cast<BaseItem>()
            .ToList();
        var userData = present.Count == 0
            ? new Dictionary<Guid, UserItemData>()
            : _userDataManager.GetUserDataBatch(present, user);

        var snapshots = new List<EpisodeSnapshot>(episodes.Count);
        foreach (var episode in episodes)
        {
            var isMissing = episode.IsMissingEpisode || episode.IsVirtualItem;
            userData.TryGetValue(episode.Id, out var data);
            snapshots.Add(new EpisodeSnapshot(
                episode.ParentIndexNumber,
                episode.PremiereDate,
                isMissing,
                data?.Played == true,
                episode.Id));
        }

        return CompletionEvaluator.EvaluateSeason(snapshots, seasonNumber, options, DateTime.UtcNow);
    }

    private CompletionResult EvaluateSeriesFullyWatched(User user, Series series, WatchlistOptions options)
    {
        // Literal fully-watched ignores the configured mode and status.
        var resetOptions = options with { Mode = CompletionMode.CaughtUp };
        return EvaluateSeries(user, series, resetOptions);
    }

    private CompletionResult EvaluateSeason(User user, Series series, int seasonNumber, WatchlistOptions options)
    {
        // Episodes for one season within a series scope.
        var includeMissing = options.Mode == CompletionMode.LatestSeasonWatched;
        List<Episode> episodes;
        try
        {
            episodes = series.GetEpisodes(user, new DtoOptions(false), shouldIncludeMissingEpisodes: includeMissing)
                .OfType<Episode>()
                .Where(e => (e.ParentIndexNumber ?? 1) == seasonNumber)
                .ToList();
        }
        catch
        {
            episodes = [];
        }

        var present = episodes
            .Where(e => !e.IsMissingEpisode && !e.IsVirtualItem)
            .Cast<BaseItem>()
            .ToList();
        var userData = present.Count == 0
            ? new Dictionary<Guid, UserItemData>()
            : _userDataManager.GetUserDataBatch(present, user);

        var snapshots = new List<EpisodeSnapshot>(episodes.Count);
        foreach (var episode in episodes)
        {
            var isMissing = episode.IsMissingEpisode || episode.IsVirtualItem;
            userData.TryGetValue(episode.Id, out var data);
            snapshots.Add(new EpisodeSnapshot(
                episode.ParentIndexNumber,
                episode.PremiereDate,
                isMissing,
                data?.Played == true,
                episode.Id));
        }

        return CompletionEvaluator.EvaluateSeason(snapshots, seasonNumber, options, DateTime.UtcNow);
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
            || fresh.AutoRemoveSeasons != options.AutoRemoveSeasons
            || fresh.EnableRewatch != options.EnableRewatch
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
                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series, Jellyfin.Data.Enums.BaseItemKind.Season],
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

    private static SeasonObservation ToSeasonObservation(CompletionResult result)
    {
        return new SeasonObservation(
            result.IsComplete,
            result.EpisodeCount,
            result.LatestSeasonNumber,
            result.ContentFingerprint ?? string.Empty);
    }

    private void LogWouldReset(User user, BaseItem item)
    {
        _logger.LogInformation(
            "Watchlist dry run: would reset {ItemType} '{ItemName}' for user '{UserName}' to start a rewatch.",
            item.GetType().Name,
            item.Name,
            user.Username);
    }

    private void PruneDeletedTombstones(User user)
    {
        try
        {
            _stateStore.PruneRemovedForMissingItems(user.Id, id => _libraryManager.GetItemById(id) is not null);
        }
        catch
        {
            // Best effort; a failed lookup must not fail the scan.
        }
    }

    private bool TryResetMovieOnReadd(User user, Movie movie, WatchlistOptions options, bool currentPlayed)
    {
        var tombstone = _stateStore.GetRemoved(user.Id, movie.Id);
        if (tombstone is null)
        {
            return false;
        }

        if (!options.EnableRewatch || !options.AutoRemoveMovies || IsExempt(movie, options))
        {
            _stateStore.RemoveRemoved(user.Id, movie.Id);
            return true;
        }

        if (!ResetPolicy.ShouldResetMovie(currentPlayed, true, options.EnableRewatch, options.AutoRemoveMovies, IsExempt(movie, options)))
        {
            _stateStore.RemoveRemoved(user.Id, movie.Id);
            return false;
        }

        if (options.DryRun)
        {
            LogWouldReset(user, movie);
            _stateStore.SetRemovedPending(user.Id, movie.Id, true);
            return true;
        }

        try
        {
            movie.MarkUnplayed(user);
            ClearFolderPlayedState(user, movie);
            _stateStore.RemoveRemoved(user.Id, movie.Id);
            _stateStore.SetMoviePlayed(user.Id, movie.Id, false);
            _logger.LogInformation(
                "Watchlist: reset {ItemType} '{ItemName}' for user '{UserName}' to start a rewatch.",
                nameof(Movie),
                movie.Name,
                user.Username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watchlist: failed to reset movie '{MovieName}' for user '{UserName}'; will retry.", movie.Name, user.Username);
            _stateStore.SetRemovedPending(user.Id, movie.Id, true);
            return true;
        }
    }

    private bool TryResetSeasonOnReadd(User user, Season season, WatchlistOptions options)
    {
        var tombstone = _stateStore.GetRemoved(user.Id, season.Id);
        if (tombstone is null)
        {
            return false;
        }

        if (!options.EnableRewatch || !options.AutoRemoveSeasons || IsExempt(season, options))
        {
            _stateStore.RemoveRemoved(user.Id, season.Id);
            return true;
        }

        var fullyWatched = EvaluateSeason(user, season, options);
        if (!ResetPolicy.ShouldResetCompletion(fullyWatched, true, options.EnableRewatch, options.AutoRemoveSeasons, IsExempt(season, options)))
        {
            _stateStore.RemoveRemoved(user.Id, season.Id);
            var post = EvaluateSeason(user, season, options);
            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(post));
            return true;
        }

        if (options.DryRun)
        {
            LogWouldReset(user, season);
            _stateStore.SetRemovedPending(user.Id, season.Id, true);
            return true;
        }

        try
        {
            ResetFolderPlayedState(user, season);
            _stateStore.RemoveRemoved(user.Id, season.Id);
            var post = EvaluateSeason(user, season, options);
            if (post.IsComplete)
            {
                post = post with { IsComplete = false };
            }

            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(post));
            RecomputeSeriesScopeAfterSeasonReset(user, season, options);
            _logger.LogInformation(
                "Watchlist: reset {ItemType} '{ItemName}' for user '{UserName}' to start a rewatch.",
                nameof(Season),
                season.Name,
                user.Username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watchlist: failed to reset season '{SeasonName}' for user '{UserName}'; will retry.", season.Name, user.Username);
            _stateStore.SetRemovedPending(user.Id, season.Id, true);
            return true;
        }
    }

    private bool TryResetSeriesOnReadd(User user, Series series, WatchlistOptions options)
    {
        var tombstone = _stateStore.GetRemoved(user.Id, series.Id);
        if (tombstone is null)
        {
            return false;
        }

        if (!options.EnableRewatch || !options.AutoRemoveSeries || IsExempt(series, options))
        {
            _stateStore.RemoveRemoved(user.Id, series.Id);
            return true;
        }

        var fullyWatched = EvaluateSeriesFullyWatched(user, series, options);
        if (!ResetPolicy.ShouldResetCompletion(fullyWatched, true, options.EnableRewatch, options.AutoRemoveSeries, IsExempt(series, options)))
        {
            _stateStore.RemoveRemoved(user.Id, series.Id);
            var post = EvaluateSeries(user, series, options);
            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(post));
            if (fullyWatched.EpisodeCount > 0 && !fullyWatched.IsComplete)
            {
                _logger.LogInformation(
                    "Watchlist: re-added series '{SeriesName}' for user '{UserName}' is not fully watched; rewatch a specific season to reset it.",
                    series.Name,
                    user.Username);
            }

            return true;
        }

        if (options.DryRun)
        {
            LogWouldReset(user, series);
            _stateStore.SetRemovedPending(user.Id, series.Id, true);
            return true;
        }

        try
        {
            ResetFolderPlayedState(user, series);
            _stateStore.RemoveRemoved(user.Id, series.Id);
            var literal = EvaluateSeriesFullyWatched(user, series, options);
            if (literal.IsComplete)
            {
                _stateStore.SetRemoved(user.Id, series.Id, nameof(WatchlistItemKind.Series), pendingReset: true);
                return true;
            }

            var post = EvaluateSeries(user, series, options);
            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(post));
            RecomputeSeasonsAfterSeriesReset(user, series, options);
            _logger.LogInformation(
                "Watchlist: reset {ItemType} '{ItemName}' for user '{UserName}' to start a rewatch.",
                nameof(Series),
                series.Name,
                user.Username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watchlist: failed to reset series '{SeriesName}' for user '{UserName}'; will retry.", series.Name, user.Username);
            _stateStore.SetRemovedPending(user.Id, series.Id, true);
            return true;
        }
    }

    private bool TryResetPendingMovieInScan(User user, Movie movie, WatchlistOptions options)
    {
        var tombstone = _stateStore.GetRemoved(user.Id, movie.Id);
        if (tombstone is null)
        {
            return false;
        }

        if (!tombstone.PendingReset)
        {
            _stateStore.RemoveRemoved(user.Id, movie.Id);
            return false;
        }

        if (!options.EnableRewatch || !options.AutoRemoveMovies || IsExempt(movie, options))
        {
            _stateStore.RemoveRemoved(user.Id, movie.Id);
            return true;
        }

        var data = _userDataManager.GetUserData(user, movie);
        var played = data?.Played == true;
        if (!played)
        {
            _stateStore.RemoveRemoved(user.Id, movie.Id);
            _stateStore.SetMoviePlayed(user.Id, movie.Id, false);
            return true;
        }

        if (options.DryRun)
        {
            LogWouldReset(user, movie);
            return true;
        }

        try
        {
            movie.MarkUnplayed(user);
            ClearFolderPlayedState(user, movie);
            _stateStore.RemoveRemoved(user.Id, movie.Id);
            _stateStore.SetMoviePlayed(user.Id, movie.Id, false);
            _logger.LogInformation(
                "Watchlist: reset {ItemType} '{ItemName}' for user '{UserName}' to start a rewatch.",
                nameof(Movie),
                movie.Name,
                user.Username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watchlist: failed to reset movie '{MovieName}' for user '{UserName}'; will retry.", movie.Name, user.Username);
            return true;
        }
    }

    private bool TryResetPendingSeasonInScan(User user, Season season, WatchlistOptions options)
    {
        var tombstone = _stateStore.GetRemoved(user.Id, season.Id);
        if (tombstone is null)
        {
            return false;
        }

        if (!tombstone.PendingReset)
        {
            _stateStore.RemoveRemoved(user.Id, season.Id);
            return false;
        }

        if (!options.EnableRewatch || !options.AutoRemoveSeasons || IsExempt(season, options))
        {
            _stateStore.RemoveRemoved(user.Id, season.Id);
            var post = EvaluateSeason(user, season, options);
            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(post));
            return true;
        }

        var fullyWatched = EvaluateSeason(user, season, options);
        if (!fullyWatched.IsComplete)
        {
            _stateStore.RemoveRemoved(user.Id, season.Id);
            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(fullyWatched));
            return true;
        }

        if (options.DryRun)
        {
            LogWouldReset(user, season);
            return true;
        }

        try
        {
            ResetFolderPlayedState(user, season);
            _stateStore.RemoveRemoved(user.Id, season.Id);
            var post = EvaluateSeason(user, season, options);
            if (post.IsComplete)
            {
                post = post with { IsComplete = false };
            }

            _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(post));
            RecomputeSeriesScopeAfterSeasonReset(user, season, options);
            _logger.LogInformation(
                "Watchlist: reset {ItemType} '{ItemName}' for user '{UserName}' to start a rewatch.",
                nameof(Season),
                season.Name,
                user.Username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watchlist: failed to reset season '{SeasonName}' for user '{UserName}'; will retry.", season.Name, user.Username);
            return true;
        }
    }

    private bool TryResetPendingSeriesInScan(User user, Series series, WatchlistOptions options)
    {
        var tombstone = _stateStore.GetRemoved(user.Id, series.Id);
        if (tombstone is null)
        {
            return false;
        }

        if (!tombstone.PendingReset)
        {
            _stateStore.RemoveRemoved(user.Id, series.Id);
            var current = EvaluateSeries(user, series, options);
            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(current));
            return true;
        }

        if (!options.EnableRewatch || !options.AutoRemoveSeries || IsExempt(series, options))
        {
            _stateStore.RemoveRemoved(user.Id, series.Id);
            var postExempt = EvaluateSeries(user, series, options);
            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(postExempt));
            return true;
        }

        var fullyWatched = EvaluateSeriesFullyWatched(user, series, options);
        if (!fullyWatched.IsComplete)
        {
            _stateStore.RemoveRemoved(user.Id, series.Id);
            var post = EvaluateSeries(user, series, options);
            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(post));
            RecomputeSeasonsAfterSeriesReset(user, series, options);
            return true;
        }

        if (options.DryRun)
        {
            LogWouldReset(user, series);
            return true;
        }

        try
        {
            ResetFolderPlayedState(user, series);
            _stateStore.RemoveRemoved(user.Id, series.Id);
            var literal = EvaluateSeriesFullyWatched(user, series, options);
            if (literal.IsComplete)
            {
                _stateStore.SetRemoved(user.Id, series.Id, nameof(WatchlistItemKind.Series), pendingReset: true);
                return true;
            }

            var post = EvaluateSeries(user, series, options);
            _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(post));
            RecomputeSeasonsAfterSeriesReset(user, series, options);
            _logger.LogInformation(
                "Watchlist: reset {ItemType} '{ItemName}' for user '{UserName}' to start a rewatch.",
                nameof(Series),
                series.Name,
                user.Username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watchlist: failed to reset series '{SeriesName}' for user '{UserName}'; will retry.", series.Name, user.Username);
            return true;
        }
    }

    private void ResetFolderPlayedState(User user, BaseItem folder)
    {
        if (folder is MediaBrowser.Controller.Entities.Folder f)
        {
            f.MarkUnplayed(user);
        }
        else if (folder is Movie m)
        {
            m.MarkUnplayed(user);
        }

        ClearFolderPlayedState(user, folder);
    }

    private void ClearFolderPlayedState(User user, BaseItem item)
    {
        try
        {
            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                return;
            }

            if (!data.Played && data.PlayCount == 0 && data.PlaybackPositionTicks == 0)
            {
                return;
            }

            data.Played = false;
            data.PlayCount = 0;
            data.PlaybackPositionTicks = 0;
            data.LastPlayedDate = null;
            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Watchlist: failed to clear played state for '{ItemName}'.", item.Name);
        }
    }

    private void RecomputeSeriesScopeAfterSeasonReset(User user, Season season, WatchlistOptions options)
    {
        try
        {
            Series? series = null;
            try
            {
                var seriesId = season.SeriesId;
                if (seriesId == Guid.Empty)
                {
                    seriesId = season.FindSeriesId();
                }

                if (seriesId != Guid.Empty)
                {
                    series = _libraryManager.GetItemById(seriesId) as Series;
                }
            }
            catch
            {
                series = null;
            }

            if (series is null)
            {
                return;
            }

            var seriesData = _userDataManager.GetUserData(user, series);
            if (seriesData is not null && IsCurrentlyFavorite(user, series, seriesData.IsFavorite, eventIsFavorite: null))
            {
                var post = EvaluateSeries(user, series, options);
                _stateStore.SetSeriesObservation(user.Id, series.Id, ToObservation(post));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Watchlist: failed to recompute series scope after season reset.");
        }
    }

    private void RecomputeSeasonsAfterSeriesReset(User user, Series series, WatchlistOptions options)
    {
        try
        {
            List<Season> seasons;
            try
            {
                seasons = series.GetSeasons(user, new DtoOptions(false)).OfType<Season>().ToList();
            }
            catch
            {
                return;
            }

            foreach (var season in seasons)
            {
                try
                {
                    var data = _userDataManager.GetUserData(user, season);
                    if (data is null || !IsCurrentlyFavorite(user, season, data.IsFavorite, eventIsFavorite: null))
                    {
                        continue;
                    }

                    var post = EvaluateSeason(user, season, options);
                    _stateStore.SetSeasonObservation(user.Id, season.Id, ToSeasonObservation(post));
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Watchlist: failed to recompute seasons after series reset.");
        }
    }
}
