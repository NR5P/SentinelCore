package com.sentinelcore.android.ui.screens

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
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowOutward
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Speaker
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.MediaItem
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import com.sentinelcore.android.data.Camera
import com.sentinelcore.android.data.TriggerEvent
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.components.PtzControls
import com.sentinelcore.android.ui.components.SentinelRemoteImage
import com.sentinelcore.android.ui.components.StatusChip
import com.sentinelcore.android.ui.components.fullDateTime
import com.sentinelcore.android.ui.components.relativeTime
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import kotlin.math.roundToInt

@Composable
fun TriggerFeedScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onTrigger: (TriggerEvent) -> Unit,
    onRefresh: () -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Triggers", state, onRefresh)
        if (state.isLoading) {
            LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        }
        if (state.triggers.isEmpty() && !state.isLoading) {
            EmptyState(
                "No motion triggers",
                "Motion detected inside configured camera zones will appear here.",
                Modifier.fillMaxSize(),
            )
        } else {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(280.dp),
                modifier = Modifier.fillMaxSize(),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
                horizontalArrangement = Arrangement.spacedBy(14.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                items(state.triggers, key = TriggerEvent::id) { trigger ->
                    TriggerCard(trigger, model, Modifier.clickable { onTrigger(trigger) })
                }
            }
        }
    }
}

@Composable
private fun TriggerCard(
    trigger: TriggerEvent,
    model: SentinelViewModel,
    modifier: Modifier = Modifier,
) {
    Card(modifier) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            SentinelRemoteImage(
                model = model,
                path = "/api/triggers/${trigger.id}/image",
                modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                    .clip(RoundedCornerShape(6.dp)),
                description = "${trigger.cameraName} motion trigger",
            )
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    trigger.cameraName,
                    color = SentinelGreen,
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.weight(1f),
                )
                if (trigger.hasClip) {
                    Icon(
                        Icons.Default.PlayArrow,
                        contentDescription = "Video clip",
                        tint = SentinelCyan,
                    )
                    Text("Clip", color = SentinelCyan, style = MaterialTheme.typography.labelSmall)
                }
            }
            Text(
                trigger.occurredAt.relativeTime(),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                "Motion area ${(trigger.triggerAreaFraction * 1000).roundToInt() / 10.0}%",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
fun TriggerDetailScreen(
    trigger: TriggerEvent,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        DetailHeader(trigger.cameraName, onBack)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            item {
                Column(
                    modifier = Modifier.padding(16.dp).fillMaxWidth().widthIn(max = 900.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    if (trigger.hasClip) {
                        SentinelPlayer(
                            url = model.absoluteUrl("/api/triggers/${trigger.id}/clip"),
                            headers = model.mediaHeaders(),
                            modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f),
                        )
                    } else {
                        SentinelRemoteImage(
                            model = model,
                            path = "/api/triggers/${trigger.id}/image",
                            modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                                .clip(RoundedCornerShape(8.dp)),
                            contentScale = ContentScale.Fit,
                            description = "${trigger.cameraName} trigger image",
                        )
                    }
                    Card(Modifier.fillMaxWidth()) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            MetadataRow("Camera", trigger.cameraName)
                            MetadataRow("Time", trigger.occurredAt.fullDateTime())
                            MetadataRow(
                                "Motion area",
                                "${(trigger.triggerAreaFraction * 1000).roundToInt() / 10.0}%",
                            )
                            MetadataRow("Media", if (trigger.hasClip) "Video clip" else "Snapshot")
                        }
                    }
                }
            }
        }
    }
}

@Composable
fun LiveCamerasScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onCamera: (Camera) -> Unit,
    onRefresh: () -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Live", state, onRefresh)
        if (state.isLoading) {
            LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        }
        if (state.cameras.isEmpty() && !state.isLoading) {
            EmptyState(
                "No cameras",
                "Add cameras in the Sentinel Core dashboard.",
                Modifier.fillMaxSize(),
            )
        } else {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(320.dp),
                modifier = Modifier.fillMaxSize(),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
                horizontalArrangement = Arrangement.spacedBy(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                items(state.cameras, key = Camera::id) { camera ->
                    Card(
                        modifier = Modifier.fillMaxWidth().clickable { onCamera(camera) },
                    ) {
                        Column(
                            modifier = Modifier.padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            SentinelPlayer(
                                url = model.absoluteUrl(
                                    "/api/cameras/${camera.id}/stream/stream.m3u8",
                                ),
                                headers = model.mediaHeaders(),
                                muted = true,
                                showControls = false,
                                modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                                    .clip(RoundedCornerShape(6.dp)),
                            )
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Box(Modifier.size(7.dp).background(SentinelGreen, CircleShape))
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    camera.name,
                                    color = SentinelGreen,
                                    style = MaterialTheme.typography.titleSmall,
                                    modifier = Modifier.weight(1f),
                                )
                                if (camera.includeAudio) {
                                    Icon(
                                        Icons.Default.Speaker,
                                        contentDescription = "Audio enabled",
                                        tint = SentinelCyan,
                                    )
                                    Spacer(Modifier.width(8.dp))
                                }
                                Icon(
                                    Icons.Default.ArrowOutward,
                                    contentDescription = "Open live camera",
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
fun LivePlayerScreen(
    camera: Camera,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val url = model.absoluteUrl("/api/cameras/${camera.id}/stream/stream.m3u8")
    Column(Modifier.fillMaxSize().background(Color.Black)) {
        DetailHeader(camera.name, onBack)
        Box(
            modifier = Modifier.fillMaxSize(),
            contentAlignment = Alignment.Center,
        ) {
            SentinelPlayer(
                url = url,
                headers = model.mediaHeaders(),
                modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f),
            )
            StatusChip(
                "● LIVE",
                SentinelGreen,
                Modifier.align(Alignment.TopStart).padding(16.dp),
            )
            if (camera.ptzEnabled) {
                PtzControls(
                    cameraId = camera.id,
                    model = model,
                    modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp),
                )
            }
        }
    }
}

@androidx.annotation.OptIn(UnstableApi::class)
@Composable
private fun SentinelPlayer(
    url: String?,
    headers: Map<String, String>,
    muted: Boolean = false,
    showControls: Boolean = true,
    modifier: Modifier = Modifier,
) {
    if (url == null) {
        Box(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
            Text("Stream unavailable", color = Color.White)
        }
        return
    }

    val context = LocalContext.current
    val player = remember(url, headers, muted) {
        val httpFactory = DefaultHttpDataSource.Factory()
            .setAllowCrossProtocolRedirects(true)
            .setDefaultRequestProperties(headers)
        ExoPlayer.Builder(context)
            .setMediaSourceFactory(DefaultMediaSourceFactory(httpFactory))
            .build()
            .apply {
                setMediaItem(MediaItem.fromUri(url))
                volume = if (muted) 0f else 1f
                playWhenReady = true
                prepare()
            }
    }

    DisposableEffect(player) {
        onDispose { player.release() }
    }

    AndroidView(
        factory = { viewContext ->
            PlayerView(viewContext).apply {
                this.player = player
                useController = showControls
            }
        },
        update = {
            it.player = player
            it.useController = showControls
        },
        modifier = modifier.background(Color.Black),
    )
}
