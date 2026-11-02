using Jellyfin.Plugin.Watchlist.Logic;
using Jellyfin.Plugin.Watchlist.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class UserDataChangeBatchSelectorTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ItemId = Guid.NewGuid();

    [Fact]
    public void Select_KeepsFirstAndLastRatingAndLatestOtherInArrivalOrder()
    {
        var unfavorite = Change(UserDataSaveReason.UpdateUserRating, played: false, isFavorite: false);
        var play = Change(UserDataSaveReason.PlaybackFinished, played: true, isFavorite: false);
        var rate = Change(UserDataSaveReason.UpdateUserRating, played: true, isFavorite: true);

        var selected = UserDataChangeBatchSelector.Select([unfavorite, play, rate]);

        Assert.Collection(
            selected,
            change => Assert.Same(unfavorite, change),
            change => Assert.Same(play, change),
            change => Assert.Same(rate, change));
    }

    [Fact]
    public void Select_KeepsOnlyLatestNonRatingEvent()
    {
        var firstRating = Change(UserDataSaveReason.UpdateUserRating, played: false);
        var firstPlay = Change(UserDataSaveReason.PlaybackFinished, played: true);
        var laterPlay = Change(UserDataSaveReason.TogglePlayed, played: true);
        var lastRating = Change(UserDataSaveReason.UpdateUserRating, played: true);

        var selected = UserDataChangeBatchSelector.Select([firstRating, firstPlay, laterPlay, lastRating]);

        Assert.Collection(
            selected,
            change => Assert.Same(firstRating, change),
            change => Assert.Same(laterPlay, change),
            change => Assert.Same(lastRating, change));
    }

    [Fact]
    public void Select_SingleRatingEventIsNotDuplicated()
    {
        var rating = Change(UserDataSaveReason.UpdateUserRating, played: false);

        var selected = UserDataChangeBatchSelector.Select([rating]);

        Assert.Single(selected);
        Assert.Same(rating, selected[0]);
    }

    [Fact]
    public void Select_DeduplicatesIdenticalReferences()
    {
        var play = Change(UserDataSaveReason.PlaybackFinished, played: true);

        var selected = UserDataChangeBatchSelector.Select([play, play]);

        Assert.Single(selected);
        Assert.Same(play, selected[0]);
    }

    [Fact]
    public void Select_GroupsKeysByFirstArrivalAndKeepsPerKeyArrivalOrder()
    {
        var otherItem = Guid.NewGuid();
        var firstRating = Change(UserDataSaveReason.UpdateUserRating, played: false);
        var secondPlay = Change(UserDataSaveReason.PlaybackFinished, played: true, itemId: otherItem);
        var firstPlay = Change(UserDataSaveReason.PlaybackFinished, played: true);

        var selected = UserDataChangeBatchSelector.Select([firstRating, secondPlay, firstPlay]);

        Assert.Collection(
            selected,
            change => Assert.Same(firstRating, change),
            change => Assert.Same(firstPlay, change),
            change => Assert.Same(secondPlay, change));
    }

    [Fact]
    public void Select_PlayBeforeLaterRating_KeepsArrivalOrder()
    {
        var play = Change(UserDataSaveReason.PlaybackFinished, played: true);
        var rate = Change(UserDataSaveReason.UpdateUserRating, played: true);

        var selected = UserDataChangeBatchSelector.Select([play, rate]);

        Assert.Collection(
            selected,
            change => Assert.Same(play, change),
            change => Assert.Same(rate, change));
    }

    [Fact]
    public void Select_EmptyBatch_ReturnsEmpty()
    {
        Assert.Empty(UserDataChangeBatchSelector.Select([]));
    }

    private static UserDataChange Change(
        UserDataSaveReason reason,
        bool played,
        bool isFavorite = true,
        Guid? itemId = null)
    {
        return new UserDataChange(
            UserId,
            itemId ?? ItemId,
            WatchlistItemKind.Movie,
            Guid.Empty,
            Guid.Empty,
            isFavorite,
            played,
            reason);
    }
}
