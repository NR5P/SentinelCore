// Kiosk wake tap.
//
// The wall kiosk's screen switches itself off after a few minutes with nobody touching it, and a
// tap turns it back on. The Pi hands that same tap to the page, so without this the tap that
// wakes a dark screen would also press whatever happens to be under the finger - a camera, a
// movie, an alarm button. In kiosk mode (html.kiosk), a press that arrives after the page has
// been untouched for as long as the screen-off delay only wakes the screen and does nothing else.
//
// The page can't see the screen itself, so the delay has to match the Pi's idle timer. Both come
// from the same place: Settings > WALL KIOSK, which the Pi's screen-sleep script reads too
// (GET /api/kiosk/screen-sleep, checked here every minute). Until that answers, the start URL's
// ?screenoff=300 (seconds) or the last value seen on this device stands in, then five minutes.
// 0 turns this off.
(function () {
  const KEY = "sentinelcore.kioskScreenOff";
  const DEFAULT_SECONDS = 300;
  const REFRESH_MS = 60000;
  // Pressing just before the screen goes dark should still work, so only a press a little past
  // the delay counts as a wake tap.
  const MARGIN_MS = 2000;

  function remember(value) {
    try { localStorage.setItem(KEY, String(value)); } catch (e) { /* storage blocked */ }
  }

  function initialSeconds() {
    try {
      const param = new URLSearchParams(location.search).get("screenoff");
      if (param !== null && /^\d+$/.test(param)) localStorage.setItem(KEY, param);
      const saved = localStorage.getItem(KEY);
      if (saved !== null && /^\d+$/.test(saved)) return Number(saved);
    } catch (e) {
      // Storage blocked: fall back to the default.
    }
    return DEFAULT_SECONDS;
  }

  let seconds = initialSeconds();

  async function refresh() {
    if (!isOn()) return;
    try {
      const response = await fetch("/api/kiosk/screen-sleep", { cache: "no-store" });
      if (!response.ok) return;
      const body = await response.json();
      if (Number.isInteger(body.offSeconds) && body.offSeconds >= 0) {
        seconds = body.offSeconds;
        remember(seconds);
      }
    } catch (e) {
      // Server unreachable: keep the last value.
    }
  }

  const isOn = () => document.documentElement.classList.contains("kiosk");
  // Something on the page (the full-screen radar) is holding the screen on, so it never went dark.
  const keptAwake = () => document.documentElement.classList.contains("keep-awake");

  let lastInput = performance.now();
  let waking = null;   // pointerId of the wake press, until its click arrives

  function touched() { lastInput = performance.now(); }

  function swallow(e) {
    e.preventDefault();
    e.stopImmediatePropagation();
  }

  // Capture phase on window, so this runs before kiosk-scroll.js and every control's own handler.
  window.addEventListener("pointerdown", (e) => {
    const idle = performance.now() - lastInput;
    touched();
    waking = null;   // a new press is never part of an earlier wake tap
    if (!isOn() || seconds <= 0 || idle < seconds * 1000 + MARGIN_MS || keptAwake()) return;
    waking = e.pointerId;
    swallow(e);
  }, true);

  for (const type of ["mousedown", "mouseup", "touchstart", "touchend", "contextmenu", "dblclick"]) {
    window.addEventListener(type, (e) => { if (waking !== null) swallow(e); }, { capture: true, passive: false });
  }

  window.addEventListener("pointermove", (e) => {
    // A touchscreen reporting as a mouse moves the pointer to the finger before pressing, so
    // only movement with a finger down counts as someone using the screen.
    if (e.buttons) touched();
    if (waking !== null && e.pointerId === waking) swallow(e);
  }, true);

  function endWake(e) {
    if (waking === null || e.pointerId !== waking) return;
    touched();
    swallow(e);
    // A cancelled press gets no click, so nothing more to swallow.
    if (e.type === "pointercancel") waking = null;
  }
  window.addEventListener("pointerup", endWake, true);
  window.addEventListener("pointercancel", endWake, true);

  // The wake press's click is the last thing it sends.
  window.addEventListener("click", (e) => {
    if (waking === null) return;
    waking = null;
    swallow(e);
  }, true);

  window.addEventListener("keydown", touched, true);
  window.addEventListener("wheel", touched, { capture: true, passive: true });

  refresh();
  setInterval(refresh, REFRESH_MS);
})();
