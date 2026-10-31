using Jellyfin.Plugin.Watchlist.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlist;

/// <summary>
/// Registers the plugin services.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(serviceProvider =>
            new WatchlistStateStore(
                serviceProvider.GetRequiredService<IApplicationPaths>().DataPath,
                serviceProvider.GetRequiredService<ILogger<WatchlistStateStore>>()));

        serviceCollection.AddSingleton<WatchlistService>();
        serviceCollection.AddHostedService<WatchlistHostedService>();
    }
}
