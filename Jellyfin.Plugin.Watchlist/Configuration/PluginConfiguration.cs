using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Watchlist.Configuration;

/// <summary>
/// The plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the series completion mode. Stored as a string so the value
    /// survives both the XML configuration serializer and the JSON web API.
    /// </summary>
    public string CompletionMode { get; set; } = "EndedAndFullyWatched";

    /// <summary>
    /// Gets or sets a value indicating whether a favorite movie is removed when it is played.
    /// </summary>
    public bool AutoRemoveMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a favorite series is removed when it completes.
    /// </summary>
    public bool AutoRemoveSeries { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether season zero episodes are ignored.
    /// </summary>
    public bool ExcludeSpecials { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a series with no status is treated as ended.
    /// </summary>
    public bool TreatUnknownSeriesStatusAsEnded { get; set; }

    /// <summary>
    /// Gets or sets a tag that prevents an item from being auto-removed. Leave empty to disable.
    /// </summary>
    public string ExemptTag { get; set; } = "keep-favorite";

    /// <summary>
    /// Gets or sets a value indicating whether startup and scheduled reconciliation scans run.
    /// </summary>
    public bool EnableReconciliation { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether removal is logged without changing favorites.
    /// </summary>
    public bool DryRun { get; set; }
}
