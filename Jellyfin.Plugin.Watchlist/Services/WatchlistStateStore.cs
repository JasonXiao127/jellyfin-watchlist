using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Watchlist.Logic;
using Jellyfin.Plugin.Watchlist.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlist.Services;

/// <summary>
/// Thread-safe, JSON-backed observations about favorite movies and series.
/// The Favorites themselves remain the source of truth; this store only records
/// what was observed the last time the plugin looked at an item.
/// </summary>
public sealed class WatchlistStateStore
{
    private const int CurrentSchemaVersion = 2;
    private const int MinimumSupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly string _filePath;
    private readonly ILogger<WatchlistStateStore> _logger;
    private readonly object _sync = new();

    private WatchlistState? _state;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistStateStore"/> class.
    /// </summary>
    /// <param name="dataPath">The Jellyfin data directory.</param>
    /// <param name="logger">The logger.</param>
    public WatchlistStateStore(string dataPath, ILogger<WatchlistStateStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(logger);

        _directory = Path.Combine(dataPath, "watchlist");
        _filePath = Path.Combine(_directory, "state.json");
        _logger = logger;
    }

    /// <summary>
    /// Clears series observations when the options that affect series completion
    /// decisions change. Movie and season observations do not depend on those rules
    /// and are preserved. Removal tombstones are always preserved; reset checks
    /// re-validate live state and configuration.
    /// </summary>
    /// <param name="optionsSignature">The current options signature.</param>
    public void EnsureOptions(string optionsSignature)
    {
        ArgumentNullException.ThrowIfNull(optionsSignature);

        lock (_sync)
        {
            var state = EnsureLoadedLocked();
            if (string.Equals(state.OptionsSignature, optionsSignature, StringComparison.Ordinal))
            {
                return;
            }

            if (state.Users.Count > 0)
            {
                _logger.LogInformation(
                    "Watchlist: completion options changed from '{Old}' to '{New}'; clearing series observations.",
                    state.OptionsSignature,
                    optionsSignature);
            }

            foreach (var user in state.Users.Values)
            {
                user.Series.Clear();
            }

            foreach (var key in state.Users
                .Where(entry => entry.Value.Movies.Count == 0 && entry.Value.Series.Count == 0 && entry.Value.Seasons.Count == 0 && entry.Value.Removed.Count == 0)
                .Select(entry => entry.Key)
                .ToList())
            {
                state.Users.Remove(key);
            }

            state.OptionsSignature = optionsSignature;
            SaveLocked();
        }
    }

    /// <summary>
    /// Gets the last observed play state of a favorite movie.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The movie id.</param>
    /// <returns>The observed play state, or null when the movie has not been observed.</returns>
    public bool? GetMoviePlayed(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            if (user is null || !user.Movies.TryGetValue(Key(itemId), out var movie) || movie is null)
            {
                return null;
            }

            return movie.Played;
        }
    }

    /// <summary>
    /// Gets the last observed completion state of a favorite series.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The series id.</param>
    /// <returns>The observation, or null when the series has not been observed.</returns>
    public SeriesObservation? GetSeriesObservation(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            if (user is null || !user.Series.TryGetValue(Key(itemId), out var series) || series is null)
            {
                return null;
            }

            return new SeriesObservation(
                series.Complete,
                series.EpisodeCount,
                series.LatestSeasonNumber,
                series.ContentFingerprint ?? string.Empty);
        }
    }

    /// <summary>
    /// Gets the last observed completion state of a favorite season.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The season id.</param>
    /// <returns>The observation, or null when the season has not been observed.</returns>
    public SeasonObservation? GetSeasonObservation(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            if (user is null || !user.Seasons.TryGetValue(Key(itemId), out var season) || season is null)
            {
                return null;
            }

            return new SeasonObservation(
                season.Complete,
                season.EpisodeCount,
                season.SeasonNumber,
                season.ContentFingerprint ?? string.Empty);
        }
    }

    /// <summary>
    /// Records the play state of a favorite movie.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The movie id.</param>
    /// <param name="played">Whether the movie is played.</param>
    public void SetMoviePlayed(Guid userId, Guid itemId, bool played)
    {
        lock (_sync)
        {
            var user = GetOrCreateUserLocked(userId);
            user.Movies[Key(itemId)] = new MovieWatchState { Played = played };
        }
    }

    /// <summary>
    /// Records the completion state of a favorite series.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The series id.</param>
    /// <param name="observation">The observation.</param>
    public void SetSeriesObservation(Guid userId, Guid itemId, SeriesObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        lock (_sync)
        {
            var user = GetOrCreateUserLocked(userId);
            user.Series[Key(itemId)] = new SeriesWatchState
            {
                Complete = observation.Complete,
                EpisodeCount = observation.EpisodeCount,
                LatestSeasonNumber = observation.LatestSeasonNumber,
                ContentFingerprint = observation.ContentFingerprint ?? string.Empty
            };
        }
    }

    /// <summary>
    /// Records the completion state of a favorite season.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The season id.</param>
    /// <param name="observation">The observation.</param>
    public void SetSeasonObservation(Guid userId, Guid itemId, SeasonObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        lock (_sync)
        {
            var user = GetOrCreateUserLocked(userId);
            user.Seasons[Key(itemId)] = new SeasonWatchState
            {
                Complete = observation.Complete,
                EpisodeCount = observation.EpisodeCount,
                SeasonNumber = observation.SeasonNumber,
                ContentFingerprint = observation.ContentFingerprint ?? string.Empty
            };
        }
    }

    /// <summary>
    /// Gets a removal tombstone.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>The tombstone, or null when absent.</returns>
    public RemovedWatchEntry? GetRemoved(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            if (user is null || !user.Removed.TryGetValue(Key(itemId), out var entry) || entry is null)
            {
                return null;
            }

            return new RemovedWatchEntry
            {
                Kind = entry.Kind ?? string.Empty,
                RemovedAt = entry.RemovedAt,
                PendingReset = entry.PendingReset
            };
        }
    }

    /// <summary>
    /// Records a removal tombstone.
    /// </summary>
    public void SetRemoved(Guid userId, Guid itemId, string kind, bool pendingReset = false)
    {
        lock (_sync)
        {
            var user = GetOrCreateUserLocked(userId);
            user.Removed[Key(itemId)] = new RemovedWatchEntry
            {
                Kind = kind ?? string.Empty,
                RemovedAt = DateTime.UtcNow,
                PendingReset = pendingReset
            };
        }
    }

    /// <summary>
    /// Sets the pending-reset flag on an existing tombstone.
    /// </summary>
    public void SetRemovedPending(Guid userId, Guid itemId, bool pendingReset)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            if (user is not null && user.Removed.TryGetValue(Key(itemId), out var entry) && entry is not null)
            {
                entry.PendingReset = pendingReset;
            }
        }
    }

    /// <summary>
    /// Removes a removal tombstone.
    /// </summary>
    public void RemoveRemoved(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            user?.Removed.Remove(Key(itemId));
        }
    }

    /// <summary>
    /// Removes tombstones for items that no longer exist in the library.
    /// Tombstones for unfavorited items are kept (normal post-removal state).
    /// </summary>
    public void PruneRemovedForMissingItems(Guid userId, Func<Guid, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        lock (_sync)
        {
            var user = FindUserLocked(userId);
            if (user is null || user.Removed.Count == 0)
            {
                return;
            }

            foreach (var key in user.Removed.Keys.ToList())
            {
                if (Guid.TryParseExact(key, "N", out var id) && !exists(id))
                {
                    user.Removed.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Removes a movie observation.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The movie id.</param>
    public void RemoveMovie(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            user?.Movies.Remove(Key(itemId));
        }
    }

    /// <summary>
    /// Removes a series observation.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The series id.</param>
    public void RemoveSeries(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            user?.Series.Remove(Key(itemId));
        }
    }

    /// <summary>
    /// Removes a season observation.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The season id.</param>
    public void RemoveSeason(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            user?.Seasons.Remove(Key(itemId));
        }
    }

    /// <summary>
    /// Removes an item observation of any kind. Tombstones are kept so a later
    /// re-add can still be distinguished from a first-time favorite.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    public void RemoveItem(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            if (user is null)
            {
                return;
            }

            var key = Key(itemId);
            user.Movies.Remove(key);
            user.Series.Remove(key);
            user.Seasons.Remove(key);
        }
    }

    /// <summary>
    /// Removes observations and tombstones for an item that no longer exists in the library.
    /// </summary>
    public void RemoveDeletedItem(Guid userId, Guid itemId)
    {
        lock (_sync)
        {
            var user = FindUserLocked(userId);
            if (user is null)
            {
                return;
            }

            var key = Key(itemId);
            user.Movies.Remove(key);
            user.Series.Remove(key);
            user.Seasons.Remove(key);
            user.Removed.Remove(key);
        }
    }

    /// <summary>
    /// Removes all observations for a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    public void RemoveUser(Guid userId)
    {
        lock (_sync)
        {
            var state = EnsureLoadedLocked();
            state.Users.Remove(Key(userId));
        }
    }

    /// <summary>
    /// Removes observations for items that are no longer favorites.
    /// Tombstones are kept: an unfavorited item with a tombstone is the normal
    /// state after an auto-removal, and the tombstone is needed for re-add detection.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="favoriteMovieIds">The user's current favorite movie ids.</param>
    /// <param name="favoriteSeriesIds">The user's current favorite series ids.</param>
    /// <param name="favoriteSeasonIds">The user's current favorite season ids.</param>
    public void PruneUser(
        Guid userId,
        IReadOnlySet<Guid> favoriteMovieIds,
        IReadOnlySet<Guid> favoriteSeriesIds,
        IReadOnlySet<Guid>? favoriteSeasonIds = null)
    {
        ArgumentNullException.ThrowIfNull(favoriteMovieIds);
        ArgumentNullException.ThrowIfNull(favoriteSeriesIds);

        // An empty favorites result usually means the query ran before the
        // library was ready, not that the user unfavorited everything. Never
        // wipe observations in that case; the next non-empty scan prunes.
        if (favoriteMovieIds.Count == 0 && favoriteSeriesIds.Count == 0 && (favoriteSeasonIds is null || favoriteSeasonIds.Count == 0))
        {
            _logger.LogWarning("Watchlist: favorites query returned no items; skipping prune without changing state.");
            return;
        }

        lock (_sync)
        {
            var state = EnsureLoadedLocked();
            if (!state.Users.TryGetValue(Key(userId), out var user))
            {
                return;
            }

            var movieKeys = favoriteMovieIds.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seriesKeys = favoriteSeriesIds.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seasonKeys = (favoriteSeasonIds ?? new HashSet<Guid>()).Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in user.Movies.Keys.Where(key => !movieKeys.Contains(key)).ToList())
            {
                user.Movies.Remove(key);
            }

            foreach (var key in user.Series.Keys.Where(key => !seriesKeys.Contains(key)).ToList())
            {
                user.Series.Remove(key);
            }

            foreach (var key in user.Seasons.Keys.Where(key => !seasonKeys.Contains(key)).ToList())
            {
                user.Seasons.Remove(key);
            }

            if (user.Movies.Count == 0 && user.Series.Count == 0 && user.Seasons.Count == 0 && user.Removed.Count == 0)
            {
                state.Users.Remove(Key(userId));
            }
        }
    }

    /// <summary>
    /// Removes observations for users that no longer exist.
    /// </summary>
    /// <param name="userIds">The current user ids.</param>
    public void PruneUsers(IReadOnlySet<Guid> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        // An empty user list usually means the user database was not ready,
        // not that every user was deleted. Never wipe all state in that case.
        if (userIds.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var state = EnsureLoadedLocked();
            var keys = userIds.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in state.Users.Keys.Where(key => !keys.Contains(key)).ToList())
            {
                state.Users.Remove(key);
            }
        }
    }

    /// <summary>
    /// Persists the current observations.
    /// </summary>
    public void Save()
    {
        lock (_sync)
        {
            SaveLocked();
        }
    }

    private static string Key(Guid id)
    {
        return id.ToString("N");
    }

    private WatchlistState EnsureLoadedLocked()
    {
        if (_loaded)
        {
            return _state!;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var state = JsonSerializer.Deserialize<WatchlistState>(json, JsonOptions);
                if (state is not null
                    && (state.SchemaVersion == CurrentSchemaVersion || state.SchemaVersion == MinimumSupportedSchemaVersion))
                {
                    // Migrate v1 (movies + series only) to v2.
                    if (state.SchemaVersion == MinimumSupportedSchemaVersion)
                    {
                        state.SchemaVersion = CurrentSchemaVersion;
                    }

                    Normalize(state);
                    _state = state;
                    _loaded = true;
                    return _state;
                }

                // Unsupported schema or null payload: preserve the file for
                // inspection instead of overwriting it with empty state.
                PreserveCorruptFileLocked();
                _logger.LogWarning(
                    "Watchlist: ignoring state file with unsupported schema version at {Path}.",
                    _filePath);
            }
        }
        catch (JsonException ex)
        {
            // Corrupt JSON: preserve the file and start fresh so one bad write
            // does not poison every future load.
            PreserveCorruptFileLocked();
            _logger.LogWarning(ex, "Watchlist: failed to parse state at {Path}; starting fresh.", _filePath);
        }
        catch (IOException ex)
        {
            // Transient read failure (lock, antivirus, network path): do not
            // mark loaded and do not overwrite the file. The caller sees an
            // exception and can retry on the next event or scan.
            _logger.LogWarning(ex, "Watchlist: failed to load state from {Path}; will retry.", _filePath);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Watchlist: failed to load state from {Path}; will retry.", _filePath);
            throw;
        }

        _state = new WatchlistState();
        _loaded = true;
        return _state;
    }

    private void PreserveCorruptFileLocked()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var backup = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                File.Move(_filePath, backup);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watchlist: failed to preserve corrupt state file at {Path}.", _filePath);
        }
    }

    private static void Normalize(WatchlistState state)
    {
        state.Users ??= new Dictionary<string, UserWatchState>();

        // Drop null user entries left by hand edits or partial corruption
        // instead of failing on every later access.
        foreach (var key in state.Users.Keys.Where(key => state.Users[key] is null).ToList())
        {
            state.Users.Remove(key);
        }

        foreach (var user in state.Users.Values)
        {
            user.Movies ??= new Dictionary<string, MovieWatchState>();
            user.Series ??= new Dictionary<string, SeriesWatchState>();
            user.Seasons ??= new Dictionary<string, SeasonWatchState>();
            user.Removed ??= new Dictionary<string, RemovedWatchEntry>();

            foreach (var key in user.Movies.Keys.Where(key => user.Movies[key] is null).ToList())
            {
                user.Movies.Remove(key);
            }

            foreach (var key in user.Series.Keys.Where(key => user.Series[key] is null).ToList())
            {
                user.Series.Remove(key);
            }

            foreach (var key in user.Seasons.Keys.Where(key => user.Seasons[key] is null).ToList())
            {
                user.Seasons.Remove(key);
            }

            foreach (var key in user.Removed.Keys.Where(key => user.Removed[key] is null).ToList())
            {
                user.Removed.Remove(key);
            }

            foreach (var series in user.Series.Values)
            {
                series.ContentFingerprint ??= string.Empty;
            }

            foreach (var season in user.Seasons.Values)
            {
                season.ContentFingerprint ??= string.Empty;
            }

            foreach (var removed in user.Removed.Values)
            {
                removed.Kind ??= string.Empty;
            }
        }
    }

    private UserWatchState? FindUserLocked(Guid userId)
    {
        var state = EnsureLoadedLocked();
        return state.Users.TryGetValue(Key(userId), out var user) ? user : null;
    }

    private UserWatchState GetOrCreateUserLocked(Guid userId)
    {
        var state = EnsureLoadedLocked();
        var key = Key(userId);
        if (!state.Users.TryGetValue(key, out var user))
        {
            user = new UserWatchState();
            state.Users[key] = user;
        }

        return user;
    }

    private void SaveLocked()
    {
        try
        {
            var state = EnsureLoadedLocked();
            state.SchemaVersion = CurrentSchemaVersion;
            Directory.CreateDirectory(_directory);

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchlist: failed to save state to {Path}.", _filePath);
        }
    }
}
