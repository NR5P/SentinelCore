// Organizer tab: calendar, reminders, notes and Trello-style boards. All reads and writes go
// through SentinelOrganizerSync (organizer-sync.js), which keeps the local copy and talks to the
// server; this file is only presentation. The same data is edited from the phone, Mac, Windows
// and Android apps, so nothing here may assume it is the only writer - every render reads the
// current copy, and a change from another device simply arrives as another render.
window.SentinelOrganizer = (function () {
  const S = window.SentinelOrganizerSync;
  const PANES = ["calendar", "reminders", "notes", "boards"];
  const WEEKDAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const RRULE_DAYS = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
  const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
  const ALERT_CHOICES = [[null, "none"], [0, "at the time"], [5, "5 min before"], [10, "10 min before"], [15, "15 min before"], [30, "30 min before"], [60, "1 hour before"], [120, "2 hours before"], [1440, "1 day before"]];
  const SPACE_COLORS = ["#39ff9c", "#4be7ff", "#ffb545", "#c792ea", "#ff7eb6", "#82aaff", "#f78c6c"];

  let pane = "calendar";
  let spaceFilter = "all";
  let monthCursor = firstOfMonth(new Date());
  let selectedDay = ymd(new Date());
  let selectedNoteId = null;
  let selectedBoardId = null;
  let noteSearch = "";
  let showCompleted = false;
  let visible = false;
  let renderQueued = false;
  let dragging = null;
  let people = null;

  const el = (id) => document.getElementById(id);

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  let toastTimeout;
  function showToast(message, success) {
    const toast = el("toast");
    clearTimeout(toastTimeout);
    toast.textContent = message;
    toast.className = "toast " + (success ? "success" : "error");
    toastTimeout = setTimeout(() => { toast.className = "toast hidden"; }, 3500);
  }

  // Only http(s), mailto and tel become clickable - a synced link is typed by whoever shares the
  // space, and a javascript: URL in someone else's note must not run in your dashboard.
  function safeUrl(url) {
    const text = String(url || "").trim();
    if (/^(https?:|mailto:|tel:)/i.test(text)) return text;
    if (/^[\w.-]+\.[a-z]{2,}(\/|$)/i.test(text)) return "https://" + text;
    return null;
  }

  // ---- dates --------------------------------------------------------------------------------

  function pad(n) { return String(n).padStart(2, "0"); }
  function ymd(date) { return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`; }
  function hm(date) { return `${pad(date.getHours())}:${pad(date.getMinutes())}`; }
  function firstOfMonth(date) { return new Date(date.getFullYear(), date.getMonth(), 1); }
  function addDays(date, days) { const d = new Date(date); d.setDate(d.getDate() + days); return d; }
  function fromYmd(text) { const [y, m, d] = String(text).slice(0, 10).split("-").map(Number); return new Date(y, (m || 1) - 1, d || 1); }

  // Local time with its offset - "2026-09-23T17:00:00-05:00" - so every other client reads the
  // same instant and a person looking at it knows what zone it was written in.
  function isoLocal(date) {
    const offset = -date.getTimezoneOffset();
    const sign = offset >= 0 ? "+" : "-";
    const abs = Math.abs(offset);
    return `${ymd(date)}T${hm(date)}:00${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
  }

  function when(value, allDay) {
    if (!value) return null;
    return allDay ? fromYmd(value) : new Date(value);
  }

  function timeZone() {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { return undefined; }
  }

  function formatTime(date) {
    return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  }

  function formatDate(date) {
    return date.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" });
  }

  function dueLabel(reminder) {
    const due = when(reminder.fields.due, reminder.fields.allDay);
    if (!due) return "";
    const today = ymd(new Date());
    const day = ymd(due);
    const dayText = day === today ? "today" : day === ymd(addDays(new Date(), 1)) ? "tomorrow" : formatDate(due);
    return reminder.fields.allDay ? dayText : `${dayText} ${formatTime(due)}`;
  }

  // ---- recurrence ---------------------------------------------------------------------------

  function parseRule(text) {
    if (!text) return null;
    const rule = {};
    for (const part of String(text).split(";")) {
      const [key, value] = part.split("=");
      if (key && value) rule[key.toUpperCase()] = value;
    }
    if (!["DAILY", "WEEKLY", "MONTHLY", "YEARLY"].includes(rule.FREQ)) return null;
    rule.interval = Math.max(1, Number(rule.INTERVAL) || 1);
    rule.count = rule.COUNT ? Number(rule.COUNT) : null;
    rule.until = rule.UNTIL ? parseUntil(rule.UNTIL) : null;
    rule.byDay = rule.BYDAY ? rule.BYDAY.split(",").map((d) => RRULE_DAYS.indexOf(d.slice(-2))).filter((d) => d >= 0) : null;
    rule.byMonthDay = rule.BYMONTHDAY ? rule.BYMONTHDAY.split(",").map(Number) : null;
    return rule;
  }

  function parseUntil(text) {
    const m = /^(\d{4})(\d{2})(\d{2})(?:T(\d{2})(\d{2})(\d{2})(Z)?)?$/.exec(text);
    if (!m) return null;
    if (!m[4]) return new Date(+m[1], +m[2] - 1, +m[3], 23, 59, 59);
    return m[7] ? new Date(Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6])) : new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]);
  }

  // Each start of the series, in order, walking local calendar fields rather than adding fixed
  // milliseconds, so a weekly 9am meeting is still at 9am after the clocks change.
  function* ruleStarts(start, rule) {
    let produced = 0;
    const limit = 3000;
    const emit = (date) => {
      if (date < start) return false;
      if (rule.until && date > rule.until) return "stop";
      produced++;
      return true;
    };
    for (let step = 0; step < limit; step++) {
      let candidates;
      if (rule.FREQ === "DAILY") {
        candidates = [addDays(start, step * rule.interval)];
      } else if (rule.FREQ === "WEEKLY") {
        const weekStart = addDays(start, step * 7 * rule.interval - start.getDay());
        const days = rule.byDay && rule.byDay.length ? [...rule.byDay].sort() : [start.getDay()];
        candidates = days.map((day) => {
          const d = addDays(weekStart, day);
          d.setHours(start.getHours(), start.getMinutes(), 0, 0);
          return d;
        });
      } else if (rule.FREQ === "MONTHLY") {
        const month = new Date(start.getFullYear(), start.getMonth() + step * rule.interval, 1, start.getHours(), start.getMinutes());
        const days = rule.byMonthDay || [start.getDate()];
        const last = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate();
        candidates = days
          .map((day) => (day < 0 ? last + day + 1 : day))
          .filter((day) => day >= 1 && day <= last)
          .sort((a, b) => a - b)
          .map((day) => new Date(month.getFullYear(), month.getMonth(), day, start.getHours(), start.getMinutes()));
      } else {
        const year = start.getFullYear() + step * rule.interval;
        const d = new Date(year, start.getMonth(), start.getDate(), start.getHours(), start.getMinutes());
        candidates = d.getMonth() === start.getMonth() ? [d] : [];
      }
      for (const date of candidates) {
        const verdict = emit(date);
        if (verdict === "stop") return;
        if (verdict) {
          yield date;
          if (rule.count && produced >= rule.count) return;
        }
      }
    }
  }

  function occurrenceKey(date, allDay) {
    return allDay ? ymd(date) : String(date.getTime());
  }

  // Every occurrence of an event overlapping [from, to), each with the start it had in the series.
  function occurrences(event, from, to) {
    const allDay = !!event.fields.allDay;
    const start = when(event.fields.start, allDay);
    if (!start || isNaN(start)) return [];
    let end = when(event.fields.end, allDay);
    if (!end || isNaN(end) || end <= start) end = allDay ? addDays(start, 1) : new Date(start.getTime() + 3600000);
    const duration = end - start;
    const rule = parseRule(event.fields.recurrence);
    if (!rule) return start < to && end > from ? [{ event, start, end, original: null }] : [];

    const skipped = new Set((event.fields.exdates || []).map((x) => {
      const d = when(x, allDay);
      return d && !isNaN(d) ? occurrenceKey(d, allDay) : x;
    }));
    const out = [];
    for (const occurrenceStart of ruleStarts(start, rule)) {
      if (occurrenceStart >= to) break;
      const occurrenceEnd = new Date(occurrenceStart.getTime() + duration);
      if (occurrenceEnd <= from || skipped.has(occurrenceKey(occurrenceStart, allDay))) continue;
      out.push({ event, start: occurrenceStart, end: occurrenceEnd, original: allDay ? ymd(occurrenceStart) : isoLocal(occurrenceStart) });
    }
    return out;
  }

  // The next due date after completing a repeating reminder.
  function nextDue(reminder) {
    const allDay = !!reminder.fields.allDay;
    const due = when(reminder.fields.due, allDay);
    const rule = parseRule(reminder.fields.recurrence);
    if (!due || !rule) return null;
    // The occurrence after the one just done - the same as Apple Reminders - rather than the
    // next one after today, so a weekly chore ticked off late doesn't silently skip a week.
    for (const date of ruleStarts(due, rule)) {
      if (date > due) return allDay ? ymd(date) : isoLocal(date);
    }
    return null;
  }

  // ---- spaces -------------------------------------------------------------------------------

  function spaces() {
    const list = S.spaces();
    return list.length ? list : [{ id: "family", kind: "Family", name: "Family", members: [] }];
  }

  function spaceColor(spaceId) {
    const list = spaces();
    const space = list.find((s) => s.id === spaceId);
    if (space && space.color) return space.color;
    const index = Math.max(0, list.findIndex((s) => s.id === spaceId));
    return SPACE_COLORS[index % SPACE_COLORS.length];
  }

  function spaceName(spaceId) {
    const space = spaces().find((s) => s.id === spaceId);
    return space ? space.name : "";
  }

  function defaultSpace() {
    if (spaceFilter !== "all") return spaceFilter;
    const personal = spaces().find((s) => s.kind === "Personal");
    return personal ? personal.id : "family";
  }

  function inFilter(item) {
    return spaceFilter === "all" || item.spaceId === spaceFilter;
  }

  function spaceOptions(selected) {
    return spaces().map((s) => `<option value="${escapeHtml(s.id)}"${s.id === selected ? " selected" : ""}>${escapeHtml(s.name)}</option>`).join("");
  }

  function byPosition(a, b) {
    const pa = a.fields.position ?? Infinity;
    const pb = b.fields.position ?? Infinity;
    if (pa !== pb) return pa - pb;
    return String(a.createdAt).localeCompare(String(b.createdAt));
  }

  // ---- markdown -----------------------------------------------------------------------------

  // Deliberately small: headings, lists, checklists, quotes, code, bold/italic and links. Text is
  // escaped first and only these constructs are turned back into markup, so nothing a synced
  // note contains can inject HTML.
  function renderMarkdown(source) {
    const lines = String(source || "").split("\n");
    const out = [];
    let list = null;
    let inCode = false;
    const closeList = () => { if (list) { out.push(`</${list}>`); list = null; } };
    const inline = (text) => escapeHtml(text)
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/(^|[^*])\*([^*]+)\*/g, "$1<em>$2</em>")
      .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (match, label, url) => {
        const safe = safeUrl(url.replace(/&amp;/g, "&"));
        return safe ? `<a href="${escapeHtml(safe)}" target="_blank" rel="noopener noreferrer">${label}</a>` : match;
      })
      .replace(/(^|\s)(https?:\/\/[^\s<]+)/g, (match, lead, url) => `${lead}<a href="${url}" target="_blank" rel="noopener noreferrer">${url}</a>`);

    lines.forEach((line, index) => {
      if (line.trim().startsWith("```")) {
        closeList();
        out.push(inCode ? "</code></pre>" : "<pre><code>");
        inCode = !inCode;
        return;
      }
      if (inCode) { out.push(escapeHtml(line) + "\n"); return; }
      let m;
      if ((m = /^(#{1,3})\s+(.*)$/.exec(line))) {
        closeList();
        out.push(`<h${m[1].length + 2}>${inline(m[2])}</h${m[1].length + 2}>`);
      } else if ((m = /^\s*[-*]\s+\[( |x|X)\]\s+(.*)$/.exec(line))) {
        if (list !== "ul") { closeList(); out.push('<ul class="org-md-checklist">'); list = "ul"; }
        const done = m[1] !== " ";
        out.push(`<li><label><input type="checkbox" data-md-line="${index}"${done ? " checked" : ""}> <span${done ? ' class="org-done"' : ""}>${inline(m[2])}</span></label></li>`);
      } else if ((m = /^\s*[-*]\s+(.*)$/.exec(line))) {
        if (list !== "ul") { closeList(); out.push("<ul>"); list = "ul"; }
        out.push(`<li>${inline(m[1])}</li>`);
      } else if ((m = /^\s*\d+[.)]\s+(.*)$/.exec(line))) {
        if (list !== "ol") { closeList(); out.push("<ol>"); list = "ol"; }
        out.push(`<li>${inline(m[1])}</li>`);
      } else if ((m = /^>\s?(.*)$/.exec(line))) {
        closeList();
        out.push(`<blockquote>${inline(m[1])}</blockquote>`);
      } else if (!line.trim()) {
        closeList();
      } else {
        closeList();
        out.push(`<p>${inline(line)}</p>`);
      }
    });
    closeList();
    if (inCode) out.push("</code></pre>");
    return out.join("");
  }

  function toggleChecklistLine(source, lineIndex) {
    const lines = String(source || "").split("\n");
    const line = lines[lineIndex];
    if (line === undefined) return source;
    lines[lineIndex] = line.replace(/\[( |x|X)\]/, (m, mark) => (mark === " " ? "[x]" : "[ ]"));
    return lines.join("\n");
  }

  // ---- shell --------------------------------------------------------------------------------

  async function init() {
    for (const button of document.querySelectorAll("#org-subtabs [data-org-pane]")) {
      button.addEventListener("click", () => switchPane(button.dataset.orgPane));
    }
    el("org-groups-btn").addEventListener("click", openGroups);
    el("org-subscribe-btn").addEventListener("click", openSubscribe);
    el("org-modal").addEventListener("mousedown", (event) => { if (event.target === el("org-modal")) closeModal(); });
    document.addEventListener("keydown", (event) => { if (event.key === "Escape" && !el("org-modal").classList.contains("hidden")) closeModal(); });

    S.subscribe(() => { if (visible) queueRender(); });
    S.onServerReset(confirmReset);

    const status = await S.refreshStatus();
    el("tab-organizer").classList.toggle("hidden", !(status && status.enabled));
  }

  function show() {
    visible = true;
    S.start();
    render();
  }

  function hide() {
    visible = false;
  }

  function switchPane(name) {
    pane = name;
    for (const button of document.querySelectorAll("#org-subtabs [data-org-pane]")) {
      button.classList.toggle("active", button.dataset.orgPane === name);
    }
    flushNoteSave();
    // Hidden panes are emptied, not just hidden: editors reuse element ids, and a note editor
    // left in a hidden pane would otherwise answer for the links box of an event being edited.
    for (const p of PANES) {
      el(`org-pane-${p}`).classList.toggle("hidden", p !== name);
      if (p !== name) el(`org-pane-${p}`).innerHTML = "";
    }
    render();
  }

  function queueRender() {
    if (renderQueued) return;
    renderQueued = true;
    requestAnimationFrame(() => {
      renderQueued = false;
      render();
    });
  }

  function render() {
    if (!visible) return;
    renderSyncState();
    renderSpaceChips();
    if (dragging) return;
    if (pane === "calendar") renderCalendar();
    else if (pane === "reminders") renderReminders();
    else if (pane === "notes") renderNotes();
    else renderBoards();
  }

  function renderSyncState() {
    const state = S.state();
    const target = el("org-sync-state");
    let text;
    if (!state.online) text = state.waitingChanges ? `offline - ${state.waitingChanges} change${state.waitingChanges === 1 ? "" : "s"} waiting` : "offline - showing saved copy";
    else if (state.syncing) text = "syncing…";
    else if (state.waitingChanges) text = `${state.waitingChanges} change${state.waitingChanges === 1 ? "" : "s"} to send`;
    else text = state.lastSyncedAt ? "synced" : "connecting…";
    target.textContent = text;
    target.classList.toggle("org-sync-offline", !state.online);
    if (state.lastError && state.online) {
      showToast(state.lastError, false);
      S.clearError();
    }
  }

  function renderSpaceChips() {
    const root = el("org-spaces");
    const list = spaces();
    if (spaceFilter !== "all" && !list.some((s) => s.id === spaceFilter)) spaceFilter = "all";
    const chips = [{ id: "all", name: "everything" }, ...list];
    root.innerHTML = chips.map((s) => `
      <button class="org-chip${s.id === spaceFilter ? " active" : ""}" data-space="${escapeHtml(s.id)}">
        ${s.id === "all" ? "" : `<span class="org-dot" style="background:${escapeHtml(spaceColor(s.id))}"></span>`}${escapeHtml(s.name)}
      </button>`).join("");
    for (const chip of root.querySelectorAll("[data-space]")) {
      chip.addEventListener("click", () => {
        spaceFilter = chip.dataset.space;
        render();
      });
    }
  }

  // ---- calendar -----------------------------------------------------------------------------

  // One color per holiday calendar, the same in every app.
  const HOLIDAY_COLORS = {
    "us-federal": "#4a9dff", "us-observances": "#93a2ad", christian: "#c98bff", orthodox: "#ff9a52",
    jewish: "#4ae8ff", islamic: "#7fd67f", hindu: "#ffd84a", chinese: "#ff4d5e"
  };

  function holidaySetName(setId) {
    const set = (S.holidayCatalog() || []).find((candidate) => candidate.id === setId);
    return set ? set.name : "holiday";
  }

  function entriesBetween(from, to) {
    const entries = [];
    // Holidays belong to no space, so they show whichever space is picked.
    for (const holiday of S.holidays()) {
      const start = fromYmd(holiday.date);
      const end = addDays(start, holiday.days || 1);
      if (start < to && end > from) entries.push({ type: "holiday", holiday, start, end });
    }
    for (const event of S.all("event").filter(inFilter)) {
      for (const occurrence of occurrences(event, from, to)) entries.push({ type: "event", ...occurrence });
    }
    for (const reminder of S.all("reminder").filter(inFilter)) {
      if (reminder.fields.completed || !reminder.fields.due || reminder.parentId) continue;
      const due = when(reminder.fields.due, reminder.fields.allDay);
      if (due && due >= from && due < to) entries.push({ type: "reminder", reminder, start: due, end: due });
    }
    return entries.sort((a, b) => a.start - b.start);
  }

  function renderCalendar() {
    const root = el("org-pane-calendar");
    const gridStart = addDays(monthCursor, -monthCursor.getDay());
    const gridEnd = addDays(gridStart, 42);
    const entries = entriesBetween(gridStart, gridEnd);
    const byDay = new Map();
    for (const entry of entries) {
      // A multi-day event or holiday shows on every day it covers.
      let day = new Date(entry.start.getFullYear(), entry.start.getMonth(), entry.start.getDate());
      const last = entry.type === "reminder" ? entry.start : new Date(entry.end.getTime() - 1);
      while (day <= last || ymd(day) === ymd(entry.start)) {
        const key = ymd(day);
        if (!byDay.has(key)) byDay.set(key, []);
        byDay.get(key).push(entry);
        day = addDays(day, 1);
        if (day > last) break;
      }
    }

    const today = ymd(new Date());
    let cells = "";
    for (let i = 0; i < 42; i++) {
      const date = addDays(gridStart, i);
      const key = ymd(date);
      const dayEntries = byDay.get(key) || [];
      const shown = dayEntries.slice(0, 3).map((entry) => entryChip(entry, true)).join("");
      const more = dayEntries.length > 3 ? `<div class="org-more">+${dayEntries.length - 3} more</div>` : "";
      cells += `
        <div class="org-day${date.getMonth() !== monthCursor.getMonth() ? " org-day-out" : ""}${key === today ? " org-today" : ""}${key === selectedDay ? " org-day-selected" : ""}" data-day="${key}">
          <div class="org-day-number">${date.getDate()}</div>
          ${shown}${more}
        </div>`;
    }

    const dayEntries = byDay.get(selectedDay) || entriesBetween(fromYmd(selectedDay), addDays(fromYmd(selectedDay), 1));
    root.innerHTML = `
      <div class="org-cal-head">
        <button class="btn btn-small" id="org-cal-prev" aria-label="previous month">&lsaquo;</button>
        <span class="org-cal-title">${MONTHS[monthCursor.getMonth()]} ${monthCursor.getFullYear()}</span>
        <button class="btn btn-small" id="org-cal-next" aria-label="next month">&rsaquo;</button>
        <button class="btn btn-small" id="org-cal-today">today</button>
        <span class="org-spacer"></span>
        <button class="btn btn-small" id="org-cal-holidays" title="Show holidays from other calendars">holidays${S.holidaySets().length ? ` (${S.holidaySets().length})` : ""}</button>
        <button class="btn" id="org-cal-add">+ event</button>
      </div>
      <div class="org-cal-grid">
        ${WEEKDAYS.map((d) => `<div class="org-weekday">${d}</div>`).join("")}
        ${cells}
      </div>
      <div class="org-day-detail">
        <div class="org-day-detail-head">
          <span>${escapeHtml(formatDate(fromYmd(selectedDay)))}</span>
          <button class="btn btn-small" id="org-day-add">+ event on this day</button>
        </div>
        ${dayEntries.length ? dayEntries.map((entry) => entryRow(entry)).join("") : '<div class="org-empty">nothing on this day</div>'}
      </div>`;

    el("org-cal-prev").addEventListener("click", () => { monthCursor = new Date(monthCursor.getFullYear(), monthCursor.getMonth() - 1, 1); renderCalendar(); });
    el("org-cal-next").addEventListener("click", () => { monthCursor = new Date(monthCursor.getFullYear(), monthCursor.getMonth() + 1, 1); renderCalendar(); });
    el("org-cal-today").addEventListener("click", () => { monthCursor = firstOfMonth(new Date()); selectedDay = ymd(new Date()); renderCalendar(); });
    el("org-cal-add").addEventListener("click", () => openEventEditor(null, { day: selectedDay }));
    el("org-cal-holidays").addEventListener("click", openHolidaySettings);
    el("org-day-add").addEventListener("click", () => openEventEditor(null, { day: selectedDay }));
    for (const cell of root.querySelectorAll("[data-day]")) {
      cell.addEventListener("click", (event) => {
        if (event.target.closest("[data-entry]")) return;
        selectedDay = cell.dataset.day;
        renderCalendar();
      });
      cell.addEventListener("dblclick", (event) => {
        if (event.target.closest("[data-entry]")) return;
        openEventEditor(null, { day: cell.dataset.day });
      });
    }
    bindEntryClicks(root, entries);
  }

  function entryChip(entry) {
    if (entry.type === "holiday") {
      const h = entry.holiday;
      return `<div class="org-entry org-entry-holiday" style="border-left-color:${escapeHtml(HOLIDAY_COLORS[h.setId] || "#93a2ad")}" data-entry="h:${escapeHtml(h.id)}" title="${escapeHtml(h.name)}">${escapeHtml(h.name)}</div>`;
    }
    if (entry.type === "reminder") {
      return `<div class="org-entry org-entry-reminder" data-entry="r:${escapeHtml(entry.reminder.id)}" title="${escapeHtml(entry.reminder.fields.title)}">&#9744; ${escapeHtml(entry.reminder.fields.title || "reminder")}</div>`;
    }
    const color = entry.event.fields.color || spaceColor(entry.event.spaceId);
    const time = entry.event.fields.allDay ? "" : `<span class="org-entry-time">${escapeHtml(formatTime(entry.start))}</span> `;
    return `<div class="org-entry" style="border-left-color:${escapeHtml(color)}" data-entry="e:${escapeHtml(entry.event.id)}:${escapeHtml(entry.original || "")}" title="${escapeHtml(entry.event.fields.title)}">${time}${escapeHtml(entry.event.fields.title || "untitled")}</div>`;
  }

  function entryRow(entry) {
    if (entry.type === "holiday") {
      const h = entry.holiday;
      return `<div class="org-row org-row-holiday" data-entry="h:${escapeHtml(h.id)}">
        <span class="org-row-time">holiday</span>
        <span class="org-row-title" style="border-left-color:${escapeHtml(HOLIDAY_COLORS[h.setId] || "#93a2ad")}">${escapeHtml(h.name)}
          ${h.note ? `<span class="org-row-sub">${escapeHtml(h.note)}</span>` : ""}
        </span>
        <span class="org-tag" style="color:${escapeHtml(HOLIDAY_COLORS[h.setId] || "#93a2ad")}">${escapeHtml(holidaySetName(h.setId))}</span>
      </div>`;
    }
    if (entry.type === "reminder") {
      const r = entry.reminder;
      return `<div class="org-row" data-entry="r:${escapeHtml(r.id)}">
        <span class="org-row-time">${r.fields.allDay ? "all day" : escapeHtml(formatTime(entry.start))}</span>
        <span class="org-row-title">&#9744; ${escapeHtml(r.fields.title || "reminder")}</span>
        <span class="org-tag" style="color:${escapeHtml(spaceColor(r.spaceId))}">${escapeHtml(spaceName(r.spaceId))}</span>
      </div>`;
    }
    const e = entry.event;
    const time = e.fields.allDay ? "all day" : `${formatTime(entry.start)} - ${formatTime(entry.end)}`;
    const attachments = S.children(e.id).filter((c) => c.kind === "attachment").length;
    return `<div class="org-row" data-entry="e:${escapeHtml(e.id)}:${escapeHtml(entry.original || "")}">
      <span class="org-row-time">${escapeHtml(time)}</span>
      <span class="org-row-title" style="border-left-color:${escapeHtml(e.fields.color || spaceColor(e.spaceId))}">${escapeHtml(e.fields.title || "untitled")}
        ${e.fields.location ? `<span class="org-row-sub">${escapeHtml(e.fields.location)}</span>` : ""}
        ${e.fields.recurrence ? '<span class="org-row-sub">&#8635; repeats</span>' : ""}
        ${attachments ? `<span class="org-row-sub">&#128206; ${attachments}</span>` : ""}
      </span>
      <span class="org-tag" style="color:${escapeHtml(spaceColor(e.spaceId))}">${escapeHtml(spaceName(e.spaceId))}</span>
    </div>`;
  }

  function bindEntryClicks(root) {
    for (const node of root.querySelectorAll("[data-entry]")) {
      node.addEventListener("click", (event) => {
        event.stopPropagation();
        const [type, id, ...rest] = node.dataset.entry.split(":");
        if (type === "h") openHolidayInfo([id, ...rest].join(":"));
        else if (type === "r") openReminderEditor(S.get(id));
        else openEventEditor(S.get(id), { original: rest.join(":") || null });
      });
    }
  }

  // ---- holiday calendars ----------------------------------------------------------------------

  function openHolidayInfo(id) {
    const h = S.holidays().find((candidate) => candidate.id === id);
    if (!h) return;
    const start = fromYmd(h.date);
    const days = h.days || 1;
    const dates = days > 1 ? `${formatDate(start)} - ${formatDate(addDays(start, days - 1))} (${days} days)` : formatDate(start);
    openModal(`
      <div class="zone-editor-header"><span>${escapeHtml(h.name.toUpperCase())}</span></div>
      <div class="wizard-body">
        <p>${escapeHtml(dates)}</p>
        ${h.note ? `<p>${escapeHtml(h.note)}</p>` : ""}
        <p class="event-count">From ${escapeHtml(holidaySetName(h.setId))}. Holidays can't be edited - turn the whole calendar off in holiday settings if you don't want it.</p>
      </div>
      <div class="zone-editor-footer">
        <button class="btn" id="org-holiday-settings">holiday settings</button>
        <button class="btn" id="org-close">close</button>
      </div>`);
    el("org-close").addEventListener("click", closeModal);
    el("org-holiday-settings").addEventListener("click", openHolidaySettings);
  }

  function openHolidaySettings() {
    const catalog = S.holidayCatalog();
    const chosen = new Set(S.holidaySets());
    openModal(`
      <div class="zone-editor-header"><span>HOLIDAYS</span></div>
      <div class="wizard-body">
        <p>Add holidays to the calendar. They follow your account, so they show in the apps too, and in the calendar subscription link.</p>
        ${catalog ? catalog.map((set) => `
          <label class="org-holiday-set">
            <input type="checkbox" data-holiday-set="${escapeHtml(set.id)}"${chosen.has(set.id) ? " checked" : ""} />
            <span class="org-dot" style="background:${escapeHtml(HOLIDAY_COLORS[set.id] || "#93a2ad")}"></span>
            <span><strong>${escapeHtml(set.name)}</strong><br /><span class="event-count">${escapeHtml(set.description)}</span></span>
          </label>`).join("") : "<p>Connect to the server once to see the holiday calendars.</p>"}
      </div>
      <div class="zone-editor-footer"><button class="btn" id="org-close">done</button></div>`);
    el("org-close").addEventListener("click", closeModal);
    if (!catalog) S.refreshHolidays().then(() => { if (S.holidayCatalog()) openHolidaySettings(); });
    for (const box of document.querySelectorAll("[data-holiday-set]")) {
      box.addEventListener("change", () => {
        const sets = [...document.querySelectorAll("[data-holiday-set]")].filter((b) => b.checked).map((b) => b.dataset.holidaySet);
        S.setHolidaySets(sets);
      });
    }
  }

  // ---- event editor -------------------------------------------------------------------------

  function repeatValue(rule) {
    if (!rule) return "";
    const parsed = parseRule(rule);
    if (!parsed) return "custom";
    const simple = `FREQ=${parsed.FREQ}`;
    return rule.split(";").filter((p) => !p.startsWith("UNTIL=")).join(";") === simple ? parsed.FREQ : "custom";
  }

  function untilValue(rule) {
    const parsed = parseRule(rule);
    return parsed && parsed.until ? ymd(parsed.until) : "";
  }

  function openEventEditor(event, context) {
    const isNew = !event;
    const f = event ? event.fields : {};
    const occurrence = context && context.original && event && event.fields.recurrence ? context.original : null;
    const allDay = isNew ? false : !!f.allDay;

    // Opened from one occurrence of a repeating event: show that occurrence's date.
    let start = isNew
      ? new Date(fromYmd(context && context.day ? context.day : ymd(new Date())).setHours(new Date().getHours() + 1, 0, 0, 0))
      : when(f.start, allDay);
    let end = isNew ? new Date(start.getTime() + 3600000) : when(f.end, allDay) || new Date(start.getTime() + (allDay ? 86400000 : 3600000));
    if (occurrence) {
      const occurrenceStart = when(occurrence, allDay);
      end = new Date(occurrenceStart.getTime() + (end - start));
      start = occurrenceStart;
    }
    // All-day ends are exclusive on the wire; the form shows the last day.
    const endShown = allDay ? addDays(end, -1) : end;
    const repeat = repeatValue(f.recurrence);
    const alert = (f.alerts || [])[0];

    openModal(`
      <div class="zone-editor-header"><span>${isNew ? "NEW EVENT" : "EVENT"}</span>${occurrence ? '<span class="event-count">one of a repeating series</span>' : ""}</div>
      <div class="org-form">
        <input class="field-input org-title-input" id="ev-title" placeholder="title" value="${escapeHtml(f.title || "")}" />
        <label class="org-check"><input type="checkbox" id="ev-allday"${allDay ? " checked" : ""}> all day</label>
        <div class="org-form-row">
          <label>starts</label>
          <input type="date" class="field-input" id="ev-start-date" value="${ymd(start)}" />
          <input type="time" class="field-input org-time" id="ev-start-time" value="${hm(start)}" />
        </div>
        <div class="org-form-row">
          <label>ends</label>
          <input type="date" class="field-input" id="ev-end-date" value="${ymd(endShown)}" />
          <input type="time" class="field-input org-time" id="ev-end-time" value="${hm(end)}" />
        </div>
        <div class="org-form-row">
          <label>repeats</label>
          <select class="field-input" id="ev-repeat">
            <option value="">never</option>
            <option value="DAILY"${repeat === "DAILY" ? " selected" : ""}>every day</option>
            <option value="WEEKLY"${repeat === "WEEKLY" ? " selected" : ""}>every week</option>
            <option value="MONTHLY"${repeat === "MONTHLY" ? " selected" : ""}>every month</option>
            <option value="YEARLY"${repeat === "YEARLY" ? " selected" : ""}>every year</option>
            ${repeat === "custom" ? '<option value="custom" selected>custom (set in another app)</option>' : ""}
          </select>
          <label class="org-inline-label">until</label>
          <input type="date" class="field-input" id="ev-until" value="${untilValue(f.recurrence)}" />
        </div>
        <div class="org-form-row">
          <label>alert</label>
          <select class="field-input" id="ev-alert">${ALERT_CHOICES.map(([v, t]) => `<option value="${v ?? ""}"${(alert ?? null) === v ? " selected" : ""}>${t}</option>`).join("")}</select>
          <label class="org-inline-label">in</label>
          <select class="field-input" id="ev-space"${occurrence ? " disabled" : ""}>${spaceOptions(isNew ? defaultSpace() : event.spaceId)}</select>
        </div>
        <input class="field-input" id="ev-location" placeholder="location" value="${escapeHtml(f.location || "")}" />
        <textarea class="field-textarea" id="ev-notes" rows="4" placeholder="notes">${escapeHtml(f.notes || "")}</textarea>
        ${linksEditor(f.links)}
        <div id="ev-attachments"></div>
      </div>
      <div class="zone-editor-footer">
        <button class="btn" id="ev-save">save</button>
        <button class="btn" id="ev-cancel">cancel</button>
        ${isNew ? "" : '<span class="org-spacer"></span><button class="btn org-danger" id="ev-delete">delete</button>'}
      </div>`);

    const syncAllDay = () => {
      const on = el("ev-allday").checked;
      el("ev-start-time").classList.toggle("hidden", on);
      el("ev-end-time").classList.toggle("hidden", on);
    };
    el("ev-allday").addEventListener("change", syncAllDay);
    syncAllDay();
    bindLinksEditor();

    let saved = event;
    const collect = () => {
      const isAllDay = el("ev-allday").checked;
      const title = el("ev-title").value.trim() || "untitled";
      let startValue;
      let endValue;
      if (isAllDay) {
        startValue = el("ev-start-date").value;
        const endDay = el("ev-end-date").value && el("ev-end-date").value >= startValue ? el("ev-end-date").value : startValue;
        endValue = ymd(addDays(fromYmd(endDay), 1));
      } else {
        const s = new Date(`${el("ev-start-date").value}T${el("ev-start-time").value || "09:00"}`);
        let e = new Date(`${el("ev-end-date").value || el("ev-start-date").value}T${el("ev-end-time").value || el("ev-start-time").value}`);
        if (isNaN(e) || e <= s) e = new Date(s.getTime() + 3600000);
        startValue = isoLocal(s);
        endValue = isoLocal(e);
      }
      const repeatChoice = el("ev-repeat").value;
      let recurrence = null;
      if (repeatChoice === "custom") recurrence = f.recurrence;
      else if (repeatChoice) {
        recurrence = `FREQ=${repeatChoice}`;
        if (el("ev-until").value) recurrence += `;UNTIL=${el("ev-until").value.replace(/-/g, "")}`;
      }
      const alertChoice = el("ev-alert").value;
      return {
        title,
        allDay: isAllDay,
        start: startValue,
        end: endValue,
        timeZone: timeZone(),
        recurrence,
        location: el("ev-location").value.trim() || null,
        notes: el("ev-notes").value || null,
        alerts: alertChoice === "" ? null : [Number(alertChoice)],
        links: collectLinks()
      };
    };

    // Attachments need an item to hang off, so a brand-new event is saved the moment the first
    // file is added.
    const ensureSaved = () => {
      if (!saved) saved = S.create("event", collect(), { spaceId: el("ev-space").value });
      return saved.id;
    };
    renderAttachments(el("ev-attachments"), () => (saved ? saved.id : null), ensureSaved);

    el("ev-cancel").addEventListener("click", closeModal);
    el("ev-save").addEventListener("click", async () => {
      const fields = collect();
      if (!saved) {
        S.create("event", fields, { spaceId: el("ev-space").value });
      } else if (occurrence) {
        const scope = await askScope("Change which events?");
        if (!scope) return;
        if (scope === "one") {
          S.update(saved.id, { exdates: [...(saved.fields.exdates || []), occurrence] });
          S.create("event", { ...fields, recurrence: null, seriesId: saved.id, originalStart: occurrence }, { spaceId: saved.spaceId });
        } else {
          // Every occurrence: keep the series' own first date and apply the new time of day and
          // length to it, rather than moving the whole series to the day that was clicked.
          const seriesStart = when(saved.fields.start, saved.fields.allDay);
          const newStart = when(fields.start, fields.allDay);
          const newEnd = when(fields.end, fields.allDay);
          const shifted = new Date(seriesStart);
          if (!fields.allDay) shifted.setHours(newStart.getHours(), newStart.getMinutes(), 0, 0);
          const shiftedEnd = new Date(shifted.getTime() + (newEnd - newStart));
          S.update(saved.id, {
            ...fields,
            start: fields.allDay ? ymd(shifted) : isoLocal(shifted),
            end: fields.allDay ? ymd(shiftedEnd) : isoLocal(shiftedEnd)
          });
        }
      } else {
        const changes = changedFields(saved.fields, fields);
        if (el("ev-space").value !== saved.spaceId) changes.spaceId = el("ev-space").value;
        if (Object.keys(changes).length) S.update(saved.id, changes);
      }
      selectedDay = fields.allDay ? fields.start : ymd(new Date(fields.start));
      closeModal();
    });
    if (!isNew) {
      el("ev-delete").addEventListener("click", async () => {
        if (occurrence) {
          const scope = await askScope("Delete which events?");
          if (!scope) return;
          if (scope === "one") S.update(event.id, { exdates: [...(event.fields.exdates || []), occurrence] });
          else deleteSeries(event);
        } else if (event.fields.recurrence) {
          if (!confirm("Delete every occurrence of this repeating event?")) return;
          deleteSeries(event);
        } else {
          S.remove(event.id);
        }
        closeModal();
      });
    }
    el("ev-title").focus();
  }

  function deleteSeries(event) {
    S.remove(event.id);
    for (const detached of S.all("event").filter((e) => e.fields.seriesId === event.id)) S.remove(detached.id);
  }

  function changedFields(before, after) {
    const changes = {};
    for (const [name, value] of Object.entries(after)) {
      if (JSON.stringify(before[name] ?? null) !== JSON.stringify(value ?? null)) changes[name] = value;
    }
    return changes;
  }

  function askScope(question) {
    return new Promise((resolve) => {
      const layer = document.createElement("div");
      layer.className = "org-scope";
      layer.innerHTML = `<div class="org-scope-box"><p>${escapeHtml(question)}</p>
        <button class="btn" data-scope="one">only this one</button>
        <button class="btn" data-scope="all">all of them</button>
        <button class="btn" data-scope="">cancel</button></div>`;
      el("org-modal-panel").appendChild(layer);
      for (const button of layer.querySelectorAll("[data-scope]")) {
        button.addEventListener("click", () => {
          layer.remove();
          resolve(button.dataset.scope || null);
        });
      }
    });
  }

  // ---- reminders ----------------------------------------------------------------------------

  function renderReminders() {
    const root = el("org-pane-reminders");
    const all = S.all("reminder").filter(inFilter).filter((r) => !r.parentId);
    const now = new Date();
    const today = ymd(now);
    const groups = { overdue: [], today: [], upcoming: [], someday: [], done: [] };
    for (const r of all) {
      if (r.fields.completed) { groups.done.push(r); continue; }
      const due = when(r.fields.due, r.fields.allDay);
      if (!due) groups.someday.push(r);
      else if (r.fields.allDay ? ymd(due) < today : due < now) groups.overdue.push(r);
      else if (ymd(due) === today) groups.today.push(r);
      else groups.upcoming.push(r);
    }
    const byDue = (a, b) => (when(a.fields.due, a.fields.allDay) || 0) - (when(b.fields.due, b.fields.allDay) || 0) || (b.fields.priority || 0) - (a.fields.priority || 0);
    groups.overdue.sort(byDue); groups.today.sort(byDue); groups.upcoming.sort(byDue);
    groups.someday.sort(byPosition);
    groups.done.sort((a, b) => String(b.fields.completedAt).localeCompare(String(a.fields.completedAt)));

    const section = (title, list, extraClass) => list.length ? `
      <div class="org-rem-section ${extraClass || ""}">
        <div class="org-rem-heading">${title} <span class="event-count">${list.length}</span></div>
        ${list.map(reminderRow).join("")}
      </div>` : "";

    root.innerHTML = `
      <div class="org-quick-add">
        <input class="field-input" id="rem-new-title" placeholder="add a reminder and press enter" />
        <input type="date" class="field-input" id="rem-new-date" title="due date (optional)" />
        <input type="time" class="field-input org-time" id="rem-new-time" title="time (optional)" />
        <button class="btn" id="rem-new-add">add</button>
      </div>
      ${section("OVERDUE", groups.overdue, "org-rem-overdue")}
      ${section("TODAY", groups.today)}
      ${section("UPCOMING", groups.upcoming)}
      ${section("NO DATE", groups.someday)}
      ${all.length === groups.done.length ? '<div class="org-empty">nothing to do</div>' : ""}
      ${groups.done.length ? `<button class="btn btn-small" id="rem-toggle-done">${showCompleted ? "hide" : "show"} completed (${groups.done.length})</button>` : ""}
      ${showCompleted ? section("COMPLETED", groups.done.slice(0, 200), "org-rem-done") : ""}`;

    const add = () => {
      const title = el("rem-new-title").value.trim();
      if (!title) return;
      const date = el("rem-new-date").value;
      const time = el("rem-new-time").value;
      const fields = { title, completed: false };
      if (date) {
        fields.allDay = !time;
        fields.due = time ? isoLocal(new Date(`${date}T${time}`)) : date;
      }
      S.create("reminder", fields, { spaceId: defaultSpace() });
      el("rem-new-title").value = "";
      el("rem-new-title").focus();
    };
    el("rem-new-add").addEventListener("click", add);
    el("rem-new-title").addEventListener("keydown", (event) => { if (event.key === "Enter") add(); });
    if (el("rem-toggle-done")) el("rem-toggle-done").addEventListener("click", () => { showCompleted = !showCompleted; renderReminders(); });

    for (const box of root.querySelectorAll("[data-complete]")) {
      box.addEventListener("change", () => completeReminder(S.get(box.dataset.complete), box.checked));
    }
    for (const row of root.querySelectorAll("[data-open-reminder]")) {
      row.addEventListener("click", () => openReminderEditor(S.get(row.dataset.openReminder)));
    }
  }

  function reminderRow(r) {
    const overdue = !r.fields.completed && r.fields.due && (r.fields.allDay ? r.fields.due < ymd(new Date()) : new Date(r.fields.due) < new Date());
    const subtasks = S.children(r.id).filter((c) => c.kind === "reminder");
    const doneSubtasks = subtasks.filter((c) => c.fields.completed).length;
    return `<div class="org-rem">
      <input type="checkbox" data-complete="${escapeHtml(r.id)}"${r.fields.completed ? " checked" : ""} aria-label="complete" />
      <div class="org-rem-main" data-open-reminder="${escapeHtml(r.id)}">
        <div class="org-rem-title${r.fields.completed ? " org-done" : ""}">${"!".repeat(r.fields.priority || 0) ? `<span class="org-priority">${"!".repeat(r.fields.priority)}</span> ` : ""}${escapeHtml(r.fields.title || "reminder")}${r.fields.flagged ? ' <span class="org-flag">&#9873;</span>' : ""}</div>
        <div class="org-rem-meta">
          ${r.fields.due ? `<span class="${overdue ? "org-overdue" : ""}">${escapeHtml(dueLabel(r))}</span>` : ""}
          ${r.fields.recurrence ? "<span>&#8635;</span>" : ""}
          ${subtasks.length ? `<span>${doneSubtasks}/${subtasks.length} steps</span>` : ""}
          ${r.fields.notes ? `<span class="org-rem-notes">${escapeHtml(r.fields.notes.split("\n")[0])}</span>` : ""}
          <span class="org-tag" style="color:${escapeHtml(spaceColor(r.spaceId))}">${escapeHtml(spaceName(r.spaceId))}</span>
        </div>
      </div>
    </div>`;
  }

  function completeReminder(reminder, done) {
    if (!reminder) return;
    if (done && reminder.fields.recurrence) {
      const next = nextDue(reminder);
      if (next) {
        S.update(reminder.id, { due: next, completed: false, lastCompletedAt: new Date().toISOString() });
        showToast(`Next one: ${dueLabel({ fields: { ...reminder.fields, due: next } })}`, true);
        return;
      }
    }
    S.update(reminder.id, { completed: done, completedAt: done ? new Date().toISOString() : null });
  }

  function openReminderEditor(reminder, parentId) {
    const isNew = !reminder;
    const f = reminder ? reminder.fields : {};
    const due = when(f.due, f.allDay);
    const repeat = repeatValue(f.recurrence);
    const alert = (f.alerts || [])[0];
    const subtasks = reminder ? S.children(reminder.id).filter((c) => c.kind === "reminder").sort(byPosition) : [];

    openModal(`
      <div class="zone-editor-header"><span>${isNew ? "NEW REMINDER" : "REMINDER"}</span></div>
      <div class="org-form">
        <input class="field-input org-title-input" id="rem-title" placeholder="title" value="${escapeHtml(f.title || "")}" />
        <div class="org-form-row">
          <label>due</label>
          <input type="date" class="field-input" id="rem-date" value="${due ? ymd(due) : ""}" />
          <input type="time" class="field-input org-time" id="rem-time" value="${due && !f.allDay ? hm(due) : ""}" />
          <button class="btn btn-small" id="rem-clear-due">no date</button>
        </div>
        <div class="org-form-row">
          <label>repeats</label>
          <select class="field-input" id="rem-repeat">
            <option value="">never</option>
            <option value="DAILY"${repeat === "DAILY" ? " selected" : ""}>every day</option>
            <option value="WEEKLY"${repeat === "WEEKLY" ? " selected" : ""}>every week</option>
            <option value="MONTHLY"${repeat === "MONTHLY" ? " selected" : ""}>every month</option>
            <option value="YEARLY"${repeat === "YEARLY" ? " selected" : ""}>every year</option>
            ${repeat === "custom" ? '<option value="custom" selected>custom (set in another app)</option>' : ""}
          </select>
          <label class="org-inline-label">priority</label>
          <select class="field-input" id="rem-priority">
            ${["none", "low", "medium", "high"].map((t, i) => `<option value="${i}"${(f.priority || 0) === i ? " selected" : ""}>${t}</option>`).join("")}
          </select>
        </div>
        <div class="org-form-row">
          <label>alert</label>
          <select class="field-input" id="rem-alert">${ALERT_CHOICES.map(([v, t]) => `<option value="${v ?? ""}"${(alert ?? null) === v ? " selected" : ""}>${t}</option>`).join("")}</select>
          ${parentId || (reminder && reminder.parentId) ? "" : `<label class="org-inline-label">in</label><select class="field-input" id="rem-space">${spaceOptions(isNew ? defaultSpace() : reminder.spaceId)}</select>`}
        </div>
        <label class="org-check"><input type="checkbox" id="rem-flagged"${f.flagged ? " checked" : ""}> flagged</label>
        <textarea class="field-textarea" id="rem-notes" rows="3" placeholder="notes">${escapeHtml(f.notes || "")}</textarea>
        ${reminder && !reminder.parentId ? `
          <div class="org-subsection">STEPS</div>
          <div class="org-subtasks">
            ${subtasks.map((s) => `<label class="org-check"><input type="checkbox" data-subtask="${escapeHtml(s.id)}"${s.fields.completed ? " checked" : ""}> <span class="${s.fields.completed ? "org-done" : ""}">${escapeHtml(s.fields.title)}</span> <button class="org-x" data-remove-subtask="${escapeHtml(s.id)}" aria-label="remove">&times;</button></label>`).join("")}
            <input class="field-input" id="rem-new-step" placeholder="add a step and press enter" />
          </div>` : ""}
        ${linksEditor(f.links)}
        <div id="rem-attachments"></div>
      </div>
      <div class="zone-editor-footer">
        <button class="btn" id="rem-save">save</button>
        <button class="btn" id="rem-cancel">cancel</button>
        ${isNew ? "" : '<span class="org-spacer"></span><button class="btn org-danger" id="rem-delete">delete</button>'}
      </div>`);

    bindLinksEditor();
    el("rem-clear-due").addEventListener("click", () => { el("rem-date").value = ""; el("rem-time").value = ""; });

    let saved = reminder;
    const collect = () => {
      const date = el("rem-date").value;
      const time = el("rem-time").value;
      const repeatChoice = el("rem-repeat").value;
      const alertChoice = el("rem-alert").value;
      return {
        title: el("rem-title").value.trim() || "reminder",
        due: date ? (time ? isoLocal(new Date(`${date}T${time}`)) : date) : null,
        allDay: date ? !time : null,
        recurrence: repeatChoice === "custom" ? f.recurrence : repeatChoice ? `FREQ=${repeatChoice}` : null,
        priority: Number(el("rem-priority").value) || 0,
        alerts: alertChoice === "" ? null : [Number(alertChoice)],
        flagged: el("rem-flagged").checked,
        notes: el("rem-notes").value || null,
        links: collectLinks()
      };
    };
    const spaceChoice = () => (el("rem-space") ? el("rem-space").value : defaultSpace());
    const ensureSaved = () => {
      if (!saved) saved = parentId ? S.create("reminder", collect(), { parentId }) : S.create("reminder", collect(), { spaceId: spaceChoice() });
      return saved.id;
    };
    renderAttachments(el("rem-attachments"), () => (saved ? saved.id : null), ensureSaved);

    for (const box of document.querySelectorAll("[data-subtask]")) {
      box.addEventListener("change", () => S.update(box.dataset.subtask, { completed: box.checked, completedAt: box.checked ? new Date().toISOString() : null }));
    }
    for (const button of document.querySelectorAll("[data-remove-subtask]")) {
      button.addEventListener("click", (event) => {
        event.preventDefault();
        S.remove(button.dataset.removeSubtask);
        button.closest("label").remove();
      });
    }
    if (el("rem-new-step")) {
      el("rem-new-step").addEventListener("keydown", (event) => {
        if (event.key !== "Enter" || !el("rem-new-step").value.trim()) return;
        const step = S.create("reminder", { title: el("rem-new-step").value.trim(), completed: false, position: subtasks.length + Date.now() / 1e13 }, { parentId: reminder.id });
        const label = document.createElement("label");
        label.className = "org-check";
        label.innerHTML = `<input type="checkbox" data-subtask="${escapeHtml(step.id)}"> <span>${escapeHtml(step.fields.title)}</span>`;
        label.querySelector("input").addEventListener("change", (e) => S.update(step.id, { completed: e.target.checked }));
        el("rem-new-step").before(label);
        el("rem-new-step").value = "";
      });
    }

    el("rem-cancel").addEventListener("click", closeModal);
    el("rem-save").addEventListener("click", () => {
      const fields = collect();
      if (!saved) {
        ensureSaved();
      } else {
        const changes = changedFields(saved.fields, fields);
        if (el("rem-space") && el("rem-space").value !== saved.spaceId) changes.spaceId = el("rem-space").value;
        if (Object.keys(changes).length) S.update(saved.id, changes);
      }
      closeModal();
    });
    if (!isNew) {
      el("rem-delete").addEventListener("click", () => {
        S.remove(reminder.id);
        for (const step of S.children(reminder.id)) S.remove(step.id);
        closeModal();
      });
    }
    el("rem-title").focus();
  }

  // ---- notes --------------------------------------------------------------------------------

  let noteSaveTimer = null;
  let notePreview = false;

  function renderNotes() {
    const root = el("org-pane-notes");
    const notes = S.all("note").filter(inFilter).filter((n) => !n.fields.archived);
    const query = noteSearch.toLowerCase();
    const shown = notes
      .filter((n) => !query || `${n.fields.title || ""}\n${n.fields.body || ""}`.toLowerCase().includes(query))
      .sort((a, b) => (b.fields.pinned ? 1 : 0) - (a.fields.pinned ? 1 : 0) || String(b.updatedAt).localeCompare(String(a.updatedAt)));
    if (selectedNoteId && !S.get(selectedNoteId)) selectedNoteId = null;
    if (selectedNoteId && S.get(selectedNoteId).deleted) selectedNoteId = null;

    // Rebuilding the editor under someone's cursor would throw away their caret and selection,
    // so while they're typing only the list is redrawn.
    const editorFocused = root.contains(document.activeElement) && document.activeElement !== document.body;
    let listRoot = el("org-note-list");
    if (!listRoot || !editorFocused || el("org-note-editor").dataset.noteId !== selectedNoteId) {
      root.innerHTML = `
        <div class="org-notes">
          <div class="org-note-side">
            <div class="org-note-tools">
              <input class="field-input" id="org-note-search" placeholder="search notes" value="${escapeHtml(noteSearch)}" />
              <button class="btn" id="org-note-add">+ note</button>
            </div>
            <div id="org-note-list" class="org-note-list"></div>
          </div>
          <div id="org-note-editor" class="org-note-editor" data-note-id="${escapeHtml(selectedNoteId || "")}"></div>
        </div>`;
      listRoot = el("org-note-list");
      el("org-note-search").addEventListener("input", () => {
        noteSearch = el("org-note-search").value;
        renderNoteList();
      });
      el("org-note-add").addEventListener("click", () => {
        const note = S.create("note", { title: "", body: "" }, { spaceId: defaultSpace() });
        selectedNoteId = note.id;
        renderNotes();
        el("note-title").focus();
      });
      renderNoteEditor();
    } else {
      refreshNoteEditorFromRemote();
    }
    renderNoteList(shown);
  }

  function renderNoteList(list) {
    const root = el("org-note-list");
    if (!root) return;
    const query = noteSearch.toLowerCase();
    const notes = list || S.all("note").filter(inFilter).filter((n) => !n.fields.archived)
      .filter((n) => !query || `${n.fields.title || ""}\n${n.fields.body || ""}`.toLowerCase().includes(query))
      .sort((a, b) => (b.fields.pinned ? 1 : 0) - (a.fields.pinned ? 1 : 0) || String(b.updatedAt).localeCompare(String(a.updatedAt)));
    root.innerHTML = notes.length ? notes.map((n) => {
      const preview = String(n.fields.body || "").replace(/[#*`>\-[\]]/g, "").trim().split("\n")[0].slice(0, 90);
      const hasFiles = S.children(n.id).some((c) => c.kind === "attachment");
      return `<div class="org-note-item${n.id === selectedNoteId ? " active" : ""}" data-note="${escapeHtml(n.id)}">
        <div class="org-note-item-title">${n.fields.pinned ? "&#128204; " : ""}${escapeHtml(n.fields.title || "untitled")}${hasFiles ? " &#128206;" : ""}</div>
        <div class="org-note-item-preview">${escapeHtml(preview)}</div>
        <div class="org-note-item-meta"><span class="org-dot" style="background:${escapeHtml(spaceColor(n.spaceId))}"></span>${escapeHtml(spaceName(n.spaceId))} &middot; ${escapeHtml(new Date(n.updatedAt).toLocaleDateString())}</div>
      </div>`;
    }).join("") : '<div class="org-empty">no notes yet</div>';
    for (const item of root.querySelectorAll("[data-note]")) {
      item.addEventListener("click", () => {
        flushNoteSave();
        selectedNoteId = item.dataset.note;
        notePreview = false;
        renderNotes();
      });
    }
  }

  function renderNoteEditor() {
    const root = el("org-note-editor");
    const note = selectedNoteId ? S.get(selectedNoteId) : null;
    if (!note) {
      root.innerHTML = '<div class="org-empty">pick a note, or make a new one</div>';
      return;
    }
    const f = note.fields;
    root.innerHTML = `
      <div class="org-note-head">
        <input class="field-input org-title-input" id="note-title" placeholder="title" value="${escapeHtml(f.title || "")}" />
        <select class="field-input org-note-space" id="note-space">${spaceOptions(note.spaceId)}</select>
      </div>
      <div class="org-note-actions">
        <button class="btn btn-small${f.pinned ? " btn-on" : ""}" id="note-pin">${f.pinned ? "pinned" : "pin"}</button>
        <button class="btn btn-small${notePreview ? " btn-on" : ""}" id="note-preview">${notePreview ? "edit" : "preview"}</button>
        <span class="event-count">markdown: # heading, **bold**, - [ ] checklist</span>
        <span class="org-spacer"></span>
        <button class="btn btn-small org-danger" id="note-delete">delete</button>
      </div>
      ${notePreview
        ? `<div class="org-md" id="note-rendered">${renderMarkdown(f.body) || '<div class="org-empty">empty</div>'}</div>`
        : `<textarea class="field-textarea org-note-body" id="note-body" placeholder="write here">${escapeHtml(f.body || "")}</textarea>`}
      ${linksEditor(f.links, true)}
      <div id="note-attachments"></div>
      <div class="event-count">${f.updatedBy || note.updatedBy ? `last edited by ${escapeHtml(note.updatedBy || "")} ` : ""}${escapeHtml(new Date(note.updatedAt).toLocaleString())}</div>`;

    el("note-title").addEventListener("input", () => queueNoteSave());
    if (el("note-body")) {
      el("note-body").addEventListener("input", () => queueNoteSave());
      el("note-body").addEventListener("paste", (event) => {
        const files = [...(event.clipboardData ? event.clipboardData.files : [])];
        if (files.length) {
          event.preventDefault();
          addFiles(note.id, files);
        }
      });
    }
    el("note-space").addEventListener("change", () => S.update(note.id, { spaceId: el("note-space").value }));
    el("note-pin").addEventListener("click", () => { S.update(note.id, { pinned: !note.fields.pinned }); renderNoteEditor(); });
    el("note-preview").addEventListener("click", () => { flushNoteSave(); notePreview = !notePreview; renderNoteEditor(); });
    el("note-delete").addEventListener("click", () => {
      if (!confirm(`Delete "${note.fields.title || "untitled"}"?`)) return;
      S.remove(note.id);
      selectedNoteId = null;
      renderNotes();
    });
    if (el("note-rendered")) {
      for (const box of el("note-rendered").querySelectorAll("[data-md-line]")) {
        box.addEventListener("change", () => {
          S.update(note.id, { body: toggleChecklistLine(S.get(note.id).fields.body, Number(box.dataset.mdLine)) });
          renderNoteEditor();
        });
      }
    }
    bindLinksEditor(() => S.update(note.id, { links: collectLinks() }));
    renderAttachments(el("note-attachments"), () => note.id, () => note.id);
  }

  // Another device changed the note that's open: take its text unless this person is mid-edit
  // with unsaved changes of their own (theirs goes up and the server keeps both if they clash).
  function refreshNoteEditorFromRemote() {
    const note = selectedNoteId ? S.get(selectedNoteId) : null;
    if (!note || noteSaveTimer) return;
    const title = el("note-title");
    const body = el("note-body");
    if (title && document.activeElement !== title && title.value !== (note.fields.title || "")) title.value = note.fields.title || "";
    if (body && document.activeElement !== body && body.value !== (note.fields.body || "")) body.value = note.fields.body || "";
  }

  function queueNoteSave() {
    clearTimeout(noteSaveTimer);
    noteSaveTimer = setTimeout(flushNoteSave, 500);
  }

  function flushNoteSave() {
    if (!noteSaveTimer) return;
    clearTimeout(noteSaveTimer);
    noteSaveTimer = null;
    const note = selectedNoteId ? S.get(selectedNoteId) : null;
    if (!note) return;
    const changes = {};
    if (el("note-title") && el("note-title").value !== (note.fields.title || "")) changes.title = el("note-title").value;
    if (el("note-body") && el("note-body").value !== (note.fields.body || "")) changes.body = el("note-body").value;
    if (Object.keys(changes).length) S.update(note.id, changes);
  }

  // ---- boards -------------------------------------------------------------------------------

  function renderBoards() {
    const root = el("org-pane-boards");
    const boards = S.all("board").filter(inFilter).filter((b) => !b.fields.archived).sort(byPosition);
    if (selectedBoardId && !boards.some((b) => b.id === selectedBoardId)) selectedBoardId = null;
    if (!selectedBoardId && boards.length) selectedBoardId = boards[0].id;
    const board = selectedBoardId ? S.get(selectedBoardId) : null;

    let body = '<div class="org-empty">no boards yet - make one to track anything in columns</div>';
    if (board) {
      const columns = S.children(board.id).filter((c) => c.kind === "column").sort(byPosition);
      body = `
        <div class="org-board-head">
          <span class="org-board-title" style="border-color:${escapeHtml(board.fields.color || spaceColor(board.spaceId))}">${escapeHtml(board.fields.title || "board")}</span>
          <select class="field-input org-note-space" id="board-space">${spaceOptions(board.spaceId)}</select>
          <button class="btn btn-small" id="board-rename">rename</button>
          <button class="btn btn-small org-danger" id="board-delete">delete board</button>
        </div>
        <div class="org-columns" id="org-columns">
          ${columns.map((column, index) => columnHtml(column, index, columns.length)).join("")}
          <div class="org-column org-column-add">
            <input class="field-input" id="org-new-column" placeholder="+ add a column" />
          </div>
        </div>`;
    }

    root.innerHTML = `
      <div class="org-board-tabs">
        ${boards.map((b) => `<button class="org-chip${b.id === selectedBoardId ? " active" : ""}" data-board="${escapeHtml(b.id)}"><span class="org-dot" style="background:${escapeHtml(b.fields.color || spaceColor(b.spaceId))}"></span>${escapeHtml(b.fields.title || "board")}</button>`).join("")}
        <button class="btn btn-small" id="org-board-add">+ board</button>
      </div>
      ${body}`;

    for (const tab of root.querySelectorAll("[data-board]")) {
      tab.addEventListener("click", () => { selectedBoardId = tab.dataset.board; renderBoards(); });
    }
    el("org-board-add").addEventListener("click", () => {
      const title = prompt("Board name", "New board");
      if (!title) return;
      const newBoard = S.create("board", { title, position: Date.now() }, { spaceId: defaultSpace() });
      ["To do", "Doing", "Done"].forEach((name, i) => S.create("column", { title: name, position: i + 1 }, { parentId: newBoard.id }));
      selectedBoardId = newBoard.id;
      renderBoards();
    });
    if (!board) return;

    el("board-space").addEventListener("change", () => S.update(board.id, { spaceId: el("board-space").value }));
    el("board-rename").addEventListener("click", () => {
      const title = prompt("Board name", board.fields.title || "");
      if (title) S.update(board.id, { title });
    });
    el("board-delete").addEventListener("click", () => {
      if (!confirm(`Delete the board "${board.fields.title}" and every card on it?`)) return;
      S.remove(board.id);
      selectedBoardId = null;
    });
    el("org-new-column").addEventListener("keydown", (event) => {
      if (event.key !== "Enter" || !event.target.value.trim()) return;
      const columns = S.children(board.id).filter((c) => c.kind === "column");
      const last = columns.reduce((max, c) => Math.max(max, c.fields.position || 0), 0);
      S.create("column", { title: event.target.value.trim(), position: last + 1 }, { parentId: board.id });
    });
    bindBoard(root, board);
  }

  function columnHtml(column, index, count) {
    const cards = S.children(column.id).filter((c) => c.kind === "card").sort(byPosition);
    return `<div class="org-column" data-column="${escapeHtml(column.id)}">
      <div class="org-column-head">
        <span class="org-column-title" data-rename-column="${escapeHtml(column.id)}" title="click to rename">${escapeHtml(column.fields.title || "column")}</span>
        <span class="event-count">${cards.length}</span>
        <span class="org-spacer"></span>
        ${index > 0 ? `<button class="org-x" data-move-column="${escapeHtml(column.id)}" data-dir="-1" aria-label="move left">&lsaquo;</button>` : ""}
        ${index < count - 1 ? `<button class="org-x" data-move-column="${escapeHtml(column.id)}" data-dir="1" aria-label="move right">&rsaquo;</button>` : ""}
        <button class="org-x" data-delete-column="${escapeHtml(column.id)}" aria-label="delete column">&times;</button>
      </div>
      <div class="org-cards" data-drop-column="${escapeHtml(column.id)}">
        ${cards.map(cardHtml).join("")}
      </div>
      <input class="field-input org-card-add" data-add-card="${escapeHtml(column.id)}" placeholder="+ add a card" />
    </div>`;
  }

  function cardHtml(card) {
    const f = card.fields;
    const attachments = S.children(card.id).filter((c) => c.kind === "attachment");
    const comments = S.children(card.id).filter((c) => c.kind === "comment").length;
    const cover = f.cover ? attachments.find((a) => a.id === f.cover) : attachments.find((a) => String(a.fields.contentType).startsWith("image/"));
    const checklist = f.checklist || [];
    const done = checklist.filter((c) => c.done).length;
    const overdue = f.due && !f.completed && (f.due.length === 10 ? f.due < ymd(new Date()) : new Date(f.due) < new Date());
    return `<div class="org-card${f.completed ? " org-card-done" : ""}" draggable="true" data-card="${escapeHtml(card.id)}">
      ${cover ? `<img class="org-card-cover" src="${escapeHtml(S.blobUrl(cover.fields.hash, true))}" alt="" loading="lazy" />` : ""}
      ${(f.labels || []).length ? `<div class="org-labels">${f.labels.map((l) => `<span class="org-label" style="background:${escapeHtml(l.color || labelColor(l.name))}">${escapeHtml(l.name)}</span>`).join("")}</div>` : ""}
      <div class="org-card-title">${escapeHtml(f.title || "card")}</div>
      <div class="org-card-meta">
        ${f.due ? `<span class="${overdue ? "org-overdue" : ""}">&#128339; ${escapeHtml(dueLabel({ fields: { due: f.due, allDay: f.due.length === 10 } }))}</span>` : ""}
        ${checklist.length ? `<span>&#9745; ${done}/${checklist.length}</span>` : ""}
        ${attachments.length ? `<span>&#128206; ${attachments.length}</span>` : ""}
        ${comments ? `<span>&#128172; ${comments}</span>` : ""}
        ${f.description ? "<span>&#9776;</span>" : ""}
        ${(f.assignees || []).map((a) => `<span class="org-avatar" title="${escapeHtml(a)}">${escapeHtml(a.slice(0, 2).toUpperCase())}</span>`).join("")}
      </div>
    </div>`;
  }

  function labelColor(name) {
    let hash = 0;
    for (const ch of String(name)) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
    return SPACE_COLORS[hash % SPACE_COLORS.length];
  }

  function bindBoard(root, board) {
    for (const input of root.querySelectorAll("[data-add-card]")) {
      input.addEventListener("keydown", (event) => {
        if (event.key !== "Enter" || !input.value.trim()) return;
        const cards = S.children(input.dataset.addCard).filter((c) => c.kind === "card");
        const last = cards.reduce((max, c) => Math.max(max, c.fields.position || 0), 0);
        S.create("card", { title: input.value.trim(), position: last + 1 }, { parentId: input.dataset.addCard });
        const column = input.dataset.addCard;
        setTimeout(() => { const again = root.querySelector(`[data-add-card="${CSS.escape(column)}"]`); if (again) again.focus(); }, 30);
      });
    }
    for (const title of root.querySelectorAll("[data-rename-column]")) {
      title.addEventListener("click", () => {
        const column = S.get(title.dataset.renameColumn);
        const name = prompt("Column name", column.fields.title || "");
        if (name) S.update(column.id, { title: name });
      });
    }
    for (const button of root.querySelectorAll("[data-delete-column]")) {
      button.addEventListener("click", () => {
        const column = S.get(button.dataset.deleteColumn);
        const cards = S.children(column.id).filter((c) => c.kind === "card");
        if (cards.length && !confirm(`Delete "${column.fields.title}" and its ${cards.length} card(s)?`)) return;
        for (const card of cards) S.remove(card.id);
        S.remove(column.id);
      });
    }
    for (const button of root.querySelectorAll("[data-move-column]")) {
      button.addEventListener("click", () => {
        const columns = S.children(board.id).filter((c) => c.kind === "column").sort(byPosition);
        const index = columns.findIndex((c) => c.id === button.dataset.moveColumn);
        const target = index + Number(button.dataset.dir);
        if (target < 0 || target >= columns.length) return;
        const [moved] = columns.splice(index, 1);
        columns.splice(target, 0, moved);
        columns.forEach((c, i) => { if (c.fields.position !== i + 1) S.update(c.id, { position: i + 1 }); });
      });
    }
    for (const card of root.querySelectorAll("[data-card]")) {
      card.addEventListener("click", () => openCardEditor(S.get(card.dataset.card)));
      card.addEventListener("dragstart", (event) => {
        dragging = card.dataset.card;
        card.classList.add("org-dragging");
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", dragging);
      });
      card.addEventListener("dragend", () => {
        dragging = null;
        card.classList.remove("org-dragging");
        queueRender();
      });
    }
    for (const zone of root.querySelectorAll("[data-drop-column]")) {
      zone.addEventListener("dragover", (event) => {
        if (!dragging) return;
        event.preventDefault();
        zone.classList.add("org-drop-target");
        const after = cardAfterPointer(zone, event.clientY);
        const node = root.querySelector(`[data-card="${CSS.escape(dragging)}"]`);
        if (node) {
          if (after) zone.insertBefore(node, after);
          else zone.appendChild(node);
        }
      });
      zone.addEventListener("dragleave", () => zone.classList.remove("org-drop-target"));
      zone.addEventListener("drop", (event) => {
        event.preventDefault();
        zone.classList.remove("org-drop-target");
        const cardId = dragging;
        if (!cardId) return;
        // The node is already where it was dropped; read its neighbours to find a position
        // between them, so only the moved card is written - not every card in the column.
        const nodes = [...zone.querySelectorAll("[data-card]")];
        const index = nodes.findIndex((n) => n.dataset.card === cardId);
        const before = index > 0 ? S.get(nodes[index - 1].dataset.card) : null;
        const after = index < nodes.length - 1 ? S.get(nodes[index + 1].dataset.card) : null;
        const lo = before ? before.fields.position ?? 0 : 0;
        const hi = after ? after.fields.position ?? lo + 2 : lo + 2;
        const card = S.get(cardId);
        const changes = { position: (lo + hi) / 2 };
        if (card.parentId !== zone.dataset.dropColumn) changes.parentId = zone.dataset.dropColumn;
        dragging = null;
        S.update(cardId, changes);
      });
    }
  }

  function cardAfterPointer(zone, y) {
    const cards = [...zone.querySelectorAll("[data-card]:not(.org-dragging)")];
    return cards.find((card) => {
      const box = card.getBoundingClientRect();
      return y < box.top + box.height / 2;
    }) || null;
  }

  async function loadPeople() {
    if (people) return people;
    try {
      const response = await fetch("/api/organizer/people");
      people = response.ok ? (await response.json()).people : [];
    } catch {
      people = [];
    }
    return people;
  }

  async function openCardEditor(card) {
    if (!card) return;
    const f = card.fields;
    const list = S.status() && S.status().personalSpaces ? await loadPeople() : [];
    const checklist = (f.checklist || []).map((c) => ({ ...c }));
    const due = f.due ? when(f.due, f.due.length === 10) : null;

    openModal(`
      <div class="zone-editor-header"><span>CARD</span><span class="event-count">${escapeHtml(S.get(card.parentId) ? S.get(card.parentId).fields.title : "")}</span></div>
      <div class="org-form">
        <input class="field-input org-title-input" id="card-title" value="${escapeHtml(f.title || "")}" placeholder="title" />
        <label class="org-check"><input type="checkbox" id="card-done"${f.completed ? " checked" : ""}> done</label>
        <div class="org-form-row">
          <label>due</label>
          <input type="date" class="field-input" id="card-date" value="${due ? ymd(due) : ""}" />
          <input type="time" class="field-input org-time" id="card-time" value="${due && f.due.length > 10 ? hm(due) : ""}" />
        </div>
        <div class="org-form-row">
          <label>labels</label>
          <input class="field-input" id="card-labels" placeholder="comma separated, e.g. urgent, outside" value="${escapeHtml((f.labels || []).map((l) => l.name).join(", "))}" />
        </div>
        ${list.length ? `<div class="org-form-row"><label>people</label><div class="org-people">${list.map((p) => `<label class="org-check"><input type="checkbox" data-assignee="${escapeHtml(p.username)}"${(f.assignees || []).includes(p.username) ? " checked" : ""}> ${escapeHtml(p.displayName || p.username)}</label>`).join("")}</div></div>` : ""}
        <textarea class="field-textarea" id="card-description" rows="5" placeholder="description (markdown)">${escapeHtml(f.description || "")}</textarea>
        <div class="org-subsection">CHECKLIST</div>
        <div id="card-checklist" class="org-subtasks"></div>
        <input class="field-input" id="card-new-check" placeholder="add an item and press enter" />
        ${linksEditor(f.links)}
        <div id="card-attachments"></div>
        <div class="org-subsection">COMMENTS</div>
        <div id="card-comments" class="org-comments"></div>
        <textarea class="field-textarea" id="card-new-comment" rows="2" placeholder="write a comment, then press enter (shift+enter for a new line)"></textarea>
      </div>
      <div class="zone-editor-footer">
        <button class="btn" id="card-save">save</button>
        <button class="btn" id="card-cancel">cancel</button>
        <span class="org-spacer"></span>
        <button class="btn org-danger" id="card-delete">delete</button>
      </div>`);

    const drawChecklist = () => {
      el("card-checklist").innerHTML = checklist.map((c, i) => `<label class="org-check"><input type="checkbox" data-check="${i}"${c.done ? " checked" : ""}> <span class="${c.done ? "org-done" : ""}">${escapeHtml(c.text)}</span> <button class="org-x" data-uncheck="${i}" aria-label="remove">&times;</button></label>`).join("");
      for (const box of el("card-checklist").querySelectorAll("[data-check]")) {
        box.addEventListener("change", () => { checklist[Number(box.dataset.check)].done = box.checked; drawChecklist(); });
      }
      for (const button of el("card-checklist").querySelectorAll("[data-uncheck]")) {
        button.addEventListener("click", (event) => { event.preventDefault(); checklist.splice(Number(button.dataset.uncheck), 1); drawChecklist(); });
      }
    };
    drawChecklist();
    el("card-new-check").addEventListener("keydown", (event) => {
      if (event.key !== "Enter" || !event.target.value.trim()) return;
      checklist.push({ id: Math.random().toString(36).slice(2, 10), text: event.target.value.trim(), done: false });
      event.target.value = "";
      drawChecklist();
    });
    bindLinksEditor();
    renderAttachments(el("card-attachments"), () => card.id, () => card.id);
    renderComments(el("card-comments"), el("card-new-comment"), card.id);

    el("card-cancel").addEventListener("click", closeModal);
    el("card-delete").addEventListener("click", () => { S.remove(card.id); closeModal(); });
    el("card-save").addEventListener("click", () => {
      const date = el("card-date").value;
      const time = el("card-time").value;
      const existingLabels = new Map((f.labels || []).map((l) => [l.name, l]));
      const fields = {
        title: el("card-title").value.trim() || "card",
        completed: el("card-done").checked,
        due: date ? (time ? isoLocal(new Date(`${date}T${time}`)) : date) : null,
        labels: el("card-labels").value.split(",").map((s) => s.trim()).filter(Boolean).map((name) => existingLabels.get(name) || { name, color: labelColor(name) }),
        description: el("card-description").value || null,
        checklist,
        links: collectLinks()
      };
      if (list.length) fields.assignees = [...document.querySelectorAll("[data-assignee]:checked")].map((b) => b.dataset.assignee);
      const changes = changedFields(f, fields);
      if (Object.keys(changes).length) S.update(card.id, changes);
      closeModal();
    });
  }

  // ---- links & attachments ------------------------------------------------------------------

  function linksEditor(links, live) {
    const rows = (links || []).map((l) => linkRow(l)).join("");
    return `<div class="org-links" data-live="${live ? "1" : ""}">
      <div class="org-subsection">LINKS</div>
      <div id="org-link-rows">${rows}</div>
      <div class="org-form-row org-link-add">
        <input class="field-input" id="org-link-url" placeholder="https://…" />
        <input class="field-input" id="org-link-title" placeholder="label (optional)" />
        <button class="btn btn-small" id="org-link-add">add link</button>
      </div>
    </div>`;
  }

  function linkRow(link) {
    const safe = safeUrl(link.url);
    return `<div class="org-link" data-url="${escapeHtml(link.url)}" data-title="${escapeHtml(link.title || "")}">
      ${safe ? `<a href="${escapeHtml(safe)}" target="_blank" rel="noopener noreferrer">${escapeHtml(link.title || link.url)}</a>` : `<span>${escapeHtml(link.title || link.url)}</span>`}
      ${link.title ? `<span class="event-count">${escapeHtml(link.url)}</span>` : ""}
      <button class="org-x" data-remove-link aria-label="remove link">&times;</button>
    </div>`;
  }

  function bindLinksEditor(onChange) {
    const rows = el("org-link-rows");
    if (!rows) return;
    const wireRemove = () => {
      for (const button of rows.querySelectorAll("[data-remove-link]")) {
        button.onclick = () => { button.closest(".org-link").remove(); if (onChange) onChange(); };
      }
    };
    wireRemove();
    const add = () => {
      const url = el("org-link-url").value.trim();
      if (!url) return;
      const fixed = safeUrl(url) || url;
      rows.insertAdjacentHTML("beforeend", linkRow({ url: fixed, title: el("org-link-title").value.trim() || null }));
      el("org-link-url").value = "";
      el("org-link-title").value = "";
      wireRemove();
      if (onChange) onChange();
    };
    el("org-link-add").addEventListener("click", add);
    el("org-link-url").addEventListener("keydown", (event) => { if (event.key === "Enter") add(); });
  }

  function collectLinks() {
    const rows = el("org-link-rows");
    if (!rows) return null;
    const links = [...rows.querySelectorAll(".org-link")].map((row) => ({ url: row.dataset.url, ...(row.dataset.title ? { title: row.dataset.title } : {}) }));
    return links.length ? links : null;
  }

  function formatSize(bytes) {
    if (!bytes) return "";
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1048576) return `${Math.round(bytes / 1024)} KB`;
    return `${(bytes / 1048576).toFixed(1)} MB`;
  }

  // Pictures and files on an item. Drop, paste or pick; images show as thumbnails and open full
  // size, anything else downloads. `ensureParent` creates the item first when it's still new.
  // A card's comments. Each is its own item, posted the moment it's written rather than on save,
  // and the list follows sync while the card is open. Only the author may edit a comment (the
  // server enforces it); anyone may delete one.
  function renderComments(container, input, cardId) {
    const me = S.status() && S.status().username;
    const names = new Map();
    let editing = null;

    // No author means login is off: one shared household, so there is no name to show.
    const nameOf = (username) => names.get(username.toLowerCase()) || username;
    const mine = (comment) => !me || !comment.createdBy || comment.createdBy.toLowerCase() === me.toLowerCase();

    const draw = () => {
      const comments = S.children(cardId)
        .filter((c) => c.kind === "comment")
        .sort((a, b) => String(a.createdAt).localeCompare(String(b.createdAt)));
      container.innerHTML = comments.length ? comments.map((c) => `
        <div class="org-comment" data-comment="${escapeHtml(c.id)}">
          <div class="org-comment-head">
            ${c.createdBy ? `<span class="org-avatar">${escapeHtml(nameOf(c.createdBy).slice(0, 2).toUpperCase())}</span>
            <strong>${escapeHtml(nameOf(c.createdBy))}</strong>` : ""}
            <span class="event-count">${escapeHtml(new Date(c.createdAt).toLocaleString())}${c.fields.editedAt ? " (edited)" : ""}</span>
            <span class="org-spacer"></span>
            ${mine(c) && editing !== c.id ? `<button class="btn btn-small" data-edit-comment="${escapeHtml(c.id)}">edit</button>` : ""}
            <button class="org-x" data-delete-comment="${escapeHtml(c.id)}" aria-label="delete comment">&times;</button>
          </div>
          ${editing === c.id
            ? `<textarea class="field-textarea" data-editing rows="3">${escapeHtml(c.fields.body || "")}</textarea>
               <div class="org-form-row"><button class="btn btn-small" data-save-comment>save</button><button class="btn btn-small" data-cancel-comment>cancel</button></div>`
            : `<div class="org-comment-body">${escapeHtml(c.fields.body || "")}</div>`}
        </div>`).join("") : `<div class="event-count">No comments yet.</div>`;

      for (const button of container.querySelectorAll("[data-edit-comment]")) {
        button.addEventListener("click", () => { editing = button.dataset.editComment; draw(); container.querySelector("[data-editing]").focus(); });
      }
      for (const button of container.querySelectorAll("[data-delete-comment]")) {
        button.addEventListener("click", () => {
          if (!confirm("Delete this comment?")) return;
          if (editing === button.dataset.deleteComment) editing = null;
          S.remove(button.dataset.deleteComment);
          draw();
        });
      }
      const box = container.querySelector("[data-editing]");
      if (box) {
        const save = () => {
          const body = box.value.trim();
          const comment = S.get(editing);
          editing = null;
          if (body && comment && body !== comment.fields.body) S.update(comment.id, { body, editedAt: new Date().toISOString() });
          draw();
        };
        container.querySelector("[data-save-comment]").addEventListener("click", save);
        container.querySelector("[data-cancel-comment]").addEventListener("click", () => { editing = null; draw(); });
        box.addEventListener("keydown", (event) => {
          if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); save(); }
          if (event.key === "Escape") { event.stopPropagation(); editing = null; draw(); }
        });
      }
    };

    input.addEventListener("keydown", (event) => {
      if (event.key !== "Enter" || event.shiftKey) return;
      event.preventDefault();
      const body = input.value.trim();
      if (!body) return;
      S.create("comment", { body }, { parentId: cardId });
      input.value = "";
      draw();
    });

    // Follow sync while the card is open, but never redraw under someone's half-edited comment.
    const unsubscribe = S.subscribe(() => {
      if (!document.body.contains(container)) { unsubscribe(); return; }
      if (!editing) draw();
    });
    loadPeople().then((list) => {
      for (const p of list) names.set(p.username.toLowerCase(), p.displayName || p.username);
      if (!editing && document.body.contains(container)) draw();
    });
    draw();
  }

  function renderAttachments(container, parentIdOf, ensureParent) {
    const draw = () => {
      const parentId = parentIdOf();
      const attachments = parentId ? S.children(parentId).filter((c) => c.kind === "attachment").sort(byPosition) : [];
      container.innerHTML = `
        <div class="org-subsection">PICTURES &amp; FILES</div>
        <div class="org-attachments">
          ${attachments.map((a) => {
            const isImage = String(a.fields.contentType || "").startsWith("image/");
            const full = S.blobUrl(a.fields.hash, false);
            return `<div class="org-attachment">
              <a href="${escapeHtml(isImage ? full : full + (full.startsWith("blob:") ? "" : "?download=true"))}" target="_blank" rel="noopener noreferrer"${isImage ? "" : ` download="${escapeHtml(a.fields.name || "file")}"`}>
                ${isImage
                  ? `<img src="${escapeHtml(S.blobUrl(a.fields.hash, true))}" alt="${escapeHtml(a.fields.name || "")}" loading="lazy" onerror="this.replaceWith(Object.assign(document.createElement('span'),{className:'org-file-icon',textContent:'IMG'}))" />`
                  : `<span class="org-file-icon">${escapeHtml((a.fields.name || "file").split(".").pop().slice(0, 4).toUpperCase())}</span>`}
              </a>
              <div class="org-attachment-name" title="${escapeHtml(a.fields.name || "")}">${escapeHtml(a.fields.name || "file")}</div>
              <div class="event-count">${escapeHtml(formatSize(a.fields.size))}</div>
              <button class="org-x org-attachment-remove" data-remove-attachment="${escapeHtml(a.id)}" aria-label="remove">&times;</button>
            </div>`;
          }).join("")}
          <label class="org-attachment org-attachment-add">
            <input type="file" multiple hidden />
            <span>+ add<br><span class="event-count">or drop / paste</span></span>
          </label>
        </div>`;
      container.querySelector("input[type=file]").addEventListener("change", (event) => {
        addFiles(ensureParent(), [...event.target.files]).then(draw);
      });
      for (const button of container.querySelectorAll("[data-remove-attachment]")) {
        button.addEventListener("click", () => { S.remove(button.dataset.removeAttachment); draw(); });
      }
    };
    container.addEventListener("dragover", (event) => { event.preventDefault(); container.classList.add("org-drop-target"); });
    container.addEventListener("dragleave", () => container.classList.remove("org-drop-target"));
    container.addEventListener("drop", (event) => {
      event.preventDefault();
      container.classList.remove("org-drop-target");
      const files = [...(event.dataTransfer ? event.dataTransfer.files : [])];
      if (files.length) addFiles(ensureParent(), files).then(draw);
    });
    draw();
  }

  async function addFiles(parentId, files) {
    for (const file of files) {
      try {
        await S.attach(parentId, file);
      } catch (err) {
        showToast(err.message || "Couldn't attach that file.", false);
      }
    }
  }

  // ---- groups, subscription, reset ------------------------------------------------------------

  async function openGroups() {
    const status = S.status();
    if (!status || !status.personalSpaces) {
      openModal(`
        <div class="zone-editor-header"><span>GROUPS</span></div>
        <div class="wizard-body">
          <p>Groups let you share notes, events, reminders and boards with only the people you pick.</p>
          <p>They need login turned on, with an account for each person, so the server knows who is who. Turn it on under <strong>Settings &rarr; Access</strong> and add accounts on the <strong>Accounts</strong> tab. Until then, everything here is shared with everyone as Family.</p>
        </div>
        <div class="zone-editor-footer"><button class="btn" id="org-close">close</button></div>`);
      el("org-close").addEventListener("click", closeModal);
      return;
    }
    const list = await loadPeople();
    const groups = spaces().filter((s) => s.kind === "Group");
    const personName = (username) => { const p = list.find((x) => x.username === username); return p && p.displayName ? p.displayName : username; };

    openModal(`
      <div class="zone-editor-header"><span>SPACES &amp; GROUPS</span></div>
      <div class="wizard-body">
        <p class="event-count">Personal is only you. Family is everyone with an account. Groups are only the people you choose - anyone in a group can change who's in it.</p>
        ${groups.map((g) => `<div class="org-group">
          <span class="org-dot" style="background:${escapeHtml(spaceColor(g.id))}"></span>
          <strong>${escapeHtml(g.name)}</strong>
          <span class="event-count">${escapeHtml(g.members.map(personName).join(", "))}</span>
          <span class="org-spacer"></span>
          <button class="btn btn-small" data-edit-group="${escapeHtml(g.id)}">edit</button>
        </div>`).join("") || '<div class="org-empty">no groups yet</div>'}
        <div id="org-group-form"></div>
      </div>
      <div class="zone-editor-footer">
        <button class="btn" id="org-group-new">+ new group</button>
        <button class="btn" id="org-close">close</button>
      </div>`);

    const form = (group) => {
      const members = new Set(group ? group.members : [status.username]);
      el("org-group-form").innerHTML = `
        <div class="org-subsection">${group ? "EDIT GROUP" : "NEW GROUP"}</div>
        <div class="org-form-row">
          <input class="field-input" id="org-group-name" placeholder="name, e.g. Parents" value="${escapeHtml(group ? group.name : "")}" />
          <input type="color" id="org-group-color" value="${escapeHtml(group && group.color ? group.color : "#ffb545")}" title="colour" />
        </div>
        <div class="org-people">${list.map((p) => `<label class="org-check"><input type="checkbox" data-member="${escapeHtml(p.username)}"${members.has(p.username) ? " checked" : ""}> ${escapeHtml(p.displayName || p.username)}${p.username === status.username ? " (you)" : ""}</label>`).join("")}</div>
        <div class="org-form-row">
          <button class="btn" id="org-group-save">${group ? "save" : "create"}</button>
          ${group ? '<button class="btn org-danger" id="org-group-delete">delete group</button>' : ""}
        </div>
        <ul class="field-errors" id="org-group-errors"></ul>`;
      el("org-group-save").addEventListener("click", async () => {
        const body = {
          name: el("org-group-name").value,
          color: el("org-group-color").value,
          members: [...document.querySelectorAll("[data-member]:checked")].map((b) => b.dataset.member)
        };
        const response = await fetch(group ? `/api/organizer/spaces/${encodeURIComponent(group.id)}` : "/api/organizer/spaces", {
          method: group ? "PUT" : "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body)
        });
        if (!response.ok) {
          const payload = await response.json().catch(() => ({}));
          el("org-group-errors").innerHTML = (payload.errors || ["Couldn't save the group."]).map((e) => `<li>${escapeHtml(e)}</li>`).join("");
          return;
        }
        await S.sync();
        openGroups();
      });
      if (group) {
        el("org-group-delete").addEventListener("click", async () => {
          if (!confirm(`Delete "${group.name}"? Everything in it is deleted too, for everyone in the group.`)) return;
          await fetch(`/api/organizer/spaces/${encodeURIComponent(group.id)}`, { method: "DELETE" });
          await S.sync();
          openGroups();
        });
      }
      el("org-group-name").focus();
    };
    el("org-group-new").addEventListener("click", () => form(null));
    for (const button of document.querySelectorAll("[data-edit-group]")) {
      button.addEventListener("click", () => form(groups.find((g) => g.id === button.dataset.editGroup)));
    }
    el("org-close").addEventListener("click", closeModal);
  }

  async function openSubscribe() {
    let path = null;
    try {
      const response = await fetch("/api/organizer/calendar-feed");
      if (response.ok) path = (await response.json()).path;
    } catch {
      // Shown below as unavailable.
    }
    const url = path ? location.origin + path : null;
    openModal(`
      <div class="zone-editor-header"><span>SUBSCRIBE IN ANOTHER CALENDAR</span></div>
      <div class="wizard-body">
        ${url ? `
          <p>Add this link as a subscribed calendar in Apple Calendar, Google Calendar or Outlook to see your events there. It's read-only on their side - edits still happen here and in the SentinelCore apps.</p>
          <input class="field-input" id="org-feed-url" readonly value="${escapeHtml(url)}" />
          <div class="org-form-row">
            <button class="btn" id="org-feed-copy">copy link</button>
            <a class="btn" href="${escapeHtml(url.replace(/^https?:/, "webcal:"))}">open in calendar app</a>
          </div>
          <p class="event-count">Anyone with this link can see your events, so treat it like a password. The other calendar has to be able to reach this server - on the internet, that means Internet Exposed Mode. Resetting makes the old link stop working.</p>
          <button class="btn org-danger" id="org-feed-reset">reset link</button>` : "<p>Couldn't get a link - is the server reachable?</p>"}
      </div>
      <div class="zone-editor-footer"><button class="btn" id="org-close">close</button></div>`);
    el("org-close").addEventListener("click", closeModal);
    if (!url) return;
    el("org-feed-copy").addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(url);
        showToast("Link copied.", true);
      } catch {
        el("org-feed-url").select();
      }
    });
    el("org-feed-reset").addEventListener("click", async () => {
      if (!confirm("Make a new link? Calendars subscribed to the old one will stop updating.")) return;
      await fetch("/api/organizer/calendar-feed/reset", { method: "POST" });
      openSubscribe();
    });
  }

  function confirmReset(info) {
    return new Promise((resolve) => {
      openModal(`
        <div class="zone-editor-header"><span>THIS SERVER IS NEW TO THIS BROWSER</span></div>
        <div class="wizard-body">
          <p>This browser has a saved copy of ${info.localCount} Organizer item${info.localCount === 1 ? "" : "s"} from a server that isn't the one answering now${info.serverItemCount != null ? ` (it has ${info.serverItemCount})` : ""}.</p>
          <p>If the old server was lost or reinstalled, upload this copy to fill the new one back up. Anything already on the new server is kept - newer edits win.</p>
        </div>
        <div class="zone-editor-footer">
          <button class="btn" id="org-reset-upload">upload this copy</button>
          <button class="btn" id="org-reset-discard">use the server's copy</button>
        </div>`);
      el("org-reset-upload").addEventListener("click", () => { closeModal(); resolve(true); });
      el("org-reset-discard").addEventListener("click", () => { closeModal(); resolve(false); });
    });
  }

  // ---- modal --------------------------------------------------------------------------------

  function openModal(html) {
    el("org-modal-panel").innerHTML = html;
    el("org-modal").classList.remove("hidden");
  }

  function closeModal() {
    el("org-modal").classList.add("hidden");
    el("org-modal-panel").innerHTML = "";
    queueRender();
  }

  window.addEventListener("DOMContentLoaded", init);
  window.addEventListener("beforeunload", flushNoteSave);

  return { show, hide, renderMarkdown, occurrences, nextDue };
})();
