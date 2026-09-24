import CryptoKit
import Foundation
import Observation

// The Organizer on an Apple device: this device's copy, every edit made to it, and keeping it in
// step with the server. The rules are core/docs/ORGANIZER_SYNC.md; the dashboard's
// organizer-sync.js is the same algorithm in JavaScript, and the two should read alike.
//
// Every edit lands in the local copy at once, stamped with a fresh clock per field, and joins a
// queue of pending changes that `sync()` pushes. Pending changes for one item coalesce, so typing
// into a note sends one change rather than one per keystroke. A queue survives the app being
// killed - it is on disk before `update` returns.
//
// Views read through `items(_:)`, `item(_:)` and `children(of:)`, which touch `revision`, so any
// change - local or synced in - redraws whatever used them.
@MainActor
@Observable
public final class OrganizerEngine {
    public struct SyncState: Equatable, Sendable {
        public var online = false
        public var syncing = false
        public var lastSyncedAt: Date?
        public var lastError: String?
        public var waitingChanges = 0
        public var needsSignIn = false
        public var turnedOff = false
    }

    // The server answering isn't the one this device last synced with - it was rebuilt, or this
    // is a different install. Nothing is sent until the person decides what to do.
    public struct ResetPrompt: Equatable, Identifiable, Sendable {
        public var id: String
        public var localCount: Int
        public var serverCount: Int?
    }

    public struct StorageSummary: Equatable, Sendable {
        public var itemCount: Int
        public var serverItemCount: Int?
        public var attachmentCount: Int
        public var attachmentsWithOriginal: Int
        public var bytesOnDevice: Int64
        public var waitingUploads: Int
    }

    public private(set) var revision = 0
    public private(set) var status: OrganizerStatus?
    public private(set) var spaces: [OrganizerSpace] = []
    public private(set) var syncState = SyncState()
    public var resetPrompt: ResetPrompt?
    public private(set) var policy: OfflinePolicy
    public private(set) var keptOffline: Set<String> = []
    public private(set) var plannedAlerts: [PlannedAlert] = []
    // Holiday calendars: the choice, what there is, and the cached days. See Holidays.swift.
    public private(set) var holidayState = HolidayState()
    @ObservationIgnored public var onAlertsChanged: (@MainActor ([PlannedAlert]) -> Void)?

    @ObservationIgnored private let store: LocalStore
    @ObservationIgnored private var clock: HybridClock
    @ObservationIgnored private let calendar: Calendar
    @ObservationIgnored private var items: [String: OrganizerItem] = [:]
    // Things beyond this device's offline window, fetched to look at while online. Never saved.
    @ObservationIgnored private var archive: [String: OrganizerItem] = [:]
    @ObservationIgnored private var pending: [String: OrganizerChange] = [:]
    @ObservationIgnored private var inflight: [String: OrganizerChange] = [:]
    @ObservationIgnored private var serverId: String?
    @ObservationIgnored private var cursor: Int64 = 0
    @ObservationIgnored private var transport: (any OrganizerTransport)?
    // Set while syncOnce holds a transport it borrowed; a real connect() in the meantime keeps it.
    @ObservationIgnored private var borrowedTransport = false
    @ObservationIgnored private var syncTask: Task<Void, Never>?
    @ObservationIgnored private var requestedPass = 0
    @ObservationIgnored private var completedPass = 0
    @ObservationIgnored private var debounceTask: Task<Void, Never>?
    @ObservationIgnored private var waitTask: Task<Void, Never>?
    @ObservationIgnored private var blobTask: Task<Void, Never>?
    @ObservationIgnored private var blobWorkAgain = false
    @ObservationIgnored private var missingBlobs: Set<String> = []
    @ObservationIgnored private var childIndex: (revision: Int, map: [String: [OrganizerItem]])?
    @ObservationIgnored private var holidaysBusy = false
    @ObservationIgnored private var holidaysAgain = false
    @ObservationIgnored private var holidaysCheckedAt: Date?
    // Bumped on every change of choice, so a send only counts as done for the choice it carried.
    @ObservationIgnored private var holidayVersion = 0

    // `directory` nil keeps everything in memory (tests). `deviceId` has to be stable for this
    // install: it is the tiebreak in every clock this device writes.
    public init(directory: URL?, deviceId: String, policy: OfflinePolicy, calendar: Calendar = .current) throws {
        store = try LocalStore(directory: directory)
        clock = HybridClock(deviceId: deviceId)
        self.policy = policy
        self.calendar = calendar

        // "Off" keeps nothing between launches - only what has edits that never made it up.
        if policy.mode == .off { store.clearItems(keeping: Set(store.loadPending().keys)) }

        for item in store.loadItems() {
            items[item.id] = item
            item.clocks.values.forEach { clock.observe($0) }
        }
        pending = store.loadPending()
        for change in pending.values { change.clocks.values.forEach { clock.observe($0) } }
        serverId = store.meta("serverId")
        cursor = Int64(store.meta("cursor") ?? "") ?? 0
        // Syncs by an older build skipped kinds it didn't know (see KnownItems), and the cursor
        // has moved past them. When this build knows a different set, start over once so they
        // arrive. A full sync is idempotent, so the only cost is one larger download.
        let kinds = OrganizerKind.allCases.map(\.rawValue).joined(separator: ",")
        if store.meta("kinds") != kinds {
            cursor = 0
            store.setMeta("cursor", "0")
            store.setMeta("kinds", kinds)
        }
        if let data = store.meta("spaces")?.data(using: .utf8), let saved = try? JSONDecoder.organizer.decode([OrganizerSpace].self, from: data) {
            spaces = saved
        }
        if let data = store.meta("keptOffline")?.data(using: .utf8), let saved = try? JSONDecoder().decode([String].self, from: data) {
            keptOffline = Set(saved)
        }
        if let data = store.meta("holidays")?.data(using: .utf8), let saved = try? JSONDecoder().decode(HolidayState.self, from: data) {
            holidayState = saved
        }
        syncState.waitingChanges = pending.count
        replanAlerts()
    }

    // MARK: - reading

    public func items(_ kind: OrganizerKind) -> [OrganizerItem] {
        _ = revision
        var out = items.values.filter { $0.kind == kind && !$0.deleted }
        for item in archive.values where item.kind == kind && !item.deleted && items[item.id] == nil { out.append(item) }
        return out
    }

    public func item(_ id: String) -> OrganizerItem? {
        _ = revision
        guard let item = items[id] ?? archive[id], !item.deleted else { return nil }
        return item
    }

    public func children(of id: String, kind: OrganizerKind? = nil) -> [OrganizerItem] {
        _ = revision
        if childIndex?.revision != revision {
            var map: [String: [OrganizerItem]] = [:]
            for item in items.values where !item.deleted {
                if let parent = item.parentId { map[parent, default: []].append(item) }
            }
            for item in archive.values where !item.deleted && items[item.id] == nil {
                if let parent = item.parentId { map[parent, default: []].append(item) }
            }
            childIndex = (revision, map)
        }
        let all = childIndex?.map[id] ?? []
        return kind.map { kind in all.filter { $0.kind == kind } } ?? all
    }

    public var visibleSpaces: [OrganizerSpace] { spaces.isEmpty ? [.family] : spaces }

    public func space(_ id: String) -> OrganizerSpace? { visibleSpaces.first { $0.id == id } }

    // Where something new goes when nobody says otherwise: this person's own space if there is
    // one, otherwise Family.
    public var defaultSpaceId: String {
        visibleSpaces.first { $0.kind == .personal }?.id ?? OrganizerSpace.family.id
    }

    // Whether the server has told this device which spaces there are. Until then defaultSpaceId
    // falls back to Family, so anything added without a person choosing - by Siri, or brought in
    // from Apple Reminders - waits for this rather than risk landing where everyone sees it.
    public var knowsSpaces: Bool { !spaces.isEmpty }

    // Whether this device holds an item with this id, deleted or not. For imports that choose
    // their own ids: a second device bringing in the same thing must neither duplicate it nor
    // bring back one somebody deleted.
    public func knows(_ id: String) -> Bool { items[id] != nil || archive[id] != nil }

    // MARK: - editing

    // `id` is for imports whose ids must come out the same on every device (see
    // AppleReminderImport); everything else gets a fresh one.
    @discardableResult
    public func create(_ kind: OrganizerKind, fields: [String: JSONValue], spaceId: String? = nil, parentId: String? = nil, id: String? = nil) -> OrganizerItem {
        let parent = parentId.flatMap { items[$0] ?? archive[$0] }
        let now = Date()
        var item = OrganizerItem(
            id: id ?? UUID().uuidString.lowercased(), kind: kind,
            spaceId: parent?.spaceId ?? spaceId ?? defaultSpaceId,
            parentId: parent?.id,
            createdAt: now, createdBy: status?.username, updatedAt: now, updatedBy: status?.username)
        var structural: [String: JSONValue] = parent.map { ["parentId": .string($0.id)] } ?? ["spaceId": .string(item.spaceId)]
        structural.merge(fields) { $1 }
        edit(&item, structural)
        return item
    }

    // `.null` clears a field.
    public func update(_ id: String, _ changes: [String: JSONValue]) {
        guard var item = items[id] ?? archive[id] else { return }
        archive[id] = nil
        let movedSpace = changes["spaceId"]?.string.map { $0 != item.spaceId } ?? false
        edit(&item, changes)
        if movedSpace { cascadeSpace(from: item) }
    }

    public func delete(_ id: String) {
        update(id, ["deleted": true])
    }

    // An item and everything under it - a board's columns and cards, a note's attachments. The
    // server keeps children of a deleted parent as they are, so every client deletes the tree.
    public func deleteTree(_ id: String) {
        var queue = [id]
        var seen: Set<String> = []
        while let next = queue.popLast(), seen.insert(next).inserted {
            queue.append(contentsOf: children(of: next).map(\.id))
            delete(next)
        }
    }

    private func edit(_ item: inout OrganizerItem, _ changes: [String: JSONValue]) {
        var change = pending[item.id] ?? OrganizerChange(id: item.id, kind: item.kind)
        for (name, value) in changes {
            // The note body's starting point, sent so the server can tell two people rewrote the
            // same note rather than letting the later one silently win.
            if name == "body", item.kind == .note, let current = item.clocks["body"],
               change.baseClocks?["body"] == nil, inflight[item.id]?.baseClocks?["body"] == nil {
                change.baseClocks = ["body": current]
            }
            let stamp = clock.next()
            Self.applyField(&item, name: name, value: value, clock: stamp)
            change.fields[name] = value
            change.clocks[name] = stamp
        }
        item.updatedAt = Date()
        item.updatedBy = status?.username ?? item.updatedBy
        items[item.id] = item
        pending[item.id] = change
        store.save([item])
        persistPending(item.id)
        syncState.waitingChanges = pending.count + inflight.count
        changed()
        scheduleSync(after: 0.8)
    }

    private static func applyField(_ item: inout OrganizerItem, name: String, value: JSONValue, clock: String) {
        switch name {
        case "spaceId": if let space = value.string { item.spaceId = space }
        case "parentId": item.parentId = value.string
        case "deleted": item.deleted = value.bool ?? false
        default:
            if value.isNull { item.fields[name] = nil } else { item.fields[name] = value }
        }
        item.clocks[name] = clock
    }

    private func cascadeSpace(from root: OrganizerItem) {
        var queue = [root.id]
        var touched: [OrganizerItem] = []
        while let parentId = queue.popLast() {
            for var child in items.values where child.parentId == parentId {
                queue.append(child.id)
                guard child.spaceId != root.spaceId else { continue }
                child.spaceId = root.spaceId
                items[child.id] = child
                touched.append(child)
            }
        }
        store.save(touched)
        changed()
    }

    private func persistPending(_ id: String) {
        let merged: OrganizerChange? = switch (inflight[id], pending[id]) {
        case let (older?, newer?): older.merging(newer)
        case let (older?, nil): older
        case let (nil, newer?): newer
        case (nil, nil): nil
        }
        store.savePending(merged, id: id)
    }

    private func changed() {
        revision &+= 1
    }

    // MARK: - attachments

    // Keeps the file on this device straight away and creates the attachment item; the bytes go
    // up whenever the server is reachable. Works with no signal at all.
    @discardableResult
    public func attach(_ data: Data, name: String, contentType: String, to parentId: String, width: Int? = nil, height: Int? = nil) throws -> OrganizerItem {
        if let limit = status?.maxAttachmentBytes, Int64(data.count) > limit {
            throw OrganizerError.server(status: 413, message: "\(name) is larger than the \(limit / 1_048_576) MB attachment limit.")
        }
        let hash = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        try data.write(to: store.originalURL(hash), options: .atomic)
        store.saveBlob(.init(hash: hash, needsUpload: true, hasOriginal: true, size: Int64(data.count), lastUsed: Date()))
        let siblings = children(of: parentId, kind: .attachment).count
        var fields: [String: JSONValue] = [
            "hash": .string(hash), "name": .string(name), "contentType": .string(contentType),
            "size": .number(Double(data.count)), "position": .number(Double(siblings)),
        ]
        if let width { fields["width"] = .number(Double(width)) }
        if let height { fields["height"] = .number(Double(height)) }
        let item = create(.attachment, fields: fields, parentId: parentId)
        startBlobWork()
        return item
    }

    public func hasOriginal(_ hash: String) -> Bool {
        FileManager.default.fileExists(atPath: store.originalURL(hash).path)
    }

    // Image bytes to show. A thumbnail request is happy with the original if that's what's here.
    public func imageData(hash: String, thumbnail: Bool) async -> Data? {
        let original = store.originalURL(hash)
        if let data = try? Data(contentsOf: original) {
            touch(hash)
            return data
        }
        let thumbURL = store.thumbnailURL(hash)
        if thumbnail, let data = try? Data(contentsOf: thumbURL) { return data }
        do {
            let data = try await download(hash: hash, thumbnail: thumbnail)
            return data
        } catch {
            return thumbnail ? nil : try? Data(contentsOf: thumbURL)
        }
    }

    // A file URL for the original, named as it was attached, for opening or sharing.
    public func fileForOpening(hash: String, name: String) async throws -> URL {
        if !hasOriginal(hash) { _ = try await download(hash: hash, thumbnail: false) }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("organizer-open-\(hash.prefix(12))", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let safeName = name.replacingOccurrences(of: "/", with: "-").isEmpty ? hash : name.replacingOccurrences(of: "/", with: "-")
        let target = folder.appendingPathComponent(safeName)
        try? FileManager.default.removeItem(at: target)
        try FileManager.default.copyItem(at: store.originalURL(hash), to: target)
        return target
    }

    private func download(hash: String, thumbnail: Bool) async throws -> Data {
        let response = try await send("GET", "/api/organizer/blobs/\(hash)" + (thumbnail ? "/thumbnail" : ""), timeout: 120)
        let data = response.data
        if thumbnail {
            try data.write(to: store.thumbnailURL(hash), options: .atomic)
            var record = store.blob(hash) ?? .init(hash: hash, needsUpload: false, hasOriginal: false, size: 0, lastUsed: Date())
            record.lastUsed = Date()
            store.saveBlob(record)
        } else {
            try data.write(to: store.originalURL(hash), options: .atomic)
            var record = store.blob(hash) ?? .init(hash: hash, needsUpload: false, hasOriginal: true, size: 0, lastUsed: Date())
            record.hasOriginal = true
            record.size = Int64(data.count)
            record.lastUsed = Date()
            store.saveBlob(record)
            enforceStorageCap()
        }
        return data
    }

    private func touch(_ hash: String) {
        guard var record = store.blob(hash) else { return }
        record.lastUsed = Date()
        store.saveBlob(record)
    }

    // One blob pass at a time. A request while one runs makes it go round again: the running pass
    // took its list of attachments before the new one arrived, so dropping the request left an
    // attachment unfetched until some later sync happened to start another pass.
    private func startBlobWork() {
        guard transport != nil else { return }
        guard blobTask == nil else {
            blobWorkAgain = true
            return
        }
        blobTask = Task { [weak self] in
            repeat {
                self?.blobWorkAgain = false
                await self?.runBlobWork()
            } while self?.blobWorkAgain == true && !Task.isCancelled
            self?.blobTask = nil
        }
    }

    // Uploads first (files attached here, and anything the server says it lost that this device
    // still has), then downloads by policy, then tidying.
    private func runBlobWork() async {
        let referenced = Set(items.values.filter { $0.kind == .attachment && !$0.deleted }.compactMap { $0.string("hash") })

        for record in store.allBlobs() where record.needsUpload || (missingBlobs.contains(record.hash) && record.hasOriginal) {
            guard let data = try? Data(contentsOf: store.originalURL(record.hash)) else { continue }
            do {
                let response = try await transport?.send(
                    method: "PUT", path: "/api/organizer/blobs/\(record.hash)", query: [], body: data,
                    contentType: "application/octet-stream", timeout: 300)
                guard let response else { return }
                if (200..<300).contains(response.status) {
                    var updated = record
                    updated.needsUpload = false
                    store.saveBlob(updated)
                    missingBlobs.remove(record.hash)
                } else if response.status == 400 || response.status == 413 {
                    var updated = record
                    updated.needsUpload = false
                    store.saveBlob(updated)
                    syncState.lastError = Self.message(from: response.data) ?? "An attachment was too large to upload."
                } else {
                    return
                }
            } catch {
                return
            }
        }

        if policy.attachments != .none {
            let attachments = items.values.filter { $0.kind == .attachment && !$0.deleted }
            for attachment in attachments {
                guard let hash = attachment.string("hash"), !Task.isCancelled else { continue }
                let isImage = attachment.string("contentType")?.hasPrefix("image/") == true
                do {
                    if policy.attachments == .full, !hasOriginal(hash) {
                        _ = try await download(hash: hash, thumbnail: false)
                    } else if policy.attachments == .thumbnails, isImage, !hasOriginal(hash),
                              !FileManager.default.fileExists(atPath: store.thumbnailURL(hash).path) {
                        _ = try await download(hash: hash, thumbnail: true)
                    }
                } catch OrganizerError.offline {
                    break
                } catch {
                    continue
                }
            }
        }

        // Files nothing here points at any more, unless they still have to go up.
        for record in store.allBlobs() where !referenced.contains(record.hash) && !record.needsUpload {
            store.deleteBlob(record.hash)
        }
        enforceStorageCap()
        changed()
    }

    // Least recently looked-at originals go first. Never a file that hasn't been uploaded yet,
    // and never thumbnails, which are small and are what keeps a light copy looking complete.
    private func enforceStorageCap() {
        guard let cap = policy.storageCapBytes else { return }
        var records = store.allBlobs().filter(\.hasOriginal)
        var total = records.reduce(Int64(0)) { $0 + $1.size }
        guard total > cap else { return }
        records.sort { $0.lastUsed < $1.lastUsed }
        for record in records where total > cap && !record.needsUpload {
            try? FileManager.default.removeItem(at: store.originalURL(record.hash))
            var updated = record
            updated.hasOriginal = false
            store.saveBlob(updated)
            total -= record.size
        }
    }

    // MARK: - connection

    public func connect(_ transport: any OrganizerTransport) {
        self.transport = transport
        borrowedTransport = false
        Task {
            await refreshStatus()
            await sync()
            startWaiting()
        }
    }

    public func disconnect() {
        waitTask?.cancel()
        waitTask = nil
        debounceTask?.cancel()
        debounceTask = nil
        blobTask?.cancel()
        blobTask = nil
        transport = nil
    }

    // A real connection - not syncOnce borrowing a transport for one pass. The app connects
    // whenever this is false; counting a borrowed one let a background sync that happened to be
    // running when the app came forward stop the app connecting, and then hang up on finishing,
    // leaving the app in front with nothing syncing.
    public var isConnected: Bool { transport != nil && !borrowedTransport }

    // One sync and nothing after it - no long poll - for an app woken in the background (a
    // silent push, a background refresh). Already connected, it just syncs. Returns whether the
    // server answered.
    @discardableResult
    public func syncOnce(_ transport: any OrganizerTransport) async -> Bool {
        if self.transport != nil {
            await sync()
            return syncState.online
        }
        self.transport = transport
        borrowedTransport = true
        await refreshStatus()
        await sync()
        let online = syncState.online
        // Unless the app came forward and connected for real meanwhile.
        if borrowedTransport { disconnect() }
        return online
    }

    public func refreshStatus() async {
        do {
            let response = try await send("GET", "/api/organizer/status")
            let fresh = try JSONDecoder.organizer.decode(OrganizerStatus.self, from: response.data)
            adoptOwner(fresh.username ?? "")
            status = fresh
            syncState.turnedOff = !fresh.enabled
            syncState.needsSignIn = false
        } catch OrganizerError.signInRequired {
            syncState.needsSignIn = true
        } catch {
            // Keep the last status; offline is reported by sync.
        }
    }

    // Signing in as someone else on this device must not show them the last person's copy.
    private func adoptOwner(_ owner: String) {
        let stored = store.meta("owner")
        if let stored, stored != owner {
            // Someone else's holiday choice isn't theirs either; the list of sets is.
            var fresh = HolidayState()
            fresh.catalog = holidayState.catalog
            holidayState = fresh
            resetLocalCopy(keepPending: false)
        }
        store.setMeta("owner", owner)
    }

    private func resetLocalCopy(keepPending: Bool) {
        items = [:]
        archive = [:]
        inflight = [:]
        if !keepPending { pending = [:] }
        serverId = nil
        cursor = 0
        spaces = []
        store.wipe(keepPending: keepPending)
        // The wipe takes the holiday choice with the rest of the metadata. It's kept: a rebuilt
        // server gets it back from here.
        saveHolidays()
        syncState.waitingChanges = pending.count
        replanAlerts()
        changed()
    }

    private func scheduleSync(after delay: TimeInterval) {
        debounceTask?.cancel()
        guard transport != nil else { return }
        debounceTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            await self?.sync()
        }
    }

    private func startWaiting() {
        waitTask?.cancel()
        waitTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                if self.resetPrompt != nil || self.syncState.turnedOff || self.syncState.needsSignIn {
                    try? await Task.sleep(for: .seconds(30))
                    if self.syncState.turnedOff || self.syncState.needsSignIn { await self.refreshStatus() }
                    continue
                }
                do {
                    let response = try await self.send("GET", "/api/organizer/wait",
                                                       query: [URLQueryItem(name: "cursor", value: String(self.cursor)), URLQueryItem(name: "timeout", value: "25")],
                                                       timeout: 40)
                    let answer = try JSONDecoder.organizer.decode(WaitResponse.self, from: response.data)
                    if answer.cursor != self.cursor { await self.sync() }
                } catch is CancellationError {
                    return
                } catch {
                    self.syncState.online = false
                    try? await Task.sleep(for: .seconds(15))
                    await self.sync()
                }
            }
        }
    }

    // MARK: - sync

    // Returns once a sync pass that started after this call has finished - so an edit made just
    // before calling it has been sent. Passes never overlap: a call that arrives while one is
    // running waits for it and then, if its edit might have missed that pass, for the next.
    //
    // This used to be a "sync again" flag checked at the bottom of the running pass. A call that
    // landed after that check but before the pass returned set the flag, awaited the finished
    // pass, and returned without its edit having gone anywhere - found by running the two-device
    // tests against the real server repeatedly.
    public func sync() async {
        requestedPass += 1
        let wanted = requestedPass
        while completedPass < wanted {
            if let running = syncTask {
                await running.value
                continue
            }
            let task = Task { await self.runSyncLoop() }
            syncTask = task
            await task.value
        }
    }

    private func runSyncLoop() async {
        defer { syncTask = nil }
        guard transport != nil else {
            completedPass = requestedPass
            return
        }
        syncState.syncing = true
        defer {
            syncState.syncing = false
            syncState.waitingChanges = pending.count + inflight.count
        }
        // A pass settles the calls made before it started - and only those, even when it fails.
        // Settling everything on failure let a sync asked for the moment the network came back be
        // answered by a pass that had started, and failed, while it was still down.
        while completedPass < requestedPass {
            let pass = requestedPass
            defer { completedPass = max(completedPass, pass) }
            do {
                try await syncOnce(restoreSpaces: nil)
                syncState.online = true
                syncState.needsSignIn = false
                syncState.turnedOff = false
                syncState.lastSyncedAt = Date()
            } catch OrganizerError.signInRequired {
                syncState.needsSignIn = true
                return
            } catch OrganizerError.turnedOff {
                syncState.turnedOff = true
                return
            } catch is CancellationError {
                completedPass = requestedPass
                return
            } catch {
                syncState.online = false
                if let organizerError = error as? OrganizerError, organizerError != .offline {
                    syncState.lastError = organizerError.localizedDescription
                }
                scheduleSync(after: 20)
                return
            }
        }
        startBlobWork()
        Task { await refreshHolidays() }
    }

    private func syncOnce(restoreSpaces: [OrganizerSpace]?) async throws {
        guard resetPrompt == nil else { return }
        for (id, change) in pending { inflight[id] = inflight[id].map { $0.merging(change) } ?? change }
        pending = [:]

        var first = true
        var hasMore = true
        while hasMore {
            let request = SyncRequest(
                serverId: serverId, cursor: cursor, knownSpaces: spaces.map(\.id),
                changes: first ? Array(inflight.values) : [], restoreSpaces: first ? restoreSpaces : nil, limit: nil)
            let reply: SyncResponse
            do {
                let body = try JSONEncoder.organizer.encode(request)
                let response = try await send("POST", "/api/organizer/sync", body: body, timeout: 60)
                reply = try JSONDecoder.organizer.decode(SyncResponse.self, from: response.data)
            } catch {
                restoreInflight()
                throw error
            }

            if reply.reset {
                restoreInflight()
                await handleReset(reply)
                return
            }
            apply(reply, settlesInflight: first)
            first = false
            hasMore = reply.hasMore
        }
    }

    private func restoreInflight() {
        for (id, change) in inflight {
            pending[id] = pending[id].map { change.merging($0) } ?? change
        }
        inflight = [:]
    }

    private func apply(_ reply: SyncResponse, settlesInflight: Bool) {
        var saved: [OrganizerItem] = []
        var dropped: [String] = []

        for id in reply.removed {
            items[id] = nil
            dropped.append(id)
        }

        let echoed = Set(reply.items.map(\.id))
        for var item in reply.items {
            item.clocks.values.forEach { clock.observe($0) }
            // Edits made while this request was out are newer than anything in it - lay them back on.
            if let newer = pending[item.id] {
                for (name, value) in newer.fields {
                    Self.applyField(&item, name: name, value: value, clock: newer.clocks[name] ?? "")
                }
            }
            items[item.id] = item
            archive[item.id] = nil
            saved.append(item)
        }

        if settlesInflight {
            for rejection in reply.rejected {
                syncState.lastError = rejection.error
                // Refused and not on the server at all - a new item that can never be created.
                if !echoed.contains(rejection.id), pending[rejection.id] == nil, items[rejection.id]?.seq == nil {
                    items[rejection.id] = nil
                    dropped.append(rejection.id)
                }
            }
            let settled = Array(inflight.keys)
            inflight = [:]
            settled.forEach(persistPending)
        }

        spaces = reply.spaces
        let visible = Set(spaces.map(\.id))
        for (id, item) in items where !visible.contains(item.spaceId) && pending[id] == nil {
            items[id] = nil
            dropped.append(id)
        }

        let prune = policy.itemsToPrune(items, protected: Set(pending.keys).union(keptOffline), calendar: calendar)
        for id in prune {
            items[id] = nil
            dropped.append(id)
        }

        cursor = reply.cursor
        serverId = reply.serverId
        missingBlobs = Set(reply.missingBlobs)
        store.save(saved.filter { items[$0.id] != nil })
        store.deleteItems(dropped)
        store.setMeta("cursor", String(cursor))
        store.setMeta("serverId", serverId)
        store.setMeta("spaces", (try? JSONEncoder.organizer.encode(spaces)).flatMap { String(data: $0, encoding: .utf8) })
        replanAlerts()
        changed()
    }

    private func handleReset(_ reply: SyncResponse) async {
        let live = items.values.filter { !$0.deleted }.count
        if serverId == nil || live == 0 {
            // Nothing here worth offering - just take the server's copy.
            resetLocalCopy(keepPending: serverId == nil)
            serverId = reply.serverId
            store.setMeta("serverId", serverId)
            requestedPass += 1
            return
        }
        await refreshStatus()
        resetPrompt = ResetPrompt(id: reply.serverId, localCount: live, serverCount: status?.itemCount)
    }

    // The person's answer to a ResetPrompt. Uploading sends every item with the clocks it already
    // has, so the server merges it exactly like the original edits - anything newer already on
    // the new server still wins - and brings the groups back with it.
    public func resolveReset(upload: Bool) async {
        guard let prompt = resetPrompt else { return }
        resetPrompt = nil
        if upload {
            var restore: [String: OrganizerChange] = [:]
            for item in items.values {
                var fields = item.fields
                fields["spaceId"] = .string(item.spaceId)
                fields["deleted"] = .bool(item.deleted)
                if let parentId = item.parentId { fields["parentId"] = .string(parentId) }
                var clocks: [String: String] = [:]
                let fallback = clock.next()
                for name in fields.keys { clocks[name] = item.clocks[name] ?? fallback }
                restore[item.id] = OrganizerChange(id: item.id, kind: item.kind, fields: fields, clocks: clocks)
            }
            for (id, change) in pending { restore[id] = restore[id].map { $0.merging(change) } ?? change }
            pending = [:]
            inflight = restore
            restore.keys.forEach(persistPending)
            serverId = prompt.id
            cursor = 0
            store.setMeta("serverId", serverId)
            store.setMeta("cursor", "0")
            let groups = spaces.filter { $0.kind == .group }
            syncState.syncing = true
            do {
                try await syncOnce(restoreSpaces: groups)
                syncState.online = true
                syncState.lastSyncedAt = Date()
                missingBlobs.formUnion(store.allBlobs().filter(\.hasOriginal).map(\.hash))
            } catch {
                syncState.online = false
            }
            syncState.syncing = false
            syncState.waitingChanges = pending.count + inflight.count
            startBlobWork()
        } else {
            resetLocalCopy(keepPending: false)
            serverId = prompt.id
            store.setMeta("serverId", serverId)
            await sync()
        }
    }

    // MARK: - policy

    public func setPolicy(_ newPolicy: OfflinePolicy) {
        let widened = newPolicy.widens(policy)
        policy = newPolicy
        if widened {
            // Items thrown away under the old policy are behind the cursor now; read everything
            // again once.
            cursor = 0
            store.setMeta("cursor", "0")
        } else {
            let prune = policy.itemsToPrune(items, protected: Set(pending.keys).union(keptOffline), calendar: calendar)
            for id in prune { items[id] = nil }
            store.deleteItems(Array(prune))
            changed()
        }
        Task {
            await sync()
            enforceStorageCap()
        }
    }

    public func setKeptOffline(_ id: String, _ keep: Bool) {
        if keep { keptOffline.insert(id) } else { keptOffline.remove(id) }
        store.setMeta("keptOffline", (try? JSONEncoder().encode(Array(keptOffline))).flatMap { String(data: $0, encoding: .utf8) })
    }

    public func storageSummary() -> StorageSummary {
        let attachments = items.values.filter { $0.kind == .attachment && !$0.deleted }
        let blobs = store.allBlobs()
        var bytes: Int64 = 0
        for record in blobs where record.hasOriginal { bytes += record.size }
        if let thumbnails = try? FileManager.default.contentsOfDirectory(at: store.thumbnailDirectory, includingPropertiesForKeys: [.fileSizeKey]) {
            for file in thumbnails { bytes += Int64((try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
        }
        return StorageSummary(
            itemCount: items.values.filter { !$0.deleted }.count,
            serverItemCount: status?.itemCount,
            attachmentCount: attachments.count,
            attachmentsWithOriginal: attachments.filter { $0.string("hash").map(hasOriginal) ?? false }.count,
            bytesOnDevice: bytes,
            waitingUploads: blobs.filter(\.needsUpload).count)
    }

    // MARK: - alerts

    private func replanAlerts() {
        let planned = AlertPlanner.plan(items: Array(items.values), calendar: calendar)
        guard planned != plannedAlerts else { return }
        plannedAlerts = planned
        onAlertsChanged?(planned)
    }

    public func refreshAlerts() {
        plannedAlerts = AlertPlanner.plan(items: Array(items.values), calendar: calendar)
        onAlertsChanged?(plannedAlerts)
    }

    // MARK: - online-only actions

    // Everything of one kind, including what this device's offline window leaves out - for
    // browsing back further than a light copy keeps. Held in memory only.
    public func loadArchive(_ kind: OrganizerKind) async throws {
        let response = try await send("GET", "/api/organizer/items", query: [URLQueryItem(name: "kind", value: kind.rawValue)])
        let fetched = try JSONDecoder.organizer.decode(ItemsResponse.self, from: response.data).items
        for item in fetched where items[item.id] == nil { archive[item.id] = item }
        changed()
    }

    public func people() async throws -> [OrganizerPerson] {
        let response = try await send("GET", "/api/organizer/people")
        return try JSONDecoder.organizer.decode(PeopleResponse.self, from: response.data).people
    }

    public func saveGroup(id: String?, _ input: OrganizerGroupInput) async throws {
        let body = try JSONEncoder.organizer.encode(input)
        if let id {
            _ = try await send("PUT", "/api/organizer/spaces/\(id.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? id)", body: body)
        } else {
            _ = try await send("POST", "/api/organizer/spaces", body: body)
        }
        await sync()
    }

    public func deleteGroup(id: String) async throws {
        _ = try await send("DELETE", "/api/organizer/spaces/\(id.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? id)")
        await sync()
    }

    public func calendarFeedPath(reset: Bool = false) async throws -> String {
        let response = reset
            ? try await send("POST", "/api/organizer/calendar-feed/reset")
            : try await send("GET", "/api/organizer/calendar-feed")
        return try JSONDecoder.organizer.decode(FeedResponse.self, from: response.data).path
    }

    // MARK: - holiday calendars

    public var holidaySets: [String] { holidayState.sets }

    // Every holiday overlapping a range, whatever space is being looked at - they belong to none.
    public func holidays(from: Date, to: Date) -> [(holiday: Holiday, start: Date, end: Date)] {
        holidayState.list.compactMap { holiday in
            guard let span = holiday.span(in: calendar), span.start < to, span.end > from else { return nil }
            return (holiday, span.start, span.end)
        }
    }

    public func holidaySetName(_ id: String) -> String {
        holidayState.catalog?.first { $0.id == id }?.name ?? "Holidays"
    }

    public func setHolidaySets(_ sets: [String]) {
        var seen = Set<String>()
        holidayState.sets = sets.filter { seen.insert($0).inserted }
        holidayState.dirty = true
        holidayVersion += 1
        saveHolidays()
        Task { await refreshHolidays(force: true) }
    }

    // After a sync, and at most every five minutes unless forced: send an unsent choice or take
    // the server's, then fetch the days again if the choice or the years have changed.
    public func refreshHolidays(force: Bool = false) async {
        guard transport != nil else { return }
        if holidaysBusy {
            if force { holidaysAgain = true }
            return
        }
        if !force, !holidayState.dirty, let checked = holidaysCheckedAt, Date().timeIntervalSince(checked) < 300 { return }
        holidaysBusy = true
        defer {
            holidaysBusy = false
            if holidaysAgain {
                holidaysAgain = false
                Task { await refreshHolidays(force: true) }
            }
        }
        do {
            if holidayState.catalog == nil {
                let response = try await send("GET", "/api/organizer/holidays/sets")
                holidayState.catalog = try JSONDecoder().decode(HolidaySetsResponse.self, from: response.data).sets
            }
            if holidayState.dirty {
                try await sendHolidaySets()
            } else {
                let version = holidayVersion
                let response = try await send("GET", "/api/organizer/preferences")
                // A choice made while this was out is newer than the answer - it goes up next time.
                if version == holidayVersion {
                    let chosen = try JSONDecoder().decode(HolidayPreferences.self, from: response.data).holidaySets
                    if let chosen {
                        holidayState.sets = chosen
                    } else if !holidayState.sets.isEmpty {
                        // Never chosen on this server - a rebuilt one - so this device's goes back.
                        try await sendHolidaySets()
                    }
                }
            }
            let year = calendar.component(.year, from: Date())
            let key = "\(holidayState.sets.sorted().joined(separator: ","))|\(year - 1)-\(year + 2)"
            if holidayState.sets.isEmpty {
                holidayState.list = []
                holidayState.key = key
            } else if holidayState.key != key {
                let response = try await send("GET", "/api/organizer/holidays", query: [
                    URLQueryItem(name: "sets", value: holidayState.sets.joined(separator: ",")),
                    URLQueryItem(name: "from", value: String(year - 1)),
                    URLQueryItem(name: "to", value: String(year + 2)),
                ])
                holidayState.list = try JSONDecoder().decode(HolidaysResponse.self, from: response.data).holidays
                holidayState.key = key
            }
            holidaysCheckedAt = Date()
            saveHolidays()
        } catch {
            // Unreachable, or an older server: the cached copy stays and the next sync tries again.
        }
    }

    private func sendHolidaySets() async throws {
        let version = holidayVersion
        let body = try JSONEncoder().encode(HolidayPreferences(holidaySets: holidayState.sets))
        do {
            _ = try await send("PUT", "/api/organizer/preferences", body: body)
        } catch OrganizerError.server(status: 400, _) {
            // A set this server doesn't know; sending it again won't help.
        }
        if version == holidayVersion { holidayState.dirty = false }
    }

    private func saveHolidays() {
        store.setMeta("holidays", (try? JSONEncoder().encode(holidayState)).flatMap { String(data: $0, encoding: .utf8) })
    }

    // MARK: - transport

    private func send(_ method: String, _ path: String, query: [URLQueryItem] = [], body: Data? = nil, timeout: TimeInterval? = nil) async throws -> OrganizerHTTPResponse {
        guard let transport else { throw OrganizerError.offline }
        let response = try await transport.send(
            method: method, path: path, query: query, body: body,
            contentType: body == nil ? nil : "application/json", timeout: timeout)
        switch response.status {
        case 200..<300: return response
        case 401: throw OrganizerError.signInRequired
        case 404 where Self.message(from: response.data)?.contains("turned off") == true: throw OrganizerError.turnedOff
        default: throw OrganizerError.server(status: response.status, message: Self.message(from: response.data))
        }
    }

    static func message(from data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let error = object["error"] as? String { return error }
        if let errors = object["errors"] as? [String] { return errors.joined(separator: "\n") }
        return nil
    }
}
