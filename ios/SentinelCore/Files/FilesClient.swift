import Foundation

// /api/family-files - the household's shared file library. Every call here runs on the client's
// long-timeout session rather than the 10-second one everything else uses: a listing is quick,
// but a phone video going up or a folder of photos coming down takes as long as it takes, and
// the same session keeps things simple. Errors come out the same way as every other request
// (see SentinelClient.validated): 401 is "sign in", anything else carries the server's message.
extension SentinelClient {
    private static let base = "/api/family-files"
    private static let transferTimeout: TimeInterval = 15 * 60

    // MARK: - Status and browsing

    // Always answers on a server that has the feature, even when it is off in Settings; a 404
    // means a server older than the feature. AppModel reads either as "no Files section".
    func familyFilesStatus() async throws -> FamilyFilesStatus {
        try await familyJSON("GET", "/status")
    }

    func familyFilesJob() async throws -> FamilyFilesJob? {
        let response: FamilyFilesJobResponse = try await familyJSON("GET", "/job")
        return response.job
    }

    func familyFolder(path: String) async throws -> FamilyFolderListing {
        try await familyJSON("GET", "/list", query: [URLQueryItem(name: "path", value: path)])
    }

    func familySearch(_ query: String, limit: Int = 100) async throws -> [FamilyFileEntry] {
        let response: FamilySearchResult = try await familyJSON(
            "GET", "/search",
            query: [URLQueryItem(name: "q", value: query), URLQueryItem(name: "limit", value: String(limit))])
        return response.files
    }

    // A JPEG up to 400px, for image files only - the server answers 404 for anything else.
    func familyThumbnail(path: String) async throws -> Data {
        try await familyData("GET", "/thumbnail/\(path)")
    }

    // MARK: - Transfers

    // Streams the file from disk with URLSession's upload task, so a multi-gigabyte video never
    // has to fit in memory. `modified` is the file's own date (a photo's) when the caller knows
    // it, which is what the server's "oldest copy" rule for duplicates goes by.
    func familyUpload(fileURL: URL, to path: String, modified: Date? = nil, contentType: String? = nil) async throws -> FamilyUploadResult {
        var request = try familyRequest("PUT", "/files/\(path)", query: Self.modifiedQuery(modified), timeout: Self.transferTimeout)
        request.setValue(contentType ?? "application/octet-stream", forHTTPHeaderField: "Content-Type")
        log("PUT \(request.url?.absoluteString ?? path) (from file)")
        let (data, response) = try await organizerSession.upload(for: request, fromFile: fileURL)
        return try decode(try validated(data, response), for: request)
    }

    // For bytes that are already in memory - a picture the picker handed over as Data.
    func familyUpload(data: Data, to path: String, modified: Date? = nil, contentType: String? = nil) async throws -> FamilyUploadResult {
        var request = try familyRequest("PUT", "/files/\(path)", query: Self.modifiedQuery(modified), timeout: Self.transferTimeout)
        request.setValue(contentType ?? "application/octet-stream", forHTTPHeaderField: "Content-Type")
        log("PUT \(request.url?.absoluteString ?? path) (\(data.count) bytes)")
        let (body, response) = try await organizerSession.upload(for: request, from: data)
        return try decode(try validated(body, response), for: request)
    }

    // Fetches a file into a temporary folder of its own, keeping its real name so Quick Look and
    // the share sheet know what it is. The caller owns the file; FilesTemporaryFiles.clear()
    // sweeps everything on the next launch.
    func familyDownload(_ file: FamilyFileEntry) async throws -> URL {
        let request = try familyRequest("GET", "/files/\(file.path)", timeout: Self.transferTimeout)
        log("GET \(request.url?.absoluteString ?? file.path) (to file)")
        let (temporary, response) = try await organizerSession.download(for: request)
        // The downloaded file only lives until this call returns, so it moves first and the
        // status is judged afterwards - an error body is small enough to read back.
        let destination = FilesTemporaryFiles.directory().appendingPathComponent(UUID().uuidString, isDirectory: true)
            .appendingPathComponent(file.name)
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.moveItem(at: temporary, to: destination)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let body = (try? Data(contentsOf: destination)) ?? Data()
            try? FileManager.default.removeItem(at: destination.deletingLastPathComponent())
            _ = try validated(body, response)
            throw ClientError.invalidResponse
        }
        return destination
    }

    // MARK: - Changing things

    func familyCreateFolder(path: String) async throws -> String {
        let result: FamilyPathResult = try await familyJSON("POST", "/folders", body: ["path": path])
        return result.path
    }

    // Rename or move a file or a whole folder; `to` is the full new path.
    func familyMove(from: String, to: String) async throws -> String {
        let result: FamilyPathResult = try await familyJSON("POST", "/move", body: ["from": from, "to": to])
        return result.path
    }

    // To the trash, not gone - a file or a whole folder.
    func familyDelete(path: String) async throws -> FamilyDeleteResult {
        try await familyJSON("DELETE", "/files/\(path)")
    }

    func familyTrash() async throws -> [FamilyTrashBatch] {
        let listing: FamilyTrashListing = try await familyJSON("GET", "/trash")
        return listing.batches
    }

    // The whole batch, or one file of it when `path` is given.
    func familyRestore(batchId: String, path: String? = nil) async throws -> FamilyRestoreResult {
        var body = ["batchId": batchId]
        if let path { body["path"] = path }
        return try await familyJSON("POST", "/trash/restore", body: body)
    }

    // MARK: - Admin: drives and jobs

    func familyEmptyTrash() async throws -> FamilyDetailResult {
        try await familyJSON("DELETE", "/trash")
    }

    func familyDrives() async throws -> [FamilyDrive] {
        let response: FamilyDrivesResponse = try await familyJSON("GET", "/drives")
        return response.drives
    }

    func familyCreateDrive(_ input: FamilyDriveInput) async throws -> FamilyDrive {
        try await familyJSON("POST", "/drives", body: input)
    }

    func familyUpdateDrive(id: String, _ input: FamilyDriveInput) async throws -> FamilyDrive {
        try await familyJSON("PUT", "/drives/\(id)", body: input)
    }

    func familyDeleteDrive(id: String) async throws -> FamilyDriveRemovedResult {
        try await familyJSON("DELETE", "/drives/\(id)")
    }

    // Tests the form as typed, before it is saved. A failure is a ClientError.server with the
    // server's explanation; when the host key was the problem the fingerprint it saw is in the
    // error body, which familyTestDriveFailure digs back out.
    func familyTestDrive(_ input: FamilyDriveInput) async throws -> FamilyDriveTestResult {
        let request = try familyRequest("POST", "/drives/test", body: try JSONEncoder().encode(input), timeout: 90)
        log("POST \(request.url?.absoluteString ?? "/drives/test")")
        let (data, response) = try await organizerSession.data(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode == 400,
           let failure = try? JSONDecoder.sentinel.decode(FamilyDriveTestFailure.self, from: data) {
            throw failure
        }
        return try decode(try validated(data, response), for: request)
    }

    // Signs in (saving the sign-in for that computer when a username is given and works) and
    // writes then deletes a test file. Nothing about the library changes.
    func familyTestLibraryLocation(_ input: FamilyLibraryLocationInput) async throws -> FamilyLibraryLocationResult {
        try await familyLibraryLocation("POST", "/library/test", input)
    }

    // The same check, then the library moves there. Files in the old place are not moved.
    func familySetLibraryLocation(_ input: FamilyLibraryLocationInput) async throws -> FamilyLibraryLocationResult {
        try await familyLibraryLocation("PUT", "/library", input)
    }

    private func familyLibraryLocation(_ method: String, _ path: String, _ input: FamilyLibraryLocationInput) async throws -> FamilyLibraryLocationResult {
        // Reaching a computer that is asleep or off takes the network's own timeout.
        let request = try familyRequest(method, path, body: try JSONEncoder().encode(input), timeout: 90)
        log("\(method) \(request.url?.absoluteString ?? path)")
        let (data, response) = try await organizerSession.data(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode == 400,
           let failure = try? JSONDecoder.sentinel.decode(FamilyLibraryLocationFailure.self, from: data) {
            throw failure
        }
        return try decode(try validated(data, response), for: request)
    }

    func familySyncDrive(id: String) async throws -> FamilyDetailResult {
        try await familyJSON("POST", "/drives/\(id)/sync")
    }

    func familyRestoreDrive(id: String) async throws -> FamilyDetailResult {
        try await familyJSON("POST", "/drives/\(id)/restore")
    }

    func familySyncAll() async throws -> FamilyDetailResult {
        try await familyJSON("POST", "/sync")
    }

    func familyCheckDuplicates(remove: Bool) async throws -> FamilyDetailResult {
        try await familyJSON("POST", "/duplicates/check", query: [URLQueryItem(name: "remove", value: remove ? "true" : "false")])
    }

    func familyDuplicateReport() async throws -> FamilyDuplicateReport? {
        let response: FamilyDuplicatesResponse = try await familyJSON("GET", "/duplicates")
        return response.report
    }

    func familyRunNightly() async throws -> FamilyDetailResult {
        try await familyJSON("POST", "/nightly/run")
    }

    // MARK: - Plumbing

    private static func modifiedQuery(_ modified: Date?) -> [URLQueryItem] {
        guard let modified else { return [] }
        return [URLQueryItem(name: "modified", value: modified.formatted(.iso8601))]
    }

    private func familyRequest(_ method: String, _ path: String, query: [URLQueryItem] = [], body: Data? = nil, timeout: TimeInterval) throws -> URLRequest {
        guard let url = url(path: Self.base + path, query: query) else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = timeout
        if let body {
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        return request
    }

    private func familyData(_ method: String, _ path: String, query: [URLQueryItem] = [], body: Data? = nil, timeout: TimeInterval = 60) async throws -> Data {
        let request = try familyRequest(method, path, query: query, body: body, timeout: timeout)
        log("\(method) \(request.url?.absoluteString ?? path)")
        let (data, response) = try await organizerSession.data(for: request)
        return try validated(data, response)
    }

    private func familyJSON<T: Decodable>(_ method: String, _ path: String, query: [URLQueryItem] = []) async throws -> T {
        try decode(try await familyData(method, path, query: query), for: path)
    }

    private func familyJSON<T: Decodable, Body: Encodable>(_ method: String, _ path: String, body: Body) async throws -> T {
        try decode(try await familyData(method, path, body: try JSONEncoder().encode(body)), for: path)
    }

    private func decode<T: Decodable>(_ data: Data, for request: URLRequest) throws -> T {
        try decode(data, for: request.url?.absoluteString ?? "(invalid URL)")
    }

    private func decode<T: Decodable>(_ data: Data, for label: String) throws -> T {
        do {
            return try JSONDecoder.sentinel.decode(T.self, from: data)
        } catch {
            log("Could not decode \(label): \(error)", level: .error)
            throw error
        }
    }
}

// A failed drive test: the reason, and the host key the server saw when that was the reason -
// so the form can offer to paste it in rather than making the person type 43 characters.
struct FamilyDriveTestFailure: Decodable, LocalizedError {
    let error: String?
    let hostKeyFingerprint: String?
    var errorDescription: String? { error ?? "The drive could not be reached." }
}

// A library location that didn't work, and whether a username and password would help.
struct FamilyLibraryLocationFailure: Decodable, LocalizedError {
    let error: String?
    let needsLogin: Bool?
    var errorDescription: String? { error ?? "That folder could not be used." }
}

// Where downloaded files land: a folder of the app's temporary directory, cleared on launch so
// a big video opened last week is not still taking up space.
enum FilesTemporaryFiles {
    static func directory() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("FamilyFiles", isDirectory: true)
    }

    static func clear() {
        try? FileManager.default.removeItem(at: directory())
    }
}
