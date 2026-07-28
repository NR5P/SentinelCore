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
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
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

@Composable
fun EventFeedScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onEvent: (SecurityEvent) -> Unit,
    onRefresh: () -> Unit,
) {
    var search by rememberSaveable { mutableStateOf("") }
    var notifiedOnly by rememberSaveable { mutableStateOf(false) }
    val filtered = state.events.filter { event ->
        (!notifiedOnly || event.notified) &&
            (search.isBlank() ||
                event.camera.contains(search, ignoreCase = true) ||
                event.summary.contains(search, ignoreCase = true) ||
                event.eventType.contains(search, ignoreCase = true))
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader(
            title = "Feed",
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
                    }
                }
            }

            if (filtered.isEmpty()) {
                item {
                    EmptyState(
                        title = if (state.isLoading) {
                            "Loading feed…"
                        } else if (search.isNotBlank() || notifiedOnly) {
                            "No matching events"
                        } else {
                            "No events"
                        },
                        message = if (search.isNotBlank() || notifiedOnly) {
                            "Try changing your search or notification filter."
                        } else {
                            "Alerts received by Sentinel Core will appear here."
                        },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            } else {
                items(filtered, key = SecurityEvent::id) { event ->
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
    event: SecurityEvent,
    model: SentinelViewModel,
    modifier: Modifier = Modifier,
) {
    Card(modifier, colors = CardDefaults.cardColors()) {
        BoxWithConstraints(Modifier.padding(14.dp)) {
            val compact = maxWidth < 620.dp
            if (compact) {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    SentinelRemoteImage(
                        model = model,
                        path = event.imagePath(),
                        modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                            .clip(RoundedCornerShape(6.dp)),
                        description = "${event.camera} event",
                    )
                    EventSummary(event)
                }
            } else {
                Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    SentinelRemoteImage(
                        model = model,
                        path = event.imagePath(),
                        modifier = Modifier.width(156.dp).height(96.dp)
                            .clip(RoundedCornerShape(6.dp)),
                        description = "${event.camera} event",
                    )
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
private fun EventSummary(
    event: SecurityEvent,
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
        Text(
            event.summary.ifBlank { "No description" },
            maxLines = 4,
            overflow = TextOverflow.Ellipsis,
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                event.eventType.replace('_', ' ').replaceFirstChar(Char::uppercase),
                color = SentinelCyan,
                style = MaterialTheme.typography.labelSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            if (event.notified) {
                Icon(
                    Icons.Default.Notifications,
                    contentDescription = "Notification sent",
                    tint = SentinelGreen,
                    modifier = Modifier.size(16.dp),
                )
                Spacer(Modifier.width(10.dp))
            }
            Text(
                "${(event.confidence * 100).roundToInt()}%",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
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
) {
    TopAppBar(
        title = {
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                ConnectionBadge(state.connectionState)
            }
        },
        actions = {
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
fun DetailHeader(title: String, onBack: () -> Unit) {
    TopAppBar(
        title = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        navigationIcon = {
            IconButton(onClick = onBack) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
            }
        },
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
