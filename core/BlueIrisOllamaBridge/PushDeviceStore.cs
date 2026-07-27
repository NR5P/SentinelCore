using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public static class PushPlatforms
{
    public const string Apple = "apple";
    public const string Android = "android";

    public static bool IsSupported(string value) =>
        string.Equals(value, Apple, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Android, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

public sealed record PushDevice(
    Guid Id,
    string Platform,
    string Token,
    string? DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IPushDeviceStore
{
    Task<PushDevice> UpsertAsync(
        string platform,
        string token,
        string? deviceName,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PushDevice>> GetAllAsync(CancellationToken cancellationToken);
    Task<int> CountAsync(CancellationToken cancellationToken);
    Task RemoveAsync(string platform, string token, CancellationToken cancellationToken);
}

public sealed class SqlitePushDeviceStore : IPushDeviceStore
{
    private readonly string _connectionString;

    public SqlitePushDeviceStore(IOptions<BridgeOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "push-devices.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PushDevices (
                Id TEXT PRIMARY KEY,
                Platform TEXT NOT NULL,
                Token TEXT NOT NULL,
                DeviceName TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                UNIQUE (Platform, Token)
            );
            CREATE INDEX IF NOT EXISTS IX_PushDevices_UpdatedAt ON PushDevices(UpdatedAt);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<PushDevice> UpsertAsync(
        string platform,
        string token,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        var normalizedPlatform = PushPlatforms.Normalize(platform);
        var normalizedToken = token.Trim();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO PushDevices (Id, Platform, Token, DeviceName, CreatedAt, UpdatedAt)
                VALUES ($id, $platform, $token, $deviceName, $createdAt, $updatedAt)
                ON CONFLICT(Platform, Token) DO UPDATE SET
                    DeviceName = excluded.DeviceName,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$platform", normalizedPlatform);
            command.Parameters.AddWithValue("$token", normalizedToken);
            command.Parameters.AddWithValue("$deviceName", (object?)deviceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT Id, Platform, Token, DeviceName, CreatedAt, UpdatedAt
            FROM PushDevices
            WHERE Platform = $platform AND Token = $token;
            """;
        select.Parameters.AddWithValue("$platform", normalizedPlatform);
        select.Parameters.AddWithValue("$token", normalizedToken);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The push device was not saved.");
        return ReadDevice(reader);
    }

    public async Task<IReadOnlyList<PushDevice>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Platform, Token, DeviceName, CreatedAt, UpdatedAt
            FROM PushDevices
            ORDER BY UpdatedAt DESC;
            """;

        var devices = new List<PushDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            devices.Add(ReadDevice(reader));
        return devices;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PushDevices;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task RemoveAsync(string platform, string token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PushDevices WHERE Platform = $platform AND Token = $token;";
        command.Parameters.AddWithValue("$platform", PushPlatforms.Normalize(platform));
        command.Parameters.AddWithValue("$token", token.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static PushDevice ReadDevice(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4)),
        DateTimeOffset.Parse(reader.GetString(5)));
}
