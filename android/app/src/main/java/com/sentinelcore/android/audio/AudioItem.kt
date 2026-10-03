package com.sentinelcore.android.audio

import java.util.concurrent.atomic.AtomicLong

// One entry in the player's queue: a track or an episode, with what the player, the notification
// and Now Playing need to show it. `uid` tells two copies of the same track apart.
data class AudioItem(
    val uid: Long,
    // track or episode.
    val type: String,
    val id: String,
    val title: String,
    val subtitle: String,
    val album: String? = null,
    val artUrl: String? = null,
    val durationSeconds: Double = 0.0,
    val track: Track? = null,
    val episode: Episode? = null,
) {
    val isEpisode: Boolean get() = type == TYPE_EPISODE
    val ref: QueueRef get() = QueueRef(type, id)
    val key: String get() = "$type:$id"

    companion object {
        const val TYPE_TRACK = "track"
        const val TYPE_EPISODE = "episode"
        private val next = AtomicLong(1)

        fun newUid(): Long = next.getAndIncrement()

        fun of(track: Track): AudioItem = AudioItem(
            uid = newUid(),
            type = TYPE_TRACK,
            id = track.id,
            title = track.title,
            subtitle = track.artist,
            album = track.album,
            artUrl = track.artUrl ?: "/api/music/tracks/${AudioApi.seg(track.id)}/art",
            durationSeconds = track.durationSeconds,
            track = track,
        )

        fun of(episode: Episode): AudioItem = AudioItem(
            uid = newUid(),
            type = TYPE_EPISODE,
            id = episode.id,
            title = episode.title,
            subtitle = episode.podcastTitle,
            album = episode.podcastTitle,
            artUrl = episode.artUrl ?: "/api/podcasts/episodes/${AudioApi.seg(episode.id)}/art",
            durationSeconds = episode.durationSeconds,
            episode = episode,
        )

        // When only an id is known (a speaker command that could not be resolved, or an offline
        // download): enough to play it; the title fills in when it can.
        fun bare(type: String, id: String, title: String = if (type == TYPE_EPISODE) "Episode" else "Track", subtitle: String = ""): AudioItem = AudioItem(
            uid = newUid(),
            type = type,
            id = id,
            title = title,
            subtitle = subtitle,
            artUrl = if (type == TYPE_EPISODE) "/api/podcasts/episodes/${AudioApi.seg(id)}/art" else "/api/music/tracks/${AudioApi.seg(id)}/art",
        )
    }
}
