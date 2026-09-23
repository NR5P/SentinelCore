import Foundation

// How much of the Organizer this device keeps for use with no connection - chosen per device in
// the app's settings. Phones default light, computers default to everything: the computers are
// what a dead server gets rebuilt from, and a phone shouldn't spend its storage on five-year-old
// notes it can still fetch whenever it has signal.
public struct OfflinePolicy: Codable, Hashable, Sendable {
    public enum Mode: String, Codable, CaseIterable, Sendable {
        // Nothing kept between launches. Edits made offline are still kept until they're sent.
        case off
        // Recent and upcoming things, and anything pinned.
        case light
        // Everything this person can see.
        case full
    }

    public enum Attachments: String, Codable, CaseIterable, Sendable {
        case none, thumbnails, full
    }

    public var mode: Mode
    // Light mode only: how far back to keep. nil keeps everything (which makes light the same as
    // full for items, but still lets attachments stay thumbnails).
    public var windowDays: Int?
    public var attachments: Attachments
    // Attachment files only - items are tiny. nil is no limit.
    public var storageCapBytes: Int64?

    public init(mode: Mode, windowDays: Int?, attachments: Attachments, storageCapBytes: Int64?) {
        self.mode = mode
        self.windowDays = windowDays
        self.attachments = attachments
        self.storageCapBytes = storageCapBytes
    }

    public static let phoneDefault = OfflinePolicy(mode: .light, windowDays: 90, attachments: .thumbnails, storageCapBytes: 500 * 1024 * 1024)
    public static let computerDefault = OfflinePolicy(mode: .full, windowDays: nil, attachments: .full, storageCapBytes: nil)
    public static let televisionDefault = OfflinePolicy(mode: .light, windowDays: 30, attachments: .thumbnails, storageCapBytes: 100 * 1024 * 1024)

    // Whether moving from `old` to this policy means items this device threw away may now be
    // wanted again - in which case it has to re-read everything from the server, since its
    // cursor is already past them.
    public func widens(_ old: OfflinePolicy) -> Bool {
        func rank(_ policy: OfflinePolicy) -> Int {
            switch policy.mode {
            case .off: return 0
            case .light: return policy.windowDays.map { 1 + $0 } ?? Int.max - 1
            case .full: return Int.max
            }
        }
        return rank(self) > rank(old)
    }

    // Item ids a light copy doesn't keep. Never an item with edits still waiting to go up, never
    // anything pinned to stay offline, never the parents of something that is kept (a card needs
    // its column and board to be shown), and children always go with their parent.
    public func itemsToPrune(
        _ items: [String: OrganizerItem], protected: Set<String>, now: Date = Date(), calendar: Calendar = .current
    ) -> Set<String> {
        guard mode == .light, let windowDays else { return [] }
        let cutoff = calendar.date(byAdding: .day, value: -windowDays, to: now) ?? now

        func root(of item: OrganizerItem) -> OrganizerItem {
            var current = item
            var seen: Set<String> = [item.id]
            while let parentId = current.parentId, let parent = items[parentId], !seen.contains(parentId) {
                seen.insert(parentId)
                current = parent
            }
            return current
        }

        func keepsOnItsOwn(_ item: OrganizerItem) -> Bool {
            if item.deleted { return true }
            switch item.kind {
            case .event:
                switch Recurrence.lastEnd(of: item, calendar: calendar) {
                case .forever?, nil: return true
                case let .at(end)?: return end >= cutoff
                }
            case .reminder:
                guard item.bool("completed") else { return true }
                let completed = item.string("completedAt").flatMap(OrganizerDates.parseTimestamp) ?? item.updatedAt ?? now
                return completed >= cutoff
            case .note:
                return item.bool("pinned") || (item.updatedAt ?? now) >= cutoff
            case .card:
                // A board is a working surface - dropping a card that's still on it would make the
                // board look wrong offline. Only finished cards age out.
                return !item.bool("completed") || (item.updatedAt ?? now) >= cutoff
            case .board, .column, .attachment:
                return true
            }
        }

        var keep: Set<String> = []
        for item in items.values {
            let top = root(of: item)
            let keptByRoot = top.id == item.id ? keepsOnItsOwn(item) : keepsOnItsOwn(top) && keepsOnItsOwn(item)
            if keptByRoot || protected.contains(item.id) || protected.contains(top.id) {
                keep.insert(item.id)
            }
        }
        // Whatever is kept keeps its ancestors.
        for id in keep {
            var parentId = items[id]?.parentId
            var guardCount = 0
            while let current = parentId, guardCount < 64 {
                keep.insert(current)
                parentId = items[current]?.parentId
                guardCount += 1
            }
        }
        return Set(items.keys).subtracting(keep)
    }
}
