# Music & Podcasts: the audio API

Two tabs built into SentinelCore, on the dashboard and in every app (iPhone/iPad, Mac, Apple TV,
Android, Windows):

- **Music** plays the household's own music files: folders on the server or on another computer's
  share. The server reads the tags and cover art, fetches missing art and lyrics if allowed, and
  streams each track in a format the device can play.
- **Podcasts** subscribes to RSS feeds. The server checks the feeds, downloads episodes onto its
  own disk, and keeps them after a show drops them from its feed. It can also make episodes louder
  and clearer, cut long silences, and write transcripts.

The two tabs also share some features:

- **Play where you left off:** the queue, play position and speed follow a person to any device.
- **Speakers:** a kiosk, Apple TV or open app can be played to and controlled from another
  device.
- **Wake-up alarms** start music or a podcast on a speaker at a set time.
- **Morning briefing:** today's weather, calendar and reminders are read out, then news episodes
  play.
- **Listening stats** and a year-in-review "Wrapped".

All routes need a signed-in user, unless marked **admin** (Admin role only). JSON is camelCase,
enums are lower-case strings, and dates are ISO-8601 UTC. Errors are `{ "error": "…" }` or
`{ "errors": ["…"] }`.

- `/api/music/*` routes answer **404** `{ "error": "Music is turned off in Settings." }` when
  Music is off.
- `/api/podcasts/*` routes answer **404** `{ "error": "Podcasts are turned off in Settings." }`
  when Podcasts are off.
- `/api/audio/*` routes work when either tab is on, and return 404 only when both are off.
- `/api/audio/enabled` always answers.

Ids are GUIDs and stay the same across rescans:

- **Track:** made from the library id plus the file's path inside the library.
- **Album:** made from the normalized album artist plus the album title.
- **Artist:** made from the normalized artist name.
- **Episode:** made from the podcast id plus the feed's `<guid>` (or the enclosure URL if there
  is no `<guid>`).

Play history, ratings, loves, playlists and podcast progress therefore survive a rescan and a
backup restore.

"User key" means the lower-cased username. When login is off, the empty string is used and the
household shares one view, the same as Movies and the Organizer.

---

## 1. Feature flags, settings and folders

- `GET /api/audio/enabled` → `{ "music": true, "podcasts": true }`. It answers even when both
  are off. If an older server answers 404, treat both as off. Apps show each tab only when its
  flag is true.

### Settings sections

These are rendered by the existing schema-driven settings screens: `/api/settings/schema` lists
them, and each is saved with `PUT /api/settings/music` or `PUT /api/settings/podcasts`.

- **`music`** (title **MUSIC**):
  - `enabled`
  - `rescanHours` (0–168, default 12)
  - `fetchArtworkOnline` (MusicBrainz + Cover Art Archive for albums with no art; default true)
  - `fetchLyricsOnline` (LRCLIB for tracks with no lyrics; default true)
  - `transcodeBitrate` (96–320 kbps, default 256: used when a file is converted for a device, or
    when a device asks for data-saver quality)
  - `pauseSpeakersOnAlarm` (default true)
  - `announceOnSpeakers` (camera person/vehicle alerts are spoken on speakers that allow
    announcements; default false)
- **`podcasts`** (title **PODCASTS**):
  - `enabled`
  - `downloadFolder` (blank = `<data>/podcasts`; set from the picker in §1)
  - `refreshMinutes` (15–1440, default 60)
  - `autoDownloadNew` (default for new subscriptions, default true)
  - `keepEpisodes` (default per show, 0 = all, default 10)
  - `deletePlayedAfterDays` (0 = never, default 7)
  - `maxStorageGb` (0 = no limit, default 50)
  - `directoryCountry` (two-letter store country for search and Top Shows, default `us`)
  - `transcriptsEnabled` (writes transcripts on this server with whisper.cpp; default false)
  - `whisperCommand` (default `whisper-cli`)
  - `whisperModel` (path to a `ggml-*.bin` model)
  - `whisperThreads` (1–32, default 4)

The Music and Podcasts settings screens in each app show those fields plus a hand-built part:

- **Settings → MUSIC, library folders:** the list of library folders (§2), with Add (folder picker), Rename,
  Remove, Scan, and the scan status.
- **Podcasts → Download folder:** the `downloadFolder` field with a "Choose…" button that opens
  the folder picker, plus a storage line from `/api/podcasts/status` (§9). There is also
  OPML import/export.

### Folder picker (admin)

`GET /api/audio/browse?path=` has the same shape and rules as `GET /api/media/browse` (see
`MOVIES.md` → Folder picker). There is one difference: each folder's `videoCount` and the
response's `videoCount` count **audio files** (mp3, m4a, m4b, aac, flac, alac, ogg, oga, opus,
wav, aif, aiff, wma, ape, wv, dsf, dff). `audioCount` carries the same number. Apps can reuse
their Movies picker and point it at this route. `POST /api/audio/browse/folder` is the same
"New folder" call as `POST /api/media/browse/folder`, answering with an audio browse page.

Network computers have the same routes under `/api/audio/network`:

- `GET /api/audio/network`
- `POST /api/audio/network`
- `PUT /api/audio/network/{id}`
- `DELETE /api/audio/network/{id}`
- `GET /api/audio/network/discover`

The bodies and replies are identical to `/api/media/network*`, and the sign-ins are the same
saved list, so a computer signed in to for Movies already works here.

---

## 2. Music libraries (admin, except GET)

- `GET /api/music/status` →
  ```json
  { "enabled": true, "ffmpegAvailable": true, "scanning": false,
    "scan": { "phase": "files|tags|artwork|lyrics", "libraryName": "Music", "processed": 120, "total": 4000, "startedAt": "…" },
    "libraries": [ MusicLibrary ],
    "trackCount": 8123, "albumCount": 702, "artistCount": 410,
    "totalDurationSeconds": 2000000, "totalBytes": 61000000000,
    "lastScanFinishedAt": "…", "lastScanError": null }
  ```
  `MusicLibrary = { "id", "name", "path", "network": false, "trackCount", "lastScanFinishedAt", "lastScanError", "createdAt" }`.
- `GET /api/music/libraries` → `[ MusicLibrary ]`.
- `POST /api/music/libraries` with body `{ "name": "Music", "path": "/mnt/music" }` → 201
  `MusicLibrary`, and starts a scan. Errors are 400 `{ errors }` (no name; folder missing or
  unreadable; already used by another music library).
- `PUT /api/music/libraries/{id}` with the same body → `MusicLibrary`. Changing the path rescans.
- `DELETE /api/music/libraries/{id}` → 204. The library's tracks are forgotten; history,
  playlists and loves for them are kept in case the folder comes back.
- `POST /api/music/libraries/{id}/scan` → 202. `POST /api/music/scan` → 202 for every library.
  Either returns 409 if a scan is already running.

### Scanning rules

- Hidden folders and `$RECYCLE.BIN` are skipped, along with files under 16 KB.
- Tags are read from the file: ID3v1/v2, Vorbis comments, MP4 atoms, APE and ASF. Tracks with no
  tags get the title from the file name (`01 - Song.mp3` → track 1, "Song"), the album from the
  folder, and the artist from the parent folder.
- `albumArtist` falls back to `artist`. An album whose tracks have three or more different
  artists and no album artist becomes **Various Artists** (`compilation: true`).
- Artists split on `;`, ` feat. `, ` ft. ` and ` & ` (but never inside a known band name such as
  "Simon & Garfunkel", which stays whole when it is the album artist).
- Album art, in order of preference: the embedded picture of the first track; `cover.jpg`,
  `folder.jpg`, `front.jpg` or `album.jpg` (png too) in the album folder; then Cover Art Archive
  if `fetchArtworkOnline`.
- Artist images: `artist.jpg` in the artist folder. Without one, `artUrl` is the art of the
  artist's newest album.
- Lyrics, in order of preference: a sidecar `<file>.lrc` (synced); embedded lyrics (USLT or the
  LYRICS tag, synced when they contain `[mm:ss.xx]` stamps); then LRCLIB on first request if
  `fetchLyricsOnline`.
- ReplayGain tags (`REPLAYGAIN_TRACK_GAIN` etc., or iTunes `iTunNORM` is ignored) are passed to
  clients.

---

## 3. Music objects

```json
Track = {
  "id": "…", "title": "Dreams", "artist": "Fleetwood Mac", "artists": ["Fleetwood Mac"],
  "artistId": "…", "albumArtist": "Fleetwood Mac", "album": "Rumours", "albumId": "…",
  "trackNumber": 2, "discNumber": 1, "year": 1977, "genres": ["Rock"],
  "durationSeconds": 257.8, "codec": "flac", "bitrate": 900000, "sampleRate": 44100, "bitDepth": 16,
  "channels": 2, "sizeBytes": 29000000, "composer": null, "bpm": null,
  "replayGain": { "trackGain": -6.2, "trackPeak": 0.98, "albumGain": -7.1, "albumPeak": 1.0 },
  "hasLyrics": true,
  "artUrl": "/api/music/albums/{albumId}/art",
  "streamUrl": "/api/music/tracks/{id}/stream",
  "addedAt": "…",
  "loved": false, "rating": 0, "playCount": 12, "lastPlayedAt": "…",
  "path": "Fleetwood Mac/Rumours/02 Dreams.flac (admin only, else null)"
}
```

- `loved`, `rating` (0–5, 0 = unrated), `playCount` and `lastPlayedAt` are **the current
  user's**.
- `replayGain` is null when the file has none, and each field inside it can be null.
- `hasLyrics` is true when lyrics are known to exist (sidecar or embedded). False can still find
  lyrics online.

```json
Album = { "id", "title": "Rumours", "artist": "Fleetwood Mac", "artistId": "…", "year": 1977,
          "genres": ["Rock"], "trackCount": 11, "discCount": 1, "durationSeconds": 2399,
          "artUrl": "/api/music/albums/{id}/art", "addedAt": "…", "compilation": false,
          "loved": false, "rating": 0, "playCount": 40, "lastPlayedAt": "…" }
```

`playCount` on an album is the user's plays of its tracks.

```json
Artist = { "id", "name": "Fleetwood Mac", "albumCount": 3, "trackCount": 38, "genres": ["Rock"],
           "artUrl": "/api/music/artists/{id}/art", "loved": false, "playCount": 120 }
```

```json
Playlist = { "id", "name": "Road Trip", "description": "", "ownerName": "morgan", "mine": true,
              "shared": true, "collaborative": false, "smart": false, "rules": null,
              "trackCount": 42, "durationSeconds": 9000,
              "artUrls": ["/api/music/albums/…/art", "…"], "createdAt": "…", "updatedAt": "…", "pinned": false }
```

- `artUrls` holds up to 4 distinct album arts, for a 2×2 mosaic (use one if there is only one).
- `shared`: everyone in the household can see and play it.
- `collaborative`: everyone can also add, remove and reorder its tracks.
- `pinned`: whether the current user pinned it to the top of Music Home.

`rules` (smart playlists only):

```json
{ "match": "all|any",
  "rules": [ { "field": "genre", "op": "is", "value": "Rock" },
             { "field": "rating", "op": "gte", "value": 4 },
             { "field": "lastPlayed", "op": "notInLast", "value": 90 } ],
  "sort": "random|title|artist|album|year|added|plays|lastPlayed|rating",
  "order": "asc|desc", "limit": 100 }
```

Fields, with the ops each accepts:

| Field | Ops |
| --- | --- |
| `title`, `artist`, `album`, `genre`, `composer` (text) | `is`, `isNot`, `contains`, `notContains` |
| `year`, `rating`, `playCount`, `durationSeconds`, `bpm` | `is`, `gte`, `lte` |
| `loved` (bool) | `is` |
| `added`, `lastPlayed` (days) | `inLast`, `notInLast` |
| `codec` | `is`, `isNot` |
| `library` (library id) | `is`, `isNot` |

`lastPlayed notInLast N` also matches tracks never played. `limit` is 0 for no limit (capped at
5000). A smart playlist's tracks are worked out fresh each time it is opened.

---

## 4. Music routes

### Home

`GET /api/music/home` →
```json
{
  "pinned": [ Playlist ],
  "recentlyPlayed": [ { "kind": "album|playlist|artist|mix", "album"?: Album, "playlist"?: Playlist, "artist"?: Artist, "mix"?: Mix } ],
  "recentlyAdded": [ Album ],
  "heavyRotation": [ Album ],
  "mixes": [ Mix ],
  "forgottenFavorites": [ Track ],
  "onThisDay": { "label": "October 2, 2025", "tracks": [ Track ] } | null,
  "discover": [ Album ],
  "lovedTracks": [ Track ],
  "decades": [ { "decade": 1990, "label": "90s", "trackCount": 812 } ],
  "trackCount": 8123, "albumCount": 702, "artistCount": 410
}
```

- `heavyRotation`: the user's most played albums in the last 30 days.
- `forgottenFavorites`: tracks the user played 5+ times or loved, but not in the last 90 days.
- `onThisDay`: what the user played on this date in an earlier year, from the most recent such
  year.
- `discover`: albums the user has never played, with a daily shuffle.
- Rows hold 20 items at most, and empty rows are omitted from screens.

Suggested order: Pinned, Recently played, Mixes, Recently added, Heavy rotation, On this day,
Forgotten favorites, Discover, Loved, Decades.

```json
Mix = { "id": "daily-1", "name": "Daily Mix 1", "description": "Rock, Classic Rock and more",
        "artUrls": ["…"], "seedType": "genre|artist|decade|loved|discover", "seedId": "rock" }
```

Mixes (`daily-1`…`daily-4` cluster the user's top genres; also `decade-1990`, `loved`,
`discover`) are played with `GET /api/music/mix?mix={id}`.

### Browse and search

All list routes take `offset` (default 0) and `limit` (default 100, max 1000), and return
`{ "items": [ … ], "total": N }`.

- `GET /api/music/artists?q=&genre=&sort=name|plays|added&order=` → Artist list.
  `albumArtistsOnly=true` hides artists who only appear on other artists' albums.
- `GET /api/music/albums?q=&artistId=&genre=&year=&decade=1990&sort=title|artist|year|added|plays|rating&order=` → Album list.
- `GET /api/music/tracks?q=&artistId=&albumId=&genre=&decade=&loved=true&minRating=&sort=title|artist|album|year|added|plays|lastPlayed|rating|duration&order=` → Track list.
- `GET /api/music/genres` → `[ { "name": "Rock", "trackCount": 900, "albumCount": 80, "artUrls": ["…"] } ]`.
- `GET /api/music/artists/{id}` → `Artist` plus:
  - `albums: [Album]`, the artist's own albums, newest first
  - `appearsOn: [Album]`
  - `topTracks: [Track]`, the 10 most played by the household
  - `similar: [Artist]`, up to 8 by shared genres
- `GET /api/music/albums/{id}` → `Album` plus:
  - `tracks: [Track]`, in disc then track order
  - `moreByArtist: [Album]`
  - `totalBytes`
  - `formats: ["flac 16/44.1"]`
- `GET /api/music/search?q=` →
  `{ "artists": [Artist], "albums": [Album], "tracks": [Track], "playlists": [Playlist], "genres": [ "…" ] }`,
  up to 8 artists, 12 albums, 25 tracks and 8 playlists. The search matches the start of words,
  and is case- and accent-insensitive.
- `GET /api/music/folders?libraryId=&path=` →
  `{ "libraryId", "path": "Fleetwood Mac", "parent": "", "folders": [ { "name", "path", "trackCount" } ], "tracks": [Track] }`.
  This browses by folder. No `libraryId` lists the libraries as folders.

### Art and lyrics

- `GET /api/music/albums/{id}/art?size=600` and `GET /api/music/artists/{id}/art?size=600`
  return `image/jpeg` (or png), cached for a day, or 404 when there is no art.
  `GET /api/music/tracks/{id}/art` is the track's album art. `size` (64–1200) asks for a
  downscaled copy; the server may return the original if it cannot resize.
- `GET /api/music/tracks/{id}/lyrics` →
  `{ "synced": true, "lines": [ { "time": 12.3, "text": "Now here you go again" } ], "plain": "…", "source": "sidecar|embedded|lrclib" }`.
  When unsynced, `lines` have `time: null`. Returns 404 `{ error }` when none are found. A 404
  from the online lookup is cached for 30 days.

### Streaming

`GET /api/music/tracks/{id}/stream?codecs=mp3,aac,alac,flac,wav&maxBitrate=` returns the audio
itself, with HTTP range support, so the URL can go straight into a player or a download.

- `codecs` lists what the device decodes natively:

  | Surface | `codecs` |
  | --- | --- |
  | Apple | `mp3,aac,alac,flac,wav,aiff` |
  | Android | `mp3,aac,flac,alac,opus,vorbis,wav` |
  | Web and Windows (Chromium) | `mp3,aac,flac,opus,vorbis,wav` |

  It defaults to `mp3,aac`.
- `maxBitrate` (kbps) is for data saver. If the original exceeds it, the track is converted.
- When the original codec is listed and under `maxBitrate`, the original file is served.
  Otherwise the server converts it once to AAC in an `.m4a` (at `maxBitrate`, or Settings'
  `transcodeBitrate`), keeps it in a cache (`<data>/audio-cache/`, newest 5 GB), and serves that.
  The first request for a converted track takes a second or two.
- Response headers: `X-Audio-Codec` (what is being sent) and `X-Audio-Transcoded: true|false`.
- `GET /api/music/tracks/{id}/download?codecs=…&maxBitrate=` is the same, with a
  `Content-Disposition` attachment name. Apps use it to save tracks for offline listening.

### Playing, loving, rating

- `POST /api/music/tracks/{id}/played` with body
  `{ "secondsListened": 180, "completed": true, "skipped": false, "playedAt": "…"?, "deviceName": "Kitchen" }`
  → 204.
  - Send it when a track ends or is skipped.
  - A **play** counts when `secondsListened ≥ 30` and (≥ 50% of the track, or ≥ 240 s).
  - A track ended inside its first 30 s with `skipped: true` counts as a **skip**.
  - Plays feed Home, Mixes, Stats, smart playlists and ListenBrainz.
  - `playedAt` defaults to now; offline plays send the real time. Duplicate reports (same user,
    track and `playedAt` within 5 s) are ignored.
- `POST /api/music/now-playing-track` with body `{ "trackId" }` → 204. This is optional; it
  updates ListenBrainz's "playing now".
- `POST /api/music/tracks/{id}/love`, `POST /api/music/albums/{id}/love` and
  `POST /api/music/artists/{id}/love` take `{ "loved": true }` and return `{ "loved": true }`.
- `POST /api/music/tracks/{id}/rating` and `POST /api/music/albums/{id}/rating` take
  `{ "rating": 0-5 }` and return `{ "rating": 4 }`.

### Mixes and radio

`GET /api/music/mix?seedType=track|album|artist|genre|playlist|decade&seedId=…&limit=50`, or
`?mix=daily-1` →
`{ "name": "Dreams Radio", "tracks": [Track] }`.

- The web dashboard starts radio from a track, album, artist or genre only; `playlist` and
  `decade` seeds are part of the contract but nothing on the web asks for them.
- The seed comes first (for a track), then a household-library radio. The radio scores tracks by
  shared artist, genre and decade, the user's loves and ratings, and how often the user plays a
  track versus skips it. A pinch of randomness is added.
- No artist appears twice within 3 tracks, and nothing played in the last hour is included.
- Ask again with `excludeIds=a,b,c` to extend an endless radio.
- `GET /api/music/shuffle?limit=200` is a "Shuffle all" queue weighted away from recent plays
  and skips.

### Playlists

- `GET /api/music/playlists` → `[Playlist]`: the current user's, plus everyone's shared ones.
- `POST /api/music/playlists` with body
  `{ "name", "description"?, "shared"?: false, "collaborative"?: false, "rules"?: SmartRules, "trackIds"?: [] }`
  → 201 `Playlist`.
- `GET /api/music/playlists/{id}` → `Playlist` plus `tracks: [Track]`. Each track also carries
  `entryId`, which tells duplicates apart.
- `PUT /api/music/playlists/{id}` takes the same body without `trackIds` → `Playlist`. Only the
  owner may change it, and admins.
- `DELETE /api/music/playlists/{id}` → 204 (owner/admin).
- `POST /api/music/playlists/{id}/tracks` with body `{ "trackIds": [..], "position": null }` →
  `Playlist`. `null` appends. Allowed for the owner, admins, or anyone if the playlist is
  collaborative.
- `DELETE /api/music/playlists/{id}/tracks/{entryId}` → 204.
- `PUT /api/music/playlists/{id}/order` with body `{ "entryIds": [..] }` → 204. This is the full
  new order.
- `POST /api/music/playlists/{id}/pin` with body `{ "pinned": true }`.
- `POST /api/music/playlists/{id}/duplicate` → 201 `Playlist`. It is owned by the caller. A
  duplicated smart playlist becomes a regular one with today's tracks.
- `GET /api/music/playlists/{id}/m3u` returns an `.m3u8` export (paths relative to the
  library).

### Getting music ("+ Add", only when `/api/music/downloads/status` says `canDownload`)

This uses the same VPN/Prowlarr/qBittorrent stack and Settings as Movies (`MOVIE_DOWNLOADS.md`).

This uses the same VPN, Prowlarr and qBittorrent setup as Movies, and the same Settings
(Settings → MOVIE DOWNLOADS: enabled, who can add, VPN). It also uses the same sites. Like Movies,
a search runs in the background, because starting the VPN can take a minute or two.

- `GET /api/music/downloads/status` → `{ "enabled": true, "canDownload": true, "musicLibrary": true, "status": { … the same object as /api/media/downloads/status → status } }`.
  - `canDownload` means downloads are on, the caller is allowed, and a music library is on the
    server's own disk.
  - `musicLibrary` is false when there is no such library; say "Add a music library on this
    server's own disk first".
  - This answers even when downloads are off.
- `GET /api/music/downloads/lookup?q=rumours` →
  `[ { "mbid": "…", "title": "Rumours", "artist": "Fleetwood Mac", "year": 1977, "type": "Album|EP|Single|Compilation|Live", "trackCount": 11, "artUrl": "https://coverartarchive.org/release-group/…/front-250", "inLibrary": true } ]`.
  - This searches MusicBrainz albums (release groups). Type `artist album` for the best
    results, or just an album or an artist.
  - `inLibrary` means an album with that artist and title is already in the library.
  - `artUrl` may 404 when the Cover Art Archive has no picture; show a placeholder.
- `POST /api/music/downloads/search` with body
  `{ "artist": "Fleetwood Mac", "album": "Rumours", "year": 1977, "mbid": "…"?, "artUrl": "…"? }`
  → **202** `Search`. Poll `GET /api/music/downloads/search/{id}` every 2 s until `phase` is
  `done` or `failed`.
  ```json
  Search = { "id", "phase": "connecting|searching|done|failed", "message": "Connecting to the VPN...",
             "error": null, "artist": "Fleetwood Mac", "album": "Rumours", "year": 1977,
             "results": [ MusicRelease ], "others": [ MusicRelease ] }
  MusicRelease = { "id": "r1", "title": "Fleetwood Mac - Rumours (1977) [FLAC]", "site": "The Pirate Bay",
                   "sizeBytes": 300000000, "seeders": 40, "leechers": 2, "publishedAt": "…",
                   "format": "FLAC|FLAC 24-bit|ALAC|MP3 320|MP3 V0|MP3|AAC|OGG|null", "lossless": true,
                   "matches": true, "warning": "Discography: much more than this album"|null }
  ```
  - `results` are the releases that match and have people sharing them, best first. Lossless
    comes first when most of the library already is, then the most seeders.
  - `others` holds the rest, worth showing behind a "Show more" link.
- `POST /api/music/downloads` with body `{ "searchId", "releaseId", "libraryId"?, "downloadAgain"? }` → 201
  Download (400 `{ error }` when the search expired).
  - An album already in the library - same artist, and the same title once edition words are
    left out ("Rumours (Deluxe Edition)" is "Rumours"; `MusicText.EditionlessKey`) - answers
    **409** `{ "error": "You already have Rumours by Fleetwood Mac.", "code": "alreadyInLibrary",
    "existing": { "title", "artist" } }`. The app asks "Download it again?" and sends the same body
    with `"downloadAgain": true`.
  - The same album already downloading (any copy) answers 201 with that download.
  - `libraryId` defaults to the first music library on the server's own disk.
  - Download has the same shape as Movies' Download, with `kind: "album"`, plus `artist`.
    `title` is the album, `subtitle` the artist, and `posterUrl` is the `artUrl` sent with the
    search.
- `GET /api/music/downloads` → `{ "status", "active": [Download], "recent": [Download] }`, the
  same as Movies but with album downloads only. Movies' own list no longer shows albums.
- `GET /api/music/downloads/{id}`, `DELETE /api/music/downloads/{id}` (cancels one, or clears a
  finished one) and `POST /api/music/downloads/{id}/retry` work as in Movies.
- A finished download is filed as `<library>/<Artist>/<Album> (<Year>)/…`, keeping the torrent's
  own disc folders (`CD1`, `Disc 2`). Only audio, `.cue`, `.log`, `.lrc` and image files are kept.
  The music library is then rescanned, so the album shows up under Recently added.

### Suggestions ("Suggested for you", same `canDownload` gate as "+ Add")

Albums by artists like the ones this person listens to, that are not in the library yet. The
server weighs who they **love** (artist 6, album 4, track 3), **rate** 4-5 stars, **play**
(log of plays), put on **their own playlists**, and had on **lists they imported** (Spotify
likes count even before anything downloads). The top 12 are looked up on MusicBrainz, their
similar artists come from ListenBrainz (free, open data, no key), artists already in the library
are skipped, and each remaining artist's best-known studio albums come from ListenBrainz
popularity (live records, hits collections, singles and EPs left out). Every outside answer is
cached for 30 days (`MusicSuggestionCache` in media-downloads.db), so only the first build is slow.

- `GET /api/music/downloads/suggestions[?refresh=true]` →
  ```json
  { "state": "ready|working|error", "builtAt": "2026-10-18T22:00:00Z"|null, "message": "Couldn't reach ListenBrainz…"|null,
    "seeds": [ { "artist": "Fleetwood Mac", "reason": "loved|rated|played|playlists|imported|library" } ],
    "albums": [ { "key": "steely dan|aja", "artist": "Steely Dan", "title": "Aja", "year": 1977,
                  "mbid": "…release group…", "artUrl": "https://coverartarchive.org/release-group/…/front-250",
                  "because": ["Fleetwood Mac", "Eagles"], "listens": 900000, "queued": false } ] }
  ```
  - Built in the background, per person; the first read (or one more than 24 h after the last
    build, or `refresh=true`) starts a build and waits up to 2 s for it. While `state` is
    `working`, show what `albums` already holds (the previous list, or nothing yet) with a
    spinner and read again every few seconds. `error` keeps the last good `albums` too.
  - At most 40 albums, two per artist, every artist's best album first. Albums that reach the
    library drop out on the next read; `queued` = already on one of this person's imported lists
    or downloading.
  - `because` = the seeds that led there, best first ("Because you like Fleetwood Mac and Eagles").
- `POST /api/music/downloads/suggestions/dismiss` with `{ "artist", "title"?, "dismissed"?: true }`
  → 204. "Not interested": with `title` that one album, without it the whole artist. `dismissed:
  false` takes it back. The album disappears from the next read at once.
- **Downloading** is an imported list: `POST /api/music/downloads/imports` with
  `{ "name": "Suggested for you", "items": [ { "artist", "title", "year" } ] }` (one album for a
  single "Download" tap, the ticked ones for "Download all"). The server then searches, picks the
  best seeded release and files it exactly as in "Import a list" (MOVIE_DOWNLOADS.md), and the
  list shows under Downloads → Imported lists.

---

## 5. Podcast objects

```json
Podcast = {
  "id": "…", "feedUrl": "https://…/feed.xml", "title": "The Daily", "author": "The New York Times",
  "description": "…(plain text)", "artUrl": "/api/podcasts/{id}/art", "link": "https://…",
  "categories": ["News", "Daily News"], "language": "en", "explicit": false,
  "episodeCount": 2100, "unplayedCount": 3, "newestEpisodeAt": "…",
  "lastRefreshedAt": "…", "refreshError": null,
  "subscribed": true, "subscriberCount": 2,
  "funding": [ { "url": "https://…", "title": "Support the show" } ],
  "settings": PodcastSettings, "mySettings": PodcastMySettings
}
```

- `unplayedCount` is the current user's.
- `subscribed` is whether the current user follows the show.
- `subscriberCount` is how many people in the household do.

```json
PodcastSettings = {             // household-wide; changing them is admin-only
  "autoDownload": true,         // download new episodes onto the server
  "keepEpisodes": 10,           // newest N downloaded episodes kept; 0 = all
  "voiceBoost": false,          // even out loudness + clarity (timing unchanged)
  "trimSilence": false,         // cut silences longer than 1 s down to 0.3 s on download
  "transcribe": false           // write a transcript for each new episode (needs transcripts on)
}
PodcastMySettings = {           // per person
  "playbackSpeed": 1.0,         // 0.5–3.0; null = use the app's default
  "skipIntroSeconds": 0, "skipOutroSeconds": 0,
  "notify": true,               // push when a new episode arrives
  "autoQueue": false,           // put each new episode at the end of Up Next
  "inBriefing": false,          // include the newest episode in the Morning Briefing
  "sort": "newest|oldest"
}
```

```json
Episode = {
  "id": "…", "podcastId": "…", "podcastTitle": "The Daily",
  "title": "…", "summary": "plain text, ≤ 300 chars", "publishedAt": "…",
  "durationSeconds": 1780, "season": null, "episode": 512, "episodeType": "full|trailer|bonus",
  "explicit": false, "link": "https://…",
  "artUrl": "/api/podcasts/episodes/{id}/art",
  "audioUrl": "/api/podcasts/episodes/{id}/audio",
  "mimeType": "audio/mpeg", "sizeBytes": 28000000,
  "download": { "state": "none|queued|downloading|done|failed", "progress": 0.0, "error": null, "enhanced": false, "timeSavedSeconds": 0 },
  "archived": false,
  "hasChapters": true, "hasTranscript": true, "transcriptSource": "feed|whisper|null",
  "progress": { "positionSeconds": 300, "durationSeconds": 1780, "percent": 16.9, "played": false, "updatedAt": "…" } | null,
  "inQueue": false, "bookmarkCount": 0
}
```

- `audioUrl` always points at the server. It serves the downloaded file when there is one (with
  ranges), and otherwise relays the original URL (ranges passed through). Players never need the
  publisher's URL.
- `archived` means the episode is no longer in the feed but the server kept it.
- `artUrl` is the episode image, or else the show art.
- With `trimSilence`, the downloaded audio is shorter than the original. `durationSeconds`,
  chapters, transcripts and bookmarks are all in the **downloaded file's** time, so nothing needs
  converting. Progress saved before the download finished is moved across by the server.
- `GET /api/podcasts/episodes/{id}` → Episode plus `description` (HTML, sanitized: `p`, `br`,
  `a`, `b`, `i`, `em`, `strong`, `ul`, `ol`, `li`, `blockquote`) and `descriptionText` (plain).

---

## 6. Podcast routes

### Finding shows

- `GET /api/podcasts/directory/search?q=` → `[DirectoryShow]`, from the Apple Podcasts
  directory in Settings' country.
- `GET /api/podcasts/directory/top?genre=` → `[DirectoryShow]`, the top 50 shows.
  `GET /api/podcasts/directory/genres` →
  `[ { "id": 1311, "name": "News" }, … ]`.
- `DirectoryShow = { "itunesId": 1200361736, "title", "author", "artUrl": "https://…", "feedUrl", "genre", "episodeCount", "podcastId": null|"…", "subscribed": false }`.
  `podcastId` is set when the household already has the feed, and `subscribed` when the current
  user follows it.
- `GET /api/podcasts/preview?feedUrl=` (or `?itunesId=`) → `{ podcast: Podcast-without-id-fields, episodes: [Episode-lite] }`.
  This reads a feed without subscribing, for a "look before you follow" screen. The episodes have
  no progress or download fields.
- `GET /api/podcasts/recommended` → `[DirectoryShow]`: top shows in the genres the household
  listens to most, minus what it already has.

### Subscriptions

- `GET /api/podcasts` → `[Podcast]`, the current user's subscriptions (most recently updated
  first). `?all=true` lists every show the household has.
- `POST /api/podcasts/subscriptions` with body `{ "feedUrl": "…" }` or `{ "itunesId": 123 }` →
  201 `Podcast`. It fetches the feed now, and 400 `{ errors }` comes back when the feed cannot be
  read. Subscribing to a show the household already has just adds the user. If `autoDownload` is
  on, the newest episode is downloaded straight away.
- `DELETE /api/podcasts/{id}/subscription` → 204. When nobody is subscribed any more, the show
  stops refreshing. Its downloads are deleted after `deletePlayedAfterDays`, and the show stays in
  `?all=true` until an admin deletes it.
- `DELETE /api/podcasts/{id}` (admin) → 204. This forgets the show, its episodes and its
  downloads.
- `GET /api/podcasts/{id}` → `Podcast`.
- `PUT /api/podcasts/{id}/settings` (admin) with body `PodcastSettings` → `Podcast`.
- `PUT /api/podcasts/{id}/my-settings` with body `PodcastMySettings` → `Podcast`.
- `POST /api/podcasts/{id}/refresh` → `Podcast`, after re-reading the feed.
  `POST /api/podcasts/refresh` → 202 for every show.
- `GET /api/podcasts/{id}/art` → image, cached.
- `GET /api/podcasts/{id}/episodes?sort=newest|oldest&filter=all|unplayed|downloaded|inProgress|bookmarked&q=&season=&offset=&limit=`
  → `{ "items": [Episode], "total": N, "seasons": [1,2,3] }`.
- `POST /api/podcasts/{id}/played` with body `{ "played": true, "before": "…"? }` marks every
  episode (or every one published before `before`) played or unplayed → 204. The web dashboard
  only marks a whole show; it never sends `before`.
- OPML:
  - `GET /api/podcasts/opml` returns `text/x-opml` with the current user's subscriptions (as a
    download).
  - `POST /api/podcasts/opml` takes the raw OPML XML as the body (`Content-Type: text/xml` or
    `application/xml`) → `{ "added": 12, "existing": 3, "failed": [ { "feedUrl", "error" } ] }`.
    The feeds are fetched in the background, so `added` counts accepted URLs.

### Home ("Listen now")

`GET /api/podcasts/home` →
```json
{
  "upNext": [Episode],
  "continueListening": [Episode],
  "newEpisodes": [Episode],
  "shortOnes": [Episode],
  "downloaded": [Episode],
  "subscriptions": [Podcast],
  "bookmarks": [Bookmark],
  "timeSavedSeconds": 12000
}
```

- `upNext` is the queue, in order.
- `continueListening` is in progress, newest first.
- `newEpisodes` are unplayed episodes from subscriptions in the last 14 days, newest first, at
  most 50.
- `shortOnes` are unplayed episodes under 20 minutes.
- `downloaded` are unplayed and downloaded episodes.
- `bookmarks` are the newest 10.
- `timeSavedSeconds` is what speed, trim-silence and skips saved the user in total.

### Queue (Up Next, per person)

- `GET /api/podcasts/queue` → `[Episode]`.
- `POST /api/podcasts/queue` with body `{ "episodeId", "position": "next|last" }` →
  `[Episode]`.
- `PUT /api/podcasts/queue` with body `{ "episodeIds": [..] }` sets the whole order →
  `[Episode]`.
- `DELETE /api/podcasts/queue/{episodeId}` → `[Episode]`.
- A finished episode leaves the queue on its own.

### Listening

- `POST /api/podcasts/episodes/{id}/progress` with body
  `{ "positionSeconds": 300, "durationSeconds": 1780, "speed": 1.5, "deviceName": "iPhone" }` →
  `progress`.
  - Send it every 15 s while playing, on pause and on close.
  - It is marked played when within 30 s of the end or at 95% or more.
  - Under 10 s, nothing is saved.
  - `speed` is used for "time saved".
- `POST /api/podcasts/episodes/{id}/played` with body `{ "played": true }` → `progress`.
- `GET /api/podcasts/episodes/{id}/audio` serves the audio (see Episode). Clients may send
  `?download=1` to get an attachment name for saving offline.
- `POST /api/podcasts/episodes/{id}/download` → Episode. Anyone can start one.
  `DELETE /api/podcasts/episodes/{id}/download` → Episode. Admin only, or anyone when they are the
  last subscriber.
- `GET /api/podcasts/episodes/{id}/chapters` →
  `[ { "startSeconds": 0, "endSeconds": 95, "title": "Intro", "imageUrl": null, "url": null } ]`.
  Chapters come from the feed's `podcast:chapters` JSON, or from the file's own ID3/MP4 chapters
  once downloaded. Returns `[]` when there are none.
- `GET /api/podcasts/fill?minutes=30` → `{ "episodes": [Episode], "totalSeconds": 1700 }`. This
  is "Fill my drive": unplayed or in-progress episodes from subscriptions (queue first, then
  newest) that fit in the time, counting remaining time only.

### Transcripts

- `GET /api/podcasts/episodes/{id}/transcript` →
  `{ "source": "feed|whisper", "language": "en", "segments": [ { "start": 0.0, "end": 4.2, "speaker": "Host"|null, "text": "…" } ] }`.
  It comes from the feed's `podcast:transcript` (WebVTT, SRT, JSON or HTML) when offered.
  Otherwise it is whatever whisper wrote. Returns 404 `{ error }` when there is none.
- `POST /api/podcasts/episodes/{id}/transcribe` → 202 `{ "state": "queued" }`. It needs
  `transcriptsEnabled` and a working whisper. Otherwise it returns 400 `{ error }` explaining
  what's missing. It downloads the episode first if needed.
- `GET /api/podcasts/transcripts/search?q=` →
  `[ { "episodeId", "episodeTitle", "podcastId", "podcastTitle", "startSeconds": 1234.5, "snippet": "…matched words…" } ]`,
  at most 50 hits across every transcript. Tapping a hit plays that episode from `startSeconds`.

### Bookmarks and clips

```json
Bookmark = { "id", "episodeId", "episodeTitle", "podcastId", "podcastTitle", "positionSeconds": 1234,
             "note": "great point about…", "transcriptSnippet": "…"|null, "createdAt": "…", "artUrl": "…" }
```

- `GET /api/podcasts/bookmarks?episodeId=` → `[Bookmark]` (the current user's).
- `POST /api/podcasts/episodes/{id}/bookmarks` with body `{ "positionSeconds", "note": "" }` →
  201 `Bookmark`. The server fills `transcriptSnippet` with the transcript text from
  `positionSeconds - 15` to `positionSeconds + 15` when there is a transcript.
- `PUT /api/podcasts/bookmarks/{id}` with body `{ "note" }` → `Bookmark`.
  `DELETE /api/podcasts/bookmarks/{id}` → 204.
- `POST /api/podcasts/bookmarks/{id}/to-note` → `{ "noteId": "…" }`. This creates an Organizer
  note in the user's Personal space (the Family space when login is off) titled
  `"<Podcast> — <Episode>"` with the time, the note, the snippet and a link. It returns 404 when
  the Organizer is off.
- `POST /api/podcasts/episodes/{id}/clips` with body `{ "startSeconds": 1200, "durationSeconds": 45, "title": "" }`
  → 201 `{ "id", "url": "/api/podcasts/clips/{id}.m4a", "durationSeconds": 45 }`. The duration
  is 5–180 s. The clip is cut by ffmpeg (which needs the episode downloaded or reachable), and
  apps offer the file to the system share sheet. `GET /api/podcasts/clips` lists the user's clips,
  `GET /api/podcasts/clips/{id}.m4a` serves one, and `DELETE /api/podcasts/clips/{id}` deletes
  one.

### Status (admin sees paths)

`GET /api/podcasts/status` →
```json
{ "enabled": true, "ffmpegAvailable": true, "refreshing": false, "lastRefreshAt": "…",
  "downloadFolder": "/srv/media/…/podcasts", "storageBytes": 12000000000, "maxStorageBytes": 53687091200,
  "downloadsActive": 1, "downloadsQueued": 3,
  "transcripts": { "enabled": true, "available": true, "error": null, "queued": 2 },
  "podcastCount": 18, "episodeCount": 5400 }
```

`transcripts.available` is false with `error` explaining what's missing when whisper is not set
up.

---

## 7. Play where you left off (both tabs)

Each device reports what it is playing. Any of the person's devices can show "Playing on Kitchen
iPad — continue here?" and pick up the queue at the same spot.

```json
NowPlaying = {
  "deviceId": "ios-…", "deviceName": "Morgan's iPhone", "kind": "music|podcast",
  "queue": [ { "type": "track|episode", "id": "…" } ],
  "index": 3, "positionSeconds": 61.2, "playing": true,
  "shuffle": false, "repeat": "off|all|one", "speed": 1.0,
  "context": { "type": "album|playlist|artist|mix|podcast|queue|search|radio", "id": "…", "name": "Rumours" },
  "updatedAt": "…"
}
```

- `PUT /api/audio/now-playing` with body `NowPlaying` (without `updatedAt`) → 204. Send it on
  track change, play/pause, seek, and every 30 s while playing. Queues longer than 500 entries
  are cut to the 500 around `index`.
- `GET /api/audio/now-playing` → `[NowPlaying]`, the current user's devices, newest first.
  Devices silent for 7 days are dropped.
- `DELETE /api/audio/now-playing/{deviceId}` → 204.
- `GET /api/audio/now-playing/{deviceId}/resolve` → `{ nowPlaying, "tracks": [Track], "episodes": [Episode] }`.
  It resolves the queue so a device can take over in one request.

## 8. Speakers: play on another device

A device that is willing to be played to (the kiosk, the Apple TV, an app left open) registers
as a **speaker**. Speakers belong to the household: anyone signed in can see and control them.

- `PUT /api/audio/speakers/{deviceId}` with body
  `{ "name": "Living Room TV", "kind": "tv|kiosk|phone|tablet|mac|windows|web", "allowAnnouncements": true, "volume": 0.6 }`
  → 204. This is the heartbeat: send it on start and every 60 s while the speaker option is on.
  A speaker unseen for 3 minutes is offline.
- `DELETE /api/audio/speakers/{deviceId}` → 204, when the option is switched off.
- `GET /api/audio/speakers` →
  `[ { "deviceId", "name", "kind", "online": true, "allowAnnouncements", "volume", "lastSeenAt", "state": SpeakerState|null } ]`.
- `PUT /api/audio/speakers/{deviceId}/state` with body
  `SpeakerState = { "playing": true, "title": "Dreams", "subtitle": "Fleetwood Mac", "artUrl": "…", "positionSeconds": 12, "durationSeconds": 257, "volume": 0.6, "kind": "music|podcast", "itemId": "…" }`
  → 204. The speaker sends it whenever its state changes.
- `POST /api/audio/speakers/{deviceId}/commands` with body `SpeakerCommand` → 202
  `{ "seq": 42 }`. Any signed-in user may send one.
  ```json
  SpeakerCommand = { "action": "play|pause|resume|next|previous|seek|volume|stop|duck|unduck|announce",
                     "queue": [ { "type": "track|episode", "id" } ], "index": 0, "positionSeconds": 0,
                     "shuffle": false, "volume": 0.5, "rampSeconds": 0, "text": "Someone is at the front door",
                     "context": { … as NowPlaying.context } }
  ```
  - `play` replaces the queue.
  - `duck` drops the volume to 20% until `unduck` (or 30 s).
  - `announce` ducks, speaks `text` with the device's speech voice, then unducks.
  - `rampSeconds` fades the volume up from 0 to `volume` (used by alarms).
  - **A speaker applies commands in `seq` order, and finishes speaking an `announce` before it
    applies the next one.** An alarm playing the briefing sends `announce` (the spoken briefing)
    and then `play` (its episodes), and the episodes must start after the voice ends. An
    `announce` with nothing playing just speaks (at `volume` when given).
- `GET /api/audio/speakers/{deviceId}/commands?after=41&wait=25` is a long poll →
  `{ "commands": [ { "seq": 42, "at": "…", "from": "morgan", ...SpeakerCommand } ] }`. It returns
  as soon as there is anything after `after`, or `[]` after `wait` seconds. Commands older than
  2 minutes are dropped and never delivered. On first connect, pass `after=-1` to get only new
  ones. The reply also carries `latestSeq`.

The server sends commands to speakers itself in these cases:

- **Wake-up alarms** (§9).
- **The home alarm going off:** every speaker gets `pause` when `pauseSpeakersOnAlarm`, plus an
  `announce` "The alarm is going off" when the speaker allows announcements.
- **Camera person/vehicle alerts:** when `announceOnSpeakers` is on, speakers that allow
  announcements get `announce` with the alert's summary, at most once a minute per camera.

## 9. Wake-up alarms, briefing, stats, ListenBrainz

### Wake-up alarms (per person; they fire on a speaker)

```json
WakeAlarm = { "id", "name": "Weekdays", "time": "06:45", "days": ["mon","tue","wed","thu","fri"],
              "speakerId": "kiosk-…", "source": { "type": "playlist|album|artist|mix|briefing|podcast|shuffle", "id": "…", "name": "Morning" },
              "volume": 0.5, "rampSeconds": 120, "enabled": true, "nextFireAt": "…", "lastFiredAt": null }
```

- `GET /api/audio/alarms` → `[WakeAlarm]`.
- `POST /api/audio/alarms` → 201. `PUT /api/audio/alarms/{id}` → `WakeAlarm`.
  `DELETE /api/audio/alarms/{id}` → 204.
- `POST /api/audio/alarms/{id}/test` → 202. It plays the alarm now.
- `days` empty means once (at the next `time`, after which `enabled` turns false).
- Times are the server's local time zone.
- When an alarm fires, the server sends the speaker a `play` with `rampSeconds`.
- `source.type`:
  - `briefing`: the queue is the briefing's episodes, and the briefing speech is sent first as an
    `announce` (see below).
  - `podcast`: the newest unplayed episode of that show.
  - `shuffle`: shuffle all.
- An offline speaker misses the alarm, and the person is pushed "Your 6:45 alarm couldn't reach
  Kitchen".

### Morning briefing

`GET /api/audio/briefing` →
```json
{ "greeting": "Good morning, Morgan.",
  "speech": [ "It's Thursday, October 2nd.", "Right now it's 61 degrees and clear in Holland…", "You have 2 events today: …", "3 reminders are due: …" ],
  "episodes": [Episode] }
```

- The weather comes from the Weather tab's location (when set).
- The events and reminders come from the user's Organizer (when on).
- The episodes are the newest unplayed episode of each show with `inBriefing`, oldest show order
  first, at most 5.
- Apps play it as "Morning Briefing": speak each `speech` line with the system voice, then play
  the episodes as a queue.

### Stats

`GET /api/audio/stats?period=week|month|year|all&year=2026` →
```json
{ "period": "year", "from": "…", "to": "…",
  "music": { "minutes": 12034, "plays": 3400, "skips": 210, "distinctTracks": 1500, "distinctArtists": 220, "newArtists": 31,
             "topArtists": [ { "artist": Artist, "plays": 300, "minutes": 1100 } ],
             "topAlbums": [ { "album": Album, "plays": 120 } ], "topTracks": [ { "track": Track, "plays": 40 } ],
             "topGenres": [ { "genre": "Rock", "minutes": 5000 } ],
             "byHour": [ 24 numbers, minutes ], "byWeekday": [ 7 numbers mon..sun, minutes ],
             "longestStreakDays": 41, "firstPlay": { "track": Track, "playedAt": "…" } | null },
  "podcasts": { "minutes": 4000, "episodesFinished": 120, "timeSavedMinutes": 600,
                "topShows": [ { "podcast": Podcast, "minutes": 900, "episodes": 30 } ],
                "byHour": [24], "byWeekday": [7] } }
```

Lists hold at most 10 entries. `period=year` uses `year` (default the current year): this is
"Wrapped". Stats cover only the current user.

### ListenBrainz scrobbling (per person)

- `GET /api/audio/me/listenbrainz` → `{ "connected": true, "username": "morgan" }`.
- `PUT /api/audio/me/listenbrainz` with body `{ "token": "…" }` → `{ "connected": true, "username" }`.
  The token is checked against `api.listenbrainz.org/1/validate-token`, and 400 comes back when it
  is bad. A blank token disconnects.
- Every counted music play is then submitted, retried for up to a day if ListenBrainz is down.

---

## 10. Backups and data

- `music.db`: libraries, the track/album/artist catalog (rebuilt by a scan), and per-user loves,
  ratings, plays, playlists and pins.
- `podcasts.db`: shows, episodes, subscriptions, per-show settings, progress, queue, bookmarks,
  clips and transcripts.
- `audio.db`: now-playing, speakers, alarms and ListenBrainz tokens.

Backups carry two sections, without a schema bump; older backups simply lack them:

- `music`: libraries, plays, loves, ratings and playlists.
- `podcasts`: shows with their settings, everyone's subscriptions, progress, queues and
  bookmarks, plus alarms.

Downloaded audio, art caches and transcripts are rebuilt or re-downloaded.

## 11. What each app does (summary for client authors)

Every app gets two new tabs: **Music** and **Podcasts**. Both use one shared **player** with:

- A mini player bar plus a full-screen Now Playing screen.
- A queue, shuffle, repeat off/all/one, and a seek bar.
- A sleep timer (minutes, or end of track/episode, with a 10 s fade).
- Playback speed (podcasts by default: per show, remembered; music optional).
- Skip back 15 / forward 30 for podcasts.
- Chapters, lyrics (synced lyrics scroll and highlight; tapping a line seeks), and transcripts
  (follows playback; tapping seeks; search inside it).
- Sound Check (ReplayGain: volume = 10^(gain/20), clamped to 1.0, album gain when playing an
  album in order).
- AirPlay/Cast route pickers where the platform has them (not the web dashboard, whose "Play on…"
  button is the speaker picker below).
- Lock-screen / system media controls with artwork. On iPhone this also makes CarPlay's Now
  Playing work. On Android, a MediaSession service is browsable by Android Auto.
- Background playback.
- Offline downloads (phone, tablet, Mac and Android; not the web dashboard).
- Reporting via `played`, `progress` and `now-playing`.
- A "Continue from <device>" banner.
- A "Play on…" speaker picker.
- A "Let others play here" switch (registers the device as a speaker).
- Wake-up alarm editing.
- Morning Briefing.
- A Stats / Wrapped screen.
