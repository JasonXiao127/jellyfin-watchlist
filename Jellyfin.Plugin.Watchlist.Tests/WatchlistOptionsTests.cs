using Jellyfin.Plugin.Watchlist.Configuration;
using Jellyfin.Plugin.Watchlist.Models;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class WatchlistOptionsTests
{
    [Fact]
    public void From_ParsesCompletionMode()
    {
        var config = new PluginConfiguration { CompletionMode = "LatestSeasonWatched" };

        var options = WatchlistOptions.From(config);

        Assert.Equal(CompletionMode.LatestSeasonWatched, options.Mode);
    }

    [Fact]
    public void From_InvalidCompletionMode_FallsBackToDefault()
    {
        var config = new PluginConfiguration { CompletionMode = "SomethingElse" };

        var options = WatchlistOptions.From(config);

        Assert.Equal(CompletionMode.EndedAndFullyWatched, options.Mode);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("-1")]
    public void From_NumericCompletionMode_FallsBackToDefault(string mode)
    {
        var config = new PluginConfiguration { CompletionMode = mode };

        var options = WatchlistOptions.From(config);

        Assert.Equal(CompletionMode.EndedAndFullyWatched, options.Mode);
    }

    [Fact]
    public void From_NormalizesNullExemptTag()
    {
        var config = new PluginConfiguration { ExemptTag = null! };

        var options = WatchlistOptions.From(config);

        Assert.Equal(string.Empty, options.ExemptTag);
    }

    [Fact]
    public void Signature_ChangesWithCompletionAffectingOptions()
    {
        var first = WatchlistOptions.From(new PluginConfiguration());
        var second = WatchlistOptions.From(new PluginConfiguration
        {
            CompletionMode = "CaughtUp"
        });

        Assert.NotEqual(first.Signature, second.Signature);
    }

    [Fact]
    public void Signature_ChangesWhenSpecialHandlingChanges()
    {
        var first = WatchlistOptions.From(new PluginConfiguration());
        var second = WatchlistOptions.From(new PluginConfiguration
        {
            ExcludeSpecials = false
        });

        Assert.NotEqual(first.Signature, second.Signature);
    }

    [Fact]
    public void Signature_ChangesWhenUnknownStatusHandlingChanges()
    {
        var first = WatchlistOptions.From(new PluginConfiguration());
        var second = WatchlistOptions.From(new PluginConfiguration
        {
            TreatUnknownSeriesStatusAsEnded = true
        });

        Assert.NotEqual(first.Signature, second.Signature);
    }

    [Fact]
    public void From_TrimsExemptTag()
    {
        var config = new PluginConfiguration { ExemptTag = "  keep-favorite  " };

        var options = WatchlistOptions.From(config);

        Assert.Equal("keep-favorite", options.ExemptTag);
    }
}
