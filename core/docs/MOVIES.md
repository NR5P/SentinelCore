# Movies & TV: the media library API

The Movies tab is a Jellyfin-style media library built into SentinelCore. The server scans folders of
movies and TV shows into `media.db`, fetches artwork and descriptions from TMDB (The Movie Database,
free API key, https://www.themoviedb.org/settings/api), and plays the files back to the dashboard and
every app: direct play for MP4 files the device can decode, otherwise an on-demand fMP4 HLS remux or
transcode through ffmpeg.

All routes are under `/api/media`, JSON is camelCase, enums are strings, dates are ISO-8601 UTC.
Every route needs a signed-in user (session cookie, same as the rest of the API) unless marked
**admin**, which requires the Admin role. Every route except `/enabled` and `/status` returns
**404** `{ "error": "Movies & TV is turned off in Settings." }` when the section is disabled.
Errors are `{ "error": "…" }` or `{ "errors": ["…"] }`.

Adding titles by downloading them ("+ Add", torrents through a VPN) is its own contract:
`core/docs/MOVIE_DOWNLOADS.md`.

Item ids are stable across rescans: a movie or episode id is derived from its library and relative
path, a show id from its library and normalized title. Progress therefore survives a rescan and a
backup restore.

## Feature flag and status

- `GET /api/media/enabled` → `{ "enabled": true }`. Answers even when off. A 404 from an older
  server also means "off"; apps hide the tab in both cases.
- `GET /api/media/status` →
  ```json
  {
    "enabled": true,
    "ffmpegAvailable": true,
    "tmdbConfigured": true,
    "scanning": false,
    "scan": { "phase": "idle|files|metadata|probing", "libraryName": "Movies", "processed": 12, "total": 400, "startedAt": "…" },
    "libraries": [ { "id": "…", "name": "Movies", "kind": "movies", "path": "/mnt/movies/Media/Movies",
                     "itemCount": 396, "lastScanFinishedAt": "…", "lastScanError": null } ],
    "movieCount": 396, "showCount": 45, "episodeCount": 1900,
    "lastScanFinishedAt": "…"
  }
  ```
  `scan` is null when idle. `phase` `files` walks folders, `metadata` talks to TMDB, `probing` runs
  ffprobe. Playback works during a scan.

## Libraries (admin, except GET)

- `GET /api/media/libraries` → `[ Library ]` (same shape as in status).
- `POST /api/media/libraries` body `{ "name": "Movies", "kind": "movies|shows", "path": "/mnt/…" }`
  → 201 `Library`. The path must be a folder the server can read; otherwise 400 with `errors`.
  Adding a library starts a scan of it.
- `PUT /api/media/libraries/{id}` same body → `Library`. Changing the path rescans.
- `DELETE /api/media/libraries/{id}` → 204. Removes its items and their progress.
- `POST /api/media/libraries/{id}/scan` → 202. `POST /api/media/scan` → 202 rescans everything.
  A 409 means a scan is already running.
- `Library` also carries `"network": true` when its path is a shared folder on another computer.

### Folder picker (admin)

`GET /api/media/browse?path=` always answers 200; a folder that cannot be opened comes back with
`error` (and `needsLogin: true` when signing in to that computer would fix it), so a picker can
show the message, and a sign-in form, without special-casing status codes.

- No `path` → the starting page:
  ```json
  { "path": "", "parent": null, "kind": "roots", "videoCount": 0,
    "folders": [ { "name": "/mnt/movies", "path": "/mnt/movies", "videoCount": 0 } ],
    "computers": [ NetworkComputer ] }
  ```
  `folders` are places on the server itself (every drive on Windows; mounted disks, /mnt, /media,
  /Volumes, the home folder and / elsewhere). `computers` are the network computers signed in to.
- A local path → `{ "path", "parent", "kind": "local", "folders": [ { "name", "path", "videoCount" } ], "videoCount" }`.
- `smb://host` → `{ "kind": "computer", "host", "folders": [ its shared folders ], "hint"? }`.
  Not pickable itself. `hint` is set when the computer listed nothing (type the share name instead).
- `smb://host/Share/…` → `{ "kind": "network", "host", "folders", "videoCount" }`.

Everywhere: `parent` is where "up" goes (`""` is the starting page, null only on it),
`videoCount` on the response counts videos directly in that folder, and on a folder entry it is
the same count for that subfolder - null for network folders (it would cost a round trip each).
Typed paths are accepted: `\\PC\Share\Folder` and `//PC/Share` are read as `smb://PC/Share/Folder`.

## Folders on other computers (admin)

The server reads shared folders (Windows file sharing, a Mac with File Sharing on, a NAS, Samba)
itself, over SMB 2/3: nothing is mounted, so this works the same with the server on Linux,
Windows or macOS and needs no root. A library on a share has a path like
`smb://192.168.1.20/Movies/Films` (a port goes after the host: `smb://nas:4455/…`). ffmpeg reads
those files through a loopback relay inside the server.

Sign-ins are saved per computer (host, plus port if any) in `media-network.json` beside
`media.db`, and go into backups with the libraries. With no saved sign-in the server tries a
guest sign-in, as Explorer and Finder do.

```json
NetworkComputer = { "id": "…", "name": "Den PC", "host": "192.168.1.20", "username": "sam",
                    "domain": "", "hasPassword": true, "path": "smb://192.168.1.20", "createdAt": "…" }
```

- `GET /api/media/network` → `[ NetworkComputer ]`. Passwords are never returned.
- `POST /api/media/network` body `{ "host": "DESKTOP-PC", "name": "Den PC", "username": "sam", "password": "…", "domain": "" }`
  signs in and lists the computer's shares; it is saved only if that works.
  → 201 (or 200 when that host was already saved, which it replaces)
  `{ "computer": NetworkComputer, "shares": [ { "name": "Movies", "path": "smb://DESKTOP-PC/Movies" } ] }`.
  Failure → 400 `{ "errors": [ "…" ], "needsLogin": true|false }`. `host` may be a name, an IP,
  `name.local`, `host:port`, or a whole `smb://`/UNC path (only the computer part is used).
  `username` may be `DOMAIN\user`. Names that DNS cannot resolve are tried as `name.local` over
  multicast DNS, which Windows 10+, macOS and most NASes answer.
- `PUT /api/media/network/{id}` same body → 200. A blank `password` keeps the saved one (when the
  username is unchanged).
- `DELETE /api/media/network/{id}` → 204. Libraries on that computer fail to scan until it is
  signed in to again (or allow guests).
- `GET /api/media/network/discover` → takes ~3 s →
  `[ { "name": "Living Room Mac", "host": "Living-Room-Mac.local", "address": "192.168.1.31", "path": "smb://Living-Room-Mac.local", "savedId": null } ]`.
  Computers that advertise file sharing over mDNS (`_smb._tcp`): Macs, NASes, Samba with Avahi.
  Windows PCs do not; add them by name or address.

## Browsing

### ItemSummary

```json
{
  "id": "…", "kind": "movie|show|episode", "libraryId": "…",
  "title": "17 Again", "sortTitle": "17 again", "year": 2009, "overview": "…",
  "genres": ["Comedy", "Fantasy"], "rating": 6.2, "certification": "PG-13",
  "runtimeMinutes": 102, "durationSeconds": 6120.4,
  "posterUrl": "/api/media/items/{id}/poster", "backdropUrl": "/api/media/items/{id}/backdrop",
  "thumbUrl": null,
  "addedAt": "2026-09-25T…",
  "progress": { "positionSeconds": 1200, "durationSeconds": 6120, "percent": 19.6, "watched": false, "updatedAt": "…" },

  "showId": null, "showTitle": null, "seasonNumber": null, "episodeNumber": null, "airDate": null,

  "seasonCount": null, "episodeCount": null, "watchedEpisodeCount": null, "nextEpisodeId": null,

  "playCount": 3, "inList": false
}
```

- `posterUrl` / `backdropUrl` / `thumbUrl` are null when there is no artwork; render a text card.
  Episodes carry `thumbUrl` (the episode still) and `posterUrl` of their show.
- `progress` is null when the current user never played it. `percent` is 0–100.
- Episode summaries fill `showId`, `showTitle`, `seasonNumber`, `episodeNumber`, `airDate`.
- Show summaries fill `seasonCount`, `episodeCount`, `watchedEpisodeCount` and `nextEpisodeId`
  (the episode to play next: the in-progress one, else the first unwatched, else the first).
- `runtimeMinutes` comes from TMDB; `durationSeconds` from the file (null until probed).
- `playCount` is how many times anyone in the household has pressed Play on it (a show adds up
  its episodes). `inList` is whether the current user saved it to My List (always false on
  episodes). Both are missing on servers older than these features; treat that as 0 / false.

### Routes

- `GET /api/media/home` →
  ```json
  { "continueWatching": [ ItemSummary ], "recentMovies": [ ItemSummary ], "recentShows": [ ItemSummary ],
    "recentEpisodes": [ ItemSummary ], "featured": ItemSummary|null,
    "myList": [ ItemSummary ], "mostPlayed": [ ItemSummary ], "recentlyWatched": [ ItemSummary ],
    "topRated": [ ItemSummary ], "genreShelves": [ { "genre": "Comedy", "items": [ ItemSummary ] } ],
    "movieCount": 396, "showCount": 45, "libraries": [ Library ] }
  ```
  `continueWatching` mixes movies and episodes: everything in progress, newest first, plus the
  next episode of a show whose last episode was finished ("next up"). `featured` is something with a
  backdrop to use as a hero. 20 items per row at most. The rest, all movies and shows only:
  - `myList`: the current user's My List, most recently added first.
  - `mostPlayed`: the household's most played titles (everyone's plays), ties broken by the most
    recently played.
  - `recentlyWatched` ("Watch again"): movies this user finished and shows they have finished
    episodes of, newest first, leaving out anything on `continueWatching`.
  - `topRated`: TMDB rating 7 or more that this user has not watched, best first. Empty without TMDB.
  - `genreShelves`: up to three genres with at least six titles, a different pick each day, each
    row unwatched titles first in an order that changes daily.

  Suggested order on screen: Continue watching, My List, Recently added movies, Recently added
  shows, New episodes, Most played, Top rated, the genre shelves, Watch again. Hide empty rows.
  All five keys are missing from older servers; treat that as empty.
- `GET /api/media/items?kind=movie|show&libraryId=&q=&genre=&sort=title|added|year|rating|plays&order=asc|desc&unwatched=true&list=true&offset=0&limit=60`
  → `{ "items": [ ItemSummary ], "total": 396, "genres": ["Action", …] }`. `genres` is the full
  list for the filter menu. Default sort is `title` ascending; `added`, `rating` and `plays`
  ("Most played") default to descending. `list=true` keeps only the current user's My List. `kind` omitted returns movies and shows together (never episodes).
- `GET /api/media/random?kind=movie|show&libraryId=&genre=&unwatched=true` → one ItemSummary picked
  at random ("Surprise me"). Movies and shows only. Titles the user has not watched are preferred
  (`unwatched=false` turns that off; if everything is watched it picks from everything). **404**
  `{ "error": "Nothing in the library matches that yet." }` when nothing matches.
- `GET /api/media/items/{id}` → ItemDetail: an ItemSummary plus
  ```json
  {
    "overview": "…", "tagline": "…", "releaseDate": "2009-04-10",
    "cast": [ { "name": "Zac Efron", "character": "Mike O'Donnell" } ],
    "tmdbId": 17638, "tmdbUrl": "https://www.themoviedb.org/movie/17638",
    "file": { "container": "mp4", "videoCodec": "h264", "videoProfile": "High", "width": 1920, "height": 800,
              "audioCodec": "aac", "audioChannels": 2, "sizeBytes": 1734000000, "bitrate": 2267000,
              "path": "/mnt/… (admin only, else null)" },
    "audioTracks": [ { "index": 0, "codec": "eac3", "channels": 6, "language": "eng", "label": "English 5.1", "isDefault": true } ],
    "subtitles": [ { "index": 0, "language": "eng", "label": "English", "url": "/api/media/items/{id}/subtitles/0.vtt", "forced": false, "external": false } ],
    "seasons": [ { "number": 1, "name": "Season 1", "episodeCount": 3, "watchedCount": 1, "posterUrl": null,
                   "episodes": [ ItemSummary ] } ]
  }
  ```
  `file`, `audioTracks` and `subtitles` are null for shows; `seasons` is null for movies and
  episodes. Episode detail includes `showId` and the episode's own `file`. Subtitle `index` counts
  text subtitles only (bitmap subtitles are not offered). `canDelete` is true when the caller may
  delete this title (admins; movies on the server's own disks).
- `DELETE /api/media/items/{id}` (admin) → **204**. Deletes a movie from the disk and forgets it,
  with its progress, plays and My List entries. Cannot be undone, so ask first. If the movie had
  a folder to itself (no other video in it outside Sample/Extras folders) the whole folder goes;
  otherwise only the video and the files named after it (`Movie.en.srt`, `Movie-poster.jpg`).
  400 `{ error }` for shows/episodes and for movies on another computer's share; 404 when gone.
- Artwork, all `image/jpeg` or `image/png`, cached for a day, 404 when absent:
  `GET /api/media/items/{id}/poster`, `GET /api/media/items/{id}/backdrop`, `GET /api/media/items/{id}/thumb`.
  Show seasons: `GET /api/media/items/{showId}/seasons/{n}/poster`.

## Playback

`GET /api/media/items/{id}/playback?start=0&video=h264,hevc&audio=aac,ac3,eac3,mp3&audioTrack=&subtitle=`

- `start` seconds to begin at. Omit (or `-1`) to resume from the saved progress; `0` starts over.
- `video` and `audio` list the codecs the device decodes. Defaults are `h264` and `aac,mp3`.
  Apple: `video=h264,hevc&audio=aac,ac3,eac3,mp3`. Android (Media3): `video=h264,hevc&audio=aac,ac3,eac3,mp3`.
  Web and Windows (Chromium): `video=h264` plus `hevc` only when `MediaSource.isTypeSupported('video/mp4; codecs="hvc1.1.6.L120.B0"')`, `audio=aac,mp3`.
- `audioTrack` is an `audioTracks[].index`; the default track is used when omitted.

Response:

```json
{
  "itemId": "…", "title": "Black Mirror", "subtitle": "S1 · E1 · The National Anthem",
  "durationSeconds": 2646, "startSeconds": 1200, "resumeSeconds": 1200,
  "mode": "direct|hls",
  "url": "/api/media/stream/{id}/video.mp4",
  "mimeType": "video/mp4",
  "video": "copy|h264", "audio": "copy|aac",
  "subtitles": [ { "index": 0, "language": "eng", "label": "English", "url": "/api/media/items/{id}/subtitles/0.vtt", "forced": false } ],
  "nextEpisodeId": "…", "nextEpisodeTitle": "S1 · E2 · Fifteen Million Merits",
  "backdropUrl": "…", "posterUrl": "…"
}
```

- `mode: direct`: `url` is the file itself with HTTP range support. Seek anywhere, then seek the
  player to `startSeconds` yourself. Use `subtitles[].url` as WebVTT `<track>`s (web) or ignore.
- `mode: hls`: `url` is `/api/media/hls/{session}/master.m3u8`, `mimeType`
  `application/vnd.apple.mpegurl`. **The playlist's clock starts at `startSeconds`**: the player
  sees time 0 where the movie is at `startSeconds`, so add `startSeconds` to the player position
  before reporting progress, and display `startSeconds + position` as the current time. The
  playlist is an EVENT playlist that grows while ffmpeg works through the file and ends with
  `EXT-X-ENDLIST`; a remux finishes in a minute or two, a transcode runs at roughly real time. The
  player can only seek within what exists; to jump earlier than `startSeconds` or far ahead of what
  has been produced, request a new playback with a new `start`. The master playlist carries the
  text subtitles as HLS subtitle renditions, so AVPlayer, Media3 and hls.js show them in their own
  subtitle menus. Sessions die three minutes after the last segment request.
- Progress reporting: `POST /api/media/items/{id}/progress` body
  `{ "positionSeconds": 1234.5, "durationSeconds": 6120 }` every 10 seconds while playing, on
  pause, and when the player closes. At 92% or more the server marks the item watched and clears
  the resume point; below 30 seconds nothing is saved. Returns the new `progress` object.
- `POST /api/media/items/{id}/list` body `{ "inList": true }` adds to (or with false removes from)
  the current user's My List → `{ "itemId": "…", "inList": true }`. Posting an episode's id saves its
  show, and `itemId` says which one was saved.
- Every successful `playback` request records a play for "Most played", once per person per
  title per four hours (a seek, a new audio track or a reconnect asks for playback again).
- `POST /api/media/items/{id}/watched` body `{ "watched": true }` → the new `progress`. On a show it
  applies to every episode, on a season route `POST /api/media/items/{showId}/seasons/{n}/watched` to
  that season.
- `GET /api/media/stream/{id}/{fileName}` is the direct file (any `fileName`; the extension is
  there so players sniff the container). `GET /api/media/hls/{session}/{file}` serves
  `master.m3u8`, `index.m3u8`, `init.mp4`, `seg_00001.m4s`, … and `sub_{index}.m3u8`.
- `GET /api/media/items/{id}/subtitles/{index}.vtt?offset=0` returns WebVTT, extracted and cached
  on first request. `offset` shifts every cue earlier by that many seconds (HLS sessions use it).

Playback also returns **503** `{ "error": "…" }` when ffmpeg is missing and the file needs it,
**404** when the file is gone, and **502** with ffmpeg's last lines when a session fails to start.

## Metadata fixes (admin)

- `GET /api/media/tmdb/search?q=Alice&kind=movie|show&year=1951` →
  `[ { "tmdbId": 12092, "title": "Alice in Wonderland", "year": 1951, "overview": "…", "posterUrl": "https://image.tmdb.org/t/p/w185/…" } ]`.
- `POST /api/media/items/{id}/match` body `{ "tmdbId": 12092 }` → ItemDetail, refetched and locked
  to that match so later scans keep it.
- `POST /api/media/items/{id}/refresh` → ItemDetail, refetched from TMDB (unlocks a manual match
  when body is `{ "unlock": true }`).

## Settings section `media` (Settings → MOVIES & TV, `PUT /api/settings/media`)

`{ "enabled": true, "tmdbApiKey": "…", "rescanHours": 12 }`. `tmdbApiKey` is a secret (masked as
`••••••••` on read). A v3 key (32 hex chars) or a v4 read-access token both work. `rescanHours` 0
turns the periodic rescan off; the server also scans at startup and whenever a library is added.

## Scanning rules

Video files: mkv, mp4, m4v, mov, avi, wmv, mpg, mpeg, ts, m2ts, webm, flv. Hidden folders,
`$RECYCLE.BIN`, `System Volume Information`, `.trickplay`, `metadata`, `extras`, `samples` and
files whose name contains "sample" or "trailer" are skipped.

- **Movies library**: every video file is a movie. The title and year are parsed from both the file
  name and its folder name (`17 Again (2009) [1080p]/17.Again.2009.1080p.BrRip.x264.YIFY.mp4` →
  "17 Again", 2009), dots and underscores become spaces, and release tags (1080p, BluRay, x265,
  YIFY, …) are stripped.
- **TV library**: each top-level folder is a show (`Black Mirror (2011) Season 1-6 …` → "Black
  Mirror", 2011). Folders that clean to the same title merge into one show. Episodes are found by
  `S01E02`, `1x02`, `Season 1/…E02` or `Episode 2`; the season comes from the file name, else the
  `Season N` folder, else 1. Files with no episode number keep their cleaned file name as the title
  and sort after numbered ones. A loose video file at the top level is a one-episode show.
- **Local artwork wins** over TMDB: `poster.jpg`, `folder.jpg`, `cover.jpg`, `<file>-poster.jpg`,
  `backdrop.jpg`, `fanart.jpg`, `<file>-backdrop.jpg`, `<file>-thumb.jpg`, `<file>.jpg`, season
  folder `poster.jpg`. TMDB artwork is downloaded once into `<data>/media-art/`.
- **Subtitles**: embedded text tracks (SubRip, ASS/SSA, WebVTT, mov_text) and sidecar
  `<file>.srt`, `<file>.en.srt`, `.vtt`, `.ass`.

## Data

`media.db` in the web host's data folder: `MediaLibraries`, `MediaItems`, `MediaProgress`,
`MediaState`, `MediaPlays` (play history, seeded with one play per progress row when first
created), `MediaWatchlist` (My List). Backups (schema 7) carry the libraries, everyone's progress,
and (as `media.plays` / `media.watchlist`, no schema bump) the play history and My Lists; the catalog and the
artwork cache are rebuilt by a rescan. HLS sessions live under `<data>/media-hls/`, extracted
subtitles under `<data>/media-subs/`.
