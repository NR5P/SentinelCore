window.GateWatchTriggers = (function () {
  let currentCamera = null;
  // One entry per zone: { name, polygon: [{x, y}] (fractions 0-1 against the reference frame),
  // minTriggerArea (fraction of the whole frame's area) }. Always at least one - the server sends
  // a synthesized one for cameras configured before zones could be split up.
  let zones = [];
  let activeZone = 0;
  let referenceUrl = null;
  let draggingCircle = false;
  // Cached so the "reading to compare" list can be rebuilt when the sensor changes without
  // re-fetching the device list.
  let sensorDevices = [];

  // The detector works on a fixed 320x180 grid (MotionDetector.GridWidth/GridHeight), so a size
  // expressed as a fraction of the frame turns into this many pixels - the number the trigger
  // threshold is actually compared against, and far easier to reason about than "0.0015".
  const DETECTOR_PIXELS = 320 * 180;

  // The smallest size a zone can be set to. Four detector pixels is about as small as the grid can
  // meaningfully express - below a couple of pixels a blob is indistinguishable from sensor noise -
  // and it is far below what this editor used to allow. The drag handle clamped its radius to 8
  // *screen pixels*, so the floor depended on how big the pane happened to be, and on the phone
  // apps (same control, same clamp) it bottomed out around 250 detector pixels: roughly a person at
  // 40 feet, with nothing smaller or further away reachable at all.
  const MIN_DETECTOR_PIXELS = 4;

  // How many steps the slider has. It is logarithmic (see sliderPositionOf): a linear slider over a
  // range this wide would put every useful small size inside its first few pixels of travel, which
  // is exactly the end of the range this control exists to reach.
  const SIZE_SLIDER_STEPS = 1000;

  // The server rejects anything outside (0, 1], and everything that writes minTriggerArea goes
  // through here so neither end can be reached by accident.
  const triggerAreaOf = detectorPixels =>
    Math.min(Math.max(detectorPixels, MIN_DETECTOR_PIXELS), DETECTOR_PIXELS) / DETECTOR_PIXELS;

  function sliderPositionOf(area) {
    const pixels = Math.min(Math.max(area * DETECTOR_PIXELS, MIN_DETECTOR_PIXELS), DETECTOR_PIXELS);
    const fraction = Math.log(pixels / MIN_DETECTOR_PIXELS) / Math.log(DETECTOR_PIXELS / MIN_DETECTOR_PIXELS);
    return Math.round(fraction * SIZE_SLIDER_STEPS);
  }

  function triggerAreaAtSlider(position) {
    const fraction = Math.min(Math.max(position / SIZE_SLIDER_STEPS, 0), 1);
    const pixels = MIN_DETECTOR_PIXELS * Math.pow(DETECTOR_PIXELS / MIN_DETECTOR_PIXELS, fraction);
    return triggerAreaOf(Math.round(pixels));
  }

  // A single detector pixel at the small end, 5% further up. Even a log slider can't be dragged to
  // an exact value of 6, and 6 versus 7 is a real difference down there.
  function nudgedArea(area, direction) {
    const pixels = Math.round(area * DETECTOR_PIXELS);
    const step = pixels < 50 ? 1 : Math.round(pixels * 0.05);
    return triggerAreaOf(pixels + direction * step);
  }

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
      let sensorMeasurement = "";
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
        document.getElementById("zone-sensor-threshold").value = config.sensorThreshold ?? 0;
        sensorMeasurement = config.sensorMeasurement || "";
        document.getElementById("zone-night-window").checked = !!config.nightWindowEnabled;
        document.getElementById("zone-night-start").value = config.nightStartHour ?? 21;
        document.getElementById("zone-night-end").value = config.nightEndHour ?? 7;
        document.getElementById("zone-bypass-fastlocal").checked = !!config.bypassFastLocal;
        document.getElementById("zone-keep-only-alertable").checked = !!config.keepOnlyAlertable;
      }

      const profileNames = profilesResp.ok ? await profilesResp.json() : [];
      populateProfileOptions(profileNames, alertProfile);

      const devices = devicesResp && devicesResp.ok ? await devicesResp.json() : [];
      populateSensorOptions(devices, sensorDeviceId);
      populateMeasurementOptions(sensorDeviceId, sensorMeasurement);
      updateSensorModeRows();

      const triggerUrl = triggerUrlResp && triggerUrlResp.ok ? await triggerUrlResp.json() : null;
      showTriggerUrl(triggerUrl);

      await loadRecordingSchedules();
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

  // Sensors first, because they are what this is for - but not sensors only. A contact on a gate
  // or a garage door is registered as that kind rather than as a sensor, and "film the driveway
  // when the gate opens" is exactly the thing people want. Anything without a state topic is left
  // out: it has no reading to fire on. A bound device that has since been deleted is still listed
  // (as "no longer available") so it's visible rather than silently swapped to "none" on save.
  function populateSensorOptions(devices, selected) {
    const select = document.getElementById("zone-sensor-device");
    sensorDevices = (devices || []).filter(d => d.stateTopic);

    const sensors = sensorDevices.filter(d => d.kind === "sensor");
    const others = sensorDevices.filter(d => d.kind !== "sensor");
    const label = d => escapeHtml(d.room ? `${d.name} - ${d.room}` : d.name);
    const option = d => `<option value="${escapeHtml(d.id)}">${label(d)}</option>`;
    const group = (name, list) =>
      list.length ? `<optgroup label="${name}">${list.map(option).join("")}</optgroup>` : "";

    let html = `<option value="">None</option>` + group("Sensors", sensors) + group("Other devices", others);
    if (selected && !sensorDevices.some(d => d.id === selected))
      html += `<option value="${escapeHtml(selected)}">(no longer available)</option>`;

    select.innerHTML = html;
    select.value = selected || "";
  }

  // The readings this sensor has actually published, which is the only list there is - nothing
  // declares up front what a device reports (see DeviceTopics.HarvestMeasurements). A saved key
  // the device hasn't reported since the hub restarted is kept in the list rather than dropped,
  // because losing it here would silently rewrite a working binding on the next save.
  function populateMeasurementOptions(deviceId, selected) {
    const select = document.getElementById("zone-sensor-measurement");
    const device = sensorDevices.find(d => d.id === deviceId);
    const keys = ((device && device.measurements) || []).map(m => m.key);
    if (selected && !keys.includes(selected)) keys.push(selected);

    select.innerHTML = keys.length
      ? keys.map(k => `<option value="${escapeHtml(k)}">${escapeHtml(k)}</option>`).join("")
      : `<option value="">(nothing reported yet)</option>`;
    select.value = selected || keys[0] || "";
  }

  // The comparison rows only mean anything for the two numeric modes, and a threshold box sitting
  // under an on/off binding reads as though it applies.
  function updateSensorModeRows() {
    const mode = document.getElementById("zone-sensor-state").value;
    const numeric = mode === "below" || mode === "above";
    document.getElementById("zone-sensor-measurement-row").classList.toggle("hidden", !numeric);
    document.getElementById("zone-sensor-threshold-row").classList.toggle("hidden", !numeric);
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

    const slider = document.getElementById("zone-size-slider");
    if (slider) slider.value = sliderPositionOf(zone.minTriggerArea);

    const cx = w / 2;
    const cy = h / 2;
    const r = Math.sqrt((zone.minTriggerArea * w * h) / Math.PI);

    // At the small end of the range the circle is a pixel or two across and effectively invisible
    // against a busy frame. The dashed ring says "the size you set is in here" without pretending
    // to be the size itself.
    if (r < 9) {
      const locator = svgNs("circle");
      locator.setAttribute("cx", cx);
      locator.setAttribute("cy", cy);
      locator.setAttribute("r", 9);
      locator.setAttribute("class", "zone-size-locator");
      svg.appendChild(locator);
    }

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
    // No floor on the radius any more, only the domain floor triggerAreaOf applies: an 8-pixel
    // minimum here is what made small sizes unreachable, and it did it differently depending on
    // how wide the pane had been laid out.
    const r = Math.max(0, Math.min(Math.hypot(px - cx, py - cy), maxR));
    const area = (Math.PI * r * r) / (rect.width * rect.height);
    zone.minTriggerArea = triggerAreaOf(area * DETECTOR_PIXELS);
    renderCircle();
    renderZoneTabs(); // the tab shows this zone's pixel size
  });

  document.addEventListener("pointerup", () => {
    draggingCircle = false;
  });

  document.getElementById("zone-size-slider").addEventListener("input", e => {
    const zone = activeZoneOrNull();
    if (!zone) return;
    zone.minTriggerArea = triggerAreaAtSlider(parseInt(e.target.value, 10) || 0);
    renderCircle();
    renderZoneTabs();
  });

  for (const [id, direction] of [["zone-size-smaller", -1], ["zone-size-larger", 1]]) {
    document.getElementById(id).addEventListener("click", () => {
      const zone = activeZoneOrNull();
      if (!zone) return;
      zone.minTriggerArea = nudgedArea(zone.minTriggerArea, direction);
      renderCircle();
      renderZoneTabs();
    });
  }

  function setShadowSensitivity(value) {
    document.getElementById("zone-shadow-sensitivity").value = value;
    document.getElementById("zone-shadow-sensitivity-value").textContent = value;
  }

  document.getElementById("zone-shadow-sensitivity").addEventListener("input", e => {
    document.getElementById("zone-shadow-sensitivity-value").textContent = e.target.value;
  });

  // An hour box, read so that 0 survives and anything unusable falls back to the caller's default
  // rather than to midnight. Out-of-range values are left alone for the server to reject with a
  // message, which is more use than clamping them silently to something the user didn't type.
  function hourOrDefault(elementId, fallback) {
    const parsed = parseInt(document.getElementById(elementId).value, 10);
    return Number.isNaN(parsed) ? fallback : parsed;
  }

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

  // The whole frame, as the four corners of the unit square rather than as a special empty-means-
  // everything case: an empty polygon already means the opposite (it rasterizes to a mask no blob
  // can be inside), and every consumer downstream - the detector, the overlays, the validator -
  // then goes on treating this like any other shape. The corners really do cover everything: the
  // detector tests pixel centres at (i + 0.5) / size, so all 320x180 cells fall strictly inside.
  document.getElementById("zone-cover-everything").addEventListener("click", () => {
    const zone = activeZoneOrNull();
    if (!zone) return;
    zone.polygon = [{ x: 0, y: 0 }, { x: 1, y: 0 }, { x: 1, y: 1 }, { x: 0, y: 1 }];
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

  document.getElementById("zone-sensor-state").addEventListener("change", updateSensorModeRows);

  // Each sensor reports its own set of readings, so the compare list has to follow the device.
  document.getElementById("zone-sensor-device").addEventListener("change", e => {
    populateMeasurementOptions(e.target.value, "");
  });

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
      sensorCooldownSeconds: parseInt(document.getElementById("zone-sensor-cooldown").value, 10) || 0,
      sensorMeasurement: document.getElementById("zone-sensor-measurement").value || "",
      sensorThreshold: parseFloat(document.getElementById("zone-sensor-threshold").value) || 0,
      nightWindowEnabled: document.getElementById("zone-night-window").checked,
      // Not the `|| 0` idiom used above: hour 0 is a legitimate value that `||` would swallow, and
      // an empty box should fall back to the shipped window rather than silently to midnight.
      nightStartHour: hourOrDefault("zone-night-start", 21),
      nightEndHour: hourOrDefault("zone-night-end", 7),
      bypassFastLocal: document.getElementById("zone-bypass-fastlocal").checked,
      keepOnlyAlertable: document.getElementById("zone-keep-only-alertable").checked
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

  // --- scheduled recording ---------------------------------------------------------------------
  //
  // Clock-driven recording windows, edited here rather than on the Schedules tab because this is
  // where someone already is when they're thinking about what a camera should capture - and because
  // a window belongs to one camera, which the Schedules tab has no notion of.
  //
  // The editor deals in two clock times; the server stores a start and a duration. See
  // RecordingSchedule.DurationSeconds for why - in short, a stored stop *time* can be orphaned from
  // its start and leave a camera recording forever, and a length cannot.

  const DAY_NAMES = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

  function recordingEl(id) { return document.getElementById(id); }

  function buildDayToggles() {
    const container = recordingEl("recording-days");
    if (container.childElementCount > 0) return;

    for (let day = 0; day < 7; day++) {
      const label = document.createElement("label");
      label.className = "recording-day";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = String(day);
      input.addEventListener("change", previewRecording);
      label.append(input, document.createTextNode(DAY_NAMES[day]));
      container.appendChild(label);
    }
  }

  function selectedDays() {
    return [...recordingEl("recording-days").querySelectorAll("input:checked")].map(i => parseInt(i.value, 10));
  }

  // Which fields are relevant depends entirely on the kind, and showing all of them at once is how
  // someone ends up filling in a cron expression that a daily schedule will ignore.
  function applyRecordingKind() {
    const kind = recordingEl("recording-kind").value;
    recordingEl("recording-cron-row").classList.toggle("hidden", kind !== "cron");
    recordingEl("recording-once-row").classList.toggle("hidden", kind !== "once");
    recordingEl("recording-solar-row").classList.toggle("hidden", kind !== "solar");
    recordingEl("recording-solar-offset-row").classList.toggle("hidden", kind !== "solar");
    recordingEl("recording-days-row").classList.toggle("hidden", kind === "once" || kind === "cron");

    // Only a daily window has a start time of its own to type. The others still need a length,
    // which is what the stop time is really setting, so it stays visible throughout.
    recordingEl("recording-start").closest(".field-row").classList.toggle("hidden", kind !== "daily");
    previewRecording();
  }

  function recordingBody() {
    const kind = recordingEl("recording-kind").value;
    const start = recordingEl("recording-start").value;
    const stop = recordingEl("recording-stop").value;

    // For the kinds with no clock start of their own, the two time fields can't be subtracted -
    // the start comes from a cron expression or the sun. The stop time is read as a length from
    // midnight instead, which is what it means in that context: "record for two hours" is 02:00.
    let durationSeconds = null;
    if (kind !== "daily" && stop) {
      const [h, m] = stop.split(":").map(Number);
      durationSeconds = (h * 3600) + (m * 60);
    }

    const onceValue = recordingEl("recording-once").value;
    return {
      name: recordingEl("recording-name").value.trim(),
      enabled: recordingEl("recording-enabled").checked,
      kind,
      startTime: kind === "daily" ? start : "",
      stopTime: kind === "daily" ? stop : "",
      daysOfWeek: kind === "once" || kind === "cron" ? [] : selectedDays(),
      cronExpression: recordingEl("recording-cron").value.trim(),
      runAtUtc: kind === "once" && onceValue ? new Date(onceValue).toISOString() : null,
      solarEvent: kind === "solar" ? recordingEl("recording-solar").value : "",
      solarOffsetMinutes: parseInt(recordingEl("recording-solar-offset").value, 10) || 0,
      solarElevationDegrees: 0,
      durationSeconds,
      includeAudio: false
    };
  }

  function describeWindow(startIso, endIso) {
    const start = new Date(startIso);
    const end = new Date(endIso);
    const time = d => d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    return `${start.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" })} ${time(start)}-${time(end)}`;
  }

  let previewTimer = null;

  // Debounced because it fires on every keystroke in the cron box, and each call is a round trip.
  function previewRecording() {
    clearTimeout(previewTimer);
    previewTimer = setTimeout(runPreview, 350);
  }

  async function runPreview() {
    const label = recordingEl("recording-preview");
    const body = recordingBody();
    if (!body.name) { label.textContent = "Fill in a window to see when it would run."; return; }

    try {
      const response = await fetch("/api/recording-schedules/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) { label.textContent = (payload.errors || ["That window isn't valid yet."])[0]; return; }

      if (!payload.occurrences || payload.occurrences.length === 0) {
        label.textContent = "This would never run - check the days and times.";
        return;
      }

      const minutes = Math.round(payload.durationSeconds / 60);
      const next = payload.occurrences.slice(0, 3).map(o => describeWindow(o.start, o.end)).join(" · ");
      label.textContent = `${minutes} min each · next: ${next}`;
    } catch {
      label.textContent = "Could not work out when this would run.";
    }
  }

  async function loadRecordingSchedules() {
    const list = recordingEl("recording-schedule-list");
    list.innerHTML = "";

    try {
      const response = await fetch(`/api/cameras/${currentCamera.id}/recording-schedules`);
      if (!response.ok) return;

      const schedules = await response.json();
      if (schedules.length === 0) {
        const empty = document.createElement("li");
        empty.className = "recording-schedule-empty";
        empty.textContent = "No timed recordings on this camera yet.";
        list.appendChild(empty);
        return;
      }

      for (const schedule of schedules) list.appendChild(renderRecordingRow(schedule));
    } catch (err) {
      console.error("Failed to load recording schedules", err);
    }
  }

  function renderRecordingRow(schedule) {
    const row = document.createElement("li");
    row.className = "recording-schedule-row";
    if (!schedule.enabled) row.classList.add("recording-schedule-off");

    const name = document.createElement("span");
    name.className = "recording-schedule-name";
    name.textContent = schedule.name;

    const when = document.createElement("span");
    when.className = "recording-schedule-when";
    when.textContent = schedule.nextOccurrences && schedule.nextOccurrences.length > 0
      ? `next ${describeWindow(schedule.nextOccurrences[0], new Date(new Date(schedule.nextOccurrences[0]).getTime() + schedule.durationSeconds * 1000).toISOString())}`
      : schedule.enabled ? "no upcoming runs" : "off";

    const remove = document.createElement("button");
    remove.className = "btn btn-small";
    remove.type = "button";
    remove.textContent = "remove";
    remove.addEventListener("click", async () => {
      // The rule goes; recordings it already made stay. Said out loud because "remove" next to a
      // list of video is easy to read as "delete the video".
      const response = await fetch(`/api/cameras/${currentCamera.id}/recording-schedules/${schedule.id}`, { method: "DELETE" });
      if (response.ok) await loadRecordingSchedules();
    });

    row.append(name, when, remove);
    return row;
  }

  async function addRecordingSchedule() {
    const errorsEl = document.getElementById("zone-editor-errors");
    errorsEl.innerHTML = "";

    const body = recordingBody();
    try {
      const response = await fetch(`/api/cameras/${currentCamera.id}/recording-schedules`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        for (const message of payload.errors || ["Could not save that recording window."]) showEditorError(message);
        return;
      }

      recordingEl("recording-name").value = "";
      await loadRecordingSchedules();
      previewRecording();
    } catch {
      showEditorError("Network error while saving the recording window.");
    }
  }

  buildDayToggles();
  recordingEl("recording-kind").addEventListener("change", applyRecordingKind);
  recordingEl("recording-add-btn").addEventListener("click", addRecordingSchedule);
  for (const id of ["recording-name", "recording-start", "recording-stop", "recording-cron", "recording-once", "recording-solar", "recording-solar-offset"]) {
    recordingEl(id).addEventListener("input", previewRecording);
  }
  applyRecordingKind();

  return { openZoneEditor };
})();
