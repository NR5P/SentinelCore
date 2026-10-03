package com.sentinelcore.android.audio

import android.app.PendingIntent
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSourceBitmapLoader
import androidx.media3.session.CacheBitmapLoader
import androidx.media3.session.CommandButton
import androidx.media3.session.DefaultMediaNotificationProvider
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaLibraryService
import androidx.media3.session.MediaSession
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionResult
import com.google.common.collect.ImmutableList
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import com.sentinelcore.android.MainActivity
import com.sentinelcore.android.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.guava.future
import kotlinx.coroutines.launch
import java.util.concurrent.Executors

// The shared player's home outside the app's screens (§11): the media notification with art, the
// lock screen, headset and Bluetooth buttons, and a browsable library for Android Auto. The player
// itself belongs to AudioEngine; this wraps it in a MediaLibrarySession.
@OptIn(UnstableApi::class)
class AudioPlaybackService : MediaLibraryService() {
    private lateinit var runtime: AudioRuntime
    private var session: MediaLibrarySession? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    override fun onCreate() {
        super.onCreate()
        runtime = AudioRuntime.get(this)
        val engine = runtime.engine
        val bitmaps = CacheBitmapLoader(
            DataSourceBitmapLoader(MoreExecutors.listeningDecorator(Executors.newSingleThreadExecutor()), engine.dataSourceFactory),
        )
        val openApp = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java)
                .setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
                .putExtra(MainActivity.NowPlayingExtra, true),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        session = MediaLibrarySession.Builder(this, engine.player, Callback())
            .setSessionActivity(openApp)
            .setBitmapLoader(bitmaps)
            .build()
        setMediaNotificationProvider(
            DefaultMediaNotificationProvider.Builder(this)
                .setChannelId(CHANNEL)
                .setChannelName(R.string.audio_channel_name)
                .build()
                .apply { setSmallIcon(R.drawable.ic_notification) },
        )
        // Skip buttons for podcasts, a heart for music - swapped as the playing item changes.
        scope.launch {
            engine.state.map { it.current?.let { item -> item.type to (item.track?.loved == true) } }
                .distinctUntilChanged()
                .collect { session?.setCustomLayout(buttonsFor(it)) }
        }
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaLibrarySession? = session

    // Swiping the app away while paused ends the notification; while playing, music carries on.
    override fun onTaskRemoved(rootIntent: Intent?) {
        val player = session?.player
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0) stopSelf()
    }

    override fun onDestroy() {
        scope.cancel()
        session?.release()
        session = null
        super.onDestroy()
    }

    private fun buttonsFor(current: Pair<String, Boolean>?): List<CommandButton> {
        current ?: return emptyList()
        return if (current.first == AudioItem.TYPE_EPISODE) {
            listOf(
                CommandButton.Builder(CommandButton.ICON_SKIP_BACK_15)
                    .setPlayerCommand(Player.COMMAND_SEEK_BACK)
                    .setDisplayName("Back 15 seconds")
                    .build(),
                CommandButton.Builder(CommandButton.ICON_SKIP_FORWARD_30)
                    .setPlayerCommand(Player.COMMAND_SEEK_FORWARD)
                    .setDisplayName("Forward 30 seconds")
                    .build(),
            )
        } else {
            listOf(
                CommandButton.Builder(if (current.second) CommandButton.ICON_HEART_FILLED else CommandButton.ICON_HEART_UNFILLED)
                    .setSessionCommand(SessionCommand(LOVE, Bundle.EMPTY))
                    .setDisplayName(if (current.second) "Loved" else "Love")
                    .build(),
            )
        }
    }

    private inner class Callback : MediaLibrarySession.Callback {
        override fun onConnect(session: MediaSession, controller: MediaSession.ControllerInfo): MediaSession.ConnectionResult {
            val commands = MediaSession.ConnectionResult.DEFAULT_SESSION_AND_LIBRARY_COMMANDS.buildUpon()
                .add(SessionCommand(LOVE, Bundle.EMPTY))
                .build()
            val current = runtime.engine.state.value.current
            return MediaSession.ConnectionResult.AcceptedResultBuilder(session)
                .setAvailableSessionCommands(commands)
                .setCustomLayout(buttonsFor(current?.let { it.type to (it.track?.loved == true) }))
                .build()
        }

        override fun onCustomCommand(session: MediaSession, controller: MediaSession.ControllerInfo, customCommand: SessionCommand, args: Bundle): ListenableFuture<SessionResult> {
            if (customCommand.customAction != LOVE) return super.onCustomCommand(session, controller, customCommand, args)
            return scope.future {
                val track = runtime.engine.state.value.current?.track ?: return@future SessionResult(SessionResult.RESULT_ERROR_BAD_VALUE)
                runCatching { runtime.api.love("tracks", track.id, !track.loved) }
                    .onSuccess { loved -> runtime.engine.updateTrack(track.copy(loved = loved)) }
                SessionResult(SessionResult.RESULT_SUCCESS)
            }
        }

        override fun onGetLibraryRoot(session: MediaLibrarySession, browser: MediaSession.ControllerInfo, params: LibraryParams?): ListenableFuture<LibraryResult<MediaItem>> =
            Futures.immediateFuture(LibraryResult.ofItem(folder(ROOT, "Sentinel Core"), params))

        override fun onGetItem(session: MediaLibrarySession, browser: MediaSession.ControllerInfo, mediaId: String): ListenableFuture<LibraryResult<MediaItem>> =
            Futures.immediateFuture(LibraryResult.ofItem(folder(mediaId, mediaId.substringAfter(':')), null))

        override fun onGetChildren(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            parentId: String,
            page: Int,
            pageSize: Int,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> = scope.future {
            val children = runCatching { AutoBrowse.children(runtime, parentId) }.getOrElse {
                listOf(message("Can't reach Sentinel Core", it.message ?: "Check the connection."))
            }
            val from = (page * pageSize).coerceAtMost(children.size)
            val to = if (pageSize <= 0) children.size else (from + pageSize).coerceAtMost(children.size)
            LibraryResult.ofItemList(children.subList(from, to), params)
        }

        override fun onSearch(session: MediaLibrarySession, browser: MediaSession.ControllerInfo, query: String, params: LibraryParams?): ListenableFuture<LibraryResult<Void>> {
            scope.launch {
                val count = runCatching { AutoBrowse.search(runtime, query).size }.getOrDefault(0)
                session.notifySearchResultChanged(browser, query, count, params)
            }
            return Futures.immediateFuture(LibraryResult.ofVoid())
        }

        override fun onGetSearchResult(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            query: String,
            page: Int,
            pageSize: Int,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> = scope.future {
            LibraryResult.ofItemList(runCatching { AutoBrowse.search(runtime, query) }.getOrDefault(emptyList()), params)
        }

        // Android Auto (or the Assistant) asked to play something from the library: expand it to
        // its album, playlist, queue or mix so Next works the way people expect.
        override fun onSetMediaItems(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            mediaItems: MutableList<MediaItem>,
            startIndex: Int,
            startPositionMs: Long,
        ): ListenableFuture<MediaSession.MediaItemsWithStartPosition> = scope.future {
            val first = mediaItems.firstOrNull()
            val request = if (mediaItems.size == 1 && first != null) AutoBrowse.resolvePlay(runtime, first) else null
            if (request != null && request.items.isNotEmpty()) {
                MediaSession.MediaItemsWithStartPosition(
                    runtime.engine.register(request.items, request.context),
                    request.startIndex,
                    (request.startSeconds * 1000).toLong(),
                )
            } else {
                val items = mediaItems.mapNotNull { AutoBrowse.resolveSingle(runtime, it) }
                MediaSession.MediaItemsWithStartPosition(runtime.engine.register(items, PlayContext("queue")), startIndex.coerceAtLeast(0), startPositionMs)
            }
        }

        override fun onAddMediaItems(mediaSession: MediaSession, controller: MediaSession.ControllerInfo, mediaItems: MutableList<MediaItem>): ListenableFuture<MutableList<MediaItem>> =
            scope.future {
                runtime.engine.registerAdded(mediaItems.mapNotNull { AutoBrowse.resolveSingle(runtime, it) }).toMutableList()
            }

        // A Bluetooth play press after the app was gone: pick up this phone's last queue, which
        // the server kept as its now-playing.
        override fun onPlaybackResumption(mediaSession: MediaSession, controller: MediaSession.ControllerInfo): ListenableFuture<MediaSession.MediaItemsWithStartPosition> =
            scope.future {
                val engine = runtime.engine
                val state = engine.state.value
                if (state.hasQueue) {
                    return@future MediaSession.MediaItemsWithStartPosition(state.queue.map(engine::mediaItem), state.index, engine.player.currentPosition)
                }
                val resolved = runtime.api.resolveNowPlaying(runtime.deviceId)
                val items = engine.buildFromRefs(resolved.nowPlaying.queue, resolved.tracks, resolved.episodes)
                MediaSession.MediaItemsWithStartPosition(
                    engine.register(items, resolved.nowPlaying.context),
                    resolved.nowPlaying.index.coerceIn(0, (items.size - 1).coerceAtLeast(0)),
                    (resolved.nowPlaying.positionSeconds * 1000).toLong(),
                )
            }
    }

    companion object {
        const val LOVE = "sentinel.love"
        const val CHANNEL = "audio_playback"
        const val ROOT = "root"

        fun folder(id: String, title: String, subtitle: String? = null, art: Uri? = null, playable: Boolean = false, type: Int = MediaMetadata.MEDIA_TYPE_FOLDER_MIXED): MediaItem =
            MediaItem.Builder()
                .setMediaId(id)
                .setMediaMetadata(
                    MediaMetadata.Builder()
                        .setTitle(title)
                        .setSubtitle(subtitle)
                        .setArtist(subtitle)
                        .setArtworkUri(art)
                        .setIsBrowsable(true)
                        .setIsPlayable(playable)
                        .setMediaType(type)
                        .build(),
                )
                .build()

        fun playable(id: String, title: String, subtitle: String?, art: Uri?, type: Int): MediaItem =
            MediaItem.Builder()
                .setMediaId(id)
                .setMediaMetadata(
                    MediaMetadata.Builder()
                        .setTitle(title)
                        .setSubtitle(subtitle)
                        .setArtist(subtitle)
                        .setArtworkUri(art)
                        .setIsBrowsable(false)
                        .setIsPlayable(true)
                        .setMediaType(type)
                        .build(),
                )
                .build()

        fun message(title: String, subtitle: String): MediaItem =
            MediaItem.Builder()
                .setMediaId("message:$title")
                .setMediaMetadata(MediaMetadata.Builder().setTitle(title).setSubtitle(subtitle).setIsBrowsable(false).setIsPlayable(false).build())
                .build()
    }
}
