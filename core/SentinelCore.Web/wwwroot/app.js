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
const buildVersionEl = document.getElementById("build-version");

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

function plural(seconds) {
  return `${seconds} second${seconds === 1 ? "" : "s"}`;
}

// What the clip badge says, and whether it plays. Everything on this tab passed the AI check, so
// it should have video to go with it - the states that aren't "ready" each say why it doesn't yet
// or won't, rather than leaving a card that silently refuses to play.
function clipBadgeHtml(event) {
  const window = event.clipPreSeconds != null && event.clipPostSeconds != null
    ? ` The clip covers ${plural(event.clipPreSeconds)} before the trigger and ${plural(event.clipPostSeconds)} after, as set on this camera's trigger zone.`
    : "";

  switch (event.clipState) {
    case "ready":
      return `<span class="clip-badge" data-tip="A clip was recorded around this event. Click to play it.${window}">&#9654; clip</span>`;
    case "recording":
      return `<span class="clip-badge clip-badge-waiting" data-tip="Still recording the tail of this clip.${window} It'll be playable here within a few seconds - the list refreshes on its own.">&#9679; recording&hellip;</span>`;
    case "missing":
      return `<span class="clip-badge clip-badge-warning" data-tip="No clip was saved for this event, though this camera is set to record them. Usually means the rolling buffer had a gap - check the logs around this time.">&#9888; no clip</span>`;
    case "off":
      return `<span class="clip-badge clip-badge-muted" data-tip="Clip recording is off for this camera - turn on 'record clips' on its trigger zone to save video around events like this one.">no clip &mdash; recording off</span>`;
    default:
      return "";
  }
}

// One card per real-world moment, whatever records back it. An "analyzed" item has an AI summary
// and a notify decision; a "motion" item has neither and never will - nothing looked at it,
// because the camera doesn't forward to the alert pipeline or the forward was refused. Either can
// carry a clip, since that comes from the trigger side. See ActivityEndpoints for the join.
// Where a row came from, as the badge on its card. Three values (ActivityOrigins server-side):
// the cameras noticing something, the record button, or a recording window on the clock. Every
// card wears one, because a hand-started clip and a motion clip are otherwise identical.
const ORIGIN_BADGES = {
  alert: { label: "alert", tip: "The cameras noticed something - a motion zone, a sensor, or the trigger API - and this is what was kept." },
  manual: { label: "manual", tip: "Recorded on purpose with the record button in one of the apps. Runs from the press until it was stopped, or until the limit set in Settings." },
  scheduled: { label: "timer", tip: "Recorded by a timed recording window on this camera's trigger zone, whether or not anything moved." }
};

function originBadgeHtml(event) {
  const origin = ORIGIN_BADGES[event.origin] || ORIGIN_BADGES.alert;
  return `<span class="origin-badge origin-${escapeHtml(event.origin || "alert")}" data-tip="${origin.tip}">${origin.label}</span>`;
}

function renderCard(event) {
  const analyzed = event.kind === "analyzed";
  // Footage somebody asked for rather than something a camera noticed - see ActivityEndpoints.
  // The summary is the server's own line about it ("Recorded from the record button - 16s"), so
  // it takes the description slot rather than the muted "motion in zone" one.
  const recording = event.kind === "recording";

  const card = document.createElement("div");
  card.className = "card" + (event.notified ? " notified" : "");

  // ?size=card, not the frame as the camera sent it. These are 3840x2160 and 2560x1920 snapshots
  // averaging well over a megabyte, and this element is a card in a 320px-minimum grid - fifty of
  // them meant tens of megabytes downloaded and fifty multi-megapixel JPEG decodes on the main
  // thread, which is the same thread the live tiles append their video on. The full frame is still
  // one click away below, and what is stored on disk is untouched.
  const imageHtml = event.imageUrl
    ? `<img src="${event.imageUrl}?size=card" loading="lazy" decoding="async" alt="${escapeHtml(event.summary || event.zoneName || "camera snapshot")}" />`
    : `<div class="no-image">no image</div>`;

  const badgeHtml = event.notified
    ? `<span class="badge" data-tip="${escapeHtml(event.notificationReason) || "The AI judged this worth telling you about, so an alert was sent."}">notified</span>`
    : "";

  const clipHtml = clipBadgeHtml(event);

  // Only ever present with "show hidden" on - a confirmed event is the normal case and gets no
  // badge, so the list looks the same as it does with the filter doing its job quietly.
  const confirmationHtml = {
    rejected: `<span class="clip-badge confirmation-badge" data-tip="The AI looked at this frame and decided it wasn't worth showing you, so it's hidden from the normal list. Usually that means nothing was there - no person, vehicle, package or animal. On a camera set to &quot;only keep what the AI would alert on&quot; it can also mean something WAS there and was expected, like the chickens in a coop. The snapshot is kept either way; the video is deleted as soon as the verdict lands, unless the same clip also covers a trigger that was confirmed.">&#9888; filtered out</span>`,
    pending: `<span class="clip-badge confirmation-badge" data-tip="Still waiting on the AI to say whether anything was really here. It'll appear in the normal list if confirmed.">&#8230; checking</span>`,
    unverified: `<span class="clip-badge confirmation-badge" data-tip="Nothing ever judged this frame - the analysis failed, or the forward to it was refused. It's hidden from the normal list because it never passed the AI check, not because it failed one. The snapshot is kept; the video is kept too, until it ages out of the Unverified Motion Days window like a reject does.">&#9888; never checked</span>`
  }[event.confirmation] || "";

  // A camera with AI analysis switched off never gets a verdict, so its motion is kept here rather
  // than filtered out - but flagged, because a camera quietly dropping out of the AI filter looks
  // exactly like a camera that has stopped seeing anything.
  const aiOffHtml = event.aiAnalysisOff
    ? `<span class="clip-badge ai-off-badge" data-tip="AI analysis is off for this camera, so nothing looked at this frame. Turn on 'send to AI analysis' on its trigger zone to have events from this camera checked and described.">AI off</span>`
    : "";

  const linkedCamerasHtml = event.linkedCameras
    ? `<div class="card-meta" data-tip="A Camera Group also pulled in footage from these cameras when this one triggered.">🔗 Also recorded: ${escapeHtml(event.linkedCameras)}</div>`
    : "";

  const packageTheftHtml = event.packageTheftReason
    ? `<div class="card-meta card-warning" data-tip="Package Theft Watch flagged this alert.">📦 ${escapeHtml(event.packageTheftReason)}</div>`
    : "";

  // Motion rows have no description to show, so the zone that caught it takes that line instead -
  // it's the one thing that says why this frame exists.
  const summaryHtml = analyzed
    ? `<div class="card-summary" data-tip="The AI's description of what it saw in this snapshot.">${escapeHtml(event.summary) || "&mdash;"}</div>`
    : recording
      ? `<div class="card-summary" data-tip="A recording somebody asked for, not something the AI described. Click the picture to play it.">${escapeHtml(event.summary) || "recording"}</div>`
      : `<div class="card-summary card-muted" data-tip="Motion caught this frame, but nothing analyzed it - this camera doesn't send to AI analysis (Settings on the camera's trigger zone).">${escapeHtml(event.zoneName || "motion in zone")}</div>`;

  const footerLabel = analyzed
    ? `<span class="card-event-type" data-tip="What kind of event the AI classified this as.">${escapeHtml(event.eventType)}</span>`
    : recording
      ? `<span class="card-event-type" data-tip="Video that was recorded on request. Nothing analyzed it - it isn't an event the cameras noticed.">recording</span>`
      : `<span class="card-event-type" data-tip="No AI analysis exists for this one - motion only.">motion only</span>`;

  const imageTip = event.clipUrl
    ? "Click to play the clip recorded around this event."
    : event.clipState === "recording"
      ? "Click to view the full-size snapshot. The clip is still recording and will be playable here shortly."
      : event.imageUrl ? "Click to view the full-size snapshot. No clip was saved for this one." : "";

  card.innerHTML = `
    <div class="card-image"${imageTip ? ` data-tip="${imageTip}"` : ""}>
      ${imageHtml}
      ${badgeHtml}
      ${clipHtml}
      ${confirmationHtml}
      ${aiOffHtml}
    </div>
    <div class="card-body">
      <div class="card-meta">
        <span class="card-camera" data-tip="Which camera this came from.">${escapeHtml(event.camera)}</span>
        <span title="${escapeHtml(event.occurredAt)}">${timeAgo(event.occurredAt)}</span>
      </div>
      ${summaryHtml}
      ${linkedCamerasHtml}
      ${packageTheftHtml}
      <div class="card-footer">
        ${footerLabel}
        ${originBadgeHtml(event)}
        <span>${formatClock(event.occurredAt)}</span>
      </div>
    </div>
  `;

  const img = card.querySelector(".card-image img");
  if (img) {
    img.addEventListener("click", () => {
      if (event.clipUrl) openLightboxVideo(event.clipUrl);
      // event.imageUrl rather than img.src: the card now holds the card-sized copy, and the
      // lightbox is the one place that genuinely wants every pixel the camera recorded - it is
      // where someone reads a plate or checks a face.
      else openLightbox(event.imageUrl);
    });
  }

  return card;
}

function escapeHtml(value) {
  // Deliberately a string replace rather than the textContent/innerHTML trick this used to be.
  // That trick escapes exactly what a *text node* needs - & < > - and quotes are not among them,
  // because a quote means nothing in text. Half the interpolations here are into attributes
  // (alt=, title=, data-tip=, value=), and there a surviving double quote ends the attribute and
  // everything after it is parsed as more attributes. Confirmed against a real DOM: an event
  // summary reading `A man held a sign reading " onerror=alert(1) x="` produced an <img> carrying
  // an onerror the markup never contained.
  //
  // Vision models write these summaries, and they quote things - a sign, a brand, a plate - so
  // the merely-broken version of this happens without anybody attacking anything.
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
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
const filterVisualSearchEl = document.getElementById("filter-visual-search");
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

// Deliberately not persisted across reloads: the confirmed-only list is the one to be looking at
// day to day, and this is for spot-checking what the AI filter threw away.
let showHidden = false;

function buildFilterQuery() {
  const params = new URLSearchParams({ limit: "50" });
  if (showHidden) params.set("includeUnconfirmed", "true");
  if (filterSearchEl.value.trim()) params.set("search", filterSearchEl.value.trim());
  if (filterCameraEl.value) params.set("camera", filterCameraEl.value);
  if (filterEventTypeEl.value) params.set("eventType", filterEventTypeEl.value);
  if (filterNotifiedOnlyEl.checked) params.set("notifiedOnly", "true");
  if (filterVisualSearchEl.checked && filterSearchEl.value.trim()) params.set("visualSearch", "true");
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
for (const el of [filterCameraEl, filterEventTypeEl, filterFromEl, filterToEl, filterNotifiedOnlyEl, filterVisualSearchEl]) {
  el.addEventListener("change", loadEvents);
}
filterClearBtn.addEventListener("click", () => {
  filterSearchEl.value = "";
  filterCameraEl.value = "";
  filterEventTypeEl.value = "";
  filterFromEl.value = "";
  filterToEl.value = "";
  filterNotifiedOnlyEl.checked = false;
  filterVisualSearchEl.checked = false;
  loadEvents();
});

// ---------------- Feed loading ----------------

async function loadEvents() {
  try {
    const response = await fetch(`/api/activity?${buildFilterQuery()}`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const events = await response.json();

    setStatus(true);
    eventCountEl.textContent = `${events.length} event${events.length === 1 ? "" : "s"}`;

    feedEl.innerHTML = "";
    if (events.length === 0) {
      feedEl.innerHTML = hasActiveFilters()
        ? `<div class="empty-state">no confirmed events match these filters</div>`
        : showHidden
          ? `<div class="empty-state">no events recorded yet</div>`
          : `<div class="empty-state">no confirmed events yet &mdash; this tab only lists what the AI agreed was really there. Use "show hidden" to see what was filtered out and why.</div>`;
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

// Fetched once at load, not polled: the version can't change without the server restarting,
// which drops the connection and gets the page reloaded anyway. On failure the footer is left
// blank rather than showing an error - the status indicator in the topbar is what tells you the
// dashboard has lost the server, and a second complaint down here adds nothing.
async function loadBuildVersion() {
  try {
    const response = await fetch("/api/health");
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const { build } = await response.json();
    if (!build?.display) return;

    buildVersionEl.textContent = `build ${build.display}`;
    buildVersionEl.classList.toggle("dirty", build.isDirty === true);
  } catch (err) {
    console.error("Failed to load build version", err);
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

const showHiddenBtn = document.getElementById("show-hidden-btn");
showHiddenBtn.addEventListener("click", () => {
  showHidden = !showHidden;
  showHiddenBtn.classList.toggle("btn-on", showHidden);
  showHiddenBtn.textContent = showHidden ? "hide hidden" : "show hidden";
  loadEvents();
});

loadFilterOptions();
loadEvents();
loadQueueStatus();
loadBuildVersion();
setInterval(loadEvents, REFRESH_INTERVAL_MS);
setInterval(loadQueueStatus, REFRESH_INTERVAL_MS);
window.GateWatchWizard.checkAndMaybeShow();
window.GateWatchAuth.check();
window.GateWatchLicense.check();
window.GateWatchExposure.check();

// Tab switching. Settings/cameras are lazy-loaded on first visit; the live view starts/stops
// video playback as it's shown/hidden so streams nobody is looking at don't keep running.
// Scoped to the top nav on purpose: the Automations view has its own .tab buttons for its two
// sub-panes, and a bare ".tab" selector picked those up too. Clicking one read an undefined
// data-view, which hid every view at once and blanked the page.
const tabs = document.querySelectorAll("#tabs .tab");
const views = {
  events: document.getElementById("view-events"),
  timeline: document.getElementById("view-timeline"),
  live: document.getElementById("view-live"),
  cameras: document.getElementById("view-cameras"),
  automations: document.getElementById("view-automations"),
  devices: document.getElementById("view-devices"),
  alarm: document.getElementById("view-alarm"),
  docs: document.getElementById("view-docs"),
  settings: document.getElementById("view-settings"),
  accounts: document.getElementById("view-accounts"),
  organizer: document.getElementById("view-organizer"),
  movies: document.getElementById("view-movies"),
  music: document.getElementById("view-music"),
  podcasts: document.getElementById("view-podcasts"),
  weather: document.getElementById("view-weather"),
  files: document.getElementById("view-files")
};
let settingsLoaded = false;
let camerasLoaded = false;
let timelineLoaded = false;
let currentView = "events";

for (const tab of tabs) {
  tab.addEventListener("click", async () => {
    const target = tab.dataset.view;
    for (const t of tabs) t.classList.toggle("active", t === tab);
    for (const [name, el] of Object.entries(views)) el.classList.toggle("hidden", name !== target);

    if (currentView === "live" && target !== "live") window.GateWatchCameras.hideLive();
    if (currentView === "timeline" && target !== "timeline") window.GateWatchTimeline.stop();
    // Device state polls every few seconds while the tab is open - stop it the moment it isn't.
    if (currentView === "devices" && target !== "devices") window.GateWatchDevices.stop();
    if (currentView === "organizer" && target !== "organizer") window.SentinelOrganizer.hide();
    // Closes the player and stops the scan-status poll - a film must not keep streaming (and
    // reporting progress) behind the Events tab.
    if (currentView === "movies" && target !== "movies") window.SentinelMovies.hide();
    // The weather asks weather.gov every ten minutes while it's open - and only while it's open.
    if (currentView === "weather" && target !== "weather") window.SentinelWeather.hide();
    if (currentView === "alarm" && target !== "alarm") window.SentinelAlarm.stop();
    if (currentView === "files" && target !== "files") window.SentinelFiles.hide();
    // Leaving Music or Podcasts only stops their polling - the player bar keeps playing on
    // every tab, which is the point of it.
    if (currentView === "music" && target !== "music") window.SentinelMusic.hide();
    if (currentView === "podcasts" && target !== "podcasts") window.SentinelPodcasts.hide();

    if (target === "settings" && !settingsLoaded) {
      settingsLoaded = true;
      window.GateWatchSettings.load();
    }
    // Set from the result, not ahead of it. Latching the flag before the await meant a first load
    // that failed - one dropped request on a flaky connection - was remembered as a load that had
    // happened, so every later visit to Cameras or Live skipped the fetch and rendered an empty
    // list. It never recovered on its own: only saving a camera, removing a group, or reloading
    // the page called load() again. A false flag just costs one retry on the next visit.
    if ((target === "cameras" || target === "live") && !camerasLoaded) {
      camerasLoaded = await window.GateWatchCameras.load();
    }
    if (target === "cameras") window.GateWatchCameras.renderList();
    if (target === "live") window.GateWatchCameras.showLive();
    if (target === "events") loadEvents();
    // Two panes share this tab - rules and schedules - so the one actually on screen is the one
    // that gets reloaded, rather than always the rules.
    if (target === "automations") window.SentinelSchedules.loadActivePane();
    if (target === "devices") window.GateWatchDevices.load();
    if (target === "docs") window.GateWatchDocs.load();
    if (target === "organizer") window.SentinelOrganizer.show();
    // Reads the rest of the hash itself (#movies/item/<id>, #movies/shows, ...), so a refresh
    // or the back button lands on the same film, not just the same tab.
    if (target === "movies") window.SentinelMovies.show();
    if (target === "weather") window.SentinelWeather.show();
    if (target === "alarm") window.SentinelAlarm.show();
    if (target === "files") window.SentinelFiles.show();
    // Both read the rest of the hash themselves (#music/album/<id>, #podcasts/show/<id>, ...).
    if (target === "music") window.SentinelMusic.show();
    if (target === "podcasts") window.SentinelPodcasts.show();
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

// A link can land on a tab: /#cameras opens Cameras. The webcam picker's "open the secure
// address" button relies on it, so changing address doesn't also mean finding the tab again -
// the person following that button has already been told enough steps.
//
// Deferred to DOMContentLoaded rather than run here: the click handler above reaches for
// window.GateWatchCameras, and cameras.js is loaded after this file.
//
// /#wall is the Device Hub's wall display: a tablet mounted on a wall gets bookmarked (or added
// to its home screen) with this so a reload comes straight back to the big tiles, and the
// preference is set before the tab opens so the normal grid never flashes first. Also answered
// on hashchange, so typing it into a dashboard that's already open works the same way.
function landOnHash() {
  let wanted = location.hash.replace(/^#/, "");
  if (wanted === "wall") {
    window.GateWatchDevices.setDisplay("wall");
    wanted = "devices";
  }
  // /#movies/item/<id> and the other Movies sub-routes all land on the Movies tab; movies.js
  // reads the part after the slash when its show() runs.
  if (wanted.startsWith("movies/")) wanted = "movies";
  if (wanted.startsWith("music/")) wanted = "music";
  if (wanted.startsWith("podcasts/")) wanted = "podcasts";
  const landing = wanted && Array.from(tabs).find((tab) => tab.dataset.view === wanted);
  if (landing) landing.click();
}

window.addEventListener("DOMContentLoaded", landOnHash);
window.addEventListener("hashchange", landOnHash);
