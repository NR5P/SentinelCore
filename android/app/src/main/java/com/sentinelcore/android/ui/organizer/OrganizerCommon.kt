package com.sentinelcore.android.ui.organizer

import android.app.DatePickerDialog
import android.app.TimePickerDialog
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.filled.CheckBoxOutlineBlank
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Photo
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sentinelcore.android.organizer.OrganizerEngine
import com.sentinelcore.android.organizer.OrganizerItem
import com.sentinelcore.android.organizer.OrganizerKinds
import com.sentinelcore.android.organizer.OrganizerSpaceKind
import com.sentinelcore.android.organizer.RecurrenceRule
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import java.io.File
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZonedDateTime
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Pieces every Organizer screen shares: space colours and pickers, the links and attachments
// sections events, reminders, notes and cards all carry, a small Markdown renderer, and date and
// time pickers.

// Every Organizer view reads the engine through this, so anything that changes it - an edit here
// or a sync from another device - redraws whatever used it.
@Composable
fun rememberOrganizerRevision(engine: OrganizerEngine): Long {
    val revision by engine.revision.collectAsStateWithLifecycle()
    engine.spaces.collectAsStateWithLifecycle().value
    return revision
}

@Composable
fun spaceColor(engine: OrganizerEngine, spaceId: String): Color {
    val colors = listOf(SentinelGreen, SentinelCyan, SentinelAmber, Color(0xFFB388FF), Color(0xFFFF7EB6), Color(0xFF82AAFF))
    engine.space(spaceId)?.color?.let { hex -> parseHex(hex)?.let { return it } }
    val index = engine.visibleSpaces.indexOfFirst { it.id == spaceId }.coerceAtLeast(0)
    return colors[index % colors.size]
}

fun parseHex(hex: String): Color? = runCatching {
    val text = hex.removePrefix("#")
    if (text.length != 6) null else Color(0xFF000000 or text.toLong(16))
}.getOrNull()

@Composable
fun SpaceTag(engine: OrganizerEngine, spaceId: String) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        Box(Modifier.size(7.dp).clip(CircleShape).background(spaceColor(engine, spaceId)))
        Text(engine.space(spaceId)?.name.orEmpty(), style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
    }
}

// Which space an item goes in: Personal, Family or a group.
@Composable
fun SpaceSelector(engine: OrganizerEngine, spaceId: String, onChange: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        OutlinedButton(onClick = { open = true }) {
            Box(Modifier.size(8.dp).clip(CircleShape).background(spaceColor(engine, spaceId)))
            Spacer(Modifier.width(8.dp))
            Text("Shared with: " + (engine.space(spaceId)?.name ?: "Family"))
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            engine.visibleSpaces.forEach { space ->
                DropdownMenuItem(
                    text = {
                        Text(space.name + when (space.kind) {
                            OrganizerSpaceKind.PERSONAL -> " (only you)"
                            OrganizerSpaceKind.FAMILY -> " (everyone)"
                            OrganizerSpaceKind.GROUP -> ""
                        })
                    },
                    onClick = {
                        onChange(space.id)
                        open = false
                    },
                )
            }
        }
    }
}

// ---- links ----------------------------------------------------------------------------------

data class OrganizerLink(val url: String, val title: String) {
    // Only web, mail and phone links open - a synced link is typed by whoever shares the space.
    val openable: String?
        get() {
            val text = url.trim()
            val lower = text.lowercase()
            return when {
                lower.startsWith("http://") || lower.startsWith("https://") || lower.startsWith("mailto:") || lower.startsWith("tel:") -> text
                "." in text && " " !in text -> "https://$text"
                else -> null
            }
        }

    companion object {
        fun from(item: OrganizerItem?): List<OrganizerLink> = item?.list("links").orEmpty().mapNotNull { entry ->
            val map = entry as? Map<*, *> ?: return@mapNotNull null
            OrganizerLink(map["url"] as? String ?: return@mapNotNull null, map["title"] as? String ?: "")
        }

        fun json(links: List<OrganizerLink>): Any? = links.takeIf { it.isNotEmpty() }?.map { link ->
            buildMap { put("url", link.url); if (link.title.isNotBlank()) put("title", link.title) }
        }
    }
}

@Composable
fun LinksEditor(links: List<OrganizerLink>, onChange: (List<OrganizerLink>) -> Unit) {
    val uriHandler = LocalUriHandler.current
    var url by remember { mutableStateOf("") }
    var label by remember { mutableStateOf("") }
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text("Links", color = SentinelCyan, style = MaterialTheme.typography.labelLarge)
        links.forEach { link ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(
                    Modifier.weight(1f).clickable(enabled = link.openable != null) { link.openable?.let(uriHandler::openUri) },
                ) {
                    Text(link.title.ifBlank { link.url }, color = SentinelCyan, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (link.title.isNotBlank()) {
                        Text(link.url, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
                IconButton(onClick = { onChange(links - link) }) { Icon(Icons.Default.Close, contentDescription = "Remove link") }
            }
        }
        OutlinedTextField(value = url, onValueChange = { url = it }, label = { Text("Add a link: https://…") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        Row(verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(value = label, onValueChange = { label = it }, label = { Text("Label (optional)") }, singleLine = true, modifier = Modifier.weight(1f))
            TextButton(enabled = url.isNotBlank(), onClick = {
                onChange(links + OrganizerLink(url.trim(), label.trim()))
                url = ""
                label = ""
            }) { Text("Add") }
        }
    }
}

// ---- attachments ----------------------------------------------------------------------------

// Pictures and files on an item. `parentId` null means the item isn't created yet; `ensureParent`
// creates it the moment the first file is added, so nothing has to be saved first.
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun AttachmentsEditor(engine: OrganizerEngine, parentId: String?, ensureParent: () -> String) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    rememberOrganizerRevision(engine)
    val attachments = parentId?.let { engine.children(it, OrganizerKinds.ATTACHMENT).sortedBy { a -> a.sortPosition } }.orEmpty()

    fun add(uris: List<Uri>) {
        if (uris.isEmpty()) return
        val parent = ensureParent()
        scope.launch {
            for (uri in uris) {
                val result = withContext(Dispatchers.IO) {
                    runCatching {
                        val bytes = context.contentResolver.openInputStream(uri)!!.use { it.readBytes() }
                        val type = context.contentResolver.getType(uri) ?: "application/octet-stream"
                        val name = displayName(context, uri) ?: ("attachment." + (MimeTypeMap.getSingleton().getExtensionFromMimeType(type) ?: "bin"))
                        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                        if (type.startsWith("image/")) BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
                        engine.attach(bytes, name, type, parent,
                            bounds.outWidth.takeIf { it > 0 }, bounds.outHeight.takeIf { it > 0 })
                    }
                }
                result.exceptionOrNull()?.let { Toast.makeText(context, it.message ?: "Couldn't attach that file.", Toast.LENGTH_LONG).show() }
            }
        }
    }

    val photoPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(10)) { add(it) }
    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { add(it) }

    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text("Pictures & files", color = SentinelCyan, style = MaterialTheme.typography.labelLarge)
        if (attachments.isNotEmpty()) {
            LazyRow(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                items(attachments, key = { it.id }) { attachment ->
                    var menu by remember { mutableStateOf(false) }
                    Box {
                        Column(
                            Modifier.width(100.dp).combinedClickable(
                                onClick = { scope.launch { openAttachment(context, engine, attachment) } },
                                onLongClick = { menu = true },
                            ),
                        ) {
                            val hash = attachment.string("hash").orEmpty()
                            if (attachment.string("contentType")?.startsWith("image/") == true) {
                                OrganizerImage(engine, hash, Modifier.size(100.dp, 75.dp).clip(RoundedCornerShape(8.dp)))
                            } else {
                                Box(
                                    Modifier.size(100.dp, 75.dp).clip(RoundedCornerShape(8.dp)).background(MaterialTheme.colorScheme.surfaceVariant),
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Text(attachment.string("name").orEmpty().substringAfterLast('.', "file").take(4).uppercase(), color = SentinelCyan)
                                }
                            }
                            Text(attachment.string("name").orEmpty(), style = MaterialTheme.typography.labelSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                            DropdownMenuItem(text = { Text("Remove", color = SentinelRed) }, onClick = {
                                engine.delete(attachment.id)
                                menu = false
                            })
                        }
                    }
                }
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = {
                photoPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
            }) {
                Icon(Icons.Default.Photo, contentDescription = null)
                Spacer(Modifier.width(6.dp))
                Text("Photo")
            }
            OutlinedButton(onClick = { filePicker.launch(arrayOf("*/*")) }) {
                Icon(Icons.Default.AttachFile, contentDescription = null)
                Spacer(Modifier.width(6.dp))
                Text("File")
            }
        }
        Text("Kept on this phone straight away; they upload whenever the server is reachable.",
            style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
    }
}

private fun displayName(context: Context, uri: Uri): String? =
    context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) cursor.getString(0) else null
    }

// Copies the original out under its real name and hands it to whatever app opens that type.
private suspend fun openAttachment(context: Context, engine: OrganizerEngine, attachment: OrganizerItem) {
    val hash = attachment.string("hash") ?: return
    val name = attachment.string("name")?.replace("/", "-")?.ifBlank { null } ?: hash
    val file = try {
        withContext(Dispatchers.IO) {
            val source = engine.ensureOriginal(hash)
            File(context.cacheDir, "organizer-open").apply { mkdirs() }.let { folder -> File(folder, name).also { source.copyTo(it, overwrite = true) } }
        }
    } catch (_: Exception) {
        Toast.makeText(context, "That file isn't on this phone and the server can't be reached right now.", Toast.LENGTH_LONG).show()
        return
    }
    val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
    val intent = Intent(Intent.ACTION_VIEW).apply {
        setDataAndType(uri, attachment.string("contentType") ?: "*/*")
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    }
    runCatching { context.startActivity(Intent.createChooser(intent, name).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
}

// A picture from an attachment: this phone's copy when there is one, otherwise fetched. Decoded
// straight down to display size - an original from a phone camera is 12+ megapixels.
@Composable
fun OrganizerImage(engine: OrganizerEngine, hash: String, modifier: Modifier = Modifier, thumbnail: Boolean = true) {
    var bitmap by remember(hash) { mutableStateOf<ImageBitmap?>(null) }
    LaunchedEffect(hash) {
        bitmap = withContext(Dispatchers.IO) {
            val bytes = engine.imageBytes(hash, thumbnail) ?: return@withContext null
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            var sample = 1
            while (maxOf(bounds.outWidth, bounds.outHeight) / (sample * 2) >= 600) sample *= 2
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })?.asImageBitmap()
        }
    }
    Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant), contentAlignment = Alignment.Center) {
        bitmap?.let { Image(it, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.matchParentSize()) }
            ?: Icon(Icons.Default.Photo, contentDescription = null, tint = SentinelTextDim)
    }
}

// ---- markdown --------------------------------------------------------------------------------

// The same small Markdown the dashboard and Apple apps render: headings, lists, checklists (tap to
// tick), quotes and **bold**. Each line keeps its index so a tap flips exactly that line.
@Composable
fun MarkdownNote(source: String, onToggleLine: ((Int) -> Unit)?) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        source.split("\n").forEachIndexed { index, line ->
            val trimmed = line.trim()
            val check = checklist(trimmed)
            when {
                trimmed.startsWith("#") ->
                    Text(inline(trimmed.trimStart('#').trim()), color = SentinelCyan, style = MaterialTheme.typography.titleMedium)
                check != null -> Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth().clickable(enabled = onToggleLine != null) { onToggleLine?.invoke(index) },
                ) {
                    Icon(if (check.first) Icons.Default.CheckBox else Icons.Default.CheckBoxOutlineBlank, contentDescription = null,
                        tint = if (check.first) SentinelGreen else SentinelTextDim)
                    Spacer(Modifier.width(8.dp))
                    Text(inline(check.second), textDecoration = if (check.first) TextDecoration.LineThrough else null,
                        color = if (check.first) SentinelTextDim else Color.Unspecified)
                }
                trimmed.startsWith("- ") || trimmed.startsWith("* ") -> Text(inline("•  " + trimmed.drop(2)))
                trimmed.startsWith("> ") -> Text(inline(trimmed.drop(2)), color = SentinelTextDim, modifier = Modifier.padding(start = 10.dp))
                trimmed.isEmpty() -> Spacer(Modifier.size(4.dp))
                else -> Text(inline(line))
            }
        }
    }
}

private fun inline(text: String) = buildAnnotatedString {
    var rest = text
    while (rest.isNotEmpty()) {
        val start = rest.indexOf("**")
        val end = if (start >= 0) rest.indexOf("**", start + 2) else -1
        if (start < 0 || end < 0) {
            append(rest)
            break
        }
        append(rest.substring(0, start))
        withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append(rest.substring(start + 2, end)) }
        rest = rest.substring(end + 2)
    }
}

fun checklist(line: String): Pair<Boolean, String>? {
    for ((marker, done) in listOf("- [ ] " to false, "- [x] " to true, "- [X] " to true, "* [ ] " to false, "* [x] " to true)) {
        if (line.startsWith(marker)) return done to line.drop(marker.length)
    }
    return null
}

fun toggleChecklistLine(source: String, index: Int): String {
    val lines = source.split("\n").toMutableList()
    if (index !in lines.indices) return source
    lines[index] = when {
        "[ ]" in lines[index] -> lines[index].replaceFirst("[ ]", "[x]")
        "[x]" in lines[index] -> lines[index].replaceFirst("[x]", "[ ]")
        "[X]" in lines[index] -> lines[index].replaceFirst("[X]", "[ ]")
        else -> lines[index]
    }
    return lines.joinToString("\n")
}

// ---- choices & pickers ------------------------------------------------------------------------

enum class RepeatChoice(val label: String, val frequency: String?) {
    NEVER("Never", null), DAILY("Every day", "DAILY"), WEEKLY("Every week", "WEEKLY"),
    MONTHLY("Every month", "MONTHLY"), YEARLY("Every year", "YEARLY"), CUSTOM("Custom (set elsewhere)", null);

    fun rule(until: ZonedDateTime?, keeping: String?): String? = when (this) {
        NEVER -> null
        CUSTOM -> keeping
        else -> RecurrenceRule(frequency!!, until = until).rruleValue
    }

    companion object {
        fun from(rule: String?): RepeatChoice {
            if (rule.isNullOrBlank()) return NEVER
            val simple = rule.split(";").filterNot { it.startsWith("UNTIL=") }.joinToString(";")
            return entries.firstOrNull { it.frequency != null && simple == "FREQ=${it.frequency}" } ?: CUSTOM
        }
    }
}

val alertChoices = listOf(null to "None", 0 to "At the time", 5 to "5 minutes before", 10 to "10 minutes before",
    15 to "15 minutes before", 30 to "30 minutes before", 60 to "1 hour before", 120 to "2 hours before", 1440 to "1 day before")

@Composable
fun <T> ChoiceMenu(label: String, current: String, options: List<Pair<T, String>>, onPick: (T) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        Box {
            TextButton(onClick = { open = true }) { Text(current) }
            DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                options.forEach { (value, text) ->
                    DropdownMenuItem(text = { Text(text) }, onClick = {
                        onPick(value)
                        open = false
                    })
                }
            }
        }
    }
}

fun pickDate(context: Context, initial: LocalDate, onPicked: (LocalDate) -> Unit) {
    DatePickerDialog(context, { _, year, month, day -> onPicked(LocalDate.of(year, month + 1, day)) },
        initial.year, initial.monthValue - 1, initial.dayOfMonth).show()
}

fun pickTime(context: Context, initial: LocalTime, onPicked: (LocalTime) -> Unit) {
    TimePickerDialog(context, { _, hour, minute -> onPicked(LocalTime.of(hour, minute)) },
        initial.hour, initial.minute, android.text.format.DateFormat.is24HourFormat(context)).show()
}
