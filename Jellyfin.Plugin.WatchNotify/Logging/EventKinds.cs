namespace Jellyfin.Plugin.WatchNotify.Logging;

/// <summary>
/// The event kinds written to the plugin's own event log.
/// </summary>
public static class EventKinds
{
    /// <summary>Playback of a movie or episode started.</summary>
    public const string PlaybackStart = "playback-start";

    /// <summary>Playback stopped.</summary>
    public const string PlaybackStop = "playback-stop";

    /// <summary>A stop counted as a completed watch.</summary>
    public const string Watched = "watched";

    /// <summary>A stop did not reach the watched threshold.</summary>
    public const string NotWatched = "not-watched";

    /// <summary>A watch was suppressed because the same user and item fired recently.</summary>
    public const string DedupSkipped = "dedup-skipped";

    /// <summary>A push notification was delivered.</summary>
    public const string ScnSent = "scn-sent";

    /// <summary>A push notification attempt failed.</summary>
    public const string ScnFailed = "scn-failed";

    /// <summary>A watch-log line was appended.</summary>
    public const string JoplinAppended = "joplin-appended";

    /// <summary>A watch-log append attempt failed.</summary>
    public const string JoplinFailed = "joplin-failed";

    /// <summary>An item was added to the library.</summary>
    public const string ItemAdded = "item-added";

    /// <summary>The plugin configuration was saved.</summary>
    public const string ConfigChanged = "config-changed";
}
