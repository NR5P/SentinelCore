import Foundation

// "Share to Sentinel Core": text or a link shared from any app becomes an Organizer note. On the
// iPhone the share extension can't reach the app's copy of the Organizer, so it leaves what was
// shared in the App Group container (SharedNoteInbox) and the app turns it into a note the next
// time it runs - at once if it's running, since the extension posts a Darwin notification. On the
// Mac the Services menu hands the text straight to the app. The Android app has the same rules in
// organizer/VoiceCapture.kt.

public struct SharedNote: Codable, Equatable, Sendable {
    public var text: String
    // The sending app's title for it - a web page's title, a note's name - when it gave one.
    public var subject: String?
    public var url: String?
    public var at: Date

    public init(text: String, subject: String? = nil, url: String? = nil, at: Date = Date()) {
        self.text = text
        self.subject = subject
        self.url = url
        self.at = at
    }

    public var isEmpty: Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && (url ?? "").isEmpty
    }

    // A note's fields. The title is the sender's title if it gave one, otherwise the opening words;
    // the body keeps everything that was shared; a link also goes in `links`, so it's tappable.
    public var fields: [String: JSONValue] {
        let body = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let link = (url ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let subject = (self.subject ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let title: String
        if !subject.isEmpty {
            title = subject
        } else if !body.isEmpty, body != link {
            title = NoteText.title(for: body)
        } else {
            title = URL(string: link)?.host.map { $0.hasPrefix("www.") ? String($0.dropFirst(4)) : $0 } ?? link
        }
        var lines: [String] = []
        if !body.isEmpty, body != title { lines.append(body) }
        if !link.isEmpty, !body.contains(link) { lines.append(link) }
        var fields: [String: JSONValue] = ["title": .string(title), "body": .string(lines.joined(separator: "\n\n"))]
        if !link.isEmpty {
            fields["links"] = .array([.object(subject.isEmpty ? ["url": .string(link)] : ["url": .string(link), "title": .string(subject)])])
        }
        return fields
    }
}

public enum NoteText {
    // A short note is its own title. A longer one is titled with its opening words (the whole of
    // it stays in the body).
    public static func title(for text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let firstLine = trimmed.split(whereSeparator: \.isNewline).first.map(String.init) ?? trimmed
        guard firstLine.count > 60 else { return firstLine }
        let clipped = firstLine.prefix(50)
        let cut = clipped.lastIndex(of: " ").map { clipped[..<$0] } ?? clipped
        return cut.trimmingCharacters(in: CharacterSet(charactersIn: " ,.;:-")) + "\u{2026}"
    }
}

// The share extension's drop-box in the App Group container. The extension adds; the app takes.
public struct SharedNoteInbox: Sendable {
    // Darwin notification the extension posts after adding, so a running app takes it at once.
    public static let posted = "com.sentinelcore.client.shared-note"

    public let directory: URL

    public init(directory: URL) {
        self.directory = directory
    }

    public init?() {
        guard let url = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: OrganizerWidgetStore.appGroup) else { return nil }
        self.init(directory: url)
    }

    private var fileURL: URL { directory.appendingPathComponent("organizer-shared-notes.json") }

    public func add(_ note: SharedNote) {
        coordinate { url in
            var notes = Self.read(url)
            notes.append(note)
            if let data = try? Self.encoder.encode(notes) { try? data.write(to: url, options: .atomic) }
        }
    }

    public var isEmpty: Bool {
        var empty = true
        coordinate { empty = Self.read($0).isEmpty }
        return empty
    }

    // Everything waiting, cleared in the same coordinated step so one shared meanwhile is kept.
    public func take() -> [SharedNote] {
        var notes: [SharedNote] = []
        coordinate { url in
            notes = Self.read(url)
            if !notes.isEmpty { try? FileManager.default.removeItem(at: url) }
        }
        return notes
    }

    private func coordinate(_ body: (URL) -> Void) {
        var error: NSError?
        NSFileCoordinator().coordinate(writingItemAt: fileURL, options: [], error: &error, byAccessor: body)
    }

    private static func read(_ url: URL) -> [SharedNote] {
        guard let data = try? Data(contentsOf: url) else { return [] }
        return (try? decoder.decode([SharedNote].self, from: data)) ?? []
    }

    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .secondsSince1970
        return encoder
    }()

    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .secondsSince1970
        return decoder
    }()
}
