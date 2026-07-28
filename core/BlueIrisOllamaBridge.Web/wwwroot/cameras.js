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
    closeDiscovery();
    editingId = camera ? camera.id : null;
    const wrap = document.getElementById("camera-form-wrap");
    const fields = document.getElementById("camera-form-fields");
    const errors = document.getElementById("camera-form-errors");
    errors.innerHTML = "";
    fields.innerHTML = "";
    setTestStatus("", null);

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

  function setTestStatus(message, kind) {
    const el = document.getElementById("camera-test-status");
    el.textContent = message;
    el.className = "camera-test-status" + (kind ? ` camera-test-status-${kind}` : "");
  }

  // Accepts either a bare IP/host or a full rtsp://host/path style URL - pulls just the host out
  // of either, so the same Stream URL field works whether the user already knows the exact path
  // or just pasted in the camera's IP hoping to find out.
  function extractAddress(rawUrl) {
    try {
      const url = new URL(rawUrl.includes("://") ? rawUrl : `rtsp://${rawUrl}`);
      return url.hostname;
    } catch {
      return rawUrl;
    }
  }

  // True for a bare IP/host or a URL with no real path yet (what someone types when they know
  // the camera's address but not its stream path) - false once there's an actual path, which
  // means the user already knows the URL that works and it must not be silently overwritten.
  function looksLikeABareAddress(rawUrl) {
    try {
      const url = new URL(rawUrl.includes("://") ? rawUrl : `rtsp://${rawUrl}`);
      return !url.pathname || url.pathname === "/";
    } catch {
      return true;
    }
  }

  async function testConnection() {
    const urlField = document.getElementById("cam-url");
    const nameField = document.getElementById("cam-name");
    const usernameField = document.getElementById("cam-username");
    const passwordField = document.getElementById("cam-password");

    const address = extractAddress(urlField.value.trim());
    if (!address) {
      setTestStatus("Enter a stream URL or IP address first.", "error");
      return;
    }

    setTestStatus("testing…", null);

    try {
      const response = await fetch("/api/cameras/discovery/probe", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          address,
          username: usernameField.value.trim() || null,
          password: passwordField.value || null
        })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        setTestStatus((payload.errors || ["Test failed."]).join(" "), "error");
        return;
      }

      if (payload.status === "Found") {
        const details = [
          payload.resolution,
          payload.manufacturer && payload.model ? `${payload.manufacturer} ${payload.model}` : null
        ].filter(Boolean).join(", ");
        setTestStatus(`found${details ? " - " + details : ""}`, "ok");

        // Offer the real, verified stream URL/name it found - never overwrites a URL the user
        // already knows the real path for, only fills in a bare address or an empty name.
        if (payload.streamUrl && looksLikeABareAddress(urlField.value.trim())) urlField.value = payload.streamUrl;
        if (payload.suggestedName && !nameField.value.trim()) nameField.value = payload.suggestedName;
      } else if (payload.status === "NeedsCredentials") {
        setTestStatus(payload.message, "warn");
      } else {
        setTestStatus(payload.message || "No camera found at this address.", "error");
      }
    } catch {
      setTestStatus("Network error while testing.", "error");
    }
  }

  // ---------------- Network discovery ----------------

  let discoveryPollTimer = null;

  async function openDiscovery() {
    closeForm();
    const wrap = document.getElementById("camera-discovery-wrap");
    const interfacesRoot = document.getElementById("discovery-interfaces-root");
    setDiscoveryStatus("", null);
    document.getElementById("discovery-results-root").innerHTML = "";
    interfacesRoot.innerHTML = `<div class="empty-state">loading network interfaces&hellip;</div>`;
    wrap.classList.remove("hidden");
    wrap.scrollIntoView({ behavior: "smooth", block: "nearest" });

    try {
      const response = await fetch("/api/cameras/discovery/interfaces");
      const interfaces = await response.json();
      renderInterfaceCheckboxes(interfacesRoot, interfaces);
    } catch {
      interfacesRoot.innerHTML = `<div class="empty-state">Could not load network interfaces.</div>`;
    }
  }

  function closeDiscovery() {
    stopDiscoveryPolling();
    document.getElementById("camera-discovery-wrap").classList.add("hidden");
  }

  function renderInterfaceCheckboxes(root, interfaces) {
    root.innerHTML = "";
    if (interfaces.length === 0) {
      root.innerHTML = `<div class="empty-state">No usable network interfaces were found.</div>`;
      return;
    }

    const label = document.createElement("div");
    label.className = "field-label";
    label.textContent = "scan these network interfaces:";
    root.appendChild(label);

    for (const iface of interfaces) {
      const row = document.createElement("label");
      row.className = "field-checkbox-row";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = iface.address;
      input.dataset.role = "discovery-interface";
      // Default-checked only for interfaces that don't look virtual (Docker, VPN, WSL, etc.) -
      // the heuristic informs the default, it doesn't decide on its own; every interface stays
      // available to opt into.
      input.checked = !iface.looksVirtual;
      const text = document.createElement("span");
      text.textContent = `${iface.address} (${iface.name}${iface.looksVirtual ? " - looks virtual" : ""})`;
      row.append(input, text);
      root.appendChild(row);
    }
  }

  function setDiscoveryStatus(message, kind) {
    const el = document.getElementById("discovery-status");
    el.textContent = message;
    el.className = "camera-test-status" + (kind ? ` camera-test-status-${kind}` : "");
  }

  async function startScan() {
    const selected = [...document.querySelectorAll('input[data-role="discovery-interface"]:checked')]
      .map(input => input.value);
    if (selected.length === 0) {
      setDiscoveryStatus("Select at least one network interface first.", "error");
      return;
    }

    setDiscoveryStatus("scanning…", null);
    document.getElementById("discovery-results-root").innerHTML = "";

    try {
      const response = await fetch("/api/cameras/discovery/scan", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ interfaceAddresses: selected })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        setDiscoveryStatus((payload.errors || ["Scan failed to start."]).join(" "), "error");
        return;
      }
      pollScan(payload.scanId);
    } catch {
      setDiscoveryStatus("Network error while starting the scan.", "error");
    }
  }

  function pollScan(scanId) {
    stopDiscoveryPolling();
    discoveryPollTimer = setInterval(async () => {
      try {
        const response = await fetch(`/api/cameras/discovery/scan/${scanId}`);
        if (!response.ok) {
          stopDiscoveryPolling();
          setDiscoveryStatus("Scan result no longer available.", "error");
          return;
        }
        const snapshot = await response.json();
        renderDiscoveryResults(snapshot.results);
        if (snapshot.status === "Completed") {
          stopDiscoveryPolling();
          setDiscoveryStatus(
            snapshot.results.length === 0 ? "no cameras found" : `found ${snapshot.results.length} camera(s)`,
            snapshot.results.length === 0 ? null : "ok"
          );
        }
      } catch {
        stopDiscoveryPolling();
        setDiscoveryStatus("Network error while checking scan progress.", "error");
      }
    }, 1000);
  }

  function stopDiscoveryPolling() {
    if (discoveryPollTimer) {
      clearInterval(discoveryPollTimer);
      discoveryPollTimer = null;
    }
  }

  function renderDiscoveryResults(results) {
    const root = document.getElementById("discovery-results-root");
    root.innerHTML = "";

    for (const item of results) {
      const card = document.createElement("div");
      card.className = "camera-card";

      const info = document.createElement("div");
      info.className = "camera-card-info";
      const details = [
        item.manufacturer && item.model ? `${item.manufacturer} ${item.model}` : null,
        item.needsCredentials ? "needs credentials" : null
      ].filter(Boolean).join(" - ");
      info.innerHTML = `
        <span class="camera-card-name">${escapeHtml(item.suggestedName)}</span>
        <span class="camera-card-meta">${escapeHtml(item.ipAddress)}${details ? " - " + escapeHtml(details) : ""}</span>
        ${item.alreadySaved ? `<span class="camera-card-meta">already added</span>` : ""}
      `;

      const actions = document.createElement("div");
      actions.className = "camera-card-actions";
      if (!item.alreadySaved) {
        const addBtn = document.createElement("button");
        addBtn.className = "btn";
        addBtn.textContent = "add";
        addBtn.dataset.tipRight = "";
        addBtn.dataset.tip = "Open the add-camera form with this camera's address filled in. Use Test Connection there to find its real stream URL, especially if it needs credentials.";
        addBtn.addEventListener("click", () => {
          closeDiscovery();
          // Never write a real stream URL here - just the bare address. The manual form's own
          // Test Connection button (same probe endpoint) is what turns that into a verified,
          // working URL, especially once the user supplies credentials this scan never asked for.
          openForm({ name: item.suggestedName, streamUrl: item.ipAddress, username: "", password: "", includeAudio: false });
        });
        actions.appendChild(addBtn);
      }

      card.append(info, actions);
      root.appendChild(card);
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
  document.getElementById("camera-test-btn").addEventListener("click", testConnection);
  document.getElementById("discover-cameras-btn").addEventListener("click", openDiscovery);
  document.getElementById("discovery-cancel-btn").addEventListener("click", closeDiscovery);
  document.getElementById("discovery-scan-btn").addEventListener("click", startScan);

  return { load, renderList, showLive, hideLive };
})();
