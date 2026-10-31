using Jellyfin.Plugin.Watchlist.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Watchlist.Logic;

/// <summary>
/// Selects the user data changes a batch must keep so no favorite/play/rating
/// transition is lost. For each item this preserves the first and last rating
/// event plus the latest non-rating event, in arrival order.
/// </summary>
public static class UserDataChangeBatchSelector
{
    /// <summary>
    /// Selects the changes to process from an arrival-ordered batch.
    /// </summary>
    /// <param name="changes">The changes in arrival order.</param>
    /// <returns>The selected changes, grouped by item in first-arrival order.</returns>
    public static IReadOnlyList<UserDataChange> Select(IReadOnlyList<UserDataChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var keysInOrder = new List<(Guid UserId, Guid ItemId)>();
        var states = new Dictionary<(Guid UserId, Guid ItemId), ItemSelection>();

        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            if (change is null)
            {
                continue;
            }

            var key = (change.UserId, change.ItemId);
            if (!states.TryGetValue(key, out var state))
            {
                state = new ItemSelection();
                states[key] = state;
                keysInOrder.Add(key);
            }

            if (change.SaveReason == UserDataSaveReason.UpdateUserRating)
            {
                state.FirstRatingIndex ??= index;
                state.LastRatingIndex = index;
            }
            else
            {
                state.LatestOtherIndex = index;
            }
        }

        var selected = new List<UserDataChange>(changes.Count);
        var emitted = new HashSet<UserDataChange>(ReferenceEqualityComparer.Instance);

        foreach (var key in keysInOrder)
        {
            var state = states[key];
            var indices = new SortedSet<int>();
            if (state.FirstRatingIndex is { } first)
            {
                indices.Add(first);
            }

            if (state.LastRatingIndex is { } last)
            {
                indices.Add(last);
            }

            if (state.LatestOtherIndex is { } other)
            {
                indices.Add(other);
            }

            foreach (var index in indices)
            {
                var change = changes[index];
                if (emitted.Add(change))
                {
                    selected.Add(change);
                }
            }
        }

        return selected;
    }

    private sealed class ItemSelection
    {
        public int? FirstRatingIndex { get; set; }

        public int? LastRatingIndex { get; set; }

        public int? LatestOtherIndex { get; set; }
    }
}
