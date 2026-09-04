using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchNotify.Logging;

/// <summary>
/// The rolling event log rendered by the plugin's log page, persisted so it
/// survives a server restart.
/// </summary>
public sealed class EventLogStore : IDisposable
{
    private const int DefaultCapacity = 500;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ILogger<EventLogStore> _logger;
    private readonly string _filePath;
    private readonly Lock _lock = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly Timer _saveTimer;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventLogStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths; the log lives under the data path.</param>
    /// <param name="logger">The logger.</param>
    public EventLogStore(IApplicationPaths applicationPaths, ILogger<EventLogStore> logger)
    {
        _logger = logger;

        // Deliberately not the plugin's own data folder: that is versioned and
        // discarded on every update, and the configuration folder is the server's.
        var directory = Path.Combine(applicationPaths.DataPath, "watchnotify");
        _filePath = Path.Combine(directory, "events.json");

        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);

        Load();
    }

    /// <summary>
    /// Appends an entry, dropping the oldest entries beyond the configured size.
    /// </summary>
    /// <param name="kind">The event kind.</param>
    /// <param name="user">The user the event concerns.</param>
    /// <param name="item">The item the event concerns.</param>
    /// <param name="detail">A human-readable detail line.</param>
    /// <param name="success">Whether the event represents a success.</param>
    public void Add(string kind, string? user = null, string? item = null, string? detail = null, bool success = true)
    {
        var capacity = Math.Max(0, Plugin.Instance?.Configuration.EventLogSize ?? DefaultCapacity);
        if (capacity == 0)
        {
            return;
        }

        lock (_lock)
        {
            _entries.Enqueue(new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Kind = kind,
                User = user ?? string.Empty,
                Item = item ?? string.Empty,
                Detail = detail ?? string.Empty,
                Success = success,
            });

            while (_entries.Count > capacity)
            {
                _entries.Dequeue();
            }
        }

        ScheduleSave();
    }

    /// <summary>
    /// Returns the stored entries, newest first.
    /// </summary>
    /// <param name="limit">The maximum number of entries to return.</param>
    /// <param name="kind">An optional kind to filter by.</param>
    /// <returns>The matching entries.</returns>
    public IReadOnlyList<LogEntry> GetEntries(int? limit = null, string? kind = null)
    {
        lock (_lock)
        {
            IEnumerable<LogEntry> query = _entries.Reverse();

            if (!string.IsNullOrEmpty(kind))
            {
                query = query.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
            }

            if (limit is > 0)
            {
                query = query.Take(limit.Value);
            }

            return query.ToList();
        }
    }

    /// <summary>
    /// Removes every stored entry.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }

        ScheduleSave();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveTimer.Dispose();
        Save();
    }

    private void ScheduleSave()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down; the final Save in Dispose covers this write.
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<LogEntry>>(json, JsonOptions);
            if (loaded is null)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var entry in loaded)
                {
                    _entries.Enqueue(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the event log at {Path}", _filePath);
        }
    }

    private void Save()
    {
        List<LogEntry> snapshot;
        lock (_lock)
        {
            snapshot = _entries.ToList();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not write the event log to {Path}", _filePath);
        }
    }
}
