using Microsoft.Data.Sqlite;

namespace BlueIrisOllamaBridge.Web;

// Cameras added here are purely for live viewing in this dashboard - deliberately unrelated
// to the Blue Iris alert pipeline the main app runs. Stored in their own SQLite file, separate
// from events.db, since the main app has no reason to know these exist.
public sealed record Camera(
    Guid Id,
    string Name,
    string StreamUrl,
    string? Username,
    string? Password,
    bool IncludeAudio,
    DateTimeOffset CreatedAt);

public interface ICameraStore
{
    Task<IReadOnlyList<Camera>> GetAllAsync(CancellationToken cancellationToken);
    Task<Camera?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Camera> AddAsync(string name, string streamUrl, string? username, string? password, bool includeAudio, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(Guid id, string name, string streamUrl, string? username, string? password, bool includeAudio, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
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
    }

    public async Task<IReadOnlyList<Camera>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, StreamUrl, Username, Password, IncludeAudio, CreatedAt FROM Cameras ORDER BY Name ASC;";

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
            command.CommandText = "SELECT Id, Name, StreamUrl, Username, Password, IncludeAudio, CreatedAt FROM Cameras WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadCamera(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Camera> AddAsync(string name, string streamUrl, string? username, string? password, bool includeAudio, CancellationToken cancellationToken)
    {
        var camera = new Camera(Guid.NewGuid(), name, streamUrl, username, password, includeAudio, DateTimeOffset.UtcNow);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Cameras (Id, Name, StreamUrl, Username, Password, IncludeAudio, CreatedAt)
                VALUES ($id, $name, $streamUrl, $username, $password, $includeAudio, $createdAt);
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

    public async Task<bool> UpdateAsync(Guid id, string name, string streamUrl, string? username, string? password, bool includeAudio, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE Cameras SET Name = $name, StreamUrl = $streamUrl, Username = $username,
                    Password = $password, IncludeAudio = $includeAudio
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$streamUrl", streamUrl);
            command.Parameters.AddWithValue("$username", (object?)username ?? DBNull.Value);
            command.Parameters.AddWithValue("$password", (object?)password ?? DBNull.Value);
            command.Parameters.AddWithValue("$includeAudio", includeAudio ? 1 : 0);

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

    private static void AddParameters(SqliteCommand command, Camera camera)
    {
        command.Parameters.AddWithValue("$id", camera.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", camera.Name);
        command.Parameters.AddWithValue("$streamUrl", camera.StreamUrl);
        command.Parameters.AddWithValue("$username", (object?)camera.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$password", (object?)camera.Password ?? DBNull.Value);
        command.Parameters.AddWithValue("$includeAudio", camera.IncludeAudio ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", camera.CreatedAt.UtcDateTime.ToString("O"));
    }

    private static Camera ReadCamera(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5) != 0,
        DateTimeOffset.Parse(reader.GetString(6)));

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
