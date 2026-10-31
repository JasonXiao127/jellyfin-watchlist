using Jellyfin.Plugin.Watchlist.Logic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class SeriesStatusEvaluatorTests
{
    [Fact]
    public void EndedStatus_IsEnded()
    {
        Assert.True(SeriesStatusEvaluator.IsEnded(SeriesStatus.Ended, treatUnknownAsEnded: false));
    }

    [Theory]
    [InlineData(SeriesStatus.Continuing)]
    [InlineData(SeriesStatus.Unreleased)]
    public void OtherStatuses_AreNotEnded(SeriesStatus status)
    {
        Assert.False(SeriesStatusEvaluator.IsEnded(status, treatUnknownAsEnded: true));
    }

    [Fact]
    public void UnknownStatus_IsNotEndedByDefault()
    {
        Assert.False(SeriesStatusEvaluator.IsEnded(null, treatUnknownAsEnded: false));
    }

    [Fact]
    public void UnknownStatus_CanBeTreatedAsEnded()
    {
        Assert.True(SeriesStatusEvaluator.IsEnded(null, treatUnknownAsEnded: true));
    }
}
