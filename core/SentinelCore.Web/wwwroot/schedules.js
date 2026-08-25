// Schedules: "at this time - or at sunset, or when it gets dark - do this". The clock-driven
// counterpart to the automation rules alongside them, sharing the same action vocabulary plus a
// Device Hub option. Schedules live in the main app (see ScheduleEndpoints/ScheduleWorker in
// SentinelCore); this dashboard only talks to the proxy routes under /api/schedules (see
// ScheduleProxyEndpoints.cs).
window.SentinelSchedules = (function () {
  let schedules = [];
  let metadata = {
    kinds: [], solarEvents: [], darknessLevels: [], actionTypes: [], catchUpPolicies: [],
    deviceActions: [], tokens: [], devices: [], locationSet: false, timeZone: "",
    minimumIntervalSeconds: 10
  };
  let editingId = null;
  let loaded = false;
  let previewTimer = null;

  const DAY_NAMES = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

  function escapeHtml(value) {
    // Deliberately a string replace rather than the textContent/innerHTML trick this used to be.
    // That trick escapes exactly what a *text node* needs - & < > - and quotes are not among them,
    // because a quote means nothing in text. Half the interpolations here are into attributes
    // (alt=, title=, data-tip=, value=), and there a surviving double quote ends the attribute and
    // everything after it is parsed as more attributes. Confirmed against a real DOM: an event
    // summary reading `A man held a sign reading " onerror=alert(1) x="` produced an <img> carrying
    // an onerror the markup never contained.
    //
    // Vision models write these summaries, and they quote things - a sign, a brand, a plate - so
    // the merely-broken version of this happens without anybody attacking anything.
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function el(id) {
    return document.getElementById(id);
  }

  async function load() {
    const root = el("schedules-root");
    try {
      if (!loaded) {
        const metaResponse = await fetch("/api/schedules/metadata");
        if (metaResponse.ok) metadata = await metaResponse.json();
        loaded = true;
      }

      const timezoneEl = el("schedule-timezone");
      if (timezoneEl) timezoneEl.textContent = metadata.timeZone ? `times are ${metadata.timeZone}` : "";

      const response = await fetch("/api/schedules");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      schedules = await response.json();
      renderList();
    } catch (err) {
      console.error("Failed to load schedules", err);
      root.innerHTML = `<div class="empty-state">could not load schedules</div>`;
    }
  }

  // Reloads the device list and the location flag, which change under this tab's feet - a device
  // added on the Device Hub tab has to be selectable here without a page refresh.
  async function refreshMetadata() {
    try {
      const response = await fetch("/api/schedules/metadata");
      if (response.ok) metadata = await response.json();
    } catch {
      // Keeping the previous metadata is better than emptying the form's dropdowns.
    }
  }

  function renderList() {
    const root = el("schedules-root");
    root.innerHTML = "";
    if (schedules.length === 0) {
      root.innerHTML = `<div class="empty-state">no schedules yet - add one to switch a device, publish MQTT, or call an API endpoint at a time of your choosing, at sunset, or when it gets dark</div>`;
      return;
    }

    for (const schedule of schedules) root.appendChild(buildCard(schedule));
  }

  function formatLocal(value) {
    if (!value) return "";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "";
    return date.toLocaleString([], {
      weekday: "short", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit"
    });
  }

  function describeTiming(schedule) {
    const days = schedule.daysOfWeek.length === 0
      ? "every day"
      : schedule.daysOfWeek.map(d => DAY_NAMES[d]).join(", ");

    switch (schedule.kind) {
      case "daily":
        return `at ${schedule.timeOfDay}, ${days}`;
      case "interval": {
        const every = schedule.intervalSeconds % 3600 === 0
          ? `${schedule.intervalSeconds / 3600}h`
          : schedule.intervalSeconds % 60 === 0
            ? `${schedule.intervalSeconds / 60}m`
            : `${schedule.intervalSeconds}s`;
        const window = schedule.windowStart && schedule.windowEnd
          ? `, ${schedule.windowStart}-${schedule.windowEnd}`
          : "";
        return `every ${every}${window}`;
      }
      case "cron":
        return `cron: ${schedule.cronExpression}`;
      case "once":
        return `once, ${formatLocal(schedule.runAtUtc)}`;
      case "solar":
        return describeSolar(schedule, days);
      default:
        return schedule.kind;
    }
  }

  function describeSolar(schedule, days) {
    const offset = schedule.solarOffsetMinutes === 0
      ? "at"
      : schedule.solarOffsetMinutes > 0
        ? `${schedule.solarOffsetMinutes}m after`
        : `${Math.abs(schedule.solarOffsetMinutes)}m before`;

    if (schedule.solarEvent !== "dusk" && schedule.solarEvent !== "dawn")
      return `${offset} ${schedule.solarEvent}, ${days}`;

    const preset = (metadata.darknessLevels || []).find(
      level => Math.abs(level.elevationDegrees - schedule.solarElevationDegrees) < 0.01);
    const level = preset ? preset.label.toLowerCase() : `${schedule.solarElevationDegrees}°`;
    const half = schedule.solarEvent === "dusk" ? "evening" : "morning";
    return `${offset} ${level} (${half}), ${days}`;
  }

  function describeAction(schedule) {
    if (schedule.action.type === "device") {
      const device = (metadata.devices || []).find(d => d.id === schedule.action.deviceId);
      const name = device ? device.name : "(device no longer in the hub)";
      const brightness = schedule.action.deviceBrightness === null || schedule.action.deviceBrightness === undefined
        ? ""
        : ` at ${schedule.action.deviceBrightness}%`;
      return `${escapeHtml(schedule.action.deviceAction || "set")} ${escapeHtml(name)}${brightness}`;
    }
    if (schedule.action.type === "mqtt")
      return `MQTT &rarr; ${escapeHtml(schedule.action.mqttTopic) || "(no topic set)"}`;
    return `Webhook &rarr; ${escapeHtml(schedule.action.webhookMethod)} ${escapeHtml(schedule.action.webhookUrl) || "(no URL set)"}`;
  }

  function describeLastRun(schedule) {
    if (!schedule.lastStatus) return "never run";

    const when = schedule.lastRunUtc ? formatLocal(schedule.lastRunUtc) : "";
    const detail = schedule.lastDetail ? ` - ${escapeHtml(schedule.lastDetail)}` : "";
    return `last: ${escapeHtml(schedule.lastStatus)}${when ? ` ${when}` : ""}${detail}`;
  }

  function buildCard(schedule) {
    const card = document.createElement("div");
    card.className = "camera-card";

    const info = document.createElement("div");
    info.className = "camera-card-info";

    // A sun-following schedule with no location set never fires, and the row is the only place
    // that is visible - so it says so rather than just showing an empty next-run time.
    const needsLocation = schedule.kind === "solar" && !metadata.locationSet;
    const next = needsLocation
      ? "no location set - add your town under Settings"
      : schedule.nextDueUtc
        ? `next: ${formatLocal(schedule.nextDueUtc)}`
        : schedule.enabled ? "no upcoming run" : "";

    info.innerHTML = `
      <span class="camera-card-name">${escapeHtml(schedule.name)}${schedule.enabled ? "" : " (disabled)"}</span>
      <span class="camera-card-meta">${escapeHtml(describeTiming(schedule))} &middot; ${describeAction(schedule)}</span>
      <span class="camera-card-meta">${escapeHtml(next)}</span>
      <span class="camera-card-meta">${describeLastRun(schedule)}</span>
      <span class="camera-card-meta" id="schedule-status-${schedule.id}"></span>
      <div class="camera-card-meta hidden" id="schedule-runs-${schedule.id}" style="white-space:normal;"></div>
    `;

    const actions = document.createElement("div");
    actions.className = "camera-card-actions";

    const runBtn = document.createElement("button");
    runBtn.className = "btn";
    runBtn.textContent = "run now";
    runBtn.setAttribute("data-tip-right", "");
    runBtn.dataset.tip = "Do this schedule's action right now, ignoring its timing. This really switches the device or sends the message.";
    runBtn.addEventListener("click", () => runNow(schedule, runBtn));

    const toggleBtn = document.createElement("button");
    toggleBtn.className = "btn";
    toggleBtn.textContent = schedule.enabled ? "disable" : "enable";
    toggleBtn.setAttribute("data-tip-right", "");
    toggleBtn.dataset.tip = "Stop or resume this schedule without deleting it. Its history is kept either way.";
    toggleBtn.addEventListener("click", () => setEnabled(schedule, !schedule.enabled));

    const historyBtn = document.createElement("button");
    historyBtn.className = "btn";
    historyBtn.textContent = "history";
    historyBtn.setAttribute("data-tip-right", "");
    historyBtn.dataset.tip = "Every recent firing and what came of it - the place to check whether last night's run actually happened.";
    historyBtn.addEventListener("click", () => toggleHistory(schedule));

    const editBtn = document.createElement("button");
    editBtn.className = "btn";
    editBtn.textContent = "edit";
    editBtn.setAttribute("data-tip-right", "");
    editBtn.dataset.tip = "Change when this schedule fires or what it does.";
    editBtn.addEventListener("click", () => openForm(schedule));

    const deleteBtn = document.createElement("button");
    deleteBtn.className = "btn";
    deleteBtn.textContent = "delete";
    deleteBtn.setAttribute("data-tip-right", "");
    deleteBtn.dataset.tip = "Remove this schedule and its history. It stops firing immediately.";
    deleteBtn.addEventListener("click", () => remove(schedule));

    actions.append(runBtn, toggleBtn, historyBtn, editBtn, deleteBtn);
    card.append(info, actions);
    return card;
  }

  async function runNow(schedule, button) {
    const statusEl = el(`schedule-status-${schedule.id}`);
    button.disabled = true;
    const originalText = button.textContent;
    button.textContent = "running…";
    if (statusEl) { statusEl.textContent = ""; statusEl.className = "camera-card-meta"; }

    try {
      const response = await fetch(`/api/schedules/${schedule.id}/run`, { method: "POST" });
      const payload = await response.json().catch(() => ({}));
      if (statusEl) {
        statusEl.textContent = response.ok ? `ok: ${payload.detail}` : `failed: ${payload.error || "unknown error"}`;
        statusEl.classList.add(response.ok ? "camera-test-status-ok" : "camera-test-status-error");
      }
    } catch {
      if (statusEl) {
        statusEl.textContent = "network error while running";
        statusEl.classList.add("camera-test-status-error");
      }
    } finally {
      button.disabled = false;
      button.textContent = originalText;
    }
  }

  async function setEnabled(schedule, enabled) {
    try {
      await fetch(`/api/schedules/${schedule.id}/enabled`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled })
      });
      await load();
    } catch (err) {
      console.error("Failed to change whether a schedule is enabled", err);
    }
  }

  async function toggleHistory(schedule) {
    const target = el(`schedule-runs-${schedule.id}`);
    if (!target) return;

    if (!target.classList.contains("hidden")) {
      target.classList.add("hidden");
      return;
    }

    target.classList.remove("hidden");
    target.textContent = "loading…";

    try {
      const response = await fetch(`/api/schedules/${schedule.id}/runs?limit=25`);
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const runs = await response.json();

      target.innerHTML = runs.length === 0
        ? "no runs recorded yet"
        : runs.map(run =>
          `<div>${escapeHtml(formatLocal(run.firedAtUtc))} &middot; ${escapeHtml(run.status)}${run.detail ? ` &middot; ${escapeHtml(run.detail)}` : ""}</div>`
        ).join("");
    } catch {
      target.textContent = "could not load the run history";
    }
  }

  async function remove(schedule) {
    if (!confirm(`Remove schedule "${schedule.name}"? Its run history goes with it.`)) return;

    try {
      await fetch(`/api/schedules/${schedule.id}`, { method: "DELETE" });
      await load();
    } catch (err) {
      console.error("Failed to delete schedule", err);
    }
  }

  // ---------------- Form ----------------

  async function openForm(schedule) {
    // Devices and the location flag are read fresh each time the form opens, since both are
    // edited on other tabs.
    await refreshMetadata();

    editingId = schedule ? schedule.id : null;
    const wrap = el("schedule-form-wrap");
    const fields = el("schedule-form-fields");
    el("schedule-form-errors").innerHTML = "";
    el("schedule-preview").textContent = "";
    fields.innerHTML = "";

    fields.appendChild(buildTextField("sch-name", "Name", schedule?.name ?? "",
      "A short label, e.g. 'Close the coop at dusk'."));
    fields.appendChild(buildCheckboxField("sch-enabled", "Enabled", schedule?.enabled ?? true,
      "Turn this schedule off without deleting it."));

    fields.appendChild(buildKindField(schedule?.kind ?? "daily"));
    fields.appendChild(buildDailyFields(schedule));
    fields.appendChild(buildIntervalFields(schedule));
    fields.appendChild(buildCronFields(schedule));
    fields.appendChild(buildOnceFields(schedule));
    fields.appendChild(buildSolarFields(schedule));
    fields.appendChild(buildDaysField(schedule?.daysOfWeek ?? []));

    fields.appendChild(buildActionTypeField(schedule?.action?.type ?? "device"));
    fields.appendChild(buildDeviceFields(schedule));
    fields.appendChild(buildMqttFields(schedule));
    fields.appendChild(buildWebhookFields(schedule));

    fields.appendChild(buildCatchUpField(schedule?.catchUpPolicy ?? "skip"));
    fields.appendChild(buildTokenLegend());

    applyKindVisibility(el("sch-kind").value);
    applyActionTypeVisibility(el("sch-action-type").value);

    // Anything that changes the timing re-runs the preview, which is the only way to see what a
    // cron expression or a darkness level actually means before saving it.
    for (const id of ["sch-kind", "sch-time", "sch-interval", "sch-interval-unit", "sch-window-start",
      "sch-window-end", "sch-cron", "sch-run-at", "sch-solar-event", "sch-darkness", "sch-darkness-custom",
      "sch-solar-offset"]) {
      const input = el(id);
      if (input) input.addEventListener("input", schedulePreview);
    }
    for (const input of document.querySelectorAll('input[data-role="sch-day"]'))
      input.addEventListener("change", schedulePreview);

    wrap.classList.remove("hidden");
    wrap.scrollIntoView({ behavior: "smooth", block: "nearest" });
    schedulePreview();
  }

  function closeForm() {
    editingId = null;
    el("schedule-form-wrap").classList.add("hidden");
  }

  function fieldRow(tip, wide) {
    const row = document.createElement("div");
    row.className = "field-row" + (wide ? " field-row-wide" : "");
    if (tip) row.dataset.tip = tip;
    return row;
  }

  function buildTextField(id, label, value, tip) {
    const row = fieldRow(tip, true);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "text";
    input.className = "field-input";
    input.id = id;
    input.value = value ?? "";
    row.appendChild(input);
    return row;
  }

  function buildNumberField(id, label, value, min, max, step, tip) {
    const row = fieldRow(tip, false);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "number";
    input.className = "field-input";
    input.id = id;
    input.min = min;
    input.max = max;
    input.step = step;
    input.value = value;
    row.appendChild(input);
    return row;
  }

  function buildCheckboxField(id, label, checked, tip) {
    const row = fieldRow(tip, true);
    row.classList.add("field-checkbox-row");
    const switchLabel = document.createElement("label");
    switchLabel.className = "switch";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.id = id;
    input.checked = checked;
    const track = document.createElement("span");
    track.className = "switch-track";
    switchLabel.append(input, track);
    const text = document.createElement("span");
    text.className = "field-label";
    text.textContent = label;
    row.append(switchLabel, text);
    return row;
  }

  function buildTextareaField(id, label, value, tip, rows) {
    const row = fieldRow(tip, true);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const textarea = document.createElement("textarea");
    textarea.className = "field-textarea";
    textarea.id = id;
    textarea.rows = rows || 3;
    textarea.value = value ?? "";
    row.appendChild(textarea);
    return row;
  }

  function buildSelectField(id, label, options, value, tip) {
    const row = fieldRow(tip, true);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const select = document.createElement("select");
    select.className = "field-input";
    select.id = id;
    select.innerHTML = options
      .map(o => `<option value="${escapeHtml(o.value)}">${escapeHtml(o.label)}</option>`).join("");
    select.value = value;
    row.appendChild(select);
    return row;
  }

  function group(id, children) {
    const wrap = document.createElement("div");
    wrap.id = id;
    wrap.style.display = "contents";
    for (const child of children) wrap.appendChild(child);
    return wrap;
  }

  function buildKindField(value) {
    const row = buildSelectField("sch-kind", "When", [
      { value: "daily", label: "At a time of day" },
      { value: "solar", label: "At sunset / sunrise, or when it gets dark" },
      { value: "interval", label: "Every so often" },
      { value: "once", label: "Once, at a specific date and time" },
      { value: "cron", label: "On a cron expression (advanced)" }
    ], value, "How this schedule decides when to fire.");
    row.querySelector("select").addEventListener("change", e => {
      applyKindVisibility(e.target.value);
      schedulePreview();
    });
    return row;
  }

  function buildDailyFields(schedule) {
    const row = fieldRow("The wall-clock time to fire at, in 24-hour form. It stays this time on both sides of a daylight-saving change.", true);
    row.innerHTML = `<label class="field-label" for="sch-time">Time</label>`;
    const input = document.createElement("input");
    input.type = "time";
    input.className = "field-input";
    input.id = "sch-time";
    input.value = schedule?.timeOfDay || "18:00";
    row.appendChild(input);
    return group("sch-daily-fields", [row]);
  }

  function buildIntervalFields(schedule) {
    const seconds = schedule?.intervalSeconds ?? 900;
    const useHours = seconds % 3600 === 0 && seconds >= 3600;
    const value = useHours ? seconds / 3600 : Math.max(1, Math.round(seconds / 60));

    const amount = buildNumberField("sch-interval", "Every", value, 1, 1000, 1,
      "How often to fire. Without a time window below, firings line up with the clock - every 15 minutes means :00, :15, :30, :45.");
    const unit = buildSelectField("sch-interval-unit", "Unit", [
      { value: "minutes", label: "minutes" },
      { value: "hours", label: "hours" }
    ], useHours ? "hours" : "minutes", "Whether the number above is minutes or hours.");

    const start = fieldRow("Optional. Confines the firings to part of the day, so 'every 15 minutes' doesn't also mean every 15 minutes at 3am. Leave both empty to run all day.", false);
    start.innerHTML = `<label class="field-label" for="sch-window-start">Only between</label>`;
    const startInput = document.createElement("input");
    startInput.type = "time";
    startInput.className = "field-input";
    startInput.id = "sch-window-start";
    startInput.value = schedule?.windowStart || "";
    start.appendChild(startInput);

    const end = fieldRow("The other end of the window. A start later than the end means a window that crosses midnight, e.g. 22:00 to 06:00.", false);
    end.innerHTML = `<label class="field-label" for="sch-window-end">and</label>`;
    const endInput = document.createElement("input");
    endInput.type = "time";
    endInput.className = "field-input";
    endInput.id = "sch-window-end";
    endInput.value = schedule?.windowEnd || "";
    end.appendChild(endInput);

    return group("sch-interval-fields", [amount, unit, start, end]);
  }

  function buildCronFields(schedule) {
    const row = buildTextField("sch-cron", "Cron expression", schedule?.cronExpression ?? "0 6 * * 1-5",
      "Five fields: minute hour day-of-month month day-of-week. '0 6 * * 1-5' is 6am on weekdays. A leading sixth field adds seconds.");
    return group("sch-cron-fields", [row]);
  }

  function buildOnceFields(schedule) {
    const row = fieldRow("The single date and time to fire at. The schedule turns itself off afterwards, keeping its history.", true);
    row.innerHTML = `<label class="field-label" for="sch-run-at">Date and time</label>`;
    const input = document.createElement("input");
    input.type = "datetime-local";
    input.className = "field-input";
    input.id = "sch-run-at";
    if (schedule?.runAtUtc) {
      const date = new Date(schedule.runAtUtc);
      // datetime-local wants a local-time string with no zone, so the offset is removed first.
      const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
      input.value = local.toISOString().slice(0, 16);
    }
    row.appendChild(input);
    return group("sch-once-fields", [row]);
  }

  function buildSolarFields(schedule) {
    const event = buildSelectField("sch-solar-event", "Sun position", [
      { value: "dusk", label: "When it gets dark (evening)" },
      { value: "dawn", label: "When it gets light (morning)" },
      { value: "sunset", label: "Sunset" },
      { value: "sunrise", label: "Sunrise" }
    ], schedule?.solarEvent ?? "dusk",
      "Fires relative to where the sun actually is, so the clock time follows the seasons on its own - no adjusting it twice a year.");
    event.querySelector("select").addEventListener("change", () => {
      applySolarVisibility();
      schedulePreview();
    });

    const presets = (metadata.darknessLevels || []).map(level => ({
      value: String(level.elevationDegrees),
      label: `${level.label} (${level.elevationDegrees}°)`
    }));
    presets.push({ value: "custom", label: "Custom angle…" });

    const current = schedule?.solarElevationDegrees ?? -6;
    const matching = (metadata.darknessLevels || [])
      .find(level => Math.abs(level.elevationDegrees - current) < 0.01);

    const darkness = buildSelectField("sch-darkness", "How dark", presets,
      matching ? String(matching.elevationDegrees) : "custom",
      "How far the sun has to be below the horizon. 'Getting dark' is the usual choice for shutting a coop - the birds have gone in to roost by then.");
    darkness.querySelector("select").addEventListener("change", () => {
      applySolarVisibility();
      schedulePreview();
    });

    const custom = buildNumberField("sch-darkness-custom", "Sun angle (degrees)", current, -18, 10, 0.5,
      "Negative is below the horizon. -0.8 is the sun touching the horizon, -6 is dusk, -18 is fully dark.");
    custom.id = "sch-darkness-custom-row";

    const offset = buildNumberField("sch-solar-offset", "Offset (minutes)", schedule?.solarOffsetMinutes ?? 0, -720, 720, 5,
      "Shifts the firing either side of that moment. Positive is after, negative is before - e.g. 20 to give the birds a few minutes to finish going in.");

    const warning = document.createElement("div");
    warning.id = "sch-solar-warning";
    warning.className = "camera-card-meta";
    warning.style.whiteSpace = "normal";
    warning.textContent = metadata.locationSet
      ? ""
      : "No location is set, so sunrise and sunset can't be worked out. Add your town under Settings → LOCATION first.";

    return group("sch-solar-fields", [event, darkness, custom, offset, warning]);
  }

  function buildDaysField(selected) {
    const row = fieldRow("Which days this fires on. Leave everything unchecked for every day. Ignored for a cron expression, which has its own day-of-week field, and for a one-off.", true);
    row.id = "sch-days-row";
    row.innerHTML = `<label class="field-label">Days (none checked = every day)</label>`;
    const grid = document.createElement("div");
    grid.style.display = "flex";
    grid.style.flexWrap = "wrap";
    grid.style.gap = "4px 16px";

    DAY_NAMES.forEach((name, index) => {
      const label = document.createElement("label");
      label.className = "field-checkbox-row";
      label.style.marginRight = "4px";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = String(index);
      input.checked = selected.includes(index);
      input.dataset.role = "sch-day";
      const text = document.createElement("span");
      text.textContent = name;
      label.append(input, text);
      grid.appendChild(label);
    });

    row.appendChild(grid);
    return row;
  }

  function buildActionTypeField(value) {
    const row = buildSelectField("sch-action-type", "Then", [
      { value: "device", label: "Switch a device from the Device Hub" },
      { value: "mqtt", label: "Publish a custom MQTT message" },
      { value: "webhook", label: "Call an API endpoint (webhook)" }
    ], value, "What this schedule does when it fires.");
    row.querySelector("select").addEventListener("change", e => applyActionTypeVisibility(e.target.value));
    return row;
  }

  function buildDeviceFields(schedule) {
    const devices = metadata.devices || [];
    const options = devices.length === 0
      ? [{ value: "", label: "No devices in the Device Hub yet" }]
      : devices.map(device => ({
        value: device.id,
        label: device.room ? `${device.name} (${device.room})` : device.name
      }));

    const device = buildSelectField("sch-device", "Device", options,
      schedule?.action?.deviceId ?? (devices[0]?.id ?? ""),
      "Which device to switch. Add devices on the Device Hub tab; only ones that can be commanded appear here.");
    device.querySelector("select").addEventListener("change", applyDeviceVisibility);

    const action = buildSelectField("sch-device-action", "Do what",
      (metadata.deviceActions || []).map(a => ({ value: a, label: a })),
      schedule?.action?.deviceAction || "on",
      "What to tell the device. A gate takes open/close/stop, a light takes on/off, a button takes press.");

    const brightness = buildNumberField("sch-device-brightness", "Brightness (%)",
      schedule?.action?.deviceBrightness ?? 100, 0, 100, 1,
      "Only for devices that support dimming. Leave the box unchecked below to send no brightness at all.");
    brightness.id = "sch-device-brightness-row";

    const useBrightness = buildCheckboxField("sch-device-use-brightness", "Also set brightness",
      schedule?.action?.deviceBrightness !== null && schedule?.action?.deviceBrightness !== undefined,
      "Send a brightness level along with the on/off command.");
    useBrightness.id = "sch-device-use-brightness-row";
    useBrightness.querySelector("input").addEventListener("change", applyDeviceVisibility);

    return group("sch-device-fields", [device, action, useBrightness, brightness]);
  }

  function buildMqttFields(schedule) {
    const topic = buildTextField("sch-mqtt-topic", "MQTT topic", schedule?.action?.mqttTopic ?? "",
      "Where to publish, e.g. home/coop/door. Requires the MQTT integration to be turned on under Settings.");
    const payload = buildTextareaField("sch-mqtt-payload", "MQTT payload", schedule?.action?.mqttPayload ?? "CLOSE",
      "The message body. The tokens below are filled in when it fires.", 3);
    const retain = buildCheckboxField("sch-mqtt-retain", "Retain (broker keeps the last message for new subscribers)",
      schedule?.action?.mqttRetain ?? false,
      "Turn on for a status-style topic a new subscriber should see immediately.");
    return group("sch-mqtt-fields", [topic, payload, retain]);
  }

  function buildWebhookFields(schedule) {
    const url = buildTextField("sch-webhook-url", "URL", schedule?.action?.webhookUrl ?? "",
      "The full URL to call, e.g. https://example.com/hooks/coop.");
    const method = buildSelectField("sch-webhook-method", "Method",
      ["POST", "PUT", "PATCH", "GET", "DELETE"].map(m => ({ value: m, label: m })),
      schedule?.action?.webhookMethod ?? "POST",
      "The HTTP method to use. Most APIs expect POST.");
    const contentType = buildTextField("sch-webhook-content-type", "Content-Type",
      schedule?.action?.webhookContentType ?? "application/json",
      "Sent as the request's Content-Type header when there's a body.");
    const headers = buildTextareaField("sch-webhook-headers", "Extra headers (one per line, Name: Value)",
      schedule?.action?.webhookHeaders ?? "",
      "Optional. One header per line, e.g. Authorization: Bearer xyz.", 3);
    const body = buildTextareaField("sch-webhook-body", "Body",
      schedule?.action?.webhookBody ?? '{"schedule":"{{schedule_name}}","at":"{{now}}"}',
      "Sent as the request body (ignored for GET/DELETE).", 3);
    return group("sch-webhook-fields", [url, method, contentType, headers, body]);
  }

  function buildCatchUpField(value) {
    return buildSelectField("sch-catchup", "If the server was off when it was due", [
      { value: "skip", label: "Skip it - record that it was missed" },
      { value: "runOnce", label: "Run it once on the way back up" }
    ], value,
      "Skipping suits anything where being late is pointless, like flashing a light. Running once suits anything where the state it leaves behind is the point, like arming or shutting a door. Either way there's a limit on how late is too late, set under Settings.");
  }

  function buildTokenLegend() {
    const row = fieldRow(null, true);
    const label = document.createElement("div");
    label.className = "field-label";
    label.textContent = "Available tokens (use as {{token}} in the MQTT and webhook fields):";
    const list = document.createElement("div");
    list.className = "camera-card-meta";
    list.style.whiteSpace = "normal";
    list.textContent = (metadata.tokens || []).map(t => `{{${t}}}`).join("  ");
    row.append(label, list);
    return row;
  }

  function applyKindVisibility(kind) {
    const show = (id, visible) => {
      const node = el(id);
      if (node) node.style.display = visible ? "contents" : "none";
    };

    show("sch-daily-fields", kind === "daily");
    show("sch-interval-fields", kind === "interval");
    show("sch-cron-fields", kind === "cron");
    show("sch-once-fields", kind === "once");
    show("sch-solar-fields", kind === "solar");

    // Days-of-week only applies where it isn't already covered by the timing itself.
    const daysRow = el("sch-days-row");
    if (daysRow) daysRow.style.display = kind === "daily" || kind === "solar" ? "" : "none";

    if (kind === "solar") applySolarVisibility();
  }

  function applySolarVisibility() {
    const event = el("sch-solar-event")?.value;
    const usesElevation = event === "dusk" || event === "dawn";

    const darknessRow = el("sch-darkness")?.closest(".field-row");
    if (darknessRow) darknessRow.style.display = usesElevation ? "" : "none";

    const customRow = el("sch-darkness-custom-row");
    if (customRow)
      customRow.style.display = usesElevation && el("sch-darkness")?.value === "custom" ? "" : "none";
  }

  function applyActionTypeVisibility(actionType) {
    const show = (id, visible) => {
      const node = el(id);
      if (node) node.style.display = visible ? "contents" : "none";
    };

    show("sch-device-fields", actionType === "device");
    show("sch-mqtt-fields", actionType === "mqtt");
    show("sch-webhook-fields", actionType === "webhook");

    if (actionType === "device") applyDeviceVisibility();
  }

  function applyDeviceVisibility() {
    const deviceId = el("sch-device")?.value;
    const device = (metadata.devices || []).find(d => d.id === deviceId);
    const supportsBrightness = !!device?.supportsBrightness;

    // Hidden rather than disabled for devices that can't dim: the field would only ever produce
    // a save error.
    const useRow = el("sch-device-use-brightness-row");
    if (useRow) useRow.style.display = supportsBrightness ? "" : "none";

    const brightnessRow = el("sch-device-brightness-row");
    if (brightnessRow)
      brightnessRow.style.display =
        supportsBrightness && el("sch-device-use-brightness")?.checked ? "" : "none";
  }

  // ---------------- Preview ----------------

  function schedulePreview() {
    // Debounced: this fires on every keystroke in the cron box, and each one is a round trip.
    if (previewTimer) clearTimeout(previewTimer);
    previewTimer = setTimeout(runPreview, 250);
  }

  async function runPreview() {
    const target = el("schedule-preview");
    if (!target) return;

    try {
      const response = await fetch("/api/schedules/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(collectBody())
      });
      const payload = await response.json().catch(() => ({}));

      if (!response.ok) {
        target.textContent = (payload.errors || []).join(" ") || "";
        return;
      }

      if (!payload.occurrences || payload.occurrences.length === 0) {
        target.textContent = "This never fires as configured.";
        return;
      }

      const times = payload.occurrences.map(o => o.local).join("  •  ");
      target.textContent = `${payload.explanation}  Next: ${times}`;
    } catch {
      target.textContent = "";
    }
  }

  function collectBody() {
    const kind = el("sch-kind").value;
    const days = [...document.querySelectorAll('input[data-role="sch-day"]:checked')].map(i => Number(i.value));

    const unit = el("sch-interval-unit")?.value === "hours" ? 3600 : 60;
    const intervalSeconds = Number(el("sch-interval")?.value || 15) * unit;

    const darknessValue = el("sch-darkness")?.value;
    const solarElevationDegrees = darknessValue === "custom"
      ? Number(el("sch-darkness-custom")?.value ?? -6)
      : Number(darknessValue ?? -6);

    const runAtLocal = el("sch-run-at")?.value;

    const actionType = el("sch-action-type").value;
    const useBrightness = el("sch-device-use-brightness")?.checked;

    return {
      name: el("sch-name").value.trim(),
      enabled: el("sch-enabled").checked,
      kind,
      timeOfDay: el("sch-time")?.value || "",
      // Cron carries its own day-of-week field and a one-off is a single instant, so sending
      // days for either would be silently ignored at best and confusing at worst.
      daysOfWeek: kind === "daily" || kind === "solar" ? days : [],
      intervalSeconds,
      windowStart: el("sch-window-start")?.value || "",
      windowEnd: el("sch-window-end")?.value || "",
      cronExpression: el("sch-cron")?.value.trim() || "",
      // datetime-local has no zone; new Date() reads it as local time, which is what was meant.
      runAtUtc: runAtLocal ? new Date(runAtLocal).toISOString() : null,
      solarEvent: el("sch-solar-event")?.value || "dusk",
      solarOffsetMinutes: Number(el("sch-solar-offset")?.value ?? 0),
      solarElevationDegrees,
      actionType,
      deviceId: el("sch-device")?.value || null,
      deviceAction: el("sch-device-action")?.value || "",
      deviceBrightness: actionType === "device" && useBrightness
        ? Number(el("sch-device-brightness")?.value ?? 100)
        : null,
      mqttTopic: el("sch-mqtt-topic")?.value.trim() || "",
      mqttPayload: el("sch-mqtt-payload")?.value ?? "",
      mqttRetain: el("sch-mqtt-retain")?.checked ?? false,
      webhookUrl: el("sch-webhook-url")?.value.trim() || "",
      webhookMethod: el("sch-webhook-method")?.value || "POST",
      webhookHeaders: el("sch-webhook-headers")?.value ?? "",
      webhookBody: el("sch-webhook-body")?.value ?? "",
      webhookContentType: el("sch-webhook-content-type")?.value.trim() || "application/json",
      catchUpPolicy: el("sch-catchup").value
    };
  }

  async function save() {
    const errorsEl = el("schedule-form-errors");
    errorsEl.innerHTML = "";

    const endpoint = editingId ? `/api/schedules/${editingId}` : "/api/schedules";
    const method = editingId ? "PUT" : "POST";

    try {
      const response = await fetch(endpoint, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(collectBody())
      });
      const payload = await response.json().catch(() => ({}));

      if (!response.ok) {
        for (const message of payload.errors || ["Save failed."]) {
          const li = document.createElement("li");
          li.textContent = message;
          errorsEl.appendChild(li);
        }
        return;
      }

      closeForm();
      await load();
    } catch {
      const li = document.createElement("li");
      li.textContent = "Network error while saving.";
      errorsEl.appendChild(li);
    }
  }

  // ---------------- Sub-tabs ----------------

  function selectPane(pane) {
    for (const button of document.querySelectorAll("#automation-subtabs .tab"))
      button.classList.toggle("active", button.dataset.pane === pane);

    el("automations-pane-rules").classList.toggle("hidden", pane !== "rules");
    el("automations-pane-schedules").classList.toggle("hidden", pane !== "schedules");

    if (pane === "schedules") load();
    else window.GateWatchAutomations.load();
  }

  // Called by app.js when the Automations tab is opened, so whichever pane is showing is the
  // one that refreshes.
  function loadActivePane() {
    const active = document.querySelector("#automation-subtabs .tab.active");
    selectPane(active?.dataset.pane ?? "rules");
  }

  for (const button of document.querySelectorAll("#automation-subtabs .tab"))
    button.addEventListener("click", () => selectPane(button.dataset.pane));

  el("add-schedule-btn").addEventListener("click", () => openForm(null));
  el("schedule-cancel-btn").addEventListener("click", closeForm);
  el("schedule-save-btn").addEventListener("click", save);

  return { load, renderList, loadActivePane };
})();
