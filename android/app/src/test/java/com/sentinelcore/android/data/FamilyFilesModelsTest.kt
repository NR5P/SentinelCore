package com.sentinelcore.android.data

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class FamilyFilesModelsTest {

    @Test
    fun `a full status parses every part`() {
        val status = parseFamilyFilesStatus(
            JSONObject(
                """
                { "enabled": true, "isAdmin": true, "username": "nathan", "libraryFolder": "/srv/family",
                  "maxUploadBytes": 4294967296,
                  "library": { "files": 1200, "bytes": 5368709120, "indexedUtc": "2026-09-25T02:00:00Z" },
                  "drives": [ { "id": "d1", "name": "USB disk", "kind": "folder", "enabled": true, "location": "/media/usb",
                                "folderPath": "/media/usb", "sftpHost": null, "sftpPort": 22, "sftpUsername": null, "sftpFolder": null,
                                "sftpHostKeyFingerprint": null, "hasPassword": false,
                                "lastAttemptUtc": "2026-09-25T02:05:00Z", "lastSuccessUtc": "2026-09-25T02:05:00Z",
                                "lastResult": "ok", "lastMessage": "1200 files", "lastFileCount": 1200, "lastBytes": 5368709120,
                                "lastCopied": 3, "lastRemoved": 0, "lastFreeBytes": 99999999999, "lastHostKeyFingerprint": null } ],
                  "nightly": { "hour": 2, "minute": 30, "removeDuplicates": true, "trashKeepDays": 30,
                               "nextRunUtc": "2026-09-26T02:30:00Z", "lastRunUtc": "2026-09-25T02:30:00Z", "lastResult": "ok", "lastMessage": "done" },
                  "duplicates": { "ranUtc": "2026-09-25T02:31:00Z", "removed": true, "groups": 2, "copies": 3, "bytes": 1000, "filesChecked": 1200, "trashBatchId": "b9" },
                  "trash": { "batches": 1, "files": 3, "bytes": 1000 },
                  "job": { "kind": "sync", "driveId": "d1", "driveName": "USB disk", "startedUtc": "2026-09-25T10:00:00Z", "message": "copying", "done": 4, "total": 10 } }
                """.trimIndent(),
            ),
        )

        assertTrue(status.enabled)
        assertTrue(status.isAdmin)
        assertEquals("nathan", status.username)
        assertEquals("/srv/family", status.libraryFolder)
        assertEquals(4294967296L, status.maxUploadBytes)
        assertEquals(1200, status.library?.files)
        assertEquals(5368709120L, status.library?.bytes)

        val drive = status.drives.single()
        assertEquals("d1", drive.id)
        assertFalse(drive.isSftp)
        assertEquals("/media/usb", drive.folderPath)
        assertNull(drive.sftpHost)
        assertEquals(22, drive.sftpPort)
        assertEquals(false, drive.hasPassword)
        assertEquals("ok", drive.lastResult)
        assertEquals(1200, drive.lastFileCount)
        assertEquals(99999999999L, drive.lastFreeBytes)
        assertNull(drive.lastHostKeyFingerprint)

        assertEquals(2, status.nightly?.hour)
        assertEquals(30, status.nightly?.minute)
        assertTrue(status.nightly?.removeDuplicates == true)
        assertEquals("2026-09-26T02:30:00Z", status.nightly?.nextRunUtc)

        assertEquals(3, status.duplicates?.copies)
        assertEquals("b9", status.duplicates?.trashBatchId)
        assertTrue(status.duplicates?.sample?.isEmpty() == true)

        assertEquals(3, status.trash?.files)

        assertEquals("sync", status.job?.kind)
        assertEquals("USB disk", status.job?.driveName)
        assertEquals(4, status.job?.done)
        assertEquals(10, status.job?.total)
    }

    @Test
    fun `a disabled status carries nothing but the three flags`() {
        val status = parseFamilyFilesStatus(JSONObject("""{ "enabled": false, "isAdmin": false, "username": null }"""))

        assertFalse(status.enabled)
        assertFalse(status.isAdmin)
        assertNull(status.username)
        assertNull(status.libraryFolder)
        assertEquals(0L, status.maxUploadBytes)
        assertNull(status.library)
        assertTrue(status.drives.isEmpty())
        assertNull(status.nightly)
        assertNull(status.duplicates)
        assertNull(status.trash)
        assertNull(status.job)
    }

    @Test
    fun `a viewer's status hides the drive settings and idle nulls parse`() {
        val status = parseFamilyFilesStatus(
            JSONObject(
                """
                { "enabled": true, "isAdmin": false, "username": "kid", "libraryFolder": null, "maxUploadBytes": 10,
                  "library": { "files": 0, "bytes": 0, "indexedUtc": null },
                  "drives": [ { "id": "d2", "name": "NAS", "kind": "sftp", "enabled": false, "location": "media@nas:/backup",
                                "folderPath": null, "sftpHost": null, "sftpPort": null, "sftpUsername": null, "sftpFolder": null,
                                "sftpHostKeyFingerprint": null, "hasPassword": null,
                                "lastAttemptUtc": null, "lastSuccessUtc": null, "lastResult": "", "lastMessage": "",
                                "lastFileCount": null, "lastBytes": null, "lastCopied": null, "lastRemoved": null,
                                "lastFreeBytes": null, "lastHostKeyFingerprint": null } ],
                  "nightly": { "hour": 2, "minute": 0, "removeDuplicates": false, "trashKeepDays": 14,
                               "nextRunUtc": null, "lastRunUtc": null, "lastResult": "", "lastMessage": "" },
                  "duplicates": null, "trash": { "batches": 0, "files": 0, "bytes": 0 }, "job": null }
                """.trimIndent(),
            ),
        )

        assertFalse(status.isAdmin)
        assertNull(status.libraryFolder)
        assertNull(status.library?.indexedUtc)
        val drive = status.drives.single()
        assertTrue(drive.isSftp)
        assertFalse(drive.enabled)
        assertNull(drive.folderPath)
        assertNull(drive.sftpPort)
        assertNull(drive.hasPassword)
        assertNull(drive.lastSuccessUtc)
        assertEquals("", drive.lastResult)
        assertNull(drive.lastFileCount)
        assertNull(status.nightly?.nextRunUtc)
        assertNull(status.duplicates)
        assertNull(status.job)
    }

    @Test
    fun `a folder listing keeps folders and files apart`() {
        val listing = parseFamilyFolderListing(
            JSONObject(
                """
                { "path": "Photos/2026", "name": "2026", "parent": "Photos",
                  "folders": [ { "path": "Photos/2026/Beach", "name": "Beach" } ],
                  "files": [ { "path": "Photos/2026/sunset.jpg", "name": "sunset.jpg", "size": 2048, "modifiedUtc": "2026-08-01T18:00:00Z",
                               "sha256": "abc", "isImage": true, "contentType": "image/jpeg" },
                             { "path": "Photos/2026/notes.txt", "name": "notes.txt", "size": 12, "modifiedUtc": "2026-08-02T18:00:00Z",
                               "sha256": null, "isImage": false, "contentType": "text/plain" } ] }
                """.trimIndent(),
            ),
        )

        assertEquals("Photos/2026", listing.path)
        assertEquals("2026", listing.name)
        assertEquals("Photos", listing.parent)
        assertEquals(listOf("Beach"), listing.folders.map { it.name })
        assertEquals(2, listing.files.size)
        assertTrue(listing.files[0].isImage)
        assertEquals("abc", listing.files[0].sha256)
        assertEquals(2048L, listing.files[0].size)
        assertFalse(listing.files[1].isImage)
        assertNull(listing.files[1].sha256)
    }

    @Test
    fun `the root listing has no parent`() {
        val listing = parseFamilyFolderListing(JSONObject("""{ "path": "", "name": "", "parent": null, "folders": [], "files": [] }"""))

        assertEquals("", listing.path)
        assertNull(listing.parent)
        assertTrue(listing.folders.isEmpty())
        assertTrue(listing.files.isEmpty())
    }

    @Test
    fun `search results, uploads and trash batches parse`() {
        val results = parseFamilyFileList(
            JSONObject("""{ "files": [ { "path": "a.jpg", "name": "a.jpg", "size": 1, "modifiedUtc": "2026-01-01T00:00:00Z", "sha256": null, "isImage": true, "contentType": "image/jpeg" } ] }"""),
        )
        assertEquals(listOf("a.jpg"), results.map { it.path })

        val upload = parseFamilyUploadResult(JSONObject("""{ "path": "Docs/tax (2).pdf", "name": "tax (2).pdf", "size": 99, "sha256": "ff", "alreadyThere": false }"""))
        assertEquals("Docs/tax (2).pdf", upload.path)
        assertFalse(upload.alreadyThere)

        val batches = parseFamilyTrashBatches(
            JSONObject(
                """
                { "batches": [ { "id": "b1", "deletedUtc": "2026-09-25T09:00:00Z", "reason": "duplicate", "by": null,
                                 "files": [ { "path": "Photos/copy.jpg", "name": "copy.jpg", "size": 5, "keptCopy": "Photos/orig.jpg" } ] },
                               { "id": "b2", "deletedUtc": "2026-09-25T09:30:00Z", "reason": "deleted", "by": "nathan",
                                 "files": [ { "path": "old.txt", "name": "old.txt", "size": 1, "keptCopy": null } ] } ] }
                """.trimIndent(),
            ),
        )
        assertEquals(2, batches.size)
        assertEquals("duplicate", batches[0].reason)
        assertNull(batches[0].by)
        assertEquals("Photos/orig.jpg", batches[0].files.single().keptCopy)
        assertEquals("nathan", batches[1].by)
        assertNull(batches[1].files.single().keptCopy)
    }

    @Test
    fun `the duplicates report carries its sample`() {
        val report = parseFamilyDuplicateReport(
            JSONObject(
                """
                { "ranUtc": "2026-09-25T02:31:00Z", "removed": false, "groups": 1, "copies": 2, "bytes": 20, "filesChecked": 10, "trashBatchId": null,
                  "sample": [ { "kept": "a.jpg", "copies": [ "b.jpg", "c.jpg" ], "size": 10 } ] }
                """.trimIndent(),
            ),
        )

        assertFalse(report.removed)
        assertNull(report.trashBatchId)
        assertEquals(1, report.sample.size)
        assertEquals("a.jpg", report.sample[0].kept)
        assertEquals(listOf("b.jpg", "c.jpg"), report.sample[0].copies)
        assertEquals(10L, report.sample[0].size)
    }

    @Test
    fun `a drive input sends every field and the id only when editing`() {
        val fresh = FamilyDriveInput(name = "USB", kind = "folder", enabled = true, folderPath = "/media/usb").toJson()

        assertFalse(fresh.has("id"))
        assertEquals("USB", fresh.getString("name"))
        assertEquals("folder", fresh.getString("kind"))
        assertTrue(fresh.getBoolean("enabled"))
        assertEquals("/media/usb", fresh.getString("folderPath"))
        assertEquals("", fresh.getString("sftpHost"))
        assertEquals(22, fresh.getInt("sftpPort"))
        assertEquals("", fresh.getString("sftpUsername"))
        assertEquals("", fresh.getString("sftpPassword"))
        assertEquals("", fresh.getString("sftpFolder"))
        assertEquals("", fresh.getString("sftpHostKeyFingerprint"))

        val edited = FamilyDriveInput(
            id = "d7", name = "NAS", kind = "sftp", enabled = false,
            sftpHost = "nas.local", sftpPort = 2222, sftpUsername = "media", sftpPassword = "",
            sftpFolder = "/backup", sftpHostKeyFingerprint = "SHA256:xyz",
        ).toJson()

        assertEquals("d7", edited.getString("id"))
        assertEquals("sftp", edited.getString("kind"))
        assertEquals(2222, edited.getInt("sftpPort"))
        assertEquals("SHA256:xyz", edited.getString("sftpHostKeyFingerprint"))
        assertEquals("", edited.getString("sftpPassword"))
        assertEquals("", edited.getString("folderPath"))
    }

    @Test
    fun `paths join and split`() {
        assertEquals("beach.jpg", FamilyFilePaths.join("", "beach.jpg"))
        assertEquals("Photos/2026/beach.jpg", FamilyFilePaths.join("Photos/2026", "beach.jpg"))
        assertEquals("Photos/2026/beach.jpg", FamilyFilePaths.join("/Photos/2026/", "/beach.jpg"))
        assertEquals("Photos", FamilyFilePaths.join("Photos", ""))

        assertNull(FamilyFilePaths.parent(""))
        assertEquals("", FamilyFilePaths.parent("beach.jpg"))
        assertEquals("Photos/2026", FamilyFilePaths.parent("Photos/2026/beach.jpg"))
        assertEquals("Photos", FamilyFilePaths.parent("Photos/2026/"))

        assertEquals("beach.jpg", FamilyFilePaths.name("Photos/2026/beach.jpg"))
        assertEquals("beach.jpg", FamilyFilePaths.name("beach.jpg"))
        assertEquals(listOf("Photos", "2026"), FamilyFilePaths.segments("Photos/2026"))
        assertTrue(FamilyFilePaths.segments("").isEmpty())
    }

    @Test
    fun `paths encode per segment with spaces as percent-20`() {
        assertEquals("Photos/Summer%202026/beach%20%26%20sun.jpg", FamilyFilePaths.encode("Photos/Summer 2026/beach & sun.jpg"))
        assertEquals("caf%C3%A9.txt", FamilyFilePaths.encode("café.txt"))
        assertEquals("", FamilyFilePaths.encode(""))
        assertEquals("a/b", FamilyFilePaths.encode("/a//b/"))
    }

    @Test
    fun `names follow the server's rule`() {
        assertNull(FamilyFilePaths.nameProblem("beach.jpg"))
        assertNull(FamilyFilePaths.nameProblem("Summer 2026"))
        assertTrue(FamilyFilePaths.nameProblem("") != null)
        assertTrue(FamilyFilePaths.nameProblem("   ") != null)
        assertTrue(FamilyFilePaths.nameProblem(".hidden") != null)
        assertTrue(FamilyFilePaths.nameProblem("a/b") != null)
        assertTrue(FamilyFilePaths.nameProblem("what?") != null)
        assertTrue(FamilyFilePaths.nameProblem("a:b") != null)
    }
}
