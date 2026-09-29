using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace SentinelCore.Web;

// Prowlarr (prowlarr.com) keeps the list of torrent sites and knows how to search each one - the
// part that breaks every few weeks when a site changes, and that Prowlarr's maintainers keep
// fixed. This server only uses its API: search, fetch a release, and the site list behind
// Movies -> Downloads -> Sites. The API key is read from Prowlarr's own config.xml when Settings
// leaves it blank, which is how install-downloads.sh leaves it.
public sealed class ProwlarrClient(IHttpClientFactory httpClientFactory, AppSettingsFile settings, IConfiguration configuration)
{
    public const string HttpClientName = "Prowlarr";
    // Newznab categories: 2000 Movies, 5000 TV.
    public const int MovieCategory = 2000;
    public const int TvCategory = 5000;

    public sealed record Release(
        string Guid, string Title, string? Indexer, long? Size, int? Seeders, int? Leechers, DateTimeOffset? PublishDate,
        string? DownloadUrl, string? MagnetUrl, string? InfoHash, string Protocol);

    public sealed record Site(int Id, string Name, bool Enabled, string? Privacy, string? Language, string? Description, string? Definition);

    public sealed record SiteField(string Name, string Label, string Type, string? HelpText, JsonNode? Value, IReadOnlyList<SettingsFieldOption>? Options, bool Advanced);

    public sealed record SiteDefinition(string Definition, string Name, string? Description, string? Privacy, string? Language,
        IReadOnlyList<SiteField> Fields, JsonObject Schema);

    public async Task<string> VersionAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("api/v1/system/status", cancellationToken);
        return document.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
    }

    public async Task<IReadOnlyList<Release>> SearchAsync(string query, int category, CancellationToken cancellationToken)
    {
        var url = $"api/v1/search?type=search&limit=100&categories={category}&query={Uri.EscapeDataString(query)}";
        using var document = await GetJsonAsync(url, cancellationToken, TimeSpan.FromSeconds(90));
        var list = new List<Release>();
        foreach (var r in document.RootElement.EnumerateArray())
        {
            var protocol = Str(r, "protocol") ?? "torrent";
            if (!protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new Release(
                Str(r, "guid") ?? Guid.NewGuid().ToString("N"),
                Str(r, "title") ?? "",
                Str(r, "indexer"),
                Long(r, "size"),
                (int?)Long(r, "seeders"),
                (int?)Long(r, "leechers"),
                r.TryGetProperty("publishDate", out var date) && date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out var d) ? d : null,
                Str(r, "downloadUrl"),
                Str(r, "magnetUrl"),
                Str(r, "infoHash"),
                protocol));
        }
        return list;
    }

    // What qBittorrent should be given for a release: its magnet link, or the .torrent file.
    // Prowlarr's download URL goes through Prowlarr (and so through the VPN) and answers either
    // with the file or with a redirect to a magnet link.
    public async Task<(string? Magnet, byte[]? Torrent)> FetchReleaseAsync(string source, CancellationToken cancellationToken)
    {
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return (source, null);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = source;
        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location.OriginalString : new Uri(new Uri(url), location).ToString();
                if (next.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return (next, null);
                url = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new ProwlarrException($"The site would not hand over that release ({(int)response.StatusCode}). Try another one.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            // A bencoded dictionary starts with "d"; anything else is an error page.
            if (bytes.Length == 0 || bytes[0] != (byte)'d')
                throw new ProwlarrException("The site sent back something that is not a torrent. Try another release.");
            return (null, bytes);
        }
        throw new ProwlarrException("The site kept redirecting. Try another release.");
    }

    // MARK: - Sites

    public async Task<IReadOnlyList<Site>> SitesAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("api/v1/indexer", cancellationToken);
        return document.RootElement.EnumerateArray()
            .Select(i => new Site(
                (int)(Long(i, "id") ?? 0), Str(i, "name") ?? "", i.TryGetProperty("enable", out var e) && e.ValueKind == JsonValueKind.True,
                Str(i, "privacy"), Str(i, "language"), Str(i, "description"), Str(i, "definitionName")))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Every site Prowlarr knows how to add. A few hundred entries and slow to build, so it is
    // cached for the hour.
    private (DateTimeOffset At, List<SiteDefinition> List)? _catalog;

    public async Task<IReadOnlyList<SiteDefinition>> CatalogAsync(CancellationToken cancellationToken)
    {
        if (_catalog is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromHours(1)) return cached.List;
        using var document = await GetJsonAsync("api/v1/indexer/schema", cancellationToken, TimeSpan.FromSeconds(60));
        var list = new List<SiteDefinition>();
        foreach (var schema in document.RootElement.EnumerateArray())
        {
            if (!(Str(schema, "protocol") ?? "torrent").Equals("torrent", StringComparison.OrdinalIgnoreCase)) continue;
            var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
            var definition = Str(schema, "definitionName") ?? Str(schema, "implementation") ?? "";
            if (definition.Length == 0) continue;
            list.Add(new SiteDefinition(definition, Str(schema, "name") ?? definition, Str(schema, "description"),
                Str(schema, "privacy"), Str(schema, "language"), Fields(schema, SiteUrls(schema)), node));
        }
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        _catalog = (DateTimeOffset.UtcNow, list);
        return list;
    }

    // Adds a site from the catalog. Prowlarr tests it before saving, so a site that is down, or a
    // wrong login, comes back as an error here rather than as a site that never finds anything.
    public async Task<Site> AddSiteAsync(string definitionName, string? name, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken)
    {
        var catalog = await CatalogAsync(cancellationToken);
        var definition = catalog.FirstOrDefault(d => string.Equals(d.Definition, definitionName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProwlarrException("Prowlarr does not know that site any more. Refresh the list and pick it again.");

        var body = definition.Schema.DeepClone().AsObject();
        body["name"] = string.IsNullOrWhiteSpace(name) ? definition.Name : name.Trim();
        body["enable"] = true;
        body["appProfileId"] = await AppProfileIdAsync(cancellationToken);
        body.Remove("id");
        var urls = definition.Schema["indexerUrls"] is JsonArray list ? list.Select(u => u?.GetValue<string>()).OfType<string>().ToList() : [];
        if (body["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>())
            {
                var fieldName = field["name"]?.GetValue<string>();
                if (fieldName is not null && values.TryGetValue(fieldName, out var value))
                    field["value"] = JsonNode.Parse(value.GetRawText());
                // Prowlarr leaves a site's address blank in the schema and keeps the choices in
                // indexerUrls; its own web page quietly picks the first. Without one the site
                // test fails with "Invalid URI: The URI is empty".
                if (fieldName == "baseUrl" && string.IsNullOrWhiteSpace(field["value"]?.ToString()) && urls.Count > 0)
                    field["value"] = urls[0];
            }

        using var response = await SendAsync(HttpMethod.Post, "api/v1/indexer", body, cancellationToken, TimeSpan.FromSeconds(90));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var i = document.RootElement;
        return new Site((int)(Long(i, "id") ?? 0), Str(i, "name") ?? "", true, Str(i, "privacy"), Str(i, "language"), Str(i, "description"), Str(i, "definitionName"));
    }

    public async Task SetSiteEnabledAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        using var current = await GetJsonAsync($"api/v1/indexer/{id}", cancellationToken);
        var body = JsonNode.Parse(current.RootElement.GetRawText())!.AsObject();
        body["enable"] = enabled;
        using var response = await SendAsync(HttpMethod.Put, $"api/v1/indexer/{id}", body, cancellationToken, TimeSpan.FromSeconds(90));
    }

    public async Task DeleteSiteAsync(int id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"api/v1/indexer/{id}", null, cancellationToken);
    }

    private async Task<int> AppProfileIdAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("api/v1/appprofile", cancellationToken);
        foreach (var profile in document.RootElement.EnumerateArray())
            if (Long(profile, "id") is { } id) return (int)id;
        return 1;
    }

    // The fields a person has to fill in: the ones with no sensible default (a login, a cookie)
    // plus the choices a site offers. Prowlarr's own bookkeeping fields are left out.
    private static List<string> SiteUrls(JsonElement schema) =>
        schema.TryGetProperty("indexerUrls", out var urls) && urls.ValueKind == JsonValueKind.Array
            ? urls.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToList()
            : [];

    // A field a site genuinely needs filled in before it works: a login, a cookie, a key. Most
    // text fields (an uploader filter, a seed ratio) are optional, and a site with only those is
    // added with one tap.
    public static bool IsCredential(string fieldName) =>
        System.Text.RegularExpressions.Regex.IsMatch(fieldName, "user|pass|cookie|key|token|email|login|uid|pid|2fa|captcha|auth", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static List<SiteField> Fields(JsonElement schema, List<string> siteUrls)
    {
        var list = new List<SiteField>();
        if (!schema.TryGetProperty("fields", out var fields)) return list;
        foreach (var f in fields.EnumerateArray())
        {
            var type = Str(f, "type") ?? "textbox";
            var name = Str(f, "name") ?? "";
            if (name.Length == 0 || type is "info" or "hidden" or "tagSelect" or "captcha" or "cardigannCaptcha") continue;
            if (name is "definitionFile" or "baseSettings.limitsUnit" or "torrentBaseSettings.preferMagnetUrl") continue;
            var advanced = f.TryGetProperty("advanced", out var a) && a.ValueKind == JsonValueKind.True;
            if (advanced && !name.StartsWith("baseUrl", StringComparison.Ordinal)) continue;
            List<SettingsFieldOption>? options = null;
            if (f.TryGetProperty("selectOptions", out var select) && select.ValueKind == JsonValueKind.Array)
                options = select.EnumerateArray()
                    .Select(o => new SettingsFieldOption(o.TryGetProperty("value", out var v) ? v.ToString() : "", Str(o, "name") ?? ""))
                    .ToList();
            var fieldValue = f.TryGetProperty("value", out var value) && value.ValueKind != JsonValueKind.Null ? JsonNode.Parse(value.GetRawText()) : null;
            // The site's addresses are not in the field's own options (Prowlarr fetches them
            // with a separate call); offer them, starting on the first as Prowlarr's page does.
            if (name == "baseUrl" && siteUrls.Count > 0)
            {
                options = siteUrls.Select(u => new SettingsFieldOption(u, u)).ToList();
                fieldValue ??= JsonValue.Create(siteUrls[0]);
            }
            list.Add(new SiteField(name, Str(f, "label") ?? name, type switch
            {
                "password" => "password",
                "checkbox" => "checkbox",
                "select" => "select",
                "number" => "number",
                _ => "text"
            }, Str(f, "helpText"), fieldValue, options, advanced));
        }
        return list;
    }

    // MARK: - Transport

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken, timeout);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var options = await MediaDownloadManager.ReadOptionsAsync(settings, cancellationToken);
        var baseUri = BaseUri(options.ProwlarrUrl);
        var key = ApiKey(options);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Add("X-Api-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, timer.Token); }
        catch (HttpRequestException ex) { throw new ProwlarrException($"Prowlarr did not answer at {baseUri}: {ex.Message}", ex); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw new ProwlarrException("Prowlarr took too long to answer. A site may be slow; try again.", ex); }

        if (response.IsSuccessStatusCode) return response;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;
        response.Dispose();
        if (status == HttpStatusCode.Unauthorized)
            throw new ProwlarrException("Prowlarr rejected the API key. Check it under Settings -> MOVIE DOWNLOADS, or leave it blank to read it from Prowlarr.");
        throw new ProwlarrException(ErrorMessage(text) ?? $"Prowlarr answered {(int)status}.");
    }

    // Prowlarr's validation errors are an array of { propertyName, errorMessage }; anything else
    // is usually { message }.
    private static string? ErrorMessage(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var messages = root.EnumerateArray().Select(e => Str(e, "errorMessage")).OfType<string>().Distinct().ToList();
                return messages.Count > 0 ? string.Join(" ", messages) : null;
            }
            return Str(root, "message") ?? Str(root, "description");
        }
        catch (JsonException) { return text.Length is > 0 and < 300 ? text : null; }
    }

    private string ApiKey(MediaDownloadsSettingsDto options)
    {
        if (!string.IsNullOrWhiteSpace(options.ProwlarrApiKey)) return options.ProwlarrApiKey.Trim();
        var path = configuration["MediaDownloads:ProwlarrConfigPath"] ?? "/var/lib/sentinelcore-downloads/prowlarr/config.xml";
        try
        {
            if (File.Exists(path) && XDocument.Load(path).Root?.Element("ApiKey")?.Value is { Length: > 0 } key) return key.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
        throw new ProwlarrException("No Prowlarr API key. Run install-downloads.sh on the server, or paste the key from Prowlarr (Settings -> General) under Settings -> MOVIE DOWNLOADS.");
    }

    private static Uri BaseUri(string url)
    {
        var text = string.IsNullOrWhiteSpace(url) ? "http://10.250.0.2:9696" : url.Trim();
        if (!text.EndsWith('/')) text += "/";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri
            : throw new ProwlarrException($"\"{url}\" is not a web address. Fix Prowlarr's address under Settings -> MOVIE DOWNLOADS.");
    }

    private static string? Str(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}

public sealed class ProwlarrException(string message, Exception? inner = null) : Exception(message, inner);
