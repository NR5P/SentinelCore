// Kiosk Home.
//
// The wall kiosk's start page: what someone walking past wants to know without touching anything
// (the time, the weather, what's on today, whether the alarm is set, what the cameras see), and
// the few things they might tap (a light, a plug, the gate). Everything else is a tab away. Each
// card opens the tab that has the rest of it. Only on screen in kiosk mode - the tab is hidden
// everywhere else - and it only polls while it is the open tab.
window.SentinelKioskHome = (function () {
  const WEATHER_MS = 10 * 60 * 1000;
  const ALARM_MS = 15000;
  const DEVICES_MS = 10000;
  const THUMBS_MS = 30000;
  const CLOCK_MS = 5000;

  let visible = false;
  let built = false;
  let timers = [];
  let weather = null;
  let alarm = null;
  let devices = [];
  let cameras = [];
  let unsubscribeOrganizer = null;

  const root = () => document.getElementById("home-root");
  const $ = (id) => document.getElementById(id);

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function openTab(view) {
    document.querySelector(`#tabs .tab[data-view="${view}"]`)?.click();
  }

  function build() {
    root().innerHTML = `
      <div class="kh-grid">
        <button type="button" class="kh-card kh-now" data-open="weather">
          <div class="kh-clock" id="kh-clock"></div>
          <div class="kh-date" id="kh-date"></div>
          <div class="kh-weather" id="kh-weather"></div>
        </button>
        <button type="button" class="kh-card kh-today" data-open="organizer">
          <div class="kh-card-title">today</div>
          <div class="kh-today-list" id="kh-today"></div>
        </button>
        <button type="button" class="kh-card kh-alarm" id="kh-alarm" data-open="alarm"></button>
      </div>
      <div class="kh-section-title">cameras</div>
      <div class="kh-cams" id="kh-cams"></div>
      <div class="kh-section-title" id="kh-devices-title">switches</div>
      <div class="kh-devices" id="kh-devices"></div>`;
    root().addEventListener("click", (e) => {
      const device = e.target.closest("[data-device]");
      if (device) return pressDevice(device.dataset.device);
      const cam = e.target.closest("[data-camera]");
      if (cam) return openTab("live");
      const card = e.target.closest("[data-open]");
      if (card) openTab(card.dataset.open);
    });
    built = true;
  }

  // ---- clock + weather ------------------------------------------------------------------------

  function renderClock() {
    const now = new Date();
    $("kh-clock").textContent = now.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    $("kh-date").textContent = now.toLocaleDateString([], { weekday: "long", month: "long", day: "numeric" });
  }

  async function loadWeather() {
    try {
      const response = await fetch("/api/weather");
      if (response.ok) weather = await response.json();
    } catch { /* keep the last reading */ }
    renderWeather();
  }

  function renderWeather() {
    const el = $("kh-weather");
    if (!el) return;
    if (!weather || weather.state !== "ok" || !weather.current) {
      el.innerHTML = `<div class="kh-muted">${escapeHtml(weather?.message || "weather unavailable")}</div>`;
      return;
    }
    const current = weather.current;
    const periods = weather.daily || [];
    const lead = periods[0] || {};
    const glyph = window.SentinelWeather?.glyph
      ? window.SentinelWeather.glyph(current.icon || lead.icon, current.isDaytime ?? lead.isDaytime)
      : "";
    const high = periods.find((p) => p.isDaytime);
    const low = periods.find((p) => p.isDaytime === false);
    const rain = Math.max(0, ...periods.slice(0, 2).map((p) => p.precipitationChance || 0));
    el.innerHTML = `
      <div class="kh-temp"><span class="kh-glyph" aria-hidden="true">${glyph}</span>${current.temperatureF ?? "–"}°</div>
      <div class="kh-forecast">${escapeHtml(lead.shortForecast || current.description || "")}</div>
      <div class="kh-muted">${high ? `H ${high.temperatureF}°` : ""}${high && low ? " · " : ""}${low ? `L ${low.temperatureF}°` : ""}${rain ? ` · ${rain}% rain` : ""}</div>`;
  }

  // ---- today ----------------------------------------------------------------------------------

  function renderToday() {
    const el = $("kh-today");
    const S = window.SentinelOrganizerSync;
    const O = window.SentinelOrganizer;
    if (!el || !S || !O) return;
    const start = new Date();
    start.setHours(0, 0, 0, 0);
    const end = new Date(start.getTime() + 86400000);
    const now = new Date();
    const time = (d) => d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });

    const rows = [];
    for (const event of S.all("event")) {
      for (const occurrence of O.occurrences(event, start, end)) {
        // A finished timed event drops off; all-day ones stay for the day.
        if (!event.fields.allDay && occurrence.end < now) continue;
        rows.push({ at: event.fields.allDay ? start : occurrence.start, when: event.fields.allDay ? "all day" : time(occurrence.start), title: event.fields.title || "event", kind: "event" });
      }
    }
    for (const reminder of S.all("reminder")) {
      const f = reminder.fields;
      if (f.completed || !f.due || reminder.parentId) continue;
      const due = f.allDay ? new Date(`${f.due}T00:00:00`) : new Date(f.due);
      if (isNaN(due) || due >= end) continue;
      const overdue = due < start;
      rows.push({ at: due, when: overdue ? "overdue" : f.allDay ? "today" : time(due), title: f.title || "reminder", kind: overdue ? "overdue" : "reminder" });
    }
    for (const holiday of S.holidays ? S.holidays() : []) {
      const day = new Date(`${holiday.date}T00:00:00`);
      const last = new Date(day.getTime() + (holiday.days || 1) * 86400000);
      if (day < end && last > start) rows.push({ at: start, when: "holiday", title: holiday.name || "holiday", kind: "holiday" });
    }
    rows.sort((a, b) => a.at - b.at);

    if (!rows.length) {
      el.innerHTML = `<div class="kh-empty">nothing on today</div>`;
      return;
    }
    const shown = rows.slice(0, 6);
    el.innerHTML = shown.map((r) => `
      <div class="kh-row kh-row-${r.kind}">
        <span class="kh-row-when">${escapeHtml(r.when)}</span>
        <span class="kh-row-title">${escapeHtml(r.title)}</span>
      </div>`).join("") + (rows.length > shown.length ? `<div class="kh-muted">+${rows.length - shown.length} more</div>` : "");
  }

  // ---- alarm ----------------------------------------------------------------------------------

  async function loadAlarm() {
    try {
      const response = await fetch("/api/alarm/status");
      alarm = response.ok ? await response.json() : null;
    } catch { /* keep the last state */ }
    renderAlarm();
  }

  function renderAlarm() {
    const el = $("kh-alarm");
    if (!el) return;
    const tabShown = !document.getElementById("tab-alarm")?.classList.contains("hidden");
    if (!alarm || !alarm.enabled || !tabShown) {
      el.classList.add("hidden");
      return;
    }
    el.classList.remove("hidden");
    const status = alarm.status || alarm.mode || "unknown";
    const label = status === "disarmed" ? "disarmed"
      : status === "triggered" ? "ALARM"
      : status === "pending" ? "entry delay"
      : status === "arming" ? `arming ${alarm.mode}`
      : `armed ${alarm.mode}`;
    const detail = status === "disarmed"
      // The alarm tab's wording points at things "below" it, which aren't on this page.
      ? (alarm.ready ? "ready to arm" : String((alarm.issues && alarm.issues[0]) || "not ready").replace(/ below$/, ""))
      : alarm.changedBy ? `by ${alarm.changedBy}` : "";
    el.className = `kh-card kh-alarm kh-alarm-${status}`;
    el.innerHTML = `
      <div class="kh-card-title">alarm</div>
      <div class="kh-alarm-state">${escapeHtml(label)}</div>
      <div class="kh-muted">${escapeHtml(detail)}</div>
      <div class="kh-alarm-hint">tap to arm or disarm</div>`;
  }

  // ---- cameras --------------------------------------------------------------------------------

  async function loadCameras() {
    try {
      const response = await fetch("/api/cameras");
      // Webcam devices only have a picture while a phone or laptop is streaming, so they'd mostly
      // be blank squares here.
      if (response.ok) cameras = (await response.json()).filter((c) => !c.isBrowserPublished);
    } catch { /* keep the last list */ }
    renderCameras();
  }

  function renderCameras() {
    const el = $("kh-cams");
    if (!el) return;
    const stamp = Math.floor(Date.now() / THUMBS_MS);
    el.classList.toggle("hidden", !cameras.length);
    el.previousElementSibling.classList.toggle("hidden", !cameras.length);
    el.innerHTML = cameras.map((c) => `
      <button type="button" class="kh-cam" data-camera="${escapeHtml(c.id)}">
        <img src="/api/cameras/${encodeURIComponent(c.id)}/thumbnail.jpg?t=${stamp}" alt="" loading="lazy" onerror="this.style.visibility='hidden'" />
        <span class="kh-cam-name">${escapeHtml(c.name)}</span>
      </button>`).join("");
  }

  function refreshThumbnails() {
    const stamp = Math.floor(Date.now() / THUMBS_MS);
    for (const img of document.querySelectorAll("#kh-cams img")) {
      img.style.visibility = "";
      img.src = img.src.replace(/t=\d+/, `t=${stamp}`);
    }
  }

  // ---- devices --------------------------------------------------------------------------------

  const isMomentary = (d) => !!d.momentary || d.kind === "button";
  const isSimpleSwitch = (d) => d.commandable && !isMomentary(d) && !["cover", "gate", "garage", "lock"].includes(d.kind);

  async function loadDevices() {
    try {
      const response = await fetch("/api/devices");
      if (response.ok) devices = (await response.json()).filter((d) => d.enabled && d.commandable);
    } catch { /* keep the last list */ }
    renderDevices();
  }

  async function refreshDeviceState() {
    try {
      const response = await fetch("/api/devices/state");
      if (!response.ok) return;
      const state = await response.json();
      for (const s of state.devices || []) {
        const device = devices.find((d) => d.id === s.id);
        if (device) Object.assign(device, { power: s.power, available: s.available });
      }
      renderDevices();
    } catch { /* next poll */ }
  }

  function renderDevices() {
    const el = $("kh-devices");
    if (!el) return;
    const tabShown = !document.getElementById("tab-devices")?.classList.contains("hidden");
    const show = tabShown && devices.length > 0;
    el.classList.toggle("hidden", !show);
    $("kh-devices-title").classList.toggle("hidden", !show);
    if (!show) return;
    el.innerHTML = devices.map((d) => {
      const on = d.power === "on";
      const state = !d.available ? "offline"
        : isMomentary(d) ? "press"
        : isSimpleSwitch(d) ? (on ? "on" : d.power === "off" ? "off" : "–")
        : d.power && d.power !== "unknown" ? d.power : "open";
      return `
        <button type="button" class="kh-device${on ? " kh-device-on" : ""}${d.available ? "" : " kh-device-offline"}" data-device="${escapeHtml(d.id)}">
          <span class="kh-device-name">${escapeHtml(d.name)}</span>
          <span class="kh-device-state">${escapeHtml(state)}</span>
        </button>`;
    }).join("");
  }

  async function pressDevice(id) {
    const device = devices.find((d) => d.id === id);
    if (!device) return;
    // Covers, gates and locks have more than one thing to say; the Device Hub has the buttons.
    if (!isMomentary(device) && !isSimpleSwitch(device)) return openTab("devices");
    const button = root().querySelector(`[data-device="${CSS.escape(id)}"]`);
    button?.classList.add("kh-device-busy");
    try {
      await fetch(`/api/devices/${encodeURIComponent(id)}/command`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action: isMomentary(device) ? "press" : "toggle" })
      });
    } catch { /* the next state poll shows what really happened */ }
    setTimeout(refreshDeviceState, 600);
    setTimeout(() => button?.classList.remove("kh-device-busy"), 600);
  }

  // ---- lifecycle ------------------------------------------------------------------------------

  function every(ms, fn) {
    timers.push(setInterval(fn, ms));
  }

  function show() {
    if (!built) build();
    visible = true;
    renderClock();
    loadWeather();
    loadAlarm();
    loadCameras();
    loadDevices();
    const S = window.SentinelOrganizerSync;
    if (S) {
      S.start().catch(() => {});
      if (!unsubscribeOrganizer && S.subscribe) unsubscribeOrganizer = S.subscribe(() => { if (visible) renderToday(); });
    }
    renderToday();
    every(CLOCK_MS, () => {
      renderClock();
      // Crossing midnight (or an event ending) changes what "today" holds.
      renderToday();
    });
    every(WEATHER_MS, loadWeather);
    every(ALARM_MS, loadAlarm);
    every(DEVICES_MS, refreshDeviceState);
    every(THUMBS_MS, refreshThumbnails);
  }

  function hide() {
    visible = false;
    for (const t of timers) clearInterval(t);
    timers = [];
  }

  return { show, hide };
})();
