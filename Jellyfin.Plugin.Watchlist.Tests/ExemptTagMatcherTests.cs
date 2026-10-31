using Jellyfin.Plugin.Watchlist.Logic;

namespace Jellyfin.Plugin.Watchlist.Tests;

public sealed class ExemptTagMatcherTests
{
    [Fact]
    public void MatchingTag_IsExempt()
    {
        Assert.True(ExemptTagMatcher.Matches(["drama", "keep-favorite"], "keep-favorite"));
    }

    [Fact]
    public void MatchingTag_IsCaseInsensitive()
    {
        Assert.True(ExemptTagMatcher.Matches(["KEEP-FAVORITE"], "keep-favorite"));
    }

    [Fact]
    public void MissingTag_IsNotExempt()
    {
        Assert.False(ExemptTagMatcher.Matches(["drama"], "keep-favorite"));
    }

    [Fact]
    public void EmptyConfiguredTag_DisablesExemption()
    {
        Assert.False(ExemptTagMatcher.Matches(["keep-favorite"], string.Empty));
    }

    [Fact]
    public void NullTags_AreNotExempt()
    {
        Assert.False(ExemptTagMatcher.Matches(null, "keep-favorite"));
    }
}
