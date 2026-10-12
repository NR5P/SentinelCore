// Kiosk weather warnings.
//
// On the wall kiosk (html.kiosk), a tornado warning, severe thunderstorm watch or any other NWS
// alert that Settings > WALL KIOSK WEATHER WARNINGS asks for gets a flashing banner across the top
// of the screen. It floats over the page without blocking it: whoever is using the tablet carries
// on, and the rest of the screen still works. Tapping the banner opens the radar full screen;
// × dismisses it. Either way it counts as seen and goes away, and the Weather button keeps a
// coloured dot until the alert ends.
//
// A new alert also wakes the screen (a wake lock, which the Pi's screen-sleep timer honours and
// which lights a dimmed or dark screen) for the configured few minutes, and if nobody has touched
// the tablet for a minute (so it has dimmed or gone dark) it goes straight to the radar full
// screen, with the banner and the NWS headline over it saying what's happening.
//
// "Seen" is remembered per device in localStorage. The NWS reissues a warning every so often as it
// tracks the storm, each reissue a new id that references the old one - a reissue of something
// already seen is seen too, so one storm is one banner. An upgrade (watch to warning) is a new
// alert and flashes again.
(function () {
  const POLL_MS = 60_000;
  const SEEN_KEY = "sentinelcore.kiosk.weatherSeen";
  const IDLE_FOR_RADAR_MS = 60_000;
  const SEEN_FALLBACK_MS = 24 * 3600_000;

  const isKiosk = () => document.documentElement.classList.contains("kiosk");

  let alerts = [];
  let wakeMinutes = 10;
  let openRadarOnArrival = true;
  // When this page first saw each alert, for how long it keeps the screen lit.
  const firstShown = new Map();

  function readSeen() {
    try { return JSON.parse(localStorage.getItem(SEEN_KEY) || "{}") || {}; } catch { return {}; }
  }

  function writeSeen() {
    try { localStorage.setItem(SEEN_KEY, JSON.stringify(seen)); } catch { /* storage blocked */ }
  }

  // id -> when it can be forgotten (ms).
  const seen = readSeen();
  for (const [id, until] of Object.entries(seen)) if (until < Date.now()) delete seen[id];

  function endsMs(alert) {
    const t = alert.ends ? Date.parse(alert.ends) : NaN;
    return Number.isFinite(t) ? t : Date.now() + SEEN_FALLBACK_MS;
  }

  function isSeen(alert) {
    if (seen[alert.id]) return true;
    if ((alert.references || []).some((ref) => seen[ref])) {
      // A reissue of something already seen: remember it in its own right, so the next reissue
      // (which references this one) is seen too after the original is forgotten.
      seen[alert.id] = endsMs(alert);
      writeSeen();
      return true;
    }
    return false;
  }

  function markSeen(list) {
    for (const alert of list) {
      seen[alert.id] = endsMs(alert);
      for (const ref of alert.references || []) seen[ref] = endsMs(alert);
    }
    writeSeen();
    update();
  }

  // ---- input and "is somebody using it" ----------------------------------------------------

  let lastInput = Date.now();
  for (const type of ["pointerdown", "keydown", "wheel", "touchstart"]) {
    document.addEventListener(type, () => { lastInput = Date.now(); }, { capture: true, passive: true });
  }

  function busy() {
    if (Date.now() - lastInput < IDLE_FOR_RADAR_MS) return true;
    if (document.fullscreenElement) return true;
    const focused = document.activeElement;
    if (focused && focused.matches?.("input, textarea, select, [contenteditable]")) return true;
    for (const el of document.querySelectorAll('.lightbox, [class*="modal"], .au-np, dialog[open]')) {
      if (el.getClientRects().length) return true;
    }
    for (const video of document.querySelectorAll("#view-movies video")) {
      if (!video.paused) return true;
    }
    return false;
  }

  // ---- the banner ---------------------------------------------------------------------------

  const banner = document.createElement("div");
  banner.className = "kwx-banner hidden";
  banner.setAttribute("role", "alert");
  banner.innerHTML = `
    <button type="button" class="kwx-main">
      <svg class="kwx-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 3l10 18H2z"/><path d="M12 10v5"/><path d="M12 18h.01"/></svg>
      <span class="kwx-text">
        <span class="kwx-line"><span class="kwx-event"></span> <span class="kwx-until"></span> <span class="kwx-more"></span></span>
        <span class="kwx-headline"></span>
      </span>
      <span class="kwx-hint">tap for radar</span>
    </button>
    <button type="button" class="kwx-close" aria-label="dismiss">×</button>`;
  document.body.appendChild(banner);

  let shownNow = [];

  banner.querySelector(".kwx-main").addEventListener("click", () => {
    const onRadar = !!document.querySelector(".wx-radar.wx-fullscreen");
    markSeen(shownNow);
    if (!onRadar) showRadar();
  });
  banner.querySelector(".kwx-close").addEventListener("click", () => markSeen(shownNow));

  function showRadar() {
    const tab = document.querySelector('#tabs .tab[data-view="weather"]');
    if (!tab || !window.SentinelWeather?.openRadar) return;
    if (!tab.classList.contains("active")) tab.click();
    window.SentinelWeather.openRadar();
  }

  function untilText(alert) {
    if (!alert.ends) return "";
    const ends = new Date(alert.ends);
    const time = ends.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    const sameDay = ends.toDateString() === new Date().toDateString();
    return `until ${sameDay ? time : ends.toLocaleDateString([], { weekday: "short" }) + " " + time}`;
  }

  function setLevelClass(el, prefix, level) {
    for (const l of ["warning", "watch", "advisory"]) el.classList.toggle(`${prefix}-${l}`, level === l);
  }

  function update() {
    const now = Date.now();
    const active = isKiosk() ? alerts.filter((a) => endsMs(a) > now) : [];
    const unseen = active.filter((a) => !isSeen(a));
    shownNow = unseen;

    // The Weather button's dot: the worst alert in force, seen or not.
    const tab = document.querySelector('#tabs .tab[data-view="weather"]');
    if (tab) {
      let dot = tab.querySelector(".kwx-dot");
      if (!dot) {
        dot = document.createElement("span");
        dot.className = "kwx-dot";
        tab.appendChild(dot);
      }
      if (active.length) tab.dataset.wxAlert = active[0].level;
      else delete tab.dataset.wxAlert;
    }

    if (!unseen.length) {
      banner.classList.add("hidden");
      KeepAwake.set("weather-alert", false);
      return;
    }

    const top = unseen[0];
    banner.querySelector(".kwx-event").textContent = top.event;
    banner.querySelector(".kwx-until").textContent = untilText(top);
    banner.querySelector(".kwx-more").textContent = unseen.length > 1 ? `+${unseen.length - 1} more` : "";
    banner.querySelector(".kwx-headline").textContent = top.headline || "";
    // Over the radar there's nothing more to open: the tap just says "seen".
    const onRadar = !!document.querySelector(".wx-radar.wx-fullscreen");
    banner.querySelector(".kwx-hint").textContent = onRadar ? "got it" : "tap for radar";
    setLevelClass(banner, "kwx", top.level);
    banner.classList.remove("hidden");

    // Wake and hold the screen while any unseen alert is still new.
    const wakeMs = wakeMinutes * 60_000;
    const waking = wakeMs > 0 && unseen.some((a) => now - (firstShown.get(a.id) ?? now) < wakeMs);
    KeepAwake.set("weather-alert", waking);
  }

  async function poll() {
    if (!isKiosk()) {
      if (alerts.length) { alerts = []; update(); }
      return;
    }
    let body;
    try {
      const response = await fetch("/api/kiosk/weather-alerts", { cache: "no-store" });
      if (!response.ok) return;
      body = await response.json();
    } catch {
      return;   // server unreachable: keep what's showing
    }
    alerts = body.alerts || [];
    wakeMinutes = Number.isFinite(body.wakeMinutes) ? body.wakeMinutes : 10;
    openRadarOnArrival = body.openRadar !== false;

    const now = Date.now();
    let arrived = false;
    for (const alert of alerts) {
      if (firstShown.has(alert.id)) continue;
      firstShown.set(alert.id, now);
      if (!isSeen(alert)) arrived = true;
    }
    for (const id of firstShown.keys()) if (!alerts.some((a) => a.id === id)) firstShown.delete(id);

    update();
    // Nobody at the tablet (it has dimmed or gone dark): straight to the radar, banner on top.
    if (arrived && openRadarOnArrival && !busy()) showRadar();
  }

  // Leaving kiosk mode (or coming back to it) re-checks straight away. Only that class: the
  // wake lock's html.keep-awake changes on every update.
  let wasKiosk = isKiosk();
  new MutationObserver(() => {
    if (isKiosk() === wasKiosk) return;
    wasKiosk = isKiosk();
    poll();
  }).observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });

  // The radar opening or closing changes what tapping the banner does.
  document.addEventListener("sentinel:radar-fullscreen", update);

  // The wake hold runs out between polls too.
  setInterval(update, 30_000);
  setInterval(poll, POLL_MS);
  window.addEventListener("DOMContentLoaded", poll);
})();
