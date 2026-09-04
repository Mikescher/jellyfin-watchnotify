using Jellyfin.Plugin.WatchNotify.Configuration;
using Jellyfin.Plugin.WatchNotify.Dispatch;
using Jellyfin.Plugin.WatchNotify.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchNotify.Watch;

/// <summary>
/// Subscribes to the server's playback events for the lifetime of the host and
/// turns completed watches into dispatched notifications.
/// </summary>
public sealed class WatchNotifyEntryPoint : IHostedService, IDisposable
{
    /// <summary>
    /// The server saves user data before it raises PlaybackStopped, so the
    /// user-data fallback waits this long for the richer stop event to claim
    /// the watch first.
    /// </summary>
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CollectInterval = TimeSpan.FromMinutes(30);

    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly SessionTracker _tracker;
    private readonly DispatchQueue _dispatch;
    private readonly EventLogStore _eventLog;
    private readonly ILogger<WatchNotifyEntryPoint> _logger;
    private readonly CancellationTokenSource _cts = new();

    private Task? _collector;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchNotifyEntryPoint"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="tracker">The session tracker.</param>
    /// <param name="dispatch">The outbound dispatch queue.</param>
    /// <param name="eventLog">The plugin's event log.</param>
    /// <param name="logger">The logger.</param>
    public WatchNotifyEntryPoint(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        SessionTracker tracker,
        DispatchQueue dispatch,
        EventLogStore eventLog,
        ILogger<WatchNotifyEntryPoint> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _tracker = tracker;
        _dispatch = dispatch;
        _eventLog = eventLog;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _libraryManager.ItemAdded += OnItemAdded;

        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _dispatch.Start(_cts.Token);
        _collector = Task.Run(() => CollectLoopAsync(_cts.Token), CancellationToken.None);

        _logger.LogInformation("WatchNotify started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemAdded -= OnItemAdded;

        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        await _dispatch.StopAsync().ConfigureAwait(false);

        if (_collector is not null)
        {
            try
            {
                await _collector.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _logger.LogInformation("WatchNotify stopped");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Dispose();
    }

    private static TimeZoneInfo ResolveTimeZone(string id, ILogger logger)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("Unknown display timezone {TimeZone}, falling back to UTC", id);
            return TimeZoneInfo.Utc;
        }
    }

    private static string ResolveUserName(PlaybackProgressEventArgs e)
        => e.Users.FirstOrDefault()?.Username ?? e.Session?.UserName ?? string.Empty;

    private static Guid ResolveUserId(PlaybackProgressEventArgs e)
        => e.Users.FirstOrDefault()?.Id ?? e.Session?.UserId ?? Guid.Empty;

    private static string ResolveSessionId(PlaybackProgressEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Session?.Id))
        {
            return e.Session.Id;
        }

        if (!string.IsNullOrEmpty(e.PlaySessionId))
        {
            return e.PlaySessionId;
        }

        return ResolveUserId(e).ToString("N");
    }

    private async Task CollectLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CollectInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            _tracker.Collect(TimeSpan.FromMinutes(Config.DedupMinutes));
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        _eventLog.Add(EventKinds.ConfigChanged, detail: "Configuration saved");
        _logger.LogInformation("WatchNotify configuration updated");
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (!WatchEventFactory.IsWatchable(e.Item))
            {
                return;
            }

            RecordActivity(e);

            _eventLog.Add(
                EventKinds.PlaybackStart,
                ResolveUserName(e),
                WatchEventFactory.ShortTitle(e.Item),
                Describe(e.DeviceName, e.ClientName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback start");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (!WatchEventFactory.IsWatchable(e.Item))
            {
                return;
            }

            RecordActivity(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback progress");
        }
    }

    private void RecordActivity(PlaybackProgressEventArgs e)
    {
        _tracker.Record(
            SessionTracker.SessionKey(ResolveSessionId(e), e.Item.Id),
            ResolveUserId(e),
            e.Item.Id,
            DateTime.UtcNow,
            TimeSpan.FromMinutes(Config.StartCoalesceMinutes));
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var config = Config;

            if (!WatchEventFactory.IsWatchable(e.Item))
            {
                _logger.LogDebug("Stop ignored, item is not a movie or episode: {Item}", e.Item?.Name);
                return;
            }

            var user = ResolveUserName(e);
            var title = WatchEventFactory.ShortTitle(e.Item);
            var fraction = WatchEventFactory.Fraction(e.PlaybackPositionTicks, e.Item.RunTimeTicks);

            _eventLog.Add(
                EventKinds.PlaybackStop,
                user,
                title,
                $"{Format.Percent(fraction)} watched, completed={e.PlayedToCompletion}");

            if (!e.PlayedToCompletion && fraction < config.WatchedThreshold)
            {
                _eventLog.Add(
                    EventKinds.NotWatched,
                    user,
                    title,
                    $"{Format.Percent(fraction)} is below the {Format.Percent(config.WatchedThreshold)} threshold",
                    success: false);
                return;
            }

            var userId = ResolveUserId(e);
            var sessionKey = SessionTracker.SessionKey(ResolveSessionId(e), e.Item.Id);
            var endUtc = DateTime.UtcNow;

            if (!_tracker.TryPopStart(sessionKey, out var startUtc)
                && !_tracker.TryPopStartForItem(userId, e.Item.Id, out startUtc))
            {
                startUtc = EstimateStart(endUtc, e.PlaybackPositionTicks);
            }

            Complete(config, e.Item, userId, user, startUtc, endUtc, e.PlaybackPositionTicks, e.PlayedToCompletion, e.DeviceName, e.ClientName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback stop");
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var config = Config;

            if (!WatchEventFactory.IsWatchable(e.Item) || e.UserData?.Played != true)
            {
                return;
            }

            switch (e.SaveReason)
            {
                case UserDataSaveReason.PlaybackFinished:
                    // Some clients report a natural end of file as a stop with no
                    // position and no completion flag; this path catches those. It
                    // runs late so a well-behaved stop event claims the watch first.
                    _ = Task.Run(() => RunFallbackAsync(e, FallbackDelay, _cts.Token), CancellationToken.None);
                    break;

                case UserDataSaveReason.TogglePlayed when config.NotifyOnManualMarkWatched:
                    _ = Task.Run(() => RunFallbackAsync(e, TimeSpan.Zero, _cts.Token), CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle user data save");
        }
    }

    private async Task RunFallbackAsync(UserDataSaveEventArgs e, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var item = e.Item;
            var userName = _userManager.GetUserById(e.UserId)?.Username ?? string.Empty;
            var endUtc = DateTime.UtcNow;
            var positionTicks = e.UserData?.PlaybackPositionTicks ?? 0;

            if (!_tracker.TryPopStartForItem(e.UserId, item.Id, out var startUtc))
            {
                startUtc = EstimateStart(endUtc, positionTicks);
            }

            Complete(Config, item, e.UserId, userName, startUtc, endUtc, positionTicks, playedToCompletion: true, device: null, client: null);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle the user-data watch fallback");
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item.IsVirtualItem)
            {
                return;
            }

            _eventLog.Add(EventKinds.ItemAdded, item: e.Item.Name, detail: e.Item.GetType().Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle item added");
        }
    }

    /// <summary>
    /// Applies duplicate suppression and hands the finished watch to the outbound actions.
    /// </summary>
    private void Complete(
        PluginConfiguration config,
        BaseItem item,
        Guid userId,
        string userName,
        DateTime startUtc,
        DateTime endUtc,
        long? positionTicks,
        bool playedToCompletion,
        string? device,
        string? client)
    {
        var title = WatchEventFactory.ShortTitle(item);

        if (!_tracker.MarkNotified(SessionTracker.DedupKey(userId, item.Id), TimeSpan.FromMinutes(config.DedupMinutes)))
        {
            _eventLog.Add(EventKinds.DedupSkipped, userName, title, "Already notified within the dedup window");
            return;
        }

        var watchEvent = WatchEventFactory.Create(
            item,
            userName,
            startUtc,
            endUtc,
            positionTicks,
            playedToCompletion,
            device,
            client,
            ResolveTimeZone(config.DisplayTimeZone, _logger));

        _eventLog.Add(
            EventKinds.Watched,
            userName,
            title,
            $"{Format.Percent(watchEvent.Fraction)}, {Format.Time(watchEvent.Start)} → {Format.Time(watchEvent.End)}");

        _logger.LogInformation(
            "Watched: {User} finished {Item} ({Percent})",
            userName,
            title,
            Format.Percent(watchEvent.Fraction));

        _dispatch.Dispatch(config, watchEvent, userId);
    }

    /// <summary>
    /// Estimates a start time when no playback start was recorded, for example
    /// after a server restart mid-watch.
    /// </summary>
    private static DateTime EstimateStart(DateTime endUtc, long? positionTicks)
        => positionTicks is > 0 ? endUtc - TimeSpan.FromTicks(positionTicks.Value) : endUtc;

    private static string Describe(string? device, string? client)
    {
        if (string.IsNullOrEmpty(device))
        {
            return client ?? string.Empty;
        }

        return string.IsNullOrEmpty(client) ? device : device + " · " + client;
    }
}
