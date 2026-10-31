using Jellyfin.Plugin.Watchlist.Logic;
using Jellyfin.Plugin.Watchlist.Models;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class CompletionEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EndedAndFullyWatched_EndedAndAllPlayed_IsComplete()
    {
        var result = Evaluate(
            CompletionMode.EndedAndFullyWatched,
            isEnded: true,
            Episode(1, played: true),
            Episode(1, played: true));

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.EpisodeCount);
        Assert.Equal(1, result.LatestSeasonNumber);
    }

    [Fact]
    public void EndedAndFullyWatched_ContinuingAndAllPlayed_IsNotComplete()
    {
        var result = Evaluate(
            CompletionMode.EndedAndFullyWatched,
            isEnded: false,
            Episode(1, played: true),
            Episode(1, played: true));

        Assert.False(result.IsComplete);
    }

    [Fact]
    public void EndedAndFullyWatched_OneUnplayed_IsNotComplete()
    {
        var result = Evaluate(
            CompletionMode.EndedAndFullyWatched,
            isEnded: true,
            Episode(1, played: true),
            Episode(2, played: false));

        Assert.False(result.IsComplete);
    }

    [Fact]
    public void CaughtUp_ContinuingAndAllPlayed_IsComplete()
    {
        var result = Evaluate(
            CompletionMode.CaughtUp,
            isEnded: false,
            Episode(1, played: true),
            Episode(2, played: true));

        Assert.True(result.IsComplete);
    }

    [Fact]
    public void CaughtUp_OneUnplayed_IsNotComplete()
    {
        var result = Evaluate(
            CompletionMode.CaughtUp,
            isEnded: false,
            Episode(1, played: true),
            Episode(2, played: false));

        Assert.False(result.IsComplete);
    }

    [Fact]
    public void Specials_AreExcludedByDefault()
    {
        var result = Evaluate(
            CompletionMode.EndedAndFullyWatched,
            isEnded: true,
            Episode(0, played: false),
            Episode(1, played: true));

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.EpisodeCount);
    }

    [Fact]
    public void Specials_CanBeIncluded()
    {
        var result = Evaluate(
            CompletionMode.EndedAndFullyWatched,
            isEnded: true,
            excludeSpecials: false,
            Episode(0, played: false),
            Episode(1, played: true));

        Assert.False(result.IsComplete);
        Assert.Equal(2, result.EpisodeCount);
    }

    [Fact]
    public void UnairedEpisodes_AreIgnored()
    {
        var result = Evaluate(
            CompletionMode.EndedAndFullyWatched,
            isEnded: true,
            Episode(1, played: true),
            Episode(2, played: false, premiereDate: Now.AddDays(7)));

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.EpisodeCount);
    }

    [Fact]
    public void MissingEpisodes_AreIgnored()
    {
        var result = Evaluate(
            CompletionMode.EndedAndFullyWatched,
            isEnded: true,
            Episode(1, played: true),
            Episode(2, played: false, isMissing: true));

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.EpisodeCount);
    }

    [Fact]
    public void NoEligibleEpisodes_IsNotComplete()
    {
        var result = Evaluate(
            CompletionMode.CaughtUp,
            isEnded: true,
            Episode(0, played: true),
            Episode(1, played: false, isMissing: true));

        Assert.False(result.IsComplete);
        Assert.Equal(0, result.EpisodeCount);
    }

    [Fact]
    public void LatestSeasonWatched_OnlyRequiresTheNewestSeason()
    {
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            Episode(1, played: false),
            Episode(2, played: true),
            Episode(2, played: true));

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.EpisodeCount);
        Assert.Equal(2, result.LatestSeasonNumber);
    }

    [Fact]
    public void LatestSeasonWatched_NewestSeasonIncomplete_IsNotComplete()
    {
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            Episode(1, played: true),
            Episode(2, played: true),
            Episode(2, played: false));

        Assert.False(result.IsComplete);
    }

    [Fact]
    public void LatestSeasonWatched_IgnoresSeasonZero()
    {
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            excludeSpecials: false,
            Episode(0, played: false),
            Episode(1, played: true));

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.LatestSeasonNumber);
    }

    [Fact]
    public void LatestSeasonWatched_FutureSeason_DoesNotComplete()
    {
        // Season 2 is fully watched, but season 3 exists with only unaired
        // episodes. The plugin must not fall back to season 2 and remove the
        // series before season 3 airs.
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            Episode(2, played: true),
            Episode(2, played: true),
            Episode(3, played: false, premiereDate: Now.AddDays(7)));

        Assert.False(result.IsComplete);
        Assert.Equal(3, result.LatestSeasonNumber);
    }

    [Fact]
    public void LatestSeasonWatched_PartiallyAiredNewestSeason_OnlyConsidersAired()
    {
        // Only the season 3 premiere has aired and been watched; the rest of
        // season 3 is unaired and ignored.
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            Episode(2, played: true),
            Episode(3, played: true, premiereDate: Now.AddDays(-1)),
            Episode(3, played: false, premiereDate: Now.AddDays(7)));

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.EpisodeCount);
        Assert.Equal(3, result.LatestSeasonNumber);
    }

    [Fact]
    public void MissingSeasonNumber_IsTreatedAsSeasonOne()
    {
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            Episode(null, played: true),
            Episode(null, played: true));

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.LatestSeasonNumber);
        Assert.Equal(2, result.EpisodeCount);
    }

    [Fact]
    public void EndedAndFullyWatched_LatestSeasonNumberReflectsNewContent()
    {
        var result = Evaluate(
            CompletionMode.EndedAndFullyWatched,
            isEnded: true,
            Episode(1, played: true),
            Episode(2, played: true),
            Episode(2, played: true));

        Assert.True(result.IsComplete);
        Assert.Equal(3, result.EpisodeCount);
        Assert.Equal(2, result.LatestSeasonNumber);
    }

    [Fact]
    public void Fingerprint_IsStableRegardlessOfOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var one = Evaluate(
            CompletionMode.CaughtUp,
            isEnded: true,
            Episode(1, played: true, id: first),
            Episode(1, played: true, id: second));

        var two = Evaluate(
            CompletionMode.CaughtUp,
            isEnded: true,
            Episode(1, played: true, id: second),
            Episode(1, played: true, id: first));

        Assert.NotEqual(string.Empty, one.ContentFingerprint);
        Assert.Equal(one.ContentFingerprint, two.ContentFingerprint);
    }

    [Fact]
    public void Fingerprint_DiffersWhenEpisodeReplaced()
    {
        var result = Evaluate(
            CompletionMode.CaughtUp,
            isEnded: true,
            Episode(1, played: true, id: Guid.NewGuid()),
            Episode(1, played: true, id: Guid.NewGuid()));

        Assert.NotEqual(string.Empty, result.ContentFingerprint);
    }

    private static CompletionResult Evaluate(
        CompletionMode mode,
        bool isEnded,
        params EpisodeSnapshot[] episodes)
    {
        return Evaluate(mode, isEnded, excludeSpecials: true, episodes);
    }

    private static CompletionResult Evaluate(
        CompletionMode mode,
        bool isEnded,
        bool excludeSpecials,
        params EpisodeSnapshot[] episodes)
    {
        var options = new WatchlistOptions(
            mode,
            excludeSpecials,
            TreatUnknownSeriesStatusAsEnded: false,
            AutoRemoveMovies: true,
            AutoRemoveSeries: true,
            ExemptTag: "keep-favorite",
            DryRun: false,
            EnableReconciliation: true);

        return CompletionEvaluator.Evaluate(
            new SeriesSnapshot(isEnded, episodes),
            options,
            Now);
    }

    private static EpisodeSnapshot Episode(
        int? season,
        bool played,
        DateTime? premiereDate = null,
        bool isMissing = false,
        Guid id = default)
    {
        return new EpisodeSnapshot(
            season,
            premiereDate ?? Now.AddDays(-1),
            isMissing,
            played,
            id);
    }

    [Fact]
    public void LatestSeasonWatched_AnnouncedFutureSeasonWithoutFiles_DoesNotComplete()
    {
        // A future season that only exists as virtual/missing placeholders must
        // still block the fallback to an older fully watched season.
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            Episode(2, played: true),
            Episode(2, played: true),
            Episode(3, played: false, premiereDate: Now.AddDays(7), isMissing: true));

        Assert.False(result.IsComplete);
        Assert.Equal(3, result.LatestSeasonNumber);
    }

    [Fact]
    public void LatestSeasonWatched_MissingSeasonWithPastPremiere_DoesNotBlock()
    {
        // Placeholders for removed content are not an announced season and must
        // not keep an older fully watched season on the list.
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            Episode(2, played: true),
            Episode(2, played: true),
            Episode(3, played: false, premiereDate: Now.AddDays(-30), isMissing: true));

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.LatestSeasonNumber);
    }

    [Fact]
    public void LatestSeasonWatched_OnlyAnnouncedSeason_ReportsNewestSeason()
    {
        var result = Evaluate(
            CompletionMode.LatestSeasonWatched,
            isEnded: false,
            Episode(3, played: false, premiereDate: Now.AddDays(7), isMissing: true));

        Assert.False(result.IsComplete);
        Assert.Equal(0, result.EpisodeCount);
        Assert.Equal(3, result.LatestSeasonNumber);
    }

    [Fact]
    public void Fingerprint_DiffersBetweenDifferentEpisodeSets()
    {
        var first = Evaluate(
            CompletionMode.CaughtUp,
            isEnded: true,
            Episode(1, played: true, id: Guid.NewGuid()),
            Episode(1, played: true, id: Guid.NewGuid()));

        var second = Evaluate(
            CompletionMode.CaughtUp,
            isEnded: true,
            Episode(1, played: true, id: Guid.NewGuid()),
            Episode(1, played: true, id: Guid.NewGuid()));

        Assert.NotEqual(string.Empty, first.ContentFingerprint);
        Assert.NotEqual(first.ContentFingerprint, second.ContentFingerprint);
    }
}
