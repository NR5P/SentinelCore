# Settings parity audit (2026-10-18)

Rule (Nathan, 2026-10-18): every setting or setup flow must be available from every front end (web, iPhone, Mac, Android, Windows; tvOS shows status where sensible).

This lists only what goes **beyond plain schema fields**. Schema-driven sections (including `ExtraAction` buttons such as test email/Telegram/webhook/weather alert, scan now, refresh now, check now, export now, back up now, re-run wizard; `Lookup`; `OptionsSource`) render on iPhone/Mac/Android/Windows through ServerSettingsView / ServerSettingsScreen / ServerSettingsWindow and are not repeated. The Family Files library location is excluded (another session).

Method: compared `core/SentinelCore.Web/wwwroot/*.js` and `SettingsSchema.cs` against every `/api/...` path the native sources call, then spot-read the likely candidates. "None" means no code path found. "Present" means a screen or client call exists; I did not run each app. Sizes are rough: S under a day, M a day or two per surface, L several days per surface.

## Gaps, most important first

| # | Setting / flow | Web location | iPhone/Mac | Android | Windows | tvOS | Gap | Size |
|---|---|---|---|---|---|---|---|---|
| 1 | **Automation rules** (when a camera sees X, publish custom MQTT or call an API endpoint; test button) | `automations.js`, `/api/automation-rules`, `/metadata`, `/{id}/test` | None | None | None | None | Whole editor and test flow missing. This is the generalized MQTT/Webhook feature, and it is a rule-builder UI, not schema fields. | L |
| 2 | **Full backup bundle** (passphrase, certificates, HomeKit identity, "replacement server" restore, auto-export status) | Settings → Backup, `settings.js` `renderBackupSection`, `/api/backup/bundle`, `/api/backup/auto/status` | Only settings-only export/import (`/api/backup/export`, `/import`) | Same | Same | None | Cannot build or restore the schema-4 bundle, so disaster recovery needs a browser. No nightly auto-export status either; only the "export now" button works. | M |
| 3 | **Automatic updates panel** (current/latest build, last check, install now) | Settings → Automatic updates, `buildAutoUpdatePanel`, `/api/updates/status`, `/check`, `/install` | Schema fields and "check now" only | Same | Same | None | No status line and no Install now button. Users can turn updates on but cannot see or trigger the result. | S–M |
| 4 | **Camera groups** (create, edit, order) | Cameras admin, `cameras.js`, `/api/camera-groups` | None (only counted in the restore summary) | None | None | None | Groups are created and edited on web only. | M |
| 5 | **Gate checks** (per-camera "is the gate open/closed" check) | `cameras.js`, `/api/gate-checks`, `/{cameraId}` | None | None | None | None | Web only. | M |
| 6 | **Schedule weather tests** ("only if", "wait for", "check the weather now") | Schedules, `schedules.js` `weatherTests`, `/api/schedules/weather-check` | Schedule editor has no weather section | None | None | None | Built 2026-10-17 on web only. Schedules saved with weather tests may also lose them if a native editor round-trips the schedule (verify). | M |
| 7 | **Recording-schedule preview** | `schedules.js`, `/api/recording-schedules/preview` | None | None | None | None | Preview of when a recording schedule runs is web only. | S |
| 8 | **Import a list** (Spotify/Letterboxd/IMDb CSV/JSON → bulk downloads; pause, retry, remove) | `media-import.js`, `/api/media/import/preview`, `/imports...` | None | None | None | None | Whole import wizard and progress screen missing on Movies and Music. | L |
| 9 | **Setup wizard** (first-run steps, generate API key, vision status, complete) | `wizard.js`, `/api/setup/status`, `/complete`, `/generate-api-key`, `/vision-status` | None. "Re-run setup wizard" button exists but the wizard it triggers is web only. | Same | Same | None | A fresh install cannot be configured from an app. The reset button leads somewhere only a browser can follow. | L |
| 10 | **HomeKit pairing code / QR** | Settings → HomeKit, `renderHomeKitSection`, `/api/homekit/qr` | Status, switches, reset pairing; no code or QR found | Same | Same (`HomeKitWindow`) | None | Adding the server to Apple Home needs the setup code. Show the QR or code in each app (verify nothing shows it today). | S |
| 11 | **Local addresses panel** (which addresses the server answers on) | Settings → Server address, `buildLocalAddressPanel`, `/api/system/addresses` | None | None | None | None | Read-only list with copy; web only. | S |
| 12 | **Android: speaker network computers** (add, discover, remove) | Music → speakers, `audio-player.js`, `/api/audio/network`, `/discover` | Present (`AudioClient.swift`) | None | Present, but no `/discover` | Present on tvOS (speakers view) | Android lacks it entirely. Windows lacks only the discover call. | S–M |
| 13 | **Change sign-in email** (self-service) | `accounts.js`, `/api/accounts/me/email` | Account admin screen exists; self-service email change not found | Same | Same | None | Verify; change password exists everywhere. | S |
| 14 | **Reorder alarm sensors** | `alarm.js`, `/api/alarm/sensors/order` | None | None | None | None | Minor. | S |
| 15 | **iOS/Mac: install the server CA certificate** | `publisher.js`, `/api/certificate-authority` | None | Present | Present | None | Android and Windows can download it; iPhone/Mac cannot. Matters mainly for phone-as-camera publishing over HTTPS. | S |
| 16 | **Windows: music/podcast library admin calls** (scan, refresh, status, fill, recommended) | `music.js`, `podcasts.js` | Present | Present | Missing as direct calls. The schema "scan now" / "refresh now" buttons cover the admin side. | None | Probably fine; confirm no podcast or music admin flow is stranded. | S |
| 17 | **Kiosk-only settings** (screen sleep schedule, on-screen keyboard, TMDB search UI) | `kiosk-*.js`, `/api/kiosk/screen-sleep`, `/api/media/tmdb/search` | Kiosk fields via schema | Same | Same | n/a | Kiosk screen is web only by design. Settings fields are reachable via the schema. No action needed unless the kiosk gets a non-schema setting. | none |
| 18 | **Exposure banner / external-access evidence** | `exposure.js`, `/api/exposure/status` | None | None | None | None | Informational banner, not a setting. Skip unless wanted. | S |

## Checked and already at parity

Present on iPhone/Mac, Android and Windows: Account admin, license, Known Faces and Vehicles, Prompt Profiles, Camera admin/edit/discovery/zone editor, Device Hub (edit, discovery), Alarm settings and sensors, Family Files drives and trash, Media Libraries admin, Movie download sites and VPN sign-in, Podcast and Music audio settings, Host reboot panel, Restart-service buttons, System status, Push pairing, Dashboard login enable/disable/change password, Board automations and the Reminders/Calendar planner rules (the planner screen is present in all three), HomeKit admin (without the QR). Apple TV: status and a subset of browse/admin (Device Hub, libraries admin, downloads, wake alarms, speakers, audio settings); the gaps above show "None" for tvOS and few need it.

## Not verified

- No app was run. Everything above comes from reading source and grepping API paths. A feature using a different route string than the web would show up as a false gap; the entries I'm least sure of are 10, 13 and 16.
- Windows UI code compiles on Windows only.
- I did not check whether native schedule editors drop `weatherTests` when saving (see 6).

## Suggested order

1 (automation rules), 2 (backup bundle), 3 (updates), 9 (wizard), 6 (schedule weather), 8 (import), then the S items in one sweep.
