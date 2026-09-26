namespace SentinelCore.Client.Models;

// The wire types of /api/family-files - the household's shared file library and its backup
// drives. Field for field the contract in core/SentinelCore.Web/FamilyFilesEndpoints.cs. Anything
// the server sends only to an admin, or only once the feature is on, is nullable here: a Viewer's
// status carries none of the admin fields, and a server with the feature switched off answers
// with nothing but enabled/isAdmin/username.

/// <summary>GET /api/family-files/status.</summary>
public sealed class FamilyFilesStatus
{
    public bool Enabled { get; init; }
    public bool IsAdmin { get; init; }
    public string? Username { get; init; }

    // Admin only; null for a Viewer.
    public string? LibraryFolder { get; init; }

    public long? MaxUploadBytes { get; init; }
    public FamilyLibraryTotals? Library { get; init; }
    public List<FamilyDrive> Drives { get; init; } = new();
    public FamilyNightlyStatus? Nightly { get; init; }
    public FamilyDuplicateReport? Duplicates { get; init; }
    public FamilyTrashTotals? Trash { get; init; }
    public FamilyFilesJob? Job { get; init; }
}

public sealed class FamilyLibraryTotals
{
    public int Files { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset? IndexedUtc { get; init; }
}

public sealed class FamilyTrashTotals
{
    public int Batches { get; init; }
    public int Files { get; init; }
    public long Bytes { get; init; }
}

public sealed class FamilyNightlyStatus
{
    public int Hour { get; init; }
    public int Minute { get; init; }
    public bool RemoveDuplicates { get; init; }
    public int TrashKeepDays { get; init; }
    public DateTimeOffset? NextRunUtc { get; init; }
    public DateTimeOffset? LastRunUtc { get; init; }
    public string LastResult { get; init; } = ""; // "ok" | "incomplete" | "failed" | ""
    public string LastMessage { get; init; } = "";
}

/// <summary>
/// One backup drive as the server describes it. The connection details (folderPath, sftp*,
/// hasPassword) are only sent to an admin; everyone gets the name, the location in words, and how
/// the last backup went.
/// </summary>
public sealed class FamilyDrive
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "folder"; // "folder" | "sftp"
    public bool Enabled { get; init; } = true;
    public string Location { get; init; } = "";

    public string? FolderPath { get; init; }
    public string? SftpHost { get; init; }
    public int? SftpPort { get; init; }
    public string? SftpUsername { get; init; }
    public string? SftpFolder { get; init; }
    public string? SftpHostKeyFingerprint { get; init; }
    public bool? HasPassword { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string LastResult { get; init; } = ""; // "ok" | "failed" | ""
    public string LastMessage { get; init; } = "";
    public int? LastFileCount { get; init; }
    public long? LastBytes { get; init; }
    public int? LastCopied { get; init; }
    public int? LastRemoved { get; init; }
    public long? LastFreeBytes { get; init; }
    public string? LastHostKeyFingerprint { get; init; }

    public bool IsSftp => string.Equals(Kind, "sftp", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The body of POST /drives, PUT /drives/{id} and POST /drives/test. Every field travels, blanks
/// for the kind not in use. A blank password on an existing drive keeps the saved one, which is
/// why Id is here too: /drives/test needs it to know which saved password "blank" means.
/// </summary>
public sealed class FamilyDriveInput
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "folder";
    public bool Enabled { get; set; } = true;
    public string FolderPath { get; set; } = "";
    public string SftpHost { get; set; } = "";
    public int SftpPort { get; set; } = 22;
    public string SftpUsername { get; set; } = "";
    public string SftpPassword { get; set; } = "";
    public string SftpFolder { get; set; } = "";
    public string SftpHostKeyFingerprint { get; set; } = "";

    public static FamilyDriveInput FromDrive(FamilyDrive drive) => new()
    {
        Id = drive.Id,
        Name = drive.Name,
        Kind = drive.Kind,
        Enabled = drive.Enabled,
        FolderPath = drive.FolderPath ?? "",
        SftpHost = drive.SftpHost ?? "",
        SftpPort = drive.SftpPort ?? 22,
        SftpUsername = drive.SftpUsername ?? "",
        SftpFolder = drive.SftpFolder ?? "",
        SftpHostKeyFingerprint = drive.SftpHostKeyFingerprint ?? "",
    };
}

public sealed class FamilyDrivesListing
{
    public List<FamilyDrive> Drives { get; init; } = new();
}

/// <summary>DELETE /drives/{id}.</summary>
public sealed class FamilyDriveRemoveResult
{
    public bool Removed { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// POST /drives/test. The server answers 200 with a detail, or 400 with an error - and in both
/// cases may carry the host key fingerprint it saw, which is how a fingerprint gets filled in
/// without anyone typing 43 characters of base64.
/// </summary>
public sealed class FamilyDriveTestResult
{
    public bool Ok { get; init; }
    public string? Detail { get; init; }
    public string? Error { get; init; }
    public string? HostKeyFingerprint { get; init; }

    public string Message => Ok ? Detail ?? "Connected." : Error ?? "The connection test failed.";
}

/// <summary>The 202 of every "start a job" route.</summary>
public sealed class FamilyJobStart
{
    public bool Started { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>What the server is doing right now in the background, if anything.</summary>
public sealed class FamilyFilesJob
{
    public string Kind { get; init; } = ""; // "nightly" | "sync" | "restore" | "duplicates" | "index"
    public string? DriveId { get; init; }
    public string? DriveName { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public string Message { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }

    public string Title => Kind switch
    {
        "nightly" => "Nightly pass",
        "sync" => DriveName is { Length: > 0 } name ? $"Backing up to {name}" : "Backing up",
        "restore" => DriveName is { Length: > 0 } name ? $"Bringing files back from {name}" : "Bringing files back",
        "duplicates" => "Checking for duplicates",
        "index" => "Indexing the library",
        _ => "Working",
    };
}

public sealed class FamilyJobResponse
{
    public FamilyFilesJob? Job { get; init; }
}

/// <summary>GET /duplicates, and the summary inside /status (which has no sample).</summary>
public sealed class FamilyDuplicateReport
{
    public DateTimeOffset RanUtc { get; init; }
    public bool Removed { get; init; }
    public int Groups { get; init; }
    public int Copies { get; init; }
    public long Bytes { get; init; }
    public int FilesChecked { get; init; }
    public string? TrashBatchId { get; init; }
    public List<FamilyDuplicateGroup>? Sample { get; init; }
}

public sealed class FamilyDuplicateGroup
{
    public string Kept { get; init; } = "";
    public List<string> Copies { get; init; } = new();
    public long Size { get; init; }
}

public sealed class FamilyDuplicateReportResponse
{
    public FamilyDuplicateReport? Report { get; init; }
}

/// <summary>GET /list: one folder of the library.</summary>
public sealed class FamilyFilesListing
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Parent { get; init; }
    public List<FamilyFolderEntry> Folders { get; init; } = new();
    public List<FamilyFileEntry> Files { get; init; } = new();
}

public sealed class FamilyFolderEntry
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class FamilyFileEntry
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
    public string? Sha256 { get; init; }
    public bool IsImage { get; init; }
    public string ContentType { get; init; } = "";
}

public sealed class FamilySearchResult
{
    public List<FamilyFileEntry> Files { get; init; } = new();
}

/// <summary>PUT /files/{path}. The server never overwrites - see AlreadyThere and Path.</summary>
public sealed class FamilyUploadResult
{
    // Where it actually landed: "name (2).ext" when different bytes already had the name.
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string? Sha256 { get; init; }
    public bool AlreadyThere { get; init; }
}

/// <summary>POST /folders and POST /move both answer with just the resulting path.</summary>
public sealed class FamilyPathResult
{
    public string Path { get; init; } = "";
}

/// <summary>DELETE /files/{path}: nothing is gone, it went to the trash as one batch.</summary>
public sealed class FamilyDeleteResult
{
    public int Trashed { get; init; }
    public string BatchId { get; init; } = "";
}

public sealed class FamilyTrashListing
{
    public List<FamilyTrashBatch> Batches { get; init; } = new();
}

public sealed class FamilyTrashBatch
{
    public string Id { get; init; } = "";
    public DateTimeOffset DeletedUtc { get; init; }
    public string Reason { get; init; } = ""; // "deleted" | "duplicate"
    public string? By { get; init; }
    public List<FamilyTrashFile> Files { get; init; } = new();

    public long Bytes => Files.Sum(file => file.Size);
}

public sealed class FamilyTrashFile
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
    // For a duplicate: the copy that stayed in the library.
    public string? KeptCopy { get; init; }
}

public sealed class FamilyRestoreResult
{
    public int Restored { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class FamilyEmptyTrashResult
{
    public int Removed { get; init; }
    public string Detail { get; init; } = "";
}

// Request bodies.

public sealed class FamilyFolderRequest
{
    public string Path { get; init; } = "";
}

public sealed class FamilyMoveRequest
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
}

public sealed class FamilyTrashRestoreRequest
{
    public string BatchId { get; init; } = "";
    public string? Path { get; init; }
}
