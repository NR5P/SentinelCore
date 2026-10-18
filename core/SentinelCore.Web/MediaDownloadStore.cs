using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// The download queue and its history, in media-downloads.db beside media.db. Kept out of media.db
// and IMediaStore on purpose: none of it is library data, and it is history rather than
// configuration, so it is not in backups either (BackupCoverageTests records why).
public sealed class MediaDownloadStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MediaDownloadStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "media-downloads.db")}");
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS MediaDownloads (
                Id TEXT PRIMARY KEY,
                State TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Data TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_MediaDownloads_Created ON MediaDownloads (CreatedAt);

            CREATE TABLE IF NOT EXISTS MediaImports (
                Id TEXT PRIMARY KEY,
                CreatedAt TEXT NOT NULL,
                Data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MusicSuggestionCache (
                Key TEXT PRIMARY KEY,
                FetchedAt TEXT NOT NULL,
                Data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MusicSuggestionDismissals (
                UserKey TEXT NOT NULL,
                Key TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY (UserKey, Key)
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<MediaDownload>> AllAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Data FROM MediaDownloads ORDER BY CreatedAt DESC";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaDownload>();
            while (await reader.ReadAsync(cancellationToken))
                if (JsonSerializer.Deserialize<MediaDownload>(reader.GetString(0), JsonOptions) is { } download) list.Add(download);
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<MediaDownload?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Data FROM MediaDownloads WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            return await command.ExecuteScalarAsync(cancellationToken) is string json
                ? JsonSerializer.Deserialize<MediaDownload>(json, JsonOptions)
                : null;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(MediaDownload download, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MediaDownloads (Id, State, CreatedAt, Data) VALUES ($id, $state, $created, $data)
                ON CONFLICT(Id) DO UPDATE SET State = excluded.State, Data = excluded.Data
                """;
            command.Parameters.AddWithValue("$id", download.Id.ToString("N"));
            command.Parameters.AddWithValue("$state", download.State);
            command.Parameters.AddWithValue("$created", download.CreatedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(download, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM MediaDownloads WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // MARK: - Imported lists ("Import a list" on Music and Movies)

    public async Task<IReadOnlyList<MediaImportJob>> AllImportsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Data FROM MediaImports ORDER BY CreatedAt";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaImportJob>();
            while (await reader.ReadAsync(cancellationToken))
                if (JsonSerializer.Deserialize<MediaImportJob>(reader.GetString(0), JsonOptions) is { } job) list.Add(job);
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<MediaImportJob?> GetImportAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Data FROM MediaImports WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            return await command.ExecuteScalarAsync(cancellationToken) is string json
                ? JsonSerializer.Deserialize<MediaImportJob>(json, JsonOptions)
                : null;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveImportAsync(MediaImportJob job, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MediaImports (Id, CreatedAt, Data) VALUES ($id, $created, $data)
                ON CONFLICT(Id) DO UPDATE SET Data = excluded.Data
                """;
            command.Parameters.AddWithValue("$id", job.Id.ToString("N"));
            command.Parameters.AddWithValue("$created", job.CreatedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(job, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteImportAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM MediaImports WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // MARK: - Music suggestions (MusicSuggestions.cs)

    // What ListenBrainz or MusicBrainz said about one artist, and when it was asked.
    public async Task<(DateTimeOffset FetchedAt, string Data)?> GetSuggestionCacheAsync(string key, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT FetchedAt, Data FROM MusicSuggestionCache WHERE Key = $key";
            command.Parameters.AddWithValue("$key", key);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? (DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(1))
                : null;
        }
        finally { _lock.Release(); }
    }

    public async Task SetSuggestionCacheAsync(string key, DateTimeOffset fetchedAt, string data, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MusicSuggestionCache (Key, FetchedAt, Data) VALUES ($key, $at, $data)
                ON CONFLICT(Key) DO UPDATE SET FetchedAt = excluded.FetchedAt, Data = excluded.Data
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$at", fetchedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$data", data);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // "Not interested": an artist ("artist:<key>") or one album ("album:<artist key>|<title key>").
    public async Task<HashSet<string>> GetSuggestionDismissalsAsync(string userKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Key FROM MusicSuggestionDismissals WHERE UserKey = $user";
            command.Parameters.AddWithValue("$user", userKey);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken)) keys.Add(reader.GetString(0));
            return keys;
        }
        finally { _lock.Release(); }
    }

    public async Task SetSuggestionDismissedAsync(string userKey, string key, bool dismissed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = dismissed
                ? "INSERT OR IGNORE INTO MusicSuggestionDismissals (UserKey, Key, CreatedAt) VALUES ($user, $key, $at)"
                : "DELETE FROM MusicSuggestionDismissals WHERE UserKey = $user AND Key = $key";
            command.Parameters.AddWithValue("$user", userKey);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$at", now.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
