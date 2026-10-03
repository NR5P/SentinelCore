using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// podcasts.db: shows, episodes, downloads, everyone's subscriptions, progress, Up Next queues,
// bookmarks, clips, transcripts (with a full-text index) and listening time. Episode metadata
// (written by a feed refresh) and download state (written by the download manager) are separate
// tables on purpose: both are long-running writers and neither may overwrite the other's work.
public interface IPodcastStore
{
    Task<IReadOnlyList<PodcastShow>> GetShowsAsync(CancellationToken cancellationToken);
    Task<PodcastShow?> GetShowAsync(Guid id, CancellationToken cancellationToken);
    Task SaveShowAsync(PodcastShow show, CancellationToken cancellationToken);
    // Forgets the show and everything hanging off it except listening history (Stats).
    Task DeleteShowAsync(Guid id, CancellationToken cancellationToken);

    Task<PodcastEpisode?> GetEpisodeAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastEpisode>> GetEpisodesAsync(Guid podcastId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastEpisode>> GetEpisodesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastEpisode>> GetEpisodesForShowsAsync(IEnumerable<Guid> podcastIds, DateTimeOffset? publishedSince, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetEpisodeIdsAsync(Guid podcastId, CancellationToken cancellationToken);
    // Just the columns, for counting: no descriptions are read.
    Task<IReadOnlyList<PodcastEpisodeHeader>> GetEpisodeHeadersAsync(CancellationToken cancellationToken);
    Task UpsertEpisodesAsync(IEnumerable<PodcastEpisode> episodes, CancellationToken cancellationToken);
    // Marks every episode of the show not in `present` archived, and un-archives those that are.
    Task<int> ArchiveMissingAsync(Guid podcastId, IReadOnlySet<Guid> present, CancellationToken cancellationToken);
    Task<(int Shows, int Episodes)> CountsAsync(CancellationToken cancellationToken);

    Task<PodcastDownload?> GetDownloadAsync(Guid episodeId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastDownload>> GetDownloadsAsync(CancellationToken cancellationToken);
    Task SaveDownloadAsync(PodcastDownload download, CancellationToken cancellationToken);

    Task<IReadOnlyList<PodcastChapter>?> GetChaptersAsync(Guid episodeId, CancellationToken cancellationToken);
    Task SaveChaptersAsync(Guid episodeId, string source, IReadOnlyList<PodcastChapter> chapters, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> EpisodesWithChaptersAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PodcastSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken);
    Task SaveSubscriptionAsync(PodcastSubscription subscription, CancellationToken cancellationToken);

    Task<IReadOnlyList<PodcastProgress>> GetProgressAsync(string userKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastProgress>> GetProgressForEpisodesAsync(IEnumerable<Guid> episodeIds, CancellationToken cancellationToken);
    Task SetProgressAsync(PodcastProgress progress, CancellationToken cancellationToken);
    Task DeleteProgressAsync(string userKey, Guid episodeId, CancellationToken cancellationToken);
    // "Mark all played" on a show with two thousand episodes: one transaction, not two thousand.
    Task SetProgressManyAsync(IReadOnlyList<PodcastProgress> rows, CancellationToken cancellationToken);
    Task DeleteProgressManyAsync(string userKey, IReadOnlyList<Guid> episodeIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastProgress>> AllProgressAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PodcastQueueEntry>> GetQueueAsync(string userKey, CancellationToken cancellationToken);
    Task SetQueueAsync(string userKey, IReadOnlyList<Guid> episodeIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastQueueEntry>> AllQueuesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PodcastBookmark>> GetBookmarksAsync(string? userKey, CancellationToken cancellationToken);
    Task<PodcastBookmark?> GetBookmarkAsync(Guid id, CancellationToken cancellationToken);
    Task SaveBookmarkAsync(PodcastBookmark bookmark, CancellationToken cancellationToken);
    Task DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PodcastClip>> GetClipsAsync(string? userKey, CancellationToken cancellationToken);
    Task<PodcastClip?> GetClipAsync(Guid id, CancellationToken cancellationToken);
    Task SaveClipAsync(PodcastClip clip, CancellationToken cancellationToken);
    Task DeleteClipAsync(Guid id, CancellationToken cancellationToken);

    Task<PodcastTranscript?> GetTranscriptAsync(Guid episodeId, CancellationToken cancellationToken);
    Task SaveTranscriptAsync(Guid episodeId, PodcastTranscript transcript, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> TranscriptSourcesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastTranscriptHit>> SearchTranscriptsAsync(string query, int limit, CancellationToken cancellationToken);

    Task AddListeningAsync(PodcastListening listening, CancellationToken cancellationToken);
    Task<IReadOnlyList<PodcastListening>> GetListeningAsync(string? userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task<PodcastBackup> ExportAsync(CancellationToken cancellationToken);
    // Newest edit wins; nothing already here is rolled back.
    Task<int> RestoreAsync(PodcastBackup backup, CancellationToken cancellationToken);
}

public sealed class SqlitePodcastStore : IPodcastStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqlitePodcastStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "podcasts.db")}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS PodcastShows (
                Id TEXT PRIMARY KEY,
                FeedUrl TEXT NOT NULL,
                Title TEXT NOT NULL,
                NewestEpisodeAt TEXT,
                Data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PodcastEpisodes (
                Id TEXT PRIMARY KEY,
                PodcastId TEXT NOT NULL,
                PublishedAt TEXT,
                Archived INTEGER NOT NULL DEFAULT 0,
                Data TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PodcastEpisodes_Show ON PodcastEpisodes (PodcastId, PublishedAt);

            CREATE TABLE IF NOT EXISTS PodcastDownloads (
                EpisodeId TEXT PRIMARY KEY,
                State TEXT NOT NULL,
                RequestedAt TEXT,
                Data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PodcastChapters (
                EpisodeId TEXT PRIMARY KEY,
                Source TEXT NOT NULL,
                Data TEXT NOT NULL,
                FetchedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PodcastSubscriptions (
                UserKey TEXT NOT NULL,
                PodcastId TEXT NOT NULL,
                Subscribed INTEGER NOT NULL,
                SubscribedAt TEXT NOT NULL,
                Settings TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (UserKey, PodcastId)
            );

            CREATE TABLE IF NOT EXISTS PodcastProgress (
                UserKey TEXT NOT NULL,
                EpisodeId TEXT NOT NULL,
                PositionSeconds REAL NOT NULL,
                DurationSeconds REAL,
                Played INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PlayedAt TEXT,
                PRIMARY KEY (UserKey, EpisodeId)
            );
            CREATE INDEX IF NOT EXISTS IX_PodcastProgress_Episode ON PodcastProgress (EpisodeId);

            CREATE TABLE IF NOT EXISTS PodcastQueue (
                UserKey TEXT NOT NULL,
                EpisodeId TEXT NOT NULL,
                Position REAL NOT NULL,
                AddedAt TEXT NOT NULL,
                PRIMARY KEY (UserKey, EpisodeId)
            );

            CREATE TABLE IF NOT EXISTS PodcastBookmarks (
                Id TEXT PRIMARY KEY,
                UserKey TEXT NOT NULL,
                EpisodeId TEXT NOT NULL,
                PositionSeconds REAL NOT NULL,
                Note TEXT NOT NULL,
                TranscriptSnippet TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PodcastClips (
                Id TEXT PRIMARY KEY,
                UserKey TEXT NOT NULL,
                EpisodeId TEXT NOT NULL,
                StartSeconds REAL NOT NULL,
                DurationSeconds REAL NOT NULL,
                Title TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PodcastTranscripts (
                EpisodeId TEXT PRIMARY KEY,
                Source TEXT NOT NULL,
                Language TEXT,
                Segments TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS PodcastTranscriptSearch USING fts5(
                EpisodeId UNINDEXED, Start UNINDEXED, Text, tokenize = 'unicode61 remove_diacritics 2'
            );

            CREATE TABLE IF NOT EXISTS PodcastListening (
                UserKey TEXT NOT NULL,
                HourStart TEXT NOT NULL,
                PodcastId TEXT NOT NULL,
                ListenedSeconds REAL NOT NULL,
                SavedSeconds REAL NOT NULL,
                PRIMARY KEY (UserKey, HourStart, PodcastId)
            );
            """;
        command.ExecuteNonQuery();
    }

    // Every call is a handful of statements on one connection; the lock keeps them from
    // interleaving (Microsoft.Data.Sqlite's async methods run synchronously anyway).
    private async Task<T> LockedAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try { return work(); }
        finally { _lock.Release(); }
    }

    private Task LockedAsync(Action work, CancellationToken cancellationToken) =>
        LockedAsync(() => { work(); return true; }, cancellationToken);

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, null, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(read(reader));
        return list;
    }

    private static string Id(Guid id) => id.ToString("N");
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static object? Time(DateTimeOffset? value) => value is { } v ? Time(v) : null;
    private static DateTimeOffset ReadTime(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ReadTimeOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTime(reader, ordinal);
    private static double? ReadDoubleOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    private static string? ReadStringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)!;

    private static string InList(IEnumerable<Guid> ids) =>
        string.Join(",", ids.Distinct().Select(id => $"'{id:N}'"));

    // ---- shows ------------------------------------------------------------------------------

    public Task<IReadOnlyList<PodcastShow>> GetShowsAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastShow>>(() => Query("SELECT Data FROM PodcastShows ORDER BY Title", r => FromJson<PodcastShow>(r.GetString(0))), cancellationToken);

    public Task<PodcastShow?> GetShowAsync(Guid id, CancellationToken cancellationToken) =>
        LockedAsync(() => Query("SELECT Data FROM PodcastShows WHERE Id = $id", r => FromJson<PodcastShow>(r.GetString(0)), ("$id", Id(id))).FirstOrDefault(), cancellationToken);

    public Task SaveShowAsync(PodcastShow show, CancellationToken cancellationToken) =>
        LockedAsync(() => SaveShow(show, null), cancellationToken);

    private void SaveShow(PodcastShow show, SqliteTransaction? transaction)
    {
        using var command = Command("""
            INSERT INTO PodcastShows (Id, FeedUrl, Title, NewestEpisodeAt, Data) VALUES ($id, $feed, $title, $newest, $data)
            ON CONFLICT(Id) DO UPDATE SET FeedUrl = excluded.FeedUrl, Title = excluded.Title, NewestEpisodeAt = excluded.NewestEpisodeAt, Data = excluded.Data
            """, transaction, ("$id", Id(show.Id)), ("$feed", show.FeedUrl), ("$title", show.Title), ("$newest", Time(show.NewestEpisodeAt)), ("$data", Json(show)));
        command.ExecuteNonQuery();
    }

    public Task DeleteShowAsync(Guid id, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var transaction = _connection.BeginTransaction();
            var episodes = $"SELECT Id FROM PodcastEpisodes WHERE PodcastId = $id";
            using var command = Command($"""
                DELETE FROM PodcastProgress WHERE EpisodeId IN ({episodes});
                DELETE FROM PodcastQueue WHERE EpisodeId IN ({episodes});
                DELETE FROM PodcastBookmarks WHERE EpisodeId IN ({episodes});
                DELETE FROM PodcastDownloads WHERE EpisodeId IN ({episodes});
                DELETE FROM PodcastChapters WHERE EpisodeId IN ({episodes});
                DELETE FROM PodcastTranscripts WHERE EpisodeId IN ({episodes});
                DELETE FROM PodcastTranscriptSearch WHERE EpisodeId IN ({episodes});
                DELETE FROM PodcastClips WHERE EpisodeId IN ({episodes});
                DELETE FROM PodcastEpisodes WHERE PodcastId = $id;
                DELETE FROM PodcastSubscriptions WHERE PodcastId = $id;
                DELETE FROM PodcastShows WHERE Id = $id;
                """, transaction, ("$id", Id(id)));
            command.ExecuteNonQuery();
            transaction.Commit();
        }, cancellationToken);

    // ---- episodes ---------------------------------------------------------------------------

    private const string EpisodeSelect = """
        SELECT e.Data, d.Data FROM PodcastEpisodes e LEFT JOIN PodcastDownloads d ON d.EpisodeId = e.Id
        """;

    private static PodcastEpisode ReadEpisode(SqliteDataReader reader)
    {
        var episode = FromJson<PodcastEpisode>(reader.GetString(0));
        if (!reader.IsDBNull(1)) episode.Download = FromJson<PodcastDownload>(reader.GetString(1));
        return episode;
    }

    public Task<PodcastEpisode?> GetEpisodeAsync(Guid id, CancellationToken cancellationToken) =>
        LockedAsync(() => Query($"{EpisodeSelect} WHERE e.Id = $id", ReadEpisode, ("$id", Id(id))).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<PodcastEpisode>> GetEpisodesAsync(Guid podcastId, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastEpisode>>(() =>
            Query($"{EpisodeSelect} WHERE e.PodcastId = $id ORDER BY e.PublishedAt DESC", ReadEpisode, ("$id", Id(podcastId))), cancellationToken);

    public Task<IReadOnlyList<PodcastEpisode>> GetEpisodesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = InList(ids);
        return list.Length == 0
            ? Task.FromResult<IReadOnlyList<PodcastEpisode>>([])
            : LockedAsync<IReadOnlyList<PodcastEpisode>>(() => Query($"{EpisodeSelect} WHERE e.Id IN ({list})", ReadEpisode), cancellationToken);
    }

    public Task<IReadOnlyList<PodcastEpisode>> GetEpisodesForShowsAsync(IEnumerable<Guid> podcastIds, DateTimeOffset? publishedSince, CancellationToken cancellationToken)
    {
        var list = InList(podcastIds);
        if (list.Length == 0) return Task.FromResult<IReadOnlyList<PodcastEpisode>>([]);
        return LockedAsync<IReadOnlyList<PodcastEpisode>>(() => publishedSince is { } since
            ? Query($"{EpisodeSelect} WHERE e.PodcastId IN ({list}) AND e.PublishedAt >= $since ORDER BY e.PublishedAt DESC", ReadEpisode, ("$since", Time(since)))
            : Query($"{EpisodeSelect} WHERE e.PodcastId IN ({list}) ORDER BY e.PublishedAt DESC", ReadEpisode), cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> GetEpisodeIdsAsync(Guid podcastId, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<Guid>>(() => Query("SELECT Id FROM PodcastEpisodes WHERE PodcastId = $id", r => Guid.Parse(r.GetString(0)), ("$id", Id(podcastId))), cancellationToken);

    public Task<IReadOnlyList<PodcastEpisodeHeader>> GetEpisodeHeadersAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastEpisodeHeader>>(() => Query("SELECT Id, PodcastId, PublishedAt, Archived FROM PodcastEpisodes",
            r => new PodcastEpisodeHeader(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), ReadTime(r, 2), r.GetInt64(3) != 0)), cancellationToken);

    public Task UpsertEpisodesAsync(IEnumerable<PodcastEpisode> episodes, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var episode in episodes) UpsertEpisode(episode, transaction);
            transaction.Commit();
        }, cancellationToken);

    private void UpsertEpisode(PodcastEpisode episode, SqliteTransaction transaction)
    {
        using var command = Command("""
            INSERT INTO PodcastEpisodes (Id, PodcastId, PublishedAt, Archived, Data) VALUES ($id, $podcast, $published, $archived, $data)
            ON CONFLICT(Id) DO UPDATE SET PodcastId = excluded.PodcastId, PublishedAt = excluded.PublishedAt, Archived = excluded.Archived, Data = excluded.Data
            """, transaction, ("$id", Id(episode.Id)), ("$podcast", Id(episode.PodcastId)),
            ("$published", Time(episode.PublishedAt ?? episode.FirstSeenAt)), ("$archived", episode.Archived ? 1 : 0), ("$data", Json(episode)));
        command.ExecuteNonQuery();
    }

    public Task<int> ArchiveMissingAsync(Guid podcastId, IReadOnlySet<Guid> present, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            var rows = Query("SELECT Data FROM PodcastEpisodes WHERE PodcastId = $id", r => FromJson<PodcastEpisode>(r.GetString(0)), ("$id", Id(podcastId)));
            using var transaction = _connection.BeginTransaction();
            var archived = 0;
            foreach (var episode in rows)
            {
                var shouldArchive = !present.Contains(episode.Id);
                if (episode.Archived == shouldArchive) continue;
                episode.Archived = shouldArchive;
                if (shouldArchive) archived++;
                UpsertEpisode(episode, transaction);
            }
            transaction.Commit();
            return archived;
        }, cancellationToken);

    public Task<(int Shows, int Episodes)> CountsAsync(CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var command = Command("SELECT (SELECT COUNT(*) FROM PodcastShows), (SELECT COUNT(*) FROM PodcastEpisodes)");
            using var reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt32(1));
        }, cancellationToken);

    // ---- downloads --------------------------------------------------------------------------

    public Task<PodcastDownload?> GetDownloadAsync(Guid episodeId, CancellationToken cancellationToken) =>
        LockedAsync(() => Query("SELECT Data FROM PodcastDownloads WHERE EpisodeId = $id", r => FromJson<PodcastDownload>(r.GetString(0)), ("$id", Id(episodeId))).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<PodcastDownload>> GetDownloadsAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastDownload>>(() =>
            Query("SELECT Data FROM PodcastDownloads WHERE State <> 'none' ORDER BY RequestedAt", r => FromJson<PodcastDownload>(r.GetString(0))), cancellationToken);

    public Task SaveDownloadAsync(PodcastDownload download, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var command = Command("""
                INSERT INTO PodcastDownloads (EpisodeId, State, RequestedAt, Data) VALUES ($id, $state, $requested, $data)
                ON CONFLICT(EpisodeId) DO UPDATE SET State = excluded.State, RequestedAt = excluded.RequestedAt, Data = excluded.Data
                """, null, ("$id", Id(download.EpisodeId)), ("$state", download.State), ("$requested", Time(download.RequestedAt)), ("$data", Json(download)));
            command.ExecuteNonQuery();
        }, cancellationToken);

    // ---- chapters ---------------------------------------------------------------------------

    public Task<IReadOnlyList<PodcastChapter>?> GetChaptersAsync(Guid episodeId, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastChapter>?>(() =>
            Query("SELECT Data FROM PodcastChapters WHERE EpisodeId = $id", r => FromJson<List<PodcastChapter>>(r.GetString(0)), ("$id", Id(episodeId))).FirstOrDefault(), cancellationToken);

    public Task SaveChaptersAsync(Guid episodeId, string source, IReadOnlyList<PodcastChapter> chapters, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var command = Command("""
                INSERT INTO PodcastChapters (EpisodeId, Source, Data, FetchedAt) VALUES ($id, $source, $data, $at)
                ON CONFLICT(EpisodeId) DO UPDATE SET Source = excluded.Source, Data = excluded.Data, FetchedAt = excluded.FetchedAt
                """, null, ("$id", Id(episodeId)), ("$source", source), ("$data", Json(chapters)), ("$at", Time(DateTimeOffset.UtcNow)));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<IReadOnlySet<Guid>> EpisodesWithChaptersAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlySet<Guid>>(() =>
            Query("SELECT EpisodeId FROM PodcastChapters WHERE Data <> '[]'", r => Guid.Parse(r.GetString(0))).ToHashSet(), cancellationToken);

    // ---- subscriptions ----------------------------------------------------------------------

    private static PodcastSubscription ReadSubscription(SqliteDataReader r) => new(
        r.GetString(0), Guid.Parse(r.GetString(1)), r.GetInt64(2) != 0, ReadTime(r, 3), FromJson<PodcastMySettings>(r.GetString(4)), ReadTime(r, 5));

    public Task<IReadOnlyList<PodcastSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastSubscription>>(() =>
            Query("SELECT UserKey, PodcastId, Subscribed, SubscribedAt, Settings, UpdatedAt FROM PodcastSubscriptions ORDER BY SubscribedAt", ReadSubscription), cancellationToken);

    public Task SaveSubscriptionAsync(PodcastSubscription subscription, CancellationToken cancellationToken) =>
        LockedAsync(() => SaveSubscription(subscription, null), cancellationToken);

    private void SaveSubscription(PodcastSubscription s, SqliteTransaction? transaction)
    {
        using var command = Command("""
            INSERT INTO PodcastSubscriptions (UserKey, PodcastId, Subscribed, SubscribedAt, Settings, UpdatedAt)
            VALUES ($user, $podcast, $subscribed, $at, $settings, $updated)
            ON CONFLICT(UserKey, PodcastId) DO UPDATE SET Subscribed = excluded.Subscribed, SubscribedAt = excluded.SubscribedAt,
                Settings = excluded.Settings, UpdatedAt = excluded.UpdatedAt
            """, transaction, ("$user", s.UserKey), ("$podcast", Id(s.PodcastId)), ("$subscribed", s.Subscribed ? 1 : 0),
            ("$at", Time(s.SubscribedAt)), ("$settings", Json(s.Settings)), ("$updated", Time(s.UpdatedAt)));
        command.ExecuteNonQuery();
    }

    // ---- progress ---------------------------------------------------------------------------

    private const string ProgressSelect = "SELECT UserKey, EpisodeId, PositionSeconds, DurationSeconds, Played, UpdatedAt, PlayedAt FROM PodcastProgress";

    private static PodcastProgress ReadProgress(SqliteDataReader r) => new(
        r.GetString(0), Guid.Parse(r.GetString(1)), r.GetDouble(2), ReadDoubleOrNull(r, 3), r.GetInt64(4) != 0, ReadTime(r, 5), ReadTimeOrNull(r, 6));

    public Task<IReadOnlyList<PodcastProgress>> GetProgressAsync(string userKey, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastProgress>>(() => Query($"{ProgressSelect} WHERE UserKey = $user", ReadProgress, ("$user", userKey)), cancellationToken);

    public Task<IReadOnlyList<PodcastProgress>> GetProgressForEpisodesAsync(IEnumerable<Guid> episodeIds, CancellationToken cancellationToken)
    {
        var list = InList(episodeIds);
        return list.Length == 0
            ? Task.FromResult<IReadOnlyList<PodcastProgress>>([])
            : LockedAsync<IReadOnlyList<PodcastProgress>>(() => Query($"{ProgressSelect} WHERE EpisodeId IN ({list})", ReadProgress), cancellationToken);
    }

    public Task SetProgressAsync(PodcastProgress progress, CancellationToken cancellationToken) =>
        LockedAsync(() => SetProgress(progress, null), cancellationToken);

    private void SetProgress(PodcastProgress p, SqliteTransaction? transaction)
    {
        using var command = Command("""
            INSERT INTO PodcastProgress (UserKey, EpisodeId, PositionSeconds, DurationSeconds, Played, UpdatedAt, PlayedAt)
            VALUES ($user, $episode, $position, $duration, $played, $updated, $playedAt)
            ON CONFLICT(UserKey, EpisodeId) DO UPDATE SET PositionSeconds = excluded.PositionSeconds, DurationSeconds = excluded.DurationSeconds,
                Played = excluded.Played, UpdatedAt = excluded.UpdatedAt, PlayedAt = excluded.PlayedAt
            """, transaction, ("$user", p.UserKey), ("$episode", Id(p.EpisodeId)), ("$position", p.PositionSeconds), ("$duration", p.DurationSeconds),
            ("$played", p.Played ? 1 : 0), ("$updated", Time(p.UpdatedAt)), ("$playedAt", Time(p.PlayedAt)));
        command.ExecuteNonQuery();
    }

    public Task DeleteProgressAsync(string userKey, Guid episodeId, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var command = Command("DELETE FROM PodcastProgress WHERE UserKey = $user AND EpisodeId = $episode", null, ("$user", userKey), ("$episode", Id(episodeId)));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task SetProgressManyAsync(IReadOnlyList<PodcastProgress> rows, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var row in rows) SetProgress(row, transaction);
            transaction.Commit();
        }, cancellationToken);

    public Task DeleteProgressManyAsync(string userKey, IReadOnlyList<Guid> episodeIds, CancellationToken cancellationToken)
    {
        var list = InList(episodeIds);
        return list.Length == 0
            ? Task.CompletedTask
            : LockedAsync(() =>
            {
                using var command = Command($"DELETE FROM PodcastProgress WHERE UserKey = $user AND EpisodeId IN ({list})", null, ("$user", userKey));
                command.ExecuteNonQuery();
            }, cancellationToken);
    }

    public Task<IReadOnlyList<PodcastProgress>> AllProgressAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastProgress>>(() => Query(ProgressSelect, ReadProgress), cancellationToken);

    // ---- queue ------------------------------------------------------------------------------

    private static PodcastQueueEntry ReadQueue(SqliteDataReader r) => new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetDouble(2), ReadTime(r, 3));

    public Task<IReadOnlyList<PodcastQueueEntry>> GetQueueAsync(string userKey, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastQueueEntry>>(() =>
            Query("SELECT UserKey, EpisodeId, Position, AddedAt FROM PodcastQueue WHERE UserKey = $user ORDER BY Position, AddedAt", ReadQueue, ("$user", userKey)), cancellationToken);

    public Task SetQueueAsync(string userKey, IReadOnlyList<Guid> episodeIds, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            var added = Query("SELECT EpisodeId, AddedAt FROM PodcastQueue WHERE UserKey = $user", r => (Guid.Parse(r.GetString(0)), ReadTime(r, 1)), ("$user", userKey))
                .ToDictionary(x => x.Item1, x => x.Item2);
            using var transaction = _connection.BeginTransaction();
            using (var clear = Command("DELETE FROM PodcastQueue WHERE UserKey = $user", transaction, ("$user", userKey))) clear.ExecuteNonQuery();
            var now = DateTimeOffset.UtcNow;
            var position = 0;
            foreach (var id in episodeIds.Distinct())
            {
                using var insert = Command("INSERT INTO PodcastQueue (UserKey, EpisodeId, Position, AddedAt) VALUES ($user, $episode, $position, $added)",
                    transaction, ("$user", userKey), ("$episode", Id(id)), ("$position", (double)position++), ("$added", Time(added.GetValueOrDefault(id, now))));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }, cancellationToken);

    public Task<IReadOnlyList<PodcastQueueEntry>> AllQueuesAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastQueueEntry>>(() =>
            Query("SELECT UserKey, EpisodeId, Position, AddedAt FROM PodcastQueue ORDER BY UserKey, Position", ReadQueue), cancellationToken);

    // ---- bookmarks --------------------------------------------------------------------------

    private const string BookmarkSelect = "SELECT Id, UserKey, EpisodeId, PositionSeconds, Note, TranscriptSnippet, CreatedAt, UpdatedAt FROM PodcastBookmarks";

    private static PodcastBookmark ReadBookmark(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), r.GetString(1), Guid.Parse(r.GetString(2)), r.GetDouble(3), r.GetString(4), ReadStringOrNull(r, 5), ReadTime(r, 6), ReadTime(r, 7));

    public Task<IReadOnlyList<PodcastBookmark>> GetBookmarksAsync(string? userKey, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastBookmark>>(() => userKey is null
            ? Query($"{BookmarkSelect} ORDER BY CreatedAt DESC", ReadBookmark)
            : Query($"{BookmarkSelect} WHERE UserKey = $user ORDER BY CreatedAt DESC", ReadBookmark, ("$user", userKey)), cancellationToken);

    public Task<PodcastBookmark?> GetBookmarkAsync(Guid id, CancellationToken cancellationToken) =>
        LockedAsync(() => Query($"{BookmarkSelect} WHERE Id = $id", ReadBookmark, ("$id", Id(id))).FirstOrDefault(), cancellationToken);

    public Task SaveBookmarkAsync(PodcastBookmark bookmark, CancellationToken cancellationToken) =>
        LockedAsync(() => SaveBookmark(bookmark, null), cancellationToken);

    private void SaveBookmark(PodcastBookmark b, SqliteTransaction? transaction)
    {
        using var command = Command("""
            INSERT INTO PodcastBookmarks (Id, UserKey, EpisodeId, PositionSeconds, Note, TranscriptSnippet, CreatedAt, UpdatedAt)
            VALUES ($id, $user, $episode, $position, $note, $snippet, $created, $updated)
            ON CONFLICT(Id) DO UPDATE SET PositionSeconds = excluded.PositionSeconds, Note = excluded.Note,
                TranscriptSnippet = excluded.TranscriptSnippet, UpdatedAt = excluded.UpdatedAt
            """, transaction, ("$id", Id(b.Id)), ("$user", b.UserKey), ("$episode", Id(b.EpisodeId)), ("$position", b.PositionSeconds),
            ("$note", b.Note), ("$snippet", b.TranscriptSnippet), ("$created", Time(b.CreatedAt)), ("$updated", Time(b.UpdatedAt)));
        command.ExecuteNonQuery();
    }

    public Task DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var command = Command("DELETE FROM PodcastBookmarks WHERE Id = $id", null, ("$id", Id(id)));
            command.ExecuteNonQuery();
        }, cancellationToken);

    // ---- clips ------------------------------------------------------------------------------

    private const string ClipSelect = "SELECT Id, UserKey, EpisodeId, StartSeconds, DurationSeconds, Title, FilePath, CreatedAt FROM PodcastClips";

    private static PodcastClip ReadClip(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), r.GetString(1), Guid.Parse(r.GetString(2)), r.GetDouble(3), r.GetDouble(4), r.GetString(5), r.GetString(6), ReadTime(r, 7));

    public Task<IReadOnlyList<PodcastClip>> GetClipsAsync(string? userKey, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastClip>>(() => userKey is null
            ? Query($"{ClipSelect} ORDER BY CreatedAt DESC", ReadClip)
            : Query($"{ClipSelect} WHERE UserKey = $user ORDER BY CreatedAt DESC", ReadClip, ("$user", userKey)), cancellationToken);

    public Task<PodcastClip?> GetClipAsync(Guid id, CancellationToken cancellationToken) =>
        LockedAsync(() => Query($"{ClipSelect} WHERE Id = $id", ReadClip, ("$id", Id(id))).FirstOrDefault(), cancellationToken);

    public Task SaveClipAsync(PodcastClip clip, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var command = Command("""
                INSERT OR REPLACE INTO PodcastClips (Id, UserKey, EpisodeId, StartSeconds, DurationSeconds, Title, FilePath, CreatedAt)
                VALUES ($id, $user, $episode, $start, $duration, $title, $path, $created)
                """, null, ("$id", Id(clip.Id)), ("$user", clip.UserKey), ("$episode", Id(clip.EpisodeId)), ("$start", clip.StartSeconds),
                ("$duration", clip.DurationSeconds), ("$title", clip.Title), ("$path", clip.FilePath), ("$created", Time(clip.CreatedAt)));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task DeleteClipAsync(Guid id, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var command = Command("DELETE FROM PodcastClips WHERE Id = $id", null, ("$id", Id(id)));
            command.ExecuteNonQuery();
        }, cancellationToken);

    // ---- transcripts ------------------------------------------------------------------------

    public Task<PodcastTranscript?> GetTranscriptAsync(Guid episodeId, CancellationToken cancellationToken) =>
        LockedAsync(() => Query("SELECT Source, Language, Segments FROM PodcastTranscripts WHERE EpisodeId = $id",
            r => new PodcastTranscript(r.GetString(0), ReadStringOrNull(r, 1), FromJson<List<PodcastTranscriptSegment>>(r.GetString(2))),
            ("$id", Id(episodeId))).FirstOrDefault(), cancellationToken);

    public Task SaveTranscriptAsync(Guid episodeId, PodcastTranscript transcript, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var transaction = _connection.BeginTransaction();
            using (var command = Command("""
                INSERT INTO PodcastTranscripts (EpisodeId, Source, Language, Segments, CreatedAt) VALUES ($id, $source, $language, $segments, $at)
                ON CONFLICT(EpisodeId) DO UPDATE SET Source = excluded.Source, Language = excluded.Language, Segments = excluded.Segments, CreatedAt = excluded.CreatedAt;
                DELETE FROM PodcastTranscriptSearch WHERE EpisodeId = $id;
                """, transaction, ("$id", Id(episodeId)), ("$source", transcript.Source), ("$language", transcript.Language),
                ("$segments", Json(transcript.Segments)), ("$at", Time(DateTimeOffset.UtcNow))))
                command.ExecuteNonQuery();

            // Indexed a few segments at a time, so a hit lands within seconds of the words rather
            // than at the start of a ten-minute block, and the snippet has words either side.
            foreach (var group in GroupForSearch(transcript.Segments))
            {
                using var insert = Command("INSERT INTO PodcastTranscriptSearch (EpisodeId, Start, Text) VALUES ($id, $start, $text)",
                    transaction, ("$id", Id(episodeId)), ("$start", group.Start), ("$text", group.Text));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }, cancellationToken);

    private static IEnumerable<(double Start, string Text)> GroupForSearch(IReadOnlyList<PodcastTranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        double? start = null;
        foreach (var segment in segments)
        {
            start ??= segment.Start;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(segment.Text);
            if (builder.Length >= 240)
            {
                yield return (start.Value, builder.ToString());
                builder.Clear();
                start = null;
            }
        }
        if (builder.Length > 0 && start is { } last) yield return (last, builder.ToString());
    }

    public Task<IReadOnlyDictionary<Guid, string>> TranscriptSourcesAsync(CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyDictionary<Guid, string>>(() =>
            Query("SELECT EpisodeId, Source FROM PodcastTranscripts", r => (Guid.Parse(r.GetString(0)), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2),
            cancellationToken);

    public Task<IReadOnlyList<PodcastTranscriptHit>> SearchTranscriptsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var match = FtsQuery(query);
        if (match is null) return Task.FromResult<IReadOnlyList<PodcastTranscriptHit>>([]);
        return LockedAsync<IReadOnlyList<PodcastTranscriptHit>>(() => Query("""
            SELECT EpisodeId, Start, snippet(PodcastTranscriptSearch, 2, '', '', '…', 16)
            FROM PodcastTranscriptSearch WHERE PodcastTranscriptSearch MATCH $match ORDER BY rank LIMIT $limit
            """, r => new PodcastTranscriptHit(Guid.Parse(r.GetString(0)), r.GetDouble(1), r.GetString(2)), ("$match", match), ("$limit", limit)), cancellationToken);
    }

    // Every word must appear; each is quoted so punctuation and FTS5 operators in what someone
    // typed are just text. The last word matches as a prefix, for search-as-you-type.
    public static string? FtsQuery(string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(c => char.IsLetterOrDigit(c) || c == '\'').ToArray()))
            .Where(word => word.Length > 0)
            .Take(12)
            .ToList();
        if (words.Count == 0) return null;
        return string.Join(" ", words.Select((word, index) =>
            "\"" + word.Replace("\"", "\"\"") + "\"" + (index == words.Count - 1 && word.Length >= 3 ? "*" : "")));
    }

    // ---- listening --------------------------------------------------------------------------

    public Task AddListeningAsync(PodcastListening l, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            using var command = Command("""
                INSERT INTO PodcastListening (UserKey, HourStart, PodcastId, ListenedSeconds, SavedSeconds) VALUES ($user, $hour, $podcast, $listened, $saved)
                ON CONFLICT(UserKey, HourStart, PodcastId) DO UPDATE SET ListenedSeconds = ListenedSeconds + excluded.ListenedSeconds,
                    SavedSeconds = SavedSeconds + excluded.SavedSeconds
                """, null, ("$user", l.UserKey), ("$hour", Time(l.HourStart)), ("$podcast", Id(l.PodcastId)), ("$listened", l.ListenedSeconds), ("$saved", l.SavedSeconds));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<IReadOnlyList<PodcastListening>> GetListeningAsync(string? userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        LockedAsync<IReadOnlyList<PodcastListening>>(() =>
        {
            static PodcastListening Read(SqliteDataReader r) => new(r.GetString(0), ReadTime(r, 1), Guid.Parse(r.GetString(2)), r.GetDouble(3), r.GetDouble(4));
            const string select = "SELECT UserKey, HourStart, PodcastId, ListenedSeconds, SavedSeconds FROM PodcastListening WHERE HourStart >= $from AND HourStart < $to";
            return userKey is null
                ? Query(select, Read, ("$from", Time(from)), ("$to", Time(to)))
                : Query(select + " AND UserKey = $user", Read, ("$from", Time(from)), ("$to", Time(to)), ("$user", userKey));
        }, cancellationToken);

    // ---- backup -----------------------------------------------------------------------------

    public async Task<PodcastBackup> ExportAsync(CancellationToken cancellationToken)
    {
        var archived = await LockedAsync(() => Query("SELECT Data FROM PodcastEpisodes WHERE Archived = 1", r => FromJson<PodcastEpisode>(r.GetString(0))), cancellationToken);
        return new PodcastBackup(
            await GetShowsAsync(cancellationToken),
            await GetSubscriptionsAsync(cancellationToken),
            await AllProgressAsync(cancellationToken),
            await AllQueuesAsync(cancellationToken),
            await GetBookmarksAsync(null, cancellationToken),
            archived,
            await GetListeningAsync(null, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, cancellationToken));
    }

    public Task<int> RestoreAsync(PodcastBackup backup, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            var restored = 0;
            // Read before the transaction opens: a command outside it cannot run while it is open.
            var shows = Query("SELECT Id FROM PodcastShows", r => Guid.Parse(r.GetString(0))).ToHashSet();
            var episodes = Query("SELECT Id FROM PodcastEpisodes", r => Guid.Parse(r.GetString(0))).ToHashSet();
            var subscriptions = Query("SELECT UserKey, PodcastId, Subscribed, SubscribedAt, Settings, UpdatedAt FROM PodcastSubscriptions", ReadSubscription)
                .ToDictionary(s => (s.UserKey, s.PodcastId));
            var progress = Query(ProgressSelect, ReadProgress).ToDictionary(p => (p.UserKey, p.EpisodeId));
            var bookmarks = Query(BookmarkSelect, ReadBookmark).ToDictionary(b => b.Id);
            using var transaction = _connection.BeginTransaction();

            foreach (var show in backup.Shows ?? [])
            {
                if (show.Id == Guid.Empty || string.IsNullOrWhiteSpace(show.FeedUrl) || shows.Contains(show.Id)) continue;
                // Never refreshed here: the next refresh is a first fetch, which neither pushes
                // nor queues the whole back catalogue as "new".
                show.LastRefreshedAt = null;
                show.ETag = null;
                show.LastModified = null;
                show.Settings ??= new PodcastShowSettings();
                SaveShow(show, transaction);
                shows.Add(show.Id);
                restored++;
            }

            foreach (var episode in backup.ArchivedEpisodes ?? [])
            {
                if (episode.Id == Guid.Empty || !shows.Contains(episode.PodcastId) || !episodes.Add(episode.Id)) continue;
                episode.Archived = true;
                UpsertEpisode(episode, transaction);
                restored++;
            }

            foreach (var subscription in backup.Subscriptions ?? [])
            {
                if (subscription.UserKey is null || !shows.Contains(subscription.PodcastId)) continue;
                if (subscriptions.TryGetValue((subscription.UserKey, subscription.PodcastId), out var current) && current.UpdatedAt >= subscription.UpdatedAt) continue;
                SaveSubscription(subscription with { Settings = subscription.Settings ?? new PodcastMySettings() }, transaction);
                restored++;
            }

            foreach (var row in backup.Progress ?? [])
            {
                if (row.UserKey is null || row.EpisodeId == Guid.Empty) continue;
                if (progress.TryGetValue((row.UserKey, row.EpisodeId), out var current) && current.UpdatedAt >= row.UpdatedAt) continue;
                SetProgress(row, transaction);
                restored++;
            }

            foreach (var entry in backup.Queue ?? [])
            {
                if (entry.UserKey is null || entry.EpisodeId == Guid.Empty) continue;
                using var insert = Command("INSERT OR IGNORE INTO PodcastQueue (UserKey, EpisodeId, Position, AddedAt) VALUES ($user, $episode, $position, $added)",
                    transaction, ("$user", entry.UserKey), ("$episode", Id(entry.EpisodeId)), ("$position", entry.Position), ("$added", Time(entry.AddedAt)));
                restored += insert.ExecuteNonQuery();
            }

            foreach (var bookmark in backup.Bookmarks ?? [])
            {
                if (bookmark.Id == Guid.Empty || bookmark.UserKey is null) continue;
                if (bookmarks.TryGetValue(bookmark.Id, out var current) && current.UpdatedAt >= bookmark.UpdatedAt) continue;
                SaveBookmark(bookmark with { Note = bookmark.Note ?? "" }, transaction);
                restored++;
            }

            foreach (var row in backup.Listening ?? [])
            {
                if (row.UserKey is null) continue;
                using var insert = Command("INSERT OR IGNORE INTO PodcastListening (UserKey, HourStart, PodcastId, ListenedSeconds, SavedSeconds) VALUES ($user, $hour, $podcast, $listened, $saved)",
                    transaction, ("$user", row.UserKey), ("$hour", Time(row.HourStart)), ("$podcast", Id(row.PodcastId)), ("$listened", row.ListenedSeconds), ("$saved", row.SavedSeconds));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return restored;
        }, cancellationToken);

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
