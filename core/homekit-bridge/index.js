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
// One HomeKit Bridge accessory holds one MotionSensor per camera. Motion sensors are created
// lazily - the first time we hear a camera's name - so there's no camera list to keep in sync
// with the .NET side. Accessory UUIDs are derived from the camera name, so identity survives
// restarts and a re-created accessory is recognised as the same one it was before.

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

const PERSIST_DIR = path.join(__dirname, "persist");
const IDENTITY_FILE = path.join(PERSIST_DIR, "identity.json");

const BRIDGE_NAME = "Sentinel Core";
const CONTROL_PORT = Number(process.env.HOMEKIT_CONTROL_PORT || 3321);
const HAP_PORT = Number(process.env.HOMEKIT_HAP_PORT || 51826);

// How long a motion sensor stays "detected" before resetting. The true -> false transition is
// what Apple Home turns into a push notification, so this has to actually flip back.
const MOTION_RESET_MS = 10_000;

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

// normalized camera name -> { accessory, service, resetTimer }
const motionAccessories = new Map();

function normalize(cameraName) {
  return cameraName.trim().toLowerCase();
}

function getOrCreateMotionAccessory(cameraName) {
  const key = normalize(cameraName);
  const existing = motionAccessories.get(key);
  if (existing) return existing;

  // Deriving the UUID from the name (rather than randomly) is what makes this safe to do lazily:
  // the same camera always resolves to the same accessory identity, so a restart re-creates it
  // as the accessory HomeKit already knows rather than as a new one.
  const accessory = new Accessory(cameraName, uuid.generate(`sentinelcore:camera:${key}`));
  const service = accessory.addService(Service.MotionSensor, cameraName);

  // Supported at runtime, after publish() - the Bridge pushes the addition to paired controllers.
  bridge.addBridgedAccessory(accessory);

  const entry = { accessory, service, resetTimer: null, name: cameraName };
  motionAccessories.set(key, entry);
  console.log(`Added HomeKit motion sensor for camera "${cameraName}"`);
  return entry;
}

function triggerMotion(cameraName) {
  const entry = getOrCreateMotionAccessory(cameraName);

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

function isPaired() {
  // HAP-NodeJS keeps pairing state on the accessory's AccessoryInfo, which only exists once
  // publish() has run. Guarded because the exact shape isn't part of the documented API.
  try {
    return Boolean(bridge._accessoryInfo && bridge._accessoryInfo.paired());
  } catch {
    return false;
  }
}

bridge.publish({
  username: identity.username,
  pincode: identity.pincode,
  setupID: identity.setupId,
  port: HAP_PORT,
  category: Categories.BRIDGE,
  // This box runs avahi-daemon, which owns UDP 5353. Talking to it over D-Bus rather than
  // binding our own responder avoids fighting it for the port.
  advertiser: MDNSAdvertiser.AVAHI,
});

console.log(`HomeKit bridge "${BRIDGE_NAME}" published on port ${HAP_PORT}`);
console.log(`Pairing code: ${identity.pincode}`);

// ---------------------------------------------------------------------------
// Control API (loopback only - never exposed to the LAN)
// ---------------------------------------------------------------------------

function sendJson(res, statusCode, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(statusCode, { "content-type": "application/json" });
  res.end(body);
}

const server = http.createServer((req, res) => {
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
      paired: isPaired(),
      accessories: [...motionAccessories.values()].map((entry) => entry.name),
      // Only useful until they've paired; after that it's just noise on the settings page.
      pairingCode: isPaired() ? null : identity.pincode,
    });
  }

  if (req.method === "GET" && req.url === "/api/qr") {
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
});

// systemd sends SIGTERM on stop/restart; unpublishing cleanly means paired iPhones see the
// bridge go away rather than treating it as unresponsive.
for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => {
    console.log(`Received ${signal}, shutting down.`);
    server.close();
    bridge.unpublish();
    process.exit(0);
  });
}
