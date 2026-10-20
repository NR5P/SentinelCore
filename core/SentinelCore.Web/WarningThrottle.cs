using System.Text.Json;

namespace SentinelCore.Web;

// "At most one warning per RepeatAfter for each thing being warned about", remembered in a file so
// a restart - a deploy restarts the dashboard several times a day - doesn't send the same warning
// again (the box, 2026-10-04: two llama-server memory warnings 24 minutes apart, either side of a
// restart). Shared by MemoryWatch and AiSpeedWatch; each has its own file.
public sealed class WarningThrottle(TimeSpan repeatAfter, ILogger logger, TimeProvider? clock = null, string? statePath = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Dictionary<string, DateTimeOffset>? _lastSent;

    public TimeSpan RepeatAfter => repeatAfter;

    public bool IsDue(string key) =>
        !LastSent.TryGetValue(key, out var sent) || _clock.GetUtcNow() - sent >= repeatAfter;

    // Counted as sent whether or not any channel took it: with no channel set up there is nobody
    // to repeat it to, and the caller's log line is the record.
    public void MarkSent(string key)
    {
        LastSent[key] = _clock.GetUtcNow();
        Save();
    }

    private Dictionary<string, DateTimeOffset> LastSent => _lastSent ??= Load();

    private Dictionary<string, DateTimeOffset> Load()
    {
        var lastSent = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (statePath is null || !File.Exists(statePath)) return lastSent;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(statePath), JsonOptions);
            foreach (var pair in loaded ?? []) lastSent[pair.Key] = pair.Value;
        }
        // A missing or corrupt file costs one repeat of a warning, the right side to fail on.
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't read {Path}; warnings may repeat once", statePath);
        }
        return lastSent;
    }

    private void Save()
    {
        if (statePath is null) return;
        var now = _clock.GetUtcNow();
        foreach (var key in LastSent.Where(pair => now - pair.Value >= repeatAfter).Select(pair => pair.Key).ToList())
            LastSent.Remove(key);
        try
        {
            var directory = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = $"{statePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(LastSent, JsonOptions));
            File.Move(temporary, statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't save {Path}; warnings may repeat after a restart", statePath);
        }
    }
}
