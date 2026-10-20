#!/usr/bin/env python3
"""The App Review demo server's data: build it, seed it, rebuild it every night.

Runs as the demo's own user (scdemo), never as the real install's user. demo.sh (root) calls it:

    demo.py secrets      make the admin/reviewer passwords and the Bridge key, once
    demo.py media        generate the invented movies, show and albums from catalog.json, once
    demo.py build-live   wipe live/ and lay down settings, cameras and events, dated up to now
    demo.py seed         with both demo services running: accounts, libraries, organizer, files
    demo.py login        print the reviewer login for App Store Connect / Play Console

Paths: SCDEMO_ROOT (default /srv/scdemo) holds app/ (the copied binaries), kit/ (this folder),
media/, live/ (rebuilt every reset) and secrets/. Standard library only.
"""
import base64
import datetime as dt
import hashlib
import http.cookiejar
import json
import os
import pathlib
import secrets as rand
import shutil
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from zoneinfo import ZoneInfo

KIT = pathlib.Path(__file__).resolve().parent
ROOT = pathlib.Path(os.environ.get("SCDEMO_ROOT", "/srv/scdemo"))
APP = pathlib.Path(os.environ.get("SCDEMO_APP", ROOT / "app"))
LIVE = ROOT / "live"
SECRETS = ROOT / "secrets"
MEDIA = ROOT / "media"
WEB_PORT = int(os.environ.get("SCDEMO_WEB_PORT", "3360"))
INGEST_PORT = int(os.environ.get("SCDEMO_INGEST_PORT", "5091"))
ZONE = ZoneInfo("America/Denver")
ADMIN = "demo-admin"
REVIEWER = "appreview"


# ---------------------------------------------------------------- secrets

def secret(name, make):
    path = SECRETS / name
    if not path.exists():
        SECRETS.mkdir(parents=True, exist_ok=True)
        os.chmod(SECRETS, 0o700)
        path.write_text(make())
        os.chmod(path, 0o600)
    return path.read_text().strip()


def password():
    # Easy to type from a review note: four groups of letters and digits, no look-alikes.
    alphabet = "abcdefghjkmnpqrstuvwxyz23456789"
    return "-".join("".join(rand.choice(alphabet) for _ in range(5)) for _ in range(4))


def cmd_secrets():
    secret("admin-password", password)
    secret("reviewer-password", password)
    secret("bridge-key", lambda: rand.token_hex(32))
    print(f"Secrets are in {SECRETS} (mode 600).")


def pbkdf2(pw):
    # PasswordHasher.cs: PBKDF2.SHA256.<iterations>.<b64 salt>.<b64 hash>, 16-byte salt, 32-byte hash.
    salt = os.urandom(16)
    digest = hashlib.pbkdf2_hmac("sha256", pw.encode(), salt, 210_000, 32)
    return f"PBKDF2.SHA256.210000.{base64.b64encode(salt).decode()}.{base64.b64encode(digest).decode()}"


# ---------------------------------------------------------------- media

def ffmpeg(*args):
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", *args], check=True)


def visual(kind, palette, seconds):
    a, b = (c.lstrip("#") for c in palette)
    return {
        "gradients": f"gradients=s=1280x720:r=24:d={seconds}:speed=0.01:c0=0x{a}:c1=0x{b}:n=2",
        "life": f"life=s=320x180:r=24:mold=10:ratio=0.12:death_color=0x{a}:life_color=0x{b},scale=1280:720:flags=neighbor",
        "mandelbrot": "mandelbrot=s=1280x720:r=24",
        "cellauto": f"cellauto=s=320x180:r=24:rule=110:random_fill_ratio=0.5,scale=1280:720:flags=neighbor,"
                    f"colorize=hue={abs(hash(a)) % 360}:saturation=0.5:lightness=0.3",
    }[kind]


def tone(base, seconds, variant=0):
    # A slow three-note chord with a gentle swell: obviously generated, never anyone's song.
    f1, f2, f3 = base, base * 1.25, base * 1.5
    swell = 0.15 + 0.05 * (variant % 3)
    expr = (f"(0.16*sin(2*PI*{f1:.2f}*t)+0.11*sin(2*PI*{f2:.2f}*t)+0.08*sin(2*PI*{f3:.2f}*t))"
            f"*(0.65+0.35*sin(2*PI*{swell:.2f}*t))")
    return f"aevalsrc='{expr}':s=44100:d={seconds}", f"afade=t=in:d=2,afade=t=out:st={seconds - 3}:d=3"


def video(path, kind, palette, seconds, base):
    source, fades = tone(base, seconds)
    ffmpeg("-f", "lavfi", "-t", str(seconds), "-i", visual(kind, palette, seconds), "-f", "lavfi", "-i", source,
           "-af", fades, "-vf", "format=yuv420p", "-c:v", "libx264", "-preset", "veryfast", "-crf", "30",
           "-g", "48", "-c:a", "aac", "-b:a", "96k", "-shortest", "-movflags", "+faststart", str(path))


def cmd_media():
    catalog_bytes = (KIT / "catalog.json").read_bytes()
    stamp = MEDIA / ".catalog-sha256"
    digest = hashlib.sha256(catalog_bytes).hexdigest()
    if stamp.exists() and stamp.read_text() == digest:
        print("Media is up to date.")
        return
    catalog = json.loads(catalog_bytes)
    art = KIT / "seed" / "art"
    if MEDIA.exists():
        shutil.rmtree(MEDIA)
    for i, m in enumerate(catalog["movies"]):
        name = f"{m['title']} ({m['year']})"
        folder = MEDIA / "Movies" / name
        folder.mkdir(parents=True)
        video(folder / f"{name}.mp4", m["visual"], m["palette"], 90, 196 + 12 * i)
        shutil.copy(art / f"movie-{m['slug']}.jpg", folder / "poster.jpg")
        print("movie", name, flush=True)
    for s in catalog["shows"]:
        show = MEDIA / "Shows" / s["title"]
        show.mkdir(parents=True)
        shutil.copy(art / f"show-{s['slug']}.jpg", show / "poster.jpg")
        for e in s["episodes"]:
            season = show / f"Season {e['season']:02d}"
            season.mkdir(exist_ok=True)
            file = season / f"{s['title']} - S{e['season']:02d}E{e['episode']:02d} - {e['title']}.mp4"
            video(file, e["visual"], s["palette"], 60, 220 + 15 * e["episode"])
            print("episode", file.name, flush=True)
    for a in catalog["albums"]:
        folder = MEDIA / "Music" / a["artist"] / a["title"]
        folder.mkdir(parents=True)
        shutil.copy(art / f"album-{a['slug']}.jpg", folder / "cover.jpg")
        for n, title in enumerate(a["tracks"], 1):
            base = a["base"] * 2 ** ((n * 2) / 12)
            source, fades = tone(base, 75, n)
            ffmpeg("-f", "lavfi", "-i", source, "-af", fades, "-c:a", "aac", "-b:a", "128k",
                   "-metadata", f"title={title}", "-metadata", f"artist={a['artist']}",
                   "-metadata", f"album_artist={a['artist']}", "-metadata", f"album={a['title']}",
                   "-metadata", f"track={n}/{len(a['tracks'])}", "-metadata", f"date={a['year']}",
                   "-metadata", f"genre={a['genre']}", str(folder / f"{n:02d} - {title}.m4a"))
        print("album", a["title"], flush=True)
    stamp.write_text(digest)


# ---------------------------------------------------------------- live data

def fill(text, extra=None):
    values = {"ROOT": str(ROOT), "LIVE": str(LIVE), "WEB_PORT": str(WEB_PORT), "INGEST_PORT": str(INGEST_PORT)}
    values.update(extra or {})
    for key, value in values.items():
        text = text.replace(f"@{key}@", value)
    if "@" in text and any(f"@{k}@" in text for k in ("ADMIN_HASH", "BRIDGE_KEY")):
        sys.exit("A placeholder was left unfilled.")
    return text


def merge(base, over):
    for key, value in over.items():
        if key == "_comment":
            continue
        if isinstance(value, dict) and isinstance(base.get(key), dict):
            merge(base[key], value)
        else:
            base[key] = value
    return base


def iso(moment):
    # The .NET "O" format the stores write: 7 fractional digits, Z.
    return moment.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z"


def parse(text):
    # "O" carries 7 fractional digits; Python's parser takes at most 6.
    stamp, _, fraction = text.rstrip("Z").partition(".")
    return dt.datetime.fromisoformat(f"{stamp}.{(fraction or '0')[:6]:0<6}+00:00")


def shift_dates(db_path, table, columns, delta):
    db = sqlite3.connect(db_path)
    rows = db.execute(f"SELECT rowid, {', '.join(columns)} FROM {table}").fetchall()
    for rowid, *values in rows:
        moved = [iso(parse(v) + delta) if v else v for v in values]
        db.execute(f"UPDATE {table} SET {', '.join(c + '=?' for c in columns)} WHERE rowid=?", (*moved, rowid))
    db.commit()
    db.close()


def replace_paths(db_path, table, columns):
    db = sqlite3.connect(db_path)
    for column in columns:
        db.execute(f"UPDATE {table} SET {column}=replace(replace({column}, '@INGEST@', ?), '@WEB@', ?)",
                   (str(LIVE / "ingest" / "data"), str(LIVE / "web" / "data")))
    db.commit()
    db.close()


def cmd_build_live():
    admin_hash = pbkdf2(secret("admin-password", password))
    bridge = secret("bridge-key", lambda: rand.token_hex(32))
    if LIVE.exists():
        shutil.rmtree(LIVE)
    ingest, web = LIVE / "ingest", LIVE / "web"
    for folder in (ingest / "data" / "images" / "demo", web / "data", web / "homekit-empty", web / "no-vpn"):
        folder.mkdir(parents=True)

    defaults = json.loads((APP / "ingest-defaults.json").read_text())
    overrides = json.loads(fill((KIT / "settings" / "ingest.overrides.json").read_text(),
                                {"ADMIN_HASH": admin_hash, "BRIDGE_KEY": bridge}))
    (ingest / "appsettings.json").write_text(json.dumps(merge(defaults, overrides), indent=2))
    web_settings = json.loads(fill((KIT / "settings" / "web.appsettings.json").read_text()))
    web_settings.pop("_comment", None)
    (web / "appsettings.json").write_text(json.dumps(web_settings, indent=2))
    for path in (ingest / "appsettings.json", web / "appsettings.json"):
        os.chmod(path, 0o600)

    # The dashboard finds wwwroot and Fonts under its content root, and both apps read ../docs.
    (web / "wwwroot").symlink_to(APP / "SentinelCore.Web" / "wwwroot")
    (web / "Fonts").symlink_to(APP / "SentinelCore.Web" / "Fonts")
    (LIVE / "docs").symlink_to(APP / "docs")

    seed = KIT / "seed" / "cameras"
    for name in ("cameras.db", "live-order.db", "triggers.db"):
        shutil.copy(seed / name, web / "data" / name)
    shutil.copy(seed / "events.db", ingest / "data" / "events.db")
    for image in (seed / "images").iterdir():
        shutil.copy(image, ingest / "data" / "images" / "demo" / image.name)
    shutil.copytree(seed / "trigger-clips", web / "data" / "trigger-clips")
    replace_paths(ingest / "data" / "events.db", "AlertEvents", ["ImagePath"])
    replace_paths(web / "data" / "triggers.db", "TriggerEvents", ["ImagePath", "ClipPath"])

    # Bring the newest event to about an hour ago, so the feed reads "today" and "yesterday".
    db = sqlite3.connect(ingest / "data" / "events.db")
    newest = parse(db.execute("SELECT max(OccurredAt) FROM AlertEvents").fetchone()[0])
    db.close()
    delta = dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=1) - newest
    shift_dates(ingest / "data" / "events.db", "AlertEvents", ["OccurredAt", "ReceivedAt", "CompletedAt"], delta)
    shift_dates(web / "data" / "triggers.db", "TriggerEvents", ["OccurredAt"], delta)
    print(f"Built {LIVE} (events moved by {delta.days} days).")


# ---------------------------------------------------------------- seeding through the API

class Api:
    def __init__(self, port):
        self.base = f"http://127.0.0.1:{port}"
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, method, path, body=None, raw=None, content_type=None, ok=(200, 201, 202, 204)):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        request = urllib.request.Request(self.base + path, data=data, method=method)
        if data is not None:
            request.add_header("Content-Type", content_type or "application/json")
        try:
            with self.opener.open(request, timeout=60) as response:
                text = response.read().decode() or "null"
                return json.loads(text) if response.headers.get_content_type() == "application/json" else text
        except urllib.error.HTTPError as error:
            if error.code in ok:
                return None
            raise SystemExit(f"{method} {path} -> {error.code}: {error.read().decode()[:300]}")

    def wait(self, seconds=180):
        deadline = time.time() + seconds
        while time.time() < deadline:
            try:
                with urllib.request.urlopen(self.base + "/api/auth/status", timeout=5) as response:
                    if response.status == 200:
                        return
            except OSError:
                pass
            time.sleep(2)
        sys.exit(f"The demo dashboard never answered on {self.base}.")


class Clock:
    def __init__(self):
        self.last = 0
        self.counter = 0

    def __call__(self):
        now = int(time.time() * 1000)
        if now <= self.last:
            self.counter += 1
        else:
            self.last, self.counter = now, 0
        return f"{self.last:013d}-{self.counter:04d}-demo-seed"


def organizer_changes():
    clock = Clock()
    today = dt.datetime.now(ZONE).replace(hour=0, minute=0, second=0, microsecond=0)

    def at(days, hour, minute=0):
        return (today + dt.timedelta(days=days, hours=hour, minutes=minute)).isoformat(timespec="seconds")

    changes = []

    def item(kind, fields, space="family", parent=None):
        item_id = str(uuid.uuid4())
        fields = dict(fields)
        if parent:
            fields["parentId"] = parent
        else:
            fields["spaceId"] = space
        changes.append({"id": item_id, "kind": kind, "fields": fields, "clocks": {k: clock() for k in fields}})
        return item_id

    item("note", {"title": "Groceries", "pinned": True, "body":
         "- [ ] Milk\n- [ ] Eggs\n- [x] Coffee beans\n- [ ] Apples\n- [ ] Bread\n- [ ] Dish soap"})
    item("note", {"title": "Paint colors for the hallway", "color": "#f6c56b", "tags": ["house"], "body":
         "Front runner: **Warm Linen**.\n\nSecond choice: Harbor Gray. Pick up two sample pots before Saturday."})
    item("note", {"title": "Babysitter notes", "body":
         "Bedtime is 8:00. Pizza money is in the kitchen drawer. The back door sticks - lift the handle."})
    item("reminder", {"title": "Take out the recycling", "due": at(1, 19), "recurrence": "FREQ=WEEKLY", "priority": 2})
    item("reminder", {"title": "Renew the car registration", "due": at(5, 9), "flagged": True, "priority": 3})
    item("reminder", {"title": "Book the furnace tune-up", "due": at(9, 10), "priority": 1})
    item("reminder", {"title": "Return library books", "due": at(-1, 17), "completed": True,
                      "completedAt": at(-1, 16)})
    item("event", {"title": "Soccer practice", "location": "Riverside Park", "start": at(1, 17, 30),
                   "end": at(1, 19), "timeZone": "America/Denver", "recurrence": "FREQ=WEEKLY", "alerts": [30]})
    item("event", {"title": "Dentist", "start": at(3, 9), "end": at(3, 10), "timeZone": "America/Denver", "alerts": [60]})
    item("event", {"title": "Family dinner at Grandma's", "start": at(6, 18), "end": at(6, 20, 30),
                   "timeZone": "America/Denver"})
    item("event", {"title": "School picture day", "allDay": True,
                   "start": (today + dt.timedelta(days=4)).date().isoformat(),
                   "end": (today + dt.timedelta(days=5)).date().isoformat()})
    board = item("board", {"title": "Weekend projects", "color": "#9bd65b", "position": 1})
    columns = {name: item("column", {"title": name, "position": i}, parent=board)
               for i, name in enumerate(["To do", "Doing", "Done"], 1)}
    item("card", {"title": "Fix the squeaky gate", "position": 1, "labels": [{"name": "Outside", "color": "#5b8cff"}]},
         parent=columns["To do"])
    item("card", {"title": "Clean the gutters", "position": 2, "due": at(6, 10)}, parent=columns["To do"])
    item("card", {"title": "Build the garden bench", "position": 1, "description": "Cedar boards are in the garage.",
                  "checklist": [{"id": "c1", "text": "Cut the legs", "done": True},
                                {"id": "c2", "text": "Sand everything", "done": False},
                                {"id": "c3", "text": "Two coats of stain", "done": False}]}, parent=columns["Doing"])
    item("card", {"title": "Hang the porch light", "position": 1, "completed": True}, parent=columns["Done"])
    return changes


def wait_for_scans(api, path, seconds=600):
    deadline = time.time() + seconds
    while time.time() < deadline:
        status = api.call("GET", path) or {}
        if not status.get("scan"):
            return
        time.sleep(3)


def cmd_seed():
    api = Api(WEB_PORT)
    api.wait()
    api.call("POST", "/api/auth/login", {"username": ADMIN, "password": secret("admin-password", password)})
    api.call("POST", "/api/accounts", {"username": REVIEWER, "password": secret("reviewer-password", password),
                                       "role": "Viewer", "displayName": "App Review"})

    api.call("POST", "/api/media/libraries", {"name": "Movies", "kind": "movies", "path": str(MEDIA / "Movies")})
    api.call("POST", "/api/media/libraries", {"name": "TV Shows", "kind": "shows", "path": str(MEDIA / "Shows")})
    api.call("POST", "/api/music/libraries", {"name": "Music", "path": str(MEDIA / "Music")})
    # Adding a library starts its scan; these only make sure, so "already running" (409) is fine.
    api.call("POST", "/api/media/scan", ok=(202, 409))
    api.call("POST", "/api/music/scan", ok=(202, 409))

    reply = api.call("POST", "/api/organizer/sync", {"serverId": None, "cursor": 0, "knownSpaces": [],
                                                     "changes": organizer_changes(), "limit": 2000})
    if reply and reply.get("rejected"):
        sys.exit(f"Organizer rejected: {reply['rejected']}")

    files = {
        "Household/Emergency contacts.txt": "Poison control: 1-800-222-1222\nPlumber: Rivera Plumbing (call the office)\n",
        "Household/Wi-Fi for guests.txt": "Network: Maple-Guest\nPassword: on the fridge\n",
        "Recipes/Pancakes.md": "# Saturday pancakes\n\n- 2 cups flour\n- 2 eggs\n- 1.5 cups milk\n- A pinch of salt\n",
        "School/Fall schedule.txt": "Mon/Wed soccer, Thu piano, Fri library day.\n",
    }
    for path, text in files.items():
        api.call("PUT", "/api/family-files/files/" + urllib.parse.quote(path), raw=text.encode(),
                 content_type="application/octet-stream")
    for image in sorted((KIT / "seed" / "art").glob("album-*.jpg"))[:2]:
        api.call("PUT", "/api/family-files/files/" + urllib.parse.quote(f"Photos/{image.stem}.jpg"),
                 raw=image.read_bytes(), content_type="image/jpeg")

    wait_for_scans(api, "/api/media/status")
    wait_for_scans(api, "/api/music/status")
    print("Seeded: reviewer account, Movies, TV Shows, Music, Organizer, Family Files.")


def cmd_login():
    print(f"Server:   https://demo.nr5p.com\nUsername: {REVIEWER}\nPassword: {secret('reviewer-password', password)}")


def main():
    commands = {"secrets": cmd_secrets, "media": cmd_media, "build-live": cmd_build_live,
                "seed": cmd_seed, "login": cmd_login}
    if len(sys.argv) != 2 or sys.argv[1] not in commands:
        sys.exit(__doc__)
    commands[sys.argv[1]]()


if __name__ == "__main__":
    main()
