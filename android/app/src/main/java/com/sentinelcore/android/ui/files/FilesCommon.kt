package com.sentinelcore.android.ui.files

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.widget.Toast
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import com.sentinelcore.android.data.FamilyFileEntry
import com.sentinelcore.android.data.FamilyFilePaths
import com.sentinelcore.android.data.FamilyJob
import com.sentinelcore.android.data.ServerTimestamp
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

// Bits shared by the Files screens: sizes and dates in words, thumbnails, the open/share hand-off
// to another app, and the small dialogs.

internal fun formatFileSize(bytes: Long): String {
    if (bytes <= 0) return "0 B"
    val units = arrayOf("B", "KB", "MB", "GB", "TB")
    var value = bytes.toDouble()
    var unit = 0
    while (value >= 1024 && unit < units.size - 1) {
        value /= 1024
        unit++
    }
    return if (unit == 0) "${value.toInt()} ${units[unit]}" else "%.1f %s".format(value, units[unit])
}

private val fileDateFormat: DateTimeFormatter =
    DateTimeFormatter.ofPattern("d MMM yyyy, HH:mm").withZone(ZoneId.systemDefault())

// "" and unparseable stamps come back null, so a caller can leave the line out.
internal fun formatFileDate(iso: String?): String? {
    if (iso.isNullOrBlank()) return null
    val instant = runCatching { ServerTimestamp.parse(iso) }.getOrNull() ?: return null
    return fileDateFormat.format(instant)
}

internal fun formatFileDate(instant: Instant): String = fileDateFormat.format(instant)

// The file, copied out of the library under its real name so another app can open it - the
// same arrangement the Organizer uses for attachments, under its own cache folder (see
// res/xml/file_paths.xml). Share hands the same copy to ACTION_SEND instead.
internal suspend fun openOrShareFamilyFile(context: Context, model: SentinelViewModel, file: FamilyFileEntry, share: Boolean) {
    val name = FamilyFilePaths.name(file.path).ifBlank { file.name }.replace("/", "-")
    val copy = try {
        withContext(Dispatchers.IO) {
            val folder = File(context.cacheDir, "files-open").apply { mkdirs() }
            File(folder, name).also { model.familyFilesDownload(file.path, it) }
        }
    } catch (error: Exception) {
        Toast.makeText(context, error.message ?: "Couldn't fetch that file.", Toast.LENGTH_LONG).show()
        return
    }
    val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", copy)
    val type = file.contentType.ifBlank { "*/*" }
    val intent = if (share) {
        Intent(Intent.ACTION_SEND).apply {
            this.type = type
            putExtra(Intent.EXTRA_STREAM, uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
    } else {
        Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, type)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
    }
    val chooser = Intent.createChooser(intent, name).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    runCatching { context.startActivity(chooser) }
        .onFailure { Toast.makeText(context, "No app on this phone can open that.", Toast.LENGTH_LONG).show() }
}

// A picture's thumbnail, or a generic file mark for everything else.
@Composable
internal fun FamilyFileThumbnail(model: SentinelViewModel, file: FamilyFileEntry, modifier: Modifier = Modifier) {
    val shape = RoundedCornerShape(8.dp)
    Box(
        modifier.clip(shape).background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        if (file.isImage) {
            var bitmap by remember(file.path, file.sha256) { mutableStateOf<Bitmap?>(null) }
            var finished by remember(file.path, file.sha256) { mutableStateOf(false) }
            LaunchedEffect(file.path, file.sha256) {
                bitmap = model.familyFilesThumbnail(file.path)
                finished = true
            }
            when {
                bitmap != null -> Image(
                    bitmap = bitmap!!.asImageBitmap(),
                    contentDescription = file.name,
                    modifier = Modifier.fillMaxSize(),
                    contentScale = ContentScale.Crop,
                )
                !finished -> CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                else -> Icon(Icons.AutoMirrored.Filled.InsertDriveFile, contentDescription = null, tint = SentinelTextDim)
            }
        } else {
            Text(
                file.name.substringAfterLast('.', "").take(4).uppercase().ifBlank { "FILE" },
                color = SentinelCyan,
                style = MaterialTheme.typography.labelSmall,
            )
        }
    }
}

// The server is busy with a backup, a restore, a duplicate check or the nightly pass. Shown on
// every Files screen while it lasts, since the library can change under the person meanwhile.
@Composable
internal fun FamilyJobBanner(job: FamilyJob, modifier: Modifier = Modifier) {
    val what = when (job.kind) {
        "sync" -> "Backing up to ${job.driveName ?: "a drive"}"
        "restore" -> "Bringing files back from ${job.driveName ?: "a drive"}"
        "duplicates" -> "Checking for duplicates"
        "nightly" -> "Running the nightly pass"
        "index" -> "Indexing the library"
        else -> "Working"
    }
    Card(modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
                Text(what, style = MaterialTheme.typography.titleSmall, color = SentinelCyan)
            }
            val detail = buildString {
                append(job.message)
                if (job.total > 0) {
                    if (isNotEmpty()) append(" · ")
                    append("${job.done} of ${job.total}")
                }
            }
            if (detail.isNotBlank()) {
                Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (job.total > 0) {
                LinearProgressIndicator(
                    progress = { (job.done.toFloat() / job.total).coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth(),
                )
            } else {
                LinearProgressIndicator(Modifier.fillMaxWidth())
            }
        }
    }
}

@Composable
internal fun FilesBackRow(label: String, onBack: () -> Unit) {
    TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
        Text(label, modifier = Modifier.weight(1f).padding(start = 8.dp))
    }
}

@Composable
internal fun ConfirmDialog(
    title: String,
    text: String,
    confirmLabel: String,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit,
    destructive: Boolean = true,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { Text(text) },
        confirmButton = {
            TextButton(onClick = onConfirm) {
                if (destructive) Text(confirmLabel, color = SentinelRed) else Text(confirmLabel)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

// One text field, validated with the server's naming rule before it can be sent.
@Composable
internal fun NameDialog(
    title: String,
    initial: String,
    confirmLabel: String,
    onConfirm: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    var value by remember { mutableStateOf(initial) }
    val problem = FamilyFilePaths.nameProblem(value)
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                OutlinedTextField(
                    value = value,
                    onValueChange = { value = it },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                    isError = value.isNotEmpty() && problem != null,
                )
                if (value.isNotEmpty() && problem != null) {
                    Text(problem, color = SentinelRed, style = MaterialTheme.typography.bodySmall)
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(value.trim()) }, enabled = problem == null && value.trim() != initial) {
                Text(confirmLabel)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

// The library lives on another computer that can't be reached right now. Every listing fails
// until it's back, so this says why instead of leaving an empty folder.
@Composable
internal fun FamilyLibraryProblemBanner(problem: String, modifier: Modifier = Modifier) {
    Card(modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("The library's computer can't be reached", style = MaterialTheme.typography.titleSmall, color = SentinelAmber)
            Text(problem, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
