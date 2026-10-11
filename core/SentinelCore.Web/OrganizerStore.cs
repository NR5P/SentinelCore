using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// The Organizer: notes, calendar events, reminders and Trello-style boards, shared across every
// client app and kept on each device so it keeps working with no signal. The wire protocol is
// written up in docs/ORGANIZER_SYNC.md; this file is the server's half of it.
//
// Everything is one generic "item" with a kind and a bag of fields rather than a table per kind.
// That is what lets six clients and a backup share one sync and one merge rule: a new field on a
// card is a client change, not a migration here, and restoring a dead server from a phone's copy
// is the same merge as any other sync.
//
// Merging is per field, last writer wins by the field's clock (see OrganizerClock). Two phones
// editing different fields of the same card offline both keep their edit. The one place LWW would
// quietly lose real work - two people rewriting the same note's text - makes a conflicted copy
// instead (see SqliteOrganizerStore.Apply).
//
// Visibility is by space. Every item sits in exactly one: Family (everyone on the server),
// Personal (one account) or a Group (a named list of accounts). Spaces are keyed by username, not
// account id, on purpose: when this server dies and a new one is installed, accounts get
// recreated with fresh ids but the same names, and a device re-uploading "personal:morgan" has to
// land in Morgan's space on the new box.
// Written as text on the wire and in backups. The backup importer reads with plain Web defaults,
// which have no string-enum converter, so it has to be declared here rather than relied on from
// the host's JSON options.
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<OrganizerSpaceKind>))]
public enum OrganizerSpaceKind { Family, Personal, Group }

public sealed record OrganizerSpace(
    string Id,
    OrganizerSpaceKind Kind,
    string Name,
    IReadOnlyList<string> Members,
    string? Color,
    string? CreatedBy);

// Who is asking. Username is null when login is turned off, in which case every request is the
// same anonymous household and only the Family space exists for it - there is nobody to have a
// personal space or to be put in a group.
public sealed record OrganizerUser(string? Username, string? DisplayName)
{
    public static readonly OrganizerUser Shared = new(null, null);
}

public sealed class OrganizerItem
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string SpaceId { get; set; }
    public string? ParentId { get; set; }
    public bool Deleted { get; set; }
    public JsonObject Fields { get; set; } = [];
    public Dictionary<string, string> Clocks { get; set; } = new(StringComparer.Ordinal);
    public long Seq { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

// One item's worth of edits from a device. Fields carries only what changed, each with a clock.
// spaceId, parentId and deleted travel as ordinary fields so they merge by the same rule as a
// title does - moving a card and renaming it on two phones both survive.
public sealed class OrganizerChange
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public JsonObject Fields { get; set; } = [];
    public Dictionary<string, string> Clocks { get; set; } = new(StringComparer.Ordinal);

    // The clock of the note body the device started editing from. Only consulted for a note's
    // body; see SqliteOrganizerStore.Apply.
    public Dictionary<string, string>? BaseClocks { get; set; }
}

public sealed record OrganizerRejection(string Id, string Error);

public sealed record OrganizerPushResult(IReadOnlyList<OrganizerItem> Items, IReadOnlyList<OrganizerRejection> Rejected);

public sealed record OrganizerPullResult(
    long Cursor,
    bool HasMore,
    IReadOnlyList<OrganizerItem> Items,
    IReadOnlyList<string> Removed);

// FeedTokens and Preferences were added in backup schema 4, defaulted so an older file still reads.
// A feed token is the secret in a calendar subscription URL someone has pasted into another app;
// losing it breaks that subscription silently, with nothing on either side saying why.
public sealed record OrganizerBackup(
    IReadOnlyList<OrganizerSpace> Spaces,
    IReadOnlyList<OrganizerItem> Items,
    IReadOnlyList<OrganizerFeedToken>? FeedTokens = null,
    IReadOnlyDictionary<string, string>? Preferences = null);

public sealed record OrganizerFeedToken(string Token, string UserKey, DateTimeOffset CreatedAt);

public static class OrganizerKinds
{
    public const string Note = "note";
    public const string Event = "event";
    public const string Reminder = "reminder";
    public const string Board = "board";
    public const string Column = "column";
    public const string Card = "card";
    public const string Attachment = "attachment";
    public const string Comment = "comment";

    // What each kind may hang under. Null means "may stand alone". A reminder can be a sub-task of
    // another reminder; an attachment can go on anything but another attachment. A comment is its
    // own item under a card rather than a list inside the card, so two people commenting offline
    // both keep their comment instead of one list overwriting the other.
    private static readonly Dictionary<string, string[]?> Parents = new(StringComparer.Ordinal)
    {
        [Note] = null,
        [Event] = null,
        [Reminder] = [Reminder],
        [Board] = null,
        [Column] = [Board],
        [Card] = [Column],
        [Attachment] = [Note, Event, Reminder, Board, Column, Card],
        [Comment] = [Card],
    };

    // Kinds that are never valid without a parent.
    private static readonly HashSet<string> NeedsParent = new(StringComparer.Ordinal) { Column, Card, Attachment, Comment };

    public static bool IsKnown(string kind) => Parents.ContainsKey(kind);

    public static string? CheckParent(string kind, string? parentKind)
    {
        if (parentKind is null)
            return NeedsParent.Contains(kind) ? $"A {kind} has to belong to something." : null;

        var allowed = Parents[kind];
        return allowed is not null && allowed.Contains(parentKind)
            ? null
            : $"A {kind} can't go inside a {parentKind}.";
    }
}

public static class OrganizerSpaceIds
{
    public const string Family = "family";
    public static string Personal(string username) => $"personal:{username.ToLowerInvariant()}";
    public static string NewGroup() => $"group:{Guid.NewGuid():N}";
}

// A field's clock: "<13-digit unix ms>-<4-digit counter>-<device id>". Fixed-width on purpose so
// that ordinary ordinal string comparison IS the time comparison and every client can compare
// clocks without parsing them. The counter orders several edits inside one millisecond, and the
// device id breaks exact ties the same way everywhere.
public static partial class OrganizerClock
{
    // A device whose clock is running ahead would otherwise win every argument until real time
    // caught up with it - a phone set to next year would freeze every field it ever touched.
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"^(\d{13})-(\d{4})-([A-Za-z0-9_.:-]{1,64})$")]
    private static partial Regex Shape();

    public static bool IsValid(string? clock) => clock is not null && Shape().IsMatch(clock);

    public static string Clamp(string clock, DateTimeOffset now)
    {
        var match = Shape().Match(clock);
        var ms = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var limit = (now + MaxFutureSkew).ToUnixTimeMilliseconds();
        return ms <= limit ? clock : $"{now.ToUnixTimeMilliseconds():D13}-{match.Groups[2].Value}-{match.Groups[3].Value}";
    }

    public static string Server(DateTimeOffset now, int counter = 0) =>
        $"{now.ToUnixTimeMilliseconds():D13}-{counter % 10000:D4}-server";

    public static bool IsNewer(string candidate, string? current) =>
        current is null || string.CompareOrdinal(candidate, current) > 0;
}

public interface IOrganizerStore
{
    // Minted once when organizer.db is first created and never changed. A device that last
    // synced with a different id is talking to a new (or rebuilt) server and should offer to
    // upload its copy - that's the disaster-recovery path.
    string ServerId { get; }

    long CurrentCursor { get; }

    Task<IReadOnlyList<OrganizerSpace>> GetVisibleSpacesAsync(OrganizerUser user, CancellationToken cancellationToken);

    Task<OrganizerPushResult> ApplyChangesAsync(
        OrganizerUser user,
        IReadOnlyList<OrganizerChange> changes,
        IReadOnlyList<OrganizerSpace>? restoreSpaces,
        CancellationToken cancellationToken);

    Task<OrganizerPullResult> PullAsync(
        OrganizerUser user, long cursor, IReadOnlySet<string> knownSpaceIds, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrganizerItem>> GetItemsAsync(
        OrganizerUser user, string? kind, string? spaceId, CancellationToken cancellationToken);

    // Every item in every space (tombstones included) changed after `cursor`, oldest first. For
    // the server's own board automations, which watch the whole Organizer - never for a client.
    Task<OrganizerPullResult> PullEverythingAsync(long cursor, int limit, CancellationToken cancellationToken);

    Task<int> CountVisibleAsync(OrganizerUser user, CancellationToken cancellationToken);

    Task<OrganizerSpace> CreateGroupAsync(OrganizerUser user, string name, IReadOnlyList<string> members, string? color, CancellationToken cancellationToken);

    // Null when the space doesn't exist or isn't this person's to change.
    Task<OrganizerSpace?> UpdateGroupAsync(OrganizerUser user, string spaceId, string name, IReadOnlyList<string> members, string? color, CancellationToken cancellationToken);

    Task<bool> DeleteGroupAsync(OrganizerUser user, string spaceId, CancellationToken cancellationToken);

    // The attachment item that makes a blob visible to this person, if any - used to authorize a
    // download and to know what to call the file.
    Task<OrganizerItem?> FindVisibleAttachmentAsync(OrganizerUser user, string hash, CancellationToken cancellationToken);

    // Every blob hash a live attachment this person can see points at.
    Task<IReadOnlyList<string>> GetVisibleBlobHashesAsync(OrganizerUser user, CancellationToken cancellationToken);

    // Every blob hash any live attachment points at, for the orphan sweep.
    Task<IReadOnlySet<string>> GetAllLiveBlobHashesAsync(CancellationToken cancellationToken);

    Task<string> GetOrCreateFeedTokenAsync(OrganizerUser user, CancellationToken cancellationToken);
    Task<string> ResetFeedTokenAsync(OrganizerUser user, CancellationToken cancellationToken);

    // Who a calendar-subscription token belongs to, or null for an unknown/revoked token.
    Task<OrganizerUser?> ResolveFeedTokenAsync(string token, CancellationToken cancellationToken);

    // One person's Organizer preferences as JSON (which holiday calendars they show), or null
    // when they've never saved any. Kept per person so every device they use shows the same.
    Task<string?> GetPreferencesAsync(OrganizerUser user, CancellationToken cancellationToken);
    Task SetPreferencesAsync(OrganizerUser user, string json, CancellationToken cancellationToken);

    Task<OrganizerBackup> ExportAsync(CancellationToken cancellationToken);
    Task<int> ImportAsync(OrganizerBackup backup, CancellationToken cancellationToken);

    // Completes when anything changes after `cursor`, or when the timeout passes - the web and
    // desktop clients hold one of these open so an edit on the phone appears without polling.
    Task<long> WaitForChangeAsync(long cursor, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SqliteOrganizerStore : IOrganizerStore, IDisposable
{
    // Large enough for a long note with pasted content; small enough that one item can't be used
    // to push a database-sized blob through the sync path. Pictures and files go through the blob
    // endpoints, not fields.
    public const int MaxItemJsonBytes = 1024 * 1024;
    private const int MaxFieldsPerItem = 100;

    private static readonly Regex FieldName = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex ItemId = new("^[A-Za-z0-9_.:-]{1,80}$", RegexOptions.Compiled);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time;
    private long _seq;
    private readonly Lock _signalLock = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ServerId { get; }
    public long CurrentCursor => Interlocked.Read(ref _seq);

    public SqliteOrganizerStore(string dataDirectory, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "organizer.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Spaces (
                    Id TEXT PRIMARY KEY,
                    Kind TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Members TEXT NOT NULL,
                    Color TEXT,
                    CreatedBy TEXT,
                    CreatedAt TEXT NOT NULL,
                    Deleted INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS Items (
                    Id TEXT PRIMARY KEY,
                    Kind TEXT NOT NULL,
                    SpaceId TEXT NOT NULL,
                    ParentId TEXT,
                    Deleted INTEGER NOT NULL,
                    BlobHash TEXT,
                    Fields TEXT NOT NULL,
                    Clocks TEXT NOT NULL,
                    Seq INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    CreatedBy TEXT,
                    UpdatedAt TEXT NOT NULL,
                    UpdatedBy TEXT
                );
                CREATE INDEX IF NOT EXISTS IX_Items_Seq ON Items(Seq);
                CREATE INDEX IF NOT EXISTS IX_Items_Space ON Items(SpaceId);
                CREATE INDEX IF NOT EXISTS IX_Items_Parent ON Items(ParentId);
                CREATE INDEX IF NOT EXISTS IX_Items_Blob ON Items(BlobHash);
                -- An item that moved out of a space: whoever could see the old space but not the
                -- new one has to be told to drop it, and the item row alone can't say that.
                CREATE TABLE IF NOT EXISTS Removals (ItemId TEXT NOT NULL, FromSpaceId TEXT NOT NULL, Seq INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_Removals_Seq ON Removals(Seq);
                CREATE TABLE IF NOT EXISTS FeedTokens (Token TEXT PRIMARY KEY, UserKey TEXT NOT NULL UNIQUE, CreatedAt TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
        }

        ServerId = ReadMeta("serverId") ?? WriteMeta("serverId", Guid.NewGuid().ToString("N"));
        _seq = long.Parse(ReadMeta("seq") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

        var now = _time.GetUtcNow();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO Spaces (Id, Kind, Name, Members, Color, CreatedBy, CreatedAt)
                VALUES ($id, 'Family', 'Family', '[]', NULL, NULL, $now);
                """;
            command.Parameters.AddWithValue("$id", OrganizerSpaceIds.Family);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    // ---- spaces -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OrganizerSpace>> GetVisibleSpacesAsync(OrganizerUser user, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            EnsurePersonalSpace(user);
            return VisibleSpaces(user);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OrganizerSpace> CreateGroupAsync(
        OrganizerUser user, string name, IReadOnlyList<string> members, string? color, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var space = new OrganizerSpace(
                OrganizerSpaceIds.NewGroup(), OrganizerSpaceKind.Group, name.Trim(),
                NormalizeMembers(members, user.Username), color, user.Username);
            InsertSpace(space);
            BumpAndSignal();
            return space;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OrganizerSpace?> UpdateGroupAsync(
        OrganizerUser user, string spaceId, string name, IReadOnlyList<string> members, string? color, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var existing = LoadSpace(spaceId);
            if (existing is not { Kind: OrganizerSpaceKind.Group } || !CanSee(user, existing)) return null;

            // Not re-adding the editor here, unlike create: taking yourself out of a group is how
            // you leave it.
            var updated = existing with { Name = name.Trim(), Members = NormalizeMembers(members, null), Color = color };
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE Spaces SET Name = $name, Members = $members, Color = $color WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", spaceId);
            command.Parameters.AddWithValue("$name", updated.Name);
            command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(updated.Members));
            command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
            command.ExecuteNonQuery();
            BumpAndSignal();
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Deleting a group deletes what was in it: every item becomes a tombstone, so a device that
    // was offline at the time doesn't later push it all back. The space row is kept, marked
    // deleted, so its id can never be reused for something else.
    public async Task<bool> DeleteGroupAsync(OrganizerUser user, string spaceId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var existing = LoadSpace(spaceId);
            if (existing is not { Kind: OrganizerSpaceKind.Group } || !CanSee(user, existing)) return false;

            using var transaction = _connection.BeginTransaction();
            var now = _time.GetUtcNow();
            var clock = OrganizerClock.Server(now);
            foreach (var item in LoadItems("SpaceId = $space AND Deleted = 0", ("$space", spaceId)))
            {
                item.Deleted = true;
                item.Clocks["deleted"] = clock;
                item.Seq = NextSeq();
                item.UpdatedAt = now;
                item.UpdatedBy = user.Username;
                SaveItem(item);
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "UPDATE Spaces SET Deleted = 1 WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", spaceId);
                command.ExecuteNonQuery();
            }

            NextSeq();
            PersistSeq();
            transaction.Commit();
            Signal();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- sync ---------------------------------------------------------------------------------

    public async Task<OrganizerPushResult> ApplyChangesAsync(
        OrganizerUser user,
        IReadOnlyList<OrganizerChange> changes,
        IReadOnlyList<OrganizerSpace>? restoreSpaces,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            EnsurePersonalSpace(user);
            var startSeq = _seq;
            using var transaction = _connection.BeginTransaction();

            // Group definitions a device is carrying back to a rebuilt server. Only groups the
            // uploader belongs to, and only ones this server has never heard of - an existing
            // group's membership is changed through the spaces endpoints, never by a stale copy.
            foreach (var space in restoreSpaces ?? [])
            {
                if (space.Kind != OrganizerSpaceKind.Group || !space.Id.StartsWith("group:", StringComparison.Ordinal)) continue;
                if (user.Username is null || !space.Members.Contains(user.Username, StringComparer.OrdinalIgnoreCase)) continue;
                if (LoadSpace(space.Id, includeDeleted: true) is not null) continue;
                InsertSpace(space with { Members = NormalizeMembers(space.Members, null), Name = space.Name.Trim() });
            }

            var visible = VisibleSpaces(user).Select(space => space.Id).ToHashSet(StringComparer.Ordinal);
            var touched = new Dictionary<string, OrganizerItem>(StringComparer.Ordinal);
            var rejected = new List<OrganizerRejection>();

            // Parents before children: a board, its columns and its cards made offline arrive in
            // one batch, and a card can only be checked against a column that exists. Anything
            // whose parent is still missing after everything else has gone in is rejected then.
            var pending = changes.ToList();
            while (pending.Count > 0)
            {
                var deferred = new List<OrganizerChange>();
                foreach (var change in pending)
                {
                    var outcome = Apply(user, change, visible, touched, parentMayArriveLater: true);
                    if (outcome == ApplyOutcome.WaitingForParent) deferred.Add(change);
                    else if (outcome is { Error: { } error }) rejected.Add(new OrganizerRejection(change.Id, error));
                }

                if (deferred.Count == pending.Count)
                {
                    foreach (var change in deferred)
                    {
                        var outcome = Apply(user, change, visible, touched, parentMayArriveLater: false);
                        if (outcome is { Error: { } error }) rejected.Add(new OrganizerRejection(change.Id, error));
                    }
                    break;
                }

                pending = deferred;
            }

            if (_seq != startSeq) PersistSeq();
            transaction.Commit();
            if (_seq != startSeq) Signal();

            return new OrganizerPushResult([.. touched.Values], rejected);
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record ApplyOutcome(string? Error)
    {
        public static readonly ApplyOutcome Ok = new((string?)null);
        public static readonly ApplyOutcome WaitingForParent = new("waiting");
    }

    private ApplyOutcome Apply(
        OrganizerUser user, OrganizerChange change, HashSet<string> visible,
        Dictionary<string, OrganizerItem> touched, bool parentMayArriveLater)
    {
        if (!ItemId.IsMatch(change.Id ?? "")) return new ApplyOutcome("That item id isn't valid.");
        if (!OrganizerKinds.IsKnown(change.Kind ?? "")) return new ApplyOutcome($"\"{change.Kind}\" isn't something the Organizer holds.");
        if (change.Fields.Count > MaxFieldsPerItem) return new ApplyOutcome("Too many fields on one item.");

        var now = _time.GetUtcNow();
        var clocks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _) in change.Fields)
        {
            if (!FieldName.IsMatch(name)) return new ApplyOutcome($"\"{name}\" isn't a valid field name.");
            if (!change.Clocks.TryGetValue(name, out var clock) || !OrganizerClock.IsValid(clock))
                return new ApplyOutcome($"The \"{name}\" field needs a valid clock.");
            clocks[name] = OrganizerClock.Clamp(clock, now);
        }

        var existing = LoadItem(change.Id!);
        if (existing is not null)
        {
            if (existing.Kind != change.Kind) return new ApplyOutcome("An item can't change what kind of thing it is.");
            if (!visible.Contains(existing.SpaceId)) return new ApplyOutcome("You don't have access to that item.");

            // A comment's words belong to whoever wrote it. Anyone who can see the card may delete
            // one, since tidying a board is everyone's job, but only the author may change it.
            if (existing.Kind == OrganizerKinds.Comment && user.Username is not null && existing.CreatedBy is not null
                && !string.Equals(existing.CreatedBy, user.Username, StringComparison.OrdinalIgnoreCase)
                && change.Fields.Any(field => field.Key != "deleted"))
            {
                return new ApplyOutcome("Only the person who wrote a comment can change it.");
            }
        }

        var item = existing ?? new OrganizerItem
        {
            Id = change.Id!,
            Kind = change.Kind!,
            SpaceId = "",
            CreatedAt = now,
            CreatedBy = user.Username,
        };
        var previousSpace = existing?.SpaceId;
        var changedAny = existing is null;
        var parentChanged = false;
        OrganizerItem? conflictCopy = null;

        foreach (var (name, value) in change.Fields)
        {
            var incomingClock = clocks[name];
            item.Clocks.TryGetValue(name, out var currentClock);

            if (existing is not null && name == "body" && item.Kind == OrganizerKinds.Note
                && change.BaseClocks?.GetValueOrDefault("body") is { } baseClock
                && currentClock is not null && baseClock != currentClock
                && !JsonNode.DeepEquals(item.Fields["body"], value))
            {
                // Both sides rewrote the text since the version this device started from. Keep
                // the newer as the note and the other as a copy beside it rather than letting the
                // clock quietly throw one person's writing away.
                var incomingWins = OrganizerClock.IsNewer(incomingClock, currentClock);
                var loserBody = incomingWins ? item.Fields["body"]?.DeepClone() : value?.DeepClone();
                conflictCopy = BuildConflictCopy(item, change, loserBody, incomingClock, user, now);
                if (!incomingWins) continue;
            }
            else if (!OrganizerClock.IsNewer(incomingClock, currentClock))
            {
                continue;
            }

            item.Clocks[name] = incomingClock;
            changedAny = true;
            switch (name)
            {
                case "spaceId":
                    item.SpaceId = value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";
                    break;
                case "parentId":
                    item.ParentId = value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
                    parentChanged = true;
                    break;
                case "deleted":
                    item.Deleted = value?.GetValueKind() == JsonValueKind.True;
                    break;
                default:
                    item.Fields[name] = value?.DeepClone();
                    break;
            }
        }

        // A child always lives where its parent lives: a card is visible exactly when its board
        // is, whatever space the device that made it happened to write.
        OrganizerItem? parent = null;
        if (item.ParentId is { } parentId)
        {
            parent = touched.GetValueOrDefault(parentId) ?? LoadItem(parentId);
            if (parent is null && (existing is null || parentChanged))
            {
                return parentMayArriveLater
                    ? ApplyOutcome.WaitingForParent
                    : new ApplyOutcome("The thing this belongs to doesn't exist.");
            }
        }

        // Checked when the item is new or has just been re-parented. An existing card whose column
        // was deleted elsewhere keeps its (now dangling) parent rather than being rejected for it.
        if ((existing is null || parentChanged) && OrganizerKinds.CheckParent(item.Kind, parent?.Kind) is { } parentError)
            return new ApplyOutcome(parentError);

        if (parent is not null && item.SpaceId != parent.SpaceId)
        {
            item.SpaceId = parent.SpaceId;
            item.Clocks["spaceId"] = clocks.GetValueOrDefault("parentId") ?? OrganizerClock.Server(now);
            changedAny = true;
        }

        if (string.IsNullOrEmpty(item.SpaceId)) return new ApplyOutcome("New items need a space.");
        if (!visible.Contains(item.SpaceId)) return new ApplyOutcome("You don't have access to that space.");

        if (item.Kind == OrganizerKinds.Attachment)
        {
            var hash = item.Fields["hash"]?.GetValueKind() == JsonValueKind.String ? item.Fields["hash"]!.GetValue<string>() : null;
            if (!OrganizerBlobStore.IsValidHash(hash)) return new ApplyOutcome("An attachment needs the SHA-256 of its file.");
        }

        if (Encoding.UTF8.GetByteCount(item.Fields.ToJsonString()) > MaxItemJsonBytes)
            return new ApplyOutcome("That item is too large. Attach big content as a file instead.");

        if (changedAny)
        {
            item.Seq = NextSeq();
            item.UpdatedAt = now;
            item.UpdatedBy = user.Username;
            SaveItem(item);

            if (previousSpace is not null && previousSpace != item.SpaceId)
            {
                RecordRemoval(item.Id, previousSpace, item.Seq);
                CascadeSpace(item, previousSpace, touched);
            }
        }

        touched[item.Id] = item;

        if (conflictCopy is not null)
        {
            conflictCopy.Seq = NextSeq();
            SaveItem(conflictCopy);
            touched[conflictCopy.Id] = conflictCopy;
        }

        return ApplyOutcome.Ok;
    }

    private static OrganizerItem BuildConflictCopy(
        OrganizerItem item, OrganizerChange change, JsonNode? body, string clock, OrganizerUser user, DateTimeOffset now)
    {
        // Deterministic, so a device resending the same batch after a dropped response makes
        // the same copy again rather than a second one.
        var id = "conflict-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{item.Id}|{clock}")))[..24];

        var title = (change.Fields["title"] ?? item.Fields["title"])?.GetValueKind() == JsonValueKind.String
            ? (change.Fields["title"] ?? item.Fields["title"])!.GetValue<string>()
            : "Untitled";

        var fields = new JsonObject
        {
            ["title"] = $"{title} (conflicted copy)",
            ["body"] = body,
        };
        return new OrganizerItem
        {
            Id = id,
            Kind = OrganizerKinds.Note,
            SpaceId = item.SpaceId,
            Fields = fields,
            Clocks = new(StringComparer.Ordinal) { ["title"] = clock, ["body"] = clock, ["spaceId"] = clock },
            CreatedAt = now,
            CreatedBy = user.Username,
            UpdatedAt = now,
            UpdatedBy = user.Username,
        };
    }

    // Moving a board moves its columns, cards and their attachments with it; the same for a
    // note's attachments. Descendants take the same removal record so a person who could see the
    // old space drops the lot.
    private void CascadeSpace(OrganizerItem root, string previousSpace, Dictionary<string, OrganizerItem> touched)
    {
        var queue = new Queue<string>([root.Id]);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            foreach (var child in LoadItems("ParentId = $parent", ("$parent", parentId)))
            {
                queue.Enqueue(child.Id);
                if (child.SpaceId == root.SpaceId) continue;

                var from = child.SpaceId;
                child.SpaceId = root.SpaceId;
                child.Clocks["spaceId"] = root.Clocks.GetValueOrDefault("spaceId") ?? OrganizerClock.Server(_time.GetUtcNow());
                child.Seq = NextSeq();
                child.UpdatedAt = root.UpdatedAt;
                child.UpdatedBy = root.UpdatedBy;
                SaveItem(child);
                RecordRemoval(child.Id, from == "" ? previousSpace : from, child.Seq);
                touched[child.Id] = child;
            }
        }
    }

    public async Task<OrganizerPullResult> PullAsync(
        OrganizerUser user, long cursor, IReadOnlySet<string> knownSpaceIds, int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            EnsurePersonalSpace(user);
            var visible = VisibleSpaces(user).Select(space => space.Id).ToList();
            if (visible.Count == 0) return new OrganizerPullResult(_seq, false, [], []);

            // A cursor from the future means the device last spoke to a different database (a
            // restored or rebuilt one) - start it over rather than skipping everything.
            if (cursor > _seq) cursor = 0;

            var (spaceFilter, spaceParameters) = InClause("s", visible);
            var page = LoadItems(
                $"Seq > $cursor AND SpaceId IN ({spaceFilter}) ORDER BY Seq LIMIT $limit",
                [("$cursor", cursor), ("$limit", limit + 1), .. spaceParameters]);

            var hasMore = page.Count > limit;
            if (hasMore) page.RemoveAt(page.Count - 1);
            var newCursor = hasMore ? page[^1].Seq : _seq;

            // A space this device hasn't seen before - someone just added this person to a group
            // - needs everything already in it, not only what changed since the cursor.
            var items = page;
            var newlyVisible = cursor == 0 ? [] : visible.Where(id => !knownSpaceIds.Contains(id)).ToList();
            if (newlyVisible.Count > 0)
            {
                var have = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                var (newFilter, newParameters) = InClause("n", newlyVisible);
                items.AddRange(LoadItems($"SpaceId IN ({newFilter}) AND Seq <= $cursor", [("$cursor", cursor), .. newParameters])
                    .Where(item => !have.Contains(item.Id)));
            }

            var removed = new List<string>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT DISTINCT r.ItemId FROM Removals r JOIN Items i ON i.Id = r.ItemId
                    WHERE r.Seq > $cursor AND r.Seq <= $newCursor
                      AND r.FromSpaceId IN ({spaceFilter}) AND i.SpaceId NOT IN ({spaceFilter});
                    """;
                command.Parameters.AddWithValue("$cursor", cursor);
                command.Parameters.AddWithValue("$newCursor", newCursor);
                foreach (var (name, value) in spaceParameters) command.Parameters.AddWithValue(name, value);
                using var reader = command.ExecuteReader();
                while (reader.Read()) removed.Add(reader.GetString(0));
            }

            return new OrganizerPullResult(newCursor, hasMore, items, removed);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<OrganizerItem>> GetItemsAsync(
        OrganizerUser user, string? kind, string? spaceId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var visible = VisibleSpaces(user).Select(space => space.Id).ToList();
            if (spaceId is not null) visible = visible.Where(id => id == spaceId).ToList();
            if (visible.Count == 0) return [];

            var (filter, parameters) = InClause("s", visible);
            var where = $"Deleted = 0 AND SpaceId IN ({filter})";
            if (kind is not null)
            {
                where += " AND Kind = $kind";
                parameters = [.. parameters, ("$kind", kind)];
            }
            return LoadItems(where + " ORDER BY Seq", parameters);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OrganizerPullResult> PullEverythingAsync(long cursor, int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (cursor > _seq) cursor = 0;
            var page = LoadItems("Seq > $cursor ORDER BY Seq LIMIT $limit", ("$cursor", cursor), ("$limit", limit + 1));
            var hasMore = page.Count > limit;
            if (hasMore) page.RemoveAt(page.Count - 1);
            return new OrganizerPullResult(hasMore ? page[^1].Seq : _seq, hasMore, page, []);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountVisibleAsync(OrganizerUser user, CancellationToken cancellationToken) =>
        (await GetItemsAsync(user, null, null, cancellationToken)).Count;

    public async Task<long> WaitForChangeAsync(long cursor, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task changed;
        lock (_signalLock)
        {
            if (CurrentCursor > cursor) return CurrentCursor;
            changed = _changed.Task;
        }

        try
        {
            await changed.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
        return CurrentCursor;
    }

    // ---- attachments --------------------------------------------------------------------------

    public async Task<OrganizerItem?> FindVisibleAttachmentAsync(OrganizerUser user, string hash, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var visible = VisibleSpaces(user).Select(space => space.Id).ToList();
            if (visible.Count == 0) return null;
            var (filter, parameters) = InClause("s", visible);
            return LoadItems($"BlobHash = $hash AND Deleted = 0 AND SpaceId IN ({filter}) LIMIT 1", [("$hash", hash), .. parameters])
                .FirstOrDefault();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetVisibleBlobHashesAsync(OrganizerUser user, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var visible = VisibleSpaces(user).Select(space => space.Id).ToList();
            if (visible.Count == 0) return [];
            var (filter, parameters) = InClause("s", visible);
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT BlobHash FROM Items WHERE BlobHash IS NOT NULL AND Deleted = 0 AND SpaceId IN ({filter});";
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            var hashes = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) hashes.Add(reader.GetString(0));
            return hashes;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlySet<string>> GetAllLiveBlobHashesAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT BlobHash FROM Items WHERE BlobHash IS NOT NULL AND Deleted = 0;";
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read()) hashes.Add(reader.GetString(0));
            return hashes;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- calendar feed tokens -----------------------------------------------------------------

    public async Task<string> GetOrCreateFeedTokenAsync(OrganizerUser user, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Token FROM FeedTokens WHERE UserKey = $user;";
                command.Parameters.AddWithValue("$user", UserKey(user));
                if (command.ExecuteScalar() is string token) return token;
            }
            return InsertFeedToken(user);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> ResetFeedTokenAsync(OrganizerUser user, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM FeedTokens WHERE UserKey = $user;";
                command.Parameters.AddWithValue("$user", UserKey(user));
                command.ExecuteNonQuery();
            }
            return InsertFeedToken(user);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OrganizerUser?> ResolveFeedTokenAsync(string token, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT UserKey FROM FeedTokens WHERE Token = $token;";
            command.Parameters.AddWithValue("$token", token);
            return command.ExecuteScalar() is string key
                ? new OrganizerUser(key.Length == 0 ? null : key, null)
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string InsertFeedToken(OrganizerUser user)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO FeedTokens (Token, UserKey, CreatedAt) VALUES ($token, $user, $now);";
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$user", UserKey(user));
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
        return token;
    }

    private static string UserKey(OrganizerUser user) => user.Username ?? "";

    // ---- preferences ---------------------------------------------------------------------------

    // In Meta rather than a table of their own: a few bytes per person, and a rebuilt server that
    // lost them gets them back from the first device that still has them (see the clients).
    public async Task<string?> GetPreferencesAsync(OrganizerUser user, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return ReadMeta("prefs:" + UserKey(user));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetPreferencesAsync(OrganizerUser user, string json, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            WriteMeta("prefs:" + UserKey(user), json);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- backup -------------------------------------------------------------------------------

    public async Task<OrganizerBackup> ExportAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var spaces = new List<OrganizerSpace>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Id, Kind, Name, Members, Color, CreatedBy FROM Spaces WHERE Deleted = 0;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) spaces.Add(ReadSpace(reader));
            }
            var feedTokens = new List<OrganizerFeedToken>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Token, UserKey, CreatedAt FROM FeedTokens;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    feedTokens.Add(new OrganizerFeedToken(
                        reader.GetString(0), reader.GetString(1),
                        DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture)));
            }

            var preferences = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Key, Value FROM Meta WHERE Key LIKE 'prefs:%';";
                using var reader = command.ExecuteReader();
                while (reader.Read()) preferences[reader.GetString(0)["prefs:".Length..]] = reader.GetString(1);
            }

            // serverId and seq stay behind on purpose: a different serverId is how a device
            // notices it is talking to a rebuilt server and re-sends what it has.
            return new OrganizerBackup(spaces, LoadItems("1 = 1 ORDER BY Seq"), feedTokens, preferences);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Overlays a backup the same way a device's re-upload does: per field, newest clock wins, so
    // restoring an old file over newer data can't roll anything back, and importing twice is a
    // no-op. Unlike a device push there is no visibility check - an admin restoring the server
    // is putting back everybody's things, not their own.
    public async Task<int> ImportAsync(OrganizerBackup backup, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var space in backup.Spaces)
            {
                if (LoadSpace(space.Id, includeDeleted: true) is null) InsertSpace(space);
            }

            var restored = 0;
            var now = _time.GetUtcNow();
            foreach (var incoming in backup.Items)
            {
                var item = LoadItem(incoming.Id);
                if (item is null)
                {
                    incoming.Seq = NextSeq();
                    SaveItem(incoming);
                    restored++;
                    continue;
                }

                var changed = false;
                foreach (var (name, clock) in incoming.Clocks)
                {
                    if (!OrganizerClock.IsNewer(clock, item.Clocks.GetValueOrDefault(name))) continue;
                    item.Clocks[name] = clock;
                    changed = true;
                    switch (name)
                    {
                        case "spaceId": item.SpaceId = incoming.SpaceId; break;
                        case "parentId": item.ParentId = incoming.ParentId; break;
                        case "deleted": item.Deleted = incoming.Deleted; break;
                        default: item.Fields[name] = incoming.Fields[name]?.DeepClone(); break;
                    }
                }

                if (!changed) continue;
                item.Seq = NextSeq();
                item.UpdatedAt = now;
                SaveItem(item);
                restored++;
            }

            // Both only where this install has nothing of its own yet. A person who already has a
            // feed token here has handed that URL out, and their preferences here are at least
            // as new as the backup's - a device re-sends them on every change.
            foreach (var feedToken in backup.FeedTokens ?? [])
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO FeedTokens (Token, UserKey, CreatedAt) VALUES ($token, $user, $created);";
                command.Parameters.AddWithValue("$token", feedToken.Token);
                command.Parameters.AddWithValue("$user", feedToken.UserKey);
                command.Parameters.AddWithValue("$created", feedToken.CreatedAt.ToString("O"));
                command.ExecuteNonQuery();
            }

            foreach (var (user, json) in backup.Preferences ?? new Dictionary<string, string>())
            {
                if (ReadMeta("prefs:" + user) is null) WriteMeta("prefs:" + user, json);
            }

            PersistSeq();
            transaction.Commit();
            Signal();
            return restored;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- internals ----------------------------------------------------------------------------

    private static bool CanSee(OrganizerUser user, OrganizerSpace space) => space.Kind switch
    {
        OrganizerSpaceKind.Family => true,
        OrganizerSpaceKind.Personal => user.Username is not null && space.Id == OrganizerSpaceIds.Personal(user.Username),
        OrganizerSpaceKind.Group => user.Username is not null && space.Members.Contains(user.Username, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };

    private List<OrganizerSpace> VisibleSpaces(OrganizerUser user)
    {
        var spaces = new List<OrganizerSpace>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Id, Kind, Name, Members, Color, CreatedBy FROM Spaces WHERE Deleted = 0 ORDER BY CreatedAt;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var space = ReadSpace(reader);
            if (CanSee(user, space)) spaces.Add(space);
        }

        // Family, then the person's own, then groups - the order every client lists them in.
        return [.. spaces.OrderBy(space => space.Kind switch
        {
            OrganizerSpaceKind.Personal => 0,
            OrganizerSpaceKind.Family => 1,
            _ => 2,
        })];
    }

    private void EnsurePersonalSpace(OrganizerUser user)
    {
        if (user.Username is null) return;
        var id = OrganizerSpaceIds.Personal(user.Username);
        if (LoadSpace(id, includeDeleted: true) is not null) return;
        InsertSpace(new OrganizerSpace(id, OrganizerSpaceKind.Personal, "Personal", [user.Username.ToLowerInvariant()], null, user.Username));
    }

    private OrganizerSpace? LoadSpace(string id, bool includeDeleted = false)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Id, Kind, Name, Members, Color, CreatedBy FROM Spaces WHERE Id = $id"
            + (includeDeleted ? ";" : " AND Deleted = 0;");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSpace(reader) : null;
    }

    private void InsertSpace(OrganizerSpace space)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO Spaces (Id, Kind, Name, Members, Color, CreatedBy, CreatedAt)
            VALUES ($id, $kind, $name, $members, $color, $createdBy, $now);
            """;
        command.Parameters.AddWithValue("$id", space.Id);
        command.Parameters.AddWithValue("$kind", space.Kind.ToString());
        command.Parameters.AddWithValue("$name", space.Name);
        command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(space.Members));
        command.Parameters.AddWithValue("$color", (object?)space.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdBy", (object?)space.CreatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
    }

    private static OrganizerSpace ReadSpace(SqliteDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<OrganizerSpaceKind>(reader.GetString(1)),
        reader.GetString(2),
        JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    private static List<string> NormalizeMembers(IEnumerable<string> members, string? include)
    {
        var set = members.Where(member => !string.IsNullOrWhiteSpace(member))
            .Select(member => member.Trim().ToLowerInvariant())
            .ToList();
        if (include is not null) set.Add(include.ToLowerInvariant());
        return [.. set.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private OrganizerItem? LoadItem(string id) => LoadItems("Id = $id", ("$id", id)).FirstOrDefault();

    private List<OrganizerItem> LoadItems(string where, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Kind, SpaceId, ParentId, Deleted, Fields, Clocks, Seq, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy
            FROM Items WHERE {where};
            """;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        var items = new List<OrganizerItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new OrganizerItem
            {
                Id = reader.GetString(0),
                Kind = reader.GetString(1),
                SpaceId = reader.GetString(2),
                ParentId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Deleted = reader.GetInt64(4) != 0,
                Fields = JsonNode.Parse(reader.GetString(5)) as JsonObject ?? [],
                Clocks = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) is { } clocks
                    ? new Dictionary<string, string>(clocks, StringComparer.Ordinal)
                    : new(StringComparer.Ordinal),
                Seq = reader.GetInt64(7),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                CreatedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture),
                UpdatedBy = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }
        return items;
    }

    private void SaveItem(OrganizerItem item)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Items (Id, Kind, SpaceId, ParentId, Deleted, BlobHash, Fields, Clocks, Seq, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
            VALUES ($id, $kind, $space, $parent, $deleted, $blob, $fields, $clocks, $seq, $createdAt, $createdBy, $updatedAt, $updatedBy)
            ON CONFLICT(Id) DO UPDATE SET
                SpaceId = excluded.SpaceId, ParentId = excluded.ParentId, Deleted = excluded.Deleted,
                BlobHash = excluded.BlobHash, Fields = excluded.Fields, Clocks = excluded.Clocks,
                Seq = excluded.Seq, UpdatedAt = excluded.UpdatedAt, UpdatedBy = excluded.UpdatedBy;
            """;
        var hash = item.Kind == OrganizerKinds.Attachment && item.Fields["hash"]?.GetValueKind() == JsonValueKind.String
            ? item.Fields["hash"]!.GetValue<string>().ToLowerInvariant()
            : null;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$kind", item.Kind);
        command.Parameters.AddWithValue("$space", item.SpaceId);
        command.Parameters.AddWithValue("$parent", (object?)item.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", item.Deleted ? 1 : 0);
        command.Parameters.AddWithValue("$blob", (object?)hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$fields", item.Fields.ToJsonString());
        command.Parameters.AddWithValue("$clocks", JsonSerializer.Serialize(item.Clocks));
        command.Parameters.AddWithValue("$seq", item.Seq);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$createdBy", (object?)item.CreatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedBy", (object?)item.UpdatedBy ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void RecordRemoval(string itemId, string fromSpaceId, long seq)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO Removals (ItemId, FromSpaceId, Seq) VALUES ($item, $from, $seq);";
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$from", fromSpaceId);
        command.Parameters.AddWithValue("$seq", seq);
        command.ExecuteNonQuery();
    }

    private static (string Sql, (string Name, object Value)[] Parameters) InClause(string prefix, IReadOnlyList<string> values)
    {
        var parameters = values.Select((value, index) => ($"${prefix}{index}", (object)value)).ToArray();
        return (string.Join(", ", parameters.Select(parameter => parameter.Item1)), parameters);
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);

    private void PersistSeq() => WriteMeta("seq", _seq.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void BumpAndSignal()
    {
        NextSeq();
        PersistSeq();
        Signal();
    }

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (_signalLock)
        {
            previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previous.TrySetResult();
    }

    private string? ReadMeta(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Meta WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private string WriteMeta(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO Meta (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
        return value;
    }

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
