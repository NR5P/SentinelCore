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
// One HomeKit Bridge accessory holds two kinds of child accessory:
//
//   * a MotionSensor per camera, tripped by the main app through /api/trigger. A camera is
//     learned the first time an alert names it, then persisted and restored before the bridge is
//     published on every later boot.
//   * a Lightbulb/Switch/Lock/GarageDoorOpener per Device Hub device, when HomeKit:IncludeDevices
//     is on. Those are read from the main app's own /api/devices over loopback and kept in sync
//     by polling, so anything on the MQTT broker becomes controllable from Apple Home.
//
// HomeKit requires a stable accessory graph and stable UUIDs; publishing an empty bridge and
// adding the first accessory at the same instant it fires can leave Apple Home with an
// unresponsive or missing child accessory. So both kinds are persisted and rebuilt before
// publish() rather than discovered afterwards.
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
  HAPStatus,
  HapStatusError,
  HAPStorage,
  MDNSAdvertiser,
  Service,
  uuid,
} = require("@homebridge/hap-nodejs");
const QRCode = require("qrcode");

const PERSIST_DIR = process.env.HOMEKIT_PERSIST_DIR || path.join(__dirname, "persist");
const IDENTITY_FILE = path.join(PERSIST_DIR, "identity.json");
const CAMERAS_FILE = path.join(PERSIST_DIR, "cameras.json");
const DEVICES_FILE = path.join(PERSIST_DIR, "devices.json");

// The main app's config is the single source of truth for whether HomeKit is on - the dashboard
// writes it there, same as every other setting.
const MAIN_SETTINGS_PATH = process.env.HOMEKIT_MAIN_SETTINGS
  || path.join(__dirname, "..", "SentinelCore", "appsettings.json");

const BRIDGE_NAME = "Sentinel Core";
const CONTROL_PORT = Number(process.env.HOMEKIT_CONTROL_PORT || 3321);
const HAP_PORT = Number(process.env.HOMEKIT_HAP_PORT || 51826);

// Identity every accessory this bridge publishes carries. The firmware string has to parse as a
// version or Apple Home shows the accessory as misconfigured, so it comes from package.json
// rather than being written out by hand in two places.
const MANUFACTURER = "Sentinel Core";
const FIRMWARE_REVISION = require("./package.json").version;

// Where the main app serves its own HTTP API. Loopback: the Device Hub calls below carry the
// shared API key out of appsettings.json, and that key is not for the LAN.
const MAIN_APP_URL = (process.env.HOMEKIT_MAIN_APP_URL || "http://127.0.0.1:5080").replace(/\/+$/, "");
const MAIN_APP_TIMEOUT_MS = 5_000;

// How long a motion sensor stays "detected" before resetting. The true -> false transition is
// what Apple Home turns into a push notification, so this has to actually flip back.
const MOTION_RESET_MS = 10_000;

// How long a pulse device's switch stays on before springing back. A momentary gate relay has no
// "off" to report, so the tile has to un-press itself rather than sit there implying the gate is
// being held open.
const MOMENTARY_RESET_MS = 3_000;

// How often to re-read the enabled flag. Matches the dashboard's other "takes effect within a
// few seconds, no restart needed" settings.
const SETTINGS_POLL_MS = 10_000;

// How often to reconcile the Device Hub roster and push device state into HomeKit. The Device Hub
// tab polls the same state endpoint at a similar rate, so this adds no new load pattern.
const DEVICE_POLL_MS = Number(process.env.HOMEKIT_DEVICE_POLL_MS || 5_000);

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
// Accessory identity
// ---------------------------------------------------------------------------

// HomeKit tells the children of a bridge apart by serial number, not by name. hap-nodejs gives
// every accessory the same "Default-SerialNumber" unless it is set, and the Home app's response
// to two accessories claiming one serial is to merge them into a single tile or drop one
// entirely. It only looks harmless while a bridge has exactly one child.
//
// So every accessory gets a serial derived from the same key its UUID comes from: stable across
// restarts, unique per accessory, and still readable in the Home app's accessory details. The
// hash suffix is what guarantees uniqueness once the readable part has been truncated.
function serialFor(prefix, key) {
  const slug = String(key).trim().toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 32);
  const digest = crypto.createHash("sha1").update(String(key)).digest("hex").slice(0, 8);
  return `${prefix}-${slug || "unnamed"}-${digest}`.toUpperCase();
}

function applyAccessoryInformation(accessory, model, serialNumber) {
  accessory.getService(Service.AccessoryInformation)
    .setCharacteristic(Characteristic.Manufacturer, MANUFACTURER)
    .setCharacteristic(Characteristic.Model, model)
    .setCharacteristic(Characteristic.SerialNumber, serialNumber)
    .setCharacteristic(Characteristic.FirmwareRevision, FIRMWARE_REVISION);
  return serialNumber;
}

// Reads back what an accessory is actually telling HomeKit, rather than what it was meant to be
// told. Worth logging: a serial collision is invisible in Apple Home (it just shows fewer tiles
// than you added), so the journal is the only place it can be caught.
function serialOf(accessory) {
  return accessory.getService(Service.AccessoryInformation)
    .getCharacteristic(Characteristic.SerialNumber).value;
}

// ---------------------------------------------------------------------------
// Main app settings + API
// ---------------------------------------------------------------------------

// Returns the parsed appsettings.json, or null when it could not be read. Callers keep whatever
// state they already had on null rather than treating a missing or briefly half-written config
// (the dashboard writes atomically, but a race is still conceivable) as "everything is off".
function readMainSettings() {
  try {
    return JSON.parse(fs.readFileSync(MAIN_SETTINGS_PATH, "utf8"));
  } catch (error) {
    if (error.code !== "ENOENT") {
      console.warn(`Could not read ${MAIN_SETTINGS_PATH}: ${error.message}`);
    }
    return null;
  }
}

async function callMainApp(apiPath, { method = "GET", body } = {}) {
  const apiKey = readMainSettings()?.Bridge?.ApiKey;
  if (!apiKey) throw new Error("the main app's API key is not set in appsettings.json");

  const headers = { "x-api-key": apiKey };
  if (body !== undefined) headers["content-type"] = "application/json";

  const response = await fetch(`${MAIN_APP_URL}${apiPath}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(MAIN_APP_TIMEOUT_MS),
  });

  if (!response.ok) throw new Error(`${method} ${apiPath} returned ${response.status}`);
  return response.status === 204 ? null : response.json();
}

// ---------------------------------------------------------------------------
// Bridge
// ---------------------------------------------------------------------------

const identity = loadOrCreateIdentity();

// Must happen before any Accessory/Bridge is constructed - it's where pairing state lives.
HAPStorage.setCustomStoragePath(PERSIST_DIR);

const bridge = new Bridge(BRIDGE_NAME, uuid.generate("sentinelcore:bridge"));
// The virtual MAC is already unique and already stable across restarts, which is exactly what a
// serial number has to be.
applyAccessoryInformation(bridge, "Sentinel Core Bridge", serialFor("bridge", identity.username));

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
// device id -> { accessory, service, id, name, kind, shape, momentary, supportsBrightness, ... }
const deviceAccessories = new Map();
let published = false;
const startedAt = new Date().toISOString();
let lastTriggerAt = null;
let lastTriggerCamera = null;

function normalize(cameraName) {
  return cameraName.trim().toLowerCase();
}

// ---------------------------------------------------------------------------
// Camera motion sensors
// ---------------------------------------------------------------------------

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
  const mappings = readMainSettings()?.BlueIris?.CameraMappings;
  if (!mappings || typeof mappings !== "object" || Array.isArray(mappings)) return [];
  return Object.keys(mappings).filter((name) => name.trim() !== "");
}

function getOrCreateMotionAccessory(cameraName, persist = true) {
  cameraName = cameraName.trim();
  const key = normalize(cameraName);
  const existing = motionAccessories.get(key);
  if (existing) return existing;

  // The same camera always resolves to the same accessory identity across restarts.
  const accessory = new Accessory(cameraName, uuid.generate(`sentinelcore:camera:${key}`));
  applyAccessoryInformation(accessory, "Camera Motion Sensor", serialFor("camera", key));
  const service = accessory.addService(Service.MotionSensor, cameraName);
  service.updateCharacteristic(Characteristic.MotionDetected, false);

  bridge.addBridgedAccessory(accessory);

  const entry = { accessory, service, resetTimer: null, name: cameraName };
  motionAccessories.set(key, entry);
  if (persist) persistCameraNames();
  console.log(`Added HomeKit motion sensor for camera "${cameraName}" (serial ${serialOf(accessory)})`);
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

// ---------------------------------------------------------------------------
// Device Hub accessories
// ---------------------------------------------------------------------------

// Which HomeKit service each Device Hub kind becomes. "sensor" is deliberately absent: a
// read-only MQTT sensor carries arbitrary measurements (see DeviceTopics in the main app) and
// there is no honest generic HomeKit service for "some number".
//
// "shape" is how the rest of this file addresses the service without re-deciding the mapping:
// "power" is an On characteristic, "lock" is LockCurrent/TargetState, "garage" is
// CurrentDoorState/TargetDoorState.
function serviceForKind(kind, device) {
  const momentary = device?.momentary === true;
  switch (String(kind || "").toLowerCase()) {
    case "light": return { service: Service.Lightbulb, model: "MQTT Light", shape: "power" };
    case "outlet": return { service: Service.Outlet, model: "MQTT Outlet", shape: "power" };
    case "fan": return { service: Service.Fan, model: "MQTT Fan", shape: "power" };
    case "switch": return { service: Service.Switch, model: "MQTT Switch", shape: "power" };
    case "button": return { service: Service.Switch, model: "MQTT Button", shape: "power" };
    case "lock": return { service: Service.LockMechanism, model: "MQTT Lock", shape: "lock" };
    case "cover":
    case "gate":
    case "garage": {
      const label = { cover: "Cover", gate: "Gate", garage: "Garage Door" }[String(kind).toLowerCase()];
      // A momentary cover is a pulse relay that never reports whether it is open. HomeKit's
      // GarageDoorOpener would sit on "Opening..." forever waiting for a state that never
      // arrives, so a pulse gets an honest spring-back Switch instead of a door tile that lies.
      return momentary
        ? { service: Service.Switch, model: `MQTT ${label} (pulse)`, shape: "power" }
        : { service: Service.GarageDoorOpener, model: `MQTT ${label}`, shape: "garage" };
    }
    default: return null;
  }
}

// A device the bridge is willing to publish: switched on in the Device Hub, and of a kind that
// maps onto a HomeKit service.
function isBridgeable(device) {
  return Boolean(device)
    && typeof device.id === "string"
    && device.enabled !== false
    && serviceForKind(device.kind, device) !== null;
}

// Home shows "No Response" when a characteristic read fails, which is the truthful thing to show
// for a device whose MQTT availability topic has gone quiet - better than a light tile that
// cheerfully reports "off" for a bulb that has been unplugged for a week.
function guardAvailability(entry) {
  if (!entry.available) throw new HapStatusError(HAPStatus.SERVICE_COMMUNICATION_FAILURE);
}

async function sendDeviceCommand(entry, action, brightness) {
  const body = {};
  if (action) body.action = action;
  if (brightness !== undefined && brightness !== null) body.brightness = brightness;

  try {
    const result = await callMainApp(`/api/devices/${entry.id}/command`, { method: "POST", body });
    // The endpoint answers 200 with sent:false when the broker took the publish but the device
    // never acknowledged it, so a 2xx alone isn't proof the command landed.
    if (result && result.sent === false) {
      throw new Error(result.error || "the device did not accept the command");
    }
  } catch (error) {
    console.warn(`HomeKit command for "${entry.name}" failed: ${error.message}`);
    // Raised as a HAP error so Home reports the accessory as unresponsive rather than drawing
    // the light as on when nothing actually happened.
    throw new HapStatusError(HAPStatus.SERVICE_COMMUNICATION_FAILURE);
  }
}

function wireDeviceHandlers(entry) {
  if (entry.shape === "lock") {
    entry.service.getCharacteristic(Characteristic.LockCurrentState)
      .onGet(() => {
        guardAvailability(entry);
        return entry.on ? Characteristic.LockCurrentState.SECURED : Characteristic.LockCurrentState.UNSECURED;
      });
    entry.service.getCharacteristic(Characteristic.LockTargetState)
      .onGet(() => {
        guardAvailability(entry);
        return entry.on ? Characteristic.LockTargetState.SECURED : Characteristic.LockTargetState.UNSECURED;
      })
      // The main app maps "lock" onto PayloadOn and "unlock" onto PayloadOff, so a locked device
      // is one reporting power on.
      .onSet((value) => sendDeviceCommand(
        entry, value === Characteristic.LockTargetState.SECURED ? "lock" : "unlock"));
    return;
  }

  if (entry.shape === "garage") {
    entry.service.getCharacteristic(Characteristic.CurrentDoorState)
      .onGet(() => {
        guardAvailability(entry);
        return entry.on ? Characteristic.CurrentDoorState.OPEN : Characteristic.CurrentDoorState.CLOSED;
      });
    entry.service.getCharacteristic(Characteristic.TargetDoorState)
      .onGet(() => {
        guardAvailability(entry);
        return entry.on ? Characteristic.TargetDoorState.OPEN : Characteristic.TargetDoorState.CLOSED;
      })
      .onSet((value) => sendDeviceCommand(
        entry, value === Characteristic.TargetDoorState.OPEN ? "open" : "close"));
    return;
  }

  entry.service.getCharacteristic(Characteristic.On)
    .onGet(() => {
      guardAvailability(entry);
      return entry.on;
    })
    .onSet(async (value) => {
      if (entry.momentary) {
        // A pulse device has no "off" to send. Switching the tile off is the spring-back below
        // finishing, so it must not publish a second command.
        if (!value) return;
        await sendDeviceCommand(entry, "press");
        if (entry.resetTimer) clearTimeout(entry.resetTimer);
        entry.resetTimer = setTimeout(() => {
          entry.resetTimer = null;
          entry.on = false;
          entry.service.updateCharacteristic(Characteristic.On, false);
        }, MOMENTARY_RESET_MS);
        entry.on = true;
        return;
      }
      await sendDeviceCommand(entry, value ? "on" : "off");
      entry.on = Boolean(value);
    });

  if (entry.supportsBrightness) {
    entry.service.getCharacteristic(Characteristic.Brightness)
      .onGet(() => {
        guardAvailability(entry);
        return entry.brightness;
      })
      // No action alongside it: the main app treats a brightness-only command as "leave the
      // switch where it is", which is what HomeKit means by dragging the slider.
      .onSet(async (value) => {
        await sendDeviceCommand(entry, null, value);
        entry.brightness = Number(value);
      });
  }
}

function createDeviceAccessory(device) {
  const mapping = serviceForKind(device.kind, device);
  const accessory = new Accessory(device.name, uuid.generate(`sentinelcore:device:${device.id}`));
  applyAccessoryInformation(accessory, mapping.model, serialFor("device", device.id));

  const service = accessory.addService(mapping.service, device.name);

  const entry = {
    id: device.id,
    name: device.name,
    kind: String(device.kind).toLowerCase(),
    shape: mapping.shape,
    momentary: device.momentary === true,
    supportsBrightness: device.supportsBrightness === true && mapping.shape === "power",
    accessory,
    service,
    // Assumed reachable until the first state poll says otherwise - a brand new accessory that
    // reports "No Response" before any state has arrived just looks broken.
    available: true,
    on: false,
    brightness: 100,
    resetTimer: null,
  };

  wireDeviceHandlers(entry);
  bridge.addBridgedAccessory(accessory);
  deviceAccessories.set(entry.id, entry);
  console.log(`Added HomeKit accessory for device "${device.name}" (${mapping.model}, serial ${serialOf(accessory)})`);
  return entry;
}

function removeDeviceAccessory(id) {
  const entry = deviceAccessories.get(id);
  if (!entry) return;
  if (entry.resetTimer) clearTimeout(entry.resetTimer);
  bridge.removeBridgedAccessory(entry.accessory);
  deviceAccessories.delete(id);
  console.log(`Removed HomeKit accessory for device "${entry.name}"`);
}

// Whether an existing accessory still matches what the Device Hub now says about the device.
// Name changes are absorbed in place - the Home app keeps whatever the user renamed it to
// anyway - but a changed kind means a different HomeKit service, which needs a new accessory.
function matchesExisting(entry, device) {
  return entry.kind === String(device.kind).toLowerCase()
    && entry.momentary === (device.momentary === true)
    && entry.supportsBrightness === (device.supportsBrightness === true && entry.shape === "power");
}

// Returns whether anything the persisted roster cares about changed, so a rename is written back
// rather than left to be rediscovered after the next restart.
function ensureDeviceAccessory(device) {
  const existing = deviceAccessories.get(device.id);
  if (existing && matchesExisting(existing, device)) {
    if (existing.name === device.name) return false;
    existing.name = device.name;
    existing.accessory.displayName = device.name;
    existing.service.updateCharacteristic(Characteristic.Name, device.name);
    return true;
  }
  if (existing) removeDeviceAccessory(device.id);
  createDeviceAccessory(device);
  return true;
}

function applyDeviceState(entry, state) {
  if (!state) {
    entry.available = false;
    return;
  }

  entry.available = state.available !== false;
  const on = state.power === "on";

  if (entry.shape === "lock") {
    entry.on = on;
    entry.service.updateCharacteristic(Characteristic.LockCurrentState,
      on ? Characteristic.LockCurrentState.SECURED : Characteristic.LockCurrentState.UNSECURED);
    entry.service.updateCharacteristic(Characteristic.LockTargetState,
      on ? Characteristic.LockTargetState.SECURED : Characteristic.LockTargetState.UNSECURED);
    return;
  }

  if (entry.shape === "garage") {
    entry.on = on;
    entry.service.updateCharacteristic(Characteristic.CurrentDoorState,
      on ? Characteristic.CurrentDoorState.OPEN : Characteristic.CurrentDoorState.CLOSED);
    entry.service.updateCharacteristic(Characteristic.TargetDoorState,
      on ? Characteristic.TargetDoorState.OPEN : Characteristic.TargetDoorState.CLOSED);
    return;
  }

  // A pulse device reports no state of its own, so the spring-back timer owns the switch and a
  // poll must not yank it back off mid-press.
  if (!entry.momentary) {
    entry.on = on;
    entry.service.updateCharacteristic(Characteristic.On, on);
  }

  if (entry.supportsBrightness && typeof state.brightness === "number") {
    entry.brightness = state.brightness;
    entry.service.updateCharacteristic(Characteristic.Brightness, state.brightness);
  }
}

function persistDeviceRoster() {
  fs.mkdirSync(PERSIST_DIR, { recursive: true });
  const roster = [...deviceAccessories.values()].map((entry) => ({
    id: entry.id,
    name: entry.name,
    kind: entry.kind,
    momentary: entry.momentary,
    supportsBrightness: entry.supportsBrightness,
  }));
  const tempPath = `${DEVICES_FILE}.${process.pid}.tmp`;
  fs.writeFileSync(tempPath, JSON.stringify(roster, null, 2), { mode: 0o600 });
  fs.renameSync(tempPath, DEVICES_FILE);
}

// Same reason the cameras are restored before publish: the main app may not be answering yet
// when this process starts, and a bridge that publishes empty and grows accessories seconds
// later is how Apple Home ends up with ghost tiles.
function restoreDeviceAccessories() {
  if (!includeDevices) return;

  let roster;
  try {
    if (!fs.existsSync(DEVICES_FILE)) return;
    roster = JSON.parse(fs.readFileSync(DEVICES_FILE, "utf8"));
    if (!Array.isArray(roster)) throw new Error("expected a JSON array");
  } catch (error) {
    console.warn(`Could not load ${DEVICES_FILE}: ${error.message}`);
    return;
  }

  for (const device of roster) {
    if (!isBridgeable(device)) continue;
    if (deviceAccessories.has(device.id)) continue;
    createDeviceAccessory(device);
  }
  if (deviceAccessories.size > 0) {
    console.log(`Restored ${deviceAccessories.size} HomeKit device accessory(ies) before publish.`);
  }
}

// One line per distinct problem rather than one per poll: an unreachable main app would
// otherwise write the same warning every few seconds forever.
let lastDeviceError = null;

function reportDeviceProblem(error) {
  const message = error?.message || String(error);
  if (message !== lastDeviceError) {
    console.warn(`Could not sync Device Hub accessories: ${message}`);
    lastDeviceError = message;
  }
}

function clearDeviceProblem() {
  if (lastDeviceError !== null) {
    console.log("Device Hub sync recovered.");
    lastDeviceError = null;
  }
}

async function syncDevices() {
  if (!includeDevices) {
    if (deviceAccessories.size > 0) {
      for (const id of [...deviceAccessories.keys()]) removeDeviceAccessory(id);
      persistDeviceRoster();
    }
    return;
  }

  let roster;
  let state;
  try {
    [roster, state] = await Promise.all([
      callMainApp("/api/devices"),
      callMainApp("/api/devices/state"),
    ]);
  } catch (error) {
    reportDeviceProblem(error);
    return;
  }
  clearDeviceProblem();

  const wanted = (Array.isArray(roster) ? roster : []).filter(isBridgeable);
  const wantedIds = new Set(wanted.map((device) => device.id));

  let changed = false;
  for (const id of [...deviceAccessories.keys()]) {
    if (!wantedIds.has(id)) {
      removeDeviceAccessory(id);
      changed = true;
    }
  }
  for (const device of wanted) {
    if (ensureDeviceAccessory(device)) changed = true;
  }
  if (changed) persistDeviceRoster();

  const byId = new Map((state?.devices || []).map((item) => [item.id, item]));
  for (const [id, entry] of deviceAccessories) applyDeviceState(entry, byId.get(id));
}

// ---------------------------------------------------------------------------
// Enabled flags - polled from the main app's config
// ---------------------------------------------------------------------------

let enabled = false;
let includeDevices = false;

function readFlags() {
  const settings = readMainSettings();
  // Keep the current state on an unreadable config rather than flapping the bridge off.
  if (!settings) return { enabled, includeDevices };

  const homeKit = settings.HomeKit || {};
  const on = homeKit.Enabled === true;
  return {
    enabled: on,
    // Devices ride on the same pairing as the cameras, so they can only reach Home while HomeKit
    // itself is on. Turning HomeKit off has to take everything off the network, not leave the
    // lights behind.
    includeDevices: on && homeKit.IncludeDevices === true,
  };
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
    const flags = readFlags();
    includeDevices = flags.includeDevices;
    await applyEnabledState(flags.enabled);
  } catch (error) {
    console.error("Failed to apply HomeKit enabled state:", error.message);
  }
}

async function pollDevices() {
  try {
    await syncDevices();
  } catch (error) {
    reportDeviceProblem(error);
  }
}

// ---------------------------------------------------------------------------
// Control API (loopback only - never exposed to the LAN)
// ---------------------------------------------------------------------------

function sendJson(res, statusCode, payload) {
  res.writeHead(statusCode, { "content-type": "application/json" });
  res.end(JSON.stringify(payload));
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
      includeDevices,
      devices: [...deviceAccessories.values()].map((entry) => ({
        id: entry.id,
        name: entry.name,
        kind: entry.kind,
        available: entry.available,
      })),
      // Whatever stopped the last Device Hub sync, so the dashboard can say "the main app isn't
      // answering" rather than showing an empty list that looks like "you have no devices".
      deviceError: lastDeviceError,
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
// consistent accessory graph during pairing and after every sidecar restart. The flags are read
// synchronously first because restoreDeviceAccessories() needs to know whether devices are in
// play before the bridge goes on the network.
includeDevices = readFlags().includeDevices;
restoreMotionAccessories();
restoreDeviceAccessories();
pollSettings();
const pollTimer = setInterval(pollSettings, SETTINGS_POLL_MS);
pollDevices();
const deviceTimer = setInterval(pollDevices, DEVICE_POLL_MS);

// systemd sends SIGTERM on stop/restart; unpublishing cleanly means paired iPhones see the
// bridge go away rather than treating it as unresponsive.
let shuttingDown = false;
for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, async () => {
    if (shuttingDown) return;
    shuttingDown = true;
    console.log(`Received ${signal}, shutting down.`);
    clearInterval(pollTimer);
    clearInterval(deviceTimer);
    server.close();
    if (published) await bridge.unpublish();
    process.exit(0);
  });
}
