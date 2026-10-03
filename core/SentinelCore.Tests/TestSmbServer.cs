using System.Net;
using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using FileAttributes = SMBLibrary.FileAttributes;

namespace SentinelCore.Tests;

// A real SMB2 server on a loopback port, sharing local folders read-only - what the network
// library tests talk to in place of a Windows PC or a NAS. SMBLibrary's own server, with a small
// read-only file store over a directory (the library's disk-backed stores live in packages this
// repo does not use).
public sealed class TestSmbServer : IDisposable
{
    public const string Username = "family";
    public const string Password = "popcorn";

    private readonly PortSmbServer _server;

    public TestSmbServer(IReadOnlyDictionary<string, string> shares, bool allowGuests = false)
    {
        var collection = new SMBShareCollection();
        foreach (var (name, folder) in shares)
            collection.Add(new FileSystemShare(name, new ReadOnlyDirectoryStore(folder)));
        var ntlm = new IndependentNTLMAuthenticationProvider(user =>
            string.Equals(user, Username, StringComparison.OrdinalIgnoreCase) ? Password
            : allowGuests && (user.Length == 0 || string.Equals(user, "guest", StringComparison.OrdinalIgnoreCase)) ? ""
            : null);
        _server = new PortSmbServer(collection, new GSSProvider(ntlm));
        Port = FreePort();
        _server.StartOn(IPAddress.Loopback, Port);
    }

    public int Port { get; }

    public string Host => $"127.0.0.1:{Port}";

    // SMBLibrary's Stop can race its own connection threads and throw a NullReferenceException
    // from ReleaseConnection while a client is still closing - seen on the media box 2026-09-29,
    // failing a test whose assertions had all passed. The server is going away either way.
    public void Dispose()
    {
        try { _server.Stop(); }
        catch (NullReferenceException) { }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class PortSmbServer(SMBShareCollection shares, GSSProvider provider) : SMBServer(shares, provider)
    {
        public void StartOn(IPAddress address, int port) =>
            Start(address, SMBTransportType.DirectTCPTransport, port, enableSMB1: false, enableSMB2: true, enableSMB3: true, null);
    }

    private sealed class Handle(string path, bool isDirectory)
    {
        public string Path { get; } = path;
        public bool IsDirectory { get; } = isDirectory;
        public FileStream? Stream { get; set; }
    }

    private sealed class ReadOnlyDirectoryStore(string root) : INTFileStore
    {
        private string Full(string path)
        {
            var relative = path.Replace('\\', System.IO.Path.DirectorySeparatorChar).TrimStart(System.IO.Path.DirectorySeparatorChar);
            return relative.Length == 0 ? root : System.IO.Path.Combine(root, relative);
        }

        public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess, FileAttributes fileAttributes,
            ShareAccess shareAccess, CreateDisposition createDisposition, CreateOptions createOptions, SecurityContext securityContext)
        {
            handle = null!;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
            if (createDisposition is not (CreateDisposition.FILE_OPEN or CreateDisposition.FILE_OPEN_IF)) return NTStatus.STATUS_ACCESS_DENIED;
            var full = Full(path);
            // The one write it allows: a new folder (the folder picker's "New folder").
            if (createDisposition == CreateDisposition.FILE_OPEN_IF && (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0
                && !Directory.Exists(full) && !File.Exists(full) && Directory.Exists(System.IO.Path.GetDirectoryName(full)))
                Directory.CreateDirectory(full);
            var isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full))
                return Directory.Exists(System.IO.Path.GetDirectoryName(full)) ? NTStatus.STATUS_OBJECT_NAME_NOT_FOUND : NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;
            if ((createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0 && !isDirectory) return NTStatus.STATUS_NOT_A_DIRECTORY;
            if ((createOptions & CreateOptions.FILE_NON_DIRECTORY_FILE) != 0 && isDirectory) return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
            var opened = new Handle(full, isDirectory);
            if (!isDirectory && (desiredAccess & (AccessMask.GENERIC_READ | (AccessMask)FileAccessMask.FILE_READ_DATA)) != 0)
                opened.Stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            handle = opened;
            fileStatus = FileStatus.FILE_OPENED;
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus CloseFile(object handle)
        {
            ((Handle)handle).Stream?.Dispose();
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
        {
            data = [];
            var opened = (Handle)handle;
            if (opened.Stream is null) return NTStatus.STATUS_ACCESS_DENIED;
            if (offset >= opened.Stream.Length) return NTStatus.STATUS_END_OF_FILE;
            opened.Stream.Position = offset;
            var buffer = new byte[Math.Min(maxCount, opened.Stream.Length - offset)];
            var read = opened.Stream.Read(buffer, 0, buffer.Length);
            data = read == buffer.Length ? buffer : buffer[..read];
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName, FileInformationClass informationClass)
        {
            result = [];
            var opened = (Handle)handle;
            if (!opened.IsDirectory) return NTStatus.STATUS_NOT_A_DIRECTORY;
            if (informationClass != FileInformationClass.FileDirectoryInformation) return NTStatus.STATUS_NOT_SUPPORTED;
            foreach (var entry in new DirectoryInfo(opened.Path).EnumerateFileSystemInfos())
            {
                if (fileName != "*" && !string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new FileDirectoryInformation
                {
                    FileName = entry.Name,
                    CreationTime = entry.CreationTimeUtc,
                    LastAccessTime = entry.LastAccessTimeUtc,
                    LastWriteTime = entry.LastWriteTimeUtc,
                    ChangeTime = entry.LastWriteTimeUtc,
                    EndOfFile = entry is FileInfo file ? file.Length : 0,
                    AllocationSize = entry is FileInfo sized ? sized.Length : 0,
                    FileAttributes = entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal
                });
            }
            return result.Count == 0 && fileName != "*" ? NTStatus.STATUS_NO_SUCH_FILE : NTStatus.STATUS_SUCCESS;
        }

        public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
        {
            var opened = (Handle)handle;
            FileSystemInfo info = opened.IsDirectory ? new DirectoryInfo(opened.Path) : new FileInfo(opened.Path);
            var length = info is FileInfo file ? file.Length : 0;
            var attributes = opened.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
            switch (informationClass)
            {
                case FileInformationClass.FileNetworkOpenInformation:
                    result = new FileNetworkOpenInformation
                    {
                        CreationTime = info.CreationTimeUtc,
                        LastAccessTime = info.LastAccessTimeUtc,
                        LastWriteTime = info.LastWriteTimeUtc,
                        ChangeTime = info.LastWriteTimeUtc,
                        AllocationSize = length,
                        EndOfFile = length,
                        FileAttributes = attributes
                    };
                    return NTStatus.STATUS_SUCCESS;
                case FileInformationClass.FileStandardInformation:
                    result = new FileStandardInformation { AllocationSize = length, EndOfFile = length, NumberOfLinks = 1, Directory = opened.IsDirectory };
                    return NTStatus.STATUS_SUCCESS;
                case FileInformationClass.FileBasicInformation:
                    result = new FileBasicInformation
                    {
                        CreationTime = info.CreationTimeUtc,
                        LastAccessTime = info.LastAccessTimeUtc,
                        LastWriteTime = info.LastWriteTimeUtc,
                        ChangeTime = info.LastWriteTimeUtc,
                        FileAttributes = attributes
                    };
                    return NTStatus.STATUS_SUCCESS;
                default:
                    result = null!;
                    return NTStatus.STATUS_INVALID_INFO_CLASS;
            }
        }

        public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass)
        {
            result = null!;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data) { numberOfBytesWritten = 0; return NTStatus.STATUS_ACCESS_DENIED; }
        public NTStatus FlushFileBuffers(object handle) => NTStatus.STATUS_SUCCESS;
        public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) => NTStatus.STATUS_NOT_SUPPORTED;
        public NTStatus UnlockFile(object handle, long byteOffset, long length) => NTStatus.STATUS_NOT_SUPPORTED;
        public NTStatus SetFileInformation(object handle, FileInformation information) => NTStatus.STATUS_ACCESS_DENIED;
        public NTStatus SetFileSystemInformation(FileSystemInformation information) => NTStatus.STATUS_ACCESS_DENIED;
        public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation) { result = null!; return NTStatus.STATUS_NOT_SUPPORTED; }
        public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor) => NTStatus.STATUS_ACCESS_DENIED;
        public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize,
            OnNotifyChangeCompleted onNotifyChangeCompleted, object context) { ioRequest = null!; return NTStatus.STATUS_NOT_SUPPORTED; }
        public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_NOT_SUPPORTED;
        public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength) { output = []; return NTStatus.STATUS_NOT_SUPPORTED; }
    }
}
