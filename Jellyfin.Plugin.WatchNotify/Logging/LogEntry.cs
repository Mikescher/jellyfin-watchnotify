namespace Jellyfin.Plugin.WatchNotify.Logging;

/// <summary>
/// One entry of the plugin's own event log.
/// </summary>
public sealed class LogEntry
{
    /// <summary>
    /// Gets or sets when the event happened, in UTC.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the event kind, one of <see cref="EventKinds"/>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user the event concerns.
    /// </summary>
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item the event concerns.
    /// </summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable detail line.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the event represents a success.
    /// </summary>
    public bool Success { get; set; } = true;
}
