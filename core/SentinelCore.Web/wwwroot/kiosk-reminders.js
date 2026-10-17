// Household reminders on the wall kiosk.
//
// Reminders for Everyone (the Family space) or for the Admins that are due today or overdue sit
// across the top of the kiosk, the way a weather warning does - a calm strip rather than a
// flashing one, under the weather banner when both are up. Each has a ✓ that ticks it off for
// everyone (a repeating one moves on to its next time, as in the Reminders tab), and × hides the
// strip on this screen until tomorrow. Personal and other groups' reminders never show here.
//
// The list comes from the server (/api/kiosk/reminders) rather than the kiosk's own Organizer
// copy, because the kiosk is usually signed in as a viewer and can't see the Admins space.
//
// When a reminder with a time comes due, the screen wakes for a couple of minutes so the person
// walking past sees it.
(function () {
  const POLL_MS = 60_000;
  const WAKE_MS = 2 * 60_000;
  const MAX_ROWS = 3;
  const HIDDEN_KEY = "sentinelcore.kiosk.remindersHidden";

  const isKiosk = () => document.documentElement.classList.contains("kiosk");

  let reminders = [];
  let firstPoll = true;
  // Timed reminders already seen as due, so each wakes the screen once.
  const announced = new Set();
  let wakeUntil = 0;

  const pad = (n) => String(n).padStart(2, "0");
  const ymd = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

  function hiddenDay() {
    try { return localStorage.getItem(HIDDEN_KEY) || ""; } catch { return ""; }
  }

  function hideForToday() {
    try { localStorage.setItem(HIDDEN_KEY, ymd(new Date())); } catch { /* storage blocked */ }
    update();
  }

  // When it's due as a moment: an all-day one counts from the start of its day.
  function dueAt(r) {
    return r.allDay ? new Date(`${r.due}T00:00`) : new Date(r.due);
  }

  // Due today, or overdue.
  function current(r, now) {
    return r.allDay ? r.due <= ymd(now) : ymd(dueAt(r)) <= ymd(now);
  }

  function dueWords(r, now) {
    const at = dueAt(r);
    const today = ymd(now);
    const day = r.allDay ? r.due : ymd(at);
    if (day < today) {
      return `overdue since ${at.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" })}`;
    }
    if (r.allDay) return "today";
    const time = at.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    return at <= now ? `due ${time}` : `at ${time}`;
  }

  const banner = document.createElement("div");
  banner.className = "krm-banner hidden";
  banner.setAttribute("role", "status");
  banner.innerHTML = `
    <div class="krm-head">
      <svg class="krm-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 8a6 6 0 0112 0c0 7 3 9 3 9H3s3-2 3-9"/><path d="M10.3 21a1.94 1.94 0 003.4 0"/></svg>
      <span class="krm-heading">Reminders</span>
      <span class="krm-more"></span>
      <button type="button" class="krm-close" aria-label="hide until tomorrow" title="hide until tomorrow">×</button>
    </div>
    <div class="krm-rows"></div>`;
  document.body.appendChild(banner);
  banner.querySelector(".krm-close").addEventListener("click", hideForToday);

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  }

  function update() {
    const now = new Date();
    const shown = isKiosk() && hiddenDay() !== ymd(now)
      ? reminders.filter((r) => current(r, now)).sort((a, b) => dueAt(a) - dueAt(b) || (b.priority || 0) - (a.priority || 0))
      : [];

    // Wake for a timed reminder whose moment has just come (not ones already due at page load).
    for (const r of reminders) {
      if (r.allDay || dueAt(r) > now || announced.has(r.id + r.due)) continue;
      announced.add(r.id + r.due);
      if (!firstPoll && isKiosk()) wakeUntil = Date.now() + WAKE_MS;
    }
    if (window.KeepAwake) KeepAwake.set("reminder", Date.now() < wakeUntil);

    if (!shown.length) {
      banner.classList.add("hidden");
      return;
    }
    banner.querySelector(".krm-more").textContent = shown.length > MAX_ROWS ? `+${shown.length - MAX_ROWS} more` : "";
    banner.querySelector(".krm-rows").innerHTML = shown.slice(0, MAX_ROWS).map((r) => `
      <div class="krm-row${dueAt(r) < now && ymd(dueAt(r)) < ymd(now) ? " krm-overdue" : ""}">
        <span class="krm-who krm-who-${escapeHtml(r.audience)}">${r.audience === "admins" ? "admins" : "everyone"}</span>
        <span class="krm-title">${r.priority >= 3 ? '<b class="krm-bang">!</b> ' : ""}${escapeHtml(r.title)}</span>
        <span class="krm-when">${escapeHtml(dueWords(r, now))}</span>
        <button type="button" class="krm-done" data-done="${escapeHtml(r.id)}" aria-label="mark done" title="done - for everyone">✓</button>
      </div>`).join("");
    for (const button of banner.querySelectorAll("[data-done]")) {
      button.addEventListener("click", () => done(reminders.find((r) => r.id === button.dataset.done), button));
    }
    banner.classList.remove("hidden");
  }

  async function done(reminder, button) {
    if (!reminder) return;
    button.disabled = true;
    // A repeating reminder's next time, worked out by the same code as the Reminders tab.
    let nextDue = null;
    if (reminder.recurrence && window.SentinelOrganizer?.nextDue) {
      nextDue = window.SentinelOrganizer.nextDue({ fields: { due: reminder.due, allDay: reminder.allDay, recurrence: reminder.recurrence } });
    }
    try {
      const response = await fetch(`/api/kiosk/reminders/${encodeURIComponent(reminder.id)}/done`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ nextDue }),
      });
      if (!response.ok) throw new Error();
      reminders = reminders.filter((r) => r.id !== reminder.id);
      update();
      poll();
    } catch {
      button.disabled = false;
    }
  }

  async function poll() {
    if (!isKiosk()) return;
    try {
      const response = await fetch("/api/kiosk/reminders", { cache: "no-store" });
      // Signed out (401) or an older server (404): nothing to show.
      if (!response.ok) {
        if (response.status === 401 || response.status === 404) { reminders = []; update(); }
        return;
      }
      reminders = (await response.json()).reminders || [];
    } catch {
      return;   // server unreachable: keep what's showing
    }
    update();
    firstPoll = false;
  }

  let wasKiosk = isKiosk();
  new MutationObserver(() => {
    if (isKiosk() === wasKiosk) return;
    wasKiosk = isKiosk();
    if (wasKiosk) poll();
    update();
  }).observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });

  // "At 5:00" turns into "due 5:00", and a timed one's moment arrives, between polls.
  setInterval(update, 30_000);
  setInterval(poll, POLL_MS);
  window.addEventListener("DOMContentLoaded", poll);
})();
