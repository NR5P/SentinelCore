// The Weather tab: current conditions, a zoomable radar map, the hourly chart and strip, the
// seven-day forecast, storm tracking and the severe-weather outlook for the location in Settings,
// all from this server (it talks to the National Weather Service and friends and caches the
// answers - see WeatherService.cs and WeatherMapService.cs). Nothing here reaches weather.gov
// directly: even the map tiles are proxied, so the browser only ever talks to one origin.
//
// Fetches only while the tab is open. show() loads and starts the timers, hide() stops them, so
// a dashboard left on the events tab never asks for the weather at all.
window.SentinelWeather = (function () {
  const REFRESH_INTERVAL_MS = 10 * 60_000;
  const RADAR_REFRESH_INTERVAL_MS = 2 * 60_000;
  const FRAME_INTERVAL_MS = 550;
  const LAST_FRAME_HOLD_MS = 1800;

  const root = document.getElementById("weather-root");
  const updatedEl = document.getElementById("weather-updated");
  const refreshBtn = document.getElementById("weather-refresh-btn");

  let report = null;
  let details = null;
  let storms = null;
  let loading = false;
  let refreshTimer = null;
  let mapStarted = false;
  // Which alerts and which days are expanded, by id, so a ten-minute refresh doesn't fold up
  // the warning somebody is in the middle of reading.
  const openAlerts = new Set();
  const openDays = new Set();
  let discussionOpen = false;

  // Per-viewer preferences: the layer, overlays, opacity and whether to follow this device's
  // location. Browser storage can be unavailable (private windows); the defaults then apply.
  const PREFS_KEY = "sentinel-weather-map";
  const prefs = Object.assign({
    layer: "reflectivity",
    station: null,
    opacity: 0.75,
    cells: true,
    warnings: true,
    outlook: false,
    sites: true,
    followMe: false
  }, readPrefs());

  function readPrefs() {
    try { return JSON.parse(localStorage.getItem(PREFS_KEY) || "{}") || {}; } catch { return {}; }
  }

  function savePrefs() {
    try { localStorage.setItem(PREFS_KEY, JSON.stringify(prefs)); } catch { /* storage unavailable */ }
  }

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

  function inches(value, digits = 2) {
    if (value == null) return "—";
    if (value === 0) return "0\"";
    if (value < 0.01) return "trace";
    return `${value.toFixed(digits)}"`;
  }

  // ---------------- Sky arithmetic ----------------

  // The moon's phase from its age since a known new moon (6 Jan 2000 18:14 UTC). Good to within
  // a few hours, which is all a phase name needs.
  const SYNODIC_DAYS = 29.530588853;
  function moonPhase(date) {
    const knownNew = Date.UTC(2000, 0, 6, 18, 14);
    const age = (((date.getTime() - knownNew) / 86_400_000) % SYNODIC_DAYS + SYNODIC_DAYS) % SYNODIC_DAYS;
    const illumination = Math.round((1 - Math.cos(2 * Math.PI * age / SYNODIC_DAYS)) / 2 * 100);
    const phases = [
      [1.84566, "New moon", "🌑"],
      [5.53699, "Waxing crescent", "🌒"],
      [9.22831, "First quarter", "🌓"],
      [12.91963, "Waxing gibbous", "🌔"],
      [16.61096, "Full moon", "🌕"],
      [20.30228, "Waning gibbous", "🌖"],
      [23.99361, "Last quarter", "🌗"],
      [27.68493, "Waning crescent", "🌘"],
      [SYNODIC_DAYS + 1, "New moon", "🌑"]
    ];
    const [, name, icon] = phases.find(([limit]) => age < limit);
    // Days to the next full moon (age 14.77) - handy for anyone planning a night outdoors.
    const toFull = Math.round(((14.765 - age) % SYNODIC_DAYS + SYNODIC_DAYS) % SYNODIC_DAYS);
    return { name, icon, illumination, toFull };
  }

  function daylightText(sun) {
    if (!sun?.sunrise || !sun?.sunset) return null;
    const minutes = Math.round((new Date(sun.sunset) - new Date(sun.sunrise)) / 60_000);
    if (minutes <= 0) return null;
    return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
  }

  // A one-line "what's next" from the hourly forecast: when rain (or snow, or storms) starts or
  // stops in the next twelve hours.
  function nextPrecipText() {
    const hours = (report.hourly || []).slice(0, 12);
    if (!hours.length) return null;
    const wet = h => (h.precipitationChance ?? 0) >= 50;
    const kind = h => {
      const text = (h.shortForecast || "").toLowerCase();
      if (text.includes("thunder")) return "Storms";
      if (text.includes("snow") || text.includes("flurr")) return "Snow";
      if (text.includes("sleet") || text.includes("freezing")) return "Wintry mix";
      if (text.includes("drizzle")) return "Drizzle";
      return "Rain";
    };
    if (wet(hours[0])) {
      const stop = hours.findIndex(h => !wet(h));
      return stop < 0
        ? `${kind(hours[0])} likely for the next 12 hours.`
        : `${kind(hours[0])} likely until around ${formatTime(hours[stop].startTime, HOUR)}.`;
    }
    const start = hours.find(wet);
    if (start) return `${kind(start)} likely from around ${formatTime(start.startTime, HOUR)} (${start.precipitationChance}%).`;
    const peak = hours.reduce((best, h) => (h.precipitationChance ?? 0) > (best.precipitationChance ?? 0) ? h : best, hours[0]);
    if ((peak.precipitationChance ?? 0) >= 20) return `Slight chance of ${kind(peak).toLowerCase()} around ${formatTime(peak.startTime, HOUR)} (${peak.precipitationChance}%).`;
    return "No rain expected in the next 12 hours.";
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
    const all = [...(report.problems || []), ...(details?.problems || [])];
    if (!all.length) return "";
    return `<div class="wx-problems">${all.map(p => `<div>⚠ ${escapeHtml(p)}</div>`).join("")}</div>`;
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

  // The banner that matters most on a stormy day: a tracked storm on course for the house, or
  // the house inside a warning outline. Rendered into its own slot so the two-minute storm
  // refresh can update it without redrawing the page.
  function stormBannerHtml() {
    if (!storms) return "";
    const incoming = (storms.cells || [])
      .filter(c => c.towardHome && c.etaMinutes != null)
      .sort((a, b) => b.threat - a.threat || a.etaMinutes - b.etaMinutes);
    const covering = (storms.warnings || []).filter(w => w.containsHome && ["TO", "SV", "FF", "EW", "SQ", "MA"].includes(w.phenomena));
    if (!incoming.length && !covering.length) return "";

    const lines = [];
    for (const warning of covering) {
      const until = warning.expires ? ` until ${formatTime(warning.expires, CLOCK)}` : "";
      const tags = warningTags(warning);
      lines.push(`<div class="wx-banner-line"><b>Home is inside a ${escapeHtml(warning.name)}</b>${escapeHtml(until)}${tags ? ` · ${escapeHtml(tags)}` : ""}</div>`);
    }
    for (const cell of incoming.slice(0, 3)) {
      lines.push(`<div class="wx-banner-line" data-cell-id="${escapeHtml(cell.id)}" role="button" tabindex="0"><b>${escapeHtml(cellTitle(cell))} heading this way</b> · about ${cell.etaMinutes} min out, passing ${cell.closestMiles < 1 ? "overhead" : `${cell.closestMiles.toFixed(1)} mi away`} · now ${cell.distanceMiles.toFixed(0)} mi ${escapeHtml(cell.bearingCompass || "")}${cellHazards(cell) ? ` · ${escapeHtml(cellHazards(cell))}` : ""}</div>`);
    }
    const level = covering.some(w => w.phenomena === "TO") || incoming.some(c => c.threat >= 3) ? "severe" : "moderate";
    return `<div class="wx-banner wx-banner-${level}"><span class="wx-banner-icon" aria-hidden="true">⛈️</span><div>${lines.join("")}</div></div>`;
  }

  function windText(current) {
    if (current.windMph == null) return null;
    if (current.windMph === 0) return "calm";
    return `${current.windDirection ? current.windDirection + " " : ""}${current.windMph} mph`;
  }

  const PRESSURE_ARROWS = { "rising fast": "⇈", rising: "↑", steady: "→", falling: "↓", "falling fast": "⇊" };

  function nowHtml() {
    const current = report.current;
    const sun = report.sun;
    const moon = moonPhase(new Date());
    const next = nextPrecipText();

    if (!current) {
      return `
        <div class="wx-panel wx-now">
          <div class="wx-panel-title">NOW</div>
          <div class="empty-state">no current conditions</div>
          ${next ? `<div class="wx-next">${escapeHtml(next)}</div>` : ""}
        </div>`;
    }

    const pressure = details?.pressure;
    const pressureText = current.pressureInHg != null
      ? `${current.pressureInHg.toFixed(2)} inHg${pressure ? ` ${PRESSURE_ARROWS[pressure.trend] || ""} ${pressure.trend}` : ""}`
      : null;

    const details_ = [
      ["humidity", current.humidityPercent != null ? `${current.humidityPercent}%` : null],
      ["dew point", current.dewpointF != null ? `${current.dewpointF}°${dewComfort(current.dewpointF)}` : null],
      ["wind", windText(current)],
      ["gusts", current.windGustMph != null ? `${current.windGustMph} mph` : null],
      ["pressure", pressureText],
      ["visibility", current.visibilityMiles != null ? `${current.visibilityMiles} mi` : null],
      ["sunrise", sun?.sunrise ? formatTime(sun.sunrise, CLOCK) : null],
      ["sunset", sun?.sunset ? formatTime(sun.sunset, CLOCK) : null],
      ["daylight", daylightText(sun)],
      ["moon", `${moon.icon} ${moon.name}, ${moon.illumination}% lit`]
    ].filter(([, value]) => value != null);

    const feels = current.feelsLikeF != null && current.feelsLikeF !== current.temperatureF
      ? `<div class="wx-now-feels">feels like ${current.feelsLikeF}°</div>`
      : "";

    const observed = current.observedAt ? ` · observed ${formatTime(current.observedAt, WEEKDAY_CLOCK)}` : "";
    const today = (report.daily || []).find(p => p.isDaytime);
    const tonight = (report.daily || []).find(p => !p.isDaytime);
    const hiLo = [today ? `H ${today.temperatureF}°` : null, tonight ? `L ${tonight.temperatureF}°` : null].filter(Boolean).join("  ");

    return `
      <div class="wx-panel wx-now">
        <div class="wx-panel-title">NOW</div>
        <div class="wx-now-head">
          <span class="wx-now-glyph" aria-hidden="true">${glyph(current.icon, current.isDaytime)}</span>
          <div>
            <div class="wx-now-temp">${current.temperatureF}°F</div>
            <div class="wx-now-desc">${escapeHtml(current.description || "—")}</div>
            ${feels}
            ${hiLo ? `<div class="wx-now-hilo">${escapeHtml(hiLo)}</div>` : ""}
          </div>
        </div>
        ${next ? `<div class="wx-next">${escapeHtml(next)}</div>` : ""}
        <dl class="wx-details">
          ${details_.map(([label, value]) => `<dt>${label}</dt><dd>${escapeHtml(value)}</dd>`).join("")}
        </dl>
        <div class="wx-source" data-tip="Current conditions come from the nearest National Weather Service observation station, which is usually an airport a few miles away - so it can differ a degree or two from the porch.">${escapeHtml(current.station)} · ${escapeHtml(current.stationName)}${escapeHtml(observed)}</div>
      </div>`;
  }

  // How the dew point feels, the number meteorologists actually use for "muggy".
  function dewComfort(dewpoint) {
    if (dewpoint >= 70) return " (oppressive)";
    if (dewpoint >= 65) return " (muggy)";
    if (dewpoint >= 60) return " (sticky)";
    if (dewpoint <= 30) return " (dry)";
    return "";
  }

  // ---- Cards under the map: storms near home, the severe outlook, rain and snow totals ----

  function cellTitle(cell) {
    if (cell.tornadic) return "Possible tornado";
    if (cell.rotation) return "Rotating storm";
    if (cell.threat === 2) return "Severe hail storm";
    if (cell.threat === 1) return "Strong storm";
    return "Storm";
  }

  function cellHazards(cell) {
    const parts = [];
    if (cell.tornadic) parts.push("tornado vortex signature");
    if (cell.rotation) parts.push(`mesocyclone (rank ${cell.mesoStrength})`);
    if (cell.maxHailInches) parts.push(`hail to ${cell.maxHailInches.toFixed(2)}"`);
    else if (cell.hailProbability) parts.push(`${cell.hailProbability}% hail`);
    return parts.join(" · ");
  }

  function stormsCardHtml() {
    const cells = (storms?.cells || [])
      .filter(c => c.distanceMiles != null && c.distanceMiles <= 100)
      .sort((a, b) => (b.towardHome - a.towardHome) || (b.threat - a.threat) || (a.distanceMiles - b.distanceMiles))
      .slice(0, 6);
    const body = cells.length
      ? cells.map(cell => {
          const motion = cell.speedMph ? `moving ${cell.headingCompass} ${cell.speedMph} mph` : "nearly stationary";
          const hazards = cellHazards(cell);
          const eta = cell.towardHome ? `<span class="wx-cell-eta">~${cell.etaMinutes} min</span>` : "";
          return `
            <button type="button" class="wx-cell-row wx-threat-${cell.threat}" data-cell-id="${escapeHtml(cell.id)}" data-tip="Show this storm on the map.">
              <span class="wx-cell-dot" aria-hidden="true"></span>
              <span class="wx-cell-main">
                <span class="wx-cell-name">${escapeHtml(cellTitle(cell))} ${eta}</span>
                <span class="wx-cell-meta">${cell.distanceMiles.toFixed(0)} mi ${escapeHtml(cell.bearingCompass || "")} · ${escapeHtml(motion)}${cell.topKft ? ` · tops ${cell.topKft.toFixed(0)}k ft` : ""}${hazards ? ` · ${escapeHtml(hazards)}` : ""}</span>
              </span>
            </button>`;
        }).join("")
      : `<div class="wx-dim">No storms tracked within 100 miles.</div>`;
    return `
      <div class="wx-panel wx-card">
        <div class="wx-panel-title" data-tip="Storm cells the NEXRAD radars are tracking right now, nearest and most dangerous first. A time means the storm's current track passes within ${10} miles of home.">STORMS NEAR HOME</div>
        <div class="wx-cells">${body}</div>
      </div>`;
  }

  const OUTLOOK_SCALE = {
    TSTM: ["thunderstorms possible", 0],
    MRGL: ["1 of 5 · marginal", 1],
    SLGT: ["2 of 5 · slight", 2],
    ENH: ["3 of 5 · enhanced", 3],
    MDT: ["4 of 5 · moderate", 4],
    HIGH: ["5 of 5 · high", 5]
  };

  function outlookCardHtml() {
    const severe = details?.severe;
    if (!severe) {
      return `
        <div class="wx-panel wx-card">
          <div class="wx-panel-title">SEVERE OUTLOOK</div>
          <div class="wx-dim">${details ? "The Storm Prediction Center's outlook is unavailable right now." : "loading…"}</div>
        </div>`;
    }
    const dayName = n => n === 1 ? "Today" : n === 2 ? "Tomorrow" : formatTime(new Date(Date.now() + (n - 1) * 86_400_000).toISOString(), { weekday: "long" });
    const days = severe.days.map(day => {
      const scale = OUTLOOK_SCALE[day.category];
      const chip = day.category
        ? `<span class="wx-risk wx-risk-${escapeHtml(day.category.toLowerCase())}">${escapeHtml(day.category)}</span>`
        : `<span class="wx-risk wx-risk-none">NONE</span>`;
      return `<div class="wx-risk-row">${chip}<span class="wx-risk-day">${escapeHtml(dayName(day.day))}</span><span class="wx-dim">${escapeHtml(scale ? scale[0] : "no severe storms expected")}</span></div>`;
    }).join("");
    const hazard = (label, percent, significant) =>
      `<div class="wx-hazard"><span>${label}</span><b>${percent ?? 0}%</b>${significant ? `<span class="wx-sig" data-tip="Hatched area: the SPC expects the significant kind here (EF2+ tornadoes, 2-inch hail or 75 mph gusts).">sig</span>` : ""}</div>`;
    return `
      <div class="wx-panel wx-card">
        <div class="wx-panel-title" data-tip="The NOAA Storm Prediction Center's convective outlook for home: the chance of severe thunderstorms today and the next two days, on its 1-to-5 scale, and today's odds of each hazard within 25 miles.">SEVERE OUTLOOK</div>
        ${days}
        <div class="wx-hazards">
          ${hazard("tornado", severe.tornadoPercent, severe.significantTornado)}
          ${hazard("hail", severe.hailPercent, severe.significantHail)}
          ${hazard("wind", severe.windPercent, severe.significantWind)}
        </div>
        ${severe.issuedAt ? `<div class="wx-source">SPC day 1 issued ${escapeHtml(formatTime(severe.issuedAt, WEEKDAY_CLOCK))}</div>` : ""}
      </div>`;
  }

  function totalsCardHtml() {
    const totals = details?.totals;
    if (!totals) {
      return `
        <div class="wx-panel wx-card">
          <div class="wx-panel-title">RAIN &amp; SNOW AHEAD</div>
          <div class="wx-dim">${details ? "Precipitation totals are unavailable right now." : "loading…"}</div>
        </div>`;
    }
    const rows = [
      ["rain, next 24 h", inches(totals.rainNext24HoursInches)],
      ["rain, next 7 days", inches(totals.rainNext7DaysInches)],
      totals.snowNext7DaysInches > 0 ? ["snow, next 24 h", inches(totals.snowNext24HoursInches, 1)] : null,
      totals.snowNext7DaysInches > 0 ? ["snow, next 7 days", inches(totals.snowNext7DaysInches, 1)] : null,
      totals.iceNext7DaysInches > 0 ? ["ice, next 7 days", inches(totals.iceNext7DaysInches)] : null,
      ["thunder, next 24 h", `${totals.thunderChanceNext24Hours}%`],
      ["thunder, next 7 days", `${totals.thunderChanceNext7Days}%`]
    ].filter(Boolean);
    return `
      <div class="wx-panel wx-card">
        <div class="wx-panel-title" data-tip="From the National Weather Service's forecast grid for this spot: expected liquid rain, snowfall and ice, and the highest chance of thunder.">RAIN &amp; SNOW AHEAD</div>
        <dl class="wx-details wx-details-tight">${rows.map(([label, value]) => `<dt>${label}</dt><dd>${escapeHtml(value)}</dd>`).join("")}</dl>
      </div>`;
  }

  // ---- The hourly chart and strip ----

  function hourlyChartHtml(hours) {
    const temps = hours.map(h => h.temperatureF).filter(t => t != null);
    if (temps.length < 2) return "";
    const feelsByTime = new Map((details?.hours || []).map(h => [new Date(h.startTime).getTime(), h]));
    const feels = hours.map(h => feelsByTime.get(new Date(h.startTime).getTime())?.feelsLikeF ?? null);

    const step = 42;
    const width = hours.length * step;
    const height = 150;
    const top = 22;
    const tempBottom = 92;
    const barBottom = 132;
    const all = temps.concat(feels.filter(f => f != null));
    const min = Math.min(...all);
    const max = Math.max(...all);
    const span = Math.max(max - min, 1);
    const y = t => tempBottom - (t - min) / span * (tempBottom - top);
    const x = i => i * step + step / 2;

    const line = values => values
      .map((v, i) => v == null ? null : `${x(i).toFixed(1)},${y(v).toFixed(1)}`)
      .filter(Boolean)
      .join(" ");

    const bars = hours.map((h, i) => {
      const chance = h.precipitationChance ?? 0;
      if (!chance) return "";
      const heightPx = chance / 100 * 34;
      return `<rect class="wx-chart-bar" x="${(x(i) - step * 0.35).toFixed(1)}" y="${(barBottom - heightPx).toFixed(1)}" width="${(step * 0.7).toFixed(1)}" height="${heightPx.toFixed(1)}"><title>${chance}% chance of precipitation</title></rect>
        ${chance >= 20 ? `<text class="wx-chart-pop" x="${x(i).toFixed(1)}" y="${(barBottom - heightPx - 3).toFixed(1)}">${chance}</text>` : ""}`;
    }).join("");

    let lastDay = null;
    const ticks = hours.map((h, i) => {
      const day = localDateKey(h.startTime);
      const newDay = day !== lastDay && i > 0;
      lastDay = day;
      const label = i % 3 === 0 ? `<text class="wx-chart-hour" x="${x(i).toFixed(1)}" y="${height - 4}">${escapeHtml(formatTime(h.startTime, HOUR))}</text>` : "";
      const divider = newDay ? `<line class="wx-chart-day" x1="${i * step}" x2="${i * step}" y1="4" y2="${barBottom}" /><text class="wx-chart-dayname" x="${i * step + 4}" y="12">${escapeHtml(formatTime(h.startTime, WEEKDAY).toUpperCase())}</text>` : "";
      const tempLabel = i % 3 === 0 && h.temperatureF != null ? `<text class="wx-chart-temp" x="${x(i).toFixed(1)}" y="${(y(h.temperatureF) - 7).toFixed(1)}">${h.temperatureF}°</text>` : "";
      return divider + label + tempLabel;
    }).join("");

    const feelsLine = feels.some((f, i) => f != null && f !== hours[i].temperatureF)
      ? `<polyline class="wx-chart-feels" points="${line(feels)}"><title>feels like</title></polyline>`
      : "";

    return `
      <svg class="wx-chart" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="Temperature and chance of precipitation for the next ${hours.length} hours">
        <line class="wx-chart-base" x1="0" x2="${width}" y1="${barBottom}" y2="${barBottom}" />
        ${bars}
        ${feelsLine}
        <polyline class="wx-chart-line" points="${line(hours.map(h => h.temperatureF))}" />
        ${ticks}
      </svg>`;
  }

  function hourlyHtml() {
    const hours = report.hourly || [];
    if (!hours.length) return "";
    const extraByTime = new Map((details?.hours || []).map(h => [new Date(h.startTime).getTime(), h]));
    let lastDay = null;
    const cells = hours.map(hour => {
      const day = localDateKey(hour.startTime);
      const newDay = day !== lastDay;
      lastDay = day;
      const extra = extraByTime.get(new Date(hour.startTime).getTime());
      const chance = hour.precipitationChance != null && hour.precipitationChance > 0
        ? `💧${hour.precipitationChance}%`
        : "&nbsp;";
      const wind = [hour.windDirection, hour.windSpeed].filter(Boolean).join(" ");
      const tip = [
        hour.shortForecast,
        wind ? `wind ${wind}` : null,
        extra?.gustMph ? `gusts ${extra.gustMph} mph` : null,
        hour.humidityPercent != null ? `humidity ${hour.humidityPercent}%` : null,
        extra?.skyCoverPercent != null ? `cloud ${extra.skyCoverPercent}%` : null,
        extra?.rainInches ? `rain ${inches(extra.rainInches)}` : null,
        extra?.snowInches ? `snow ${inches(extra.snowInches, 1)}` : null
      ].filter(Boolean).join(" · ");
      return `
        <div class="wx-hour${newDay ? " wx-hour-newday" : ""}" data-tip-below data-tip="${escapeHtml(tip)}">
          <div class="wx-hour-day">${newDay ? escapeHtml(formatTime(hour.startTime, WEEKDAY)) : ""}</div>
          <div class="wx-hour-time">${escapeHtml(formatTime(hour.startTime, HOUR))}</div>
          <div class="wx-hour-glyph" aria-hidden="true">${glyph(hour.icon, hour.isDaytime)}</div>
          <div class="wx-hour-temp">${hour.temperatureF != null ? `${hour.temperatureF}°` : "—"}</div>
          <div class="wx-hour-pop">${chance}</div>
          <div class="wx-hour-wind">${escapeHtml(hour.windSpeed ? hour.windSpeed.replace(/ mph/, "") : "")}${hour.windDirection ? ` ${escapeHtml(hour.windDirection)}` : ""}</div>
        </div>`;
    });
    return `
      <div class="wx-section">
        <div class="wx-panel-title">NEXT ${hours.length} HOURS <span class="wx-legend-inline"><i class="wx-key wx-key-temp"></i>temperature <i class="wx-key wx-key-feels"></i>feels like <i class="wx-key wx-key-pop"></i>chance of rain</span></div>
        <div class="wx-hourly-chart">${hourlyChartHtml(hours)}</div>
        <div class="wx-hourly">${cells.join("")}</div>
      </div>`;
  }

  // The NWS forecast is a list of half-days (Today, Tonight, Saturday, Saturday Night, ...). One
  // card per calendar day, with the day period leading and the night giving the low, and a bar
  // placing the day's range within the week's.
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

    const allTemps = periods.map(p => p.temperatureF).filter(t => t != null);
    const weekMin = Math.min(...allTemps);
    const weekMax = Math.max(...allTemps);
    const weekSpan = Math.max(weekMax - weekMin, 1);

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

      const from = low ?? high;
      const to = high ?? low;
      const range = from != null
        ? `<div class="wx-range" aria-hidden="true"><span style="left:${((Math.min(from, to) - weekMin) / weekSpan * 100).toFixed(1)}%;width:${Math.max((Math.abs(to - from)) / weekSpan * 100, 3).toFixed(1)}%"></span></div>`
        : "";

      return `
        <div class="wx-day${openDays.has(entry.key) ? " open" : ""}" data-day="${escapeHtml(entry.key)}" tabindex="0" role="button" data-tip-below data-tip="Click for the full forecast for this day and night.">
          <div class="wx-day-name">${escapeHtml(lead.name)}</div>
          <div class="wx-day-glyph" aria-hidden="true">${glyph(lead.icon, lead.isDaytime)}</div>
          <div class="wx-day-temps">${temps || "—"}</div>
          ${range}
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

  function discussionHtml() {
    const discussion = details?.discussion;
    if (!discussion?.text) return "";
    const issued = discussion.issuedAt ? ` · issued ${formatTime(discussion.issuedAt, WEEKDAY_CLOCK)}` : "";
    return `
      <details class="wx-section wx-discussion"${discussionOpen ? " open" : ""}>
        <summary class="wx-panel-title" data-tip="The forecasters' own notes on why the forecast says what it does - how sure they are, what could change, and what they're watching. Written by the local National Weather Service office a few times a day.">FORECASTER DISCUSSION · ${escapeHtml(discussion.office)}${escapeHtml(issued)}</summary>
        <pre class="wx-discussion-text">${escapeHtml(discussion.text.trim())}</pre>
      </details>`;
  }

  function footerHtml() {
    const issued = report.forecastUpdatedAt ? `, forecast issued ${formatTime(report.forecastUpdatedAt, WEEKDAY_CLOCK)}` : "";
    const zone = report.location?.timeZone ? ` Times are shown in ${report.location.timeZone.replace(/_/g, " ")}.` : "";
    return `<div class="wx-footer">Forecast, observations, radar and discussion from the US National Weather Service (weather.gov)${escapeHtml(issued)}. Severe outlook from the NOAA Storm Prediction Center; storm tracks and warning outlines via the Iowa Environmental Mesonet; basemap © Esri, HERE, Garmin and OpenStreetMap contributors.${escapeHtml(zone)}</div>`;
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

  // A render while the radar is full screen waits until it closes: moving the map panel (which
  // every render does) would throw the browser out of full screen, and nothing else shows then.
  let renderWaiting = false;

  function render() {
    if (!report) return;
    if (RadarMap.isFullscreen()) {
      renderWaiting = true;
      return;
    }
    renderWaiting = false;

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

    // The map panel is built once and moved into its slot on every render, so the ten-minute
    // refresh keeps the zoom, the layer and a playing loop exactly as they were.
    const mapPanel = RadarMap.panel();
    mapPanel.remove();

    root.innerHTML = [
      placeHtml(),
      problemsHtml(),
      `<div id="wx-banner-slot">${stormBannerHtml()}</div>`,
      alertsHtml(),
      `<div class="wx-top">${nowHtml()}<div id="wx-map-slot"></div></div>`,
      `<div class="wx-cards"><div id="wx-storms-slot">${stormsCardHtml()}</div>${outlookCardHtml()}${totalsCardHtml()}</div>`,
      hourlyHtml(),
      dailyHtml(),
      discussionHtml(),
      footerHtml()
    ].join("");

    document.getElementById("wx-map-slot").appendChild(mapPanel);
    RadarMap.attached();

    wireStormLinks(root);
    root.querySelector(".wx-discussion")?.addEventListener("toggle", (e) => { discussionOpen = e.currentTarget.open; });

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

  // Clicking a storm in the list or the banner flies the map to it.
  function wireStormLinks(scope) {
    for (const el of scope.querySelectorAll("[data-cell-id]")) {
      const go = () => RadarMap.focusCell(el.dataset.cellId);
      el.addEventListener("click", go);
      el.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); go(); } });
    }
  }

  // The storm parts of the page, refreshed every two minutes without redrawing the rest.
  function renderStormParts() {
    const banner = document.getElementById("wx-banner-slot");
    if (banner) { banner.innerHTML = stormBannerHtml(); wireStormLinks(banner); }
    const list = document.getElementById("wx-storms-slot");
    if (list) { list.innerHTML = stormsCardHtml(); wireStormLinks(list); }
  }

  function warningColor(phenomena) {
    return {
      TO: "#ff2d3d",
      SV: "#ffc400",
      FF: "#00e676",
      FA: "#2e9d4f",
      FL: "#2e9d4f",
      MA: "#ff8c00",
      EW: "#ff3df5",
      SQ: "#d63384",
      DS: "#d9b38c",
      SS: "#b388ff"
    }[phenomena] || "#4be7ff";
  }

  function warningTags(warning) {
    return [
      warning.tornadoTag ? `tornado ${warning.tornadoTag.toLowerCase()}` : null,
      warning.hailInches ? `hail ${warning.hailInches}"` : null,
      warning.windMph ? `wind ${warning.windMph} mph` : null,
      warning.damageTag ? `${warning.damageTag.toLowerCase()} damage threat` : null
    ].filter(Boolean).join(" · ");
  }

  // ---------------- The radar map ----------------

  const RadarMap = (function () {
    let panelEl = null;
    let map = null;
    let info = null;
    let baseLayer = null;
    let labelsLayer = null;
    let homeMarker = null;
    let meMarker = null;
    let meCircle = null;
    let meWatch = null;
    let cellsGroup = null;
    let warningsGroup = null;
    let outlookGroup = null;
    let sitesGroup = null;
    const cellMarkers = new Map();
    // The storm whose popup is open, so the two-minute refresh (and the one after every pan)
    // redraws it open rather than closing it under the reader.
    let openCellId = null;
    let redrawingCells = false;

    // The loop: one tile layer per frame, all on the map, only the current one visible - so each
    // frame's tiles are already loaded by the time it comes round.
    let frames = [];
    let frameLayers = [];
    let frameIndex = 0;
    let playing = false;
    let playTimer = null;
    let stormsTimer = null;

    function panel() {
      if (panelEl) return panelEl;
      panelEl = document.createElement("div");
      panelEl.className = "wx-panel wx-radar";
      panelEl.innerHTML = `
        <div class="wx-panel-head">
          <div class="wx-panel-title" id="wx-radar-title">RADAR</div>
          <div class="wx-radar-controls" id="wx-layer-buttons"></div>
        </div>
        <div class="wx-map-wrap">
          <div id="wx-map" class="wx-map" aria-label="Radar map"></div>
          <div class="wx-map-time" id="wx-map-time"></div>
        </div>
        <div class="wx-timeline">
          <button type="button" class="btn btn-small" id="wx-play" data-tip="Play the last hour or so of radar as a loop.">▶ play</button>
          <input type="range" id="wx-frame" min="0" max="0" value="0" aria-label="Radar frame" />
          <label class="wx-opacity" data-tip="How see-through the radar is over the map.">opacity <input type="range" id="wx-opacity" min="0.2" max="1" step="0.05" /></label>
        </div>
        <div class="wx-map-options">
          <label class="wx-station-pick" data-tip="Which radar the per-radar layers come from. Defaults to the one nearest home; clicking a radar dish on the map picks it too.">radar
            <select id="wx-station"></select>
          </label>
          <label class="wx-toggle"><input type="checkbox" data-overlay="cells" /> storm tracks</label>
          <label class="wx-toggle"><input type="checkbox" data-overlay="warnings" /> warnings</label>
          <label class="wx-toggle"><input type="checkbox" data-overlay="outlook" /> SPC outlook</label>
          <label class="wx-toggle"><input type="checkbox" data-overlay="sites" /> radar sites</label>
        </div>
        <div class="wx-legend">
          <img id="wx-legend-img" alt="" />
          <div class="wx-radar-note" id="wx-layer-note"></div>
        </div>`;

      panelEl.querySelector("#wx-play").addEventListener("click", () => playing ? stop() : play());
      panelEl.querySelector("#wx-frame").addEventListener("input", (e) => { stop(); showFrame(Number(e.target.value)); });
      const opacity = panelEl.querySelector("#wx-opacity");
      opacity.value = prefs.opacity;
      opacity.addEventListener("input", () => { prefs.opacity = Number(opacity.value); savePrefs(); showFrame(frameIndex); });
      panelEl.querySelector("#wx-station").addEventListener("change", (e) => selectStation(e.target.value));
      for (const box of panelEl.querySelectorAll("[data-overlay]")) {
        box.checked = !!prefs[box.dataset.overlay];
        box.addEventListener("change", () => {
          prefs[box.dataset.overlay] = box.checked;
          savePrefs();
          applyOverlays();
        });
      }
      return panelEl;
    }

    // Called after every render moves the panel into the page.
    function attached() {
      if (typeof L === "undefined") {
        panelEl.querySelector("#wx-map").innerHTML = `<div class="empty-state">the map library didn't load</div>`;
        return;
      }
      if (!map) create();
      // Leaflet measures its container; after a move (or the tab becoming visible) it has to be
      // told to look again or it draws into a 0x0 box.
      requestAnimationFrame(() => map && map.invalidateSize());
    }

    function create() {
      const home = report?.location ? [report.location.latitude, report.location.longitude] : [39.5, -98.35];
      map = L.map(panelEl.querySelector("#wx-map"), {
        center: home,
        zoom: report?.location ? 8 : 4,
        minZoom: 3,
        maxZoom: 16,
        zoomSnap: 0.5,
        zoomDelta: 0.5,
        wheelPxPerZoomLevel: 90,
        worldCopyJump: true,
        attributionControl: true
      });
      map.attributionControl.setPrefix(false);

      map.createPane("wxOutlook").style.zIndex = 330;
      map.createPane("wxRadar").style.zIndex = 350;
      const labels = map.createPane("wxLabels");
      labels.style.zIndex = 380;
      labels.style.pointerEvents = "none";
      map.createPane("wxWarnings").style.zIndex = 420;

      baseLayer = L.tileLayer("/api/weather/map/tile/base/{z}/{x}/{y}", {
        maxNativeZoom: 16,
        maxZoom: 16,
        attribution: "Basemap © Esri, HERE, Garmin, © OpenStreetMap contributors"
      }).addTo(map);
      labelsLayer = L.tileLayer("/api/weather/map/tile/labels/{z}/{x}/{y}", {
        maxNativeZoom: 16,
        maxZoom: 16,
        pane: "wxLabels"
      }).addTo(map);

      outlookGroup = L.layerGroup();
      warningsGroup = L.layerGroup();
      cellsGroup = L.layerGroup();
      sitesGroup = L.layerGroup();

      addButtons();
      placeHome();

      let moveTimer = null;
      map.on("moveend", () => {
        clearTimeout(moveTimer);
        moveTimer = setTimeout(loadStorms, 400);
      });
      map.on("zoomend", () => drawCells());

      if (prefs.followMe) locateMe(false);
    }

    // Home, locate-me and full-screen buttons under Leaflet's zoom buttons.
    function addButtons() {
      const Buttons = L.Control.extend({
        options: { position: "topleft" },
        onAdd() {
          const bar = L.DomUtil.create("div", "leaflet-bar wx-map-buttons");
          const make = (html, title, handler) => {
            const a = L.DomUtil.create("a", "", bar);
            a.href = "#";
            a.innerHTML = html;
            a.title = title;
            a.setAttribute("role", "button");
            L.DomEvent.on(a, "click", (e) => { L.DomEvent.stop(e); handler(a); });
            return a;
          };
          make("⌂", "Back to home", () => goHome());
          make("◎", "Show where this device is (asks the browser for your location)", (a) => {
            if (meWatch != null) { stopLocating(); a.classList.remove("wx-on"); }
            else { locateMe(true); a.classList.add("wx-on"); }
          }).classList.toggle("wx-on", !!prefs.followMe);
          make("⛶", "Full screen", () => toggleFullscreen());
          L.DomEvent.disableClickPropagation(bar);
          return bar;
        }
      });
      map.addControl(new Buttons());
    }

    function goHome() {
      const location = report?.location;
      if (!location) return;
      map.flyTo([location.latitude, location.longitude], Math.max(map.getZoom(), 8), { duration: 0.8 });
    }

    // Full screen fills the window with the map and also asks the browser for real full screen,
    // so its tabs and address bar go too. The screen is kept awake the whole time - the wall
    // kiosk would otherwise dim and switch off in the middle of watching a storm come in.
    let browserFullscreen = false;

    function toggleFullscreen(force) {
      const wrap = panelEl;
      const on = force ?? !wrap.classList.contains("wx-fullscreen");
      if (on === wrap.classList.contains("wx-fullscreen")) return;
      wrap.classList.toggle("wx-fullscreen", on);
      document.body.classList.toggle("wx-noscroll", on);
      if (on && wrap.requestFullscreen && !document.fullscreenElement) wrap.requestFullscreen().catch(() => {});
      if (!on && document.fullscreenElement === wrap) document.exitFullscreen().catch(() => {});
      KeepAwake.set("radar", on);
      if (!on && renderWaiting) render();
      setTimeout(() => map.invalidateSize(), 50);
    }

    function isFullscreen() {
      return !!panelEl?.classList.contains("wx-fullscreen");
    }

    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && panelEl?.classList.contains("wx-fullscreen")) toggleFullscreen(false);
    });

    // Leaving the browser's full screen its own way (Escape, F11) leaves ours too.
    document.addEventListener("fullscreenchange", () => {
      if (!panelEl) return;
      if (document.fullscreenElement === panelEl) browserFullscreen = true;
      else if (browserFullscreen) {
        browserFullscreen = false;
        toggleFullscreen(false);
      }
    });

    // The house: a pulsing ring and a labelled pin that stays readable over any radar colour.
    function placeHome() {
      const location = report?.location;
      if (!location) return;
      const label = (location.label || [location.city, location.state].filter(Boolean).join(", ") || "Home").toUpperCase();
      const icon = L.divIcon({
        className: "wx-home-icon",
        html: `<div class="wx-home-pin"><span class="wx-home-pulse"></span><span class="wx-home-dot">⌂</span><span class="wx-home-label">${escapeHtml(label)}</span></div>`,
        iconSize: [0, 0]
      });
      if (homeMarker) homeMarker.setLatLng([location.latitude, location.longitude]).setIcon(icon);
      else homeMarker = L.marker([location.latitude, location.longitude], { icon, zIndexOffset: 1000, keyboard: false, title: "Home" }).addTo(map);
    }

    // This device's own position, from the browser. Only asked for when the button is pressed
    // (or was pressed last time and remembered); browsers only allow it over HTTPS or localhost.
    function locateMe(fly) {
      if (!navigator.geolocation) {
        alert("This browser can't share its location.");
        return;
      }
      stopLocating();
      prefs.followMe = true;
      savePrefs();
      let first = true;
      meWatch = navigator.geolocation.watchPosition((position) => {
        const at = [position.coords.latitude, position.coords.longitude];
        const accuracy = position.coords.accuracy || 0;
        const icon = L.divIcon({ className: "wx-me-icon", html: `<div class="wx-me-dot"><span class="wx-me-label">YOU</span></div>`, iconSize: [0, 0] });
        if (!meMarker) {
          meMarker = L.marker(at, { icon, zIndexOffset: 1100, keyboard: false, title: "You are here" }).addTo(map);
          meCircle = L.circle(at, { radius: accuracy, className: "wx-me-accuracy", interactive: false }).addTo(map);
        } else {
          meMarker.setLatLng(at);
          meCircle.setLatLng(at).setRadius(accuracy);
        }
        meMarker.bindTooltip(`You are here (±${Math.round(accuracy)} m)${homeDistanceText(at)}`, { direction: "top", offset: [0, -10] });
        if (first && fly) map.flyTo(at, Math.max(map.getZoom(), 10), { duration: 0.8 });
        first = false;
      }, (error) => {
        stopLocating();
        prefs.followMe = false;
        savePrefs();
        panelEl.querySelector(".wx-map-buttons a:nth-child(2)")?.classList.remove("wx-on");
        if (fly) alert(error.code === 1
          ? "Location permission was refused. Allow it for this site in the browser to see where you are on the map. (Browsers only offer it over HTTPS.)"
          : "Couldn't get this device's location.");
      }, { enableHighAccuracy: true, maximumAge: 30_000, timeout: 20_000 });
    }

    function stopLocating() {
      if (meWatch != null) navigator.geolocation.clearWatch(meWatch);
      meWatch = null;
      if (meMarker) { meMarker.remove(); meMarker = null; }
      if (meCircle) { meCircle.remove(); meCircle = null; }
      prefs.followMe = false;
      savePrefs();
    }

    function homeDistanceText(at) {
      const location = report?.location;
      if (!location) return "";
      const miles = map.distance(at, [location.latitude, location.longitude]) / 1609.344;
      return miles < 0.2 ? " - at home" : ` - ${miles < 10 ? miles.toFixed(1) : Math.round(miles)} mi from home`;
    }

    // ---- Layers and frames ----

    function layerById(id) {
      return info?.layers.find(l => l.id === id);
    }

    function currentLayer() {
      return layerById(prefs.layer) || layerById("reflectivity") || info?.layers[0];
    }

    function tileUrl(layer, time) {
      const params = new URLSearchParams();
      if (layer.scope === "station" && info.station) params.set("station", info.station);
      if (time) params.set("time", time);
      const query = params.toString();
      return `/api/weather/map/tile/${encodeURIComponent(layer.id)}/{z}/{x}/{y}${query ? `?${query}` : ""}`;
    }

    // (Re)builds the frame layers for the chosen layer. `keepPosition` keeps showing the newest
    // frame after a refresh brings new ones.
    function rebuildFrames() {
      if (!map || !info) return;
      const wasPlaying = playing;
      stop();
      for (const layer of frameLayers) layer.remove();
      frameLayers = [];

      const layer = currentLayer();
      if (!layer) return;
      if (layer.scope === "station" && !info.station) return;

      frames = layer.times.length ? layer.times : [null];
      frameIndex = frames.length - 1;

      // Only the newest frame is loaded up front; the rest are added when the loop is played, so
      // a glance at the tab costs one screen of tiles rather than twelve.
      frameLayers = frames.map(() => null);
      ensureFrameLayer(frameIndex);

      const slider = panelEl.querySelector("#wx-frame");
      slider.max = String(frames.length - 1);
      slider.value = String(frameIndex);
      slider.disabled = frames.length < 2;
      panelEl.querySelector("#wx-play").disabled = frames.length < 2;

      showFrame(frameIndex);
      renderLayerChrome();
      if (wasPlaying) play();
    }

    function ensureFrameLayer(index) {
      if (frameLayers[index]) return frameLayers[index];
      const layer = currentLayer();
      const tiles = L.tileLayer(tileUrl(layer, frames[index]), {
        pane: "wxRadar",
        opacity: 0,
        maxNativeZoom: info.maximumRadarZoom || 12,
        maxZoom: 16,
        className: "wx-radar-tiles",
        keepBuffer: 1,
        updateWhenZooming: false
      }).addTo(map);
      frameLayers[index] = tiles;
      return tiles;
    }

    function showFrame(index) {
      if (!frames.length) return;
      frameIndex = Math.max(0, Math.min(index, frames.length - 1));
      ensureFrameLayer(frameIndex);
      frameLayers.forEach((layer, i) => layer && layer.setOpacity(i === frameIndex ? prefs.opacity : 0));
      panelEl.querySelector("#wx-frame").value = String(frameIndex);

      const time = frames[frameIndex];
      const label = panelEl.querySelector("#wx-map-time");
      if (time) {
        const minutesAgo = Math.round((Date.now() - new Date(time).getTime()) / 60_000);
        label.textContent = `${formatTime(time, CLOCK)} · ${minutesAgo <= 1 ? "just now" : `${minutesAgo} min ago`}`;
      } else {
        label.textContent = "latest";
      }
      label.classList.toggle("wx-map-time-old", frameIndex < frames.length - 1);
    }

    function play() {
      if (frames.length < 2) return;
      playing = true;
      // Add every frame now so their tiles load while the first few play.
      frames.forEach((_, i) => ensureFrameLayer(i));
      panelEl.querySelector("#wx-play").textContent = "❚❚ pause";
      const tick = () => {
        if (!playing) return;
        const next = frameIndex >= frames.length - 1 ? 0 : frameIndex + 1;
        showFrame(next);
        playTimer = setTimeout(tick, next === frames.length - 1 ? LAST_FRAME_HOLD_MS : FRAME_INTERVAL_MS);
      };
      if (frameIndex === frames.length - 1) showFrame(0);
      playTimer = setTimeout(tick, FRAME_INTERVAL_MS);
    }

    function stop() {
      playing = false;
      clearTimeout(playTimer);
      if (panelEl) panelEl.querySelector("#wx-play").textContent = "▶ play";
    }

    function renderLayerChrome() {
      const buttons = panelEl.querySelector("#wx-layer-buttons");
      const layer = currentLayer();
      const group = scope => info.layers.filter(l => l.scope === scope).map(l =>
        `<button type="button" class="btn btn-small${l.id === layer?.id ? " btn-on" : ""}" data-layer="${escapeHtml(l.id)}" data-tip-below data-tip="${escapeHtml(l.description)}"${l.scope === "station" && !info.station ? " disabled" : ""}>${escapeHtml(l.name)}</button>`
      ).join("");
      buttons.innerHTML = `
        <span class="wx-layer-group"><span class="wx-layer-group-name">national</span>${group("national")}</span>
        <span class="wx-layer-group"><span class="wx-layer-group-name">${escapeHtml(info.station || "radar")}</span>${group("station")}</span>`;
      for (const button of buttons.querySelectorAll("[data-layer]")) {
        button.addEventListener("click", () => {
          prefs.layer = button.dataset.layer;
          savePrefs();
          rebuildFrames();
        });
      }

      const title = panelEl.querySelector("#wx-radar-title");
      title.textContent = layer ? `RADAR · ${layer.scope === "station" ? `${info.station} ` : ""}${layer.name.toUpperCase()}` : "RADAR";

      const legend = panelEl.querySelector("#wx-legend-img");
      if (layer) {
        const params = layer.scope === "station" && info.station ? `?station=${encodeURIComponent(info.station)}` : "";
        const src = `/api/weather/map/legend/${encodeURIComponent(layer.id)}${params}`;
        if (legend.getAttribute("src") !== src) legend.src = src;
        legend.alt = `${layer.name} colour key`;
      }
      panelEl.querySelector("#wx-layer-note").textContent = layer
        ? `${layer.description} ${layer.scope === "station" ? `From radar ${info.station}; it sees about 150 miles out.` : "Updated every few minutes."} Scroll or pinch to zoom, drag to pan.`
        : "";

      const select = panelEl.querySelector("#wx-station");
      const home = report?.location;
      const sites = [...(info.stations || [])];
      const dist = s => home ? map.distance([home.latitude, home.longitude], [s.latitude, s.longitude]) / 1609.344 : 0;
      sites.sort((a, b) => dist(a) - dist(b));
      select.innerHTML = sites.map(s =>
        `<option value="${escapeHtml(s.id)}"${s.id === info.station ? " selected" : ""}>${escapeHtml(s.id)} · ${escapeHtml(s.name)}${home ? ` (${Math.round(dist(s))} mi)` : ""}</option>`
      ).join("");

      drawSites();
    }

    async function selectStation(station) {
      prefs.station = station === info?.homeStation ? null : station;
      if (layerById(prefs.layer)?.scope !== "station") prefs.layer = "station-reflectivity";
      savePrefs();
      await loadInfo();
    }

    function drawSites() {
      sitesGroup.clearLayers();
      for (const site of info.stations || []) {
        const selected = site.id === info.station;
        const marker = L.circleMarker([site.latitude, site.longitude], {
          radius: selected ? 6 : 4,
          className: selected ? "wx-site wx-site-on" : "wx-site",
          pane: "markerPane"
        });
        marker.bindTooltip(`${escapeHtml(site.id)} · ${escapeHtml(site.name)}${selected ? " (selected)" : " - click to use this radar"}`, { direction: "top" });
        marker.on("click", () => selectStation(site.id));
        marker.addTo(sitesGroup);
      }
    }

    // ---- Storms, warnings, outlook ----

    function applyOverlays() {
      if (!map) return;
      const toggle = (group, on) => on ? group.addTo(map) : group.remove();
      toggle(cellsGroup, prefs.cells);
      toggle(warningsGroup, prefs.warnings);
      toggle(outlookGroup, prefs.outlook);
      toggle(sitesGroup, prefs.sites);
    }

    function cellIcon(cell) {
      const arrow = cell.headingDegrees != null
        ? `<span class="wx-cell-arrow" style="transform:rotate(${cell.headingDegrees}deg)">➤</span>`
        : "";
      return L.divIcon({
        className: "wx-cell-icon",
        html: `<div class="wx-cell-marker wx-threat-${cell.threat}${cell.towardHome ? " wx-cell-incoming" : ""}">${cell.tornadic ? "T" : cell.rotation ? "M" : cell.threat === 2 ? "H" : ""}${arrow}</div>`,
        iconSize: [0, 0]
      });
    }

    function popupWidth() {
      return Math.max(200, Math.min(320, map.getSize().x - 70));
    }

    function cellPopup(cell) {
      const rows = [
        ["tracked by", `${cell.radar} radar · cell ${cell.stormId}`],
        ["motion", cell.speedMph ? `toward the ${cell.headingCompass} at ${cell.speedMph} mph` : "nearly stationary"],
        cell.distanceMiles != null ? ["from home", `${cell.distanceMiles.toFixed(1)} mi ${cell.bearingCompass || ""}`] : null,
        cell.towardHome ? ["reaches home", `in about ${cell.etaMinutes} min, passing ${cell.closestMiles.toFixed(1)} mi away`] : null,
        cell.tornadic ? ["tornado", "tornado vortex signature - strong rotation near the ground"] : null,
        cell.mesoStrength ? ["rotation", `mesocyclone rank ${cell.mesoStrength} of 25${cell.rotation ? "" : " (weak)"}`] : null,
        ["hail", `${cell.hailProbability ?? 0}% chance, ${cell.severeHailProbability ?? 0}% severe${cell.maxHailInches ? `, up to ${cell.maxHailInches.toFixed(2)}"` : ""}`],
        cell.maxDbz != null ? ["max reflectivity", `${cell.maxDbz} dBZ`] : null,
        cell.topKft != null ? ["storm top", `${cell.topKft.toFixed(0)},000 ft`] : null,
        cell.vil != null ? ["VIL", `${cell.vil} kg/m² (water in the column; 40+ often means hail)`] : null,
        cell.validAt ? ["scan", formatTime(cell.validAt, CLOCK)] : null
      ].filter(Boolean);
      return `<div class="wx-popup"><div class="wx-popup-title">${escapeHtml(cellTitle(cell))}</div><dl>${rows.map(([k, v]) => `<dt>${escapeHtml(k)}</dt><dd>${escapeHtml(v)}</dd>`).join("")}</dl></div>`;
    }

    // Tracks draw for anything notable, anything heading home, and everything once zoomed in far
    // enough that they don't turn the map into a hairball.
    function drawCells() {
      if (!map || !cellsGroup) return;
      redrawingCells = true;
      cellsGroup.clearLayers();
      cellMarkers.clear();
      const zoom = map.getZoom();
      for (const cell of storms?.cells || []) {
        if (cell.threat === 0 && zoom < 9 && !cell.towardHome && cell.id !== openCellId) continue;
        const showTrack = cell.track?.length && (cell.threat >= 1 || cell.towardHome || zoom >= 10);
        if (showTrack) {
          const points = [[cell.latitude, cell.longitude], ...cell.track.map(p => [p.latitude, p.longitude])];
          L.polyline(points, { className: `wx-track wx-track-${cell.threat}${cell.towardHome ? " wx-track-incoming" : ""}`, interactive: false }).addTo(cellsGroup);
          for (const point of cell.track) {
            L.circleMarker([point.latitude, point.longitude], { radius: 2.5, className: "wx-track-dot", interactive: true })
              .bindTooltip(`+${point.minutes} min`, { direction: "right", className: "wx-track-tip" })
              .addTo(cellsGroup);
          }
        }
        const marker = L.marker([cell.latitude, cell.longitude], { icon: cellIcon(cell), zIndexOffset: 500 + cell.threat * 10 })
          .bindTooltip(`${escapeHtml(cellTitle(cell))}${cellHazards(cell) ? ` · ${escapeHtml(cellHazards(cell))}` : ""}${cell.speedMph ? ` · ${escapeHtml(cell.headingCompass)} ${cell.speedMph} mph` : ""}`, { direction: "top", offset: [0, -8] })
          .bindPopup(cellPopup(cell), { maxWidth: popupWidth(), autoPanPadding: [12, 12] })
          .addTo(cellsGroup);
        marker.on("popupopen", () => { openCellId = cell.id; });
        marker.on("popupclose", () => { if (!redrawingCells) openCellId = null; });
        cellMarkers.set(cell.id, marker);
      }
      redrawingCells = false;
      if (openCellId && prefs.cells) cellMarkers.get(openCellId)?.openPopup();
    }

    function drawWarnings() {
      warningsGroup.clearLayers();
      // Weakest first so a tornado warning draws on top of the severe thunderstorm warning it
      // sits inside.
      const order = { TO: 9, EW: 8, SV: 7, SQ: 6, FF: 5, MA: 4 };
      const warnings = [...(storms?.warnings || [])].sort((a, b) => (order[a.phenomena] || 0) - (order[b.phenomena] || 0));
      for (const warning of warnings) {
        const color = warningColor(warning.phenomena);
        const latlngs = warning.polygons.map(rings => rings.map(ring => ring.map(([lon, lat]) => [lat, lon])));
        const until = warning.expires ? `until ${formatTime(warning.expires, CLOCK)}` : "";
        const tags = warningTags(warning);
        const badges = [warning.emergency ? "EMERGENCY" : null, warning.particularlyDangerous ? "PARTICULARLY DANGEROUS SITUATION" : null].filter(Boolean);
        L.polygon(latlngs, {
          pane: "wxWarnings",
          color,
          weight: warning.phenomena === "TO" ? 3 : 2,
          fillColor: color,
          fillOpacity: 0.12,
          className: "wx-warning"
        })
          .bindTooltip(`${escapeHtml(warning.name)} ${escapeHtml(until)}`, { sticky: true })
          .bindPopup(`<div class="wx-popup"><div class="wx-popup-title" style="color:${color}">${escapeHtml(warning.name)}</div>
            ${badges.length ? `<div class="wx-popup-badge">${badges.map(escapeHtml).join(" · ")}</div>` : ""}
            <dl>
              <dt>in effect</dt><dd>${escapeHtml(warning.issued ? formatTime(warning.issued, CLOCK) : "")} - ${escapeHtml(warning.expires ? formatTime(warning.expires, CLOCK) : "")}</dd>
              ${tags ? `<dt>threats</dt><dd>${escapeHtml(tags)}</dd>` : ""}
              ${warning.office ? `<dt>issued by</dt><dd>NWS ${escapeHtml(warning.office)}</dd>` : ""}
              ${warning.containsHome ? `<dt>home</dt><dd><b>inside this warning</b></dd>` : ""}
            </dl></div>`, { maxWidth: popupWidth(), autoPanPadding: [12, 12] })
          .addTo(warningsGroup);
      }
    }

    function drawOutlook() {
      outlookGroup.clearLayers();
      const areas = [...(storms?.outlook?.areas || [])].sort((a, b) => a.rank - b.rank);
      for (const area of areas) {
        const latlngs = area.polygons.map(rings => rings.map(ring => ring.map(([lon, lat]) => [lat, lon])));
        L.polygon(latlngs, {
          pane: "wxOutlook",
          color: area.stroke || "#888",
          weight: 1.5,
          fillColor: area.fill || "#888",
          fillOpacity: 0.18,
          interactive: true
        }).bindTooltip(`SPC today: ${escapeHtml(area.name)}`, { sticky: true }).addTo(outlookGroup);
      }
    }

    async function loadStorms() {
      if (!map) return;
      const bounds = map.getBounds().pad(0.3);
      const params = new URLSearchParams({
        west: bounds.getWest().toFixed(3),
        south: bounds.getSouth().toFixed(3),
        east: bounds.getEast().toFixed(3),
        north: bounds.getNorth().toFixed(3)
      });
      // Always include home's surroundings, so the "near home" list and banner stay right while
      // someone is looking at a storm three states away.
      const location = report?.location;
      try {
        const [view, near] = await Promise.all([
          fetch(`/api/weather/storms?${params}`).then(r => r.ok ? r.json() : null),
          location
            ? fetch(`/api/weather/storms?west=${location.longitude - 2.5}&south=${location.latitude - 2}&east=${location.longitude + 2.5}&north=${location.latitude + 2}`).then(r => r.ok ? r.json() : null)
            : Promise.resolve(null)
        ]);
        if (!view) return;
        const byId = new Map(view.cells.map(c => [c.id, c]));
        for (const cell of near?.cells || []) byId.set(cell.id, cell);
        const warningsById = new Map(view.warnings.map(w => [w.id, w]));
        for (const warning of near?.warnings || []) warningsById.set(warning.id, warning);
        storms = { ...view, cells: [...byId.values()], warnings: [...warningsById.values()] };
        drawCells();
        drawWarnings();
        drawOutlook();
        applyOverlays();
        renderStormParts();
      } catch (err) {
        console.error("Failed to load storms", err);
      }
    }

    async function loadInfo() {
      try {
        const params = prefs.station ? `?station=${encodeURIComponent(prefs.station)}` : "";
        const response = await fetch(`/api/weather/map${params}`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const next = await response.json();
        const sameFrames = info && JSON.stringify(info.layers.map(l => l.times)) === JSON.stringify(next.layers.map(l => l.times)) && info.station === next.station;
        info = next;
        if (!map) return;
        placeHome();
        if (!sameFrames || !frameLayers.length) rebuildFrames();
        else renderLayerChrome();
      } catch (err) {
        console.error("Failed to load the radar map", err);
      }
    }

    function focusCell(id) {
      const cell = storms?.cells.find(c => c.id === id);
      if (!cell || !map) return;
      if (!prefs.cells) {
        prefs.cells = true;
        savePrefs();
        panelEl.querySelector('[data-overlay="cells"]').checked = true;
        applyOverlays();
      }
      panelEl.scrollIntoView({ behavior: "smooth", block: "center" });
      openCellId = id;
      map.flyTo([cell.latitude, cell.longitude], Math.max(map.getZoom(), 9), { duration: 0.8 });
      map.once("moveend", () => cellMarkers.get(id)?.openPopup());
    }

    function start() {
      stopTimers();
      loadInfo().then(loadStorms);
      stormsTimer = setInterval(() => { loadInfo(); loadStorms(); }, RADAR_REFRESH_INTERVAL_MS);
    }

    function stopTimers() {
      clearInterval(stormsTimer);
      stormsTimer = null;
      stop();
      // Leaving the tab lets go of full screen and the screen with it.
      if (isFullscreen()) toggleFullscreen(false);
    }

    return { panel, attached, start, stop: stopTimers, focusCell, loadStorms, isFullscreen };
  })();

  // ---------------- Loading ----------------

  async function loadDetails() {
    try {
      const response = await fetch("/api/weather/details");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      details = await response.json();
    } catch (err) {
      console.error("Failed to load the weather details", err);
    }
  }

  async function load() {
    if (loading) return;
    loading = true;
    refreshBtn.disabled = true;
    try {
      const response = await fetch("/api/weather");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      report = await response.json();
      render();
      if (report.state === "ok") {
        if (!mapStarted) {
          mapStarted = true;
          RadarMap.start();
        }
        await loadDetails();
        render();
      }
    } catch (err) {
      console.error("Failed to load the weather", err);
      if (!report) root.innerHTML = `<div class="empty-state">could not load the weather &mdash; ${escapeHtml(err.message)}</div>`;
      else updatedEl.textContent = "refresh failed · showing the last answer";
    } finally {
      loading = false;
      refreshBtn.disabled = false;
    }
  }

  refreshBtn.addEventListener("click", () => { load(); RadarMap.loadStorms(); });

  function show() {
    hide();
    load();
    refreshTimer = setInterval(load, REFRESH_INTERVAL_MS);
  }

  function hide() {
    clearInterval(refreshTimer);
    refreshTimer = null;
    mapStarted = false;
    RadarMap.stop();
  }

  return { show, hide, glyph };
})();
