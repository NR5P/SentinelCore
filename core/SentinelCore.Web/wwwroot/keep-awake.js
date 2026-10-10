// Keeping the screen on.
//
// A screen wake lock while something is worth watching without touching it: the radar full screen
// (weather.js) or a camera full screen (cameras.js). On the wall kiosk it becomes an idle inhibitor
// the Pi's screen-sleep timer honours. Each caller holds it under its own reason, so one letting go
// doesn't switch the screen off under the other. The browser drops the lock whenever the page is
// hidden, so it is asked for again on coming back. html.keep-awake tells kiosk-wake.js the screen
// can't have gone dark, so the next tap is a real tap.
window.KeepAwake = (function () {
  const reasons = new Set();
  let lock = null;
  // True while a request is in flight. Without it two quick calls (a fullscreenchange and a
  // visibilitychange, say) each got a lock, only the second was kept, and the first was never
  // released - the kiosk then never slept again until Chromium restarted.
  let requesting = false;

  async function acquire() {
    if (reasons.size === 0 || lock || requesting || !navigator.wakeLock || document.visibilityState !== "visible") return;
    requesting = true;
    try {
      const got = await navigator.wakeLock.request("screen");
      got.addEventListener("release", () => { if (lock === got) lock = null; });
      lock = got;
      if (reasons.size === 0) release();
    } catch (e) {
      // Not allowed here (plain HTTP, say): the screen just sleeps as usual.
    } finally {
      requesting = false;
    }
  }

  function release() {
    const held = lock;
    lock = null;
    held?.release().catch(() => {});
  }

  document.addEventListener("visibilitychange", acquire);

  return {
    set(reason, on) {
      if (on) reasons.add(reason);
      else reasons.delete(reason);
      document.documentElement.classList.toggle("keep-awake", reasons.size > 0);
      if (reasons.size > 0) acquire();
      else release();
    }
  };
})();
