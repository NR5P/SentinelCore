package com.sentinelcore.android.ui.files

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.FamilyTrashBatch
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.components.SaveFeedbackHost
import com.sentinelcore.android.ui.components.rememberSaveFeedback
import com.sentinelcore.android.ui.screens.ScreenHeader
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.launch

// What has been moved out of the library and not yet cleared: one batch per delete or duplicate
// sweep, each file of which can be put back on its own. Emptying for good is Admin-only.
@Composable
fun FilesTrashScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val feedback = rememberSaveFeedback()

    var batches by remember { mutableStateOf<List<FamilyTrashBatch>?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var reloadTick by remember { mutableStateOf(0) }
    var isBusy by remember { mutableStateOf(false) }
    var confirmEmpty by remember { mutableStateOf(false) }

    LaunchedEffect(reloadTick) {
        errorMessage = null
        runCatching { model.familyFilesTrash() }
            .onSuccess { batches = it }
            .onFailure { errorMessage = it.message ?: "Could not load the trash." }
    }

    fun restore(batchId: String, path: String?) {
        scope.launch {
            isBusy = true
            runCatching { model.familyFilesRestoreTrash(batchId, path) }
                .onSuccess {
                    feedback.saved(it.detail.ifBlank { if (it.restored == 1) "Put back 1 file." else "Put back ${it.restored} files." })
                    reloadTick++
                }
                .onFailure { feedback.failed(it.message ?: "Could not put that back.") }
            isBusy = false
        }
    }

    SaveFeedbackHost(feedback) {
        ScreenHeader("Trash", state, onRefresh = { reloadTick++ })
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item { FilesBackRow("Back to Files", onBack) }

            errorMessage?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }

            val list = batches
            when {
                list == null && errorMessage == null -> item {
                    Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(10.dp))
                        Text("Loading…", color = SentinelTextDim)
                    }
                }
                list != null && list.isEmpty() -> item {
                    EmptyState("The trash is empty", "Files moved to the trash wait here until the nightly pass clears them.")
                }
                list != null -> {
                    items(list, key = { it.id }) { batch ->
                        TrashBatchCard(batch, isBusy, onRestoreAll = { restore(batch.id, null) }, onRestoreFile = { restore(batch.id, it) })
                    }
                    if (state.isAdmin) {
                        item {
                            OutlinedButton(
                                onClick = { confirmEmpty = true },
                                enabled = !isBusy,
                                modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                            ) { Text("Empty the trash for good", color = SentinelRed) }
                        }
                    }
                }
            }
            item { Spacer(Modifier.size(20.dp)) }
        }
    }

    if (confirmEmpty) {
        ConfirmDialog(
            title = "Empty the trash?",
            text = "Everything in it is deleted for good. Nothing on the backup drives is touched until their next backup.",
            confirmLabel = "Empty",
            onConfirm = {
                confirmEmpty = false
                scope.launch {
                    isBusy = true
                    runCatching { model.familyFilesEmptyTrash() }
                        .onSuccess {
                            feedback.saved(it.ifBlank { "The trash is empty." })
                            reloadTick++
                        }
                        .onFailure { feedback.failed(it.message ?: "Could not empty the trash.") }
                    isBusy = false
                }
            },
            onDismiss = { confirmEmpty = false },
        )
    }
}

@Composable
private fun TrashBatchCard(
    batch: FamilyTrashBatch,
    isBusy: Boolean,
    onRestoreAll: () -> Unit,
    onRestoreFile: (String) -> Unit,
) {
    Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(
                        when (batch.reason) {
                            "duplicate" -> "Duplicates removed"
                            else -> "Deleted"
                        },
                        style = MaterialTheme.typography.titleSmall,
                        color = if (batch.reason == "duplicate") SentinelAmber else SentinelCyan,
                    )
                    val who = batch.by?.takeIf { it.isNotBlank() }?.let { " by $it" }.orEmpty()
                    Text(
                        (formatFileDate(batch.deletedUtc) ?: batch.deletedUtc) + who,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                TextButton(onClick = onRestoreAll, enabled = !isBusy) {
                    Text(if (batch.files.size == 1) "Put back" else "Put back all ${batch.files.size}")
                }
            }
            HorizontalDivider()
            batch.files.forEach { file ->
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(file.path.ifBlank { file.name }, style = MaterialTheme.typography.bodyMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                        val detail = buildString {
                            append(formatFileSize(file.size))
                            file.keptCopy?.takeIf { it.isNotBlank() }?.let { append(" · copy kept at $it") }
                        }
                        Text(detail, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    }
                    if (batch.files.size > 1) {
                        TextButton(onClick = { onRestoreFile(file.path) }, enabled = !isBusy) { Text("Put back") }
                    }
                }
            }
        }
    }
}
