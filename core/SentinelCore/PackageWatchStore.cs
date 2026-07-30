using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public static class PackageWatchStatus
{
    public const string Watching = "watching";
    public const string PickedUp = "picked-up";
    public const string TheftSuspected = "theft-suspected";
}

public sealed record PackageWatch(
    Guid Id,
    string Camera,
    DateTimeOffset DeliveredAt,
    DateTimeOffset ExpiresAt,
    string Status);

public interface IPackageWatchStore
{
    // Only ever the most recent still-open watch for this camera - a camera can only have one
    // "waiting to see who picks this up" watch active at a time (see AlertProcessor).
    Task<PackageWatch?> GetActiveAsync(string camera, CancellationToken cancellationToken);

    // Starts a new watch, or - if one is already open for this camera - extends its expiry
    // rather than creating a second one, so re-observing the same still-present package doesn't
    // spawn duplicate watches.
    Task<PackageWatch> StartOrExtendAsync(string camera, DateTimeOffset deliveredAt, TimeSpan window, CancellationToken cancellationToken);

    Task SetStatusAsync(Guid id, string status, CancellationToken cancellationToken);
}

public sealed class SqlitePackageWatchStore : IPackageWatchStore
{
    private readonly string _connectionString;

    public SqlitePackageWatchStore(IOptions<BridgeOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "package-watches.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PackageWatches (
                Id TEXT PRIMARY KEY,
                Camera TEXT NOT NULL,
                DeliveredAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                Status TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PackageWatches_Camera_Status ON PackageWatches(Camera, Status);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<PackageWatch?> GetActiveAsync(string camera, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Camera, DeliveredAt, ExpiresAt, Status FROM PackageWatches
            WHERE Camera = $camera AND Status = $watching AND ExpiresAt > $now
            ORDER BY DeliveredAt DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$camera", camera);
        command.Parameters.AddWithValue("$watching", PackageWatchStatus.Watching);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWatch(reader) : null;
    }

    public async Task<PackageWatch> StartOrExtendAsync(string camera, DateTimeOffset deliveredAt, TimeSpan window, CancellationToken cancellationToken)
    {
        var existing = await GetActiveAsync(camera, cancellationToken);
        var expiresAt = deliveredAt + window;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (existing is not null)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE PackageWatches SET ExpiresAt = $expiresAt WHERE Id = $id;";
            update.Parameters.AddWithValue("$id", existing.Id.ToString("N"));
            update.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
            return existing with { ExpiresAt = expiresAt };
        }

        var watch = new PackageWatch(Guid.NewGuid(), camera, deliveredAt, expiresAt, PackageWatchStatus.Watching);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO PackageWatches (Id, Camera, DeliveredAt, ExpiresAt, Status)
            VALUES ($id, $camera, $deliveredAt, $expiresAt, $status);
            """;
        insert.Parameters.AddWithValue("$id", watch.Id.ToString("N"));
        insert.Parameters.AddWithValue("$camera", watch.Camera);
        insert.Parameters.AddWithValue("$deliveredAt", watch.DeliveredAt.ToString("O"));
        insert.Parameters.AddWithValue("$expiresAt", watch.ExpiresAt.ToString("O"));
        insert.Parameters.AddWithValue("$status", watch.Status);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return watch;
    }

    public async Task SetStatusAsync(Guid id, string status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PackageWatches SET Status = $status WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.Parameters.AddWithValue("$status", status);
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

    private static PackageWatch ReadWatch(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        DateTimeOffset.Parse(reader.GetString(2)),
        DateTimeOffset.Parse(reader.GetString(3)),
        reader.GetString(4));
}
