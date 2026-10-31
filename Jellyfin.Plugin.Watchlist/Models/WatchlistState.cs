namespace Jellyfin.Plugin.Watchlist.Models;

/// <summary>
/// Persisted observations used to distinguish a completion transition from an
/// item that was already complete when it was favorited.
/// </summary>
public sealed class WatchlistState
{
    /// <summary>
    /// Gets or sets the state schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets the signature of the completion options this state was built for.
    /// </summary>
    public string OptionsSignature { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets observations keyed by user id.
    /// </summary>
    public Dictionary<string, UserWatchState> Users { get; set; } = new();
}

/// <summary>
/// Observations for one user.
/// </summary>
public sealed class UserWatchState
{
    /// <summary>
    /// Gets or sets movie observations keyed by item id.
    /// </summary>
    public Dictionary<string, MovieWatchState> Movies { get; set; } = new();

    /// <summary>
    /// Gets or sets series observations keyed by item id.
    /// </summary>
    public Dictionary<string, SeriesWatchState> Series { get; set; } = new();
}

/// <summary>
/// The last observed play state of a favorite movie.
/// </summary>
public sealed class MovieWatchState
{
    /// <summary>
    /// Gets or sets a value indicating whether the movie was played when it was last observed.
    /// </summary>
    public bool Played { get; set; }
}

/// <summary>
/// The last observed completion state of a favorite series.
/// </summary>
public sealed class SeriesWatchState
{
    /// <summary>
    /// Gets or sets a value indicating whether the series was complete when it was last observed.
    /// </summary>
    public bool Complete { get; set; }

    /// <summary>
    /// Gets or sets the number of eligible episodes considered in the observation.
    /// </summary>
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets the latest season number considered in the observation.
    /// </summary>
    public int LatestSeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets a stable fingerprint of the considered episode ids.
    /// </summary>
    public string ContentFingerprint { get; set; } = string.Empty;
}
