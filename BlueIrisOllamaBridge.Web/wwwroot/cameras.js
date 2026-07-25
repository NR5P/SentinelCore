window.GateWatchCameras = (function () {
  let cameras = [];
  let editingId = null;
  const hlsInstances = new Map();

  async function load() {
    try {
      const response = await fetch("/api/cameras");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      cameras = await response.json();
    } catch (err) {
      console.error("Failed to load cameras", err);
    }
  }

  function escapeHtml(value) {
    const div = document.createElement("div");
    div.textContent = value ?? "";
    return div.innerHTML;
  }

  // ---------------- Cameras management tab ----------------

  function renderList() {
    const root = document.getElementById("cameras-root");
    root.innerHTML = "";
    if (cameras.length === 0) {
      root.innerHTML = `<div class="empty-state">no cameras added yet</div>`;
      return;
    }

    for (const camera of cameras) {
      root.appendChild(buildCameraCard(camera));
    }
  }

  function buildCameraCard(camera) {
    const card = document.createElement("div");
    card.className = "camera-card";

    const info = document.createElement("div");
    info.className = "camera-card-info";
    info.innerHTML = `
      <span class="camera-card-name">${escapeHtml(camera.name)}</span>
      <span class="camera-card-meta">${escapeHtml(camera.streamUrl)}</span>
      <span class="camera-card-meta">${camera.includeAudio ? "video + audio" : "video only"}</span>
    `;

    const actions = document.createElement("div");
    actions.className = "camera-card-actions";
    const editBtn = document.createElement("button");
    editBtn.className = "btn";
    editBtn.textContent = "edit";
    editBtn.setAttribute("data-tip-right", "");
    editBtn.dataset.tip = "Change this camera's name, stream URL, or login details.";
    editBtn.addEventListener("click", () => openForm(camera));
    const zoneBtn = document.createElement("button");
    zoneBtn.className = "btn";
    zoneBtn.textContent = "trigger zone";
    zoneBtn.setAttribute("data-tip-right", "");
    zoneBtn.dataset.tip = "Draw an area to watch for motion, and choose whether to save a video clip when something moves in it.";
    zoneBtn.addEventListener("click", () => window.GateWatchTriggers.openZoneEditor(camera));
    const deleteBtn = document.createElement("button");
    deleteBtn.className = "btn";
    deleteBtn.textContent = "delete";
    deleteBtn.setAttribute("data-tip-right", "");
    deleteBtn.dataset.tip = "Remove this camera from the dashboard. Does not touch Blue Iris or the camera itself.";
    deleteBtn.addEventListener("click", () => removeCamera(camera));
    actions.append(editBtn, zoneBtn, deleteBtn);

    card.append(info, actions);
    return card;
  }

  function openForm(camera) {
    editingId = camera ? camera.id : null;
    const wrap = document.getElementById("camera-form-wrap");
    const fields = document.getElementById("camera-form-fields");
    const errors = document.getElementById("camera-form-errors");
    errors.innerHTML = "";
    fields.innerHTML = "";

    fields.appendChild(buildTextField("cam-name", "Name", camera?.name ?? "",
      "Whatever you want to call this camera, e.g. 'front gate'. Shown on the Live and Triggers tabs."));
    fields.appendChild(buildTextField("cam-url", "Stream URL (rtsp:// or http://)", camera?.streamUrl ?? "",
      "The camera's video stream address, not its web page. Look for an RTSP or streaming section in the camera's own settings."));
    fields.appendChild(buildTextField("cam-username", "Username (optional)", camera?.username ?? "",
      "Only needed if the camera requires a login to view its stream."));
    fields.appendChild(buildPasswordField("cam-password", "Password (optional)", camera?.password ?? "",
      "Password that goes with the username above. Stored on this machine so the stream can be opened."));
    fields.appendChild(buildCheckboxField("cam-audio", "Include audio", camera?.includeAudio ?? false,
      "Include the camera's microphone in the live view and saved clips, if it has one."));

    wrap.classList.remove("hidden");
    wrap.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  function closeForm() {
    editingId = null;
    document.getElementById("camera-form-wrap").classList.add("hidden");
  }

  function buildTextField(id, label, value, tip) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide";
    if (tip) row.dataset.tip = tip;
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "text";
    input.className = "field-input";
    input.id = id;
    input.value = value;
    row.appendChild(input);
    return row;
  }

  function buildPasswordField(id, label, value, tip) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide";
    if (tip) row.dataset.tip = tip;
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const wrap = document.createElement("div");
    wrap.className = "field-password-row";
    const input = document.createElement("input");
    input.type = "password";
    input.className = "field-input";
    input.id = id;
    input.value = value;
    input.autocomplete = "off";
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "field-toggle-btn";
    toggle.textContent = "show";
    toggle.setAttribute("data-tip-right", "");
    toggle.dataset.tip = "Reveal the password so you can check it.";
    toggle.addEventListener("click", () => {
      input.type = input.type === "password" ? "text" : "password";
      toggle.textContent = input.type === "password" ? "show" : "hide";
    });
    wrap.append(input, toggle);
    row.appendChild(wrap);
    return row;
  }

  function buildCheckboxField(id, label, checked, tip) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide field-checkbox-row";
    if (tip) row.dataset.tip = tip;
    const switchLabel = document.createElement("label");
    switchLabel.className = "switch";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.id = id;
    input.checked = checked;
    const track = document.createElement("span");
    track.className = "switch-track";
    switchLabel.append(input, track);
    const text = document.createElement("span");
    text.className = "field-label";
    text.textContent = label;
    row.append(switchLabel, text);
    return row;
  }

  async function saveCamera() {
    const body = {
      name: document.getElementById("cam-name").value.trim(),
      streamUrl: document.getElementById("cam-url").value.trim(),
      username: document.getElementById("cam-username").value.trim() || null,
      password: document.getElementById("cam-password").value || null,
      includeAudio: document.getElementById("cam-audio").checked
    };

    const errorsEl = document.getElementById("camera-form-errors");
    errorsEl.innerHTML = "";

    const endpoint = editingId ? `/api/cameras/${editingId}` : "/api/cameras";
    const method = editingId ? "PUT" : "POST";

    try {
      const response = await fetch(endpoint, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        for (const message of payload.errors || ["Save failed."]) {
          const li = document.createElement("li");
          li.textContent = message;
          errorsEl.appendChild(li);
        }
        return;
      }

      closeForm();
      await load();
      renderList();
    } catch {
      const li = document.createElement("li");
      li.textContent = "Network error while saving.";
      errorsEl.appendChild(li);
    }
  }

  async function removeCamera(camera) {
    if (!confirm(`Remove camera "${camera.name}"? This only removes it from the live view - nothing to do with Blue Iris.`)) return;

    try {
      await fetch(`/api/cameras/${camera.id}`, { method: "DELETE" });
      await load();
      renderList();
    } catch (err) {
      console.error("Failed to delete camera", err);
    }
  }

  // ---------------- Live tab ----------------

  function showLive() {
    const root = document.getElementById("live-root");
    root.innerHTML = "";

    if (cameras.length === 0) {
      root.innerHTML = `<div class="empty-state">no cameras added yet - add one in the Cameras tab</div>`;
      return;
    }

    for (const camera of cameras) {
      const tile = document.createElement("div");
      tile.className = "live-tile";

      const video = document.createElement("video");
      video.muted = true;
      video.autoplay = true;
      video.playsInline = true;
      video.controls = true;

      const label = document.createElement("div");
      label.className = "live-tile-label";
      label.textContent = camera.name;
      label.dataset.tip = "Live view, a few seconds behind real time. Use the player controls for fullscreen and volume.";

      const status = document.createElement("div");
      status.className = "live-tile-status";
      status.textContent = "connecting…";

      tile.append(video, label, status);
      root.appendChild(tile);

      attachPlayer(video, status, camera);
    }
  }

  function attachPlayer(video, status, camera) {
    const src = `/api/cameras/${camera.id}/stream/stream.m3u8`;

    video.addEventListener("playing", () => status.classList.add("hidden"), { once: true });

    if (window.Hls && Hls.isSupported()) {
      const hls = new Hls({ liveSyncDurationCount: 2 });
      hls.on(Hls.Events.ERROR, (_event, data) => {
        if (data.fatal) {
          status.textContent = "stream unavailable";
          status.classList.remove("hidden");
        }
      });
      // Only attempt playback once hls.js has actually parsed the manifest - calling play()
      // right after attachMedia (before there's anything to play) causes some browsers to
      // reject the play() promise and never retry, leaving the video stuck paused.
      hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => {}));
      hls.loadSource(src);
      hls.attachMedia(video);
      hlsInstances.set(camera.id, hls);
    } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = src;
      video.addEventListener("loadedmetadata", () => video.play().catch(() => {}), { once: true });
    } else {
      status.textContent = "this browser can't play HLS video";
    }
  }

  function hideLive() {
    for (const hls of hlsInstances.values()) {
      try { hls.destroy(); } catch { /* already gone */ }
    }
    hlsInstances.clear();

    document.querySelectorAll("#live-root video").forEach(video => {
      video.pause();
      video.removeAttribute("src");
      video.load();
    });
  }

  document.getElementById("add-camera-btn").addEventListener("click", () => openForm(null));
  document.getElementById("camera-cancel-btn").addEventListener("click", closeForm);
  document.getElementById("camera-save-btn").addEventListener("click", saveCamera);

  return { load, renderList, showLive, hideLive };
})();
