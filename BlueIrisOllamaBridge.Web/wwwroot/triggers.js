window.GateWatchTriggers = (function () {
  let currentCamera = null;
  let polygonPoints = []; // fractions 0-1, drawn against the reference frame
  let minTriggerArea = 0.02; // fraction of frame area
  let referenceUrl = null;
  let draggingCircle = false;

  const drawWrap = () => document.getElementById("zone-image-wrap");
  const drawImg = () => document.getElementById("zone-reference-img");
  const drawSvg = () => document.getElementById("zone-svg");
  const sizeWrap = () => document.getElementById("zone-size-wrap");
  const sizeImg = () => document.getElementById("zone-size-img");
  const sizeSvg = () => document.getElementById("zone-size-svg");

  // ---------------- Zone editor ----------------

  async function openZoneEditor(camera) {
    currentCamera = camera;
    polygonPoints = [];
    minTriggerArea = 0.02;

    document.getElementById("zone-editor-title").textContent = `TRIGGER ZONE — ${camera.name}`;
    document.getElementById("zone-editor-errors").innerHTML = "";
    document.getElementById("zone-editor").classList.remove("hidden");
    revokeUrls();

    try {
      const [frameResp, configResp] = await Promise.all([
        fetch(`/api/cameras/${camera.id}/reference-frame`),
        fetch(`/api/cameras/${camera.id}/trigger-config`)
      ]);

      if (frameResp.ok) {
        const blob = await frameResp.blob();
        referenceUrl = URL.createObjectURL(blob);
        await Promise.all([setImageSrc(drawImg(), referenceUrl), setImageSrc(sizeImg(), referenceUrl)]);
      } else {
        showEditorError("Could not grab a reference frame from this camera - check its stream URL/credentials.");
      }

      if (configResp.ok) {
        const config = await configResp.json();
        polygonPoints = (config.zonePolygon || []).map(p => ({ x: p.x, y: p.y }));
        minTriggerArea = config.minTriggerArea || 0.02;
        document.getElementById("zone-cooldown").value = config.cooldownSeconds ?? 5;
        document.getElementById("zone-max-per-min").value = config.maxTriggersPerMinute ?? 6;
        document.getElementById("zone-enabled").checked = !!config.enabled;
        setShadowSensitivity(config.shadowSensitivity ?? 50);
        document.getElementById("zone-record-clips").checked = !!config.recordClips;
        document.getElementById("zone-clip-pre").value = config.clipPreSeconds ?? 5;
        document.getElementById("zone-clip-post").value = config.clipPostSeconds ?? 10;
      }
    } catch (err) {
      console.error("Failed to load trigger zone editor", err);
      showEditorError("Network error while loading.");
    }

    renderPolygon();
    renderCircle();
  }

  function closeZoneEditor() {
    document.getElementById("zone-editor").classList.add("hidden");
    revokeUrls();
    currentCamera = null;
  }

  function setImageSrc(img, url) {
    return new Promise(resolve => {
      img.onload = () => resolve();
      img.onerror = () => resolve();
      img.src = url;
    });
  }

  function revokeUrls() {
    if (referenceUrl) {
      URL.revokeObjectURL(referenceUrl);
      referenceUrl = null;
    }
  }

  function showEditorError(message) {
    const li = document.createElement("li");
    li.textContent = message;
    document.getElementById("zone-editor-errors").appendChild(li);
  }

  function svgNs(tag) {
    return document.createElementNS("http://www.w3.org/2000/svg", tag);
  }

  function setViewBoxToWrap(svgEl, wrapEl) {
    const rect = wrapEl.getBoundingClientRect();
    const w = Math.max(1, rect.width);
    const h = Math.max(1, rect.height);
    svgEl.setAttribute("viewBox", `0 0 ${w} ${h}`);
    return { w, h };
  }

  function renderPolygon() {
    const svg = drawSvg();
    const { w, h } = setViewBoxToWrap(svg, drawWrap());
    svg.innerHTML = "";
    if (polygonPoints.length === 0) return;

    const poly = svgNs("polygon");
    poly.setAttribute("points", polygonPoints.map(p => `${p.x * w},${p.y * h}`).join(" "));
    poly.setAttribute("class", "zone-polygon");
    svg.appendChild(poly);

    for (const p of polygonPoints) {
      const dot = svgNs("circle");
      dot.setAttribute("cx", p.x * w);
      dot.setAttribute("cy", p.y * h);
      dot.setAttribute("r", 5);
      dot.setAttribute("class", "zone-vertex");
      svg.appendChild(dot);
    }
  }

  function renderCircle() {
    const svg = sizeSvg();
    const { w, h } = setViewBoxToWrap(svg, sizeWrap());
    svg.innerHTML = "";

    const cx = w / 2;
    const cy = h / 2;
    const r = Math.sqrt((minTriggerArea * w * h) / Math.PI);

    const circle = svgNs("circle");
    circle.setAttribute("cx", cx);
    circle.setAttribute("cy", cy);
    circle.setAttribute("r", r);
    circle.setAttribute("class", "zone-size-circle");
    svg.appendChild(circle);

    const handle = svgNs("circle");
    handle.setAttribute("cx", cx + r);
    handle.setAttribute("cy", cy);
    handle.setAttribute("r", 7);
    handle.setAttribute("class", "zone-size-handle");
    handle.addEventListener("pointerdown", e => {
      e.preventDefault();
      draggingCircle = true;
    });
    svg.appendChild(handle);
  }

  drawWrap().addEventListener("click", e => {
    const rect = drawWrap().getBoundingClientRect();
    const x = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
    const y = Math.min(1, Math.max(0, (e.clientY - rect.top) / rect.height));
    polygonPoints.push({ x, y });
    renderPolygon();
  });

  document.addEventListener("pointermove", e => {
    if (!draggingCircle) return;
    const rect = sizeWrap().getBoundingClientRect();
    const cx = rect.width / 2;
    const cy = rect.height / 2;
    const px = e.clientX - rect.left;
    const py = e.clientY - rect.top;
    const maxR = Math.min(rect.width, rect.height) / 2 - 4;
    const r = Math.max(8, Math.min(Math.hypot(px - cx, py - cy), maxR));
    minTriggerArea = (Math.PI * r * r) / (rect.width * rect.height);
    renderCircle();
  });

  document.addEventListener("pointerup", () => {
    draggingCircle = false;
  });

  function setShadowSensitivity(value) {
    document.getElementById("zone-shadow-sensitivity").value = value;
    document.getElementById("zone-shadow-sensitivity-value").textContent = value;
  }

  document.getElementById("zone-shadow-sensitivity").addEventListener("input", e => {
    document.getElementById("zone-shadow-sensitivity-value").textContent = e.target.value;
  });

  document.getElementById("zone-undo-point").addEventListener("click", () => {
    polygonPoints.pop();
    renderPolygon();
  });

  document.getElementById("zone-clear-points").addEventListener("click", () => {
    polygonPoints = [];
    renderPolygon();
  });

  document.getElementById("zone-editor-close").addEventListener("click", closeZoneEditor);
  document.getElementById("zone-editor").addEventListener("click", e => {
    if (e.target.id === "zone-editor") closeZoneEditor();
  });

  document.getElementById("zone-save-btn").addEventListener("click", saveZoneConfig);

  async function saveZoneConfig() {
    const errorsEl = document.getElementById("zone-editor-errors");
    errorsEl.innerHTML = "";

    const body = {
      enabled: document.getElementById("zone-enabled").checked,
      zonePolygon: polygonPoints,
      minTriggerArea,
      cooldownSeconds: parseInt(document.getElementById("zone-cooldown").value, 10) || 0,
      maxTriggersPerMinute: parseInt(document.getElementById("zone-max-per-min").value, 10) || 1,
      shadowSensitivity: parseInt(document.getElementById("zone-shadow-sensitivity").value, 10) || 0,
      recordClips: document.getElementById("zone-record-clips").checked,
      clipPreSeconds: parseInt(document.getElementById("zone-clip-pre").value, 10) || 0,
      clipPostSeconds: parseInt(document.getElementById("zone-clip-post").value, 10) || 0
    };

    try {
      const response = await fetch(`/api/cameras/${currentCamera.id}/trigger-config`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        for (const message of payload.errors || ["Save failed."]) showEditorError(message);
        return;
      }
      closeZoneEditor();
    } catch {
      showEditorError("Network error while saving.");
    }
  }

  // ---------------- Triggers viewer tab ----------------

  async function load() {
    const root = document.getElementById("triggers-root");
    const countEl = document.getElementById("trigger-count");
    try {
      const response = await fetch("/api/triggers?limit=50");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const events = await response.json();

      countEl.textContent = `${events.length} alert${events.length === 1 ? "" : "s"}`;
      root.innerHTML = "";
      if (events.length === 0) {
        root.innerHTML = `<div class="empty-state">no trigger zone alerts yet</div>`;
        return;
      }

      for (const event of events) root.appendChild(buildTriggerCard(event));
    } catch (err) {
      console.error("Failed to load triggers", err);
      root.innerHTML = `<div class="empty-state">failed to load triggers</div>`;
    }
  }

  function buildTriggerCard(event) {
    const card = document.createElement("div");
    card.className = "card";
    card.innerHTML = `
      <div class="card-image" data-tip="${event.hasClip ? "Click to play the video clip saved around this trigger." : "Click to view the full-size snapshot. No clip was saved for this one."}">
        <img src="/api/triggers/${event.id}/image" alt="trigger snapshot" />
        ${event.hasClip ? `<span class="clip-badge">&#9654; clip</span>` : ""}
      </div>
      <div class="card-body">
        <div class="card-meta">
          <span class="card-camera">${escapeHtml(event.cameraName)}</span>
          <span title="${escapeHtml(event.occurredAt)}">${timeAgo(event.occurredAt)}</span>
        </div>
        <div class="card-footer">
          <span class="card-event-type">motion in zone</span>
          <span>${formatClock(event.occurredAt)}</span>
        </div>
      </div>
    `;
    const img = card.querySelector(".card-image img");
    img.addEventListener("click", () => {
      if (event.hasClip) openLightboxVideo(`/api/triggers/${event.id}/clip`);
      else openLightbox(img.src);
    });
    return card;
  }

  document.getElementById("triggers-refresh-btn").addEventListener("click", load);

  return { openZoneEditor, load };
})();
