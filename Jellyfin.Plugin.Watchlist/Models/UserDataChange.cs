namespace Jellyfin.Plugin.Watchlist.Models;

using MediaBrowser.Model.Entities;

/// <summary>
/// The kind of item a user data event refers to.
/// </summary>
public enum WatchlistItemKind
{
    /// <summary>A movie.</summary>
    Movie,

    /// <summary>A series.</summary>
    Series,

    /// <summary>An episode.</summary>
    Episode,

    /// <summary>Any other item type.</summary>
    Other
}

/// <summary>
/// A copy of the parts of a user data event that the background worker needs.
/// </summary>
/// <param name="UserId">The user whose data changed.</param>
/// <param name="ItemId">The item that changed.</param>
/// <param name="Kind">The kind of item.</param>
/// <param name="SeriesId">The parent series for an episode, when known.</param>
/// <param name="IsFavorite">Whether the item is now a favorite.</param>
/// <param name="Played">Whether the item is now played.</param>
/// <param name="SaveReason">Why the user data was saved.</param>
public sealed record UserDataChange(
    Guid UserId,
    Guid ItemId,
    WatchlistItemKind Kind,
    Guid SeriesId,
    bool IsFavorite,
    bool Played,
    UserDataSaveReason SaveReason);
