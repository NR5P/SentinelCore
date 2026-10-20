# CarPlay and Android Auto

Sentinel Core on the car screen: Music and Podcasts, the home alarm, Device Hub (MQTT) controls,
scenes and security alerts.

| | Apple CarPlay | Android Auto |
|---|---|---|
| Music & Podcasts | Audio category: Music and Podcasts tabs, Now Playing, Up Next | Media3 `MediaLibraryService` (already shipped) + voice "play X" |
| Alarm, devices, scenes | Driving Task category: Home tab | IoT category: car app (`car/`) |
| Alerts | Alerts tab + notifications allowed in CarPlay | Alerts screen + notifications shown on the car screen |
| Approval needed | **Yes, an entitlement from Apple per category** | No: a Play Console opt-in, then Google's car review |
| Code | `ios/SentinelCore/CarPlay/CarPlaySceneDelegate.swift` | `android/.../car/`, `audio/AudioPlaybackService.kt` |

What the car may do while driving (both platforms' rules agree):

- **Allowed:** see device state, one-tap on/off/open/close/press, run a scene, arm the alarm,
  read recent alerts, and browse and play music and podcasts.
- **Not allowed:** setup of any kind, fine control (brightness, thermostat setpoints), and camera
  images or video. Disarming is left out on purpose: it needs the PIN, and the car has no safe way
  to type one. The car says "When it's safe, use your phone" wherever that applies.

---

## 1. Request the CarPlay entitlements (Apple)

Form: <https://developer.apple.com/contact/carplay/>. Sign in as the Account Holder or an Admin of
team **G4GK5372G8**.

Submit **one request per category**:

1. **Audio**: Music and Podcasts. This is the easy one.
2. **Driving Task**: the alarm, devices, scenes and alerts. Apple reviews these case by case. If
   Apple turns it down, see "If Apple grants only Audio" below.

**Submitted 2026-10-18** with Audio and Driving Task both ticked. The form had no description
field, only checkboxes for the app type, so Apple reviews the app itself. The descriptions below are
kept for a resubmission, or for any follow-up questions Apple asks by email.

**Granted 2026-10-20:** both Audio and Driving Task. Both are ticked on the App ID and both keys are
in `SentinelCore-iOS.entitlements`; the first build to carry them is build 31.

Fields, with text to paste:

- **App name:** Sentinel Core
- **Bundle ID:** com.sentinelcore.client
- **Apple ID of the app:** the numeric ID from App Store Connect → App Information.
- **Is the app available on the App Store?** Answer truthfully. It's currently on TestFlight; give
  the expected release if Apple asks.
- **Category:** Audio for the first request, Driving Task for the second.

**Description for the Audio request:**

> Sentinel Core is a self-hosted home server app. One of its main features is a personal music and
> podcast library: the user's own music collection and podcast subscriptions, streamed from their
> home server or played from on-device downloads. In CarPlay the app uses the audio templates only:
> a tab bar with Music (Shuffle All, Recently Played, Pinned playlists, Playlists, Albums, Artists,
> Mixes, Loved Songs) and Podcasts (Up Next, Continue Listening, New Episodes, Shows), plus the
> system Now Playing template with shuffle, repeat, playback speed and Up Next. Playback, the lock
> screen and remote commands already use AVFoundation, MPNowPlayingInfoCenter and
> MPRemoteCommandCenter. No video, no text entry, and no setup in the car.

**Description for the Driving Task request:**

> Sentinel Core is a home security and smart-home app for a self-hosted server: camera alerts, a
> home alarm, and switches, lights, garage doors and gates connected over MQTT. Its CarPlay Driving
> Task features are the things a driver needs on the way out or the way home, as one-tap actions
> built from list and action-sheet templates:
> - Open or close the garage door or gate, and see whether it is open.
> - Arm the home alarm (Home, Night or Away) and see whether it is armed or going off. Disarming
>   is not offered in the car, because it needs a PIN.
> - Turn lights and outlets on or off, and run a pre-set scene ("Arriving home").
> - Read a text list of recent security alerts. No camera images or video are shown.
> There is no setup, editing, text entry or fine-grained control in the car; everything else sends
> the driver to the phone "when it's safe". Every screen is at most two levels deep.

Apple answers by email. Approval can take from days to weeks, and sometimes there's no reply;
resubmitting after about three weeks is normal.

## 2. When Apple approves

1. In [Certificates, Identifiers & Profiles](https://developer.apple.com/account/resources/identifiers/list),
   open **com.sentinelcore.client** → **Additional Capabilities**. The granted CarPlay
   capabilities appear there. Tick them and save.
2. Regenerate the App Store/TestFlight provisioning profile. Automatic signing in Xcode does this on
   the next archive.
3. Copy the granted keys from `ios/SentinelCore/SentinelCore-iOS-Simulator.entitlements` into
   `ios/SentinelCore/SentinelCore-iOS.entitlements`. Only copy what was granted: a device build
   with an entitlement the profile lacks fails to sign.
4. Ship as usual (bump-build + release.sh).

**If Apple grants only Audio:** set `SentinelCarPlayControls` to `false` in `ios/Info.plist`.
CarPlay then shows only Music and Podcasts. Apple's review rejects templates outside the granted
category.

Security alert notifications already carry `.allowInCarPlay`, so they appear on the car screen
once any CarPlay entitlement is in the profile.

### Testing CarPlay

- **Simulator:** the simulator build already carries both entitlements. Run the app, then use
  Simulator.app → I/O → External Displays → CarPlay. This Mac's Xcode 27 ships without
  Simulator.app, so this hasn't been run here yet.
- **Real iPhone:** after approval, use Apple's *CarPlay Simulator* (Additional Tools for Xcode, on
  developer.apple.com/download) with the iPhone on USB, or a real car.

---

## 3. Android Auto (no request needed)

1. **Play Console** → Sentinel Core → *Test and release* → *Advanced settings* → *Form factors* →
   **+ Add form factor → Android Auto**. Accept the Android Auto terms.
2. Upload a build containing this change. Google reviews the car app against the Car App Quality
   guidelines: media for Music & Podcasts, and IoT (rule IT-1) for the alarm, devices, scenes and
   alerts. The review is separate from the phone review and can take a few days.
3. Android Auto screenshots aren't required for Android Auto (only for Automotive OS).

### Testing on the Desktop Head Unit (DHU)

The DHU is installed on this Mac (`~/Library/Android/sdk/extras/google/auto/desktop-head-unit`). It
needs a phone with the full Android Auto app. The emulator here only has the stub, which needs a
Google sign-in to update.

1. On the phone, open the Android Auto settings (Settings → Connected devices → Android Auto) and
   tap **Version** ten times to unlock developer mode.
2. ⋮ → Developer settings → turn on **Unknown sources**. Without it, a debug build doesn't appear
   in the car. Then ⋮ → **Start head unit server**.
3. Install a debug build (`./gradlew :app:installDebug`), then run:

   ```bash
   adb forward tcp:5277 tcp:5277
   ```

   ```bash
   ~/Library/Android/sdk/extras/google/auto/desktop-head-unit
   ```
4. The car launcher should show **Sentinel Core** (the music app) and **Sentinel Home** (alarm,
   devices, scenes, alerts). This hasn't been checked on a head unit yet.
