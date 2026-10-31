namespace Jellyfin.Plugin.Watchlist.Logic;

/// <summary>
/// Decides whether an item should still be treated as a favorite when the
/// cached user data and the event snapshot disagree or both say "not favorite".
/// Jellyfin's cached series user data can stay stale after a library scan
/// (single-item lookups report IsFavorite=false while the list query and the
/// database report true). The fresh library favorites query is authoritative
/// in those cases; a lookup failure preserves tracking rather than wiping it.
/// </summary>
public static class FavoriteStatusResolver
{
    /// <summary>
    /// Decides whether the item should be treated as a favorite.
    /// </summary>
    /// <param name="liveIsFavorite">The current cached favorite flag, when known.</param>
    /// <param name="eventIsFavorite">The event snapshot favorite flag, or null to ignore it (episode events carry the episode flag, not the series flag).</param>
    /// <param name="freshLookup">A fresh authoritative lookup. Return null when the lookup fails.</param>
    /// <returns><c>true</c> when the item should be treated as a favorite.</returns>
    public static bool ShouldTreatAsFavorite(
        bool? liveIsFavorite,
        bool? eventIsFavorite,
        Func<bool?> freshLookup)
    {
        ArgumentNullException.ThrowIfNull(freshLookup);

        // Both sources agree it is a favorite: no extra query needed.
        if (liveIsFavorite == true && (eventIsFavorite is null || eventIsFavorite == true))
        {
            return true;
        }

        // Both say not favorite, or they disagree: the cached value may be
        // stale after a library scan, so the fresh library query decides.
        // A failed lookup preserves tracking rather than wiping a baseline.
        return freshLookup() ?? true;
    }
}
