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

  async function acquire() {
    if (reasons.size === 0 || lock || !navigator.wakeLock || document.visibilityState !== "visible") return;
    try {
      lock = await navigator.wakeLock.request("screen");
      lock.addEventListener("release", () => { lock = null; });
      if (reasons.size === 0) release();
    } catch (e) {
      // Not allowed here (plain HTTP, say): the screen just sleeps as usual.
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
