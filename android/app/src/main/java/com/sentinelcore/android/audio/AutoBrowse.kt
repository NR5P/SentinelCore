package com.sentinelcore.android.audio

import android.net.Uri
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import com.sentinelcore.android.audio.AudioPlaybackService.Companion.folder
import com.sentinelcore.android.audio.AudioPlaybackService.Companion.message
import com.sentinelcore.android.audio.AudioPlaybackService.Companion.playable

// The library as Android Auto (and any other media browser) sees it. Four tabs at the top -
// Music, Playlists, Podcasts, Downloads - each a short, glanceable list, since a car screen is not
// the place for paging through 8,000 songs. Ids:
//   album:<id> / playlist:<id> / show:<id>     browsable (albums and playlists also playable)
//   tr:<trackId>@album:<id> or @playlist:<id>  a track, played within its album or playlist
//   ep:<episodeId>@upnext / @show:<id>          an episode, from Up Next or its show
//   mix:<id>, shuffle, dl:<type>:<id>
internal object AutoBrowse {
    data class PlayRequest(val items: List<AudioItem>, val startIndex: Int, val startSeconds: Double, val context: PlayContext?)

    private fun art(runtime: AudioRuntime, url: String?): Uri? =
        AudioApi.sized(url, 300)?.let(runtime::absoluteUrl)?.let(Uri::parse)

    suspend fun children(runtime: AudioRuntime, parentId: String): List<MediaItem> {
        val api = runtime.api
        val enabled = runtime.enabled.value
        return when {
            parentId == AudioPlaybackService.ROOT -> buildList {
                if (enabled.music) {
                    add(folder("home", "Music", type = MediaMetadata.MEDIA_TYPE_FOLDER_MIXED))
                    add(folder("playlists", "Playlists", type = MediaMetadata.MEDIA_TYPE_FOLDER_PLAYLISTS))
                }
                if (enabled.podcasts) add(folder("podcasts", "Podcasts", type = MediaMetadata.MEDIA_TYPE_FOLDER_PODCASTS))
                add(folder("downloads", "Downloads", type = MediaMetadata.MEDIA_TYPE_FOLDER_MIXED))
            }
            parentId == "home" -> {
                val home = api.musicHome()
                buildList {
                    add(playable("shuffle", "Shuffle all", "${home.trackCount} songs", null, MediaMetadata.MEDIA_TYPE_PLAYLIST))
                    home.mixes.forEach { add(playable("mix:${it.id}", it.name, it.description, art(runtime, it.artUrls.firstOrNull()), MediaMetadata.MEDIA_TYPE_PLAYLIST)) }
                    home.pinned.forEach { add(playlistFolder(runtime, it)) }
                    (home.recentlyPlayed.mapNotNull { it.album } + home.recentlyAdded + home.heavyRotation)
                        .distinctBy { it.id }.take(30).forEach { add(albumFolder(runtime, it)) }
                }
            }
            parentId == "playlists" -> api.playlists().map { playlistFolder(runtime, it) }
            parentId == "podcasts" -> {
                val home = api.podcastHome()
                buildList {
                    home.upNext.forEach { add(episodeItem(runtime, it, "upnext")) }
                    home.subscriptions.forEach {
                        add(folder("show:${it.id}", it.title, if (it.unplayedCount > 0) "${it.unplayedCount} new" else it.author, art(runtime, it.artUrl), type = MediaMetadata.MEDIA_TYPE_PODCAST))
                    }
                    if (isEmpty()) add(message("Nothing here yet", "Follow a show in the Podcasts tab."))
                }
            }
            parentId == "downloads" -> runtime.downloads.entries.value.filter { it.done }.sortedByDescending { it.addedAt }.map {
                playable("dl:${it.type}:${it.id}", it.title, it.subtitle, runtime.downloads.localArt(it.artUrl)?.let(Uri::fromFile) ?: art(runtime, it.artUrl),
                    if (it.type == AudioItem.TYPE_EPISODE) MediaMetadata.MEDIA_TYPE_PODCAST_EPISODE else MediaMetadata.MEDIA_TYPE_MUSIC)
            }.ifEmpty { listOf(message("No downloads", "Download albums and episodes for offline listening.")) }
            parentId.startsWith("album:") -> {
                val album = api.album(parentId.removePrefix("album:"))
                album.tracks.map { playable("tr:${it.id}@$parentId", it.title, it.artist, art(runtime, it.artUrl ?: album.album.artUrl), MediaMetadata.MEDIA_TYPE_MUSIC) }
            }
            parentId.startsWith("playlist:") -> {
                val playlist = api.playlist(parentId.removePrefix("playlist:"))
                playlist.tracks.mapIndexed { index, track -> playable("tr:${track.id}@$parentId#$index", track.title, track.artist, art(runtime, track.artUrl), MediaMetadata.MEDIA_TYPE_MUSIC) }
            }
            parentId.startsWith("show:") -> {
                val id = parentId.removePrefix("show:")
                api.episodes(id, limit = 50).items.map { episodeItem(runtime, it, "show:$id") }
            }
            else -> emptyList()
        }
    }

    private fun albumFolder(runtime: AudioRuntime, album: Album) =
        folder("album:${album.id}", album.title, album.artist, art(runtime, album.artUrl), playable = true, type = MediaMetadata.MEDIA_TYPE_ALBUM)

    private fun playlistFolder(runtime: AudioRuntime, playlist: Playlist) =
        folder("playlist:${playlist.id}", playlist.name, "${playlist.trackCount} songs", art(runtime, playlist.artUrls.firstOrNull()), playable = true, type = MediaMetadata.MEDIA_TYPE_PLAYLIST)

    private fun episodeItem(runtime: AudioRuntime, episode: Episode, from: String): MediaItem {
        val left = if (episode.inProgress) AudioFormat.minutesLeft(episode.remainingSeconds) else AudioFormat.duration(episode.durationSeconds)
        return playable("ep:${episode.id}@$from", episode.title, "${episode.podcastTitle} · $left", art(runtime, episode.artUrl), MediaMetadata.MEDIA_TYPE_PODCAST_EPISODE)
    }

    suspend fun search(runtime: AudioRuntime, query: String): List<MediaItem> {
        if (query.isBlank() || !runtime.enabled.value.music) return emptyList()
        val result = runtime.api.search(query)
        return buildList {
            result.tracks.take(10).forEach { add(playable("tr:${it.id}@album:${it.albumId}", it.title, it.artist, art(runtime, it.artUrl), MediaMetadata.MEDIA_TYPE_MUSIC)) }
            result.albums.take(6).forEach { add(albumFolder(runtime, it)) }
            result.playlists.take(4).forEach { add(playlistFolder(runtime, it)) }
        }
    }

    // What to play for one chosen item, expanded to its context.
    suspend fun resolvePlay(runtime: AudioRuntime, item: MediaItem): PlayRequest? {
        val api = runtime.api
        val id = item.mediaId
        val query = item.requestMetadata.searchQuery
        return when {
            id.isEmpty() && !query.isNullOrBlank() -> {
                // "Hey Google, play Rumours on Sentinel Core".
                val result = api.search(query)
                when {
                    result.albums.isNotEmpty() && (result.tracks.isEmpty() || result.albums.first().title.equals(query, true)) -> {
                        val album = api.album(result.albums.first().id)
                        PlayRequest(album.tracks.map(AudioItem::of), 0, 0.0, PlayContext("album", album.album.id, album.album.title))
                    }
                    result.tracks.isNotEmpty() -> PlayRequest(result.tracks.map(AudioItem::of), 0, 0.0, PlayContext("search", null, query))
                    result.artists.isNotEmpty() -> {
                        val artist = result.artists.first()
                        val mix = api.mix("artist", artist.id)
                        PlayRequest(mix.tracks.map(AudioItem::of), 0, 0.0, PlayContext("radio", "artist:${artist.id}", mix.name))
                    }
                    else -> null
                }
            }
            id == "shuffle" -> PlayRequest(api.shuffleAll().map(AudioItem::of), 0, 0.0, PlayContext("queue", null, "Shuffle all"))
            id.startsWith("mix:") -> {
                val mix = api.namedMix(id.removePrefix("mix:"))
                PlayRequest(mix.tracks.map(AudioItem::of), 0, 0.0, PlayContext("mix", id.removePrefix("mix:"), mix.name))
            }
            id.startsWith("album:") -> {
                val album = api.album(id.removePrefix("album:"))
                PlayRequest(album.tracks.map(AudioItem::of), 0, 0.0, PlayContext("album", album.album.id, album.album.title))
            }
            id.startsWith("playlist:") -> {
                val playlist = api.playlist(id.removePrefix("playlist:"))
                PlayRequest(playlist.tracks.map(AudioItem::of), 0, 0.0, PlayContext("playlist", playlist.playlist.id, playlist.playlist.name))
            }
            id.startsWith("tr:") -> {
                val trackId = id.removePrefix("tr:").substringBefore('@')
                val from = id.substringAfter('@', "")
                when {
                    from.startsWith("album:") && from != "album:null" -> {
                        val album = api.album(from.removePrefix("album:"))
                        PlayRequest(album.tracks.map(AudioItem::of), album.tracks.indexOfFirst { it.id == trackId }.coerceAtLeast(0), 0.0, PlayContext("album", album.album.id, album.album.title))
                    }
                    from.startsWith("playlist:") -> {
                        val playlistId = from.removePrefix("playlist:").substringBefore('#')
                        val position = from.substringAfter('#', "").toIntOrNull()
                        val playlist = api.playlist(playlistId)
                        val start = position?.takeIf { playlist.tracks.getOrNull(it)?.id == trackId } ?: playlist.tracks.indexOfFirst { it.id == trackId }
                        PlayRequest(playlist.tracks.map(AudioItem::of), start.coerceAtLeast(0), 0.0, PlayContext("playlist", playlist.playlist.id, playlist.playlist.name))
                    }
                    else -> null
                }
            }
            id.startsWith("ep:") -> {
                val episodeId = id.removePrefix("ep:").substringBefore('@')
                val from = id.substringAfter('@', "")
                if (from == "upnext") {
                    val queue = api.queue()
                    val start = queue.indexOfFirst { it.id == episodeId }.coerceAtLeast(0)
                    PlayRequest(queue.map(AudioItem::of), start, queue.getOrNull(start)?.resumeSeconds ?: 0.0, PlayContext("queue", null, "Up Next"))
                } else {
                    val episode = api.episode(episodeId)
                    PlayRequest(listOf(AudioItem.of(episode)), 0, episode.resumeSeconds, PlayContext("podcast", episode.podcastId, episode.podcastTitle))
                }
            }
            id.startsWith("dl:") -> {
                val type = id.removePrefix("dl:").substringBefore(':')
                val itemId = id.removePrefix("dl:$type:")
                val entries = runtime.downloads.entries.value.filter { it.done && it.type == type }.sortedByDescending { it.addedAt }
                PlayRequest(entries.map { it.toItem() }, entries.indexOfFirst { it.id == itemId }.coerceAtLeast(0), 0.0, PlayContext("queue", null, "Downloads"))
            }
            else -> null
        }
    }

    // One item added to the queue from a browser: a single track or episode.
    suspend fun resolveSingle(runtime: AudioRuntime, item: MediaItem): AudioItem? {
        val id = item.mediaId
        return when {
            id.startsWith("ep:") -> runCatching { AudioItem.of(runtime.api.episode(id.removePrefix("ep:").substringBefore('@'))) }.getOrNull()
            id.startsWith("tr:") -> resolvePlay(runtime, item)?.let { it.items.getOrNull(it.startIndex) }
            id.startsWith("dl:") -> resolvePlay(runtime, item)?.let { it.items.getOrNull(it.startIndex) }
            else -> null
        }
    }
}
