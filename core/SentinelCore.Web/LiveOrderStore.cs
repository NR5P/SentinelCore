using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// How one person wants the Live grid arranged. Deliberately per-account rather than global: the
// camera someone wants in the top-left is a personal preference (whoever watches the driveway all
// day wants a different first tile than whoever is really only there for the front door), and
// making it global would mean the last person to drag a tile silently rearranges everybody else's
// screen.
//
// Stored as one row per owner holding the whole ordered id list as JSON, the same shape
// CameraGroups already uses for its CameraIds - the list is only ever read and written whole, so
// a row-per-camera table would buy nothing but joins. Kept in its own SQLite file next to
// cameras.db/accounts.db for the same reason those are separate: it's a .Web-only concept the
// always-on bridge process has no reason to know about.
//
// Ids of cameras that no longer exist are NOT pruned on camera deletion - LiveOrderEndpoints
// filters them out on read instead, which handles the deleted-camera case and the
// restored-from-backup case (id comes back, the person's placement for it comes back with it)
// with one rule rather than two.
public interface ILiveOrderStore
{
    // Empty means "this person has never dragged a tile" - the caller falls back to the camera
    // store's own order.
    Task<IReadOnlyList<Guid>> GetAsync(string ownerKey, CancellationToken cancellationToken);

    Task SaveAsync(string ownerKey, IReadOnlyList<Guid> cameraIds, CancellationToken cancellationToken);
}

public sealed class SqliteLiveOrderStore : ILiveOrderStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteLiveOrderStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "live-order.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS LiveOrders (
                OwnerKey TEXT PRIMARY KEY,
                CameraIds TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<Guid>> GetAsync(string ownerKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT CameraIds FROM LiveOrders WHERE OwnerKey = $ownerKey;";
            command.Parameters.AddWithValue("$ownerKey", ownerKey);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return [];

            return JsonSerializer.Deserialize<List<Guid>>(reader.GetString(0)) ?? [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(string ownerKey, IReadOnlyList<Guid> cameraIds, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO LiveOrders (OwnerKey, CameraIds, UpdatedAt)
                VALUES ($ownerKey, $cameraIds, $updatedAt)
                ON CONFLICT(OwnerKey) DO UPDATE SET CameraIds = excluded.CameraIds, UpdatedAt = excluded.UpdatedAt;
                """;
            command.Parameters.AddWithValue("$ownerKey", ownerKey);
            command.Parameters.AddWithValue("$cameraIds", JsonSerializer.Serialize(cameraIds));
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
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
