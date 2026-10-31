namespace Jellyfin.Plugin.Watchlist.Logic;

/// <summary>
/// The last observed completion state of a series.
/// </summary>
/// <param name="Complete">Whether the series was complete.</param>
/// <param name="EpisodeCount">The number of eligible episodes considered.</param>
/// <param name="LatestSeasonNumber">The latest season number considered.</param>
/// <param name="ContentFingerprint">A stable fingerprint of the considered episode ids, or empty when unknown.</param>
public sealed record SeriesObservation(
    bool Complete,
    int EpisodeCount,
    int LatestSeasonNumber,
    string ContentFingerprint = "");

/// <summary>
/// Decides whether a favorite should be removed based on the previous observation
/// and the current state. Removing only on a transition prevents items that were
/// already complete when favorited from disappearing.
/// </summary>
public static class RemovalPolicy
{
    /// <summary>
    /// Decides whether a favorite movie should be removed.
    /// </summary>
    /// <param name="observedPlayed">The previously observed play state, or null when unknown.</param>
    /// <param name="currentPlayed">The current play state.</param>
    /// <returns><c>true</c> when the movie just transitioned to played.</returns>
    public static bool ShouldRemoveMovie(bool? observedPlayed, bool currentPlayed)
    {
        return currentPlayed && observedPlayed == false;
    }

    /// <summary>
    /// Decides whether a favorite series should be removed.
    /// </summary>
    /// <param name="observed">The previous observation, or null when unknown.</param>
    /// <param name="current">The current completion result.</param>
    /// <returns><c>true</c> when the series completed or finished new content.</returns>
    public static bool ShouldRemoveSeries(SeriesObservation? observed, CompletionResult current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!current.IsComplete || observed is null)
        {
            return false;
        }

        if (!observed.Complete)
        {
            // Content shrinking counts as a library change, not as viewing.
            // Only a completion with at least as much content as the baseline
            // can remove the series.
            if (current.LatestSeasonNumber < observed.LatestSeasonNumber)
            {
                return false;
            }

            if (current.LatestSeasonNumber == observed.LatestSeasonNumber
                && current.EpisodeCount < observed.EpisodeCount)
            {
                return false;
            }

            return true;
        }

        if (current.LatestSeasonNumber > observed.LatestSeasonNumber)
        {
            return true;
        }

        if (current.LatestSeasonNumber < observed.LatestSeasonNumber)
        {
            return false;
        }

        if (current.EpisodeCount > observed.EpisodeCount)
        {
            return true;
        }

        if (current.EpisodeCount < observed.EpisodeCount)
        {
            return false;
        }

        // Same season and count: only replaced content (different episode ids)
        // counts as new. An empty fingerprint means unknown (old state or
        // tests), which never triggers removal by itself.
        return FingerprintDiffers(observed.ContentFingerprint, current.ContentFingerprint);
    }

    /// <summary>
    /// Decides whether the stored series observation should be replaced with the
    /// current completion result when no removal happens.
    /// </summary>
    /// <param name="observed">The previous observation, or null when unknown.</param>
    /// <param name="current">The current completion result.</param>
    /// <returns><c>true</c> when the current result should become the new baseline.</returns>
    public static bool ShouldReplaceSeriesObservation(SeriesObservation? observed, CompletionResult current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (observed is null)
        {
            return true;
        }

        if (!observed.Complete)
        {
            // Always refresh an incomplete baseline. Shrinking content cannot
            // remove (see ShouldRemoveSeries), so re-baselining here is safe
            // and lets returning content be tracked correctly.
            return true;
        }

        if (current.IsComplete)
        {
            // Never downgrade a complete baseline to smaller or older content.
            // A transient library change (offline share, missing episodes)
            // must not shrink the baseline that protects a kept series.
            if (current.LatestSeasonNumber < observed.LatestSeasonNumber)
            {
                return false;
            }

            if (current.LatestSeasonNumber == observed.LatestSeasonNumber
                && current.EpisodeCount < observed.EpisodeCount)
            {
                return false;
            }

            return true;
        }

        // Complete -> incomplete: only new content (more episodes, a higher
        // season, or replaced episode ids) re-baselines. Anything else is an
        // unplay/rewatch or shrunk content, which keeps the complete baseline
        // for maximum rewatch safety.
        if (current.LatestSeasonNumber > observed.LatestSeasonNumber)
        {
            return true;
        }

        if (current.LatestSeasonNumber < observed.LatestSeasonNumber)
        {
            return false;
        }

        if (current.EpisodeCount > observed.EpisodeCount)
        {
            return true;
        }

        if (current.EpisodeCount < observed.EpisodeCount)
        {
            return false;
        }

        return FingerprintDiffers(observed.ContentFingerprint, current.ContentFingerprint);
    }

    private static bool FingerprintDiffers(string observedFingerprint, string currentFingerprint)
    {
        return !string.IsNullOrEmpty(observedFingerprint)
            && !string.IsNullOrEmpty(currentFingerprint)
            && !string.Equals(observedFingerprint, currentFingerprint, StringComparison.Ordinal);
    }
}
