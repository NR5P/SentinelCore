window.GateWatchTriggers = (function () {
  let currentCamera = null;
  // One entry per zone: { name, polygon: [{x, y}] (fractions 0-1 against the reference frame),
  // minTriggerArea (fraction of the whole frame's area) }. Always at least one - the server sends
  // a synthesized one for cameras configured before zones could be split up.
  let zones = [];
  let activeZone = 0;
  let referenceUrl = null;
  let draggingCircle = false;

  // The detector works on a fixed 320x180 grid (MotionDetector.GridWidth/GridHeight), so a size
  // expressed as a fraction of the frame turns into this many pixels - the number the trigger
  // threshold is actually compared against, and far easier to reason about than "0.0015".
  const DETECTOR_PIXELS = 320 * 180;

  const activeZoneOrNull = () => zones[activeZone] || null;

  const drawWrap = () => document.getElementById("zone-image-wrap");
  const drawImg = () => document.getElementById("zone-reference-img");
  const drawSvg = () => document.getElementById("zone-svg");
  const sizeWrap = () => document.getElementById("zone-size-wrap");
  const sizeImg = () => document.getElementById("zone-size-img");
  const sizeSvg = () => document.getElementById("zone-size-svg");

  // ---------------- Zone editor ----------------

  async function openZoneEditor(camera) {
    currentCamera = camera;
    zones = [{ name: "Zone 1", polygon: [], minTriggerArea: 0.02 }];
    activeZone = 0;

    document.getElementById("zone-editor-title").textContent = `TRIGGER ZONE — ${camera.name}`;
    document.getElementById("zone-editor-errors").innerHTML = "";
    document.getElementById("zone-editor").classList.remove("hidden");
    revokeUrls();

    try {
      // Devices and the trigger address are both allowed to fail quietly: the Device Hub is off
      // on most installs, and neither one has any bearing on editing the zone itself.
      const [frameResp, configResp, profilesResp, devicesResp, triggerUrlResp] = await Promise.all([
        fetch(`/api/cameras/${camera.id}/reference-frame`),
        fetch(`/api/cameras/${camera.id}/trigger-config`),
        fetch(`/api/prompt-profile-names`),
        fetch(`/api/devices`).catch(() => null),
        fetch(`/api/cameras/${camera.id}/trigger-url`).catch(() => null)
      ]);

      if (frameResp.ok) {
        const blob = await frameResp.blob();
        referenceUrl = URL.createObjectURL(blob);
        await Promise.all([setImageSrc(drawImg(), referenceUrl), setImageSrc(sizeImg(), referenceUrl)]);
      } else {
        showEditorError("Could not grab a reference frame from this camera - check its stream URL/credentials.");
      }

      let alertProfile = "default";
      let sensorDeviceId = "";
      if (configResp.ok) {
        const config = await configResp.json();
        // Prefer the zone list. Fall back to the legacy single-polygon fields for a server that
        // predates multi-zone, so the editor still opens against an older backend.
        zones = Array.isArray(config.zones) && config.zones.length > 0
          ? config.zones.map((z, i) => ({
              name: z.name || `Zone ${i + 1}`,
              polygon: (z.polygon || []).map(p => ({ x: p.x, y: p.y })),
              minTriggerArea: z.minTriggerArea || 0.02
            }))
          : [{
              name: "Zone 1",
              polygon: (config.zonePolygon || []).map(p => ({ x: p.x, y: p.y })),
              minTriggerArea: config.minTriggerArea || 0.02
            }];
        activeZone = 0;
        document.getElementById("zone-cooldown").value = config.cooldownSeconds ?? 5;
        document.getElementById("zone-max-per-min").value = config.maxTriggersPerMinute ?? 6;
        document.getElementById("zone-enabled").checked = !!config.enabled;
        setShadowSensitivity(config.shadowSensitivity ?? 50);
        document.getElementById("zone-record-clips").checked = !!config.recordClips;
        document.getElementById("zone-clip-pre").value = config.clipPreSeconds ?? 5;
        document.getElementById("zone-clip-post").value = config.clipPostSeconds ?? 10;
        document.getElementById("zone-send-to-alerts").checked = !!config.sendToAlerts;
        alertProfile = config.alertProfile || "default";
        sensorDeviceId = config.sensorDeviceId || "";
        document.getElementById("zone-sensor-state").value = config.sensorTriggerOn || "on";
        document.getElementById("zone-sensor-cooldown").value = config.sensorCooldownSeconds ?? 30;
      }

      const profileNames = profilesResp.ok ? await profilesResp.json() : [];
      populateProfileOptions(profileNames, alertProfile);

      const devices = devicesResp && devicesResp.ok ? await devicesResp.json() : [];
      populateSensorOptions(devices, sensorDeviceId);

      const triggerUrl = triggerUrlResp && triggerUrlResp.ok ? await triggerUrlResp.json() : null;
      showTriggerUrl(triggerUrl);
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

  // Includes the saved value as an option even if it's missing from the fetched list (e.g. a
  // profile that was since renamed or deleted), so it's still visible rather than silently lost.
  function populateProfileOptions(profiles, selected) {
    const select = document.getElementById("zone-alert-profile");
    const all = selected && !profiles.some(p => p.key === selected)
      ? [...profiles, { key: selected, displayName: selected }]
      : profiles;
    select.innerHTML = all
      .map(p => `<option value="${escapeHtml(p.key)}">${escapeHtml(p.displayName)}</option>`)
      .join("");
    select.value = selected || "default";
  }

  // Sensors only - a light or a plug has a state topic too, but firing a camera off one is not
  // what this is for. A bound device that has since been deleted or changed kind is still listed
  // (as "no longer available") so it's visible rather than silently swapped to "none" on save.
  function populateSensorOptions(devices, selected) {
    const select = document.getElementById("zone-sensor-device");
    const sensors = (devices || []).filter(d => d.kind === "sensor");
    const options = [{ id: "", name: "None" }, ...sensors];
    if (selected && !sensors.some(d => d.id === selected))
      options.push({ id: selected, name: "(no longer available)" });

    select.innerHTML = options
      .map(d => `<option value="${escapeHtml(d.id)}">${escapeHtml(d.room ? `${d.name} - ${d.room}` : d.name)}</option>`)
      .join("");
    select.value = selected || "";
  }

  function showTriggerUrl(info) {
    const row = document.getElementById("zone-trigger-url-row");
    const input = document.getElementById("zone-trigger-url");
    if (!info) {
      row.classList.add("hidden");
      return;
    }

    row.classList.remove("hidden");
    input.value = info.enabled
      ? `${window.location.origin}${info.path}?key=${encodeURIComponent(info.key)}`
      : "Turn the Trigger API on under Settings to get this camera's address.";
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

  // Every zone is drawn, but only the selected one is solid and shows its vertices - the others
  // are dashed and faint so you can see how they line up without mistaking them for the shape your
  // clicks are going into. The selected zone is drawn last so it sits on top.
  function renderPolygon() {
    const svg = drawSvg();
    const { w, h } = setViewBoxToWrap(svg, drawWrap());
    svg.innerHTML = "";

    const order = zones.map((_, i) => i).filter(i => i !== activeZone).concat(
      activeZone < zones.length ? [activeZone] : []);

    for (const index of order) {
      const zone = zones[index];
      if (!zone || zone.polygon.length === 0) continue;

      const isActive = index === activeZone;

      const poly = svgNs("polygon");
      poly.setAttribute("points", zone.polygon.map(p => `${p.x * w},${p.y * h}`).join(" "));
      poly.setAttribute("class", isActive ? "zone-polygon" : "zone-polygon-inactive");
      svg.appendChild(poly);

      const label = svgNs("text");
      label.setAttribute("x", zone.polygon[0].x * w + 6);
      label.setAttribute("y", zone.polygon[0].y * h - 6);
      label.setAttribute("class", isActive ? "zone-label" : "zone-label zone-label-inactive");
      label.textContent = zone.name;
      svg.appendChild(label);

      if (!isActive) continue;

      for (const p of zone.polygon) {
        const dot = svgNs("circle");
        dot.setAttribute("cx", p.x * w);
        dot.setAttribute("cy", p.y * h);
        dot.setAttribute("r", 5);
        dot.setAttribute("class", "zone-vertex");
        svg.appendChild(dot);
      }
    }

    renderZoneTabs();
  }

  function renderZoneTabs() {
    const tabs = document.getElementById("zone-tabs");
    tabs.innerHTML = "";

    zones.forEach((zone, index) => {
      const tab = document.createElement("button");
      tab.type = "button";
      tab.className = index === activeZone ? "zone-tab zone-tab-active" : "zone-tab";
      tab.textContent = `${zone.name} · ${Math.round(zone.minTriggerArea * DETECTOR_PIXELS)}px`;
      // Wired here rather than by delegation because these buttons are rebuilt on every render.
      tab.addEventListener("click", () => {
        activeZone = index;
        renderPolygon();
        renderCircle();
      });
      tabs.appendChild(tab);
    });
  }

  function renderCircle() {
    const svg = sizeSvg();
    const { w, h } = setViewBoxToWrap(svg, sizeWrap());
    svg.innerHTML = "";

    const zone = activeZoneOrNull();
    if (!zone) return;

    const readout = document.getElementById("zone-size-readout");
    if (readout)
      readout.textContent =
        `${zone.name}: ignores anything under ${Math.round(zone.minTriggerArea * DETECTOR_PIXELS)} detector pixels`;

    const cx = w / 2;
    const cy = h / 2;
    const r = Math.sqrt((zone.minTriggerArea * w * h) / Math.PI);

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
    const zone = activeZoneOrNull();
    if (!zone) return;

    const rect = drawWrap().getBoundingClientRect();
    const x = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
    const y = Math.min(1, Math.max(0, (e.clientY - rect.top) / rect.height));
    zone.polygon.push({ x, y });
    renderPolygon();
  });

  document.addEventListener("pointermove", e => {
    if (!draggingCircle) return;
    const zone = activeZoneOrNull();
    if (!zone) return;

    const rect = sizeWrap().getBoundingClientRect();
    const cx = rect.width / 2;
    const cy = rect.height / 2;
    const px = e.clientX - rect.left;
    const py = e.clientY - rect.top;
    const maxR = Math.min(rect.width, rect.height) / 2 - 4;
    const r = Math.max(8, Math.min(Math.hypot(px - cx, py - cy), maxR));
    zone.minTriggerArea = (Math.PI * r * r) / (rect.width * rect.height);
    renderCircle();
    renderZoneTabs(); // the tab shows this zone's pixel size
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
    const zone = activeZoneOrNull();
    if (!zone) return;
    zone.polygon.pop();
    renderPolygon();
  });

  document.getElementById("zone-clear-points").addEventListener("click", () => {
    const zone = activeZoneOrNull();
    if (!zone) return;
    zone.polygon = [];
    renderPolygon();
  });

  // A new zone starts empty and selected, and inherits the current zone's size as a starting point
  // - you're usually adding a zone for a different distance, so it's a more useful default than
  // the stock 2% would be.
  document.getElementById("zone-add").addEventListener("click", () => {
    const previous = activeZoneOrNull();
    zones.push({
      name: `Zone ${zones.length + 1}`,
      polygon: [],
      minTriggerArea: previous ? previous.minTriggerArea : 0.02
    });
    activeZone = zones.length - 1;
    renderPolygon();
    renderCircle();
  });

  document.getElementById("zone-rename").addEventListener("click", () => {
    const zone = activeZoneOrNull();
    if (!zone) return;
    const name = window.prompt("Zone name", zone.name);
    if (name === null) return;
    zone.name = name.trim().slice(0, 50) || zone.name;
    renderPolygon();
    renderCircle();
  });

  // A camera always keeps at least one zone: the legacy ZonePolygon/MinTriggerArea fields the
  // mobile apps read are mirrored from the primary, so there has to be a primary to mirror.
  document.getElementById("zone-delete").addEventListener("click", () => {
    if (zones.length <= 1) {
      showEditorError("A camera needs at least one zone - clear its points instead.");
      return;
    }

    zones.splice(activeZone, 1);
    activeZone = Math.max(0, activeZone - 1);
    renderPolygon();
    renderCircle();
  });

  document.getElementById("zone-editor-close").addEventListener("click", closeZoneEditor);
  document.getElementById("zone-editor").addEventListener("click", e => {
    if (e.target.id === "zone-editor") closeZoneEditor();
  });

  document.getElementById("zone-save-btn").addEventListener("click", saveZoneConfig);

  async function saveZoneConfig() {
    const errorsEl = document.getElementById("zone-editor-errors");
    errorsEl.innerHTML = "";

    // zonePolygon/minTriggerArea are sent as well as zones, mirroring the primary zone: the shipped
    // iOS/tvOS/Android builds decode those two as required fields, so the payload has to keep
    // carrying them. The server treats zones as authoritative when it's present.
    const primary = zones[0] || { polygon: [], minTriggerArea: 0.02 };
    const body = {
      enabled: document.getElementById("zone-enabled").checked,
      zonePolygon: primary.polygon,
      minTriggerArea: primary.minTriggerArea,
      zones: zones.map(z => ({ name: z.name, polygon: z.polygon, minTriggerArea: z.minTriggerArea })),
      cooldownSeconds: parseInt(document.getElementById("zone-cooldown").value, 10) || 0,
      maxTriggersPerMinute: parseInt(document.getElementById("zone-max-per-min").value, 10) || 1,
      shadowSensitivity: parseInt(document.getElementById("zone-shadow-sensitivity").value, 10) || 0,
      recordClips: document.getElementById("zone-record-clips").checked,
      clipPreSeconds: parseInt(document.getElementById("zone-clip-pre").value, 10) || 0,
      clipPostSeconds: parseInt(document.getElementById("zone-clip-post").value, 10) || 0,
      sendToAlerts: document.getElementById("zone-send-to-alerts").checked,
      alertProfile: document.getElementById("zone-alert-profile").value || "default",
      sensorDeviceId: document.getElementById("zone-sensor-device").value || "",
      sensorTriggerOn: document.getElementById("zone-sensor-state").value || "on",
      sensorCooldownSeconds: parseInt(document.getElementById("zone-sensor-cooldown").value, 10) || 0
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

  return { openZoneEditor };
})();
