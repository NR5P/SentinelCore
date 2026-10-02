import OrganizerKit
import SwiftUI

// Quick Add: one line, said or typed, that becomes a reminder, a note or an event - "add a
// reminder to take out the trash at 7am tomorrow". The Quick Add widget opens it (the microphone
// button straight to listening, the keyboard button to typing), and so does File > Quick Add on
// the Mac. QuickCapture in OrganizerKit decides which kind and when; the person sees the result
// before it's added, and can switch the kind if the words didn't say.

// sentinelcore://capture?mode=voice&kind=note - mode is voice or type, kind is optional.
struct QuickCaptureRequest: Identifiable, Equatable {
    let id = UUID()
    var listen: Bool
    var kind: QuickCapture.Kind?

    init(listen: Bool, kind: QuickCapture.Kind? = nil) {
        self.listen = listen
        self.kind = kind
    }

    init?(url: URL) {
        guard url.scheme == "sentinelcore", url.host() == "capture" else { return nil }
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        listen = items.first { $0.name == "mode" }?.value == "voice"
        kind = items.first { $0.name == "kind" }?.value.flatMap(QuickCapture.Kind.init(rawValue:))
    }
}

extension Notification.Name {
    // File > Quick Add on the Mac.
    static let sentinelQuickCapture = Notification.Name("sentinelQuickCapture")
}

extension View {
    // The Quick Add sheet, shown for a request (RootView sets one from the widget's link) or for
    // File > Quick Add.
    func quickCapture(_ request: Binding<QuickCaptureRequest?>, engine: OrganizerEngine, blocked: String?) -> some View {
        sheet(item: request) { request in
            QuickCaptureSheet(request: request, blocked: blocked)
                .environment(engine)
                #if os(macOS)
                .frame(minWidth: 460, minHeight: 380)
                #else
                .presentationDetents([.medium, .large])
                #endif
        }
        .onReceive(NotificationCenter.default.publisher(for: .sentinelQuickCapture)) { _ in
            request.wrappedValue = QuickCaptureRequest(listen: false)
        }
    }
}

struct QuickCaptureSheet: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    let request: QuickCaptureRequest
    // Why nothing can be added right now (no server set up, the Organizer turned off), if so.
    let blocked: String?

    @State private var text = ""
    // The kind picked by hand; nil follows the words.
    @State private var chosen: QuickCapture.Kind?
    @State private var capture: SpeechCapture?
    @State private var added: String?
    @FocusState private var typing: Bool

    private var parsed: QuickCapture { QuickCapture.parse(text) }
    private var kind: QuickCapture.Kind { chosen ?? parsed.kind }
    private var draft: QuickCapture.Draft { parsed.draft(as: kind) }
    private var listening: Bool {
        guard let capture else { return false }
        return capture.phase == .starting || capture.phase == .listening
    }
    private var canAdd: Bool { blocked == nil && engine.knowsSpaces && !draft.isEmpty && !listening && added == nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Quick Add").font(.title2.weight(.semibold))
                Spacer()
                Button("Cancel") { dismiss() }
                    .buttonStyle(.borderless)
                    .keyboardShortcut(.cancelAction)
            }

            Picker("Add as", selection: Binding(get: { kind }, set: { chosen = $0 })) {
                ForEach(QuickCapture.Kind.allCases, id: \.self) { kind in
                    Label(Self.name(kind), systemImage: Self.icon(kind)).tag(kind)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            HStack(spacing: 10) {
                TextField("Say or type it", text: $text)
                    .textFieldStyle(.roundedBorder)
                    .focused($typing)
                    .onSubmit(add)
                    .disabled(listening)
                    #if os(iOS)
                    .submitLabel(.done)
                    #endif
                Button {
                    listening ? capture?.finish() : listen()
                } label: {
                    Image(systemName: listening ? "stop.circle.fill" : "mic.circle.fill")
                        .font(.system(size: 30))
                        .foregroundStyle(listening ? SentinelTheme.amber : SentinelTheme.green)
                        .symbolEffect(.pulse, isActive: listening)
                }
                .buttonStyle(.borderless)
                .accessibilityLabel(listening ? "Stop listening" : "Speak")
            }

            status
            preview

            if let blocked {
                Label(blocked, systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(SentinelTheme.amber)
            } else if !engine.knowsSpaces {
                Label("Waiting for the Organizer\u{2019}s first sync\u{2026}", systemImage: "arrow.triangle.2.circlepath")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            Spacer(minLength: 0)

            HStack {
                Spacer()
                Button(action: add) {
                    Text(added == nil ? "Add \(Self.name(kind))" : "Added").frame(minWidth: 120)
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
                .tint(SentinelTheme.green)
                .disabled(!canAdd)
            }
        }
        .padding(20)
        .onAppear {
            chosen = request.kind
            if request.listen { listen() } else { typing = true }
        }
        // The words appear in the box as they're heard, so a misheard one can be fixed by typing.
        .onChange(of: capture?.transcript) { _, heard in
            if let heard, !heard.isEmpty { text = heard }
        }
        .onDisappear { capture?.stop() }
    }

    @ViewBuilder
    private var status: some View {
        if let capture {
            switch capture.phase {
            case .starting:
                Label("Getting ready\u{2026}", systemImage: "mic").foregroundStyle(.secondary).font(.callout)
            case .listening:
                Label(text.isEmpty ? "Listening\u{2026}" : "Listening \u{2013} pause when you\u{2019}re done", systemImage: "waveform")
                    .foregroundStyle(SentinelTheme.green)
                    .font(.callout)
                    .symbolEffect(.variableColor.iterative, isActive: true)
            case .finished:
                if text.isEmpty {
                    Label("Didn\u{2019}t catch that. \(capture.trouble ?? "")", systemImage: "ear.trianglebadge.exclamationmark")
                        .foregroundStyle(SentinelTheme.amber)
                        .font(.callout)
                }
            case .denied(let message), .failed(let message):
                Label(message, systemImage: "mic.slash")
                    .foregroundStyle(SentinelTheme.amber)
                    .font(.callout)
            }
        }
    }

    // What will be added, as it will be saved.
    @ViewBuilder
    private var preview: some View {
        if let added {
            Label(added, systemImage: "checkmark.circle.fill")
                .foregroundStyle(SentinelTheme.green)
                .font(.headline)
        } else if draft.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                Text("Try:").font(.caption.weight(.semibold))
                Text("\u{201C}Add a reminder to take out the trash at 7am tomorrow\u{201D}")
                Text("\u{201C}Note the garage code is 4412\u{201D}")
                Text("\u{201C}Dentist Friday from 2 to 3pm on my calendar\u{201D}")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        } else {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: Self.icon(kind))
                    .font(.title2)
                    .foregroundStyle(SentinelTheme.green)
                    .frame(width: 30)
                VStack(alignment: .leading, spacing: 3) {
                    Text(draft.title).font(.headline)
                    if let when = Self.when(draft) {
                        Text(when).font(.subheadline).foregroundStyle(.secondary)
                    }
                    if !draft.body.isEmpty {
                        Text(draft.body).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(12)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
        }
    }

    private func listen() {
        typing = false
        let fresh = SpeechCapture()
        capture = fresh
        Task { await fresh.start() }
    }

    private func add() {
        guard canAdd else { return }
        let draft = draft
        engine.create(draft.organizerKind, fields: draft.fields(), spaceId: engine.defaultSpaceId)
        // A local create doesn't replan alerts until the next sync; do it now so a reminder's
        // alert is set even if the server can't be reached.
        engine.refreshAlerts()
        added = "Added \u{201C}\(draft.title)\u{201D}" + (Self.when(draft).map { " \u{2013} \($0)" } ?? "")
        Task {
            try? await Task.sleep(for: .seconds(1.2))
            dismiss()
        }
    }

    static func name(_ kind: QuickCapture.Kind) -> String {
        switch kind {
        case .reminder: "Reminder"
        case .note: "Note"
        case .event: "Event"
        }
    }

    static func icon(_ kind: QuickCapture.Kind) -> String {
        switch kind {
        case .reminder: "checklist"
        case .note: "note.text"
        case .event: "calendar"
        }
    }

    // "Tomorrow at 7:00 AM", "Friday at 2:00 PM until 3:00 PM", "October 10, all day".
    static func when(_ draft: QuickCapture.Draft) -> String? {
        guard draft.kind != .note else { return nil }
        guard let start = draft.start else { return "No date" }
        var said = VoiceOrganizer.spoken(start, hasTime: draft.hasTime)
        said = said.prefix(1).uppercased() + said.dropFirst()
        guard draft.kind == .event else { return said }
        if !draft.hasTime { return said + ", all day" }
        guard let end = draft.end else { return said }
        return said + " until " + end.formatted(date: Calendar.current.isDate(end, inSameDayAs: start) ? .omitted : .abbreviated, time: .shortened)
    }
}
