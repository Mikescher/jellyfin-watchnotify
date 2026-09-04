namespace Jellyfin.Plugin.WatchNotify.Api;

/// <summary>
/// What the plugin is currently set up to do.
/// </summary>
public class StatusResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether pushes can be sent.
    /// </summary>
    public bool ScnConfigured { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether watch-log lines can be appended.
    /// </summary>
    public bool JoplinConfigured { get; set; }

    /// <summary>
    /// Gets or sets the number of watches still waiting to be delivered.
    /// </summary>
    public int QueueDepth { get; set; }

    /// <summary>
    /// Gets or sets the most recent delivery failure.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the configuration problems worth showing on the settings page.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];
}

/// <summary>
/// The state of a dashboard test send. A test runs in the background, so the
/// page starts one and then polls this until <see cref="Running"/> clears.
/// </summary>
public class TestStatus
{
    /// <summary>
    /// Gets or sets the integration being exercised.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the send is still in flight.
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a finished send was accepted.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the progress or error text.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the send started.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// Gets or sets when the send finished.
    /// </summary>
    public DateTime? FinishedAt { get; set; }
}
