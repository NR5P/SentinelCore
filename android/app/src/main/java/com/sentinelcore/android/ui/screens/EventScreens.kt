package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
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
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.ActivityItem
import com.sentinelcore.android.data.SecurityEvent
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.ConnectionBadge
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.components.SentinelRemoteImage
import com.sentinelcore.android.ui.components.StatusChip
import com.sentinelcore.android.ui.components.fullDateTime
import com.sentinelcore.android.ui.components.relativeTime
import com.sentinelcore.android.ui.components.shortTime
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelGreenDim
import kotlin.math.roundToInt

// The one list of everything the cameras caught. This used to be two screens - "Feed" for
// AI-analysed alerts and "Triggers" for motion - which meant a camera that forwards to AI
// analysis put the same moment on both, once with the description and once with the clip. The
// server now joins them (see ActivityEndpoints), so a row here is a thing that happened, and it
// carries whatever exists for it: a snapshot always, a clip when one was recorded, a description
// when something actually looked at the frame.
@Composable
fun EventFeedScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onEvent: (ActivityItem) -> Unit,
    onRefresh: () -> Unit,
) {
    var search by rememberSaveable { mutableStateOf("") }
    var notifiedOnly by rememberSaveable { mutableStateOf(false) }
    val filtered = state.activity.filter { event ->
        // A motion row has no notification decision to satisfy, so filtering on one excludes it -
        // the same rule the server applies to its own analysis-only filters.
        (!notifiedOnly || event.notified) &&
            (search.isBlank() ||
                event.camera.contains(search, ignoreCase = true) ||
                event.summary.orEmpty().contains(search, ignoreCase = true) ||
                event.zoneName.orEmpty().contains(search, ignoreCase = true) ||
                event.eventType.orEmpty().contains(search, ignoreCase = true))
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader(
            title = "Events",
            state = state,
            onRefresh = onRefresh,
        )
        if (state.isLoading) {
            LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                Column(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    OutlinedTextField(
                        value = search,
                        onValueChange = { search = it },
                        modifier = Modifier.fillMaxWidth().widthIn(max = 1000.dp),
                        placeholder = { Text("Search camera activity") },
                        singleLine = true,
                        leadingIcon = {
                            Icon(Icons.Default.Search, contentDescription = null)
                        },
                    )
                    Row(
                        modifier = Modifier.fillMaxWidth().widthIn(max = 1000.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column {
                            Text(
                                "${filtered.size} events",
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            state.lastUpdated?.let {
                                Text(
                                    "Updated ${it.shortTime()}",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.75f),
                                )
                            }
                        }
                        Spacer(Modifier.weight(1f))
                        FilterChip(
                            selected = notifiedOnly,
                            onClick = { notifiedOnly = !notifiedOnly },
                            label = { Text("Notified") },
                            leadingIcon = {
                                Icon(
                                    Icons.Default.Notifications,
                                    contentDescription = null,
                                    modifier = Modifier.size(16.dp),
                                )
                            },
                        )
                        Spacer(Modifier.width(8.dp))
                        // Reloads rather than filtering what is already here: this changes what
                        // the server is asked for.
                        FilterChip(
                            selected = state.showHiddenActivity,
                            onClick = { model.toggleShowHidden() },
                            label = { Text("Hidden") },
                            leadingIcon = {
                                Icon(
                                    Icons.Default.VisibilityOff,
                                    contentDescription = null,
                                    modifier = Modifier.size(16.dp),
                                )
                            },
                        )
                    }
                }
            }

            if (filtered.isEmpty()) {
                item {
                    EmptyState(
                        title = if (state.isLoading) {
                            "Loading events…"
                        } else if (search.isNotBlank() || notifiedOnly) {
                            "No matching events"
                        } else {
                            "No events"
                        },
                        message = if (search.isNotBlank() || notifiedOnly) {
                            "Try changing your search or notification filter."
                        } else if (!state.showHiddenActivity) {
                            "Anything the cameras catch will appear here. Motion the AI rejected is hidden - turn on Hidden to see it."
                        } else {
                            "Anything the cameras catch will appear here."
                        },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            } else {
                items(filtered, key = ActivityItem::id) { event ->
                    EventCard(
                        event = event,
                        model = model,
                        modifier = Modifier
                            .padding(horizontal = 16.dp)
                            .fillMaxWidth()
                            .widthIn(max = 1000.dp)
                            .clickable { onEvent(event) },
                    )
                }
            }

            state.lastError?.let { error ->
                item {
                    Card(
                        modifier = Modifier.padding(horizontal = 16.dp).fillMaxWidth().widthIn(max = 1000.dp),
                    ) {
                        Text(
                            error,
                            color = SentinelAmber,
                            style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.padding(14.dp),
                        )
                    }
                }
            }
            item { Spacer(Modifier.height(12.dp)) }
        }
    }
}

@Composable
private fun EventCard(
    event: ActivityItem,
    model: SentinelViewModel,
    modifier: Modifier = Modifier,
) {
    Card(modifier, colors = CardDefaults.cardColors()) {
        BoxWithConstraints(Modifier.padding(14.dp)) {
            val compact = maxWidth < 620.dp
            if (compact) {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    EventImage(event, model, Modifier.fillMaxWidth().aspectRatio(16f / 9f))
                    EventSummary(event)
                }
            } else {
                Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    EventImage(event, model, Modifier.width(156.dp).height(96.dp))
                    EventSummary(event, Modifier.weight(1f))
                }
            }
            if (event.notified) {
                Box(
                    Modifier.align(Alignment.CenterStart)
                        .width(3.dp)
                        .fillMaxSize()
                        .background(SentinelGreen),
                )
            }
        }
    }
}

@Composable
private fun EventImage(
    event: ActivityItem,
    model: SentinelViewModel,
    modifier: Modifier = Modifier,
) {
    Box(modifier) {
        SentinelRemoteImage(
            model = model,
            path = event.imageUrl,
            modifier = Modifier.fillMaxSize().clip(RoundedCornerShape(6.dp)),
            description = "${event.camera} event",
        )
        if (event.hasClip) {
            Row(
                modifier = Modifier.align(Alignment.BottomStart).padding(6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(
                    Icons.Default.PlayArrow,
                    contentDescription = "Video clip",
                    tint = SentinelCyan,
                    modifier = Modifier.size(16.dp),
                )
                Text("Clip", color = SentinelCyan, style = MaterialTheme.typography.labelSmall)
            }
        }
    }
}

@Composable
private fun EventSummary(
    event: ActivityItem,
    modifier: Modifier = Modifier,
) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                event.camera,
                color = SentinelGreen,
                fontWeight = FontWeight.Bold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            Text(
                event.occurredAt.relativeTime(),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        // Nothing analysed a motion row, so the zone that caught it stands in for a description -
        // dimmed, so a mixed list reads as "described" vs "not looked at".
        if (event.isAnalyzed) {
            Text(
                event.summary.orEmpty().ifBlank { "No description" },
                maxLines = 4,
                overflow = TextOverflow.Ellipsis,
            )
        } else {
            Text(
                event.zoneName ?: "Motion in zone",
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                fontStyle = FontStyle.Italic,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                if (event.isAnalyzed) {
                    event.eventType.orEmpty().ifBlank { "unknown" }
                        .replace('_', ' ').replaceFirstChar(Char::uppercase)
                } else {
                    "Motion only"
                },
                color = if (event.isAnalyzed) SentinelCyan else MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.labelSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            if (event.confirmation != null && event.confirmation != "confirmed") {
                Icon(
                    Icons.Default.Warning,
                    contentDescription = if (event.confirmation == "rejected") {
                        "AI found nothing"
                    } else {
                        "Awaiting AI verdict"
                    },
                    tint = SentinelAmber,
                    modifier = Modifier.size(16.dp),
                )
                Spacer(Modifier.width(10.dp))
            }
            if (event.notified) {
                Icon(
                    Icons.Default.Notifications,
                    contentDescription = "Notification sent",
                    tint = SentinelGreen,
                    modifier = Modifier.size(16.dp),
                )
                Spacer(Modifier.width(10.dp))
            }
            if (event.isAnalyzed && event.confidence != null) {
                Text(
                    "${(event.confidence * 100).roundToInt()}%",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
fun EventDetailScreen(
    event: SecurityEvent,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        DetailHeader(event.camera, onBack)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(16.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            item {
                Column(
                    modifier = Modifier.padding(16.dp).fillMaxWidth().widthIn(max = 900.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    SentinelRemoteImage(
                        model = model,
                        path = event.imagePath(),
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                            .clip(RoundedCornerShape(8.dp)),
                        description = "${event.camera} event image",
                    )
                    Card(Modifier.fillMaxWidth()) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                StatusChip(
                                    event.eventType.replace('_', ' ').uppercase(),
                                    SentinelCyan,
                                    Modifier.weight(1f, fill = false),
                                )
                                if (event.notified) StatusChip("NOTIFIED", SentinelGreen)
                                Spacer(Modifier.weight(1f))
                                Text(
                                    "${(event.confidence * 100).roundToInt()}%",
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            MetadataRow("Camera", event.camera)
                            MetadataRow("Time", event.occurredAt.fullDateTime())
                            MetadataRow("Status", event.status.replaceFirstChar(Char::uppercase))
                            HorizontalDivider(color = SentinelGreenDim)
                            Text(
                                event.summary.ifBlank { "No description" },
                                style = MaterialTheme.typography.titleMedium,
                            )
                            if (event.notified) {
                                Text(
                                    event.notificationReason.ifBlank { "Notification sent" },
                                    color = SentinelGreen,
                                    style = MaterialTheme.typography.bodySmall,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun ScreenHeader(
    title: String,
    state: SentinelUiState,
    onRefresh: (() -> Unit)? = null,
    // Extra buttons alongside Refresh, for top-level screens that have their own actions - the
    // Device Hub's add/discover, for instance. Empty by default, so existing callers are
    // unchanged.
    actions: @Composable androidx.compose.foundation.layout.RowScope.() -> Unit = {},
) {
    TopAppBar(
        title = {
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                ConnectionBadge(state.connectionState)
            }
        },
        actions = {
            actions()
            if (onRefresh != null) {
                IconButton(onClick = onRefresh, enabled = !state.isLoading) {
                    Icon(Icons.Default.Refresh, contentDescription = "Refresh")
                }
            }
        },
    )
}

@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun DetailHeader(
    title: String,
    onBack: () -> Unit,
    actions: @Composable androidx.compose.foundation.layout.RowScope.() -> Unit = {},
) {
    TopAppBar(
        title = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        navigationIcon = {
            IconButton(onClick = onBack) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
            }
        },
        actions = actions,
    )
}

@Composable
fun MetadataRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Spacer(Modifier.weight(1f))
        Text(
            value,
            style = MaterialTheme.typography.bodySmall,
            maxLines = 3,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

private fun SecurityEvent.imagePath(): String? =
    if (hasImage) "/api/events/$id/image" else null
