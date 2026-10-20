#!/usr/bin/env python3
"""Draws the demo's movie and show posters and album covers into core/demo/seed/art.

Run on the Mac (it drives a Chromium-family browser headless): python3 core/demo/tools/render-art.py
The output is committed, so the media box never needs a browser. Re-run after editing catalog.json.
"""
import json
import pathlib
import subprocess
import sys
import tempfile
from html import escape

HERE = pathlib.Path(__file__).resolve().parent
DEMO = HERE.parent
OUT = DEMO / "seed" / "art"
BROWSERS = [
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
]

PAGE = """<!doctype html><html><head><meta charset="utf-8"><style>
html,body{{margin:0;width:{w}px;height:{h}px;overflow:hidden}}
body{{background:radial-gradient(circle at 30% 25%,{b} 0%,{a} 70%);font-family:Georgia,'Times New Roman',serif;color:#fff;position:relative}}
.orb{{position:absolute;border-radius:50%;background:{b};opacity:.35;filter:blur(2px)}}
.title{{position:absolute;left:8%;right:8%;bottom:{tb}%;font-size:{ts}px;line-height:1.05;font-weight:bold;letter-spacing:.5px;text-shadow:0 3px 12px rgba(0,0,0,.45)}}
.sub{{position:absolute;left:8%;right:8%;bottom:{sb}%;font:italic {ss}px Georgia,serif;opacity:.85}}
.top{{position:absolute;left:8%;top:6%;font:600 {ks}px -apple-system,Helvetica,sans-serif;letter-spacing:4px;text-transform:uppercase;opacity:.75}}
</style></head><body>
<div class="orb" style="width:{o1}px;height:{o1}px;right:-8%;top:12%"></div>
<div class="orb" style="width:{o2}px;height:{o2}px;left:-10%;top:48%;opacity:.2"></div>
<div class="top">{kicker}</div><div class="title">{title}</div><div class="sub">{sub}</div>
</body></html>"""


def browser():
    for path in BROWSERS:
        if pathlib.Path(path).exists():
            return path
    sys.exit("No Brave or Chrome in /Applications.")


def shoot(exe, html, w, h, out_png):
    with tempfile.TemporaryDirectory() as tmp:
        page = pathlib.Path(tmp) / "art.html"
        page.write_text(html)
        subprocess.run([exe, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                        # Without these two a fresh profile waits on a keychain prompt and hangs.
                        "--no-first-run", "--use-mock-keychain", "--password-store=basic",
                        f"--user-data-dir={tmp}/profile", f"--window-size={w},{h}",
                        f"--screenshot={out_png}", page.as_uri()],
                       check=True, capture_output=True, timeout=60)


def to_jpg(png, jpg):
    subprocess.run(["sips", "-s", "format", "jpeg", "-s", "formatOptions", "85", str(png), "--out", str(jpg)],
                   check=True, capture_output=True)
    png.unlink()


def main():
    exe = browser()
    catalog = json.loads((DEMO / "catalog.json").read_text())
    OUT.mkdir(parents=True, exist_ok=True)
    jobs = []
    for m in catalog["movies"]:
        jobs.append((f"movie-{m['slug']}", 600, 900, m["palette"], "A Sentinel Demo Picture", m["title"], m["tagline"], 70))
    for s in catalog["shows"]:
        jobs.append((f"show-{s['slug']}", 600, 900, s["palette"], "Series", s["title"], s["tagline"], 70))
    for a in catalog["albums"]:
        jobs.append((f"album-{a['slug']}", 800, 800, a["palette"], a["artist"], a["title"], str(a["year"]), 84))
    for name, w, h, (a, b), kicker, title, sub, size in jobs:
        html = PAGE.format(w=w, h=h, a=a, b=b, kicker=escape(kicker), title=escape(title), sub=escape(sub),
                           ts=size, ss=round(size * 0.38), ks=round(size * 0.24),
                           tb=16 if h > w else 20, sb=9 if h > w else 11, o1=round(w * 0.7), o2=round(w * 0.55))
        png = OUT / f"{name}.png"
        shoot(exe, html, w, h, png)
        to_jpg(png, OUT / f"{name}.jpg")
        print(f"{name}.jpg")


if __name__ == "__main__":
    main()
