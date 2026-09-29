# Organizer sync protocol

The Organizer holds notes, calendar events, reminders and Trello-style boards. It is shared by the web dashboard and the iOS, macOS, tvOS, Android and Windows apps. Every client keeps its own copy and works offline, and the server is the meeting point. This document is the contract each client implements. The server side is `SentinelCore.Web/OrganizerStore.cs` and `OrganizerEndpoints.cs`, and `SentinelCore.Tests/OrganizerStoreTests.cs` exercises every rule below.

## Model

Everything is an **item**:

| property | meaning |
|---|---|
| `id` | Chosen by the client when the item is created (a UUID is fine; `[A-Za-z0-9_.:-]{1,80}`). It never changes. |
| `kind` | `note`, `event`, `reminder`, `board`, `column`, `card`, `comment` or `attachment`. It never changes. |
| `spaceId` | Which space the item is in (see below). A child always inherits its parent's space. |
| `parentId` | A column belongs to a board, a card to a column, a comment to a card, an attachment to any non-attachment, and a reminder may belong to another reminder (a sub-task). |
| `deleted` | A tombstone. Deleted items stay on the server forever, as a few bytes, so that a stale device can't bring them back. |
| `fields` | A free-form JSON object; see "Fields by kind". |
| `clocks` | One clock per field, plus `spaceId`, `parentId` and `deleted`. |

### Spaces

| id | who sees it |
|---|---|
| `family` | Everyone. This is the only space when login is turned off. |
| `personal:<username>` | That one account. It is created automatically. |
| `group:<hex>` | The accounts listed in `members` (lower-case usernames). |

Spaces are keyed by **username**, not account id. When a server is rebuilt, accounts are recreated with new ids but the same names. A device re-uploading `personal:nathan` then lands in the right place.

A client purges every local item whose `spaceId` is not in the latest `spaces` list from a sync. That is how removal from a group, or a deleted group, reaches a device.

### Clocks and merging

A clock is `<13-digit unix ms>-<4-digit counter>-<device id>`, for example `1790000000000-0000-iphone-8F2A`. The fixed width makes plain ordinal string comparison equal to time order. Each device picks a stable device id and uses a hybrid logical clock:

```
next = max(now_ms, last_ms) ; counter = (next == last_ms) ? last_counter + 1 : 0
```

`last` is the highest clock the device has produced **or received**.

- **Merging is per field: the newest clock wins.** Two devices editing different fields both keep their edits.
- **The server clamps clocks more than 5 minutes ahead of its own time.** A phone whose clock is set to next year can't freeze fields.
- **Deletes don't come undone by accident.** An edit to other fields never undeletes an item. Only `deleted: false` with a newer clock does.
- **Only a comment's author can change it.** With login on, a change to an existing comment from anyone other than its `createdBy` is rejected unless it only sets `deleted`. Anyone who can see the card may delete a comment. Clients show the author from `createdBy`. After a rebuilt server is refilled from one device, every comment that device uploads is credited to that device's account.
- **A note's `body` is the exception.** If a change carries `baseClocks.body`, the server checks it. If the stored body has a different clock and different text, both people rewrote the note. The newer text wins, and the other is saved as a new note titled "`<title>` (conflicted copy)" in the same space. Clients should always send `baseClocks.body`: the body clock the edit started from.

## Endpoints

All endpoints are under `/api/organizer` and use the dashboard session cookie. The one exception is the `.ics` feed. When the Organizer is turned off in Settings, everything except `/status` answers `404`.

| method | path | purpose |
|---|---|---|
| GET | `/status` | `{enabled, serverId, cursor, username, displayName, personalSpaces, itemCount, maxAttachmentBytes}`. Clients hide the Organizer when `enabled` is false. `personalSpaces` is false when login is off. |
| POST | `/sync` | Push and pull in one round trip (see below). |
| GET | `/wait?cursor=N&timeout=25` | Long poll. It answers `{cursor}` as soon as anything changes after `N`, or after the timeout. Then run `/sync`. |
| GET | `/items?kind=&spaceId=` | Everything of one kind this person can see. Light-mode clients use it to browse beyond their offline window. |
| GET | `/people` | `{people:[{username, displayName}]}`, for choosing group members. It is empty when login is off. |
| POST | `/spaces` | Create a group: `{name, members[], color?}`. The creator is always added. |
| PUT | `/spaces/{id}` | Edit a group. Any member may do this, and removing yourself is how you leave. |
| DELETE | `/spaces/{id}` | Delete a group and tombstone everything in it. |
| PUT | `/blobs/{sha256}` | Upload an attachment's bytes (raw body). It is rejected if the bytes don't hash to the name. |
| GET | `/blobs/{sha256}[?download=true]` | Download, allowed only if you can see an attachment pointing at it. Range requests work, and the response is cacheable forever. |
| GET | `/blobs/{sha256}/thumbnail` | A JPEG of at most 512px on the long edge, for images only. Otherwise `404`. |
| GET | `/holidays/sets` | `{sets:[{id, name, description}]}`: the holiday calendars there are. |
| GET | `/holidays?sets=a,b&from=2025&to=2028` | `{from, to, holidays:[{id, setId, name, date, days, note}]}`. At most ten years per request. |
| GET | `/preferences` | `{holidaySets}`: this person's chosen holiday calendars, or `null` if they never chose. |
| PUT | `/preferences` | `{holidaySets:[...]}`. Unknown ids are rejected. |
| GET | `/calendar-feed` | `{path}` of this person's private `.ics` subscription link. |
| POST | `/calendar-feed/reset` | Mint a new link, which kills the old one. |
| GET | `/feed/{token}.ics` | The feed itself, with no session needed; the token is the credential. |

### `POST /sync`

Request:

```json
{
  "serverId": "…or null on the very first sync",
  "cursor": 1234,
  "knownSpaces": ["personal:nathan", "family"],
  "changes": [
    {
      "id": "…", "kind": "card",
      "fields":  { "title": "Paint fence", "parentId": "col-1" },
      "clocks":  { "title": "1790000000000-0000-iphone", "parentId": "1790000000000-0000-iphone" },
      "baseClocks": { "body": "…" }
    }
  ],
  "restoreSpaces": null,
  "limit": 2000
}
```

- **In `changes`, `spaceId`, `parentId` and `deleted` travel inside `fields`,** each with a clock. A new item needs `spaceId`, or a `parentId` whose parent exists or is in the same batch. The batch can be in any order.
- **`knownSpaces`** lists the space ids this device already holds. A space that is visible now but not listed (you were just added to a group) is backfilled in full.

Response:

```json
{
  "serverId": "…",
  "reset": false,
  "cursor": 1301,
  "hasMore": false,
  "spaces":  [{ "id": "family", "kind": "Family", "name": "Family", "members": [], "color": null, "createdBy": null }],
  "items":   [{ "id", "kind", "spaceId", "parentId", "deleted", "fields", "clocks", "seq", "createdAt", "createdBy", "updatedAt", "updatedBy" }],
  "removed": ["item ids to drop locally"],
  "rejected": [{ "id": "…", "error": "human-readable" }],
  "missingBlobs": ["sha256 the server needs someone to upload"]
}
```

- **Structural properties sit at the top level.** In `items`, `spaceId`, `parentId` and `deleted` are top-level properties, not inside `fields`. Tombstones come with `fields: {}`.
- **The reply includes every pushed item, merged.** The device replaces its local copy with it and clears that item's pending changes.
- **`hasMore: true`** means the device should call again straight away with the new cursor.
- **`rejected`** changes will never succeed; for example, you lost access to that space. The device drops them and keeps the server's copy.

## Client algorithm

1. **Local edits** write to the local database immediately. Each edit stamps a fresh clock on every field it touches and records a pending change. Pending changes for the same item coalesce: the newest value per field wins, and the first `baseClocks.body` is kept.
2. **Sync**, on launch, on foreground, on reconnect, after local edits (debounced about 1s), when `/wait` returns, and on a silent push:
   - Send the pending changes, cursor, `knownSpaces` and stored `serverId`.
   - If `reset` is true, go to step 4.
   - Otherwise, apply the reply in this order: `removed`, then `items` (tombstones delete locally), then purge spaces not in `spaces`. Then store `cursor` and `serverId`, and repeat while `hasMore`.
   - Only clear a pending change once the reply has echoed that item. If the request fails, keep everything and retry later. Sync is idempotent.
   - **The silent push.** After a `/sync` that changed something, the server sends every registered device a silent push (APNs background push / FCM data-only message) with `eventType: "organizer-changed"`. It is coalesced: one push about 3 seconds after a burst of edits, and at most one a minute. It carries no data; the device just runs one sync (no `/wait`) and refreshes its Home Screen widgets. It is only sent when the server pushes directly, never through the push relay, and never to the Apple TV. The apps also run a periodic background sync (iOS background refresh, Android WorkManager about every 30 minutes) as a fallback.
3. **Attachments.** Hash the file with SHA-256, copy it into local storage, and create the `attachment` item at once; offline is fine. Upload with `PUT /blobs/{hash}` whenever online. Also upload any hash listed in `missingBlobs` that exists locally. That is how a rebuilt server gets its pictures back.
4. **Rebuilt server (`reset: true`).** The server this device last spoke to is gone, and nothing was applied.
   - If the device holds data, ask its owner: "This server doesn't have your Organizer data (it has N items). Upload this device's copy?" Use `itemCount` from `/status`.
   - On yes: sync again with the new `serverId`, `cursor: 0`, every local item as a change (all fields with their stored clocks, plus `spaceId`/`parentId`/`deleted`), and `restoreSpaces` = the local group definitions. Then upload the missing blobs.
   - On no: wipe the local copy, including pending changes (they describe items on the old server), and take the server's.
   - On "not now": keep the local copy and its pending changes, store the new `serverId` as `declinedReset`, and stop syncing (and long-polling `/wait`) with that server. A later `reset` from the same server doesn't ask again. Settings offers the question back; answering it, or a normal sync reply (the old server came back), clears `declinedReset`. A different server answering asks afresh.
5. **Light mode**, per device and chosen in that app's settings. Prune after each sync:
   - Past events whose end is before the window, and completed reminders completed before it, are dropped.
   - Notes not updated within the window are dropped, unless pinned (the note's own `pinned` field, or pinned locally with "keep offline").
   - Cards stay while they're still open on a board; only completed cards not updated within the window are dropped. A board missing cards offline would look wrong.
   - Boards and columns are always kept. Children (attachments, steps) go with their parent, and a kept item keeps its ancestors.
   - Items with pending changes are never pruned.
   - With pictures set to thumbnails, keep `/thumbnail` only and fetch originals on open.
   - Widening the window resets the cursor to 0 for one full sync.

## Fields by kind

Unknown fields must be preserved and round-tripped, so an older client never erases a newer client's data. Dates are ISO 8601. `position` is a float, ordered ascending; to insert between two neighbours, use their midpoint. `links` is `[{url, title?}]`. `alerts` is minutes-before, as a number array. Colors are CSS hex strings.

| kind | fields |
|---|---|
| **note** | `title`, `body` (Markdown, with `- [ ]` checklists), `pinned`, `color`, `tags[]`, `archived`, `links` |
| **event** | `title`, `notes`, `location`, `start`, `end`, `allDay`, `timeZone` (IANA), `recurrence`, `exdates[]`, `seriesId` + `originalStart`, `alerts`, `color`, `links` |
| **reminder** | `title`, `notes`, `due`, `allDay`, `recurrence`, `priority` (0 none, 1 low, 2 medium, 3 high), `flagged`, `completed`, `completedAt`, `alerts`, `links`, `position` |
| **board** | `title`, `color`, `archived`, `position` |
| **column** | `title`, `position` |
| **card** | `title`, `description` (Markdown), `position`, `labels[{name,color}]`, `due`, `completed`, `checklist[{id,text,done}]`, `assignees[]` (usernames), `cover` (attachment id), `links` |
| **comment** | `body` (plain text), `editedAt` (set when the author changes `body` after posting). Shown oldest first, by `createdAt`. |
| **attachment** | `hash` (required), `name`, `contentType`, `size`, `width`, `height`, `position` |

Details for events:
- **`start`** has an offset (`"2026-09-23T17:00:00-05:00"`), or is `"YYYY-MM-DD"` when `allDay` is true.
- **`end`** is exclusive for all-day events.
- **`recurrence`** is an RRULE value without the `RRULE:` prefix, for example `FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261231T000000Z`. Clients must support `FREQ` of `DAILY`, `WEEKLY`, `MONTHLY` or `YEARLY`, plus `INTERVAL`, `BYDAY`, `BYMONTHDAY`, `COUNT` and `UNTIL`.
- **Editing one occurrence** of a repeating event adds its original start to the series' `exdates` and creates a new event with `seriesId` = the series id and `originalStart`.

Details for reminders:
- **Completing a repeating reminder** moves `due` to the next occurrence and leaves `completed` false.

## Holiday calendars

People can add holiday calendars to the calendar: US federal holidays, US observances, Christian, Orthodox Christian, Jewish, Islamic and Hindu holidays, and Lunar New Year with the Chinese festivals. The server works them out (`SentinelCore.Web/OrganizerHolidays.cs`), because several need a Hebrew, Umm al-Qura or Chinese calendar. **None is shown unless the person turns it on.**

- **They aren't items.** They are all-day, read-only, and never synced as changes. `id` is stable (`holiday:<set>:<yyyyMMdd>:<slug>`). `days` is how many days it lasts: 8 for Hanukkah, 7 for Sukkot. `note` is optional, for example "Begins at sundown the evening before."
- **The choice is per person, not per device.** It is kept on the server, so turning on Jewish holidays on the phone shows them on the TV too. Each client also keeps it locally, with a "not yet sent" flag, so it works offline.
- **Clients cache the dates.** Each client caches the holidays for last year through two years ahead, keyed by the chosen sets, and shows them offline. It also caches the set list, so the settings screen works offline once it has been fetched.
- **Keeping it current.** After a successful sync, and at most every few minutes, the client does the following:
  - If the local choice hasn't been sent yet, it PUTs it.
  - Otherwise it GETs `/preferences`. If that is `null` and the device has a choice (a rebuilt server), it PUTs the device's. If not, it adopts the server's.
  - It then fetches `/holidays` again if the sets or the year range differ from what's cached.
- **The subscription feed.** The `.ics` feed includes the chosen holidays, last year through two years ahead.

## Board automations

Rules that change boards on their own ("every morning at 7, create *pick up eggs* in New items"; "when a card is moved into Done, mark it done") run **on the server**, in `SentinelCore.Web/OrganizerAutomationEngine.cs`. They are built in the web dashboard, stored in `organizer-automations.db` (and the server backup), and are **not items**: they never sync, and clients need nothing new to support them.

What a rule does reaches devices as ordinary changes from `ApplyChangesAsync`, made as the person who last saved the rule. Their clocks end in `-automation` (for example `1790000000000-0000-automation`), and the server sends the usual silent push. Clients must not special-case that device id. It exists so the engine can tell its own changes from people's.

Endpoints, under `/api/organizer`: `GET /automations?boardId=`, `POST /automations`, `PUT` and `DELETE /automations/{id}`, `POST /automations/{id}/run` (scheduled rules only), `GET /automations/{id}/runs`, and `POST /automations/preview` (body: a schedule; answers the next three run times).

## Alerts

Event and reminder alerts are scheduled by each device as **local notifications** from its own copy. They fire with no signal and with the server down. Rescheduling after every sync is the simplest correct approach, because the platforms cap how many pending notifications an app may have. Schedule the nearest few dozen.
