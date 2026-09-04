using Jellyfin.Plugin.WatchNotify.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.WatchNotify;

/// <summary>
/// Notifies external services when a movie or episode is watched to completion.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths, used to locate the config file.</param>
    /// <param name="xmlSerializer">Serializer used to persist the configuration.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the single loaded instance, for components the server does not inject into.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "WatchNotify";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("295982d0-fc94-4c98-9bf4-54bc7cf05b30");

    /// <inheritdoc />
    public override string Description =>
        "Sends a push notification and appends a watch-log line when a movie or episode is watched.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;

        // Only the settings page is listed in the dashboard's plugin drawer; the log
        // page is reached from the tab strip the pages render themselves.
        yield return new PluginPageInfo
        {
            Name = "WatchNotify",
            DisplayName = "WatchNotify",
            EmbeddedResourcePath = prefix + ".Configuration.configPage.html",
            EnableInMainMenu = true,
        };

        yield return new PluginPageInfo
        {
            Name = "WatchNotify.js",
            EmbeddedResourcePath = prefix + ".Configuration.configPage.js",
        };

        yield return new PluginPageInfo
        {
            Name = "WatchNotifyLog",
            DisplayName = "WatchNotify Log",
            EmbeddedResourcePath = prefix + ".Configuration.logPage.html",
        };

        yield return new PluginPageInfo
        {
            Name = "WatchNotifyLog.js",
            EmbeddedResourcePath = prefix + ".Configuration.logPage.js",
        };
    }
}
