package com.sentinelcore.android.ui.audio

import android.content.Intent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.PlaylistAdd
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.filled.Album
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.FavoriteBorder
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.PlaylistPlay
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material.icons.filled.Radio
import androidx.compose.material.icons.filled.RemoveCircleOutline
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AudioItem
import com.sentinelcore.android.audio.Episode
import com.sentinelcore.android.audio.PlayContext

// Everything drawn over the screens: Now Playing (sliding up), the long-press menus, the
// playlist picker, the speaker picker and the snackbar.
@Composable
fun AudioOverlays(snackbarBottom: Dp) {
    val ui = LocalAudioUi.current
    val openRequest by AudioNavigation.openNowPlaying.collectAsState()
    LaunchedEffect(openRequest) {
        if (openRequest) {
            AudioNavigation.openNowPlaying.value = false
            if (ui.engine.state.value.hasQueue) ui.nowPlayingOpen = true
        }
    }
    Box(Modifier.fillMaxSize()) {
        SnackbarHost(ui.snackbar, Modifier.align(Alignment.BottomCenter).padding(bottom = snackbarBottom))
        AnimatedVisibility(
            visible = ui.nowPlayingOpen,
            enter = slideInVertically(tween(320)) { it },
            exit = slideOutVertically(tween(260)) { it },
        ) {
            Box(Modifier.fillMaxSize()) {
                NowPlayingScreen(onClose = { ui.nowPlayingOpen = false })
                SnackbarHost(ui.snackbar, Modifier.align(Alignment.BottomCenter).padding(bottom = 24.dp))
            }
        }
    }
    ui.trackMenu?.let { TrackMenuSheet(it) { ui.trackMenu = null } }
    ui.episodeMenu?.let { EpisodeMenuSheet(it) { ui.episodeMenu = null } }
    ui.addToPlaylist?.let { AddToPlaylistSheet(it) { ui.addToPlaylist = null } }
    if (ui.speakerPicker) SpeakerPickerSheet { ui.speakerPicker = false }
}

@Composable
private fun MenuRow(icon: ImageVector, label: String, tint: Color = MaterialTheme.colorScheme.onSurface, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 24.dp, vertical = 13.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, contentDescription = null, tint = tint)
        Spacer(Modifier.width(18.dp))
        Text(label, color = tint, style = MaterialTheme.typography.bodyLarge)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TrackMenuSheet(target: TrackMenuTarget, onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    val track = target.track
    val downloads by ui.runtime.downloads.entries.collectAsState()
    val downloaded = downloads.any { it.type == AudioItem.TYPE_TRACK && it.id == track.id }
    var loved by remember(track.id) { mutableStateOf(track.loved) }
    var rating by remember(track.id) { mutableStateOf(track.rating) }
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                ArtImage(track.artUrl, Modifier.size(56.dp), sizePx = 168, seed = track.album)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(track.title, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(listOf(track.artist, track.album).filter { it.isNotBlank() }.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                IconButton(onClick = { ui.loveTrack(track.copy(loved = loved), !loved) { loved = it } }) {
                    Icon(if (loved) Icons.Default.Favorite else Icons.Default.FavoriteBorder, contentDescription = if (loved) "Unlove" else "Love", tint = if (loved) Color(0xFFE5486B) else MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            // Tap a star to rate; tap the same star again to clear it.
            Row(Modifier.padding(horizontal = 88.dp)) {
                for (star in 1..5) {
                    IconButton(onClick = {
                        val value = if (rating == star) 0 else star
                        rating = value
                        ui.rateTrack(track, value)
                    }, modifier = Modifier.size(36.dp)) {
                        Icon(if (star <= rating) Icons.Default.Star else Icons.Default.StarBorder, contentDescription = "$star stars", tint = Color(0xFFFFB400))
                    }
                }
            }
            HorizontalDivider(Modifier.padding(vertical = 6.dp))
            MenuRow(Icons.Default.PlaylistPlay, "Play next") { ui.playNext(listOf(track)); onDismiss() }
            MenuRow(Icons.AutoMirrored.Filled.QueueMusic, "Add to queue") { ui.addToQueue(listOf(track)); onDismiss() }
            MenuRow(Icons.AutoMirrored.Filled.PlaylistAdd, "Add to playlist…") { onDismiss(); ui.addToPlaylist = listOf(track.id) }
            MenuRow(Icons.Default.Radio, "Start radio") { ui.engine.startRadio("track", track.id, track.title); onDismiss() }
            track.albumId?.let { id -> MenuRow(Icons.Default.Album, "Go to album") { onDismiss(); ui.nowPlayingOpen = false; ui.nav.openAlbum(id) } }
            track.artistId?.let { id -> MenuRow(Icons.Default.Person, "Go to artist") { onDismiss(); ui.nowPlayingOpen = false; ui.nav.openArtist(id) } }
            if (downloaded) {
                MenuRow(Icons.Default.Delete, "Remove download") { ui.runtime.downloads.remove(AudioItem.TYPE_TRACK, track.id); onDismiss() }
            } else {
                MenuRow(Icons.Default.Download, "Download") { ui.download(listOf(track)); onDismiss() }
            }
            if (target.canRemove && target.playlistId != null && target.entryId != null) {
                MenuRow(Icons.Default.RemoveCircleOutline, "Remove from this playlist", MaterialTheme.colorScheme.error) {
                    ui.launch("Couldn't remove it.") {
                        ui.api.removeFromPlaylist(target.playlistId, target.entryId)
                        ui.libraryVersion++
                        ui.message("Removed \"${track.title}\"")
                    }
                    onDismiss()
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun EpisodeMenuSheet(episode: Episode, onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    val context = LocalContext.current
    val downloads by ui.runtime.downloads.entries.collectAsState()
    val downloaded = downloads.any { it.type == AudioItem.TYPE_EPISODE && it.id == episode.id }
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                ArtImage(episode.artUrl, Modifier.size(56.dp), sizePx = 168, fallbackIcon = Icons.Default.Podcasts, seed = episode.podcastTitle)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(episode.title, style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Text(episode.podcastTitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            HorizontalDivider(Modifier.padding(vertical = 6.dp))
            MenuRow(Icons.Default.PlayArrow, if (episode.inProgress) "Resume" else "Play") { ui.playEpisode(episode); onDismiss() }
            if (episode.inQueue) {
                MenuRow(Icons.Default.RemoveCircleOutline, "Remove from Up Next") {
                    ui.launch { ui.api.dequeue(episode.id); ui.libraryVersion++; ui.message("Removed from Up Next") }
                    onDismiss()
                }
            } else {
                MenuRow(Icons.Default.PlaylistPlay, "Play next in Up Next") {
                    ui.launch { ui.api.enqueue(episode.id, "next"); ui.libraryVersion++; ui.message("Next in Up Next") }
                    onDismiss()
                }
                MenuRow(Icons.AutoMirrored.Filled.QueueMusic, "Add to the end of Up Next") {
                    ui.launch { ui.api.enqueue(episode.id, "last"); ui.libraryVersion++; ui.message("Added to Up Next") }
                    onDismiss()
                }
            }
            MenuRow(Icons.Default.Check, if (episode.played) "Mark unplayed" else "Mark played") {
                ui.launch {
                    ui.api.setEpisodePlayed(episode.id, !episode.played)
                    ui.libraryVersion++
                }
                onDismiss()
            }
            if (downloaded) {
                MenuRow(Icons.Default.Delete, "Remove from this phone") { ui.runtime.downloads.remove(AudioItem.TYPE_EPISODE, episode.id); onDismiss() }
            } else {
                MenuRow(Icons.Default.Download, "Download to this phone") {
                    ui.runtime.downloads.downloadEpisode(episode)
                    ui.message("Downloading \"${episode.title}\"", action = "View") { ui.nav.open(AudioRoutes.DOWNLOADS) }
                    onDismiss()
                }
            }
            if (episode.download?.state != "done" && episode.download?.state != "downloading") {
                MenuRow(Icons.Default.CloudDownload, "Keep on the server") {
                    ui.launch { ui.api.downloadEpisodeOnServer(episode.id); ui.libraryVersion++; ui.message("The server is downloading it") }
                    onDismiss()
                }
            }
            MenuRow(Icons.Default.Info, "Episode details") { onDismiss(); ui.nowPlayingOpen = false; ui.nav.openEpisode(episode.id) }
            if (episode.podcastId.isNotBlank()) {
                MenuRow(Icons.Default.Podcasts, "Go to show") { onDismiss(); ui.nowPlayingOpen = false; ui.nav.openShow(episode.podcastId) }
            }
            episode.link?.let { link ->
                MenuRow(Icons.Default.Share, "Share") {
                    context.startActivity(
                        Intent.createChooser(
                            Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, "${episode.podcastTitle}: ${episode.title}\n$link"),
                            "Share episode",
                        ),
                    )
                    onDismiss()
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AddToPlaylistSheet(trackIds: List<String>, onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    val playlists = rememberLoadable(Unit) { ui.api.playlists().filter { it.canEditTracks(ui.isAdmin) } }
    var newName by remember { mutableStateOf("") }
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
            Text(
                if (trackIds.size == 1) "Add to playlist" else "Add ${trackIds.size} songs to a playlist",
                style = MaterialTheme.typography.titleLarge,
                modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp),
            )
            Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = newName,
                    onValueChange = { newName = it },
                    placeholder = { Text("New playlist name") },
                    singleLine = true,
                    modifier = Modifier.weight(1f),
                )
                TextButton(enabled = newName.isNotBlank(), onClick = {
                    val name = newName.trim()
                    ui.launch("Couldn't make the playlist.") {
                        val created = ui.api.createPlaylist(name, trackIds = trackIds)
                        ui.libraryVersion++
                        ui.message("Made \"${created.name}\"", action = "Open") { ui.nav.openPlaylist(created.id) }
                    }
                    onDismiss()
                }) { Text("Create") }
            }
            val list = playlists.value
            when {
                list == null && playlists.error == null -> CircularProgressIndicator(Modifier.align(Alignment.CenterHorizontally).padding(16.dp))
                list == null -> Text(playlists.error.orEmpty(), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(20.dp))
                list.isEmpty() -> Text("No playlists you can add to yet.", modifier = Modifier.padding(20.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                else -> list.forEach { playlist ->
                    Row(
                        Modifier.fillMaxWidth().clickable {
                            ui.launch("Couldn't add to ${playlist.name}.") {
                                ui.api.addToPlaylist(playlist.id, trackIds)
                                ui.libraryVersion++
                                ui.message("Added to \"${playlist.name}\"", action = "Open") { ui.nav.openPlaylist(playlist.id) }
                            }
                            onDismiss()
                        }.padding(horizontal = 20.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        MosaicArt(playlist.artUrls, Modifier.size(48.dp), seed = playlist.name, shape = RoundedCornerShape(6.dp), sizePx = 144)
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(playlist.name, fontWeight = FontWeight.Medium)
                            Text(playlistSubtitle(playlist) + if (playlist.collaborative && !playlist.mine) " · collaborative" else "", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }
}

