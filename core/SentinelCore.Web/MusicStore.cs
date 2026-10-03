using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

public sealed record MusicArtRecord(string Key, string? Path, string Source, DateTimeOffset CheckedAt);

public sealed record MusicLyricsRecord(Guid TrackId, bool Found, string? Source, bool Synced, string? Body, DateTimeOffset FetchedAt);

// Everyone's plays of one track: what "most played" and the artist page's top tracks count.
public sealed record MusicTrackPlayCount(Guid TrackId, int Plays);

public interface IMusicStore
{
    // Bumped by every write that changes what the catalog looks like (tracks, art), so
    // MusicCatalogCache knows when to rebuild.
    long CatalogVersion { get; }

    Task<IReadOnlyList<MusicLibrary>> GetLibrariesAsync(CancellationToken cancellationToken);
    Task<MusicLibrary?> GetLibraryAsync(Guid id, CancellationToken cancellationToken);
    Task SaveLibraryAsync(MusicLibrary library, CancellationToken cancellationToken);
    // Forgets the library and its tracks. Plays, loves, ratings and playlists are kept, in case
    // the folder comes back.
    Task DeleteLibraryAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<MusicTrack>> GetTracksAsync(Guid? libraryId, bool includeMissing, CancellationToken cancellationToken);
    // What the file walk found: path, size, date, sidecar lyrics. A new file is added with its
    // tags unread; a file whose size or date changed has its tags read again.
    Task UpsertFilesAsync(IEnumerable<MusicTrack> tracks, CancellationToken cancellationToken);
    // What the tags and the album rules said.
    Task UpdateTrackDataAsync(IEnumerable<MusicTrack> tracks, CancellationToken cancellationToken);
    Task<int> ReconcileMissingAsync(Guid libraryId, IReadOnlySet<Guid> presentIds, CancellationToken cancellationToken);
    Task<int> PurgeMissingAsync(DateTimeOffset missingSince, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicTrack>> TracksNeedingTagsAsync(int limit, CancellationToken cancellationToken);

    // Records a play or skip unless the same person already reported the same track within the
    // duplicate window of the same time. True when recorded.
    Task<bool> RecordPlayAsync(MusicPlay play, TimeSpan duplicateWindow, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicPlay>> GetPlaysAsync(string userKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicTrackPlayCount>> HouseholdPlayCountsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicPlay>> AllPlaysAsync(CancellationToken cancellationToken);
    Task<int> RestorePlaysAsync(IEnumerable<MusicPlay> rows, CancellationToken cancellationToken);

    Task SetLoveAsync(MusicLove love, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicLove>> GetLovesAsync(string userKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicLove>> AllLovesAsync(CancellationToken cancellationToken);
    Task<int> RestoreLovesAsync(IEnumerable<MusicLove> rows, CancellationToken cancellationToken);

    Task SetRatingAsync(MusicRating rating, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicRating>> GetRatingsAsync(string userKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicRating>> AllRatingsAsync(CancellationToken cancellationToken);
    Task<int> RestoreRatingsAsync(IEnumerable<MusicRating> rows, CancellationToken cancellationToken);

    Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken);
    Task<MusicPlaylist?> GetPlaylistAsync(Guid id, CancellationToken cancellationToken);
    // Writes the playlist and replaces its entries with the ones it carries, in that order.
    Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken);
    Task DeletePlaylistAsync(Guid id, CancellationToken cancellationToken);
    // A playlist from the backup replaces this one only if it was edited more recently.
    Task<int> RestorePlaylistsAsync(IEnumerable<MusicPlaylist> playlists, CancellationToken cancellationToken);

    Task<IReadOnlyList<MusicPin>> GetPinsAsync(string userKey, CancellationToken cancellationToken);
    Task SetPinnedAsync(string userKey, Guid playlistId, bool pinned, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicPin>> AllPinsAsync(CancellationToken cancellationToken);
    Task<int> RestorePinsAsync(IEnumerable<MusicPin> rows, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, MusicArtRecord>> GetArtAsync(CancellationToken cancellationToken);
    Task SetArtAsync(MusicArtRecord art, CancellationToken cancellationToken);
    Task DeleteArtAsync(IEnumerable<string> keys, CancellationToken cancellationToken);

    Task<MusicLyricsRecord?> GetLyricsAsync(Guid trackId, CancellationToken cancellationToken);
    Task SetLyricsAsync(MusicLyricsRecord lyrics, CancellationToken cancellationToken);
}

public sealed class SqliteMusicStore : IMusicStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _catalogVersion;

    public SqliteMusicStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "music.db")}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS MusicLibraries (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Path TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastScanStartedAt TEXT,
                LastScanFinishedAt TEXT,
                LastScanError TEXT,
                TrackCount INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS MusicTracks (
                Id TEXT PRIMARY KEY,
                LibraryId TEXT NOT NULL,
                Path TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                FileSize INTEGER NOT NULL,
                FileModifiedAt TEXT,
                AddedAt TEXT NOT NULL,
                Missing INTEGER NOT NULL DEFAULT 0,
                MissingSince TEXT,
                LyricsSidecar TEXT,
                TagsReadAt TEXT,
                AlbumId TEXT,
                ArtistId TEXT,
                Data TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS IX_MusicTracks_Library ON MusicTracks (LibraryId);
            CREATE INDEX IF NOT EXISTS IX_MusicTracks_Album ON MusicTracks (AlbumId);

            CREATE TABLE IF NOT EXISTS MusicPlays (
                UserKey TEXT NOT NULL,
                TrackId TEXT NOT NULL,
                PlayedAt TEXT NOT NULL,
                SecondsListened REAL NOT NULL,
                Skipped INTEGER NOT NULL DEFAULT 0,
                DeviceName TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_MusicPlays_User ON MusicPlays (UserKey, PlayedAt);
            CREATE INDEX IF NOT EXISTS IX_MusicPlays_Track ON MusicPlays (TrackId, UserKey);

            CREATE TABLE IF NOT EXISTS MusicLoves (
                UserKey TEXT NOT NULL,
                Kind TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                Loved INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (UserKey, Kind, ItemId)
            );

            CREATE TABLE IF NOT EXISTS MusicRatings (
                UserKey TEXT NOT NULL,
                Kind TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                Rating INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (UserKey, Kind, ItemId)
            );

            CREATE TABLE IF NOT EXISTS MusicPlaylists (
                Id TEXT PRIMARY KEY,
                OwnerKey TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                Shared INTEGER NOT NULL DEFAULT 0,
                Collaborative INTEGER NOT NULL DEFAULT 0,
                Rules TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MusicPlaylistEntries (
                EntryId TEXT PRIMARY KEY,
                PlaylistId TEXT NOT NULL,
                TrackId TEXT NOT NULL,
                Position INTEGER NOT NULL,
                AddedAt TEXT NOT NULL,
                AddedBy TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_MusicPlaylistEntries_Playlist ON MusicPlaylistEntries (PlaylistId, Position);

            CREATE TABLE IF NOT EXISTS MusicPlaylistPins (
                UserKey TEXT NOT NULL,
                PlaylistId TEXT NOT NULL,
                PinnedAt TEXT NOT NULL,
                PRIMARY KEY (UserKey, PlaylistId)
            );

            CREATE TABLE IF NOT EXISTS MusicArt (
                Key TEXT PRIMARY KEY,
                Path TEXT,
                Source TEXT NOT NULL,
                CheckedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MusicLyrics (
                TrackId TEXT PRIMARY KEY,
                Found INTEGER NOT NULL,
                Source TEXT,
                Synced INTEGER NOT NULL DEFAULT 0,
                Body TEXT,
                FetchedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public long CatalogVersion => Interlocked.Read(ref _catalogVersion);

    private void CatalogChanged() => Interlocked.Increment(ref _catalogVersion);

    // MARK: - Libraries

    public async Task<IReadOnlyList<MusicLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MusicLibraries ORDER BY CreatedAt";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MusicLibrary>();
            while (await reader.ReadAsync(cancellationToken)) list.Add(ReadLibrary(reader));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<MusicLibrary?> GetLibraryAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MusicLibraries WHERE Id = $id";
            command.Parameters.AddWithValue("$id", Key(id));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadLibrary(reader) : null;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveLibraryAsync(MusicLibrary library, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MusicLibraries (Id, Name, Path, CreatedAt, LastScanStartedAt, LastScanFinishedAt, LastScanError, TrackCount)
                VALUES ($id, $name, $path, $created, $started, $finished, $error, $count)
                ON CONFLICT(Id) DO UPDATE SET Name = excluded.Name, Path = excluded.Path,
                    LastScanStartedAt = excluded.LastScanStartedAt, LastScanFinishedAt = excluded.LastScanFinishedAt,
                    LastScanError = excluded.LastScanError, TrackCount = excluded.TrackCount
                """;
            command.Parameters.AddWithValue("$id", Key(library.Id));
            command.Parameters.AddWithValue("$name", library.Name);
            command.Parameters.AddWithValue("$path", library.Path);
            command.Parameters.AddWithValue("$created", library.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$started", Text(library.LastScanStartedAt));
            command.Parameters.AddWithValue("$finished", Text(library.LastScanFinishedAt));
            command.Parameters.AddWithValue("$error", (object?)library.LastScanError ?? DBNull.Value);
            command.Parameters.AddWithValue("$count", library.TrackCount);
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
                DELETE FROM MusicLyrics WHERE TrackId IN (SELECT Id FROM MusicTracks WHERE LibraryId = $id);
                DELETE FROM MusicTracks WHERE LibraryId = $id;
                DELETE FROM MusicLibraries WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", Key(id));
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            CatalogChanged();
        }
        finally { _lock.Release(); }
    }

    // MARK: - Tracks

    public async Task<IReadOnlyList<MusicTrack>> GetTracksAsync(Guid? libraryId, bool includeMissing, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            var where = new List<string>();
            if (!includeMissing) where.Add("Missing = 0");
            if (libraryId is { } library)
            {
                where.Add("LibraryId = $library");
                command.Parameters.AddWithValue("$library", Key(library));
            }
            command.CommandText = "SELECT * FROM MusicTracks" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "");
            return await ReadTracksAsync(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task UpsertFilesAsync(IEnumerable<MusicTrack> tracks, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var track in tracks)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                // A changed file (size or date) has its tags read again; an unchanged one keeps
                // everything the tag pass worked out.
                command.CommandText = """
                    INSERT INTO MusicTracks (Id, LibraryId, Path, RelativePath, FileSize, FileModifiedAt, AddedAt, Missing, MissingSince,
                        LyricsSidecar, TagsReadAt, AlbumId, ArtistId, Data)
                    VALUES ($id, $library, $path, $relative, $size, $modified, $added, 0, NULL, $lrc, NULL, NULL, NULL, '{}')
                    ON CONFLICT(Id) DO UPDATE SET
                        LibraryId = excluded.LibraryId, Path = excluded.Path, RelativePath = excluded.RelativePath,
                        Missing = 0, MissingSince = NULL, LyricsSidecar = excluded.LyricsSidecar,
                        TagsReadAt = CASE WHEN FileSize = excluded.FileSize AND FileModifiedAt IS excluded.FileModifiedAt THEN TagsReadAt ELSE NULL END,
                        FileSize = excluded.FileSize, FileModifiedAt = excluded.FileModifiedAt
                    """;
                command.Parameters.AddWithValue("$id", Key(track.Id));
                command.Parameters.AddWithValue("$library", Key(track.LibraryId));
                command.Parameters.AddWithValue("$path", track.Path);
                command.Parameters.AddWithValue("$relative", track.RelativePath);
                command.Parameters.AddWithValue("$size", track.FileSize);
                command.Parameters.AddWithValue("$modified", Text(track.FileModifiedAt));
                command.Parameters.AddWithValue("$added", track.AddedAt.ToString("O"));
                command.Parameters.AddWithValue("$lrc", (object?)track.LyricsSidecar ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            CatalogChanged();
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateTrackDataAsync(IEnumerable<MusicTrack> tracks, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var track in tracks)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE MusicTracks SET TagsReadAt = $read, AlbumId = $album, ArtistId = $artist, Data = $data WHERE Id = $id";
                command.Parameters.AddWithValue("$id", Key(track.Id));
                command.Parameters.AddWithValue("$read", Text(track.TagsReadAt));
                command.Parameters.AddWithValue("$album", track.AlbumId == Guid.Empty ? DBNull.Value : Key(track.AlbumId));
                command.Parameters.AddWithValue("$artist", track.ArtistId == Guid.Empty ? DBNull.Value : Key(track.ArtistId));
                command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(TrackBlob.From(track), JsonOptions));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            CatalogChanged();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> ReconcileMissingAsync(Guid libraryId, IReadOnlySet<Guid> presentIds, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            var toMark = new List<string>();
            using (var select = _connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT Id FROM MusicTracks WHERE LibraryId = $library AND Missing = 0";
                select.Parameters.AddWithValue("$library", Key(libraryId));
                using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetString(0);
                    if (!presentIds.Contains(Guid.ParseExact(id, "N"))) toMark.Add(id);
                }
            }
            foreach (var id in toMark)
            {
                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE MusicTracks SET Missing = 1, MissingSince = $since WHERE Id = $id";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.ToString("O"));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            if (toMark.Count > 0) CatalogChanged();
            return toMark.Count;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> PurgeMissingAsync(DateTimeOffset missingSince, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM MusicLyrics WHERE TrackId IN (SELECT Id FROM MusicTracks WHERE Missing = 1 AND MissingSince IS NOT NULL AND MissingSince < $since);
                DELETE FROM MusicTracks WHERE Missing = 1 AND MissingSince IS NOT NULL AND MissingSince < $since;
                """;
            command.Parameters.AddWithValue("$since", missingSince.ToString("O"));
            var purged = await command.ExecuteNonQueryAsync(cancellationToken);
            if (purged > 0) CatalogChanged();
            return purged;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MusicTrack>> TracksNeedingTagsAsync(int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MusicTracks WHERE Missing = 0 AND TagsReadAt IS NULL ORDER BY RelativePath LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            return await ReadTracksAsync(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // MARK: - Plays

    public async Task<bool> RecordPlayAsync(MusicPlay play, TimeSpan duplicateWindow, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MusicPlays (UserKey, TrackId, PlayedAt, SecondsListened, Skipped, DeviceName)
                SELECT $user, $track, $at, $seconds, $skipped, $device
                WHERE NOT EXISTS (SELECT 1 FROM MusicPlays WHERE UserKey = $user AND TrackId = $track AND PlayedAt >= $from AND PlayedAt <= $to)
                """;
            AddPlayParameters(command, play);
            command.Parameters.AddWithValue("$from", Utc(play.PlayedAt - duplicateWindow));
            command.Parameters.AddWithValue("$to", Utc(play.PlayedAt + duplicateWindow));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MusicPlay>> GetPlaysAsync(string userKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MusicPlays WHERE UserKey = $user ORDER BY PlayedAt";
            command.Parameters.AddWithValue("$user", userKey);
            return await ReadPlaysAsync(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MusicTrackPlayCount>> HouseholdPlayCountsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT TrackId, COUNT(*) FROM MusicPlays WHERE Skipped = 0 GROUP BY TrackId";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<MusicTrackPlayCount>();
            while (await reader.ReadAsync(cancellationToken))
                list.Add(new MusicTrackPlayCount(Guid.ParseExact(reader.GetString(0), "N"), (int)reader.GetInt64(1)));
            return list;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MusicPlay>> AllPlaysAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MusicPlays ORDER BY PlayedAt, UserKey, TrackId";
            return await ReadPlaysAsync(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RestorePlaysAsync(IEnumerable<MusicPlay> rows, CancellationToken cancellationToken)
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
                    INSERT INTO MusicPlays (UserKey, TrackId, PlayedAt, SecondsListened, Skipped, DeviceName)
                    SELECT $user, $track, $at, $seconds, $skipped, $device
                    WHERE NOT EXISTS (SELECT 1 FROM MusicPlays WHERE UserKey = $user AND TrackId = $track AND PlayedAt = $at)
                    """;
                AddPlayParameters(command, row);
                restored += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            return restored;
        }
        finally { _lock.Release(); }
    }

    private static void AddPlayParameters(SqliteCommand command, MusicPlay play)
    {
        command.Parameters.AddWithValue("$user", play.UserKey);
        command.Parameters.AddWithValue("$track", Key(play.TrackId));
        command.Parameters.AddWithValue("$at", Utc(play.PlayedAt));
        command.Parameters.AddWithValue("$seconds", play.SecondsListened);
        command.Parameters.AddWithValue("$skipped", play.Skipped ? 1 : 0);
        command.Parameters.AddWithValue("$device", (object?)play.DeviceName ?? DBNull.Value);
    }

    private static async Task<IReadOnlyList<MusicPlay>> ReadPlaysAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<MusicPlay>();
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new MusicPlay(
                reader.GetString(reader.GetOrdinal("UserKey")),
                Guid.ParseExact(reader.GetString(reader.GetOrdinal("TrackId")), "N"),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("PlayedAt"))),
                reader.GetDouble(reader.GetOrdinal("SecondsListened")),
                reader.GetInt64(reader.GetOrdinal("Skipped")) != 0,
                NullableString(reader, "DeviceName")));
        return list;
    }

    // MARK: - Loves and ratings

    public Task SetLoveAsync(MusicLove love, CancellationToken cancellationToken) =>
        WriteMarkAsync("MusicLoves", "Loved", love.UserKey, love.Kind, love.ItemId, love.Loved ? 1 : 0, love.UpdatedAt, onlyNewer: false, cancellationToken);

    public async Task<IReadOnlyList<MusicLove>> GetLovesAsync(string userKey, CancellationToken cancellationToken) =>
        (await ReadMarksAsync("MusicLoves", "Loved", userKey, cancellationToken))
            .Select(m => new MusicLove(m.User, m.Kind, m.Item, m.Value != 0, m.At)).ToList();

    public async Task<IReadOnlyList<MusicLove>> AllLovesAsync(CancellationToken cancellationToken) =>
        (await ReadMarksAsync("MusicLoves", "Loved", null, cancellationToken))
            .Select(m => new MusicLove(m.User, m.Kind, m.Item, m.Value != 0, m.At)).ToList();

    public async Task<int> RestoreLovesAsync(IEnumerable<MusicLove> rows, CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var row in rows)
            restored += await WriteMarkAsync("MusicLoves", "Loved", row.UserKey, row.Kind, row.ItemId, row.Loved ? 1 : 0, row.UpdatedAt, onlyNewer: true, cancellationToken);
        return restored;
    }

    public Task SetRatingAsync(MusicRating rating, CancellationToken cancellationToken) =>
        WriteMarkAsync("MusicRatings", "Rating", rating.UserKey, rating.Kind, rating.ItemId, rating.Rating, rating.UpdatedAt, onlyNewer: false, cancellationToken);

    public async Task<IReadOnlyList<MusicRating>> GetRatingsAsync(string userKey, CancellationToken cancellationToken) =>
        (await ReadMarksAsync("MusicRatings", "Rating", userKey, cancellationToken))
            .Select(m => new MusicRating(m.User, m.Kind, m.Item, m.Value, m.At)).ToList();

    public async Task<IReadOnlyList<MusicRating>> AllRatingsAsync(CancellationToken cancellationToken) =>
        (await ReadMarksAsync("MusicRatings", "Rating", null, cancellationToken))
            .Select(m => new MusicRating(m.User, m.Kind, m.Item, m.Value, m.At)).ToList();

    public async Task<int> RestoreRatingsAsync(IEnumerable<MusicRating> rows, CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var row in rows)
            restored += await WriteMarkAsync("MusicRatings", "Rating", row.UserKey, row.Kind, row.ItemId, Math.Clamp(row.Rating, 0, 5), row.UpdatedAt, onlyNewer: true, cancellationToken);
        return restored;
    }

    // Loves and ratings are the same shape: one value per person per thing, newest edit wins.
    private async Task<int> WriteMarkAsync(string table, string column, string userKey, string kind, Guid itemId, int value,
        DateTimeOffset updatedAt, bool onlyNewer, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {table} (UserKey, Kind, ItemId, {column}, UpdatedAt) VALUES ($user, $kind, $item, $value, $at)
                ON CONFLICT(UserKey, Kind, ItemId) DO UPDATE SET {column} = excluded.{column}, UpdatedAt = excluded.UpdatedAt
                """ + (onlyNewer ? $" WHERE excluded.UpdatedAt > {table}.UpdatedAt" : "");
            command.Parameters.AddWithValue("$user", userKey);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$item", Key(itemId));
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$at", Utc(updatedAt));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    private async Task<List<(string User, string Kind, Guid Item, int Value, DateTimeOffset At)>> ReadMarksAsync(
        string table, string column, string? userKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT UserKey, Kind, ItemId, {column}, UpdatedAt FROM {table}"
                + (userKey is null ? "" : " WHERE UserKey = $user") + " ORDER BY UserKey, Kind, ItemId";
            if (userKey is not null) command.Parameters.AddWithValue("$user", userKey);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<(string, string, Guid, int, DateTimeOffset)>();
            while (await reader.ReadAsync(cancellationToken))
                list.Add((reader.GetString(0), reader.GetString(1), Guid.ParseExact(reader.GetString(2), "N"),
                    (int)reader.GetInt64(3), DateTimeOffset.Parse(reader.GetString(4))));
            return list;
        }
        finally { _lock.Release(); }
    }

    // MARK: - Playlists

    public async Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try { return await ReadPlaylistsAsync(null, cancellationToken); }
        finally { _lock.Release(); }
    }

    public async Task<MusicPlaylist?> GetPlaylistAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try { return (await ReadPlaylistsAsync(id, cancellationToken)).FirstOrDefault(); }
        finally { _lock.Release(); }
    }

    private async Task<List<MusicPlaylist>> ReadPlaylistsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, List<MusicPlaylistEntry>>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM MusicPlaylistEntries" + (id is null ? "" : " WHERE PlaylistId = $id") + " ORDER BY PlaylistId, Position";
            if (id is { } one) command.Parameters.AddWithValue("$id", Key(one));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var playlist = reader.GetString(reader.GetOrdinal("PlaylistId"));
                if (!entries.TryGetValue(playlist, out var list)) entries[playlist] = list = [];
                list.Add(new MusicPlaylistEntry(
                    Guid.ParseExact(reader.GetString(reader.GetOrdinal("EntryId")), "N"),
                    Guid.ParseExact(reader.GetString(reader.GetOrdinal("TrackId")), "N"),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("AddedAt"))),
                    NullableString(reader, "AddedBy")));
            }
        }

        var playlists = new List<MusicPlaylist>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM MusicPlaylists" + (id is null ? "" : " WHERE Id = $id") + " ORDER BY CreatedAt, Id";
            if (id is { } one) command.Parameters.AddWithValue("$id", Key(one));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(reader.GetOrdinal("Id"));
                var rules = NullableString(reader, "Rules");
                playlists.Add(new MusicPlaylist(
                    Guid.ParseExact(key, "N"),
                    reader.GetString(reader.GetOrdinal("OwnerKey")),
                    reader.GetString(reader.GetOrdinal("Name")),
                    reader.GetString(reader.GetOrdinal("Description")),
                    reader.GetInt64(reader.GetOrdinal("Shared")) != 0,
                    reader.GetInt64(reader.GetOrdinal("Collaborative")) != 0,
                    rules is null ? null : JsonSerializer.Deserialize<MusicSmartRules>(rules, JsonOptions),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
                    entries.GetValueOrDefault(key) ?? []));
            }
        }
        return playlists;
    }

    public async Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            await WritePlaylistAsync(transaction, playlist, onlyNewer: false, cancellationToken);
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    private async Task<bool> WritePlaylistAsync(SqliteTransaction transaction, MusicPlaylist playlist, bool onlyNewer, CancellationToken cancellationToken)
    {
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO MusicPlaylists (Id, OwnerKey, Name, Description, Shared, Collaborative, Rules, CreatedAt, UpdatedAt)
                VALUES ($id, $owner, $name, $description, $shared, $collaborative, $rules, $created, $updated)
                ON CONFLICT(Id) DO UPDATE SET OwnerKey = excluded.OwnerKey, Name = excluded.Name, Description = excluded.Description,
                    Shared = excluded.Shared, Collaborative = excluded.Collaborative, Rules = excluded.Rules, UpdatedAt = excluded.UpdatedAt
                """ + (onlyNewer ? " WHERE excluded.UpdatedAt > MusicPlaylists.UpdatedAt" : "");
            command.Parameters.AddWithValue("$id", Key(playlist.Id));
            command.Parameters.AddWithValue("$owner", playlist.OwnerKey);
            command.Parameters.AddWithValue("$name", playlist.Name);
            command.Parameters.AddWithValue("$description", playlist.Description);
            command.Parameters.AddWithValue("$shared", playlist.Shared ? 1 : 0);
            command.Parameters.AddWithValue("$collaborative", playlist.Collaborative ? 1 : 0);
            command.Parameters.AddWithValue("$rules", playlist.Rules is null ? DBNull.Value : JsonSerializer.Serialize(playlist.Rules, JsonOptions));
            command.Parameters.AddWithValue("$created", Utc(playlist.CreatedAt));
            command.Parameters.AddWithValue("$updated", Utc(playlist.UpdatedAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM MusicPlaylistEntries WHERE PlaylistId = $id";
            delete.Parameters.AddWithValue("$id", Key(playlist.Id));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        var position = 0;
        foreach (var entry in playlist.Entries)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO MusicPlaylistEntries (EntryId, PlaylistId, TrackId, Position, AddedAt, AddedBy)
                VALUES ($entry, $playlist, $track, $position, $added, $by)
                """;
            insert.Parameters.AddWithValue("$entry", Key(entry.EntryId));
            insert.Parameters.AddWithValue("$playlist", Key(playlist.Id));
            insert.Parameters.AddWithValue("$track", Key(entry.TrackId));
            insert.Parameters.AddWithValue("$position", position++);
            insert.Parameters.AddWithValue("$added", Utc(entry.AddedAt));
            insert.Parameters.AddWithValue("$by", (object?)entry.AddedBy ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        return true;
    }

    public async Task DeletePlaylistAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM MusicPlaylistEntries WHERE PlaylistId = $id;
                DELETE FROM MusicPlaylistPins WHERE PlaylistId = $id;
                DELETE FROM MusicPlaylists WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", Key(id));
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RestorePlaylistsAsync(IEnumerable<MusicPlaylist> playlists, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            var restored = 0;
            foreach (var playlist in playlists)
                if (await WritePlaylistAsync(transaction, playlist, onlyNewer: true, cancellationToken)) restored++;
            transaction.Commit();
            return restored;
        }
        finally { _lock.Release(); }
    }

    // MARK: - Pins

    public async Task<IReadOnlyList<MusicPin>> GetPinsAsync(string userKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT UserKey, PlaylistId, PinnedAt FROM MusicPlaylistPins WHERE UserKey = $user ORDER BY PinnedAt";
            command.Parameters.AddWithValue("$user", userKey);
            return await ReadPinsAsync(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task SetPinnedAsync(string userKey, Guid playlistId, bool pinned, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = pinned
                ? "INSERT OR IGNORE INTO MusicPlaylistPins (UserKey, PlaylistId, PinnedAt) VALUES ($user, $playlist, $at)"
                : "DELETE FROM MusicPlaylistPins WHERE UserKey = $user AND PlaylistId = $playlist";
            command.Parameters.AddWithValue("$user", userKey);
            command.Parameters.AddWithValue("$playlist", Key(playlistId));
            command.Parameters.AddWithValue("$at", Utc(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<MusicPin>> AllPinsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT UserKey, PlaylistId, PinnedAt FROM MusicPlaylistPins ORDER BY UserKey, PinnedAt, PlaylistId";
            return await ReadPinsAsync(command, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RestorePinsAsync(IEnumerable<MusicPin> rows, CancellationToken cancellationToken)
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
                command.CommandText = "INSERT OR IGNORE INTO MusicPlaylistPins (UserKey, PlaylistId, PinnedAt) VALUES ($user, $playlist, $at)";
                command.Parameters.AddWithValue("$user", row.UserKey);
                command.Parameters.AddWithValue("$playlist", Key(row.PlaylistId));
                command.Parameters.AddWithValue("$at", Utc(row.PinnedAt));
                restored += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            return restored;
        }
        finally { _lock.Release(); }
    }

    private static async Task<IReadOnlyList<MusicPin>> ReadPinsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<MusicPin>();
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new MusicPin(reader.GetString(0), Guid.ParseExact(reader.GetString(1), "N"), DateTimeOffset.Parse(reader.GetString(2))));
        return list;
    }

    // MARK: - Art and lyrics caches

    public async Task<IReadOnlyDictionary<string, MusicArtRecord>> GetArtAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Key, Path, Source, CheckedAt FROM MusicArt";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var map = new Dictionary<string, MusicArtRecord>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
                map[reader.GetString(0)] = new MusicArtRecord(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)));
            return map;
        }
        finally { _lock.Release(); }
    }

    public async Task SetArtAsync(MusicArtRecord art, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO MusicArt (Key, Path, Source, CheckedAt) VALUES ($key, $path, $source, $at)";
            command.Parameters.AddWithValue("$key", art.Key);
            command.Parameters.AddWithValue("$path", (object?)art.Path ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", art.Source);
            command.Parameters.AddWithValue("$at", Utc(art.CheckedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
            CatalogChanged();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteArtAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var key in keys)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM MusicArt WHERE Key = $key";
                command.Parameters.AddWithValue("$key", key);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            CatalogChanged();
        }
        finally { _lock.Release(); }
    }

    public async Task<MusicLyricsRecord?> GetLyricsAsync(Guid trackId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MusicLyrics WHERE TrackId = $id";
            command.Parameters.AddWithValue("$id", Key(trackId));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new MusicLyricsRecord(trackId,
                reader.GetInt64(reader.GetOrdinal("Found")) != 0,
                NullableString(reader, "Source"),
                reader.GetInt64(reader.GetOrdinal("Synced")) != 0,
                NullableString(reader, "Body"),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("FetchedAt"))));
        }
        finally { _lock.Release(); }
    }

    public async Task SetLyricsAsync(MusicLyricsRecord lyrics, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO MusicLyrics (TrackId, Found, Source, Synced, Body, FetchedAt) VALUES ($id, $found, $source, $synced, $body, $at)";
            command.Parameters.AddWithValue("$id", Key(lyrics.TrackId));
            command.Parameters.AddWithValue("$found", lyrics.Found ? 1 : 0);
            command.Parameters.AddWithValue("$source", (object?)lyrics.Source ?? DBNull.Value);
            command.Parameters.AddWithValue("$synced", lyrics.Synced ? 1 : 0);
            command.Parameters.AddWithValue("$body", (object?)lyrics.Body ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", Utc(lyrics.FetchedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _lock.Release(); }
    }

    // MARK: - Rows

    private static async Task<IReadOnlyList<MusicTrack>> ReadTracksAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<MusicTrack>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(ReadTrack(reader));
        return list;
    }

    private static MusicTrack ReadTrack(SqliteDataReader reader)
    {
        var track = new MusicTrack
        {
            Id = Guid.ParseExact(reader.GetString(reader.GetOrdinal("Id")), "N"),
            LibraryId = Guid.ParseExact(reader.GetString(reader.GetOrdinal("LibraryId")), "N"),
            Path = reader.GetString(reader.GetOrdinal("Path")),
            RelativePath = reader.GetString(reader.GetOrdinal("RelativePath")),
            FileSize = reader.GetInt64(reader.GetOrdinal("FileSize")),
            FileModifiedAt = Date(reader, "FileModifiedAt"),
            AddedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("AddedAt"))),
            Missing = reader.GetInt64(reader.GetOrdinal("Missing")) != 0,
            LyricsSidecar = NullableString(reader, "LyricsSidecar"),
            TagsReadAt = Date(reader, "TagsReadAt"),
            AlbumId = NullableString(reader, "AlbumId") is { } album ? Guid.ParseExact(album, "N") : Guid.Empty,
            ArtistId = NullableString(reader, "ArtistId") is { } artist ? Guid.ParseExact(artist, "N") : Guid.Empty
        };
        (JsonSerializer.Deserialize<TrackBlob>(reader.GetString(reader.GetOrdinal("Data")), JsonOptions) ?? new()).ApplyTo(track);
        return track;
    }

    private static MusicLibrary ReadLibrary(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(reader.GetOrdinal("Id")), "N"),
        reader.GetString(reader.GetOrdinal("Name")),
        reader.GetString(reader.GetOrdinal("Path")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        Date(reader, "LastScanStartedAt"),
        Date(reader, "LastScanFinishedAt"),
        NullableString(reader, "LastScanError"),
        (int)reader.GetInt64(reader.GetOrdinal("TrackCount")));

    private static string Key(Guid id) => id.ToString("N");

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static object Text(DateTimeOffset? value) => value is { } date ? date.ToUniversalTime().ToString("O") : DBNull.Value;

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? Date(SqliteDataReader reader, string column) =>
        NullableString(reader, column) is { } text ? DateTimeOffset.Parse(text) : null;

    // Everything the tags and the album rules said, as one JSON blob so a new field is a code
    // change and not a migration.
    private sealed class TrackBlob
    {
        public string? TagError { get; set; }
        public string? Title { get; set; }
        public string? ArtistText { get; set; }
        public string? AlbumArtistTag { get; set; }
        public bool? CompilationTag { get; set; }
        public string? Album { get; set; }
        public int? TrackNumber { get; set; }
        public int? DiscNumber { get; set; }
        public int? Year { get; set; }
        public List<string>? Genres { get; set; }
        public string? Composer { get; set; }
        public int? Bpm { get; set; }
        public double? TrackGain { get; set; }
        public double? TrackPeak { get; set; }
        public double? AlbumGain { get; set; }
        public double? AlbumPeak { get; set; }
        public bool? HasEmbeddedArt { get; set; }
        public bool? HasEmbeddedLyrics { get; set; }
        public string? MusicBrainzReleaseId { get; set; }
        public string? MusicBrainzReleaseGroupId { get; set; }
        public double? DurationSeconds { get; set; }
        public string? Codec { get; set; }
        public long? Bitrate { get; set; }
        public int? SampleRate { get; set; }
        public int? BitDepth { get; set; }
        public int? Channels { get; set; }
        public List<string>? Artists { get; set; }
        public string? AlbumArtist { get; set; }
        public bool? Compilation { get; set; }

        public static TrackBlob From(MusicTrack t) => new()
        {
            TagError = t.TagError, Title = t.Title, ArtistText = t.ArtistText, AlbumArtistTag = t.AlbumArtistTag,
            CompilationTag = t.CompilationTag ? true : null, Album = t.Album, TrackNumber = t.TrackNumber, DiscNumber = t.DiscNumber,
            Year = t.Year, Genres = t.Genres.Count > 0 ? t.Genres : null, Composer = t.Composer, Bpm = t.Bpm,
            TrackGain = t.TrackGain, TrackPeak = t.TrackPeak, AlbumGain = t.AlbumGain, AlbumPeak = t.AlbumPeak,
            HasEmbeddedArt = t.HasEmbeddedArt ? true : null, HasEmbeddedLyrics = t.HasEmbeddedLyrics ? true : null,
            MusicBrainzReleaseId = t.MusicBrainzReleaseId, MusicBrainzReleaseGroupId = t.MusicBrainzReleaseGroupId,
            DurationSeconds = t.DurationSeconds, Codec = t.Codec, Bitrate = t.Bitrate, SampleRate = t.SampleRate,
            BitDepth = t.BitDepth, Channels = t.Channels, Artists = t.Artists.Count > 0 ? t.Artists : null,
            AlbumArtist = t.AlbumArtist, Compilation = t.Compilation ? true : null
        };

        public void ApplyTo(MusicTrack t)
        {
            t.TagError = TagError; t.Title = Title ?? ""; t.ArtistText = ArtistText ?? ""; t.AlbumArtistTag = AlbumArtistTag;
            t.CompilationTag = CompilationTag == true; t.Album = Album ?? ""; t.TrackNumber = TrackNumber; t.DiscNumber = DiscNumber;
            t.Year = Year; t.Genres = Genres ?? []; t.Composer = Composer; t.Bpm = Bpm;
            t.TrackGain = TrackGain; t.TrackPeak = TrackPeak; t.AlbumGain = AlbumGain; t.AlbumPeak = AlbumPeak;
            t.HasEmbeddedArt = HasEmbeddedArt == true; t.HasEmbeddedLyrics = HasEmbeddedLyrics == true;
            t.MusicBrainzReleaseId = MusicBrainzReleaseId; t.MusicBrainzReleaseGroupId = MusicBrainzReleaseGroupId;
            t.DurationSeconds = DurationSeconds ?? 0; t.Codec = Codec ?? ""; t.Bitrate = Bitrate; t.SampleRate = SampleRate;
            t.BitDepth = BitDepth; t.Channels = Channels; t.Artists = Artists ?? []; t.AlbumArtist = AlbumArtist ?? "";
            t.Compilation = Compilation == true;
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
