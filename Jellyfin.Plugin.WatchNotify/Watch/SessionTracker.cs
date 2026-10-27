namespace Jellyfin.Plugin.WatchNotify.Watch;

/// <summary>
/// Ephemeral in-memory state: playback start times and furthest reported
/// positions keyed by session and item, and a ledger of recently-notified user
/// and item pairs. All of it is lost on restart, which is acceptable for this
/// use case.
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
    /// <param name="positionTicks">The playback position reported with the event.</param>
    /// <param name="coalesceWindow">The window within which an entry is reused.</param>
    public void Record(string key, Guid userId, Guid itemId, DateTime start, long? positionTicks, TimeSpan coalesceWindow)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var position = Math.Max(positionTicks ?? 0, 0);
            if (_sessions.TryGetValue(key, out var entry) && now - entry.LastSeen < coalesceWindow)
            {
                _sessions[key] = entry with
                {
                    Start = start < entry.Start ? start : entry.Start,
                    LastSeen = now,
                    FurthestTicks = Math.Max(entry.FurthestTicks, position),
                };
                return;
            }

            _sessions[key] = new SessionEntry(start, now, userId, itemId, position);
        }
    }

    /// <summary>
    /// Returns the furthest position reported by the most recently active
    /// session of a user and item.
    /// </summary>
    /// <param name="userId">The watching user.</param>
    /// <param name="itemId">The item being played.</param>
    /// <returns>The furthest position, or null when no session is tracked.</returns>
    public long? FurthestPosition(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            SessionEntry? latest = null;
            foreach (var entry in _sessions.Values)
            {
                if (entry.UserId == userId && entry.ItemId == itemId
                    && (latest is null || entry.LastSeen > latest.LastSeen))
                {
                    latest = entry;
                }
            }

            return latest?.FurthestTicks;
        }
    }

    /// <summary>
    /// Ends the watch of a user and item: drops every session tracked for them,
    /// so none of them counts towards a later watch.
    /// </summary>
    /// <param name="userId">The watching user.</param>
    /// <param name="itemId">The watched item.</param>
    /// <param name="key">The session that finished the watch, if known.</param>
    /// <returns>
    /// The start of session <paramref name="key"/> if it is tracked, else the
    /// earliest tracked start, else null.
    /// </returns>
    public DateTime? EndWatch(Guid userId, Guid itemId, string? key = null)
    {
        lock (_lock)
        {
            DateTime? own = key is not null && _sessions.Remove(key, out var session) ? session.Start : null;
            DateTime? earliest = null;
            foreach (var (otherKey, entry) in _sessions
                         .Where(kv => kv.Value.UserId == userId && kv.Value.ItemId == itemId)
                         .ToList())
            {
                _sessions.Remove(otherKey);
                if (earliest is null || entry.Start < earliest)
                {
                    earliest = entry.Start;
                }
            }

            return own ?? earliest;
        }
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

    private sealed record SessionEntry(DateTime Start, DateTime LastSeen, Guid UserId, Guid ItemId, long FurthestTicks);
}
