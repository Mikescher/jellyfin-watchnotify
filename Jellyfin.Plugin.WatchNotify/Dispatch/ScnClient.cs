using System.Globalization;
using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.WatchNotify.Configuration;
using Jellyfin.Plugin.WatchNotify.Watch;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchNotify.Dispatch;

/// <summary>
/// Sends push notifications via the Simple Cloud Notifier API.
/// </summary>
public sealed class ScnClient
{
    private const string SenderName = "jellyfin-watchnotify";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ScnClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScnClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The server's http client factory.</param>
    /// <param name="logger">The logger.</param>
    public ScnClient(IHttpClientFactory httpClientFactory, ILogger<ScnClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Reports whether the configuration is complete enough to send.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>Whether pushes can be sent.</returns>
    public static bool IsConfigured(PluginConfiguration config)
        => config.ScnEnabled
           && !string.IsNullOrWhiteSpace(config.ScnBaseUrl)
           && !string.IsNullOrWhiteSpace(config.ScnUserId)
           && !string.IsNullOrWhiteSpace(config.ScnKey);

    /// <summary>
    /// Renders the notification body.
    /// </summary>
    /// <param name="watchEvent">The finished watch.</param>
    /// <returns>The body text.</returns>
    public static string BuildContent(WatchEvent watchEvent)
    {
        var device = watchEvent.Device;
        if (!string.IsNullOrEmpty(watchEvent.Client))
        {
            device = string.IsNullOrEmpty(device) ? watchEvent.Client : device + " · " + watchEvent.Client;
        }

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"What:    {watchEvent.Title}\n");
        if (!string.IsNullOrEmpty(watchEvent.EpisodeName))
        {
            // Second row, indented to align with the "What:" value above.
            body.Append(CultureInfo.InvariantCulture, $"         {watchEvent.EpisodeName}\n");
        }

        body.Append(CultureInfo.InvariantCulture, $"Who:     {watchEvent.User}\n");
        body.Append(CultureInfo.InvariantCulture, $"Started: {Format.Time(watchEvent.Start)}\n");
        body.Append(CultureInfo.InvariantCulture, $"Ended:   {Format.Time(watchEvent.End)}\n");
        body.Append(CultureInfo.InvariantCulture, $"Watched: {Format.Percent(watchEvent.Fraction)} ({Format.Duration(watchEvent.Position)} / {Format.Duration(watchEvent.Runtime)})\n");
        if (!string.IsNullOrEmpty(device))
        {
            body.Append(CultureInfo.InvariantCulture, $"Device:  {device}\n");
        }

        return body.ToString();
    }

    /// <summary>
    /// Posts a watched notification, throwing when the endpoint rejects it.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="watchEvent">The finished watch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the notification is accepted.</returns>
    public async Task SendAsync(PluginConfiguration config, WatchEvent watchEvent, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user_id"] = config.ScnUserId,
            ["key"] = config.ScnKey,
            ["title"] = watchEvent.ScnTitle,
            ["content"] = BuildContent(watchEvent),
            ["priority"] = config.ScnPriority.ToString(CultureInfo.InvariantCulture),
            ["sender_name"] = SenderName,
            ["msg_id"] = Guid.NewGuid().ToString("D"),
        };

        if (!string.IsNullOrEmpty(config.ScnChannel))
        {
            fields["channel"] = config.ScnChannel;
        }

        using var content = new FormUrlEncodedContent(fields);
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;

        using var response = await client.PostAsync(new Uri(config.ScnBaseUrl), content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                string.Format(CultureInfo.InvariantCulture, "scn returned {0}: {1}", (int)response.StatusCode, body.Trim()));
        }

        _logger.LogInformation("SCN notification sent for {User} / {Item}", watchEvent.User, watchEvent.Title);
    }
}
