"use strict";

const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const fs = require("node:fs");
const http = require("node:http");
const net = require("node:net");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      server.close(() => resolve(address.port));
    });
  });
}

function request(port, method, requestPath) {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { hostname: "127.0.0.1", port, path: requestPath, method },
      (res) => {
        let body = "";
        res.setEncoding("utf8");
        res.on("data", (chunk) => { body += chunk; });
        res.on("end", () => resolve({ statusCode: res.statusCode, body }));
      });
    req.once("error", reject);
    req.end();
  });
}

function waitForOutput(child, expected) {
  return new Promise((resolve, reject) => {
    let output = "";
    const timeout = setTimeout(() => reject(new Error(`Timed out waiting for "${expected}". Output: ${output}`)), 10_000);
    child.stdout.on("data", (chunk) => {
      output += chunk.toString();
      if (output.includes(expected)) {
        clearTimeout(timeout);
        resolve(output);
      }
    });
    child.stderr.on("data", (chunk) => { output += chunk.toString(); });
    child.once("exit", (code) => {
      clearTimeout(timeout);
      reject(new Error(`Sidecar exited early with ${code}. Output: ${output}`));
    });
  });
}

// Polls until the predicate holds. The sidecar reconciles devices on its own timer, so the tests
// below can't assert immediately after startup without racing it.
async function waitFor(read, predicate, what) {
  const deadline = Date.now() + 10_000;
  let last;
  while (Date.now() < deadline) {
    last = await read();
    if (predicate(last)) return last;
    await new Promise((resolve) => setTimeout(resolve, 150));
  }
  throw new Error(`Timed out waiting for ${what}. Last saw: ${JSON.stringify(last)}`);
}

test("restores configured cameras before publish and keeps them through pairing reset", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sentinel-homekit-test-"));
  const persistDir = path.join(root, "persist");
  const settingsPath = path.join(root, "appsettings.json");
  fs.writeFileSync(settingsPath, JSON.stringify({
    HomeKit: { Enabled: false },
    BlueIris: { CameraMappings: { "gate camera front door": "gateCamera" } }
  }));

  const controlPort = await freePort();
  const hapPort = await freePort();
  const child = spawn(process.execPath, [path.join(__dirname, "index.js")], {
    env: {
      ...process.env,
      HOMEKIT_MAIN_SETTINGS: settingsPath,
      HOMEKIT_PERSIST_DIR: persistDir,
      HOMEKIT_CONTROL_PORT: String(controlPort),
      HOMEKIT_HAP_PORT: String(hapPort),
      HOMEKIT_ADVERTISER: "ciao"
    },
    stdio: ["ignore", "pipe", "pipe"]
  });

  try {
    await waitForOutput(child, "Control API listening");
    const statusResponse = await request(controlPort, "GET", "/api/status");
    assert.equal(statusResponse.statusCode, 200);
    const status = JSON.parse(statusResponse.body);
    assert.deepEqual(status.accessories, ["gate camera front door"]);
    assert.equal(status.published, false);
    assert.equal(status.hapPort, hapPort);
    assert.ok(status.startedAt);

    assert.deepEqual(
      JSON.parse(fs.readFileSync(path.join(persistDir, "cameras.json"), "utf8")),
      ["gate camera front door"]);

    const resetResponse = await request(controlPort, "POST", "/api/reset");
    assert.equal(resetResponse.statusCode, 202);
    assert.equal(JSON.parse(resetResponse.body).reset, true);

    const exitCode = await new Promise((resolve) => child.once("exit", resolve));
    assert.equal(exitCode, 75);
    assert.equal(fs.existsSync(path.join(persistDir, "identity.json")), false);
    assert.equal(fs.existsSync(path.join(persistDir, "cameras.json")), true);
  } finally {
    if (child.exitCode === null) child.kill("SIGTERM");
    fs.rmSync(root, { recursive: true, force: true });
  }
});

// Every accessory under one bridge needs its own serial number - hap-nodejs hands them all the
// same "Default-SerialNumber" otherwise, and Apple Home responds to a collision by merging or
// dropping tiles. The serials asserted here are read back off the accessories themselves (the
// sidecar logs what it actually told HomeKit, not what it intended to), because a test that
// re-derives the expected value would still pass if applyAccessoryInformation never ran.
test("gives every bridged accessory a distinct, non-default serial number", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sentinel-homekit-serial-"));
  const persistDir = path.join(root, "persist");
  const settingsPath = path.join(root, "appsettings.json");

  const devices = [
    { id: "11111111-1111-1111-1111-111111111111", name: "Office lights", kind: "light", enabled: true, momentary: false, supportsBrightness: true },
    { id: "22222222-2222-2222-2222-222222222222", name: "Fire place fan plug", kind: "switch", enabled: true, momentary: false, supportsBrightness: false }
  ];
  const stub = await startStubMainApp(devices, devices.map((d) => ({ id: d.id, power: "off", brightness: null, available: true })));

  fs.writeFileSync(settingsPath, JSON.stringify({
    HomeKit: { Enabled: true, IncludeDevices: true },
    Bridge: { ApiKey: "test-key" },
    BlueIris: { CameraMappings: { "front gate": "a", "back door": "b" } }
  }));

  const controlPort = await freePort();
  const hapPort = await freePort();
  const child = spawn(process.execPath, [path.join(__dirname, "index.js")], {
    env: {
      ...process.env,
      HOMEKIT_MAIN_SETTINGS: settingsPath,
      HOMEKIT_PERSIST_DIR: persistDir,
      HOMEKIT_CONTROL_PORT: String(controlPort),
      HOMEKIT_HAP_PORT: String(hapPort),
      HOMEKIT_ADVERTISER: "ciao",
      HOMEKIT_MAIN_APP_URL: `http://127.0.0.1:${stub.port}`,
      HOMEKIT_DEVICE_POLL_MS: "200"
    },
    stdio: ["ignore", "pipe", "pipe"]
  });

  let output = "";
  child.stdout.on("data", (chunk) => { output += chunk.toString(); });

  try {
    // Waiting on the collector above rather than waitForOutput, which would attach a second
    // listener and could miss a line already emitted by the time it does.
    await waitFor(async () => output, (o) => o.includes("Control API listening"),
      "the control API to start");

    const readStatus = async () => JSON.parse((await request(controlPort, "GET", "/api/status")).body);
    await waitFor(readStatus, (s) => s.accessories.length === 2 && s.devices.length === 2,
      "two cameras and two devices");

    const serials = [...output.matchAll(/serial ([^)\s]+)\)/g)].map((match) => match[1]);
    assert.equal(serials.length, 4, `expected four accessory serials, got: ${serials.join(", ")}`);
    assert.equal(new Set(serials).size, 4, `serials must be unique per accessory: ${serials.join(", ")}`);
    for (const serial of serials) {
      assert.notEqual(serial, "Default-SerialNumber");
      assert.match(serial, /^(CAMERA|DEVICE)-[A-Z0-9-]+-[0-9A-F]{8}$/);
    }
  } finally {
    if (child.exitCode === null) child.kill("SIGTERM");
    stub.server.close();
    fs.rmSync(root, { recursive: true, force: true });
  }
});

// A stand-in for the main app's Device Hub API. Records the commands it was sent so the test can
// assert that a HomeKit write actually turned into the right call, without a broker or a device.
function startStubMainApp(devices, state) {
  const commands = [];
  const server = http.createServer((req, res) => {
    if (req.headers["x-api-key"] !== "test-key") {
      res.writeHead(401).end();
      return;
    }
    if (req.url === "/api/devices") {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify(devices));
      return;
    }
    if (req.url === "/api/devices/state") {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({ connected: true, devices: state }));
      return;
    }
    const match = req.url.match(/^\/api\/devices\/([^/]+)\/command$/);
    if (match && req.method === "POST") {
      let body = "";
      req.on("data", (chunk) => { body += chunk; });
      req.on("end", () => {
        commands.push({ id: match[1], body: JSON.parse(body || "{}") });
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ sent: true, acknowledged: true, power: "on", brightness: null }));
      });
      return;
    }
    res.writeHead(404).end();
  });
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => resolve({ server, port: server.address().port, commands }));
  });
}

test("publishes Device Hub devices as HomeKit accessories when IncludeDevices is on", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sentinel-homekit-devices-"));
  const persistDir = path.join(root, "persist");
  const settingsPath = path.join(root, "appsettings.json");

  const devices = [
    { id: "11111111-1111-1111-1111-111111111111", name: "Office lights", kind: "light", enabled: true, momentary: false, supportsBrightness: true },
    { id: "22222222-2222-2222-2222-222222222222", name: "Fire place fan plug", kind: "switch", enabled: true, momentary: false, supportsBrightness: false },
    { id: "33333333-3333-3333-3333-333333333333", name: "gate-esp32", kind: "gate", enabled: true, momentary: true, supportsBrightness: false },
    // Neither of these may reach HomeKit: one is switched off in the hub, the other is a
    // read-only sensor with no honest HomeKit service.
    { id: "44444444-4444-4444-4444-444444444444", name: "Disabled thing", kind: "switch", enabled: false, momentary: false, supportsBrightness: false },
    { id: "55555555-5555-5555-5555-555555555555", name: "Porch temp", kind: "sensor", enabled: true, momentary: false, supportsBrightness: false }
  ];
  const state = [
    { id: devices[0].id, power: "on", brightness: 40, available: true },
    { id: devices[1].id, power: "off", brightness: null, available: true },
    { id: devices[2].id, power: "off", brightness: null, available: false }
  ];

  const stub = await startStubMainApp(devices, state);
  fs.writeFileSync(settingsPath, JSON.stringify({
    HomeKit: { Enabled: true, IncludeDevices: true },
    Bridge: { ApiKey: "test-key" },
    BlueIris: { CameraMappings: {} }
  }));

  const controlPort = await freePort();
  const hapPort = await freePort();
  const child = spawn(process.execPath, [path.join(__dirname, "index.js")], {
    env: {
      ...process.env,
      HOMEKIT_MAIN_SETTINGS: settingsPath,
      HOMEKIT_PERSIST_DIR: persistDir,
      HOMEKIT_CONTROL_PORT: String(controlPort),
      HOMEKIT_HAP_PORT: String(hapPort),
      HOMEKIT_ADVERTISER: "ciao",
      HOMEKIT_MAIN_APP_URL: `http://127.0.0.1:${stub.port}`,
      HOMEKIT_DEVICE_POLL_MS: "200"
    },
    stdio: ["ignore", "pipe", "pipe"]
  });

  try {
    await waitForOutput(child, "Control API listening");

    const readStatus = async () => JSON.parse((await request(controlPort, "GET", "/api/status")).body);
    const status = await waitFor(readStatus, (s) => s.devices.length === 3, "three device accessories");

    assert.equal(status.includeDevices, true);
    assert.equal(status.deviceError, null);
    assert.deepEqual(
      status.devices.map((d) => d.name).sort(),
      ["Fire place fan plug", "Office lights", "gate-esp32"]);

    // The unavailable gate is reported as such rather than quietly drawn as off.
    const gate = status.devices.find((d) => d.name === "gate-esp32");
    assert.equal(gate.kind, "gate");
    assert.equal(gate.available, false);

    // The roster is persisted so the accessory graph can be rebuilt before publish next boot.
    const roster = JSON.parse(fs.readFileSync(path.join(persistDir, "devices.json"), "utf8"));
    assert.equal(roster.length, 3);
    assert.ok(roster.every((entry) => typeof entry.id === "string" && typeof entry.kind === "string"));
  } finally {
    if (child.exitCode === null) child.kill("SIGTERM");
    stub.server.close();
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("keeps Device Hub devices out of HomeKit unless IncludeDevices is on", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sentinel-homekit-nodevices-"));
  const persistDir = path.join(root, "persist");
  const settingsPath = path.join(root, "appsettings.json");

  const devices = [
    { id: "11111111-1111-1111-1111-111111111111", name: "Office lights", kind: "light", enabled: true, momentary: false, supportsBrightness: true }
  ];
  const stub = await startStubMainApp(devices, [{ id: devices[0].id, power: "on", brightness: 40, available: true }]);

  // HomeKit itself is on; only the device opt-in is off. This is the state every existing
  // install upgrades into, and it must leave the paired bridge exactly as it was.
  fs.writeFileSync(settingsPath, JSON.stringify({
    HomeKit: { Enabled: true, IncludeDevices: false },
    Bridge: { ApiKey: "test-key" },
    BlueIris: { CameraMappings: { "gate camera front door": "gateCamera" } }
  }));

  const controlPort = await freePort();
  const hapPort = await freePort();
  const child = spawn(process.execPath, [path.join(__dirname, "index.js")], {
    env: {
      ...process.env,
      HOMEKIT_MAIN_SETTINGS: settingsPath,
      HOMEKIT_PERSIST_DIR: persistDir,
      HOMEKIT_CONTROL_PORT: String(controlPort),
      HOMEKIT_HAP_PORT: String(hapPort),
      HOMEKIT_ADVERTISER: "ciao",
      HOMEKIT_MAIN_APP_URL: `http://127.0.0.1:${stub.port}`,
      HOMEKIT_DEVICE_POLL_MS: "200"
    },
    stdio: ["ignore", "pipe", "pipe"]
  });

  try {
    await waitForOutput(child, "Control API listening");
    // Long enough for several device polls to have come and gone.
    await new Promise((resolve) => setTimeout(resolve, 1200));

    const status = JSON.parse((await request(controlPort, "GET", "/api/status")).body);
    assert.equal(status.includeDevices, false);
    assert.deepEqual(status.devices, []);
    // The camera side is untouched by any of this.
    assert.deepEqual(status.accessories, ["gate camera front door"]);
  } finally {
    if (child.exitCode === null) child.kill("SIGTERM");
    stub.server.close();
    fs.rmSync(root, { recursive: true, force: true });
  }
});

// Locks and non-momentary covers use LockCurrent/TargetState and CurrentDoorState/TargetDoorState
// rather than a plain On, and nothing on the author's own broker exercises either. Without this
// the wrong enum name would only surface as a crash on somebody else's install.
test("maps locks and state-reporting covers onto their own HomeKit services", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sentinel-homekit-shapes-"));
  const persistDir = path.join(root, "persist");
  const settingsPath = path.join(root, "appsettings.json");

  const devices = [
    { id: "aaaaaaaa-0000-0000-0000-000000000001", name: "Front door lock", kind: "lock", enabled: true, momentary: false, supportsBrightness: false },
    { id: "aaaaaaaa-0000-0000-0000-000000000002", name: "Shop garage", kind: "garage", enabled: true, momentary: false, supportsBrightness: false },
    { id: "aaaaaaaa-0000-0000-0000-000000000003", name: "Side cover", kind: "cover", enabled: true, momentary: false, supportsBrightness: false }
  ];
  const stub = await startStubMainApp(devices, [
    { id: devices[0].id, power: "on", brightness: null, available: true },
    { id: devices[1].id, power: "on", brightness: null, available: true },
    // Unavailable on purpose: the only way to prove applyDeviceState actually ran down the
    // lock/door branches is to observe it changing something away from the seeded default.
    { id: devices[2].id, power: "off", brightness: null, available: false }
  ]);

  fs.writeFileSync(settingsPath, JSON.stringify({
    HomeKit: { Enabled: true, IncludeDevices: true },
    Bridge: { ApiKey: "test-key" },
    BlueIris: { CameraMappings: {} }
  }));

  const controlPort = await freePort();
  const hapPort = await freePort();
  const child = spawn(process.execPath, [path.join(__dirname, "index.js")], {
    env: {
      ...process.env,
      HOMEKIT_MAIN_SETTINGS: settingsPath,
      HOMEKIT_PERSIST_DIR: persistDir,
      HOMEKIT_CONTROL_PORT: String(controlPort),
      HOMEKIT_HAP_PORT: String(hapPort),
      HOMEKIT_ADVERTISER: "ciao",
      HOMEKIT_MAIN_APP_URL: `http://127.0.0.1:${stub.port}`,
      HOMEKIT_DEVICE_POLL_MS: "200"
    },
    stdio: ["ignore", "pipe", "pipe"]
  });

  let output = "";
  child.stdout.on("data", (chunk) => { output += chunk.toString(); });
  child.stderr.on("data", (chunk) => { output += chunk.toString(); });

  try {
    await waitForOutput(child, "Control API listening");
    const readStatus = async () => JSON.parse((await request(controlPort, "GET", "/api/status")).body);
    // Applying state to a lock or a door is where a wrong enum name would throw, and the poll
    // that does it runs after the accessories exist - so wait past at least one full cycle.
    const status = await waitFor(readStatus,
      (s) => s.devices.length === 3 && s.devices.some((d) => d.available === false),
      "three device accessories with state applied");

    assert.deepEqual(status.devices.map((d) => d.kind).sort(), ["cover", "garage", "lock"]);
    assert.equal(status.devices.find((d) => d.kind === "lock").available, true);
    assert.equal(status.devices.find((d) => d.kind === "garage").available, true);
    assert.equal(status.devices.find((d) => d.kind === "cover").available, false);
    assert.equal(status.deviceError, null);

    // A non-momentary cover is a door tile, not a switch.
    assert.match(output, /MQTT Lock, serial DEVICE-/);
    assert.match(output, /MQTT Garage Door, serial DEVICE-/);
    assert.match(output, /MQTT Cover, serial DEVICE-/);
    assert.doesNotMatch(output, /\(pulse\)/);
    assert.doesNotMatch(output, /TypeError|is not a function|undefined/);
  } finally {
    if (child.exitCode === null) child.kill("SIGTERM");
    stub.server.close();
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("reports a Device Hub sync failure instead of showing an empty device list", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sentinel-homekit-deverr-"));
  const persistDir = path.join(root, "persist");
  const settingsPath = path.join(root, "appsettings.json");

  // A port with nothing on it: the main app is down, which is the ordinary case while the
  // machine is still booting.
  const deadPort = await freePort();
  fs.writeFileSync(settingsPath, JSON.stringify({
    HomeKit: { Enabled: true, IncludeDevices: true },
    Bridge: { ApiKey: "test-key" },
    BlueIris: { CameraMappings: {} }
  }));

  const controlPort = await freePort();
  const hapPort = await freePort();
  const child = spawn(process.execPath, [path.join(__dirname, "index.js")], {
    env: {
      ...process.env,
      HOMEKIT_MAIN_SETTINGS: settingsPath,
      HOMEKIT_PERSIST_DIR: persistDir,
      HOMEKIT_CONTROL_PORT: String(controlPort),
      HOMEKIT_HAP_PORT: String(hapPort),
      HOMEKIT_ADVERTISER: "ciao",
      HOMEKIT_MAIN_APP_URL: `http://127.0.0.1:${deadPort}`,
      HOMEKIT_DEVICE_POLL_MS: "200"
    },
    stdio: ["ignore", "pipe", "pipe"]
  });

  try {
    await waitForOutput(child, "Control API listening");
    const readStatus = async () => JSON.parse((await request(controlPort, "GET", "/api/status")).body);
    const status = await waitFor(readStatus, (s) => s.deviceError !== null, "a device sync error");

    assert.equal(status.includeDevices, true);
    assert.deepEqual(status.devices, []);
    assert.ok(status.deviceError.length > 0);
  } finally {
    if (child.exitCode === null) child.kill("SIGTERM");
    fs.rmSync(root, { recursive: true, force: true });
  }
});
