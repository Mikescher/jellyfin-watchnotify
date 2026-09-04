using Jellyfin.Plugin.WatchNotify.Api;
using Jellyfin.Plugin.WatchNotify.Configuration;
using Jellyfin.Plugin.WatchNotify.Logging;
using Jellyfin.Plugin.WatchNotify.Watch;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchNotify.Dispatch;

/// <summary>
/// Runs a dashboard test send in the background and keeps its outcome for the
/// page to poll. A Joplin insert can take minutes, which is far longer than a
/// browser request should be held open.
/// </summary>
public sealed class TestRunner
{
    private readonly ScnClient _scn;
    private readonly JoplinClient _joplin;
    private readonly EventLogStore _eventLog;
    private readonly ILogger<TestRunner> _logger;
    private readonly Lock _lock = new();

    private TestStatus? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestRunner"/> class.
    /// </summary>
    /// <param name="scn">The push notification client.</param>
    /// <param name="joplin">The watch-log client.</param>
    /// <param name="eventLog">The plugin's event log.</param>
    /// <param name="logger">The logger.</param>
    public TestRunner(ScnClient scn, JoplinClient joplin, EventLogStore eventLog, ILogger<TestRunner> logger)
    {
        _scn = scn;
        _joplin = joplin;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>
    /// Gets the running or most recently finished test, if there has been one.
    /// </summary>
    public TestStatus? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Starts a test send unless one is already running.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="target">The integration to exercise.</param>
    /// <returns>The state of the running test.</returns>
    public TestStatus Start(PluginConfiguration config, DispatchTarget target)
    {
        TestStatus status;

        lock (_lock)
        {
            if (_current is { Running: true } running)
            {
                return running;
            }

            status = new TestStatus
            {
                Target = target.ToString(),
                Running = true,
                StartedAt = DateTime.UtcNow,
                Message = "Sending…",
            };
            _current = status;
        }

        _ = Task.Run(() => RunAsync(config, target, status));
        return status;
    }

    private async Task RunAsync(PluginConfiguration config, DispatchTarget target, TestStatus status)
    {
        var watchEvent = BuildSampleEvent(config);

        try
        {
            if (target == DispatchTarget.Scn)
            {
                await _scn.SendAsync(config, watchEvent, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _joplin.AppendAsync(config, watchEvent, timeout: null, CancellationToken.None).ConfigureAwait(false);
            }

            Finish(status, true, "Delivered.");
            _eventLog.Add(
                target == DispatchTarget.Scn ? EventKinds.ScnSent : EventKinds.JoplinAppended,
                watchEvent.User,
                watchEvent.Title,
                $"Test delivered in {Format.Duration(DateTime.UtcNow - status.StartedAt)}");
        }
        catch (Exception ex)
        {
            var message = Format.Describe(ex);
            Finish(status, false, message);
            _eventLog.Add(
                target == DispatchTarget.Scn ? EventKinds.ScnFailed : EventKinds.JoplinFailed,
                watchEvent.User,
                watchEvent.Title,
                "Test failed: " + message,
                success: false);
            _logger.LogWarning(ex, "{Target} test send failed", target);
        }
    }

    private void Finish(TestStatus status, bool success, string message)
    {
        lock (_lock)
        {
            status.Running = false;
            status.Success = success;
            status.Message = message;
            status.FinishedAt = DateTime.UtcNow;
        }
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
