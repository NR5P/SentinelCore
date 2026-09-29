using System.Net;
using System.Text.Json;

namespace SentinelCore.Web;

// qBittorrent's Web API (v2), the part of it downloads need: add a torrent, watch it, list its
// files, remove it. install-downloads.sh runs qbittorrent-nox inside the VPN namespace with login
// skipped for the one address this server reaches it from, so a blank username is normal; a
// qBittorrent someone runs themselves can have a username and password set in Settings instead.
public sealed class QbittorrentClient(IHttpClientFactory httpClientFactory, AppSettingsFile settings)
{
    public const string HttpClientName = "Qbittorrent";
    public const string Category = "sentinelcore";

    public sealed record Torrent(
        string Hash, string Name, string State, double Progress, long Size, long Downloaded, long DownloadSpeed,
        long? EtaSeconds, int Seeds, int Peers, DateTimeOffset? CompletedAt, string SavePath, string? ContentPath, string Tags);

    public sealed record TorrentFile(string Name, long Size, double Progress);

    private string? _cookie;

    public async Task<string> VersionAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/v2/app/version"), cancellationToken);
        return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
    }

    // Adds a magnet link or a .torrent file, tagged so the download can find it again.
    public async Task AddAsync(string? magnet, byte[]? torrentFile, string savePath, string tag, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            if (magnet is not null) form.Add(new StringContent(magnet), "urls");
            if (torrentFile is not null)
            {
                var file = new ByteArrayContent(torrentFile);
                file.Headers.ContentType = new("application/x-bittorrent");
                form.Add(file, "torrents", "release.torrent");
            }
            form.Add(new StringContent(savePath), "savepath");
            form.Add(new StringContent(Category), "category");
            form.Add(new StringContent(tag), "tags");
            // Files are moved into the library afterwards; a single root folder keeps the pieces
            // of one release together until then.
            form.Add(new StringContent("Original"), "contentLayout");
            return new HttpRequestMessage(HttpMethod.Post, "api/v2/torrents/add") { Content = form };
        }, cancellationToken);
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (body.StartsWith("Fails", StringComparison.OrdinalIgnoreCase))
            throw new QbittorrentException("qBittorrent refused the torrent. It may already be in its list, or the link is broken.");
    }

    public async Task<IReadOnlyList<Torrent>> ListAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/v2/torrents/info?category={Category}"), cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var list = new List<Torrent>();
        foreach (var t in document.RootElement.EnumerateArray())
        {
            var eta = Long(t, "eta");
            var completion = Long(t, "completion_on");
            list.Add(new Torrent(
                Str(t, "hash") ?? "",
                Str(t, "name") ?? "",
                Str(t, "state") ?? "unknown",
                t.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0,
                Long(t, "total_size") ?? Long(t, "size") ?? 0,
                Long(t, "downloaded") ?? 0,
                Long(t, "dlspeed") ?? 0,
                // 8640000 is qBittorrent's "infinity".
                eta is >= 0 and < 8640000 ? eta : null,
                (int)(Long(t, "num_seeds") ?? 0),
                (int)(Long(t, "num_leechs") ?? 0),
                completion is > 0 ? DateTimeOffset.FromUnixTimeSeconds(completion.Value) : null,
                Str(t, "save_path") ?? "",
                Str(t, "content_path"),
                Str(t, "tags") ?? ""));
        }
        return list;
    }

    public async Task<IReadOnlyList<TorrentFile>> FilesAsync(string hash, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/v2/torrents/files?hash={hash}"), cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.EnumerateArray()
            .Select(f => new TorrentFile(Str(f, "name") ?? "", Long(f, "size") ?? 0,
                f.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0))
            .ToList();
    }

    public async Task DeleteAsync(string hash, bool deleteFiles, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/v2/torrents/delete")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hash, ["deleteFiles"] = deleteFiles ? "true" : "false" })
        }, cancellationToken);
    }

    // Sends with the saved session cookie, signing in first (or again, after qBittorrent forgot
    // the session) when it answers 403.
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        var options = await MediaDownloadManager.ReadOptionsAsync(settings, cancellationToken);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var baseUri = BaseUri(options.QbittorrentUrl);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = build();
            request.RequestUri = new Uri(baseUri, request.RequestUri!.ToString());
            if (_cookie is not null) request.Headers.Add("Cookie", _cookie);
            // qBittorrent refuses API calls whose Referer/Origin is another site; say it is itself.
            request.Headers.Referrer = baseUri;
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, cancellationToken); }
            catch (HttpRequestException ex) { throw new QbittorrentException($"qBittorrent did not answer at {baseUri}: {ex.Message}", ex); }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw new QbittorrentException($"qBittorrent did not answer at {baseUri} in time.", ex); }

            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
            {
                response.Dispose();
                await LoginAsync(client, baseUri, options, cancellationToken);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                throw new QbittorrentException($"qBittorrent answered {(int)response.StatusCode}: {text.Trim()}");
            }
            return response;
        }
        throw new QbittorrentException("qBittorrent refused the sign-in. Check the username and password under Settings -> MOVIE DOWNLOADS.");
    }

    private async Task LoginAsync(HttpClient client, Uri baseUri, MediaDownloadsSettingsDto options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.QbittorrentUsername))
            throw new QbittorrentException("qBittorrent wants a sign-in. Put its Web UI username and password under Settings -> MOVIE DOWNLOADS.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v2/auth/login"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = options.QbittorrentUsername, ["password"] = options.QbittorrentPassword
            })
        };
        request.Headers.Referrer = baseUri;
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || !body.Trim().StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
            throw new QbittorrentException("qBittorrent refused the sign-in. Check the username and password under Settings -> MOVIE DOWNLOADS.");
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            _cookie = cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("SID=", StringComparison.Ordinal)) ?? _cookie;
    }

    private static Uri BaseUri(string url)
    {
        var text = string.IsNullOrWhiteSpace(url) ? "http://10.250.0.2:8080" : url.Trim();
        if (!text.EndsWith('/')) text += "/";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri
            : throw new QbittorrentException($"\"{url}\" is not a web address. Fix qBittorrent's address under Settings -> MOVIE DOWNLOADS.");
    }

    private static string? Str(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}

public sealed class QbittorrentException(string message, Exception? inner = null) : Exception(message, inner);
