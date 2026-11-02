namespace Jellyfin.Plugin.Watchlist.Logic;

/// <summary>
/// Decides whether a re-added favorite qualifies for a rewatch reset.
/// Reset only applies after the plugin auto-removed the item (tombstone),
/// when the unit is still fully watched, the feature is enabled, and the
/// item is not exempt or disabled.
/// </summary>
public static class ResetPolicy
{
    /// <summary>
    /// Whether a re-added movie should be reset.
    /// </summary>
    public static bool ShouldResetMovie(
        bool currentPlayed,
        bool hasTombstone,
        bool rewatchEnabled,
        bool autoRemoveEnabled,
        bool isExempt,
        bool dryRunPendingOnly = false)
    {
        if (!hasTombstone || !rewatchEnabled || !autoRemoveEnabled || isExempt)
        {
            return false;
        }

        return currentPlayed;
    }

    /// <summary>
    /// Whether a re-added season or series should be reset.
    /// The caller passes the literal fully-watched result.
    /// </summary>
    public static bool ShouldResetCompletion(
        CompletionResult fullyWatched,
        bool hasTombstone,
        bool rewatchEnabled,
        bool autoRemoveEnabled,
        bool isExempt)
    {
        ArgumentNullException.ThrowIfNull(fullyWatched);

        if (!hasTombstone || !rewatchEnabled || !autoRemoveEnabled || isExempt)
        {
            return false;
        }

        return fullyWatched.IsComplete;
    }
}
