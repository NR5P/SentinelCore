using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BlueIrisOllamaBridge.Web;

// Trigger zones are a separate, isolated alert pipeline for the standalone cameras added in this
// dashboard - own database, own image folder, no relation to Blue Iris's events.db or the main
// alert pipeline. Detection is a plain frame-diff (see MotionDetector) with no AI involvement.
public sealed record TriggerPoint(double X, double Y);

public sealed record TriggerConfig(
    Guid CameraId,
    bool Enabled,
    IReadOnlyList<TriggerPoint> ZonePolygon,
    double MinTriggerArea,
    int CooldownSeconds,
    int MaxTriggersPerMinute)
{
    public static TriggerConfig Disabled(Guid cameraId) =>
        new(cameraId, false, [], 0.02, 5, 6);
}

public sealed record TriggerEvent(
    Guid Id,
    Guid CameraId,
    DateTimeOffset OccurredAt,
    string ImagePath,
    double TriggerAreaFraction);

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
    }

    public async Task<TriggerConfig?> GetAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute
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
                SELECT CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute
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
                INSERT INTO TriggerConfigs (CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute)
                VALUES ($cameraId, $enabled, $zonePolygon, $minTriggerArea, $cooldownSeconds, $maxTriggersPerMinute)
                ON CONFLICT(CameraId) DO UPDATE SET
                    Enabled = excluded.Enabled,
                    ZonePolygon = excluded.ZonePolygon,
                    MinTriggerArea = excluded.MinTriggerArea,
                    CooldownSeconds = excluded.CooldownSeconds,
                    MaxTriggersPerMinute = excluded.MaxTriggersPerMinute;
                """;
            command.Parameters.AddWithValue("$cameraId", config.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$enabled", config.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$zonePolygon", JsonSerializer.Serialize(config.ZonePolygon));
            command.Parameters.AddWithValue("$minTriggerArea", config.MinTriggerArea);
            command.Parameters.AddWithValue("$cooldownSeconds", config.CooldownSeconds);
            command.Parameters.AddWithValue("$maxTriggersPerMinute", config.MaxTriggersPerMinute);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TriggerEvent> AddAsync(Guid cameraId, string imagePath, double triggerAreaFraction, CancellationToken cancellationToken)
    {
        var trigger = new TriggerEvent(Guid.NewGuid(), cameraId, DateTimeOffset.UtcNow, imagePath, triggerAreaFraction);

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
                ? "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction FROM TriggerEvents ORDER BY OccurredAt DESC LIMIT $limit;"
                : "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction FROM TriggerEvents WHERE CameraId = $cameraId ORDER BY OccurredAt DESC LIMIT $limit;";
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

    async Task<TriggerEvent?> ITriggerEventStore.GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction FROM TriggerEvents WHERE Id = $id;";
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
        (int)reader.GetInt64(5));

    private static TriggerEvent ReadEvent(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        Guid.ParseExact(reader.GetString(1), "N"),
        DateTimeOffset.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetDouble(4));

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
