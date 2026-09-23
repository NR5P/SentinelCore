#!/usr/bin/env python3
"""Upload an .aab to a Google Play track through the Play Developer API.

No third-party packages: the service account's RS256 assertion is signed with the openssl
binary and the HTTP goes through curl (which trusts the macOS keychain - the python.org build's
urllib ships without root certificates). Same one-command shape as the TestFlight scripts.

    play-upload.py --key ~/sentinelcore-play-publisher.json --bundle app-release.aab
    play-upload.py --key ... --check          # just prove the key works (no upload)

Steps, all through one "edit" so a failure part-way leaves the track untouched:
  1. exchange a signed JWT for an access token
  2. edits.insert            -> edit id
  3. edits.bundles.upload    -> version code Google saw in the bundle
  4. edits.tracks.update     -> put that version code on the track as a completed rollout
  5. edits.commit            -> publish (testers get it within minutes)
"""
import argparse
import base64
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.parse

API = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications"
UPLOAD_API = "https://androidpublisher.googleapis.com/upload/androidpublisher/v3/applications"
SCOPE = "https://www.googleapis.com/auth/androidpublisher"


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def access_token(key: dict) -> str:
    now = int(time.time())
    header = b64url(json.dumps({"alg": "RS256", "typ": "JWT"}).encode())
    claims = b64url(json.dumps({
        "iss": key["client_email"],
        "scope": SCOPE,
        "aud": key["token_uri"],
        "iat": now,
        "exp": now + 3600,
    }).encode())
    unsigned = f"{header}.{claims}".encode()

    # openssl wants the key in a file; keep it private and short-lived.
    fd, key_path = tempfile.mkstemp(suffix=".pem")
    try:
        with os.fdopen(fd, "w") as f:
            os.chmod(key_path, 0o600)
            f.write(key["private_key"])
        signature = subprocess.run(
            ["openssl", "dgst", "-sha256", "-sign", key_path],
            input=unsigned, capture_output=True, check=True).stdout
    finally:
        os.unlink(key_path)

    assertion = f"{unsigned.decode()}.{b64url(signature)}"
    body = urllib.parse.urlencode({
        "grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer",
        "assertion": assertion,
    })
    return http("POST", key["token_uri"], body.encode(), "application/x-www-form-urlencoded")["access_token"]


class PlayError(Exception):
    def __init__(self, status: int, body: str):
        super().__init__(f"HTTP {status}")
        self.status, self.body = status, body


def http(method: str, url: str, body: bytes | None, content_type: str, token: str | None = None) -> dict:
    """One HTTPS call via curl; JSON body back, PlayError on a non-2xx status."""
    command = ["curl", "-sS", "-X", method, url, "-H", f"Content-Type: {content_type}",
               "-w", "\n%{http_code}"]
    if token:
        command += ["-H", f"Authorization: Bearer {token}"]
    if body is not None:
        command += ["--data-binary", "@-"]
    result = subprocess.run(command, input=body if body is not None else b"", capture_output=True, check=True)
    text, _, status = result.stdout.decode(errors="replace").rpartition("\n")
    code = int(status)
    if not 200 <= code < 300:
        raise PlayError(code, text)
    return json.loads(text) if text.strip() else {}


def call(token: str, method: str, url: str, body=None, content_type="application/json"):
    data = body if isinstance(body, (bytes, bytearray)) else (json.dumps(body).encode() if body is not None else None)
    return http(method, url, data, content_type, token)


def explain(error: PlayError) -> str:
    try:
        message = json.loads(error.body)["error"]["message"]
    except (ValueError, KeyError, TypeError):
        try:
            message = json.loads(error.body)["error_description"]
        except (ValueError, KeyError, TypeError):
            message = error.body[:400]
    hints = {
        400: "Google rejected the request - for a token error this means the key file isn't one Google issued "
             "(re-download the JSON key from the service account's Keys tab).",
        401: "the key was rejected - is this the JSON key of the service account you invited in Play Console?",
        403: "the service account isn't allowed to do this - in Play Console → Users and permissions, give it "
             "'Release apps to testing tracks' on Sentinel Core (invitations take a few minutes to apply).",
        404: "Google doesn't know this package for this account - check --package matches the Play listing.",
    }
    hint = hints.get(error.status, "")
    return f"HTTP {error.status}: {message}" + (f"\n    → {hint}" if hint else "")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--key", required=True, help="service-account JSON key file")
    parser.add_argument("--bundle", help=".aab to upload")
    parser.add_argument("--package", default="com.sentinelcore.android")
    parser.add_argument("--track", default="internal")
    parser.add_argument("--check", action="store_true", help="authenticate and list the track, upload nothing")
    args = parser.parse_args()

    with open(os.path.expanduser(args.key)) as f:
        key = json.load(f)
    if key.get("type") != "service_account" or "private_key" not in key:
        print(f"{args.key} is not a service-account key file (expected \"type\": \"service_account\").", file=sys.stderr)
        return 1

    try:
        token = access_token(key)
        print(f"==> Authenticated as {key['client_email']}")
        app = f"{API}/{args.package}"

        edit = call(token, "POST", f"{app}/edits", {})
        edit_id = edit["id"]

        if args.check:
            track = call(token, "GET", f"{app}/edits/{edit_id}/tracks/{args.track}")
            releases = track.get("releases", [])
            current = ", ".join(f"{r.get('name', '?')} [{r.get('status')}]" for r in releases) or "none"
            print(f"==> Track '{args.track}' reachable; current release(s): {current}")
            call(token, "DELETE", f"{app}/edits/{edit_id}")
            print("==> Key works. Run again with --bundle to upload.")
            return 0

        if not args.bundle:
            parser.error("--bundle is required unless --check")
        with open(args.bundle, "rb") as f:
            bundle = f.read()
        print(f"==> Uploading {os.path.basename(args.bundle)} ({len(bundle) / 1_048_576:.1f} MB)")
        uploaded = call(token, "POST",
                        f"{UPLOAD_API}/{args.package}/edits/{edit_id}/bundles?uploadType=media",
                        bundle, content_type="application/octet-stream")
        version_code = str(uploaded["versionCode"])
        print(f"==> Google accepted version code {version_code}")

        call(token, "PUT", f"{app}/edits/{edit_id}/tracks/{args.track}", {
            "track": args.track,
            "releases": [{"versionCodes": [version_code], "status": "completed"}],
        })
        call(token, "POST", f"{app}/edits/{edit_id}:commit")
        print(f"==> Released version code {version_code} to the '{args.track}' track. "
              "Testers get it within minutes; Play auto-updates their phones after that.")
        return 0
    except PlayError as error:
        print(f"Google Play refused: {explain(error)}", file=sys.stderr)
        return 1
    except subprocess.CalledProcessError as error:
        print(f"openssl could not sign with the key: {error.stderr.decode(errors='replace')}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
