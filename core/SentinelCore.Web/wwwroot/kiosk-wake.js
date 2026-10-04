// Kiosk wake tap.
//
// The wall kiosk's screen switches itself off after a few minutes with nobody touching it, and a
// tap turns it back on. The Pi hands that same tap to the page, so without this the tap that
// wakes a dark screen would also press whatever happens to be under the finger - a camera, a
// movie, an alarm button. In kiosk mode (html.kiosk), a press that arrives after the page has
// been untouched for as long as the screen-off delay only wakes the screen and does nothing else.
//
// The page can't see the screen itself, so the delay has to match the Pi's idle timer. It is
// set from the kiosk's start URL - ?screenoff=300 (seconds) - remembered on this device, and
// ?screenoff=0 turns this off. Five minutes when never set.
(function () {
  const KEY = "sentinelcore.kioskScreenOff";
  const DEFAULT_SECONDS = 300;
  // Pressing just before the screen goes dark should still work, so only a press a little past
  // the delay counts as a wake tap.
  const MARGIN_MS = 2000;

  function delaySeconds() {
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

  const seconds = delaySeconds();
  if (seconds <= 0) return;
  const idleMs = seconds * 1000 + MARGIN_MS;

  const isOn = () => document.documentElement.classList.contains("kiosk");

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
    if (!isOn() || idle < idleMs) return;
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
})();
