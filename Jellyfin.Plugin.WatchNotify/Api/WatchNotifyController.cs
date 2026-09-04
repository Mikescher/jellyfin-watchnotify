using System.Net.Mime;
using Jellyfin.Plugin.WatchNotify.Configuration;
using Jellyfin.Plugin.WatchNotify.Dispatch;
using Jellyfin.Plugin.WatchNotify.Logging;
using Jellyfin.Plugin.WatchNotify.Watch;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WatchNotify.Api;

/// <summary>
/// The endpoints the plugin's dashboard pages call.
/// </summary>
/// <remarks>
/// Do not inject <c>ILogger&lt;T&gt;</c> here: plugin controllers are activated by a
/// logger factory that cannot resolve it (jellyfin#11488). Logging stays in the
/// injected services.
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("WatchNotify")]
[Produces(MediaTypeNames.Application.Json)]
public class WatchNotifyController : ControllerBase
{
    private readonly ScnClient _scn;
    private readonly JoplinClient _joplin;
    private readonly DispatchQueue _dispatch;
    private readonly EventLogStore _eventLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchNotifyController"/> class.
    /// </summary>
    /// <param name="scn">The push notification client.</param>
    /// <param name="joplin">The watch-log client.</param>
    /// <param name="dispatch">The outbound dispatch queue.</param>
    /// <param name="eventLog">The plugin's event log.</param>
    public WatchNotifyController(ScnClient scn, JoplinClient joplin, DispatchQueue dispatch, EventLogStore eventLog)
    {
        _scn = scn;
        _joplin = joplin;
        _dispatch = dispatch;
        _eventLog = eventLog;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Returns what the plugin is currently set up to do.
    /// </summary>
    /// <returns>The plugin status.</returns>
    [HttpGet("Status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var config = Config;
        var warnings = new List<string>();

        if (config.ScnEnabled && !ScnClient.IsConfigured(config))
        {
            warnings.Add("SCN is enabled but the base url, user id or key is missing.");
        }

        if (config.JoplinEnabled && !JoplinClient.IsConfigured(config))
        {
            warnings.Add("Joplin is enabled but the base url, token, note id or anchor is missing.");
        }

        if (config.ScnEnabled && !config.ScnAllUsers && config.ScnUserIds.Length == 0)
        {
            warnings.Add("SCN is enabled but no users are selected.");
        }

        if (config.JoplinEnabled && !config.JoplinAllUsers && config.JoplinUserIds.Length == 0)
        {
            warnings.Add("Joplin is enabled but no users are selected.");
        }

        return new StatusResponse
        {
            ScnConfigured = ScnClient.IsConfigured(config),
            JoplinConfigured = JoplinClient.IsConfigured(config),
            QueueDepth = _dispatch.Depth,
            LastError = _dispatch.LastError,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Returns the plugin's own event log, newest first.
    /// </summary>
    /// <param name="limit">The maximum number of entries to return.</param>
    /// <param name="kind">An optional kind to filter by.</param>
    /// <returns>The matching entries.</returns>
    [HttpGet("Events")]
    public ActionResult<IReadOnlyList<LogEntry>> GetEvents([FromQuery] int? limit, [FromQuery] string? kind)
        => Ok(_eventLog.GetEntries(limit, kind));

    /// <summary>
    /// Clears the plugin's own event log.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Events")]
    public ActionResult ClearEvents()
    {
        _eventLog.Clear();
        return NoContent();
    }

    /// <summary>
    /// Sends a synthetic watch notification through one integration and reports the result.
    /// </summary>
    /// <param name="target">Either <c>scn</c> or <c>joplin</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the notification was accepted.</returns>
    [HttpPost("Test")]
    public async Task<ActionResult<TestResponse>> SendTest([FromQuery] string target, CancellationToken cancellationToken)
    {
        var config = Config;
        var watchEvent = BuildSampleEvent(config);

        try
        {
            if (string.Equals(target, "scn", StringComparison.OrdinalIgnoreCase))
            {
                if (!ScnClient.IsConfigured(config))
                {
                    return new TestResponse { Success = false, Message = "SCN is not enabled or not fully configured." };
                }

                await _scn.SendAsync(config, watchEvent, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(target, "joplin", StringComparison.OrdinalIgnoreCase))
            {
                if (!JoplinClient.IsConfigured(config))
                {
                    return new TestResponse { Success = false, Message = "Joplin is not enabled or not fully configured." };
                }

                await _joplin.AppendAsync(config, watchEvent, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return BadRequest("target must be scn or joplin");
            }
        }
        catch (Exception ex)
        {
            _eventLog.Add(
                string.Equals(target, "scn", StringComparison.OrdinalIgnoreCase) ? EventKinds.ScnFailed : EventKinds.JoplinFailed,
                watchEvent.User,
                watchEvent.Title,
                "Test failed: " + ex.Message,
                success: false);

            return new TestResponse { Success = false, Message = ex.Message };
        }

        _eventLog.Add(
            string.Equals(target, "scn", StringComparison.OrdinalIgnoreCase) ? EventKinds.ScnSent : EventKinds.JoplinAppended,
            watchEvent.User,
            watchEvent.Title,
            "Test delivered");

        return new TestResponse { Success = true, Message = "Delivered." };
    }

    private static WatchEvent BuildSampleEvent(PluginConfiguration config)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(config.DisplayTimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
        }

        var runtime = TimeSpan.FromMinutes(90);
        var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);

        return new WatchEvent
        {
            User = "WatchNotify",
            Title = "WatchNotify test (1999)",
            ScnTitle = "[👁️] WatchNotify test",
            EpisodeName = string.Empty,
            JoplinTitle = "WatchNotify test",
            ItemType = "Movie",
            Start = end - runtime,
            End = end,
            Fraction = 1,
            Position = runtime,
            Runtime = runtime,
            Device = "Dashboard",
            Client = "WatchNotify",
        };
    }
}
