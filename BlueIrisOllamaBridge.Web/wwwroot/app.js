const REFRESH_INTERVAL_MS = 30_000;

const feedEl = document.getElementById("feed");
const eventCountEl = document.getElementById("event-count");
const statusDotEl = document.getElementById("status-dot");
const statusTextEl = document.getElementById("status-text");
const refreshBtn = document.getElementById("refresh-btn");
const lightbox = document.getElementById("lightbox");
const lightboxImg = document.getElementById("lightbox-img");

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

  const badgeHtml = event.notified ? `<span class="badge">notified</span>` : "";

  card.innerHTML = `
    <div class="card-image">
      ${imageHtml}
      ${badgeHtml}
    </div>
    <div class="card-body">
      <div class="card-meta">
        <span class="card-camera">${escapeHtml(event.camera)}</span>
        <span title="${escapeHtml(event.occurredAt)}">${timeAgo(event.occurredAt)}</span>
      </div>
      <div class="card-summary">${escapeHtml(event.summary) || "&mdash;"}</div>
      <div class="card-footer">
        <span class="card-event-type">${escapeHtml(event.eventType)}</span>
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

function openLightbox(src) {
  lightboxImg.src = src;
  lightbox.classList.remove("hidden");
}

lightbox.addEventListener("click", () => {
  lightbox.classList.add("hidden");
  lightboxImg.src = "";
});

async function loadEvents() {
  try {
    const response = await fetch("/api/events?limit=50");
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const events = await response.json();

    setStatus(true);
    eventCountEl.textContent = `${events.length} event${events.length === 1 ? "" : "s"}`;

    feedEl.innerHTML = "";
    if (events.length === 0) {
      feedEl.innerHTML = `<div class="empty-state">no events recorded yet</div>`;
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

refreshBtn.addEventListener("click", loadEvents);

loadEvents();
setInterval(loadEvents, REFRESH_INTERVAL_MS);

// Tab switching. Settings/cameras are lazy-loaded on first visit; the live view starts/stops
// video playback as it's shown/hidden so streams nobody is looking at don't keep running.
const tabs = document.querySelectorAll(".tab");
const views = {
  feed: document.getElementById("view-feed"),
  live: document.getElementById("view-live"),
  cameras: document.getElementById("view-cameras"),
  triggers: document.getElementById("view-triggers"),
  settings: document.getElementById("view-settings")
};
let settingsLoaded = false;
let camerasLoaded = false;
let currentView = "feed";

for (const tab of tabs) {
  tab.addEventListener("click", async () => {
    const target = tab.dataset.view;
    for (const t of tabs) t.classList.toggle("active", t === tab);
    for (const [name, el] of Object.entries(views)) el.classList.toggle("hidden", name !== target);

    if (currentView === "live" && target !== "live") window.GateWatchCameras.hideLive();

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

    currentView = target;
  });
}
