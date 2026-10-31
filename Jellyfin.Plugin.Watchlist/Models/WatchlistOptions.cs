using Jellyfin.Plugin.Watchlist.Configuration;

namespace Jellyfin.Plugin.Watchlist.Models;

/// <summary>
/// A normalized snapshot of the plugin configuration used while processing an event.
/// </summary>
/// <param name="Mode">The series completion mode.</param>
/// <param name="ExcludeSpecials">Whether season zero episodes are ignored.</param>
/// <param name="TreatUnknownSeriesStatusAsEnded">Whether a missing series status counts as ended.</param>
/// <param name="AutoRemoveMovies">Whether played favorites are removed.</param>
/// <param name="AutoRemoveSeries">Whether completed series favorites are removed.</param>
/// <param name="ExemptTag">An optional tag that protects an item from removal.</param>
/// <param name="DryRun">Whether removal is only logged.</param>
/// <param name="EnableReconciliation">Whether startup and scheduled scans run.</param>
public sealed record WatchlistOptions(
    CompletionMode Mode,
    bool ExcludeSpecials,
    bool TreatUnknownSeriesStatusAsEnded,
    bool AutoRemoveMovies,
    bool AutoRemoveSeries,
    string ExemptTag,
    bool DryRun,
    bool EnableReconciliation)
{
    /// <summary>
    /// Creates normalized options from the plugin configuration.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The normalized options.</returns>
    public static WatchlistOptions From(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var mode = Enum.TryParse<CompletionMode>(config.CompletionMode, ignoreCase: true, out var parsed)
            && Enum.IsDefined(typeof(CompletionMode), parsed)
            ? parsed
            : CompletionMode.EndedAndFullyWatched;

        return new WatchlistOptions(
            mode,
            config.ExcludeSpecials,
            config.TreatUnknownSeriesStatusAsEnded,
            config.AutoRemoveMovies,
            config.AutoRemoveSeries,
            (config.ExemptTag ?? string.Empty).Trim(),
            config.DryRun,
            config.EnableReconciliation);
    }

    /// <summary>
    /// Gets a signature of the options that affect completion decisions.
    /// </summary>
    public string Signature => $"{Mode}|{ExcludeSpecials}|{TreatUnknownSeriesStatusAsEnded}";
}
