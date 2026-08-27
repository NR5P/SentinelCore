using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// "Record this camera from 8pm to 8:30pm every Tuesday" - the clock-driven counterpart to a
// trigger zone, for the times you know in advance that you want everything rather than only the
// moments something moved.
//
// Deliberately its own feature in this project rather than a new AutomationAction type over in the
// main app's Schedules, for two reasons. The cameras, ffmpeg and the clips all live here, and no
// push channel from that app to this one exists (SensorTriggerWatcher says the same thing at
// length, and resorted to an MQTT subscription rather than invent one) - so a schedule fired over
// there would have to reach back across a boundary that isn't there. And an AutomationAction fires
// at an *instant*, while a recording is a window: modelling it as a start schedule plus a stop
// schedule means a missed stop records until the disk fills.
//
// The timing fields below are a deliberate mirror of SentinelCore.Schedule's, because ToSchedule()
// hands them straight to ScheduleTiming.NextOccurrence. That is the whole reason this can support
// cron expressions, day-of-week sets, one-offs and "at dusk" without a second implementation of
// any of it - daylight saving and all. This project already references SentinelCore for its types,
// so the reuse costs nothing at run time.
public sealed record RecordingSchedule(
    Guid Id,
    Guid CameraId,
    string Name,
    bool Enabled,
    // "daily" | "cron" | "once" | "solar" - the subset of Schedule.KnownKinds that makes sense for
    // a recording. "interval" is deliberately absent: "record for 30 minutes every 15 minutes" is
    // a request for overlapping windows, which the recorder has no sane answer for.
    string Kind,
    string TimeOfDay,
    IReadOnlyList<int> DaysOfWeek,
    string CronExpression,
    DateTimeOffset? RunAtUtc,
    string SolarEvent,
    int SolarOffsetMinutes,
    double SolarElevationDegrees,
    // The "stop time", stored as a length rather than a second clock time. A stored stop *time* has
    // to be matched back to its start at run time, and anything that breaks that pairing - a
    // restart at the wrong moment, an edit to one half - leaves a camera recording with nothing to
    // stop it. A duration cannot be orphaned: the recorder knows its own end the instant it starts,
    // and a resume after a restart can work out exactly how much is left. The editor still shows
    // the owner two clock times and does this arithmetic for them.
    //
    // It also makes a window that crosses midnight (23:00 for two hours) fall out for free rather
    // than being a case anyone has to handle.
    int DurationSeconds,
    bool IncludeAudio,
    DateTimeOffset? LastStartedUtc,
    string LastStatus,
    string LastDetail,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const string KindDaily = Schedule.KindDaily;
    public const string KindCron = Schedule.KindCron;
    public const string KindOnce = Schedule.KindOnce;
    public const string KindSolar = Schedule.KindSolar;

    public static readonly IReadOnlyList<string> KnownKinds = [KindDaily, KindCron, KindOnce, KindSolar];

    public static bool IsKnownKind(string kind) => KnownKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

    // A minute is the shortest window worth the machinery - below that a trigger clip's post-roll
    // does the job better. The ceiling is a day: anything longer is continuous recording, which is
    // a different feature with different disk arithmetic, and an unbounded box here is how someone
    // types 86400000 by accident and fills the array.
    public const int MinimumDurationSeconds = 60;
    public const int MaximumDurationSeconds = 24 * 60 * 60;

    // The timing half of this row expressed as the main app's Schedule, purely so ScheduleTiming
    // can be asked when it next fires. Every field the recorder doesn't have an opinion about is
    // left at its empty value - notably the action, which is meaningless here: what happens when
    // this fires isn't a dispatchable action, it's a recording, and that is the point of this type
    // existing separately at all.
    public Schedule ToSchedule() => new(
        Id,
        Name,
        Enabled,
        Kind,
        TimeOfDay,
        DaysOfWeek,
        IntervalSeconds: 0,
        WindowStart: string.Empty,
        WindowEnd: string.Empty,
        CronExpression,
        RunAtUtc,
        SolarEvent,
        SolarOffsetMinutes,
        SolarElevationDegrees,
        Action: EmptyAction,
        CatchUpPolicy: Schedule.CatchUpSkip,
        LastRunUtc: LastStartedUtc,
        LastStatus: LastStatus,
        LastDetail: LastDetail,
        NextDueUtc: null,
        RunCount: 0,
        CreatedAt,
        UpdatedAt);

    private static readonly AutomationAction EmptyAction =
        new(string.Empty, null, string.Empty, null, string.Empty, string.Empty, false,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}

// One window that was actually captured, or is being captured right now.
//
// Requested and actual times are both kept because they routinely differ and the difference is the
// diagnostic: a recording that started nine seconds late because go2rtc had to dial the camera is
// fine, one that started four minutes late is a symptom. Only the actual pair describes the file.
public sealed record Recording(
    Guid Id,
    Guid CameraId,
    // Null for a recording started by hand rather than by a schedule, and for one whose schedule
    // has since been deleted - the footage outlives the rule that asked for it.
    Guid? ScheduleId,
    string Name,
    DateTimeOffset RequestedStartUtc,
    DateTimeOffset RequestedEndUtc,
    DateTimeOffset? ActualStartUtc,
    DateTimeOffset? ActualEndUtc,
    string? FilePath,
    long ByteSize,
    string Status,
    string Detail,
    // True for a recording somebody started with a camera's record button. ScheduleId alone cannot
    // answer this - it is also null for a scheduled recording whose schedule was deleted while the
    // footage outlived it - and the two want opposite treatment when a restart finds them still in
    // progress. See RecordingScheduleWorker.ResumeInterruptedAsync.
    bool IsManual = false)
{
    // ffmpeg is writing segments for this window right now. A row left in this state by a restart
    // is what RecordingScheduleWorker's resume pass looks for.
    public const string StatusRecording = "recording";

    // The window ran to its end and the segments concatenated into a playable file.
    public const string StatusComplete = "complete";

    // There is a file, but it does not cover the whole window - the process was down for part of
    // it, or ffmpeg died and was restarted. Deliberately not "failed": partial footage of the half
    // hour you cared about is worth infinitely more than a row saying something went wrong, and
    // hiding it behind an error state is how it would go unwatched.
    public const string StatusPartial = "partial";

    // No usable footage. The camera never answered, the disk was too full to start, or the
    // concatenation produced nothing.
    public const string StatusFailed = "failed";

    public bool IsFinished => Status is StatusComplete or StatusPartial or StatusFailed;
}

public interface IRecordingScheduleStore
{
    Task<IReadOnlyList<RecordingSchedule>> GetAllAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RecordingSchedule>> GetForCameraAsync(Guid cameraId, CancellationToken cancellationToken);
    Task<RecordingSchedule?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<RecordingSchedule> SaveAsync(RecordingSchedule schedule, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    // Records how the most recent firing went, for the "last run" line in the editor. Separate from
    // SaveAsync so the worker never has to read-modify-write a row the owner may be editing.
    Task SetLastRunAsync(Guid id, DateTimeOffset startedUtc, string status, string detail, CancellationToken cancellationToken);
}

public interface IRecordingStore
{
    Task<Recording> AddAsync(Recording recording, CancellationToken cancellationToken);
    Task<Recording?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Recording>> GetRecentAsync(int limit, Guid? cameraId, CancellationToken cancellationToken);

    // Recordings still marked as in progress. On startup these are the ones a restart interrupted;
    // during normal running there is one per active window.
    Task<IReadOnlyList<Recording>> GetInProgressAsync(CancellationToken cancellationToken);

    Task FinishAsync(Guid id, DateTimeOffset actualEndUtc, string? filePath, long byteSize, string status, string detail, CancellationToken cancellationToken);
    Task SetStartedAsync(Guid id, DateTimeOffset actualStartUtc, CancellationToken cancellationToken);

    // Moves the end of the window somebody asked for. Only a manual recording does this, when it is
    // stopped by hand: the window it was created with is the *maximum* it was allowed to run, not
    // an intention, so leaving it in place would have every hand-stopped recording measured against
    // a length nobody wanted and filed as partial. See ScheduledRecorder.RunAsync.
    Task SetRequestedEndAsync(Guid id, DateTimeOffset requestedEndUtc, CancellationToken cancellationToken);

    // Ages out recordings older than the cutoff, handing back the files those rows owned so the
    // caller can unlink them. Same order of operations as the trigger retention sweep, and for the
    // same reason: the row goes first, because a row pointing at a missing file is a visible gap
    // while a file with no row is invisible and unreclaimable.
    Task<IReadOnlyList<string>> DeleteBeforeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetAllReferencedFilesAsync(CancellationToken cancellationToken);
}

public sealed class SqliteRecordingStore : IRecordingScheduleStore, IRecordingStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteRecordingStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "recordings.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS RecordingSchedules (
                Id TEXT PRIMARY KEY,
                CameraId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                TimeOfDay TEXT NOT NULL,
                DaysOfWeek TEXT NOT NULL,
                CronExpression TEXT NOT NULL,
                RunAtUtc TEXT,
                SolarEvent TEXT NOT NULL,
                SolarOffsetMinutes INTEGER NOT NULL,
                SolarElevationDegrees REAL NOT NULL,
                DurationSeconds INTEGER NOT NULL,
                IncludeAudio INTEGER NOT NULL,
                LastStartedUtc TEXT,
                LastStatus TEXT NOT NULL,
                LastDetail TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_RecordingSchedules_CameraId ON RecordingSchedules (CameraId);

            CREATE TABLE IF NOT EXISTS Recordings (
                Id TEXT PRIMARY KEY,
                CameraId TEXT NOT NULL,
                ScheduleId TEXT,
                Name TEXT NOT NULL,
                RequestedStartUtc TEXT NOT NULL,
                RequestedEndUtc TEXT NOT NULL,
                ActualStartUtc TEXT,
                ActualEndUtc TEXT,
                FilePath TEXT,
                ByteSize INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Detail TEXT NOT NULL,
                IsManual INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_Recordings_RequestedStart ON Recordings (RequestedStartUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_Recordings_Status ON Recordings (Status);
            """;
        command.ExecuteNonQuery();

        // Databases written before the record button could hold a recording open. The default of 0
        // is right for every row already in there: manual recordings did not exist.
        AddColumnIfMissing("Recordings", "IsManual", "INTEGER NOT NULL DEFAULT 0");
    }

    // Same idiom as SqliteTriggerStore and CameraStore - ALTER TABLE ADD COLUMN is the whole
    // migration story this app needs, and asking pragma_table_info first makes it idempotent.
    private void AddColumnIfMissing(string table, string column, string definition)
    {
        using var check = _connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        check.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

        using var migrate = _connection.CreateCommand();
        migrate.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        migrate.ExecuteNonQuery();
    }

    private const string ScheduleColumns = """
        Id, CameraId, Name, Enabled, Kind, TimeOfDay, DaysOfWeek, CronExpression, RunAtUtc,
        SolarEvent, SolarOffsetMinutes, SolarElevationDegrees, DurationSeconds, IncludeAudio,
        LastStartedUtc, LastStatus, LastDetail, CreatedAt, UpdatedAt
        """;

    public Task<IReadOnlyList<RecordingSchedule>> GetAllAsync(CancellationToken cancellationToken) =>
        QuerySchedulesAsync($"SELECT {ScheduleColumns} FROM RecordingSchedules ORDER BY Name;", null, cancellationToken);

    public Task<IReadOnlyList<RecordingSchedule>> GetForCameraAsync(Guid cameraId, CancellationToken cancellationToken) =>
        QuerySchedulesAsync(
            $"SELECT {ScheduleColumns} FROM RecordingSchedules WHERE CameraId = $cameraId ORDER BY Name;",
            command => command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N")),
            cancellationToken);

    public async Task<RecordingSchedule?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var rows = await QuerySchedulesAsync(
            $"SELECT {ScheduleColumns} FROM RecordingSchedules WHERE Id = $id;",
            command => command.Parameters.AddWithValue("$id", id.ToString("N")),
            cancellationToken);

        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task<IReadOnlyList<RecordingSchedule>> QuerySchedulesAsync(
        string sql, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command);

            var results = new List<RecordingSchedule>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) results.Add(ReadSchedule(reader));
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RecordingSchedule> SaveAsync(RecordingSchedule schedule, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO RecordingSchedules
                    (Id, CameraId, Name, Enabled, Kind, TimeOfDay, DaysOfWeek, CronExpression, RunAtUtc,
                     SolarEvent, SolarOffsetMinutes, SolarElevationDegrees, DurationSeconds, IncludeAudio,
                     LastStartedUtc, LastStatus, LastDetail, CreatedAt, UpdatedAt)
                VALUES
                    ($id, $cameraId, $name, $enabled, $kind, $timeOfDay, $daysOfWeek, $cronExpression, $runAtUtc,
                     $solarEvent, $solarOffsetMinutes, $solarElevationDegrees, $durationSeconds, $includeAudio,
                     $lastStartedUtc, $lastStatus, $lastDetail, $createdAt, $updatedAt)
                ON CONFLICT(Id) DO UPDATE SET
                    CameraId = excluded.CameraId,
                    Name = excluded.Name,
                    Enabled = excluded.Enabled,
                    Kind = excluded.Kind,
                    TimeOfDay = excluded.TimeOfDay,
                    DaysOfWeek = excluded.DaysOfWeek,
                    CronExpression = excluded.CronExpression,
                    RunAtUtc = excluded.RunAtUtc,
                    SolarEvent = excluded.SolarEvent,
                    SolarOffsetMinutes = excluded.SolarOffsetMinutes,
                    SolarElevationDegrees = excluded.SolarElevationDegrees,
                    DurationSeconds = excluded.DurationSeconds,
                    IncludeAudio = excluded.IncludeAudio,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            BindSchedule(command, schedule);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return schedule;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM RecordingSchedules WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetLastRunAsync(
        Guid id, DateTimeOffset startedUtc, string status, string detail, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE RecordingSchedules
                SET LastStartedUtc = $lastStartedUtc, LastStatus = $lastStatus, LastDetail = $lastDetail
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$lastStartedUtc", startedUtc.ToString("O"));
            command.Parameters.AddWithValue("$lastStatus", status);
            command.Parameters.AddWithValue("$lastDetail", detail);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void BindSchedule(SqliteCommand command, RecordingSchedule schedule)
    {
        command.Parameters.AddWithValue("$id", schedule.Id.ToString("N"));
        command.Parameters.AddWithValue("$cameraId", schedule.CameraId.ToString("N"));
        command.Parameters.AddWithValue("$name", schedule.Name);
        command.Parameters.AddWithValue("$enabled", schedule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$kind", schedule.Kind);
        command.Parameters.AddWithValue("$timeOfDay", schedule.TimeOfDay);
        command.Parameters.AddWithValue("$daysOfWeek", JsonSerializer.Serialize(schedule.DaysOfWeek));
        command.Parameters.AddWithValue("$cronExpression", schedule.CronExpression);
        command.Parameters.AddWithValue("$runAtUtc", (object?)schedule.RunAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$solarEvent", schedule.SolarEvent);
        command.Parameters.AddWithValue("$solarOffsetMinutes", schedule.SolarOffsetMinutes);
        command.Parameters.AddWithValue("$solarElevationDegrees", schedule.SolarElevationDegrees);
        command.Parameters.AddWithValue("$durationSeconds", schedule.DurationSeconds);
        command.Parameters.AddWithValue("$includeAudio", schedule.IncludeAudio ? 1 : 0);
        command.Parameters.AddWithValue("$lastStartedUtc", (object?)schedule.LastStartedUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastStatus", schedule.LastStatus);
        command.Parameters.AddWithValue("$lastDetail", schedule.LastDetail);
        command.Parameters.AddWithValue("$createdAt", schedule.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", schedule.UpdatedAt.ToString("O"));
    }

    private static RecordingSchedule ReadSchedule(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("CameraId"))),
        reader.GetString(reader.GetOrdinal("Name")),
        reader.GetInt32(reader.GetOrdinal("Enabled")) == 1,
        reader.GetString(reader.GetOrdinal("Kind")),
        reader.GetString(reader.GetOrdinal("TimeOfDay")),
        JsonSerializer.Deserialize<List<int>>(reader.GetString(reader.GetOrdinal("DaysOfWeek"))) ?? [],
        reader.GetString(reader.GetOrdinal("CronExpression")),
        ReadNullableTime(reader, "RunAtUtc"),
        reader.GetString(reader.GetOrdinal("SolarEvent")),
        reader.GetInt32(reader.GetOrdinal("SolarOffsetMinutes")),
        reader.GetDouble(reader.GetOrdinal("SolarElevationDegrees")),
        reader.GetInt32(reader.GetOrdinal("DurationSeconds")),
        reader.GetInt32(reader.GetOrdinal("IncludeAudio")) == 1,
        ReadNullableTime(reader, "LastStartedUtc"),
        reader.GetString(reader.GetOrdinal("LastStatus")),
        reader.GetString(reader.GetOrdinal("LastDetail")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt")), null),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")), null));

    private const string RecordingColumns = """
        Id, CameraId, ScheduleId, Name, RequestedStartUtc, RequestedEndUtc, ActualStartUtc,
        ActualEndUtc, FilePath, ByteSize, Status, Detail, IsManual
        """;

    public async Task<Recording> AddAsync(Recording recording, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Recordings
                    (Id, CameraId, ScheduleId, Name, RequestedStartUtc, RequestedEndUtc, ActualStartUtc,
                     ActualEndUtc, FilePath, ByteSize, Status, Detail, IsManual)
                VALUES
                    ($id, $cameraId, $scheduleId, $name, $requestedStartUtc, $requestedEndUtc, $actualStartUtc,
                     $actualEndUtc, $filePath, $byteSize, $status, $detail, $isManual);
                """;
            command.Parameters.AddWithValue("$id", recording.Id.ToString("N"));
            command.Parameters.AddWithValue("$cameraId", recording.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$scheduleId", (object?)recording.ScheduleId?.ToString("N") ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", recording.Name);
            command.Parameters.AddWithValue("$requestedStartUtc", recording.RequestedStartUtc.ToString("O"));
            command.Parameters.AddWithValue("$requestedEndUtc", recording.RequestedEndUtc.ToString("O"));
            command.Parameters.AddWithValue("$actualStartUtc", (object?)recording.ActualStartUtc?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$actualEndUtc", (object?)recording.ActualEndUtc?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$filePath", (object?)recording.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$byteSize", recording.ByteSize);
            command.Parameters.AddWithValue("$status", recording.Status);
            command.Parameters.AddWithValue("$detail", recording.Detail);
            command.Parameters.AddWithValue("$isManual", recording.IsManual ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return recording;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Explicitly implemented because IRecordingScheduleStore.GetAsync already claims this exact
    // signature on this class - the same collision SqliteTriggerStore resolves the same way.
    async Task<Recording?> IRecordingStore.GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var rows = await QueryRecordingsAsync(
            $"SELECT {RecordingColumns} FROM Recordings WHERE Id = $id;",
            command => command.Parameters.AddWithValue("$id", id.ToString("N")),
            cancellationToken);

        return rows.Count > 0 ? rows[0] : null;
    }

    public Task<IReadOnlyList<Recording>> GetRecentAsync(int limit, Guid? cameraId, CancellationToken cancellationToken) =>
        QueryRecordingsAsync(
            cameraId is null
                ? $"SELECT {RecordingColumns} FROM Recordings ORDER BY RequestedStartUtc DESC LIMIT $limit;"
                : $"SELECT {RecordingColumns} FROM Recordings WHERE CameraId = $cameraId ORDER BY RequestedStartUtc DESC LIMIT $limit;",
            command =>
            {
                command.Parameters.AddWithValue("$limit", limit);
                if (cameraId is { } id) command.Parameters.AddWithValue("$cameraId", id.ToString("N"));
            },
            cancellationToken);

    public Task<IReadOnlyList<Recording>> GetInProgressAsync(CancellationToken cancellationToken) =>
        QueryRecordingsAsync(
            $"SELECT {RecordingColumns} FROM Recordings WHERE Status = $status ORDER BY RequestedStartUtc;",
            command => command.Parameters.AddWithValue("$status", Recording.StatusRecording),
            cancellationToken);

    private async Task<IReadOnlyList<Recording>> QueryRecordingsAsync(
        string sql, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command);

            var results = new List<Recording>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) results.Add(ReadRecording(reader));
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetStartedAsync(Guid id, DateTimeOffset actualStartUtc, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE Recordings SET ActualStartUtc = $actualStartUtc WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$actualStartUtc", actualStartUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetRequestedEndAsync(Guid id, DateTimeOffset requestedEndUtc, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE Recordings SET RequestedEndUtc = $requestedEndUtc WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$requestedEndUtc", requestedEndUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task FinishAsync(
        Guid id, DateTimeOffset actualEndUtc, string? filePath, long byteSize, string status, string detail,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE Recordings
                SET ActualEndUtc = $actualEndUtc, FilePath = $filePath, ByteSize = $byteSize,
                    Status = $status, Detail = $detail
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$actualEndUtc", actualEndUtc.ToString("O"));
            command.Parameters.AddWithValue("$filePath", (object?)filePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$byteSize", byteSize);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$detail", detail);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> DeleteBeforeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var files = new List<string>();

            using (var select = _connection.CreateCommand())
            {
                // In-progress rows are never swept, however old they look. A window that outlives
                // the retention setting is a misconfiguration, not a reason to delete the file
                // ffmpeg still has open.
                select.CommandText = """
                    SELECT FilePath FROM Recordings
                    WHERE RequestedStartUtc < $cutoff AND Status <> $recording AND FilePath IS NOT NULL;
                    """;
                select.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));
                select.Parameters.AddWithValue("$recording", Recording.StatusRecording);

                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) files.Add(reader.GetString(0));
            }

            using var delete = _connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM Recordings WHERE RequestedStartUtc < $cutoff AND Status <> $recording;
                """;
            delete.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));
            delete.Parameters.AddWithValue("$recording", Recording.StatusRecording);
            await delete.ExecuteNonQueryAsync(cancellationToken);

            return files;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetAllReferencedFilesAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT FilePath FROM Recordings WHERE FilePath IS NOT NULL;";

            var files = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) files.Add(reader.GetString(0));
            return files;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Recording ReadRecording(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("CameraId"))),
        reader.IsDBNull(reader.GetOrdinal("ScheduleId"))
            ? null
            : Guid.Parse(reader.GetString(reader.GetOrdinal("ScheduleId"))),
        reader.GetString(reader.GetOrdinal("Name")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("RequestedStartUtc")), null),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("RequestedEndUtc")), null),
        ReadNullableTime(reader, "ActualStartUtc"),
        ReadNullableTime(reader, "ActualEndUtc"),
        reader.IsDBNull(reader.GetOrdinal("FilePath")) ? null : reader.GetString(reader.GetOrdinal("FilePath")),
        reader.GetInt64(reader.GetOrdinal("ByteSize")),
        reader.GetString(reader.GetOrdinal("Status")),
        reader.GetString(reader.GetOrdinal("Detail")),
        reader.GetInt64(reader.GetOrdinal("IsManual")) != 0);

    private static DateTimeOffset? ReadNullableTime(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), null);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
