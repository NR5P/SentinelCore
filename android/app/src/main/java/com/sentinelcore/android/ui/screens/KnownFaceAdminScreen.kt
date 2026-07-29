package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.KnownFace
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.components.SentinelRemoteImage
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch

@Composable
fun KnownFaceAdminScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
    onAddFace: () -> Unit,
    onEditFace: (KnownFace) -> Unit,
) {
    var isLoading by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun load() {
        isLoading = true
        try {
            model.refreshKnownFaces()
            errorMessage = null
        } catch (error: Exception) {
            errorMessage = error.message ?: "Could not load known faces."
        } finally {
            isLoading = false
        }
    }

    LaunchedEffect(Unit) { load() }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Known Faces", onBack) {
            IconButton(onClick = onAddFace) {
                Icon(Icons.Default.Add, contentDescription = "Add person")
            }
        }
        if (isLoading) {
            LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        }
        errorMessage?.let {
            Text(it, color = SentinelRed, modifier = Modifier.padding(16.dp))
        }
        if (state.knownFaces.isEmpty() && !isLoading) {
            EmptyState(
                "No known faces",
                "Enroll a family member or regular visitor from a clear photo, so alerts can note when it's them.",
                Modifier.fillMaxSize(),
            )
        } else {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                items(state.knownFaces, key = KnownFace::id) { face ->
                    Card(Modifier.fillMaxWidth().clickable { onEditFace(face) }) {
                        Row(
                            modifier = Modifier.padding(14.dp).fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            SentinelRemoteImage(
                                model = model,
                                path = face.enrollments.firstOrNull()?.photoUrl,
                                modifier = Modifier.size(44.dp).clip(CircleShape),
                                description = face.label,
                            )
                            Spacer(Modifier.width(12.dp))
                            Column(Modifier.weight(1f)) {
                                Text(face.label, style = MaterialTheme.typography.titleSmall)
                                Text(
                                    "${face.enrollments.size} photo${if (face.enrollments.size == 1) "" else "s"}",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            IconButton(onClick = {
                                scope.launch {
                                    try {
                                        model.deleteKnownFace(face)
                                    } catch (error: Exception) {
                                        errorMessage = error.message ?: "Could not delete this person."
                                    }
                                }
                            }) {
                                Icon(
                                    Icons.Default.Delete,
                                    contentDescription = "Delete ${face.label}",
                                    tint = SentinelRed,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
