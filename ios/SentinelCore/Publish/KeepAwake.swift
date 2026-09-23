import Foundation
#if os(iOS)
import UIKit
#endif

// Everything the app itself can do to keep this device streaming unattended, and plain words
// for the rest - because on every platform there is a rest. An app can stop the OS from idling
// into sleep; it cannot stop a closed lid, a power button, or a battery running out.
//
// Held while DevicePublisher has a target, released when it stops. Idempotent both ways.
@MainActor
enum KeepAwake {
    #if os(macOS)
    private static var activity: NSObjectProtocol?
    #endif

    static func hold() {
        #if os(iOS)
        // The idle timer is what would otherwise lock the screen a minute in - and iOS suspends
        // the camera the moment the screen locks. Sleep in Settings → Display → Auto-Lock is not
        // consulted while this is set, so nothing there needs changing.
        UIApplication.shared.isIdleTimerDisabled = true
        #elseif os(macOS)
        guard activity == nil else { return }
        // Idle *system* sleep only. The display is allowed to sleep: the camera keeps capturing
        // with the screen dark, and a Mac left as a camera for a week has no reason to light a
        // room all night. Lid sleep is outside any assertion's reach.
        activity = ProcessInfo.processInfo.beginActivity(
            options: [.idleSystemSleepDisabled, .userInitiated],
            reason: "Streaming this Mac as a Sentinel Core camera")
        #endif
    }

    static func release() {
        #if os(iOS)
        UIApplication.shared.isIdleTimerDisabled = false
        #elseif os(macOS)
        if let activity { ProcessInfo.processInfo.endActivity(activity) }
        activity = nil
        #endif
    }

    // What the app is doing on this platform, and what the person still has to. Shown next to
    // the stream they are being asked to leave running.
    static var handled: [String] {
        #if os(iOS)
        [
            "Auto-Lock is off while streaming: this screen stays on, so nothing under Settings → Display needs changing.",
            "If the app is sent to the background or the screen locks, iOS takes the camera away. Streaming pauses, and picks straight back up when the app is in front again.",
            "If the app is quit or the phone restarts, streaming resumes on its own the next time the app is opened."
        ]
        #else
        [
            "This Mac is held out of idle sleep while streaming. The display may still go dark, and that is fine: the camera keeps going.",
            "The app can be in the background or hidden; only quitting it stops the stream. If it is quit or the Mac restarts, streaming resumes the next time the app is opened."
        ]
        #endif
    }

    static var todo: [String] {
        #if os(iOS)
        [
            "Plug it in. A phone streaming video all day will not last on its battery.",
            "Leave Sentinel Core in front. Use \"Dim screen\" below so the screen stays on without lighting the room, and turn off Low Power Mode, which dims and throttles it.",
            "Optional: Settings → Accessibility → Guided Access, then triple-click the side button on this screen, so a tap can't leave the app."
        ]
        #else
        [
            "Plug it in, and if it is a laptop keep the lid open - closing the lid sleeps the Mac and stops the camera regardless.",
            "So the Mac stays up when the display sleeps: System Settings → Battery (or Energy) → Options → Prevent automatic sleeping on power adapter when the display is off.",
            "Don't quit Sentinel Core. Hiding it or minimizing its window is fine."
        ]
        #endif
    }
}
