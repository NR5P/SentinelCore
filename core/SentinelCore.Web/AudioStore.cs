using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// audio.db: the small shared tables behind both audio tabs. Each row keeps its object as JSON
// beside the columns it is looked up by - these are a few dozen rows per household, read whole.
public sealed class SqliteAudioStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteAudioStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "audio.db")}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS AudioNowPlaying (
                UserKey TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                Json TEXT NOT NULL,
                PRIMARY KEY (UserKey, DeviceId)
            );

            CREATE TABLE IF NOT EXISTS AudioSpeakers (
                DeviceId TEXT PRIMARY KEY,
                LastSeenAt TEXT NOT NULL,
                Json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AudioAlarms (
                Id TEXT PRIMARY KEY,
                UserKey TEXT NOT NULL,
                Json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AudioListenBrainz (
                UserKey TEXT PRIMARY KEY,
                Token TEXT NOT NULL,
                Username TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AudioListenQueue (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserKey TEXT NOT NULL,
                Json TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                NextAttemptAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    // MARK: - Now playing

    public async Task SaveNowPlayingAsync(string userKey, AudioNowPlaying nowPlaying, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AudioNowPlaying (UserKey, DeviceId, UpdatedAt, Json) VALUES ($user, $device, $updated, $json)
                ON CONFLICT(UserKey, DeviceId) DO UPDATE SET UpdatedAt = excluded.UpdatedAt, Json = excluded.Json
                """;
            command.Parameters.AddWithValue("$user", userKey);
            command.Parameters.AddWithValue("$device", nowPlaying.DeviceId);
            command.Parameters.AddWithValue("$updated", nowPlaying.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(nowPlaying, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // Newest first; devices quiet for a week are forgotten on the way.
    public async Task<IReadOnlyList<AudioNowPlaying>> GetNowPlayingAsync(string userKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using (var prune = _connection.CreateCommand())
            {
                prune.CommandText = "DELETE FROM AudioNowPlaying WHERE UpdatedAt < $before";
                prune.Parameters.AddWithValue("$before", (now - AudioNowPlaying.Forgotten).ToString("O"));
                await prune.ExecuteNonQueryAsync(cancellationToken);
            }
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Json FROM AudioNowPlaying WHERE UserKey = $user ORDER BY UpdatedAt DESC";
            command.Parameters.AddWithValue("$user", userKey);
            return await ReadJsonAsync<AudioNowPlaying>(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteNowPlayingAsync(string userKey, string deviceId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM AudioNowPlaying WHERE UserKey = $user AND DeviceId = $device";
            command.Parameters.AddWithValue("$user", userKey);
            command.Parameters.AddWithValue("$device", deviceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // MARK: - Speakers

    public async Task SaveSpeakerAsync(AudioSpeaker speaker, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AudioSpeakers (DeviceId, LastSeenAt, Json) VALUES ($device, $seen, $json)
                ON CONFLICT(DeviceId) DO UPDATE SET LastSeenAt = excluded.LastSeenAt, Json = excluded.Json
                """;
            command.Parameters.AddWithValue("$device", speaker.DeviceId);
            command.Parameters.AddWithValue("$seen", speaker.LastSeenAt.ToString("O"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(speaker, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<AudioSpeaker>> GetSpeakersAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Json FROM AudioSpeakers ORDER BY LastSeenAt DESC";
            return await ReadJsonAsync<AudioSpeaker>(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteSpeakerAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM AudioSpeakers WHERE DeviceId = $device";
            command.Parameters.AddWithValue("$device", deviceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // MARK: - Alarms

    public async Task SaveAlarmAsync(AudioWakeAlarm alarm, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AudioAlarms (Id, UserKey, Json) VALUES ($id, $user, $json)
                ON CONFLICT(Id) DO UPDATE SET UserKey = excluded.UserKey, Json = excluded.Json
                """;
            command.Parameters.AddWithValue("$id", alarm.Id.ToString("N"));
            command.Parameters.AddWithValue("$user", alarm.UserKey);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(alarm, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // Everyone's when userKey is null (the alarm worker); otherwise one person's.
    public async Task<IReadOnlyList<AudioWakeAlarm>> GetAlarmsAsync(string? userKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = userKey is null ? "SELECT Json FROM AudioAlarms" : "SELECT Json FROM AudioAlarms WHERE UserKey = $user";
            if (userKey is not null) command.Parameters.AddWithValue("$user", userKey);
            var alarms = await ReadJsonAsync<AudioWakeAlarm>(command, cancellationToken);
            return alarms.OrderBy(a => a.Time, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAlarmAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM AudioAlarms WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // MARK: - ListenBrainz

    public async Task<AudioListenBrainzAccount?> GetListenBrainzAsync(string userKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT UserKey, Token, Username FROM AudioListenBrainz WHERE UserKey = $user";
            command.Parameters.AddWithValue("$user", userKey);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new AudioListenBrainzAccount(reader.GetString(0), reader.GetString(1), reader.GetString(2))
                : null;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<AudioListenBrainzAccount>> AllListenBrainzAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT UserKey, Token, Username FROM AudioListenBrainz";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<AudioListenBrainzAccount>();
            while (await reader.ReadAsync(cancellationToken))
                list.Add(new AudioListenBrainzAccount(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return list;
        }
        finally { _lock.Release(); }
    }

    // A null account disconnects, and drops whatever was still waiting to be sent for it.
    public async Task SetListenBrainzAsync(string userKey, AudioListenBrainzAccount? account, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            if (account is null)
            {
                command.CommandText = "DELETE FROM AudioListenBrainz WHERE UserKey = $user; DELETE FROM AudioListenQueue WHERE UserKey = $user;";
            }
            else
            {
                command.CommandText = """
                    INSERT INTO AudioListenBrainz (UserKey, Token, Username) VALUES ($user, $token, $name)
                    ON CONFLICT(UserKey) DO UPDATE SET Token = excluded.Token, Username = excluded.Username
                    """;
                command.Parameters.AddWithValue("$token", account.Token);
                command.Parameters.AddWithValue("$name", account.Username);
            }
            command.Parameters.AddWithValue("$user", userKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task EnqueueListenAsync(string userKey, AudioListen listen, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO AudioListenQueue (UserKey, Json, CreatedAt, NextAttemptAt) VALUES ($user, $json, $now, $now)";
            command.Parameters.AddWithValue("$user", userKey);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(listen, JsonOptions));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<AudioQueuedListen>> DueListensAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, UserKey, Json, CreatedAt, Attempts FROM AudioListenQueue WHERE NextAttemptAt <= $now ORDER BY Id LIMIT $limit";
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<AudioQueuedListen>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var listen = JsonSerializer.Deserialize<AudioListen>(reader.GetString(2), JsonOptions);
                if (listen is null) continue;
                list.Add(new AudioQueuedListen(reader.GetInt64(0), reader.GetString(1), listen,
                    DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt32(4)));
            }
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task CompleteListenAsync(long id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM AudioListenQueue WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task RetryListenAsync(long id, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE AudioListenQueue SET Attempts = Attempts + 1, NextAttemptAt = $next WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$next", nextAttemptAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> PendingListenCountAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM AudioListenQueue";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally { _lock.Release(); }
    }

    // MARK: - Backup

    public async Task<AudioBackup> ExportAsync(CancellationToken cancellationToken) =>
        new(await GetAlarmsAsync(null, cancellationToken), await AllListenBrainzAsync(cancellationToken));

    // Alarms and connections that already exist are left as they are: the live copy is newer
    // than any backup of it.
    public async Task<(int Alarms, int ListenBrainz)> RestoreAsync(AudioBackup backup, CancellationToken cancellationToken)
    {
        var alarms = 0;
        var listenBrainz = 0;
        var existingAlarms = (await GetAlarmsAsync(null, cancellationToken)).Select(a => a.Id).ToHashSet();
        foreach (var alarm in backup.Alarms ?? [])
        {
            if (alarm.Id == Guid.Empty || existingAlarms.Contains(alarm.Id)) continue;
            await SaveAlarmAsync(alarm, cancellationToken);
            alarms++;
        }
        foreach (var account in backup.ListenBrainz ?? [])
        {
            if (string.IsNullOrWhiteSpace(account.Token) || await GetListenBrainzAsync(account.UserKey, cancellationToken) is not null) continue;
            await SetListenBrainzAsync(account.UserKey, account, cancellationToken);
            listenBrainz++;
        }
        return (alarms, listenBrainz);
    }

    private static async Task<IReadOnlyList<T>> ReadJsonAsync<T>(SqliteCommand command, CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions) is { } value) list.Add(value);
            }
            catch (JsonException) { }
        }
        return list;
    }

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}

public sealed record AudioQueuedListen(long Id, string UserKey, AudioListen Listen, DateTimeOffset CreatedAt, int Attempts);
