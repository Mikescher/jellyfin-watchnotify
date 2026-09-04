using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WatchNotify.Configuration;

/// <summary>
/// Plugin settings, persisted by the server as XML.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the IANA timezone watch times are rendered in.
    /// </summary>
    public string DisplayTimeZone { get; set; } = "Europe/Berlin";

    /// <summary>
    /// Gets or sets the played fraction (0..1) at which a stop counts as watched.
    /// </summary>
    public double WatchedThreshold { get; set; } = 0.90;

    /// <summary>
    /// Gets or sets the window during which a repeated notification for the same
    /// user and item is suppressed.
    /// </summary>
    public int DedupMinutes { get; set; } = 360;

    /// <summary>
    /// Gets or sets the gap after the last session activity within which a fresh
    /// playback start is folded into the running watch instead of restarting it.
    /// </summary>
    public int StartCoalesceMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets the delay between dispatch attempts after a failed send.
    /// </summary>
    public int RetrySeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether SCN push notifications are sent.
    /// </summary>
    public bool ScnEnabled { get; set; }

    /// <summary>
    /// Gets or sets the Simple Cloud Notifier endpoint.
    /// </summary>
    public string ScnBaseUrl { get; set; } = "https://simplecloudnotifier.blackforestbytes.com/";

    /// <summary>
    /// Gets or sets the SCN user id.
    /// </summary>
    public string ScnUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SCN key.
    /// </summary>
    public string ScnKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SCN channel, or an empty string for the default channel.
    /// </summary>
    public string ScnChannel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SCN priority (0, 1 or 2).
    /// </summary>
    public int ScnPriority { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether every user triggers an SCN push.
    /// </summary>
    public bool ScnAllUsers { get; set; } = true;

    /// <summary>
    /// Gets or sets the Jellyfin user ids that trigger an SCN push when
    /// <see cref="ScnAllUsers"/> is off.
    /// </summary>
    public string[] ScnUserIds { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether watch-log lines are appended to Joplin.
    /// </summary>
    public bool JoplinEnabled { get; set; }

    /// <summary>
    /// Gets or sets the Joplin API server base url.
    /// </summary>
    public string JoplinBaseUrl { get; set; } = "http://10.8.0.4:4466";

    /// <summary>
    /// Gets or sets the Joplin API bearer token.
    /// </summary>
    public string JoplinToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the note that watch-log lines are inserted into.
    /// </summary>
    public string JoplinNoteId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text marker in the note that lines are inserted relative to.
    /// </summary>
    public string JoplinAnchor { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether lines go before or after the anchor.
    /// </summary>
    public string JoplinPosition { get; set; } = "before";

    /// <summary>
    /// Gets or sets the number of blank lines kept between the inserted line and the anchor.
    /// </summary>
    public int JoplinEmptylineGap { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every user is written to the watch log.
    /// </summary>
    public bool JoplinAllUsers { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin user ids written to the watch log when
    /// <see cref="JoplinAllUsers"/> is off.
    /// </summary>
    public string[] JoplinUserIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of entries kept in the plugin's own event log.
    /// </summary>
    public int EventLogSize { get; set; } = 500;

    /// <summary>
    /// Gets or sets a value indicating whether notable events are also written to
    /// Jellyfin's activity log.
    /// </summary>
    public bool WriteToActivityLog { get; set; } = true;
}
