using System.Threading.Channels;
using Jellyfin.Plugin.WatchNotify.Configuration;
using Jellyfin.Plugin.WatchNotify.Logging;
using Jellyfin.Plugin.WatchNotify.Watch;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchNotify.Dispatch;

/// <summary>
/// The outbound action to take for a watch event.
/// </summary>
public enum DispatchTarget
{
    /// <summary>Send a Simple Cloud Notifier push.</summary>
    Scn,

    /// <summary>Append a line to the Joplin watch log.</summary>
    Joplin,
}

/// <summary>
/// Accepts finished watches and delivers them to the enabled integrations,
/// retrying until each succeeds or the server shuts down. Each target has its
/// own queue and consumer so a failing integration never holds up the other.
/// </summary>
public sealed class DispatchQueue
{
    private readonly Dictionary<DispatchTarget, Channel<WatchEvent>> _queues = new()
    {
        [DispatchTarget.Scn] = Channel.CreateUnbounded<WatchEvent>(new UnboundedChannelOptions { SingleReader = true }),
        [DispatchTarget.Joplin] = Channel.CreateUnbounded<WatchEvent>(new UnboundedChannelOptions { SingleReader = true }),
    };

    private readonly ScnClient _scn;
    private readonly JoplinClient _joplin;
    private readonly EventLogStore _eventLog;
    private readonly ILogger<DispatchQueue> _logger;

    private List<Task>? _consumers;
    private int _depth;

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchQueue"/> class.
    /// </summary>
    /// <param name="scn">The push notification client.</param>
    /// <param name="joplin">The watch-log client.</param>
    /// <param name="eventLog">The plugin's event log.</param>
    /// <param name="logger">The logger.</param>
    public DispatchQueue(ScnClient scn, JoplinClient joplin, EventLogStore eventLog, ILogger<DispatchQueue> logger)
    {
        _scn = scn;
        _joplin = joplin;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of watches queued or currently being retried. Counted by
    /// hand because a single-consumer channel does not support Count.
    /// </summary>
    public int Depth => Volatile.Read(ref _depth);

    /// <summary>
    /// Gets the most recent delivery failure, or null when nothing has failed.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Reports whether the given user should be notified through a target.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="target">The outbound action.</param>
    /// <param name="userId">The watching user.</param>
    /// <returns>Whether the user is on the target's allowlist.</returns>
    public static bool IsAllowed(PluginConfiguration config, DispatchTarget target, Guid userId)
    {
        var (allUsers, allowed) = target == DispatchTarget.Scn
            ? (config.ScnAllUsers, config.ScnUserIds)
            : (config.JoplinAllUsers, config.JoplinUserIds);

        return allUsers || allowed.Any(id => Guid.TryParse(id, out var parsed) && parsed == userId);
    }

    /// <summary>
    /// Starts the consumer loops.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the server shuts down.</param>
    public void Start(CancellationToken cancellationToken)
    {
        _consumers = _queues
            .Select(pair => Task.Run(() => ConsumeAsync(pair.Key, pair.Value.Reader, cancellationToken), CancellationToken.None))
            .ToList();
    }

    /// <summary>
    /// Waits for the consumer loops to finish after their cancellation token fires.
    /// </summary>
    /// <returns>A task that completes once the loops have exited.</returns>
    public async Task StopAsync()
    {
        if (_consumers is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(_consumers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown; undelivered notifications are abandoned.
        }

        _consumers = null;
    }

    /// <summary>
    /// Queues a finished watch for every enabled integration the user is allowed on.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="watchEvent">The finished watch.</param>
    /// <param name="userId">The watching user.</param>
    public void Dispatch(PluginConfiguration config, WatchEvent watchEvent, Guid userId)
    {
        Queue(config, DispatchTarget.Scn, ScnClient.IsConfigured(config), watchEvent, userId);
        Queue(config, DispatchTarget.Joplin, JoplinClient.IsConfigured(config), watchEvent, userId);
    }

    private void Queue(PluginConfiguration config, DispatchTarget target, bool configured, WatchEvent watchEvent, Guid userId)
    {
        if (config.JoplinEnabled && target == DispatchTarget.Joplin && !configured)
        {
            _logger.LogWarning("Joplin is enabled but incompletely configured; skipping {Item}", watchEvent.Title);
        }

        if (!configured || !IsAllowed(config, target, userId))
        {
            return;
        }

        if (_queues[target].Writer.TryWrite(watchEvent))
        {
            Interlocked.Increment(ref _depth);
        }
    }

    private async Task ConsumeAsync(DispatchTarget target, ChannelReader<WatchEvent> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var watchEvent in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await DeliverAsync(target, watchEvent, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _depth);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The {Target} dispatch loop stopped unexpectedly", target);
        }
    }

    private async Task DeliverAsync(DispatchTarget target, WatchEvent watchEvent, CancellationToken cancellationToken)
    {
        for (var attempt = 1; !cancellationToken.IsCancellationRequested; attempt++)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            try
            {
                if (target == DispatchTarget.Scn)
                {
                    await _scn.SendAsync(config, watchEvent, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _joplin.AppendAsync(config, watchEvent, timeout: null, cancellationToken).ConfigureAwait(false);
                }

                LastError = null;
                _eventLog.Add(
                    target == DispatchTarget.Scn ? EventKinds.ScnSent : EventKinds.JoplinAppended,
                    watchEvent.User,
                    watchEvent.Title,
                    attempt > 1 ? $"Delivered on attempt {attempt}" : "Delivered");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = Format.Describe(ex);
                _eventLog.Add(
                    target == DispatchTarget.Scn ? EventKinds.ScnFailed : EventKinds.JoplinFailed,
                    watchEvent.User,
                    watchEvent.Title,
                    $"Attempt {attempt} failed: {Format.Describe(ex)}",
                    success: false);
                _logger.LogWarning(ex, "{Target} delivery attempt {Attempt} failed for {Item}", target, attempt, watchEvent.Title);
            }

            var retry = TimeSpan.FromSeconds(Math.Max(1, config.RetrySeconds));
            await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
        }
    }
}
