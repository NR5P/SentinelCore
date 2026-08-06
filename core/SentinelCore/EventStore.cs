using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public interface IEventStore
{
    Task RecordAsync(AlertResult result, CancellationToken cancellationToken);

    // Returns completed/failed/queued rows with OccurredAt in [from, to), oldest first.
    Task<IReadOnlyList<AlertEventRecord>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task<AlertEventRecord?> GetEventAsync(Guid id, CancellationToken cancellationToken);

    // Alerts still marked "queued" - meaning the process went down before analysis finished.
    // Read at startup so they can be resumed rather than lingering forever. See AlertRecoveryService.
    Task<IReadOnlyList<PendingAlertRecord>> GetPendingAlertsAsync(CancellationToken cancellationToken);

    // Newest first, with every filter field optional (null/empty means "don't filter on this").
    Task<IReadOnlyList<AlertEventRecord>> SearchEventsAsync(EventFilter filter, CancellationToken cancellationToken);

    // Ranks alerts that have a stored embedding (see ImageEmbeddingService) by cosine similarity
    // to queryEmbedding, applying the same camera/date/notified filters as SearchEventsAsync -
    // filter.SearchText is ignored here, since similarity search replaces literal text matching
    // rather than combining with it. Results are similarity-ranked descending and capped at
    // filter.Limit; rows scoring below minimumSimilarity are excluded entirely.
    Task<IReadOnlyList<AlertEventRecord>> SearchBySimilarityAsync(EventFilter filter, float[] queryEmbedding, double minimumSimilarity, CancellationToken cancellationToken);

    // Distinct camera names and event types actually present in history, for populating
    // filter dropdowns - independent of whatever filter is currently applied.
    Task<EventFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken);
}

public sealed record EventFilter(
    string? Camera = null,
    string? EventType = null,
    bool? NotifiedOnly = null,
    string? SearchText = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 50,
    // Drop rows the AI didn't confirm, in the query rather than after it. Limit is applied by
    // SQLite last, so without this a run of rejected frames longer than Limit fills the whole page
    // and every confirmed event older than that run falls off the end - the list reads as "nothing
    // ever happened" while the rows sit untouched in the table. One night of a camera pointed at a
    // spider web is enough to do it.
    //
    // Safe to push down because ActivityEndpoints.VerdictFor can only return Confirmed for a row
    // whose SubjectConfirmed is true: this is a superset of what survives the merge, never a
    // narrower one, so a row that would have been shown can't be filtered out here. The trigger
    // side already does the same thing (see SqliteTriggerStore.GetRecentAsync).
    bool ConfirmedOnly = false);

public sealed record EventFilterOptions(IReadOnlyList<string> Cameras, IReadOnlyList<string> EventTypes);

// Everything needed to rebuild an IncomingAlert from history, minus the image itself, which is
// read back off disk. ContentType and OriginalFileName aren't stored - the former is inferred
// from the file extension, and nothing downstream reads the latter.
public sealed record PendingAlertRecord(
    Guid Id,
    string Camera,
    string? BlueIrisAlertId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string Profile,
    string Model,
    string? ImagePath);

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
    string? ImagePath,
    string? BlueIrisAlertId = null,
    // See IncomingAlert.LinkedCameras/IncidentId - only ever populated for a trigger-zone
    // camera that belongs to a Camera Group.
    string? LinkedCameras = null,
    string? IncidentId = null,
    // See VisionAnalysis.PackageTheftReason - only ever populated by Package Theft Watch on the
    // alert where a watched package was observed gone with nobody recognized handling it.
    string? PackageTheftReason = null,
    // Whether the AI actually found something in this frame - the same
    // AlertNotificationPolicy.HasVisibleSubject rule that decides a motion trigger's fate, stored
    // here so the dashboard can apply it to an alert that has no trigger behind it (Blue Iris, a
    // gate check, the trigger API) without re-reading the analysis JSON off disk.
    //
    // Three-valued on purpose: true = the AI looked and found a subject, false = the AI looked and
    // found nothing, null = no verdict exists (still queued, the analysis failed, or the row was
    // written before this column). Only true is treated as "passed the AI check".
    bool? SubjectConfirmed = null);

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

        // Added after the initial release - older databases won't have these columns yet. Same
        // AddColumnIfMissing/ColumnExists pattern already used in CameraStore.cs/TriggerStore.cs.
        AddColumnIfMissing("AlertEvents", "LinkedCameras", "TEXT");
        AddColumnIfMissing("AlertEvents", "IncidentId", "TEXT");
        AddColumnIfMissing("AlertEvents", "PackageTheftReason", "TEXT");
        // A 512-float CLIP vector stored as raw bytes via Buffer.BlockCopy, not base64 text - see
        // ImageEmbeddingService. Only populated when Semantic Search is enabled; a row with no
        // embedding just isn't a candidate for SearchBySimilarityAsync.
        AddColumnIfMissing("AlertEvents", "Embedding", "BLOB");

        // See AlertEventRecord.SubjectConfirmed. Existing completed rows are backfilled to 1 rather
        // than left NULL: the Events tab now hides anything without a positive verdict, and the
        // analyses behind old rows aren't re-readable from here, so leaving them NULL would empty
        // the tab of all history the moment this ships. Fail-open for the past, real verdicts from
        // here on - the same trade TriggerConfirmation.Confirmed already makes for its own column.
        var backfillSubjectConfirmed = !ColumnExists("AlertEvents", "SubjectConfirmed");
        AddColumnIfMissing("AlertEvents", "SubjectConfirmed", "INTEGER");
        if (backfillSubjectConfirmed)
        {
            using var backfill = _connection.CreateCommand();
            backfill.CommandText = "UPDATE AlertEvents SET SubjectConfirmed = 1 WHERE Status = 'completed';";
            backfill.ExecuteNonQuery();
        }

        using var incidentIndex = _connection.CreateCommand();
        incidentIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_AlertEvents_IncidentId ON AlertEvents(IncidentId);";
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

    public async Task RecordAsync(AlertResult result, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AlertEvents
                    (Id, Camera, BlueIrisAlertId, OccurredAt, ReceivedAt, CompletedAt, Profile, Model, Status,
                     ImagePath, EventType, Summary, NotificationRecommended, NotificationReason, Confidence, Error,
                     LinkedCameras, IncidentId, PackageTheftReason, Embedding, SubjectConfirmed)
                VALUES
                    ($id, $camera, $blueIrisAlertId, $occurredAt, $receivedAt, $completedAt, $profile, $model, $status,
                     $imagePath, $eventType, $summary, $notificationRecommended, $notificationReason, $confidence, $error,
                     $linkedCameras, $incidentId, $packageTheftReason, $embedding, $subjectConfirmed)
                ON CONFLICT(Id) DO UPDATE SET
                    CompletedAt = excluded.CompletedAt,
                    Model = excluded.Model,
                    Status = excluded.Status,
                    ImagePath = excluded.ImagePath,
                    EventType = excluded.EventType,
                    Summary = excluded.Summary,
                    NotificationRecommended = excluded.NotificationRecommended,
                    NotificationReason = excluded.NotificationReason,
                    Confidence = excluded.Confidence,
                    Error = excluded.Error,
                    -- COALESCE, not a straight overwrite: these three describe where the alert came
                    -- from, and several writers re-save a row without them. AlertRecoveryService
                    -- rebuilds an IncomingAlert from PendingAlertRecord (which carries neither
                    -- LinkedCameras nor IncidentId), and /api/queue/clear marks rows cancelled the
                    -- same way - so a plain overwrite silently unlinked a camera-group incident
                    -- from its members every time the service restarted mid-analysis.
                    LinkedCameras = COALESCE(excluded.LinkedCameras, AlertEvents.LinkedCameras),
                    IncidentId = COALESCE(excluded.IncidentId, AlertEvents.IncidentId),
                    PackageTheftReason = excluded.PackageTheftReason,
                    Embedding = COALESCE(excluded.Embedding, AlertEvents.Embedding),
                    SubjectConfirmed = excluded.SubjectConfirmed;
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
            command.Parameters.AddWithValue("$linkedCameras", (object?)result.LinkedCameras ?? DBNull.Value);
            command.Parameters.AddWithValue("$incidentId", (object?)result.IncidentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$packageTheftReason", (object?)result.Analysis?.PackageTheftReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$embedding", result.Embedding is { } embedding ? FloatsToBytes(embedding) : DBNull.Value);
            command.Parameters.AddWithValue(
                "$subjectConfirmed",
                AlertNotificationPolicy.AnalysisFoundSubject(result) is { } found ? found ? 1 : 0 : DBNull.Value);

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
                       NotificationRecommended, NotificationReason, Confidence, ImagePath,
                       BlueIrisAlertId, LinkedCameras, IncidentId, PackageTheftReason, SubjectConfirmed
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

    public async Task<IReadOnlyList<PendingAlertRecord>> GetPendingAlertsAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Id, Camera, BlueIrisAlertId, OccurredAt, ReceivedAt, Profile, Model, ImagePath
                FROM AlertEvents
                WHERE Status = 'queued'
                ORDER BY ReceivedAt ASC;
                """;

            var results = new List<PendingAlertRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new PendingAlertRecord(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3)).ToLocalTime(),
                    DateTimeOffset.Parse(reader.GetString(4)).ToLocalTime(),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

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
                       NotificationRecommended, NotificationReason, Confidence, ImagePath,
                       BlueIrisAlertId, LinkedCameras, IncidentId, PackageTheftReason, SubjectConfirmed
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

    public async Task<IReadOnlyList<AlertEventRecord>> SearchEventsAsync(EventFilter filter, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            var conditions = new List<string>();

            if (!string.IsNullOrWhiteSpace(filter.Camera))
            {
                conditions.Add("Camera = $camera");
                command.Parameters.AddWithValue("$camera", filter.Camera);
            }
            if (!string.IsNullOrWhiteSpace(filter.EventType))
            {
                conditions.Add("EventType = $eventType");
                command.Parameters.AddWithValue("$eventType", filter.EventType);
            }
            if (filter.NotifiedOnly == true)
            {
                conditions.Add("NotificationRecommended = 1");
            }
            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                conditions.Add("Summary LIKE $search");
                command.Parameters.AddWithValue("$search", $"%{filter.SearchText}%");
            }
            if (filter.From.HasValue)
            {
                conditions.Add("OccurredAt >= $from");
                command.Parameters.AddWithValue("$from", ToUtcSortable(filter.From.Value));
            }
            if (filter.To.HasValue)
            {
                conditions.Add("OccurredAt < $to");
                command.Parameters.AddWithValue("$to", ToUtcSortable(filter.To.Value));
            }
            if (filter.ConfirmedOnly)
            {
                conditions.Add("SubjectConfirmed = 1");
            }

            var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            command.CommandText = $"""
                SELECT Id, Camera, OccurredAt, Status, EventType, Summary,
                       NotificationRecommended, NotificationReason, Confidence, ImagePath,
                       BlueIrisAlertId, LinkedCameras, IncidentId, PackageTheftReason, SubjectConfirmed
                FROM AlertEvents
                {whereClause}
                ORDER BY OccurredAt DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(filter.Limit, 1, 500));

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

    // Bounds how many candidate rows get pulled into memory for ranking before ORDER BY OccurredAt
    // even applies filter.Limit - similarity ranking can't happen in SQL, so this is the app-level
    // stand-in for an index. A personal camera history fits well within this comfortably; a linear
    // scan of a few thousand 512-float vectors is a few milliseconds of dot products, the same
    // reasoning KnownFaceMatcher already relies on for face matching.
    private const int SimilarityCandidatePoolSize = 5000;

    public async Task<IReadOnlyList<AlertEventRecord>> SearchBySimilarityAsync(EventFilter filter, float[] queryEmbedding, double minimumSimilarity, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            var conditions = new List<string> { "Embedding IS NOT NULL" };

            if (!string.IsNullOrWhiteSpace(filter.Camera))
            {
                conditions.Add("Camera = $camera");
                command.Parameters.AddWithValue("$camera", filter.Camera);
            }
            if (!string.IsNullOrWhiteSpace(filter.EventType))
            {
                conditions.Add("EventType = $eventType");
                command.Parameters.AddWithValue("$eventType", filter.EventType);
            }
            if (filter.NotifiedOnly == true)
            {
                conditions.Add("NotificationRecommended = 1");
            }
            if (filter.From.HasValue)
            {
                conditions.Add("OccurredAt >= $from");
                command.Parameters.AddWithValue("$from", ToUtcSortable(filter.From.Value));
            }
            if (filter.To.HasValue)
            {
                conditions.Add("OccurredAt < $to");
                command.Parameters.AddWithValue("$to", ToUtcSortable(filter.To.Value));
            }
            if (filter.ConfirmedOnly)
            {
                conditions.Add("SubjectConfirmed = 1");
            }

            command.CommandText = $"""
                SELECT Id, Camera, OccurredAt, Status, EventType, Summary,
                       NotificationRecommended, NotificationReason, Confidence, ImagePath,
                       BlueIrisAlertId, LinkedCameras, IncidentId, PackageTheftReason, SubjectConfirmed, Embedding
                FROM AlertEvents
                WHERE {string.Join(" AND ", conditions)}
                ORDER BY OccurredAt DESC
                LIMIT $candidatePoolSize;
                """;
            command.Parameters.AddWithValue("$candidatePoolSize", SimilarityCandidatePoolSize);

            var ranked = new List<(AlertEventRecord Record, double Similarity)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var embedding = BytesToFloats((byte[])reader["Embedding"]);
                var similarity = CosineSimilarity(embedding, queryEmbedding);
                if (similarity < minimumSimilarity) continue;
                ranked.Add((ReadRecord(reader), similarity));
            }

            return ranked
                .OrderByDescending(r => r.Similarity)
                .Take(Math.Clamp(filter.Limit, 1, 500))
                .Select(r => r.Record)
                .ToList();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<EventFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            var cameras = new List<string>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT Camera FROM AlertEvents ORDER BY Camera COLLATE NOCASE;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    cameras.Add(reader.GetString(0));
            }

            var eventTypes = new List<string>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT DISTINCT EventType FROM AlertEvents
                    WHERE EventType IS NOT NULL AND EventType <> ''
                    ORDER BY EventType COLLATE NOCASE;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    eventTypes.Add(reader.GetString(0));
            }

            return new EventFilterOptions(cameras, eventTypes);
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
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetInt64(14) != 0);

    private static string ToUtcSortable(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static byte[] FloatsToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    // Duplicated from KnownFaceMatcher's cosine similarity rather than shared - that one operates
    // on float[] face embeddings with a face-specific similarity threshold baked into its own
    // call sites; this one is float[] CLIP embeddings with entirely different score semantics.
    // Keeping them separate avoids coupling two unrelated matching concerns to one signature.
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    public void Dispose()
    {
        _connectionLock.Dispose();
        _connection.Dispose();
    }
}
