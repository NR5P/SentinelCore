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
