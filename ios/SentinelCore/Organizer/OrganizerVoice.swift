import AVFoundation
import OrganizerKit
import Speech
import SwiftUI

// A reminder said out loud inside the app: the microphone button beside "New reminder". Tap it,
// say "add a reminder to walk the dog tomorrow at 1", and the words go through VoicePhrase - the
// same rules Siri's intent uses - to become a title and a due date. No model or server is
// involved: the date comes out of plain rules, so it is instant and works offline. The person
// sees what was understood before it is added, and can open the editor if a word was misheard.
//
// Recognition itself is the system's (SFSpeechRecognizer). It runs on the device where the
// device can, and otherwise sends the audio to Apple, which Info.plist says.

// A spoken reminder on its way from the voice sheet to the editor.
struct VoiceDraft: Identifiable {
    let id = UUID()
    let phrase: VoicePhrase
}

struct VoiceReminderSheet: View {
    @Environment(OrganizerEngine.self) private var engine
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL
    // The space the list is filtered to, so the reminder lands where the person is looking.
    let spaceId: String?
    // "Edit" hands the draft back; the list opens the editor once this sheet is gone.
    let edit: (VoiceDraft) -> Void

    @State private var capture = SpeechCapture()
    @State private var phrase: VoicePhrase?

    var body: some View {
        VStack(spacing: 20) {
            switch capture.phase {
            case .starting:
                ProgressView()
                Text("Getting ready…").foregroundStyle(.secondary)
            case .listening:
                Image(systemName: "waveform")
                    .font(.system(size: 44))
                    .foregroundStyle(SentinelTheme.green)
                    .symbolEffect(.variableColor.iterative, isActive: true)
                Text(capture.transcript.isEmpty ? "Listening…" : capture.transcript)
                    .font(.title3)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(capture.transcript.isEmpty ? .secondary : .primary)
                Text("Say what to do and when, like \u{201C}call mom tomorrow at 5\u{201D}.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                Button("Done") { capture.finish() }
                    .keyboardShortcut(.defaultAction)
            case .finished:
                if let phrase {
                    Image(systemName: "checkmark.circle")
                        .font(.system(size: 44))
                        .foregroundStyle(SentinelTheme.green)
                    Text(phrase.title)
                        .font(.title2.weight(.semibold))
                        .multilineTextAlignment(.center)
                    Text(phrase.due.map { VoiceOrganizer.spoken($0, hasTime: phrase.hasTime).capitalizedFirst } ?? "No date")
                        .foregroundStyle(.secondary)
                    HStack(spacing: 12) {
                        Button("Try Again") { listenAgain() }
                        Button("Edit…") {
                            edit(VoiceDraft(phrase: phrase))
                            dismiss()
                        }
                        Button("Add") { add(phrase) }
                            .keyboardShortcut(.defaultAction)
                            .buttonStyle(.sentinelProminent)
                            .tint(SentinelTheme.green)
                    }
                } else {
                    Image(systemName: "ear.trianglebadge.exclamationmark")
                        .font(.system(size: 44))
                        .foregroundStyle(SentinelTheme.amber)
                    Text("Didn\u{2019}t catch that.")
                    if let trouble = capture.trouble {
                        Text(trouble)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                    }
                    Button("Try Again") { listenAgain() }
                        .keyboardShortcut(.defaultAction)
                }
            case .denied(let message):
                Image(systemName: "mic.slash")
                    .font(.system(size: 44))
                    .foregroundStyle(SentinelTheme.amber)
                Text(message).multilineTextAlignment(.center)
                Button("Open Privacy Settings") { openURL(Self.privacySettings) }
            case .failed(let message):
                Image(systemName: "exclamationmark.triangle")
                    .font(.system(size: 44))
                    .foregroundStyle(SentinelTheme.amber)
                Text(message).multilineTextAlignment(.center)
                Button("Try Again") { listenAgain() }
            }
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .overlay(alignment: .topTrailing) {
            Button("Cancel") { dismiss() }
                .buttonStyle(.borderless)
                .keyboardShortcut(.cancelAction)
                .padding()
        }
        .task { await capture.start() }
        .onChange(of: capture.phase) { _, phase in
            guard phase == .finished else { return }
            let text = capture.transcript.trimmingCharacters(in: .whitespacesAndNewlines)
            phrase = text.isEmpty ? nil : VoicePhrase.parse(text)
        }
        .onDisappear { capture.stop() }
    }

    private func listenAgain() {
        phrase = nil
        capture = SpeechCapture()
        Task { await capture.start() }
    }

    private func add(_ phrase: VoicePhrase) {
        var fields: [String: JSONValue] = ["title": .string(phrase.title), "completed": false]
        if let due = phrase.dueField() {
            fields["due"] = .string(due)
            fields["allDay"] = .bool(!phrase.hasTime)
        }
        engine.create(.reminder, fields: fields, spaceId: spaceId ?? engine.defaultSpaceId)
        // A local create doesn't replan alerts until the next sync; do it now so the alert is set
        // even if the server can't be reached.
        engine.refreshAlerts()
        dismiss()
    }

    private static var privacySettings: URL {
        #if os(macOS)
        URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_SpeechRecognition")!
        #else
        URL(string: UIApplication.openSettingsURLString)!
        #endif
    }
}

private extension String {
    var capitalizedFirst: String {
        guard let first else { return self }
        return first.uppercased() + dropFirst()
    }
}

// The microphone and the system's speech recognizer, from "start" to a final transcript. It stops
// on its own after a pause once something has been said, after a while if nothing is, or when
// the person taps Done.
@MainActor
@Observable
final class SpeechCapture {
    enum Phase: Equatable {
        case starting
        case listening
        case finished
        case denied(String)
        case failed(String)
    }

    private(set) var phase: Phase = .starting
    private(set) var transcript = ""
    // Why the recognizer stopped, if it stopped on its own - shown when nothing was heard.
    private(set) var trouble: String?

    private let audio = AVAudioEngine()
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private var silence: Task<Void, Never>?
    private var limit: Task<Void, Never>?

    // A pause this long after the last word ends the reminder; nobody says one slower than that.
    private static let pause: Duration = .milliseconds(1800)
    // With nothing said at all, give up after this.
    private static let patience: Duration = .seconds(8)
    private static let longest: Duration = .seconds(30)

    func start() async {
        guard let recognizer = SFSpeechRecognizer(locale: .current) ?? SFSpeechRecognizer(), recognizer.isAvailable else {
            phase = .failed("Speech recognition isn\u{2019}t available on this device right now.")
            return
        }
        // These completions arrive on the system's own queues. Written inside a @MainActor
        // method they would be taken as main-actor code and trap when called elsewhere
        // (dispatch_assert_queue), so each is marked @Sendable and touches nothing of self.
        let speech = await withCheckedContinuation { continuation in
            SFSpeechRecognizer.requestAuthorization { @Sendable status in continuation.resume(returning: status) }
        }
        guard speech == .authorized else {
            phase = .denied("Sentinel Core isn\u{2019}t allowed to use speech recognition.")
            return
        }
        guard await Self.microphoneAllowed() else {
            phase = .denied("Sentinel Core isn\u{2019}t allowed to use the microphone.")
            return
        }
        do {
            try begin(recognizer)
        } catch {
            stop()
            phase = .failed("The microphone couldn\u{2019}t be started: \(error.localizedDescription)")
        }
    }

    private static func microphoneAllowed() async -> Bool {
        #if os(iOS)
        await AVAudioApplication.requestRecordPermission()
        #else
        await AVCaptureDevice.requestAccess(for: .audio)
        #endif
    }

    private func begin(_ recognizer: SFSpeechRecognizer) throws {
        #if os(iOS)
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.record, mode: .measurement, options: .duckOthers)
        try session.setActive(true, options: .notifyOthersOnDeactivation)
        #endif
        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        request.taskHint = .dictation
        self.request = request

        // The tap runs on the audio thread. The request is only ever fed from there and ended
        // from here, which SFSpeechAudioBufferRecognitionRequest is built for.
        nonisolated(unsafe) let feed = request
        let input = audio.inputNode
        input.installTap(onBus: 0, bufferSize: 1024, format: input.outputFormat(forBus: 0)) { @Sendable buffer, _ in
            feed.append(buffer)
        }
        audio.prepare()
        try audio.start()
        phase = .listening

        task = recognizer.recognitionTask(with: request) { @Sendable [weak self] result, error in
            // Only plain values cross to the main actor.
            let text = result?.bestTranscription.formattedString
            let final = result?.isFinal ?? false
            let failure = error?.localizedDescription
            Task { @MainActor in self?.heard(text, final: final, failure: failure) }
        }
        armSilence(after: Self.patience)
        limit = Task { [weak self] in
            try? await Task.sleep(for: Self.longest)
            guard !Task.isCancelled else { return }
            self?.finish()
        }
    }

    private func heard(_ text: String?, final: Bool, failure: String?) {
        guard phase == .listening else { return }
        if let text, !text.isEmpty {
            transcript = text
            armSilence(after: Self.pause)
        }
        if final {
            finish()
        } else if let failure {
            // The recognizer gave up (no speech, no network for the server-side kind, or the
            // session was taken away). Whatever was heard so far still counts.
            trouble = failure
            finish()
        }
    }

    private func armSilence(after wait: Duration) {
        silence?.cancel()
        silence = Task { [weak self] in
            try? await Task.sleep(for: wait)
            guard !Task.isCancelled else { return }
            self?.finish()
        }
    }

    // Done listening: the transcript is whatever was heard by now.
    func finish() {
        guard phase == .listening else { return }
        stop()
        phase = .finished
    }

    func stop() {
        silence?.cancel()
        limit?.cancel()
        silence = nil
        limit = nil
        if audio.isRunning {
            audio.stop()
            audio.inputNode.removeTap(onBus: 0)
        }
        request?.endAudio()
        task?.cancel()
        request = nil
        task = nil
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        #endif
    }
}
