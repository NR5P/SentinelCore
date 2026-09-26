using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SentinelCore.Web;

// The Movie Database (themoviedb.org): titles, descriptions, ratings, cast and the official
// posters and backdrops for what the scanner found. Free for personal use with an API key the
// user pastes into Settings -> MOVIES & TV; a v3 key goes in the query string, a v4 read-access
// token goes in the Authorization header, and both are accepted here because the TMDB site
// shows both on the same page and people copy whichever is nearer.
public sealed class TmdbClient(IHttpClientFactory httpClientFactory, AppSettingsFile settings, ILogger<TmdbClient> logger)
{
    public const string HttpClientName = "Tmdb";
    private const string ApiBase = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    public sealed record SearchResult(int TmdbId, string Title, int? Year, string? Overview, string? PosterPath);

    public sealed record Movie(
        int TmdbId, string Title, int? Year, string? Overview, string? Tagline, List<string> Genres, double? Rating,
        int? RuntimeMinutes, string? ReleaseDate, string? PosterPath, string? BackdropPath, List<MediaCastMember> Cast, string? Certification);

    public sealed record Show(
        int TmdbId, string Title, int? Year, string? Overview, string? Tagline, List<string> Genres, double? Rating,
        int? RuntimeMinutes, string? FirstAirDate, string? PosterPath, string? BackdropPath, List<MediaCastMember> Cast,
        string? Certification, List<int> SeasonNumbers);

    public sealed record Episode(int Season, int Number, string Name, string? Overview, string? StillPath, string? AirDate, double? Rating);

    public sealed record Season(int Number, string Name, string? PosterPath, List<Episode> Episodes);

    // TMDB allows roughly fifty requests a second; four in flight keeps a first scan of a big
    // library well clear of that while still finishing in minutes.
    private readonly SemaphoreSlim _inFlight = new(4, 4);

    public async Task<string?> ApiKeyAsync(CancellationToken cancellationToken)
    {
        var section = await settings.GetSectionAsync<MediaSettingsDto>(MediaSettingsDto.SectionName, cancellationToken);
        var key = section?.TmdbApiKey?.Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    public static string ImageUrl(string path, string size) => ImageBase + size + path;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string kind, string query, int? year, CancellationToken cancellationToken)
    {
        var isShow = kind == MediaKinds.Show;
        var parameters = new Dictionary<string, string> { ["query"] = query, ["include_adult"] = "false", ["language"] = "en-US" };
        if (year is { } y) parameters[isShow ? "first_air_date_year" : "year"] = y.ToString();
        using var document = await GetAsync(isShow ? "/search/tv" : "/search/movie", parameters, cancellationToken);
        if (document is null || !document.RootElement.TryGetProperty("results", out var results)) return [];

        var list = new List<SearchResult>();
        foreach (var result in results.EnumerateArray())
        {
            var id = Int(result, "id");
            if (id is null) continue;
            list.Add(new SearchResult(
                id.Value,
                String(result, isShow ? "name" : "title") ?? "",
                YearOf(String(result, isShow ? "first_air_date" : "release_date")),
                String(result, "overview"),
                String(result, "poster_path")));
        }
        return list;
    }

    public async Task<Movie?> GetMovieAsync(int tmdbId, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"/movie/{tmdbId}",
            new() { ["append_to_response"] = "credits,release_dates", ["language"] = "en-US" }, cancellationToken);
        if (document is null) return null;
        var root = document.RootElement;
        return new Movie(
            tmdbId,
            String(root, "title") ?? "",
            YearOf(String(root, "release_date")),
            Blank(String(root, "overview")),
            Blank(String(root, "tagline")),
            Genres(root),
            Rating(root),
            Int(root, "runtime") is { } runtime and > 0 ? runtime : null,
            Blank(String(root, "release_date")),
            String(root, "poster_path"),
            String(root, "backdrop_path"),
            Cast(root),
            MovieCertification(root));
    }

    public async Task<Show?> GetShowAsync(int tmdbId, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"/tv/{tmdbId}",
            new() { ["append_to_response"] = "credits,content_ratings", ["language"] = "en-US" }, cancellationToken);
        if (document is null) return null;
        var root = document.RootElement;

        var seasons = new List<int>();
        if (root.TryGetProperty("seasons", out var seasonList))
            foreach (var season in seasonList.EnumerateArray())
                if (Int(season, "season_number") is { } number) seasons.Add(number);

        int? runtime = null;
        if (root.TryGetProperty("episode_run_time", out var runtimes) && runtimes.ValueKind == JsonValueKind.Array)
            foreach (var value in runtimes.EnumerateArray())
                if (value.ValueKind == JsonValueKind.Number && value.GetInt32() > 0) { runtime = value.GetInt32(); break; }

        return new Show(
            tmdbId,
            String(root, "name") ?? "",
            YearOf(String(root, "first_air_date")),
            Blank(String(root, "overview")),
            Blank(String(root, "tagline")),
            Genres(root),
            Rating(root),
            runtime,
            Blank(String(root, "first_air_date")),
            String(root, "poster_path"),
            String(root, "backdrop_path"),
            Cast(root),
            ShowCertification(root),
            seasons);
    }

    public async Task<Season?> GetSeasonAsync(int showTmdbId, int seasonNumber, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"/tv/{showTmdbId}/season/{seasonNumber}", new() { ["language"] = "en-US" }, cancellationToken);
        if (document is null) return null;
        var root = document.RootElement;
        var episodes = new List<Episode>();
        if (root.TryGetProperty("episodes", out var list))
            foreach (var episode in list.EnumerateArray())
            {
                if (Int(episode, "episode_number") is not { } number) continue;
                episodes.Add(new Episode(
                    Int(episode, "season_number") ?? seasonNumber, number, String(episode, "name") ?? "",
                    Blank(String(episode, "overview")), String(episode, "still_path"), Blank(String(episode, "air_date")), Rating(episode)));
            }
        return new Season(seasonNumber, String(root, "name") ?? $"Season {seasonNumber}", String(root, "poster_path"), episodes);
    }

    // Fetches one image into the art cache. Returns false (and leaves nothing behind) on any
    // failure, so a caller can simply try again another day.
    public async Task<bool> DownloadImageAsync(string tmdbPath, string size, string destination, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        await _inFlight.WaitAsync(cancellationToken);
        try
        {
            using var response = await client.GetAsync(ImageUrl(tmdbPath, size), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".part";
            await using (var file = File.Create(temporary))
                await response.Content.CopyToAsync(file, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
            return true;
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("TMDB image {Path} failed: {Message}", tmdbPath, ex.Message);
            return false;
        }
        finally { _inFlight.Release(); }
    }

    private async Task<JsonDocument?> GetAsync(string path, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var key = await ApiKeyAsync(cancellationToken);
        if (key is null) throw new TmdbNotConfiguredException();

        var client = httpClientFactory.CreateClient(HttpClientName);
        var isBearer = key.Length > 60 || key.StartsWith("eyJ", StringComparison.Ordinal);
        if (!isBearer) parameters["api_key"] = key;
        var url = ApiBase + path + "?" + string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _inFlight.WaitAsync(cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (isBearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.StatusCode is HttpStatusCode.Unauthorized)
                    throw new TmdbRejectedKeyException();
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonDocument.Parse(json);
            }
            finally { _inFlight.Release(); }
        }
        throw new HttpRequestException("TMDB kept answering 429 (too many requests).");
    }

    private static List<string> Genres(JsonElement root)
    {
        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var list))
            foreach (var genre in list.EnumerateArray())
                if (String(genre, "name") is { Length: > 0 } name) genres.Add(name);
        return genres;
    }

    private static List<MediaCastMember> Cast(JsonElement root)
    {
        var cast = new List<MediaCastMember>();
        if (root.TryGetProperty("credits", out var credits) && credits.TryGetProperty("cast", out var list))
            foreach (var member in list.EnumerateArray().Take(12))
                if (String(member, "name") is { Length: > 0 } name) cast.Add(new MediaCastMember(name, Blank(String(member, "character"))));
        return cast;
    }

    // The US certification ("PG", "TV-Y7") - what a family shelf wants to show at a glance.
    private static string? MovieCertification(JsonElement root)
    {
        if (!root.TryGetProperty("release_dates", out var dates) || !dates.TryGetProperty("results", out var countries)) return null;
        foreach (var country in countries.EnumerateArray())
        {
            if (String(country, "iso_3166_1") != "US" || !country.TryGetProperty("release_dates", out var releases)) continue;
            foreach (var release in releases.EnumerateArray())
                if (Blank(String(release, "certification")) is { } certification) return certification;
        }
        return null;
    }

    private static string? ShowCertification(JsonElement root)
    {
        if (!root.TryGetProperty("content_ratings", out var ratings) || !ratings.TryGetProperty("results", out var countries)) return null;
        foreach (var country in countries.EnumerateArray())
            if (String(country, "iso_3166_1") == "US" && Blank(String(country, "rating")) is { } rating) return rating;
        return null;
    }

    private static double? Rating(JsonElement element) =>
        element.TryGetProperty("vote_average", out var value) && value.ValueKind == JsonValueKind.Number && value.GetDouble() > 0
            ? Math.Round(value.GetDouble(), 1) : null;

    private static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var year) && year > 1800 ? year : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? String(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}

public sealed class TmdbNotConfiguredException() : InvalidOperationException("No TMDB API key is set. Add one under Settings -> MOVIES & TV.");

public sealed class TmdbRejectedKeyException() : InvalidOperationException("TMDB rejected the API key. Check it under Settings -> MOVIES & TV.");
