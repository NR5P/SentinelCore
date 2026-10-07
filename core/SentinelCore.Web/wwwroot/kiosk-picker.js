// Kiosk date and time picker.
//
// The wall kiosk's Chromium (Wayland, touch, full screen) brings up nothing usable for date and
// time fields: tapping one just focuses a field that wants a hardware keyboard, and the on-screen
// keyboard (kiosk-keyboard.js) only handles text. In kiosk mode (html.kiosk) a tap on a date, time
// or datetime-local field opens this instead: a touch-sized month calendar and/or hour, minute and
// AM/PM steppers. Done writes the value back and fires the field's input and change events, as if
// it had been typed. Anywhere else (kiosk mode off) the browser's own picker is left alone.
(function () {
  const TYPES = new Set(["date", "time", "datetime-local"]);
  const MINUTE_STEP = 5;
  const REPEAT_DELAY = 450;   // ms holding a stepper before it starts repeating
  const REPEAT_EVERY = 110;   // ms between repeats after that
  const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August",
    "September", "October", "November", "December"];
  const WEEKDAYS = ["S", "M", "T", "W", "T", "F", "S"];

  const isOn = () => document.documentElement.classList.contains("kiosk");
  const pad = (n) => String(n).padStart(2, "0");

  let overlay = null;
  let field = null;       // the input being edited
  let kind = "";          // its type
  let date = null;        // { y, m, d } chosen, or null when nothing is picked yet
  let view = null;        // { y, m } month shown in the calendar
  let time = null;        // { h, min } (24-hour)
  let repeat = null;

  function pickable(el) {
    return el && el.tagName === "INPUT" && TYPES.has((el.type || "").toLowerCase())
      && !el.disabled && !el.readOnly;
  }

  function parse(el) {
    const v = el.value || "";
    const now = new Date();
    let d = null;
    let t = null;
    const dm = /^(\d{4})-(\d{2})-(\d{2})/.exec(v);
    if (dm) d = { y: +dm[1], m: +dm[2] - 1, d: +dm[3] };
    const tm = /(?:^|T)(\d{2}):(\d{2})/.exec(v);
    if (tm) t = { h: +tm[1], min: +tm[2] };
    if (kind !== "time" && !d) d = { y: now.getFullYear(), m: now.getMonth(), d: now.getDate() };
    // An empty time starts at the next quarter hour rather than midnight.
    if (kind !== "date" && !t) {
      const q = new Date(now.getTime() + 15 * 60000);
      t = { h: q.getHours(), min: Math.floor(q.getMinutes() / 15) * 15 };
    }
    return { d, t };
  }

  function open(el) {
    field = el;
    kind = el.type.toLowerCase();
    const { d, t } = parse(el);
    date = d;
    time = t;
    view = d ? { y: d.y, m: d.m } : { y: new Date().getFullYear(), m: new Date().getMonth() };
    if (!overlay) build();
    render();
    overlay.classList.add("open");
  }

  function close() {
    stopRepeat();
    if (overlay) overlay.classList.remove("open");
    field = null;
  }

  function commit(value) {
    const el = field;
    close();
    if (!el) return;
    el.value = value;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function valueOf() {
    const ds = date ? `${date.y}-${pad(date.m + 1)}-${pad(date.d)}` : "";
    const ts = time ? `${pad(time.h)}:${pad(time.min)}` : "";
    if (kind === "date") return ds;
    if (kind === "time") return ts;
    return `${ds}T${ts}`;
  }

  function build() {
    overlay = document.createElement("div");
    overlay.className = "kiosk-picker";
    overlay.innerHTML = '<div class="kiosk-picker-panel"></div>';
    // A tap on the dimmed backdrop cancels.
    overlay.addEventListener("pointerdown", (e) => {
      if (e.target === overlay) { e.preventDefault(); close(); }
    });
    overlay.addEventListener("pointerdown", onPress);
    for (const type of ["pointerup", "pointercancel", "pointerleave"]) overlay.addEventListener(type, stopRepeat);
    overlay.addEventListener("mousedown", (e) => e.preventDefault());
    overlay.addEventListener("contextmenu", (e) => e.preventDefault());
    document.body.appendChild(overlay);
  }

  function render() {
    const panel = overlay.firstChild;
    let html = "";
    if (kind !== "time") html += calendarHtml();
    if (kind !== "date") html += timeHtml();
    html += `<div class="kiosk-picker-actions">
      <div class="kiosk-picker-btn" data-act="clear">clear</div>
      ${kind === "time" ? '<div class="kiosk-picker-btn" data-act="now">now</div>' : '<div class="kiosk-picker-btn" data-act="today">today</div>'}
      <div class="kiosk-picker-btn" data-act="cancel">cancel</div>
      <div class="kiosk-picker-btn kiosk-picker-done" data-act="done">done</div>
    </div>`;
    panel.innerHTML = html;
  }

  function calendarHtml() {
    const { y, m } = view;
    const first = new Date(y, m, 1).getDay();
    const days = new Date(y, m + 1, 0).getDate();
    const now = new Date();
    let cells = WEEKDAYS.map((w) => `<div class="kiosk-picker-wd">${w}</div>`).join("");
    for (let i = 0; i < first; i++) cells += "<div></div>";
    for (let d = 1; d <= days; d++) {
      const cls = ["kiosk-picker-day"];
      if (date && date.y === y && date.m === m && date.d === d) cls.push("selected");
      if (now.getFullYear() === y && now.getMonth() === m && now.getDate() === d) cls.push("today");
      cells += `<div class="${cls.join(" ")}" data-day="${d}">${d}</div>`;
    }
    return `<div class="kiosk-picker-month">
        <div class="kiosk-picker-btn kiosk-picker-nav" data-act="prev">‹</div>
        <div class="kiosk-picker-title">${MONTHS[m]} ${y}</div>
        <div class="kiosk-picker-btn kiosk-picker-nav" data-act="next">›</div>
      </div>
      <div class="kiosk-picker-grid">${cells}</div>`;
  }

  function timeHtml() {
    const h12 = time.h % 12 === 0 ? 12 : time.h % 12;
    const pm = time.h >= 12;
    const col = (unit, text) => `<div class="kiosk-picker-col">
        <div class="kiosk-picker-btn kiosk-picker-step" data-step="${unit}" data-dir="1">▲</div>
        <div class="kiosk-picker-value">${text}</div>
        <div class="kiosk-picker-btn kiosk-picker-step" data-step="${unit}" data-dir="-1">▼</div>
      </div>`;
    return `<div class="kiosk-picker-time">
        ${col("h", h12)}<div class="kiosk-picker-colon">:</div>${col("min", pad(time.min))}
        <div class="kiosk-picker-ampm">
          <div class="kiosk-picker-btn${pm ? "" : " selected"}" data-act="am">AM</div>
          <div class="kiosk-picker-btn${pm ? " selected" : ""}" data-act="pm">PM</div>
        </div>
      </div>`;
  }

  function step(unit, dir) {
    if (unit === "h") {
      time.h = (time.h + dir + 24) % 24;
    } else {
      // Snap to the step grid first, so 9:07 goes to 9:10 / 9:05 rather than 9:12 / 9:02.
      let min = time.min;
      if (min % MINUTE_STEP) min = dir > 0 ? Math.ceil(min / MINUTE_STEP) * MINUTE_STEP : Math.floor(min / MINUTE_STEP) * MINUTE_STEP;
      else min += dir * MINUTE_STEP;
      if (min >= 60) { min -= 60; time.h = (time.h + 1) % 24; }
      if (min < 0) { min += 60; time.h = (time.h + 23) % 24; }
      time.min = min;
    }
    render();
  }

  function stopRepeat() {
    clearTimeout(repeat);
    clearInterval(repeat);
    repeat = null;
  }

  function onPress(e) {
    const t = e.target.closest("[data-act], [data-day], [data-step]");
    if (!t) return;
    e.preventDefault();
    if (t.dataset.day) {
      date = { y: view.y, m: view.m, d: +t.dataset.day };
      // A date-only field is done as soon as the day is tapped.
      if (kind === "date") { commit(valueOf()); return; }
      render();
      return;
    }
    if (t.dataset.step) {
      const unit = t.dataset.step;
      const dir = +t.dataset.dir;
      step(unit, dir);
      stopRepeat();
      repeat = setTimeout(() => { repeat = setInterval(() => step(unit, dir), REPEAT_EVERY); }, REPEAT_DELAY);
      return;
    }
    const now = new Date();
    switch (t.dataset.act) {
      case "prev": view = view.m === 0 ? { y: view.y - 1, m: 11 } : { y: view.y, m: view.m - 1 }; render(); return;
      case "next": view = view.m === 11 ? { y: view.y + 1, m: 0 } : { y: view.y, m: view.m + 1 }; render(); return;
      case "today":
        date = { y: now.getFullYear(), m: now.getMonth(), d: now.getDate() };
        view = { y: date.y, m: date.m };
        if (kind === "date") { commit(valueOf()); return; }
        render();
        return;
      case "now": time = { h: now.getHours(), min: now.getMinutes() }; render(); return;
      case "am": if (time.h >= 12) time.h -= 12; render(); return;
      case "pm": if (time.h < 12) time.h += 12; render(); return;
      case "clear": commit(""); return;
      case "cancel": close(); return;
      case "done": commit(valueOf()); return;
    }
  }

  // Take the tap before the browser does: no focus, no native picker, no text-field editing.
  document.addEventListener("pointerdown", (e) => {
    if (!isOn() || !pickable(e.target)) return;
    e.preventDefault();
    e.target.blur();
    // Cancelling the press also stops focus leaving the field typed in before (the event's title),
    // which would keep the on-screen keyboard up over the picker. Let it go so the keyboard hides.
    const prev = document.activeElement;
    if (prev && prev !== document.body && prev !== e.target) prev.blur();
    open(e.target);
  }, true);
  document.addEventListener("click", (e) => {
    if (isOn() && pickable(e.target)) e.preventDefault();
  }, true);
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && overlay && overlay.classList.contains("open")) { e.stopPropagation(); close(); }
  }, true);
})();
