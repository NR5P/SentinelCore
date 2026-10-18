package com.sentinelcore.android.ui.files

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Photo
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Storage
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.runtime.toMutableStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sentinelcore.android.data.FamilyFileEntry
import com.sentinelcore.android.data.FamilyFilePaths
import com.sentinelcore.android.data.FamilyFilesStatus
import com.sentinelcore.android.data.FamilyFolderEntry
import com.sentinelcore.android.data.FamilyFolderListing
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.components.SaveFeedbackHost
import com.sentinelcore.android.ui.components.rememberSaveFeedback
import com.sentinelcore.android.ui.screens.ScreenHeader
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// The household's shared file library - the web dashboard's FILES section. Folders first, then
// files with a thumbnail, size and date; a search box over the whole library; the phone's own
// photo picker and file picker for adding; and per-row open, share, rename and trash.
//
// The folder stack is this screen's own, so the system Back button walks up through the folders
// that were opened before it leaves the tab. Uploads are the ViewModel's (see
// SentinelViewModel.uploadFamilyFiles) and keep going if the person switches tabs.
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun FilesScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onTrash: () -> Unit,
    onDrives: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val feedback = rememberSaveFeedback()

    // Where we are, plus the way back. Survives rotation; a fresh process starts at the root.
    val stack: SnapshotStateList<String> = rememberSaveable(
        saver = listSaver(save = { it.toList() }, restore = { it.toMutableStateList() }),
    ) { mutableStateListOf() }
    var path by rememberSaveable { mutableStateOf("") }

    var listing by remember { mutableStateOf<FamilyFolderListing?>(null) }
    var isLoading by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var reloadTick by remember { mutableStateOf(0) }

    var query by rememberSaveable { mutableStateOf("") }
    var searchResults by remember { mutableStateOf<List<FamilyFileEntry>?>(null) }
    var isSearching by remember { mutableStateOf(false) }

    var status by remember { mutableStateOf<FamilyFilesStatus?>(null) }

    var showNewFolder by remember { mutableStateOf(false) }
    var addMenu by remember { mutableStateOf(false) }

    val upload by model.familyUpload.collectAsStateWithLifecycle()

    fun openFolder(target: String) {
        if (target == path) return
        stack.add(path)
        path = target
        query = ""
    }

    fun goUp(): Boolean {
        if (stack.isEmpty()) return false
        path = stack.removeAt(stack.lastIndex)
        return true
    }

    BackHandler(enabled = stack.isNotEmpty() || query.isNotEmpty()) {
        if (query.isNotEmpty()) query = "" else goUp()
    }

    LaunchedEffect(path, reloadTick) {
        isLoading = true
        errorMessage = null
        runCatching { model.familyFilesList(path) }
            .onSuccess { listing = it }
            .onFailure { errorMessage = it.message ?: "Could not load the library." }
        isLoading = false
    }

    // The search box hits the server a moment after typing stops; blank means back to browsing.
    LaunchedEffect(query, reloadTick) {
        if (query.isBlank()) {
            searchResults = null
            isSearching = false
            return@LaunchedEffect
        }
        isSearching = true
        delay(300)
        runCatching { model.familyFilesSearch(query.trim()) }
            .onSuccess { searchResults = it }
            .onFailure { feedback.failed(it.message ?: "Search failed.") }
        isSearching = false
    }

    // The status feeds the job banner; while a job runs it is re-read every 2 s, and the folder
    // is reloaded once the job ends, since a restore or a duplicate sweep changes what is here.
    LaunchedEffect(reloadTick) {
        var wasBusy = false
        while (true) {
            val next = runCatching { model.familyFilesStatus() }.getOrNull()
            if (next != null) status = next
            val busy = next?.job != null
            if (wasBusy && !busy) reloadTick++
            wasBusy = busy
            if (!busy) break
            delay(2_000)
        }
    }

    // A finished upload batch: reload the folder and say how it went.
    LaunchedEffect(upload.finished) {
        if (upload.finished == 0L) return@LaunchedEffect
        reloadTick++
        val failures = upload.lastFailures
        when {
            failures.isNotEmpty() -> feedback.failed(
                (if (upload.lastUploaded > 0) "${upload.lastUploaded} added. " else "") +
                    "${failures.size} failed: ${failures.first()}",
            )
            upload.lastAlreadyThere == upload.lastUploaded && upload.lastUploaded > 0 ->
                feedback.note(if (upload.lastUploaded == 1) "That file was already in the library." else "Those files were already in the library.")
            upload.lastUploaded > 0 -> feedback.saved(if (upload.lastUploaded == 1) "Added 1 file." else "Added ${upload.lastUploaded} files.")
        }
    }

    val photoPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(10)) { uris ->
        model.uploadFamilyFiles(uris, path)
    }
    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        model.uploadFamilyFiles(uris, path)
    }

    fun act(failure: String, block: suspend () -> Unit) {
        scope.launch {
            runCatching { block() }
                .onSuccess { reloadTick++ }
                .onFailure { feedback.failed(it.message ?: failure) }
        }
    }

    val title = if (path.isEmpty()) "Files" else FamilyFilePaths.segments(path).joinToString(" › ")

    SaveFeedbackHost(feedback) {
        ScreenHeader(
            title,
            state,
            onRefresh = { reloadTick++ },
            actions = {
                IconButton(onClick = onTrash) { Icon(Icons.Default.Delete, contentDescription = "Trash") }
                IconButton(onClick = onDrives) { Icon(Icons.Default.Storage, contentDescription = "Backup drives") }
            },
        )

        Box(Modifier.fillMaxSize()) {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                verticalArrangement = Arrangement.spacedBy(6.dp),
                contentPadding = PaddingValues(bottom = 88.dp),
            ) {
                item {
                    OutlinedTextField(
                        value = query,
                        onValueChange = { query = it },
                        singleLine = true,
                        placeholder = { Text("Search the whole library") },
                        leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                        trailingIcon = {
                            if (query.isNotEmpty()) {
                                IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, contentDescription = "Clear search") }
                            }
                        },
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 4.dp),
                    )
                }

                upload.current?.let { progress ->
                    item {
                        Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                            Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                                Text(
                                    "Uploading ${progress.index} of ${progress.total}: ${progress.name}",
                                    style = MaterialTheme.typography.titleSmall,
                                    color = SentinelCyan,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                                val total = progress.totalBytes
                                if (total != null && total > 0) {
                                    LinearProgressIndicator(
                                        progress = { (progress.sentBytes.toFloat() / total).coerceIn(0f, 1f) },
                                        modifier = Modifier.fillMaxWidth(),
                                    )
                                    Text(
                                        "${formatFileSize(progress.sentBytes)} of ${formatFileSize(total)}",
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                } else {
                                    LinearProgressIndicator(Modifier.fillMaxWidth())
                                }
                            }
                        }
                    }
                }

                status?.libraryProblem?.let { problem ->
                    item { FamilyLibraryProblemBanner(problem, Modifier.padding(horizontal = 14.dp)) }
                }
                status?.job?.let { job ->
                    item { FamilyJobBanner(job, Modifier.padding(horizontal = 14.dp)) }
                }

                errorMessage?.let { message ->
                    item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
                }

                if (query.isNotBlank()) {
                    val results = searchResults
                    when {
                        results == null || isSearching -> item {
                            Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                                Spacer(Modifier.width(10.dp))
                                Text("Searching…", color = SentinelTextDim)
                            }
                        }
                        results.isEmpty() -> item { EmptyState("Nothing found", "No file name matches \"${query.trim()}\".") }
                        else -> items(results, key = { "s:" + it.path }) { file ->
                            FileRow(
                                model = model,
                                file = file,
                                showFolder = true,
                                onOpenFolder = { openFolder(FamilyFilePaths.parent(file.path).orEmpty()) },
                                onOpen = { scope.launch { openOrShareFamilyFile(context, model, file, share = false) } },
                                onShare = { scope.launch { openOrShareFamilyFile(context, model, file, share = true) } },
                                onRename = { newName ->
                                    act("Could not rename that.") {
                                        model.familyFilesMove(file.path, FamilyFilePaths.join(FamilyFilePaths.parent(file.path).orEmpty(), newName))
                                    }
                                },
                                onTrash = { act("Could not move that to the trash.") { model.familyFilesDelete(file.path) } },
                            )
                        }
                    }
                } else {
                    val current = listing
                    if (stack.isNotEmpty()) {
                        item { FilesBackRow(if (stack.last().isEmpty()) "Back to Files" else "Back to ${FamilyFilePaths.name(stack.last())}") { goUp() } }
                    }
                    if (current == null && isLoading) {
                        item {
                            Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                                Spacer(Modifier.width(10.dp))
                                Text("Loading…", color = SentinelTextDim)
                            }
                        }
                    } else if (current != null) {
                        if (current.folders.isEmpty() && current.files.isEmpty()) {
                            item {
                                EmptyState(
                                    if (path.isEmpty()) "The library is empty" else "This folder is empty",
                                    "Use + to add photos, videos or files from this phone, or make a folder.",
                                )
                            }
                        }
                        items(current.folders, key = { "d:" + it.path }) { folder ->
                            FolderRow(
                                folder = folder,
                                onOpen = { openFolder(folder.path) },
                                onRename = { newName ->
                                    act("Could not rename that folder.") {
                                        model.familyFilesMove(folder.path, FamilyFilePaths.join(FamilyFilePaths.parent(folder.path).orEmpty(), newName))
                                    }
                                },
                                onTrash = { act("Could not move that folder to the trash.") { model.familyFilesDelete(folder.path) } },
                            )
                        }
                        items(current.files, key = { "f:" + it.path }) { file ->
                            FileRow(
                                model = model,
                                file = file,
                                showFolder = false,
                                onOpenFolder = {},
                                onOpen = { scope.launch { openOrShareFamilyFile(context, model, file, share = false) } },
                                onShare = { scope.launch { openOrShareFamilyFile(context, model, file, share = true) } },
                                onRename = { newName ->
                                    act("Could not rename that.") { model.familyFilesMove(file.path, FamilyFilePaths.join(path, newName)) }
                                },
                                onTrash = { act("Could not move that to the trash.") { model.familyFilesDelete(file.path) } },
                            )
                        }
                    }
                }
            }

            Box(Modifier.align(Alignment.BottomEnd).padding(20.dp)) {
                FloatingActionButton(onClick = { addMenu = true }) {
                    Icon(Icons.Default.Add, contentDescription = "Add")
                }
                DropdownMenu(expanded = addMenu, onDismissRequest = { addMenu = false }) {
                    DropdownMenuItem(
                        text = { Text("Add photos & videos") },
                        leadingIcon = { Icon(Icons.Default.Photo, contentDescription = null) },
                        onClick = {
                            addMenu = false
                            photoPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
                        },
                    )
                    DropdownMenuItem(
                        text = { Text("Add files") },
                        leadingIcon = { Icon(Icons.Default.AttachFile, contentDescription = null) },
                        onClick = {
                            addMenu = false
                            filePicker.launch(arrayOf("*/*"))
                        },
                    )
                    DropdownMenuItem(
                        text = { Text("New folder") },
                        leadingIcon = { Icon(Icons.Default.CreateNewFolder, contentDescription = null) },
                        onClick = {
                            addMenu = false
                            showNewFolder = true
                        },
                    )
                }
            }
        }
    }

    if (showNewFolder) {
        NameDialog(
            title = "New folder",
            initial = "",
            confirmLabel = "Create",
            onConfirm = { name ->
                showNewFolder = false
                act("Could not create that folder.") { model.familyFilesCreateFolder(FamilyFilePaths.join(path, name)) }
            },
            onDismiss = { showNewFolder = false },
        )
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun FolderRow(
    folder: FamilyFolderEntry,
    onOpen: () -> Unit,
    onRename: (String) -> Unit,
    onTrash: () -> Unit,
) {
    var menu by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf(false) }
    var confirmTrash by remember { mutableStateOf(false) }

    Row(
        Modifier
            .fillMaxWidth()
            .combinedClickable(onClick = onOpen, onLongClick = { menu = true })
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(52.dp), contentAlignment = Alignment.Center) {
            Icon(Icons.Default.Folder, contentDescription = null, tint = SentinelCyan, modifier = Modifier.size(32.dp))
        }
        Spacer(Modifier.width(12.dp))
        Text(
            folder.name,
            style = MaterialTheme.typography.bodyLarge,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "More") }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text("Open") }, onClick = { menu = false; onOpen() })
                DropdownMenuItem(text = { Text("Rename") }, onClick = { menu = false; renaming = true })
                DropdownMenuItem(text = { Text("Move to trash", color = SentinelRed) }, onClick = { menu = false; confirmTrash = true })
            }
        }
    }

    if (renaming) {
        NameDialog("Rename folder", folder.name, "Rename", onConfirm = { renaming = false; onRename(it) }, onDismiss = { renaming = false })
    }
    if (confirmTrash) {
        ConfirmDialog(
            title = "Move \"${folder.name}\" to the trash?",
            text = "Everything in it goes too. It can be put back from the trash until the nightly pass clears it.",
            confirmLabel = "Move to trash",
            onConfirm = { confirmTrash = false; onTrash() },
            onDismiss = { confirmTrash = false },
        )
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun FileRow(
    model: SentinelViewModel,
    file: FamilyFileEntry,
    showFolder: Boolean,
    onOpenFolder: () -> Unit,
    onOpen: () -> Unit,
    onShare: () -> Unit,
    onRename: (String) -> Unit,
    onTrash: () -> Unit,
) {
    var menu by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf(false) }
    var confirmTrash by remember { mutableStateOf(false) }

    Row(
        Modifier
            .fillMaxWidth()
            .combinedClickable(onClick = onOpen, onLongClick = { menu = true })
            .padding(horizontal = 14.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        FamilyFileThumbnail(model, file, Modifier.size(52.dp))
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(file.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val detail = listOfNotNull(formatFileSize(file.size), formatFileDate(file.modifiedUtc)).joinToString(" · ")
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (showFolder) {
                val folder = FamilyFilePaths.parent(file.path).orEmpty()
                Text(
                    if (folder.isEmpty()) "In the top level" else "In $folder",
                    style = MaterialTheme.typography.labelSmall,
                    color = SentinelTextDim,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
        Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "More") }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text("Open") }, onClick = { menu = false; onOpen() })
                DropdownMenuItem(text = { Text("Share") }, onClick = { menu = false; onShare() })
                if (showFolder) {
                    DropdownMenuItem(text = { Text("Show folder") }, onClick = { menu = false; onOpenFolder() })
                }
                DropdownMenuItem(text = { Text("Rename") }, onClick = { menu = false; renaming = true })
                DropdownMenuItem(text = { Text("Move to trash", color = SentinelRed) }, onClick = { menu = false; confirmTrash = true })
            }
        }
    }

    if (renaming) {
        NameDialog("Rename file", file.name, "Rename", onConfirm = { renaming = false; onRename(it) }, onDismiss = { renaming = false })
    }
    if (confirmTrash) {
        ConfirmDialog(
            title = "Move \"${file.name}\" to the trash?",
            text = "It can be put back from the trash until the nightly pass clears it.",
            confirmLabel = "Move to trash",
            onConfirm = { confirmTrash = false; onTrash() },
            onDismiss = { confirmTrash = false },
        )
    }
}
