// Kiosk swipe-to-scroll.
//
// The wall kiosk is Chromium on a Raspberry Pi touchscreen, and it reports the screen as a mouse:
// a finger dragged up the page selects text instead of scrolling, and the only way to move is the
// scrollbar. In kiosk mode (html.kiosk) this turns a drag with that "mouse" into a phone-style
// scroll: the content follows the finger, keeps gliding after a flick and eases to a stop, and a
// drag never counts as a tap on whatever it started over. Real touch input is left alone - the
// browser already scrolls that natively.
(function () {
  const START_DISTANCE = 8;      // px a press must travel before it becomes a scroll, not a tap
  const FRICTION = 0.95;         // fraction of the glide speed kept per 16ms frame
  const MIN_SPEED = 0.02;        // px/ms below which a glide stops
  const MAX_SPEED = 5;           // px/ms cap on a flick, so a hard swipe doesn't fly off the page
  const VELOCITY_WINDOW = 100;   // ms of recent movement that sets the flick speed

  // Places where a drag already means something else.
  const SKIP = "input, textarea, select, [contenteditable=''], [contenteditable='true'], " +
    "[draggable='true'], video, canvas, .leaflet-container, .au-drag";

  const isOn = () => document.documentElement.classList.contains("kiosk");

  let press = null;     // the current press, before and during a drag
  let glide = null;     // requestAnimationFrame id of the current glide
  let swallowClick = false;

  function canScroll(el, axis) {
    if (el === document.scrollingElement) {
      return axis === "y"
        ? el.scrollHeight > el.clientHeight
        : el.scrollWidth > el.clientWidth;
    }
    const style = getComputedStyle(el);
    const overflow = axis === "y" ? style.overflowY : style.overflowX;
    if (overflow !== "auto" && overflow !== "scroll") return false;
    return axis === "y"
      ? el.scrollHeight > el.clientHeight + 1
      : el.scrollWidth > el.clientWidth + 1;
  }

  // The nearest thing under the finger that scrolls along this axis, ending at the page itself.
  function scrollerFor(start, axis) {
    for (let el = start; el && el !== document.body && el !== document.documentElement; el = el.parentElement) {
      if (el.nodeType === 1 && canScroll(el, axis)) return el;
    }
    const page = document.scrollingElement || document.documentElement;
    return canScroll(page, axis) ? page : null;
  }

  function stopGlide() {
    if (glide) cancelAnimationFrame(glide);
    glide = null;
  }

  // Scroll snapping would yank the content back to a card edge on every frame of a drag, so it
  // is paused while the finger is down and the content is gliding.
  function pauseSnap(el) {
    if (el.dataset.kioskSnap !== undefined) return;
    el.dataset.kioskSnap = el.style.scrollSnapType;
    el.style.scrollSnapType = "none";
  }
  function resumeSnap(el) {
    if (el.dataset.kioskSnap === undefined) return;
    el.style.scrollSnapType = el.dataset.kioskSnap;
    delete el.dataset.kioskSnap;
  }

  function scrollBy(el, axis, delta) {
    if (axis === "y") el.scrollTo({ top: el.scrollTop + delta, behavior: "instant" });
    else el.scrollTo({ left: el.scrollLeft + delta, behavior: "instant" });
  }

  function startGlide(el, axis, velocity) {
    let last = performance.now();
    const step = (now) => {
      const dt = Math.min(now - last, 50);
      last = now;
      const before = axis === "y" ? el.scrollTop : el.scrollLeft;
      scrollBy(el, axis, -velocity * dt);
      const after = axis === "y" ? el.scrollTop : el.scrollLeft;
      velocity *= Math.pow(FRICTION, dt / 16);
      // Stop once it has slowed right down or run into the end.
      if (Math.abs(velocity) < MIN_SPEED || after === before) {
        glide = null;
        resumeSnap(el);
        return;
      }
      glide = requestAnimationFrame(step);
    };
    glide = requestAnimationFrame(step);
  }

  document.addEventListener("pointerdown", (e) => {
    if (glide) {
      // A finger on gliding content stops it, like a phone, and is not a tap.
      stopGlide();
      if (press && press.scroller) resumeSnap(press.scroller);
    }
    press = null;
    if (!isOn() || e.pointerType !== "mouse" || e.button !== 0) return;
    // Another control already claimed this press (a reorder grip, a zone handle).
    if (e.defaultPrevented) return;
    if (e.target.closest && e.target.closest(SKIP)) return;
    press = {
      id: e.pointerId,
      target: e.target,
      x: e.clientX,
      y: e.clientY,
      axis: null,
      scroller: null,
      samples: [{ t: e.timeStamp, x: e.clientX, y: e.clientY }],
    };
  });

  document.addEventListener("pointermove", (e) => {
    if (!press || e.pointerId !== press.id) return;
    if (!(e.buttons & 1)) { press = null; return; }

    if (!press.axis) {
      const dx = e.clientX - press.x;
      const dy = e.clientY - press.y;
      if (Math.hypot(dx, dy) < START_DISTANCE) return;
      // Try the direction the finger is mostly going first, then the other one, so a slightly
      // diagonal swipe on a sideways row still scrolls the row.
      const order = Math.abs(dy) >= Math.abs(dx) ? ["y", "x"] : ["x", "y"];
      for (const axis of order) {
        const scroller = scrollerFor(press.target, axis);
        if (scroller) { press.axis = axis; press.scroller = scroller; break; }
      }
      if (!press.axis) { press = null; return; }
      pauseSnap(press.scroller);
      document.documentElement.classList.add("kiosk-dragging");
      const sel = window.getSelection && window.getSelection();
      if (sel) sel.removeAllRanges();
      press.x = e.clientX;
      press.y = e.clientY;
      return;
    }

    e.preventDefault();
    const delta = press.axis === "y" ? press.y - e.clientY : press.x - e.clientX;
    scrollBy(press.scroller, press.axis, delta);
    press.x = e.clientX;
    press.y = e.clientY;
    press.samples.push({ t: e.timeStamp, x: e.clientX, y: e.clientY });
    while (press.samples.length > 2 && e.timeStamp - press.samples[0].t > VELOCITY_WINDOW) press.samples.shift();
  });

  function release(e, cancelled) {
    if (!press || e.pointerId !== press.id) return;
    const p = press;
    press = null;
    if (!p.axis) return;
    document.documentElement.classList.remove("kiosk-dragging");
    if (!cancelled) {
      // The press ended a scroll, so the click the browser is about to send is not a tap.
      swallowClick = true;
      setTimeout(() => { swallowClick = false; }, 0);
    }
    const first = p.samples[0];
    const age = e.timeStamp - first.t;
    // Holding still before letting go means "stop here", not "flick".
    const stale = e.timeStamp - p.samples[p.samples.length - 1].t > 60;
    if (cancelled || stale || age <= 0) { resumeSnap(p.scroller); return; }
    const moved = p.axis === "y" ? e.clientY - first.y : e.clientX - first.x;
    const velocity = Math.max(-MAX_SPEED, Math.min(MAX_SPEED, moved / age));
    if (Math.abs(velocity) < MIN_SPEED * 5) { resumeSnap(p.scroller); return; }
    press = { scroller: p.scroller };   // remembered so a press mid-glide can restore snapping
    startGlide(p.scroller, p.axis, velocity);
  }

  document.addEventListener("pointerup", (e) => release(e, false));
  document.addEventListener("pointercancel", (e) => release(e, true));

  window.addEventListener("click", (e) => {
    if (!swallowClick) return;
    swallowClick = false;
    e.preventDefault();
    e.stopPropagation();
  }, true);

  // Links and pictures are natively draggable; a swipe that starts on one would otherwise pick
  // it up instead of scrolling. Things that are deliberately draggable keep working.
  document.addEventListener("dragstart", (e) => {
    if (!isOn()) return;
    const target = e.target.nodeType === 1 ? e.target : e.target.parentElement;
    if (target && !target.closest("[draggable='true']")) e.preventDefault();
  }, true);
})();
