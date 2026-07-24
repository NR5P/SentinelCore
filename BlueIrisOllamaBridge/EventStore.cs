using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public interface IEventStore
{
    Task RecordAsync(AlertResult result, CancellationToken cancellationToken);

    // Returns completed/failed/queued rows with OccurredAt in [from, to), oldest first.
    Task<IReadOnlyList<AlertEventRecord>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    // Returns the most recent rows regardless of date, newest first.
    Task<IReadOnlyList<AlertEventRecord>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken);

    Task<AlertEventRecord?> GetEventAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record AlertEventRecord(
    Guid Id,
    string Camera,
    DateTimeOffset OccurredAt,
    string Status,
    string? EventType,
    string? Summary,
    bool? NotificationRecommended,
    string? NotificationReason,
    double? Confidence,
    string? ImagePath);

// SQLite-backed history of every alert (queued/completed/failed), one row per alert id,
// updated in place as the alert progresses. This is a queryable companion to the JSON
// files FileResultStore writes, not a replacement for them.
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public SqliteEventStore(IOptions<BridgeOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "events.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AlertEvents (
                Id TEXT PRIMARY KEY,
                Camera TEXT NOT NULL,
                BlueIrisAlertId TEXT,
                OccurredAt TEXT NOT NULL,
                ReceivedAt TEXT NOT NULL,
                CompletedAt TEXT,
                Profile TEXT NOT NULL,
                Model TEXT NOT NULL,
                Status TEXT NOT NULL,
                ImagePath TEXT,
                EventType TEXT,
                Summary TEXT,
                NotificationRecommended INTEGER,
                NotificationReason TEXT,
                Confidence REAL,
                Error TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_AlertEvents_OccurredAt ON AlertEvents(OccurredAt);
            CREATE INDEX IF NOT EXISTS IX_AlertEvents_Camera ON AlertEvents(Camera);
            """;
        command.ExecuteNonQuery();
    }

    public async Task RecordAsync(AlertResult result, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AlertEvents
                    (Id, Camera, BlueIrisAlertId, OccurredAt, ReceivedAt, CompletedAt, Profile, Model, Status,
                     ImagePath, EventType, Summary, NotificationRecommended, NotificationReason, Confidence, Error)
                VALUES
                    ($id, $camera, $blueIrisAlertId, $occurredAt, $receivedAt, $completedAt, $profile, $model, $status,
                     $imagePath, $eventType, $summary, $notificationRecommended, $notificationReason, $confidence, $error)
                ON CONFLICT(Id) DO UPDATE SET
                    CompletedAt = excluded.CompletedAt,
                    Status = excluded.Status,
                    ImagePath = excluded.ImagePath,
                    EventType = excluded.EventType,
                    Summary = excluded.Summary,
                    NotificationRecommended = excluded.NotificationRecommended,
                    NotificationReason = excluded.NotificationReason,
                    Confidence = excluded.Confidence,
                    Error = excluded.Error;
                """;

            command.Parameters.AddWithValue("$id", result.Id.ToString("N"));
            command.Parameters.AddWithValue("$camera", result.Camera);
            command.Parameters.AddWithValue("$blueIrisAlertId", (object?)result.BlueIrisAlertId ?? DBNull.Value);
            command.Parameters.AddWithValue("$occurredAt", ToUtcSortable(result.OccurredAt));
            command.Parameters.AddWithValue("$receivedAt", ToUtcSortable(result.ReceivedAt));
            command.Parameters.AddWithValue("$completedAt", result.CompletedAt.HasValue ? ToUtcSortable(result.CompletedAt.Value) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$profile", result.Profile);
            command.Parameters.AddWithValue("$model", result.Model);
            command.Parameters.AddWithValue("$status", result.Status);
            command.Parameters.AddWithValue("$imagePath", (object?)result.ImagePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$eventType", (object?)result.Analysis?.EventType ?? DBNull.Value);
            command.Parameters.AddWithValue("$summary", (object?)result.Analysis?.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$notificationRecommended",
                result.Analysis is null ? DBNull.Value : result.Analysis.NotificationRecommended ? 1 : 0);
            command.Parameters.AddWithValue("$notificationReason", (object?)result.Analysis?.NotificationReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$confidence", result.Analysis is null ? DBNull.Value : result.Analysis.Confidence);
            command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<IReadOnlyList<AlertEventRecord>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Id, Camera, OccurredAt, Status, EventType, Summary,
                       NotificationRecommended, NotificationReason, Confidence, ImagePath
                FROM AlertEvents
                WHERE OccurredAt >= $from AND OccurredAt < $to
                ORDER BY OccurredAt ASC;
                """;
            command.Parameters.AddWithValue("$from", ToUtcSortable(from));
            command.Parameters.AddWithValue("$to", ToUtcSortable(to));

            var results = new List<AlertEventRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadRecord(reader));

            return results;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<IReadOnlyList<AlertEventRecord>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Id, Camera, OccurredAt, Status, EventType, Summary,
                       NotificationRecommended, NotificationReason, Confidence, ImagePath
                FROM AlertEvents
                ORDER BY OccurredAt DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            var results = new List<AlertEventRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadRecord(reader));

            return results;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<AlertEventRecord?> GetEventAsync(Guid id, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Id, Camera, OccurredAt, Status, EventType, Summary,
                       NotificationRecommended, NotificationReason, Confidence, ImagePath
                FROM AlertEvents
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static AlertEventRecord ReadRecord(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        DateTimeOffset.Parse(reader.GetString(2)).ToLocalTime(),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6) != 0,
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetDouble(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));

    private static string ToUtcSortable(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    public void Dispose()
    {
        _connectionLock.Dispose();
        _connection.Dispose();
    }
}
