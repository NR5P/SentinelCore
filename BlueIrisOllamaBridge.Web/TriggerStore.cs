using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BlueIrisOllamaBridge.Web;

// Trigger zones are their own detection pipeline for the standalone cameras added in this
// dashboard - own database, own image folder, and motion detection here has nothing to do
// with Blue Iris (see MotionDetector - plain frame-diff, no AI). What happens *after* a
// trigger fires is opt-in per camera: with SendToAlerts on, the snapshot is forwarded to the
// main app's own alert pipeline (same Ollama analysis, same prompt, same email/Telegram/webhook
// dispatch Blue Iris alerts already use) instead of just sitting in the Triggers tab - see
// TriggerZoneMonitor.ForwardToAlertPipelineAsync.
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
    bool SendToAlerts)
{
    public static TriggerConfig Disabled(Guid cameraId) =>
        new(cameraId, false, [], 0.02, 5, 6, 50, false, 5, 10, false);
}

public sealed record TriggerEvent(
    Guid Id,
    Guid CameraId,
    DateTimeOffset OccurredAt,
    string ImagePath,
    double TriggerAreaFraction,
    string? ClipPath);

public interface ITriggerConfigStore
{
    Task<TriggerConfig?> GetAsync(Guid cameraId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TriggerConfig>> GetEnabledAsync(CancellationToken cancellationToken);
    Task SaveAsync(TriggerConfig config, CancellationToken cancellationToken);
}

public interface ITriggerEventStore
{
    Task<TriggerEvent> AddAsync(Guid cameraId, string imagePath, double triggerAreaFraction, CancellationToken cancellationToken);
    Task<IReadOnlyList<TriggerEvent>> GetRecentAsync(int limit, Guid? cameraId, CancellationToken cancellationToken);
    Task<TriggerEvent?> GetAsync(Guid id, CancellationToken cancellationToken);

    // Clips can only be cut once the post-roll has actually elapsed, so the event row is written
    // immediately (image first) and the clip path is patched in afterwards.
    Task SetClipPathAsync(Guid id, string clipPath, CancellationToken cancellationToken);
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
                       RecordClips, ClipPreSeconds, ClipPostSeconds, SendToAlerts
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
                       RecordClips, ClipPreSeconds, ClipPostSeconds, SendToAlerts
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

    public async Task SaveAsync(TriggerConfig config, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TriggerConfigs (CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute, ShadowSensitivity,
                                            RecordClips, ClipPreSeconds, ClipPostSeconds, SendToAlerts)
                VALUES ($cameraId, $enabled, $zonePolygon, $minTriggerArea, $cooldownSeconds, $maxTriggersPerMinute, $shadowSensitivity,
                        $recordClips, $clipPreSeconds, $clipPostSeconds, $sendToAlerts)
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
                    SendToAlerts = excluded.SendToAlerts;
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
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TriggerEvent> AddAsync(Guid cameraId, string imagePath, double triggerAreaFraction, CancellationToken cancellationToken)
    {
        var trigger = new TriggerEvent(Guid.NewGuid(), cameraId, DateTimeOffset.UtcNow, imagePath, triggerAreaFraction, null);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TriggerEvents (Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction)
                VALUES ($id, $cameraId, $occurredAt, $imagePath, $triggerAreaFraction);
                """;
            command.Parameters.AddWithValue("$id", trigger.Id.ToString("N"));
            command.Parameters.AddWithValue("$cameraId", trigger.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$occurredAt", trigger.OccurredAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$imagePath", trigger.ImagePath);
            command.Parameters.AddWithValue("$triggerAreaFraction", trigger.TriggerAreaFraction);
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
                ? "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath FROM TriggerEvents ORDER BY OccurredAt DESC LIMIT $limit;"
                : "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath FROM TriggerEvents WHERE CameraId = $cameraId ORDER BY OccurredAt DESC LIMIT $limit;";
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
            command.CommandText = "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath FROM TriggerEvents WHERE Id = $id;";
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
        reader.GetInt64(10) != 0);

    private static TriggerEvent ReadEvent(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        Guid.ParseExact(reader.GetString(1), "N"),
        DateTimeOffset.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetDouble(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
