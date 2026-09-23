using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SentinelCore.Client.Organizer;

public sealed record OrganizerResponse(int Status, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// How the engine reaches the server. The app hands it one backed by its own SentinelClient, so the
/// Organizer shares the signed-in cookie and the chosen local/remote address; the tests hand it one
/// pointed at a server they started. Answers every HTTP status; throws
/// <see cref="OrganizerOfflineException"/> only when the server can't be reached at all.
/// </summary>
public interface IOrganizerTransport
{
    Task<OrganizerResponse> SendAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        byte[]? body = null,
        string? contentType = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}

public class OrganizerException(string message) : Exception(message);

public sealed class OrganizerOfflineException() : OrganizerException("Can't reach the server.");

public sealed class OrganizerSignInRequiredException() : OrganizerException("Sign in to use the Organizer.");

public sealed class OrganizerTurnedOffException() : OrganizerException("The Organizer is turned off in the server's settings.");

public sealed class OrganizerServerException(int status, string? message)
    : OrganizerException(message ?? $"The server answered with an error (HTTP {status}).")
{
    public int Status { get; } = status;
}

public sealed record OrganizerSyncState(
    bool Online = false,
    bool Syncing = false,
    DateTimeOffset? LastSyncedAt = null,
    string? LastError = null,
    int WaitingChanges = 0,
    bool NeedsSignIn = false,
    bool TurnedOff = false);

/// <summary>The server answering isn't the one this PC last synced with. Nothing is sent until the
/// person decides.</summary>
public sealed record OrganizerResetPrompt(string ServerId, int LocalCount, int? ServerCount);

public sealed record OrganizerStorageSummary(
    int ItemCount,
    int? ServerItemCount,
    int AttachmentCount,
    int AttachmentsWithOriginal,
    long BytesOnDevice,
    int WaitingUploads);

/// <summary>
/// The Organizer on this PC: its copy, every edit made to it, and keeping it in step with the
/// server. The rules are core/docs/ORGANIZER_SYNC.md; OrganizerKit's OrganizerEngine (Apple),
/// Android's OrganizerEngine and the dashboard's organizer-sync.js are the same algorithm, and
/// should read alike.
/// </summary>
/// <remarks>
/// Every edit lands in the local copy at once, stamped with a fresh clock per field, and joins a
/// queue that <see cref="SyncAsync"/> pushes. The queue is on disk before an edit returns, so
/// closing the app with no network loses nothing. State is guarded by one lock. <see cref="Changed"/>
/// fires - on whatever thread made the change - after every edit, every sync and every change of
/// status, so a view only has to marshal to its dispatcher and re-read.
/// </remarks>
public sealed class OrganizerEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly IOrganizerLocalStore _store;
    private readonly HybridClock _clock;
    private readonly Func<TimeZoneInfo> _zone;
    private readonly Dictionary<string, OrganizerItem> _items = new();
    private readonly Dictionary<string, OrganizerItem> _archive = new();
    private readonly Dictionary<string, OrganizerChange> _pending = new();
    private readonly Dictionary<string, OrganizerChange> _inflight = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private string? _serverId;
    private long _cursor;
    private HashSet<string> _missingBlobs = new();
    private (long Revision, Dictionary<string, List<OrganizerItem>> Index)? _childIndex;

    private volatile IOrganizerTransport? _transport;
    private CancellationTokenSource? _connection;
    private CancellationTokenSource? _debounce;
    // Blob work runs one pass at a time; a request while one is running makes it go round again,
    // since the running pass took its list of attachments before the new one arrived.
    private bool _blobWorkRunning;
    private bool _blobWorkAgain;
    private long _requestedPass;
    private long _completedPass;

    private long _revision;
    private OrganizerStatus? _status;
    private IReadOnlyList<OrganizerSpace> _spaces = Array.Empty<OrganizerSpace>();
    private OrganizerSyncState _syncState = new();
    private OrganizerResetPrompt? _resetPrompt;
    private OfflinePolicy _policy;
    private IReadOnlySet<string> _keptOffline = new HashSet<string>();
    private IReadOnlyList<PlannedAlert> _plannedAlerts = Array.Empty<PlannedAlert>();

    public OrganizerEngine(IOrganizerLocalStore store, string deviceId, OfflinePolicy policy, Func<TimeZoneInfo>? zone = null)
    {
        _store = store;
        _clock = new HybridClock(deviceId);
        _policy = policy;
        _zone = zone ?? (() => TimeZoneInfo.Local);

        lock (_gate)
        {
            // "Off" keeps nothing between launches - only what has edits that never made it up.
            if (policy.Mode == OfflineMode.Off) store.ClearItems(store.LoadPending().Keys.ToHashSet());
            foreach (var item in store.LoadItems())
            {
                _items[item.Id] = item;
                foreach (var clock in item.Clocks.Values) _clock.Observe(clock);
            }
            foreach (var (id, change) in store.LoadPending())
            {
                _pending[id] = change;
                foreach (var clock in change.Clocks.Values) _clock.Observe(clock);
            }
            _serverId = store.Meta("serverId");
            _cursor = long.TryParse(store.Meta("cursor"), out var cursor) ? cursor : 0;
            if (store.Meta("spaces") is { } spaces)
            {
                try
                {
                    _spaces = (JsonNode.Parse(spaces) as JsonArray ?? new JsonArray())
                        .OfType<JsonObject>().Select(OrganizerJson.Space).ToList();
                }
                catch (Exception) { /* re-read on the next sync */ }
            }
            if (store.Meta("keptOffline") is { } kept)
            {
                try
                {
                    _keptOffline = (JsonNode.Parse(kept) as JsonArray ?? new JsonArray())
                        .Select(node => node?.GetValue<string>()).OfType<string>().ToHashSet();
                }
                catch (Exception) { }
            }
            _syncState = _syncState with { WaitingChanges = _pending.Count };
        }
        ReplanAlerts();
    }

    /// <summary>Anything a view shows may have changed. Raised on the thread that changed it.</summary>
    public event Action? Changed;

    /// <summary>The alerts this PC should raise changed. Raised on the thread that changed it.</summary>
    public event Action<IReadOnlyList<PlannedAlert>>? AlertsChanged;

    public long Revision => Interlocked.Read(ref _revision);
    public OrganizerStatus? Status { get { lock (_gate) return _status; } }
    public IReadOnlyList<OrganizerSpace> Spaces { get { lock (_gate) return _spaces; } }
    public OrganizerSyncState SyncState { get { lock (_gate) return _syncState; } }
    public OrganizerResetPrompt? ResetPrompt { get { lock (_gate) return _resetPrompt; } }
    public OfflinePolicy Policy { get { lock (_gate) return _policy; } }
    public IReadOnlySet<string> KeptOffline { get { lock (_gate) return _keptOffline; } }
    public IReadOnlyList<PlannedAlert> PlannedAlerts { get { lock (_gate) return _plannedAlerts; } }

    // ---- reading ---------------------------------------------------------------------------

    public List<OrganizerItem> Items(string kind)
    {
        lock (_gate)
        {
            return _items.Values.Where(item => item.Kind == kind && !item.Deleted)
                .Concat(_archive.Values.Where(item => item.Kind == kind && !item.Deleted && !_items.ContainsKey(item.Id)))
                .ToList();
        }
    }

    public OrganizerItem? Item(string id)
    {
        lock (_gate)
        {
            var item = _items.TryGetValue(id, out var local) ? local : _archive.GetValueOrDefault(id);
            return item is { Deleted: false } ? item : null;
        }
    }

    public List<OrganizerItem> Children(string id, string? kind = null)
    {
        lock (_gate)
        {
            var revision = Revision;
            if (_childIndex is not { } cached || cached.Revision != revision)
            {
                var index = new Dictionary<string, List<OrganizerItem>>();
                foreach (var item in _items.Values.Concat(_archive.Values.Where(item => !_items.ContainsKey(item.Id))))
                {
                    if (item.Deleted || item.ParentId is not { } parentId) continue;
                    if (!index.TryGetValue(parentId, out var list)) index[parentId] = list = new List<OrganizerItem>();
                    list.Add(item);
                }
                cached = (revision, index);
                _childIndex = cached;
            }
            var all = cached.Index.GetValueOrDefault(id) ?? new List<OrganizerItem>();
            return kind is null ? all.ToList() : all.Where(item => item.Kind == kind).ToList();
        }
    }

    public IReadOnlyList<OrganizerSpace> VisibleSpaces
    {
        get
        {
            var spaces = Spaces;
            return spaces.Count == 0 ? new[] { OrganizerSpace.Family } : spaces;
        }
    }

    public OrganizerSpace? Space(string id) => VisibleSpaces.FirstOrDefault(space => space.Id == id);

    /// <summary>Where something new goes when nobody says otherwise: this person's own space if
    /// there is one, otherwise Family.</summary>
    public string DefaultSpaceId =>
        VisibleSpaces.FirstOrDefault(space => space.Kind == OrganizerSpaceKind.Personal)?.Id ?? OrganizerSpace.Family.Id;

    // ---- editing ---------------------------------------------------------------------------

    public OrganizerItem Create(string kind, IReadOnlyDictionary<string, object?> fields, string? spaceId = null, string? parentId = null)
    {
        OrganizerItem created;
        lock (_gate)
        {
            var parent = parentId is null ? null : _items.GetValueOrDefault(parentId) ?? _archive.GetValueOrDefault(parentId);
            var now = DateTimeOffset.UtcNow;
            var item = new OrganizerItem(
                Guid.NewGuid().ToString(), kind,
                parent?.SpaceId ?? spaceId ?? DefaultSpaceIdLocked(),
                parent?.Id,
                CreatedAt: now, CreatedBy: _status?.Username, UpdatedAt: now, UpdatedBy: _status?.Username);
            var changes = new List<KeyValuePair<string, object?>>
            {
                parent is not null ? new("parentId", parent.Id) : new("spaceId", item.SpaceId),
            };
            changes.AddRange(fields);
            created = EditLocked(item, changes);
        }
        AfterEdit();
        return created;
    }

    private string DefaultSpaceIdLocked() =>
        _spaces.FirstOrDefault(space => space.Kind == OrganizerSpaceKind.Personal)?.Id ?? OrganizerSpace.Family.Id;

    /// <summary>A null value clears the field.</summary>
    public void Update(string id, IReadOnlyDictionary<string, object?> changes)
    {
        lock (_gate)
        {
            var item = _items.GetValueOrDefault(id) ?? _archive.GetValueOrDefault(id);
            if (item is null) return;
            _archive.Remove(id);
            var moved = changes.TryGetValue("spaceId", out var space) && space is string newSpace && newSpace != item.SpaceId;
            var edited = EditLocked(item, changes);
            if (moved) CascadeSpaceLocked(edited);
        }
        AfterEdit();
    }

    public void Update(string id, string field, object? value) =>
        Update(id, new Dictionary<string, object?> { [field] = value });

    public void Delete(string id) => Update(id, "deleted", true);

    /// <summary>An item and everything under it - a board's columns and cards, a note's attachments.</summary>
    public void DeleteTree(string id)
    {
        var queue = new Stack<string>();
        queue.Push(id);
        var seen = new HashSet<string>();
        while (queue.Count > 0)
        {
            var next = queue.Pop();
            if (!seen.Add(next)) continue;
            foreach (var child in Children(next)) queue.Push(child.Id);
            Delete(next);
        }
    }

    private OrganizerItem EditLocked(OrganizerItem original, IEnumerable<KeyValuePair<string, object?>> changes)
    {
        var item = original;
        var change = _pending.GetValueOrDefault(item.Id) ?? new OrganizerChange(item.Id, item.Kind);
        foreach (var (name, value) in changes)
        {
            // The note body's starting point, so the server can tell two people rewrote the same
            // note rather than letting the later one silently win.
            if (name == "body" && item.Kind == OrganizerKinds.Note
                && change.BaseClocks?.GetValueOrDefault("body") is null
                && _inflight.GetValueOrDefault(item.Id)?.BaseClocks?.GetValueOrDefault("body") is null
                && item.Clocks.TryGetValue("body", out var baseClock))
            {
                change = change with { BaseClocks = new Dictionary<string, string> { ["body"] = baseClock } };
            }
            var stamp = _clock.Next();
            item = ApplyField(item, name, value, stamp);
            change = change with
            {
                Fields = OrganizerChange.Combine(change.Fields, new Dictionary<string, object?> { [name] = value }),
                Clocks = OrganizerChange.Combine(change.Clocks, new Dictionary<string, string> { [name] = stamp }),
            };
        }
        item = item with { UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = _status?.Username ?? item.UpdatedBy };
        _items[item.Id] = item;
        _pending[item.Id] = change;
        _store.SaveItems(new[] { item });
        PersistPendingLocked(item.Id);
        _syncState = _syncState with { WaitingChanges = _pending.Count + _inflight.Count };
        return item;
    }

    private void AfterEdit()
    {
        RaiseChanged();
        ReplanAlerts();
        ScheduleSync(TimeSpan.FromMilliseconds(800));
    }

    private static OrganizerItem ApplyField(OrganizerItem item, string name, object? value, string stamp)
    {
        var clocks = OrganizerChange.Combine(item.Clocks, new Dictionary<string, string> { [name] = stamp });
        switch (name)
        {
            case "spaceId":
                return value is string space ? item with { SpaceId = space, Clocks = clocks } : item with { Clocks = clocks };
            case "parentId":
                return item with { ParentId = value as string, Clocks = clocks };
            case "deleted":
                return item with { Deleted = value is true, Clocks = clocks };
            default:
                var fields = new Dictionary<string, object?>(item.Fields);
                if (value is null) fields.Remove(name);
                else fields[name] = value;
                return item with { Fields = fields, Clocks = clocks };
        }
    }

    private void CascadeSpaceLocked(OrganizerItem root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root.Id);
        var touched = new List<OrganizerItem>();
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            foreach (var child in _items.Values.Where(item => item.ParentId == parentId).ToList())
            {
                queue.Enqueue(child.Id);
                if (child.SpaceId == root.SpaceId) continue;
                var moved = child with { SpaceId = root.SpaceId };
                _items[child.Id] = moved;
                touched.Add(moved);
            }
        }
        _store.SaveItems(touched);
    }

    private void PersistPendingLocked(string id)
    {
        var older = _inflight.GetValueOrDefault(id);
        var newer = _pending.GetValueOrDefault(id);
        _store.SavePending(id, older is not null && newer is not null ? older.Merging(newer) : older ?? newer);
    }

    private void RaiseChanged()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    private void UpdateSyncState(Func<OrganizerSyncState, OrganizerSyncState> update)
    {
        lock (_gate) _syncState = update(_syncState);
        Changed?.Invoke();
    }

    // ---- attachments -----------------------------------------------------------------------

    /// <summary>
    /// Keeps the file on this PC straight away and creates the attachment; the bytes go up whenever
    /// the server is reachable. Works with no network at all.
    /// </summary>
    public OrganizerItem Attach(byte[] bytes, string name, string contentType, string parentId, int? width = null, int? height = null)
    {
        if (Status?.MaxAttachmentBytes is { } limit && bytes.LongLength > limit)
        {
            throw new OrganizerServerException(413, $"{name} is larger than the {limit / 1_048_576} MB attachment limit.");
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(_store.OriginalFile(hash), bytes);
        _store.SaveBlob(new BlobRecord(hash, NeedsUpload: true, HasOriginal: true, bytes.LongLength, NowMillis()));
        var fields = new Dictionary<string, object?>
        {
            ["hash"] = hash,
            ["name"] = name,
            ["contentType"] = contentType,
            ["size"] = (double)bytes.LongLength,
            ["position"] = (double)Children(parentId, OrganizerKinds.Attachment).Count,
        };
        if (width is { } w) fields["width"] = (double)w;
        if (height is { } h) fields["height"] = (double)h;
        var item = Create(OrganizerKinds.Attachment, fields, parentId: parentId);
        StartBlobWork();
        return item;
    }

    public bool HasOriginal(string hash) => File.Exists(_store.OriginalFile(hash));

    public string OriginalFile(string hash) => _store.OriginalFile(hash);

    /// <summary>Image bytes to show. A thumbnail request is happy with the original if that's what's here.</summary>
    public async Task<byte[]?> ImageBytesAsync(string hash, bool thumbnail, CancellationToken ct = default)
    {
        var original = _store.OriginalFile(hash);
        if (File.Exists(original))
        {
            Touch(hash);
            return await File.ReadAllBytesAsync(original, ct).ConfigureAwait(false);
        }
        var small = _store.ThumbnailFile(hash);
        if (thumbnail && File.Exists(small)) return await File.ReadAllBytesAsync(small, ct).ConfigureAwait(false);
        try
        {
            return await DownloadAsync(hash, thumbnail, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return !thumbnail && File.Exists(small) ? await File.ReadAllBytesAsync(small, ct).ConfigureAwait(false) : null;
        }
    }

    /// <summary>Makes sure the original is on this PC, for opening or saving.</summary>
    public async Task<string> EnsureOriginalAsync(string hash, CancellationToken ct = default)
    {
        if (!HasOriginal(hash)) await DownloadAsync(hash, thumbnail: false, ct).ConfigureAwait(false);
        return _store.OriginalFile(hash);
    }

    private async Task<byte[]> DownloadAsync(string hash, bool thumbnail, CancellationToken ct)
    {
        var response = await SendAsync("GET", $"/api/organizer/blobs/{hash}" + (thumbnail ? "/thumbnail" : ""),
            timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
        if (thumbnail)
        {
            await File.WriteAllBytesAsync(_store.ThumbnailFile(hash), response.Body, ct).ConfigureAwait(false);
            var record = _store.Blob(hash) ?? new BlobRecord(hash, false, false, 0, 0);
            _store.SaveBlob(record with { LastUsedMillis = NowMillis() });
        }
        else
        {
            await File.WriteAllBytesAsync(_store.OriginalFile(hash), response.Body, ct).ConfigureAwait(false);
            var record = _store.Blob(hash) ?? new BlobRecord(hash, false, true, 0, 0);
            _store.SaveBlob(record with { HasOriginal = true, Size = response.Body.LongLength, LastUsedMillis = NowMillis() });
            EnforceStorageCap();
        }
        return response.Body;
    }

    private void Touch(string hash)
    {
        if (_store.Blob(hash) is { } record) _store.SaveBlob(record with { LastUsedMillis = NowMillis() });
    }

    private static long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void StartBlobWork()
    {
        if (_transport is null) return;
        CancellationToken token;
        lock (_gate)
        {
            if (_blobWorkRunning)
            {
                _blobWorkAgain = true;
                return;
            }
            _blobWorkRunning = true;
            token = _connection?.Token ?? _lifetime.Token;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await RunBlobWorkAsync(token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_blobWorkAgain || _lifetime.IsCancellationRequested)
                    {
                        _blobWorkRunning = false;
                        _blobWorkAgain = false;
                        return;
                    }
                    _blobWorkAgain = false;
                    // A reconnect while this ran cancelled the old connection's token.
                    token = _connection?.Token ?? _lifetime.Token;
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>Uploads first (files attached here, and anything the server says it lost that this
    /// PC still has), then downloads by policy, then tidying.</summary>
    private async Task RunBlobWorkAsync(CancellationToken ct)
    {
        try
        {
            List<OrganizerItem> attachments;
            HashSet<string> missing;
            lock (_gate)
            {
                attachments = _items.Values.Where(item => item.Kind == OrganizerKinds.Attachment && !item.Deleted).ToList();
                missing = new HashSet<string>(_missingBlobs);
            }
            var referenced = attachments.Select(item => item.String("hash")).OfType<string>().ToHashSet();

            foreach (var record in _store.AllBlobs().Where(record => record.NeedsUpload || (missing.Contains(record.Hash) && record.HasOriginal)))
            {
                var file = _store.OriginalFile(record.Hash);
                if (!File.Exists(file)) continue;
                if (_transport is not { } transport) return;
                OrganizerResponse response;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                    response = await transport.SendAsync("PUT", $"/api/organizer/blobs/{record.Hash}", body: bytes,
                        contentType: "application/octet-stream", timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }
                if (response.Status is >= 200 and < 300)
                {
                    _store.SaveBlob(record with { NeedsUpload = false });
                    lock (_gate) _missingBlobs.Remove(record.Hash);
                }
                else if (response.Status is 400 or 413)
                {
                    _store.SaveBlob(record with { NeedsUpload = false });
                    UpdateSyncState(state => state with
                    {
                        LastError = OrganizerJson.ErrorMessage(response.Body) ?? "An attachment was too large to upload.",
                    });
                }
                else
                {
                    return;
                }
            }

            var policy = Policy;
            if (policy.Attachments != OfflineAttachments.None)
            {
                foreach (var attachment in attachments)
                {
                    if (attachment.String("hash") is not { } hash) continue;
                    var isImage = attachment.String("contentType")?.StartsWith("image/", StringComparison.Ordinal) == true;
                    try
                    {
                        if (policy.Attachments == OfflineAttachments.Full && !HasOriginal(hash))
                        {
                            await DownloadAsync(hash, thumbnail: false, ct).ConfigureAwait(false);
                        }
                        else if (policy.Attachments == OfflineAttachments.Thumbnails && isImage && !HasOriginal(hash)
                                 && !File.Exists(_store.ThumbnailFile(hash)))
                        {
                            await DownloadAsync(hash, thumbnail: true, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OrganizerOfflineException)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // One missing or unreadable file doesn't stop the rest.
                    }
                }
            }

            foreach (var record in _store.AllBlobs().Where(record => !referenced.Contains(record.Hash) && !record.NeedsUpload))
            {
                _store.DeleteBlob(record.Hash);
            }
            EnforceStorageCap();
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Least recently looked-at originals go first; never one not yet uploaded, never thumbnails.</summary>
    private void EnforceStorageCap()
    {
        if (Policy.StorageCapBytes is not { } cap) return;
        var records = _store.AllBlobs().Where(record => record.HasOriginal).OrderBy(record => record.LastUsedMillis).ToList();
        var total = records.Sum(record => record.Size);
        foreach (var record in records)
        {
            if (total <= cap) break;
            if (record.NeedsUpload) continue;
            StoreFiles.DeleteQuietly(_store.OriginalFile(record.Hash));
            _store.SaveBlob(record with { HasOriginal = false });
            total -= record.Size;
        }
    }

    // ---- connection ------------------------------------------------------------------------

    public bool IsConnected => _transport is not null;

    public void Connect(IOrganizerTransport transport)
    {
        CancellationToken token;
        lock (_gate)
        {
            _connection?.Cancel();
            _connection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _connection.Token;
        }
        _transport = transport;
        _ = Task.Run(async () =>
        {
            await RefreshStatusAsync().ConfigureAwait(false);
            await SyncAsync().ConfigureAwait(false);
            await WaitLoopAsync(token).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            _connection?.Cancel();
            _connection = null;
            _debounce?.Cancel();
            _debounce = null;
        }
        _transport = null;
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            var fresh = OrganizerJson.Status(OrganizerJson.Parse((await SendAsync("GET", "/api/organizer/status").ConfigureAwait(false)).Body));
            AdoptOwner(fresh.Username ?? "");
            lock (_gate)
            {
                _status = fresh;
                _syncState = _syncState with { TurnedOff = !fresh.Enabled, NeedsSignIn = false };
            }
            RaiseChanged();
        }
        catch (OrganizerSignInRequiredException)
        {
            UpdateSyncState(state => state with { NeedsSignIn = true });
        }
        catch (Exception)
        {
            // Keep the last status; being offline is reported by sync.
        }
    }

    // Signing in as someone else on this PC must not show them the last person's copy.
    private void AdoptOwner(string owner)
    {
        var stored = _store.Meta("owner");
        if (stored is not null && stored != owner) ResetLocalCopy(keepPending: false);
        _store.SetMeta("owner", owner);
    }

    private void ResetLocalCopy(bool keepPending)
    {
        lock (_gate)
        {
            _items.Clear();
            _archive.Clear();
            _inflight.Clear();
            if (!keepPending) _pending.Clear();
            _serverId = null;
            _cursor = 0;
            _spaces = Array.Empty<OrganizerSpace>();
            _store.Wipe(keepPending);
            _syncState = _syncState with { WaitingChanges = _pending.Count };
        }
        ReplanAlerts();
        RaiseChanged();
    }

    private void ScheduleSync(TimeSpan delay)
    {
        if (_transport is null) return;
        CancellationToken token;
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce = CancellationTokenSource.CreateLinkedTokenSource(_connection?.Token ?? _lifetime.Token);
            token = _debounce.Token;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                await SyncAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private async Task WaitLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var state = SyncState;
                if (ResetPrompt is not null || state.TurnedOff || state.NeedsSignIn)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    if (state.TurnedOff || state.NeedsSignIn) await RefreshStatusAsync().ConfigureAwait(false);
                    continue;
                }
                try
                {
                    var cursor = CursorValue();
                    var response = await SendAsync("GET", "/api/organizer/wait",
                        new Dictionary<string, string> { ["cursor"] = cursor.ToString(), ["timeout"] = "25" },
                        timeout: TimeSpan.FromSeconds(40), ct: ct).ConfigureAwait(false);
                    var answer = OrganizerJson.Parse(response.Body)["cursor"]?.GetValue<long>() ?? cursor;
                    if (answer != CursorValue()) await SyncAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    UpdateSyncState(s => s with { Online = false });
                    await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    await SyncAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private long CursorValue() { lock (_gate) return _cursor; }

    // ---- sync ------------------------------------------------------------------------------

    /// <summary>
    /// Returns once a sync pass that started after this call has finished - so an edit made just
    /// before calling it has been sent. Passes never overlap. (OrganizerKit's engine explains why
    /// this counts passes rather than keeping a "sync again" flag: the flag raced.)
    /// </summary>
    public async Task SyncAsync()
    {
        var wanted = Interlocked.Increment(ref _requestedPass);
        await _syncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _completedPass) >= wanted || _transport is null)
            {
                if (_transport is null) Interlocked.Exchange(ref _completedPass, Interlocked.Read(ref _requestedPass));
                return;
            }
            UpdateSyncState(state => state with { Syncing = true });
            try
            {
                while (Interlocked.Read(ref _completedPass) < Interlocked.Read(ref _requestedPass))
                {
                    var pass = Interlocked.Read(ref _requestedPass);
                    try
                    {
                        await SyncOnceAsync(restoreSpaces: null).ConfigureAwait(false);
                        Settle(pass);
                        UpdateSyncState(state => state with
                        {
                            Online = true, NeedsSignIn = false, TurnedOff = false, LastSyncedAt = DateTimeOffset.Now,
                        });
                    }
                    catch (OrganizerSignInRequiredException)
                    {
                        Settle(pass);
                        UpdateSyncState(state => state with { NeedsSignIn = true });
                        return;
                    }
                    catch (OrganizerTurnedOffException)
                    {
                        Settle(pass);
                        UpdateSyncState(state => state with { TurnedOff = true });
                        return;
                    }
                    catch (Exception error)
                    {
                        Settle(pass);
                        UpdateSyncState(state => state with
                        {
                            Online = false,
                            LastError = error is OrganizerException and not OrganizerOfflineException ? error.Message : state.LastError,
                        });
                        ScheduleSync(TimeSpan.FromSeconds(20));
                        return;
                    }
                }
            }
            finally
            {
                int waiting;
                lock (_gate) waiting = _pending.Count + _inflight.Count;
                UpdateSyncState(state => state with { Syncing = false, WaitingChanges = waiting });
            }
        }
        finally
        {
            _syncGate.Release();
        }
        StartBlobWork();
    }

    // A pass settles the requests made before it started - and only those. A failure used to mark
    // every request so far as done, so a sync asked for the moment the network came back could be
    // answered by a pass that had started, and failed, while it was still down.
    private void Settle(long pass) =>
        Interlocked.Exchange(ref _completedPass, Math.Max(Interlocked.Read(ref _completedPass), pass));

    private async Task SyncOnceAsync(IReadOnlyList<OrganizerSpace>? restoreSpaces)
    {
        if (ResetPrompt is not null) return;
        lock (_gate)
        {
            foreach (var (id, change) in _pending)
            {
                _inflight[id] = _inflight.TryGetValue(id, out var older) ? older.Merging(change) : change;
            }
            _pending.Clear();
        }

        var first = true;
        var hasMore = true;
        while (hasMore)
        {
            JsonObject body;
            lock (_gate)
            {
                body = new JsonObject
                {
                    ["serverId"] = _serverId,
                    ["cursor"] = _cursor,
                    ["knownSpaces"] = new JsonArray(_spaces.Select(space => (JsonNode?)JsonValue.Create(space.Id)).ToArray()),
                    ["changes"] = new JsonArray(first ? _inflight.Values.Select(change => (JsonNode?)OrganizerJson.ChangeJson(change)).ToArray() : Array.Empty<JsonNode?>()),
                };
                if (first && restoreSpaces is not null)
                {
                    body["restoreSpaces"] = new JsonArray(restoreSpaces.Select(space => (JsonNode?)OrganizerJson.SpaceJson(space)).ToArray());
                }
            }
            JsonObject reply;
            try
            {
                var response = await SendAsync("POST", "/api/organizer/sync", body: Encoding.UTF8.GetBytes(body.ToJsonString()),
                    timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                reply = OrganizerJson.Parse(response.Body);
            }
            catch (Exception)
            {
                RestoreInflight();
                throw;
            }

            if (reply["reset"] is JsonValue reset && reset.GetValue<bool>())
            {
                RestoreInflight();
                await HandleResetAsync(OrganizerJson.Text(reply, "serverId") ?? "").ConfigureAwait(false);
                return;
            }
            Apply(reply, settlesInflight: first);
            first = false;
            hasMore = reply["hasMore"] is JsonValue more && more.GetValue<bool>();
        }
    }

    private void RestoreInflight()
    {
        lock (_gate)
        {
            foreach (var (id, change) in _inflight)
            {
                _pending[id] = _pending.TryGetValue(id, out var newer) ? change.Merging(newer) : change;
            }
            _inflight.Clear();
        }
    }

    private void Apply(JsonObject reply, bool settlesInflight)
    {
        lock (_gate)
        {
            var saved = new List<OrganizerItem>();
            var dropped = new List<string>();
            foreach (var node in reply["removed"] as JsonArray ?? new JsonArray())
            {
                if (node?.GetValue<string>() is not { } id) continue;
                _items.Remove(id);
                dropped.Add(id);
            }

            var echoed = new HashSet<string>();
            foreach (var node in reply["items"] as JsonArray ?? new JsonArray())
            {
                if (node is not JsonObject json) continue;
                OrganizerItem item;
                try { item = OrganizerJson.Item(json); }
                catch (Exception) { continue; }
                echoed.Add(item.Id);
                foreach (var clock in item.Clocks.Values) _clock.Observe(clock);
                // Edits made while this request was out are newer than anything in it.
                if (_pending.TryGetValue(item.Id, out var newer))
                {
                    foreach (var (name, value) in newer.Fields)
                    {
                        item = ApplyField(item, name, value, newer.Clocks.GetValueOrDefault(name) ?? "");
                    }
                }
                _items[item.Id] = item;
                _archive.Remove(item.Id);
                saved.Add(item);
            }

            if (settlesInflight)
            {
                foreach (var node in reply["rejected"] as JsonArray ?? new JsonArray())
                {
                    if (node is not JsonObject rejection || OrganizerJson.Text(rejection, "id") is not { } id) continue;
                    _syncState = _syncState with { LastError = OrganizerJson.Text(rejection, "error") };
                    // Refused and not on the server at all - a new item that can never be made.
                    if (!echoed.Contains(id) && !_pending.ContainsKey(id) && _items.GetValueOrDefault(id)?.Seq is null)
                    {
                        _items.Remove(id);
                        dropped.Add(id);
                    }
                }
                var settled = _inflight.Keys.ToList();
                _inflight.Clear();
                foreach (var id in settled) PersistPendingLocked(id);
            }

            var spaces = (reply["spaces"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(OrganizerJson.Space).ToList();
            _spaces = spaces;
            var visible = spaces.Select(space => space.Id).ToHashSet();
            foreach (var item in _items.Values.Where(item => !visible.Contains(item.SpaceId) && !_pending.ContainsKey(item.Id)).ToList())
            {
                _items.Remove(item.Id);
                dropped.Add(item.Id);
            }

            var protectedIds = _pending.Keys.Concat(_keptOffline).ToHashSet();
            foreach (var id in _policy.ItemsToPrune(_items, protectedIds, DateTimeOffset.UtcNow, _zone()))
            {
                _items.Remove(id);
                dropped.Add(id);
            }

            _cursor = reply["cursor"] is JsonValue cursor ? cursor.GetValue<long>() : _cursor;
            _serverId = OrganizerJson.Text(reply, "serverId") ?? _serverId;
            _missingBlobs = (reply["missingBlobs"] as JsonArray ?? new JsonArray())
                .Select(node => node?.GetValue<string>()).OfType<string>().ToHashSet();
            _store.SaveItems(saved.Where(item => _items.ContainsKey(item.Id)).ToList());
            _store.DeleteItems(dropped);
            _store.SetMeta("cursor", _cursor.ToString());
            _store.SetMeta("serverId", _serverId);
            _store.SetMeta("spaces", new JsonArray(spaces.Select(space => (JsonNode?)OrganizerJson.SpaceJson(space)).ToArray()).ToJsonString());
        }
        ReplanAlerts();
        RaiseChanged();
    }

    private async Task HandleResetAsync(string newServerId)
    {
        int live;
        string? known;
        lock (_gate)
        {
            live = _items.Values.Count(item => !item.Deleted);
            known = _serverId;
        }
        if (known is null || live == 0)
        {
            // Nothing here worth offering - take the server's copy.
            ResetLocalCopy(keepPending: known is null);
            lock (_gate)
            {
                _serverId = newServerId;
                _store.SetMeta("serverId", newServerId);
            }
            Interlocked.Increment(ref _requestedPass);
            return;
        }
        await RefreshStatusAsync().ConfigureAwait(false);
        lock (_gate) _resetPrompt = new OrganizerResetPrompt(newServerId, live, _status?.ItemCount);
        RaiseChanged();
    }

    /// <summary>
    /// The person's answer to a <see cref="OrganizerResetPrompt"/>. Uploading sends every item with
    /// the clocks it already has, so the new server merges it exactly like the original edits and
    /// newer edits already there still win; groups come back with it.
    /// </summary>
    public async Task ResolveResetAsync(bool upload)
    {
        OrganizerResetPrompt? prompt;
        lock (_gate)
        {
            prompt = _resetPrompt;
            _resetPrompt = null;
        }
        if (prompt is null) return;
        RaiseChanged();
        if (!upload)
        {
            ResetLocalCopy(keepPending: false);
            lock (_gate)
            {
                _serverId = prompt.ServerId;
                _store.SetMeta("serverId", prompt.ServerId);
            }
            await SyncAsync().ConfigureAwait(false);
            return;
        }
        List<OrganizerSpace> groups;
        lock (_gate)
        {
            var restore = new Dictionary<string, OrganizerChange>();
            foreach (var item in _items.Values)
            {
                var fields = new Dictionary<string, object?>(item.Fields)
                {
                    ["spaceId"] = item.SpaceId,
                    ["deleted"] = item.Deleted,
                };
                if (item.ParentId is not null) fields["parentId"] = item.ParentId;
                var fallback = _clock.Next();
                restore[item.Id] = new OrganizerChange(item.Id, item.Kind, fields,
                    fields.Keys.ToDictionary(key => key, key => item.Clocks.GetValueOrDefault(key) ?? fallback));
            }
            foreach (var (id, change) in _pending)
            {
                restore[id] = restore.TryGetValue(id, out var older) ? older.Merging(change) : change;
            }
            _pending.Clear();
            _inflight.Clear();
            foreach (var (id, change) in restore) _inflight[id] = change;
            foreach (var id in restore.Keys) PersistPendingLocked(id);
            _serverId = prompt.ServerId;
            _cursor = 0;
            _store.SetMeta("serverId", prompt.ServerId);
            _store.SetMeta("cursor", "0");
            groups = _spaces.Where(space => space.Kind == OrganizerSpaceKind.Group).ToList();
        }
        await _syncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            UpdateSyncState(state => state with { Syncing = true });
            await SyncOnceAsync(groups).ConfigureAwait(false);
            UpdateSyncState(state => state with { Online = true, LastSyncedAt = DateTimeOffset.Now });
            var local = _store.AllBlobs().Where(record => record.HasOriginal).Select(record => record.Hash);
            lock (_gate) _missingBlobs.UnionWith(local);
        }
        catch (Exception)
        {
            UpdateSyncState(state => state with { Online = false });
        }
        finally
        {
            int waiting;
            lock (_gate) waiting = _pending.Count + _inflight.Count;
            UpdateSyncState(state => state with { Syncing = false, WaitingChanges = waiting });
            _syncGate.Release();
        }
        StartBlobWork();
    }

    // ---- policy & storage ------------------------------------------------------------------

    public void SetPolicy(OfflinePolicy policy)
    {
        lock (_gate)
        {
            var widened = policy.Widens(_policy);
            _policy = policy;
            if (widened)
            {
                // Items thrown away under the old policy are behind the cursor; read everything once.
                _cursor = 0;
                _store.SetMeta("cursor", "0");
            }
            else
            {
                var prune = policy.ItemsToPrune(_items, _pending.Keys.Concat(_keptOffline).ToHashSet(), DateTimeOffset.UtcNow, _zone());
                foreach (var id in prune) _items.Remove(id);
                _store.DeleteItems(prune);
            }
        }
        RaiseChanged();
        _ = Task.Run(async () =>
        {
            await SyncAsync().ConfigureAwait(false);
            EnforceStorageCap();
        });
    }

    public void SetKeptOffline(string id, bool keep)
    {
        lock (_gate)
        {
            var kept = new HashSet<string>(_keptOffline);
            if (keep) kept.Add(id);
            else kept.Remove(id);
            _keptOffline = kept;
            _store.SetMeta("keptOffline", new JsonArray(kept.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()).ToJsonString());
        }
        RaiseChanged();
    }

    public OrganizerStorageSummary StorageSummary()
    {
        lock (_gate)
        {
            var attachments = _items.Values.Where(item => item.Kind == OrganizerKinds.Attachment && !item.Deleted).ToList();
            var blobs = _store.AllBlobs();
            var thumbnails = Directory.Exists(_store.ThumbnailDirectory)
                ? new DirectoryInfo(_store.ThumbnailDirectory).EnumerateFiles().Sum(file => file.Length)
                : 0;
            return new OrganizerStorageSummary(
                ItemCount: _items.Values.Count(item => !item.Deleted),
                ServerItemCount: _status?.ItemCount,
                AttachmentCount: attachments.Count,
                AttachmentsWithOriginal: attachments.Count(item => item.String("hash") is { } hash && HasOriginal(hash)),
                BytesOnDevice: blobs.Where(record => record.HasOriginal).Sum(record => record.Size) + thumbnails,
                WaitingUploads: blobs.Count(record => record.NeedsUpload));
        }
    }

    public void ClearError() => UpdateSyncState(state => state with { LastError = null });

    // ---- alerts ----------------------------------------------------------------------------

    private void ReplanAlerts()
    {
        List<OrganizerItem> snapshot;
        lock (_gate) snapshot = _items.Values.ToList();
        var planned = AlertPlanner.Plan(snapshot, DateTimeOffset.UtcNow, _zone());
        lock (_gate)
        {
            if (planned.SequenceEqual(_plannedAlerts)) return;
            _plannedAlerts = planned;
        }
        AlertsChanged?.Invoke(planned);
    }

    // ---- online-only actions ---------------------------------------------------------------

    /// <summary>Everything of one kind, including what the offline window leaves out. Memory only.</summary>
    public async Task LoadArchiveAsync(string kind)
    {
        var reply = OrganizerJson.Parse((await SendAsync("GET", "/api/organizer/items",
            new Dictionary<string, string> { ["kind"] = kind }).ConfigureAwait(false)).Body);
        lock (_gate)
        {
            foreach (var json in (reply["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var item = OrganizerJson.Item(json);
                if (!_items.ContainsKey(item.Id)) _archive[item.Id] = item;
            }
        }
        RaiseChanged();
    }

    public async Task<List<OrganizerPerson>> PeopleAsync()
    {
        var reply = OrganizerJson.Parse((await SendAsync("GET", "/api/organizer/people").ConfigureAwait(false)).Body);
        return (reply["people"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(json => new OrganizerPerson(OrganizerJson.Text(json, "username") ?? "", OrganizerJson.Text(json, "displayName")))
            .Where(person => person.Username.Length > 0)
            .ToList();
    }

    public async Task SaveGroupAsync(string? id, string name, IReadOnlyList<string> members, string? color)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["name"] = name,
            ["members"] = new JsonArray(members.Select(member => (JsonNode?)JsonValue.Create(member)).ToArray()),
            ["color"] = color,
        }.ToJsonString());
        if (id is not null) await SendAsync("PUT", $"/api/organizer/spaces/{Uri.EscapeDataString(id)}", body: body).ConfigureAwait(false);
        else await SendAsync("POST", "/api/organizer/spaces", body: body).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(string id)
    {
        await SendAsync("DELETE", $"/api/organizer/spaces/{Uri.EscapeDataString(id)}").ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    public async Task<string> CalendarFeedPathAsync(bool reset = false)
    {
        var response = reset
            ? await SendAsync("POST", "/api/organizer/calendar-feed/reset").ConfigureAwait(false)
            : await SendAsync("GET", "/api/organizer/calendar-feed").ConfigureAwait(false);
        return OrganizerJson.Text(OrganizerJson.Parse(response.Body), "path") ?? throw new OrganizerServerException(500, null);
    }

    // ---- transport -------------------------------------------------------------------------

    private async Task<OrganizerResponse> SendAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        byte[]? body = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var transport = _transport ?? throw new OrganizerOfflineException();
        var response = await transport.SendAsync(method, path, query, body, body is null ? null : "application/json", timeout, ct)
            .ConfigureAwait(false);
        if (response.Status is >= 200 and < 300) return response;
        if (response.Status == 401) throw new OrganizerSignInRequiredException();
        var message = OrganizerJson.ErrorMessage(response.Body);
        if (response.Status == 404 && message?.Contains("turned off", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new OrganizerTurnedOffException();
        }
        throw new OrganizerServerException(response.Status, message);
    }

    public void Dispose()
    {
        Disconnect();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
