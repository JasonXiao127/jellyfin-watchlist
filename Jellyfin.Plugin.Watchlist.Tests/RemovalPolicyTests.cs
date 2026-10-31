using Jellyfin.Plugin.Watchlist.Logic;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class RemovalPolicyTests
{
    [Theory]
    [InlineData(null, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public void ShouldRemoveMovie_OnlyRemovesOnPlayedTransition(
        bool? observedPlayed,
        bool currentPlayed,
        bool expected)
    {
        Assert.Equal(expected, RemovalPolicy.ShouldRemoveMovie(observedPlayed, currentPlayed));
    }

    [Fact]
    public void ShouldRemoveSeries_UnknownObservation_DoesNotRemove()
    {
        var current = new CompletionResult(true, 10, 2);

        Assert.False(RemovalPolicy.ShouldRemoveSeries(null, current));
    }

    [Fact]
    public void ShouldRemoveSeries_IncompleteToComplete_Removes()
    {
        var observed = new SeriesObservation(false, 10, 2);
        var current = new CompletionResult(true, 10, 2);

        Assert.True(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_StillIncomplete_DoesNotRemove()
    {
        var observed = new SeriesObservation(false, 10, 2);
        var current = new CompletionResult(false, 10, 2);

        Assert.False(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_AlreadyCompleteWithSameContent_DoesNotRemove()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(true, 10, 2);

        Assert.False(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_NewLatestSeason_Removes()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(true, 9, 3);

        Assert.True(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_NewEpisodesInSameSeason_Removes()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(true, 12, 2);

        Assert.True(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_FewerEpisodes_DoesNotRemove()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(true, 8, 2);

        Assert.False(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_IncompleteToCompleteWithFewerEpisodes_DoesNotRemove()
    {
        // Content loss is not viewing: an incomplete baseline with more
        // content must not be removed by a smaller complete result.
        var observed = new SeriesObservation(false, 10, 2);
        var current = new CompletionResult(true, 5, 2);

        Assert.False(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_IncompleteToCompleteWithLowerSeason_DoesNotRemove()
    {
        var observed = new SeriesObservation(false, 10, 3);
        var current = new CompletionResult(true, 10, 2);

        Assert.False(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_SameContentDifferentFingerprint_Removes()
    {
        var observed = new SeriesObservation(true, 10, 2, "AAA");
        var current = new CompletionResult(true, 10, 2, "BBB");

        Assert.True(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_SameContentEmptyFingerprint_DoesNotRemove()
    {
        // Old state has no fingerprint; never treat unknown as new content.
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(true, 10, 2, "BBB");

        Assert.False(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldRemoveSeries_BecameIncomplete_DoesNotRemove()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(false, 10, 2);

        Assert.False(RemovalPolicy.ShouldRemoveSeries(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_UnknownObservation_Replaces()
    {
        var current = new CompletionResult(false, 10, 2);

        Assert.True(RemovalPolicy.ShouldReplaceSeriesObservation(null, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_IncompleteObservation_Replaces()
    {
        var observed = new SeriesObservation(false, 10, 2);
        var current = new CompletionResult(false, 10, 2);

        Assert.True(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_IncompleteToComplete_Replaces()
    {
        var observed = new SeriesObservation(false, 10, 2);
        var current = new CompletionResult(true, 10, 2);

        Assert.True(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_CompleteToIncompleteSameContent_KeepsBaseline()
    {
        // An unplay/rewatch of identical content must not overwrite the complete
        // baseline, otherwise replaying would remove a series that was already
        // complete when favorited.
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(false, 10, 2);

        Assert.False(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_CompleteToIncompleteNewEpisodes_Replaces()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(false, 12, 2);

        Assert.True(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_CompleteToIncompleteNewSeason_Replaces()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(false, 10, 3);

        Assert.True(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_CompleteToIncompleteFewerEpisodes_KeepsBaseline()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(false, 8, 2);

        Assert.False(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_CompleteToIncompleteLowerSeason_KeepsBaseline()
    {
        var observed = new SeriesObservation(true, 10, 3);
        var current = new CompletionResult(false, 10, 2);

        Assert.False(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_CompleteToCompleteWithFewerEpisodes_KeepsBaseline()
    {
        // A transient library change (offline share, missing episodes) must
        // not downgrade the complete baseline that protects a kept series.
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(true, 5, 2);

        Assert.False(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_CompleteToCompleteWithLowerSeason_KeepsBaseline()
    {
        var observed = new SeriesObservation(true, 10, 3);
        var current = new CompletionResult(true, 10, 2);

        Assert.False(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_SameContentDifferentFingerprint_Replaces()
    {
        var observed = new SeriesObservation(true, 10, 2, "AAA");
        var current = new CompletionResult(false, 10, 2, "BBB");

        Assert.True(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }

    [Fact]
    public void ShouldReplaceSeriesObservation_CompleteToComplete_Replaces()
    {
        var observed = new SeriesObservation(true, 10, 2);
        var current = new CompletionResult(true, 10, 2);

        Assert.True(RemovalPolicy.ShouldReplaceSeriesObservation(observed, current));
    }
}
