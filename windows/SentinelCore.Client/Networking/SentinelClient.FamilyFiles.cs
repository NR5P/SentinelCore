using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Networking;

/// <summary>
/// The Family Files half of the client: the household's shared file library, its trash, and the
/// backup drives it is copied to. The contract is core/SentinelCore.Web/FamilyFilesEndpoints.cs.
/// </summary>
/// <remarks>
/// Library paths are relative to the library root with forward slashes ("Photos/2026/beach.jpg")
/// and travel in the URL, so every segment is escaped on its own: a name with a space or a hash in
/// it is a perfectly good file name and must reach the server as the same name.
///
/// Uploads and downloads stream. A phone's worth of photos, or one video, can run to gigabytes;
/// reading that into a byte array first would be the difference between an app that copes and one
/// that falls over on exactly the files people most want kept. They go on the no-timeout session,
/// with the caller's token as the only limit - the 15-second budget every other request has is
/// tuned to "is the server there", not "is the file there yet".
/// </remarks>
public sealed partial class SentinelClient
{
    private const string FamilyFilesBase = "/api/family-files";

    // MARK: - Status and browsing

    /// <summary>
    /// Whether the FILES section should exist at all, and everything the section's header shows.
    /// Answers for every signed-in person, even when the feature is off (enabled: false).
    /// </summary>
    public Task<FamilyFilesStatus> FamilyFilesStatusAsync(CancellationToken ct = default) =>
        GetAsync<FamilyFilesStatus>($"{FamilyFilesBase}/status", ct: ct);

    /// <summary>One folder of the library; "" is the root.</summary>
    public Task<FamilyFilesListing> FamilyFilesListAsync(string path, CancellationToken ct = default) =>
        GetAsync<FamilyFilesListing>(
            $"{FamilyFilesBase}/list",
            new[] { new KeyValuePair<string, string>("path", path) },
            ct);

    /// <summary>Files whose name matches, newest first. An empty query is the most recent files.</summary>
    public async Task<List<FamilyFileEntry>> FamilyFilesSearchAsync(
        string q, int limit = 100, CancellationToken ct = default)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("q", q),
            new("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        var result = await GetAsync<FamilySearchResult>($"{FamilyFilesBase}/search", query, ct).ConfigureAwait(false);
        return result.Files;
    }

    /// <summary>A JPEG up to 400px for an image file; 404 for anything else.</summary>
    public Task<byte[]> FamilyFilesThumbnailAsync(string path, CancellationToken ct = default) =>
        GetBytesAsync($"{FamilyFilesBase}/thumbnail/{EscapeLibraryPath(path)}", ct);

    /// <summary>The background job in progress, or null when the server is idle.</summary>
    public async Task<FamilyFilesJob?> FamilyFilesJobAsync(CancellationToken ct = default) =>
        (await GetAsync<FamilyJobResponse>($"{FamilyFilesBase}/job", ct: ct).ConfigureAwait(false)).Job;

    // MARK: - Transfers

    /// <summary>
    /// Streams a file from disk into the library at <paramref name="remotePath"/>. The server never
    /// overwrites: the result says where the file actually landed, and whether it was already
    /// there byte for byte.
    /// </summary>
    /// <param name="modified">
    /// The file's original date - a photo's, not the moment it was copied - so "oldest copy" in the
    /// duplicate check means what it says.
    /// </param>
    /// <param name="progress">Bytes sent so far, reported as the stream is read.</param>
    public async Task<FamilyUploadResult> FamilyFilesUploadAsync(
        string localPath,
        string remotePath,
        DateTimeOffset? modified = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        var query = modified is { } stamp
            ? new[] { new KeyValuePair<string, string>("modified", stamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")) }
            : null;

        await using var file = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read, TransferBufferSize, useAsync: true);
        await using Stream body = progress is null ? file : new ProgressStream(file, progress);

        using var content = new StreamContent(body, TransferBufferSize);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // Announced up front rather than chunked, so the server can refuse a file over its limit
        // before the whole thing has crossed the network.
        content.Headers.ContentLength = file.Length;

        using var request = new HttpRequestMessage(
            HttpMethod.Put, RequireUri($"{FamilyFilesBase}/files/{EscapeLibraryPath(remotePath)}", query))
        {
            Content = content,
        };
        var data = await ExecuteAndValidateAsync(request, ct, _organizerHttp).ConfigureAwait(false);
        return Deserialize<FamilyUploadResult>(data, $"{FamilyFilesBase}/files");
    }

    /// <summary>
    /// Streams one library file to <paramref name="localPath"/>, creating the folder it goes in.
    /// Written beside its final name and moved into place at the end, so a download that dies
    /// halfway never leaves a truncated file looking like the real one.
    /// </summary>
    public async Task FamilyFilesDownloadAsync(
        string remotePath,
        string localPath,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, RequireUri($"{FamilyFilesBase}/files/{EscapeLibraryPath(remotePath)}"));
        _log.Log($"GET {request.RequestUri}");

        using var response = await _organizerHttp
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            _log.Log($"GET {(int)response.StatusCode} for {remotePath}", DiagnosticLevel.Notice);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SentinelAuthenticationRequiredException();
            throw new SentinelServerException((int)response.StatusCode, ExtractErrorMessage(data));
        }

        if (Path.GetDirectoryName(localPath) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        var partial = localPath + ".part";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(
                             partial, FileMode.Create, FileAccess.Write, FileShare.None, TransferBufferSize, useAsync: true))
            {
                var buffer = new byte[TransferBufferSize];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
            }
            File.Move(partial, localPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception) { /* nothing to tidy */ }
            throw;
        }
    }

    // MARK: - Changing the library

    public Task<FamilyPathResult> FamilyFilesCreateFolderAsync(string path, CancellationToken ct = default) =>
        SendAsync<FamilyPathResult, FamilyFolderRequest>(
            $"{FamilyFilesBase}/folders", HttpMethod.Post, new FamilyFolderRequest { Path = path }, ct);

    /// <summary>Renames or moves a file or a whole folder.</summary>
    public Task<FamilyPathResult> FamilyFilesMoveAsync(string from, string to, CancellationToken ct = default) =>
        SendAsync<FamilyPathResult, FamilyMoveRequest>(
            $"{FamilyFilesBase}/move", HttpMethod.Post, new FamilyMoveRequest { From = from, To = to }, ct);

    /// <summary>To the trash, not gone: a file or a whole folder, as one batch that can be put back.</summary>
    public Task<FamilyDeleteResult> FamilyFilesDeleteAsync(string path, CancellationToken ct = default) =>
        SendAsync<FamilyDeleteResult, object>(
            $"{FamilyFilesBase}/files/{EscapeLibraryPath(path)}", HttpMethod.Delete, null, ct);

    // MARK: - Trash

    public async Task<List<FamilyTrashBatch>> FamilyFilesTrashAsync(CancellationToken ct = default) =>
        (await GetAsync<FamilyTrashListing>($"{FamilyFilesBase}/trash", ct: ct).ConfigureAwait(false)).Batches;

    /// <summary>A whole batch, or one file of it when <paramref name="path"/> is given.</summary>
    public Task<FamilyRestoreResult> FamilyFilesRestoreTrashAsync(
        string batchId, string? path = null, CancellationToken ct = default) =>
        SendAsync<FamilyRestoreResult, FamilyTrashRestoreRequest>(
            $"{FamilyFilesBase}/trash/restore",
            HttpMethod.Post,
            new FamilyTrashRestoreRequest { BatchId = batchId, Path = path },
            ct);

    /// <summary>Admin. Empties the trash for good.</summary>
    public Task<FamilyEmptyTrashResult> FamilyFilesEmptyTrashAsync(CancellationToken ct = default) =>
        SendAsync<FamilyEmptyTrashResult, object>($"{FamilyFilesBase}/trash", HttpMethod.Delete, null, ct);

    // MARK: - Backup drives (admin)

    public async Task<List<FamilyDrive>> FamilyFilesDrivesAsync(CancellationToken ct = default) =>
        (await GetAsync<FamilyDrivesListing>($"{FamilyFilesBase}/drives", ct: ct).ConfigureAwait(false)).Drives;

    public Task<FamilyDrive> FamilyFilesCreateDriveAsync(FamilyDriveInput input, CancellationToken ct = default) =>
        SendAsync<FamilyDrive, FamilyDriveInput>($"{FamilyFilesBase}/drives", HttpMethod.Post, input, ct);

    public Task<FamilyDrive> FamilyFilesUpdateDriveAsync(
        string id, FamilyDriveInput input, CancellationToken ct = default) =>
        SendAsync<FamilyDrive, FamilyDriveInput>(
            $"{FamilyFilesBase}/drives/{Uri.EscapeDataString(id)}", HttpMethod.Put, input, ct);

    /// <summary>Removes the drive from the list. Nothing on the drive itself is touched.</summary>
    public Task<FamilyDriveRemoveResult> FamilyFilesDeleteDriveAsync(string id, CancellationToken ct = default) =>
        SendAsync<FamilyDriveRemoveResult, object>(
            $"{FamilyFilesBase}/drives/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);

    /// <summary>
    /// Tests the form as typed, before it is saved. A failed test is an answer, not an exception:
    /// the 400 carries the reason and, for a host-key mismatch, the fingerprint the server saw, and
    /// the edit form needs both. Only a session or transport failure throws.
    /// </summary>
    public async Task<FamilyDriveTestResult> FamilyFilesTestDriveAsync(
        FamilyDriveInput input, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RequireUri($"{FamilyFilesBase}/drives/test"))
        {
            Content = new StringContent(JsonSerializer.Serialize(input, JsonOptions), System.Text.Encoding.UTF8, "application/json"),
        };
        _log.Log($"POST {request.RequestUri}");

        // The caller's token is the only limit: an SFTP connect to a box that drops packets can sit
        // in TCP for longer than the ordinary 15 seconds, and "timed out" is a worse answer than
        // the server's own "could not connect to host".
        using var response = await _organizerHttp.SendAsync(request, ct).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        _log.Log($"POST {(int)response.StatusCode} for drive test");

        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SentinelAuthenticationRequiredException();
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest)
        {
            var decoded = Deserialize<FamilyDriveTestResult>(data, $"{FamilyFilesBase}/drives/test");
            return new FamilyDriveTestResult
            {
                Ok = response.IsSuccessStatusCode,
                Detail = decoded.Detail,
                Error = decoded.Error,
                HostKeyFingerprint = decoded.HostKeyFingerprint,
            };
        }
        throw new SentinelServerException((int)response.StatusCode, ExtractErrorMessage(data));
    }

    /// <summary>Starts a backup to one drive in the background (202), or 409 while another job runs.</summary>
    public Task<FamilyJobStart> FamilyFilesSyncDriveAsync(string id, CancellationToken ct = default) =>
        SendAsync<FamilyJobStart, object>(
            $"{FamilyFilesBase}/drives/{Uri.EscapeDataString(id)}/sync", HttpMethod.Post, null, ct);

    /// <summary>Copies anything on the drive that the library lacks back into the library.</summary>
    public Task<FamilyJobStart> FamilyFilesRestoreDriveAsync(string id, CancellationToken ct = default) =>
        SendAsync<FamilyJobStart, object>(
            $"{FamilyFilesBase}/drives/{Uri.EscapeDataString(id)}/restore", HttpMethod.Post, null, ct);

    /// <summary>Every enabled drive, one after another. 400 when there are no drives.</summary>
    public Task<FamilyJobStart> FamilyFilesSyncAllAsync(CancellationToken ct = default) =>
        SendAsync<FamilyJobStart, object>($"{FamilyFilesBase}/sync", HttpMethod.Post, null, ct);

    // MARK: - Duplicates and the nightly pass (admin)

    /// <param name="remove">True moves every extra copy to the trash; false only reports.</param>
    public Task<FamilyJobStart> FamilyFilesCheckDuplicatesAsync(bool remove, CancellationToken ct = default) =>
        SendAsync<FamilyJobStart, object>(
            $"{FamilyFilesBase}/duplicates/check?remove={(remove ? "true" : "false")}", HttpMethod.Post, null, ct);

    public async Task<FamilyDuplicateReport?> FamilyFilesDuplicateReportAsync(CancellationToken ct = default) =>
        (await GetAsync<FamilyDuplicateReportResponse>($"{FamilyFilesBase}/duplicates", ct: ct).ConfigureAwait(false)).Report;

    public Task<FamilyJobStart> FamilyFilesRunNightlyAsync(CancellationToken ct = default) =>
        SendAsync<FamilyJobStart, object>($"{FamilyFilesBase}/nightly/run", HttpMethod.Post, null, ct);

    // MARK: - Plumbing

    private const int TransferBufferSize = 128 * 1024;

    /// <summary>
    /// A library path as a URL path: each segment escaped on its own, the slashes kept. Escaped
    /// with EscapeDataString rather than left to UriBuilder because the two disagree on '#', '?'
    /// and '+', and a file called "Q&A #2.pdf" has to arrive as exactly that.
    /// </summary>
    internal static string EscapeLibraryPath(string path) =>
        string.Join("/", path.Trim('/').Split('/').Select(Uri.EscapeDataString));

    /// <summary>Reports how much of the wrapped stream has been read - the upload's progress.</summary>
    private sealed class ProgressStream(Stream inner, IProgress<long> progress) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Advance(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Advance(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Advance(int read)
        {
            if (read <= 0) return;
            _read += read;
            progress.Report(_read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // The FileStream is disposed by the caller's own `await using`; this wrapper owns nothing.
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}
