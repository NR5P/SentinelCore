(() => {
  const cameraEl = document.getElementById("timeline-camera");
  const dateEl = document.getElementById("timeline-date");
  const countEl = document.getElementById("timeline-count");
  const statusEl = document.getElementById("timeline-recording-status");
  const frameEl = document.getElementById("timeline-frame");
  const frameEmptyEl = document.getElementById("timeline-frame-empty");
  const rangesEl = document.getElementById("timeline-ranges");
  const markersEl = document.getElementById("timeline-markers");
  const cursorEl = document.getElementById("timeline-cursor");
  const scrubberEl = document.getElementById("timeline-scrubber");
  const clockEl = document.getElementById("timeline-clock");
  const playEl = document.getElementById("timeline-play");
  const eventsEl = document.getElementById("timeline-events");
  const categoryEl = document.getElementById("timeline-category");
  const severityEl = document.getElementById("timeline-severity");
  const reviewedEl = document.getElementById("timeline-reviewed");
  const skipGapsEl = document.getElementById("timeline-skip-gaps");

  let day = null;
  let playing = false;
  let timer = null;
  let frameTimer = null;

  function localDateString(date = new Date()) {
    const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
    return local.toISOString().slice(0, 10);
  }

  function filteredEvents() {
    if (!day) return [];
    return day.events.filter(event =>
      (!categoryEl.value || event.category === categoryEl.value) &&
      (!severityEl.value || event.severity === severityEl.value) &&
      (!reviewedEl.value || String(event.reviewed) === reviewedEl.value));
  }

  function totalSeconds() {
    return day ? (new Date(day.end) - new Date(day.start)) / 1000 : 86400;
  }

  function secondsFor(iso) {
    return Math.max(0, Math.min(totalSeconds() - 1, (new Date(iso) - new Date(day.start)) / 1000));
  }

  function atSeconds(seconds) {
    return new Date(new Date(day.start).getTime() + seconds * 1000);
  }

  function segmentForEvent(event) {
    return day?.recording.segments.find(segment => segment.eventId === event.id);
  }

  function segmentAt(date) {
    return day?.recording.segments.find(segment =>
      new Date(segment.start) <= date && new Date(segment.end) >= date);
  }

  async function loadCameras() {
    const response = await fetch("/api/timeline/cameras");
    if (!response.ok) throw new Error("Could not load timeline cameras.");
    const cameras = await response.json();
    const previous = cameraEl.value;
    cameraEl.innerHTML = cameras.map(camera =>
      `<option value="${escapeHtml(camera)}">${escapeHtml(camera)}</option>`).join("");
    if (cameras.includes(previous)) cameraEl.value = previous;
  }

  async function load() {
    stop();
    try {
      statusEl.textContent = "loading trigger clips…";
      frameEl.removeAttribute("src");
      frameEl.classList.add("hidden");
      frameEmptyEl.classList.remove("hidden");
      frameEmptyEl.textContent = "loading day…";
      if (!dateEl.value) dateEl.value = localDateString();
      if (!cameraEl.options.length) await loadCameras();
      if (!cameraEl.value) {
        eventsEl.innerHTML = `<div class="empty-state">no event cameras are available</div>`;
        return;
      }
      const query = new URLSearchParams({ camera: cameraEl.value, date: dateEl.value });
      const response = await fetch(`/api/timeline/day?${query}`);
      if (!response.ok) throw new Error((await response.json()).error || "Timeline request failed.");
      day = await response.json();
      const initial = day.recording.segments.length
        ? secondsFor(day.recording.segments[day.recording.segments.length - 1].start)
        : day.events.length ? secondsFor(day.events[day.events.length - 1].occurredAt) : 43200;
      scrubberEl.max = String(totalSeconds() - 1);
      scrubberEl.value = String(initial);
      render();
      updateFrame();
    } catch (error) {
      day = null;
      statusEl.textContent = "timeline unavailable";
      eventsEl.innerHTML = `<div class="empty-state">${escapeHtml(error.message)}</div>`;
    }
  }

  function render() {
    if (!day) return;
    const events = filteredEvents();
    countEl.textContent = `${events.length} of ${day.events.length} AI events`;
    statusEl.textContent = day.recording.available
      ? `${day.recording.segments.length} trigger clips`
      : "trigger clips unavailable";
    rangesEl.innerHTML = day.recording.segments.map(segment => {
      const left = secondsFor(segment.start) / totalSeconds() * 100;
      const width = Math.max(.08,
        (secondsFor(segment.end) - secondsFor(segment.start)) / totalSeconds() * 100);
      return `<span class="timeline-range" style="left:${left}%;width:${width}%"></span>`;
    }).join("");
    markersEl.innerHTML = events.map(event =>
      `<button class="timeline-marker severity-${event.severity}" style="left:${secondsFor(event.occurredAt) / totalSeconds() * 100}%" data-id="${event.id}" aria-label="${escapeHtml(event.category)} at ${escapeHtml(event.occurredAt)}"></button>`
    ).join("");
    for (const marker of markersEl.querySelectorAll(".timeline-marker")) {
      marker.addEventListener("click", () => {
        const event = day.events.find(item => item.id === marker.dataset.id);
        if (event) seek(secondsFor(segmentForEvent(event)?.start || event.occurredAt));
      });
    }
    eventsEl.innerHTML = events.length ? "" : `<div class="empty-state">no events match these filters</div>`;
    for (const event of events) eventsEl.appendChild(renderEvent(event));
    updateCursor();
  }

  function renderEvent(event) {
    const row = document.createElement("div");
    row.className = `timeline-event${event.reviewed ? " reviewed" : ""}`;
    row.innerHTML = `
      <img src="${event.hasImage ? `/api/events/${event.id}/image` : ""}" alt="">
      <div class="timeline-event-body">
        <div class="timeline-event-meta">
          <span>${new Date(event.occurredAt).toLocaleTimeString()}</span>
          <span class="category-${event.category}">${escapeHtml(event.category)}</span>
          <span class="severity-${event.severity}">${escapeHtml(event.severity)}</span>
          ${event.hasClip ? "" : "<span>NO CLIP</span>"}
        </div>
        <div>${escapeHtml(event.summary || event.eventType)}</div>
      </div>
      <button class="btn timeline-review">${event.reviewed ? "reviewed ✓" : "mark reviewed"}</button>`;
    row.addEventListener("click", () =>
      seek(secondsFor(segmentForEvent(event)?.start || event.occurredAt)));
    row.querySelector(".timeline-review").addEventListener("click", async click => {
      click.stopPropagation();
      const reviewed = !event.reviewed;
      const response = await fetch(`/api/events/${event.id}/reviewed`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reviewed })
      });
      if (response.ok) {
        event.reviewed = reviewed;
        render();
      }
    });
    return row;
  }

  function seek(seconds) {
    scrubberEl.value = String(Math.max(0, Math.min(totalSeconds() - 1, seconds)));
    stop();
    updateCursor();
    updateFrame();
  }

  function updateCursor() {
    if (!day) return;
    const seconds = Number(scrubberEl.value);
    cursorEl.style.left = `${seconds / totalSeconds() * 100}%`;
    clockEl.textContent = atSeconds(seconds).toLocaleTimeString();
  }

  function updateFrame() {
    if (!day?.recording.available) {
      frameEl.removeAttribute("src");
      frameEl.classList.add("hidden");
      frameEmptyEl.classList.remove("hidden");
      frameEmptyEl.textContent = day?.recording.error || "trigger clips are unavailable";
      return;
    }
    const at = atSeconds(Number(scrubberEl.value));
    const segment = segmentAt(at);
    if (!segment) {
      frameEl.removeAttribute("src");
      frameEl.classList.add("hidden");
      frameEmptyEl.classList.remove("hidden");
      frameEmptyEl.textContent = "no saved trigger clip at this point";
      return;
    }
    const query = new URLSearchParams({
      eventId: segment.eventId,
      at: at.toISOString(),
      width: "1280",
      height: "720"
    });
    frameEl.src = `/api/timeline/frame?${query}`;
    frameEl.classList.remove("hidden");
    frameEmptyEl.classList.add("hidden");
  }

  function start() {
    if (!day || playing) return;
    playing = true;
    playEl.textContent = "pause";
    timer = setInterval(() => {
      let next = Number(scrubberEl.value) + .5;
      if (skipGapsEl.checked) {
        const at = atSeconds(next);
        const inside = day.recording.segments.some(segment =>
          new Date(segment.start) <= at && new Date(segment.end) > at);
        if (!inside) {
          const upcoming = day.recording.segments.find(segment => new Date(segment.start) > at);
          if (!upcoming) return stop();
          next = secondsFor(upcoming.start);
        }
      }
      if (next >= totalSeconds()) return stop();
      scrubberEl.value = String(next);
      updateCursor();
    }, 500);
    frameTimer = setInterval(updateFrame, 1000);
  }

  function stop() {
    playing = false;
    playEl.textContent = "play";
    clearInterval(timer);
    clearInterval(frameTimer);
  }

  function jump(direction) {
    const current = Number(scrubberEl.value);
    const events = filteredEvents().sort((a, b) => new Date(a.occurredAt) - new Date(b.occurredAt));
    const event = direction > 0
      ? events.find(item => secondsFor(item.occurredAt) > current + .5)
      : [...events].reverse().find(item => secondsFor(item.occurredAt) < current - .5);
    if (event) seek(secondsFor(segmentForEvent(event)?.start || event.occurredAt));
  }

  function escapeHtml(value) {
    const node = document.createElement("div");
    node.textContent = value ?? "";
    return node.innerHTML;
  }

  dateEl.max = localDateString();
  dateEl.value = localDateString();
  document.getElementById("timeline-refresh").addEventListener("click", load);
  cameraEl.addEventListener("change", load);
  dateEl.addEventListener("change", load);
  categoryEl.addEventListener("change", render);
  severityEl.addEventListener("change", render);
  reviewedEl.addEventListener("change", render);
  scrubberEl.addEventListener("input", () => {
    stop();
    updateCursor();
    clearTimeout(frameTimer);
    frameTimer = setTimeout(updateFrame, 120);
  });
  playEl.addEventListener("click", () => playing ? stop() : start());
  document.getElementById("timeline-prev-event").addEventListener("click", () => jump(-1));
  document.getElementById("timeline-next-event").addEventListener("click", () => jump(1));
  document.getElementById("timeline-prev-day").addEventListener("click", () => {
    const value = new Date(`${dateEl.value}T12:00:00`);
    value.setDate(value.getDate() - 1);
    dateEl.value = localDateString(value);
    load();
  });
  document.getElementById("timeline-next-day").addEventListener("click", () => {
    if (dateEl.value >= localDateString()) return;
    const value = new Date(`${dateEl.value}T12:00:00`);
    value.setDate(value.getDate() + 1);
    dateEl.value = localDateString(value);
    load();
  });
  frameEl.addEventListener("error", () => {
    frameEl.classList.add("hidden");
    frameEmptyEl.classList.remove("hidden");
    frameEmptyEl.textContent = "no saved trigger frame at this point";
  });

  window.GateWatchTimeline = { load, stop };
})();
