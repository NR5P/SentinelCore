"use strict";

// HomeKit sidecar for Sentinel Core.
//
// Why this exists as a separate Node process rather than living in the .NET app: HomeKit speaks
// HAP (SRP6a pairing, ChaCha20-Poly1305 session encryption, TLV8, mDNS advertisement). There's no
// maintained .NET implementation, and hand-rolling that is a security-sensitive project in its own
// right. @homebridge/hap-nodejs is the library Homebridge itself runs on, so this file is just a
// thin wrapper: it owns everything Apple-specific and exposes a tiny loopback-only HTTP API that
// the main app pings when an alert is worth a notification.
//
// One HomeKit Bridge accessory holds one MotionSensor per camera. A camera is learned the first
// time an alert names it, then persisted and restored before the bridge is published on every
// later boot. HomeKit requires a stable accessory graph and stable UUIDs; publishing an empty
// bridge and adding the first sensor at the same instant it fires can leave Apple Home with an
// unresponsive or missing child accessory.
//
// This service is installed for everyone, but stays off the network until HomeKit:Enabled is
// turned on in the dashboard. Installing the product shouldn't put an unpaired accessory on
// someone's network uninvited.

const http = require("node:http");
const path = require("node:path");
const fs = require("node:fs");
const crypto = require("node:crypto");

const {
  Accessory,
  Bridge,
  Categories,
  Characteristic,
  HAPStorage,
  MDNSAdvertiser,
  Service,
  uuid,
} = require("@homebridge/hap-nodejs");
const QRCode = require("qrcode");

const PERSIST_DIR = process.env.HOMEKIT_PERSIST_DIR || path.join(__dirname, "persist");
const IDENTITY_FILE = path.join(PERSIST_DIR, "identity.json");
const CAMERAS_FILE = path.join(PERSIST_DIR, "cameras.json");

// The main app's config is the single source of truth for whether HomeKit is on - the dashboard
// writes it there, same as every other setting.
const MAIN_SETTINGS_PATH = process.env.HOMEKIT_MAIN_SETTINGS
  || path.join(__dirname, "..", "BlueIrisOllamaBridge", "appsettings.json");

const BRIDGE_NAME = "Sentinel Core";
const CONTROL_PORT = Number(process.env.HOMEKIT_CONTROL_PORT || 3321);
const HAP_PORT = Number(process.env.HOMEKIT_HAP_PORT || 51826);

// How long a motion sensor stays "detected" before resetting. The true -> false transition is
// what Apple Home turns into a push notification, so this has to actually flip back.
const MOTION_RESET_MS = 10_000;

// How often to re-read the enabled flag. Matches the dashboard's other "takes effect within a
// few seconds, no restart needed" settings.
const SETTINGS_POLL_MS = 10_000;

// ---------------------------------------------------------------------------
// Stable identity
// ---------------------------------------------------------------------------

// The HAP username (a virtual MAC) and pincode identify this bridge to every paired iPhone. If
// either changes, existing pairings silently break and everyone has to re-pair - so they're
// generated exactly once and read back verbatim on every subsequent boot.
function loadOrCreateIdentity() {
  fs.mkdirSync(PERSIST_DIR, { recursive: true });

  if (fs.existsSync(IDENTITY_FILE)) {
    return JSON.parse(fs.readFileSync(IDENTITY_FILE, "utf8"));
  }

  const identity = {
    username: randomHapUsername(),
    pincode: randomPincode(),
    setupId: randomSetupId(),
  };

  // 0600 - this is pairing material, not general config.
  fs.writeFileSync(IDENTITY_FILE, JSON.stringify(identity, null, 2), { mode: 0o600 });
  console.log(`Generated new HomeKit identity at ${IDENTITY_FILE}`);
  return identity;
}

function randomHapUsername() {
  const bytes = crypto.randomBytes(6);
  // Locally-administered, unicast - the same convention Homebridge uses for its virtual MACs.
  bytes[0] = (bytes[0] & 0xfe) | 0x02;
  return [...bytes].map((b) => b.toString(16).padStart(2, "0").toUpperCase()).join(":");
}

function randomPincode() {
  // HAP requires ddd-dd-ddd. Apple rejects a handful of trivial codes (all-same-digit and
  // sequential runs); avoiding them entirely is easier than special-casing the list.
  const digits = () => crypto.randomInt(0, 10).toString();
  let code;
  do {
    code = `${digits()}${digits()}${digits()}-${digits()}${digits()}-${digits()}${digits()}${digits()}`;
  } while (isWeakPincode(code));
  return code;
}

function isWeakPincode(code) {
  const digits = code.replace(/-/g, "");
  const forbidden = [
    "00000000", "11111111", "22222222", "33333333", "44444444",
    "55555555", "66666666", "77777777", "88888888", "99999999",
    "12345678", "87654321",
  ];
  return forbidden.includes(digits);
}

function randomSetupId() {
  // 4 chars, uppercase alphanumeric. Pairs with the pincode inside the QR payload.
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
  return Array.from({ length: 4 }, () => alphabet[crypto.randomInt(0, alphabet.length)]).join("");
}

// ---------------------------------------------------------------------------
// Bridge + accessories
// ---------------------------------------------------------------------------

const identity = loadOrCreateIdentity();

// Must happen before any Accessory/Bridge is constructed - it's where pairing state lives.
HAPStorage.setCustomStoragePath(PERSIST_DIR);

const bridge = new Bridge(BRIDGE_NAME, uuid.generate("sentinelcore:bridge"));

// On Linux, avahi-daemon almost always already owns UDP 5353; talking to it over D-Bus rather
// than binding our own responder avoids fighting it for the port. Windows and macOS have no
// avahi, so they use hap-nodejs's own bundled responder instead. Override with
// HOMEKIT_ADVERTISER if your setup differs (e.g. a Linux box with no avahi installed - use
// "ciao" there).
const ADVERTISER = process.env.HOMEKIT_ADVERTISER
  || (process.platform === "linux" ? MDNSAdvertiser.AVAHI : MDNSAdvertiser.CIAO);

const publishInfo = {
  username: identity.username,
  pincode: identity.pincode,
  setupID: identity.setupId,
  port: HAP_PORT,
  category: Categories.BRIDGE,
  advertiser: ADVERTISER,
};

// normalized camera name -> { accessory, service, resetTimer, name }
const motionAccessories = new Map();
let published = false;
const startedAt = new Date().toISOString();
let lastTriggerAt = null;
let lastTriggerCamera = null;

function normalize(cameraName) {
  return cameraName.trim().toLowerCase();
}

function persistCameraNames() {
  fs.mkdirSync(PERSIST_DIR, { recursive: true });
  const names = [...motionAccessories.values()].map((entry) => entry.name);
  const tempPath = `${CAMERAS_FILE}.${process.pid}.tmp`;
  fs.writeFileSync(tempPath, JSON.stringify(names, null, 2), { mode: 0o600 });
  fs.renameSync(tempPath, CAMERAS_FILE);
}

function loadCameraNames() {
  try {
    if (!fs.existsSync(CAMERAS_FILE)) return [];
    const value = JSON.parse(fs.readFileSync(CAMERAS_FILE, "utf8"));
    if (!Array.isArray(value)) throw new Error("expected a JSON array");
    return value
      .filter((name) => typeof name === "string" && name.trim() !== "")
      .map((name) => name.trim());
  } catch (error) {
    console.warn(`Could not load ${CAMERAS_FILE}: ${error.message}`);
    return [];
  }
}

function loadConfiguredCameraNames() {
  try {
    const settings = JSON.parse(fs.readFileSync(MAIN_SETTINGS_PATH, "utf8"));
    const mappings = settings?.BlueIris?.CameraMappings;
    if (!mappings || typeof mappings !== "object" || Array.isArray(mappings)) return [];
    return Object.keys(mappings).filter((name) => name.trim() !== "");
  } catch (error) {
    if (error.code !== "ENOENT") {
      console.warn(`Could not seed HomeKit cameras from ${MAIN_SETTINGS_PATH}: ${error.message}`);
    }
    return [];
  }
}

function getOrCreateMotionAccessory(cameraName, persist = true) {
  cameraName = cameraName.trim();
  const key = normalize(cameraName);
  const existing = motionAccessories.get(key);
  if (existing) return existing;

  // The same camera always resolves to the same accessory identity across restarts.
  const accessory = new Accessory(cameraName, uuid.generate(`sentinelcore:camera:${key}`));
  const service = accessory.addService(Service.MotionSensor, cameraName);
  service.updateCharacteristic(Characteristic.MotionDetected, false);

  bridge.addBridgedAccessory(accessory);

  const entry = { accessory, service, resetTimer: null, name: cameraName };
  motionAccessories.set(key, entry);
  if (persist) persistCameraNames();
  console.log(`Added HomeKit motion sensor for camera "${cameraName}"`);
  return entry;
}

function triggerMotion(cameraName) {
  const entry = getOrCreateMotionAccessory(cameraName);
  lastTriggerAt = new Date().toISOString();
  lastTriggerCamera = entry.name;

  // updateCharacteristic (not setValue): this is a passive sensor with no SET handler, we're
  // pushing a new value out to paired controllers.
  entry.service.updateCharacteristic(Characteristic.MotionDetected, true);

  // Re-triggering while already active just extends the window rather than stacking timers.
  if (entry.resetTimer) clearTimeout(entry.resetTimer);
  entry.resetTimer = setTimeout(() => {
    entry.service.updateCharacteristic(Characteristic.MotionDetected, false);
    entry.resetTimer = null;
  }, MOTION_RESET_MS);
}

function restoreMotionAccessories() {
  const names = [...new Set([...loadCameraNames(), ...loadConfiguredCameraNames()])];
  for (const name of names) getOrCreateMotionAccessory(name, false);
  if (names.length > 0) {
    persistCameraNames();
    console.log(`Restored ${motionAccessories.size} HomeKit camera sensor(s) before publish.`);
  }
}

function isPaired() {
  // HAP-NodeJS keeps pairing state on the accessory's AccessoryInfo, which only exists once
  // publish() has run. Guarded because the exact shape isn't part of the documented API.
  try {
    return Boolean(bridge._accessoryInfo && bridge._accessoryInfo.paired());
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------
// Enabled flag - polled from the main app's config
// ---------------------------------------------------------------------------

let enabled = false;

function readEnabledFlag() {
  try {
    const raw = fs.readFileSync(MAIN_SETTINGS_PATH, "utf8");
    return JSON.parse(raw)?.HomeKit?.Enabled === true;
  } catch (error) {
    // A missing or briefly half-written config (the dashboard writes atomically, but a race is
    // still conceivable) must not flap the bridge off. Keep the current state and try again.
    if (error.code !== "ENOENT") {
      console.warn(`Could not read ${MAIN_SETTINGS_PATH}: ${error.message}`);
    }
    return enabled;
  }
}

async function applyEnabledState(next) {
  // A failed publish must be retried on the next poll even though the configured enabled value
  // did not change. Likewise, an interrupted unpublish is retried until state matches config.
  if (next === enabled && ((next && published) || (!next && !published))) return;
  enabled = next;

  if (enabled && !published) {
    await bridge.publish(publishInfo);
    published = true;
    console.log(`HomeKit enabled - bridge "${BRIDGE_NAME}" published on port ${HAP_PORT}`);
    console.log(`Pairing code: ${identity.pincode}`);
  } else if (!enabled && published) {
    await bridge.unpublish();
    published = false;
    // Pairings are kept - this only stops advertising and serving. Turning it back on picks up
    // exactly where it left off, with no re-pairing needed.
    console.log("HomeKit disabled - bridge unpublished (existing pairings kept).");
  }
}

async function pollSettings() {
  try {
    await applyEnabledState(readEnabledFlag());
  } catch (error) {
    console.error("Failed to apply HomeKit enabled state:", error.message);
  }
}

// ---------------------------------------------------------------------------
// Control API (loopback only - never exposed to the LAN)
// ---------------------------------------------------------------------------

function sendJson(res, statusCode, payload) {
  res.writeHead(statusCode, { "content-type": "application/json" });
  res.end(JSON.stringify(payload));
}

const server = http.createServer(async (req, res) => {
  if (req.method === "POST" && req.url === "/api/trigger") {
    let body = "";
    req.on("data", (chunk) => {
      body += chunk;
      // A camera name is all we accept; anything large enough to matter is malformed.
      if (body.length > 4096) {
        res.writeHead(413).end();
        req.destroy();
      }
    });
    req.on("end", () => {
      try {
        const { camera } = JSON.parse(body || "{}");
        if (typeof camera !== "string" || camera.trim() === "") {
          return sendJson(res, 400, { error: "A camera name is required." });
        }
        // The main app checks its own HomeKit:Enabled before calling, so this is belt and
        // braces for a stale caller - and it stops accessories accumulating for a feature
        // nobody turned on.
        if (!enabled) return sendJson(res, 409, { error: "HomeKit is turned off." });

        triggerMotion(camera);
        res.writeHead(204).end();
      } catch {
        sendJson(res, 400, { error: "Expected a JSON body with a camera name." });
      }
    });
    return;
  }

  if (req.method === "GET" && req.url === "/api/status") {
    return sendJson(res, 200, {
      enabled,
      published,
      paired: isPaired(),
      accessories: [...motionAccessories.values()].map((entry) => entry.name),
      hapPort: HAP_PORT,
      advertiser: ADVERTISER,
      startedAt,
      lastTriggerAt,
      lastTriggerCamera,
      // Only useful until they've paired; after that it's just noise on the settings page.
      pairingCode: published && !isPaired() ? identity.pincode : null,
    });
  }

  if (req.method === "POST" && req.url === "/api/reset") {
    // This API only listens on loopback. The dashboard additionally requires an explicit
    // confirmation value before proxying a reset. A new identity is the cleanest recovery from
    // the case where Apple Home and HAP storage each retain different controller keys.
    try {
      if (published) {
        await bridge.unpublish();
        published = false;
      }
      Accessory.cleanupAccessoryData(identity.username);
      if (fs.existsSync(IDENTITY_FILE)) fs.unlinkSync(IDENTITY_FILE);
      sendJson(res, 202, { reset: true, restarting: true });
      console.log("HomeKit pairing data reset; exiting so the service manager creates a new identity.");
      setTimeout(() => process.exit(75), 250);
    } catch (error) {
      console.error("Could not reset HomeKit pairing data:", error);
      sendJson(res, 500, { error: "Could not reset HomeKit pairing data." });
    }
    return;
  }

  if (req.method === "GET" && req.url === "/api/qr") {
    // setupURI() is only meaningful once published - there's no pairing to offer before that.
    if (!published) {
      return sendJson(res, 409, { error: "HomeKit is turned off." });
    }
    QRCode.toBuffer(bridge.setupURI(), { errorCorrectionLevel: "M", margin: 2, width: 320 })
      .then((png) => {
        res.writeHead(200, { "content-type": "image/png", "cache-control": "no-store" });
        res.end(png);
      })
      .catch((error) => {
        console.error("Could not render the pairing QR code:", error);
        sendJson(res, 500, { error: "Could not render the pairing QR code." });
      });
    return;
  }

  res.writeHead(404).end();
});

server.listen(CONTROL_PORT, "127.0.0.1", () => {
  console.log(`Control API listening on http://127.0.0.1:${CONTROL_PORT}`);
  console.log(`Watching ${MAIN_SETTINGS_PATH} for HomeKit:Enabled`);
  console.log(`mDNS advertiser: ${ADVERTISER} (platform ${process.platform})`);
});

// Restore every known child accessory before publish(), so Apple Home receives a complete,
// consistent accessory graph during pairing and after every sidecar restart.
restoreMotionAccessories();
pollSettings();
const pollTimer = setInterval(pollSettings, SETTINGS_POLL_MS);

// systemd sends SIGTERM on stop/restart; unpublishing cleanly means paired iPhones see the
// bridge go away rather than treating it as unresponsive.
let shuttingDown = false;
for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, async () => {
    if (shuttingDown) return;
    shuttingDown = true;
    console.log(`Received ${signal}, shutting down.`);
    clearInterval(pollTimer);
    server.close();
    if (published) await bridge.unpublish();
    process.exit(0);
  });
}
