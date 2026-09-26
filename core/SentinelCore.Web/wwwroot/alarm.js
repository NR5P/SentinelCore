// The Alarm tab: arm and disarm the house, watch the sensors, and (for admins) set them up.
// Everything here talks to /api/alarm (see AlarmEndpoints.cs); the state machine and the MQTT
// subscription live in the server, so this page is a remote control and a status board, never
// the thing keeping time. It polls while on screen - app.js calls stop() when you leave - and
// counts the delays down between polls from the deadlines the server hands it.
window.SentinelAlarm = (function () {
  const POLL_MS = 2_000;

  let status = null;
  let devices = [];
  let cameras = [];
  let pollTimer = null;
  let countdownTimer = null;
  let clockOffsetMs = 0;
  let pendingPinAction = null;
  let eventsOpen = false;
  let initialised = false;

  const ZONE_LABELS = {
    entry: "entry door",
    perimeter: "perimeter",
    interior: "interior motion",
    always: "24-hour"
  };
  const ZONE_TIPS = {
    entry: "The way in. Opening it while armed starts the entry delay so whoever came in can disarm; the siren only sounds if they don't.",
    perimeter: "Windows and other doors. Instant when armed.",
    interior: "Motion inside. Instant, and by default only watched in Away - people at home walk around.",
    always: "Watched around the clock, armed or not: water leak, smoke, tamper."
  };

  function el(id) { return document.getElementById(id); }

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  let toastTimeout;
  function showToast(message, success) {
    const toast = el("toast");
    clearTimeout(toastTimeout);
    toast.textContent = message;
    toast.className = "toast " + (success ? "success" : "error");
    toastTimeout = setTimeout(() => { toast.className = "toast hidden"; }, 4000);
  }

  // ---------------- Loading ----------------

  async function refreshStatus() {
    try {
      const response = await fetch("/api/alarm/status");
      if (!response.ok) return null;
      status = await response.json();
      if (status.now) clockOffsetMs = new Date(status.now).getTime() - Date.now();
      return status;
    } catch {
      return null;
    }
  }

  async function init() {
    if (initialised) return;
    initialised = true;
    el("alarm-add-sensor-btn").addEventListener("click", () => openSensorForm(null));
    el("alarm-settings-btn").addEventListener("click", openSettings);
    el("alarm-test-btn").addEventListener("click", runTest);
    el("alarm-events-toggle").addEventListener("click", () => {
      eventsOpen = !eventsOpen;
      el("alarm-events").classList.toggle("hidden", !eventsOpen);
      el("alarm-events-toggle").textContent = eventsOpen ? "hide activity" : "show activity";
      if (eventsOpen) loadEvents();
    });
    el("alarm-modal").addEventListener("mousedown", (event) => { if (event.target === el("alarm-modal")) closeModal(); });
    document.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && !el("alarm-modal").classList.contains("hidden")) closeModal();
    });

    const first = await refreshStatus();
    el("tab-alarm").classList.toggle("hidden", !(first && first.enabled));
  }

  async function show() {
    await refreshStatus();
    render();
    loadReferenceData();
    if (eventsOpen) loadEvents();
    stop();
    pollTimer = setInterval(async () => {
      await refreshStatus();
      render();
      if (eventsOpen && status && (status.status === "triggered" || status.status === "pending")) loadEvents();
    }, POLL_MS);
    countdownTimer = setInterval(renderCountdown, 250);
  }

  function stop() {
    if (pollTimer) clearInterval(pollTimer);
    if (countdownTimer) clearInterval(countdownTimer);
    pollTimer = null;
    countdownTimer = null;
  }

  // Devices (for the sensor picker and the siren list) and cameras (for the snapshot list).
  // Both are admin-facing choices, but harmless to read for anyone.
  async function loadReferenceData() {
    try {
      const [deviceResponse, cameraResponse] = await Promise.all([fetch("/api/devices"), fetch("/api/cameras")]);
      devices = deviceResponse.ok ? await deviceResponse.json() : [];
      cameras = cameraResponse.ok ? await cameraResponse.json() : [];
    } catch {
      devices = [];
      cameras = [];
    }
  }

  // ---------------- Rendering ----------------

  function serverNow() { return Date.now() + clockOffsetMs; }

  function secondsLeft(until) {
    if (!until) return 0;
    return Math.max(0, Math.ceil((new Date(until).getTime() - serverNow()) / 1000));
  }

  function modeLabel(mode) {
    return { disarmed: "Disarmed", home: "Home", night: "Night", away: "Away" }[mode] || mode;
  }

  function formatWhen(iso) {
    if (!iso) return "";
    const date = new Date(iso);
    const today = new Date();
    const sameDay = date.toDateString() === today.toDateString();
    return sameDay
      ? date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })
      : date.toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
  }

  function timeAgo(iso) {
    if (!iso) return "never";
    const seconds = Math.max(0, Math.round((serverNow() - new Date(iso).getTime()) / 1000));
    if (seconds < 60) return `${seconds}s ago`;
    if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.round(seconds / 3600)}h ago`;
    return `${Math.round(seconds / 86400)}d ago`;
  }

  function render() {
    const root = el("view-alarm");
    if (!status) {
      el("alarm-headline").textContent = "could not reach the server";
      return;
    }
    if (!status.enabled) {
      el("alarm-headline").textContent = "the alarm is turned off in Settings";
      return;
    }

    const canEdit = !!status.canEdit;
    root.classList.toggle("alarm-readonly", !canEdit);
    el("alarm-add-sensor-btn").classList.toggle("hidden", !canEdit);
    el("alarm-settings-btn").classList.toggle("hidden", !canEdit);
    el("alarm-test-btn").classList.toggle("hidden", !canEdit);

    const connection = el("alarm-connection");
    if (!status.hubEnabled) {
      connection.textContent = "device hub off";
      connection.classList.add("camera-test-status-error");
    } else {
      connection.textContent = status.brokerConnected ? "broker connected" : (status.sensors.length ? "broker disconnected" : "no sensors");
      connection.classList.toggle("camera-test-status-error", !status.brokerConnected && status.sensors.length > 0);
    }

    renderPanel();
    renderSensors();
  }

  function renderPanel() {
    const panel = el("alarm-panel");
    panel.className = `alarm-panel alarm-status-${status.status} alarm-mode-${status.mode}`;

    renderCountdown();

    const detail = el("alarm-detail");
    const by = status.changedBy ? ` by ${escapeHtml(status.changedBy)}` : "";
    if (status.status === "triggered") {
      detail.innerHTML = `<strong>${escapeHtml(status.triggeredBy || "Alarm")}</strong> at ${escapeHtml(formatWhen(status.triggeredAt))}${status.sirenOn ? " &middot; siren on" : ""}. Disarm to silence it.`;
    } else if (status.status === "disarmed") {
      detail.textContent = status.changedAt ? `Disarmed${status.changedBy ? " by " + status.changedBy : ""} ${formatWhen(status.changedAt)}` : "Never armed";
    } else {
      detail.innerHTML = `${modeLabel(status.mode)}${by} since ${escapeHtml(formatWhen(status.changedAt))}`;
    }

    const ready = el("alarm-ready");
    const issues = status.issues || [];
    if (status.status !== "disarmed") {
      ready.className = "alarm-ready hidden";
    } else if (status.ready) {
      ready.className = "alarm-ready alarm-ready-ok";
      ready.textContent = "ready to arm - every sensor is closed and reporting";
    } else {
      ready.className = "alarm-ready alarm-ready-warn";
      ready.textContent = issues.length ? "not ready: " + issues.join(" · ") : "not ready";
    }
    const issuesEl = el("alarm-issues");
    if (status.status !== "disarmed" && issues.length) {
      issuesEl.classList.remove("hidden");
      issuesEl.innerHTML = issues.map(i => `<li>${escapeHtml(i)}</li>`).join("");
    } else {
      issuesEl.classList.add("hidden");
      issuesEl.innerHTML = "";
    }

    const modes = el("alarm-modes");
    modes.innerHTML = "";
    const armed = status.status !== "disarmed";
    const buttons = [
      { mode: "disarmed", label: armed && status.status === "triggered" ? "silence & disarm" : "disarm", tip: "Turn the alarm off. Asks for the PIN if one is set." },
      { mode: "home", label: "home", tip: "Doors and windows watch; motion inside is ignored. Arms at once." },
      { mode: "night", label: "night", tip: "Same sensors as Home, for the night. Arms at once." },
      { mode: "away", label: "away", tip: "Everything watches, including motion inside, after the exit delay." }
    ];
    for (const { mode, label, tip } of buttons) {
      const button = document.createElement("button");
      button.className = "btn alarm-mode-btn" + (status.mode === mode ? " btn-on" : "");
      button.dataset.mode = mode;
      button.dataset.tip = tip;
      button.textContent = label;
      button.disabled = status.mode === mode && status.status !== "triggered";
      button.addEventListener("click", () => mode === "disarmed" ? disarm() : arm(mode, false));
      modes.appendChild(button);
    }
    const panic = document.createElement("button");
    panic.className = "btn alarm-panic-btn";
    panic.dataset.tip = "Set the alarm off right now, whatever mode it is in: siren, notifications and cameras.";
    panic.textContent = "panic";
    panic.disabled = status.status === "triggered";
    panic.addEventListener("click", panicNow);
    modes.appendChild(panic);
  }

  function renderCountdown() {
    if (!status || !status.enabled) return;
    const headline = el("alarm-headline");
    switch (status.status) {
      case "arming":
        headline.textContent = `arming away · ${secondsLeft(status.exitUntil)}s to leave`;
        break;
      case "pending":
        headline.textContent = `entry · ${secondsLeft(status.pendingUntil)}s to disarm`;
        break;
      case "triggered":
        headline.textContent = "🚨 alarm";
        break;
      case "armed":
        headline.textContent = `armed ${modeLabel(status.mode).toLowerCase()}`;
        break;
      default:
        headline.textContent = "disarmed";
    }
  }

  function batteryGlyph(sensor) {
    if (sensor.battery === null || sensor.battery === undefined) return sensor.lowBattery ? "🪫" : "";
    if (sensor.lowBattery) return "🪫";
    return "🔋";
  }

  function renderSensors() {
    const root = el("alarm-sensors");
    root.innerHTML = "";
    const sensors = status.sensors || [];
    if (sensors.length === 0) {
      root.innerHTML = status.hubEnabled
        ? `<div class="empty-state">no sensors yet - add a Device Hub sensor (a door contact, a motion sensor, a leak sensor) and say what it guards</div>`
        : `<div class="empty-state">the alarm listens to Device Hub sensors, and the Device Hub is off - turn on MQTT and DEVICE HUB in Settings, add your sensors there, then come back here</div>`;
      return;
    }

    for (const sensor of sensors) {
      const tile = document.createElement("div");
      tile.className = "alarm-tile" +
        (sensor.state === "open" ? " alarm-tile-open" : "") +
        (!sensor.enabled ? " alarm-tile-disabled" : "") +
        (sensor.offline || sensor.deviceMissing ? " alarm-tile-offline" : "") +
        (sensor.bypassed ? " alarm-tile-bypassed" : "");
      tile.dataset.id = sensor.id;

      const flags = [];
      if (sensor.deviceMissing) flags.push(["device missing", "err"]);
      else if (sensor.offline) flags.push(["offline", "err"]);
      if (sensor.bypassed) flags.push(["skipped until disarm", "warn"]);
      if (sensor.lowBattery) flags.push(["low battery", "warn"]);
      if (!sensor.enabled) flags.push(["disabled", "dim"]);
      else if (!sensor.watched && status.status !== "disarmed") flags.push([`not watched in ${modeLabel(status.mode).toLowerCase()}`, "dim"]);

      const stateWord = sensor.state === "open"
        ? (sensor.zone === "interior" ? "motion" : sensor.zone === "always" ? "alert" : "open")
        : sensor.state === "closed" ? (sensor.zone === "interior" ? "clear" : sensor.zone === "always" ? "ok" : "closed") : "no reading";

      const battery = sensor.battery !== null && sensor.battery !== undefined ? `${batteryGlyph(sensor)} ${Math.round(sensor.battery)}%` : (sensor.lowBattery ? "🪫 low" : "");
      const signal = sensor.linkQuality !== null && sensor.linkQuality !== undefined ? `📶 ${Math.round(sensor.linkQuality)}` : "";

      tile.innerHTML = `
        <div class="alarm-tile-header">
          <span class="alarm-tile-name">${escapeHtml(sensor.name)}</span>
          <span class="alarm-zone alarm-zone-${escapeHtml(sensor.zone)}" data-tip="${escapeHtml(ZONE_TIPS[sensor.zone] || "")}">${escapeHtml(ZONE_LABELS[sensor.zone] || sensor.zone)}</span>
        </div>
        <div class="alarm-tile-state alarm-state-${escapeHtml(sensor.state)}">${escapeHtml(stateWord)}</div>
        <div class="alarm-tile-meta">
          ${battery ? `<span data-tip="Battery, as the sensor last reported it.">${escapeHtml(battery)}</span>` : ""}
          ${signal ? `<span data-tip="Zigbee link quality (0-255). Under about 50 means it is at the edge of range.">${escapeHtml(signal)}</span>` : ""}
          <span data-tip="When the sensor last said anything at all - a battery report counts.">heard ${escapeHtml(timeAgo(sensor.lastSeen))}</span>
          ${sensor.lastChanged ? `<span data-tip="When it last opened or closed.">changed ${escapeHtml(timeAgo(sensor.lastChanged))}</span>` : ""}
        </div>
        ${flags.length ? `<div class="alarm-tile-flags">${flags.map(([text, kind]) => `<span class="alarm-flag alarm-flag-${kind}">${escapeHtml(text)}</span>`).join("")}</div>` : ""}
        <div class="alarm-tile-device">${escapeHtml(sensor.deviceName || "unknown device")}${sensor.room ? ` · ${escapeHtml(sensor.room)}` : ""}</div>
        ${status.canEdit ? `<div class="alarm-tile-actions"><button class="btn alarm-edit-btn" data-tip="Change what this sensor guards and when it is watched.">edit</button><button class="btn alarm-delete-btn" data-tip="Remove this sensor from the alarm. The Device Hub device stays.">remove</button></div>` : ""}
      `;
      if (status.canEdit) {
        tile.querySelector(".alarm-edit-btn").addEventListener("click", () => openSensorForm(sensor));
        tile.querySelector(".alarm-delete-btn").addEventListener("click", () => deleteSensor(sensor));
      }
      root.appendChild(tile);
    }
    if (status.canEdit) wireDragToReorder(root);
  }

  function wireDragToReorder(root) {
    let dragging = null;
    for (const tile of root.querySelectorAll(".alarm-tile")) {
      tile.draggable = true;
      tile.addEventListener("dragstart", () => { dragging = tile; tile.classList.add("alarm-tile-dragging"); });
      tile.addEventListener("dragend", async () => {
        tile.classList.remove("alarm-tile-dragging");
        dragging = null;
        const ids = Array.from(root.querySelectorAll(".alarm-tile")).map(t => t.dataset.id);
        await fetch("/api/alarm/sensors/order", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ids }) });
      });
      tile.addEventListener("dragover", (event) => {
        event.preventDefault();
        if (!dragging || dragging === tile) return;
        const rect = tile.getBoundingClientRect();
        const before = event.clientX < rect.left + rect.width / 2;
        root.insertBefore(dragging, before ? tile : tile.nextSibling);
      });
    }
  }

  async function loadEvents() {
    const root = el("alarm-events");
    try {
      const response = await fetch("/api/alarm/events?limit=100");
      if (!response.ok) return;
      const events = await response.json();
      if (events.length === 0) {
        root.innerHTML = `<div class="empty-state">nothing has happened yet</div>`;
        return;
      }
      root.innerHTML = events.map(e => `
        <div class="alarm-event alarm-event-${escapeHtml(e.kind)}">
          <span class="alarm-event-when">${escapeHtml(formatWhen(e.at))}</span>
          <span class="alarm-event-text">${escapeHtml(e.message)}</span>
        </div>`).join("");
    } catch {
      // The next poll will try again.
    }
  }

  // ---------------- Commands ----------------

  async function post(path, body) {
    const response = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body ?? {})
    });
    let payload = {};
    try { payload = await response.json(); } catch { /* empty reply */ }
    return { ok: response.ok, status: response.status, payload };
  }

  async function arm(mode, force, pin) {
    const result = await post("/api/alarm/arm", { mode, force, pin });
    if (result.ok) {
      if (result.payload.note) showToast(result.payload.note, true);
      await refreshStatus();
      render();
      return;
    }
    if (result.status === 403 && status.pinSet) {
      openPinPrompt(`Enter the PIN to arm ${modeLabel(mode).toLowerCase()}`, (entered) => arm(mode, force, entered), result.payload.error);
      return;
    }
    if (result.status === 409 && result.payload.openSensors && result.payload.openSensors.length) {
      openConfirm(
        "Something is open",
        result.payload.error,
        "arm anyway",
        () => arm(mode, true, pin));
      return;
    }
    showToast(result.payload.error || `could not arm (HTTP ${result.status})`, false);
  }

  async function disarm(pin) {
    const result = await post("/api/alarm/disarm", { pin });
    if (result.ok) {
      await refreshStatus();
      render();
      return;
    }
    if (result.status === 403 && status.pinSet) {
      openPinPrompt("Enter the PIN to disarm", (entered) => disarm(entered), pin ? result.payload.error : null);
      return;
    }
    showToast(result.payload.error || `could not disarm (HTTP ${result.status})`, false);
  }

  function panicNow() {
    openConfirm("Set the alarm off?", "The siren, everyone's phones and the cameras - right now.", "set it off", async () => {
      const result = await post("/api/alarm/panic");
      if (!result.ok) showToast(result.payload.error || "could not trigger", false);
      await refreshStatus();
      render();
    });
  }

  async function runTest() {
    const result = await post("/api/alarm/test");
    if (result.ok) showToast(result.payload.note || "test sent - siren for 5 seconds, a notification, and the cameras", !result.payload.note);
    else showToast(result.payload.error || "test failed", false);
    if (eventsOpen) loadEvents();
  }

  async function deleteSensor(sensor) {
    openConfirm("Remove this sensor?", `${sensor.name} will no longer be part of the alarm. Its Device Hub device is not touched.`, "remove", async () => {
      const response = await fetch(`/api/alarm/sensors/${sensor.id}`, { method: "DELETE" });
      if (!response.ok) showToast(`could not remove (HTTP ${response.status})`, false);
      await refreshStatus();
      render();
    });
  }

  // ---------------- Modals ----------------

  function openModal(title, bodyNode, footerButtons) {
    const modal = el("alarm-modal");
    el("alarm-modal-title").textContent = title;
    const body = el("alarm-modal-body");
    body.innerHTML = "";
    body.appendChild(bodyNode);
    const footer = el("alarm-modal-footer");
    footer.innerHTML = "";
    for (const button of footerButtons) footer.appendChild(button);
    modal.classList.remove("hidden");
    const focus = body.querySelector("input, select");
    if (focus) focus.focus();
  }

  function closeModal() {
    el("alarm-modal").classList.add("hidden");
    pendingPinAction = null;
  }

  function button(label, onClick, extraClass) {
    const b = document.createElement("button");
    b.className = "btn" + (extraClass ? " " + extraClass : "");
    b.textContent = label;
    b.addEventListener("click", onClick);
    return b;
  }

  function openConfirm(title, text, confirmLabel, onConfirm) {
    const body = document.createElement("p");
    body.className = "alarm-modal-text";
    body.textContent = text;
    openModal(title, body, [
      button("cancel", closeModal),
      button(confirmLabel, async () => { closeModal(); await onConfirm(); }, "alarm-confirm-btn")
    ]);
  }

  function openPinPrompt(title, onPin, error) {
    const body = document.createElement("div");
    body.className = "alarm-pin";
    body.innerHTML = `
      <input class="field-input alarm-pin-input" type="password" inputmode="numeric" pattern="[0-9]*" autocomplete="one-time-code" maxlength="8" placeholder="PIN">
      <div class="field-errors alarm-pin-error">${error ? escapeHtml(error) : ""}</div>`;
    const input = body.querySelector("input");
    const submit = () => {
      const pin = input.value.trim();
      if (!pin) return;
      closeModal();
      onPin(pin);
    };
    input.addEventListener("keydown", (event) => { if (event.key === "Enter") submit(); });
    openModal(title, body, [button("cancel", closeModal), button("ok", submit, "alarm-confirm-btn")]);
  }

  // ---------------- Sensor form ----------------

  function fieldRow(label, control, tip) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide";
    if (tip) row.dataset.tip = tip;
    const labelEl = document.createElement("label");
    labelEl.className = "field-label";
    labelEl.textContent = label;
    row.append(labelEl, control);
    return row;
  }

  function checkbox(id, label, checked, tip) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide field-checkbox-row";
    if (tip) row.dataset.tip = tip;
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

  function openSensorForm(sensor) {
    const form = document.createElement("div");
    form.className = "field-grid";

    const taken = new Set((status.sensors || []).filter(s => !sensor || s.id !== sensor.id).map(s => s.deviceId));
    const sensorDevices = devices.filter(d => d.kind === "sensor" && !taken.has(d.id));
    const deviceSelect = document.createElement("select");
    deviceSelect.className = "field-input";
    deviceSelect.id = "alarm-f-device";
    if (sensorDevices.length === 0 && !sensor) {
      const opt = document.createElement("option");
      opt.value = "";
      opt.textContent = "no unused sensor devices in the Device Hub";
      deviceSelect.appendChild(opt);
    }
    for (const device of sensorDevices.concat(sensor && !sensorDevices.some(d => d.id === sensor.deviceId) ? [{ id: sensor.deviceId, name: sensor.deviceName || "(missing device)", room: sensor.room }] : [])) {
      const opt = document.createElement("option");
      opt.value = device.id;
      opt.textContent = device.room ? `${device.name} (${device.room})` : device.name;
      if (sensor && device.id === sensor.deviceId) opt.selected = true;
      deviceSelect.appendChild(opt);
    }
    form.appendChild(fieldRow("Device Hub device", deviceSelect, "Which sensor this is. Only devices of kind 'sensor' are offered - add the sensor on the Device Hub tab first (discover finds Zigbee2MQTT ones by itself)."));

    const name = document.createElement("input");
    name.className = "field-input";
    name.id = "alarm-f-name";
    name.type = "text";
    name.value = sensor ? sensor.name : (sensorDevices[0] ? sensorDevices[0].name : "");
    name.placeholder = "Front door";
    deviceSelect.addEventListener("change", () => {
      if (name.value.trim()) return;
      const chosen = sensorDevices.find(d => d.id === deviceSelect.value);
      if (chosen) name.value = chosen.name;
    });
    form.appendChild(fieldRow("Name", name, "How the sensor is named in alerts: 'Front door opened'."));

    const zone = document.createElement("select");
    zone.className = "field-input";
    zone.id = "alarm-f-zone";
    for (const z of status.zones || ["entry", "perimeter", "interior", "always"]) {
      const opt = document.createElement("option");
      opt.value = z;
      opt.textContent = ZONE_LABELS[z] || z;
      if ((sensor ? sensor.zone : "perimeter") === z) opt.selected = true;
      zone.appendChild(opt);
    }
    form.appendChild(fieldRow("What it guards", zone, Object.values(ZONE_TIPS).join(" ")));

    const modesWrap = document.createElement("div");
    modesWrap.className = "alarm-form-modes";
    const home = checkbox("alarm-f-home", "watched in Home", sensor ? sensor.armedHome : true, "Doors and windows usually yes; motion inside usually no.");
    const night = checkbox("alarm-f-night", "watched in Night", sensor ? sensor.armedNight : true, "");
    const away = checkbox("alarm-f-away", "watched in Away", sensor ? sensor.armedAway : true, "");
    modesWrap.append(home, night, away);
    form.appendChild(modesWrap);
    const applyZoneDefaults = () => {
      const always = zone.value === "always";
      modesWrap.classList.toggle("hidden", always);
      if (sensor) return;
      const interior = zone.value === "interior";
      home.querySelector("input").checked = !interior;
      night.querySelector("input").checked = !interior;
      away.querySelector("input").checked = true;
    };
    zone.addEventListener("change", applyZoneDefaults);
    applyZoneDefaults();

    form.appendChild(checkbox("alarm-f-inverted", "reads the other way round", sensor ? sensor.inverted : false,
      "Most sensors say ON for open, motion or wet. If this one shows 'open' while the door is shut, turn this on."));
    form.appendChild(checkbox("alarm-f-enabled", "enabled", sensor ? sensor.enabled : true,
      "Off means the alarm ignores this sensor entirely, without forgetting it."));

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    errors.id = "alarm-f-errors";
    form.appendChild(errors);

    const save = async () => {
      const body = {
        deviceId: deviceSelect.value,
        name: name.value.trim(),
        zone: zone.value,
        armedHome: home.querySelector("input").checked,
        armedNight: night.querySelector("input").checked,
        armedAway: away.querySelector("input").checked,
        inverted: el("alarm-f-inverted").checked,
        enabled: el("alarm-f-enabled").checked
      };
      if (!body.deviceId) {
        errors.innerHTML = `<li>Pick the Device Hub device this sensor is.</li>`;
        return;
      }
      const response = await fetch(sensor ? `/api/alarm/sensors/${sensor.id}` : "/api/alarm/sensors", {
        method: sensor ? "PUT" : "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      if (!response.ok) {
        let error = `HTTP ${response.status}`;
        try { error = (await response.json()).error || error; } catch { /* keep */ }
        errors.innerHTML = `<li>${escapeHtml(error)}</li>`;
        return;
      }
      closeModal();
      showToast(sensor ? "sensor saved" : "sensor added", true);
      await refreshStatus();
      render();
    };

    openModal(sensor ? "Edit sensor" : "Add sensor", form, [button("cancel", closeModal), button("save", save, "alarm-confirm-btn")]);
  }

  // ---------------- Settings form ----------------

  function numberField(id, label, value, min, max, tip) {
    const input = document.createElement("input");
    input.className = "field-input";
    input.type = "number";
    input.id = id;
    input.min = min;
    input.max = max;
    input.step = 1;
    input.value = value;
    return fieldRow(label, input, tip);
  }

  function checkList(id, items, chosen, emptyText) {
    const wrap = document.createElement("div");
    wrap.className = "alarm-checklist";
    wrap.id = id;
    if (items.length === 0) {
      wrap.innerHTML = `<div class="empty-state">${escapeHtml(emptyText)}</div>`;
      return wrap;
    }
    for (const item of items) {
      const label = document.createElement("label");
      label.className = "alarm-checklist-item";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = item.id;
      input.checked = chosen.includes(item.id);
      label.append(input, document.createTextNode(" " + item.name));
      wrap.appendChild(label);
    }
    return wrap;
  }

  function chosenIds(wrap) {
    return Array.from(wrap.querySelectorAll("input:checked")).map(i => i.value);
  }

  function openSettings() {
    const config = status.config;
    const form = document.createElement("div");
    form.className = "field-grid";

    form.appendChild(numberField("alarm-c-exit", "Exit delay (seconds)", config.exitDelaySeconds, 0, 600,
      "How long you have to leave after arming Away before the sensors watch. Home and Night arm at once."));
    form.appendChild(numberField("alarm-c-entry", "Entry delay (seconds)", config.entryDelaySeconds, 0, 600,
      "How long an entry door may be open before the siren, so whoever came in can disarm. 0 makes entry doors instant."));
    form.appendChild(numberField("alarm-c-siren", "Siren time (seconds)", config.sirenSeconds, 0, 3600,
      "How long the siren devices stay on after a trigger. Disarming turns them off sooner. 0 means never switch them on."));

    const switchable = devices.filter(d => d.kind !== "sensor" && d.kind !== "button" && d.kind !== "cover" && d.kind !== "gate" && d.kind !== "garage" && d.kind !== "lock");
    const sirens = checkList("alarm-c-sirens", switchable, config.sirenDeviceIds || [],
      "no switchable Device Hub devices yet - a siren, a strobe or a plug with a lamp on it, added on the Device Hub tab, can be switched on when the alarm goes off");
    form.appendChild(fieldRow("Switch on when triggered", sirens, "Device Hub devices switched ON when the alarm triggers and OFF when it is disarmed or the siren time is up. A siren, every lamp in the house, or a plug with a lamp on it for testing."));

    const cameraList = checkList("alarm-c-cameras", cameras.map(c => ({ id: c.id, name: c.name })), config.cameraIds || [],
      "no cameras yet");
    form.appendChild(fieldRow("Cameras to snapshot", cameraList, "Which cameras grab a frame through the AI pipeline when the alarm goes off. None ticked means all of them."));

    form.appendChild(checkbox("alarm-c-push", "notify everyone's phones", config.notifyPush, "Push notifications for triggers, low batteries and sensors going offline - through the same Push setup as the camera alerts."));
    form.appendChild(checkbox("alarm-c-push-arm", "also on every arm and disarm", config.notifyArmDisarm, "A quieter alarm tells the phones only when it goes off."));
    form.appendChild(numberField("alarm-c-battery", "Low battery at (%)", config.lowBatteryPercent, 1, 90, "A sensor at or below this is flagged and everyone is told once."));
    form.appendChild(numberField("alarm-c-offline", "Offline after (hours)", config.offlineAfterHours, 1, 720, "A sensor silent for this long is treated as offline. Zigbee battery sensors check in at least hourly."));
    form.appendChild(checkbox("alarm-c-pin-arm", "ask for the PIN to arm as well", config.requirePinToArm, "Normally only disarming needs the PIN."));

    const pinRow = document.createElement("div");
    pinRow.className = "field-row field-row-wide";
    pinRow.dataset.tip = "4 to 8 digits. Anyone signed in can arm; with a PIN set, disarming needs it. Leave blank to keep the current one; enter and tick 'remove' to clear it.";
    pinRow.innerHTML = `<label class="field-label" for="alarm-c-pin">PIN ${status.pinSet ? "(set - enter a new one to change it)" : "(none - anyone signed in can disarm)"}</label>
      <input class="field-input" id="alarm-c-pin" type="password" inputmode="numeric" pattern="[0-9]*" maxlength="8" autocomplete="off" placeholder="${status.pinSet ? "unchanged" : "no PIN"}">`;
    form.appendChild(pinRow);
    if (status.pinSet) form.appendChild(checkbox("alarm-c-pin-remove", "remove the PIN", false, "Clear the PIN so anyone signed in can disarm."));

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    form.appendChild(errors);

    const save = async () => {
      const body = {
        exitDelaySeconds: Number(el("alarm-c-exit").value),
        entryDelaySeconds: Number(el("alarm-c-entry").value),
        sirenSeconds: Number(el("alarm-c-siren").value),
        sirenDeviceIds: chosenIds(sirens),
        cameraIds: chosenIds(cameraList),
        notifyPush: el("alarm-c-push").checked,
        notifyArmDisarm: el("alarm-c-push-arm").checked,
        lowBatteryPercent: Number(el("alarm-c-battery").value),
        offlineAfterHours: Number(el("alarm-c-offline").value),
        requirePinToArm: el("alarm-c-pin-arm").checked
      };
      const response = await fetch("/api/alarm/config", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        let error = `HTTP ${response.status}`;
        try { error = (await response.json()).error || error; } catch { /* keep */ }
        errors.innerHTML = `<li>${escapeHtml(error)}</li>`;
        return;
      }

      const remove = status.pinSet && el("alarm-c-pin-remove") && el("alarm-c-pin-remove").checked;
      const newPin = el("alarm-c-pin").value.trim();
      if (remove || newPin) {
        const pinResponse = await fetch("/api/alarm/pin", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ pin: remove ? "" : newPin }) });
        if (!pinResponse.ok) {
          let error = `HTTP ${pinResponse.status}`;
          try { error = (await pinResponse.json()).error || error; } catch { /* keep */ }
          errors.innerHTML = `<li>Settings saved, but the PIN was not: ${escapeHtml(error)}</li>`;
          return;
        }
      }
      closeModal();
      showToast("alarm settings saved", true);
      await refreshStatus();
      render();
    };

    openModal("Alarm settings", form, [button("cancel", closeModal), button("save", save, "alarm-confirm-btn")]);
  }

  window.addEventListener("DOMContentLoaded", init);

  return { show, stop, init };
})();
