package com.sentinelcore.android.audio

import org.json.JSONObject

// Music objects - core/docs/MUSIC_AND_PODCASTS.md §2-§4. Dates stay the server's ISO strings and
// are formatted where they're shown (AudioFormat), which keeps these plain and testable.

data class ReplayGainInfo(
    val trackGain: Double? = null,
    val trackPeak: Double? = null,
    val albumGain: Double? = null,
    val albumPeak: Double? = null,
)

data class Track(
    val id: String,
    val title: String,
    val artist: String = "",
    val artists: List<String> = emptyList(),
    val artistId: String? = null,
    val albumArtist: String? = null,
    val album: String = "",
    val albumId: String? = null,
    val trackNumber: Int? = null,
    val discNumber: Int? = null,
    val year: Int? = null,
    val genres: List<String> = emptyList(),
    val durationSeconds: Double = 0.0,
    val codec: String? = null,
    val bitrate: Long? = null,
    val sampleRate: Int? = null,
    val bitDepth: Int? = null,
    val channels: Int? = null,
    val sizeBytes: Long? = null,
    val composer: String? = null,
    val bpm: Int? = null,
    val replayGain: ReplayGainInfo? = null,
    val hasLyrics: Boolean = false,
    val artUrl: String? = null,
    val streamUrl: String? = null,
    val addedAt: String? = null,
    val loved: Boolean = false,
    val rating: Int = 0,
    val playCount: Int = 0,
    val lastPlayedAt: String? = null,
    val path: String? = null,
    // Only on a playlist's tracks: tells duplicates apart for remove and reorder.
    val entryId: String? = null,
)

data class Album(
    val id: String,
    val title: String,
    val artist: String = "",
    val artistId: String? = null,
    val year: Int? = null,
    val genres: List<String> = emptyList(),
    val trackCount: Int = 0,
    val discCount: Int = 1,
    val durationSeconds: Double = 0.0,
    val artUrl: String? = null,
    val addedAt: String? = null,
    val compilation: Boolean = false,
    val loved: Boolean = false,
    val rating: Int = 0,
    val playCount: Int = 0,
    val lastPlayedAt: String? = null,
)

data class AlbumDetail(
    val album: Album,
    val tracks: List<Track>,
    val moreByArtist: List<Album>,
    val totalBytes: Long?,
    val formats: List<String>,
)

data class Artist(
    val id: String,
    val name: String,
    val albumCount: Int = 0,
    val trackCount: Int = 0,
    val genres: List<String> = emptyList(),
    val artUrl: String? = null,
    val loved: Boolean = false,
    val playCount: Int = 0,
)

data class ArtistDetail(
    val artist: Artist,
    val albums: List<Album>,
    val appearsOn: List<Album>,
    val topTracks: List<Track>,
    val similar: List<Artist>,
)

data class Playlist(
    val id: String,
    val name: String,
    val description: String = "",
    val ownerName: String? = null,
    val mine: Boolean = true,
    val shared: Boolean = false,
    val collaborative: Boolean = false,
    val smart: Boolean = false,
    val rules: SmartRules? = null,
    val trackCount: Int = 0,
    val durationSeconds: Double = 0.0,
    val artUrls: List<String> = emptyList(),
    val createdAt: String? = null,
    val updatedAt: String? = null,
    val pinned: Boolean = false,
) {
    // Changing the tracks: the owner (an admin too, which the server enforces), or anyone on a
    // collaborative playlist. Smart playlists are worked out by their rules instead.
    fun canEditTracks(isAdmin: Boolean): Boolean = !smart && (mine || collaborative || isAdmin)

    fun canEditDetails(isAdmin: Boolean): Boolean = mine || isAdmin
}

data class PlaylistDetail(val playlist: Playlist, val tracks: List<Track>)

data class Mix(
    val id: String,
    val name: String,
    val description: String = "",
    val artUrls: List<String> = emptyList(),
    val seedType: String? = null,
    val seedId: String? = null,
)

data class RecentItem(
    val kind: String,
    val album: Album? = null,
    val playlist: Playlist? = null,
    val artist: Artist? = null,
    val mix: Mix? = null,
)

data class Decade(val decade: Int, val label: String, val trackCount: Int)

data class OnThisDay(val label: String, val tracks: List<Track>)

data class MusicHome(
    val pinned: List<Playlist> = emptyList(),
    val recentlyPlayed: List<RecentItem> = emptyList(),
    val recentlyAdded: List<Album> = emptyList(),
    val heavyRotation: List<Album> = emptyList(),
    val mixes: List<Mix> = emptyList(),
    val forgottenFavorites: List<Track> = emptyList(),
    val onThisDay: OnThisDay? = null,
    val discover: List<Album> = emptyList(),
    val lovedTracks: List<Track> = emptyList(),
    val decades: List<Decade> = emptyList(),
    val trackCount: Int = 0,
    val albumCount: Int = 0,
    val artistCount: Int = 0,
)

data class Genre(val name: String, val trackCount: Int, val albumCount: Int, val artUrls: List<String>)

data class Page<T>(val items: List<T>, val total: Int)

data class MusicSearch(
    val artists: List<Artist> = emptyList(),
    val albums: List<Album> = emptyList(),
    val tracks: List<Track> = emptyList(),
    val playlists: List<Playlist> = emptyList(),
    val genres: List<String> = emptyList(),
) {
    val isEmpty: Boolean get() = artists.isEmpty() && albums.isEmpty() && tracks.isEmpty() && playlists.isEmpty() && genres.isEmpty()
}

data class MusicFolder(val name: String, val path: String, val trackCount: Int)

data class FolderListing(
    val libraryId: String?,
    val path: String,
    val parent: String?,
    val folders: List<MusicFolder>,
    val tracks: List<Track>,
)

data class LyricLine(val time: Double?, val text: String)

data class Lyrics(val synced: Boolean, val lines: List<LyricLine>, val plain: String?, val source: String?)

data class MixResult(val name: String, val tracks: List<Track>)

data class MusicLibrary(
    val id: String,
    val name: String,
    val path: String,
    val network: Boolean = false,
    val trackCount: Int = 0,
    val lastScanFinishedAt: String? = null,
    val lastScanError: String? = null,
    val createdAt: String? = null,
)

data class ScanProgress(
    val phase: String?,
    val libraryName: String?,
    val processed: Int,
    val total: Int,
    val startedAt: String?,
)

data class MusicStatus(
    val enabled: Boolean,
    val ffmpegAvailable: Boolean,
    val scanning: Boolean,
    val scan: ScanProgress?,
    val libraries: List<MusicLibrary>,
    val trackCount: Int,
    val albumCount: Int,
    val artistCount: Int,
    val totalDurationSeconds: Double,
    val totalBytes: Long,
    val lastScanFinishedAt: String?,
    val lastScanError: String?,
)

// GET /api/music/downloads/status. `status` is the same VPN/services object Movies shows.
data class MusicDownloadStatus(
    val enabled: Boolean,
    val canDownload: Boolean,
    val musicLibrary: Boolean,
    val status: com.sentinelcore.android.data.MediaDownloadServices?,
)

data class MusicLookup(
    val mbid: String,
    val title: String,
    val artist: String,
    val year: Int?,
    val type: String?,
    val trackCount: Int?,
    val artUrl: String?,
    val inLibrary: Boolean,
)

data class MusicRelease(
    val id: String,
    val title: String,
    val site: String?,
    val sizeBytes: Long?,
    val seeders: Int?,
    val leechers: Int?,
    val publishedAt: String?,
    val format: String?,
    val lossless: Boolean,
    val matches: Boolean,
    val warning: String?,
)

// A background search (POST /api/music/downloads/search → 202, polled every 2 s).
data class MusicReleaseSearch(
    val id: String,
    // connecting, searching, done, failed.
    val phase: String,
    val message: String?,
    val error: String?,
    val artist: String?,
    val album: String?,
    val year: Int?,
    val results: List<MusicRelease>,
    val others: List<MusicRelease>,
) {
    val finished: Boolean get() = phase == "done" || phase == "failed"
}

// ---------------- Parsing ----------------

internal fun parseReplayGain(json: JSONObject?): ReplayGainInfo? = json?.let {
    ReplayGainInfo(it.dbl("trackGain"), it.dbl("trackPeak"), it.dbl("albumGain"), it.dbl("albumPeak"))
}

internal fun parseTrack(json: JSONObject): Track = Track(
    id = json.getString("id"),
    title = json.text("title", "Untitled"),
    artist = json.text("artist"),
    artists = json.strings("artists"),
    artistId = json.str("artistId"),
    albumArtist = json.str("albumArtist"),
    album = json.text("album"),
    albumId = json.str("albumId"),
    trackNumber = json.int("trackNumber"),
    discNumber = json.int("discNumber"),
    year = json.int("year"),
    genres = json.strings("genres"),
    durationSeconds = json.dbl("durationSeconds") ?: 0.0,
    codec = json.str("codec"),
    bitrate = json.long("bitrate"),
    sampleRate = json.int("sampleRate"),
    bitDepth = json.int("bitDepth"),
    channels = json.int("channels"),
    sizeBytes = json.long("sizeBytes"),
    composer = json.str("composer"),
    bpm = json.int("bpm"),
    replayGain = parseReplayGain(json.obj("replayGain")),
    hasLyrics = json.bool("hasLyrics"),
    artUrl = json.str("artUrl"),
    streamUrl = json.str("streamUrl"),
    addedAt = json.str("addedAt"),
    loved = json.bool("loved"),
    rating = json.int("rating") ?: 0,
    playCount = json.int("playCount") ?: 0,
    lastPlayedAt = json.str("lastPlayedAt"),
    path = json.str("path"),
    entryId = json.str("entryId"),
)

internal fun parseAlbum(json: JSONObject): Album = Album(
    id = json.getString("id"),
    title = json.text("title", "Unknown Album"),
    artist = json.text("artist"),
    artistId = json.str("artistId"),
    year = json.int("year"),
    genres = json.strings("genres"),
    trackCount = json.int("trackCount") ?: 0,
    discCount = json.int("discCount") ?: 1,
    durationSeconds = json.dbl("durationSeconds") ?: 0.0,
    artUrl = json.str("artUrl"),
    addedAt = json.str("addedAt"),
    compilation = json.bool("compilation"),
    loved = json.bool("loved"),
    rating = json.int("rating") ?: 0,
    playCount = json.int("playCount") ?: 0,
    lastPlayedAt = json.str("lastPlayedAt"),
)

internal fun parseAlbumDetail(json: JSONObject): AlbumDetail = AlbumDetail(
    album = parseAlbum(json),
    tracks = json.list("tracks", ::parseTrack),
    moreByArtist = json.list("moreByArtist", ::parseAlbum),
    totalBytes = json.long("totalBytes"),
    formats = json.strings("formats"),
)

internal fun parseArtist(json: JSONObject): Artist = Artist(
    id = json.getString("id"),
    name = json.text("name", "Unknown Artist"),
    albumCount = json.int("albumCount") ?: 0,
    trackCount = json.int("trackCount") ?: 0,
    genres = json.strings("genres"),
    artUrl = json.str("artUrl"),
    loved = json.bool("loved"),
    playCount = json.int("playCount") ?: 0,
)

internal fun parseArtistDetail(json: JSONObject): ArtistDetail = ArtistDetail(
    artist = parseArtist(json),
    albums = json.list("albums", ::parseAlbum),
    appearsOn = json.list("appearsOn", ::parseAlbum),
    topTracks = json.list("topTracks", ::parseTrack),
    similar = json.list("similar", ::parseArtist),
)

internal fun parsePlaylist(json: JSONObject): Playlist = Playlist(
    id = json.getString("id"),
    name = json.text("name", "Playlist"),
    description = json.text("description"),
    ownerName = json.str("ownerName"),
    mine = json.bool("mine", true),
    shared = json.bool("shared"),
    collaborative = json.bool("collaborative"),
    smart = json.bool("smart"),
    rules = json.obj("rules")?.let(SmartRules::fromJson),
    trackCount = json.int("trackCount") ?: 0,
    durationSeconds = json.dbl("durationSeconds") ?: 0.0,
    artUrls = json.strings("artUrls"),
    createdAt = json.str("createdAt"),
    updatedAt = json.str("updatedAt"),
    pinned = json.bool("pinned"),
)

internal fun parsePlaylistDetail(json: JSONObject): PlaylistDetail =
    PlaylistDetail(parsePlaylist(json), json.list("tracks", ::parseTrack))

internal fun parseMix(json: JSONObject): Mix = Mix(
    id = json.getString("id"),
    name = json.text("name", "Mix"),
    description = json.text("description"),
    artUrls = json.strings("artUrls"),
    seedType = json.str("seedType"),
    seedId = json.str("seedId"),
)

internal fun parseRecentItem(json: JSONObject): RecentItem = RecentItem(
    kind = json.text("kind"),
    album = json.obj("album")?.let(::parseAlbum),
    playlist = json.obj("playlist")?.let(::parsePlaylist),
    artist = json.obj("artist")?.let(::parseArtist),
    mix = json.obj("mix")?.let(::parseMix),
)

internal fun parseMusicHome(json: JSONObject): MusicHome = MusicHome(
    pinned = json.list("pinned", ::parsePlaylist),
    // An entry whose kind names an object that isn't there is dropped rather than drawn blank.
    recentlyPlayed = json.list("recentlyPlayed", ::parseRecentItem).filter {
        it.album != null || it.playlist != null || it.artist != null || it.mix != null
    },
    recentlyAdded = json.list("recentlyAdded", ::parseAlbum),
    heavyRotation = json.list("heavyRotation", ::parseAlbum),
    mixes = json.list("mixes", ::parseMix),
    forgottenFavorites = json.list("forgottenFavorites", ::parseTrack),
    onThisDay = json.obj("onThisDay")?.let { OnThisDay(it.text("label"), it.list("tracks", ::parseTrack)) }
        ?.takeIf { it.tracks.isNotEmpty() },
    discover = json.list("discover", ::parseAlbum),
    lovedTracks = json.list("lovedTracks", ::parseTrack),
    decades = json.list("decades") { Decade(it.int("decade") ?: 0, it.text("label"), it.int("trackCount") ?: 0) },
    trackCount = json.int("trackCount") ?: 0,
    albumCount = json.int("albumCount") ?: 0,
    artistCount = json.int("artistCount") ?: 0,
)

internal fun parseGenre(json: JSONObject): Genre =
    Genre(json.text("name"), json.int("trackCount") ?: 0, json.int("albumCount") ?: 0, json.strings("artUrls"))

internal fun <T> parsePage(json: JSONObject, parse: (JSONObject) -> T): Page<T> {
    val items = json.list("items", parse)
    return Page(items, json.int("total") ?: items.size)
}

internal fun parseMusicSearch(json: JSONObject): MusicSearch = MusicSearch(
    artists = json.list("artists", ::parseArtist),
    albums = json.list("albums", ::parseAlbum),
    tracks = json.list("tracks", ::parseTrack),
    playlists = json.list("playlists", ::parsePlaylist),
    genres = json.strings("genres"),
)

internal fun parseFolderListing(json: JSONObject): FolderListing = FolderListing(
    libraryId = json.str("libraryId"),
    path = json.text("path"),
    parent = json.str("parent"),
    folders = json.list("folders") { MusicFolder(it.text("name"), it.text("path"), it.int("trackCount") ?: 0) },
    tracks = json.list("tracks", ::parseTrack),
)

internal fun parseLyrics(json: JSONObject): Lyrics {
    val lines = json.list("lines") { LyricLine(it.dbl("time"), it.text("text")) }
    val synced = json.bool("synced") && lines.any { it.time != null }
    return Lyrics(synced, lines, json.str("plain"), json.str("source"))
}

internal fun parseMixResult(json: JSONObject): MixResult =
    MixResult(json.text("name", "Mix"), json.list("tracks", ::parseTrack))

internal fun parseMusicLibrary(json: JSONObject): MusicLibrary = MusicLibrary(
    id = json.getString("id"),
    name = json.text("name", "Music"),
    path = json.text("path"),
    network = json.bool("network"),
    trackCount = json.int("trackCount") ?: 0,
    lastScanFinishedAt = json.str("lastScanFinishedAt"),
    lastScanError = json.str("lastScanError"),
    createdAt = json.str("createdAt"),
)

internal fun parseMusicStatus(json: JSONObject): MusicStatus = MusicStatus(
    enabled = json.bool("enabled", true),
    ffmpegAvailable = json.bool("ffmpegAvailable", true),
    scanning = json.bool("scanning"),
    scan = json.obj("scan")?.let {
        ScanProgress(it.str("phase"), it.str("libraryName"), it.int("processed") ?: 0, it.int("total") ?: 0, it.str("startedAt"))
    },
    libraries = json.list("libraries", ::parseMusicLibrary),
    trackCount = json.int("trackCount") ?: 0,
    albumCount = json.int("albumCount") ?: 0,
    artistCount = json.int("artistCount") ?: 0,
    totalDurationSeconds = json.dbl("totalDurationSeconds") ?: 0.0,
    totalBytes = json.long("totalBytes") ?: 0L,
    lastScanFinishedAt = json.str("lastScanFinishedAt"),
    lastScanError = json.str("lastScanError"),
)

internal fun parseMusicLookup(json: JSONObject): MusicLookup = MusicLookup(
    mbid = json.text("mbid"),
    title = json.text("title"),
    artist = json.text("artist"),
    year = json.int("year"),
    type = json.str("type"),
    trackCount = json.int("trackCount"),
    artUrl = json.str("artUrl"),
    inLibrary = json.bool("inLibrary"),
)

internal fun parseMusicRelease(json: JSONObject): MusicRelease = MusicRelease(
    id = json.text("id"),
    title = json.text("title"),
    site = json.str("site"),
    sizeBytes = json.long("sizeBytes"),
    seeders = json.int("seeders"),
    leechers = json.int("leechers"),
    publishedAt = json.str("publishedAt"),
    format = json.str("format"),
    lossless = json.bool("lossless"),
    matches = json.bool("matches", true),
    warning = json.str("warning"),
)

internal fun parseMusicReleaseSearch(json: JSONObject): MusicReleaseSearch = MusicReleaseSearch(
    id = json.text("id"),
    phase = json.str("phase") ?: "connecting",
    message = json.str("message"),
    error = json.str("error"),
    artist = json.str("artist"),
    album = json.str("album"),
    year = json.int("year"),
    results = json.list("results", ::parseMusicRelease),
    others = json.list("others", ::parseMusicRelease),
)

internal fun parseMusicDownloadStatus(json: JSONObject): MusicDownloadStatus = MusicDownloadStatus(
    enabled = json.bool("enabled"),
    canDownload = json.bool("canDownload"),
    musicLibrary = json.bool("musicLibrary", true),
    status = com.sentinelcore.android.data.parseMediaDownloadServices(json.obj("status")),
)
