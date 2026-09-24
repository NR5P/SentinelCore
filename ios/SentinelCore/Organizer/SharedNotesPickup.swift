import Foundation
import OrganizerKit
#if os(macOS)
import AppKit
#endif

// "Share to Sentinel Core" on the app's side (see SharedNotes in OrganizerKit).
//
// iPhone/iPad: the share extension leaves what was shared in the App Group container; this turns
// it into notes - when the app starts, comes to the front, runs a background refresh, or hears the
// extension's Darwin notification while it's running.
//
// Mac: select text or a link anywhere, then Services > New Sentinel Core Note. The Services menu
// hands it straight to the running app (see NSServices in Info.plist), so there's no extension.
//
// Notes go to the person's own space. Like the voice intents, nothing is made until this device
// has synced and knows that space; on the iPhone the share just waits in the container till then.
@MainActor
enum SharedNotesPickup {
    private static var engine: OrganizerEngine?
    private static var organizerIsOn: () -> Bool = { false }
    private static var log: (String) -> Void = { _ in }

    static func attach(_ engine: OrganizerEngine, organizerIsOn: @escaping () -> Bool, log: @escaping (String) -> Void) {
        Self.engine = engine
        Self.organizerIsOn = organizerIsOn
        Self.log = log
        #if os(iOS)
        CFNotificationCenterAddObserver(
            CFNotificationCenterGetDarwinNotifyCenter(), nil,
            { _, _, _, _, _ in Task { @MainActor in SharedNotesPickup.pickUp() } },
            SharedNoteInbox.posted as CFString, nil, .deliverImmediately)
        pickUp()
        #endif
    }

    #if os(macOS)
    // From applicationDidFinishLaunching: a Services request that launched the app arrives right
    // after it, so the provider must be in place by then.
    static func registerServices() {
        NSApplication.shared.servicesProvider = ServicesProvider.shared
        NSUpdateDynamicServices()
        _ = AppModel.shared
    }
    #endif

    // Turns everything waiting into notes, if they can go somewhere private.
    @discardableResult
    static func pickUp() -> Int {
        #if os(iOS)
        guard let engine, ready(engine), let inbox = SharedNoteInbox(), !inbox.isEmpty else { return 0 }
        let notes = inbox.take()
        for note in notes { add(note, to: engine) }
        if !notes.isEmpty { log("Added \(notes.count) shared note\(notes.count == 1 ? "" : "s")") }
        return notes.count
        #else
        return 0
        #endif
    }

    private static func ready(_ engine: OrganizerEngine) -> Bool {
        organizerIsOn() && engine.knowsSpaces
    }

    private static func add(_ note: SharedNote, to engine: OrganizerEngine) {
        guard !note.isEmpty else { return }
        engine.create(.note, fields: note.fields, spaceId: engine.defaultSpaceId)
    }

    #if os(macOS)
    // Services > New Sentinel Core Note. The selector matches NSMessage in Info.plist. AppKit calls
    // service providers on the main thread.
    @MainActor
    final class ServicesProvider: NSObject {
        static let shared = ServicesProvider()

        @objc func newSentinelCoreNote(_ pasteboard: NSPasteboard, userData: String?, error: AutoreleasingUnsafeMutablePointer<NSString?>) {
            guard let engine = SharedNotesPickup.engine else { return }
            guard SharedNotesPickup.ready(engine) else {
                error.pointee = "Sentinel Core hasn't synced your Organizer yet. Open it while it can reach your server, then try again." as NSString
                return
            }
            let note = SharedNote(text: pasteboard.string(forType: .string) ?? "", url: pasteboard.string(forType: .URL))
            guard !note.isEmpty else { return }
            SharedNotesPickup.add(note, to: engine)
            SharedNotesPickup.log("Added a note from the Services menu")
        }
    }
    #endif
}
