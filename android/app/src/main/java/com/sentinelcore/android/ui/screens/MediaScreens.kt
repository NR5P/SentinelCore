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
import androidx.compose.material.icons.filled.Videocam
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
import com.sentinelcore.android.ui.components.SentinelPanel
import com.sentinelcore.android.ui.components.SentinelRemoteImage
import com.sentinelcore.android.ui.components.StatusChip
import com.sentinelcore.android.ui.components.fullDateTime
import com.sentinelcore.android.ui.components.relativeTime
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelGreenDim
import com.sentinelcore.android.ui.theme.SentinelText
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlin.math.roundToInt

@Composable
fun TriggerFeedScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onTrigger: (TriggerEvent) -> Unit,
    onRefresh: () -> Unit,
) {
    Column(Modifier.fillMaxSize().background(SentinelBackground)) {
        ScreenHeader("TRIGGERS", state, onRefresh)
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
    SentinelPanel(modifier) {
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            SentinelRemoteImage(
                model = model,
                path = "/api/triggers/${trigger.id}/image",
                modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                    .clip(RoundedCornerShape(5.dp)),
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
                    Text("CLIP", color = SentinelCyan, style = MaterialTheme.typography.labelSmall)
                }
            }
            Text(
                trigger.occurredAt.relativeTime(),
                color = SentinelTextDim,
                style = MaterialTheme.typography.bodySmall,
            )
            Text(
                "MOTION AREA ${(trigger.triggerAreaFraction * 1000).roundToInt() / 10.0}%",
                color = SentinelTextDim,
                style = MaterialTheme.typography.labelSmall,
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
    Column(Modifier.fillMaxSize().background(SentinelBackground)) {
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
                    SentinelPanel(Modifier.fillMaxWidth()) {
                        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                            MetadataRow("CAMERA", trigger.cameraName)
                            MetadataRow("TIME", trigger.occurredAt.fullDateTime())
                            MetadataRow(
                                "MOTION AREA",
                                "${(trigger.triggerAreaFraction * 1000).roundToInt() / 10.0}%",
                            )
                            MetadataRow("MEDIA", if (trigger.hasClip) "VIDEO CLIP" else "SNAPSHOT")
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
    onCamera: (Camera) -> Unit,
    onRefresh: () -> Unit,
) {
    Column(Modifier.fillMaxSize().background(SentinelBackground)) {
        ScreenHeader("LIVE", state, onRefresh)
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
                    SentinelPanel(
                        Modifier.fillMaxWidth().clickable { onCamera(camera) },
                    ) {
                        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                            Box(
                                Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                                    .clip(RoundedCornerShape(5.dp))
                                    .background(Color.Black),
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(
                                    Icons.Default.Videocam,
                                    contentDescription = null,
                                    tint = SentinelGreenDim,
                                    modifier = Modifier.size(42.dp),
                                )
                            }
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
                                    tint = SentinelTextDim,
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
        }
    }
}

@androidx.annotation.OptIn(UnstableApi::class)
@Composable
private fun SentinelPlayer(
    url: String?,
    headers: Map<String, String>,
    modifier: Modifier = Modifier,
) {
    if (url == null) {
        Box(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
            Text("STREAM UNAVAILABLE", color = SentinelTextDim)
        }
        return
    }

    val context = LocalContext.current
    val player = remember(url, headers) {
        val httpFactory = DefaultHttpDataSource.Factory()
            .setAllowCrossProtocolRedirects(true)
            .setDefaultRequestProperties(headers)
        ExoPlayer.Builder(context)
            .setMediaSourceFactory(DefaultMediaSourceFactory(httpFactory))
            .build()
            .apply {
                setMediaItem(MediaItem.fromUri(url))
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
                useController = true
            }
        },
        update = { it.player = player },
        modifier = modifier.background(Color.Black),
    )
}
