namespace Jellyfin.Plugin.Watchlist.Logic;

/// <summary>
/// Matches an item's tags against the configured exempt tag.
/// </summary>
public static class ExemptTagMatcher
{
    /// <summary>
    /// Determines whether an item carries the configured exempt tag.
    /// </summary>
    /// <param name="tags">The item's tags.</param>
    /// <param name="exemptTag">The configured exempt tag; empty disables the feature.</param>
    /// <returns><c>true</c> when the item is exempt.</returns>
    public static bool Matches(IEnumerable<string>? tags, string? exemptTag)
    {
        if (tags is null || string.IsNullOrWhiteSpace(exemptTag))
        {
            return false;
        }

        return tags.Any(tag =>
            string.Equals(tag, exemptTag, StringComparison.OrdinalIgnoreCase));
    }
}
