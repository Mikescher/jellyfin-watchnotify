using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.WatchNotify.Watch;

/// <summary>
/// Derives titles, progress and timestamps for a finished watch.
/// </summary>
public static class WatchEventFactory
{
    /// <summary>
    /// Reports whether the item is one of the types worth notifying about.
    /// </summary>
    /// <param name="item">The played item.</param>
    /// <returns>Whether the item is a movie or an episode.</returns>
    public static bool IsWatchable(BaseItem? item) => item is Movie or Episode;

    /// <summary>
    /// Returns the fraction of the item that was played, 0..1.
    /// </summary>
    /// <param name="positionTicks">The playback position.</param>
    /// <param name="runTimeTicks">The item runtime.</param>
    /// <returns>The played fraction.</returns>
    public static double Fraction(long? positionTicks, long? runTimeTicks)
        => runTimeTicks is > 0 ? (double)(positionTicks ?? 0) / runTimeTicks.Value : 0;

    /// <summary>
    /// Builds the event for a finished watch.
    /// </summary>
    /// <param name="item">The played item.</param>
    /// <param name="user">The watching user's name.</param>
    /// <param name="startUtc">When the watch began.</param>
    /// <param name="endUtc">When the watch ended.</param>
    /// <param name="positionTicks">The playback position reached.</param>
    /// <param name="playedToCompletion">Whether the client reported completion.</param>
    /// <param name="device">The playing device, if known.</param>
    /// <param name="client">The playing client, if known.</param>
    /// <param name="displayZone">The timezone timestamps are rendered in.</param>
    /// <returns>The watch event.</returns>
    public static WatchEvent Create(
        BaseItem item,
        string user,
        DateTime startUtc,
        DateTime endUtc,
        long? positionTicks,
        bool playedToCompletion,
        string? device,
        string? client,
        TimeZoneInfo displayZone)
    {
        var (fraction, position, runtime) = EffectiveProgress(positionTicks, item.RunTimeTicks, playedToCompletion);

        return new WatchEvent
        {
            User = user,
            Title = ShortTitle(item),
            ScnTitle = ScnTitle(item),
            EpisodeName = EpisodeTitle(item),
            JoplinTitle = JoplinTitle(item),
            ItemType = item is Episode ? "Episode" : "Movie",
            Start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), displayZone),
            End = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc), displayZone),
            Fraction = fraction,
            Position = position,
            Runtime = runtime,
            Device = device ?? string.Empty,
            Client = client ?? string.Empty,
        };
    }

    /// <summary>
    /// Returns the fraction, position and runtime to display. A completed
    /// playback reads as fully watched even when the client reset or
    /// under-reported the final position, and any overshoot is clamped.
    /// </summary>
    /// <param name="positionTicks">The playback position.</param>
    /// <param name="runTimeTicks">The item runtime.</param>
    /// <param name="playedToCompletion">Whether the client reported completion.</param>
    /// <returns>The fraction, position and runtime.</returns>
    public static (double Fraction, TimeSpan Position, TimeSpan Runtime) EffectiveProgress(
        long? positionTicks, long? runTimeTicks, bool playedToCompletion)
    {
        var runtime = TicksToDuration(runTimeTicks);
        var position = TicksToDuration(positionTicks);
        var fraction = Fraction(positionTicks, runTimeTicks);

        if (playedToCompletion || fraction > 1)
        {
            fraction = 1;
            position = runtime;
        }

        return (fraction, position, runtime);
    }

    /// <summary>
    /// Renders the short display title used in notification bodies and logs.
    /// </summary>
    /// <param name="item">The played item.</param>
    /// <returns>The short title.</returns>
    public static string ShortTitle(BaseItem item)
    {
        if (item is Episode episode)
        {
            return (episode.SeriesName + " " + SeasonEpisodeTag(episode)).Trim();
        }

        return item.ProductionYear is > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", item.Name, item.ProductionYear.Value)
            : item.Name ?? string.Empty;
    }

    /// <summary>
    /// Renders the push-notification title.
    /// </summary>
    /// <param name="item">The played item.</param>
    /// <returns>The notification title.</returns>
    public static string ScnTitle(BaseItem item)
        => item is Episode episode
            ? $"[👁️] [{SeasonEpisodeTag(episode)}] {episode.SeriesName}".Trim()
            : $"[👁️] {item.Name}".Trim();

    /// <summary>
    /// Renders the title used in the watch-log line.
    /// </summary>
    /// <param name="item">The played item.</param>
    /// <returns>The watch-log title.</returns>
    public static string JoplinTitle(BaseItem item)
        => item is Episode episode
            ? $"{episode.SeriesName} [{SeasonEpisodeTag(episode)}]".Trim()
            : item.Name ?? string.Empty;

    /// <summary>
    /// Returns the episode's own title, or an empty string for non-episodes
    /// whose name the short title already carries.
    /// </summary>
    /// <param name="item">The played item.</param>
    /// <returns>The episode title.</returns>
    public static string EpisodeTitle(BaseItem item)
        => item is Episode ? (item.Name ?? string.Empty).Trim() : string.Empty;

    private static string SeasonEpisodeTag(Episode episode)
        => string.Format(
            CultureInfo.InvariantCulture,
            "S{0:00}E{1:00}",
            episode.ParentIndexNumber ?? 0,
            episode.IndexNumber ?? 0);

    private static TimeSpan TicksToDuration(long? ticks)
        => ticks is > 0 ? TimeSpan.FromSeconds(ticks.Value / TimeSpan.TicksPerSecond) : TimeSpan.Zero;
}
