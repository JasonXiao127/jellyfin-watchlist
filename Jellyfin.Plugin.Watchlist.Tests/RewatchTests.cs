using Jellyfin.Plugin.Watchlist.Configuration;
using Jellyfin.Plugin.Watchlist.Logic;
using Jellyfin.Plugin.Watchlist.Models;
using Jellyfin.Plugin.Watchlist.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class RewatchTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyfin-watchlist-rewatch", Guid.NewGuid().ToString("N"));

    private static WatchlistOptions Options(
        bool autoRemoveMovies = true,
        bool autoRemoveSeries = true,
        bool autoRemoveSeasons = true,
        bool enableRewatch = true,
        bool excludeSpecials = true,
        bool dryRun = false)
    {
        return new WatchlistOptions(
            CompletionMode.EndedAndFullyWatched,
            excludeSpecials,
            TreatUnknownSeriesStatusAsEnded: false,
            AutoRemoveMovies: autoRemoveMovies,
            AutoRemoveSeries: autoRemoveSeries,
            AutoRemoveSeasons: autoRemoveSeasons,
            EnableRewatch: enableRewatch,
            WaitForAiring: true,
            ExemptTag: "keep-favorite",
            DryRun: dryRun,
            EnableReconciliation: true);
    }

    private static EpisodeSnapshot Ep(int? season, bool played, bool missing = false, DateTime? aired = null)
    {
        return new EpisodeSnapshot(season, aired, missing, played, Guid.NewGuid());
    }

    [Fact]
    public void EvaluateSeason_AllPlayed_IsComplete()
    {
        var result = CompletionEvaluator.EvaluateSeason(
            [Ep(1, true), Ep(1, true)],
            1,
            Options(),
            Now);

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.EpisodeCount);
        Assert.Equal(1, result.LatestSeasonNumber);
    }

    [Fact]
    public void EvaluateSeason_OneUnplayed_IsNotComplete()
    {
        var result = CompletionEvaluator.EvaluateSeason(
            [Ep(1, true), Ep(1, false)],
            1,
            Options(),
            Now);

        Assert.False(result.IsComplete);
    }

    [Fact]
    public void EvaluateSeason_SeasonZeroLiteral_IgnoresExcludeSpecials()
    {
        var result = CompletionEvaluator.EvaluateSeason(
            [Ep(0, true), Ep(0, true)],
            0,
            Options(excludeSpecials: true),
            Now);

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.EpisodeCount);
    }

    [Fact]
    public void EvaluateSeason_UnairedAndMissing_AreIgnored()
    {
        var future = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = CompletionEvaluator.EvaluateSeason(
            [Ep(1, true), Ep(1, false, aired: future), Ep(1, true, missing: true)],
            1,
            Options(),
            Now);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.EpisodeCount);
    }

    [Fact]
    public void EvaluateSeason_Empty_IsNotComplete()
    {
        var result = CompletionEvaluator.EvaluateSeason(
            [],
            1,
            Options(),
            Now);

        Assert.False(result.IsComplete);
    }

    [Fact]
    public void ShouldRemoveSeason_IncompleteToComplete_Removes()
    {
        var observed = new SeasonObservation(false, 2, 1);
        var current = new CompletionResult(true, 2, 1, "FP");

        Assert.True(RemovalPolicy.ShouldRemoveSeason(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeason_Shrink_DoesNotRemove()
    {
        var observed = new SeasonObservation(false, 3, 1);
        var current = new CompletionResult(true, 2, 1, "FP");

        Assert.False(RemovalPolicy.ShouldRemoveSeason(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeason_SameComplete_DoesNotRemove()
    {
        var observed = new SeasonObservation(true, 2, 1, "A");
        var current = new CompletionResult(true, 2, 1, "A");

        Assert.False(RemovalPolicy.ShouldRemoveSeason(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeason_NewEpisodes_Removes()
    {
        var observed = new SeasonObservation(true, 2, 1, "A");
        var current = new CompletionResult(true, 3, 1, "B");

        Assert.True(RemovalPolicy.ShouldRemoveSeason(observed, current));
    }

    [Fact]
    public void ResetPolicy_Movie_RequiresPlayedAndTombstone()
    {
        Assert.True(ResetPolicy.ShouldResetMovie(true, true, true, true, false));
        Assert.False(ResetPolicy.ShouldResetMovie(false, true, true, true, false));
        Assert.False(ResetPolicy.ShouldResetMovie(true, false, true, true, false));
        Assert.False(ResetPolicy.ShouldResetMovie(true, true, false, true, false));
        Assert.False(ResetPolicy.ShouldResetMovie(true, true, true, false, false));
        Assert.False(ResetPolicy.ShouldResetMovie(true, true, true, true, true));
    }

    [Fact]
    public void ResetPolicy_Completion_RequiresCompleteAndFlags()
    {
        var complete = new CompletionResult(true, 2, 1);
        var incomplete = new CompletionResult(false, 2, 1);

        Assert.True(ResetPolicy.ShouldResetCompletion(complete, true, true, true, false));
        Assert.False(ResetPolicy.ShouldResetCompletion(incomplete, true, true, true, false));
        Assert.False(ResetPolicy.ShouldResetCompletion(complete, false, true, true, false));
        Assert.False(ResetPolicy.ShouldResetCompletion(complete, true, false, true, false));
    }

    [Fact]
    public void State_Season_RoundTrips()
    {
        var store = CreateStore();
        var user = Guid.NewGuid();
        var season = Guid.NewGuid();

        store.EnsureOptions("A");
        store.SetSeasonObservation(user, season, new SeasonObservation(true, 2, 1, "FP"));
        store.Save();

        var second = CreateStore();
        second.EnsureOptions("A");
        var obs = second.GetSeasonObservation(user, season);

        Assert.NotNull(obs);
        Assert.True(obs.Complete);
        Assert.Equal(2, obs.EpisodeCount);
        Assert.Equal(1, obs.SeasonNumber);
    }

    [Fact]
    public void State_Tombstone_CreateConsumePrune()
    {
        var store = CreateStore();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        store.EnsureOptions("A");
        store.SetRemoved(user, item, "Movie");
        Assert.NotNull(store.GetRemoved(user, item));

        store.SetRemovedPending(user, item, true);
        Assert.True(store.GetRemoved(user, item)!.PendingReset);

        store.RemoveRemoved(user, item);
        Assert.Null(store.GetRemoved(user, item));
    }

    [Fact]
    public void State_MigratesV1ToV2_PreservingMoviesAndSeries()
    {
        var user = Guid.NewGuid();
        var movie = Guid.NewGuid();
        var series = Guid.NewGuid();
        var userKey = user.ToString("N");

        Directory.CreateDirectory(Path.Combine(_root, "watchlist"));
        File.WriteAllText(
            Path.Combine(_root, "watchlist", "state.json"),
            $$"""
            {
              "SchemaVersion": 1,
              "OptionsSignature": "A",
              "Users": {
                "{{userKey}}": {
                  "Movies": {
                    "{{movie:N}}": { "Played": true }
                  },
                  "Series": {
                    "{{series:N}}": { "Complete": true, "EpisodeCount": 2, "LatestSeasonNumber": 1, "ContentFingerprint": "FP" }
                  }
                }
              }
            }
            """);

        var store = CreateStore();
        store.EnsureOptions("A");

        Assert.True(store.GetMoviePlayed(user, movie));
        var s = store.GetSeriesObservation(user, series);
        Assert.NotNull(s);
        Assert.True(s.Complete);

        // Seasons and tombstones start empty after migration.
        var season = Guid.NewGuid();
        Assert.Null(store.GetSeasonObservation(user, season));
        Assert.Null(store.GetRemoved(user, movie));
    }

    [Fact]
    public void State_EnsureOptions_PreservesSeasonsAndTombstones()
    {
        var store = CreateStore();
        var user = Guid.NewGuid();
        var season = Guid.NewGuid();
        var item = Guid.NewGuid();

        store.EnsureOptions("A");
        store.SetSeasonObservation(user, season, new SeasonObservation(false, 2, 1));
        store.SetRemoved(user, item, "Movie");
        store.Save();

        store.EnsureOptions("B");

        Assert.NotNull(store.GetSeasonObservation(user, season));
        Assert.NotNull(store.GetRemoved(user, item));
    }

    [Fact]
    public void Options_Signature_IgnoresNewToggles()
    {
        var baseline = WatchlistOptions.From(new PluginConfiguration());
        var toggled = WatchlistOptions.From(new PluginConfiguration
        {
            AutoRemoveSeasons = false,
            EnableRewatch = false
        });

        Assert.Equal(baseline.Signature, toggled.Signature);
    }

    [Fact]
    public void Options_Defaults_EnableNewToggles()
    {
        var options = WatchlistOptions.From(new PluginConfiguration());

        Assert.True(options.AutoRemoveSeasons);
        Assert.True(options.EnableRewatch);
    }

    [Fact]
    public void State_PruneUser_WithSeasons()
    {
        var store = CreateStore();
        var user = Guid.NewGuid();
        var keep = Guid.NewGuid();
        var remove = Guid.NewGuid();

        store.EnsureOptions("A");
        store.SetSeasonObservation(user, keep, new SeasonObservation(false, 2, 1));
        store.SetSeasonObservation(user, remove, new SeasonObservation(false, 2, 1));

        store.PruneUser(user, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid> { keep });

        Assert.NotNull(store.GetSeasonObservation(user, keep));
        Assert.Null(store.GetSeasonObservation(user, remove));
    }

    [Fact]
    public void State_PruneRemovedForMissingItems()
    {
        var store = CreateStore();
        var user = Guid.NewGuid();
        var keep = Guid.NewGuid();
        var gone = Guid.NewGuid();

        store.EnsureOptions("A");
        store.SetRemoved(user, keep, "Movie");
        store.SetRemoved(user, gone, "Movie");

        store.PruneRemovedForMissingItems(user, id => id == keep);

        Assert.NotNull(store.GetRemoved(user, keep));
        Assert.Null(store.GetRemoved(user, gone));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WatchlistStateStore CreateStore()
    {
        return new WatchlistStateStore(_root, NullLogger<WatchlistStateStore>.Instance);
    }
}
