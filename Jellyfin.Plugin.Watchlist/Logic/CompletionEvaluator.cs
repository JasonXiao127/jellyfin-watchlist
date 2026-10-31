using Jellyfin.Plugin.Watchlist.Models;

namespace Jellyfin.Plugin.Watchlist.Logic;

/// <summary>
/// A single episode as needed by the completion rules.
/// </summary>
/// <param name="SeasonNumber">The season number reported by Jellyfin, or null when unknown.</param>
/// <param name="PremiereDate">The episode's premiere date, if known.</param>
/// <param name="IsMissing">Whether the episode is missing or virtual.</param>
/// <param name="Played">Whether the user has played the episode.</param>
/// <param name="Id">The episode id, when known. Used to detect replaced content.</param>
public sealed record EpisodeSnapshot(
    int? SeasonNumber,
    DateTime? PremiereDate,
    bool IsMissing,
    bool Played,
    Guid Id = default);

/// <summary>
/// A series and its episodes as needed by the completion rules.
/// </summary>
/// <param name="IsEnded">Whether Jellyfin considers the series ended.</param>
/// <param name="Episodes">The episodes in the series.</param>
public sealed record SeriesSnapshot(
    bool IsEnded,
    IReadOnlyList<EpisodeSnapshot> Episodes);

/// <summary>
/// The result of evaluating a series.
/// </summary>
/// <param name="IsComplete">Whether the series is complete under the selected rule.</param>
/// <param name="EpisodeCount">The number of eligible episodes considered.</param>
/// <param name="LatestSeasonNumber">The latest season number considered.</param>
/// <param name="ContentFingerprint">A stable fingerprint of the considered episode ids, or empty when unknown.</param>
public sealed record CompletionResult(
    bool IsComplete,
    int EpisodeCount,
    int LatestSeasonNumber,
    string ContentFingerprint = "");

/// <summary>
/// Applies the configured watchlist completion rules. This type is deliberately
/// independent of Jellyfin entities so it can be tested directly.
/// </summary>
public static class CompletionEvaluator
{
    /// <summary>
    /// Evaluates a series.
    /// </summary>
    /// <param name="series">The series snapshot.</param>
    /// <param name="options">The completion options.</param>
    /// <param name="nowUtc">The current UTC time, used to ignore unaired episodes.</param>
    /// <returns>The completion result.</returns>
    public static CompletionResult Evaluate(
        SeriesSnapshot series,
        WatchlistOptions options,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(options);

        var eligible = series.Episodes
            .Where(episode => !episode.IsMissing)
            .Where(episode => !episode.PremiereDate.HasValue || episode.PremiereDate.Value <= nowUtc)
            .Select(episode => new EligibleEpisode(episode, NormalizeSeason(episode.SeasonNumber)))
            .ToList();

        if (options.ExcludeSpecials)
        {
            eligible = eligible.Where(episode => episode.SeasonNumber != 0).ToList();
        }

        if (options.Mode == CompletionMode.LatestSeasonWatched)
        {
            // The newest season is determined from all non-missing episodes
            // plus announced (unaired) episodes that have no files yet. Virtual
            // or missing placeholders for a future season must still block the
            // fallback to an older season: the series is not complete until its
            // actual newest season is watched. Missing episodes with a past or
            // unknown premiere date never count as a newest season.
            var newestSeason = series.Episodes
                .Where(episode => !episode.IsMissing || IsAnnounced(episode, nowUtc))
                .Select(episode => NormalizeSeason(episode.SeasonNumber))
                .Where(season => season > 0)
                .DefaultIfEmpty(0)
                .Max();

            if (newestSeason <= 0)
            {
                return new CompletionResult(false, 0, 1, string.Empty);
            }

            // Season zero is not a "latest season" even when specials are counted
            // for the full-series modes.
            var seasonEpisodes = eligible.Where(episode => episode.SeasonNumber > 0).ToList();
            if (seasonEpisodes.Count == 0)
            {
                return new CompletionResult(false, 0, newestSeason, string.Empty);
            }

            var latestSeason = seasonEpisodes.Max(episode => episode.SeasonNumber);

            // If the newest known season has no aired eligible episodes yet,
            // report it explicitly so the baseline tracks the new season
            // instead of completing an older one.
            if (latestSeason < newestSeason)
            {
                return new CompletionResult(false, 0, newestSeason, string.Empty);
            }

            var considered = seasonEpisodes
                .Where(episode => episode.SeasonNumber == latestSeason)
                .ToList();

            return new CompletionResult(
                considered.All(episode => episode.Episode.Played),
                considered.Count,
                latestSeason,
                ComputeFingerprint(considered.Select(episode => episode.Episode)));
        }

        if (eligible.Count == 0)
        {
            return new CompletionResult(false, 0, 1);
        }

        var complete = eligible.All(episode => episode.Episode.Played);

        if (options.Mode == CompletionMode.EndedAndFullyWatched && !series.IsEnded)
        {
            complete = false;
        }

        var latestSeasonNumber = eligible.Max(episode => episode.SeasonNumber);
        return new CompletionResult(
            complete,
            eligible.Count,
            latestSeasonNumber,
            ComputeFingerprint(eligible.Select(episode => episode.Episode)));
    }

    private static int NormalizeSeason(int? seasonNumber)
    {
        return seasonNumber ?? 1;
    }

    private static bool IsAnnounced(EpisodeSnapshot episode, DateTime nowUtc)
    {
        return episode.PremiereDate.HasValue && episode.PremiereDate.Value > nowUtc;
    }

    private static string ComputeFingerprint(IEnumerable<EpisodeSnapshot> episodes)
    {
        var ids = episodes
            .Select(episode => episode.Id)
            .Where(id => id != Guid.Empty)
            .Select(id => id.ToString("N"))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
        {
            return string.Empty;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join(",", ids));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private sealed record EligibleEpisode(EpisodeSnapshot Episode, int SeasonNumber);
}
