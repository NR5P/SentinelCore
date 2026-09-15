// The standalone host for SentinelPublisher (publisher.js): this page is the publisher with
// nothing else around it, for a laptop or spare phone that is left running for days. Everything
// that captures, connects, reconnects and stays awake lives in the shared module; what is here is
// just which panel to show and how to paint the module's state into it.
//
// Deliberately its own page rather than only a dashboard tab: it has to survive being left alone,
// so it holds a screen wake lock and does nothing else - no polling of events, no live grid
// behind it quietly eating the battery of the device it is running on. The Cameras tab embeds the
// same module for the case where the device with the webcam is the one the dashboard is open on.
(function () {
  const publisher = window.SentinelPublisher;
  const params = new URLSearchParams(location.search);

  const els = {
    blocked: document.getElementById("publish-blocked"),
    blockedReason: document.getElementById("publish-blocked-reason"),
    blockedFix: document.getElementById("publish-blocked-fix"),
    picker: document.getElementById("publish-picker"),
    pickerList: document.getElementById("publish-picker-list"),
    stage: document.getElementById("publish-stage"),
    cameraName: document.getElementById("publish-camera-name"),
    preview: document.getElementById("publish-preview"),
    idleOverlay: document.getElementById("publish-idle-overlay"),
    device: document.getElementById("publish-device"),
    mic: document.getElementById("publish-mic"),
    toggle: document.getElementById("publish-toggle"),
    status: document.getElementById("publish-status"),
    errors: document.getElementById("publish-errors")
  };

  let camera = null;

  function show(panel) {
    for (const el of [els.blocked, els.picker, els.stage]) el.classList.add("hidden");
    panel.classList.remove("hidden");
  }

  function blocked(reason, fixHtml) {
    els.blockedReason.textContent = reason;
    els.blockedFix.innerHTML = fixHtml ?? "";
    show(els.blocked);
  }

  // ---------------- Startup ----------------

  async function start() {
    if (!publisher.canCapture()) {
      blocked(publisher.insecureReason(), await publisher.insecureFixHtml(location.pathname + location.search));
      return;
    }

    let cameras;
    try {
      const response = await fetch("/api/cameras");
      if (response.status === 401 || response.status === 403) {
        blocked(
          "This device isn't signed in to SENTINEL_CORE.",
          `Open <a href="/">the dashboard</a> on this device, sign in, then come back to this page.`
        );
        return;
      }
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      cameras = await response.json();
    } catch {
      blocked("Couldn't reach the SENTINEL_CORE server from this device.", "Check the address and that the server is running.");
      return;
    }

    const publishable = cameras.filter((c) => c.isBrowserPublished);
    if (publishable.length === 0) {
      blocked(
        "No webcam cameras have been set up yet.",
        `On <a href="/#cameras">the dashboard's Cameras tab</a>, choose <em>add webcam device</em>, then return here.`
      );
      return;
    }

    const requested = params.get("camera");
    camera = publishable.find((c) => c.id === requested) ?? null;
    if (!camera) {
      renderPicker(publishable);
      return;
    }

    await openStage();
  }

  function renderPicker(publishable) {
    els.pickerList.innerHTML = "";
    for (const item of publishable) {
      const link = document.createElement("a");
      link.className = "btn publish-picker-item";
      link.href = `?camera=${encodeURIComponent(item.id)}`;
      link.textContent = item.name;
      els.pickerList.appendChild(link);
    }
    show(els.picker);
  }

  // ---------------- The stage ----------------

  function session() {
    return { camera: { id: camera.id, name: camera.name }, deviceId: els.device.value, includeMic: els.mic.checked, host: "publish" };
  }

  async function openStage() {
    els.cameraName.textContent = camera.name;
    els.mic.checked = !!camera.includeAudio;
    show(els.stage);
    publisher.onChange(render);

    els.toggle.addEventListener("click", () => {
      if (publisher.snapshot().publishing) {
        publisher.stop();
        publisher.preview(els.device.value, "stopped on this device - previewing only, nothing is being sent");
      } else {
        publisher.start(session());
      }
    });
    // Changing camera or microphone mid-session restarts it, which is the only way to swap tracks
    // without renegotiating - and a restart here costs a second of video, not a lost session.
    // Before a session, changing the camera re-opens the preview, so what is on screen is always
    // the camera that would be streamed.
    els.device.addEventListener("change", () =>
      publisher.snapshot().publishing ? publisher.start(session()) : publisher.preview(els.device.value));
    els.mic.addEventListener("change", () => { if (publisher.snapshot().publishing) publisher.start(session()); });

    document.getElementById("publish-keep-awake").innerHTML = publisher.keepAwakeHtml();

    // A reload (a browser update, an accidental refresh) picks streaming straight back up rather
    // than sitting at "start streaming" until someone comes back to the room.
    if (publisher.resume("publish", camera.id)) return;
    await publisher.preview("");
  }

  let shownStream = null;
  async function render(state) {
    els.toggle.textContent = state.publishing ? "stop streaming" : "start streaming";
    els.status.textContent = state.status;
    els.errors.innerHTML = "";
    if (state.error) {
      const li = document.createElement("li");
      if (state.error.html) li.innerHTML = state.error.message; else li.textContent = state.error.message;
      els.errors.appendChild(li);
    }

    // Only when the capture actually changed: re-pointing the <video> at the same stream restarts
    // it, and the device list only changes when a capture has (permission, or a new camera).
    if (state.stream !== shownStream) {
      shownStream = state.stream;
      els.preview.srcObject = state.stream;
      els.idleOverlay.classList.toggle("hidden", !!state.stream);
      if (state.stream) await publisher.populateDeviceSelect(els.device);
    }
  }

  start();
})();
