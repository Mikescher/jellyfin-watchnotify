namespace Jellyfin.Plugin.WatchNotify.Watch;

/// <summary>
/// The normalized data derived from a completed playback that the outbound
/// actions consume.
/// </summary>
public sealed record WatchEvent
{
    /// <summary>
    /// Gets the Jellyfin username of the watching user.
    /// </summary>
    public required string User { get; init; }

    /// <summary>
    /// Gets the short display title, e.g. "The Matrix (1999)" or "Firefly S01E02".
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the push-notification title, e.g. "[👁️] [S02E13] Firefly".
    /// </summary>
    public required string ScnTitle { get; init; }

    /// <summary>
    /// Gets the episode's own title, or an empty string for non-episodes.
    /// </summary>
    public required string EpisodeName { get; init; }

    /// <summary>
    /// Gets the watch-log title, e.g. "Firefly [S01E02]".
    /// </summary>
    public required string JoplinTitle { get; init; }

    /// <summary>
    /// Gets the item type, "Movie" or "Episode".
    /// </summary>
    public required string ItemType { get; init; }

    /// <summary>
    /// Gets the watch start as wall-clock time in the configured display timezone.
    /// </summary>
    public required DateTime Start { get; init; }

    /// <summary>
    /// Gets the watch end as wall-clock time in the configured display timezone.
    /// </summary>
    public required DateTime End { get; init; }

    /// <summary>
    /// Gets the watched fraction of the runtime, 0..1.
    /// </summary>
    public required double Fraction { get; init; }

    /// <summary>
    /// Gets the playback position reached.
    /// </summary>
    public required TimeSpan Position { get; init; }

    /// <summary>
    /// Gets the item's total runtime.
    /// </summary>
    public required TimeSpan Runtime { get; init; }

    /// <summary>
    /// Gets the device the item was played on.
    /// </summary>
    public string Device { get; init; } = string.Empty;

    /// <summary>
    /// Gets the client application the item was played with.
    /// </summary>
    public string Client { get; init; } = string.Empty;
}
