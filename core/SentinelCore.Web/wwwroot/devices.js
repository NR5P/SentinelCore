// The Device Hub: lights, plugs, and anything else on the MQTT broker, switchable from here.
// Devices and the broker connection itself live in the main app (see DeviceHubEndpoints/
// MqttDeviceBus in SentinelCore); this dashboard only talks to the proxy routes under
// /api/devices (see DeviceHubProxyEndpoints.cs). Adding, editing, and discovery are Admin-only
// server-side - a Viewer gets a 403 there, which is why those failures are reported plainly
// rather than treated as bugs.
window.GateWatchDevices = (function () {
  const STATE_POLL_MS = 5_000;

  let devices = [];
  let metadata = { kinds: [], schemas: [], discoverySeconds: 10 };
  let metadataLoaded = false;
  let editingId = null;
  let pollTimer = null;
  let discoveryTimer = null;

  function escapeHtml(value) {
    const div = document.createElement("div");
    div.textContent = value ?? "";
    return div.innerHTML;
  }

  // ---------------- Loading ----------------

  async function load() {
    const root = document.getElementById("devices-root");
    try {
      if (!metadataLoaded) {
        const metaResponse = await fetch("/api/devices/metadata");
        if (metaResponse.ok) metadata = await metaResponse.json();
        metadataLoaded = true;
      }

      const response = await fetch("/api/devices");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      devices = await response.json();
      renderList();
      // Straight away, not on the first timer tick - otherwise the broker-connection label sits
      // blank for five seconds every time the tab is opened.
      await refreshState();
      startPolling();
    } catch (err) {
      console.error("Failed to load devices", err);
      root.innerHTML = `<div class="empty-state">could not load devices</div>`;
    }
  }

  // Polling only runs while this tab is on screen - app.js calls stop() when you leave, the
  // same way the live and timeline views shut their own work down.
  function startPolling() {
    stopPolling();
    pollTimer = setInterval(refreshState, STATE_POLL_MS);
  }

  function stopPolling() {
    if (pollTimer) clearInterval(pollTimer);
    pollTimer = null;
  }

  function stop() {
    stopPolling();
    if (discoveryTimer) clearInterval(discoveryTimer);
    discoveryTimer = null;
  }

  async function refreshState() {
    try {
      const response = await fetch("/api/devices/state");
      if (!response.ok) return;
      const payload = await response.json();

      const connectionEl = document.getElementById("device-connection");
      connectionEl.textContent = payload.connected ? "broker connected" : "broker disconnected";
      connectionEl.classList.toggle("camera-test-status-error", !payload.connected);

      for (const state of payload.devices) {
        const device = devices.find(d => d.id === state.id);
        if (!device) continue;
        device.power = state.power;
        device.brightness = state.brightness;
        device.available = state.available;
        device.measurements = state.measurements;
        applyTileState(device);
      }
    } catch {
      // A dropped poll is not worth surfacing - the next one is five seconds away.
    }
  }

  // ---------------- Rendering ----------------

  function renderList() {
    const root = document.getElementById("devices-root");
    root.innerHTML = "";

    if (devices.length === 0) {
      root.innerHTML = `<div class="empty-state">no devices yet - use discover to find what's already on your broker, or add one by hand</div>`;
      return;
    }

    for (const device of devices) root.appendChild(buildTile(device));
  }

  function kindIcon(kind) {
    switch (kind) {
      case "light": return "☀";
      case "switch": case "outlet": return "⏻";
      case "fan": return "❀";
      case "cover": return "▤";
      case "gate": return "⌸";
      case "garage": return "⌂";
      case "lock": return "⚿";
      case "button": return "◎";
      default: return "◉";
    }
  }

  // Kind-flavoured behaviour, mirroring DeviceTopics on the server: cover-like kinds speak
  // open/close (+stop when a stop payload exists), locks speak lock/unlock, and a momentary
  // device is a single stateless press.
  function isCoverLike(device) { return ["cover", "gate", "garage"].includes(device.kind); }
  function isMomentary(device) { return !!device.momentary || device.kind === "button"; }
  function hasStop(device) { return !!(device.payloadStop || "").trim(); }

  // ---------------- Drag to reorder ----------------

  let draggingTile = null;

  // The whole arrangement in one PUT once the drop lands, mirroring the live grid: the DOM is
  // the order the user just made, so it's read back from there. Admin-only server-side - a
  // Viewer's attempt gets a 403 and the list reloads back to the stored order.
  async function saveOrder() {
    const ids = [...document.querySelectorAll(".device-tile")]
      .map(tile => tile.id.replace("device-tile-", ""));
    devices.sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id));

    try {
      const response = await fetch("/api/devices/order", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deviceIds: ids })
      });
      if (!response.ok) {
        if (response.status === 403) alert("Only an admin account can rearrange devices.");
        await load();
      }
    } catch {
      await load();
    }
  }

  function wireDragToReorder(tile, device) {
    tile.draggable = true;
    tile.addEventListener("dragstart", event => {
      draggingTile = tile;
      tile.classList.add("device-tile-dragging");
      event.dataTransfer.effectAllowed = "move";
      // Firefox refuses to start a drag without data attached.
      event.dataTransfer.setData("text/plain", device.id);
    });
    tile.addEventListener("dragend", () => {
      tile.classList.remove("device-tile-dragging");
      if (draggingTile) saveOrder();
      draggingTile = null;
    });
    tile.addEventListener("dragover", event => {
      if (!draggingTile || draggingTile === tile) return;
      event.preventDefault();
      // Live preview: the dragged tile slots in before or after the one under the cursor.
      const rect = tile.getBoundingClientRect();
      const before = (event.clientY - rect.top) < rect.height / 2;
      tile.parentNode.insertBefore(draggingTile, before ? tile : tile.nextSibling);
    });
    tile.addEventListener("drop", event => event.preventDefault());
  }

  function buildTile(device) {
    const tile = document.createElement("div");
    tile.className = "device-tile";
    tile.id = `device-tile-${device.id}`;
    wireDragToReorder(tile, device);

    const header = document.createElement("div");
    header.className = "device-tile-header";
    header.innerHTML = `
      <span class="device-tile-icon">${kindIcon(device.kind)}</span>
      <span class="device-tile-name">${escapeHtml(device.name)}</span>
      <span class="device-tile-room">${escapeHtml(device.room)}</span>
    `;

    const status = document.createElement("div");
    status.className = "device-tile-status";
    status.id = `device-status-${device.id}`;

    // Whatever numbers the device reports, filled in by applyTileState. Stays empty - and takes
    // up no room - for the switches and lamps that report none.
    const measurements = document.createElement("div");
    measurements.className = "device-measurements";
    measurements.id = `device-measurements-${device.id}`;

    const controls = document.createElement("div");
    controls.className = "device-tile-controls";

    if (device.commandable && isMomentary(device)) {
      // A pulse device gets a single press - a gate relay, a garage remote, a doorbell.
      const pressBtn = document.createElement("button");
      pressBtn.className = "btn";
      pressBtn.textContent = "press";
      pressBtn.disabled = !device.enabled;
      pressBtn.dataset.tip = "Send this device its press payload. A momentary device is a pulse - there's no on or off to hold.";
      pressBtn.addEventListener("click", () => sendCommand(device, { action: "press" }));
      controls.appendChild(pressBtn);
    } else if (device.commandable && isCoverLike(device)) {
      const actions = hasStop(device) ? ["open", "stop", "close"] : ["open", "close"];
      for (const action of actions) {
        const btn = document.createElement("button");
        btn.className = "btn";
        btn.textContent = action;
        btn.disabled = !device.enabled;
        btn.addEventListener("click", () => sendCommand(device, { action }));
        controls.appendChild(btn);
      }
    } else if (device.commandable && device.kind === "lock") {
      for (const action of ["unlock", "lock"]) {
        const btn = document.createElement("button");
        btn.className = "btn";
        btn.textContent = action;
        btn.disabled = !device.enabled;
        btn.addEventListener("click", () => sendCommand(device, { action }));
        controls.appendChild(btn);
      }
    } else if (device.commandable) {
      const toggleLabel = document.createElement("label");
      toggleLabel.className = "switch";
      toggleLabel.dataset.tip = "Switch this device on or off.";
      const toggle = document.createElement("input");
      toggle.type = "checkbox";
      toggle.id = `device-toggle-${device.id}`;
      // The visible part of a .switch is the track, not the (zero-sized) checkbox, so there's no
      // adjacent text for a screen reader to associate with it the way the settings rows have.
      toggle.setAttribute("aria-label", `${device.name} power`);
      toggle.checked = device.power === "on";
      toggle.disabled = !device.enabled;
      toggle.addEventListener("change", () => sendCommand(device, { action: toggle.checked ? "on" : "off" }));
      const track = document.createElement("span");
      track.className = "switch-track";
      toggleLabel.append(toggle, track);
      controls.appendChild(toggleLabel);

      if (device.supportsBrightness) {
        const slider = document.createElement("input");
        slider.type = "range";
        slider.className = "device-brightness";
        slider.id = `device-brightness-${device.id}`;
        slider.min = 0;
        slider.max = 100;
        slider.step = 1;
        slider.value = device.brightness ?? 100;
        slider.disabled = !device.enabled;
        slider.setAttribute("aria-label", `${device.name} brightness`);
        // On 'change', not 'input' - dragging fires input continuously, and each one would be
        // its own MQTT publish to a real bulb.
        slider.addEventListener("change", () => sendCommand(device, { brightness: Number(slider.value) }));
        controls.appendChild(slider);
      }
    } else {
      const readOnly = document.createElement("span");
      readOnly.className = "device-tile-readonly";
      readOnly.textContent = "read-only";
      readOnly.dataset.tip = "A sensor reports its state but can't be switched.";
      controls.appendChild(readOnly);
    }

    const actions = document.createElement("div");
    actions.className = "device-tile-actions";

    const editBtn = document.createElement("button");
    editBtn.className = "btn";
    editBtn.textContent = "edit";
    editBtn.setAttribute("data-tip-right", "");
    editBtn.dataset.tip = "Change this device's name, room, or the MQTT topics it uses.";
    editBtn.addEventListener("click", () => openForm(device));

    const deleteBtn = document.createElement("button");
    deleteBtn.className = "btn";
    deleteBtn.textContent = "delete";
    deleteBtn.setAttribute("data-tip-right", "");
    deleteBtn.dataset.tip = "Remove this device from the hub. The device itself is left alone - only this app forgets about it.";
    deleteBtn.addEventListener("click", () => removeDevice(device));

    actions.append(editBtn, deleteBtn);
    tile.append(header, status, measurements, controls, actions);
    applyTileState(device, tile);
    return tile;
  }

  function applyTileState(device, tileEl) {
    const tile = tileEl || document.getElementById(`device-tile-${device.id}`);
    if (!tile) return;

    const readings = device.measurements || [];

    // A thermometer reports numbers and no on/off at all. Saying "state unknown" about a device
    // that is plainly working - and dimming it the way a switched-off lamp is dimmed - reads as
    // broken, so a sensor that's reporting gets said so instead.
    const reporting = !device.commandable && device.power === "unknown" && readings.length > 0;

    const status = tile.querySelector(`#device-status-${device.id}`);
    if (status) {
      if (!device.enabled) status.textContent = "disabled";
      else if (!device.available) status.textContent = "unavailable";
      // A momentary device has no on/off to report - reachable is the whole story.
      else if (isMomentary(device)) status.textContent = "ready";
      else if (reporting) status.textContent = "reporting";
      else if (device.power === "unknown") status.textContent = "state unknown";
      else if (isCoverLike(device)) status.textContent = device.power === "on" ? "open" : "closed";
      else if (device.kind === "lock") status.textContent = device.power === "on" ? "locked" : "unlocked";
      else status.textContent = device.power === "on" ? "on" : "off";

      status.classList.toggle("device-status-on", device.enabled && device.available && device.power === "on");
      status.classList.toggle("device-status-stale", !device.available || !device.enabled);
    }

    renderMeasurements(device, tile, readings);

    tile.classList.toggle("device-tile-off", !reporting && device.power !== "on");
    tile.classList.toggle("device-tile-unavailable", !device.available || !device.enabled);

    const toggle = tile.querySelector(`#device-toggle-${device.id}`);
    if (toggle && document.activeElement !== toggle) toggle.checked = device.power === "on";

    const slider = tile.querySelector(`#device-brightness-${device.id}`);
    // Never yank the slider out from under someone mid-drag.
    if (slider && document.activeElement !== slider && device.brightness != null) slider.value = device.brightness;
  }

  function renderMeasurements(device, tile, readings) {
    const host = tile.querySelector(`#device-measurements-${device.id}`);
    if (!host) return;

    // The rows are rebuilt only when the set of keys changes. A device reports the same handful
    // every time, and throwing the DOM away on every five-second poll would fight anyone trying
    // to read or select what's in it.
    const signature = readings.map(reading => reading.key).join(" ");
    if (host.dataset.keys !== signature) {
      host.dataset.keys = signature;
      host.innerHTML = "";
      for (const reading of readings) {
        const row = document.createElement("div");
        row.className = "device-measurement";

        const key = document.createElement("span");
        key.className = "device-measurement-key";
        key.textContent = reading.key;

        const value = document.createElement("span");
        value.className = "device-measurement-value";
        value.dataset.key = reading.key;

        row.append(key, value);
        host.appendChild(row);
      }
    }

    for (const reading of readings) {
      const valueEl = host.querySelector(`[data-key="${CSS.escape(reading.key)}"]`);
      if (valueEl) valueEl.textContent = formatMeasurement(reading.value);
    }
  }

  // The raw number, minus the float noise a scaled reading picks up on the way here
  // (21.500000000000004). The device's own key is the label: units and friendly names are a
  // display concern no two vendors agree on, and guessing them would be worse than not trying.
  function formatMeasurement(value) {
    if (typeof value !== "number" || !Number.isFinite(value)) return "-";
    return String(Math.round(value * 100) / 100);
  }

  // ---------------- Commands ----------------

  async function sendCommand(device, body) {
    const status = document.getElementById(`device-status-${device.id}`);
    if (status) status.textContent = "sending…";

    try {
      const response = await fetch(`/api/devices/${device.id}/command`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));

      if (!response.ok) {
        if (status) {
          status.textContent = response.status === 403
            ? "not allowed for your account"
            : (payload.error || "command failed");
          status.classList.add("device-status-stale");
        }
        // Put the control back where the device actually is, rather than leaving it showing a
        // change that never happened.
        applyTileState(device);
        return;
      }

      device.power = payload.power;
      if (payload.brightness != null) device.brightness = payload.brightness;
      applyTileState(device);

      // The publish succeeded but the device never confirmed - worth saying, because it usually
      // means the topics are right but the device is off or the state topic is wrong.
      if (payload.acknowledged === false && status) {
        status.textContent = `${device.power} (not confirmed)`;
        status.classList.add("device-status-stale");
      }
    } catch {
      if (status) status.textContent = "network error";
      applyTileState(device);
    }
  }

  async function removeDevice(device) {
    if (!confirm(`Remove "${device.name}" from the Device Hub? The device itself is left alone.`)) return;

    try {
      const response = await fetch(`/api/devices/${device.id}`, { method: "DELETE" });
      if (!response.ok && response.status === 403) {
        alert("Only an admin account can remove a device.");
        return;
      }
      await load();
    } catch (err) {
      console.error("Failed to delete device", err);
    }
  }

  // ---------------- Add / edit form ----------------

  function fieldRow(tip, wide) {
    const row = document.createElement("div");
    row.className = "field-row" + (wide ? " field-row-wide" : "");
    if (tip) row.dataset.tip = tip;
    return row;
  }

  function buildTextField(id, label, value, tip, wide = true) {
    const row = fieldRow(tip, wide);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "text";
    input.className = "field-input";
    input.id = id;
    input.value = value ?? "";
    row.appendChild(input);
    return row;
  }

  function buildNumberField(id, label, value, min, max, tip) {
    const row = fieldRow(tip, false);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "number";
    input.className = "field-input";
    input.id = id;
    input.min = min;
    input.max = max;
    input.step = 1;
    input.value = value;
    row.appendChild(input);
    return row;
  }

  function buildCheckboxField(id, label, checked, tip) {
    const row = fieldRow(tip, true);
    row.classList.add("field-checkbox-row");
    const wrap = document.createElement("label");
    wrap.className = "switch";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.id = id;
    input.checked = !!checked;
    const track = document.createElement("span");
    track.className = "switch-track";
    wrap.append(input, track);
    const text = document.createElement("span");
    text.className = "field-label";
    text.textContent = label;
    row.append(wrap, text);
    return row;
  }

  function buildSelectField(id, label, value, options, tip) {
    const row = fieldRow(tip, false);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const select = document.createElement("select");
    select.className = "field-input";
    select.id = id;
    for (const option of options) {
      const opt = document.createElement("option");
      opt.value = option;
      opt.textContent = option;
      if (option === value) opt.selected = true;
      select.appendChild(opt);
    }
    row.appendChild(select);
    return row;
  }

  function openForm(device) {
    editingId = device ? device.id : null;
    const wrap = document.getElementById("device-form-wrap");
    const fields = document.getElementById("device-form-fields");
    document.getElementById("device-form-errors").innerHTML = "";
    fields.innerHTML = "";
    document.getElementById("device-discovery-wrap").classList.add("hidden");

    const kinds = metadata.kinds.length
      ? metadata.kinds
      : ["light", "switch", "outlet", "fan", "cover", "gate", "garage", "lock", "button", "sensor"];
    const schemas = metadata.schemas.length ? metadata.schemas : ["raw", "json"];

    fields.appendChild(buildTextField("device-name", "Name", device?.name ?? "",
      "What this device is called in the hub, e.g. 'Kitchen Lamp'."));
    fields.appendChild(buildTextField("device-room", "Room", device?.room ?? "",
      "Optional grouping label, e.g. 'Kitchen'. Devices are listed by room, then name."));
    fields.appendChild(buildSelectField("device-kind", "Kind", device?.kind ?? "light", kinds,
      "What sort of device this is. Everything except 'sensor' can be switched. cover/gate/garage get open/close controls, lock gets lock/unlock, and button is a stateless press."));
    fields.appendChild(buildCheckboxField("device-enabled", "Enabled", device?.enabled ?? true,
      "Turn this device off in the hub without deleting it. A disabled device stops being watched or controlled."));
    fields.appendChild(buildCheckboxField("device-momentary", "Momentary (press only)", device?.momentary ?? false,
      "A pulse device: one press publishes the ON/Press payload and nothing more is expected back - a gate relay wired to pulse, a garage remote, a doorbell. The 'button' kind is always momentary."));
    fields.appendChild(buildTextField("device-command-topic", "Command Topic", device?.commandTopic ?? "",
      "The MQTT topic the device listens on for on/off commands, e.g. cmnd/kitchen/POWER or zigbee2mqtt/kitchen/set. No + or # wildcards."));
    fields.appendChild(buildTextField("device-state-topic", "State Topic", device?.stateTopic ?? "",
      "The topic the device reports its current state on, e.g. stat/kitchen/POWER. Leave blank if it never reports - and turn on Optimistic below."));
    fields.appendChild(buildTextField("device-availability-topic", "Availability Topic", device?.availabilityTopic ?? "",
      "Optional. The topic carrying the device's online/offline status, e.g. tele/kitchen/LWT."));
    fields.appendChild(buildSelectField("device-schema", "Payload Schema", device?.schema ?? "raw", schemas,
      "'raw' means the payload is just ON or OFF (Tasmota, ESPHome). 'json' means it's an object like {\"state\":\"ON\"} (Zigbee2MQTT, Home Assistant's JSON light schema)."));
    fields.appendChild(buildTextField("device-state-template", "State Value Key (json only)", device?.stateValueTemplate ?? "state",
      "For the json schema: which key holds ON/OFF. 'state' for Zigbee2MQTT, 'POWER' for Tasmota. Dotted paths like StatusSTS.POWER work for nested payloads."));
    fields.appendChild(buildTextField("device-payload-on", "Payload for ON / Open / Lock / Press", device?.payloadOn ?? "ON",
      "The exact text that switches this device on - or opens a cover/gate (OPEN for a Home Assistant cover), locks a lock (LOCK), or fires a button's press (PRESS).", false));
    fields.appendChild(buildTextField("device-payload-off", "Payload for OFF / Close / Unlock", device?.payloadOff ?? "OFF",
      "The exact text that switches this device off - or closes a cover/gate (CLOSE), or unlocks a lock (UNLOCK).", false));
    fields.appendChild(buildTextField("device-payload-stop", "Payload for Stop (covers, optional)", device?.payloadStop ?? "",
      "Only for cover/gate/garage motors that take a third command to halt mid-travel - STOP for a Home Assistant cover. Leave blank and no stop button is shown.", false));
    fields.appendChild(buildTextField("device-payload-available", "Payload for online", device?.payloadAvailable ?? "online",
      "What the availability topic says when the device is reachable. Tasmota says 'Online'.", false));
    fields.appendChild(buildTextField("device-payload-not-available", "Payload for offline", device?.payloadNotAvailable ?? "offline",
      "What the availability topic says when the device has gone. Tasmota says 'Offline'.", false));
    fields.appendChild(buildCheckboxField("device-optimistic", "Optimistic", device?.optimistic ?? false,
      "Assume a command worked instead of waiting for the device to confirm. Needed for devices that never report their state - without it the tile stays on 'state unknown'."));
    fields.appendChild(buildCheckboxField("device-supports-brightness", "Supports brightness", device?.supportsBrightness ?? false,
      "Show a brightness slider for this device."));
    fields.appendChild(buildTextField("device-brightness-command-topic", "Brightness Command Topic", device?.brightnessCommandTopic ?? "",
      "Only for devices with a separate dimmer topic, e.g. cmnd/kitchen/Dimmer. Leave blank for json devices that take brightness on the main command topic."));
    fields.appendChild(buildTextField("device-brightness-state-topic", "Brightness State Topic", device?.brightnessStateTopic ?? "",
      "Optional. Where a separate dimmer reports its level, e.g. stat/kitchen/Dimmer."));
    fields.appendChild(buildNumberField("device-brightness-scale", "Brightness Scale", device?.brightnessScale ?? 255, 1, 65535,
      "The device's own value for full brightness - 255 for Home Assistant and most Zigbee bulbs, 254 for Zigbee2MQTT, 100 for Tasmota's Dimmer. The slider is always 0-100 and is converted for you."));

    wrap.classList.remove("hidden");
    wrap.scrollIntoView({ behavior: "smooth", block: "nearest" });
    if (window.GateWatchTooltips) window.GateWatchTooltips.schedule();
  }

  function closeForm() {
    editingId = null;
    document.getElementById("device-form-wrap").classList.add("hidden");
  }

  function readForm() {
    const value = id => document.getElementById(id).value.trim();
    return {
      name: value("device-name"),
      room: value("device-room"),
      kind: value("device-kind"),
      enabled: document.getElementById("device-enabled").checked,
      commandTopic: value("device-command-topic"),
      stateTopic: value("device-state-topic"),
      availabilityTopic: value("device-availability-topic"),
      schema: value("device-schema"),
      stateValueTemplate: value("device-state-template"),
      payloadOn: value("device-payload-on"),
      payloadOff: value("device-payload-off"),
      payloadStop: value("device-payload-stop"),
      momentary: document.getElementById("device-momentary").checked,
      payloadAvailable: value("device-payload-available"),
      payloadNotAvailable: value("device-payload-not-available"),
      optimistic: document.getElementById("device-optimistic").checked,
      supportsBrightness: document.getElementById("device-supports-brightness").checked,
      brightnessCommandTopic: value("device-brightness-command-topic"),
      brightnessStateTopic: value("device-brightness-state-topic"),
      brightnessScale: Number(document.getElementById("device-brightness-scale").value),
      discoverySource: "manual"
    };
  }

  async function saveDevice() {
    const errorsEl = document.getElementById("device-form-errors");
    errorsEl.innerHTML = "";

    const endpoint = editingId ? `/api/devices/${editingId}` : "/api/devices";
    const method = editingId ? "PUT" : "POST";

    await postDevice(endpoint, method, readForm(), errorsEl);
  }

  async function postDevice(endpoint, method, body, errorsEl) {
    try {
      const response = await fetch(endpoint, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));

      if (!response.ok) {
        const messages = response.status === 403
          ? ["Only an admin account can add or change a device."]
          : (payload.errors || [payload.error || "Save failed."]);
        for (const message of messages) {
          const li = document.createElement("li");
          li.textContent = message;
          errorsEl.appendChild(li);
        }
        return false;
      }

      closeForm();
      await load();
      return true;
    } catch {
      const li = document.createElement("li");
      li.textContent = "Network error while saving.";
      errorsEl.appendChild(li);
      return false;
    }
  }

  // ---------------- Discovery ----------------

  async function startDiscovery() {
    const wrap = document.getElementById("device-discovery-wrap");
    const statusEl = document.getElementById("device-discovery-status");
    const resultsEl = document.getElementById("device-discovery-results");

    closeForm();
    resultsEl.innerHTML = "";
    wrap.classList.remove("hidden");
    statusEl.classList.remove("hidden");
    statusEl.textContent = "starting scan…";

    try {
      const response = await fetch("/api/devices/discover", { method: "POST" });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        statusEl.textContent = response.status === 403
          ? "Only an admin account can scan for devices."
          : (payload.error || "Could not start a scan.");
        return;
      }

      statusEl.textContent = `listening to the broker for ${payload.scanSeconds} seconds…`;
      pollDiscovery(payload.scanId);
    } catch {
      statusEl.textContent = "Network error while starting the scan.";
    }
  }

  function pollDiscovery(scanId) {
    if (discoveryTimer) clearInterval(discoveryTimer);

    discoveryTimer = setInterval(async () => {
      try {
        const response = await fetch(`/api/devices/discover/${scanId}`);
        if (!response.ok) return;
        const payload = await response.json();
        if (payload.status !== "completed") return;

        clearInterval(discoveryTimer);
        discoveryTimer = null;
        renderDiscoveryResults(payload.results);
      } catch {
        // Keep polling - the scan is still running server-side regardless.
      }
    }, 1_000);
  }

  function renderDiscoveryResults(results) {
    const statusEl = document.getElementById("device-discovery-status");
    const resultsEl = document.getElementById("device-discovery-results");
    resultsEl.innerHTML = "";

    if (!results || results.length === 0) {
      statusEl.textContent = "scan finished - nothing found. Devices only show up if they publish while the scan is running, or if they announce themselves for Home Assistant.";
      return;
    }

    statusEl.textContent = `scan finished - ${results.length} device${results.length === 1 ? "" : "s"} found. Nothing is added until you click add.`;

    for (const candidate of results) {
      const card = document.createElement("div");
      card.className = "camera-card";

      const info = document.createElement("div");
      info.className = "camera-card-info";
      info.innerHTML = `
        <span class="camera-card-name">${escapeHtml(candidate.suggestedName)}${candidate.alreadyAdded ? " (already added)" : ""}</span>
        <span class="camera-card-meta">${escapeHtml(candidate.kind)} &middot; ${escapeHtml(candidate.schema)} payloads &middot; found by ${escapeHtml(candidate.source)}</span>
        <span class="camera-card-meta">cmd: ${escapeHtml(candidate.commandTopic) || "(none)"}</span>
        <span class="camera-card-meta">state: ${escapeHtml(candidate.stateTopic) || "(none)"}</span>
      `;

      const actions = document.createElement("div");
      actions.className = "camera-card-actions";

      const addBtn = document.createElement("button");
      addBtn.className = "btn";
      addBtn.textContent = candidate.alreadyAdded ? "added" : "add";
      addBtn.disabled = candidate.alreadyAdded;
      addBtn.dataset.tip = "Add this device to the hub with the topics found by the scan. You can edit it afterwards.";
      addBtn.addEventListener("click", () => addCandidate(candidate, addBtn));

      const reviewBtn = document.createElement("button");
      reviewBtn.className = "btn";
      reviewBtn.textContent = "review";
      reviewBtn.dataset.tip = "Open the add form pre-filled with what the scan found, so you can check or change it before saving.";
      reviewBtn.addEventListener("click", () => {
        document.getElementById("device-discovery-wrap").classList.add("hidden");
        openForm(candidateToDevice(candidate));
      });

      actions.append(addBtn, reviewBtn);
      card.append(info, actions);
      resultsEl.appendChild(card);
    }
  }

  // A candidate has the same field names as a device minus the identity/state ones, so the edit
  // form can render it directly.
  function candidateToDevice(candidate) {
    return {
      name: candidate.suggestedName,
      room: "",
      kind: candidate.kind,
      enabled: true,
      commandTopic: candidate.commandTopic,
      stateTopic: candidate.stateTopic,
      availabilityTopic: candidate.availabilityTopic,
      schema: candidate.schema,
      stateValueTemplate: candidate.stateValueTemplate,
      payloadOn: candidate.payloadOn,
      payloadOff: candidate.payloadOff,
      payloadStop: candidate.payloadStop ?? "",
      momentary: candidate.momentary ?? false,
      payloadAvailable: "online",
      payloadNotAvailable: "offline",
      optimistic: false,
      supportsBrightness: candidate.supportsBrightness,
      brightnessCommandTopic: candidate.brightnessCommandTopic,
      brightnessStateTopic: candidate.brightnessStateTopic,
      brightnessScale: candidate.brightnessScale
    };
  }

  async function addCandidate(candidate, button) {
    button.disabled = true;
    button.textContent = "adding…";

    const body = { ...candidateToDevice(candidate), discoverySource: candidate.source };
    const errorsEl = document.getElementById("device-form-errors");
    errorsEl.innerHTML = "";

    const saved = await postDevice("/api/devices", "POST", body, errorsEl);
    if (saved) {
      button.textContent = "added";
      document.getElementById("device-discovery-wrap").classList.add("hidden");
    } else {
      // The reasons live in the form's error list, which is hidden behind the discovery panel -
      // send the user to the pre-filled form where they can actually see and fix them.
      button.disabled = false;
      button.textContent = "add";
      document.getElementById("device-discovery-wrap").classList.add("hidden");
      openForm(candidateToDevice(candidate));
    }
  }

  document.getElementById("add-device-btn").addEventListener("click", () => openForm(null));
  document.getElementById("device-cancel-btn").addEventListener("click", closeForm);
  document.getElementById("device-save-btn").addEventListener("click", saveDevice);
  document.getElementById("discover-devices-btn").addEventListener("click", startDiscovery);
  document.getElementById("device-discovery-close-btn").addEventListener("click", () => {
    if (discoveryTimer) clearInterval(discoveryTimer);
    discoveryTimer = null;
    document.getElementById("device-discovery-wrap").classList.add("hidden");
  });

  return { load, renderList, stop };
})();
