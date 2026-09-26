package com.sentinelcore.android.data

import org.json.JSONArray
import org.json.JSONObject
import java.net.URLEncoder
import java.nio.charset.StandardCharsets

// The household's shared file library and its backup drives - the server's /api/family-files
// routes (core/SentinelCore.Web/FamilyFilesEndpoints.cs). Plain data classes and hand-written
// parsers, like the rest of this package, so the JVM unit tests can cover the wire format
// without Android around.
//
// Every path is relative to the library root with forward slashes and no leading slash;
// "" is the root itself.

data class FamilyFilesStatus(
    val enabled: Boolean,
    val isAdmin: Boolean,
    val username: String?,
    // Admin only; null for a Viewer.
    val libraryFolder: String?,
    val maxUploadBytes: Long,
    val library: FamilyLibrarySummary?,
    val drives: List<FamilyDrive>,
    val nightly: FamilyNightly?,
    val duplicates: FamilyDuplicateReport?,
    val trash: FamilyTrashSummary?,
    val job: FamilyJob?,
)

data class FamilyLibrarySummary(
    val files: Int,
    val bytes: Long,
    val indexedUtc: String?,
)

data class FamilyDrive(
    val id: String,
    val name: String,
    // "folder" or "sftp".
    val kind: String,
    val enabled: Boolean,
    // Where it points, in words - the folder path, or user@host:folder.
    val location: String,
    // The admin-only settings; all null for a Viewer.
    val folderPath: String?,
    val sftpHost: String?,
    val sftpPort: Int?,
    val sftpUsername: String?,
    val sftpFolder: String?,
    val sftpHostKeyFingerprint: String?,
    val hasPassword: Boolean?,
    val lastAttemptUtc: String?,
    val lastSuccessUtc: String?,
    // "ok", "failed" or "" when it has never run.
    val lastResult: String,
    val lastMessage: String,
    val lastFileCount: Int?,
    val lastBytes: Long?,
    val lastCopied: Int?,
    val lastRemoved: Int?,
    val lastFreeBytes: Long?,
    val lastHostKeyFingerprint: String?,
) {
    val isSftp: Boolean get() = kind == "sftp"
}

data class FamilyNightly(
    val hour: Int,
    val minute: Int,
    val removeDuplicates: Boolean,
    val trashKeepDays: Int,
    val nextRunUtc: String?,
    val lastRunUtc: String?,
    // "ok", "incomplete", "failed" or "".
    val lastResult: String,
    val lastMessage: String,
)

data class FamilyDuplicateReport(
    val ranUtc: String,
    val removed: Boolean,
    val groups: Int,
    val copies: Int,
    val bytes: Long,
    val filesChecked: Int,
    val trashBatchId: String?,
    // Only the GET /duplicates report carries a sample; the status summary leaves it empty.
    val sample: List<FamilyDuplicateGroup>,
)

data class FamilyDuplicateGroup(
    val kept: String,
    val copies: List<String>,
    val size: Long,
)

data class FamilyTrashSummary(
    val batches: Int,
    val files: Int,
    val bytes: Long,
)

data class FamilyJob(
    // "nightly", "sync", "restore", "duplicates" or "index".
    val kind: String,
    val driveId: String?,
    val driveName: String?,
    val startedUtc: String,
    val message: String,
    val done: Int,
    val total: Int,
)

data class FamilyFolderEntry(
    val path: String,
    val name: String,
)

data class FamilyFileEntry(
    val path: String,
    val name: String,
    val size: Long,
    val modifiedUtc: String,
    val sha256: String?,
    val isImage: Boolean,
    val contentType: String,
)

data class FamilyFolderListing(
    val path: String,
    val name: String,
    val parent: String?,
    val folders: List<FamilyFolderEntry>,
    val files: List<FamilyFileEntry>,
)

data class FamilyUploadResult(
    val path: String,
    val name: String,
    val size: Long,
    val sha256: String?,
    // The same bytes were already there under that name, so nothing was written.
    val alreadyThere: Boolean,
)

data class FamilyTrashFile(
    val path: String,
    val name: String,
    val size: Long,
    // For a duplicate: the path of the copy that stayed in the library.
    val keptCopy: String?,
)

data class FamilyTrashBatch(
    val id: String,
    val deletedUtc: String,
    // "deleted" or "duplicate".
    val reason: String,
    val by: String?,
    val files: List<FamilyTrashFile>,
)

data class FamilyDeleteResult(val trashed: Int, val batchId: String?)

data class FamilyRestoreResult(val restored: Int, val detail: String)

data class FamilyDriveTestResult(
    val ok: Boolean,
    val detail: String,
    // What the server saw when it connected, whether or not that matched the form.
    val hostKeyFingerprint: String?,
)

// The drive form as sent to POST/PUT /drives and /drives/test. Every field goes every time;
// blanks are fine for the kind not in use. A blank password on an existing drive keeps the
// saved one, which is why `id` rides along on a test of an existing drive.
data class FamilyDriveInput(
    val id: String? = null,
    val name: String,
    val kind: String,
    val enabled: Boolean,
    val folderPath: String = "",
    val sftpHost: String = "",
    val sftpPort: Int = 22,
    val sftpUsername: String = "",
    val sftpPassword: String = "",
    val sftpFolder: String = "",
    val sftpHostKeyFingerprint: String = "",
) {
    fun toJson(): JSONObject = JSONObject().apply {
        id?.let { put("id", it) }
        put("name", name)
        put("kind", kind)
        put("enabled", enabled)
        put("folderPath", folderPath)
        put("sftpHost", sftpHost)
        put("sftpPort", sftpPort)
        put("sftpUsername", sftpUsername)
        put("sftpPassword", sftpPassword)
        put("sftpFolder", sftpFolder)
        put("sftpHostKeyFingerprint", sftpHostKeyFingerprint)
    }
}

// What the Files screen's upload banner shows: "Uploading 2 of 5: beach.jpg", with bytes when
// the provider said how big the file is.
data class FamilyUploadProgress(
    val index: Int,
    val total: Int,
    val name: String,
    val sentBytes: Long,
    val totalBytes: Long?,
)

// The upload queue as the screen sees it. `finished` ticks once per batch so the folder can be
// reloaded; the `last*` fields describe that batch.
data class FamilyUploadState(
    val current: FamilyUploadProgress? = null,
    val finished: Long = 0,
    val lastUploaded: Int = 0,
    val lastAlreadyThere: Int = 0,
    val lastFailures: List<String> = emptyList(),
) {
    val isUploading: Boolean get() = current != null
}

// Library paths: joining, splitting and getting them into a URL.
object FamilyFilePaths {
    fun join(folder: String, name: String): String {
        val cleanFolder = folder.trim('/')
        val cleanName = name.trim('/')
        return when {
            cleanFolder.isEmpty() -> cleanName
            cleanName.isEmpty() -> cleanFolder
            else -> "$cleanFolder/$cleanName"
        }
    }

    // The folder a path sits in: "" for something at the root, null for the root itself.
    fun parent(path: String): String? {
        val clean = path.trim('/')
        if (clean.isEmpty()) return null
        return clean.substringBeforeLast('/', "")
    }

    fun name(path: String): String = path.trim('/').substringAfterLast('/')

    // Breadcrumb pieces, root first: "Photos/2026" -> ["Photos", "2026"].
    fun segments(path: String): List<String> = path.trim('/').split('/').filter(String::isNotEmpty)

    // For the /files/{path} and /thumbnail/{path} routes: each segment percent-encoded on its
    // own, slashes kept. URLEncoder is a form encoder, so its "+" for a space has to become the
    // "%20" a path needs.
    fun encode(path: String): String =
        segments(path).joinToString("/") { segment ->
            URLEncoder.encode(segment, StandardCharsets.UTF_8.name()).replace("+", "%20")
        }

    // Names may not start with "." or contain any of these, matching the server's rule; a
    // slash would make a folder out of a name.
    fun nameProblem(name: String): String? {
        val trimmed = name.trim()
        if (trimmed.isEmpty()) return "Enter a name."
        if (trimmed.startsWith(".")) return "A name can't start with a dot."
        if (trimmed.any { it in "<>:\"|?*\\/" }) return "A name can't contain < > : \" | ? * \\ or /."
        return null
    }
}

private fun JSONObject.stringOrNull(key: String): String? =
    if (!has(key) || isNull(key)) null else optString(key)

private fun JSONObject.intOrNull(key: String): Int? =
    if (!has(key) || isNull(key)) null else optInt(key)

private fun JSONObject.longOrNull(key: String): Long? =
    if (!has(key) || isNull(key)) null else optLong(key)

private fun JSONObject.booleanOrNull(key: String): Boolean? =
    if (!has(key) || isNull(key)) null else optBoolean(key)

private fun <T> JSONArray?.mapObjects(transform: (JSONObject) -> T): List<T> {
    if (this == null) return emptyList()
    return buildList {
        for (index in 0 until length()) {
            optJSONObject(index)?.let { add(transform(it)) }
        }
    }
}

private fun JSONArray?.strings(): List<String> {
    if (this == null) return emptyList()
    return buildList {
        for (index in 0 until length()) add(optString(index))
    }
}

internal fun parseFamilyFilesStatus(json: JSONObject): FamilyFilesStatus =
    FamilyFilesStatus(
        enabled = json.optBoolean("enabled"),
        isAdmin = json.optBoolean("isAdmin"),
        username = json.stringOrNull("username"),
        libraryFolder = json.stringOrNull("libraryFolder"),
        maxUploadBytes = json.optLong("maxUploadBytes"),
        library = json.optJSONObject("library")?.let {
            FamilyLibrarySummary(
                files = it.optInt("files"),
                bytes = it.optLong("bytes"),
                indexedUtc = it.stringOrNull("indexedUtc"),
            )
        },
        drives = json.optJSONArray("drives").mapObjects(::parseFamilyDrive),
        nightly = json.optJSONObject("nightly")?.let(::parseFamilyNightly),
        duplicates = json.optJSONObject("duplicates")?.let(::parseFamilyDuplicateReport),
        trash = json.optJSONObject("trash")?.let {
            FamilyTrashSummary(
                batches = it.optInt("batches"),
                files = it.optInt("files"),
                bytes = it.optLong("bytes"),
            )
        },
        job = json.optJSONObject("job")?.let(::parseFamilyJob),
    )

internal fun parseFamilyDrive(json: JSONObject): FamilyDrive =
    FamilyDrive(
        id = json.optString("id"),
        name = json.optString("name"),
        kind = json.optString("kind"),
        enabled = json.optBoolean("enabled"),
        location = json.optString("location"),
        folderPath = json.stringOrNull("folderPath"),
        sftpHost = json.stringOrNull("sftpHost"),
        sftpPort = json.intOrNull("sftpPort"),
        sftpUsername = json.stringOrNull("sftpUsername"),
        sftpFolder = json.stringOrNull("sftpFolder"),
        sftpHostKeyFingerprint = json.stringOrNull("sftpHostKeyFingerprint"),
        hasPassword = json.booleanOrNull("hasPassword"),
        lastAttemptUtc = json.stringOrNull("lastAttemptUtc"),
        lastSuccessUtc = json.stringOrNull("lastSuccessUtc"),
        lastResult = json.optString("lastResult"),
        lastMessage = json.optString("lastMessage"),
        lastFileCount = json.intOrNull("lastFileCount"),
        lastBytes = json.longOrNull("lastBytes"),
        lastCopied = json.intOrNull("lastCopied"),
        lastRemoved = json.intOrNull("lastRemoved"),
        lastFreeBytes = json.longOrNull("lastFreeBytes"),
        lastHostKeyFingerprint = json.stringOrNull("lastHostKeyFingerprint"),
    )

internal fun parseFamilyNightly(json: JSONObject): FamilyNightly =
    FamilyNightly(
        hour = json.optInt("hour"),
        minute = json.optInt("minute"),
        removeDuplicates = json.optBoolean("removeDuplicates"),
        trashKeepDays = json.optInt("trashKeepDays"),
        nextRunUtc = json.stringOrNull("nextRunUtc"),
        lastRunUtc = json.stringOrNull("lastRunUtc"),
        lastResult = json.optString("lastResult"),
        lastMessage = json.optString("lastMessage"),
    )

internal fun parseFamilyDuplicateReport(json: JSONObject): FamilyDuplicateReport =
    FamilyDuplicateReport(
        ranUtc = json.optString("ranUtc"),
        removed = json.optBoolean("removed"),
        groups = json.optInt("groups"),
        copies = json.optInt("copies"),
        bytes = json.optLong("bytes"),
        filesChecked = json.optInt("filesChecked"),
        trashBatchId = json.stringOrNull("trashBatchId"),
        sample = json.optJSONArray("sample").mapObjects { group ->
            FamilyDuplicateGroup(
                kept = group.optString("kept"),
                copies = group.optJSONArray("copies").strings(),
                size = group.optLong("size"),
            )
        },
    )

internal fun parseFamilyJob(json: JSONObject): FamilyJob =
    FamilyJob(
        kind = json.optString("kind"),
        driveId = json.stringOrNull("driveId"),
        driveName = json.stringOrNull("driveName"),
        startedUtc = json.optString("startedUtc"),
        message = json.optString("message"),
        done = json.optInt("done"),
        total = json.optInt("total"),
    )

internal fun parseFamilyFileEntry(json: JSONObject): FamilyFileEntry =
    FamilyFileEntry(
        path = json.optString("path"),
        name = json.optString("name"),
        size = json.optLong("size"),
        modifiedUtc = json.optString("modifiedUtc"),
        sha256 = json.stringOrNull("sha256"),
        isImage = json.optBoolean("isImage"),
        contentType = json.optString("contentType"),
    )

internal fun parseFamilyFolderListing(json: JSONObject): FamilyFolderListing =
    FamilyFolderListing(
        path = json.optString("path"),
        name = json.optString("name"),
        parent = json.stringOrNull("parent"),
        folders = json.optJSONArray("folders").mapObjects {
            FamilyFolderEntry(path = it.optString("path"), name = it.optString("name"))
        },
        files = json.optJSONArray("files").mapObjects(::parseFamilyFileEntry),
    )

internal fun parseFamilyFileList(json: JSONObject): List<FamilyFileEntry> =
    json.optJSONArray("files").mapObjects(::parseFamilyFileEntry)

internal fun parseFamilyUploadResult(json: JSONObject): FamilyUploadResult =
    FamilyUploadResult(
        path = json.optString("path"),
        name = json.optString("name"),
        size = json.optLong("size"),
        sha256 = json.stringOrNull("sha256"),
        alreadyThere = json.optBoolean("alreadyThere"),
    )

internal fun parseFamilyTrashBatches(json: JSONObject): List<FamilyTrashBatch> =
    json.optJSONArray("batches").mapObjects { batch ->
        FamilyTrashBatch(
            id = batch.optString("id"),
            deletedUtc = batch.optString("deletedUtc"),
            reason = batch.optString("reason"),
            by = batch.stringOrNull("by"),
            files = batch.optJSONArray("files").mapObjects { file ->
                FamilyTrashFile(
                    path = file.optString("path"),
                    name = file.optString("name"),
                    size = file.optLong("size"),
                    keptCopy = file.stringOrNull("keptCopy"),
                )
            },
        )
    }

internal fun parseFamilyDriveList(json: JSONObject): List<FamilyDrive> =
    json.optJSONArray("drives").mapObjects(::parseFamilyDrive)
