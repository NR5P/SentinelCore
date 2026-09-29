using System.Globalization;
using System.Net.Http;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Networking;

/// <summary>
/// Movie downloads, "+ Add" on the Movies tab: find a title on TMDB, search the household's
/// torrent sites for it, and have the server download the chosen release through its VPN and file
/// it into the library. The contract is core/docs/MOVIE_DOWNLOADS.md.
/// </summary>
/// <remarks>
/// Everything but <see cref="MediaDownloadStatusAsync"/> answers 404 while the feature is off and
/// 403 to a non-admin when only admins may add; both arrive as a <see cref="SentinelServerException"/>
/// carrying the server's sentence. The sites routes are admin only and start the VPN, so they may
/// wait up to ~25 s on the server before answering 503 with <see cref="SentinelServerException.Starting"/>
/// set - they ride the no-timeout session with a limit of their own, because the usual 15-second
/// one would give up before the server does.
/// </remarks>
public sealed partial class SentinelClient
{
    private const string DownloadsBase = "/api/media/downloads";

    // Longer than the server's 25-second wait for the VPN, so its 503 arrives rather than a timeout.
    private static readonly TimeSpan DownloadSitesTimeout = TimeSpan.FromSeconds(45);

    // Adding a site has Prowlarr test it first, and a grab fetches the torrent from the site, both
    // through the VPN: slower than an ordinary request, so they get more room.
    private static readonly TimeSpan DownloadSlowTimeout = TimeSpan.FromSeconds(60);

    // MARK: - Status

    /// <summary>
    /// Whether to show "+ Add", and the VPN's state. A server older than the feature answers 404,
    /// which comes back as <see cref="MediaDownloadStatus.Off"/> rather than an error.
    /// </summary>
    public async Task<MediaDownloadStatus> MediaDownloadStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<MediaDownloadStatus>($"{DownloadsBase}/status", ct: ct).ConfigureAwait(false);
        }
        catch (SentinelServerException error) when (error.Status == 404)
        {
            return MediaDownloadStatus.Off;
        }
    }

    // MARK: - Finding the title

    /// <summary>TMDB matches for what was typed. <paramref name="kind"/> is movie or show.</summary>
    public Task<MediaDownloadLookup> LookupMediaDownloadAsync(string query, string kind, CancellationToken ct = default) =>
        GetAsync<MediaDownloadLookup>(
            $"{DownloadsBase}/lookup",
            new[]
            {
                new KeyValuePair<string, string>("q", query.Trim()),
                new KeyValuePair<string, string>("kind", kind),
            },
            ct);

    /// <summary>A show's seasons and episodes, each marked with whether the library has it.</summary>
    public Task<MediaDownloadShow> MediaDownloadShowAsync(long tmdbId, CancellationToken ct = default) =>
        GetAsync<MediaDownloadShow>($"{DownloadsBase}/show/{tmdbId.ToString(CultureInfo.InvariantCulture)}", ct: ct);

    // MARK: - Searching the sites

    /// <summary>Starts a search (202). Poll <see cref="MediaDownloadSearchAsync"/> every second until it is finished.</summary>
    public Task<MediaDownloadSearch> StartMediaDownloadSearchAsync(MediaDownloadSearchInput input, CancellationToken ct = default) =>
        SendAsync<MediaDownloadSearch, MediaDownloadSearchInput>($"{DownloadsBase}/search", HttpMethod.Post, input, ct);

    /// <summary>A search's progress and, once done, its releases. 404 once it has expired (30 minutes).</summary>
    public Task<MediaDownloadSearch> MediaDownloadSearchAsync(string searchId, CancellationToken ct = default) =>
        GetAsync<MediaDownloadSearch>($"{DownloadsBase}/search/{Uri.EscapeDataString(searchId)}", ct: ct);

    // MARK: - The queue

    public Task<MediaDownloadList> MediaDownloadsAsync(CancellationToken ct = default) =>
        GetAsync<MediaDownloadList>(DownloadsBase, ct: ct);

    /// <summary>
    /// Downloads one release of a search (201). No library means the first of the right kind on the
    /// server's own disks; 400 with a sentence when there is none, or the search has expired.
    /// </summary>
    public Task<MediaDownload> StartMediaDownloadAsync(
        string searchId, string releaseId, string? libraryId = null, CancellationToken ct = default) =>
        SlowSendAsync<MediaDownload, MediaDownloadGrabInput>(
            DownloadsBase,
            HttpMethod.Post,
            new MediaDownloadGrabInput { SearchId = searchId, ReleaseId = releaseId, LibraryId = libraryId },
            DownloadSlowTimeout,
            ct);

    public Task<MediaDownload> MediaDownloadAsync(string id, CancellationToken ct = default) =>
        GetAsync<MediaDownload>($"{DownloadsBase}/{Uri.EscapeDataString(id)}", ct: ct);

    /// <summary>Cancels an active download (its files are deleted) or removes a finished one from the list.</summary>
    public Task CancelMediaDownloadAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{DownloadsBase}/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);

    /// <summary>Puts a failed download back in the queue.</summary>
    public Task<MediaDownload> RetryMediaDownloadAsync(string id, CancellationToken ct = default) =>
        SendAsync<MediaDownload, object>($"{DownloadsBase}/{Uri.EscapeDataString(id)}/retry", HttpMethod.Post, null, ct);

    // MARK: - Sites (admin)

    public Task<List<MediaDownloadSite>> MediaDownloadSitesAsync(CancellationToken ct = default) =>
        SlowSendAsync<List<MediaDownloadSite>, object>($"{DownloadsBase}/sites", HttpMethod.Get, null, DownloadSitesTimeout, ct);

    /// <summary>The sites that can be added, public ones first. A blank query is the whole catalog.</summary>
    public Task<List<MediaDownloadSiteDefinition>> MediaDownloadSiteCatalogAsync(string? query, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString(query?.Trim() ?? "");
        return SlowSendAsync<List<MediaDownloadSiteDefinition>, object>(
            $"{DownloadsBase}/sites/catalog?q={q}", HttpMethod.Get, null, DownloadSitesTimeout, ct);
    }

    /// <summary>Prowlarr tests the site before saving it: a site that is down, or a wrong login, is a 400 with the reason.</summary>
    public Task<MediaDownloadSite> AddMediaDownloadSiteAsync(MediaDownloadSiteInput input, CancellationToken ct = default) =>
        SlowSendAsync<MediaDownloadSite, MediaDownloadSiteInput>(
            $"{DownloadsBase}/sites", HttpMethod.Post, input, DownloadSlowTimeout, ct);

    public Task SetMediaDownloadSiteEnabledAsync(int id, bool enabled, CancellationToken ct = default) =>
        SlowSendAsync<object, MediaDownloadSiteUpdate>(
            $"{DownloadsBase}/sites/{id.ToString(CultureInfo.InvariantCulture)}",
            HttpMethod.Put,
            new MediaDownloadSiteUpdate { Enabled = enabled },
            DownloadSitesTimeout,
            ct,
            expectBody: false);

    public Task DeleteMediaDownloadSiteAsync(int id, CancellationToken ct = default) =>
        SlowSendAsync<object, object>(
            $"{DownloadsBase}/sites/{id.ToString(CultureInfo.InvariantCulture)}",
            HttpMethod.Delete,
            null,
            DownloadSitesTimeout,
            ct,
            expectBody: false);

    /// <summary>
    /// A request on the no-timeout session with its own limit. Running out of time surfaces as an
    /// OperationCanceledException, which <c>AppModel.UserFacingMessage</c> words as "didn't respond in time".
    /// </summary>
    private async Task<TResult> SlowSendAsync<TResult, TBody>(
        string path, HttpMethod method, TBody? body, TimeSpan timeout, CancellationToken ct, bool expectBody = true)
    {
        using var request = new HttpRequestMessage(method, RequireUri(path));
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            var json = body is null ? "" : System.Text.Json.JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var data = await ExecuteAndValidateAsync(request, limit.Token, _organizerHttp).ConfigureAwait(false);
        return expectBody ? Deserialize<TResult>(data, path) : default!;
    }
}
