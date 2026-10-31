using Jellyfin.Plugin.Watchlist.Logic;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class FavoriteStatusResolverTests
{
    [Fact]
    public void BothFavorite_NoFreshLookupNeeded()
    {
        var calls = 0;
        var result = FavoriteStatusResolver.ShouldTreatAsFavorite(
            liveIsFavorite: true,
            eventIsFavorite: true,
            () =>
            {
                calls++;
                return false;
            });

        Assert.True(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void LiveFavoriteWithoutEventFlag_NoFreshLookupNeeded()
    {
        var calls = 0;
        var result = FavoriteStatusResolver.ShouldTreatAsFavorite(
            liveIsFavorite: true,
            eventIsFavorite: null,
            () =>
            {
                calls++;
                return false;
            });

        Assert.True(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void BothNotFavorite_DefersToFreshLookup()
    {
        var result = FavoriteStatusResolver.ShouldTreatAsFavorite(
            liveIsFavorite: false,
            eventIsFavorite: false,
            () => true);

        Assert.True(result);
    }

    [Fact]
    public void BothNotFavorite_FreshSaysNotFavorite()
    {
        var result = FavoriteStatusResolver.ShouldTreatAsFavorite(
            liveIsFavorite: false,
            eventIsFavorite: false,
            () => false);

        Assert.False(result);
    }

    [Fact]
    public void Disagreement_DefersToFreshLookup()
    {
        Assert.True(FavoriteStatusResolver.ShouldTreatAsFavorite(false, true, () => true));
        Assert.False(FavoriteStatusResolver.ShouldTreatAsFavorite(false, true, () => false));
        Assert.True(FavoriteStatusResolver.ShouldTreatAsFavorite(true, false, () => true));
        Assert.False(FavoriteStatusResolver.ShouldTreatAsFavorite(true, false, () => false));
    }

    [Fact]
    public void FreshLookupFailure_PreservesTracking()
    {
        var result = FavoriteStatusResolver.ShouldTreatAsFavorite(
            liveIsFavorite: false,
            eventIsFavorite: false,
            () => null);

        Assert.True(result);
    }

    [Fact]
    public void EpisodeCase_IgnoresEventFlagAndUsesFreshLookup()
    {
        // Episode events carry the episode flag, not the series flag, so the
        // caller passes null and the fresh series lookup decides.
        var result = FavoriteStatusResolver.ShouldTreatAsFavorite(
            liveIsFavorite: false,
            eventIsFavorite: null,
            () => true);

        Assert.True(result);
    }
}
