// The Weather tab: current conditions, radar, the hourly strip and the seven-day forecast for the
// location in Settings, all from /api/weather (the server talks to the National Weather Service
// and caches the answer - see WeatherService.cs). Nothing here reaches weather.gov directly: the
// radar picture is proxied by the server too, so the browser only ever talks to one origin.
//
// Fetches only while the tab is open. show() loads and starts the timers, hide() stops them, so
// a dashboard left on the events tab never asks for the weather at all.
window.SentinelWeather = (function () {
  const REFRESH_INTERVAL_MS = 10 * 60_000;
  const RADAR_REFRESH_INTERVAL_MS = 2 * 60_000;

  const root = document.getElementById("weather-root");
  const updatedEl = document.getElementById("weather-updated");
  const refreshBtn = document.getElementById("weather-refresh-btn");

  let report = null;
  let loading = false;
  let refreshTimer = null;
  let radarTimer = null;
  let radarScope = "local";
  let radarKind = "loop";
  // Which alerts and which days are expanded, by id, so a ten-minute refresh doesn't fold up
  // the warning somebody is in the middle of reading.
  const openAlerts = new Set();
  const openDays = new Set();

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // NWS icon codes (api.weather.gov/icons) to a glyph, [day, night]. The server sends the code
  // rather than the NWS picture so nothing on this page loads from outside the server. Anything
  // unrecognised gets a thermometer rather than a broken image.
  const GLYPHS = {
    skc: ["☀️", "🌙"],
    few: ["🌤️", "🌙"],
    sct: ["⛅", "☁️"],
    bkn: ["🌥️", "☁️"],
    ovc: ["☁️", "☁️"],
    wind_skc: ["💨", "💨"],
    wind_few: ["💨", "💨"],
    wind_sct: ["💨", "💨"],
    wind_bkn: ["💨", "💨"],
    wind_ovc: ["💨", "💨"],
    snow: ["🌨️", "🌨️"],
    rain_snow: ["🌨️", "🌨️"],
    rain_sleet: ["🌨️", "🌨️"],
    snow_sleet: ["🌨️", "🌨️"],
    fzra: ["🧊", "🧊"],
    rain_fzra: ["🧊", "🧊"],
    snow_fzra: ["🧊", "🧊"],
    sleet: ["🌨️", "🌨️"],
    rain: ["🌧️", "🌧️"],
    rain_showers: ["🌦️", "🌧️"],
    rain_showers_hi: ["🌦️", "🌧️"],
    tsra: ["⛈️", "⛈️"],
    tsra_sct: ["⛈️", "⛈️"],
    tsra_hi: ["🌩️", "🌩️"],
    tornado: ["🌪️", "🌪️"],
    hurricane: ["🌀", "🌀"],
    tropical_storm: ["🌀", "🌀"],
    dust: ["🌫️", "🌫️"],
    smoke: ["🌫️", "🌫️"],
    haze: ["🌫️", "🌫️"],
    fog: ["🌫️", "🌫️"],
    hot: ["🥵", "🥵"],
    cold: ["🥶", "🥶"],
    blizzard: ["🌨️", "🌨️"]
  };

  function glyph(code, isDaytime) {
    const pair = GLYPHS[code];
    if (!pair) return "🌡️";
    return isDaytime === false ? pair[1] : pair[0];
  }

  // Times are shown in the location's own time zone (the NWS reports it with the forecast), so
  // the forecast for the house reads right even from a hotel two zones away.
  function formatTime(iso, options) {
    const date = new Date(iso);
    const timeZone = report?.location?.timeZone || undefined;
    try {
      return new Intl.DateTimeFormat(undefined, { timeZone, ...options }).format(date);
    } catch {
      return new Intl.DateTimeFormat(undefined, options).format(date);
    }
  }

  const CLOCK = { hour: "numeric", minute: "2-digit" };
  const HOUR = { hour: "numeric" };
  const WEEKDAY = { weekday: "short" };
  const WEEKDAY_CLOCK = { weekday: "short", hour: "numeric", minute: "2-digit" };

  // A key that groups periods by the local calendar day they start on.
  function localDateKey(iso) {
    return formatTime(iso, { year: "numeric", month: "2-digit", day: "2-digit" });
  }

  function radarUrl() {
    return `/api/weather/radar?scope=${radarScope}&kind=${radarKind}&t=${Date.now()}`;
  }

  // ---------------- Rendering ----------------

  function placeHtml() {
    const location = report.location;
    if (!location) return "";
    const nearest = [location.city, location.state].filter(Boolean).join(", ");
    const name = location.label || nearest || "this location";
    const parts = [];
    if (location.label && nearest && nearest.toLowerCase() !== location.label.toLowerCase()) parts.push(`near ${nearest}`);
    parts.push(`${location.latitude.toFixed(4)}, ${location.longitude.toFixed(4)}`);
    if (location.forecastOffice) parts.push(`forecast office ${location.forecastOffice}`);
    return `
      <div class="wx-place">
        <span class="wx-place-name">${escapeHtml(name.toUpperCase())}</span>
        <span class="wx-place-meta">${escapeHtml(parts.join(" · "))}</span>
      </div>`;
  }

  function problemsHtml() {
    if (!report.problems?.length) return "";
    return `<div class="wx-problems">${report.problems.map(p => `<div>⚠ ${escapeHtml(p)}</div>`).join("")}</div>`;
  }

  function severityClass(severity) {
    if (severity === "Extreme" || severity === "Severe") return "severe";
    if (severity === "Moderate") return "moderate";
    return "minor";
  }

  function alertsHtml() {
    if (!report.alerts?.length) return "";
    const items = report.alerts.map(alert => {
      const until = alert.ends ? `until ${formatTime(alert.ends, WEEKDAY_CLOCK)}` : "";
      const instruction = alert.instruction
        ? `<pre class="wx-alert-text wx-alert-instruction">${escapeHtml(alert.instruction)}</pre>`
        : "";
      const meta = [alert.areaDescription, alert.sender].filter(Boolean).map(escapeHtml).join(" · ");
      return `
        <div class="wx-alert wx-alert-${severityClass(alert.severity)}${openAlerts.has(alert.id) ? " open" : ""}" data-alert-id="${escapeHtml(alert.id)}">
          <button type="button" class="wx-alert-head" data-tip-below data-tip="Click for the full text of this ${escapeHtml(alert.severity.toLowerCase())} ${escapeHtml(alert.urgency.toLowerCase())}-urgency alert.">
            <span class="wx-alert-event">${escapeHtml(alert.event)}</span>
            <span class="wx-alert-when">${escapeHtml(until)}</span>
            <span class="wx-alert-caret">▸</span>
          </button>
          ${alert.headline ? `<div class="wx-alert-headline">${escapeHtml(alert.headline)}</div>` : ""}
          <div class="wx-alert-body">
            <pre class="wx-alert-text">${escapeHtml(alert.description)}</pre>
            ${instruction}
            ${meta ? `<div class="wx-alert-meta">${meta}</div>` : ""}
          </div>
        </div>`;
    });
    return `<div class="wx-alerts">${items.join("")}</div>`;
  }

  function windText(current) {
    if (current.windMph == null) return null;
    if (current.windMph === 0) return "calm";
    return `${current.windDirection ? current.windDirection + " " : ""}${current.windMph} mph`;
  }

  function nowHtml() {
    const current = report.current;
    const sun = report.sun;
    if (!current) {
      return `
        <div class="wx-panel wx-now">
          <div class="wx-panel-title">NOW</div>
          <div class="empty-state">no current conditions</div>
        </div>`;
    }

    const details = [
      ["humidity", current.humidityPercent != null ? `${current.humidityPercent}%` : null],
      ["dew point", current.dewpointF != null ? `${current.dewpointF}°` : null],
      ["wind", windText(current)],
      ["gusts", current.windGustMph != null ? `${current.windGustMph} mph` : null],
      ["pressure", current.pressureInHg != null ? `${current.pressureInHg.toFixed(2)} inHg` : null],
      ["visibility", current.visibilityMiles != null ? `${current.visibilityMiles} mi` : null],
      ["sunrise", sun?.sunrise ? formatTime(sun.sunrise, CLOCK) : null],
      ["sunset", sun?.sunset ? formatTime(sun.sunset, CLOCK) : null]
    ].filter(([, value]) => value != null);

    const feels = current.feelsLikeF != null && current.feelsLikeF !== current.temperatureF
      ? `<div class="wx-now-feels">feels like ${current.feelsLikeF}°</div>`
      : "";

    const observed = current.observedAt ? ` · observed ${formatTime(current.observedAt, WEEKDAY_CLOCK)}` : "";

    return `
      <div class="wx-panel wx-now">
        <div class="wx-panel-title">NOW</div>
        <div class="wx-now-head">
          <span class="wx-now-glyph" aria-hidden="true">${glyph(current.icon, current.isDaytime)}</span>
          <div>
            <div class="wx-now-temp">${current.temperatureF}°F</div>
            <div class="wx-now-desc">${escapeHtml(current.description || "—")}</div>
            ${feels}
          </div>
        </div>
        <dl class="wx-details">
          ${details.map(([label, value]) => `<dt>${label}</dt><dd>${escapeHtml(value)}</dd>`).join("")}
        </dl>
        <div class="wx-source" data-tip="Current conditions come from the nearest National Weather Service observation station, which is usually an airport a few miles away - so it can differ a degree or two from the porch.">${escapeHtml(current.station)} · ${escapeHtml(current.stationName)}${escapeHtml(observed)}</div>
      </div>`;
  }

  function radarHtml() {
    const hasLocal = !!report.radar;
    if (!hasLocal && radarScope === "local") radarScope = "national";

    const button = (attr, value, label, active, disabled) =>
      `<button type="button" class="btn btn-small${active ? " btn-on" : ""}" ${attr}="${value}"${disabled ? " disabled" : ""}>${label}</button>`;

    return `
      <div class="wx-panel wx-radar">
        <div class="wx-panel-head">
          <div class="wx-panel-title">RADAR${hasLocal ? ` · ${escapeHtml(report.radar.station)}` : ""}</div>
          <div class="wx-radar-controls">
            ${button("data-radar-scope", "local", "local", radarScope === "local", !hasLocal)}
            ${button("data-radar-scope", "national", "national", radarScope === "national", false)}
            <span class="wx-radar-sep"></span>
            ${button("data-radar-kind", "loop", "loop", radarKind === "loop", false)}
            ${button("data-radar-kind", "still", "still", radarKind === "still", false)}
          </div>
        </div>
        <div class="wx-radar-frame">
          <img class="wx-radar-img" src="${radarUrl()}" alt="National Weather Service radar" data-tip="Click to see the radar full size." />
        </div>
        <div class="wx-radar-note">Base reflectivity from radar.weather.gov, refreshed every two minutes while this tab is open. The loop is the last hour or so.</div>
      </div>`;
  }

  function hourlyHtml() {
    const hours = report.hourly || [];
    if (!hours.length) return "";
    let lastDay = null;
    const cells = hours.map(hour => {
      const day = localDateKey(hour.startTime);
      const newDay = day !== lastDay;
      lastDay = day;
      const chance = hour.precipitationChance != null && hour.precipitationChance > 0
        ? `💧${hour.precipitationChance}%`
        : "&nbsp;";
      const wind = [hour.windDirection, hour.windSpeed].filter(Boolean).join(" ");
      return `
        <div class="wx-hour${newDay ? " wx-hour-newday" : ""}" data-tip-below data-tip="${escapeHtml(hour.shortForecast)}${wind ? ` · wind ${escapeHtml(wind)}` : ""}${hour.humidityPercent != null ? ` · humidity ${hour.humidityPercent}%` : ""}">
          <div class="wx-hour-day">${newDay ? escapeHtml(formatTime(hour.startTime, WEEKDAY)) : ""}</div>
          <div class="wx-hour-time">${escapeHtml(formatTime(hour.startTime, HOUR))}</div>
          <div class="wx-hour-glyph" aria-hidden="true">${glyph(hour.icon, hour.isDaytime)}</div>
          <div class="wx-hour-temp">${hour.temperatureF != null ? `${hour.temperatureF}°` : "—"}</div>
          <div class="wx-hour-pop">${chance}</div>
        </div>`;
    });
    return `
      <div class="wx-section">
        <div class="wx-panel-title">NEXT ${hours.length} HOURS</div>
        <div class="wx-hourly">${cells.join("")}</div>
      </div>`;
  }

  // The NWS forecast is a list of half-days (Today, Tonight, Saturday, Saturday Night, ...). One
  // card per calendar day, with the day period leading and the night giving the low.
  function dailyHtml() {
    const periods = report.daily || [];
    if (!periods.length) return "";

    const days = [];
    for (const period of periods) {
      const key = localDateKey(period.startTime);
      let day = days.find(d => d.key === key);
      if (!day) {
        day = { key, day: null, night: null };
        days.push(day);
      }
      if (period.isDaytime) day.day = period;
      else day.night = period;
    }

    const cards = days.map(entry => {
      const lead = entry.day || entry.night;
      const high = entry.day?.temperatureF;
      const low = entry.night?.temperatureF;
      const chance = Math.max(entry.day?.precipitationChance ?? 0, entry.night?.precipitationChance ?? 0);
      const wind = [lead.windDirection, lead.windSpeed].filter(Boolean).join(" ");
      const meta = [chance > 0 ? `💧 ${chance}%` : null, wind || null].filter(Boolean).join(" · ");
      const temps = [
        high != null ? `<span class="wx-hi">${high}°</span>` : "",
        low != null ? `<span class="wx-lo">${low}°</span>` : ""
      ].filter(Boolean).join(" / ");
      const detail = [entry.day, entry.night].filter(Boolean)
        .map(p => `<p><b>${escapeHtml(p.name)}.</b> ${escapeHtml(p.detailedForecast)}</p>`).join("");

      return `
        <div class="wx-day${openDays.has(entry.key) ? " open" : ""}" data-day="${escapeHtml(entry.key)}" tabindex="0" role="button" data-tip-below data-tip="Click for the full forecast for this day and night.">
          <div class="wx-day-name">${escapeHtml(lead.name)}</div>
          <div class="wx-day-glyph" aria-hidden="true">${glyph(lead.icon, lead.isDaytime)}</div>
          <div class="wx-day-temps">${temps || "—"}</div>
          <div class="wx-day-short">${escapeHtml(lead.shortForecast)}</div>
          ${meta ? `<div class="wx-day-meta">${escapeHtml(meta)}</div>` : ""}
          <div class="wx-day-detail">${detail}</div>
        </div>`;
    });

    return `
      <div class="wx-section">
        <div class="wx-panel-title">${days.length}-DAY FORECAST</div>
        <div class="wx-daily">${cards.join("")}</div>
      </div>`;
  }

  function footerHtml() {
    const issued = report.forecastUpdatedAt ? `, forecast issued ${formatTime(report.forecastUpdatedAt, WEEKDAY_CLOCK)}` : "";
    const zone = report.location?.timeZone ? ` Times are shown in ${report.location.timeZone.replace(/_/g, " ")}.` : "";
    return `<div class="wx-footer">Forecast, observations and radar from the US National Weather Service (weather.gov)${escapeHtml(issued)}.${escapeHtml(zone)}</div>`;
  }

  function noticeHtml(title, message, extra) {
    return `
      <div class="wx-notice">
        <div class="wx-notice-title">${escapeHtml(title)}</div>
        <p>${escapeHtml(message || "")}</p>
        ${extra || ""}
      </div>`;
  }

  function setupHtml() {
    const settingsTab = document.getElementById("tab-settings");
    const canOpenSettings = settingsTab && !settingsTab.classList.contains("hidden");
    const action = canOpenSettings
      ? `<button type="button" class="btn" id="wx-open-settings">open location settings</button>`
      : `<p class="wx-dim">Ask an admin to set it under Settings → LOCATION.</p>`;
    return noticeHtml("NO LOCATION SET", report.message,
      `<p>The forecast comes from the US National Weather Service, so this tab works for places in the United States and its territories. The coordinates are sent to weather.gov only while this tab is open.</p>${action}`);
  }

  // Jumps to Settings and filters it down to the LOCATION section. Settings loads on its first
  // visit, so wait for its search box to be wired before typing into it.
  function openLocationSettings() {
    const tab = document.querySelector('#tabs .tab[data-view="settings"]');
    if (!tab) return;
    tab.click();
    const startedAt = Date.now();
    (function focusSearch() {
      const search = document.getElementById("settings-search");
      if (search && search.oninput) {
        search.value = "location";
        search.oninput();
        search.focus();
        return;
      }
      if (Date.now() - startedAt < 5000) setTimeout(focusSearch, 100);
    })();
  }

  function render() {
    if (!report) return;

    const fetched = report.fetchedAt ? `updated ${formatTime(report.fetchedAt, CLOCK)}` : "";
    updatedEl.textContent = report.stale ? `${fetched} · showing the last answer weather.gov gave` : fetched;

    if (report.state === "unconfigured") {
      root.innerHTML = setupHtml();
      document.getElementById("wx-open-settings")?.addEventListener("click", openLocationSettings);
      return;
    }
    if (report.state === "unsupported") {
      root.innerHTML = placeHtml() + noticeHtml("NO FORECAST FOR THIS LOCATION", report.message);
      return;
    }
    if (report.state === "unreachable") {
      root.innerHTML = noticeHtml("WEATHER.GOV UNREACHABLE", report.message,
        `<p class="wx-dim">The tab tries again on its own every ten minutes, or press refresh.</p>`);
      return;
    }

    root.innerHTML = [
      placeHtml(),
      problemsHtml(),
      alertsHtml(),
      `<div class="wx-top">${nowHtml()}${radarHtml()}</div>`,
      hourlyHtml(),
      dailyHtml(),
      footerHtml()
    ].join("");

    for (const button of root.querySelectorAll("[data-radar-scope]")) {
      button.addEventListener("click", () => { radarScope = button.dataset.radarScope; render(); });
    }
    for (const button of root.querySelectorAll("[data-radar-kind]")) {
      button.addEventListener("click", () => { radarKind = button.dataset.radarKind; render(); });
    }
    root.querySelector(".wx-radar-img")?.addEventListener("click", (e) => openLightbox(e.currentTarget.src));

    for (const head of root.querySelectorAll(".wx-alert-head")) {
      head.addEventListener("click", () => {
        const alert = head.closest(".wx-alert");
        const id = alert.dataset.alertId;
        const open = alert.classList.toggle("open");
        if (open) openAlerts.add(id); else openAlerts.delete(id);
      });
    }
    for (const day of root.querySelectorAll(".wx-day")) {
      const toggle = () => {
        const open = day.classList.toggle("open");
        if (open) openDays.add(day.dataset.day); else openDays.delete(day.dataset.day);
      };
      day.addEventListener("click", toggle);
      day.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(); } });
    }
  }

  // The radar picture refreshes on its own, faster than the rest of the tab, because the server
  // only holds each frame for two minutes and a storm moves in that time.
  function refreshRadar() {
    const img = root.querySelector(".wx-radar-img");
    if (img) img.src = radarUrl();
  }

  // ---------------- Loading ----------------

  async function load() {
    if (loading) return;
    loading = true;
    refreshBtn.disabled = true;
    try {
      const response = await fetch("/api/weather");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      report = await response.json();
      render();
    } catch (err) {
      console.error("Failed to load the weather", err);
      if (!report) root.innerHTML = `<div class="empty-state">could not load the weather &mdash; ${escapeHtml(err.message)}</div>`;
      else updatedEl.textContent = "refresh failed · showing the last answer";
    } finally {
      loading = false;
      refreshBtn.disabled = false;
    }
  }

  refreshBtn.addEventListener("click", load);

  function show() {
    hide();
    load();
    refreshTimer = setInterval(load, REFRESH_INTERVAL_MS);
    radarTimer = setInterval(refreshRadar, RADAR_REFRESH_INTERVAL_MS);
  }

  function hide() {
    clearInterval(refreshTimer);
    clearInterval(radarTimer);
    refreshTimer = null;
    radarTimer = null;
  }

  return { show, hide };
})();
