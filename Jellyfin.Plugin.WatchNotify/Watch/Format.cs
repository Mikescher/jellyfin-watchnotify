using System.Globalization;

namespace Jellyfin.Plugin.WatchNotify.Watch;

/// <summary>
/// Rendering helpers shared by the notification bodies and the watch log.
/// </summary>
public static class Format
{
    /// <summary>
    /// The human-readable timestamp format used in notifications and the watch log.
    /// </summary>
    public const string TimeLayout = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Formats a wall-clock timestamp.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The formatted timestamp.</returns>
    public static string Time(DateTime value) => value.ToString(TimeLayout, CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a 0..1 fraction as a whole-percent string, e.g. "95%".
    /// </summary>
    /// <param name="fraction">The fraction.</param>
    /// <returns>The formatted percentage.</returns>
    public static string Percent(double fraction)
        => Math.Round(fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Formats a duration as "1h02m03s" or "2m03s".
    /// </summary>
    /// <param name="value">The duration.</param>
    /// <returns>The formatted duration.</returns>
    public static string Duration(TimeSpan value)
    {
        var total = TimeSpan.FromSeconds(Math.Round(value.TotalSeconds, MidpointRounding.AwayFromZero));
        var hours = (int)total.TotalHours;
        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}h{1:00}m{2:00}s", hours, total.Minutes, total.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}m{1:00}s", total.Minutes, total.Seconds);
    }

    /// <summary>
    /// Left-justifies a string to the given width, counted in codepoints so that
    /// non-BMP characters do not shift the column.
    /// </summary>
    /// <param name="value">The string to pad.</param>
    /// <param name="width">The target width.</param>
    /// <returns>The padded string.</returns>
    public static string PadRight(string value, int width)
    {
        var length = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            length++;
        }

        return length < width ? value + new string(' ', width - length) : value;
    }
}
