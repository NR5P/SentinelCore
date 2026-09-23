using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Client.Organizer;

public sealed record BlobRecord(string Hash, bool NeedsUpload, bool HasOriginal, long Size, long LastUsedMillis);

/// <summary>
/// This PC's copy: items, the queue of unacknowledged edits, sync state and the index of attachment
/// files. An interface so the engine's tests can run against memory, while the app uses SQLite -
/// see <see cref="SqliteOrganizerStore"/>.
/// </summary>
public interface IOrganizerLocalStore
{
    string BlobDirectory { get; }
    string ThumbnailDirectory { get; }

    List<OrganizerItem> LoadItems();
    void SaveItems(IReadOnlyCollection<OrganizerItem> items);
    void DeleteItems(IReadOnlyCollection<string> ids);
    void ClearItems(IReadOnlySet<string> keeping);

    Dictionary<string, OrganizerChange> LoadPending();
    void SavePending(string id, OrganizerChange? change);

    string? Meta(string key);
    void SetMeta(string key, string? value);

    BlobRecord? Blob(string hash);
    List<BlobRecord> AllBlobs();
    void SaveBlob(BlobRecord record);
    void DeleteBlob(string hash);

    /// <summary>Everything, except edits still waiting to go up when keepPending is set.</summary>
    void Wipe(bool keepPending);

    string OriginalFile(string hash) => Path.Combine(BlobDirectory, hash);
    string ThumbnailFile(string hash) => Path.Combine(ThumbnailDirectory, hash + ".jpg");
}

internal static class StoreFiles
{
    public static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void EmptyDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory)) DeleteQuietly(file);
    }
}

public sealed class InMemoryOrganizerStore : IOrganizerLocalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OrganizerItem> _items = new();
    private readonly Dictionary<string, OrganizerChange> _pending = new();
    private readonly Dictionary<string, string> _meta = new();
    private readonly Dictionary<string, BlobRecord> _blobs = new();

    public InMemoryOrganizerStore(string root)
    {
        BlobDirectory = Directory.CreateDirectory(Path.Combine(root, "blobs")).FullName;
        ThumbnailDirectory = Directory.CreateDirectory(Path.Combine(root, "thumbnails")).FullName;
    }

    public string BlobDirectory { get; }
    public string ThumbnailDirectory { get; }

    public List<OrganizerItem> LoadItems() { lock (_gate) return _items.Values.ToList(); }

    public void SaveItems(IReadOnlyCollection<OrganizerItem> items) { lock (_gate) foreach (var item in items) _items[item.Id] = item; }

    public void DeleteItems(IReadOnlyCollection<string> ids) { lock (_gate) foreach (var id in ids) _items.Remove(id); }

    public void ClearItems(IReadOnlySet<string> keeping)
    {
        lock (_gate) foreach (var id in _items.Keys.Where(id => !keeping.Contains(id)).ToList()) _items.Remove(id);
    }

    public Dictionary<string, OrganizerChange> LoadPending() { lock (_gate) return new Dictionary<string, OrganizerChange>(_pending); }

    public void SavePending(string id, OrganizerChange? change)
    {
        lock (_gate)
        {
            if (change is null) _pending.Remove(id);
            else _pending[id] = change;
        }
    }

    public string? Meta(string key) { lock (_gate) return _meta.TryGetValue(key, out var value) ? value : null; }

    public void SetMeta(string key, string? value)
    {
        lock (_gate)
        {
            if (value is null) _meta.Remove(key);
            else _meta[key] = value;
        }
    }

    public BlobRecord? Blob(string hash) { lock (_gate) return _blobs.TryGetValue(hash, out var record) ? record : null; }

    public List<BlobRecord> AllBlobs() { lock (_gate) return _blobs.Values.ToList(); }

    public void SaveBlob(BlobRecord record) { lock (_gate) _blobs[record.Hash] = record; }

    public void DeleteBlob(string hash)
    {
        lock (_gate) _blobs.Remove(hash);
        StoreFiles.DeleteQuietly(((IOrganizerLocalStore)this).OriginalFile(hash));
        StoreFiles.DeleteQuietly(((IOrganizerLocalStore)this).ThumbnailFile(hash));
    }

    public void Wipe(bool keepPending)
    {
        lock (_gate)
        {
            _items.Clear();
            _meta.Clear();
            if (keepPending) return;
            _pending.Clear();
            _blobs.Clear();
        }
        StoreFiles.EmptyDirectory(BlobDirectory);
        StoreFiles.EmptyDirectory(ThumbnailDirectory);
    }
}

/// <summary>
/// Items are stored whole as JSON - only this PC reads them, the engine holds them in memory while
/// running, and JSON is what lets fields this build doesn't know about survive.
/// </summary>
public sealed class SqliteOrganizerStore : IOrganizerLocalStore, IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    public SqliteOrganizerStore(string root)
    {
        Directory.CreateDirectory(root);
        BlobDirectory = Directory.CreateDirectory(Path.Combine(root, "blobs")).FullName;
        ThumbnailDirectory = Directory.CreateDirectory(Path.Combine(root, "thumbnails")).FullName;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "organizer.sqlite"),
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS items (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS pending (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS blobs (hash TEXT PRIMARY KEY, needsUpload INTEGER NOT NULL,
                hasOriginal INTEGER NOT NULL, size INTEGER NOT NULL, lastUsed INTEGER NOT NULL);
            """);
    }

    public string BlobDirectory { get; }
    public string ThumbnailDirectory { get; }

    public List<OrganizerItem> LoadItems()
    {
        var result = new List<OrganizerItem>();
        lock (_gate)
        {
            using var command = Command("SELECT json FROM items");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (JsonNode.Parse(reader.GetString(0)) is JsonObject json) result.Add(OrganizerJson.Item(json));
                }
                catch (Exception)
                {
                    // One unreadable row is skipped; the next full sync puts it back.
                }
            }
        }
        return result;
    }

    public void SaveItems(IReadOnlyCollection<OrganizerItem> items)
    {
        if (items.Count == 0) return;
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = Command("INSERT OR REPLACE INTO items (id, json) VALUES ($id, $json)");
            command.Transaction = transaction;
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var json = command.Parameters.Add("$json", SqliteType.Text);
            foreach (var item in items)
            {
                id.Value = item.Id;
                json.Value = OrganizerJson.ItemJson(item).ToJsonString();
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void DeleteItems(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = Command("DELETE FROM items WHERE id = $id");
            command.Transaction = transaction;
            var id = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var value in ids)
            {
                id.Value = value;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void ClearItems(IReadOnlySet<string> keeping) =>
        DeleteItems(LoadItems().Select(item => item.Id).Where(id => !keeping.Contains(id)).ToList());

    public Dictionary<string, OrganizerChange> LoadPending()
    {
        var result = new Dictionary<string, OrganizerChange>();
        lock (_gate)
        {
            using var command = Command("SELECT id, json FROM pending");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (JsonNode.Parse(reader.GetString(1)) is JsonObject json) result[reader.GetString(0)] = OrganizerJson.Change(json);
                }
                catch (Exception)
                {
                    // Unreadable: nothing sensible to send, so it is dropped rather than retried forever.
                }
            }
        }
        return result;
    }

    public void SavePending(string id, OrganizerChange? change)
    {
        lock (_gate)
        {
            if (change is null)
            {
                using var delete = Command("DELETE FROM pending WHERE id = $id");
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
                return;
            }
            using var command = Command("INSERT OR REPLACE INTO pending (id, json) VALUES ($id, $json)");
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$json", OrganizerJson.ChangeJson(change).ToJsonString());
            command.ExecuteNonQuery();
        }
    }

    public string? Meta(string key)
    {
        lock (_gate)
        {
            using var command = Command("SELECT value FROM meta WHERE key = $key");
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value)
    {
        lock (_gate)
        {
            using var command = value is null
                ? Command("DELETE FROM meta WHERE key = $key")
                : Command("INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value)");
            command.Parameters.AddWithValue("$key", key);
            if (value is not null) command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public BlobRecord? Blob(string hash)
    {
        lock (_gate)
        {
            using var command = Command("SELECT hash, needsUpload, hasOriginal, size, lastUsed FROM blobs WHERE hash = $hash");
            command.Parameters.AddWithValue("$hash", hash);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Record(reader) : null;
        }
    }

    public List<BlobRecord> AllBlobs()
    {
        var result = new List<BlobRecord>();
        lock (_gate)
        {
            using var command = Command("SELECT hash, needsUpload, hasOriginal, size, lastUsed FROM blobs");
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(Record(reader));
        }
        return result;
    }

    public void SaveBlob(BlobRecord record)
    {
        lock (_gate)
        {
            using var command = Command(
                "INSERT OR REPLACE INTO blobs (hash, needsUpload, hasOriginal, size, lastUsed) VALUES ($hash, $needs, $has, $size, $used)");
            command.Parameters.AddWithValue("$hash", record.Hash);
            command.Parameters.AddWithValue("$needs", record.NeedsUpload ? 1 : 0);
            command.Parameters.AddWithValue("$has", record.HasOriginal ? 1 : 0);
            command.Parameters.AddWithValue("$size", record.Size);
            command.Parameters.AddWithValue("$used", record.LastUsedMillis);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteBlob(string hash)
    {
        lock (_gate)
        {
            using var command = Command("DELETE FROM blobs WHERE hash = $hash");
            command.Parameters.AddWithValue("$hash", hash);
            command.ExecuteNonQuery();
        }
        StoreFiles.DeleteQuietly(((IOrganizerLocalStore)this).OriginalFile(hash));
        StoreFiles.DeleteQuietly(((IOrganizerLocalStore)this).ThumbnailFile(hash));
    }

    public void Wipe(bool keepPending)
    {
        lock (_gate)
        {
            Execute("DELETE FROM items; DELETE FROM meta;");
            if (keepPending) return;
            Execute("DELETE FROM pending; DELETE FROM blobs;");
        }
        StoreFiles.EmptyDirectory(BlobDirectory);
        StoreFiles.EmptyDirectory(ThumbnailDirectory);
    }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    private static BlobRecord Record(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1) != 0, reader.GetInt64(2) != 0, reader.GetInt64(3), reader.GetInt64(4));
}
