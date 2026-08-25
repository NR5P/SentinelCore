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
  const verdictEl = document.getElementById("timeline-verdict");
  const skipGapsEl = document.getElementById("timeline-skip-gaps");

  let day = null;
  let playing = false;
  // The trigger clip (identified by triggerEventId) currently loaded into the <video> element,
  // so scrubbing within the same clip only seeks instead of re-fetching it.
  let loadedSegment = null;

  function localDateString(date = new Date()) {
    const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
    return local.toISOString().slice(0, 10);
  }

  // "confirmed" keeps anything the AI didn't actively reject, so a frame whose analysis failed or
  // was cancelled still shows up - an outage should look like an outage, not like a quiet day.
  function matchesVerdict(event) {
    if (verdictEl.value === "rejected") return event.verdict === "rejected";
    if (verdictEl.value === "confirmed") return event.verdict !== "rejected";
    return true;
  }

  function filteredEvents() {
    if (!day) return [];
    return day.events.filter(event =>
      matchesVerdict(event) &&
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

  // The filters decide what day you're reviewing, so a clip belonging to a hidden event is treated
  // as not being there at all - it stays off the rail, play skips over it, and scrubbing across it
  // reads as a gap. Otherwise hiding 350 rejected rows from the list would still leave 350 bands
  // on the rail and playback would keep dropping into empty frames.
  function visibleSegments() {
    if (!day) return [];
    const shown = new Set(filteredEvents().map(event => event.id));
    return day.recording.segments.filter(segment => shown.has(segment.eventId));
  }

  function segmentAt(date) {
    return visibleSegments().find(segment =>
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
      resetFrame("loading day…");
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
      // Open on the last thing worth looking at, which is the last *shown* event - landing on a
      // rejected frame's clip would put the cursor somewhere the rail doesn't even draw.
      const shown = filteredEvents();
      const shownSegments = visibleSegments();
      const initial = shownSegments.length
        ? secondsFor(shownSegments[shownSegments.length - 1].start)
        : shown.length ? secondsFor(shown[shown.length - 1].occurredAt) : 43200;
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
    const segments = visibleSegments();
    // How much the camera threw away is worth stating outright rather than leaving to be inferred
    // from a shrinking list: a day that rejected 350 frames is a day the camera needs attention.
    const rejected = day.events.filter(event => event.verdict === "rejected").length;
    countEl.textContent = rejected && verdictEl.value === "confirmed"
      ? `${events.length} shown · ${rejected} rejected`
      : `${events.length} of ${day.events.length} AI events`;
    statusEl.textContent = day.recording.available
      ? `${segments.length} trigger clips`
      : "trigger clips unavailable";
    rangesEl.innerHTML = segments.map(segment => {
      const left = secondsFor(segment.start) / totalSeconds() * 100;
      const width = Math.max(.08,
        (secondsFor(segment.end) - secondsFor(segment.start)) / totalSeconds() * 100);
      return `<span class="timeline-range" style="left:${left}%;width:${width}%"></span>`;
    }).join("");
    markersEl.innerHTML = events.map(event =>
      `<button class="timeline-marker severity-${event.severity}${event.verdict === "rejected" ? " rejected" : ""}" style="left:${secondsFor(event.occurredAt) / totalSeconds() * 100}%" data-id="${event.id}" aria-label="${escapeHtml(event.category)} at ${escapeHtml(event.occurredAt)}"></button>`
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
    row.className = `timeline-event${event.reviewed ? " reviewed" : ""}${event.verdict === "rejected" ? " rejected" : ""}`;
    row.innerHTML = `
      <img src="${event.hasImage ? `/api/events/${event.id}/image` : ""}" alt="">
      <div class="timeline-event-body">
        <div class="timeline-event-meta">
          <span>${new Date(event.occurredAt).toLocaleTimeString()}</span>
          <span class="category-${event.category}">${escapeHtml(event.category)}</span>
          <span class="severity-${event.severity}">${escapeHtml(event.severity)}</span>
          ${event.verdict === "rejected" ? "<span class=\"verdict-rejected\">REJECTED</span>" : ""}
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

  function resetFrame(message) {
    loadedSegment = null;
    frameEl.pause();
    frameEl.removeAttribute("src");
    frameEl.load();
    frameEl.classList.add("hidden");
    frameEmptyEl.classList.remove("hidden");
    frameEmptyEl.textContent = message;
  }

  // Loads/seeks the <video> to match the scrubber position. Only re-fetches the clip when the
  // scrubber has moved into a different trigger event's segment.
  function updateFrame() {
    if (!day?.recording.available) {
      resetFrame(day?.recording.error || "trigger clips are unavailable");
      return;
    }
    const at = atSeconds(Number(scrubberEl.value));
    const segment = segmentAt(at);
    if (!segment) {
      resetFrame("no saved trigger clip at this point");
      return;
    }
    const offset = Math.max(0, (at - new Date(segment.start)) / 1000);
    frameEl.classList.remove("hidden");
    frameEmptyEl.classList.add("hidden");
    if (loadedSegment?.triggerEventId === segment.triggerEventId) {
      if (Math.abs(frameEl.currentTime - offset) > 0.25) frameEl.currentTime = offset;
      return;
    }
    loadedSegment = segment;
    frameEl.src = `/api/triggers/${segment.triggerEventId}/clip`;
    frameEl.addEventListener("loadedmetadata", () => { frameEl.currentTime = offset; }, { once: true });
  }

  function start() {
    if (!day || playing) return;
    updateFrame();
    if (!loadedSegment) return;
    playing = true;
    playEl.textContent = "pause";
    frameEl.play().catch(() => {});
  }

  function stop() {
    playing = false;
    playEl.textContent = "play";
    frameEl.pause();
  }

  // Reached the end of the currently-playing clip's saved window. With "skip unsaved gaps" on,
  // jump straight to the next trigger clip (there's no footage to show in between); otherwise
  // stop, mirroring the old flip-book behavior of pausing at the edge of what was recorded.
  function advancePastSegment() {
    if (!playing || !loadedSegment) return;
    if (!skipGapsEl.checked) return stop();
    const finishedEnd = new Date(loadedSegment.end);
    const next = visibleSegments().find(segment => new Date(segment.start) > finishedEnd);
    if (!next) return stop();
    scrubberEl.value = String(secondsFor(next.start));
    updateCursor();
    updateFrame();
    frameEl.play().catch(() => {});
  }

  // Where the cursor actually ends up for an event: the start of its clip when it has one, which
  // is ClipPreSeconds *before* the event itself. Jumping has to compare against this rather than
  // against occurredAt - otherwise "next event" lands a few seconds ahead of the cursor, the
  // event it just moved to still counts as being in the future, and every further press picks
  // the same one again. That looked like the button doing nothing at all.
  function positionFor(event) {
    return secondsFor(segmentForEvent(event)?.start || event.occurredAt);
  }

  function jump(direction) {
    const current = Number(scrubberEl.value);
    const positions = filteredEvents()
      .map(event => positionFor(event))
      .sort((a, b) => a - b);

    // The scrubber is a step="1" range input, so a fractional seek snaps to a whole second and
    // the cursor can sit up to half a second off where it was sent. A one-second margin keeps
    // that rounding from re-selecting the event we're already on.
    const target = direction > 0
      ? positions.find(seconds => seconds > current + 1)
      : [...positions].reverse().find(seconds => seconds < current - 1);

    if (target !== undefined) seek(target);
  }

  function escapeHtml(value) {
    // Deliberately a string replace rather than the textContent/innerHTML trick this used to be.
    // That trick escapes exactly what a *text node* needs - & < > - and quotes are not among them,
    // because a quote means nothing in text. Half the interpolations here are into attributes
    // (alt=, title=, data-tip=, value=), and there a surviving double quote ends the attribute and
    // everything after it is parsed as more attributes. Confirmed against a real DOM: an event
    // summary reading `A man held a sign reading " onerror=alert(1) x="` produced an <img>
    // carrying an onerror the markup never contained.
    //
    // Vision models write those summaries, and they quote things - a sign, a brand, a plate - so
    // the merely-broken version of this happens without anybody attacking anything.
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  dateEl.max = localDateString();
  dateEl.value = localDateString();
  document.getElementById("timeline-refresh").addEventListener("click", load);
  cameraEl.addEventListener("change", load);
  dateEl.addEventListener("change", load);
  verdictEl.addEventListener("change", () => { render(); updateFrame(); });
  categoryEl.addEventListener("change", render);
  severityEl.addEventListener("change", render);
  reviewedEl.addEventListener("change", render);
  scrubberEl.addEventListener("input", () => {
    stop();
    updateCursor();
    updateFrame();
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
    if (!frameEl.getAttribute("src")) return;
    frameEl.classList.add("hidden");
    frameEmptyEl.classList.remove("hidden");
    frameEmptyEl.textContent = "no saved trigger clip at this point";
  });
  frameEl.addEventListener("timeupdate", () => {
    if (!playing || !loadedSegment) return;
    const seconds = secondsFor(loadedSegment.start) + frameEl.currentTime;
    if (seconds >= secondsFor(loadedSegment.end)) {
      advancePastSegment();
      return;
    }
    scrubberEl.value = String(Math.min(totalSeconds() - 1, seconds));
    updateCursor();
  });
  frameEl.addEventListener("ended", advancePastSegment);

  window.GateWatchTimeline = { load, stop };
})();
