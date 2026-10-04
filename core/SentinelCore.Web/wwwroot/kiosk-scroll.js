// Kiosk swipe-to-scroll.
//
// The wall kiosk is Chromium on a Raspberry Pi touchscreen, and it reports the screen as a mouse:
// a finger dragged up the page selects text instead of scrolling, and the only way to move is the
// scrollbar. In kiosk mode (html.kiosk) this turns a drag with that "mouse" into a phone-style
// scroll: the content follows the finger, keeps gliding after a flick and eases to a stop, and a
// drag never counts as a tap on whatever it started over. Real touch input is left alone - the
// browser already scrolls that natively, and much more smoothly, so this is the fallback: the
// Pi itself now gives Chromium real touch (labwc mouseEmulation="no" + --ozone-platform=wayland).
(function () {
  const START_DISTANCE = 8;      // px a press must travel before it becomes a scroll, not a tap
  const GLIDE_TIME = 325;        // ms time constant of the glide's slow-down (what iOS uses)
  const MIN_SPEED = 0.1;         // px/ms a flick needs to glide at all
  const MAX_SPEED = 6;           // px/ms cap on a flick, so a hard swipe doesn't fly off the page
  const VELOCITY_WINDOW = 80;    // ms of movement before letting go that sets the flick speed

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

  // The position is tracked here as a fraction rather than read back from the element: the
  // browser rounds scrollTop to whole pixels, so adding a sub-pixel step to it each frame stalls a
  // slow glide and makes a drag move in uneven jumps.
  const posOf = (el, axis) => axis === "y" ? el.scrollTop : el.scrollLeft;
  const maxOf = (el, axis) => axis === "y"
    ? el.scrollHeight - el.clientHeight
    : el.scrollWidth - el.clientWidth;
  function scrollTo(el, axis, pos) {
    if (axis === "y") el.scrollTop = pos; else el.scrollLeft = pos;
  }

  // The glide follows a fixed curve worked out from the moment of release, rather than adding
  // up per-frame steps, so a frame the Pi drops costs no distance and no speed - it just lands
  // where it should have been.
  function startGlide(el, axis, velocity) {
    const from = posOf(el, axis);
    const max = maxOf(el, axis);
    const distance = -velocity * GLIDE_TIME;
    const begin = performance.now();
    const step = (now) => {
      const t = now - begin;
      const left = Math.exp(-t / GLIDE_TIME);   // fraction of the glide still to come
      const pos = Math.max(0, Math.min(max, from + distance * (1 - left)));
      scrollTo(el, axis, pos);
      // Stop once the remaining movement is under half a pixel or it has run into an end.
      if (Math.abs(distance * left) < 0.5 || pos === 0 || pos === max) {
        glide = null;
        resumeSnap(el);
        return;
      }
      glide = requestAnimationFrame(step);
    };
    glide = requestAnimationFrame(step);
  }

  // Finger movement arrives faster and less evenly than the screen redraws, so it is collected
  // here and applied once per frame.
  let frame = null;
  function applyDrag() {
    frame = null;
    if (!press || !press.axis) return;
    const finger = press.axis === "y" ? press.y : press.x;
    const pos = Math.max(0, Math.min(maxOf(press.scroller, press.axis), press.origin - (finger - press.start)));
    scrollTo(press.scroller, press.axis, pos);
  }

  document.addEventListener("pointerdown", (e) => {
    if (frame) { cancelAnimationFrame(frame); frame = null; }
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
      // From here the content stays pinned under the finger: its position is where it was at
      // this moment, minus however far the finger has gone since.
      press.x = e.clientX;
      press.y = e.clientY;
      press.start = press.axis === "y" ? e.clientY : e.clientX;
      press.origin = posOf(press.scroller, press.axis);
      press.samples = [{ t: e.timeStamp, x: e.clientX, y: e.clientY }];
      return;
    }

    e.preventDefault();
    press.x = e.clientX;
    press.y = e.clientY;
    const points = e.getCoalescedEvents ? e.getCoalescedEvents() : [];
    for (const p of points.length ? points : [e]) press.samples.push({ t: p.timeStamp, x: p.clientX, y: p.clientY });
    while (press.samples.length > 2 && e.timeStamp - press.samples[0].t > VELOCITY_WINDOW * 2) press.samples.shift();
    if (!frame) frame = requestAnimationFrame(applyDrag);
  });

  function release(e, cancelled) {
    if (!press || e.pointerId !== press.id) return;
    const p = press;
    press = null;
    if (!p.axis) return;
    if (frame) { cancelAnimationFrame(frame); frame = null; }
    press = p; applyDrag(); press = null;   // land the last bit of movement before gliding
    document.documentElement.classList.remove("kiosk-dragging");
    if (!cancelled) {
      // The press ended a scroll, so the click the browser is about to send is not a tap.
      swallowClick = true;
      setTimeout(() => { swallowClick = false; }, 0);
    }
    // The flick speed is the finger's average over the last moments before it lifted - just the
    // last two samples would be too jumpy, and the whole swipe would include its slow start.
    const last = p.samples[p.samples.length - 1];
    const recent = p.samples.filter((s) => last.t - s.t <= VELOCITY_WINDOW);
    const first = recent[0];
    const age = last.t - first.t;
    // Holding still before letting go means "stop here", not "flick".
    const stale = e.timeStamp - last.t > 60;
    if (cancelled || stale || age <= 0) { resumeSnap(p.scroller); return; }
    const moved = p.axis === "y" ? last.y - first.y : last.x - first.x;
    const velocity = Math.max(-MAX_SPEED, Math.min(MAX_SPEED, moved / age));
    if (Math.abs(velocity) < MIN_SPEED) { resumeSnap(p.scroller); return; }
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

  // With real touch input a long press opens the browser's right-click menu, which a wall screen
  // has no use for and no way to dismiss tidily.
  document.addEventListener("contextmenu", (e) => {
    if (isOn() && !e.target.closest?.(SKIP)) e.preventDefault();
  }, true);

  // Links and pictures are natively draggable; a swipe that starts on one would otherwise pick
  // it up instead of scrolling. Things that are deliberately draggable keep working.
  document.addEventListener("dragstart", (e) => {
    if (!isOn()) return;
    const target = e.target.nodeType === 1 ? e.target : e.target.parentElement;
    if (target && !target.closest("[draggable='true']")) e.preventDefault();
  }, true);
})();
