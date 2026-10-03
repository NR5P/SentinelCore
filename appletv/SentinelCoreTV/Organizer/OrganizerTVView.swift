import ImageIO
import OrganizerKit
import SwiftUI

// The Organizer on the television: what's coming up, reminders to tick off, notes and boards to
// read. Viewing first - typing on a TV remote is miserable, so creating and editing stay on the
// phones, computers and dashboard - but ticking a reminder or a card off from the sofa is exactly
// the kind of thing a TV is for, so that works here.

struct AppOrganizerTransport: OrganizerTransport {
    let client: SentinelClient

    func send(method: String, path: String, query: [URLQueryItem], body: Data?, contentType: String?, timeout: TimeInterval?) async throws -> OrganizerHTTPResponse {
        do {
            let (status, data) = try await client.organizerRequest(
                method: method, path: path, query: query, body: body, contentType: contentType, timeout: timeout)
            return OrganizerHTTPResponse(status: status, data: data)
        } catch let error as URLError where error.code == .cancelled {
            throw CancellationError()
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw OrganizerError.offline
        }
    }
}

enum OrganizerDevice {
    static var id: String {
        if let existing = UserDefaults.standard.string(forKey: "organizerDeviceId") { return existing }
        let id = "tv-\(UUID().uuidString.prefix(8).lowercased())"
        UserDefaults.standard.set(id, forKey: "organizerDeviceId")
        return id
    }

    // Caches, not Application Support: tvOS gives apps no guaranteed persistent storage, and
    // everything here can be read again from the server.
    static var directory: URL {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory
        let url = base.appendingPathComponent("Organizer", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}

struct OrganizerTVView: View {
    enum Pane: String, CaseIterable, Identifiable {
        case agenda, reminders, notes, boards
        var id: Self { self }
        var title: String {
            switch self {
            case .agenda: "Coming Up"
            case .reminders: "Reminders"
            case .notes: "Notes"
            case .boards: "Boards"
            }
        }
    }

    @Environment(OrganizerEngine.self) private var engine
    @State private var pane: Pane = .agenda

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            HStack {
                Picker("Show", selection: $pane) {
                    ForEach(Pane.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .frame(maxWidth: 1000)
                Spacer()
                // "Not Now" on the new-server question - the way back to it, since nothing syncs until then.
                if engine.declinedReset != nil {
                    Button("Sync Paused - Decide…", systemImage: "exclamationmark.icloud") { engine.reconsiderReset() }
                        .font(.caption)
                        .foregroundStyle(SentinelTheme.amber)
                } else if !engine.syncState.online,engine.isConnected || engine.syncState.waitingChanges > 0 {
                    Label("Offline - showing this TV's copy", systemImage: "icloud.slash")
                        .font(.caption)
                        .foregroundStyle(SentinelTheme.amber)
                }
            }
            switch pane {
            case .agenda: AgendaTV()
            case .reminders: RemindersTV()
            case .notes: NotesTV()
            case .boards: BoardsTV()
            }
        }
        .padding(.horizontal, 60)
    }
}

// The holiday calendar's color, the same as on every other device.
private func holidayColor(_ setId: String) -> Color {
    let hex = HolidayColors.hex(setId).dropFirst()
    guard hex.count == 6, let value = UInt32(hex, radix: 16) else { return .secondary }
    return Color(red: Double(value >> 16 & 0xFF) / 255, green: Double(value >> 8 & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
}

@MainActor
private func spaceColor(_ engine: OrganizerEngine, _ id: String) -> Color {
    let colors: [Color] = [SentinelTheme.green, SentinelTheme.cyan, SentinelTheme.amber, .purple, .pink, .blue]
    let index = engine.visibleSpaces.firstIndex { $0.id == id } ?? 0
    return colors[index % colors.count]
}

// MARK: - agenda

private struct AgendaTV: View {
    @Environment(OrganizerEngine.self) private var engine

    var body: some View {
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: Date())
        let end = calendar.date(byAdding: .day, value: 14, to: today)!
        let days = Dictionary(grouping: entries(from: today, to: end)) { calendar.startOfDay(for: $0.start) }
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 28) {
                if days.isEmpty {
                    Text("Nothing in the next two weeks").foregroundStyle(.secondary)
                }
                ForEach(days.keys.sorted(), id: \.self) { day in
                    VStack(alignment: .leading, spacing: 12) {
                        Text(calendar.isDateInToday(day) ? "Today" : calendar.isDateInTomorrow(day) ? "Tomorrow" : day.formatted(.dateTime.weekday(.wide).month().day()))
                            .font(.headline)
                            .foregroundStyle(SentinelTheme.cyan)
                        ForEach(days[day] ?? [], id: \.id) { entry in
                            Button {} label: {
                                HStack(spacing: 20) {
                                    RoundedRectangle(cornerRadius: 3).fill(entry.color).frame(width: 8)
                                    Text(entry.time).frame(width: 260, alignment: .leading).foregroundStyle(.secondary)
                                    VStack(alignment: .leading) {
                                        Text(entry.title)
                                        if let detail = entry.detail { Text(detail).font(.caption).foregroundStyle(.secondary) }
                                    }
                                    Spacer()
                                    Text(entry.tag).font(.caption).foregroundStyle(.secondary)
                                }
                                .padding(.vertical, 6)
                            }
                            .buttonStyle(.card)
                        }
                    }
                }
            }
            .padding(.vertical, 20)
        }
    }

    private struct Entry {
        var id: String
        var start: Date
        var time: String
        var title: String
        var detail: String?
        var tag: String
        var color: Color
    }

    private func entries(from: Date, to: Date) -> [Entry] {
        var out: [Entry] = []
        // Holiday calendars chosen on the phone, Mac or dashboard - the choice follows the account.
        // One that lasts several days shows on each of them.
        let calendar = Calendar.current
        for (holiday, start, end) in engine.holidays(from: from, to: to) {
            var day = max(start, from)
            var index = 0
            while day < min(end, to) {
                out.append(Entry(
                    id: "\(holiday.id)-\(index)", start: day,
                    time: holiday.dayCount > 1 ? "Day \(calendar.dateComponents([.day], from: start, to: day).day! + 1) of \(holiday.dayCount)" : "Holiday",
                    title: holiday.name, detail: holiday.note,
                    tag: engine.holidaySetName(holiday.setId), color: holidayColor(holiday.setId)))
                day = calendar.date(byAdding: .day, value: 1, to: day)!
                index += 1
            }
        }
        for event in engine.items(.event) {
            for occurrence in Recurrence.occurrences(of: event, from: from, to: to) {
                let allDay = event.bool("allDay")
                out.append(Entry(
                    id: "\(event.id)-\(occurrence.start.timeIntervalSince1970)", start: occurrence.start,
                    time: allDay ? "All day" : "\(occurrence.start.formatted(date: .omitted, time: .shortened)) – \(occurrence.end.formatted(date: .omitted, time: .shortened))",
                    title: event.title.isEmpty ? "Untitled" : event.title, detail: event.string("location"),
                    tag: engine.space(event.spaceId)?.name ?? "", color: spaceColor(engine, event.spaceId)))
            }
        }
        for reminder in engine.items(.reminder) where reminder.parentId == nil && !reminder.bool("completed") {
            guard let due = OrganizerDates.parse(reminder.string("due"), allDay: reminder.bool("allDay")), due >= from, due < to else { continue }
            out.append(Entry(
                id: reminder.id, start: due,
                time: reminder.bool("allDay") ? "Reminder" : "Due \(due.formatted(date: .omitted, time: .shortened))",
                title: "☐ " + reminder.title, detail: nil, tag: engine.space(reminder.spaceId)?.name ?? "", color: SentinelTheme.amber))
        }
        return out.sorted { $0.start < $1.start }
    }
}

// MARK: - reminders

private struct RemindersTV: View {
    @Environment(OrganizerEngine.self) private var engine

    var body: some View {
        let reminders = engine.items(.reminder)
            .filter { $0.parentId == nil && !$0.bool("completed") }
            .sorted {
                (OrganizerDates.parse($0.string("due"), allDay: $0.bool("allDay")) ?? .distantFuture)
                    < (OrganizerDates.parse($1.string("due"), allDay: $1.bool("allDay")) ?? .distantFuture)
            }
        List {
            if reminders.isEmpty {
                Text("Nothing to do").foregroundStyle(.secondary)
            }
            ForEach(reminders) { reminder in
                Button {
                    complete(reminder)
                } label: {
                    HStack(spacing: 24) {
                        Image(systemName: "circle").foregroundStyle(.secondary)
                        VStack(alignment: .leading, spacing: 4) {
                            Text(reminder.title.isEmpty ? "Reminder" : reminder.title)
                            if let due = OrganizerDates.parse(reminder.string("due"), allDay: reminder.bool("allDay")) {
                                Text(reminder.bool("allDay") ? due.formatted(date: .abbreviated, time: .omitted) : due.formatted(date: .abbreviated, time: .shortened))
                                    .font(.caption)
                                    .foregroundStyle(due < Date() ? SentinelTheme.red : .secondary)
                            }
                        }
                        Spacer()
                        Text(engine.space(reminder.spaceId)?.name ?? "").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        }
        .buttonStyle(.automatic) // rows, not the app-wide FocusContrastButtonStyle
        .overlay(alignment: .bottomLeading) {
            Text("Select a reminder to tick it off.").font(.caption).foregroundStyle(.secondary).padding()
        }
    }

    // A repeating reminder moves on to its next date rather than disappearing - see the phone
    // app's completeReminder, which this matches.
    private func complete(_ reminder: OrganizerItem) {
        if let next = Recurrence.nextDue(of: reminder) {
            engine.update(reminder.id, ["due": .string(next), "completed": false, "lastCompletedAt": .string(OrganizerDates.timestamp(Date()))])
        } else {
            engine.update(reminder.id, ["completed": true, "completedAt": .string(OrganizerDates.timestamp(Date()))])
        }
    }
}

// MARK: - notes

private struct NotesTV: View {
    @Environment(OrganizerEngine.self) private var engine

    var body: some View {
        let notes = engine.items(.note)
            .filter { !$0.bool("archived") }
            .sorted { ($0.bool("pinned") ? 1 : 0, $0.updatedAt ?? .distantPast) > ($1.bool("pinned") ? 1 : 0, $1.updatedAt ?? .distantPast) }
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 520), spacing: 40)], spacing: 40) {
                ForEach(notes) { note in
                    NavigationLink {
                        NoteTVDetail(noteId: note.id)
                    } label: {
                        VStack(alignment: .leading, spacing: 10) {
                            Text(note.title.isEmpty ? "Untitled" : note.title).font(.headline).lineLimit(1)
                            Text((note.string("body") ?? "").replacingOccurrences(of: "- [ ] ", with: "☐ ").replacingOccurrences(of: "- [x] ", with: "☑ "))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(5)
                                .frame(maxWidth: .infinity, alignment: .topLeading)
                            Spacer(minLength: 0)
                            HStack {
                                Text(engine.space(note.spaceId)?.name ?? "").font(.caption2).foregroundStyle(spaceColor(engine, note.spaceId))
                                Spacer()
                                let progress = NoteChecklist.progress(note.string("body") ?? "")
                                if progress.total > 0 {
                                    Text("☑ \(progress.done)/\(progress.total)").font(.caption2).foregroundStyle(.secondary)
                                }
                            }
                        }
                        .padding(24)
                        .frame(height: 280)
                    }
                    .buttonStyle(.card)
                }
            }
            .padding(.vertical, 20)
        }
    }
}

private struct NoteTVDetail: View {
    @Environment(OrganizerEngine.self) private var engine
    let noteId: String

    var body: some View {
        ScrollView {
            if let note = engine.item(noteId) {
                VStack(alignment: .leading, spacing: 24) {
                    Text(note.title.isEmpty ? "Untitled" : note.title).font(.title2.bold())
                    ForEach(Array(NoteChecklist.lines(note.string("body") ?? "").enumerated()), id: \.offset) { index, line in
                        noteLine(line, index: index, note: note)
                    }
                    let pictures = engine.children(of: note.id, kind: .attachment).filter { $0.string("contentType")?.hasPrefix("image/") == true }
                    if !pictures.isEmpty {
                        ScrollView(.horizontal) {
                            HStack(spacing: 30) {
                                ForEach(pictures) { picture in
                                    TVImage(hash: picture.string("hash") ?? "").frame(width: 480, height: 320).clipShape(RoundedRectangle(cornerRadius: 12))
                                }
                            }
                        }
                    }
                }
                .frame(maxWidth: 1400, alignment: .leading)
                .padding(60)
            }
        }
    }

    // Checklist lines are buttons, so the shopping list on the TV can be ticked off from the sofa.
    // Which lines are items is NoteChecklist's call, the same rule the phone and dashboard use.
    @ViewBuilder
    private func noteLine(_ line: NoteChecklist.Line, index: Int, note: OrganizerItem) -> some View {
        let trimmed = line.text.trimmingCharacters(in: .whitespaces)
        if let item = line.item {
            Button {
                engine.update(note.id, ["body": .string(NoteChecklist.toggle(note.string("body") ?? "", line: index))])
            } label: {
                HStack {
                    Image(systemName: item.done ? "checkmark.square.fill" : "square").foregroundStyle(item.done ? SentinelTheme.green : .secondary)
                    Text(item.text).strikethrough(item.done)
                }
            }
        } else if line.isCode {
            if !trimmed.hasPrefix("```") { Text(line.text).font(.body.monospaced()) }
        } else if trimmed.hasPrefix("#") {
            Text(trimmed.drop { $0 == "#" }.trimmingCharacters(in: .whitespaces)).font(.headline).foregroundStyle(SentinelTheme.cyan)
        } else if !trimmed.isEmpty {
            Text((try? AttributedString(markdown: line.text)) ?? AttributedString(line.text))
        }
    }
}

// MARK: - boards

private struct BoardsTV: View {
    @Environment(OrganizerEngine.self) private var engine
    @State private var boardId: String?

    var body: some View {
        let boards = engine.items(.board).filter { !$0.bool("archived") }.sorted { ($0.number("position") ?? 0) < ($1.number("position") ?? 0) }
        let board = boards.first { $0.id == boardId } ?? boards.first
        VStack(alignment: .leading, spacing: 20) {
            if boards.count > 1 {
                Picker("Board", selection: Binding(get: { board?.id ?? "" }, set: { boardId = $0 })) {
                    ForEach(boards) { Text($0.title).tag($0.id) }
                }
                .pickerStyle(.segmented)
            }
            if let board {
                ScrollView(.horizontal) {
                    HStack(alignment: .top, spacing: 40) {
                        ForEach(engine.children(of: board.id, kind: .column).sorted { ($0.number("position") ?? 0) < ($1.number("position") ?? 0) }) { column in
                            VStack(alignment: .leading, spacing: 16) {
                                Text(column.title).font(.headline).foregroundStyle(SentinelTheme.cyan)
                                ForEach(engine.children(of: column.id, kind: .card).sorted { ($0.number("position") ?? 0) < ($1.number("position") ?? 0) }) { card in
                                    Button {
                                        engine.update(card.id, ["completed": .bool(!card.bool("completed"))])
                                    } label: {
                                        VStack(alignment: .leading, spacing: 8) {
                                            Text(card.title).strikethrough(card.bool("completed"))
                                            let checklist = card.fields["checklist"]?.array ?? []
                                            let comments = engine.children(of: card.id, kind: .comment).count
                                            if !checklist.isEmpty || comments > 0 {
                                                HStack(spacing: 20) {
                                                    if !checklist.isEmpty {
                                                        Text("\(checklist.filter { $0.object?["done"]?.bool == true }.count)/\(checklist.count) done")
                                                    }
                                                    if comments > 0 { Label("\(comments)", systemImage: "bubble.left") }
                                                }
                                                .font(.caption).foregroundStyle(.secondary)
                                            }
                                        }
                                        .frame(width: 400, alignment: .leading)
                                        .padding(16)
                                    }
                                    .buttonStyle(.card)
                                }
                            }
                            .frame(width: 440, alignment: .topLeading)
                        }
                    }
                    .padding(.vertical, 20)
                }
                Text("Select a card to mark it done.").font(.caption).foregroundStyle(.secondary)
            } else {
                Text("No boards yet").foregroundStyle(.secondary)
            }
        }
    }
}

private struct TVImage: View {
    @Environment(OrganizerEngine.self) private var engine
    let hash: String
    @State private var image: Image?

    var body: some View {
        ZStack {
            Rectangle().fill(.quaternary)
            image?.resizable().scaledToFill()
        }
        .task(id: hash) {
            guard let data = await engine.imageData(hash: hash, thumbnail: true),
                  let source = CGImageSourceCreateWithData(data as CFData, nil),
                  let cgImage = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                      kCGImageSourceCreateThumbnailFromImageAlways: true,
                      kCGImageSourceCreateThumbnailWithTransform: true,
                      kCGImageSourceThumbnailMaxPixelSize: 960,
                  ] as CFDictionary) else { return }
            image = Image(decorative: cgImage, scale: 1)
        }
    }
}
