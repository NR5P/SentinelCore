package com.sentinelcore.android.ui.music

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.PlaylistAdd
import androidx.compose.material.icons.filled.DownloadDone
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.FavoriteBorder
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlaylistPlay
import androidx.compose.material.icons.filled.Radio
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AlbumDetail
import com.sentinelcore.android.audio.ArtistDetail
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.AudioItem
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.ui.audio.AlbumCard
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.ArtistCircle
import com.sentinelcore.android.ui.audio.HeroHeader
import com.sentinelcore.android.ui.audio.LoadableContent
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.PlayShuffleRow
import com.sentinelcore.android.ui.audio.SectionTitle
import com.sentinelcore.android.ui.audio.Shelf
import com.sentinelcore.android.ui.audio.TrackMenuTarget
import com.sentinelcore.android.ui.audio.TrackRow
import com.sentinelcore.android.ui.audio.rememberLoadable
import com.sentinelcore.android.ui.screens.DetailHeader

// ---------------- Album ----------------

@Composable
fun AlbumScreen(albumId: String) {
    val ui = LocalAudioUi.current
    val detail = rememberLoadable(albumId) { ui.api.album(albumId) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(detail.value?.album?.title ?: "Album", onBack = ui.nav.back)
        LoadableContent(detail) { AlbumContent(it) }
    }
}

@Composable
private fun AlbumContent(detail: AlbumDetail) {
    val ui = LocalAudioUi.current
    val album = detail.album
    val context = PlayContext("album", album.id, album.title)
    var loved by remember(album.id) { mutableStateOf(album.loved) }
    var rating by remember(album.id) { mutableIntStateOf(album.rating) }
    val downloads by ui.runtime.downloads.entries.collectAsState()
    val downloadedCount = detail.tracks.count { track -> downloads.any { it.done && it.type == AudioItem.TYPE_TRACK && it.id == track.id } }
    val downloading = detail.tracks.any { track -> downloads.any { !it.done && it.state != "failed" && it.type == AudioItem.TYPE_TRACK && it.id == track.id } }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
        item {
            HeroHeader(
                art = { ArtImage(album.artUrl, Modifier.fillMaxSize(), sizePx = 800, seed = album.title) },
                title = album.title,
                subtitle = album.artist,
                detail = listOfNotNull(
                    album.genres.firstOrNull(),
                    album.year?.toString(),
                    "${detail.tracks.size} songs, ${AudioFormat.duration(album.durationSeconds.takeIf { it > 0 } ?: detail.tracks.sumOf { it.durationSeconds })}",
                ).joinToString(" · "),
                onSubtitle = album.artistId?.let { id -> { ui.nav.openArtist(id) } },
            )
        }
        item { PlayShuffleRow(onPlay = { ui.playTracks(detail.tracks, 0, context) }, onShuffle = { ui.playTracks(detail.tracks, 0, context, shuffle = true) }) }
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = {
                    ui.launch { loved = ui.api.love("albums", album.id, !loved); ui.libraryVersion++ }
                }) { Icon(if (loved) Icons.Default.Favorite else Icons.Default.FavoriteBorder, contentDescription = if (loved) "Unlove album" else "Love album", tint = if (loved) Color(0xFFE5486B) else MaterialTheme.colorScheme.onSurface) }
                IconButton(onClick = {
                    if (downloadedCount == detail.tracks.size) {
                        detail.tracks.forEach { ui.runtime.downloads.remove(AudioItem.TYPE_TRACK, it.id) }
                        ui.message("Removed the download")
                    } else ui.download(detail.tracks)
                }) {
                    when {
                        downloading -> CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                        downloadedCount == detail.tracks.size && detail.tracks.isNotEmpty() -> Icon(Icons.Default.DownloadDone, contentDescription = "Downloaded - tap to remove", tint = MaterialTheme.colorScheme.primary)
                        else -> Icon(Icons.Default.Download, contentDescription = "Download album")
                    }
                }
                IconButton(onClick = { ui.addToPlaylist = detail.tracks.map { it.id } }) { Icon(Icons.AutoMirrored.Filled.PlaylistAdd, contentDescription = "Add to playlist") }
                IconButton(onClick = { ui.playNext(detail.tracks) }) { Icon(Icons.Default.PlaylistPlay, contentDescription = "Play next") }
                IconButton(onClick = { ui.engine.startRadio("album", album.id, album.title) }) { Icon(Icons.Default.Radio, contentDescription = "Album radio") }
            }
        }
        item {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
                for (star in 1..5) {
                    IconButton(onClick = {
                        val value = if (rating == star) 0 else star
                        rating = value
                        ui.launch { ui.api.rate("albums", album.id, value); ui.libraryVersion++ }
                    }, modifier = Modifier.size(34.dp)) {
                        Icon(if (star <= rating) Icons.Default.Star else Icons.Default.StarBorder, contentDescription = "Rate $star", tint = Color(0xFFFFB400), modifier = Modifier.size(22.dp))
                    }
                }
            }
        }
        val discs = detail.tracks.groupBy { it.discNumber ?: 1 }
        discs.toSortedMap().forEach { (disc, tracks) ->
            if (discs.size > 1) {
                item(key = "disc$disc") {
                    Text("Disc $disc", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(start = 16.dp, top = 14.dp, bottom = 4.dp))
                }
            }
            itemsIndexed(tracks, key = { _, track -> "t" + track.id }) { _, track ->
                val index = detail.tracks.indexOf(track)
                TrackRow(
                    track,
                    number = track.trackNumber ?: (index + 1),
                    showArt = false,
                    onMore = { ui.trackMenu = TrackMenuTarget(track) },
                ) { ui.playTracks(detail.tracks, index, context) }
            }
        }
        item {
            Column(Modifier.fillMaxWidth().padding(16.dp)) {
                album.addedAt?.let { Text("Added ${AudioFormat.day(it)}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                val bytes = detail.totalBytes
                Text(
                    listOfNotNull(detail.formats.joinToString(", ").ifBlank { null }, bytes?.let { AudioFormat.bytes(it) }, if (album.playCount > 0) "You've played it ${album.playCount} times" else null).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        if (detail.moreByArtist.isNotEmpty()) {
            item { SectionTitle("More by ${album.artist}") }
            item { Shelf { items(detail.moreByArtist, key = { it.id }) { AlbumCard(it) { ui.nav.openAlbum(it.id) } } } }
        }
    }
}

// ---------------- Artist ----------------

@Composable
fun ArtistScreen(artistId: String) {
    val ui = LocalAudioUi.current
    val detail = rememberLoadable(artistId) { ui.api.artist(artistId) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(detail.value?.artist?.name ?: "Artist", onBack = ui.nav.back)
        LoadableContent(detail) { ArtistContent(it) }
    }
}

@Composable
private fun ArtistContent(detail: ArtistDetail) {
    val ui = LocalAudioUi.current
    val artist = detail.artist
    var loved by remember(artist.id) { mutableStateOf(artist.loved) }
    var allTop by remember { mutableStateOf(false) }
    val context = PlayContext("artist", artist.id, artist.name)
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
        item {
            HeroHeader(
                art = { ArtImage(artist.artUrl, Modifier.fillMaxSize(), sizePx = 800, shape = CircleShape, fallbackIcon = Icons.Default.Person, seed = artist.name) },
                title = artist.name,
                subtitle = artist.genres.take(3).joinToString(", ").ifBlank { null },
                detail = "${AudioFormat.count(artist.albumCount, "album")} · ${AudioFormat.count(artist.trackCount, "song")}" +
                    if (artist.playCount > 0) " · ${AudioFormat.count(artist.playCount, "play")}" else "",
            )
        }
        item {
            PlayShuffleRow(
                onPlay = { ui.playTracks(detail.topTracks, 0, context) },
                onShuffle = {
                    ui.launch { ui.playTracks(ui.api.tracks(artistId = artist.id, limit = 500).items, 0, context, shuffle = true) }
                },
                playLabel = "Top songs",
            )
        }
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                FilledTonalButton(onClick = { ui.engine.startRadio("artist", artist.id, artist.name) }, modifier = Modifier.weight(1f)) {
                    Icon(Icons.Default.Radio, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("Radio")
                }
                FilledTonalButton(onClick = { ui.launch { loved = ui.api.love("artists", artist.id, !loved); ui.libraryVersion++ } }, modifier = Modifier.weight(1f)) {
                    Icon(if (loved) Icons.Default.Favorite else Icons.Default.FavoriteBorder, contentDescription = null, modifier = Modifier.size(18.dp), tint = if (loved) Color(0xFFE5486B) else MaterialTheme.colorScheme.onSecondaryContainer)
                    Spacer(Modifier.width(6.dp))
                    Text(if (loved) "Loved" else "Love")
                }
            }
        }
        if (detail.topTracks.isNotEmpty()) {
            item { SectionTitle("Top songs", subtitle = "Most played in the household") }
            val shown = if (allTop) detail.topTracks else detail.topTracks.take(5)
            itemsIndexed(shown, key = { _, track -> "top" + track.id }) { index, track ->
                TrackRow(track, showAlbum = true, onMore = { ui.trackMenu = TrackMenuTarget(track) }) { ui.playTracks(detail.topTracks, index, context) }
            }
            if (detail.topTracks.size > 5 && !allTop) item { TextButton(onClick = { allTop = true }, modifier = Modifier.padding(horizontal = 8.dp)) { Text("Show all ${detail.topTracks.size}") } }
        }
        if (detail.albums.isNotEmpty()) {
            item { SectionTitle("Albums") }
            item { Shelf { items(detail.albums, key = { it.id }) { AlbumCard(it) { ui.nav.openAlbum(it.id) } } } }
        }
        if (detail.appearsOn.isNotEmpty()) {
            item { SectionTitle("Appears on") }
            item { Shelf { items(detail.appearsOn, key = { "ap" + it.id }) { AlbumCard(it) { ui.nav.openAlbum(it.id) } } } }
        }
        if (detail.similar.isNotEmpty()) {
            item { SectionTitle("Similar artists") }
            item { Shelf { items(detail.similar, key = { "sim" + it.id }) { ArtistCircle(it) { ui.nav.openArtist(it.id) } } } }
        }
    }
}

// ---------------- Genre and decade ----------------

@Composable
fun GenreScreen(name: String) {
    val ui = LocalAudioUi.current
    var tab by rememberSaveable { mutableIntStateOf(0) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(name, onBack = ui.nav.back)
        PlayShuffleRow(
            onPlay = { ui.engine.startRadio("genre", name, name) },
            onShuffle = { ui.launch { ui.playTracks(ui.api.tracks(genre = name, limit = 500).items, 0, PlayContext("queue", null, name), shuffle = true) } },
            playLabel = "$name radio",
        )
        TabRow(selectedTabIndex = tab, modifier = Modifier.padding(top = 8.dp)) {
            Tab(selected = tab == 0, onClick = { tab = 0 }, text = { Text("Albums") })
            Tab(selected = tab == 1, onClick = { tab = 1 }, text = { Text("Songs") })
        }
        if (tab == 0) AlbumsGrid(initialSort = "plays", genre = name, showToolbar = true) else SongsList(genre = name, initialSort = "plays", contextName = name)
    }
}

@Composable
fun DecadeScreen(decade: Int) {
    val ui = LocalAudioUi.current
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val label = "${decade % 100}".padStart(2, '0') + "s"
    Column(Modifier.fillMaxSize()) {
        DetailHeader("The $label", onBack = ui.nav.back)
        PlayShuffleRow(
            onPlay = {
                ui.launch {
                    val mix = ui.api.namedMix("decade-$decade")
                    ui.playTracks(mix.tracks, 0, PlayContext("mix", "decade-$decade", mix.name.ifBlank { "The $label" }))
                }
            },
            onShuffle = { ui.launch { ui.playTracks(ui.api.tracks(decade = decade, limit = 500).items, 0, PlayContext("queue", null, "The $label"), shuffle = true) } },
            playLabel = "$label mix",
        )
        TabRow(selectedTabIndex = tab, modifier = Modifier.padding(top = 8.dp)) {
            Tab(selected = tab == 0, onClick = { tab = 0 }, text = { Text("Albums") })
            Tab(selected = tab == 1, onClick = { tab = 1 }, text = { Text("Songs") })
        }
        if (tab == 0) AlbumsGrid(initialSort = "year", decade = decade) else SongsList(decade = decade, initialSort = "plays", contextName = "The $label")
    }
}

@Composable
fun AllAlbumsScreen(sort: String) {
    val ui = LocalAudioUi.current
    Column(Modifier.fillMaxSize()) {
        DetailHeader(if (sort == "added") "Recently added" else "Albums", onBack = ui.nav.back)
        AlbumsGrid(initialSort = sort)
    }
}

@Composable
fun FoldersScreen() {
    val ui = LocalAudioUi.current
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Folders", onBack = ui.nav.back)
        FoldersBrowser(null, null)
    }
}

