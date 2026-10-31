using Jellyfin.Plugin.Watchlist.Logic;
using Jellyfin.Plugin.Watchlist.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class WatchlistStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "jellyfin-watchlist-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void State_RoundTripsAcrossInstances()
    {
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var first = CreateStore();
        first.EnsureOptions("A");
        first.SetMoviePlayed(userId, movieId, true);
        first.SetSeriesObservation(userId, seriesId, new SeriesObservation(true, 12, 3, "FP"));
        first.Save();

        var second = CreateStore();
        second.EnsureOptions("A");

        Assert.True(second.GetMoviePlayed(userId, movieId));
        var series = second.GetSeriesObservation(userId, seriesId);
        Assert.NotNull(series);
        Assert.True(series.Complete);
        Assert.Equal(12, series.EpisodeCount);
        Assert.Equal(3, series.LatestSeasonNumber);
        Assert.Equal("FP", series.ContentFingerprint);
    }

    [Fact]
    public void EnsureOptions_ClearsStateWhenSignatureChanges()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var store = CreateStore();
        store.EnsureOptions("A");
        store.SetSeriesObservation(userId, seriesId, new SeriesObservation(false, 10, 2));
        store.Save();

        store.EnsureOptions("B");

        Assert.Null(store.GetSeriesObservation(userId, seriesId));
    }

    [Fact]
    public void EnsureOptions_KeepsStateForSameSignature()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var store = CreateStore();
        store.EnsureOptions("A");
        store.SetSeriesObservation(userId, seriesId, new SeriesObservation(false, 10, 2));
        store.EnsureOptions("A");

        Assert.NotNull(store.GetSeriesObservation(userId, seriesId));
    }

    [Fact]
    public void PruneUser_RemovesItemsThatAreNoLongerFavorites()
    {
        var userId = Guid.NewGuid();
        var keepMovie = Guid.NewGuid();
        var removeMovie = Guid.NewGuid();
        var keepSeries = Guid.NewGuid();
        var removeSeries = Guid.NewGuid();

        var store = CreateStore();
        store.EnsureOptions("A");
        store.SetMoviePlayed(userId, keepMovie, false);
        store.SetMoviePlayed(userId, removeMovie, false);
        store.SetSeriesObservation(userId, keepSeries, new SeriesObservation(false, 5, 1));
        store.SetSeriesObservation(userId, removeSeries, new SeriesObservation(false, 5, 1));

        store.PruneUser(
            userId,
            new HashSet<Guid> { keepMovie },
            new HashSet<Guid> { keepSeries });

        Assert.NotNull(store.GetMoviePlayed(userId, keepMovie));
        Assert.Null(store.GetMoviePlayed(userId, removeMovie));
        Assert.NotNull(store.GetSeriesObservation(userId, keepSeries));
        Assert.Null(store.GetSeriesObservation(userId, removeSeries));
    }

    [Fact]
    public void PruneUsers_RemovesObservationsForDeletedUsers()
    {
        var keepUser = Guid.NewGuid();
        var deletedUser = Guid.NewGuid();
        var keepMovie = Guid.NewGuid();
        var deletedMovie = Guid.NewGuid();

        var store = CreateStore();
        store.EnsureOptions("A");
        store.SetMoviePlayed(keepUser, keepMovie, true);
        store.SetMoviePlayed(deletedUser, deletedMovie, true);

        store.PruneUsers(new HashSet<Guid> { keepUser });

        Assert.True(store.GetMoviePlayed(keepUser, keepMovie));
        Assert.Null(store.GetMoviePlayed(deletedUser, deletedMovie));
    }

    [Fact]
    public void PruneUsers_EmptyList_DoesNotWipeState()
    {
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();

        var store = CreateStore();
        store.EnsureOptions("A");
        store.SetMoviePlayed(userId, movieId, true);

        store.PruneUsers(new HashSet<Guid>());

        Assert.True(store.GetMoviePlayed(userId, movieId));
    }

    [Fact]
    public void StateFileWithNullValues_IsNormalized()
    {
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var keepMovie = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var userKey = userId.ToString("N");

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
                    "{{movieId:N}}": null,
                    "{{keepMovie:N}}": { "Played": true }
                  },
                  "Series": {
                    "{{seriesId:N}}": null
                  }
                }
              }
            }
            """);

        var store = CreateStore();
        store.EnsureOptions("A");

        Assert.Null(store.GetMoviePlayed(userId, movieId));
        Assert.True(store.GetMoviePlayed(userId, keepMovie));
        Assert.Null(store.GetSeriesObservation(userId, seriesId));

        // A save after normalization must not throw and must keep the valid entry.
        store.Save();
        Assert.True(store.GetMoviePlayed(userId, keepMovie));
    }

    [Fact]
    public void CorruptStateFile_IsPreservedNotOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(_root, "watchlist"));
        var path = Path.Combine(_root, "watchlist", "state.json");
        File.WriteAllText(path, "{not valid json");

        var store = CreateStore();
        store.EnsureOptions("A");

        Assert.True(File.Exists(path) || Directory.GetFiles(Path.Combine(_root, "watchlist"), "state.json.corrupt-*").Length == 1);
    }

    [Fact]
    public void RemoveItem_RemovesMovieAndSeriesObservations()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var store = CreateStore();
        store.EnsureOptions("A");
        store.SetMoviePlayed(userId, itemId, true);
        store.SetSeriesObservation(userId, itemId, new SeriesObservation(true, 1, 1));

        store.RemoveItem(userId, itemId);

        Assert.Null(store.GetMoviePlayed(userId, itemId));
        Assert.Null(store.GetSeriesObservation(userId, itemId));
    }

    [Fact]
    public void StateFileWithNullCollections_IsNormalized()
    {
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var userKey = userId.ToString("N");

        Directory.CreateDirectory(Path.Combine(_root, "watchlist"));
        File.WriteAllText(
            Path.Combine(_root, "watchlist", "state.json"),
            $$"""
            {
              "SchemaVersion": 1,
              "OptionsSignature": "A",
              "Users": {
                "{{userKey}}": {
                  "Movies": null,
                  "Series": null
                }
              }
            }
            """);

        var store = CreateStore();
        store.EnsureOptions("A");
        store.SetMoviePlayed(userId, movieId, true);
        store.Save();

        Assert.True(store.GetMoviePlayed(userId, movieId));
    }

    [Fact]
    public void CorruptStateFile_StartsFreshAndCanBeSaved()
    {
        Directory.CreateDirectory(Path.Combine(_root, "watchlist"));
        File.WriteAllText(Path.Combine(_root, "watchlist", "state.json"), "{not valid json");

        var store = CreateStore();
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();

        store.EnsureOptions("A");
        Assert.Null(store.GetMoviePlayed(userId, movieId));

        store.SetMoviePlayed(userId, movieId, true);
        store.Save();

        var reloaded = CreateStore();
        reloaded.EnsureOptions("A");
        Assert.True(reloaded.GetMoviePlayed(userId, movieId));
    }

    [Fact]
    public void EnsureOptions_PreservesMoviesWhenSeriesRulesChange()
    {
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var store = CreateStore();
        store.EnsureOptions("EndedAndFullyWatched|True|False");
        store.SetMoviePlayed(userId, movieId, false);
        store.SetSeriesObservation(userId, seriesId, new SeriesObservation(false, 10, 2));
        store.Save();

        store.EnsureOptions("CaughtUp|True|False");

        Assert.False(store.GetMoviePlayed(userId, movieId));
        Assert.Null(store.GetSeriesObservation(userId, seriesId));
    }

    [Fact]
    public void PruneUser_EmptyFavorites_DoesNotWipeState()
    {
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var store = CreateStore();
        store.EnsureOptions("A");
        store.SetMoviePlayed(userId, movieId, false);
        store.SetSeriesObservation(userId, seriesId, new SeriesObservation(false, 5, 1));

        store.PruneUser(userId, new HashSet<Guid>(), new HashSet<Guid>());

        Assert.NotNull(store.GetMoviePlayed(userId, movieId));
        Assert.NotNull(store.GetSeriesObservation(userId, seriesId));
    }

    [Fact]
    public void CorruptStateFile_BackupContainsOriginalContent()
    {
        const string corrupt = "{not valid json";

        Directory.CreateDirectory(Path.Combine(_root, "watchlist"));
        var dir = Path.Combine(_root, "watchlist");
        File.WriteAllText(Path.Combine(dir, "state.json"), corrupt);

        var store = CreateStore();
        store.EnsureOptions("A");

        var backups = Directory.GetFiles(dir, "state.json.corrupt-*");
        Assert.Single(backups);
        Assert.Equal(corrupt, File.ReadAllText(backups[0]));
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
