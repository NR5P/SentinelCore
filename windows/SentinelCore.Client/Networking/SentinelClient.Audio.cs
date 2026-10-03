using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Networking;

/// <summary>
/// Music &amp; Podcasts: every route in core/docs/MUSIC_AND_PODCASTS.md, in the order the document
/// lists them. Errors arrive the usual way - a <see cref="SentinelServerException"/> carrying the
/// server's own sentence (404 "Music is turned off in Settings." and the like).
/// </summary>
public sealed partial class SentinelClient
{
    private const string MusicBase = "/api/music";
    private const string PodcastsBase = "/api/podcasts";

    /// <summary>
    /// What this app's player decodes natively. The Windows player is Chromium (WebView2), so this
    /// is the contract's "Web and Windows (Chromium)" row; anything else is converted to AAC.
    /// </summary>
    public const string AudioCodecs = "mp3,aac,flac,opus,vorbis,wav";

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Builds a query from pairs, leaving out every null or blank value.</summary>
    internal static List<KeyValuePair<string, string>> Query(params (string Key, object? Value)[] pairs)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in pairs)
        {
            var text = value switch
            {
                null => null,
                bool flag => flag ? "true" : null,
                double number => Num(number),
                int number => number.ToString(CultureInfo.InvariantCulture),
                long number => number.ToString(CultureInfo.InvariantCulture),
                string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
            if (text is not null) list.Add(new KeyValuePair<string, string>(key, text));
        }
        return list;
    }

    // ================================================================ §1 Feature flags

    /// <summary>Always answers. A 404 from an older server means both tabs are off - the caller's job.</summary>
    public Task<AudioEnabled> AudioEnabledAsync(CancellationToken ct = default) =>
        GetAsync<AudioEnabled>("/api/audio/enabled", ct: ct);

    // ================================================================ §2 Music libraries

    public Task<MusicStatus> MusicStatusAsync(CancellationToken ct = default) =>
        GetAsync<MusicStatus>($"{MusicBase}/status", ct: ct);

    public Task<List<MusicLibrary>> MusicLibrariesAsync(CancellationToken ct = default) =>
        GetAsync<List<MusicLibrary>>($"{MusicBase}/libraries", ct: ct);

    public Task<MusicLibrary> CreateMusicLibraryAsync(MusicLibraryInput input, CancellationToken ct = default) =>
        SendAsync<MusicLibrary, MusicLibraryInput>($"{MusicBase}/libraries", HttpMethod.Post, input, ct);

    public Task<MusicLibrary> UpdateMusicLibraryAsync(string id, MusicLibraryInput input, CancellationToken ct = default) =>
        SendAsync<MusicLibrary, MusicLibraryInput>($"{MusicBase}/libraries/{Esc(id)}", HttpMethod.Put, input, ct);

    public Task DeleteMusicLibraryAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MusicBase}/libraries/{Esc(id)}", HttpMethod.Delete, null, ct);

    /// <summary>202; a 409 (already scanning) throws with the server's message.</summary>
    public Task ScanMusicLibraryAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MusicBase}/libraries/{Esc(id)}/scan", HttpMethod.Post, null, ct);

    public Task ScanAllMusicAsync(CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MusicBase}/scan", HttpMethod.Post, null, ct);

    // ================================================================ §4 Music: home, browse, search

    public Task<MusicHome> MusicHomeAsync(CancellationToken ct = default) =>
        GetAsync<MusicHome>($"{MusicBase}/home", ct: ct);

    public Task<AudioPage<MusicArtist>> MusicArtistsAsync(
        string? q = null, string? genre = null, string? sort = null, string? order = null,
        bool albumArtistsOnly = false, int offset = 0, int limit = 100, CancellationToken ct = default) =>
        GetAsync<AudioPage<MusicArtist>>($"{MusicBase}/artists",
            Query(("q", q), ("genre", genre), ("sort", sort), ("order", order), ("albumArtistsOnly", albumArtistsOnly),
                ("offset", offset > 0 ? offset : null), ("limit", limit)), ct);

    public Task<AudioPage<MusicAlbum>> MusicAlbumsAsync(
        string? q = null, string? artistId = null, string? genre = null, int? year = null, int? decade = null,
        string? sort = null, string? order = null, int offset = 0, int limit = 100, CancellationToken ct = default) =>
        GetAsync<AudioPage<MusicAlbum>>($"{MusicBase}/albums",
            Query(("q", q), ("artistId", artistId), ("genre", genre), ("year", year), ("decade", decade),
                ("sort", sort), ("order", order), ("offset", offset > 0 ? offset : null), ("limit", limit)), ct);

    public Task<AudioPage<MusicTrack>> MusicTracksAsync(
        string? q = null, string? artistId = null, string? albumId = null, string? genre = null, int? decade = null,
        bool loved = false, int? minRating = null, string? sort = null, string? order = null,
        int offset = 0, int limit = 100, CancellationToken ct = default) =>
        GetAsync<AudioPage<MusicTrack>>($"{MusicBase}/tracks",
            Query(("q", q), ("artistId", artistId), ("albumId", albumId), ("genre", genre), ("decade", decade),
                ("loved", loved), ("minRating", minRating), ("sort", sort), ("order", order),
                ("offset", offset > 0 ? offset : null), ("limit", limit)), ct);

    public Task<List<MusicGenre>> MusicGenresAsync(CancellationToken ct = default) =>
        GetAsync<List<MusicGenre>>($"{MusicBase}/genres", ct: ct);

    public Task<MusicArtistDetail> MusicArtistAsync(string id, CancellationToken ct = default) =>
        GetAsync<MusicArtistDetail>($"{MusicBase}/artists/{Esc(id)}", ct: ct);

    public Task<MusicAlbumDetail> MusicAlbumAsync(string id, CancellationToken ct = default) =>
        GetAsync<MusicAlbumDetail>($"{MusicBase}/albums/{Esc(id)}", ct: ct);

    public Task<MusicSearchResult> MusicSearchAsync(string q, CancellationToken ct = default) =>
        GetAsync<MusicSearchResult>($"{MusicBase}/search", Query(("q", q)), ct);

    /// <summary>No libraryId lists the libraries themselves as folders.</summary>
    public Task<MusicFolderListing> MusicFoldersAsync(string? libraryId = null, string? path = null, CancellationToken ct = default) =>
        GetAsync<MusicFolderListing>($"{MusicBase}/folders", Query(("libraryId", libraryId), ("path", path)), ct);

    // ---------------------------------------------------------------- Art, lyrics, streaming

    /// <summary>The server-relative path of a track's audio, with this player's codecs.</summary>
    public static string TrackStreamPath(MusicTrack track, int? maxBitrate = null)
    {
        var path = track.StreamPath;
        var query = $"codecs={AudioCodecs}";
        if (maxBitrate is { } kbps && kbps > 0) query += $"&maxBitrate={kbps.ToString(CultureInfo.InvariantCulture)}";
        return path.Contains('?') ? $"{path}&{query}" : $"{path}?{query}";
    }

    /// <summary>Same as the stream, with an attachment name - for saving a track to a file.</summary>
    public static string TrackDownloadPath(string trackId) =>
        $"{MusicBase}/tracks/{Esc(trackId)}/download?codecs={AudioCodecs}";

    /// <summary>A size-limited copy of any Music or Podcasts art path (64-1200).</summary>
    public static string ArtPath(string? artUrl, int size)
    {
        if (string.IsNullOrEmpty(artUrl)) return "";
        if (artUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return artUrl;
        var clamped = Math.Clamp(size, 64, 1200).ToString(CultureInfo.InvariantCulture);
        return artUrl.Contains('?') ? $"{artUrl}&size={clamped}" : $"{artUrl}?size={clamped}";
    }

    /// <summary>404 (thrown) when none are found anywhere.</summary>
    public Task<MusicLyrics> LyricsAsync(string trackId, CancellationToken ct = default) =>
        GetAsync<MusicLyrics>($"{MusicBase}/tracks/{Esc(trackId)}/lyrics", ct: ct);

    // ---------------------------------------------------------------- Playing, loving, rating

    public Task ReportTrackPlayedAsync(string trackId, MusicPlayedInput input, CancellationToken ct = default) =>
        SendNoResultAsync($"{MusicBase}/tracks/{Esc(trackId)}/played", HttpMethod.Post, input, ct);

    public Task ReportNowPlayingTrackAsync(string trackId, CancellationToken ct = default) =>
        SendNoResultAsync($"{MusicBase}/now-playing-track", HttpMethod.Post, new TrackIdBody { TrackId = trackId }, ct);

    /// <summary>kind is "tracks", "albums" or "artists".</summary>
    public async Task<bool> SetLovedAsync(string kind, string id, bool loved, CancellationToken ct = default) =>
        (await SendAsync<LovedBody, LovedBody>($"{MusicBase}/{kind}/{Esc(id)}/love", HttpMethod.Post, new LovedBody { Loved = loved }, ct)
            .ConfigureAwait(false)).Loved;

    /// <summary>kind is "tracks" or "albums"; rating 0-5 (0 clears).</summary>
    public async Task<int> SetRatingAsync(string kind, string id, int rating, CancellationToken ct = default) =>
        (await SendAsync<RatingBody, RatingBody>($"{MusicBase}/{kind}/{Esc(id)}/rating", HttpMethod.Post,
            new RatingBody { Rating = Math.Clamp(rating, 0, 5) }, ct).ConfigureAwait(false)).Rating;

    // ---------------------------------------------------------------- Mixes and radio

    public Task<MusicMixResult> MusicMixAsync(string mixId, CancellationToken ct = default) =>
        GetAsync<MusicMixResult>($"{MusicBase}/mix", Query(("mix", mixId)), ct);

    /// <summary>A radio seeded from something. Pass excludeIds to extend an endless radio.</summary>
    public Task<MusicMixResult> MusicRadioAsync(
        string seedType, string seedId, int limit = 50, IEnumerable<string>? excludeIds = null, CancellationToken ct = default)
    {
        var exclude = excludeIds is null ? null : string.Join(",", excludeIds.Take(400));
        return GetAsync<MusicMixResult>($"{MusicBase}/mix",
            Query(("seedType", seedType), ("seedId", seedId), ("limit", limit), ("excludeIds", exclude)), ct);
    }

    /// <summary>"Shuffle all": tracks list (the route answers { name?, tracks } or a bare list - both decode).</summary>
    public async Task<List<MusicTrack>> MusicShuffleAllAsync(int limit = 200, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequireUri($"{MusicBase}/shuffle", Query(("limit", limit))));
        var data = await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
        var trimmed = Encoding.UTF8.GetString(data).TrimStart();
        return trimmed.StartsWith('[')
            ? Deserialize<List<MusicTrack>>(data, "/api/music/shuffle")
            : Deserialize<MusicMixResult>(data, "/api/music/shuffle").Tracks;
    }

    // ---------------------------------------------------------------- Playlists

    public Task<List<MusicPlaylist>> MusicPlaylistsAsync(CancellationToken ct = default) =>
        GetAsync<List<MusicPlaylist>>($"{MusicBase}/playlists", ct: ct);

    public Task<MusicPlaylist> CreateMusicPlaylistAsync(MusicPlaylistInput input, CancellationToken ct = default) =>
        SendAsync<MusicPlaylist, MusicPlaylistInput>($"{MusicBase}/playlists", HttpMethod.Post, input, ct);

    public Task<MusicPlaylist> MusicPlaylistAsync(string id, CancellationToken ct = default) =>
        GetAsync<MusicPlaylist>($"{MusicBase}/playlists/{Esc(id)}", ct: ct);

    /// <summary>Same body as create, without trackIds (they are ignored if sent).</summary>
    public Task<MusicPlaylist> UpdateMusicPlaylistAsync(string id, MusicPlaylistInput input, CancellationToken ct = default) =>
        SendAsync<MusicPlaylist, MusicPlaylistInput>($"{MusicBase}/playlists/{Esc(id)}", HttpMethod.Put,
            new MusicPlaylistInput
            {
                Name = input.Name, Description = input.Description, Shared = input.Shared,
                Collaborative = input.Collaborative, Rules = input.Rules,
            }, ct);

    public Task DeleteMusicPlaylistAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MusicBase}/playlists/{Esc(id)}", HttpMethod.Delete, null, ct);

    /// <summary>position null appends.</summary>
    public Task<MusicPlaylist> AddToPlaylistAsync(string id, IEnumerable<string> trackIds, int? position = null, CancellationToken ct = default) =>
        SendAsync<MusicPlaylist, PlaylistTracksInput>($"{MusicBase}/playlists/{Esc(id)}/tracks", HttpMethod.Post,
            new PlaylistTracksInput { TrackIds = trackIds.ToList(), Position = position }, ct);

    public Task RemoveFromPlaylistAsync(string id, string entryId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MusicBase}/playlists/{Esc(id)}/tracks/{Esc(entryId)}", HttpMethod.Delete, null, ct);

    /// <summary>The full new order, by entry id.</summary>
    public Task ReorderPlaylistAsync(string id, IEnumerable<string> entryIds, CancellationToken ct = default) =>
        SendNoResultAsync($"{MusicBase}/playlists/{Esc(id)}/order", HttpMethod.Put,
            new PlaylistOrderInput { EntryIds = entryIds.ToList() }, ct);

    public Task PinPlaylistAsync(string id, bool pinned, CancellationToken ct = default) =>
        SendNoResultAsync($"{MusicBase}/playlists/{Esc(id)}/pin", HttpMethod.Post, new PinnedBody { Pinned = pinned }, ct);

    public Task<MusicPlaylist> DuplicatePlaylistAsync(string id, CancellationToken ct = default) =>
        SendAsync<MusicPlaylist, object>($"{MusicBase}/playlists/{Esc(id)}/duplicate", HttpMethod.Post, null, ct);

    public Task<byte[]> PlaylistM3uAsync(string id, CancellationToken ct = default) =>
        GetBytesAsync($"{MusicBase}/playlists/{Esc(id)}/m3u", ct);

    // ---------------------------------------------------------------- Getting music (+ Add)

    public Task<MusicDownloadStatus> MusicDownloadStatusAsync(CancellationToken ct = default) =>
        GetAsync<MusicDownloadStatus>($"{MusicBase}/downloads/status", ct: ct);

    public Task<List<MusicReleaseGroup>> MusicDownloadLookupAsync(string q, CancellationToken ct = default) =>
        GetAsync<List<MusicReleaseGroup>>($"{MusicBase}/downloads/lookup", Query(("q", q)), ct);

    /// <summary>202 with the search; poll <see cref="MusicDownloadSearchStateAsync"/> every 2 s until it finishes.</summary>
    public Task<MusicReleaseSearch> StartMusicDownloadSearchAsync(MusicReleaseSearchInput input, CancellationToken ct = default) =>
        SendAsync<MusicReleaseSearch, MusicReleaseSearchInput>($"{MusicBase}/downloads/search", HttpMethod.Post, input, ct);

    public Task<MusicReleaseSearch> MusicDownloadSearchStateAsync(string searchId, CancellationToken ct = default) =>
        GetAsync<MusicReleaseSearch>($"{MusicBase}/downloads/search/{Esc(searchId)}", ct: ct);

    /// <summary>201 Download; 400 when the search expired.</summary>
    public Task<MediaDownload> MusicDownloadGrabAsync(MusicGrabInput input, CancellationToken ct = default) =>
        SendAsync<MediaDownload, MusicGrabInput>($"{MusicBase}/downloads", HttpMethod.Post, input, ct);

    public Task<MusicDownloadList> MusicDownloadsAsync(CancellationToken ct = default) =>
        GetAsync<MusicDownloadList>($"{MusicBase}/downloads", ct: ct);

    public Task<MediaDownload> MusicDownloadAsync(string id, CancellationToken ct = default) =>
        GetAsync<MediaDownload>($"{MusicBase}/downloads/{Esc(id)}", ct: ct);

    /// <summary>Cancels one, or clears a finished one.</summary>
    public Task DeleteMusicDownloadAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{MusicBase}/downloads/{Esc(id)}", HttpMethod.Delete, null, ct);

    public Task<MediaDownload> RetryMusicDownloadAsync(string id, CancellationToken ct = default) =>
        SendAsync<MediaDownload, object>($"{MusicBase}/downloads/{Esc(id)}/retry", HttpMethod.Post, null, ct);

    // ================================================================ §6 Podcasts

    public Task<List<DirectoryShow>> PodcastDirectorySearchAsync(string q, CancellationToken ct = default) =>
        GetAsync<List<DirectoryShow>>($"{PodcastsBase}/directory/search", Query(("q", q)), ct);

    public Task<List<DirectoryShow>> PodcastTopShowsAsync(long? genre = null, CancellationToken ct = default) =>
        GetAsync<List<DirectoryShow>>($"{PodcastsBase}/directory/top", Query(("genre", genre)), ct);

    public Task<List<DirectoryGenre>> PodcastGenresAsync(CancellationToken ct = default) =>
        GetAsync<List<DirectoryGenre>>($"{PodcastsBase}/directory/genres", ct: ct);

    public Task<PodcastPreview> PodcastPreviewAsync(string? feedUrl, long? itunesId = null, CancellationToken ct = default) =>
        GetAsync<PodcastPreview>($"{PodcastsBase}/preview", Query(("feedUrl", feedUrl), ("itunesId", itunesId)), ct);

    public Task<List<DirectoryShow>> PodcastRecommendedAsync(CancellationToken ct = default) =>
        GetAsync<List<DirectoryShow>>($"{PodcastsBase}/recommended", ct: ct);

    public Task<List<Podcast>> PodcastsAsync(bool all = false, CancellationToken ct = default) =>
        GetAsync<List<Podcast>>(PodcastsBase, Query(("all", all)), ct);

    public Task<Podcast> SubscribeAsync(string? feedUrl, long? itunesId = null, CancellationToken ct = default) =>
        SendAsync<Podcast, PodcastSubscribeInput>($"{PodcastsBase}/subscriptions", HttpMethod.Post,
            new PodcastSubscribeInput { FeedUrl = string.IsNullOrWhiteSpace(feedUrl) ? null : feedUrl.Trim(), ItunesId = itunesId }, ct);

    public Task UnsubscribeAsync(string podcastId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{PodcastsBase}/{Esc(podcastId)}/subscription", HttpMethod.Delete, null, ct);

    /// <summary>Admin: forgets the show, its episodes and downloads.</summary>
    public Task DeletePodcastAsync(string podcastId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{PodcastsBase}/{Esc(podcastId)}", HttpMethod.Delete, null, ct);

    public Task<Podcast> PodcastAsync(string podcastId, CancellationToken ct = default) =>
        GetAsync<Podcast>($"{PodcastsBase}/{Esc(podcastId)}", ct: ct);

    public Task<Podcast> SavePodcastSettingsAsync(string podcastId, PodcastSettings settings, CancellationToken ct = default) =>
        SendAsync<Podcast, PodcastSettings>($"{PodcastsBase}/{Esc(podcastId)}/settings", HttpMethod.Put, settings, ct);

    public Task<Podcast> SavePodcastMySettingsAsync(string podcastId, PodcastMySettings settings, CancellationToken ct = default) =>
        SendAsync<Podcast, PodcastMySettings>($"{PodcastsBase}/{Esc(podcastId)}/my-settings", HttpMethod.Put, settings, ct);

    public Task<Podcast> RefreshPodcastAsync(string podcastId, CancellationToken ct = default) =>
        SendAsync<Podcast, object>($"{PodcastsBase}/{Esc(podcastId)}/refresh", HttpMethod.Post, null, ct);

    public Task RefreshAllPodcastsAsync(CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{PodcastsBase}/refresh", HttpMethod.Post, null, ct);

    public Task<PodcastEpisodePage> PodcastEpisodesAsync(
        string podcastId, string? sort = null, string? filter = null, string? q = null, int? season = null,
        int offset = 0, int limit = 100, CancellationToken ct = default) =>
        GetAsync<PodcastEpisodePage>($"{PodcastsBase}/{Esc(podcastId)}/episodes",
            Query(("sort", sort), ("filter", filter), ("q", q), ("season", season),
                ("offset", offset > 0 ? offset : null), ("limit", limit)), ct);

    public Task MarkPodcastPlayedAsync(string podcastId, bool played, DateTimeOffset? before = null, CancellationToken ct = default) =>
        SendNoResultAsync($"{PodcastsBase}/{Esc(podcastId)}/played", HttpMethod.Post, new PlayedBody { Played = played, Before = before }, ct);

    // ---------------------------------------------------------------- OPML

    public Task<byte[]> ExportOpmlAsync(CancellationToken ct = default) =>
        GetBytesAsync($"{PodcastsBase}/opml", ct);

    /// <summary>The raw OPML file, sent as text/xml.</summary>
    public async Task<OpmlImportResult> ImportOpmlAsync(string opml, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RequireUri($"{PodcastsBase}/opml"))
        {
            Content = new StringContent(opml, Encoding.UTF8, "text/xml"),
        };
        var data = await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
        return Deserialize<OpmlImportResult>(data, $"{PodcastsBase}/opml");
    }

    // ---------------------------------------------------------------- Home and queue

    public Task<PodcastHome> PodcastHomeAsync(CancellationToken ct = default) =>
        GetAsync<PodcastHome>($"{PodcastsBase}/home", ct: ct);

    public Task<List<PodcastEpisode>> PodcastQueueAsync(CancellationToken ct = default) =>
        GetAsync<List<PodcastEpisode>>($"{PodcastsBase}/queue", ct: ct);

    /// <summary>position is "next" or "last".</summary>
    public Task<List<PodcastEpisode>> AddToPodcastQueueAsync(string episodeId, string position = "last", CancellationToken ct = default) =>
        SendAsync<List<PodcastEpisode>, PodcastQueueAddInput>($"{PodcastsBase}/queue", HttpMethod.Post,
            new PodcastQueueAddInput { EpisodeId = episodeId, Position = position == "next" ? "next" : "last" }, ct);

    public Task<List<PodcastEpisode>> SetPodcastQueueAsync(IEnumerable<string> episodeIds, CancellationToken ct = default) =>
        SendAsync<List<PodcastEpisode>, PodcastQueueOrderInput>($"{PodcastsBase}/queue", HttpMethod.Put,
            new PodcastQueueOrderInput { EpisodeIds = episodeIds.ToList() }, ct);

    public Task<List<PodcastEpisode>> RemoveFromPodcastQueueAsync(string episodeId, CancellationToken ct = default) =>
        SendAsync<List<PodcastEpisode>, object>($"{PodcastsBase}/queue/{Esc(episodeId)}", HttpMethod.Delete, null, ct);

    // ---------------------------------------------------------------- Episodes

    public Task<PodcastEpisodeDetail> PodcastEpisodeAsync(string episodeId, CancellationToken ct = default) =>
        GetAsync<PodcastEpisodeDetail>($"{PodcastsBase}/episodes/{Esc(episodeId)}", ct: ct);

    public Task<EpisodeProgress> ReportEpisodeProgressAsync(string episodeId, EpisodeProgressInput input, CancellationToken ct = default) =>
        SendAsync<EpisodeProgress, EpisodeProgressInput>($"{PodcastsBase}/episodes/{Esc(episodeId)}/progress", HttpMethod.Post, input, ct);

    public Task<EpisodeProgress> SetEpisodePlayedAsync(string episodeId, bool played, CancellationToken ct = default) =>
        SendAsync<EpisodeProgress, PlayedBody>($"{PodcastsBase}/episodes/{Esc(episodeId)}/played", HttpMethod.Post,
            new PlayedBody { Played = played }, ct);

    /// <summary>Saving an episode to a file: the audio route with ?download=1.</summary>
    public static string EpisodeDownloadPath(string episodeId) => $"{PodcastsBase}/episodes/{Esc(episodeId)}/audio?download=1";

    public Task<PodcastEpisode> DownloadEpisodeOnServerAsync(string episodeId, CancellationToken ct = default) =>
        SendAsync<PodcastEpisode, object>($"{PodcastsBase}/episodes/{Esc(episodeId)}/download", HttpMethod.Post, null, ct);

    public Task<PodcastEpisode> DeleteEpisodeDownloadAsync(string episodeId, CancellationToken ct = default) =>
        SendAsync<PodcastEpisode, object>($"{PodcastsBase}/episodes/{Esc(episodeId)}/download", HttpMethod.Delete, null, ct);

    public Task<List<PodcastChapter>> EpisodeChaptersAsync(string episodeId, CancellationToken ct = default) =>
        GetAsync<List<PodcastChapter>>($"{PodcastsBase}/episodes/{Esc(episodeId)}/chapters", ct: ct);

    public Task<PodcastFill> PodcastFillAsync(int minutes, CancellationToken ct = default) =>
        GetAsync<PodcastFill>($"{PodcastsBase}/fill", Query(("minutes", minutes)), ct);

    // ---------------------------------------------------------------- Transcripts

    /// <summary>404 (thrown) when there is none.</summary>
    public Task<PodcastTranscript> EpisodeTranscriptAsync(string episodeId, CancellationToken ct = default) =>
        GetAsync<PodcastTranscript>($"{PodcastsBase}/episodes/{Esc(episodeId)}/transcript", ct: ct);

    /// <summary>400 with the reason when transcripts are off or whisper isn't set up.</summary>
    public Task<TranscribeResult> TranscribeEpisodeAsync(string episodeId, CancellationToken ct = default) =>
        SendAsync<TranscribeResult, object>($"{PodcastsBase}/episodes/{Esc(episodeId)}/transcribe", HttpMethod.Post, null, ct);

    public Task<List<TranscriptSearchHit>> SearchTranscriptsAsync(string q, CancellationToken ct = default) =>
        GetAsync<List<TranscriptSearchHit>>($"{PodcastsBase}/transcripts/search", Query(("q", q)), ct);

    // ---------------------------------------------------------------- Bookmarks and clips

    public Task<List<PodcastBookmark>> BookmarksAsync(string? episodeId = null, CancellationToken ct = default) =>
        GetAsync<List<PodcastBookmark>>($"{PodcastsBase}/bookmarks", Query(("episodeId", episodeId)), ct);

    public Task<PodcastBookmark> AddBookmarkAsync(string episodeId, double positionSeconds, string note, CancellationToken ct = default) =>
        SendAsync<PodcastBookmark, BookmarkInput>($"{PodcastsBase}/episodes/{Esc(episodeId)}/bookmarks", HttpMethod.Post,
            new BookmarkInput { PositionSeconds = positionSeconds, Note = note ?? "" }, ct);

    public Task<PodcastBookmark> UpdateBookmarkAsync(string bookmarkId, string note, CancellationToken ct = default) =>
        SendAsync<PodcastBookmark, BookmarkNoteInput>($"{PodcastsBase}/bookmarks/{Esc(bookmarkId)}", HttpMethod.Put,
            new BookmarkNoteInput { Note = note ?? "" }, ct);

    public Task DeleteBookmarkAsync(string bookmarkId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{PodcastsBase}/bookmarks/{Esc(bookmarkId)}", HttpMethod.Delete, null, ct);

    /// <summary>404 when the Organizer is off.</summary>
    public Task<BookmarkToNoteResult> BookmarkToNoteAsync(string bookmarkId, CancellationToken ct = default) =>
        SendAsync<BookmarkToNoteResult, object>($"{PodcastsBase}/bookmarks/{Esc(bookmarkId)}/to-note", HttpMethod.Post, null, ct);

    /// <summary>5-180 seconds; cut by ffmpeg, which can take a while, so this rides the no-timeout session.</summary>
    public async Task<PodcastClip> CreateClipAsync(string episodeId, double startSeconds, double durationSeconds, string title, CancellationToken ct = default)
    {
        var path = $"{PodcastsBase}/episodes/{Esc(episodeId)}/clips";
        using var request = new HttpRequestMessage(HttpMethod.Post, RequireUri(path))
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new ClipInput
                {
                    StartSeconds = Math.Max(0, startSeconds),
                    DurationSeconds = Math.Clamp(durationSeconds, 5, 180),
                    Title = title ?? "",
                }, JsonOptions),
                Encoding.UTF8, "application/json"),
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        var data = await ExecuteAndValidateAsync(request, limit.Token, _organizerHttp).ConfigureAwait(false);
        return Deserialize<PodcastClip>(data, path);
    }

    public Task<List<PodcastClip>> ClipsAsync(CancellationToken ct = default) =>
        GetAsync<List<PodcastClip>>($"{PodcastsBase}/clips", ct: ct);

    public Task DeleteClipAsync(string clipId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"{PodcastsBase}/clips/{Esc(clipId)}", HttpMethod.Delete, null, ct);

    public Task<PodcastStatus> PodcastStatusAsync(CancellationToken ct = default) =>
        GetAsync<PodcastStatus>($"{PodcastsBase}/status", ct: ct);

    /// <summary>
    /// Any server path as bytes over the no-timeout session - a whole episode or album track can
    /// take minutes to save.
    /// </summary>
    public async Task<byte[]> DownloadLargeAsync(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequireUri(path));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(30));
        return await ExecuteAndValidateAsync(request, limit.Token, _organizerHttp).ConfigureAwait(false);
    }

    // ================================================================ §7 Play where you left off

    public Task PutNowPlayingAsync(NowPlaying nowPlaying, CancellationToken ct = default) =>
        SendNoResultAsync("/api/audio/now-playing", HttpMethod.Put, new
        {
            deviceId = nowPlaying.DeviceId,
            deviceName = nowPlaying.DeviceName,
            kind = nowPlaying.Kind,
            queue = nowPlaying.Queue,
            index = nowPlaying.Index,
            positionSeconds = nowPlaying.PositionSeconds,
            playing = nowPlaying.Playing,
            shuffle = nowPlaying.Shuffle,
            repeat = nowPlaying.Repeat,
            speed = nowPlaying.Speed,
            context = nowPlaying.Context,
        }, ct);

    public Task<List<NowPlaying>> NowPlayingAsync(CancellationToken ct = default) =>
        GetAsync<List<NowPlaying>>("/api/audio/now-playing", ct: ct);

    public Task DeleteNowPlayingAsync(string deviceId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/audio/now-playing/{Esc(deviceId)}", HttpMethod.Delete, null, ct);

    public Task<NowPlayingResolved> ResolveNowPlayingAsync(string deviceId, CancellationToken ct = default) =>
        GetAsync<NowPlayingResolved>($"/api/audio/now-playing/{Esc(deviceId)}/resolve", ct: ct);

    // ================================================================ §8 Speakers

    /// <summary>The heartbeat: on start and every 60 s while "Let others play here" is on.</summary>
    public Task RegisterSpeakerAsync(string deviceId, SpeakerRegistration registration, CancellationToken ct = default) =>
        SendNoResultAsync($"/api/audio/speakers/{Esc(deviceId)}", HttpMethod.Put, registration, ct);

    public Task UnregisterSpeakerAsync(string deviceId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/audio/speakers/{Esc(deviceId)}", HttpMethod.Delete, null, ct);

    public Task<List<Speaker>> SpeakersAsync(CancellationToken ct = default) =>
        GetAsync<List<Speaker>>("/api/audio/speakers", ct: ct);

    public Task PutSpeakerStateAsync(string deviceId, SpeakerState state, CancellationToken ct = default) =>
        SendNoResultAsync($"/api/audio/speakers/{Esc(deviceId)}/state", HttpMethod.Put, state, ct);

    public Task<SpeakerCommandAccepted> SendSpeakerCommandAsync(string deviceId, SpeakerCommand command, CancellationToken ct = default) =>
        SendAsync<SpeakerCommandAccepted, SpeakerCommand>($"/api/audio/speakers/{Esc(deviceId)}/commands", HttpMethod.Post, command, ct);

    /// <summary>
    /// The long poll. Answers as soon as there is a command after <paramref name="after"/>, or with
    /// none after <paramref name="waitSeconds"/>. Rides the no-timeout session with its own limit a
    /// little past the wait, so a held-open request is never mistaken for a dead server.
    /// </summary>
    public async Task<SpeakerCommandsReply> SpeakerCommandsAsync(string deviceId, long after, int waitSeconds = 25, CancellationToken ct = default)
    {
        var path = $"/api/audio/speakers/{Esc(deviceId)}/commands";
        using var request = new HttpRequestMessage(HttpMethod.Get, RequireUri(path, Query(("after", after), ("wait", waitSeconds))));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(waitSeconds + 15));
        var data = await ExecuteAndValidateAsync(request, limit.Token, _organizerHttp).ConfigureAwait(false);
        return Deserialize<SpeakerCommandsReply>(data, path);
    }

    // ================================================================ §9 Alarms, briefing, stats, ListenBrainz

    public Task<List<WakeAlarm>> WakeAlarmsAsync(CancellationToken ct = default) =>
        GetAsync<List<WakeAlarm>>("/api/audio/alarms", ct: ct);

    public Task<WakeAlarm> SaveWakeAlarmAsync(WakeAlarm alarm, CancellationToken ct = default) =>
        string.IsNullOrEmpty(alarm.Id)
            ? SendAsync<WakeAlarm, WakeAlarm>("/api/audio/alarms", HttpMethod.Post, alarm, ct)
            : SendAsync<WakeAlarm, WakeAlarm>($"/api/audio/alarms/{Esc(alarm.Id!)}", HttpMethod.Put, alarm, ct);

    public Task DeleteWakeAlarmAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/audio/alarms/{Esc(id)}", HttpMethod.Delete, null, ct);

    public Task TestWakeAlarmAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/audio/alarms/{Esc(id)}/test", HttpMethod.Post, null, ct);

    public Task<MorningBriefing> MorningBriefingAsync(CancellationToken ct = default) =>
        GetAsync<MorningBriefing>("/api/audio/briefing", ct: ct);

    /// <summary>period is week | month | year | all; year applies to period=year ("Wrapped").</summary>
    public Task<ListeningStats> ListeningStatsAsync(string period, int? year = null, CancellationToken ct = default) =>
        GetAsync<ListeningStats>("/api/audio/stats", Query(("period", period), ("year", period == "year" ? year : null)), ct);

    public Task<ListenBrainzStatus> ListenBrainzAsync(CancellationToken ct = default) =>
        GetAsync<ListenBrainzStatus>("/api/audio/me/listenbrainz", ct: ct);

    /// <summary>A blank token disconnects; a bad one is a 400.</summary>
    public Task<ListenBrainzStatus> SetListenBrainzTokenAsync(string token, CancellationToken ct = default) =>
        SendAsync<ListenBrainzStatus, ListenBrainzTokenInput>("/api/audio/me/listenbrainz", HttpMethod.Put,
            new ListenBrainzTokenInput { Token = token?.Trim() ?? "" }, ct);
}
