using System.Net.Mime;
using Jellyfin.Plugin.WatchNotify.Configuration;
using Jellyfin.Plugin.WatchNotify.Dispatch;
using Jellyfin.Plugin.WatchNotify.Logging;
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
    private readonly DispatchQueue _dispatch;
    private readonly TestRunner _testRunner;
    private readonly EventLogStore _eventLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchNotifyController"/> class.
    /// </summary>
    /// <param name="dispatch">The outbound dispatch queue.</param>
    /// <param name="testRunner">Runs dashboard test sends.</param>
    /// <param name="eventLog">The plugin's event log.</param>
    public WatchNotifyController(DispatchQueue dispatch, TestRunner testRunner, EventLogStore eventLog)
    {
        _dispatch = dispatch;
        _testRunner = testRunner;
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
    /// Starts a test send and returns immediately. A Joplin insert can take
    /// minutes, so the caller polls <see cref="GetTest"/> for the outcome.
    /// </summary>
    /// <param name="target">Either <c>scn</c> or <c>joplin</c>.</param>
    /// <returns>The state of the test.</returns>
    [HttpPost("Test")]
    public ActionResult<TestStatus> StartTest([FromQuery] string target)
    {
        var config = Config;

        if (string.Equals(target, "scn", StringComparison.OrdinalIgnoreCase))
        {
            return ScnClient.IsConfigured(config)
                ? _testRunner.Start(config, DispatchTarget.Scn)
                : NotConfigured("SCN", "scn");
        }

        if (string.Equals(target, "joplin", StringComparison.OrdinalIgnoreCase))
        {
            return JoplinClient.IsConfigured(config)
                ? _testRunner.Start(config, DispatchTarget.Joplin)
                : NotConfigured("Joplin", "joplin");
        }

        return BadRequest("target must be scn or joplin");
    }

    /// <summary>
    /// Returns the running or most recently finished test send.
    /// </summary>
    /// <returns>The state of the test, or no content when none has run.</returns>
    [HttpGet("Test")]
    public ActionResult<TestStatus> GetTest()
    {
        var current = _testRunner.Current;
        return current is null ? NoContent() : current;
    }

    private static TestStatus NotConfigured(string label, string target) => new()
    {
        Target = target,
        Running = false,
        Success = false,
        Message = $"{label} is not enabled or not fully configured.",
        StartedAt = DateTime.UtcNow,
        FinishedAt = DateTime.UtcNow,
    };
}
