using Jellyfin.Plugin.WatchNotify.Dispatch;
using Jellyfin.Plugin.WatchNotify.Logging;
using Jellyfin.Plugin.WatchNotify.Watch;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WatchNotify;

/// <summary>
/// Registers the plugin's services with the server's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<EventLogStore>();
        serviceCollection.AddSingleton<SessionTracker>();
        serviceCollection.AddSingleton<ScnClient>();
        serviceCollection.AddSingleton<JoplinClient>();
        serviceCollection.AddSingleton<DispatchQueue>();
        serviceCollection.AddSingleton<TestRunner>();
        serviceCollection.AddHostedService<WatchNotifyEntryPoint>();
    }
}
