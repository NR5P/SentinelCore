// Turns the browser this runs in into one of the dashboard's cameras: capture the device's webcam
// and push it to the server over WebRTC, the exact reverse of the live grid in cameras.js. The
// server hands the media to its go2rtc sidecar, and from there it is an ordinary camera - clips,
// trigger zones and AI analysis all work without knowing where the video came from.
//
// One object, shared by two hosts. The Cameras tab embeds it in a webcam device's form, so that
// opening the form, picking a camera and saving is the whole job when the device with the webcam
// is the one the dashboard is open on - which is the case people actually hit first. publish.html
// is the same thing with nothing else around it, for a device left running for days with no live
// grid or event polling eating its battery. Whichever host, exactly one capture and one peer
// connection exist per browser tab, and they are owned here.
//
// The awkward constraint this manages is that a tab has to keep running unattended: a wake lock
// so the screen doesn't sleep the stream away, a reconnect loop, and a status that distinguishes
// "connected" from "the server is actually receiving frames", which are not the same thing.
window.SentinelPublisher = (function () {
  const CERTIFICATE_URL = "/api/certificate-authority";
  // The dashboard's HTTPS listener. Configurable server-side, but a browser on the plain-HTTP port
  // has no authenticated way to ask, so the shipped default is offered and the text says so.
  const HTTPS_PORT = 3443;

  // What this browser was streaming, kept in localStorage so a page reload - a settings save
  // reloads the dashboard, a browser update restarts it - doesn't end a week of streaming with
  // nobody there to press start again. Tagged with the host that stored it, because both hosts
  // share the origin's storage and only the one that was streaming should pick it back up.
  const INTENT_KEY = "sentinelcore.publisher.intent";

  // Sleep detection. A machine that sleeps freezes this page's timers; when it wakes, the gap
  // between two ticks is however long it slept, while the peer connection may never announce
  // that it died. Three minutes, not one: a background tab's timers are throttled to once a
  // minute, and a spurious "reconnect" every minute would cost a second of video each time.
  const HEARTBEAT_MS = 15000;
  const SUSPEND_GAP_MS = 3 * 60 * 1000;

  const state = {
    publishing: false,
    camera: null,       // { id, name } while publishing
    host: null,         // "dashboard" | "publish" - which page is doing the streaming
    deviceId: "",       // the videoinput in use, once permission has given devices real ids
    includeMic: false,
    stream: null,       // the preview or the published capture - whichever is on screen
    receiving: false,   // the server has confirmed frames are arriving
    status: "",
    error: null,        // { message, html }
    // The camera a start() was for when it failed before connecting. The Cameras tab closes its
    // form on save, so a failure after that has nowhere to show but the camera's own card - and
    // the card has to know the failure was about it.
    failedCamera: null
  };

  let pc = null;
  let wakeLock = null;
  let statusTimer = null;
  let retryTimer = null;
  const listeners = new Set();

  // Bumped every time a session starts or stops, so callbacks belonging to a session that has
  // since been torn down (a slow SDP exchange, a late reconnect timer) can tell they are stale and
  // do nothing. Without it, stopping and restarting quickly leaves the old attempt's error handler
  // free to kill the new session.
  let generation = 0;

  function snapshot() {
    return { ...state };
  }

  function emit() {
    const current = snapshot();
    for (const listener of listeners) {
      try { listener(current); } catch { /* one host's render must not break another's */ }
    }
  }

  function onChange(listener) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  }

  // ---------------- Can this page capture at all? ----------------

  // getUserMedia only exists in a secure context, and "secure" excludes plain http on a LAN
  // address - which is exactly how this dashboard is usually reached. Worth catching up front and
  // explaining precisely, because the browser's own failure here is a bare undefined property.
  function canCapture() {
    return !!(window.isSecureContext && navigator.mediaDevices?.getUserMedia);
  }

  function insecureReason() {
    return "This browser will only share a camera with pages loaded over HTTPS (or from localhost), " +
      "and this page was loaded over plain HTTP.";
  }

  // The same address on the HTTPS port, keeping the path and hash so the person lands where they
  // were - the Cameras tab, say - rather than at the front door.
  function httpsUrl(pathAndHash) {
    return `https://${location.hostname}:${HTTPS_PORT}${pathAndHash}`;
  }

  // HTTPS alone doesn't get there either. The dashboard's certificate is signed by the install's
  // own authority, and a browser that hasn't been told to trust it lets the page load past a
  // warning and then refuses the camera without ever showing a prompt - so the fix offered is the
  // certificate, not just the address, and this page has to be the thing that hands it over: the
  // device reading this is the one that needs it.
  //
  // The authority is only offered when this install actually has one: on a real-domain
  // certificate there is nothing to download, and whatever sent someone here has another cause.
  let certificateOffered = null;
  async function certificateStepHtml() {
    if (certificateOffered === null) {
      try {
        certificateOffered = (await fetch(CERTIFICATE_URL, { method: "HEAD", cache: "no-store" })).ok;
      } catch {
        certificateOffered = false;
      }
    }
    if (!certificateOffered)
      return "Trust the dashboard's HTTPS certificate on this device - see the deployment guide, section 14a.";
    return `<a href="${CERTIFICATE_URL}" download>Download the dashboard's certificate</a> and trust it on this device, once: ` +
      "on a Mac, open the file, then in Keychain Access double-click it and set Trust to <em>Always Trust</em>; " +
      "on Windows, install it into <em>Trusted Root Certification Authorities</em>; " +
      "on Linux, import it in the browser under Settings → Privacy and security → Security → Manage certificates → Authorities " +
      "(Chrome and Firefox keep their own store there, so the system one is not enough); " +
      "on an iPhone, install the profile, then switch it on under Settings → General → About → Certificate Trust Settings; " +
      "on Android, install it as a CA certificate.";
  }

  // The full "you are on the wrong address" instructions, as HTML, with the secure address as a
  // button. Both hosts show this in place of the camera picker when canCapture() is false.
  async function insecureFixHtml(pathAndHash) {
    const url = httpsUrl(pathAndHash);
    return `<p>1. ${await certificateStepHtml()}</p>` +
      `<p>2. Then open this dashboard at its secure address on this device: ` +
      `<a class="btn btn-primary publisher-switch-link" href="${escapeAttribute(url)}">open ${escapeHtml(url)}</a>` +
      ` - if the dashboard's HTTPS port is different, use that port.</p>`;
  }

  // The browser's own reasons are terse, and one is misleading: NotAllowedError is the same answer
  // for "the person clicked block" and for "this page's certificate isn't trusted, so the camera
  // was refused without asking". On a LAN dashboard the second is by far the likelier, and its
  // fix is the certificate rather than the address-bar permission - so it gets named first.
  async function captureFailureMessage(error) {
    if (error?.name === "NotAllowedError" || error?.name === "SecurityError") {
      return {
        html: true,
        message:
          "The browser wouldn't share the camera with this page. " +
          "If you clicked through a certificate warning to get here, that is why - a browser refuses the camera to a " +
          "page whose certificate it doesn't trust, and doesn't ask. " + await certificateStepHtml() + " Then reload. " +
          "If the certificate is already trusted, allow the camera for this site in the address bar and try again."
      };
    }
    if (error?.name === "NotFoundError" || error?.name === "OverconstrainedError")
      return { html: false, message: "No camera was found on this device, or the chosen one has gone away. Pick another one and try again." };
    return { html: false, message: `Couldn't open the camera: ${error?.message ?? error}` };
  }

  // ---------------- Devices ----------------

  // Before permission is granted, browsers list one anonymous entry per kind with a blank id and
  // blank label, so the first real list only exists after a capture has succeeded - preview() is
  // what gets that permission. Called again after every capture so a camera plugged in since
  // shows up too.
  async function listCameras() {
    let devices;
    try {
      devices = await navigator.mediaDevices.enumerateDevices();
    } catch {
      return [];
    }
    return devices
      .filter((d) => d.kind === "videoinput")
      .map((d, index) => ({ deviceId: d.deviceId, label: d.label || `camera ${index + 1}` }));
  }

  // Fills a <select> with the device's cameras, keeping the one in use selected so the list and
  // the picture agree. Shared by both hosts rather than written twice.
  async function populateDeviceSelect(select) {
    const cameras = await listCameras();
    const wanted = state.deviceId || select.value;
    select.innerHTML = "";
    for (const camera of cameras) {
      const option = document.createElement("option");
      option.value = camera.deviceId;
      option.textContent = camera.label;
      select.appendChild(option);
    }
    if (wanted && cameras.some((c) => c.deviceId === wanted)) select.value = wanted;
    return cameras;
  }

  // exact, so a chosen camera is that camera and not the browser's idea of a close match - but
  // only once permission has given the devices real ids. Before that the list holds one blank
  // entry, and asking for a blank id exactly is an OverconstrainedError.
  function videoConstraint(deviceId) {
    return deviceId ? { deviceId: { exact: deviceId } } : true;
  }

  function rememberDevice(captured, requested) {
    state.deviceId = captured.getVideoTracks()[0]?.getSettings()?.deviceId || requested || "";
  }

  // ---------------- Preview ----------------

  // Shows the chosen camera without sending it anywhere. This is what asks the browser for
  // permission, and permission is what turns the device list from one unnamed "camera 1" into
  // every camera the device has, by name - until it has run there is nothing real to pick from
  // and nothing to look at, which reads as a page that does nothing. Video only: the microphone
  // is asked for when streaming starts, and only if it is wanted.
  async function preview(deviceId, statusMessage) {
    if (state.publishing) return state.stream;
    const mine = ++generation;
    stopMedia();
    state.error = null;
    state.status = "opening camera…";
    emit();

    let captured;
    try {
      captured = await navigator.mediaDevices.getUserMedia({ video: videoConstraint(deviceId) });
    } catch (error) {
      if (mine !== generation) return null;
      state.status = "";
      state.error = await captureFailureMessage(error);
      emit();
      return null;
    }
    if (mine !== generation) { for (const track of captured.getTracks()) track.stop(); return null; }

    rememberDevice(captured, deviceId);
    state.stream = captured;
    state.status = statusMessage ?? "previewing - nothing is being sent yet";
    emit();
    return captured;
  }

  // ---------------- Publishing ----------------

  async function start({ camera, deviceId, includeMic, host }) {
    clearTimeout(retryTimer);
    stopMedia();
    const mine = ++generation;
    state.publishing = true;
    state.camera = { id: camera.id, name: camera.name };
    state.host = host ?? state.host;
    state.includeMic = !!includeMic;
    state.receiving = false;
    state.error = null;
    state.status = "requesting camera…";
    emit();

    let captured;
    try {
      captured = await navigator.mediaDevices.getUserMedia({ video: videoConstraint(deviceId), audio: state.includeMic });
    } catch (error) {
      if (mine !== generation) return;
      state.publishing = false;
      state.failedCamera = state.camera;
      state.camera = null;
      state.status = "";
      state.error = await captureFailureMessage(error);
      emit();
      return;
    }
    if (mine !== generation) { for (const track of captured.getTracks()) track.stop(); return; }

    rememberDevice(captured, deviceId);
    state.stream = captured;
    state.failedCamera = null;
    rememberIntent();
    emit();
    await acquireWakeLock();

    state.status = "connecting…";
    emit();
    try {
      await connect(mine);
    } catch (error) {
      if (mine !== generation) return;
      state.status = "";
      state.error = { html: false, message: `Couldn't start publishing: ${error?.message ?? error}` };
      emit();
      scheduleRetry(mine);
      return;
    }

    if (mine !== generation) return;
    state.status = "connected - waiting for the server to confirm it's receiving video…";
    emit();
    pollStatus(mine);
  }

  async function connect(mine) {
    pc = new RTCPeerConnection();

    // sendonly in both directions of the word: this page is a source, and explicitly saying so
    // keeps the browser from offering to receive anything back.
    for (const track of state.stream.getTracks()) pc.addTrack(track, state.stream);

    pc.addEventListener("connectionstatechange", () => {
      if (mine !== generation || !pc) return;
      if (pc.connectionState === "failed" || pc.connectionState === "disconnected") {
        state.receiving = false;
        state.status = "connection lost - reconnecting…";
        emit();
        scheduleRetry(mine);
      }
    });

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    await waitForIceGathering(pc, 1500);

    const response = await fetch(`/api/cameras/${state.camera.id}/publish`, {
      method: "POST",
      headers: { "Content-Type": "application/sdp" },
      body: pc.localDescription.sdp
    });

    if (response.status === 401 || response.status === 403)
      throw new Error("this device is no longer signed in - sign in again");
    if (response.status === 503)
      throw new Error("the server can't accept published video because go2rtc isn't running on it");
    if (!response.ok) throw new Error(`the server rejected the connection (HTTP ${response.status})`);

    const answer = await response.text();
    if (mine !== generation) return;
    await pc.setRemoteDescription({ type: "answer", sdp: answer });
  }

  // A peer connection reporting "connected" only means the handshake completed - it says nothing
  // about whether frames are arriving. Asking the server what it can actually see is the only
  // honest confirmation, and it is what makes a publishing tab trustworthy to walk away from.
  function pollStatus(mine) {
    clearTimeout(statusTimer);
    const tick = async () => {
      if (mine !== generation) return;
      try {
        const response = await fetch(`/api/cameras/${state.camera.id}/publish/status`);
        if (response.ok) {
          const payload = await response.json();
          if (mine !== generation) return;
          state.receiving = !!payload.publishing;
          state.status = payload.publishing
            ? "streaming - the server is receiving video from this device"
            : "connected, but the server isn't receiving video yet…";
          emit();
        }
      } catch {
        // A failed poll is not a failed stream; the connection state handler owns real failures.
      }
      statusTimer = setTimeout(tick, 10000);
    };
    tick();
  }

  function scheduleRetry(mine) {
    if (mine !== generation || !state.publishing) return;
    clearTimeout(retryTimer);
    retryTimer = setTimeout(() => {
      if (mine === generation && state.publishing)
        start({ camera: state.camera, deviceId: state.deviceId, includeMic: state.includeMic });
    }, 3000);
  }

  // Ends publishing and preview alike; the status text is whatever the host wants shown after.
  // A deliberate stop forgets the stored intent; the page going away (keepIntent) does not - that
  // is exactly the case the intent exists for.
  function stop(reason, { keepIntent = false } = {}) {
    generation++;
    if (state.publishing && !keepIntent) forgetIntent();
    state.publishing = false;
    state.camera = null;
    state.failedCamera = null;
    state.receiving = false;
    clearTimeout(statusTimer);
    clearTimeout(retryTimer);
    stopMedia();
    releaseWakeLock();
    state.status = reason ?? "";
    state.error = null;
    emit();
  }

  function stopMedia() {
    if (pc) {
      try { pc.close(); } catch { /* already closed */ }
      pc = null;
    }
    if (state.stream) {
      for (const track of state.stream.getTracks()) track.stop();
      state.stream = null;
    }
  }

  // ---------------- Staying awake ----------------

  // Without this the screen sleeps, and on most platforms a sleeping screen suspends the capture -
  // the camera goes dark hours into a trip with the tab still cheerfully open. Not universally
  // supported, and not a substitute for the machine's own sleep settings, which is why the hosts
  // say so in plain text too.
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
    if (document.visibilityState === "visible" && state.publishing) acquireWakeLock();
  });

  window.addEventListener("beforeunload", () => stop(undefined, { keepIntent: true }));

  // ---------------- Surviving a reload, and a sleep ----------------

  function rememberIntent() {
    try {
      localStorage.setItem(INTENT_KEY, JSON.stringify({
        host: state.host, camera: state.camera, deviceId: state.deviceId, includeMic: state.includeMic, since: Date.now()
      }));
    } catch { /* storage unavailable - a reload will need a hand */ }
  }

  function forgetIntent() {
    try { localStorage.removeItem(INTENT_KEY); } catch { /* nothing stored */ }
  }

  function storedIntent() {
    try {
      const raw = localStorage.getItem(INTENT_KEY);
      return raw ? JSON.parse(raw) : null;
    } catch {
      return null;
    }
  }

  // Picks streaming back up after a reload, if this host was the one streaming. Optionally only
  // for one camera - publish.html is opened for a specific camera and must not start a different
  // one. Returns what it resumed, or null.
  function resume(host, cameraId) {
    const intent = storedIntent();
    if (!intent || intent.host !== host || !intent.camera?.id) return null;
    if (cameraId && intent.camera.id !== cameraId) return null;
    if (!canCapture() || state.publishing) return null;
    start({ camera: intent.camera, deviceId: intent.deviceId, includeMic: intent.includeMic, host });
    return intent.camera;
  }

  let lastBeat = Date.now();
  setInterval(() => {
    const now = Date.now();
    const gap = now - lastBeat;
    lastBeat = now;
    if (gap < SUSPEND_GAP_MS || !state.publishing) return;
    const minutes = Math.round(gap / 60000);
    state.status = `this device was asleep for about ${minutes} minute${minutes === 1 ? "" : "s"} - reconnecting…`;
    state.receiving = false;
    emit();
    start({ camera: state.camera, deviceId: state.deviceId, includeMic: state.includeMic, host: state.host });
  }, HEARTBEAT_MS);

  // What a browser cannot do is stop the machine from sleeping; the wake lock only keeps the
  // screen on, and a closed lid or a power-saving timer ends the stream regardless. So the hosts
  // say what to set, per platform, next to the stream they are asking someone to leave running.
  function keepAwakeHtml() {
    return "<strong>Keep this device awake for as long as you want the feed.</strong> Plug it in, leave the lid open " +
      "and this tab in front, and turn sleep off: " +
      "<em>Mac</em> - System Settings → Battery (or Energy) → Options → <em>Prevent automatic sleeping on power adapter when the display is off</em>, " +
      "or run <code>caffeinate -dis</code> in Terminal and leave it running; " +
      "<em>Windows</em> - Settings → System → Power &amp; battery → Screen and sleep → set sleep to <em>Never</em> when plugged in; " +
      "<em>Linux</em> - Settings → Power → Automatic Suspend → off. " +
      "The dashboard reconnects on its own after a network blip or a reload, and if the server stops receiving video for " +
      "two minutes it sends you a message (email, Telegram and the phone apps - whichever are set up) and another when it is back. " +
      "On a phone, a Mac or a PC, the Sentinel Core app can do this natively instead (Settings → Cameras → <em>Use this device as a camera</em>): " +
      "it keeps the device awake itself, and an Android phone can even turn its screen off.";
  }

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

  // A string replace rather than the textContent/innerHTML trick: that escapes what a text node
  // needs and not quotes, and half the interpolations here are into attributes, where a surviving
  // double quote ends the attribute and everything after it is parsed as more attributes.
  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function escapeAttribute(value) {
    return escapeHtml(value);
  }

  return {
    canCapture, insecureReason, insecureFixHtml, httpsUrl, certificateStepHtml,
    listCameras, populateDeviceSelect,
    preview, start, stop, resume, snapshot, onChange,
    keepAwakeHtml, escapeHtml
  };
})();
