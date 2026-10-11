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

- **FlareSolverr** (MIT, a headless Chrome under Xvfb, `sentinelcore-flaresolverr.service`,
  `10.250.0.2:8191`, also inside the namespace) gets Prowlarr past Cloudflare "are you human"
  pages (1337x and others). SentinelCore registers it in Prowlarr as an indexer proxy tagged
  `flaresolverr` and puts that tag on every site it adds; Prowlarr only routes a request through
  it when it actually meets a Cloudflare check. Without it such sites fail to add with
  "blocked by CloudFlare Protection".
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

The Music tab's "+ Add" for albums runs on this same stack, settings and sites
(`MusicDownload*.cs`, contract in MUSIC_AND_PODCASTS.md).

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
`/etc/sentinelcore/vpn/` (root only), six systemd units (`sentinelcore-downloads.target` and the
`-vpn`, `-vpn-watch`, `-qbittorrent`, `-prowlarr` and `-flaresolverr` services) and
`/etc/polkit-1/rules.d/50-sentinelcore-downloads.rules`, then starts everything once, prints the
home and VPN addresses and stops it again. Changing VPN later:
`sudo /usr/local/lib/sentinelcore-downloads/sentinelcore-vpn set-config NEWFILE`.
`--uninstall` removes it (keeps data and config).

Elsewhere (Windows, macOS, a qBittorrent you already run): turn **Start And Stop The Services**
off and point the two addresses at your own Prowlarr and qBittorrent, which must run on the same
machine as SentinelCore and already be behind a VPN; turn **Use VPN For Movie Downloads** (`useVpn`)
off only if nothing here can check it.

## Settings section `mediaDownloads` (`PUT /api/settings/media-downloads`)

```json
{ "enabled": false, "useVpn": true,
  "vpnProvider": "installed|mullvad|wireguard",
  "mullvadAccountNumber": "", "mullvadLocation": "se",
  "wireGuardPrivateKey": "", "wireGuardAddress": "", "wireGuardDns": "",
  "wireGuardServerPublicKey": "", "wireGuardServerEndpoint": "",
  "allowedUsers": "admins|everyone", "preferredQuality": "1080p|2160p|720p|any",
  "seedMinutes": 0, "idleMinutes": 5, "manageServices": true,
  "prowlarrUrl": "http://10.250.0.2:9696", "prowlarrApiKey": "",
  "qbittorrentUrl": "http://10.250.0.2:8080", "qbittorrentUsername": "", "qbittorrentPassword": "" }
```

A blank `prowlarrApiKey` is read from `/var/lib/sentinelcore-downloads/prowlarr/config.xml`.

- **`useVpn`** (default on). Off: `sentinelcore-vpn` routes the namespace through the host's own
  connection (nftables masquerade, `net.ipv4.ip_forward` on only while running, restored after)
  instead of the tunnel, the home-address check is skipped and `vpn.state` is `disabled` so every
  app shows an amber warning. SentinelCore passes the choice to the root side as one word in
  `/var/lib/sentinelcore-downloads/network-mode`; anything but `direct` means the VPN.
- **`vpnProvider`**: `installed` = the file `install-downloads.sh --vpn-config` put in
  `/etc/sentinelcore/vpn` (WireGuard or OpenVPN; the only way to use OpenVPN). `mullvad` = only
  the account number: the server makes an X25519 key, registers it once as a device
  (`api.mullvad.net/accounts/v1/devices`), picks an active server in `mullvadLocation` (a country
  `se` or city `se-sto`; owned servers first) and remembers all of it in
  `<data>/media-downloads-mullvad.json` (0600), so restarts reuse the same device. If the tunnel
  will not come up and the device is gone from the account, the next try registers a new one.
  `wireguard` = the five values from any provider's WireGuard file. For both, SentinelCore writes
  `/var/lib/sentinelcore-downloads/vpn/wireguard.conf`, which `sentinelcore-vpn` (root) uses only if
  it is a regular file under 4 KB whose every line is a WireGuard key, address, DNS, MTU, allowed
  IPs, endpoint or keepalive - no PostUp, no paths, no symlinks.
- `GET /api/settings/media-downloads/mullvad-locations` (admin) → `{ "options": [ { "value": "se", "label": "Sweden - any city" }, { "value": "se-sto", "label": "Sweden - Stockholm" } ] }`
  (the Mullvad Location field's options source).
- The tunnel measures the path MTU to the VPN server when it starts (don't-fragment pings; tunnel
  MTU = largest passing size - 80, else 1280) unless the WireGuard file sets `MTU`. Connections
  such as T-Mobile Home Internet carry ~1440 bytes, where the usual 1420 passes the handshake and
  DNS but hangs every TLS connection.

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
              "vpn": { "state": "connected|connecting|down|off|unknown|disabled", "required": true,
                       "verified": true, "verifiedAt": "…", "ip": "185.65.134.1",
                       "location": "Stockholm, SE", "country": "SE", "provider": "AS39351 31173 Services AB",
                       "checkedAt": "…", "error": null,
                       "account": { "provider": "mullvad", "paidUntil": "2026-10-27T12:02:43+00:00", "daysLeft": 26,
                                    "runningOut": false, "expired": false, "checkedAt": "…" } } } }
```

Show "+ Add" when `canDownload`. `services: stopped` with `vpn.state: off` is the normal idle state.
`required` is the `useVpn` setting. `verified` means the address the sites see was compared with
the server's own (api.ipify.org) and differs. `disabled` means Use VPN is off: `ip`, `location`,
`country`, `provider` and `error` are null, and downloads use the home connection.

`vpn.account` is how long the VPN account has left - only for VPN Provider = Mullvad with Use VPN
on, and null until the server has first asked Mullvad (`api.mullvad.net/accounts/v1/accounts/me`,
`expiry`). `daysLeft` is whole days, rounded down; `runningOut` = a week or less left; `expired` =
the time is used up, so Mullvad ignores the server's key and the tunnel cannot connect (the kill
switch still holds: nothing goes over the home connection). The server asks every 12 hours (the
answer is kept in `<data>/media-downloads-mullvad-account.json`), and again before starting the
services whenever the last answer said `expired` or the tunnel failed to come up - so time added on
mullvad.net counts on the very next try. The background pass that keeps a queued download waiting
(every 3 s) re-asks at most every 5 minutes instead - each ask is two Mullvad API calls, and Mullvad
rate-limits sign-ins - so a queued download resumes within 5 minutes of time being added. A search, download or Test VPN while out of time fails
straight away with "Your Mullvad account ran out of time on Tuesday, October 27. Add time at
mullvad.net, then try again." Test VPN's success message ends "Mullvad is paid until …".

The owner is told (email, Telegram and push, like the webcam watchdog - `IOwnerNotifier`) once when
a week or less is left ("Mullvad VPN runs out in 6 days") and once when it runs out ("Mullvad VPN
has run out"); each once per paid-until date, so adding time sets both up again.

**The VPN light** (every app shows it near "+ Add", and the same wording on the Downloads screen):

| look | when | words |
|---|---|---|
| amber | `disabled` | "VPN off" / "Turned off in Settings · searching and downloading use your home connection, so the sites see your home address." |
| red | `services: notInstalled` | "VPN not set up" / "The VPN and torrent services are not installed on the server yet. Run core/downloads/install-downloads.sh on it once." |
| red | `vpn.account.expired` | "VPN out of time" / "Your Mullvad account ran out of time on Oct 27. Add time at mullvad.net and downloads work again." |
| green | `connected` and `verified` | "VPN · SE" ("VPN on" with no country) / "Connected and verified · Stockholm, SE · ip · your home address is hidden" |
| cyan, pulsing | `connected`, not yet `verified` | "VPN checking" / "Connected · Stockholm, SE · ip · checking it hides your home address…" |
| cyan, pulsing | `connecting`, or `services: starting` | "VPN connecting" / "Connecting…" |
| red | `services: failed`, or `vpn.state: down` | "VPN problem" / `vpn.error`, else "The VPN or torrent services failed to start…" |
| grey | `services: external` | "VPN idle" / "qBittorrent and Prowlarr are run outside this server; the VPN is checked when they are used." |
| grey | otherwise (`off` / `unknown`, the normal idle state) | "VPN idle" / "Off until someone searches or downloads, then it connects by itself." |

Rows are checked top to bottom; the first that matches wins.

**The account line** (Downloads screen, under the light; nothing when `vpn.account` is null).
Dates are the device's local date as "Oct 27", with ", 2027" added when not this year:

| look | when | words |
|---|---|---|
| plain (secondary text) | otherwise | "Mullvad paid until Oct 27 · 26 days left" ("1 day left"; "less than a day left" at 0) |
| amber | `runningOut` | "Mullvad runs out in 5 days (Oct 27) · add time at mullvad.net" ("runs out tomorrow", "runs out today" at 1 and 0) |
| red | `expired` | "Mullvad ran out on Oct 27 · add time at mullvad.net" |

`POST /api/media/downloads/vpn/test` (anyone who can download) → `{ "ok": true, "message": "Working. The sites see 185.195.233.76 in Stockholm, SE, not your home address.", "status": {…as above} }`.
Starts everything if needed, so it can take up to a minute; `ok: false` carries what is wrong in
`message`. Show `message` as the result and redraw the light from `status`.

### 1. Find the title (TMDB)

- `GET /api/media/downloads/lookup?q=dune&kind=movie|show` →
  `{ "tmdbConfigured": true, "results": [ { "kind": "movie", "tmdbId": 693134, "title": "Dune: Part Two", "year": 2024,
  "overview": "…", "posterPath": "/abc.jpg", "posterUrl": "https://image.tmdb.org/t/p/w342/abc.jpg",
  "inLibrary": false, "itemId": null } ] }`. With no TMDB key: `{ "tmdbConfigured": false, "results": [] }` -
  the app then searches the sites with the typed text directly (step 2 with just `title`).
- `kind=any` searches every kind that can be downloaded (movie and show today; music joins when
  there is music) and returns one list, each result carrying its own `kind`: titles that are
  exactly what was typed first (ignoring case, punctuation, "&"/"and" and a leading "The"), then
  the kinds taking turns in TMDB's order. Apps skip results whose `kind` they don't know.
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
10-30 s while the VPN and services start. A title starting with "The", "A" or "An" is also searched without it
(some sites find nothing for "The Addams Family" but plenty for "Addams Family"); results are
still matched against the full title. `results` are the releases that are what was asked for
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
- **A movie already in the library** (same TMDB id, or same title and year when either has no
  TMDB id): without `replace` the server queues nothing and answers **409**
  ```json
  { "error": "You already have Dune (2021).", "code": "alreadyInLibrary",
    "existing": { "itemId": "…", "title": "Dune", "year": 2021, "sizeBytes": 4200000000, "height": 720, "canReplace": true } }
  ```
  The app asks "Replace the copy you have?" and sends the same body again with `"replace": true`
  (the old copy's files are deleted once the new one has downloaded and is being filed - see
  `DELETE /api/media/items/{id}` in MOVIES.md for what goes) or `"replace": false` (keep both).
  Offer Replace only when `canReplace` (false for a copy on another computer's share). Episodes
  never ask; the show picker already marks owned episodes.
- `GET /api/media/downloads` → `{ "status": {…as above}, "active": [ Download ], "recent": [ Download ] }`.
  Poll every 2-3 s while `active` is not empty. `recent` is the last 30 finished. Album downloads
  (the Music tab's "+ Add", same queue, VPN and sites - MUSIC_AND_PODCASTS.md "Getting music") are
  left out; they have their own list under `/api/music/downloads`.
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
             "peers": 14, "error": null, "libraryId": "…", "itemId": "…|null", "replacesItemId": "…|null", "requestedBy": "pat",
             "createdAt": "…", "completedAt": "…|null", "finishedAt": "…|null" }
```

`error` on an active download is a note, not a failure ("Waiting for the VPN to connect.",
"Looking for people sharing it..."). On `done`, `itemId` is the new movie (or the show) in the
library - `GET /api/media/items/{itemId}` answers once the rescan has reached it (usually seconds;
404 until then).

### Search offers a download

When searching the library finds nothing, the app offers to download what was typed instead of a
bare "no results". Every app's library search does this (dashboard Movies / TV Shows search boxes;
the Search pane on iPhone, Mac, Apple TV, Android and Windows).

**When:** the trimmed search text is at least 2 characters, the library answered with no items, no
other filter is on (genre, unwatched, my list), and `status.canDownload`. It appears by itself as
soon as the empty result is on screen - no Enter needed - and follows the typing with the same
pause the library search uses; an answer for older text is dropped.

**What:**

- Title **"Not in your library"**, line **"Want to download “dune 2”?"**.
- With `tmdbConfigured`: `GET /lookup?q=<text>&kind=any` and up to **6** results, each a row with
  the poster, title, year, a kind label (**Movie** / **TV show**) and an **In library** chip when
  `inLibrary`. Tapping a row opens the "+ Add" flow already at that title: a movie goes straight to
  the release search (step 2 with its `tmdbId`), a show to its season picker; an `inLibrary` row
  opens the library item instead. While loading show "Looking it up…"; no results show "Nothing on
  TMDB called “dune 2”."; a failed lookup shows its error in small text and keeps the buttons below.
- Always (and the only thing without a TMDB key): **"Search the sites for “dune 2” as a"** with
  the buttons **Movie** and **TV show**, which open "+ Add" at the release search for exactly that
  text (step 2 with just `kind` and `title`), as Add's own "search sites for" does.
- Closing "+ Add" goes back to the search, text and offer still there. Starting a download does
  what "+ Add" does after a download starts on that app.

The kind labels come from a small table in each app (`movie` -> Movie, `show` -> TV show); music
gets a row and a button there when it can be downloaded.

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
