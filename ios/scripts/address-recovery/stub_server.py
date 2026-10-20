"""Stand-in for the remote Sentinel Core address in check-address-recovery.sh.

Answers /api/auth/status (the reachability probe) and /api/echo, and counts every request by
"METHOD path" so the check can prove a command was sent exactly once. /api/slow answers only after
the client has given up, to produce a timeout that did reach the server. GET /counts reports.
"""
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

counts = {}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def handle_any(self):
        path = self.path.split("?")[0]
        if path != "/counts":
            key = f"{self.command} {path}"
            counts[key] = counts.get(key, 0) + 1
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)
        if path == "/api/slow":
            time.sleep(3)
        body = json.dumps(counts if path == "/counts" else {"ok": True, "path": self.path}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except BrokenPipeError:
            pass

    do_GET = handle_any
    do_POST = handle_any


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Handler)
    server.serve_forever()
