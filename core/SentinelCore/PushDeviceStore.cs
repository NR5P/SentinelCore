using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public sealed record PushDevice(
    Guid Id,
    string Platform,
    string Token,
    string? DeviceName,
    string AppVariant,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // The account signed in when the app registered, lower-case, or null with login off. It is
    // what lets a board automation notify one person's phones rather than everybody's.
    string? Username = null);

public interface IPushDeviceStore
{
    // appVariant distinguishes which Apple app registered the token (e.g. "ios" vs "tvos") so
    // PushNotificationDispatcher can pick the matching apns-topic - irrelevant for Android, where
    // it's just carried along unused. Defaults to "ios" so existing callers keep compiling.
    Task<PushDevice> UpsertAsync(
        string platform,
        string token,
        string? deviceName,
        CancellationToken cancellationToken,
        string appVariant = "ios",
        string? username = null);

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
                AppVariant TEXT NOT NULL DEFAULT 'ios',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                UNIQUE (Platform, Token)
            );
            CREATE INDEX IF NOT EXISTS IX_PushDevices_UpdatedAt ON PushDevices(UpdatedAt);
            """;
        command.ExecuteNonQuery();
        // Upgrade path for databases created before AppVariant existed.
        using var addColumn = connection.CreateCommand();
        addColumn.CommandText = "ALTER TABLE PushDevices ADD COLUMN AppVariant TEXT NOT NULL DEFAULT 'ios';";
        try { addColumn.ExecuteNonQuery(); } catch (SqliteException) { /* column already exists */ }
        // And before devices knew whose they were. Old rows stay null until the app next registers.
        using var addUsername = connection.CreateCommand();
        addUsername.CommandText = "ALTER TABLE PushDevices ADD COLUMN Username TEXT;";
        try { addUsername.ExecuteNonQuery(); } catch (SqliteException) { /* column already exists */ }
    }

    public async Task<PushDevice> UpsertAsync(
        string platform,
        string token,
        string? deviceName,
        CancellationToken cancellationToken,
        string appVariant = "ios",
        string? username = null)
    {
        var normalizedPlatform = PushPlatforms.Normalize(platform);
        var normalizedToken = token.Trim();
        var normalizedVariant = string.IsNullOrWhiteSpace(appVariant) ? "ios" : appVariant.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO PushDevices (Id, Platform, Token, DeviceName, AppVariant, CreatedAt, UpdatedAt, Username)
                VALUES ($id, $platform, $token, $deviceName, $appVariant, $createdAt, $updatedAt, $username)
                ON CONFLICT(Platform, Token) DO UPDATE SET
                    DeviceName = excluded.DeviceName,
                    AppVariant = excluded.AppVariant,
                    UpdatedAt = excluded.UpdatedAt,
                    Username = excluded.Username;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$platform", normalizedPlatform);
            command.Parameters.AddWithValue("$token", normalizedToken);
            command.Parameters.AddWithValue("$deviceName", (object?)deviceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$appVariant", normalizedVariant);
            command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            command.Parameters.AddWithValue("$username",
                string.IsNullOrWhiteSpace(username) ? DBNull.Value : username.Trim().ToLowerInvariant());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT Id, Platform, Token, DeviceName, AppVariant, CreatedAt, UpdatedAt, Username
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
            SELECT Id, Platform, Token, DeviceName, AppVariant, CreatedAt, UpdatedAt, Username
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
        reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5)),
        DateTimeOffset.Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7));
}
