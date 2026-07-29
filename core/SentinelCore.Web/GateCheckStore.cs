using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// A gate/door left open with nobody around never trips a motion alert, so nothing in the
// existing pipeline ever notices it. This is a periodic, opt-in-per-camera check that's
// independent of Blue Iris and trigger zones entirely - own table, own snapshot capture (see
// GateCheckMonitor), and its own notify decision (GateCheckDecision, in the main app) because
// AlertNotificationPolicy requires a visible person/vehicle/package/animal before it will ever
// notify, which a plain "gate is open, nobody around" reading would never satisfy.
//
// LastKnownState/LastStateChangedAt/LastNotifiedAt/LastKnownBlocked round-trip through the main
// app on every check (see GateCheckAnalyzer) so the transition decision survives a restart -
// otherwise a gate that was already open before a restart would look like a fresh transition and
// re-notify.
public sealed record GateCheckConfig(
    Guid CameraId,
    bool Enabled,
    string CheckLabel,
    bool CheckBlocked,
    int IntervalMinutes,
    int? RemindIfStillOpenAfterHours,
    string LastKnownState,
    DateTimeOffset? LastStateChangedAt,
    DateTimeOffset? LastNotifiedAt,
    bool LastKnownBlocked)
{
    public static GateCheckConfig Disabled(Guid cameraId) =>
        new(cameraId, false, "gate", true, 10, null, "unknown", null, null, false);
}

public interface IGateCheckStore
{
    Task<GateCheckConfig?> GetAsync(Guid cameraId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GateCheckConfig>> GetAllAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<GateCheckConfig>> GetEnabledAsync(CancellationToken cancellationToken);
    Task SaveAsync(GateCheckConfig config, CancellationToken cancellationToken);

    // Called after every check, whether or not it notified - keeps the transition memory current
    // without requiring a caller to round-trip the whole config just to update four columns.
    Task UpdateStateAsync(Guid cameraId, string lastKnownState, DateTimeOffset? lastStateChangedAt, DateTimeOffset? lastNotifiedAt, bool lastKnownBlocked, CancellationToken cancellationToken);

    Task DeleteAsync(Guid cameraId, CancellationToken cancellationToken);
}

public sealed class SqliteGateCheckStore : IGateCheckStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteGateCheckStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "gate-checks.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS GateChecks (
                CameraId TEXT PRIMARY KEY,
                Enabled INTEGER NOT NULL,
                CheckLabel TEXT NOT NULL,
                CheckBlocked INTEGER NOT NULL,
                IntervalMinutes INTEGER NOT NULL,
                RemindIfStillOpenAfterHours INTEGER,
                LastKnownState TEXT NOT NULL DEFAULT 'unknown',
                LastStateChangedAt TEXT,
                LastNotifiedAt TEXT,
                LastKnownBlocked INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<GateCheckConfig?> GetAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"{SelectColumns} FROM GateChecks WHERE CameraId = $cameraId;";
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadConfig(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<GateCheckConfig>> GetAllAsync(CancellationToken cancellationToken) =>
        await QueryAsync($"{SelectColumns} FROM GateChecks;", cancellationToken);

    public async Task<IReadOnlyList<GateCheckConfig>> GetEnabledAsync(CancellationToken cancellationToken) =>
        await QueryAsync($"{SelectColumns} FROM GateChecks WHERE Enabled = 1;", cancellationToken);

    private async Task<IReadOnlyList<GateCheckConfig>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;

            var results = new List<GateCheckConfig>();
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

    public async Task SaveAsync(GateCheckConfig config, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO GateChecks (CameraId, Enabled, CheckLabel, CheckBlocked, IntervalMinutes, RemindIfStillOpenAfterHours,
                                        LastKnownState, LastStateChangedAt, LastNotifiedAt, LastKnownBlocked)
                VALUES ($cameraId, $enabled, $checkLabel, $checkBlocked, $intervalMinutes, $remindHours,
                        $lastKnownState, $lastStateChangedAt, $lastNotifiedAt, $lastKnownBlocked)
                ON CONFLICT(CameraId) DO UPDATE SET
                    Enabled = excluded.Enabled,
                    CheckLabel = excluded.CheckLabel,
                    CheckBlocked = excluded.CheckBlocked,
                    IntervalMinutes = excluded.IntervalMinutes,
                    RemindIfStillOpenAfterHours = excluded.RemindIfStillOpenAfterHours;
                """;
            command.Parameters.AddWithValue("$cameraId", config.CameraId.ToString("N"));
            command.Parameters.AddWithValue("$enabled", config.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$checkLabel", config.CheckLabel);
            command.Parameters.AddWithValue("$checkBlocked", config.CheckBlocked ? 1 : 0);
            command.Parameters.AddWithValue("$intervalMinutes", config.IntervalMinutes);
            command.Parameters.AddWithValue("$remindHours", (object?)config.RemindIfStillOpenAfterHours ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastKnownState", config.LastKnownState);
            command.Parameters.AddWithValue("$lastStateChangedAt", (object?)config.LastStateChangedAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastNotifiedAt", (object?)config.LastNotifiedAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastKnownBlocked", config.LastKnownBlocked ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateStateAsync(Guid cameraId, string lastKnownState, DateTimeOffset? lastStateChangedAt, DateTimeOffset? lastNotifiedAt, bool lastKnownBlocked, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE GateChecks SET
                    LastKnownState = $lastKnownState,
                    LastStateChangedAt = $lastStateChangedAt,
                    LastNotifiedAt = $lastNotifiedAt,
                    LastKnownBlocked = $lastKnownBlocked
                WHERE CameraId = $cameraId;
                """;
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));
            command.Parameters.AddWithValue("$lastKnownState", lastKnownState);
            command.Parameters.AddWithValue("$lastStateChangedAt", (object?)lastStateChangedAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastNotifiedAt", (object?)lastNotifiedAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastKnownBlocked", lastKnownBlocked ? 1 : 0);
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
            command.CommandText = "DELETE FROM GateChecks WHERE CameraId = $cameraId;";
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private const string SelectColumns =
        "SELECT CameraId, Enabled, CheckLabel, CheckBlocked, IntervalMinutes, RemindIfStillOpenAfterHours, LastKnownState, LastStateChangedAt, LastNotifiedAt, LastKnownBlocked";

    private static GateCheckConfig ReadConfig(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetInt64(1) != 0,
        reader.GetString(2),
        reader.GetInt64(3) != 0,
        (int)reader.GetInt64(4),
        reader.IsDBNull(5) ? null : (int)reader.GetInt64(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
        reader.GetInt64(9) != 0);

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
