import ImageIO
import OrganizerKit
import PhotosUI
import QuickLook
import SwiftUI
import UniformTypeIdentifiers

// Pieces every Organizer screen shares: space colours and pickers, the attachments and links
// sections that events, reminders, notes and cards all carry, and a small Markdown renderer.

enum OrganizerPalette {
    static let colors: [Color] = [SentinelTheme.green, SentinelTheme.cyan, SentinelTheme.amber, .purple, .pink, .blue, .orange]

    static func color(forSpace id: String, in spaces: [OrganizerSpace]) -> Color {
        if let hex = spaces.first(where: { $0.id == id })?.color, let color = Color(hex: hex) { return color }
        let index = spaces.firstIndex { $0.id == id } ?? 0
        return colors[index % colors.count]
    }

    static func color(forLabel name: String) -> Color {
        let hash = name.unicodeScalars.reduce(UInt32(0)) { ($0 &* 31) &+ $1.value }
        return colors[Int(hash % UInt32(colors.count))]
    }
}

extension Color {
    init?(hex: String) {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6, let value = UInt32(text, radix: 16) else { return nil }
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}

extension OrganizerEngine {
    func spaceName(_ id: String) -> String { space(id)?.name ?? "" }

    func color(forSpace id: String) -> Color { OrganizerPalette.color(forSpace: id, in: visibleSpaces) }
}

// Which space a new or moved item goes in.
struct SpacePicker: View {
    @Environment(OrganizerEngine.self) private var engine
    @Binding var spaceId: String

    var body: some View {
        Picker("Shared with", selection: $spaceId) {
            ForEach(engine.visibleSpaces) { space in
                Label(space.name, systemImage: icon(for: space)).tag(space.id)
            }
        }
    }

    private func icon(for space: OrganizerSpace) -> String {
        switch space.kind {
        case .personal: "person.fill"
        case .family: "house.fill"
        case .group: "person.3.fill"
        }
    }
}

struct SpaceTag: View {
    @Environment(OrganizerEngine.self) private var engine
    let spaceId: String

    var body: some View {
        HStack(spacing: 4) {
            Circle().fill(engine.color(forSpace: spaceId)).frame(width: 7, height: 7)
            Text(engine.spaceName(spaceId))
        }
        .font(.caption2)
        .foregroundStyle(.secondary)
    }
}

// MARK: - links

struct OrganizerLink: Identifiable, Hashable {
    var id = UUID()
    var url: String
    var title: String

    static func from(_ value: JSONValue?) -> [OrganizerLink] {
        (value?.array ?? []).compactMap { entry in
            guard let object = entry.object, let url = object["url"]?.string else { return nil }
            return OrganizerLink(url: url, title: object["title"]?.string ?? "")
        }
    }

    static func json(_ links: [OrganizerLink]) -> JSONValue {
        links.isEmpty ? .null : .array(links.map { link in
            var object: [String: JSONValue] = ["url": .string(link.url)]
            if !link.title.isEmpty { object["title"] = .string(link.title) }
            return .object(object)
        })
    }

    // Only web, mail and phone links open - a synced link is typed by whoever shares the space.
    var openable: URL? {
        let text = url.trimmingCharacters(in: .whitespaces)
        let lower = text.lowercased()
        if lower.hasPrefix("http://") || lower.hasPrefix("https://") || lower.hasPrefix("mailto:") || lower.hasPrefix("tel:") {
            return URL(string: text)
        }
        if text.contains("."), !text.contains(" ") { return URL(string: "https://" + text) }
        return nil
    }
}

struct LinksSection: View {
    @Binding var links: [OrganizerLink]
    @State private var newURL = ""
    @State private var newTitle = ""

    var body: some View {
        Section("Links") {
            ForEach(links) { link in
                if let url = link.openable {
                    Link(destination: url) {
                        VStack(alignment: .leading) {
                            Text(link.title.isEmpty ? link.url : link.title)
                            if !link.title.isEmpty { Text(link.url).font(.caption).foregroundStyle(.secondary) }
                        }
                    }
                } else {
                    Text(link.title.isEmpty ? link.url : link.title)
                }
            }
            .onDelete { links.remove(atOffsets: $0) }
            // The address gets a row of its own: sharing one with the label left a real URL
            // scrolled out of sight on an iPhone.
            TextField("Add a link: https://…", text: $newURL)
                #if os(iOS)
                .keyboardType(.URL)
                .textInputAutocapitalization(.never)
                #endif
                .autocorrectionDisabled()
            HStack {
                TextField("Label (optional)", text: $newTitle)
                Button("Add") {
                    let url = newURL.trimmingCharacters(in: .whitespaces)
                    guard !url.isEmpty else { return }
                    links.append(OrganizerLink(url: url, title: newTitle.trimmingCharacters(in: .whitespaces)))
                    newURL = ""
                    newTitle = ""
                }
                .disabled(newURL.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
    }
}

// MARK: - attachments

// Pictures and files on an item. `parentId` nil means the item hasn't been created yet;
// `ensureParent` creates it the moment the first file is added, so nothing has to be saved first.
struct AttachmentsSection: View {
    @Environment(OrganizerEngine.self) private var engine
    let parentId: String?
    var ensureParent: () -> String

    @State private var photoItems: [PhotosPickerItem] = []
    @State private var importing = false
    @State private var preview: URL?
    @State private var error: String?

    var body: some View {
        Section {
            let attachments = parentId.map { engine.children(of: $0, kind: .attachment).sorted { $0.position < $1.position } } ?? []
            if !attachments.isEmpty {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 10) {
                        ForEach(attachments) { attachment in
                            AttachmentTile(attachment: attachment) { open(attachment) }
                                .contextMenu {
                                    Button("Remove", systemImage: "trash", role: .destructive) { engine.delete(attachment.id) }
                                }
                        }
                    }
                    .padding(.vertical, 4)
                }
            }
            HStack {
                PhotosPicker(selection: $photoItems, maxSelectionCount: 10, matching: .any(of: [.images, .videos])) {
                    Label("Photo", systemImage: "photo.on.rectangle")
                }
                Spacer()
                Button {
                    importing = true
                } label: {
                    Label("File", systemImage: "paperclip")
                }
            }
            .buttonStyle(.borderless)
            if let error {
                Text(error).font(.caption).foregroundStyle(SentinelTheme.red)
            }
        } header: {
            Text("Pictures & Files")
        } footer: {
            Text("Kept on this device straight away - they upload whenever the server is reachable.")
        }
        .onChange(of: photoItems) { _, items in
            guard !items.isEmpty else { return }
            let chosen = items
            photoItems = []
            Task { await addPhotos(chosen) }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            guard case let .success(urls) = result else { return }
            for url in urls { addFile(url) }
        }
        .quickLookPreview($preview)
    }

    private func addPhotos(_ items: [PhotosPickerItem]) async {
        let parent = ensureParent()
        for (index, item) in items.enumerated() {
            guard let data = try? await item.loadTransferable(type: Data.self) else { continue }
            let type = item.supportedContentTypes.first ?? .jpeg
            let name = "Photo \(Date().formatted(.dateTime.year().month().day().hour().minute()))\(items.count > 1 ? " \(index + 1)" : "").\(type.preferredFilenameExtension ?? "jpg")"
            attach(data, name: name, type: type, to: parent)
        }
    }

    private func addFile(_ url: URL) {
        let parent = ensureParent()
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        guard let data = try? Data(contentsOf: url) else {
            error = "Couldn't read \(url.lastPathComponent)."
            return
        }
        let type = UTType(filenameExtension: url.pathExtension) ?? .data
        attach(data, name: url.lastPathComponent, type: type, to: parent)
    }

    private func attach(_ data: Data, name: String, type: UTType, to parent: String) {
        do {
            let size = imageSize(data)
            try engine.attach(data, name: name, contentType: type.preferredMIMEType ?? "application/octet-stream",
                              to: parent, width: size.map { Int($0.width) }, height: size.map { Int($0.height) })
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func imageSize(_ data: Data) -> CGSize? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int else { return nil }
        return CGSize(width: width, height: height)
    }

    private func open(_ attachment: OrganizerItem) {
        guard let hash = attachment.string("hash") else { return }
        Task {
            do {
                preview = try await engine.fileForOpening(hash: hash, name: attachment.string("name") ?? hash)
            } catch {
                self.error = "That file isn't on this device and the server can't be reached right now."
            }
        }
    }
}

struct AttachmentTile: View {
    @Environment(OrganizerEngine.self) private var engine
    let attachment: OrganizerItem
    var open: () -> Void

    var body: some View {
        Button(action: open) {
            VStack(alignment: .leading, spacing: 4) {
                if attachment.string("contentType")?.hasPrefix("image/") == true, let hash = attachment.string("hash") {
                    OrganizerImage(hash: hash)
                        .frame(width: 96, height: 72)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                } else {
                    RoundedRectangle(cornerRadius: 8)
                        .fill(.quaternary)
                        .frame(width: 96, height: 72)
                        .overlay {
                            Text((attachment.string("name") ?? "file").split(separator: ".").last.map { String($0.prefix(4)).uppercased() } ?? "FILE")
                                .font(.caption.bold())
                                .foregroundStyle(SentinelTheme.cyan)
                        }
                }
                Text(attachment.string("name") ?? "file")
                    .font(.caption2)
                    .lineLimit(1)
                    .frame(width: 96, alignment: .leading)
            }
        }
        .buttonStyle(.plain)
    }
}

// A picture from an attachment, from this device's copy when there is one, otherwise fetched.
struct OrganizerImage: View {
    @Environment(OrganizerEngine.self) private var engine
    let hash: String
    var thumbnail = true
    @State private var image: Image?

    var body: some View {
        ZStack {
            Rectangle().fill(.quaternary)
            if let image {
                image.resizable().scaledToFill()
            } else {
                Image(systemName: "photo").foregroundStyle(.secondary)
            }
        }
        .task(id: hash) {
            guard let data = await engine.imageData(hash: hash, thumbnail: thumbnail) else { return }
            image = Self.downsampled(data, maxPixel: thumbnail ? 400 : 2000)
        }
    }

    // Decoded straight to display size - an original from a phone camera is 12+ megapixels.
    static func downsampled(_ data: Data, maxPixel: Int) -> Image? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
        ]
        guard let cgImage = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
        return Image(decorative: cgImage, scale: 1)
    }
}

// MARK: - markdown

// The same small Markdown the dashboard renders: headings, lists, tickable checklists, quotes,
// code and inline emphasis/links. Lines are kept so a checklist tap can flip exactly its line;
// which lines are items is NoteChecklist's call, the same rule every other device uses.
struct MarkdownNoteView: View {
    let source: String
    var toggle: ((Int) -> Void)?
    // Given, each item gets a delete button - the note's check-off view.
    var remove: ((Int) -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(Array(NoteChecklist.lines(source).enumerated()), id: \.offset) { index, line in
                row(line, index: index)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .textSelection(.enabled)
    }

    @ViewBuilder
    private func row(_ line: NoteChecklist.Line, index: Int) -> some View {
        let trimmed = line.text.trimmingCharacters(in: .whitespaces)
        if line.isCode {
            if !trimmed.hasPrefix("```") { Text(line.text).font(.body.monospaced()) }
        } else if let item = line.item {
            HStack(alignment: .firstTextBaseline) {
                Button {
                    toggle?(index)
                } label: {
                    HStack(alignment: .firstTextBaseline) {
                        Image(systemName: item.done ? "checkmark.square.fill" : "square")
                            .foregroundStyle(item.done ? SentinelTheme.green : .secondary)
                        Text(inline(item.text)).strikethrough(item.done).foregroundStyle(item.done ? .secondary : .primary)
                    }
                }
                .buttonStyle(.plain)
                if let remove {
                    Spacer(minLength: 8)
                    Button { remove(index) } label: {
                        Image(systemName: "xmark").font(.caption).foregroundStyle(.secondary)
                    }
                    .buttonStyle(.borderless)
                    .accessibilityLabel("Delete \(item.text)")
                    .help("Delete item")
                }
            }
        } else if trimmed.hasPrefix("### ") {
            Text(inline(String(trimmed.dropFirst(4)))).font(.subheadline.bold()).foregroundStyle(SentinelTheme.cyan)
        } else if trimmed.hasPrefix("## ") {
            Text(inline(String(trimmed.dropFirst(3)))).font(.headline).foregroundStyle(SentinelTheme.cyan)
        } else if trimmed.hasPrefix("# ") {
            Text(inline(String(trimmed.dropFirst(2)))).font(.title3.bold()).foregroundStyle(SentinelTheme.cyan)
        } else if trimmed.hasPrefix("- ") || trimmed.hasPrefix("* ") {
            HStack(alignment: .firstTextBaseline) { Text("•"); Text(inline(String(trimmed.dropFirst(2)))) }
        } else if trimmed.hasPrefix("> ") {
            Text(inline(String(trimmed.dropFirst(2))))
                .foregroundStyle(.secondary)
                .padding(.leading, 8)
                .overlay(alignment: .leading) { Rectangle().fill(SentinelTheme.greenDim).frame(width: 3) }
        } else if trimmed.isEmpty {
            Color.clear.frame(height: 4)
        } else {
            Text(inline(line.text))
        }
    }

    private func inline(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(text)
    }
}

// MARK: - repeat & alert choices

enum RepeatChoice: String, CaseIterable, Identifiable {
    case never, daily, weekly, monthly, yearly, custom
    var id: Self { self }

    var label: String {
        switch self {
        case .never: "Never"
        case .daily: "Every Day"
        case .weekly: "Every Week"
        case .monthly: "Every Month"
        case .yearly: "Every Year"
        case .custom: "Custom (set elsewhere)"
        }
    }

    init(_ rule: String?) {
        guard let rule, !rule.isEmpty else { self = .never; return }
        let simple = rule.split(separator: ";").filter { !$0.hasPrefix("UNTIL=") }.joined(separator: ";")
        switch simple {
        case "FREQ=DAILY": self = .daily
        case "FREQ=WEEKLY": self = .weekly
        case "FREQ=MONTHLY": self = .monthly
        case "FREQ=YEARLY": self = .yearly
        default: self = .custom
        }
    }

    // A rule from this choice; `custom` keeps whatever another app wrote.
    func rule(until: Date?, keeping existing: String?) -> JSONValue {
        switch self {
        case .never: return .null
        case .custom: return .from(existing)
        default:
            var rule = RecurrenceRule(frequency: .init(rawValue: rawValue.uppercased())!)
            rule.until = until
            return .string(rule.rruleValue)
        }
    }
}

enum AlertChoice: Int, CaseIterable, Identifiable {
    case none = -1, atTime = 0, five = 5, ten = 10, fifteen = 15, thirty = 30, hour = 60, twoHours = 120, day = 1440
    var id: Self { self }

    var label: String {
        switch self {
        case .none: "None"
        case .atTime: "At time of event"
        case .five: "5 minutes before"
        case .ten: "10 minutes before"
        case .fifteen: "15 minutes before"
        case .thirty: "30 minutes before"
        case .hour: "1 hour before"
        case .twoHours: "2 hours before"
        case .day: "1 day before"
        }
    }

    init(_ value: JSONValue?) {
        self = (value?.array?.first?.int).flatMap(AlertChoice.init(rawValue:)) ?? .none
    }

    var json: JSONValue { self == .none ? .null : .array([.number(Double(rawValue))]) }
}

extension OrganizerItem {
    var sortPosition: Double { number("position") ?? (createdAt?.timeIntervalSince1970 ?? 0) }
}
