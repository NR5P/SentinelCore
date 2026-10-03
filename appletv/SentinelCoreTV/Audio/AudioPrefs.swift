import Foundation
import UIKit

// This Apple TV's own Music & Podcasts preferences. Kept in UserDefaults (the only storage tvOS
// promises to keep); none of it is the server's business except through the speaker heartbeat.
enum AudioPrefs {
    private static var defaults: UserDefaults { .standard }

    // ReplayGain levelling ("Sound Check"), on unless switched off.
    static var soundCheck: Bool {
        get { defaults.object(forKey: "audio.soundCheck") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "audio.soundCheck") }
    }

    // The speed a show plays at when its own setting is "use the app's default".
    static var podcastSpeed: Double {
        get { defaults.object(forKey: "audio.podcastSpeed") as? Double ?? 1.0 }
        set { defaults.set(newValue, forKey: "audio.podcastSpeed") }
    }

    // "Let others play here": the TV is a speaker unless switched off.
    static var speakerEnabled: Bool {
        get { defaults.object(forKey: "audio.speakerEnabled") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "audio.speakerEnabled") }
    }

    static var allowAnnouncements: Bool {
        get { defaults.object(forKey: "audio.allowAnnouncements") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "audio.allowAnnouncements") }
    }

    // What phones see in "Play on…". Starts as the TV's own name; tvOS answers "Apple TV" to apps
    // without the user-assigned-name entitlement, so it can be renamed in Settings.
    @MainActor static var speakerName: String {
        get {
            if let name = defaults.string(forKey: "audio.speakerName"), !name.trimmingCharacters(in: .whitespaces).isEmpty {
                return name
            }
            return UIDevice.current.name
        }
        set { defaults.set(newValue.trimmingCharacters(in: .whitespacesAndNewlines), forKey: "audio.speakerName") }
    }

    static var volume: Double {
        get { defaults.object(forKey: "audio.volume") as? Double ?? 1.0 }
        set { defaults.set(min(1, max(0, newValue)), forKey: "audio.volume") }
    }

    // The same stable id the Organizer uses ("tv-1a2b3c4d") - one id per Apple TV.
    @MainActor static var deviceId: String { OrganizerDevice.id }

    static var musicEnabled: Bool {
        get { defaults.bool(forKey: "audio.musicEnabled") }
        set { defaults.set(newValue, forKey: "audio.musicEnabled") }
    }

    static var podcastsEnabled: Bool {
        get { defaults.bool(forKey: "audio.podcastsEnabled") }
        set { defaults.set(newValue, forKey: "audio.podcastsEnabled") }
    }
}
