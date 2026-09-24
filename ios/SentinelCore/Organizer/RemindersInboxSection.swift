import OrganizerKit
import SwiftUI
#if os(iOS)
import UIKit
#endif

// Per-device, in this app's Settings: bring reminders in from an Apple Reminders list. See
// RemindersInbox.
struct RemindersInboxSection: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.openURL) private var openURL
    private let inbox = RemindersInbox.shared

    var body: some View {
        Section {
            Toggle("Bring In Apple Reminders", isOn: Binding(
                get: { inbox.enabled },
                set: { on in Task { await inbox.setEnabled(on) } }))
            if inbox.enabled {
                switch inbox.access {
                case .denied:
                    Text("Sentinel Core isn't allowed to see your reminders.")
                        .foregroundStyle(SentinelTheme.amber)
                    Button("Open Privacy Settings") { openURL(Self.privacySettings) }
                case .notAsked:
                    EmptyView()
                case .granted:
                    settings
                }
            }
        } header: {
            Text("Apple Reminders")
        } footer: {
            Text("Anything added to the chosen list, by you or by Siri, moves into the Organizer and out of Apple Reminders. To send plain \u{201C}Hey Siri, remind me\u{2026}\u{201D} here, make that list your default in Settings \u{2192} Apps \u{2192} Reminders (on a Mac, in Reminders' settings). Reminders that alert by location stay in Apple Reminders. Each device has its own setting.")
        }
    }

    @ViewBuilder
    private var settings: some View {
        Picker("From List", selection: Binding(get: { inbox.listId }, set: { inbox.listId = $0 })) {
            if inbox.listId == nil { Text("Choose").tag(String?.none) }
            ForEach(inbox.lists) { list in
                // Two lists with one name (iCloud and On My iPhone) are told apart by account.
                Text(inbox.lists.filter { $0.title == list.title }.count > 1 ? "\(list.title) (\(list.account))" : list.title)
                    .tag(String?.some(list.id))
            }
        }
        if !inbox.lists.contains(where: { $0.title == RemindersInbox.listName }) {
            Button("Create a \u{201C}\(RemindersInbox.listName)\u{201D} List") { inbox.createList() }
        }
        if engine.knowsSpaces {
            Picker("Put Them In", selection: Binding(get: { inbox.spaceId }, set: { inbox.spaceId = $0 })) {
                if inbox.spaceId == nil { Text("Choose").tag(String?.none) }
                ForEach(engine.spaces) { space in
                    Text(space.name).tag(String?.some(space.id))
                }
            }
        }
        if let lastRun = inbox.lastRun {
            LabeledContent("Last Checked", value: lastRun.formatted(date: .omitted, time: .shortened))
        }
        if let brought = inbox.lastBrought {
            LabeledContent("Last Brought In") {
                Text("\(brought.count) at \(brought.at.formatted(date: .omitted, time: .shortened))")
            }
        }
        ForEach(inbox.leftBehind, id: \.self) { note in
            Text("Left in Apple Reminders: \(note)")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        if let problem = inbox.problem {
            Text(problem).foregroundStyle(SentinelTheme.amber)
        }
        Button("Bring In Now") { Task { await inbox.bringIn() } }
    }

    private static var privacySettings: URL {
        #if os(macOS)
        URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Reminders")!
        #else
        URL(string: UIApplication.openSettingsURLString)!
        #endif
    }
}
