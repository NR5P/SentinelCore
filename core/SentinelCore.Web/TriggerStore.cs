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
//
// That forwarding is also what makes a trigger eligible for AI confirmation (see
// TriggerConfirmation below and TriggerConfirmationService): the analysis it produces is what
// decides whether the trigger was a person or a moth, and a camera that doesn't forward never
// gets one, so its triggers are always shown.
public sealed record TriggerPoint(double X, double Y);

// One drawn area with its own minimum object size. Several of these per camera is the whole point:
// perspective means the same person covers wildly different pixel counts depending on how far away
// they are, so a single threshold can't serve a whole scene. On the gate camera a person at the
// gate measures ~161px on the 320x180 detector grid while the same person in the near driveway is
// several times that - set one floor low enough to see the gate and close-range noise trips it
// constantly; set it high enough to ignore that noise and the gate goes blind.
//
// MinTriggerArea stays a fraction of the WHOLE frame (not of this zone), same as it always was, so
// the numbers mean exactly what they used to and existing configs migrate unchanged.
public sealed record TriggerZone(string Name, IReadOnlyList<TriggerPoint> Polygon, double MinTriggerArea);

public sealed record TriggerConfig(
    Guid CameraId,
    bool Enabled,
    // Legacy mirror of Zones[0] - see ResolvedZones. Kept in the record, the table, and the API
    // payload because the shipped iOS/tvOS/Android builds decode these two fields as required and
    // would throw without them.
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
    int SensorCooldownSeconds = 30,
    // The real zone list, and the source of truth once it's populated. Null on a row written before
    // multi-zone existed (and on anything built by the positional constructors below, which stop
    // short of this argument) - read it through ResolvedZones, never directly.
    //
    // Appended at the end with a default on purpose: this record is constructed positionally in
    // several places and inserting a field mid-list shifts every later argument silently. The
    // default also keeps older backup files deserializing - see BackupEndpoints.
    IReadOnlyList<TriggerZone>? Zones = null)
{
    public const string DefaultZoneName = "Zone 1";

    public static TriggerConfig Disabled(Guid cameraId) =>
        new(cameraId, false, [], 0.02, 5, 6, 50, false, 5, 10, false, "default");

    public bool HasSensorBinding => !string.IsNullOrWhiteSpace(SensorDeviceId);

    // Always at least one zone. A config from before multi-zone (Zones null/empty) reads as a
    // single zone built from the legacy ZonePolygon/MinTriggerArea pair, so every consumer can just
    // iterate and never special-case the migration. Idempotent: re-saving a synthesized zone writes
    // the same polygon and area straight back into the legacy columns.
    public IReadOnlyList<TriggerZone> ResolvedZones =>
        Zones is { Count: > 0 } ? Zones : [new TriggerZone(DefaultZoneName, ZonePolygon, MinTriggerArea)];
}

// Whether the AI has agreed there was actually something here. Motion detection alone can't tell
// a person from a moth on the lens at 3am, so with TriggerConfirmation enabled a trigger stays out
// of the Triggers tab until the alert pipeline's analysis has looked at the frame - see
// TriggerConfirmationService.
//
// The image and the clip are written to disk either way. A rejected trigger is hidden, never
// deleted: the whole point of a filter you can't inspect going wrong is that you don't find out,
// so /api/triggers?includeUnconfirmed=true can still show what was filtered out.
public enum TriggerConfirmation
{
    // Either the AI confirmed a subject, or no verdict was ever expected for this trigger -
    // confirmation is switched off, the camera doesn't forward to the alert pipeline, or the
    // analysis couldn't be reached. Always visible.
    //
    // This is the fail-open value on purpose, and it's the DEFAULT for the migrated column so
    // that turning this feature on doesn't retroactively empty a year of trigger history.
    Confirmed,

    // Forwarded for analysis, no verdict yet. Hidden until it resolves - showing it and then
    // yanking it away a minute later would be worse than a short delay.
    Pending,

    // The AI looked and found no person, vehicle, package, or animal in the frame.
    Rejected
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
    string? IncidentId = null,
    TriggerConfirmation Confirmation = TriggerConfirmation.Confirmed,
    // Which of the camera's zones detected this, for the Triggers tab. Null for a companion
    // capture (no zone detected it - the camera was recorded because a group member fired), for a
    // sensor or manual trigger, and for every row written before multi-zone existed.
    string? ZoneName = null);

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
    // zoneName is trailing and optional so the callers that have no zone to name (companion
    // captures, sensor and manual triggers) are unchanged.
    Task<TriggerEvent> AddAsync(
        Guid cameraId, string imagePath, double triggerAreaFraction, string? incidentId,
        TriggerConfirmation confirmation, CancellationToken cancellationToken, string? zoneName = null);

    // includeUnconfirmed surfaces pending and rejected triggers too, for the Triggers tab's
    // "show unconfirmed" toggle - the only way to see what the AI filter is actually throwing
    // away, and to notice when it's throwing away the wrong things.
    Task<IReadOnlyList<TriggerEvent>> GetRecentAsync(int limit, Guid? cameraId, bool includeUnconfirmed, CancellationToken cancellationToken);
    Task<TriggerEvent?> GetAsync(Guid id, CancellationToken cancellationToken);

    // Powers the day timeline: this camera's local trigger events (and their clips, once built)
    // that fall within a specific day, so they can be matched up against AI-confirmed events.
    // Confirmed-only, like the Triggers tab - a rejected trigger shouldn't clutter the timeline
    // any more than it should the tab.
    Task<IReadOnlyList<TriggerEvent>> GetEventsInRangeAsync(Guid cameraId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);

    // Records the AI's verdict once the analysis for this trigger has finished.
    Task SetConfirmationAsync(Guid id, TriggerConfirmation confirmation, CancellationToken cancellationToken);

    // Triggers left Pending by a restart (the poll that was waiting on their verdict died with
    // the process). Swept to Confirmed on startup rather than left invisible forever - see
    // TriggerConfirmationSweeper.
    Task<IReadOnlyList<Guid>> GetPendingBeforeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

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
        // 'confirmed' rather than 'pending' as the default: every row that predates AI
        // confirmation was recorded under the old always-visible rule, and migrating them to
        // pending would blank the Triggers tab and the timeline on the first run after upgrade.
        AddColumnIfMissing("TriggerEvents", "Confirmation", "TEXT NOT NULL DEFAULT 'confirmed'");
        // Nullable rather than a JSON default: NULL is what marks a row as pre-multi-zone, and
        // TriggerConfig.ResolvedZones turns that into a single zone built from the legacy
        // ZonePolygon/MinTriggerArea columns. An empty-string default would just be a value the
        // deserializer would then have to treat as null anyway.
        AddColumnIfMissing("TriggerConfigs", "Zones", "TEXT");
        AddColumnIfMissing("TriggerEvents", "ZoneName", "TEXT");

        using var incidentIndex = _connection.CreateCommand();
        incidentIndex.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_TriggerEvents_IncidentId ON TriggerEvents (IncidentId);
            CREATE INDEX IF NOT EXISTS IX_TriggerEvents_Confirmation ON TriggerEvents (Confirmation, OccurredAt DESC);
            """;
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
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds, Zones
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
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds, Zones
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
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds, Zones
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
                                            SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds, Zones)
                VALUES ($cameraId, $enabled, $zonePolygon, $minTriggerArea, $cooldownSeconds, $maxTriggersPerMinute, $shadowSensitivity,
                        $recordClips, $clipPreSeconds, $clipPostSeconds, $sendToAlerts, $alertProfile,
                        $sensorDeviceId, $sensorTriggerOn, $sensorCooldownSeconds, $zones)
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
                    SensorCooldownSeconds = excluded.SensorCooldownSeconds,
                    Zones = excluded.Zones;
                """;

            // Zones is the source of truth; the legacy ZonePolygon/MinTriggerArea columns are
            // written as a mirror of the primary zone so that the shipped mobile clients - which
            // only know about those two fields - keep reading and editing something coherent.
            var zones = config.ResolvedZones;
            var primary = zones[0];

            command.Parameters.AddWithValue("$cameraId", config.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$enabled", config.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$zonePolygon", JsonSerializer.Serialize(primary.Polygon));
            command.Parameters.AddWithValue("$minTriggerArea", primary.MinTriggerArea);
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
            command.Parameters.AddWithValue("$zones", JsonSerializer.Serialize(zones));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TriggerEvent> AddAsync(
        Guid cameraId, string imagePath, double triggerAreaFraction, string? incidentId,
        TriggerConfirmation confirmation, CancellationToken cancellationToken, string? zoneName = null)
    {
        var trigger = new TriggerEvent(Guid.NewGuid(), cameraId, DateTimeOffset.UtcNow, imagePath, triggerAreaFraction, null, incidentId, confirmation, zoneName);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TriggerEvents (Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, IncidentId, Confirmation, ZoneName)
                VALUES ($id, $cameraId, $occurredAt, $imagePath, $triggerAreaFraction, $incidentId, $confirmation, $zoneName);
                """;
            command.Parameters.AddWithValue("$id", trigger.Id.ToString("N"));
            command.Parameters.AddWithValue("$cameraId", trigger.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$occurredAt", trigger.OccurredAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$imagePath", trigger.ImagePath);
            command.Parameters.AddWithValue("$triggerAreaFraction", trigger.TriggerAreaFraction);
            command.Parameters.AddWithValue("$incidentId", (object?)trigger.IncidentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$confirmation", ToStorage(confirmation));
            command.Parameters.AddWithValue("$zoneName", (object?)trigger.ZoneName ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        return trigger;
    }

    public async Task SetConfirmationAsync(Guid id, TriggerConfirmation confirmation, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE TriggerEvents SET Confirmation = $confirmation WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$confirmation", ToStorage(confirmation));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> GetPendingBeforeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id FROM TriggerEvents WHERE Confirmation = 'pending' AND OccurredAt < $olderThan;";
            command.Parameters.AddWithValue("$olderThan", olderThan.UtcDateTime.ToString("O"));

            var results = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(Guid.ParseExact(reader.GetString(0), "N"));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TriggerEvent>> GetRecentAsync(int limit, Guid? cameraId, bool includeUnconfirmed, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var filters = new List<string>();
            if (cameraId is not null) filters.Add("CameraId = $cameraId");
            if (!includeUnconfirmed) filters.Add("Confirmation = 'confirmed'");
            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)} ";

            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId, Confirmation, ZoneName " +
                $"FROM TriggerEvents {where}ORDER BY OccurredAt DESC LIMIT $limit;";
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
                SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId, Confirmation, ZoneName
                FROM TriggerEvents
                WHERE CameraId = $cameraId AND OccurredAt >= $start AND OccurredAt < $end AND Confirmation = 'confirmed'
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
            command.CommandText = "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId, Confirmation, ZoneName FROM TriggerEvents WHERE Id = $id;";
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
        (int)reader.GetInt64(14),
        // NULL here is a row written before multi-zone existed - ResolvedZones synthesizes the
        // single zone from the legacy columns above rather than this being a missing-data case.
        reader.IsDBNull(15) ? null : JsonSerializer.Deserialize<IReadOnlyList<TriggerZone>>(reader.GetString(15)));

    private static TriggerEvent ReadEvent(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        Guid.ParseExact(reader.GetString(1), "N"),
        DateTimeOffset.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetDouble(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        FromStorage(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static string ToStorage(TriggerConfirmation confirmation) => confirmation switch
    {
        TriggerConfirmation.Pending => "pending",
        TriggerConfirmation.Rejected => "rejected",
        _ => "confirmed"
    };

    // Anything unrecognised reads as Confirmed - a value this build doesn't know about (a
    // downgrade, a hand-edited row) should leave the footage visible, not hide it.
    private static TriggerConfirmation FromStorage(string value) => value switch
    {
        "pending" => TriggerConfirmation.Pending,
        "rejected" => TriggerConfirmation.Rejected,
        _ => TriggerConfirmation.Confirmed
    };

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
