using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// What the catalog is asked for. Everything here is a column filter; sorting, paging and the
// per-person watched state are applied by MediaEndpoints on top, because the catalog is a few
// thousand rows at most and "unwatched only" depends on who is asking.
public sealed record MediaQuery(
    IReadOnlyList<string>? Kinds = null,
    Guid? LibraryId = null,
    Guid? ParentId = null,
    string? Search = null,
    string? Genre = null,
    bool IncludeMissing = false);

public interface IMediaStore
{
    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken cancellationToken);
    Task<MediaLibrary?> GetLibraryAsync(Guid id, CancellationToken cancellationToken);
    Task SaveLibraryAsync(MediaLibrary library, CancellationToken cancellationToken);
    Task DeleteLibraryAsync(Guid id, CancellationToken cancellationToken);

    Task<MediaItem?> GetItemAsync(Guid id, CancellationToken cancellationToken);
    Task DeleteItemAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaItem>> GetItemsAsync(MediaQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaItem>> GetItemsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);
    Task UpsertItemsAsync(IEnumerable<MediaItem> items, CancellationToken cancellationToken);
    Task UpdateMetadataAsync(MediaItem item, CancellationToken cancellationToken);
    Task UpdateProbeAsync(MediaItem item, CancellationToken cancellationToken);
    // Marks every item of the library that is not in `presentIds` missing, and un-marks the ones
    // that are. Returns how many went missing.
    Task<int> ReconcileMissingAsync(Guid libraryId, IReadOnlySet<Guid> presentIds, CancellationToken cancellationToken);
    Task<int> PurgeMissingAsync(DateTimeOffset missingSince, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaItem>> ItemsNeedingMetadataAsync(DateTimeOffset retryFailedBefore, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaItem>> ItemsNeedingProbeAsync(int limit, CancellationToken cancellationToken);
    Task<(int Movies, int Shows, int Episodes)> CountsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GenresAsync(string? kind, Guid? libraryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaProgress>> GetProgressAsync(string userKey, CancellationToken cancellationToken);
    Task SetProgressAsync(MediaProgress progress, CancellationToken cancellationToken);
    Task DeleteProgressAsync(string userKey, Guid itemId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaProgress>> AllProgressAsync(CancellationToken cancellationToken);
    // Newest edit wins, so restoring an old backup never rolls anyone back.
    Task<int> RestoreProgressAsync(IEnumerable<MediaProgress> rows, CancellationToken cancellationToken);

    // Records a play unless the same person already started the same item within `dedupe` (a
    // seek, an audio-track change or a reconnect asks for playback again). True when recorded.
    Task<bool> RecordPlayAsync(MediaPlay play, TimeSpan dedupe, CancellationToken cancellationToken);
    // Everyone's plays per item: "Most played" is a household shelf.
    Task<IReadOnlyDictionary<Guid, MediaPlayStats>> PlayStatsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaPlay>> AllPlaysAsync(CancellationToken cancellationToken);
    // Adds the plays that are not already there (same person, item and time).
    Task<int> RestorePlaysAsync(IEnumerable<MediaPlay> rows, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaWatchlistEntry>> GetWatchlistAsync(string userKey, CancellationToken cancellationToken);
    Task SetInWatchlistAsync(string userKey, Guid itemId, bool inList, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaWatchlistEntry>> AllWatchlistAsync(CancellationToken cancellationToken);
    Task<int> RestoreWatchlistAsync(IEnumerable<MediaWatchlistEntry> rows, CancellationToken cancellationToken);
}

public sealed class SqliteMediaStore : IMediaStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteMediaStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "media.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS MediaLibraries (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Path TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastScanStartedAt TEXT,
                LastScanFinishedAt TEXT,
                LastScanError TEXT,
                ItemCount INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS MediaItems (
                Id TEXT PRIMARY KEY,
                LibraryId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                ParentId TEXT,
                Title TEXT NOT NULL,
                SortTitle TEXT NOT NULL,
                Year INTEGER,
                SeasonNumber INTEGER,
                EpisodeNumber INTEGER,
                Path TEXT NOT NULL,
                FileSize INTEGER,
                FileModifiedAt TEXT,
                AddedAt TEXT NOT NULL,
                Missing INTEGER NOT NULL DEFAULT 0,
                MissingSince TEXT,
                TmdbId INTEGER,
                Rating REAL,
                Genres TEXT NOT NULL DEFAULT '[]',
                MetadataFetchedAt TEXT,
                MetadataLocked INTEGER NOT NULL DEFAULT 0,
                ProbedAt TEXT,
                DurationSeconds REAL,
                Metadata TEXT NOT NULL DEFAULT '{}',
                Artwork TEXT NOT NULL DEFAULT '{}',
                Probe TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS IX_MediaItems_Library ON MediaItems (LibraryId, Kind);
            CREATE INDEX IF NOT EXISTS IX_MediaItems_Parent ON MediaItems (ParentId, SeasonNumber, EpisodeNumber);

            CREATE TABLE IF NOT EXISTS MediaProgress (
                UserKey TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                PositionSeconds REAL NOT NULL,
                DurationSeconds REAL,
                Watched INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (UserKey, ItemId)
            );

            CREATE TABLE IF NOT EXISTS MediaState (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MediaWatchlist (
                UserKey TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                AddedAt TEXT NOT NULL,
                PRIMARY KEY (UserKey, ItemId)
            );
            """;
        command.ExecuteNonQuery();

        // Play history arrived after people had already been watching. A new table is seeded with
        // one play per existing progress row, so "Most played" means something on day one.
        using var exists = _connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'MediaPlays'";
        var hadPlays = (long)exists.ExecuteScalar()! > 0;
        using var plays = _connection.CreateCommand();
        plays.CommandText = """
            CREATE TABLE IF NOT EXISTS MediaPlays (
                UserKey TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                PlayedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_MediaPlays_Item ON MediaPlays (ItemId, UserKey, PlayedAt);
            """
            + (hadPlays ? "" : "INSERT INTO MediaPlays (UserKey, ItemId, PlayedAt) SELECT UserKey, ItemId, UpdatedAt FROM MediaProgress;");
        plays.ExecuteNonQuery();

        // The frame-order check (MediaItem.TimestampsInDecodeOrder) arrived after the library was
        // probed: send the files whose video can be copied as-is back through the probe once.
        // AVI is left out - it has no frame timestamps to get wrong, and is re-encoded anyway.
        using var reprobe = _connection.CreateCommand();
        reprobe.CommandText = """
            UPDATE MediaItems SET ProbedAt = NULL
            WHERE ProbedAt IS NOT NULL
              AND json_extract(Probe, '$.videoCodec') IN ('h264', 'hevc')
              AND COALESCE(json_extract(Probe, '$.container'), '') <> 'avi'
              AND NOT EXISTS (SELECT 1 FROM MediaState WHERE Key = 'probe.frameOrderChecked');
            INSERT OR IGNORE INTO MediaState (Key, Value) VALUES ('probe.frameOrderChecked', '1');
            """;
        reprobe.ExecuteNonQuery();
    }

    // MARK: - Libraries

    public async Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MediaLibraries ORDER BY CreatedAt";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaLibrary>();
            while (await reader.ReadAsync(cancellationToken)) list.Add(ReadLibrary(reader));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<MediaLibrary?> GetLibraryAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MediaLibraries WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadLibrary(reader) : null;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveLibraryAsync(MediaLibrary library, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MediaLibraries (Id, Name, Kind, Path, CreatedAt, LastScanStartedAt, LastScanFinishedAt, LastScanError, ItemCount)
                VALUES ($id, $name, $kind, $path, $created, $started, $finished, $error, $count)
                ON CONFLICT(Id) DO UPDATE SET Name = excluded.Name, Kind = excluded.Kind, Path = excluded.Path,
                    LastScanStartedAt = excluded.LastScanStartedAt, LastScanFinishedAt = excluded.LastScanFinishedAt,
                    LastScanError = excluded.LastScanError, ItemCount = excluded.ItemCount
                """;
            command.Parameters.AddWithValue("$id", library.Id.ToString("N"));
            command.Parameters.AddWithValue("$name", library.Name);
            command.Parameters.AddWithValue("$kind", library.Kind);
            command.Parameters.AddWithValue("$path", library.Path);
            command.Parameters.AddWithValue("$created", library.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$started", Text(library.LastScanStartedAt));
            command.Parameters.AddWithValue("$finished", Text(library.LastScanFinishedAt));
            command.Parameters.AddWithValue("$error", (object?)library.LastScanError ?? DBNull.Value);
            command.Parameters.AddWithValue("$count", library.ItemCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteLibraryAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM MediaProgress WHERE ItemId IN (SELECT Id FROM MediaItems WHERE LibraryId = $id);
                DELETE FROM MediaPlays WHERE ItemId IN (SELECT Id FROM MediaItems WHERE LibraryId = $id);
                DELETE FROM MediaWatchlist WHERE ItemId IN (SELECT Id FROM MediaItems WHERE LibraryId = $id);
                DELETE FROM MediaItems WHERE LibraryId = $id;
                DELETE FROM MediaLibraries WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    // MARK: - Items

    // Forgets one title and everything people did with it (its files are the caller's to delete).
    public async Task DeleteItemAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM MediaProgress WHERE ItemId IN (SELECT Id FROM MediaItems WHERE Id = $id OR ParentId = $id);
                DELETE FROM MediaPlays WHERE ItemId IN (SELECT Id FROM MediaItems WHERE Id = $id OR ParentId = $id);
                DELETE FROM MediaWatchlist WHERE ItemId IN (SELECT Id FROM MediaItems WHERE Id = $id OR ParentId = $id);
                DELETE FROM MediaItems WHERE Id = $id OR ParentId = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task<MediaItem?> GetItemAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MediaItems WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MediaItem>> GetItemsAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            var where = new List<string>();
            if (!query.IncludeMissing) where.Add("Missing = 0");
            if (query.Kinds is { Count: > 0 })
            {
                var names = new List<string>();
                for (var i = 0; i < query.Kinds.Count; i++)
                {
                    names.Add($"$kind{i}");
                    command.Parameters.AddWithValue($"$kind{i}", query.Kinds[i]);
                }
                where.Add($"Kind IN ({string.Join(",", names)})");
            }
            if (query.LibraryId is { } library)
            {
                where.Add("LibraryId = $library");
                command.Parameters.AddWithValue("$library", library.ToString("N"));
            }
            if (query.ParentId is { } parent)
            {
                where.Add("ParentId = $parent");
                command.Parameters.AddWithValue("$parent", parent.ToString("N"));
            }
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                where.Add("Title LIKE $search ESCAPE '\\'");
                command.Parameters.AddWithValue("$search", "%" + EscapeLike(query.Search.Trim()) + "%");
            }
            if (!string.IsNullOrWhiteSpace(query.Genre))
            {
                where.Add("Genres LIKE $genre ESCAPE '\\'");
                command.Parameters.AddWithValue("$genre", "%" + EscapeLike(JsonSerializer.Serialize(query.Genre.Trim())) + "%");
            }
            command.CommandText = "SELECT * FROM MediaItems" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                + " ORDER BY SortTitle, SeasonNumber, EpisodeNumber";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaItem>();
            while (await reader.ReadAsync(cancellationToken)) list.Add(ReadItem(reader));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MediaItem>> GetItemsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var list = new List<MediaItem>();
            // SQLite caps bound parameters; a few hundred per statement keeps well under it.
            foreach (var chunk in wanted.Chunk(200))
            {
                using var command = _connection.CreateCommand();
                var names = new List<string>();
                for (var i = 0; i < chunk.Length; i++)
                {
                    names.Add($"$id{i}");
                    command.Parameters.AddWithValue($"$id{i}", chunk[i].ToString("N"));
                }
                command.CommandText = $"SELECT * FROM MediaItems WHERE Id IN ({string.Join(",", names)})";
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) list.Add(ReadItem(reader));
            }
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task UpsertItemsAsync(IEnumerable<MediaItem> items, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var item in items)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                // A rescan rewrites what the files say (title, path, size, season) and leaves what
                // TMDB and ffprobe said alone - those columns are only in the INSERT half.
                command.CommandText = """
                    INSERT INTO MediaItems (Id, LibraryId, Kind, ParentId, Title, SortTitle, Year, SeasonNumber, EpisodeNumber, Path,
                        FileSize, FileModifiedAt, AddedAt, Missing, MissingSince, TmdbId, Rating, Genres, MetadataFetchedAt, MetadataLocked,
                        ProbedAt, DurationSeconds, Metadata, Artwork, Probe)
                    VALUES ($id, $library, $kind, $parent, $title, $sort, $year, $season, $episode, $path,
                        $size, $modified, $added, 0, NULL, $tmdb, $rating, $genres, $fetched, $locked,
                        $probed, $duration, $metadata, $artwork, $probe)
                    ON CONFLICT(Id) DO UPDATE SET
                        LibraryId = excluded.LibraryId, Kind = excluded.Kind, ParentId = excluded.ParentId,
                        Title = CASE WHEN MetadataFetchedAt IS NULL OR TmdbId IS NULL THEN excluded.Title ELSE Title END,
                        SortTitle = CASE WHEN MetadataFetchedAt IS NULL OR TmdbId IS NULL THEN excluded.SortTitle ELSE SortTitle END,
                        Year = COALESCE(Year, excluded.Year),
                        SeasonNumber = excluded.SeasonNumber, EpisodeNumber = excluded.EpisodeNumber,
                        Path = excluded.Path, FileSize = excluded.FileSize, FileModifiedAt = excluded.FileModifiedAt,
                        Missing = 0, MissingSince = NULL,
                        Artwork = excluded.Artwork,
                        ProbedAt = CASE WHEN FileSize IS excluded.FileSize AND FileModifiedAt IS excluded.FileModifiedAt THEN ProbedAt ELSE NULL END
                    """;
                command.Parameters.AddWithValue("$id", item.Id.ToString("N"));
                command.Parameters.AddWithValue("$library", item.LibraryId.ToString("N"));
                command.Parameters.AddWithValue("$kind", item.Kind);
                command.Parameters.AddWithValue("$parent", item.ParentId is { } parent ? parent.ToString("N") : DBNull.Value);
                command.Parameters.AddWithValue("$title", item.Title);
                command.Parameters.AddWithValue("$sort", item.SortTitle);
                command.Parameters.AddWithValue("$year", (object?)item.Year ?? DBNull.Value);
                command.Parameters.AddWithValue("$season", (object?)item.SeasonNumber ?? DBNull.Value);
                command.Parameters.AddWithValue("$episode", (object?)item.EpisodeNumber ?? DBNull.Value);
                command.Parameters.AddWithValue("$path", item.Path);
                command.Parameters.AddWithValue("$size", (object?)item.FileSize ?? DBNull.Value);
                command.Parameters.AddWithValue("$modified", Text(item.FileModifiedAt));
                command.Parameters.AddWithValue("$added", item.AddedAt.ToString("O"));
                command.Parameters.AddWithValue("$tmdb", (object?)item.TmdbId ?? DBNull.Value);
                command.Parameters.AddWithValue("$rating", (object?)item.Rating ?? DBNull.Value);
                command.Parameters.AddWithValue("$genres", JsonSerializer.Serialize(item.Genres, JsonOptions));
                command.Parameters.AddWithValue("$fetched", Text(item.MetadataFetchedAt));
                command.Parameters.AddWithValue("$locked", item.MetadataLocked ? 1 : 0);
                command.Parameters.AddWithValue("$probed", Text(item.ProbedAt));
                command.Parameters.AddWithValue("$duration", (object?)item.DurationSeconds ?? DBNull.Value);
                command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(MetadataBlob.From(item), JsonOptions));
                command.Parameters.AddWithValue("$artwork", JsonSerializer.Serialize(ArtworkBlob.From(item), JsonOptions));
                command.Parameters.AddWithValue("$probe", JsonSerializer.Serialize(ProbeBlob.From(item), JsonOptions));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateMetadataAsync(MediaItem item, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE MediaItems SET Title = $title, SortTitle = $sort, Year = $year, TmdbId = $tmdb, Rating = $rating, Genres = $genres,
                    MetadataFetchedAt = $fetched, MetadataLocked = $locked, Metadata = $metadata, Artwork = $artwork
                WHERE Id = $id
                """;
            command.Parameters.AddWithValue("$id", item.Id.ToString("N"));
            command.Parameters.AddWithValue("$title", item.Title);
            command.Parameters.AddWithValue("$sort", item.SortTitle);
            command.Parameters.AddWithValue("$year", (object?)item.Year ?? DBNull.Value);
            command.Parameters.AddWithValue("$tmdb", (object?)item.TmdbId ?? DBNull.Value);
            command.Parameters.AddWithValue("$rating", (object?)item.Rating ?? DBNull.Value);
            command.Parameters.AddWithValue("$genres", JsonSerializer.Serialize(item.Genres, JsonOptions));
            command.Parameters.AddWithValue("$fetched", Text(item.MetadataFetchedAt));
            command.Parameters.AddWithValue("$locked", item.MetadataLocked ? 1 : 0);
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(MetadataBlob.From(item), JsonOptions));
            command.Parameters.AddWithValue("$artwork", JsonSerializer.Serialize(ArtworkBlob.From(item), JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateProbeAsync(MediaItem item, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE MediaItems SET ProbedAt = $probed, DurationSeconds = $duration, Probe = $probe WHERE Id = $id";
            command.Parameters.AddWithValue("$id", item.Id.ToString("N"));
            command.Parameters.AddWithValue("$probed", Text(item.ProbedAt));
            command.Parameters.AddWithValue("$duration", (object?)item.DurationSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$probe", JsonSerializer.Serialize(ProbeBlob.From(item), JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> ReconcileMissingAsync(Guid libraryId, IReadOnlySet<Guid> presentIds, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            var wentMissing = 0;
            using (var select = _connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT Id, Missing FROM MediaItems WHERE LibraryId = $library";
                select.Parameters.AddWithValue("$library", libraryId.ToString("N"));
                var toMark = new List<string>();
                using (var reader = await select.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var id = reader.GetString(0);
                        var missing = reader.GetInt64(1) != 0;
                        if (!presentIds.Contains(Guid.ParseExact(id, "N")) && !missing) toMark.Add(id);
                    }
                }
                foreach (var id in toMark)
                {
                    using var update = _connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE MediaItems SET Missing = 1, MissingSince = $since WHERE Id = $id";
                    update.Parameters.AddWithValue("$id", id);
                    update.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.ToString("O"));
                    await update.ExecuteNonQueryAsync(cancellationToken);
                    wentMissing++;
                }
            }
            transaction.Commit();
            return wentMissing;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> PurgeMissingAsync(DateTimeOffset missingSince, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM MediaItems WHERE Missing = 1 AND MissingSince IS NOT NULL AND MissingSince < $since";
            command.Parameters.AddWithValue("$since", missingSince.ToString("O"));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MediaItem>> ItemsNeedingMetadataAsync(DateTimeOffset retryFailedBefore, int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            // Shows before their episodes, since an episode's metadata comes from its show's match.
            command.CommandText = """
                SELECT * FROM MediaItems
                WHERE Missing = 0 AND MetadataLocked = 0
                  AND (MetadataFetchedAt IS NULL OR (TmdbId IS NULL AND MetadataFetchedAt < $retry))
                ORDER BY CASE Kind WHEN 'show' THEN 0 WHEN 'movie' THEN 1 ELSE 2 END, AddedAt DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$retry", retryFailedBefore.ToString("O"));
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaItem>();
            while (await reader.ReadAsync(cancellationToken)) list.Add(ReadItem(reader));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MediaItem>> ItemsNeedingProbeAsync(int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM MediaItems
                WHERE Missing = 0 AND Kind IN ('movie', 'episode') AND ProbedAt IS NULL
                ORDER BY AddedAt DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaItem>();
            while (await reader.ReadAsync(cancellationToken)) list.Add(ReadItem(reader));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<(int Movies, int Shows, int Episodes)> CountsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Kind, COUNT(*) FROM MediaItems WHERE Missing = 0 GROUP BY Kind";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            int movies = 0, shows = 0, episodes = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var count = (int)reader.GetInt64(1);
                switch (reader.GetString(0))
                {
                    case MediaKinds.Movie: movies = count; break;
                    case MediaKinds.Show: shows = count; break;
                    case MediaKinds.Episode: episodes = count; break;
                }
            }
            return (movies, shows, episodes);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<string>> GenresAsync(string? kind, Guid? libraryId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            var where = new List<string> { "Missing = 0", "Kind IN ('movie', 'show')" };
            if (kind is not null) { where.Add("Kind = $kind"); command.Parameters.AddWithValue("$kind", kind); }
            if (libraryId is { } library) { where.Add("LibraryId = $library"); command.Parameters.AddWithValue("$library", library.ToString("N")); }
            command.CommandText = "SELECT DISTINCT Genres FROM MediaItems WHERE " + string.Join(" AND ", where);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var genres = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(cancellationToken))
                foreach (var genre in JsonSerializer.Deserialize<List<string>>(reader.GetString(0), JsonOptions) ?? [])
                    genres.Add(genre);
            return [.. genres];
        }
        finally { _lock.Release(); }
    }

    // MARK: - Progress

    public async Task<IReadOnlyList<MediaProgress>> GetProgressAsync(string userKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MediaProgress WHERE UserKey = $user ORDER BY UpdatedAt DESC";
            command.Parameters.AddWithValue("$user", userKey);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaProgress>();
            while (await reader.ReadAsync(cancellationToken)) list.Add(ReadProgress(reader));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task SetProgressAsync(MediaProgress progress, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MediaProgress (UserKey, ItemId, PositionSeconds, DurationSeconds, Watched, UpdatedAt)
                VALUES ($user, $item, $position, $duration, $watched, $updated)
                ON CONFLICT(UserKey, ItemId) DO UPDATE SET PositionSeconds = excluded.PositionSeconds,
                    DurationSeconds = excluded.DurationSeconds, Watched = excluded.Watched, UpdatedAt = excluded.UpdatedAt
                """;
            AddProgressParameters(command, progress);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteProgressAsync(string userKey, Guid itemId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM MediaProgress WHERE UserKey = $user AND ItemId = $item";
            command.Parameters.AddWithValue("$user", userKey);
            command.Parameters.AddWithValue("$item", itemId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MediaProgress>> AllProgressAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MediaProgress ORDER BY UserKey, UpdatedAt DESC";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaProgress>();
            while (await reader.ReadAsync(cancellationToken)) list.Add(ReadProgress(reader));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RestoreProgressAsync(IEnumerable<MediaProgress> rows, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            var restored = 0;
            foreach (var row in rows)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO MediaProgress (UserKey, ItemId, PositionSeconds, DurationSeconds, Watched, UpdatedAt)
                    VALUES ($user, $item, $position, $duration, $watched, $updated)
                    ON CONFLICT(UserKey, ItemId) DO UPDATE SET PositionSeconds = excluded.PositionSeconds,
                        DurationSeconds = excluded.DurationSeconds, Watched = excluded.Watched, UpdatedAt = excluded.UpdatedAt
                    WHERE excluded.UpdatedAt > MediaProgress.UpdatedAt
                    """;
                AddProgressParameters(command, row);
                restored += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            return restored;
        }
        finally { _lock.Release(); }
    }

    // MARK: - Plays

    public async Task<bool> RecordPlayAsync(MediaPlay play, TimeSpan dedupe, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MediaPlays (UserKey, ItemId, PlayedAt)
                SELECT $user, $item, $at
                WHERE NOT EXISTS (SELECT 1 FROM MediaPlays WHERE UserKey = $user AND ItemId = $item AND PlayedAt > $since)
                """;
            command.Parameters.AddWithValue("$user", play.UserKey);
            command.Parameters.AddWithValue("$item", play.ItemId.ToString("N"));
            command.Parameters.AddWithValue("$at", play.PlayedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$since", (play.PlayedAt - dedupe).ToUniversalTime().ToString("O"));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<Guid, MediaPlayStats>> PlayStatsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT ItemId, COUNT(*), MAX(PlayedAt) FROM MediaPlays GROUP BY ItemId";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var stats = new Dictionary<Guid, MediaPlayStats>();
            while (await reader.ReadAsync(cancellationToken))
                stats[Guid.ParseExact(reader.GetString(0), "N")] =
                    new MediaPlayStats((int)reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2)));
            return stats;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MediaPlay>> AllPlaysAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT UserKey, ItemId, PlayedAt FROM MediaPlays ORDER BY PlayedAt";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaPlay>();
            while (await reader.ReadAsync(cancellationToken))
                list.Add(new MediaPlay(reader.GetString(0), Guid.ParseExact(reader.GetString(1), "N"), DateTimeOffset.Parse(reader.GetString(2))));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RestorePlaysAsync(IEnumerable<MediaPlay> rows, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            var restored = 0;
            foreach (var row in rows)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO MediaPlays (UserKey, ItemId, PlayedAt)
                    SELECT $user, $item, $at
                    WHERE NOT EXISTS (SELECT 1 FROM MediaPlays WHERE UserKey = $user AND ItemId = $item AND PlayedAt = $at)
                    """;
                command.Parameters.AddWithValue("$user", row.UserKey);
                command.Parameters.AddWithValue("$item", row.ItemId.ToString("N"));
                command.Parameters.AddWithValue("$at", row.PlayedAt.ToUniversalTime().ToString("O"));
                restored += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            return restored;
        }
        finally { _lock.Release(); }
    }

    // MARK: - My List

    public async Task<IReadOnlyList<MediaWatchlistEntry>> GetWatchlistAsync(string userKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT UserKey, ItemId, AddedAt FROM MediaWatchlist WHERE UserKey = $user ORDER BY AddedAt DESC";
            command.Parameters.AddWithValue("$user", userKey);
            return await ReadWatchlistAsync(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task SetInWatchlistAsync(string userKey, Guid itemId, bool inList, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            // Adding what is already there keeps its original place in the list.
            command.CommandText = inList
                ? "INSERT OR IGNORE INTO MediaWatchlist (UserKey, ItemId, AddedAt) VALUES ($user, $item, $at)"
                : "DELETE FROM MediaWatchlist WHERE UserKey = $user AND ItemId = $item";
            command.Parameters.AddWithValue("$user", userKey);
            command.Parameters.AddWithValue("$item", itemId.ToString("N"));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MediaWatchlistEntry>> AllWatchlistAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT UserKey, ItemId, AddedAt FROM MediaWatchlist ORDER BY UserKey, AddedAt DESC";
            return await ReadWatchlistAsync(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RestoreWatchlistAsync(IEnumerable<MediaWatchlistEntry> rows, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            var restored = 0;
            foreach (var row in rows)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO MediaWatchlist (UserKey, ItemId, AddedAt) VALUES ($user, $item, $at)";
                command.Parameters.AddWithValue("$user", row.UserKey);
                command.Parameters.AddWithValue("$item", row.ItemId.ToString("N"));
                command.Parameters.AddWithValue("$at", row.AddedAt.ToUniversalTime().ToString("O"));
                restored += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            return restored;
        }
        finally { _lock.Release(); }
    }

    private static async Task<IReadOnlyList<MediaWatchlistEntry>> ReadWatchlistAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<MediaWatchlistEntry>();
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new MediaWatchlistEntry(reader.GetString(0), Guid.ParseExact(reader.GetString(1), "N"), DateTimeOffset.Parse(reader.GetString(2))));
        return list;
    }

    // MARK: - Rows

    private static void AddProgressParameters(SqliteCommand command, MediaProgress progress)
    {
        command.Parameters.AddWithValue("$user", progress.UserKey);
        command.Parameters.AddWithValue("$item", progress.ItemId.ToString("N"));
        command.Parameters.AddWithValue("$position", progress.PositionSeconds);
        command.Parameters.AddWithValue("$duration", (object?)progress.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$watched", progress.Watched ? 1 : 0);
        command.Parameters.AddWithValue("$updated", progress.UpdatedAt.ToString("O"));
    }

    private static MediaLibrary ReadLibrary(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(reader.GetOrdinal("Id")), "N"),
        reader.GetString(reader.GetOrdinal("Name")),
        reader.GetString(reader.GetOrdinal("Kind")),
        reader.GetString(reader.GetOrdinal("Path")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        Date(reader, "LastScanStartedAt"),
        Date(reader, "LastScanFinishedAt"),
        NullableString(reader, "LastScanError"),
        (int)reader.GetInt64(reader.GetOrdinal("ItemCount")));

    private static MediaProgress ReadProgress(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("UserKey")),
        Guid.ParseExact(reader.GetString(reader.GetOrdinal("ItemId")), "N"),
        reader.GetDouble(reader.GetOrdinal("PositionSeconds")),
        reader.IsDBNull(reader.GetOrdinal("DurationSeconds")) ? null : reader.GetDouble(reader.GetOrdinal("DurationSeconds")),
        reader.GetInt64(reader.GetOrdinal("Watched")) != 0,
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))));

    private static MediaItem ReadItem(SqliteDataReader reader)
    {
        var item = new MediaItem
        {
            Id = Guid.ParseExact(reader.GetString(reader.GetOrdinal("Id")), "N"),
            LibraryId = Guid.ParseExact(reader.GetString(reader.GetOrdinal("LibraryId")), "N"),
            Kind = reader.GetString(reader.GetOrdinal("Kind")),
            ParentId = NullableString(reader, "ParentId") is { } parent ? Guid.ParseExact(parent, "N") : null,
            Title = reader.GetString(reader.GetOrdinal("Title")),
            SortTitle = reader.GetString(reader.GetOrdinal("SortTitle")),
            Year = NullableInt(reader, "Year"),
            SeasonNumber = NullableInt(reader, "SeasonNumber"),
            EpisodeNumber = NullableInt(reader, "EpisodeNumber"),
            Path = reader.GetString(reader.GetOrdinal("Path")),
            FileSize = reader.IsDBNull(reader.GetOrdinal("FileSize")) ? null : reader.GetInt64(reader.GetOrdinal("FileSize")),
            FileModifiedAt = Date(reader, "FileModifiedAt"),
            AddedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("AddedAt"))),
            Missing = reader.GetInt64(reader.GetOrdinal("Missing")) != 0,
            TmdbId = NullableInt(reader, "TmdbId"),
            Rating = reader.IsDBNull(reader.GetOrdinal("Rating")) ? null : reader.GetDouble(reader.GetOrdinal("Rating")),
            Genres = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("Genres")), JsonOptions) ?? [],
            MetadataFetchedAt = Date(reader, "MetadataFetchedAt"),
            MetadataLocked = reader.GetInt64(reader.GetOrdinal("MetadataLocked")) != 0,
            ProbedAt = Date(reader, "ProbedAt"),
            DurationSeconds = reader.IsDBNull(reader.GetOrdinal("DurationSeconds")) ? null : reader.GetDouble(reader.GetOrdinal("DurationSeconds"))
        };
        (JsonSerializer.Deserialize<MetadataBlob>(reader.GetString(reader.GetOrdinal("Metadata")), JsonOptions) ?? new()).ApplyTo(item);
        (JsonSerializer.Deserialize<ArtworkBlob>(reader.GetString(reader.GetOrdinal("Artwork")), JsonOptions) ?? new()).ApplyTo(item);
        (JsonSerializer.Deserialize<ProbeBlob>(reader.GetString(reader.GetOrdinal("Probe")), JsonOptions) ?? new()).ApplyTo(item);
        return item;
    }

    private static object Text(DateTimeOffset? value) => value is { } date ? date.ToString("O") : DBNull.Value;

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? NullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : (int)reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? Date(SqliteDataReader reader, string column) =>
        NullableString(reader, column) is { } text ? DateTimeOffset.Parse(text) : null;

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // The three groups of columns that change independently, each kept as one JSON blob so a
    // new field is a code change and not a migration.
    private sealed class MetadataBlob
    {
        public string? Overview { get; set; }
        public string? Tagline { get; set; }
        public string? Certification { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string? ReleaseDate { get; set; }
        public List<MediaCastMember>? Cast { get; set; }
        public string? MetadataError { get; set; }

        public static MetadataBlob From(MediaItem item) => new()
        {
            Overview = item.Overview, Tagline = item.Tagline, Certification = item.Certification, RuntimeMinutes = item.RuntimeMinutes,
            ReleaseDate = item.ReleaseDate, Cast = item.Cast.Count > 0 ? item.Cast : null, MetadataError = item.MetadataError
        };

        public void ApplyTo(MediaItem item)
        {
            item.Overview = Overview; item.Tagline = Tagline; item.Certification = Certification; item.RuntimeMinutes = RuntimeMinutes;
            item.ReleaseDate = ReleaseDate; item.Cast = Cast ?? []; item.MetadataError = MetadataError;
        }
    }

    private sealed class ArtworkBlob
    {
        public string? PosterLocal { get; set; }
        public string? PosterRemote { get; set; }
        public string? BackdropLocal { get; set; }
        public string? BackdropRemote { get; set; }
        public string? ThumbLocal { get; set; }
        public string? ThumbRemote { get; set; }

        public static ArtworkBlob From(MediaItem item) => new()
        {
            PosterLocal = item.PosterLocal, PosterRemote = item.PosterRemote, BackdropLocal = item.BackdropLocal,
            BackdropRemote = item.BackdropRemote, ThumbLocal = item.ThumbLocal, ThumbRemote = item.ThumbRemote
        };

        public void ApplyTo(MediaItem item)
        {
            item.PosterLocal = PosterLocal; item.PosterRemote = PosterRemote; item.BackdropLocal = BackdropLocal;
            item.BackdropRemote = BackdropRemote; item.ThumbLocal = ThumbLocal; item.ThumbRemote = ThumbRemote;
        }
    }

    private sealed class ProbeBlob
    {
        public string? Container { get; set; }
        public string? VideoCodec { get; set; }
        public string? VideoProfile { get; set; }
        public string? VideoTag { get; set; }
        public bool? TimestampsInDecodeOrder { get; set; }
        public string? PixelFormat { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long? Bitrate { get; set; }
        public string? AudioCodec { get; set; }
        public int? AudioChannels { get; set; }
        public List<MediaAudioTrack>? AudioTracks { get; set; }
        public List<MediaSubtitleTrack>? SubtitleTracks { get; set; }
        public string? ProbeError { get; set; }

        public static ProbeBlob From(MediaItem item) => new()
        {
            Container = item.Container, VideoCodec = item.VideoCodec, VideoProfile = item.VideoProfile, VideoTag = item.VideoTag,
            TimestampsInDecodeOrder = item.TimestampsInDecodeOrder ? true : null,
            PixelFormat = item.PixelFormat, Width = item.Width, Height = item.Height, Bitrate = item.Bitrate, AudioCodec = item.AudioCodec,
            AudioChannels = item.AudioChannels, AudioTracks = item.AudioTracks.Count > 0 ? item.AudioTracks : null,
            SubtitleTracks = item.SubtitleTracks.Count > 0 ? item.SubtitleTracks : null, ProbeError = item.ProbeError
        };

        public void ApplyTo(MediaItem item)
        {
            item.Container = Container; item.VideoCodec = VideoCodec; item.VideoProfile = VideoProfile; item.VideoTag = VideoTag;
            item.TimestampsInDecodeOrder = TimestampsInDecodeOrder == true;
            item.PixelFormat = PixelFormat; item.Width = Width; item.Height = Height; item.Bitrate = Bitrate; item.AudioCodec = AudioCodec;
            item.AudioChannels = AudioChannels; item.AudioTracks = AudioTracks ?? []; item.SubtitleTracks = SubtitleTracks ?? [];
            item.ProbeError = ProbeError;
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
