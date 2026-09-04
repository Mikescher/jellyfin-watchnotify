namespace Jellyfin.Plugin.WatchNotify.Watch;

/// <summary>
/// Ephemeral in-memory state: playback start times keyed by session and item,
/// and a ledger of recently-notified user and item pairs. All of it is lost on
/// restart, which is acceptable for this use case.
/// </summary>
public sealed class SessionTracker
{
    private const int SessionMaxAgeHours = 24;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _notified = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the key identifying a playback session and item.
    /// </summary>
    /// <param name="sessionId">The playback session id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>The session key.</returns>
    public static string SessionKey(string sessionId, Guid itemId)
        => sessionId + "|" + itemId.ToString("N");

    /// <summary>
    /// Builds the key identifying a user and item pair for duplicate suppression.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>The dedup key.</returns>
    public static string DedupKey(Guid userId, Guid itemId)
        => userId.ToString("N") + "|" + itemId.ToString("N");

    /// <summary>
    /// Folds an event into the session entry. An entry whose last activity is
    /// within <paramref name="coalesceWindow"/> keeps its earliest start, so a
    /// seek that restarts the stream does not reset it; a longer gap begins a
    /// new session instead of gluing the watch to a stale start.
    /// </summary>
    /// <param name="key">The session key.</param>
    /// <param name="userId">The watching user.</param>
    /// <param name="itemId">The item being played.</param>
    /// <param name="start">The event instant, in UTC.</param>
    /// <param name="coalesceWindow">The window within which an entry is reused.</param>
    public void Record(string key, Guid userId, Guid itemId, DateTime start, TimeSpan coalesceWindow)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_sessions.TryGetValue(key, out var entry) && now - entry.LastSeen < coalesceWindow)
            {
                _sessions[key] = entry with
                {
                    Start = start < entry.Start ? start : entry.Start,
                    LastSeen = now,
                };
                return;
            }

            _sessions[key] = new SessionEntry(start, now, userId, itemId);
        }
    }

    /// <summary>
    /// Returns and removes the recorded start time for a session key.
    /// </summary>
    /// <param name="key">The session key.</param>
    /// <param name="start">The recorded start, in UTC.</param>
    /// <returns>Whether a start time was recorded.</returns>
    public bool TryPopStart(string key, out DateTime start)
    {
        lock (_lock)
        {
            if (_sessions.Remove(key, out var entry))
            {
                start = entry.Start;
                return true;
            }
        }

        start = default;
        return false;
    }

    /// <summary>
    /// Returns and removes the earliest recorded start time for a user and item,
    /// across whichever session recorded it. Used by the paths that learn about a
    /// finished watch without knowing its session.
    /// </summary>
    /// <param name="userId">The watching user.</param>
    /// <param name="itemId">The item being played.</param>
    /// <param name="start">The recorded start, in UTC.</param>
    /// <returns>Whether a start time was recorded.</returns>
    public bool TryPopStartForItem(Guid userId, Guid itemId, out DateTime start)
    {
        lock (_lock)
        {
            string? match = null;
            foreach (var (key, entry) in _sessions)
            {
                if (entry.UserId == userId && entry.ItemId == itemId
                    && (match is null || entry.Start < _sessions[match].Start))
                {
                    match = key;
                }
            }

            if (match is not null && _sessions.Remove(match, out var found))
            {
                start = found.Start;
                return true;
            }
        }

        start = default;
        return false;
    }

    /// <summary>
    /// Records a notification and reports whether the caller should proceed. It
    /// returns false when an identical key was seen within the dedup window.
    /// </summary>
    /// <param name="key">The dedup key.</param>
    /// <param name="ttl">The dedup window.</param>
    /// <returns>Whether the notification should be sent.</returns>
    public bool MarkNotified(string key, TimeSpan ttl)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_notified.TryGetValue(key, out var last) && now - last < ttl)
            {
                return false;
            }

            _notified[key] = now;
            return true;
        }
    }

    /// <summary>
    /// Reports whether a key was already notified within the dedup window,
    /// without claiming it.
    /// </summary>
    /// <param name="key">The dedup key.</param>
    /// <param name="ttl">The dedup window.</param>
    /// <returns>Whether the key is already claimed.</returns>
    public bool WasNotified(string key, TimeSpan ttl)
    {
        lock (_lock)
        {
            return _notified.TryGetValue(key, out var last) && DateTime.UtcNow - last < ttl;
        }
    }

    /// <summary>
    /// Drops stale entries so neither map grows without bound.
    /// </summary>
    /// <param name="dedupTtl">The dedup window.</param>
    public void Collect(TimeSpan dedupTtl)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            foreach (var key in _sessions
                         .Where(kv => now - kv.Value.LastSeen > TimeSpan.FromHours(SessionMaxAgeHours))
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _sessions.Remove(key);
            }

            foreach (var key in _notified
                         .Where(kv => now - kv.Value > dedupTtl)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _notified.Remove(key);
            }
        }
    }

    private sealed record SessionEntry(DateTime Start, DateTime LastSeen, Guid UserId, Guid ItemId);
}
