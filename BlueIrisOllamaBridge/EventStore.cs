using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public interface IEventStore
{
    Task RecordAsync(AlertResult result, CancellationToken cancellationToken);
}

// SQLite-backed history of every alert (queued/completed/failed), one row per alert id,
// updated in place as the alert progresses. This is a queryable companion to the JSON
// files FileResultStore writes, not a replacement for them.
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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
        await _writeLock.WaitAsync(cancellationToken);
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
            command.Parameters.AddWithValue("$occurredAt", result.OccurredAt.ToString("O"));
            command.Parameters.AddWithValue("$receivedAt", result.ReceivedAt.ToString("O"));
            command.Parameters.AddWithValue("$completedAt", (object?)result.CompletedAt?.ToString("O") ?? DBNull.Value);
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
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _connection.Dispose();
    }
}
