using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace SentinelCore.Web;

public sealed record PodcastDirectoryEntry(
    long ItunesId,
    string Title,
    string? Author,
    string? ArtUrl,
    string? FeedUrl,
    string? Genre,
    int? EpisodeCount);

public sealed record PodcastGenre(int Id, string Name);

public sealed class PodcastDirectoryException(string message) : Exception(message);

// Apple's podcast directory: search, lookup by id and the top charts, all public JSON with no key.
// Answers are cached for six hours - the charts move daily at most, and a person paging back and
// forth through search should not cost Apple a request each time.
//
// Top charts come from two places, checked by hand against the live service on 2026-10-02:
// rss.marketingtools.apple.com answers the overall chart (no genres), and the older
// itunes.apple.com/{country}/rss/toppodcasts feed answers per genre. Neither carries the feed
// URL, so the ids are looked up in one batch afterwards.
public sealed class PodcastDirectory(IHttpClientFactory clients, ILogger<PodcastDirectory> logger)
{
    public const string HttpClientName = "PodcastDirectory";
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<PodcastDirectoryEntry> Entries)> _cache = new();

    // Apple's podcast genres (the 2019 set). Ids are what /directory/top?genre= takes.
    public static readonly IReadOnlyList<PodcastGenre> Genres =
    [
        new(1301, "Arts"), new(1321, "Business"), new(1303, "Comedy"), new(1304, "Education"),
        new(1483, "Fiction"), new(1511, "Government"), new(1512, "Health & Fitness"), new(1487, "History"),
        new(1305, "Kids & Family"), new(1502, "Leisure"), new(1310, "Music"), new(1489, "News"),
        new(1314, "Religion & Spirituality"), new(1533, "Science"), new(1324, "Society & Culture"),
        new(1545, "Sports"), new(1318, "Technology"), new(1488, "True Crime"), new(1309, "TV & Film")
    ];

    // Sub-genres a feed's categories may name, folded into their top genre.
    private static readonly Dictionary<string, int> SubGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Daily News"] = 1489, ["Politics"] = 1489, ["News Commentary"] = 1489, ["Business News"] = 1489, ["Tech News"] = 1489,
        ["Sports News"] = 1489, ["Entertainment News"] = 1489, ["Investing"] = 1321, ["Careers"] = 1321, ["Entrepreneurship"] = 1321,
        ["Management"] = 1321, ["Marketing"] = 1321, ["Improv"] = 1303, ["Comedy Interviews"] = 1303, ["Stand-Up"] = 1303,
        ["Courses"] = 1304, ["How To"] = 1304, ["Language Learning"] = 1304, ["Self-Improvement"] = 1304,
        ["Drama"] = 1483, ["Science Fiction"] = 1483, ["Comedy Fiction"] = 1483, ["Mental Health"] = 1512, ["Fitness"] = 1512,
        ["Nutrition"] = 1512, ["Medicine"] = 1512, ["Parenting"] = 1305, ["Hobbies"] = 1502, ["Games"] = 1502, ["Video Games"] = 1502,
        ["Home & Garden"] = 1502, ["Automotive"] = 1502, ["Crafts"] = 1502, ["Music Commentary"] = 1310, ["Music History"] = 1310,
        ["Music Interviews"] = 1310, ["Christianity"] = 1314, ["Spirituality"] = 1314, ["Religion"] = 1314, ["Astronomy"] = 1533,
        ["Natural Sciences"] = 1533, ["Nature"] = 1533, ["Physics"] = 1533, ["Social Sciences"] = 1533, ["Documentary"] = 1324,
        ["Personal Journals"] = 1324, ["Philosophy"] = 1324, ["Relationships"] = 1324, ["Places & Travel"] = 1324,
        ["Football"] = 1545, ["Soccer"] = 1545, ["Basketball"] = 1545, ["Baseball"] = 1545, ["Film Reviews"] = 1309,
        ["TV Reviews"] = 1309, ["After Shows"] = 1309, ["Books"] = 1301, ["Design"] = 1301, ["Food"] = 1301,
        ["Visual Arts"] = 1301, ["Performing Arts"] = 1301
    };

    public static int? GenreIdFor(string category)
    {
        var top = Genres.FirstOrDefault(g => string.Equals(g.Name, category, StringComparison.OrdinalIgnoreCase)
            || string.Equals(g.Name.Replace("&", "and"), category.Replace("&", "and"), StringComparison.OrdinalIgnoreCase));
        if (top is not null) return top.Id;
        return SubGenres.TryGetValue(category, out var id) ? id : null;
    }

    public static string Country(string? country) =>
        country?.Trim().ToLowerInvariant() is { Length: 2 } code && code.All(char.IsAsciiLetterLower) ? code : "us";

    public Task<IReadOnlyList<PodcastDirectoryEntry>> SearchAsync(string term, string country, CancellationToken cancellationToken) =>
        CachedAsync($"search|{country}|{term.Trim().ToLowerInvariant()}", async () =>
        {
            var url = $"https://itunes.apple.com/search?media=podcast&entity=podcast&limit=50&country={Country(country)}&term={Uri.EscapeDataString(term.Trim())}";
            using var document = await GetJsonAsync(url, cancellationToken);
            return ParseSearch(document.RootElement);
        });

    public Task<IReadOnlyList<PodcastDirectoryEntry>> LookupAsync(IReadOnlyCollection<long> ids, string country, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return Task.FromResult<IReadOnlyList<PodcastDirectoryEntry>>([]);
        var list = string.Join(",", ids.Distinct());
        return CachedAsync($"lookup|{country}|{list}", async () =>
        {
            using var document = await GetJsonAsync($"https://itunes.apple.com/lookup?entity=podcast&country={Country(country)}&id={list}", cancellationToken);
            return ParseSearch(document.RootElement);
        });
    }

    public Task<IReadOnlyList<PodcastDirectoryEntry>> TopAsync(string country, int? genre, CancellationToken cancellationToken) =>
        CachedAsync($"top|{country}|{genre}", async () =>
        {
            var code = Country(country);
            IReadOnlyList<PodcastDirectoryEntry> chart;
            if (genre is null)
            {
                try
                {
                    using var document = await GetJsonAsync($"https://rss.marketingtools.apple.com/api/v2/{code}/podcasts/top/50/podcasts.json", cancellationToken);
                    chart = ParseMarketingChart(document.RootElement);
                }
                catch (PodcastDirectoryException)
                {
                    using var document = await GetJsonAsync($"https://itunes.apple.com/{code}/rss/toppodcasts/limit=50/json", cancellationToken);
                    chart = ParseLegacyChart(document.RootElement);
                }
            }
            else
            {
                using var document = await GetJsonAsync($"https://itunes.apple.com/{code}/rss/toppodcasts/limit=50/genre={genre.Value.ToString(CultureInfo.InvariantCulture)}/json", cancellationToken);
                chart = ParseLegacyChart(document.RootElement);
            }

            // The charts carry no feed addresses; one lookup fills them in for all fifty.
            try
            {
                var details = (await LookupAsync(chart.Select(c => c.ItunesId).ToList(), code, cancellationToken)).ToDictionary(d => d.ItunesId);
                return chart.Select(entry => details.TryGetValue(entry.ItunesId, out var detail)
                    ? detail with { Genre = detail.Genre ?? entry.Genre, ArtUrl = detail.ArtUrl ?? entry.ArtUrl }
                    : entry).ToList();
            }
            catch (PodcastDirectoryException ex)
            {
                logger.LogInformation("Apple's lookup for the top chart failed: {Message}", ex.Message);
                return chart;
            }
        });

    private async Task<IReadOnlyList<PodcastDirectoryEntry>> CachedAsync(string key, Func<Task<IReadOnlyList<PodcastDirectoryEntry>>> load)
    {
        if (_cache.TryGetValue(key, out var hit) && hit.At > DateTimeOffset.UtcNow - CacheFor) return hit.Entries;
        var entries = await load();
        _cache[key] = (DateTimeOffset.UtcNow, entries);
        if (_cache.Count > 500)
            foreach (var stale in _cache.Where(e => e.Value.At < DateTimeOffset.UtcNow - CacheFor).Select(e => e.Key).ToList())
                _cache.TryRemove(stale, out _);
        return entries;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await clients.CreateClient(HttpClientName).GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new PodcastDirectoryException($"Apple's podcast directory answered {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new PodcastDirectoryException($"Apple's podcast directory could not be reached: {ex.Message}");
        }
    }

    public static IReadOnlyList<PodcastDirectoryEntry> ParseSearch(JsonElement root)
    {
        var list = new List<PodcastDirectoryEntry>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in results.EnumerateArray())
        {
            var id = Long(item, "collectionId") ?? Long(item, "trackId");
            var title = Str(item, "collectionName") ?? Str(item, "trackName");
            if (id is null || title is null) continue;
            if (Str(item, "kind") is { } kind && kind != "podcast") continue;
            list.Add(new PodcastDirectoryEntry(id.Value, title, Str(item, "artistName"),
                Str(item, "artworkUrl600") ?? Str(item, "artworkUrl100"), Str(item, "feedUrl"), Str(item, "primaryGenreName"),
                Long(item, "trackCount") is { } count ? (int)count : null));
        }
        return list;
    }

    public static IReadOnlyList<PodcastDirectoryEntry> ParseMarketingChart(JsonElement root)
    {
        var list = new List<PodcastDirectoryEntry>();
        if (!root.TryGetProperty("feed", out var feed) || !feed.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in results.EnumerateArray())
        {
            if (!long.TryParse(Str(item, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || Str(item, "name") is not { } name) continue;
            var genre = item.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array && genres.GetArrayLength() > 0
                ? Str(genres[0], "name")
                : null;
            list.Add(new PodcastDirectoryEntry(id, name, Str(item, "artistName"), Bigger(Str(item, "artworkUrl100")), null, genre, null));
        }
        return list;
    }

    public static IReadOnlyList<PodcastDirectoryEntry> ParseLegacyChart(JsonElement root)
    {
        var list = new List<PodcastDirectoryEntry>();
        if (!root.TryGetProperty("feed", out var feed) || !feed.TryGetProperty("entry", out var entries)) return list;
        // A chart of one comes back as an object rather than an array.
        var items = entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray().ToList() : [entries];
        foreach (var item in items)
        {
            var idText = item.TryGetProperty("id", out var idElement) && idElement.TryGetProperty("attributes", out var idAttributes) ? Str(idAttributes, "im:id") : null;
            if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            var name = Label(item, "im:name");
            if (name is null) continue;
            string? art = null;
            if (item.TryGetProperty("im:image", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                art = Bigger(Str(images[images.GetArrayLength() - 1], "label"));
            var genre = item.TryGetProperty("category", out var category) && category.TryGetProperty("attributes", out var attributes) ? Str(attributes, "label") : null;
            list.Add(new PodcastDirectoryEntry(id, name, Label(item, "im:artist"), art, null, genre, null));
        }
        return list;
    }

    // Apple's art URLs end in the size; asking for 600 gets the size every other list uses.
    private static string? Bigger(string? url) =>
        url is null ? null : System.Text.RegularExpressions.Regex.Replace(url, @"/\d+x\d+bb\.(png|jpg)$", "/600x600bb.$1");

    private static string? Label(JsonElement item, string name) =>
        item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Object ? Str(element, "label") : null;

    private static string? Str(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static long? Long(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
