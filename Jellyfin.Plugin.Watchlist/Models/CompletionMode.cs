namespace Jellyfin.Plugin.Watchlist.Models;

/// <summary>
/// The rule used to decide when a series is complete.
/// </summary>
public enum CompletionMode
{
    /// <summary>
    /// Only remove a series when it has ended and every eligible episode is played.
    /// </summary>
    EndedAndFullyWatched,

    /// <summary>
    /// Remove a series as soon as every currently available episode is played.
    /// </summary>
    CaughtUp,

    /// <summary>
    /// Remove a series when every episode of its latest season is played.
    /// </summary>
    LatestSeasonWatched
}
