using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace SentinelCore.Web;

// Movie folders shared from another computer on the network (a Windows PC, a Mac with File
// Sharing on, a NAS, Samba on Linux), read over SMB by the server itself. Nothing is mounted, so
// this works the same whether the server runs on Linux, Windows or macOS, and needs no root.
//
// A library on a share has a path like smb://192.168.1.20/Movies/Films; the credentials for that
// computer are a MediaNetworkComputer, looked up by host. Everything that reads files goes
// through IMediaFileSystem, and ffmpeg reads network files through MediaFileRelay.

// smb://host[:port]/share/folder/file. Share and Rest are empty for the computer itself; Rest
// uses '/' between folders. Names are kept exactly as the share spells them (no URL escaping):
// the path is an identifier the server hands back to itself, never a URL a browser opens.
public sealed record MediaNetworkPath(string Host, int? Port, string Share, string Rest)
{
    public const string Scheme = "smb://";

    public static bool IsNetwork(string? path) =>
        path is not null && (path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\", StringComparison.Ordinal));

    // Accepts smb://host/share/x, \\host\share\x and //host/share/x (what people paste from
    // Explorer, Finder's Connect to Server, or a Linux fstab).
    public static bool TryParse(string? text, out MediaNetworkPath path)
    {
        path = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) value = value[Scheme.Length..];
        else if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)) value = value[2..];
        else return false;

        var parts = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var hostPart = parts[0];
        // Credentials typed into the address ("smb://user@host") are not kept in the path.
        if (hostPart.LastIndexOf('@') is var at and >= 0) hostPart = hostPart[(at + 1)..];
        int? port = null;
        var colon = hostPart.LastIndexOf(':');
        if (colon > 0 && !hostPart.Contains(']') && hostPart.IndexOf(':') == colon)
        {
            if (!int.TryParse(hostPart[(colon + 1)..], out var parsed) || parsed is < 1 or > 65535) return false;
            port = parsed == 445 ? null : parsed;
            hostPart = hostPart[..colon];
        }
        hostPart = hostPart.Trim('[', ']');
        if (hostPart.Length == 0) return false;
        var share = parts.Length > 1 ? parts[1] : "";
        var rest = parts.Length > 2 ? string.Join('/', parts[2..]) : "";
        path = new MediaNetworkPath(hostPart, port, share, rest);
        return true;
    }

    public static string? Normalize(string? text) => TryParse(text, out var path) ? path.ToString() : null;

    public string HostKey => Port is { } port ? $"{Host}:{port}".ToLowerInvariant() : Host.ToLowerInvariant();

    public string ComputerPath => Scheme + HostWithPort;

    public string HostWithPort => Port is { } port ? $"{Host}:{port}" : Host;

    // The path inside the share the SMB protocol wants: folders separated by backslashes, no
    // leading slash, empty for the share's top folder.
    public string SmbPath => Rest.Replace('/', '\\');

    public override string ToString()
    {
        var text = Scheme + HostWithPort;
        if (Share.Length > 0) text += "/" + Share;
        if (Rest.Length > 0) text += "/" + Rest;
        return text;
    }

    public MediaNetworkPath Child(string name) =>
        Share.Length == 0 ? this with { Share = name } : this with { Rest = Rest.Length == 0 ? name : Rest + "/" + name };

    public MediaNetworkPath? Parent()
    {
        if (Rest.Length > 0)
        {
            var slash = Rest.LastIndexOf('/');
            return this with { Rest = slash < 0 ? "" : Rest[..slash] };
        }
        return Share.Length > 0 ? this with { Share = "" } : null;
    }
}

// Saved sign-in for one computer. The password is kept as typed (SMB needs it, not a hash), in
// media-network.json beside media.db, readable only by the server's own account - the same trust
// FamilyFiles' SFTP drives and the auto export's SFTP target already have.
public sealed record MediaNetworkComputer(
    Guid Id,
    string Host,
    string Username,
    string Password,
    string Domain,
    DateTimeOffset CreatedAt,
    string? Name = null)
{
    public string HostKey => MediaNetworkPath.TryParse(MediaNetworkPath.Scheme + Host, out var path) ? path.HostKey : Host.ToLowerInvariant();
}

public sealed class MediaNetworkComputerInput
{
    public string Host { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string Username { get; set; } = string.Empty;
    // Blank on an edit keeps the saved password, the way the Family Files drive form works.
    public string? Password { get; set; }
    public string? Domain { get; set; }
}

public sealed class MediaNetworkComputerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private List<MediaNetworkComputer>? _computers;

    public MediaNetworkComputerStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "media-network.json");
    }

    public IReadOnlyList<MediaNetworkComputer> All()
    {
        lock (_gate) return Load().ToList();
    }

    public MediaNetworkComputer? Get(Guid id)
    {
        lock (_gate) return Load().FirstOrDefault(c => c.Id == id);
    }

    public MediaNetworkComputer? ForHost(string hostKey)
    {
        lock (_gate) return Load().FirstOrDefault(c => string.Equals(c.HostKey, hostKey, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(MediaNetworkComputer computer)
    {
        lock (_gate)
        {
            var list = Load();
            // One sign-in per computer: an edit that points at a host already saved replaces it.
            list.RemoveAll(c => c.Id != computer.Id && string.Equals(c.HostKey, computer.HostKey, StringComparison.OrdinalIgnoreCase));
            var index = list.FindIndex(c => c.Id == computer.Id);
            if (index >= 0) list[index] = computer; else list.Add(computer);
            Write(list);
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            var list = Load();
            if (list.RemoveAll(c => c.Id == id) > 0) Write(list);
        }
    }

    // Restore replaces by host, so importing a backup twice does not duplicate a computer.
    public int Restore(IEnumerable<MediaNetworkComputer> computers)
    {
        lock (_gate)
        {
            var list = Load();
            var count = 0;
            foreach (var computer in computers)
            {
                if (computer.Id == Guid.Empty || string.IsNullOrWhiteSpace(computer.Host)) continue;
                list.RemoveAll(c => c.Id == computer.Id || string.Equals(c.HostKey, computer.HostKey, StringComparison.OrdinalIgnoreCase));
                list.Add(computer);
                count++;
            }
            if (count > 0) Write(list);
            return count;
        }
    }

    private List<MediaNetworkComputer> Load()
    {
        if (_computers is not null) return _computers;
        try
        {
            _computers = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<MediaNetworkComputer>>(File.ReadAllText(_path), JsonOptions) ?? []
                : [];
        }
        catch (JsonException) { _computers = []; }
        return _computers;
    }

    private void Write(List<MediaNetworkComputer> list)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(list, JsonOptions));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, _path, overwrite: true);
        _computers = list;
    }
}

// A network failure worth showing to a person. NeedsLogin is set when the computer turned down
// the saved username and password (or there are none and it does not allow guests), so a client
// can open the sign-in form instead of just printing the message.
public sealed class MediaNetworkException(string message, bool needsLogin = false) : IOException(message)
{
    public bool NeedsLogin { get; } = needsLogin;
}

public sealed class MediaNetworkFileSystem : IMediaFileSystem, IDisposable
{
    private const int ResponseTimeoutMs = 15_000;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromSeconds(60);
    private const int MaxIdlePerShare = 4;

    private readonly MediaNetworkComputerStore _computers;
    private readonly MediaNetworkResolver _resolver;
    private readonly ILogger<MediaNetworkFileSystem> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SmbConnection>> _idle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _sweep;

    public MediaNetworkFileSystem(MediaNetworkComputerStore computers, MediaNetworkResolver resolver, ILogger<MediaNetworkFileSystem> logger)
    {
        _computers = computers;
        _resolver = resolver;
        _logger = logger;
        _sweep = new Timer(_ => SweepIdle(), null, IdleLifetime, IdleLifetime);
    }

    public MediaNetworkComputerStore Computers => _computers;

    // MARK: - Computers and shares

    // Signs in to the computer and lists its shared folders, hiding the administrative ones
    // (C$, ADMIN$, IPC$). Used to test a saved sign-in and to browse a computer's top level.
    // Throws MediaNetworkException when the computer cannot be reached or refuses the sign-in.
    public IReadOnlyList<string> ListShares(MediaNetworkPath computer, MediaNetworkComputer? credentials = null)
    {
        credentials ??= _computers.ForHost(computer.HostKey);
        var client = ConnectAndLogin(computer, credentials);
        try
        {
            // Some servers refuse to list shares to a guest but open them fine; an empty list
            // tells the caller to ask for the share's name instead.
            var shares = client.ListShares(out var status);
            if (status != NTStatus.STATUS_SUCCESS || shares is null) return [];
            return shares.Where(s => !s.EndsWith('$')).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { Quietly(() => client.Logoff()); Quietly(client.Disconnect); }
    }

    // MARK: - IMediaFileSystem

    public bool DirectoryExists(string path)
    {
        var parsed = Parse(path);
        if (parsed.Share.Length == 0) return false;
        try { return Stat(path) is { IsDirectory: true }; }
        catch (MediaNetworkException) { throw; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public IReadOnlyList<MediaFsEntry> List(string directory)
    {
        var parsed = Parse(directory);
        if (parsed.Share.Length == 0)
            return ListShares(parsed).Select(share => new MediaFsEntry(share, parsed.Child(share).ToString(), true, 0, DateTime.MinValue)).ToList();

        return WithConnection(parsed, connection =>
        {
            var status = connection.Store.CreateFile(out var handle, out _, parsed.SmbPath, AccessMask.GENERIC_READ, FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            ThrowIfFailed(status, parsed, isDirectory: true);
            try
            {
                status = connection.Store.QueryDirectory(out var found, handle, "*", FileInformationClass.FileDirectoryInformation);
                if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES) ThrowIfFailed(status, parsed, isDirectory: true);
                var entries = new List<MediaFsEntry>();
                foreach (var info in (found ?? []).OfType<FileDirectoryInformation>())
                {
                    if (info.FileName is "." or ".." || info.FileName.Length == 0) continue;
                    var isDirectory = (info.FileAttributes & FileAttributes.Directory) != 0;
                    entries.Add(new MediaFsEntry(info.FileName, parsed.Child(info.FileName).ToString(), isDirectory,
                        isDirectory ? 0 : info.EndOfFile, DateTime.SpecifyKind(info.LastWriteTime, DateTimeKind.Utc)));
                }
                return entries;
            }
            finally { connection.Store.CloseFile(handle); }
        });
    }

    public MediaFsEntry? Stat(string path)
    {
        var parsed = Parse(path);
        if (parsed.Share.Length == 0) return null;
        return WithConnection(parsed, connection =>
        {
            var status = connection.Store.CreateFile(out var handle, out _, parsed.SmbPath, AccessMask.GENERIC_READ, 0,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, 0, null);
            if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE
                or NTStatus.STATUS_OBJECT_NAME_INVALID or NTStatus.STATUS_DELETE_PENDING)
                return null;
            ThrowIfFailed(status, parsed, isDirectory: false);
            try
            {
                status = connection.Store.GetFileInformation(out var information, handle, FileInformationClass.FileNetworkOpenInformation);
                ThrowIfFailed(status, parsed, isDirectory: false);
                var info = (FileNetworkOpenInformation)information;
                var isDirectory = (info.FileAttributes & FileAttributes.Directory) != 0;
                var name = parsed.Rest.Length > 0 ? parsed.Rest[(parsed.Rest.LastIndexOf('/') + 1)..] : parsed.Share;
                return new MediaFsEntry(name, parsed.ToString(), isDirectory, isDirectory ? 0 : info.EndOfFile,
                    DateTime.SpecifyKind(info.LastWriteTime ?? DateTime.MinValue, DateTimeKind.Utc));
            }
            finally { connection.Store.CloseFile(handle); }
        });
    }

    public Stream OpenRead(string path)
    {
        var parsed = Parse(path);
        if (parsed.Share.Length == 0 || parsed.Rest.Length == 0) throw new FileNotFoundException("Not a file.", path);
        for (var attempt = 0; ; attempt++)
        {
            var connection = Acquire(parsed);
            try
            {
                var status = connection.Store.CreateFile(out var handle, out _, parsed.SmbPath, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                    FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
                    CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                if (status == NTStatus.STATUS_INVALID_SMB && attempt == 0) { connection.Dispose(); continue; }
                if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE)
                    throw new FileNotFoundException("The file is not on the share any more.", path);
                ThrowIfFailed(status, parsed, isDirectory: false);
                status = connection.Store.GetFileInformation(out var information, handle, FileInformationClass.FileStandardInformation);
                if (status != NTStatus.STATUS_SUCCESS) { connection.Store.CloseFile(handle); ThrowIfFailed(status, parsed, isDirectory: false); }
                var length = ((FileStandardInformation)information).EndOfFile;
                return new SmbReadStream(this, connection, handle, length);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    public string Combine(string directory, string name) => Parse(directory).Child(name).ToString();

    public string? Parent(string path) => Parse(path).Parent()?.ToString();

    public string Relative(string root, string path)
    {
        var rootText = Parse(root).ToString().TrimEnd('/');
        var full = Parse(path).ToString();
        return full.Length > rootText.Length && full.StartsWith(rootText + "/", StringComparison.OrdinalIgnoreCase)
            ? full[(rootText.Length + 1)..]
            : full;
    }

    // MARK: - Connections

    private static MediaNetworkPath Parse(string path) =>
        MediaNetworkPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException($"{path} is not a network path.", nameof(path));

    private T WithConnection<T>(MediaNetworkPath path, Func<SmbConnection, T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            var connection = Acquire(path);
            try
            {
                var result = action(connection);
                Release(connection);
                return result;
            }
            catch (StaleConnectionException) when (attempt == 0)
            {
                // A pooled connection the other end had already dropped (Windows closes idle
                // sessions); one fresh try.
                connection.Dispose();
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    private SmbConnection Acquire(MediaNetworkPath path)
    {
        var key = ShareKey(path);
        if (_idle.TryGetValue(key, out var queue))
            while (queue.TryDequeue(out var idle))
            {
                if (idle.Client.IsConnected && DateTime.UtcNow - idle.LastUsedUtc < IdleLifetime) return idle;
                idle.Dispose();
            }

        var credentials = _computers.ForHost(path.HostKey);
        var client = ConnectAndLogin(path, credentials);
        var store = client.TreeConnect(path.Share, out var status);
        if (status != NTStatus.STATUS_SUCCESS || store is null)
        {
            Quietly(() => client.Logoff());
            Quietly(client.Disconnect);
            throw status switch
            {
                NTStatus.STATUS_BAD_NETWORK_NAME or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_OBJECT_NAME_NOT_FOUND => new MediaNetworkException($"{path.Host} has no shared folder called \"{path.Share}\"."),
                NTStatus.STATUS_ACCESS_DENIED => new MediaNetworkException(
                    $"{path.Host} does not let {(credentials is null ? "guests" : credentials.Username)} open \"{path.Share}\".", needsLogin: true),
                _ => new MediaNetworkException($"{path.Host} would not open \"{path.Share}\" ({status}).")
            };
        }
        return new SmbConnection(key, client, store);
    }

    internal void Release(SmbConnection connection)
    {
        if (!connection.Client.IsConnected) { connection.Dispose(); return; }
        connection.LastUsedUtc = DateTime.UtcNow;
        var queue = _idle.GetOrAdd(connection.Key, _ => new ConcurrentQueue<SmbConnection>());
        if (queue.Count >= MaxIdlePerShare) { connection.Dispose(); return; }
        queue.Enqueue(connection);
    }

    private void SweepIdle()
    {
        foreach (var queue in _idle.Values)
        {
            var keep = new List<SmbConnection>();
            while (queue.TryDequeue(out var connection))
            {
                if (connection.Client.IsConnected && DateTime.UtcNow - connection.LastUsedUtc < IdleLifetime) keep.Add(connection);
                else connection.Dispose();
            }
            foreach (var connection in keep) queue.Enqueue(connection);
        }
    }

    private static string ShareKey(MediaNetworkPath path) => path.HostKey + "/" + path.Share.ToLowerInvariant();

    private SMB2Client ConnectAndLogin(MediaNetworkPath path, MediaNetworkComputer? credentials)
    {
        var address = _resolver.Resolve(path.Host);
        var client = new PortableSmb2Client();
        bool connected;
        try { connected = client.ConnectTo(address, path.Port ?? 445); }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException) { connected = false; }
        if (!connected)
            throw new MediaNetworkException(
                $"Could not reach {path.Host} ({address}) on the network. Check the computer is on and file sharing is turned on for it.");

        // A saved account first; with none, a guest sign-in the way Finder and Explorer try one.
        var attempts = credentials is not null && credentials.Username.Length > 0
            ? new[] { (credentials.Domain, credentials.Username, credentials.Password) }
            : [("", "", ""), ("", "guest", "")];
        var status = NTStatus.STATUS_LOGON_FAILURE;
        foreach (var (domain, user, password) in attempts)
        {
            try { status = client.Login(domain, user, password); }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException) { status = NTStatus.STATUS_INVALID_SMB; }
            if (status == NTStatus.STATUS_SUCCESS) return client;
        }
        Quietly(client.Disconnect);
        var who = credentials is null || credentials.Username.Length == 0 ? "a guest" : credentials.Username;
        throw status switch
        {
            NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_ACCESS_DENIED or NTStatus.STATUS_WRONG_PASSWORD
                or NTStatus.STATUS_ACCOUNT_RESTRICTION or NTStatus.STATUS_USER_SESSION_DELETED =>
                new MediaNetworkException(credentials is null || credentials.Username.Length == 0
                    ? $"{path.Host} wants a username and password."
                    : $"{path.Host} turned down the username or password for {who}.", needsLogin: true),
            NTStatus.STATUS_ACCOUNT_DISABLED or NTStatus.STATUS_ACCOUNT_LOCKED_OUT or NTStatus.STATUS_PASSWORD_EXPIRED
                or NTStatus.STATUS_PASSWORD_MUST_CHANGE =>
                new MediaNetworkException($"{path.Host} says the account {who} is locked, disabled or needs a new password ({status}).", needsLogin: true),
            _ => new MediaNetworkException($"{path.Host} did not accept the sign-in ({status}).")
        };
    }

    private static void ThrowIfFailed(NTStatus status, MediaNetworkPath path, bool isDirectory)
    {
        switch (status)
        {
            case NTStatus.STATUS_SUCCESS:
                return;
            case NTStatus.STATUS_INVALID_SMB or NTStatus.STATUS_NETWORK_NAME_DELETED or NTStatus.STATUS_USER_SESSION_DELETED
                or NTStatus.STATUS_SMB_BAD_TID or NTStatus.STATUS_FILE_CLOSED:
                throw new StaleConnectionException();
            case NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE:
                throw isDirectory
                    ? new DirectoryNotFoundException($"{path} is not on {path.Host} any more.")
                    : new FileNotFoundException($"{path} is not on {path.Host} any more.");
            case NTStatus.STATUS_NOT_A_DIRECTORY:
                throw new DirectoryNotFoundException($"{path} is a file, not a folder.");
            case NTStatus.STATUS_ACCESS_DENIED:
                throw new UnauthorizedAccessException($"{path.Host} does not allow reading {path}.");
            default:
                throw new IOException($"{path.Host} answered {status} for {path}.");
        }
    }

    private static void Quietly(Action action)
    {
        try { action(); } catch { /* the connection is going away regardless */ }
    }

    private static void Quietly(Func<NTStatus> action) => Quietly(() => { action(); });

    public void Dispose()
    {
        _sweep.Dispose();
        foreach (var queue in _idle.Values)
            while (queue.TryDequeue(out var connection)) connection.Dispose();
    }

    private sealed class StaleConnectionException() : IOException("The connection to the share was lost.");

    // Connect(address, transport, port) is protected internal in SMBLibrary; subclassing is the
    // supported way to reach a server on a port other than 445 (and how the tests reach theirs).
    private sealed class PortableSmb2Client() : SMB2Client(ResponseTimeoutMs)
    {
        public bool ConnectTo(IPAddress address, int port) => Connect(address, SMBTransportType.DirectTCPTransport, port);
    }

    // One signed-in connection to one share. SMB2Client is not safe to use from two threads at
    // once, so a connection is only ever held by one caller: an operation, or an open stream.
    internal sealed class SmbConnection(string key, SMB2Client client, ISMBFileStore store) : IDisposable
    {
        public string Key { get; } = key;
        public SMB2Client Client { get; } = client;
        public ISMBFileStore Store { get; } = store;
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public uint MaxReadSize => Math.Max(64 * 1024, Math.Min(Client.MaxReadSize, 1024 * 1024));

        public void Dispose()
        {
            Quietly(() => Store.Disconnect());
            Quietly(() => Client.Logoff());
            Quietly(Client.Disconnect);
        }
    }

    // A read-only, seekable view of a file on a share, reading ahead in chunks of the server's
    // largest read so a video streams at one round trip per megabyte.
    private sealed class SmbReadStream(MediaNetworkFileSystem owner, SmbConnection connection, object handle, long length) : Stream
    {
        private byte[] _buffer = [];
        private long _bufferStart = -1;
        private long _position;
        private bool _disposed;

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.Length == 0 || _position >= length) return 0;
            if (_bufferStart < 0 || _position < _bufferStart || _position >= _bufferStart + _buffer.Length)
            {
                var status = connection.Store.ReadFile(out var data, handle, _position, (int)connection.MaxReadSize);
                if (status == NTStatus.STATUS_END_OF_FILE || (status == NTStatus.STATUS_SUCCESS && (data is null || data.Length == 0))) return 0;
                if (status != NTStatus.STATUS_SUCCESS) throw new IOException($"Reading from the share failed ({status}).");
                _buffer = data!;
                _bufferStart = _position;
            }
            var available = (int)(_bufferStart + _buffer.Length - _position);
            var take = Math.Min(available, destination.Length);
            _buffer.AsSpan((int)(_position - _bufferStart), take).CopyTo(destination);
            _position += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // SMBLibrary is synchronous; the network wait happens on a pool thread, not the caller's.
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inBuffer = _bufferStart >= 0 && _position >= _bufferStart && _position < _bufferStart + _buffer.Length;
            if (inBuffer || _position >= length) return new ValueTask<int>(Read(destination.Span));
            return new ValueTask<int>(Task.Run(() => Read(destination.Span), cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                var closed = connection.Store.CloseFile(handle);
                if (closed == NTStatus.STATUS_SUCCESS) owner.Release(connection);
                else connection.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
