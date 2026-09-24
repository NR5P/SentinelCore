# Running the push relay

This is the operator's side of [PUSH_NOTIFICATIONS.md § Relay](PUSH_NOTIFICATIONS.md#relay-the-default).
It is for whoever runs `push.nr5p.com` - not for customers, whose installs only ever *call* it.

The relay is `SentinelCore.PushRelay`: a small ASP.NET Core service that holds the APNs `.p8`
key and the Firebase service account, accepts requests from customer installs authenticated by
their signed license, and forwards each push to Apple or Google. It keeps one SQLite table -
which license owns which device token - and nothing else. No events, no images, no cameras.

It is **not part of any release bundle**. `scripts/publish-release.sh` never stages it, and
`install-linux.sh`/`install-macos.sh` never install it. It builds as part of the solution so its
tests run with everyone else's, and it deploys only where you put it.

## What it needs

- The .NET 10 runtime (already present on a host that runs Core).
- The APNs auth key and Firebase service-account JSON - the same two files a direct-mode Core
  install uses. On the current host they are `~/sentinelcore-push-keys/AuthKey_APNSKEYID0.p8`
  and `~/sentinelcore-push-keys/firebase-service-account.json`.
- Outbound HTTPS to `api.push.apple.com`, `api.sandbox.push.apple.com`,
  `fcm.googleapis.com` and `oauth2.googleapis.com`.
- Something to put it on the internet at `https://push.nr5p.com`. Today that is the
  `cloudflared` tunnel already running on the same host for `cam.example.com`.

It does **not** need the license private key. It verifies with the public key compiled into
`SentinelCore.Licensing/LicensePublicKey.cs` - the same one every Core install carries.

## Install (first time, on the vendor host)

From the repository checkout that already runs Core (`~/Desktop/BlueIrisOllamaBridge/core` on
the media box):

1. Fill in the live configuration. `SentinelCore.PushRelay/appsettings.json` is committed with
   empty credentials; edit the working copy in place, the same way Core's own is:

   ```json
   "Relay": {
     "Apple": {
       "Enabled": true,
       "TeamId": "G4GK5372G8",
       "KeyId": "APNSKEYID0",
       "BundleId": "com.sentinelcore.client",
       "TvBundleId": "com.sentinelcore.client.tv",
       "PrivateKeyPath": "/home/sentinel/sentinelcore-push-keys/AuthKey_APNSKEYID0.p8"
     },
     "Firebase": {
       "Enabled": true,
       "ProjectId": "firebase-project-id",
       "ServiceAccountPath": "/home/sentinel/sentinelcore-push-keys/firebase-service-account.json"
     }
   }
   ```

   Leave `Urls` at `http://127.0.0.1:3330`. The relay must only ever be reachable through the
   tunnel, and loopback is what guarantees that. There is no `UseSandbox` here: the environment
   is per registered token, sent by each Core from its own `Relay.ApnsSandbox` setting.

2. Install the unit, filling the same placeholders `install-linux.sh` fills for the others:

   ```bash
   sed -e "s|__DOTNET_PATH__|$(command -v dotnet)|g" \
       -e "s|__INSTALL_DIR__|$HOME/Desktop/BlueIrisOllamaBridge/core|g" \
       -e "s|__APP_USER__|$USER|g" \
       systemd/sentinelcore-push-relay.service | sudo tee /etc/systemd/system/sentinelcore-push-relay.service >/dev/null
   sudo systemctl daemon-reload
   sudo systemctl enable sentinelcore-push-relay
   ```

3. Let `deploy.sh` restart it without a password, alongside the existing entries:

   ```bash
   sudo visudo -f /etc/sudoers.d/blueiris-deploy
   # add:  media ALL=(root) NOPASSWD: /usr/bin/systemctl restart sentinelcore-push-relay
   ```

4. Build and start it:

   ```bash
   ./scripts/deploy.sh relay
   curl -s http://127.0.0.1:3330/health
   # {"status":"ok","apple":true,"firebase":true}
   ```

5. Expose it. The tunnel on this host is remotely managed (`cloudflared tunnel run --token-file`),
   so its hostnames live in the Cloudflare dashboard, not in a local file: Zero Trust → Networks
   → Tunnels → *(the existing tunnel)* → Public Hostnames → **Add**:

   - Subdomain `push`, domain `nr5p.com`
   - Service type **HTTP**, URL `localhost:3330`

   Then from anywhere:

   ```bash
   curl -s https://push.nr5p.com/health
   curl -s -o /dev/null -w '%{http_code}\n' -X POST https://push.nr5p.com/v1/push   # 401
   ```

From then on `./scripts/deploy.sh` (all) and `./scripts/deploy.sh relay` rebuild and restart it
with everything else. On a host where the unit isn't installed both silently skip it.

## Trying it against your own Core

Your own install pushes direct, because its Apple and Firebase sections are configured and direct
always wins. To exercise the relay end to end, temporarily turn one off:

1. Settings → PUSH NOTIFICATIONS: untick **Apple: Enabled**, tick **Relay: Apple Sandbox** (your
   phone runs an Xcode build, so its token is a sandbox token). Save.
2. Restart both Core services (the options are read at startup by the main app).
3. Within about 20 seconds the reconciler re-registers every known device -
   `journalctl -u sentinelcore-push-relay -f` shows one `registered a apple device` line each.
4. Settings → PUSH NOTIFICATIONS → **Send Test**. The relay logs
   `<you> (<license id>) push to apple: Delivered` and the phone buzzes.
5. Tick Apple back on, untick Apple Sandbox, save, restart. The next test goes direct: nothing
   new in the relay's journal.

## Day to day

- `journalctl -u sentinelcore-push-relay -f` - one line per registration and per push, naming
  the customer and license id and the outcome. Never the message text.
- `sqlite3 SentinelCore.PushRelay/data/relay-devices.db 'select CustomerName, Platform, count(*) from Devices group by 1,2'`
  - who has what registered.
- Limits are in `appsettings.json` under `Relay`: `MaxDevicesPerLicense` (25),
  `PushesPerLicensePer10Minutes` (200), `StaleTokenTakeoverDays` (30 - after this long without
  a refresh, a token may be claimed by a different license, which is how a phone that moved to
  another household stops being stuck).
- **Cutting off a license** (leaked, shared around, refunded or charged back): add its id to
  `Relay:BlockedLicenseIds` in the working copy of `appsettings.json` and save. No restart or
  deploy - the relay re-reads the file, and the journal says `N license(s) blocked` when it has.
  The id is the one printed in the journal next to the customer's name (`Jane Doe (3f2a…)`), and
  in the `.lic` file itself; either GUID format works:

  ```json
  "Relay": {
    "BlockedLicenseIds": [ "3f2a1c9e-0b7d-4e2a-9c51-6d8f0e4b2a17" ]
  }
  ```

  From then on every call from that license gets `403` with *"This Sentinel Core license can no
  longer use the push notification service..."* (which the customer's dashboard log shows), its
  registered devices are deleted on its next call, and it can't register new ones. Core does not
  retry on a 403, so a blocked install stays quiet. It is push only: the license still clears the
  UNLICENSED watermark on that install, since licenses are verified offline and never phone home.
  Removing the id undoes it - the install's phones re-register on their next launch or within
  six hours. An entry that isn't a GUID is ignored with a warning in the journal.
- Rotating the APNs key or the Firebase account: replace the file, update the path if it changed,
  `./scripts/deploy.sh relay`. Customers notice nothing.

## Moving it to a proper host later

Nothing about a customer's install knows where the relay physically is - only the hostname. So:

1. Build the relay on the new host (`dotnet publish -c Release` of `SentinelCore.PushRelay`, or
   copy the `bin/Release/net10.0` output), copy the two credential files and `appsettings.json`,
   and copy `data/relay-devices.db` so existing registrations carry over (they would rebuild
   themselves within six hours anyway, and on the first push before that).
2. Put it behind TLS on that host - its own `cloudflared`, or any reverse proxy with a real
   certificate. Keep the process itself on loopback.
3. Repoint `push.nr5p.com` (the tunnel's public hostname, or DNS) at the new host.
4. Stop and disable the unit on the old host.

## Threat model, briefly

- The relay holds credentials that can push to every customer's phone. It must only run on a
  host you control, bound to loopback, reached through TLS.
- A license file is a bearer credential to the relay. Its blast radius is the tokens registered
  under that same license - the holder's own devices - and nothing else. `StaleTokenTakeoverDays`
  is the one deliberate relaxation of that, for tokens nobody has refreshed in a month.
- Rate limits are per license, not per IP, because every request arrives from the tunnel's
  loopback hop. An abusive license shows up by name in the journal; cut it off with
  `Relay:BlockedLicenseIds` (see *Day to day*). Deleting its rows from `relay-devices.db` is not
  enough on its own - Core re-registers on the next push and every six hours. The license file
  itself stays valid everywhere else, since licenses are perpetual and verified offline.
