package com.sentinelcore.android.ui.podcasts

import android.content.Intent
import android.net.Uri
import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.audio.Podcast
import com.sentinelcore.android.audio.PodcastMySettings
import com.sentinelcore.android.audio.PodcastSettings
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.EmptyPanel
import com.sentinelcore.android.ui.audio.EpisodeRow
import com.sentinelcore.android.ui.audio.HeroHeader
import com.sentinelcore.android.ui.audio.LoadableContent
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.rememberLoadable
import com.sentinelcore.android.ui.music.SwitchLine
import com.sentinelcore.android.ui.music.rememberPaged
import com.sentinelcore.android.ui.screens.DetailHeader

private val episodeFilters = listOf("all" to "All", "unplayed" to "Unplayed", "inProgress" to "In progress", "downloaded" to "Downloaded", "bookmarked" to "Bookmarked")

// A show's page (§6): follow, settings, funding, and its episodes with filters, seasons and search.
@Composable
fun ShowScreen(podcastId: String) {
    val ui = LocalAudioUi.current
    val show = rememberLoadable(podcastId) { ui.api.podcast(podcastId) }
    var menu by remember { mutableStateOf(false) }
    var settingsOpen by remember { mutableStateOf(false) }
    var confirmMarkAll by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    var refreshing by remember { mutableStateOf(false) }
    val value = show.value
    Column(Modifier.fillMaxSize()) {
        DetailHeader(value?.title ?: "Podcast", onBack = ui.nav.back) {
            if (value != null) {
                IconButton(enabled = !refreshing, onClick = {
                    refreshing = true
                    ui.launch("Couldn't read the feed.") {
                        try {
                            show.value = ui.api.refreshPodcast(value.id)
                            ui.libraryVersion++
                            ui.message("Up to date")
                        } finally {
                            refreshing = false
                        }
                    }
                }) {
                    if (refreshing) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Icon(Icons.Default.Refresh, contentDescription = "Check for new episodes")
                }
                IconButton(onClick = { settingsOpen = true }) { Icon(Icons.Default.Settings, contentDescription = "Show settings") }
                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "More") }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text("Mark all played") }, onClick = { menu = false; confirmMarkAll = true })
                        DropdownMenuItem(text = { Text("Mark all unplayed") }, onClick = {
                            menu = false
                            ui.launch { ui.api.markAllPlayed(value.id, false); ui.libraryVersion++ }
                        })
                        if (ui.isAdmin) DropdownMenuItem(text = { Text("Remove from the server", color = MaterialTheme.colorScheme.error) }, onClick = { menu = false; confirmDelete = true })
                    }
                }
            }
        }
        LoadableContent(show) { podcast -> ShowContent(podcast) { show.value = it } }
    }
    if (settingsOpen && value != null) ShowSettingsSheet(value, onSaved = { show.value = it }, onDismiss = { settingsOpen = false })
    if (confirmMarkAll && value != null) {
        AlertDialog(
            onDismissRequest = { confirmMarkAll = false },
            title = { Text("Mark every episode played?") },
            text = { Text("Handy for a show you're starting from the newest. Your progress on each is cleared.") },
            confirmButton = { TextButton(onClick = { confirmMarkAll = false; ui.launch { ui.api.markAllPlayed(value.id, true); ui.libraryVersion++ } }) { Text("Mark played") } },
            dismissButton = { TextButton(onClick = { confirmMarkAll = false }) { Text("Cancel") } },
        )
    }
    if (confirmDelete && value != null) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Remove ${value.title}?") },
            text = { Text("The server forgets the show, its episodes and its downloads, for everyone (${value.subscriberCount} following).") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDelete = false
                    ui.launch("Couldn't remove it.") { ui.api.deletePodcast(value.id); ui.libraryVersion++; ui.nav.back() }
                }) { Text("Remove", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun ShowContent(podcast: Podcast, onChanged: (Podcast) -> Unit) {
    val ui = LocalAudioUi.current
    val context = LocalContext.current
    var filter by rememberSaveable { mutableStateOf("all") }
    var sort by rememberSaveable { mutableStateOf(podcast.mySettings?.sort ?: "newest") }
    var season by rememberSaveable { mutableStateOf<Int?>(null) }
    var query by rememberSaveable { mutableStateOf("") }
    var expanded by remember { mutableStateOf(false) }
    var seasons by remember { mutableStateOf<List<Int>>(emptyList()) }
    val scope = rememberCoroutineScope()
    val paged = rememberPaged(podcast.id, filter, sort, season, query.trim()) { offset ->
        val page = ui.api.episodes(podcast.id, sort, filter, query.trim(), season, offset)
        if (page.seasons.isNotEmpty()) seasons = page.seasons
        com.sentinelcore.android.audio.Page(page.items, page.total)
    }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
        item {
            HeroHeader(
                art = { ArtImage(podcast.artUrl, Modifier.fillMaxSize(), sizePx = 800, fallbackIcon = Icons.Default.Podcasts, seed = podcast.title) },
                title = podcast.title,
                subtitle = podcast.author,
                detail = listOfNotNull(
                    podcast.categories.take(2).joinToString(", ").ifBlank { null },
                    AudioFormat.count(podcast.episodeCount, "episode"),
                    podcast.newestEpisodeAt?.let { "updated ${AudioFormat.day(it).lowercase()}" },
                    "Explicit".takeIf { podcast.explicit },
                ).joinToString(" · "),
            )
        }
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                if (podcast.subscribed) {
                    OutlinedButton(onClick = {
                        ui.launch("Couldn't unfollow.") {
                            ui.api.unsubscribe(podcast.id)
                            onChanged(podcast.copy(subscribed = false, subscriberCount = (podcast.subscriberCount - 1).coerceAtLeast(0)))
                            ui.libraryVersion++
                            ui.message("Unfollowed ${podcast.title}")
                        }
                    }, modifier = Modifier.weight(1f)) { Text("Following ✓") }
                } else {
                    Button(onClick = {
                        ui.launch("Couldn't follow.") {
                            onChanged(ui.api.subscribe(podcast.feedUrl, null))
                            ui.libraryVersion++
                        }
                    }, modifier = Modifier.weight(1f)) { Text("Follow") }
                }
                Button(
                    onClick = {
                        ui.launch {
                            val unplayed = ui.api.episodes(podcast.id, sort = "oldest", filter = "unplayed", limit = 50).items
                            if (unplayed.isEmpty()) ui.message("Nothing unplayed") else ui.engine.playEpisodes(unplayed, 0, PlayContext("podcast", podcast.id, podcast.title))
                        }
                    },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.filledTonalButtonColors(),
                ) { Text(if (podcast.unplayedCount > 0) "Play ${podcast.unplayedCount} unplayed" else "Play") }
            }
        }
        podcast.refreshError?.let { error ->
            item {
                Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Default.Warning, contentDescription = null, tint = MaterialTheme.colorScheme.error, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text("The feed couldn't be read last time: $error", color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                }
            }
        }
        if (podcast.description.isNotBlank()) {
            item {
                Text(
                    podcast.description,
                    style = MaterialTheme.typography.bodyMedium,
                    maxLines = if (expanded) Int.MAX_VALUE else 3,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.fillMaxWidth().animateContentSize().clickable { expanded = !expanded }.padding(16.dp),
                )
            }
        }
        if (podcast.funding.isNotEmpty() || podcast.link != null) {
            item {
                Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    podcast.funding.forEach { fund ->
                        OutlinedButton(onClick = { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(fund.url))) }) {
                            Icon(Icons.Default.Favorite, contentDescription = null, modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(6.dp))
                            Text(fund.title)
                        }
                    }
                    podcast.link?.let { link -> TextButton(onClick = { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(link))) }) { Text("Website") } }
                }
            }
        }
        item { HorizontalDivider(Modifier.padding(top = 12.dp)) }
        item {
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                episodeFilters.forEach { (key, label) -> FilterChip(selected = filter == key, onClick = { filter = key }, label = { Text(label) }) }
            }
        }
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = query,
                    onValueChange = { query = it },
                    placeholder = { Text("Search episodes") },
                    leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                    trailingIcon = { if (query.isNotEmpty()) IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, contentDescription = "Clear") } },
                    singleLine = true,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = { sort = if (sort == "newest") "oldest" else "newest" }) { Text(if (sort == "newest") "Newest first" else "Oldest first") }
            }
        }
        if (seasons.size > 1) {
            item {
                Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    FilterChip(selected = season == null, onClick = { season = null }, label = { Text("All seasons") })
                    seasons.forEach { s -> FilterChip(selected = season == s, onClick = { season = s }, label = { Text("Season $s") }) }
                }
            }
        }
        itemsIndexed(paged.items, key = { _, episode -> episode.id }) { index, episode ->
            if (index >= paged.items.size - 8) LaunchedEffect(index) { paged.more(scope) }
            EpisodeRow(episode, showShow = false, onMore = { ui.episodeMenu = episode }) { ui.nav.openEpisode(episode.id) }
        }
        item {
            when {
                paged.loading -> Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(28.dp)) }
                paged.error != null -> Text(paged.error.orEmpty(), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp))
                paged.items.isEmpty() -> EmptyPanel(Icons.Default.Podcasts, "No episodes", if (filter == "all" && query.isBlank()) "This feed has no episodes yet." else "None match.")
            }
        }
    }
}

// Per-person settings for a show, and the household ones for an admin (§5).
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ShowSettingsSheet(podcast: Podcast, onSaved: (Podcast) -> Unit, onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    var mine by remember { mutableStateOf(podcast.mySettings ?: PodcastMySettings()) }
    var household by remember { mutableStateOf(podcast.settings ?: PodcastSettings()) }
    var speed by remember { mutableFloatStateOf((mine.playbackSpeed ?: 0.0).toFloat()) }
    var keep by remember { mutableIntStateOf(household.keepEpisodes) }
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp).padding(bottom = 28.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text("${podcast.title} settings", style = MaterialTheme.typography.titleLarge)
            Text("Yours", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(top = 8.dp))
            Text(
                if (speed <= 0f) "Speed: the app's default (${AudioFormat.speed(ui.runtime.prefs.podcastSpeed.toDouble())})" else "Speed: ${AudioFormat.speed(speed.toDouble())}",
                style = MaterialTheme.typography.bodyMedium,
            )
            Slider(value = speed, onValueChange = { speed = if (it < 0.45f) 0f else (it * 20).toInt() / 20f }, valueRange = 0f..3f)
            NumberLine("Skip the first (seconds)", mine.skipIntroSeconds) { mine = mine.copy(skipIntroSeconds = it) }
            NumberLine("Skip the last (seconds)", mine.skipOutroSeconds) { mine = mine.copy(skipOutroSeconds = it) }
            SwitchLine("Notify me of new episodes", mine.notify) { mine = mine.copy(notify = it) }
            SwitchLine("Add new episodes to Up Next", mine.autoQueue) { mine = mine.copy(autoQueue = it) }
            SwitchLine("Include in my Morning Briefing", mine.inBriefing, "Its newest episode plays after the weather and your day.") { mine = mine.copy(inBriefing = it) }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Episode order", modifier = Modifier.weight(1f))
                FilterChip(selected = mine.sort == "newest", onClick = { mine = mine.copy(sort = "newest") }, label = { Text("Newest first") })
                Spacer(Modifier.width(6.dp))
                FilterChip(selected = mine.sort == "oldest", onClick = { mine = mine.copy(sort = "oldest") }, label = { Text("Oldest first") })
            }
            if (ui.isAdmin) {
                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                Text("For the household (admin)", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
                SwitchLine("Download new episodes to the server", household.autoDownload) { household = household.copy(autoDownload = it) }
                Text(if (keep == 0) "Keep every downloaded episode" else "Keep the newest $keep downloaded", style = MaterialTheme.typography.bodyMedium)
                Slider(value = keep.toFloat(), onValueChange = { keep = it.toInt() }, valueRange = 0f..50f)
                SwitchLine("Voice boost", household.voiceBoost, "Evens out loudness and makes voices clearer.") { household = household.copy(voiceBoost = it) }
                SwitchLine("Trim silences", household.trimSilence, "Cuts pauses over a second down to 0.3 s on download.") { household = household.copy(trimSilence = it) }
                SwitchLine("Transcribe new episodes", household.transcribe, "Needs transcripts set up in Settings → Podcasts.") { household = household.copy(transcribe = it) }
            }
            Button(onClick = {
                val mySettings = mine.copy(playbackSpeed = speed.takeIf { it >= 0.5f }?.toDouble())
                ui.launch("Couldn't save the settings.") {
                    var saved = ui.api.saveMySettings(podcast.id, mySettings)
                    if (ui.isAdmin) saved = ui.api.savePodcastSettings(podcast.id, household.copy(keepEpisodes = keep))
                    ui.engine.forgetShowSettings(podcast.id)
                    onSaved(saved)
                    ui.message("Saved")
                }
                onDismiss()
            }, modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) { Text("Save") }
        }
    }
}

@Composable
private fun NumberLine(label: String, value: Int, onChange: (Int) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        OutlinedTextField(
            value = if (value == 0) "" else value.toString(),
            onValueChange = { text -> onChange(text.filter(Char::isDigit).take(4).toIntOrNull() ?: 0) },
            placeholder = { Text("0") },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            singleLine = true,
            modifier = Modifier.width(96.dp),
        )
    }
}

