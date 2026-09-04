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
    // "off", or "any" for a sensor whose meaning is the change itself. Also "below"/"above", which
    // compare SensorMeasurement against SensorThreshold instead of looking at on/off at all - see
    // UsesThreshold.
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
    IReadOnlyList<TriggerZone>? Zones = null,
    // Which number out of the sensor's payload to compare, keyed exactly as DeviceTopics harvested
    // it - "distance", "illuminance", "ENERGY.Power". Only read when SensorTriggerOn is a threshold
    // comparison; empty otherwise.
    //
    // A key rather than a declared schema because that is how the readings arrive: one
    // Zigbee2MQTT message carries half a dozen of them and nothing announces in advance which
    // matter. The cost is that a typo here is a binding that never fires, which is why
    // SensorTriggerWatcher logs the keys it did see when a lookup misses.
    string SensorMeasurement = "",
    double SensorThreshold = 0,
    // Hold back every motion trigger from this camera between NightStartHour and NightEndHour -
    // see TriggerNightWindow for why time of day is the only axis that separates the gate camera's
    // after-dark IR noise from real subjects. Off unless someone turns it on: a camera that stops
    // watching for ten hours a day is not something to start doing on anyone's behalf.
    //
    // Appended at the end with defaults for the same reason Zones was - this record is built
    // positionally in several places, and inserting a field mid-list shifts every later argument
    // silently.
    bool NightWindowEnabled = false,
    // Only read when NightWindowEnabled. The defaults describe the measured flood (21:00-07:00)
    // rather than 0/0, so switching the feature on gives a sensible window instead of a
    // zero-width one that appears to do nothing.
    int NightStartHour = 21,
    int NightEndHour = 7)
{
    public const string DefaultZoneName = "Zone 1";

    public static TriggerConfig Disabled(Guid cameraId) =>
        new(cameraId, false, [], 0.02, 5, 6, 50, false, 5, 10, false, "default");

    public bool HasSensorBinding => !string.IsNullOrWhiteSpace(SensorDeviceId);

    // A threshold binding reads a number; the on/off modes read DevicePower. The two never mix -
    // a distance sensor has no on/off state to fall back to, and a contact switch has no number.
    public bool UsesThreshold => SensorTriggerOn is "below" or "above";

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
    // Which of the camera's zones detected this, for the Events tab. Null for a companion
    // capture (no zone detected it - the camera was recorded because a group member fired), for a
    // sensor or manual trigger, and for every row written before multi-zone existed.
    string? ZoneName = null,
    // The alert this trigger was forwarded to the analysis pipeline as, once the pipeline has
    // accepted it. This is what lets the Events tab show one card per real-world moment instead
    // of a motion row and an analysis row for the same thing - see the /api/activity merge.
    // Null whenever no analysis exists to join to: the camera doesn't forward to alerts, the
    // forward was refused, or the row predates this column.
    Guid? AlertId = null,
    // How long after OccurredAt this row's image was actually fetched - the pipeline's own latency
    // between a frame being captured and the trigger being handled. The image still shows OccurredAt
    // exactly; this says how far behind real time the system was running at that moment, which is
    // the diagnostic whose absence let a three-second detection lag go unnoticed for months.
    //
    // Null when the image came from the live snapshot instead: Trigger Rewind is off, or there was
    // no buffered video to fetch from.
    double? CaptureDelaySeconds = null);

public interface ITriggerConfigStore
{
    Task<TriggerConfig?> GetAsync(Guid cameraId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TriggerConfig>> GetEnabledAsync(CancellationToken cancellationToken);

    // Unlike GetEnabledAsync, includes disabled zones too - used for config export, where
    // silently dropping a disabled zone would mean it never comes back on restore.
    Task<IReadOnlyList<TriggerConfig>> GetAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(TriggerConfig config, CancellationToken cancellationToken);

    // For a camera that no longer exists. Deliberately not exposed as an endpoint: a config is
    // switched off by saving it disabled, and the only thing that legitimately removes one outright
    // is the camera itself going away - see CameraEndpoints' delete.
    Task DeleteAsync(Guid cameraId, CancellationToken cancellationToken);
}

public interface ITriggerEventStore
{
    // zoneName, captureDelaySeconds and occurredAt are trailing and optional so the callers that have
    // none of them (companion captures, sensor and manual triggers) are unchanged.
    //
    // occurredAt is when the event happened, which is not the same as when this row is being
    // written: motion detection works on decoded video, and the gap between a frame being captured
    // and a trigger being handled is real. Callers that know the frame's capture time pass it so
    // the row, its clip and its alert all describe the same moment. Omitting it falls back to now.
    Task<TriggerEvent> AddAsync(
        Guid cameraId, string imagePath, double triggerAreaFraction, string? incidentId,
        TriggerConfirmation confirmation, CancellationToken cancellationToken, string? zoneName = null,
        double? captureDelaySeconds = null, DateTimeOffset? occurredAt = null);

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

    // The same window, but every row regardless of verdict. The rejected ones are the entire
    // subject of the night-nuisance check (see LensNuisance): a camera behind a spider web
    // produces hundreds of triggers the AI throws away, and a query that only returns what
    // survived confirmation would show that camera as having had a perfectly quiet night.
    Task<IReadOnlyList<TriggerEvent>> GetAllEventsInRangeAsync(Guid cameraId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);

    // When this camera was last advised that it looks like it has a web or insects on it, or null
    // if it never has been. Persisted rather than held in memory because the check runs once a
    // day: a process restart between two mornings would otherwise reset the cooldown and send the
    // same advice again the next morning.
    Task<DateTimeOffset?> GetLastNuisanceAdvisoryAsync(Guid cameraId, CancellationToken cancellationToken);

    Task RecordNuisanceAdvisoryAsync(Guid cameraId, DateOnly night, DateTimeOffset sentAt, CancellationToken cancellationToken);

    // Records the AI's verdict once the analysis for this trigger has finished.
    Task SetConfirmationAsync(Guid id, TriggerConfirmation confirmation, CancellationToken cancellationToken);

    // Records which alert this trigger was forwarded as, so the Events tab can show the motion
    // and its analysis as one card. Written as soon as the pipeline accepts the forward - well
    // before any verdict comes back - because the join is useful even for a trigger that was
    // never gated on confirmation.
    Task SetAlertIdAsync(Guid id, Guid alertId, CancellationToken cancellationToken);

    // Triggers left Pending by a restart (the poll that was waiting on their verdict died with
    // the process). Swept to Confirmed on startup rather than left invisible forever - see
    // TriggerConfirmationSweeper.
    Task<IReadOnlyList<Guid>> GetPendingBeforeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    // Clips can only be cut once the post-roll has actually elapsed, so the event row is written
    // immediately (image first) and the clip path is patched in afterwards.
    Task SetClipPathAsync(Guid id, string clipPath, CancellationToken cancellationToken);

    // Marks this trigger's clip as one a person deliberately asked for, so no automatic discard
    // may take it. The AI's verdict still decides whether the trigger is SHOWN - a record-button
    // press on an empty driveway is still hidden behind 'show hidden' - but somebody pressing
    // record is a request for that video, and a model disagreeing about what is in frame is not a
    // reason to delete what they asked for. Call it before SetClipPathAsync, not after: the other
    // order leaves a window in which a verdict already in hand could collect the file.
    Task ProtectClipAsync(Guid id, CancellationToken cancellationToken);

    // Hands back the clip a rejected trigger points at, so footage the AI found nothing in can be
    // unlinked at the moment of rejection instead of sitting on disk until the retention window
    // catches up with it. Returns null - and changes nothing - when there is nothing to release.
    //
    // ClipPath is cleared on every rejected row that shares the file, and only when no row that
    // isn't rejected points at it. One clip covers a span of time rather than an instant, so it is
    // routinely shared: with a CONFIRMED trigger, whose footage this is never allowed to touch, or
    // with one still PENDING, which is kept for now - if that verdict also comes back rejected it
    // arrives here in turn and collects the file then. Same rule as DeleteUnconfirmedBeforeAsync,
    // and it exists for the same incident.
    Task<string?> ReleaseRejectedClipAsync(Guid triggerId, CancellationToken cancellationToken);

    // Decides whether a fresh trigger should join an already-open incident: the most recent
    // IncidentId among any of these camera ids triggered within the window, or null if none has.
    // Used by TriggerZoneMonitor to reuse vs. mint an IncidentId when a group member fires.
    Task<string?> GetMostRecentIncidentIdAsync(IReadOnlyList<Guid> cameraIds, DateTimeOffset since, CancellationToken cancellationToken);

    // Drops motion the AI never confirmed once it is older than the cutoff, and hands back the
    // image and clip files those rows owned so the caller can unlink them. Confirmed rows are
    // never touched here - they age out under the main app's own retention setting.
    //
    // Rows and files are separate deletions on purpose, and the row goes first: a row pointing at
    // a file that is already gone renders as a missing thumbnail, while a file with no row is
    // invisible and unreclaimable. Losing the race in that direction is the recoverable one.
    //
    // The returned paths exclude anything a surviving row still points at. Neighbouring triggers
    // share a clip file, so a rejected row's ClipPath is frequently a confirmed row's ClipPath too,
    // and unlinking on row ownership alone destroys confirmed footage.
    Task<IReadOnlyList<string>> DeleteUnconfirmedBeforeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    // Every image and clip path any row still points at. The retention sweep diffs this against
    // what is actually on disk to find files nothing owns any more - see
    // MotionRetentionWorker.SelectOrphans for why those accumulate and can't be caught any other
    // way.
    Task<IReadOnlyList<string>> GetAllReferencedFilesAsync(CancellationToken cancellationToken);
}

public sealed class SqliteTriggerStore : ITriggerConfigStore, ITriggerEventStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Optional purely so the tests can keep constructing this with a directory and nothing else.
    // The one thing it reports is the retention sweep holding a clip back - see the note in
    // DeleteUnconfirmedBeforeAsync for why that event is worth a line in the journal.
    private readonly ILogger<SqliteTriggerStore>? _logger;

    public SqliteTriggerStore(string dataDirectory, ILogger<SqliteTriggerStore>? logger = null)
    {
        _logger = logger;
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
        // Defaults that read as "no threshold configured" on every existing row: SensorTriggerOn
        // on those rows is still on/off/any, so neither column is consulted until someone picks a
        // comparison in the UI.
        AddColumnIfMissing("TriggerConfigs", "SensorMeasurement", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("TriggerConfigs", "SensorThreshold", "REAL NOT NULL DEFAULT 0");
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
        // Nullable, and never backfilled: rows written before this column can't be matched to
        // their analysis after the fact (no key existed to record), so they stay motion-only in
        // the Events tab rather than being guessed at by camera and timestamp.
        AddColumnIfMissing("TriggerEvents", "AlertId", "TEXT");
        AddColumnIfMissing("TriggerEvents", "CaptureDelaySeconds", "REAL");
        // "Somebody asked for this video on purpose, so nothing may throw it away on the AI's
        // say-so." Set only by the record button (see CameraTriggerEndpoints.AttachManualClipAsync)
        // and read only by ReleaseRejectedClipAsync. 0 on every existing row and on everything the
        // motion detector records, which is what the automatic discard is about.
        AddColumnIfMissing("TriggerEvents", "KeepClip", "INTEGER NOT NULL DEFAULT 0");
        // Off on every existing row. The hours still get their real defaults rather than 0/0 so
        // that a row migrated today and enabled tomorrow suppresses the window someone would
        // actually have asked for - but with Enabled 0 they are inert either way.
        AddColumnIfMissing("TriggerConfigs", "NightWindowEnabled", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("TriggerConfigs", "NightStartHour", "INTEGER NOT NULL DEFAULT 21");
        AddColumnIfMissing("TriggerConfigs", "NightEndHour", "INTEGER NOT NULL DEFAULT 7");

        using var incidentIndex = _connection.CreateCommand();
        incidentIndex.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_TriggerEvents_IncidentId ON TriggerEvents (IncidentId);
            CREATE INDEX IF NOT EXISTS IX_TriggerEvents_Confirmation ON TriggerEvents (Confirmation, OccurredAt DESC);

            CREATE TABLE IF NOT EXISTS NuisanceAdvisories (
                CameraId TEXT NOT NULL,
                Night TEXT NOT NULL,
                SentAt TEXT NOT NULL,
                PRIMARY KEY (CameraId, Night)
            );
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
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds, Zones,
                       SensorMeasurement, SensorThreshold,
                       NightWindowEnabled, NightStartHour, NightEndHour
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
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds, Zones,
                       SensorMeasurement, SensorThreshold,
                       NightWindowEnabled, NightStartHour, NightEndHour
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
                       SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds, Zones,
                       SensorMeasurement, SensorThreshold,
                       NightWindowEnabled, NightStartHour, NightEndHour
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
                                            SensorDeviceId, SensorTriggerOn, SensorCooldownSeconds, Zones,
                                            SensorMeasurement, SensorThreshold,
                                            NightWindowEnabled, NightStartHour, NightEndHour)
                VALUES ($cameraId, $enabled, $zonePolygon, $minTriggerArea, $cooldownSeconds, $maxTriggersPerMinute, $shadowSensitivity,
                        $recordClips, $clipPreSeconds, $clipPostSeconds, $sendToAlerts, $alertProfile,
                        $sensorDeviceId, $sensorTriggerOn, $sensorCooldownSeconds, $zones,
                        $sensorMeasurement, $sensorThreshold,
                        $nightWindowEnabled, $nightStartHour, $nightEndHour)
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
                    Zones = excluded.Zones,
                    SensorMeasurement = excluded.SensorMeasurement,
                    SensorThreshold = excluded.SensorThreshold,
                    NightWindowEnabled = excluded.NightWindowEnabled,
                    NightStartHour = excluded.NightStartHour,
                    NightEndHour = excluded.NightEndHour;
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
            command.Parameters.AddWithValue("$sensorMeasurement", config.SensorMeasurement);
            command.Parameters.AddWithValue("$sensorThreshold", config.SensorThreshold);
            command.Parameters.AddWithValue("$nightWindowEnabled", config.NightWindowEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$nightStartHour", config.NightStartHour);
            command.Parameters.AddWithValue("$nightEndHour", config.NightEndHour);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            // TriggerEvents are deliberately untouched. They are what happened, not configuration:
            // the images and the incidents they belong to stay readable after the camera that
            // recorded them is gone, which is the whole point of keeping them.
            command.CommandText = "DELETE FROM TriggerConfigs WHERE CameraId = $cameraId;";
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TriggerEvent> AddAsync(
        Guid cameraId, string imagePath, double triggerAreaFraction, string? incidentId,
        TriggerConfirmation confirmation, CancellationToken cancellationToken, string? zoneName = null,
        double? captureDelaySeconds = null, DateTimeOffset? occurredAt = null)
    {
        var trigger = new TriggerEvent(
            Guid.NewGuid(), cameraId, occurredAt ?? DateTimeOffset.UtcNow, imagePath, triggerAreaFraction, null,
            incidentId, confirmation, zoneName, AlertId: null, CaptureDelaySeconds: captureDelaySeconds);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TriggerEvents (Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, IncidentId, Confirmation, ZoneName, CaptureDelaySeconds)
                VALUES ($id, $cameraId, $occurredAt, $imagePath, $triggerAreaFraction, $incidentId, $confirmation, $zoneName, $captureDelaySeconds);
                """;
            command.Parameters.AddWithValue("$id", trigger.Id.ToString("N"));
            command.Parameters.AddWithValue("$cameraId", trigger.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$occurredAt", trigger.OccurredAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$imagePath", trigger.ImagePath);
            command.Parameters.AddWithValue("$triggerAreaFraction", trigger.TriggerAreaFraction);
            command.Parameters.AddWithValue("$incidentId", (object?)trigger.IncidentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$confirmation", ToStorage(confirmation));
            command.Parameters.AddWithValue("$zoneName", (object?)trigger.ZoneName ?? DBNull.Value);
            command.Parameters.AddWithValue("$captureDelaySeconds", (object?)trigger.CaptureDelaySeconds ?? DBNull.Value);
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

    public async Task SetAlertIdAsync(Guid id, Guid alertId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE TriggerEvents SET AlertId = $alertId WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$alertId", alertId.ToString("N"));
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
                "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId, Confirmation, ZoneName, AlertId, CaptureDelaySeconds " +
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
                SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId, Confirmation, ZoneName, AlertId, CaptureDelaySeconds
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

    public async Task<IReadOnlyList<TriggerEvent>> GetAllEventsInRangeAsync(Guid cameraId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId, Confirmation, ZoneName, AlertId, CaptureDelaySeconds
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

    public async Task<DateTimeOffset?> GetLastNuisanceAdvisoryAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT MAX(SentAt) FROM NuisanceAdvisories WHERE CameraId = $cameraId;";
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string text && DateTimeOffset.TryParse(
                text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sentAt)
                ? sentAt
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordNuisanceAdvisoryAsync(Guid cameraId, DateOnly night, DateTimeOffset sentAt, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            // Keyed on the night rather than only the camera so the history stays readable, and
            // REPLACE rather than INSERT so a retry after a half-failed send can't throw.
            command.CommandText = """
                INSERT OR REPLACE INTO NuisanceAdvisories (CameraId, Night, SentAt)
                VALUES ($cameraId, $night, $sentAt);
                """;
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));
            command.Parameters.AddWithValue("$night", night.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$sentAt", sentAt.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task ProtectClipAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE TriggerEvents SET KeepClip = 1 WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> ReleaseRejectedClipAsync(Guid triggerId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Both halves of the condition matter. Not rejected: the caller is asking about a
            // trigger whose verdict has since changed or never came, and the answer is no. No clip:
            // either this camera doesn't record them, or - much more common - the verdict landed
            // before the post-roll finished and the clip is still being cut. That one is handled by
            // the recorder asking again once it has patched the path in.
            string clipPath;
            using (var select = _connection.CreateCommand())
            {
                select.CommandText =
                    "SELECT ClipPath FROM TriggerEvents " +
                    "WHERE Id = $id AND Confirmation = 'rejected' AND ClipPath IS NOT NULL AND KeepClip = 0;";
                select.Parameters.AddWithValue("$id", triggerId.ToString("N"));

                if (await select.ExecuteScalarAsync(cancellationToken) is not string path || path.Length == 0)
                    return null;

                clipPath = path;
            }

            using (var shared = _connection.CreateCommand())
            {
                shared.CommandText =
                    "SELECT COUNT(*) FROM TriggerEvents " +
                    "WHERE ClipPath = $clipPath AND (Confirmation <> 'rejected' OR KeepClip <> 0);";
                shared.Parameters.AddWithValue("$clipPath", clipPath);

                if (Convert.ToInt64(await shared.ExecuteScalarAsync(cancellationToken)) > 0)
                {
                    _logger?.LogInformation(
                        "A rejected trigger's clip is shared with a trigger that wasn't rejected, or with one somebody " +
                        "recorded on purpose, so it stays on disk");
                    return null;
                }
            }

            // Every row sharing the file, not just this one: the file is about to be unlinked, and
            // a row left pointing at it renders as a dead video link on the tab. By this point they
            // are all rejected and none is protected - that is exactly what the count above
            // established - so this cannot blank a row whose footage survives.
            using (var clear = _connection.CreateCommand())
            {
                clear.CommandText = "UPDATE TriggerEvents SET ClipPath = NULL WHERE ClipPath = $clipPath;";
                clear.Parameters.AddWithValue("$clipPath", clipPath);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            return clipPath;
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
            command.CommandText = "SELECT Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction, ClipPath, IncidentId, Confirmation, ZoneName, AlertId, CaptureDelaySeconds FROM TriggerEvents WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task<IReadOnlyList<string>> ITriggerEventStore.DeleteUnconfirmedBeforeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var cutoff = olderThan.UtcDateTime.ToString("O");

            // Collected before the DELETE because the paths only exist on the rows. Anything still
            // Pending is left alone whatever its age: a verdict may yet land on it, and the startup
            // sweeper resolves the ones orphaned by a restart (see GetPendingBeforeAsync).
            var files = new List<string>();
            using (var select = _connection.CreateCommand())
            {
                select.CommandText =
                    "SELECT ImagePath, ClipPath FROM TriggerEvents " +
                    "WHERE Confirmation NOT IN ('confirmed', 'pending') AND OccurredAt < $cutoff;";
                select.Parameters.AddWithValue("$cutoff", cutoff);

                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0)) files.Add(reader.GetString(0));
                    if (!reader.IsDBNull(1)) files.Add(reader.GetString(1));
                }
            }

            using (var delete = _connection.CreateCommand())
            {
                delete.CommandText =
                    "DELETE FROM TriggerEvents " +
                    "WHERE Confirmation NOT IN ('confirmed', 'pending') AND OccurredAt < $cutoff;";
                delete.Parameters.AddWithValue("$cutoff", cutoff);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            // A clip covers a span of time, not an instant, so two triggers that fire inside one
            // clip window are patched with the SAME ClipPath - and the AI routinely confirms one
            // and rejects the other. Returning every path the deleted rows held would therefore
            // unlink a clip a surviving CONFIRMED row still points at, which is the one file this
            // sweep is never allowed to touch. It fails silently too: the row keeps its path and
            // only renders as a dead video link, so nothing surfaces the loss.
            //
            // Not hypothetical - measured on the gate camera on 2026-08-15: three confirmed events
            // had already lost their clip this way, and seventeen more were queued to.
            //
            // Asked after the DELETE so "still referenced" means exactly the rows that survived.
            // Deliberately an inline query rather than a call to GetAllReferencedFilesAsync: that
            // one takes _lock, which is a non-reentrant SemaphoreSlim we are already holding, so
            // reusing it here would deadlock the sweep.
            var stillReferenced = new HashSet<string>(StringComparer.Ordinal);
            using (var survivors = _connection.CreateCommand())
            {
                survivors.CommandText = "SELECT ImagePath, ClipPath FROM TriggerEvents;";

                await using var reader = await survivors.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0)) stillReferenced.Add(reader.GetString(0));
                    if (!reader.IsDBNull(1)) stillReferenced.Add(reader.GetString(1));
                }
            }

            // Distinct as well: two rejected rows sharing one clip would otherwise hand it back
            // twice and double-count it in the sweep's "removed N files" log line.
            var unlinkable = files
                .Where(path => !stillReferenced.Contains(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Say so when the filter above actually saved something. The sweep's own log line
            // can't: it only ever sees the post-filter list, so a preserved clip is indis-
            // tinguishable there from a clip that was never a candidate. That matters because
            // this guarantee already failed silently once (three confirmed gate events lost
            // their footage before 2026-08-15), and the only way to check it afterwards was to
            // diff triggers.db against the disk by hand. One line per sweep, only when non-zero.
            var preserved = files.Distinct(StringComparer.Ordinal).Count() - unlinkable.Count;
            if (preserved > 0)
            {
                _logger?.LogInformation(
                    "Motion retention: kept {Preserved} file(s) a confirmed or pending trigger still shares with the rejected motion being pruned",
                    preserved);
            }

            return unlinkable;
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task<IReadOnlyList<string>> ITriggerEventStore.GetAllReferencedFilesAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT ImagePath, ClipPath FROM TriggerEvents WHERE ImagePath IS NOT NULL OR ClipPath IS NOT NULL;";

            var files = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0)) files.Add(reader.GetString(0));
                if (!reader.IsDBNull(1)) files.Add(reader.GetString(1));
            }

            return files;
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
        reader.IsDBNull(15) ? null : JsonSerializer.Deserialize<IReadOnlyList<TriggerZone>>(reader.GetString(15)),
        reader.GetString(16),
        reader.GetDouble(17),
        reader.GetInt64(18) != 0,
        (int)reader.GetInt64(19),
        (int)reader.GetInt64(20));

    private static TriggerEvent ReadEvent(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        Guid.ParseExact(reader.GetString(1), "N"),
        DateTimeOffset.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetDouble(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        FromStorage(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : Guid.ParseExact(reader.GetString(9), "N"),
        reader.IsDBNull(10) ? null : reader.GetDouble(10));

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
