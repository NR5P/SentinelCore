using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// Cameras added here are purely for live viewing in this dashboard - deliberately unrelated
// to the Blue Iris alert pipeline the main app runs. Stored in their own SQLite file, separate
// from events.db, since the main app has no reason to know these exist.
//
// OnvifPtzServiceUrl/OnvifProfileToken are populated by camera discovery (see
// CameraDiscoveryService) when a camera's ONVIF PTZ service is found - PtzEnabled defaults to
// that detection but stays a separate, independently user-toggleable flag: someone can turn it
// on for a camera discovery didn't detect it for (that PTZ call will just fail with a clear
// error, since there's no service URL/profile token to send it to), or off for one that has it.
public sealed record Camera(
    Guid Id,
    string Name,
    string StreamUrl,
    string? Username,
    string? Password,
    bool IncludeAudio,
    bool PtzEnabled,
    string? OnvifPtzServiceUrl,
    string? OnvifProfileToken,
    DateTimeOffset CreatedAt);

public interface ICameraStore
{
    Task<IReadOnlyList<Camera>> GetAllAsync(CancellationToken cancellationToken);
    Task<Camera?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Camera> AddAsync(
        string name, string streamUrl, string? username, string? password, bool includeAudio,
        bool ptzEnabled, string? onvifPtzServiceUrl, string? onvifProfileToken,
        CancellationToken cancellationToken);

    Task<bool> UpdateAsync(
        Guid id, string name, string streamUrl, string? username, string? password, bool includeAudio,
        bool ptzEnabled, string? onvifPtzServiceUrl, string? onvifProfileToken,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    // Unlike AddAsync, preserves the camera's own Id rather than minting a new one - used only
    // by config restore, so a backed-up trigger zone's CameraId still points at something real
    // once its camera comes back.
    Task RestoreAsync(Camera camera, CancellationToken cancellationToken);
}

public sealed class SqliteCameraStore : ICameraStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteCameraStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "cameras.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Cameras (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                StreamUrl TEXT NOT NULL,
                Username TEXT,
                Password TEXT,
                IncludeAudio INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        // Added after the initial release - older databases won't have these columns yet.
        AddColumnIfMissing("Cameras", "PtzEnabled", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("Cameras", "OnvifPtzServiceUrl", "TEXT");
        AddColumnIfMissing("Cameras", "OnvifProfileToken", "TEXT");
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

    public async Task<IReadOnlyList<Camera>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = SelectColumns + " FROM Cameras ORDER BY Name ASC;";

            var results = new List<Camera>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadCamera(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Camera?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = SelectColumns + " FROM Cameras WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadCamera(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Camera> AddAsync(
        string name, string streamUrl, string? username, string? password, bool includeAudio,
        bool ptzEnabled, string? onvifPtzServiceUrl, string? onvifProfileToken,
        CancellationToken cancellationToken)
    {
        var camera = new Camera(
            Guid.NewGuid(), name, streamUrl, username, password, includeAudio,
            ptzEnabled, onvifPtzServiceUrl, onvifProfileToken, DateTimeOffset.UtcNow);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Cameras
                    (Id, Name, StreamUrl, Username, Password, IncludeAudio, PtzEnabled, OnvifPtzServiceUrl, OnvifProfileToken, CreatedAt)
                VALUES
                    ($id, $name, $streamUrl, $username, $password, $includeAudio, $ptzEnabled, $onvifPtzServiceUrl, $onvifProfileToken, $createdAt);
                """;
            AddParameters(command, camera);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        return camera;
    }

    public async Task RestoreAsync(Camera camera, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Cameras
                    (Id, Name, StreamUrl, Username, Password, IncludeAudio, PtzEnabled, OnvifPtzServiceUrl, OnvifProfileToken, CreatedAt)
                VALUES
                    ($id, $name, $streamUrl, $username, $password, $includeAudio, $ptzEnabled, $onvifPtzServiceUrl, $onvifProfileToken, $createdAt)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name, StreamUrl = excluded.StreamUrl, Username = excluded.Username,
                    Password = excluded.Password, IncludeAudio = excluded.IncludeAudio,
                    PtzEnabled = excluded.PtzEnabled, OnvifPtzServiceUrl = excluded.OnvifPtzServiceUrl,
                    OnvifProfileToken = excluded.OnvifProfileToken, CreatedAt = excluded.CreatedAt;
                """;
            AddParameters(command, camera);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> UpdateAsync(
        Guid id, string name, string streamUrl, string? username, string? password, bool includeAudio,
        bool ptzEnabled, string? onvifPtzServiceUrl, string? onvifProfileToken,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE Cameras SET Name = $name, StreamUrl = $streamUrl, Username = $username,
                    Password = $password, IncludeAudio = $includeAudio, PtzEnabled = $ptzEnabled,
                    OnvifPtzServiceUrl = $onvifPtzServiceUrl, OnvifProfileToken = $onvifProfileToken
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$streamUrl", streamUrl);
            command.Parameters.AddWithValue("$username", (object?)username ?? DBNull.Value);
            command.Parameters.AddWithValue("$password", (object?)password ?? DBNull.Value);
            command.Parameters.AddWithValue("$includeAudio", includeAudio ? 1 : 0);
            command.Parameters.AddWithValue("$ptzEnabled", ptzEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$onvifPtzServiceUrl", (object?)onvifPtzServiceUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$onvifProfileToken", (object?)onvifProfileToken ?? DBNull.Value);

            var rows = await command.ExecuteNonQueryAsync(cancellationToken);
            return rows > 0;
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
            command.CommandText = "DELETE FROM Cameras WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            var rows = await command.ExecuteNonQueryAsync(cancellationToken);
            return rows > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private const string SelectColumns =
        "SELECT Id, Name, StreamUrl, Username, Password, IncludeAudio, PtzEnabled, OnvifPtzServiceUrl, OnvifProfileToken, CreatedAt";

    private static void AddParameters(SqliteCommand command, Camera camera)
    {
        command.Parameters.AddWithValue("$id", camera.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", camera.Name);
        command.Parameters.AddWithValue("$streamUrl", camera.StreamUrl);
        command.Parameters.AddWithValue("$username", (object?)camera.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$password", (object?)camera.Password ?? DBNull.Value);
        command.Parameters.AddWithValue("$includeAudio", camera.IncludeAudio ? 1 : 0);
        command.Parameters.AddWithValue("$ptzEnabled", camera.PtzEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$onvifPtzServiceUrl", (object?)camera.OnvifPtzServiceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$onvifProfileToken", (object?)camera.OnvifProfileToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", camera.CreatedAt.UtcDateTime.ToString("O"));
    }

    private static Camera ReadCamera(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5) != 0,
        reader.GetInt64(6) != 0,
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        DateTimeOffset.Parse(reader.GetString(9)));

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
