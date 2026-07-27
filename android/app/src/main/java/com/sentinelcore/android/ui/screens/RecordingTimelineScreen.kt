package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Circle
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.RecordingTimelineDay
import com.sentinelcore.android.data.TimelineSecurityEvent
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.SentinelPanel
import com.sentinelcore.android.ui.components.SentinelRemoteImage
import com.sentinelcore.android.ui.components.shortTime
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelGreenDim
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelText
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId

@Composable
fun RecordingTimelineScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
) {
    val cameras = remember(state.events, state.timelineCameras) {
        (state.timelineCameras + state.events.map { it.camera })
            .distinct()
            .sortedBy(String::lowercase)
    }
    var camera by rememberSaveable { mutableStateOf("") }
    var date by rememberSaveable { mutableStateOf(LocalDate.now().toString()) }
    var cursorMillis by rememberSaveable { mutableStateOf(Instant.now().toEpochMilli()) }
    var playing by rememberSaveable { mutableStateOf(false) }
    var categories by remember {
        mutableStateOf(setOf("person", "vehicle", "animal", "package", "plate", "other"))
    }
    var severities by remember {
        mutableStateOf(setOf("critical", "high", "medium", "low"))
    }
    var reviewFilter by rememberSaveable { mutableStateOf("all") }
    var cameraMenu by remember { mutableStateOf(false) }
    var objectMenu by remember { mutableStateOf(false) }
    var severityMenu by remember { mutableStateOf(false) }

    LaunchedEffect(cameras) {
        if (camera.isBlank() && cameras.isNotEmpty()) camera = cameras.first()
    }
    LaunchedEffect(Unit) { model.loadTimelineCameras() }
    LaunchedEffect(camera, date) {
        if (camera.isNotBlank()) model.loadTimeline(camera, LocalDate.parse(date))
    }
    LaunchedEffect(state.timelineDay) {
        state.timelineDay?.let { day ->
            cursorMillis = initialCursor(day).toEpochMilli()
        }
    }
    LaunchedEffect(playing, state.timelineDay) {
        while (playing) {
            delay(500)
            val day = state.timelineDay ?: break
            val current = Instant.ofEpochMilli(cursorMillis)
            val next = advance(day, current)
            if (next == null) playing = false else cursorMillis = next.toEpochMilli()
        }
    }

    val timeline = state.timelineDay
    val filtered = timeline?.events?.filter {
        it.category in categories &&
            it.severity in severities &&
            (reviewFilter == "all" ||
                (reviewFilter == "reviewed" && it.reviewed) ||
                (reviewFilter == "unreviewed" && !it.reviewed))
    }.orEmpty()
    val cursor = Instant.ofEpochMilli(cursorMillis)
    val currentSegment = timeline?.recording?.segments?.firstOrNull {
        !cursor.isBefore(it.start) && !cursor.isAfter(it.end)
    }

    Column(Modifier.fillMaxSize().background(SentinelBackground)) {
        ScreenHeader("DAY_TIMELINE", state)
        if (state.timelineLoading) {
            LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                SentinelPanel(
                    Modifier.padding(horizontal = 16.dp).fillMaxWidth().widthIn(max = 1000.dp),
                ) {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Box {
                                OutlinedButton(onClick = { cameraMenu = true }) {
                                    Text(camera.ifBlank { "CHOOSE CAMERA" })
                                }
                                DropdownMenu(cameraMenu, onDismissRequest = { cameraMenu = false }) {
                                    cameras.forEach {
                                        DropdownMenuItem(
                                            text = { Text(it) },
                                            onClick = { camera = it; cameraMenu = false },
                                        )
                                    }
                                }
                            }
                            Spacer(Modifier.weight(1f))
                            IconButton(onClick = {
                                date = LocalDate.parse(date).minusDays(1).toString()
                            }) {
                                Icon(Icons.Default.ChevronLeft, "Previous day")
                            }
                            Text(date, color = SentinelText)
                            IconButton(
                                onClick = { date = LocalDate.parse(date).plusDays(1).toString() },
                                enabled = LocalDate.parse(date).isBefore(LocalDate.now()),
                            ) {
                                Icon(Icons.Default.ChevronRight, "Next day")
                            }
                        }
                        timeline?.let {
                            Row {
                                Text(
                                    if (it.recording.available) {
                                        "${it.recording.segments.size} TRIGGER CLIPS"
                                    } else {
                                        "TRIGGER CLIPS UNAVAILABLE"
                                    },
                                    color = if (it.recording.available) SentinelGreen else SentinelAmber,
                                    style = MaterialTheme.typography.labelSmall,
                                )
                                Spacer(Modifier.weight(1f))
                                Text(
                                    "${filtered.size} OF ${it.events.size} AI EVENTS",
                                    color = SentinelTextDim,
                                    style = MaterialTheme.typography.labelSmall,
                                )
                            }
                        }
                    }
                }
            }

            item {
                SentinelPanel(
                    Modifier.padding(horizontal = 16.dp).fillMaxWidth().widthIn(max = 1000.dp),
                ) {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        if (timeline != null && timeline.recording.available) {
                            if (currentSegment != null) {
                                SentinelRemoteImage(
                                    model = model,
                                    path = model.timelineFramePath(currentSegment.eventId, cursor),
                                    modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                                        .clip(RoundedCornerShape(8.dp)),
                                    description = "Saved trigger frame",
                                    contentScale = ContentScale.Fit,
                                )
                            } else {
                                Box(
                                    Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                                        .background(Color.Black),
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Text(
                                        "No saved trigger clip at this time.",
                                        color = SentinelTextDim,
                                    )
                                }
                            }
                            DayTimelineRail(timeline, filtered, cursor)
                            Slider(
                                value = Duration.between(timeline.start, cursor).toMillis().toFloat(),
                                onValueChange = {
                                    playing = false
                                    cursorMillis = timeline.start.toEpochMilli() + it.toLong()
                                },
                                valueRange = 0f..Duration.between(timeline.start, timeline.end)
                                    .toMillis().coerceAtLeast(1).toFloat(),
                            )
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                IconButton(onClick = {
                                    previousEvent(filtered, cursor)?.let {
                                        cursorMillis = clipStart(timeline, it)?.toEpochMilli()
                                            ?: it.occurredAt.toEpochMilli()
                                    }
                                }) { Icon(Icons.Default.SkipPrevious, "Previous event") }
                                IconButton(onClick = { playing = !playing }) {
                                    Icon(
                                        if (playing) Icons.Default.Pause else Icons.Default.PlayArrow,
                                        if (playing) "Pause" else "Play",
                                    )
                                }
                                IconButton(onClick = {
                                    nextEvent(filtered, cursor)?.let {
                                        cursorMillis = clipStart(timeline, it)?.toEpochMilli()
                                            ?: it.occurredAt.toEpochMilli()
                                    }
                                }) { Icon(Icons.Default.SkipNext, "Next event") }
                                Text(cursor.shortTime(), color = SentinelCyan)
                            }
                        } else {
                            Box(
                                Modifier.fillMaxWidth().aspectRatio(16f / 9f).background(Color.Black),
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(
                                    timeline?.recording?.error
                                        ?: if (camera.isBlank()) {
                                            "AI events will provide camera choices."
                                        } else {
                                            "No saved trigger clip is selected."
                                        },
                                    color = SentinelTextDim,
                                    modifier = Modifier.padding(24.dp),
                                )
                            }
                        }
                        state.timelineError?.let { Text(it, color = SentinelAmber) }
                    }
                }
            }

            item {
                Row(
                    modifier = Modifier.padding(horizontal = 16.dp).fillMaxWidth()
                        .widthIn(max = 1000.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Box {
                        FilterChip(
                            selected = categories.size < 6,
                            onClick = { objectMenu = true },
                            label = { Text("OBJECTS ${categories.size}") },
                            leadingIcon = { Icon(Icons.Default.FilterList, null) },
                        )
                        DropdownMenu(objectMenu, onDismissRequest = { objectMenu = false }) {
                            listOf("person", "vehicle", "animal", "package", "plate", "other").forEach {
                                DropdownMenuItem(
                                    text = { Text((if (it in categories) "✓ " else "") + it.uppercase()) },
                                    onClick = {
                                        categories = if (it in categories) categories - it else categories + it
                                    },
                                )
                            }
                        }
                    }
                    Box {
                        FilterChip(
                            selected = severities.size < 4,
                            onClick = { severityMenu = true },
                            label = { Text("SEVERITY ${severities.size}") },
                        )
                        DropdownMenu(severityMenu, onDismissRequest = { severityMenu = false }) {
                            listOf("critical", "high", "medium", "low").forEach {
                                DropdownMenuItem(
                                    text = { Text((if (it in severities) "✓ " else "") + it.uppercase()) },
                                    onClick = {
                                        severities = if (it in severities) severities - it else severities + it
                                    },
                                )
                            }
                        }
                    }
                    FilterChip(
                        selected = reviewFilter != "all",
                        onClick = {
                            reviewFilter = when (reviewFilter) {
                                "all" -> "unreviewed"
                                "unreviewed" -> "reviewed"
                                else -> "all"
                            }
                        },
                        label = { Text(reviewFilter.uppercase()) },
                    )
                }
            }

            items(filtered, key = TimelineSecurityEvent::id) { event ->
                TimelineEventRow(
                    event = event,
                    model = model,
                    onSelect = {
                        playing = false
                        cursorMillis = clipStart(timeline, event)?.toEpochMilli()
                            ?: event.occurredAt.toEpochMilli()
                    },
                    onReviewed = {
                        model.setTimelineEventReviewed(event.id, !event.reviewed)
                    },
                    modifier = Modifier.padding(horizontal = 16.dp).fillMaxWidth()
                        .widthIn(max = 1000.dp),
                )
            }
            item { Spacer(Modifier.height(12.dp)) }
        }
    }
}

@Composable
private fun DayTimelineRail(
    day: RecordingTimelineDay,
    events: List<TimelineSecurityEvent>,
    cursor: Instant,
) {
    Canvas(
        Modifier.fillMaxWidth().height(42.dp).clip(RoundedCornerShape(6.dp))
            .background(SentinelBackground),
    ) {
        val total = Duration.between(day.start, day.end).toMillis().toFloat().coerceAtLeast(1f)
        day.recording.segments.forEach {
            val x1 = Duration.between(day.start, it.start).toMillis() / total * size.width
            val x2 = Duration.between(day.start, it.end).toMillis() / total * size.width
            drawRect(
                SentinelGreenDim,
                topLeft = Offset(x1, size.height * 0.3f),
                size = androidx.compose.ui.geometry.Size((x2 - x1).coerceAtLeast(2f), size.height * 0.4f),
            )
        }
        events.forEach {
            val x = Duration.between(day.start, it.occurredAt).toMillis() / total * size.width
            drawLine(severityColor(it.severity), Offset(x, 4f), Offset(x, size.height - 4f), 3f)
        }
        val cursorX = Duration.between(day.start, cursor).toMillis() / total * size.width
        drawLine(Color.White, Offset(cursorX, 0f), Offset(cursorX, size.height), 3f)
    }
}

@Composable
private fun TimelineEventRow(
    event: TimelineSecurityEvent,
    model: SentinelViewModel,
    onSelect: () -> Unit,
    onReviewed: () -> Unit,
    modifier: Modifier = Modifier,
) {
    SentinelPanel(modifier.clickable(onClick = onSelect)) {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            SentinelRemoteImage(
                model = model,
                path = if (event.hasImage) "/api/events/${event.id}/image" else null,
                modifier = Modifier.width(112.dp).height(68.dp).clip(RoundedCornerShape(5.dp)),
                description = "${event.category} event",
            )
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(event.occurredAt.shortTime(), color = SentinelCyan)
                    Text(event.category.uppercase(), color = categoryColor(event.category))
                    Text(event.severity.uppercase(), color = severityColor(event.severity))
                    if (!event.hasClip) Text("NO CLIP", color = SentinelTextDim)
                }
                Text(
                    event.summary.ifBlank { event.eventType },
                    color = SentinelText,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            IconButton(onClick = onReviewed) {
                Icon(
                    if (event.reviewed) Icons.Default.CheckCircle else Icons.Default.Circle,
                    if (event.reviewed) "Mark unreviewed" else "Mark reviewed",
                    tint = if (event.reviewed) SentinelGreen else SentinelTextDim,
                )
            }
        }
    }
}

private fun initialCursor(day: RecordingTimelineDay): Instant =
    when {
        day.recording.segments.isNotEmpty() -> day.recording.segments.last().start
        day.events.isNotEmpty() -> day.events.last().occurredAt
        else -> day.start.plusSeconds(12 * 60 * 60)
    }

private fun clipStart(day: RecordingTimelineDay?, event: TimelineSecurityEvent): Instant? =
    day?.recording?.segments?.firstOrNull { it.eventId == event.id }?.start

private fun advance(day: RecordingTimelineDay, cursor: Instant): Instant? {
    val next = cursor.plusMillis(500)
    if (!next.isBefore(day.end)) return null
    if (day.recording.segments.any { !next.isBefore(it.start) && next.isBefore(it.end) }) return next
    return day.recording.segments.firstOrNull { it.start.isAfter(next) }?.start
}

private fun nextEvent(events: List<TimelineSecurityEvent>, cursor: Instant) =
    events.filter { it.occurredAt.isAfter(cursor.plusMillis(500)) }.minByOrNull { it.occurredAt }

private fun previousEvent(events: List<TimelineSecurityEvent>, cursor: Instant) =
    events.filter { it.occurredAt.isBefore(cursor.minusMillis(500)) }.maxByOrNull { it.occurredAt }

private fun categoryColor(category: String) = when (category) {
    "person" -> SentinelCyan
    "vehicle" -> SentinelGreen
    "plate" -> SentinelAmber
    "package" -> Color(0xFFB388FF)
    "animal" -> Color(0xFFFF8A50)
    else -> SentinelTextDim
}

private fun severityColor(severity: String) = when (severity) {
    "critical" -> SentinelRed
    "high" -> SentinelAmber
    "medium" -> SentinelCyan
    else -> SentinelTextDim
}
