const REFRESH_INTERVAL_MS = 30_000;

const feedEl = document.getElementById("feed");
const eventCountEl = document.getElementById("event-count");
const statusDotEl = document.getElementById("status-dot");
const statusTextEl = document.getElementById("status-text");
const refreshBtn = document.getElementById("refresh-btn");
const queueStatusEl = document.getElementById("queue-status");
const clearQueueBtn = document.getElementById("clear-queue-btn");
const lightbox = document.getElementById("lightbox");
const lightboxImg = document.getElementById("lightbox-img");
const askInput = document.getElementById("ask-input");
const askBtn = document.getElementById("ask-btn");
const askAnswerEl = document.getElementById("ask-answer");

function setStatus(online) {
  statusDotEl.classList.toggle("online", online);
  statusDotEl.classList.toggle("offline", !online);
  statusTextEl.textContent = online ? "link established" : "link lost";
}

function timeAgo(iso) {
  const then = new Date(iso).getTime();
  const diffSeconds = Math.max(0, Math.floor((Date.now() - then) / 1000));
  if (diffSeconds < 60) return `${diffSeconds}s ago`;
  if (diffSeconds < 3600) return `${Math.floor(diffSeconds / 60)}m ago`;
  if (diffSeconds < 86400) return `${Math.floor(diffSeconds / 3600)}h ago`;
  return `${Math.floor(diffSeconds / 86400)}d ago`;
}

function formatClock(iso) {
  return new Date(iso).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}

function renderCard(event) {
  const card = document.createElement("div");
  card.className = "card" + (event.notified ? " notified" : "");

  const imageHtml = event.hasImage
    ? `<img src="/api/events/${event.id}/image" alt="${escapeHtml(event.summary)}" />`
    : `<div class="no-image">no image</div>`;

  const badgeHtml = event.notified
    ? `<span class="badge" data-tip="${escapeHtml(event.notificationReason) || "The AI judged this worth telling you about, so an alert was sent."}">notified</span>`
    : "";

  card.innerHTML = `
    <div class="card-image"${event.hasImage ? ` data-tip="Click to view the full-size snapshot."` : ""}>
      ${imageHtml}
      ${badgeHtml}
    </div>
    <div class="card-body">
      <div class="card-meta">
        <span class="card-camera" data-tip="Which Blue Iris camera this came from.">${escapeHtml(event.camera)}</span>
        <span title="${escapeHtml(event.occurredAt)}">${timeAgo(event.occurredAt)}</span>
      </div>
      <div class="card-summary" data-tip="The AI's description of what it saw in this snapshot.">${escapeHtml(event.summary) || "&mdash;"}</div>
      <div class="card-footer">
        <span class="card-event-type" data-tip="What kind of event the AI classified this as.">${escapeHtml(event.eventType)}</span>
        <span>${formatClock(event.occurredAt)}</span>
      </div>
    </div>
  `;

  if (event.hasImage) {
    const img = card.querySelector(".card-image img");
    img.addEventListener("click", () => openLightbox(img.src));
  }

  return card;
}

function escapeHtml(value) {
  const div = document.createElement("div");
  div.textContent = value ?? "";
  return div.innerHTML;
}

// Tooltips render to the right of their element by default, which runs off-screen for anything
// near the right edge. Rather than hand-tagging each one - and missing the dynamically rendered
// ones - measure and flip them to right-aligned as needed.
const TIP_WIDTH = 300; // max-width plus padding and border

function anchorTooltips() {
  const viewportWidth = document.documentElement.clientWidth;
  for (const el of document.querySelectorAll("[data-tip]")) {
    const box = el.getBoundingClientRect();
    if (!box.width) continue;
    const runsOffRight = box.left + TIP_WIDTH > viewportWidth;
    const flippingFits = box.right - TIP_WIDTH >= 0;
    el.toggleAttribute("data-tip-right", runsOffRight && flippingFits);
  }
}

let tipAnchorTimer;
function scheduleTooltipAnchoring() {
  clearTimeout(tipAnchorTimer);
  tipAnchorTimer = setTimeout(anchorTooltips, 60);
}
window.GateWatchTooltips = { schedule: scheduleTooltipAnchoring };

// childList covers elements being added (the zone editor's fields, camera/trigger cards, etc.).
// It does NOT cover a class toggle like switching the active tab or opening a settings section -
// those don't add or remove nodes, so the spots that do them call scheduleTooltipAnchoring()
// directly (see the tab-click handler below and settings.js/triggers.js).
new MutationObserver(scheduleTooltipAnchoring).observe(document.body, { childList: true, subtree: true });
window.addEventListener("resize", scheduleTooltipAnchoring);
anchorTooltips();

const lightboxVideo = document.getElementById("lightbox-video");

function openLightbox(src) {
  lightboxImg.src = src;
  lightboxImg.classList.remove("hidden");
  lightboxVideo.classList.add("hidden");
  lightbox.classList.remove("hidden");
}

function openLightboxVideo(src) {
  lightboxVideo.src = src;
  lightboxVideo.classList.remove("hidden");
  lightboxImg.classList.add("hidden");
  lightbox.classList.remove("hidden");
}

// The backdrop closes the lightbox, but clicks that land on the player itself are the user
// working the scrubber/controls - those must not count as "clicked outside".
lightboxVideo.addEventListener("click", e => e.stopPropagation());

lightbox.addEventListener("click", () => {
  lightbox.classList.add("hidden");
  lightboxImg.src = "";
  lightboxVideo.pause();
  lightboxVideo.removeAttribute("src");
  lightboxVideo.load();
});

// ---------------- Feed filters ----------------

const filterSearchEl = document.getElementById("filter-search");
const filterCameraEl = document.getElementById("filter-camera");
const filterEventTypeEl = document.getElementById("filter-event-type");
const filterFromEl = document.getElementById("filter-from");
const filterToEl = document.getElementById("filter-to");
const filterNotifiedOnlyEl = document.getElementById("filter-notified-only");
const filterClearBtn = document.getElementById("filter-clear-btn");

function prettifyEventType(value) {
  return value.replace(/_/g, " ");
}

async function loadFilterOptions() {
  try {
    const response = await fetch("/api/events/filter-options");
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const { cameras, eventTypes } = await response.json();

    for (const camera of cameras) {
      const opt = document.createElement("option");
      opt.value = camera;
      opt.textContent = camera;
      filterCameraEl.appendChild(opt);
    }
    for (const eventType of eventTypes) {
      const opt = document.createElement("option");
      opt.value = eventType;
      opt.textContent = prettifyEventType(eventType);
      filterEventTypeEl.appendChild(opt);
    }
  } catch (err) {
    console.error("Failed to load filter options", err);
  }
}

// A local-date <input type="date"> value ("YYYY-MM-DD") is turned into a local-midnight ISO
// timestamp rather than passed through Date's UTC-parsing constructor, so the filter lines up
// with the browser's own timezone rather than shifting a day depending on where the server is.
// endOfDay bumps to the start of the next day so the "to" filter includes the whole selected day
// (the backend treats "to" as an exclusive upper bound).
function dateInputToIso(value, endOfDay) {
  if (!value) return null;
  const [y, m, d] = value.split("-").map(Number);
  return new Date(y, m - 1, d + (endOfDay ? 1 : 0)).toISOString();
}

function buildFilterQuery() {
  const params = new URLSearchParams({ limit: "50" });
  if (filterSearchEl.value.trim()) params.set("search", filterSearchEl.value.trim());
  if (filterCameraEl.value) params.set("camera", filterCameraEl.value);
  if (filterEventTypeEl.value) params.set("eventType", filterEventTypeEl.value);
  if (filterNotifiedOnlyEl.checked) params.set("notifiedOnly", "true");
  const fromIso = dateInputToIso(filterFromEl.value, false);
  const toIso = dateInputToIso(filterToEl.value, true);
  if (fromIso) params.set("from", fromIso);
  if (toIso) params.set("to", toIso);
  return params;
}

function hasActiveFilters() {
  return !!(filterSearchEl.value.trim() || filterCameraEl.value || filterEventTypeEl.value ||
    filterFromEl.value || filterToEl.value || filterNotifiedOnlyEl.checked);
}

let searchDebounceTimer;
filterSearchEl.addEventListener("input", () => {
  clearTimeout(searchDebounceTimer);
  searchDebounceTimer = setTimeout(loadEvents, 300);
});
for (const el of [filterCameraEl, filterEventTypeEl, filterFromEl, filterToEl, filterNotifiedOnlyEl]) {
  el.addEventListener("change", loadEvents);
}
filterClearBtn.addEventListener("click", () => {
  filterSearchEl.value = "";
  filterCameraEl.value = "";
  filterEventTypeEl.value = "";
  filterFromEl.value = "";
  filterToEl.value = "";
  filterNotifiedOnlyEl.checked = false;
  loadEvents();
});

// ---------------- Feed loading ----------------

async function loadEvents() {
  try {
    const response = await fetch(`/api/events?${buildFilterQuery()}`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const events = await response.json();

    setStatus(true);
    eventCountEl.textContent = `${events.length} event${events.length === 1 ? "" : "s"}`;

    feedEl.innerHTML = "";
    if (events.length === 0) {
      feedEl.innerHTML = hasActiveFilters()
        ? `<div class="empty-state">no events match these filters</div>`
        : `<div class="empty-state">no events recorded yet</div>`;
      return;
    }

    for (const event of events) {
      feedEl.appendChild(renderCard(event));
    }
  } catch (err) {
    setStatus(false);
    console.error("Failed to load events", err);
  }
}

// Only visible when something is actually waiting - most people never see this, only when the
// queue has genuinely fallen behind. Polled on the same cadence as the feed itself.
async function loadQueueStatus() {
  try {
    const response = await fetch("/api/queue/status");
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const { queued } = await response.json();

    const show = queued > 0;
    queueStatusEl.classList.toggle("hidden", !show);
    clearQueueBtn.classList.toggle("hidden", !show);
    if (show) queueStatusEl.textContent = `${queued} queued`;
  } catch (err) {
    console.error("Failed to load queue status", err);
  }
}

async function clearQueue() {
  if (!confirm("Discard everything waiting in the analysis queue? Whatever is already being analyzed finishes normally - this only clears what's still waiting behind it. This cannot be undone.")) {
    return;
  }

  clearQueueBtn.disabled = true;
  clearQueueBtn.textContent = "clearing…";

  try {
    const response = await fetch("/api/queue/clear", { method: "POST" });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      alert(payload.error || "Could not clear the queue just now.");
      return;
    }
    await loadQueueStatus();
    await loadEvents();
  } catch {
    alert("Network error while clearing the queue.");
  } finally {
    clearQueueBtn.disabled = false;
    clearQueueBtn.textContent = "clear queue";
  }
}

// Stateless like the backend already is - each question replaces whatever answer is showing,
// no running conversation history to manage.
async function askQuestion() {
  const question = askInput.value.trim();
  if (!question) return;

  askBtn.disabled = true;
  askAnswerEl.classList.remove("hidden");
  askAnswerEl.classList.add("thinking");
  askAnswerEl.textContent = "thinking…";

  try {
    const response = await fetch("/api/chat/ask", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ question })
    });
    const payload = await response.json().catch(() => ({}));
    askAnswerEl.classList.remove("thinking");
    askAnswerEl.textContent = response.ok
      ? payload.answer
      : (payload.error || "Could not get an answer just now.");
  } catch {
    askAnswerEl.classList.remove("thinking");
    askAnswerEl.textContent = "Network error while asking.";
  } finally {
    askBtn.disabled = false;
  }
}

askBtn.addEventListener("click", askQuestion);
askInput.addEventListener("keydown", (e) => { if (e.key === "Enter") askQuestion(); });

refreshBtn.addEventListener("click", loadEvents);
clearQueueBtn.addEventListener("click", clearQueue);

loadFilterOptions();
loadEvents();
loadQueueStatus();
setInterval(loadEvents, REFRESH_INTERVAL_MS);
setInterval(loadQueueStatus, REFRESH_INTERVAL_MS);
window.GateWatchWizard.checkAndMaybeShow();
window.GateWatchAuth.check();
window.GateWatchLicense.check();
window.GateWatchExposure.check();

// Tab switching. Settings/cameras are lazy-loaded on first visit; the live view starts/stops
// video playback as it's shown/hidden so streams nobody is looking at don't keep running.
const tabs = document.querySelectorAll(".tab");
const views = {
  feed: document.getElementById("view-feed"),
  timeline: document.getElementById("view-timeline"),
  live: document.getElementById("view-live"),
  cameras: document.getElementById("view-cameras"),
  triggers: document.getElementById("view-triggers"),
  settings: document.getElementById("view-settings"),
  accounts: document.getElementById("view-accounts")
};
let settingsLoaded = false;
let camerasLoaded = false;
let timelineLoaded = false;
let currentView = "feed";

for (const tab of tabs) {
  tab.addEventListener("click", async () => {
    const target = tab.dataset.view;
    for (const t of tabs) t.classList.toggle("active", t === tab);
    for (const [name, el] of Object.entries(views)) el.classList.toggle("hidden", name !== target);

    if (currentView === "live" && target !== "live") window.GateWatchCameras.hideLive();
    if (currentView === "timeline" && target !== "timeline") window.GateWatchTimeline.stop();

    if (target === "settings" && !settingsLoaded) {
      settingsLoaded = true;
      window.GateWatchSettings.load();
    }
    if ((target === "cameras" || target === "live") && !camerasLoaded) {
      camerasLoaded = true;
      await window.GateWatchCameras.load();
    }
    if (target === "cameras") window.GateWatchCameras.renderList();
    if (target === "live") window.GateWatchCameras.showLive();
    if (target === "triggers") window.GateWatchTriggers.load();
    if (target === "timeline") {
      if (!timelineLoaded) timelineLoaded = true;
      window.GateWatchTimeline.load();
    }
    if (target === "accounts") {
      await window.GateWatchAccounts.load();
      window.GateWatchAccounts.renderList();
    }

    currentView = target;
    // Switching tabs only toggles the "active"/"hidden" classes above - no nodes are added or
    // removed, so the MutationObserver in anchorTooltips() never sees it. Nudge it directly so
    // a tooltip that was fine on the old tab doesn't run off-screen on the new one.
    scheduleTooltipAnchoring();
  });
}
