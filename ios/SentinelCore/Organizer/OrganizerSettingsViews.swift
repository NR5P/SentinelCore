import OrganizerKit
import SwiftUI

// MARK: - groups

// Personal, Family and groups - who sees what. Groups need login on, since the server has to
// know who each person is.
struct OrganizerGroupsView: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    @State private var people: [OrganizerPerson] = []
    @State private var editing: GroupDraft?
    @State private var error: String?

    struct GroupDraft: Identifiable {
        var id: String { spaceId ?? "new" }
        var spaceId: String?
        var name: String
        var members: Set<String>
        var color: Color
    }

    var body: some View {
        Form {
            if engine.status?.personalSpaces != true {
                Section {
                    Text("Groups let you share notes, events, reminders and boards with only the people you pick.")
                    Text("They need login turned on, with an account for each person, so the server knows who is who. An admin can turn it on under Settings → Accounts → Dashboard Login. Until then everything here is shared with everyone as Family.")
                        .foregroundStyle(.secondary)
                }
            } else {
                Section {
                    ForEach(engine.visibleSpaces) { space in
                        HStack {
                            Circle().fill(engine.color(forSpace: space.id)).frame(width: 10, height: 10)
                            VStack(alignment: .leading) {
                                Text(space.name)
                                Text(describe(space)).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            if space.kind == .group {
                                Button("Edit") {
                                    editing = GroupDraft(spaceId: space.id, name: space.name, members: Set(space.members),
                                                         color: space.color.flatMap(Color.init(hex:)) ?? SentinelTheme.amber)
                                }
                                .buttonStyle(.borderless)
                            }
                        }
                    }
                } footer: {
                    Text("Personal is only you. Family is everyone with an account. Anyone in a group can change who's in it.")
                }
                Section {
                    Button("New Group", systemImage: "person.3") {
                        editing = GroupDraft(name: "", members: Set([engine.status?.username ?? ""].filter { !$0.isEmpty }), color: SentinelTheme.amber)
                    }
                }
            }
            if let error {
                Text(error).foregroundStyle(SentinelTheme.red).font(.caption)
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Spaces & Groups")
        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        .task { people = (try? await engine.people()) ?? [] }
        .sheet(item: $editing) { draft in
            NavigationStack {
                GroupEditor(draft: draft, people: people) { result in
                    editing = nil
                    error = result
                }
            }
            .environment(engine)
            #if os(macOS)
            .frame(minWidth: 420, minHeight: 460)
            #endif
        }
    }

    private func describe(_ space: OrganizerSpace) -> String {
        switch space.kind {
        case .personal: return "Only you"
        case .family: return "Everyone"
        case .group:
            return space.members.map { member in people.first { $0.username == member }?.label ?? member }.joined(separator: ", ")
        }
    }
}

private struct GroupEditor: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    @State var draft: OrganizerGroupsView.GroupDraft
    let people: [OrganizerPerson]
    var finished: (String?) -> Void
    @State private var saving = false
    @State private var confirmingDelete = false

    var body: some View {
        Form {
            Section {
                TextField("Name, e.g. Parents", text: $draft.name)
                ColorPicker("Colour", selection: $draft.color, supportsOpacity: false)
            }
            Section("Who's in it") {
                ForEach(people) { person in
                    Toggle(isOn: Binding(
                        get: { draft.members.contains(person.username) },
                        set: { if $0 { draft.members.insert(person.username) } else { draft.members.remove(person.username) } })) {
                        Text(person.username == engine.status?.username ? "\(person.label) (you)" : person.label)
                    }
                }
            }
            if draft.spaceId != nil {
                Section {
                    Button("Delete Group", role: .destructive) { confirmingDelete = true }
                } footer: {
                    Text("Deleting a group deletes everything in it, for everyone in the group.")
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(draft.spaceId == nil ? "New Group" : "Edit Group")
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
            ToolbarItem(placement: .confirmationAction) {
                Button("Save") { Task { await save() } }
                    .disabled(saving || draft.name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .confirmationDialog("Delete \"\(draft.name)\" and everything in it?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Delete Group", role: .destructive) {
                Task {
                    do {
                        try await engine.deleteGroup(id: draft.spaceId!)
                        finished(nil)
                    } catch {
                        finished(error.localizedDescription)
                    }
                }
            }
        }
    }

    private func save() async {
        saving = true
        defer { saving = false }
        do {
            try await engine.saveGroup(id: draft.spaceId, OrganizerGroupInput(name: draft.name, members: Array(draft.members), color: draft.color.hexString))
            finished(nil)
        } catch {
            finished(error.localizedDescription)
        }
    }
}

extension Color {
    var hexString: String? {
        #if os(macOS)
        guard let color = NSColor(self).usingColorSpace(.sRGB) else { return nil }
        let (r, g, b) = (color.redComponent, color.greenComponent, color.blueComponent)
        #else
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        guard UIColor(self).getRed(&r, green: &g, blue: &b, alpha: &a) else { return nil }
        #endif
        return String(format: "#%02x%02x%02x", Int(max(0, min(1, r)) * 255), Int(max(0, min(1, g)) * 255), Int(max(0, min(1, b)) * 255))
    }
}

// MARK: - calendar subscription

struct OrganizerSubscribeView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    @State private var path: String?
    @State private var error: String?
    @State private var confirmingReset = false

    var body: some View {
        Form {
            Section {
                Text("Add this link as a subscribed calendar in Apple Calendar, Google Calendar or Outlook to see your events there. It's read-only on their side - edits still happen here.")
                if let url {
                    Text(url.absoluteString).font(.caption.monospaced()).textSelection(.enabled)
                    ShareLink(item: url) { Label("Share Link", systemImage: "square.and.arrow.up") }
                    if let webcal = URL(string: url.absoluteString.replacingOccurrences(of: #"^https?:"#, with: "webcal:", options: .regularExpression)) {
                        Link(destination: webcal) { Label("Open in Calendar", systemImage: "calendar.badge.plus") }
                    }
                } else if let error {
                    Text(error).foregroundStyle(SentinelTheme.red)
                } else {
                    ProgressView()
                }
            }
            Section {
                Button("Reset Link", role: .destructive) { confirmingReset = true }
            } footer: {
                Text("Anyone with this link can see your events, so treat it like a password. The other calendar has to be able to reach this server - from outside your home, that means the remote address. Resetting makes the old link stop working.")
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Subscribe")
        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        .task { await load(reset: false) }
        .confirmationDialog("Make a new link? Calendars subscribed to the old one stop updating.", isPresented: $confirmingReset, titleVisibility: .visible) {
            Button("Reset Link", role: .destructive) { Task { await load(reset: true) } }
        }
    }

    // The remote address if there is one, since a calendar service out on the internet can't
    // reach a LAN address.
    private var url: URL? {
        guard let path else { return nil }
        let base = model.hasRemoteServerURL ? model.remoteServerURL : model.activeServerURL
        return URL(string: base.trimmingCharacters(in: CharacterSet(charactersIn: "/ ")) + path)
    }

    private func load(reset: Bool) async {
        do {
            path = try await engine.calendarFeedPath(reset: reset)
            error = nil
        } catch {
            self.error = "Couldn't get a link - is the server reachable?"
        }
    }
}

// MARK: - offline copy settings

// Per-device, in this app's Settings: how much of the Organizer this device keeps for use with no
// signal. Phones default light; computers keep everything, which is what lets them rebuild a
// replacement server.
struct OrganizerOfflineSettingsSection: View {
    @Environment(OrganizerEngine.self) private var engine

    var body: some View {
        let policy = engine.policy
        let summary = engine.storageSummary()
        Section {
            Picker("Keep offline", selection: binding(\.mode)) {
                Text("Off").tag(OfflinePolicy.Mode.off)
                Text("Light").tag(OfflinePolicy.Mode.light)
                Text("Everything").tag(OfflinePolicy.Mode.full)
            }
            if policy.mode == .light {
                Picker("How far back", selection: binding(\.windowDays)) {
                    Text("30 days").tag(Int?.some(30))
                    Text("90 days").tag(Int?.some(90))
                    Text("1 year").tag(Int?.some(365))
                    Text("Everything").tag(Int?.none)
                }
            }
            if policy.mode != .off {
                Picker("Pictures & files", selection: binding(\.attachments)) {
                    Text("None").tag(OfflinePolicy.Attachments.none)
                    Text("Thumbnails").tag(OfflinePolicy.Attachments.thumbnails)
                    Text("Full size").tag(OfflinePolicy.Attachments.full)
                }
                Picker("Storage limit", selection: binding(\.storageCapBytes)) {
                    Text("500 MB").tag(Int64?.some(500 * 1_048_576))
                    Text("2 GB").tag(Int64?.some(2 * 1_073_741_824))
                    Text("10 GB").tag(Int64?.some(10 * 1_073_741_824))
                    Text("No limit").tag(Int64?.none)
                }
            }
            LabeledContent("On this device") {
                Text("\(summary.itemCount) items · \(ByteCountFormatter.string(fromByteCount: summary.bytesOnDevice, countStyle: .file))")
            }
            if let serverCount = summary.serverItemCount {
                // What a replacement server would get back from this device alone. Can briefly
                // exceed the server's own count while edits made here are still on their way up.
                let complete = summary.itemCount >= serverCount && summary.attachmentsWithOriginal >= summary.attachmentCount
                LabeledContent("Could restore") {
                    Text(complete
                         ? "Everything"
                         : "\(min(summary.itemCount, serverCount)) of \(serverCount) items · \(summary.attachmentsWithOriginal) of \(summary.attachmentCount) pictures & files")
                        .foregroundStyle(complete ? SentinelTheme.green : .secondary)
                }
            }
            if engine.syncState.waitingChanges > 0 || summary.waitingUploads > 0 {
                LabeledContent("Waiting to sync") {
                    Text("\(engine.syncState.waitingChanges) changes · \(summary.waitingUploads) files")
                        .foregroundStyle(SentinelTheme.amber)
                }
            }
            Button("Sync Now") { Task { await engine.sync() } }
        } header: {
            Text("Organizer on This Device")
                // The restore line compares against the server's own count, which is only read
                // when connecting - refresh it whenever someone comes to look.
                .task { await engine.refreshStatus() }
        } footer: {
            Text(footer(policy))
        }
    }

    private func binding<Value>(_ keyPath: WritableKeyPath<OfflinePolicy, Value>) -> Binding<Value> {
        Binding(
            get: { engine.policy[keyPath: keyPath] },
            set: { value in
                var policy = engine.policy
                policy[keyPath: keyPath] = value
                OrganizerDevice.savedPolicy = policy
                engine.setPolicy(policy)
            })
    }

    private func footer(_ policy: OfflinePolicy) -> String {
        switch policy.mode {
        case .off:
            "Nothing is kept after the app closes; everything is read from the server. Anything you change with no connection is still kept until it's sent."
        case .light:
            "Keeps upcoming events, open reminders, boards, pinned notes and anything from the chosen window. Older things are still on the server and appear whenever you look while connected. Edits you make offline are always kept until they're sent."
        case .full:
            "Keeps everything you can see, so this device works fully offline - and if the server is ever lost, a new one can be filled back up from this device."
        }
    }
}
