using System.Net.Http;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Networking;

/// <summary>
/// "Import a list" on Music and Movies: the client reads the chosen files as text and the server
/// parses them, shows what it found, and once started works through the list in the background.
/// The contract is core/docs/MOVIE_DOWNLOADS.md "Import a list (Music and Movies)".
/// </summary>
/// <remarks>
/// Behind the same "downloads on and allowed" check as the rest of each tab's downloads, so a 404 or
/// 403 arrives as a <see cref="SentinelServerException"/> with the server's sentence. A preview of
/// a big Spotify library is a few MB to upload and parse, so it gets more room than the usual
/// 15 seconds.
/// </remarks>
public sealed partial class SentinelClient
{
    private static readonly TimeSpan ImportPreviewTimeout = TimeSpan.FromSeconds(90);

    /// <summary>/api/music/downloads or /api/media/downloads.</summary>
    public static string ImportBase(MediaImportKind kind) =>
        kind == MediaImportKind.Music ? "/api/music/downloads" : DownloadsBase;

    private static string ImportPath(MediaImportKind kind, string id) =>
        $"{ImportBase(kind)}/imports/{Uri.EscapeDataString(id)}";

    /// <summary>What the server finds in the files. Nothing is saved.</summary>
    public Task<MediaImportPreview> PreviewMediaImportAsync(
        MediaImportKind kind, IEnumerable<MediaImportFile> files, CancellationToken ct = default) =>
        SlowSendAsync<MediaImportPreview, MediaImportPreviewInput>(
            $"{ImportBase(kind)}/import/preview",
            HttpMethod.Post,
            new MediaImportPreviewInput { Files = files.ToList() },
            ImportPreviewTimeout,
            ct);

    /// <summary>Starts working through the chosen items (201). Duplicates are dropped; at most 5000.</summary>
    public Task<MediaImportJob> StartMediaImportAsync(MediaImportKind kind, MediaImportStartInput input, CancellationToken ct = default) =>
        SendAsync<MediaImportJob, MediaImportStartInput>($"{ImportBase(kind)}/imports", HttpMethod.Post, input, ct);

    /// <summary>The lists this user may see (admins: everyone's), newest first, without their items.</summary>
    public async Task<List<MediaImportJob>> MediaImportsAsync(MediaImportKind kind, CancellationToken ct = default) =>
        (await GetAsync<MediaImportList>($"{ImportBase(kind)}/imports", ct: ct).ConfigureAwait(false)).Imports;

    /// <summary>One list with its items.</summary>
    public Task<MediaImportJob> MediaImportAsync(MediaImportKind kind, string id, CancellationToken ct = default) =>
        GetAsync<MediaImportJob>(ImportPath(kind, id), ct: ct);

    public Task<MediaImportJob> PauseMediaImportAsync(MediaImportKind kind, string id, bool paused, CancellationToken ct = default) =>
        SendAsync<MediaImportJob, MediaImportPauseInput>(
            $"{ImportPath(kind, id)}/pause", HttpMethod.Post, new MediaImportPauseInput { Paused = paused }, ct);

    /// <summary>Everything not found or failed is searched for again (and the list is resumed).</summary>
    public Task<MediaImportJob> RetryMediaImportAsync(MediaImportKind kind, string id, CancellationToken ct = default) =>
        SendAsync<MediaImportJob, object>($"{ImportPath(kind, id)}/retry", HttpMethod.Post, null, ct);

    /// <summary>Leaves one album or film out (it becomes canceled).</summary>
    public Task<MediaImportJob> LeaveOutMediaImportItemAsync(MediaImportKind kind, string id, string itemId, CancellationToken ct = default) =>
        SendAsync<MediaImportJob, object>($"{ImportPath(kind, id)}/items/{Uri.EscapeDataString(itemId)}", HttpMethod.Delete, null, ct);

    /// <summary>Stops and forgets the list (204). Downloads it already queued carry on.</summary>
    public Task DeleteMediaImportAsync(MediaImportKind kind, string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>(ImportPath(kind, id), HttpMethod.Delete, null, ct);
}
