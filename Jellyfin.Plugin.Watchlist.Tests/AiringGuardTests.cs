using Jellyfin.Plugin.Watchlist.Logic;
using Jellyfin.Plugin.Watchlist.Models;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class AiringGuardTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Future = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Past = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static EpisodeSnapshot Ep(int? season, DateTime? aired, bool missing = false)
    {
        return new EpisodeSnapshot(season, aired, missing, true, Guid.NewGuid());
    }

    private static WatchlistOptions Options(CompletionMode mode, bool excludeSpecials = true)
    {
        return new WatchlistOptions(
            mode,
            excludeSpecials,
            TreatUnknownSeriesStatusAsEnded: false,
            AutoRemoveMovies: true,
            AutoRemoveSeries: true,
            AutoRemoveSeasons: true,
            EnableRewatch: true,
            WaitForAiring: true,
            ExemptTag: "keep-favorite",
            DryRun: false,
            EnableReconciliation: true);
    }

    [Fact]
    public void Series_NoFutureEpisodes_IsNotAiring()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(1, Past)],
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.False(result.IsAiring);
        Assert.Null(result.NextPremiere);
    }

    [Fact]
    public void Series_FutureEpisodeAnywhere_IsAiringWithEarliestDate()
    {
        var later = new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(2, later), Ep(2, Future)],
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.True(result.IsAiring);
        Assert.Equal(Future, result.NextPremiere);
    }

    [Fact]
    public void Series_FutureVirtualPlaceholder_CountsAsAnnounced()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(2, Future, missing: true)],
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.True(result.IsAiring);
    }

    [Fact]
    public void Series_MissingWithPastOrUnknownDate_DoesNotCount()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(2, Past, missing: true), Ep(2, null, missing: true)],
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.False(result.IsAiring);
    }

    [Fact]
    public void Series_NullPremiereDate_DoesNotCount()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(1, null)],
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.False(result.IsAiring);
    }

    [Fact]
    public void Series_PremiereExactlyNow_IsNotFuture()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Now)],
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.False(result.IsAiring);
    }

    [Fact]
    public void Series_FutureSpecial_ExcludedByDefault()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(0, Future)],
            Options(CompletionMode.EndedAndFullyWatched, excludeSpecials: true),
            Now);

        Assert.False(result.IsAiring);
    }

    [Fact]
    public void Series_FutureSpecial_IncludedWhenSpecialsCounted()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(0, Future)],
            Options(CompletionMode.EndedAndFullyWatched, excludeSpecials: false),
            Now);

        Assert.True(result.IsAiring);
    }

    [Fact]
    public void Series_CaughtUp_IsNeverGuarded()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(1, Future)],
            Options(CompletionMode.CaughtUp),
            Now);

        Assert.False(result.IsAiring);
    }

    [Fact]
    public void LatestSeason_FutureInNewestSeason_IsAiring()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(2, Past), Ep(2, Future)],
            Options(CompletionMode.LatestSeasonWatched),
            Now);

        Assert.True(result.IsAiring);
        Assert.Equal(Future, result.NextPremiere);
    }

    [Fact]
    public void LatestSeason_FutureOnlyInOlderSeason_IsNotAiring()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Future), Ep(2, Past)],
            Options(CompletionMode.LatestSeasonWatched),
            Now);

        Assert.False(result.IsAiring);
    }

    [Fact]
    public void LatestSeason_AnnouncedFutureSeason_IsAiring()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(3, Future, missing: true)],
            Options(CompletionMode.LatestSeasonWatched),
            Now);

        Assert.True(result.IsAiring);
    }

    [Fact]
    public void LatestSeason_NoFutureDates_IsNotAiringBetweenSeasons()
    {
        var result = AiringGuard.CheckSeriesAiring(
            [Ep(1, Past), Ep(2, Past)],
            Options(CompletionMode.LatestSeasonWatched),
            Now);

        Assert.False(result.IsAiring);
    }

    [Fact]
    public void Season_FutureEpisodeInScope_IsAiring()
    {
        var result = AiringGuard.CheckSeasonAiring(
            [Ep(1, Past), Ep(1, Future)],
            1,
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.True(result.IsAiring);
        Assert.Equal(Future, result.NextPremiere);
    }

    [Fact]
    public void Season_FutureEpisodeInOtherSeason_IsNotAiring()
    {
        var result = AiringGuard.CheckSeasonAiring(
            [Ep(1, Past), Ep(2, Future)],
            1,
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.False(result.IsAiring);
    }

    [Fact]
    public void Season_FutureVirtualPlaceholder_Counts()
    {
        var result = AiringGuard.CheckSeasonAiring(
            [Ep(1, Past), Ep(1, Future, missing: true)],
            1,
            Options(CompletionMode.EndedAndFullyWatched),
            Now);

        Assert.True(result.IsAiring);
    }

    [Fact]
    public void Season_ZeroFavoritedDirectly_FutureSpecialBlocks()
    {
        var result = AiringGuard.CheckSeasonAiring(
            [Ep(0, Past), Ep(0, Future)],
            0,
            Options(CompletionMode.EndedAndFullyWatched, excludeSpecials: true),
            Now);

        Assert.True(result.IsAiring);
    }

    [Fact]
    public void Season_NonZeroScope_NeverMatchesSeasonZero()
    {
        var result = AiringGuard.CheckSeasonAiring(
            [Ep(0, Future)],
            1,
            Options(CompletionMode.EndedAndFullyWatched, excludeSpecials: false),
            Now);

        Assert.False(result.IsAiring);
    }

    [Theory]
    [InlineData(MediaBrowser.Model.Entities.SeriesStatus.Continuing, true)]
    [InlineData(MediaBrowser.Model.Entities.SeriesStatus.Unreleased, true)]
    [InlineData(MediaBrowser.Model.Entities.SeriesStatus.Ended, false)]
    [InlineData(null, false)]
    public void SeriesActiveForReset_MatchesContinuingAndUnreleased(
        MediaBrowser.Model.Entities.SeriesStatus? status,
        bool expected)
    {
        Assert.Equal(expected, AiringGuard.IsSeriesActiveForReset(status));
    }
}
