using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public interface IScheduleStore
{
    Task<IReadOnlyList<Schedule>> GetAllAsync(CancellationToken cancellationToken);

    // What ScheduleWorker asks for on every pass - a disabled schedule costs nothing beyond its
    // row, the same arrangement IAutomationRuleStore.GetEnabledAsync uses.
    Task<IReadOnlyList<Schedule>> GetEnabledAsync(CancellationToken cancellationToken);
    Task<Schedule?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Schedule> CreateAsync(Schedule schedule, CancellationToken cancellationToken);
    Task<Schedule?> UpdateAsync(Guid id, Schedule schedule, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    // Stores the next firing time without touching anything else, for the case where the worker
    // recalculated it (settings changed, a schedule was edited) but nothing has fired.
    Task SetNextDueAsync(Guid id, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken);

    // Turns a schedule off in place. A one-shot does this to itself once it has fired, so it
    // stays visible in the list with its history rather than vanishing.
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken);

    // Appends to the history and updates the parent row's summary columns in one go, so a
    // listing never shows a "last run" that disagrees with the history underneath it.
    //
    // Deliberately does not touch NextDueUtc: the worker advances that before it dispatches, so
    // that a slow action cannot leave a due time in the past and be fired a second time.
    Task RecordRunAsync(ScheduleRun run, CancellationToken cancellationToken);

    Task<IReadOnlyList<ScheduleRun>> GetRunsAsync(Guid scheduleId, int limit, CancellationToken cancellationToken);
}

public sealed class SqliteScheduleStore : IScheduleStore, IDisposable
{
    // How many firings to keep per schedule. An interval schedule can produce thousands a day,
    // so this is a cap on rows rather than on age - a busy schedule and a quiet one both keep a
    // useful amount of history, and neither can grow the file without bound.
    private const int MaximumRunsPerSchedule = 200;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteScheduleStore(IOptions<BridgeOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "schedules.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Schedules (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                TimeOfDay TEXT NOT NULL,
                DaysOfWeek TEXT NOT NULL,
                IntervalSeconds INTEGER NOT NULL,
                WindowStart TEXT NOT NULL,
                WindowEnd TEXT NOT NULL,
                CronExpression TEXT NOT NULL,
                RunAtUtc TEXT NULL,
                SolarEvent TEXT NOT NULL,
                SolarOffsetMinutes INTEGER NOT NULL,
                SolarElevationDegrees REAL NOT NULL,
                ActionType TEXT NOT NULL,
                DeviceId TEXT NULL,
                DeviceAction TEXT NOT NULL,
                DeviceBrightness INTEGER NULL,
                MqttTopic TEXT NOT NULL,
                MqttPayload TEXT NOT NULL,
                MqttRetain INTEGER NOT NULL,
                WebhookUrl TEXT NOT NULL,
                WebhookMethod TEXT NOT NULL,
                WebhookHeaders TEXT NOT NULL,
                WebhookBody TEXT NOT NULL,
                WebhookContentType TEXT NOT NULL,
                CatchUpPolicy TEXT NOT NULL,
                LastRunUtc TEXT NULL,
                LastStatus TEXT NOT NULL,
                LastDetail TEXT NOT NULL,
                NextDueUtc TEXT NULL,
                RunCount INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ScheduleRuns (
                Id TEXT PRIMARY KEY,
                ScheduleId TEXT NOT NULL,
                DueAtUtc TEXT NOT NULL,
                FiredAtUtc TEXT NOT NULL,
                Status TEXT NOT NULL,
                Detail TEXT NOT NULL,
                DurationMs INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ScheduleRuns_ScheduleFiredAt
                ON ScheduleRuns (ScheduleId, FiredAtUtc DESC);
            """;
        command.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<Schedule>> GetAllAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM Schedules ORDER BY Name COLLATE NOCASE;", cancellationToken);

    public Task<IReadOnlyList<Schedule>> GetEnabledAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM Schedules WHERE Enabled = 1;", cancellationToken);

    private async Task<IReadOnlyList<Schedule>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;

            var results = new List<Schedule>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadSchedule(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Schedule?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM Schedules WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadSchedule(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Schedule> CreateAsync(Schedule schedule, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Schedules (
                    Id, Name, Enabled, Kind, TimeOfDay, DaysOfWeek, IntervalSeconds, WindowStart, WindowEnd,
                    CronExpression, RunAtUtc, SolarEvent, SolarOffsetMinutes, SolarElevationDegrees,
                    ActionType, DeviceId, DeviceAction, DeviceBrightness,
                    MqttTopic, MqttPayload, MqttRetain,
                    WebhookUrl, WebhookMethod, WebhookHeaders, WebhookBody, WebhookContentType,
                    CatchUpPolicy, LastRunUtc, LastStatus, LastDetail, NextDueUtc, RunCount, CreatedAt, UpdatedAt
                ) VALUES (
                    $id, $name, $enabled, $kind, $timeOfDay, $daysOfWeek, $intervalSeconds, $windowStart, $windowEnd,
                    $cronExpression, $runAtUtc, $solarEvent, $solarOffsetMinutes, $solarElevationDegrees,
                    $actionType, $deviceId, $deviceAction, $deviceBrightness,
                    $mqttTopic, $mqttPayload, $mqttRetain,
                    $webhookUrl, $webhookMethod, $webhookHeaders, $webhookBody, $webhookContentType,
                    $catchUpPolicy, $lastRunUtc, $lastStatus, $lastDetail, $nextDueUtc, $runCount, $createdAt, $updatedAt
                );
                """;
            Bind(command, schedule);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return schedule;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Deliberately does not touch the history columns: an edit changes what a schedule will do,
    // not what it already did. The caller supplies the recalculated NextDueUtc, since only it
    // knows the current time zone and location.
    public async Task<Schedule?> UpdateAsync(Guid id, Schedule schedule, CancellationToken cancellationToken)
    {
        var toSave = schedule with { Id = id };

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE Schedules SET
                    Name = $name, Enabled = $enabled, Kind = $kind, TimeOfDay = $timeOfDay,
                    DaysOfWeek = $daysOfWeek, IntervalSeconds = $intervalSeconds,
                    WindowStart = $windowStart, WindowEnd = $windowEnd,
                    CronExpression = $cronExpression, RunAtUtc = $runAtUtc,
                    SolarEvent = $solarEvent, SolarOffsetMinutes = $solarOffsetMinutes,
                    SolarElevationDegrees = $solarElevationDegrees,
                    ActionType = $actionType, DeviceId = $deviceId, DeviceAction = $deviceAction,
                    DeviceBrightness = $deviceBrightness,
                    MqttTopic = $mqttTopic, MqttPayload = $mqttPayload, MqttRetain = $mqttRetain,
                    WebhookUrl = $webhookUrl, WebhookMethod = $webhookMethod, WebhookHeaders = $webhookHeaders,
                    WebhookBody = $webhookBody, WebhookContentType = $webhookContentType,
                    CatchUpPolicy = $catchUpPolicy, NextDueUtc = $nextDueUtc,
                    CreatedAt = $createdAt, UpdatedAt = $updatedAt
                WHERE Id = $id;
                """;
            Bind(command, toSave);
            var rows = await command.ExecuteNonQueryAsync(cancellationToken);
            return rows > 0 ? toSave : null;
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

            // The history goes with it. There is no way to look at an orphaned run again once
            // the schedule that explains it is gone.
            command.CommandText = """
                DELETE FROM ScheduleRuns WHERE ScheduleId = $id;
                DELETE FROM Schedules WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetNextDueAsync(Guid id, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE Schedules SET NextDueUtc = $nextDueUtc WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$nextDueUtc", ToStorage(nextDueUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE Schedules SET Enabled = $enabled, UpdatedAt = $updatedAt WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordRunAsync(ScheduleRun run, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO ScheduleRuns (Id, ScheduleId, DueAtUtc, FiredAtUtc, Status, Detail, DurationMs)
                    VALUES ($id, $scheduleId, $dueAtUtc, $firedAtUtc, $status, $detail, $durationMs);
                    """;
                insert.Parameters.AddWithValue("$id", run.Id.ToString("N"));
                insert.Parameters.AddWithValue("$scheduleId", run.ScheduleId.ToString("N"));
                insert.Parameters.AddWithValue("$dueAtUtc", run.DueAtUtc.UtcDateTime.ToString("O"));
                insert.Parameters.AddWithValue("$firedAtUtc", run.FiredAtUtc.UtcDateTime.ToString("O"));
                insert.Parameters.AddWithValue("$status", run.Status);
                insert.Parameters.AddWithValue("$detail", run.Detail);
                insert.Parameters.AddWithValue("$durationMs", run.DurationMs);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            // A skipped or missed firing updates the status shown in the list - that is exactly
            // what someone needs to see - but it did not run, so it must not move the last-run
            // time or the count. Otherwise a server that was off all week would report a full
            // week of runs it never performed.
            var actuallyRan = run.Status is ScheduleRun.StatusOk or ScheduleRun.StatusFailed;

            using (var update = _connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE Schedules SET
                        LastStatus = $status,
                        LastDetail = $detail,
                        LastRunUtc = CASE WHEN $actuallyRan = 1 THEN $firedAtUtc ELSE LastRunUtc END,
                        RunCount = RunCount + CASE WHEN $actuallyRan = 1 THEN 1 ELSE 0 END
                    WHERE Id = $scheduleId;
                    """;
                update.Parameters.AddWithValue("$scheduleId", run.ScheduleId.ToString("N"));
                update.Parameters.AddWithValue("$status", run.Status);
                update.Parameters.AddWithValue("$detail", run.Detail);
                update.Parameters.AddWithValue("$firedAtUtc", run.FiredAtUtc.UtcDateTime.ToString("O"));
                update.Parameters.AddWithValue("$actuallyRan", actuallyRan ? 1 : 0);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            // Pruned here rather than by RetentionWorker, which only sweeps images/ and results/
            // and would never learn about this table.
            using (var prune = _connection.CreateCommand())
            {
                prune.Transaction = (SqliteTransaction)transaction;
                prune.CommandText = """
                    DELETE FROM ScheduleRuns
                    WHERE ScheduleId = $scheduleId AND Id NOT IN (
                        SELECT Id FROM ScheduleRuns
                        WHERE ScheduleId = $scheduleId
                        ORDER BY FiredAtUtc DESC
                        LIMIT $keep
                    );
                    """;
                prune.Parameters.AddWithValue("$scheduleId", run.ScheduleId.ToString("N"));
                prune.Parameters.AddWithValue("$keep", MaximumRunsPerSchedule);
                await prune.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ScheduleRun>> GetRunsAsync(
        Guid scheduleId, int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM ScheduleRuns
                WHERE ScheduleId = $scheduleId
                ORDER BY FiredAtUtc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("N"));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaximumRunsPerSchedule));

            var results = new List<ScheduleRun>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new ScheduleRun(
                    Guid.ParseExact(reader.GetString(reader.GetOrdinal("Id")), "N"),
                    Guid.ParseExact(reader.GetString(reader.GetOrdinal("ScheduleId")), "N"),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("DueAtUtc"))),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("FiredAtUtc"))),
                    reader.GetString(reader.GetOrdinal("Status")),
                    reader.GetString(reader.GetOrdinal("Detail")),
                    reader.GetInt64(reader.GetOrdinal("DurationMs"))));
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void Bind(SqliteCommand command, Schedule schedule)
    {
        command.Parameters.AddWithValue("$id", schedule.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", schedule.Name);
        command.Parameters.AddWithValue("$enabled", schedule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$kind", schedule.Kind);
        command.Parameters.AddWithValue("$timeOfDay", schedule.TimeOfDay);
        command.Parameters.AddWithValue("$daysOfWeek", JsonSerializer.Serialize(schedule.DaysOfWeek));
        command.Parameters.AddWithValue("$intervalSeconds", schedule.IntervalSeconds);
        command.Parameters.AddWithValue("$windowStart", schedule.WindowStart);
        command.Parameters.AddWithValue("$windowEnd", schedule.WindowEnd);
        command.Parameters.AddWithValue("$cronExpression", schedule.CronExpression);
        command.Parameters.AddWithValue("$runAtUtc", ToStorage(schedule.RunAtUtc));
        command.Parameters.AddWithValue("$solarEvent", schedule.SolarEvent);
        command.Parameters.AddWithValue("$solarOffsetMinutes", schedule.SolarOffsetMinutes);
        command.Parameters.AddWithValue("$solarElevationDegrees", schedule.SolarElevationDegrees);
        command.Parameters.AddWithValue("$actionType", schedule.Action.Type);
        command.Parameters.AddWithValue("$deviceId", schedule.Action.DeviceId is { } deviceId ? deviceId.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("$deviceAction", schedule.Action.DeviceAction);
        command.Parameters.AddWithValue("$deviceBrightness", schedule.Action.DeviceBrightness ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$mqttTopic", schedule.Action.MqttTopic);
        command.Parameters.AddWithValue("$mqttPayload", schedule.Action.MqttPayload);
        command.Parameters.AddWithValue("$mqttRetain", schedule.Action.MqttRetain ? 1 : 0);
        command.Parameters.AddWithValue("$webhookUrl", schedule.Action.WebhookUrl);
        command.Parameters.AddWithValue("$webhookMethod", schedule.Action.WebhookMethod);
        command.Parameters.AddWithValue("$webhookHeaders", schedule.Action.WebhookHeaders);
        command.Parameters.AddWithValue("$webhookBody", schedule.Action.WebhookBody);
        command.Parameters.AddWithValue("$webhookContentType", schedule.Action.WebhookContentType);
        command.Parameters.AddWithValue("$catchUpPolicy", schedule.CatchUpPolicy);
        command.Parameters.AddWithValue("$lastRunUtc", ToStorage(schedule.LastRunUtc));
        command.Parameters.AddWithValue("$lastStatus", schedule.LastStatus);
        command.Parameters.AddWithValue("$lastDetail", schedule.LastDetail);
        command.Parameters.AddWithValue("$nextDueUtc", ToStorage(schedule.NextDueUtc));
        command.Parameters.AddWithValue("$runCount", schedule.RunCount);
        command.Parameters.AddWithValue("$createdAt", schedule.CreatedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", schedule.UpdatedAt.UtcDateTime.ToString("O"));
    }

    private static object ToStorage(DateTimeOffset? value) =>
        value is { } instant ? instant.UtcDateTime.ToString("O") : DBNull.Value;

    private static Schedule ReadSchedule(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(reader.GetOrdinal("Id")), "N"),
        reader.GetString(reader.GetOrdinal("Name")),
        reader.GetInt64(reader.GetOrdinal("Enabled")) != 0,
        reader.GetString(reader.GetOrdinal("Kind")),
        reader.GetString(reader.GetOrdinal("TimeOfDay")),
        JsonSerializer.Deserialize<List<int>>(reader.GetString(reader.GetOrdinal("DaysOfWeek"))) ?? [],
        (int)reader.GetInt64(reader.GetOrdinal("IntervalSeconds")),
        reader.GetString(reader.GetOrdinal("WindowStart")),
        reader.GetString(reader.GetOrdinal("WindowEnd")),
        reader.GetString(reader.GetOrdinal("CronExpression")),
        ReadNullableInstant(reader, "RunAtUtc"),
        reader.GetString(reader.GetOrdinal("SolarEvent")),
        (int)reader.GetInt64(reader.GetOrdinal("SolarOffsetMinutes")),
        reader.GetDouble(reader.GetOrdinal("SolarElevationDegrees")),
        new AutomationAction(
            reader.GetString(reader.GetOrdinal("ActionType")),
            ReadNullableGuid(reader, "DeviceId"),
            reader.GetString(reader.GetOrdinal("DeviceAction")),
            ReadNullableInt(reader, "DeviceBrightness"),
            reader.GetString(reader.GetOrdinal("MqttTopic")),
            reader.GetString(reader.GetOrdinal("MqttPayload")),
            reader.GetInt64(reader.GetOrdinal("MqttRetain")) != 0,
            reader.GetString(reader.GetOrdinal("WebhookUrl")),
            reader.GetString(reader.GetOrdinal("WebhookMethod")),
            reader.GetString(reader.GetOrdinal("WebhookHeaders")),
            reader.GetString(reader.GetOrdinal("WebhookBody")),
            reader.GetString(reader.GetOrdinal("WebhookContentType"))),
        reader.GetString(reader.GetOrdinal("CatchUpPolicy")),
        ReadNullableInstant(reader, "LastRunUtc"),
        reader.GetString(reader.GetOrdinal("LastStatus")),
        reader.GetString(reader.GetOrdinal("LastDetail")),
        ReadNullableInstant(reader, "NextDueUtc"),
        reader.GetInt64(reader.GetOrdinal("RunCount")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))));

    private static DateTimeOffset? ReadNullableInstant(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }

    private static Guid? ReadNullableGuid(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Guid.ParseExact(reader.GetString(ordinal), "N");
    }

    private static int? ReadNullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : (int)reader.GetInt64(ordinal);
    }

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
