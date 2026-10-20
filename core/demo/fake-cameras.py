#!/usr/bin/env python3
"""The demo's four cameras: each loopback port serves its clip forever as MPEG-TS over HTTP.

Core treats http://127.0.0.1:9401 etc. as a camera's stream URL. Unlike `ffmpeg -listen 1`, which
takes one client and exits, every connection here gets its own ffmpeg, so Live, the snapshot
worker and a trigger zone can all read a camera at once. Re-encoding (not -c copy) keeps the
timestamps climbing across each loop, which hls.js otherwise trips over after a few rounds.

    python3 fake-cameras.py <feeds dir>      # serves <dir>/<slug>.mp4 on the ports below
"""
import http.server
import pathlib
import shutil
import socketserver
import subprocess
import sys
import threading

CAMERAS = {9401: "back-yard", 9402: "driveway", 9403: "front-door", 9404: "front-gate"}
FFMPEG = shutil.which("ffmpeg") or "ffmpeg"


def handler_for(clip: pathlib.Path):
    class Feed(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.send_header("Content-Type", "video/mp2t")
            self.end_headers()
            ffmpeg = subprocess.Popen(
                [FFMPEG, "-loglevel", "error", "-re", "-stream_loop", "-1", "-i", str(clip),
                 "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-r", "10",
                 "-g", "20", "-pix_fmt", "yuv420p", "-an", "-f", "mpegts", "pipe:1"],
                stdout=subprocess.PIPE, stdin=subprocess.DEVNULL)
            try:
                while chunk := ffmpeg.stdout.read(64 * 1024):
                    self.wfile.write(chunk)
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                ffmpeg.kill()
                ffmpeg.wait()

        def log_message(self, *args):
            pass

    return Feed


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def main():
    feeds = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "feeds")
    servers = []
    for port, slug in CAMERAS.items():
        clip = feeds / f"{slug}.mp4"
        if not clip.exists():
            sys.exit(f"Missing {clip}")
        server = Server(("127.0.0.1", port), handler_for(clip))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        servers.append(server)
        print(f"{slug} on http://127.0.0.1:{port}", flush=True)
    threading.Event().wait()


if __name__ == "__main__":
    main()
