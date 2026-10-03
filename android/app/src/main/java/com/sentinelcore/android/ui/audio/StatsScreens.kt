package com.sentinelcore.android.ui.audio

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.sentinelcore.android.audio.ListeningStats
import com.sentinelcore.android.audio.MusicStats
import com.sentinelcore.android.audio.PodcastStats
import com.sentinelcore.android.ui.screens.DetailHeader
import kotlinx.coroutines.launch
import java.text.NumberFormat
import java.time.Year

private val weekdays = listOf("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")

// §9 Stats: this person's listening for a week, month, year or all time.
@Composable
fun StatsScreen() {
    val ui = LocalAudioUi.current
    var period by rememberSaveable { mutableStateOf("month") }
    val stats = rememberLoadable(period) { ui.api.stats(period) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Listening stats", onBack = ui.nav.back)
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("week" to "Week", "month" to "Month", "year" to "Year", "all" to "All time").forEach { (key, label) ->
                FilterChip(selected = period == key, onClick = { period = key }, label = { Text(label) })
            }
        }
        LoadableContent(stats) { value -> StatsContent(value) }
    }
}

@Composable
private fun StatsContent(stats: ListeningStats) {
    val ui = LocalAudioUi.current
    val numbers = NumberFormat.getIntegerInstance()
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item {
            Card(Modifier.fillMaxWidth().clickableCompat { ui.nav.open(AudioRoutes.WRAPPED) }) {
                Box(Modifier.fillMaxWidth().background(Brush.linearGradient(listOf(Color(0xFF7B2FF7), Color(0xFFF107A3)))).padding(16.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Default.AutoAwesome, contentDescription = null, tint = Color.White)
                        Spacer(Modifier.width(10.dp))
                        Column {
                            Text("Your ${Year.now().value} Wrapped", color = Color.White, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
                            Text("Your year in music and podcasts, as a story", color = Color.White.copy(alpha = 0.85f), style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            }
        }
        stats.music?.let { music ->
            item { Text("Music", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold) }
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Tile("${numbers.format(music.minutes.toLong())}", "minutes", Modifier.weight(1f))
                    Tile("${numbers.format(music.plays)}", "plays", Modifier.weight(1f))
                    Tile("${music.distinctArtists}", "artists", Modifier.weight(1f))
                }
            }
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Tile("${music.distinctTracks}", "different songs", Modifier.weight(1f))
                    Tile("${music.newArtists}", "new artists", Modifier.weight(1f))
                    Tile("${music.longestStreakDays}", "day streak", Modifier.weight(1f))
                }
            }
            if (music.topArtists.isNotEmpty()) {
                item { SectionHeader("Top artists") }
                music.topArtists.forEachIndexed { index, stat ->
                    item(key = "ar$index") { RankRow(index + 1, stat.artist.artUrl, stat.artist.name, "${stat.plays} plays · ${stat.minutes.toLong()} min", circle = true) { ui.nav.openArtist(stat.artist.id) } }
                }
            }
            if (music.topTracks.isNotEmpty()) {
                item { SectionHeader("Top songs") }
                music.topTracks.forEachIndexed { index, stat ->
                    item(key = "tr$index") { RankRow(index + 1, stat.track.artUrl, stat.track.title, "${stat.track.artist} · ${stat.plays} plays") { ui.playTracks(music.topTracks.map { it.track }, index) } }
                }
            }
            if (music.topAlbums.isNotEmpty()) {
                item { SectionHeader("Top albums") }
                music.topAlbums.forEachIndexed { index, stat ->
                    item(key = "al$index") { RankRow(index + 1, stat.album.artUrl, stat.album.title, "${stat.album.artist} · ${stat.plays} plays") { ui.nav.openAlbum(stat.album.id) } }
                }
            }
            if (music.topGenres.isNotEmpty()) {
                item { SectionHeader("Genres") }
                item { Bars(music.topGenres.map { it.genre to it.minutes }) }
            }
            if (music.byHour.size == 24) {
                item { SectionHeader("When you listen") }
                item { HourChart(music.byHour) }
            }
            if (music.byWeekday.size == 7) item { Bars(weekdays.zip(music.byWeekday)) }
        }
        stats.podcasts?.let { podcasts ->
            item { Text("Podcasts", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 12.dp)) }
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Tile("${numbers.format(podcasts.minutes.toLong())}", "minutes", Modifier.weight(1f))
                    Tile("${podcasts.episodesFinished}", "episodes finished", Modifier.weight(1f))
                    Tile("${podcasts.timeSavedMinutes.toLong()}", "minutes saved", Modifier.weight(1f))
                }
            }
            if (podcasts.topShows.isNotEmpty()) {
                item { SectionHeader("Top shows") }
                podcasts.topShows.forEachIndexed { index, stat ->
                    item(key = "sh$index") { RankRow(index + 1, stat.podcast.artUrl, stat.podcast.title, "${stat.minutes.toLong()} min · ${stat.episodes} episodes") { ui.nav.openShow(stat.podcast.id) } }
                }
            }
            if (podcasts.byHour.size == 24) {
                item { SectionHeader("When you listen") }
                item { HourChart(podcasts.byHour) }
            }
        }
    }
}

private fun Modifier.clickableCompat(onClick: () -> Unit) = this.then(Modifier.combinedClickableCompat(onClick))

@Composable
private fun SectionHeader(text: String) {
    Text(text, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 8.dp))
}

@Composable
private fun Tile(value: String, label: String, modifier: Modifier) {
    Card(modifier) {
        Column(Modifier.padding(12.dp)) {
            Text(value, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.primary, maxLines = 1)
            Text(label, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun RankRow(rank: Int, art: String?, title: String, detail: String, circle: Boolean = false, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).combinedClickableCompat(onClick).padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Text("$rank", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, modifier = Modifier.width(28.dp))
        ArtImage(art, Modifier.size(44.dp), sizePx = 132, shape = if (circle) CircleShape else RoundedCornerShape(6.dp), fallbackIcon = if (circle) Icons.Default.Person else Icons.Default.Podcasts, seed = title)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.Medium)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
        }
    }
}

// Horizontal bars, labelled, scaled to the biggest.
@Composable
private fun Bars(values: List<Pair<String, Double>>, color: Color = MaterialTheme.colorScheme.primary, textColor: Color = MaterialTheme.colorScheme.onSurface) {
    val max = values.maxOfOrNull { it.second }?.takeIf { it > 0 } ?: 1.0
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        values.forEach { (label, value) ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(label, modifier = Modifier.width(88.dp), style = MaterialTheme.typography.labelMedium, color = textColor, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Box(Modifier.weight(1f).height(14.dp)) {
                    Box(Modifier.fillMaxHeight().fillMaxWidth((value / max).toFloat().coerceIn(0.02f, 1f)).clip(RoundedCornerShape(7.dp)).background(color))
                }
                Text("${value.toLong()} min", modifier = Modifier.width(64.dp).padding(start = 6.dp), style = MaterialTheme.typography.labelSmall, color = textColor)
            }
        }
    }
}

// 24 columns, midnight to midnight.
@Composable
private fun HourChart(hours: List<Double>, color: Color = MaterialTheme.colorScheme.primary, textColor: Color = MaterialTheme.colorScheme.onSurfaceVariant) {
    val max = hours.maxOrNull()?.takeIf { it > 0 } ?: 1.0
    Column {
        Row(Modifier.fillMaxWidth().height(90.dp), horizontalArrangement = Arrangement.spacedBy(2.dp), verticalAlignment = Alignment.Bottom) {
            hours.forEach { value ->
                Box(Modifier.weight(1f).fillMaxHeight((value / max).toFloat().coerceIn(0.03f, 1f)).clip(RoundedCornerShape(topStart = 3.dp, topEnd = 3.dp)).background(color))
            }
        }
        Row(Modifier.fillMaxWidth()) {
            listOf("12am", "6am", "12pm", "6pm").forEach { Text(it, modifier = Modifier.weight(1f), style = MaterialTheme.typography.labelSmall, color = textColor) }
        }
    }
}

// ---------------- Wrapped ----------------

private data class StoryPage(val colors: List<Color>, val content: @Composable () -> Unit)

// §9 Wrapped as a swipeable story: progress bars at the top, tap right/left to move, each page
// timed like a story, swipe to move freely.
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun WrappedScreen() {
    val ui = LocalAudioUi.current
    val year = Year.now().value
    val stats = rememberLoadable(year) { ui.api.stats("year", year) }
    val value = stats.value
    if (value == null) {
        Column(Modifier.fillMaxSize()) {
            DetailHeader("Wrapped", onBack = ui.nav.back)
            LoadableContent(stats) {}
        }
        return
    }
    val pages = remember(value) { wrappedPages(value, year) }
    val pager = rememberPagerState { pages.size }
    val scope = rememberCoroutineScope()
    val progress = remember { Animatable(0f) }
    LaunchedEffect(pager.currentPage) {
        progress.snapTo(0f)
        progress.animateTo(1f, tween(7_000, easing = LinearEasing))
        if (pager.currentPage < pages.lastIndex) pager.animateScrollToPage(pager.currentPage + 1)
    }
    Box(Modifier.fillMaxSize().background(Color.Black)) {
        HorizontalPager(pager, Modifier.fillMaxSize()) { index ->
            val page = pages[index]
            Box(
                Modifier.fillMaxSize().background(Brush.linearGradient(page.colors))
                    .pointerInput(index) {
                        detectTapGestures { offset ->
                            scope.launch {
                                if (offset.x > size.width / 3) { if (pager.currentPage < pages.lastIndex) pager.animateScrollToPage(pager.currentPage + 1) }
                                else if (pager.currentPage > 0) pager.animateScrollToPage(pager.currentPage - 1)
                            }
                        }
                    }
                    .padding(28.dp),
                contentAlignment = Alignment.Center,
            ) { page.content() }
        }
        Column(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 12.dp, vertical = 8.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                pages.indices.forEach { index ->
                    val fill = when {
                        index < pager.currentPage -> 1f
                        index == pager.currentPage -> progress.value
                        else -> 0f
                    }
                    Box(Modifier.weight(1f).height(3.dp).clip(RoundedCornerShape(2.dp)).background(Color.White.copy(alpha = 0.3f))) {
                        Box(Modifier.fillMaxHeight().fillMaxWidth(fill).background(Color.White))
                    }
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("$year Wrapped", color = Color.White, style = MaterialTheme.typography.labelLarge, modifier = Modifier.weight(1f))
                IconButton(onClick = ui.nav.back) { Icon(Icons.Default.Close, contentDescription = "Close", tint = Color.White) }
            }
        }
    }
}

@Composable
private fun Big(text: String) = Text(text, color = Color.White, fontSize = 52.sp, fontWeight = FontWeight.Black, textAlign = TextAlign.Center, lineHeight = 56.sp)

@Composable
private fun Say(text: String) = Text(text, color = Color.White, style = MaterialTheme.typography.titleLarge, textAlign = TextAlign.Center)

@Composable
private fun Small(text: String) = Text(text, color = Color.White.copy(alpha = 0.8f), style = MaterialTheme.typography.bodyLarge, textAlign = TextAlign.Center)

private fun wrappedPages(stats: ListeningStats, year: Int): List<StoryPage> {
    val numbers = NumberFormat.getIntegerInstance()
    val music = stats.music ?: MusicStats()
    val podcasts = stats.podcasts ?: PodcastStats()
    val pages = mutableListOf<StoryPage>()
    pages += StoryPage(listOf(Color(0xFF1DB954), Color(0xFF0B3D20))) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Say("$year, in sound.")
            Big(numbers.format((music.minutes + podcasts.minutes).toLong()))
            Say("minutes of music and podcasts")
            Small("That's ${"%.1f".format((music.minutes + podcasts.minutes) / 1440)} days.")
        }
    }
    music.topArtists.firstOrNull()?.let { top ->
        pages += StoryPage(listOf(Color(0xFFF107A3), Color(0xFF7B2FF7))) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Say("Your top artist")
                ArtImage(top.artist.artUrl, Modifier.size(200.dp), sizePx = 600, shape = CircleShape, fallbackIcon = Icons.Default.Person, seed = top.artist.name)
                Big(top.artist.name)
                Small("${top.plays} plays, ${top.minutes.toLong()} minutes together.")
            }
        }
    }
    if (music.topTracks.isNotEmpty()) {
        pages += StoryPage(listOf(Color(0xFFFF7A18), Color(0xFFAF002D))) {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Say("Your top songs")
                music.topTracks.take(5).forEachIndexed { index, stat ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("${index + 1}", color = Color.White, fontSize = 28.sp, fontWeight = FontWeight.Black, modifier = Modifier.width(40.dp))
                        ArtImage(stat.track.artUrl, Modifier.size(52.dp), sizePx = 156, seed = stat.track.album)
                        Spacer(Modifier.width(12.dp))
                        Column {
                            Text(stat.track.title, color = Color.White, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text("${stat.track.artist} · ${stat.plays} plays", color = Color.White.copy(alpha = 0.8f), style = MaterialTheme.typography.bodySmall, maxLines = 1)
                        }
                    }
                }
            }
        }
    }
    music.topGenres.firstOrNull()?.let { genre ->
        pages += StoryPage(listOf(Color(0xFF00C6FB), Color(0xFF005BEA))) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Say("Your sound was")
                Big(genre.genre)
                Small(music.topGenres.drop(1).take(3).joinToString(", ") { it.genre }.let { if (it.isBlank()) "" else "with a side of $it." })
            }
        }
    }
    if (music.byHour.size == 24 && music.byHour.any { it > 0 }) {
        val peak = music.byHour.indices.maxBy { music.byHour[it] }
        val label = when (peak) { in 5..11 -> "a morning listener"; in 12..16 -> "an afternoon listener"; in 17..21 -> "an evening listener"; else -> "a night owl" }
        pages += StoryPage(listOf(Color(0xFF232526), Color(0xFF414345))) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Say("You're")
                Big(label)
                Small("Most of your listening happened around ${if (peak % 12 == 0) 12 else peak % 12}${if (peak < 12) "am" else "pm"}.")
                HourChart(music.byHour, Color.White, Color.White.copy(alpha = 0.7f))
            }
        }
    }
    if (music.longestStreakDays > 1 || music.newArtists > 0) {
        pages += StoryPage(listOf(Color(0xFFFDC830), Color(0xFFF37335))) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Big("${music.longestStreakDays} days")
                Say("your longest streak of listening every day")
                Small("And you found ${music.newArtists} artists you'd never played before.")
            }
        }
    }
    podcasts.topShows.firstOrNull()?.let { top ->
        pages += StoryPage(listOf(Color(0xFF8E2DE2), Color(0xFF4A00E0))) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Say("Your top show")
                ArtImage(top.podcast.artUrl, Modifier.size(200.dp), sizePx = 600, fallbackIcon = Icons.Default.Podcasts, seed = top.podcast.title)
                Big(top.podcast.title)
                Small("${top.episodes} episodes, ${top.minutes.toLong()} minutes. ${podcasts.episodesFinished} episodes finished in all.")
                if (podcasts.timeSavedMinutes >= 1) Small("Speed and trims saved you ${podcasts.timeSavedMinutes.toLong()} minutes.")
            }
        }
    }
    pages += StoryPage(listOf(Color(0xFF11998E), Color(0xFF38EF7D))) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Big("Thanks for listening.")
            Small("${numbers.format(music.plays)} plays · ${music.distinctArtists} artists · ${podcasts.episodesFinished} episodes")
            Small("Here's to ${year + 1}.")
        }
    }
    return pages
}

