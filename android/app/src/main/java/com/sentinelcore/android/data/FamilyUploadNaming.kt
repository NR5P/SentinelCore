package com.sentinelcore.android.data

import android.content.ContentResolver
import android.net.Uri
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import java.time.Instant

// What to call a picked file in the library, and when it was really taken - the two things the
// PUT needs beyond the bytes. Everything a content provider might refuse to answer is optional.
object FamilyUploadNaming {
    fun displayName(resolver: ContentResolver, uri: Uri): String {
        val fromProvider = runCatching {
            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst() && !cursor.isNull(0)) cursor.getString(0) else null
            }
        }.getOrNull()?.trim()?.takeIf { it.isNotEmpty() && FamilyFilePaths.nameProblem(it) == null }
        if (fromProvider != null) return fromProvider
        val fromPath = uri.lastPathSegment?.substringAfterLast('/')?.trim()
            ?.takeIf { it.isNotEmpty() && FamilyFilePaths.nameProblem(it) == null && it.contains('.') }
        if (fromPath != null) return fromPath
        val type = runCatching { resolver.getType(uri) }.getOrNull() ?: "application/octet-stream"
        val extension = MimeTypeMap.getSingleton().getExtensionFromMimeType(type) ?: "bin"
        return "upload-${System.currentTimeMillis()}.$extension"
    }

    // A photo's own date when the picker's provider knows it (DATE_TAKEN is milliseconds,
    // DATE_MODIFIED seconds), as the ISO-8601 the server takes; null when nothing is known, and
    // the server then dates the file by the upload.
    fun modified(resolver: ContentResolver, uri: Uri): String? {
        val taken = queryLong(resolver, uri, MediaStore.MediaColumns.DATE_TAKEN)?.takeIf { it > 0 }
        if (taken != null) return Instant.ofEpochMilli(taken).toString()
        val modified = queryLong(resolver, uri, MediaStore.MediaColumns.DATE_MODIFIED)?.takeIf { it > 0 }
        if (modified != null) return Instant.ofEpochSecond(modified).toString()
        return null
    }

    private fun queryLong(resolver: ContentResolver, uri: Uri, column: String): Long? =
        runCatching {
            resolver.query(uri, arrayOf(column), null, null, null)?.use { cursor ->
                val index = cursor.getColumnIndex(column)
                if (index >= 0 && cursor.moveToFirst() && !cursor.isNull(index)) cursor.getLong(index) else null
            }
        }.getOrNull()
}
