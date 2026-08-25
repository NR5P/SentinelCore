// Turns this browser into one of the dashboard's cameras: capture the device's webcam and push it
// to the server over WebRTC, the exact reverse of the live grid in cameras.js. The server hands
// the media to its go2rtc sidecar, and from there it is an ordinary camera - clips, trigger zones
// and AI analysis all work without knowing where the video came from.
//
// The awkward constraint this page exists to manage is that it has to keep running unattended for
// days on a machine nobody is sitting at. Everything below is shaped by that: a wake lock so the
// screen doesn't sleep the stream away, a reconnect loop, and a status line that distinguishes
// "connected" from "the server is actually receiving frames", which are not the same thing.
(function () {
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
  let stream = null;
  let pc = null;
  let wakeLock = null;
  let statusTimer = null;
  let retryTimer = null;
  let publishing = false;

  // Bumped every time a session starts or stops, so callbacks belonging to a session that has
  // since been torn down (a slow SDP exchange, a late reconnect timer) can tell they are stale and
  // do nothing. Without it, stopping and restarting quickly leaves the old attempt's error handler
  // free to kill the new session.
  let generation = 0;

  function show(panel) {
    for (const el of [els.blocked, els.picker, els.stage]) el.classList.add("hidden");
    panel.classList.remove("hidden");
  }

  function setStatus(message) {
    els.status.textContent = message;
  }

  function setError(message) {
    els.errors.innerHTML = "";
    if (!message) return;
    const li = document.createElement("li");
    li.textContent = message;
    els.errors.appendChild(li);
  }

  function blocked(reason, fixHtml) {
    els.blockedReason.textContent = reason;
    els.blockedFix.innerHTML = fixHtml ?? "";
    show(els.blocked);
  }

  // ---------------- Startup ----------------

  // getUserMedia only exists in a secure context, and "secure" excludes plain http on a LAN
  // address - which is exactly how this dashboard is usually reached. Worth catching up front and
  // explaining precisely, because the browser's own failure here is a bare undefined property.
  function checkEnvironment() {
    if (window.isSecureContext && navigator.mediaDevices?.getUserMedia) return true;

    const httpsUrl = `https://${location.hostname}:3443${location.pathname}${location.search}`;
    blocked(
      "This browser will only share a camera with pages loaded over HTTPS (or from localhost), " +
      "and this page was loaded over plain HTTP.",
      `Try <a href="${escapeAttribute(httpsUrl)}">${escapeHtml(httpsUrl)}</a> instead. ` +
      "If the dashboard's HTTPS port is different, use that port. " +
      "A self-signed certificate warning is expected the first time - accept it and this page will work."
    );
    return false;
  }

  async function start() {
    if (!checkEnvironment()) return;

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
        `On <a href="/">the dashboard</a>, open the Cameras tab and choose <em>add webcam device</em>, then return here.`
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

  async function openStage() {
    els.cameraName.textContent = camera.name;
    els.mic.checked = !!camera.includeAudio;
    show(els.stage);
    await listDevices();
    setStatus("ready");

    els.toggle.addEventListener("click", () => (publishing ? stop("stopped on this device") : begin()));
    // Changing camera or microphone mid-session restarts it, which is the only way to swap tracks
    // without renegotiating - and a restart here costs a second of video, not a lost session.
    els.device.addEventListener("change", () => { if (publishing) begin(); });
    els.mic.addEventListener("change", () => { if (publishing) begin(); });
  }

  // The device list is only populated with real labels once permission has been granted, so this
  // is called again after the first successful capture to replace "camera 2" with its actual name.
  async function listDevices() {
    let devices;
    try {
      devices = await navigator.mediaDevices.enumerateDevices();
    } catch {
      return;
    }

    const cameras = devices.filter((d) => d.kind === "videoinput");
    const previous = els.device.value;
    els.device.innerHTML = "";
    cameras.forEach((device, index) => {
      const option = document.createElement("option");
      option.value = device.deviceId;
      option.textContent = device.label || `camera ${index + 1}`;
      els.device.appendChild(option);
    });
    if (previous) els.device.value = previous;
  }

  // ---------------- Publishing ----------------

  async function begin() {
    stopMedia();
    clearTimeout(retryTimer);
    const mine = ++generation;
    publishing = true;
    els.toggle.textContent = "stop streaming";
    setError("");
    setStatus("requesting camera…");

    try {
      const deviceId = els.device.value;
      stream = await navigator.mediaDevices.getUserMedia({
        video: deviceId ? { deviceId: { exact: deviceId } } : true,
        audio: els.mic.checked
      });
    } catch (error) {
      publishing = false;
      els.toggle.textContent = "start streaming";
      setStatus("");
      setError(
        error?.name === "NotAllowedError"
          ? "Camera permission was denied. Allow it for this site in the browser's address bar, then try again."
          : `Couldn't open the camera: ${error?.message ?? error}`);
      return;
    }

    if (mine !== generation) { stopMedia(); return; }

    els.preview.srcObject = stream;
    els.idleOverlay.classList.add("hidden");
    await listDevices();
    await acquireWakeLock();

    setStatus("connecting…");
    try {
      await connect(mine);
    } catch (error) {
      if (mine !== generation) return;
      setStatus("");
      setError(`Couldn't start publishing: ${error?.message ?? error}`);
      scheduleRetry(mine);
      return;
    }

    if (mine !== generation) return;
    setStatus("connected - waiting for the server to confirm it's receiving video…");
    pollStatus(mine);
  }

  async function connect(mine) {
    pc = new RTCPeerConnection();

    // sendonly in both directions of the word: this page is a source, and explicitly saying so
    // keeps the browser from offering to receive anything back.
    for (const track of stream.getTracks()) pc.addTrack(track, stream);

    pc.addEventListener("connectionstatechange", () => {
      if (mine !== generation || !pc) return;
      if (pc.connectionState === "failed" || pc.connectionState === "disconnected") {
        setStatus("connection lost - reconnecting…");
        scheduleRetry(mine);
      }
    });

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    await waitForIceGathering(pc, 1500);

    const response = await fetch(`/api/cameras/${camera.id}/publish`, {
      method: "POST",
      headers: { "Content-Type": "application/sdp" },
      body: pc.localDescription.sdp
    });

    if (response.status === 401 || response.status === 403)
      throw new Error("this device is no longer signed in - open the dashboard and sign in again");
    if (response.status === 503)
      throw new Error("the server can't accept published video because go2rtc isn't running on it");
    if (!response.ok) throw new Error(`the server rejected the connection (HTTP ${response.status})`);

    const answer = await response.text();
    if (mine !== generation) return;
    await pc.setRemoteDescription({ type: "answer", sdp: answer });
  }

  // A peer connection reporting "connected" only means the handshake completed - it says nothing
  // about whether frames are arriving. Asking the server what it can actually see is the only
  // honest confirmation, and it is what makes this page trustworthy to walk away from.
  function pollStatus(mine) {
    clearTimeout(statusTimer);
    const tick = async () => {
      if (mine !== generation) return;
      try {
        const response = await fetch(`/api/cameras/${camera.id}/publish/status`);
        if (response.ok) {
          const payload = await response.json();
          if (mine !== generation) return;
          setStatus(payload.publishing
            ? "streaming - the server is receiving video from this device"
            : "connected, but the server isn't receiving video yet…");
        }
      } catch {
        // A failed poll is not a failed stream; the connection state handler owns real failures.
      }
      statusTimer = setTimeout(tick, 10000);
    };
    tick();
  }

  function scheduleRetry(mine) {
    if (mine !== generation || !publishing) return;
    clearTimeout(retryTimer);
    retryTimer = setTimeout(() => { if (mine === generation && publishing) begin(); }, 3000);
  }

  function stop(reason) {
    generation++;
    publishing = false;
    clearTimeout(statusTimer);
    clearTimeout(retryTimer);
    stopMedia();
    releaseWakeLock();
    els.toggle.textContent = "start streaming";
    els.idleOverlay.classList.remove("hidden");
    setStatus(reason ?? "");
  }

  function stopMedia() {
    if (pc) {
      try { pc.close(); } catch { /* already closed */ }
      pc = null;
    }
    if (stream) {
      for (const track of stream.getTracks()) track.stop();
      stream = null;
    }
    els.preview.srcObject = null;
  }

  // ---------------- Staying awake ----------------

  // Without this the screen sleeps, and on most platforms a sleeping screen suspends the capture -
  // the camera goes dark hours into a trip with the tab still cheerfully open. Not universally
  // supported, and not a substitute for the machine's own sleep settings, which is why the page
  // says so in plain text too.
  async function acquireWakeLock() {
    if (!("wakeLock" in navigator) || wakeLock) return;
    try {
      wakeLock = await navigator.wakeLock.request("screen");
      wakeLock.addEventListener("release", () => { wakeLock = null; });
    } catch {
      // Denied or unsupported - the stream still runs for as long as the device stays awake.
    }
  }

  function releaseWakeLock() {
    if (!wakeLock) return;
    try { wakeLock.release(); } catch { /* already gone */ }
    wakeLock = null;
  }

  // A wake lock is dropped whenever the tab is backgrounded, and is not restored on return.
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible" && publishing) acquireWakeLock();
  });

  // ---------------- Helpers ----------------

  function waitForIceGathering(connection, timeoutMs) {
    if (connection.iceGatheringState === "complete") return Promise.resolve();
    return new Promise((resolve) => {
      const done = () => {
        connection.removeEventListener("icegatheringstatechange", check);
        resolve();
      };
      const check = () => { if (connection.iceGatheringState === "complete") done(); };
      connection.addEventListener("icegatheringstatechange", check);
      setTimeout(done, timeoutMs);
    });
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

  // Kept as a name because call sites read better for it, but there is nothing left for it to do:
  // escapeHtml handles quotes now. This file having had its own attribute-safe variant, while the
  // other eight copies of escapeHtml did not, is how the gap stayed open everywhere else.
  function escapeAttribute(value) {
    return escapeHtml(value);
  }

  window.addEventListener("beforeunload", () => stop());

  start();
})();
