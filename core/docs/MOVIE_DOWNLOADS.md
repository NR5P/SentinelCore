# Movie downloads: "+ Add" on the Movies tab

Search for a movie or TV episodes, pick one of the torrents the household's sites offer, and the
server downloads it through a VPN and files it into the library, where it shows up under
**Recently added** like anything else. Off by default (Settings -> MOVIE DOWNLOADS).

What is downloaded is the household's responsibility; the server ships with no sites.

## How it works

```
  apps --HTTPS--> SentinelCore --veth 10.250.0.1 -> 10.250.0.2--> [ network namespace "scvpn" ]
                  (host network)                                     Prowlarr   :9696  (sites, search)
                                                                     qBittorrent:8080  (downloading)
                                                                     default route = VPN tunnel only
```

- **Prowlarr** keeps the site list and searches it. **qBittorrent** downloads. Both run inside a
  Linux network namespace whose only route out is the VPN interface (WireGuard `scwg0` or OpenVPN
  `sctun0`). The tunnel's encrypted packets leave through the host's normal connection; nothing
  inside can. If the VPN drops, the namespace has no route at all - it never falls back to home.
- DNS inside the namespace goes to the VPN's DNS server (from the config), with a namespace-only
  `nsswitch.conf` so nothing resolves through the host's systemd-resolved or mDNS.
- `sentinelcore-vpn-watch` asks ipinfo.io (fallback am.i.mullvad.net) from inside the tunnel every
  45 s and writes `/run/sentinelcore-vpn/status.json`. SentinelCore refuses to search or download
  unless that says connected **and** the address differs from the server's own (api.ipify.org);
  a match stops everything.
- SentinelCore starts `sentinelcore-downloads.target` (systemd, allowed by a polkit rule for its
  account only) when someone searches, downloads or manages sites, and stops it once nothing has
  needed it for `idleMinutes` (default 5). It is never started at boot; downloads left unfinished
  by a restart resume on their own.
- A download is saved to `<library>/.downloads/<id>/` (hidden, so the scanner ignores it). When
  finished - after `seedMinutes` of sharing back, default 0 - the torrent is removed from
  qBittorrent (files kept), the video and an English subtitle are moved into the library, the
  rest is deleted, and the library is rescanned:
  - `Movies/Dune Part Two (2024)/Dune Part Two (2024).mkv` (+ `.en.srt`)
  - `TV Shows/Bluey (2018)/Season 2/Bluey - S02E05.mkv` - into the show's existing folder when
    the library already has the show.
- Only libraries on the server's own disks can receive downloads (not `smb://` ones).

Code: `MediaDownload*.cs`, `QbittorrentClient.cs`, `ProwlarrClient.cs`, `MediaReleaseRanker.cs`
in `SentinelCore.Web`; the installer, VPN helper and units in `core/downloads/`.

## Installing (Linux, once)

```
sudo core/downloads/install-downloads.sh --vpn-config ~/Downloads/mullvad-se.conf
sudo core/downloads/install-downloads.sh --vpn-config us1234.nordvpn.com.udp.ovpn --vpn-user SERVICE_USER --vpn-pass SERVICE_PASS
```

WireGuard `.conf` (Mullvad, ProtonVPN, IVPN, Surfshark, Windscribe, AirVPN) or OpenVPN `.ovpn`
(NordVPN, PIA, ExpressVPN, ...). It installs `wireguard-tools openvpn qbittorrent-nox`, Prowlarr to
`/opt/sentinelcore-prowlarr`, data to `/var/lib/sentinelcore-downloads`, the helper to
`/usr/local/lib/sentinelcore-downloads/sentinelcore-vpn`, the VPN config to
`/etc/sentinelcore/vpn/` (root only), five systemd units and
`/etc/polkit-1/rules.d/50-sentinelcore-downloads.rules`, then starts everything once, prints the
home and VPN addresses and stops it again. Changing VPN later:
`sudo /usr/local/lib/sentinelcore-downloads/sentinelcore-vpn set-config NEWFILE`.
`--uninstall` removes it (keeps data and config).

Elsewhere (Windows, macOS, a qBittorrent you already run): turn **Start And Stop The Services**
off and point the two addresses at your own Prowlarr and qBittorrent, which must run on the same
machine as SentinelCore and already be behind a VPN; turn **Require The VPN** off only if nothing
here can check it.

## Settings section `mediaDownloads` (`PUT /api/settings/media-downloads`)

```json
{ "enabled": false, "allowedUsers": "admins|everyone", "preferredQuality": "1080p|2160p|720p|any",
  "seedMinutes": 0, "idleMinutes": 5, "requireVpn": true, "manageServices": true,
  "prowlarrUrl": "http://10.250.0.2:9696", "prowlarrApiKey": "",
  "qbittorrentUrl": "http://10.250.0.2:8080", "qbittorrentUsername": "", "qbittorrentPassword": "" }
```

A blank `prowlarrApiKey` is read from `/var/lib/sentinelcore-downloads/prowlarr/config.xml`.

## API

All routes need a signed-in user. Everything except `/status` answers **404**
`{ "error": "Movie downloads are turned off..." }` while off (or while Movies & TV is off), and
**403** `{ "error": "Only an admin can add..." }` to a non-admin when `allowedUsers` is `admins`.
Sites routes are admin only.

### Status

`GET /api/media/downloads/status` - decide whether to show "+ Add":

```json
{ "enabled": true, "canDownload": true, "canManageSites": true, "tmdbConfigured": true,
  "movieLibrary": true, "showLibrary": false,
  "status": { "enabled": true, "services": "running|starting|stopped|failed|notInstalled|external",
              "idleMinutes": 5,
              "vpn": { "state": "connected|connecting|down|off|unknown", "required": true, "ip": "185.65.134.1",
                       "location": "Stockholm, SE", "country": "SE", "provider": "AS39351 31173 Services AB",
                       "checkedAt": "…", "error": null } } }
```

Show "+ Add" when `canDownload`. `services: stopped` with `vpn.state: off` is the normal idle state.

### 1. Find the title (TMDB)

- `GET /api/media/downloads/lookup?q=dune&kind=movie|show` →
  `{ "tmdbConfigured": true, "results": [ { "kind": "movie", "tmdbId": 693134, "title": "Dune: Part Two", "year": 2024,
  "overview": "…", "posterPath": "/abc.jpg", "posterUrl": "https://image.tmdb.org/t/p/w342/abc.jpg",
  "inLibrary": false, "itemId": null } ] }`. With no TMDB key: `{ "tmdbConfigured": false, "results": [] }` -
  the app then searches the sites with the typed text directly (step 2 with just `title`).
- `GET /api/media/downloads/show/{tmdbId}` → the show's seasons for picking:
  `{ "tmdbId", "title", "year", "overview", "posterPath", "posterUrl", "itemId",
  "seasons": [ { "number": 1, "name": "Season 1", "episodeCount": 52, "ownedCount": 10,
  "episodes": [ { "number": 1, "name": "Magic Xylophone", "airDate": "2018-10-01", "aired": true, "inLibrary": true } ] } ] }`.
  Season 0 (specials) is last. `aired: false` episodes are not out yet.

### 2. Search the sites

`POST /api/media/downloads/search` body
`{ "kind": "movie|show", "title": "Dune: Part Two", "year": 2024, "tmdbId": 693134, "posterPath": "/abc.jpg", "season": 2, "episode": 5 }`
→ **202** Search. `season` alone = a whole season (season packs); `season` + `episode` = one episode.
Then poll `GET /api/media/downloads/search/{id}` every second until `phase` is `done` or `failed`:

```json
{ "id": "…", "phase": "connecting|searching|done|failed",
  "message": "Connecting to the VPN...", "error": null,
  "kind": "movie", "title": "…", "year": 2024, "season": null, "episode": null,
  "results": [ Release ], "others": [ Release ] }
```

`message` is what to show under the spinner (or the failure). The first search after idle takes
10-30 s while the VPN and services start. `results` are the releases that are what was asked for
and have people sharing them, best first; `others` are the rest (different title/year/episode,
nobody sharing) - show them collapsed. Searches expire after 30 minutes (404).

```json
Release = { "id": "r1", "title": "Dune.Part.Two.2024.1080p.WEB-DL.DDP5.1.H.264-FLUX", "site": "TheSite",
            "sizeBytes": 6400000000, "seeders": 812, "leechers": 40, "publishedAt": "…",
            "quality": "1080p|2160p|720p|480p|null", "codec": "H.264|HEVC|AV1|XviD|…|null",
            "source": "BluRay|WEB-DL|WEBRip|HDTV|DVD|Remux|CAM|null", "hdr": false,
            "season": null, "episode": null, "seasonPack": false, "matches": true,
            "warning": "Filmed in a cinema - poor picture and sound." }
```

### 3. Download

- `POST /api/media/downloads` body `{ "searchId": "…", "releaseId": "r1", "libraryId": null }` → **201** Download.
  `libraryId` is optional (first local library of the right kind). 400 `{ error }` when there is no
  such library ("Add one (kind: TV shows) under Movies -> Libraries") or the search expired.
- `GET /api/media/downloads` → `{ "status": {…as above}, "active": [ Download ], "recent": [ Download ] }`.
  Poll every 2-3 s while `active` is not empty. `recent` is the last 30 finished.
- `GET /api/media/downloads/{id}` → Download.
- `DELETE /api/media/downloads/{id}` → 204. Cancels an active download (its files are deleted) or
  removes a finished one from the history.
- `POST /api/media/downloads/{id}/retry` → Download (a failed one goes back in the queue).

```json
Download = { "id": "…", "kind": "movie|episode", "title": "Bluey", "year": 2018, "tmdbId": 82728,
             "season": 2, "episode": 5, "subtitle": "S02E05|Season 2|null",
             "posterUrl": "https://image.tmdb.org/…", "releaseTitle": "…", "site": "TheSite",
             "sizeBytes": 400000000, "state": "queued|downloading|seeding|importing|done|failed|canceled",
             "progress": 0.42, "downloadedBytes": 168000000, "speedBytesPerSecond": 3100000, "etaSeconds": 75,
             "peers": 14, "error": null, "libraryId": "…", "itemId": "…|null", "requestedBy": "sam",
             "createdAt": "…", "completedAt": "…|null", "finishedAt": "…|null" }
```

`error` on an active download is a note, not a failure ("Waiting for the VPN to connect.",
"Looking for people sharing it..."). On `done`, `itemId` is the new movie (or the show) in the
library - `GET /api/media/items/{itemId}` answers once the rescan has reached it (usually seconds;
404 until then).

### Sites (admin)

Prowlarr lives inside the VPN, so these start the services; if they are not up within ~25 s the
answer is **503** `{ "error": "Connecting to the VPN... try again in a moment.", "starting": true }` -
show the message and retry.

- `GET /api/media/downloads/sites` → `[ { "id": 3, "name": "…", "enabled": true, "privacy": "public|semiPrivate|private", "language": "en-US", "description": "…", "definition": "…" } ]`
- `GET /api/media/downloads/sites/catalog?q=` → what can be added, public sites first:
  `[ { "definition": "…", "name": "…", "description": "…", "privacy": "public", "language": "en-US",
  "needsSetup": false, "fields": [ { "name": "username", "label": "Username", "type": "text|password|checkbox|select|number", "help": "…", "value": null, "options": [ { "value": "1", "label": "…" } ] } ] } ]`.
  `needsSetup: false` sites can be added with one tap; otherwise show `fields` as a form.
- `POST /api/media/downloads/sites` body `{ "definition": "…", "name": null, "fields": { "username": "…", "password": "…" } }`
  → 201 `{ id, name, enabled, privacy }`. Prowlarr tests the site first; a failure is 400 `{ error }`.
- `PUT /api/media/downloads/sites/{id}` body `{ "enabled": false }` → 200.
- `DELETE /api/media/downloads/sites/{id}` → 204.

## Data

`media-downloads.db` (table `MediaDownloads`) beside `media.db`. History, not configuration: not in
backups. Settings are in the backup like every section (secrets included, as elsewhere).
