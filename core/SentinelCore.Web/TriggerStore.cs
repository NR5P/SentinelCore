using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// Trigger zones are their own detection pipeline for the standalone cameras added in this
// dashboard - own database, own image folder, and motion detection here has nothing to do
// with Blue Iris (see MotionDetector - plain frame-diff, no AI). What happens *after* a
// trigger fires is opt-in per camera: with SendToAlerts on, the snapshot is forwarded to the
// main app's own alert pipeline (same Ollama analysis, the chosen prompt profile, same
// email/Telegram/webhook dispatch Blue Iris alerts already use) instead of just sitting in
// the Triggers tab - see TriggerZoneMonitor.ForwardToAlertPipelineAsync.
public sealed record TriggerPoint(double X, double Y);

public sealed record TriggerConfig(
    Guid CameraId,
    bool Enabled,
    IReadOnlyList<TriggerPoint> ZonePolygon,
    double MinTriggerArea,
    int CooldownSeconds,
    int MaxTriggersPerMinute,
    int ShadowSensitivity,
    bool RecordClips,
    int ClipPreSeconds,
    int ClipPostSeconds,
    bool SendToAlerts,
    string AlertProfile,
    // A Device Hub sensor (a proximity/motion/contact sensor on the MQTT broker) that fires this
    // camera when it reports - see SensorTriggerWatcher. Empty means no sensor is bound, and it's
    // deliberately independent of Enabled above: Enabled is about watching the video for motion,
    // and a camera can perfectly well have no zone at all and still be fired by a sensor.
    // Stored as the device's id in string form because the devices themselves live in the main
    // app's database, not this one - there's no foreign key to point at from here.
    string SensorDeviceId = "",
    // Which reported state fires the camera: "on" (the usual - motion detected, contact opened),
    // "off", or "any" for a sensor whose meaning is the change itself.
    string SensorTriggerOn = "on",
    // Separate from the zone's own CooldownSeconds: a sensor's sensible rate is a property of the
    // sensor (a driveway loop trips once per car, a PIR trips constantly), not of the video.
    int SensorCooldownSeconds = 30)
{
    public static TriggerConfig Disabled(Guid cameraId) =>
        new(cameraId, false, [], 0.02, 5, 6, 50, false, 5, 10, false, "default");

    public bool HasSensorBinding => !string.IsNullOrWhiteSpace(SensorDeviceId);
}

public sealed record TriggerEvent(
    Guid Id,
    Guid CameraId,
    DateTimeOffset OccurredAt,
    string ImagePath,
    double TriggerAreaFraction,
    string? ClipPath,
    // Shared by every event (across one or more cameras) that belongs to the same camera-group
    // incident - see TriggerZoneMonitor.MaybeTriggerAsync. Null for a camera with no group.
    string? IncidentId = null);

public interface ITriggerConfigStore
{
    Task<TriggerConfig?> GetAsync(Guid cameraId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TriggerConfig>> GetEnabledAsync(CancellationToken cancellationToken);

    // Unlike GetEnabledAsync, includes disabled zones too - used for config export, where
    // silently dropping a disabled zone would mean it never comes back on restore.
    Task<IReadOnlyList<TriggerConfig>> GetAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(TriggerConfig config, CancellationToken cancellationToken);
}

public interface ITriggerEventStore
{
    Task<TriggerEvent> AddAsync(Guid cameraId, string imagePath, double triggerAreaFraction, string? incidentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TriggerEvent>> GetRecentAsync(int limit, Guid? cameraId, CancellationToken cancellationToken);
    Task<TriggerEvent?> GetAsync(Guid id, CancellationToken cancellationToken);

    // Powers the day timeline: this camera's local trigger events (and their clips, once built)
    // that fall within a specific day, so they can be matched up against AI-confirmed events.
    Task<IReadOnlyList<TriggerEvent>> GetEventsInRangeAsync(Guid cameraId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);

    // Clips can only be cut once the post-roll has actually elapsed, so the event row is written
    // immediately (image first) and the clip path is patched in afterwards.
    Task SetClipPathAsync(Guid id, string clipPath, CancellationToken cancellationToken);

    // Decides whether a fresh trigger should join an already-open incident: the most recent
    // IncidentId among any of these camera ids triggered within the window, or null if none has.
    // Used by TriggerZoneMonitor to reuse vs. mint an IncidentId when a group member fires.
    Task<string?> GetMostRecentIncidentIdAsync(IReadOnlyList<Guid> cameraIds, DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed class SqliteTriggerStore : ITriggerConfigStore, ITriggerEventStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteTriggerStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "triggers.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TriggerConfigs (
                CameraId TEXT PRIMARY KEY,
                Enabled INTEGER NOT NULL,
                ZonePolygon TEXT NOT NULL,
                MinTriggerArea REAL NOT NULL,
                CooldownSeconds INTEGER NOT NULL,
                MaxTriggersPerMinute INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS TriggerEvents (
                Id TEXT PRIMARY KEY,
                CameraId TEXT NOT NULL,
                OccurredAt TEXT NOT NULL,
                ImagePath TEXT NOT NULL,
                TriggerAreaFraction REAL NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_TriggerEvents_OccurredAt ON TriggerEvents (OccurredAt DESC);
            """;
        command.ExecuteNonQuery();

        // Added after the initial release - older databases won't have these columns yet.
        AddColumnIfMissing("TriggerConfigs", "ShadowSensitivity", "INTEGER NOT NULL DEFAULT 50");
        AddColumnIfMissing("TriggerConfigs", "RecordClips", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("TriggerConfigs", "ClipPreSeconds", "INTEGER NOT NULL DEFAULT 5");
        AddColumnIfMissing("TriggerConfigs", "ClipPostSeconds", "INTEGER NOT NULL DEFAULT 10");
        AddColumnIfMissing("TriggerEvents", "ClipPath", "TEXT");
        AddColumnIfMissing("TriggerConfigs", "SendToAlerts", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("TriggerConfigs", "AlertProfile", "TEXT NOT NULL DEFAULT 'default'");
        AddColumnIfMissing("TriggerEvents", "IncidentId", "TEXT");
        AddColumnIfMissing("TriggerConfigs", "SensorDeviceId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("TriggerConfigs", "SensorTriggerOn", "TEXT NOT NULL DEFAULT 'on'");
        AddColumnIfMissing("TriggerConfigs", "SensorCooldownSeconds", "INTEGER NOT NULL DEFAULT 30");

        using var incidentIndex = _connection.CreateCommand();
        incidentIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_TriggerEvents_IncidentId ON TriggerEvents (IncidentId);";
        incidentIndex.ExecuteNonQuery();
    }

    private void AddColumnIfMissing(string table, string column, string definition)
    {
        if (ColumnExists(table, column)) return;

        using var migrate = _connection.CreateCommand();
        migrate.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        migrate.ExecuteNonQuery();
    }

    private bool ColumnExists(string table, string column)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public async Task<TriggerConfig?> GetAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute, ShadowSensitivity,
                       RecordClips, ClipPreSeconds, ClipPostSeconds, SendToAlerts, AlertProfile,
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds
                FROM TriggerConfigs WHERE CameraId = $cameraId;
                """;
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadConfig(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TriggerConfig>> GetEnabledAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute, ShadowSensitivity,
                       RecordClips, ClipPreSeconds, ClipPostSeconds, SendToAlerts, AlertProfile,
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds
                FROM TriggerConfigs WHERE Enabled = 1;
                """;

            var results = new List<TriggerConfig>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadConfig(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TriggerConfig>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute, ShadowSensitivity,
                       RecordClips, ClipPreSeconds, ClipPostSeconds, SendToAlerts, AlertProfile,
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds
                FROM TriggerConfigs;
                """;

            var results = new List<TriggerConfig>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadConfig(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(TriggerConfig config, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TriggerConfigs (CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute, ShadowSensitivity,
                                            RecordClips, ClipPreSeconds, ClipPostSeconds, SendToAlerts, AlertProfile,
                                            SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds)
                VALUES ($cameraId, $enabled, $zonePolygon, $minTriggerArea, $cooldownSeconds, $maxTriggersPerMinute, $shadowSensitivity,
                        $recordClips, $clipPreSeconds, $clipPostSeconds, $sendToAlerts, $alertProfile,
                        $sensorDeviceId, $sensorTriggerOn, $sensorCooldownSeconds)
                ON CONFLICT(CameraId) DO UPDATE SET
                    Enabled = excluded.Enabled,
                    ZonePolygon = excluded.ZonePolygon,
                    MinTriggerArea = excluded.MinTriggerArea,
                    CooldownSeconds = excluded.CooldownSeconds,
                    MaxTriggersPerMinute = excluded.MaxTriggersPerMinute,
                    ShadowSensitivity = excluded.ShadowSensitivity,
                    RecordClips = excluded.RecordClips,
                    ClipPreSeconds = excluded.ClipPreSeconds,
                    ClipPostSeconds = excluded.ClipPostSeconds,
                    SendToAlerts = excluded.SendToAlerts,
                    AlertProfile = excluded.AlertProfile,
                    SensorDeviceId = excluded.SensorDeviceId,
                    SensorTriggerOn = excluded.SensorTriggerOn,
                    SensorCooldownSeconds = excluded.SensorCooldownSeconds;
                """;
            command.Parameters.AddWithValue("$cameraId", config.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$enabled", config.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$zonePolygon", JsonSerializer.Serialize(config.ZonePolygon));
            command.Parameters.AddWithValue("$minTriggerArea", config.MinTriggerArea);
            command.Parameters.AddWithValue("$cooldownSeconds", config.CooldownSeconds);
            command.Parameters.AddWithValue("$maxTriggersPerMinute", config.MaxTriggersPerMinute);
            command.Parameters.AddWithValue("$shadowSensitivity", config.ShadowSensitivity);
            command.Parameters.AddWithValue("$recordClips", config.RecordClips ? 1 : 0);
            command.Parameters.AddWithValue("$clipPreSeconds", config.ClipPreSeconds);
            command.Parameters.AddWithValue("$clipPostSeconds", config.ClipPostSeconds);
            command.Parameters.AddWithValue("$sendToAlerts", config.SendToAlerts ? 1 : 0);
            command.Parameters.AddWithValue("$alertProfile", config.AlertProfile);
            command.Parameters.AddWithValue("$sensorDeviceId", config.SensorDeviceId);
            command.Parameters.AddWithValue("$sensorTriggerOn", config.SensorTriggerOn);
            command.Parameters.AddWithValue("$sensorCooldownSeconds", config.SensorCooldownSeconds);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TriggerEvent> AddAsync(Guid cameraId, string imagePath, double triggerAreaFraction, string? incidentId, CancellationToken cancellationToken)
    {
        var trigger = new TriggerEvent(Guid.NewGuid(), cameraId, DateTimeOffset.UtcNow, imagePath, triggerAreaFraction, null, incidentId);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TriggerEvents (Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, IncidentId)
                VALUES ($id, $cameraId, $occurredAt, $imagePath, $triggerAreaFraction, $incidentId);
                """;
            command.Parameters.AddWithValue("$id", trigger.Id.ToString("N"));
            command.Parameters.AddWithValue("$cameraId", trigger.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$occurredAt", trigger.OccurredAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$imagePath", trigger.ImagePath);
            command.Parameters.AddWithValue("$triggerAreaFraction", trigger.TriggerAreaFraction);
            command.Parameters.AddWithValue("$incidentId", (object?)trigger.IncidentId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        return trigger;
    }

    public async Task<IReadOnlyList<TriggerEvent>> GetRecentAsync(int limit, Guid? cameraId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = cameraId is null
                ? "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId FROM TriggerEvents ORDER BY OccurredAt DESC LIMIT $limit;"
                : "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId FROM TriggerEvents WHERE CameraId = $cameraId ORDER BY OccurredAt DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            if (cameraId is not null) command.Parameters.AddWithValue("$cameraId", cameraId.Value.ToString("N"));

            var results = new List<TriggerEvent>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadEvent(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TriggerEvent>> GetEventsInRangeAsync(Guid cameraId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId
                FROM TriggerEvents
                WHERE CameraId = $cameraId AND OccurredAt >= $start AND OccurredAt < $end
                ORDER BY OccurredAt ASC;
                """;
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));
            command.Parameters.AddWithValue("$start", start.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$end", end.UtcDateTime.ToString("O"));

            var results = new List<TriggerEvent>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadEvent(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetClipPathAsync(Guid id, string clipPath, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE TriggerEvents SET ClipPath = $clipPath WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$clipPath", clipPath);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task<TriggerEvent?> ITriggerEventStore.GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId FROM TriggerEvents WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static TriggerConfig ReadConfig(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetInt64(1) != 0,
        JsonSerializer.Deserialize<IReadOnlyList<TriggerPoint>>(reader.GetString(2)) ?? [],
        reader.GetDouble(3),
        (int)reader.GetInt64(4),
        (int)reader.GetInt64(5),
        (int)reader.GetInt64(6),
        reader.GetInt64(7) != 0,
        (int)reader.GetInt64(8),
        (int)reader.GetInt64(9),
        reader.GetInt64(10) != 0,
        reader.GetString(11),
        reader.GetString(12),
        reader.GetString(13),
        (int)reader.GetInt64(14));

    private static TriggerEvent ReadEvent(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        Guid.ParseExact(reader.GetString(1), "N"),
        DateTimeOffset.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetDouble(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6));

    public async Task<string?> GetMostRecentIncidentIdAsync(IReadOnlyList<Guid> cameraIds, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (cameraIds.Count == 0) return null;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            var placeholders = string.Join(", ", cameraIds.Select((_, i) => $"$camera{i}"));
            command.CommandText = $"""
                SELECT IncidentId FROM TriggerEvents
                WHERE CameraId IN ({placeholders}) AND OccurredAt >= $since AND IncidentId IS NOT NULL
                ORDER BY OccurredAt DESC LIMIT 1;
                """;
            for (var i = 0; i < cameraIds.Count; i++)
                command.Parameters.AddWithValue($"$camera{i}", cameraIds[i].ToString("N"));
            command.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O"));

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
