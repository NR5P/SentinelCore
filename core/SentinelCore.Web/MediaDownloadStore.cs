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

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
