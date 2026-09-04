using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.WatchNotify.Configuration;
using Jellyfin.Plugin.WatchNotify.Watch;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchNotify.Dispatch;

/// <summary>
/// Appends watch-log lines to a note via the custom Joplin API server.
/// </summary>
public sealed class JoplinClient
{
    /// <summary>
    /// The column the watch-log title is padded to so the "// From Jellyfin [...]"
    /// suffix roughly lines up across lines.
    /// </summary>
    private const int TitleWidth = 45;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JoplinClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JoplinClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The server's http client factory.</param>
    /// <param name="logger">The logger.</param>
    public JoplinClient(IHttpClientFactory httpClientFactory, ILogger<JoplinClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Reports whether the configuration is complete enough to append. An empty
    /// anchor would leave the insert position undefined, so it counts as unconfigured.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>Whether watch-log lines can be appended.</returns>
    public static bool IsConfigured(PluginConfiguration config)
        => config.JoplinEnabled
           && !string.IsNullOrWhiteSpace(config.JoplinBaseUrl)
           && !string.IsNullOrWhiteSpace(config.JoplinToken)
           && !string.IsNullOrWhiteSpace(config.JoplinNoteId)
           && !string.IsNullOrWhiteSpace(config.JoplinAnchor);

    /// <summary>
    /// Renders the watch-log line.
    /// </summary>
    /// <param name="watchEvent">The finished watch.</param>
    /// <returns>The line to insert.</returns>
    public static string BuildLine(WatchEvent watchEvent)
        => string.Format(
            CultureInfo.InvariantCulture,
            "[{0}]: {1} // From Jellyfin [{2}]",
            Format.Time(watchEvent.Start),
            Format.PadRight(watchEvent.JoplinTitle, TitleWidth),
            watchEvent.User);

    /// <summary>
    /// Inserts a watch-log line into the configured note, throwing when the
    /// endpoint rejects it.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="watchEvent">The finished watch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the line is inserted.</returns>
    public async Task AppendAsync(PluginConfiguration config, WatchEvent watchEvent, CancellationToken cancellationToken)
    {
        var line = BuildLine(watchEvent);

        var request = new InsertRequest
        {
            Content = line,
            Search = config.JoplinAnchor,
            Position = config.JoplinPosition,
            SearchEmptylineGap = config.JoplinEmptylineGap,
        };

        var endpoint = new Uri(
            config.JoplinBaseUrl.TrimEnd('/') + "/notes/" + Uri.EscapeDataString(config.JoplinNoteId) + "/insert");

        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.JoplinToken);

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                string.Format(CultureInfo.InvariantCulture, "joplin returned {0}: {1}", (int)response.StatusCode, body.Trim()));
        }

        _logger.LogInformation("Joplin watch-log line appended: {Line}", line);
    }

    private sealed class InsertRequest
    {
        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;

        [JsonPropertyName("search")]
        public string Search { get; init; } = string.Empty;

        [JsonPropertyName("position")]
        public string Position { get; init; } = string.Empty;

        [JsonPropertyName("search_emptyline_gap")]
        public int SearchEmptylineGap { get; init; }
    }
}
