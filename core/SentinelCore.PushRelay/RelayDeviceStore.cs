using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SentinelCore.PushRelay;

public sealed record RelayDevice(
    string Platform,
    string Token,
    Guid LicenseId,
    string CustomerName,
    string AppVariant,
    bool ApnsSandbox,
    DateTimeOffset UpdatedAt);

public enum RegisterOutcome
{
    Registered,
    // Another license holds this token and has refreshed it recently. See
    // RelayOptions.StaleTokenTakeoverDays for when this stops applying.
    OwnedByAnotherLicense,
    LimitReached
}

// One table: which license each device token belongs to. Modelled on the main app's
// SqlitePushDeviceStore; the difference is that here the unit of ownership is the license, not
// the install, because the relay is the one place where many installs meet.
public sealed class RelayDeviceStore
{
    private readonly string _connectionString;
    private readonly RelayOptions _options;

    public RelayDeviceStore(IOptions<RelayOptions> options, IHostEnvironment environment)
    {
        _options = options.Value;
        var configured = _options.DataDirectory;
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "relay-devices.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Devices (
                Platform TEXT NOT NULL,
                Token TEXT NOT NULL,
                LicenseId TEXT NOT NULL,
                CustomerName TEXT NOT NULL,
                AppVariant TEXT NOT NULL,
                ApnsSandbox INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (Platform, Token)
            );
            CREATE INDEX IF NOT EXISTS IX_Devices_LicenseId ON Devices(LicenseId);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<RegisterOutcome> RegisterAsync(
        Guid licenseId,
        string customerName,
        string platform,
        string token,
        string appVariant,
        bool apnsSandbox,
        CancellationToken cancellationToken)
    {
        platform = PushPlatforms.Normalize(platform);
        token = token.Trim();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await FindAsync(connection, platform, token, cancellationToken);
        if (existing is not null && existing.LicenseId != licenseId &&
            now - existing.UpdatedAt < TimeSpan.FromDays(_options.StaleTokenTakeoverDays))
        {
            return RegisterOutcome.OwnedByAnotherLicense;
        }

        if (existing is null || existing.LicenseId != licenseId)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Devices WHERE LicenseId = $licenseId;";
            count.Parameters.AddWithValue("$licenseId", licenseId.ToString("N"));
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) >= _options.MaxDevicesPerLicense)
                return RegisterOutcome.LimitReached;
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Devices (Platform, Token, LicenseId, CustomerName, AppVariant, ApnsSandbox, CreatedAt, UpdatedAt)
                VALUES ($platform, $token, $licenseId, $customerName, $appVariant, $apnsSandbox, $now, $now)
                ON CONFLICT(Platform, Token) DO UPDATE SET
                    LicenseId = excluded.LicenseId,
                    CustomerName = excluded.CustomerName,
                    AppVariant = excluded.AppVariant,
                    ApnsSandbox = excluded.ApnsSandbox,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$token", token);
            command.Parameters.AddWithValue("$licenseId", licenseId.ToString("N"));
            command.Parameters.AddWithValue("$customerName", customerName);
            command.Parameters.AddWithValue("$appVariant", appVariant);
            command.Parameters.AddWithValue("$apnsSandbox", apnsSandbox ? 1 : 0);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return RegisterOutcome.Registered;
    }

    public async Task<RelayDevice?> FindAsync(string platform, string token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await FindAsync(connection, PushPlatforms.Normalize(platform), token.Trim(), cancellationToken);
    }

    public async Task RemoveAsync(string platform, string token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Devices WHERE Platform = $platform AND Token = $token;";
        command.Parameters.AddWithValue("$platform", PushPlatforms.Normalize(platform));
        command.Parameters.AddWithValue("$token", token.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Every registration a license holds - see RelayOptions.BlockedLicenseIds.
    public async Task<int> RemoveLicenseAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Devices WHERE LicenseId = $licenseId;";
        command.Parameters.AddWithValue("$licenseId", licenseId.ToString("N"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Devices WHERE LicenseId = $licenseId;";
        command.Parameters.AddWithValue("$licenseId", licenseId.ToString("N"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<RelayDevice?> FindAsync(
        SqliteConnection connection, string platform, string token, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Platform, Token, LicenseId, CustomerName, AppVariant, ApnsSandbox, UpdatedAt
            FROM Devices
            WHERE Platform = $platform AND Token = $token;
            """;
        command.Parameters.AddWithValue("$platform", platform);
        command.Parameters.AddWithValue("$token", token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new RelayDevice(
            reader.GetString(0),
            reader.GetString(1),
            Guid.ParseExact(reader.GetString(2), "N"),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5) != 0,
            DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture));
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
}
