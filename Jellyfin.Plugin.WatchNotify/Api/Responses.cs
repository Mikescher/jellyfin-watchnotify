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
/// The outcome of a test notification.
/// </summary>
public class TestResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the notification was accepted.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the result or error text.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
