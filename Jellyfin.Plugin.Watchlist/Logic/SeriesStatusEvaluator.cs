using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Watchlist.Logic;

/// <summary>
/// Maps Jellyfin's series status to the plugin's "ended" flag.
/// </summary>
public static class SeriesStatusEvaluator
{
    /// <summary>
    /// Determines whether a series is considered ended.
    /// </summary>
    /// <param name="status">The Jellyfin series status, if known.</param>
    /// <param name="treatUnknownAsEnded">Whether a missing status counts as ended.</param>
    /// <returns><c>true</c> when the series is considered ended.</returns>
    public static bool IsEnded(SeriesStatus? status, bool treatUnknownAsEnded)
    {
        return status == SeriesStatus.Ended
            || (status is null && treatUnknownAsEnded);
    }
}
