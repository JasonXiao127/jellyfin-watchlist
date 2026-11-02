using Jellyfin.Plugin.Watchlist.Models;

namespace Jellyfin.Plugin.Watchlist.Logic;

/// <summary>
/// Whether a watchlist unit is still airing, and the next known premiere.
/// Airing is defined by future episode premiere dates in the evaluated scope,
/// not by series status: dates are scoped and release themselves over time,
/// while status is series-wide and often stale.
/// </summary>
/// <param name="IsAiring">Whether the unit has known future episodes.</param>
/// <param name="NextPremiere">The earliest known future premiere, if any.</param>
public sealed record AiringInfo(bool IsAiring, DateTime? NextPremiere)
{
    /// <summary>
    /// Gets a shared not-airing result.
    /// </summary>
    public static AiringInfo NotAiring { get; } = new(false, null);
}

/// <summary>
/// Decides whether a series or season is still airing based on known future
/// episode premiere dates. Future episodes stay ineligible for completion;
/// this guard only decides whether an otherwise complete unit may be removed
/// or have its played state reset.
/// </summary>
public static class AiringGuard
{
    /// <summary>
    /// Checks whether a series is still airing in the scope of the configured
    /// completion mode. Caught up mode is never guarded: its contract is to
    /// remove as soon as the available episodes are watched.
    /// </summary>
    /// <param name="episodes">The series episodes, including missing and virtual placeholders.</param>
    /// <param name="options">The completion options.</param>
    /// <param name="nowUtc">The current UTC time.</param>
    /// <returns>The airing result.</returns>
    public static AiringInfo CheckSeriesAiring(
        IReadOnlyList<EpisodeSnapshot> episodes,
        WatchlistOptions options,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Mode == CompletionMode.CaughtUp)
        {
            return AiringInfo.NotAiring;
        }

        if (options.Mode == CompletionMode.LatestSeasonWatched)
        {
            // Only the newest known season matters. It is computed from aired
            // episodes plus announced (future-dated) placeholders, so an
            // announced future season is itself the newest season.
            var newest = episodes
                .Where(episode => !episode.IsMissing || IsAnnounced(episode, nowUtc))
                .Select(episode => NormalizeSeason(episode.SeasonNumber))
                .Where(season => season > 0)
                .DefaultIfEmpty(0)
                .Max();

            if (newest <= 0)
            {
                return AiringInfo.NotAiring;
            }

            return FirstFuture(
                episodes.Where(episode => NormalizeSeason(episode.SeasonNumber) == newest),
                includeSpecials: false,
                nowUtc);
        }

        // Ended + fully watched: any future episode anywhere in the series.
        return FirstFuture(episodes, !options.ExcludeSpecials, nowUtc);
    }

    /// <summary>
    /// Checks whether a single season is still airing. A directly favorited
    /// season zero is evaluated literally, mirroring completion.
    /// </summary>
    /// <param name="episodes">The season scope episodes, including missing and virtual placeholders.</param>
    /// <param name="seasonNumber">The season number being evaluated.</param>
    /// <param name="options">The completion options.</param>
    /// <param name="nowUtc">The current UTC time.</param>
    /// <returns>The airing result.</returns>
    public static AiringInfo CheckSeasonAiring(
        IReadOnlyList<EpisodeSnapshot> episodes,
        int seasonNumber,
        WatchlistOptions options,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(options);

        var scoped = episodes.Where(episode => NormalizeSeason(episode.SeasonNumber) == seasonNumber);

        // Only a directly favorited season zero bypasses the specials exclusion.
        var includeSpecials = seasonNumber == 0 || !options.ExcludeSpecials;
        return FirstFuture(scoped, includeSpecials, nowUtc);
    }

    /// <summary>
    /// Determines whether a series re-add must never trigger a rewatch reset
    /// because the show is still active, even when no future episode date is
    /// known (for example between seasons).
    /// </summary>
    /// <param name="status">The Jellyfin series status, if known.</param>
    /// <returns><c>true</c> when a reset would destroy in-progress tracking.</returns>
    public static bool IsSeriesActiveForReset(MediaBrowser.Model.Entities.SeriesStatus? status)
    {
        return status == MediaBrowser.Model.Entities.SeriesStatus.Continuing
            || status == MediaBrowser.Model.Entities.SeriesStatus.Unreleased;
    }

    private static AiringInfo FirstFuture(
        IEnumerable<EpisodeSnapshot> episodes,
        bool includeSpecials,
        DateTime nowUtc)
    {
        DateTime? next = null;
        foreach (var episode in episodes)
        {
            if (!episode.PremiereDate.HasValue || episode.PremiereDate.Value <= nowUtc)
            {
                continue;
            }

            if (!includeSpecials && NormalizeSeason(episode.SeasonNumber) == 0)
            {
                continue;
            }

            if (next is null || episode.PremiereDate.Value < next.Value)
            {
                next = episode.PremiereDate.Value;
            }
        }

        return next is null ? AiringInfo.NotAiring : new AiringInfo(true, next);
    }

    private static int NormalizeSeason(int? seasonNumber)
    {
        return seasonNumber ?? 1;
    }

    private static bool IsAnnounced(EpisodeSnapshot episode, DateTime nowUtc)
    {
        return episode.PremiereDate.HasValue && episode.PremiereDate.Value > nowUtc;
    }
}
