using BlueIrisOllamaBridge;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge.Web;

public interface ITimelineReviewStore
{
    Task<HashSet<Guid>> GetReviewedAsync(IEnumerable<Guid> eventIds, CancellationToken cancellationToken);
    Task SetReviewedAsync(Guid eventId, bool reviewed, CancellationToken cancellationToken);
}

// Review state belongs to Sentinel rather than Blue Iris. Keeping it in a small shared database
// means it survives app reinstalls and is consistent on every phone, tablet, and Mac.
public sealed class SqliteTimelineReviewStore : ITimelineReviewStore
{
    private readonly string _connectionString;

    public SqliteTimelineReviewStore(IOptions<BridgeOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "timeline-reviews.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ReviewedEvents (
                EventId TEXT PRIMARY KEY,
                ReviewedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<HashSet<Guid>> GetReviewedAsync(
        IEnumerable<Guid> eventIds,
        CancellationToken cancellationToken)
    {
        var ids = eventIds.Distinct().ToArray();
        if (ids.Length == 0) return [];

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameters = new List<string>(ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            var name = $"$id{index}";
            parameters.Add(name);
            command.Parameters.AddWithValue(name, ids[index].ToString("N"));
        }
        command.CommandText = $"SELECT EventId FROM ReviewedEvents WHERE EventId IN ({string.Join(',', parameters)});";

        var reviewed = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            reviewed.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return reviewed;
    }

    public async Task SetReviewedAsync(Guid eventId, bool reviewed, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$eventId", eventId.ToString("N"));
        if (reviewed)
        {
            command.CommandText = """
                INSERT INTO ReviewedEvents (EventId, ReviewedAt)
                VALUES ($eventId, $reviewedAt)
                ON CONFLICT(EventId) DO UPDATE SET ReviewedAt = excluded.ReviewedAt;
                """;
            command.Parameters.AddWithValue("$reviewedAt", DateTimeOffset.UtcNow.ToString("O"));
        }
        else
        {
            command.CommandText = "DELETE FROM ReviewedEvents WHERE EventId = $eventId;";
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
