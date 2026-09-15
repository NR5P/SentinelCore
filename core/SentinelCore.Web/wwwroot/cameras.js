window.GateWatchCameras = (function () {
  // SettingsSecrets.Mask. A camera's password is no longer part of the camera list - it is the
  // login to a device on the network, and the list is fetched by anyone allowed to watch video
  // at all - so what arrives here is this, and the real value is fetched one camera at a time
  // from the admin-only reveal endpoint. See CameraEndpoints.Masked.
  const SECRET_MASK = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

  let cameras = [];
  let editingId = null;
  // Which of the two camera forms is currently open - a webcam device has no URL or PTZ
  // fields, so saveCamera has to know not to read them.
  let publishedForm = false;
  // The webcam form's picker follows SentinelPublisher (publisher.js) while it is open; this is
  // how it stops following when the form closes.
  let webcamCaptureUnsubscribe = null;
  let shownCaptureStream = null;
  const hlsInstances = new Map();
  const webrtcConnections = new Map();
  const mseConnections = new Map();

  // How long a "this tier isn't available" verdict is trusted before the next tile is allowed to
  // find out for itself. These used to be plain booleans that, once set, stayed set for the life
  // of the page - which is the life of the tab, since this is a single-page dashboard people leave
  // open for days. That made a momentary server-side condition permanent: the server backs WebRTC
  // off for a few seconds while go2rtc restarts, one tile happens to ask during that window, and
  // every tile in that tab is on HLS until somebody reloads. Re-probing costs one small request
  // per tile per interval, against a grid already fetching a video segment per camera per second.
  const TIER_UNAVAILABLE_TTL_MS = 2 * 60 * 1000;

  // Set when the server answers 503 for a WebRTC offer - its "go2rtc isn't usable here" signal.
  // MSE runs through the same go2rtc, so it's skipped too, straight to HLS.
  let webrtcUnavailableUntil = 0;

  // Set when the MSE endpoint itself is missing or refused (older server, go2rtc gone) so other
  // tiles skip the doomed WebSocket attempt for a while. Distinct from webrtcUnavailable:
  // WebRTC can be dead (media port unreachable) while MSE works fine - that's its whole reason
  // to exist.
  let mseUnavailableUntil = 0;

  const webrtcUnavailable = () => Date.now() < webrtcUnavailableUntil;
  const mseUnavailable = () => Date.now() < mseUnavailableUntil;

  // Leaving the Live tab pauses players instead of tearing them down immediately, so a quick
  // flip to another tab and back doesn't force a full HLS reconnect. Only if you stay away
  // longer than this do we actually release the players/connections.
  const LIVE_TEARDOWN_GRACE_MS = 90 * 1000;

  // Which rendition a tile watches, by how big it currently is. A grid tile is a few hundred
  // pixels wide, so a main stream is decoded at 3840x2160 to be drawn at 320 - the detail lands
  // nowhere, and the tile pays for it in decode time, bandwidth and the size of each keyframe it
  // has to wait for. Fullscreen is the one place those pixels are actually visible, so that is
  // where main earns its cost.
  //
  // The server picks main on its own for a camera with no low-res URL (Go2RtcManager's
  // EffectiveQuality), so asking for "sub" here never has to know which cameras have one.
  const GRID_QUALITY = "sub";
  const FULLSCREEN_QUALITY = "main";

  // The rendition each tile is currently connected at, so a fullscreenchange can tell whether it
  // actually needs to reconnect or is already showing the right one.
  const liveTileQuality = new Map();

  // Video elements whose lifetime-scoped listeners are already attached. Weak so a rebuilt Live
  // tab's discarded tiles are collectable.
  const livePlayerBound = new WeakSet();
  let liveTeardownTimer = null;
  let liveBuilt = false;
  let liveBuiltCameraIds = null;

  // Live tile order, as this signed-in person arranged it (see LiveOrderEndpoints - it's stored
  // per account, so one household member rearranging their grid doesn't move anyone else's).
  // liveVisibleIds is the order actually on screen right now; the server list is the last saved
  // state of it.
  let liveOrderIds = [];
  let liveVisibleIds = [];
  let liveOrderSaveTimer = null;
  let draggingCameraId = null;

  // Keyed by cameraId - a camera with no entry here just has the gate/door check off.
  let gateChecks = new Map();
  let gateCheckEditingCameraId = null;

  let cameraGroups = [];
  let groupEditingId = null;

  // Why the camera list has a state and not just a length: an empty array is three different
  // situations - still fetching, fetched and genuinely empty, or the fetch failed - and rendering
  // all three as "no cameras added yet" told people with four cameras that they owned none. The
  // failed case is the one that really misled: load() used to swallow the error, leaving the array
  // empty and the screen asserting a fact about their setup that came from a dropped request.
  let loadState = "loading"; // loading | ready | failed

  // Only the camera list gates this. The other three endpoints feed decorations - the gate/door
  // badge, the group cards, the saved tile order - and they used to share a Promise.all with the
  // cameras, which meant the slowest of four unrelated requests decided when a camera first
  // appeared. They still start together, but they settle on their own and repaint when they land.
  //
  // Returns whether the camera list actually arrived, so the caller can decide whether it is safe
  // to remember that cameras have been loaded - see the tab switch in app.js.
  async function load() {
    const auxiliary = loadAuxiliary();
    if (loadState !== "ready") loadState = "loading";

    try {
      const response = await fetch("/api/cameras");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      cameras = await response.json();
      loadState = "ready";
    } catch (err) {
      console.error("Failed to load cameras", err);
      cameras = [];
      loadState = "failed";
    }

    // Deliberately not awaited: the whole point is that the list paints without them.
    auxiliary.catch(() => {});
    return loadState === "ready";
  }

  async function loadAuxiliary() {
    // One slow or broken decoration endpoint must not take the others down with it, so each is
    // caught to null rather than rejecting the group.
    const [gateChecksResponse, groupsResponse, liveOrderResponse] = await Promise.all([
      fetch("/api/gate-checks").catch(() => null),
      fetch("/api/camera-groups").catch(() => null),
      fetch("/api/live-order").catch(() => null)
    ]);

    // Failing to read the order is not worth failing the whole load over - an empty list just
    // means the grid falls back to the camera store's own order, exactly as it did before this
    // was a preference at all.
    liveOrderIds = liveOrderResponse?.ok ? (await liveOrderResponse.json()).cameraIds ?? [] : [];

    gateChecks = new Map();
    if (gateChecksResponse?.ok) {
      const list = await gateChecksResponse.json();
      for (const config of list) gateChecks.set(config.cameraId, config);
    }

    cameraGroups = groupsResponse?.ok ? await groupsResponse.json() : [];
    repaintDecorations();
  }

  // The list painted before these arrived, so whatever is on screen is now one repaint behind.
  // Both halves are cheap and safe to run against a view that isn't showing: renderList rebuilds
  // static cards, and applyLiveOrder only sets a CSS order property - it never touches a player,
  // so a tile keeps the connection it already has.
  function repaintDecorations() {
    if (loadState !== "ready") return;
    if (document.getElementById("cameras-root")?.children.length) renderList();
    if (document.getElementById("camera-groups-list-root")?.children.length) renderGroupsList();
    if (liveBuilt) applyLiveOrder(orderedCameraIds());
  }

  // The empty-state slot for both tabs: what to say when there is no list to draw yet. Kept in one
  // place so the Cameras tab and the Live grid can never disagree about whether you own cameras.
  function placeholderMarkup(emptyMessage) {
    if (loadState === "loading") return `<div class="empty-state">loading cameras&hellip;</div>`;
    if (loadState === "failed") {
      return `<div class="empty-state">Couldn't reach the server to load your cameras.<br>` +
        `<button type="button" class="btn btn-small" data-retry-cameras>Try again</button></div>`;
    }
    return `<div class="empty-state">${emptyMessage}</div>`;
  }

  // One delegated handler rather than one per rendered placeholder - the retry button is recreated
  // on every repaint, and rebinding it each time is how stale listeners accumulate.
  //
  // Repaints only the tab actually on screen. Rendering the hidden one would be worse than
  // useless for Live: showLive builds players, and building them into a hidden view spends
  // connections on video nobody is watching.
  document.addEventListener("click", async (event) => {
    if (!event.target.closest("[data-retry-cameras]")) return;
    await load();
    if (isVisible("cameras-root")) renderList();
    if (isVisible("live-root")) showLive();
  });

  // Cheap "is this actually on screen" test - app.js shows one view at a time by toggling a
  // .hidden class on the container, and offsetParent goes null for anything inside it.
  function isVisible(id) {
    const el = document.getElementById(id);
    return el !== null && el.offsetParent !== null;
  }

  function escapeHtml(value) {
    // Deliberately a string replace rather than the textContent/innerHTML trick this used to be.
    // That trick escapes exactly what a *text node* needs - & < > - and quotes are not among them,
    // because a quote means nothing in text. Half the interpolations here are into attributes
    // (alt=, title=, data-tip=, value=), and there a surviving double quote ends the attribute and
    // everything after it is parsed as more attributes. Confirmed against a real DOM: an event
    // summary reading `A man held a sign reading " onerror=alert(1) x="` produced an <img> carrying
    // an onerror the markup never contained.
    //
    // Vision models write these summaries, and they quote things - a sign, a brand, a plate - so
    // the merely-broken version of this happens without anybody attacking anything.
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // ---------------- Cameras management tab ----------------

  function renderList() {
    const root = document.getElementById("cameras-root");
    root.innerHTML = "";
    if (cameras.length === 0) {
      root.innerHTML = placeholderMarkup("no cameras added yet");
      return;
    }

    for (const camera of cameras) {
      root.appendChild(buildCameraCard(camera));
    }
  }

  function buildCameraCard(camera) {
    const card = document.createElement("div");
    card.className = "camera-card";

    const gateCheck = gateChecks.get(camera.id);

    const info = document.createElement("div");
    info.className = "camera-card-info";
    // A webcam device has no address to show, and showing an empty line reads as a broken camera.
    // What matters about it instead is that something has to be publishing for it to work at all.
    const publisher = window.SentinelPublisher;
    const publishing = publisher?.snapshot();
    const streamingHere = camera.isBrowserPublished && publishing?.camera?.id === camera.id;
    const sourceLine = !camera.isBrowserPublished
      ? escapeHtml(camera.streamUrl)
      : streamingHere
        ? `<span class="camera-card-streaming-here">streaming from this browser</span> - ${escapeHtml(publishing.status)}`
        : "webcam device - not streaming from this browser";
    // A save that turned "stream from this device" on closes the form, so a capture that then
    // fails (the certificate, usually) has nowhere to be seen but here.
    const failedHere = camera.isBrowserPublished && publishing?.failedCamera?.id === camera.id && publishing.error;
    const failureLine = failedHere
      ? `<span class="camera-card-meta camera-card-error">couldn't start streaming from this browser: ${publishing.error.html ? publishing.error.message : escapeHtml(publishing.error.message)}</span>`
      : "";
    // A plain-HTTP page can't be the camera, and the card is where someone looks when the tile is
    // blank - so it says what to do rather than leaving that to the form.
    const secureHint = camera.isBrowserPublished && publisher && !publisher.canCapture()
      ? `<span class="camera-card-meta">to stream from this device, open <a href="${escapeHtml(publisher.httpsUrl("/#cameras"))}">the secure address</a> (the edit form explains the one-time certificate step)</span>`
      : "";

    const keepAwake = streamingHere
      ? `<details class="camera-card-meta camera-card-keep-awake"><summary>keep this device awake - what to set</summary>${publisher.keepAwakeHtml()}</details>`
      : "";
    const lastSeen = camera.isBrowserPublished && !streamingHere
      ? `<span class="camera-card-meta camera-card-last-seen" data-camera="${escapeHtml(camera.id)}">${lastSeenText(camera.id)}</span>`
      : "";

    info.innerHTML = `
      <span class="camera-card-name">${escapeHtml(camera.name)}</span>
      <span class="camera-card-meta">${sourceLine}</span>
      ${lastSeen}
      ${failureLine}
      ${keepAwake}
      ${secureHint}
      <span class="camera-card-meta">${camera.includeAudio ? "video + audio" : "video only"}</span>
      ${camera.ptzEnabled ? `<span class="camera-card-meta">PTZ enabled</span>` : ""}
      ${gateCheck?.enabled ? `<span class="camera-card-meta">gate check: every ${gateCheck.intervalMinutes} min${gateCheck.checkBlocked ? ", blocked-camera check on" : ""}</span>` : ""}
    `;

    const actions = document.createElement("div");
    actions.className = "camera-card-actions";
    const editBtn = document.createElement("button");
    editBtn.className = "btn";
    editBtn.textContent = "edit";
    editBtn.setAttribute("data-tip-right", "");
    editBtn.dataset.tip = camera.isBrowserPublished
      ? "Rename this camera, pick which of this device's cameras it uses, or start and stop streaming from here."
      : "Change this camera's name, stream URL, or login details.";
    editBtn.addEventListener("click", () => openForm(camera));
    const zoneBtn = document.createElement("button");
    zoneBtn.className = "btn";
    zoneBtn.textContent = "trigger zone";
    zoneBtn.setAttribute("data-tip-right", "");
    zoneBtn.dataset.tip = "Draw an area to watch for motion, and choose whether to save a video clip when something moves in it.";
    zoneBtn.addEventListener("click", () => window.GateWatchTriggers.openZoneEditor(camera));
    const gateCheckBtn = document.createElement("button");
    gateCheckBtn.className = "btn";
    gateCheckBtn.textContent = "gate check";
    gateCheckBtn.setAttribute("data-tip-right", "");
    gateCheckBtn.dataset.tip = "Periodically ask the AI whether a gate or door in view is open, and notify if it's been left open - independent of Blue Iris and trigger zones.";
    gateCheckBtn.addEventListener("click", () => openGateCheckForm(camera));
    const deleteBtn = document.createElement("button");
    deleteBtn.className = "btn";
    deleteBtn.textContent = "delete";
    deleteBtn.setAttribute("data-tip-right", "");
    deleteBtn.dataset.tip = "Remove this camera from the dashboard. Does not touch Blue Iris or the camera itself.";
    deleteBtn.addEventListener("click", () => removeCamera(camera));

    // PTZ has nothing to drive on a webcam device, but trigger zones and gate checks work exactly
    // as they do on a real camera, so they stay.
    if (camera.isBrowserPublished) {
      const publishBtn = document.createElement("a");
      publishBtn.className = "btn";
      publishBtn.textContent = "publish page";
      publishBtn.href = `publish.html?camera=${encodeURIComponent(camera.id)}`;
      publishBtn.target = "_blank";
      publishBtn.rel = "noopener";
      publishBtn.setAttribute("data-tip-right", "");
      publishBtn.dataset.tip =
        "A page that does nothing but stream, for a device left running unattended. " +
        "Open it on the device that has the camera and press start. If the camera is on this device, edit is enough.";
      if (streamingHere) {
        const stopBtn = document.createElement("button");
        stopBtn.className = "btn";
        stopBtn.textContent = "stop streaming";
        stopBtn.setAttribute("data-tip-right", "");
        stopBtn.dataset.tip = "Stop sending this device's camera to the dashboard. The camera stays; edit it to start again.";
        stopBtn.addEventListener("click", () => { publisher.stop(); renderList(); });
        actions.append(editBtn, stopBtn, publishBtn, zoneBtn, gateCheckBtn, deleteBtn);
      } else {
        actions.append(editBtn, publishBtn, zoneBtn, gateCheckBtn, deleteBtn);
      }
    } else {
      actions.append(editBtn, zoneBtn, gateCheckBtn, deleteBtn);
    }

    card.append(info, actions);
    return card;
  }

  // ---------------- When did a webcam device last stream? ----------------

  // The one question an empty webcam tile raises is whether the device just went to sleep or was
  // never started, and the server (PublisherWatchdog) knows. Fetched per card, cached briefly so
  // the list re-rendering every few seconds while another camera streams doesn't turn into a
  // request per card per render.
  const publishStatus = new Map();
  const PUBLISH_STATUS_TTL_MS = 30000;

  function lastSeenText(cameraId) {
    const cached = publishStatus.get(cameraId);
    if (!cached || Date.now() - cached.at > PUBLISH_STATUS_TTL_MS) refreshPublishStatus(cameraId);
    return cached ? describePublishStatus(cached.payload) : "";
  }

  async function refreshPublishStatus(cameraId) {
    // Mark it in flight so the next render doesn't ask again before this answer lands.
    publishStatus.set(cameraId, { at: Date.now(), payload: publishStatus.get(cameraId)?.payload ?? null });
    try {
      const response = await fetch(`/api/cameras/${cameraId}/publish/status`);
      if (!response.ok) return;
      const payload = await response.json();
      publishStatus.set(cameraId, { at: Date.now(), payload });
      for (const el of document.querySelectorAll(`.camera-card-last-seen[data-camera="${cameraId}"]`))
        el.textContent = describePublishStatus(payload);
    } catch {
      // Left blank - the card already says it is a webcam device.
    }
  }

  function describePublishStatus(payload) {
    if (!payload) return "";
    if (payload.publishing) return "streaming from another device - the server is receiving video";
    if (!payload.lastSeenAt) return "has never streamed since the server started";
    return `not streaming - last video received ${timeAgo(payload.lastSeenAt)}`;
  }

  function timeAgo(iso) {
    const seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
    if (seconds < 90) return "moments ago";
    const minutes = Math.round(seconds / 60);
    if (minutes < 90) return `${minutes} min ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 36) return `${hours} h ago`;
    return `${Math.round(hours / 24)} days ago`;
  }

  // ---------------- Gate/door open check ----------------

  function openGateCheckForm(camera) {
    closeForm();
    closeDiscovery();
    gateCheckEditingCameraId = camera.id;
    const config = gateChecks.get(camera.id);

    const wrap = document.getElementById("gate-check-form-wrap");
    const fields = document.getElementById("gate-check-form-fields");
    const errors = document.getElementById("gate-check-form-errors");
    errors.innerHTML = "";
    fields.innerHTML = "";

    fields.appendChild(buildCheckboxField("gate-check-enabled", `Check ${camera.name} for an open gate/door`,
      config?.enabled ?? false,
      "When on, a snapshot from this camera is periodically sent to the AI to check whether a gate or door is open, and you're notified if it's been left open."));
    fields.appendChild(buildTextField("gate-check-label", "What to check (e.g. \"the front gate\", \"the garage door\")",
      config?.checkLabel ?? "the gate",
      "Describes what's in this camera's view to the AI - be specific if the camera sees more than one thing."));
    fields.appendChild(buildNumberField("gate-check-interval", "Check every (minutes)",
      config?.intervalMinutes ?? 10, 1,
      "How often to capture a snapshot and check it. Each check is a real AI analysis, so shorter intervals mean more API/Ollama load."));
    fields.appendChild(buildCheckboxField("gate-check-blocked", "Also check whether the camera's own view is blocked or covered",
      config?.checkBlocked ?? true,
      "Notifies if the lens appears covered, obstructed, or physically moved - a normal dark nighttime frame doesn't count. Uses the same snapshot, no extra AI calls."));
    fields.appendChild(buildNumberField("gate-check-remind", "Remind again if still open after (hours, optional)",
      config?.remindIfStillOpenAfterHours ?? "", 1,
      "Leave blank to only notify once when it first opens. Set this to also get a reminder if it's still open after this many hours."));

    wrap.classList.remove("hidden");
    wrap.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  function closeGateCheckForm() {
    gateCheckEditingCameraId = null;
    document.getElementById("gate-check-form-wrap").classList.add("hidden");
  }

  function buildNumberField(id, label, value, min, tip) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide";
    if (tip) row.dataset.tip = tip;
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "number";
    input.className = "field-input";
    input.id = id;
    input.min = String(min);
    input.value = value === null || value === undefined ? "" : String(value);
    row.appendChild(input);
    return row;
  }

  async function saveGateCheck() {
    const cameraId = gateCheckEditingCameraId;
    if (!cameraId) return;

    const remindRaw = document.getElementById("gate-check-remind").value.trim();
    const body = {
      enabled: document.getElementById("gate-check-enabled").checked,
      checkLabel: document.getElementById("gate-check-label").value.trim() || "the gate",
      checkBlocked: document.getElementById("gate-check-blocked").checked,
      intervalMinutes: parseInt(document.getElementById("gate-check-interval").value, 10) || 10,
      remindIfStillOpenAfterHours: remindRaw === "" ? null : parseInt(remindRaw, 10)
    };

    const errorsEl = document.getElementById("gate-check-form-errors");
    errorsEl.innerHTML = "";

    try {
      const response = await fetch(`/api/gate-checks/${cameraId}`, {
        method: "PUT",
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

      closeGateCheckForm();
      await load();
      renderList();
    } catch {
      const li = document.createElement("li");
      li.textContent = "Network error while saving.";
      errorsEl.appendChild(li);
    }
  }

  // A webcam camera is configured by what it *doesn't* have: no address, no credentials, no PTZ,
  // nothing to test a connection to. Rather than show those fields disabled, the form drops them
  // entirely - the only things left to decide are what to call it and whether to take the
  // device's microphone. Everything else is settled when the device itself connects.
  function openWebcamForm(camera) {
    closeDiscovery();
    closeGateCheckForm();
    editingId = camera ? camera.id : null;
    publishedForm = true;

    const wrap = document.getElementById("camera-form-wrap");
    const fields = document.getElementById("camera-form-fields");
    document.getElementById("camera-form-title").textContent =
      camera ? `edit webcam device - ${camera.name}` : "add webcam device";
    document.getElementById("camera-form-errors").innerHTML = "";
    fields.innerHTML = "";
    setTestStatus("", null);

    fields.appendChild(buildTextField("cam-name", "Name", camera?.name ?? "",
      "Whatever you want to call this device, e.g. 'living room laptop'. Shown on the Live and Events tabs."));
    fields.appendChild(buildCheckboxField("cam-audio", "Include the device's microphone", camera?.includeAudio ?? false,
      "Stream the device's microphone alongside its camera, into live view and saved clips."));

    fields.appendChild(buildWebcamCaptureBlock(camera));

    const note = document.createElement("p");
    note.className = "field-note field-row-wide";
    note.innerHTML = window.SentinelPublisher.keepAwakeHtml() +
      " For a device that is only ever going to sit somewhere and stream, its camera card has a publish page that does nothing else.";
    fields.appendChild(note);

    // PTZ and connection testing are meaningless here, but saveCamera still reads these inputs -
    // hidden fields keep it from having to care which form built the panel.
    fields.appendChild(buildHiddenField("cam-url", ""));
    fields.appendChild(buildHiddenField("cam-username", ""));
    fields.appendChild(buildHiddenField("cam-password", ""));
    fields.appendChild(buildHiddenField("cam-ptz-service-url", ""));
    fields.appendChild(buildHiddenField("cam-ptz-token", ""));

    document.getElementById("camera-test-btn").classList.add("hidden");
    wrap.classList.remove("hidden");
    wrap.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  // The picker for the camera on *this* device, inside the form, so that opening the form,
  // picking a camera and saving is the whole job when the device with the webcam is the one the
  // dashboard is open on. That is what everyone tries first, and before this the form could only
  // say "now go to another page". publish.html still exists for a device that is only ever going
  // to sit somewhere and stream: it is this block with nothing else around it.
  //
  // On a page that can't capture at all (plain HTTP - see SentinelPublisher.canCapture) the block
  // is the explanation instead: the certificate to trust and a button to the secure address, since
  // a picker that silently shows no cameras is exactly the dead end this replaces.
  function buildWebcamCaptureBlock(camera) {
    const publisher = window.SentinelPublisher;
    const block = document.createElement("div");
    block.className = "field-row field-row-wide webcam-capture";

    if (!publisher.canCapture()) {
      block.innerHTML =
        `<span class="field-label">Camera on this device</span>` +
        `<div class="webcam-capture-blocked">` +
        `<p class="field-note">${publisher.escapeHtml(publisher.insecureReason())} ` +
        `Two steps, once, on this device - then this form shows the camera picker:</p>` +
        `<div class="webcam-capture-blocked-fix field-note">…</div></div>`;
      publisher.insecureFixHtml("/#cameras").then((html) => {
        const fix = block.querySelector(".webcam-capture-blocked-fix");
        if (fix) fix.innerHTML = html;
      });
      return block;
    }

    const current = publisher.snapshot();
    const streamingHere = !!camera && current.camera?.id === camera.id;
    const streamingElsewhere = current.publishing && !streamingHere;

    block.dataset.tip =
      "The cameras this browser can see. Pick one, check the preview, and save - with the switch on, " +
      "streaming starts straight away and keeps going while this dashboard tab stays open.";
    block.innerHTML = `
      <label class="field-label" for="cam-device">Camera on this device</label>
      <select id="cam-device" class="field-input"></select>
      <div class="publish-video-wrap webcam-capture-preview">
        <video id="cam-preview" class="publish-video" autoplay playsinline muted></video>
        <div id="cam-preview-idle" class="publish-idle-overlay">no camera yet</div>
      </div>
      <p id="cam-capture-status" class="publish-status"></p>
      <ul id="cam-capture-errors" class="field-errors"></ul>`;

    // On by default for a new camera and for one already streaming from here. For an existing
    // camera the answer is asked of the server below: nobody publishing to it means the person
    // editing it on a device that can capture almost certainly wants this to be the device,
    // while a phone already streaming to it must not be silently displaced by a rename.
    const streamSwitch = buildCheckboxField("cam-stream-here", "Stream from this device",
      streamingHere || (!camera && !streamingElsewhere),
      "On: saving starts streaming this device's camera to the dashboard, for as long as this tab is open. " +
      "Off: nothing is sent from here - stream it from the device that has the camera instead.");
    block.appendChild(streamSwitch);
    const switchInput = streamSwitch.querySelector("input");

    if (streamingElsewhere) {
      switchInput.disabled = true;
      const busy = document.createElement("p");
      busy.className = "field-note";
      busy.textContent = `This browser is already streaming as "${current.camera.name}" - stop that first to use its camera here.`;
      block.appendChild(busy);
    } else if (camera && !streamingHere) {
      fetch(`/api/cameras/${camera.id}/publish/status`)
        .then((response) => (response.ok ? response.json() : null))
        .then((payload) => {
          // Only while the same form is still open, and only if nobody has touched the switch.
          if (payload && !payload.publishing && document.getElementById("cam-stream-here") === switchInput && !switchInput.dataset.touched)
            switchInput.checked = true;
        })
        .catch(() => { /* unknown is left as off */ });
      switchInput.addEventListener("change", () => { switchInput.dataset.touched = "1"; });
    }

    const select = block.querySelector("#cam-device");
    // Changing camera mid-stream restarts the stream on the new one; before a stream it re-opens
    // the preview, so what is on screen is always the camera that would be sent.
    select.addEventListener("change", () => {
      const now = publisher.snapshot();
      if (camera && now.camera?.id === camera.id)
        publisher.start({ camera: now.camera, deviceId: select.value, includeMic: now.includeMic, host: "dashboard" });
      else if (!now.publishing)
        publisher.preview(select.value);
    });

    releaseWebcamCapture();
    webcamCaptureUnsubscribe = publisher.onChange((state) => renderWebcamCapture(block, state));
    renderWebcamCapture(block, current);
    if (!current.publishing) publisher.preview(current.deviceId);
    return block;
  }

  async function renderWebcamCapture(block, state) {
    const status = block.querySelector("#cam-capture-status");
    const video = block.querySelector("#cam-preview");
    if (!status || !video) return;
    status.textContent = state.status;

    const errors = block.querySelector("#cam-capture-errors");
    errors.innerHTML = "";
    if (state.error) {
      const li = document.createElement("li");
      if (state.error.html) li.innerHTML = state.error.message; else li.textContent = state.error.message;
      errors.appendChild(li);
    }

    // Only when the capture actually changed: re-pointing the <video> at the same stream restarts
    // it, and the device list only changes when a capture has (permission, or a new camera).
    if (state.stream !== shownCaptureStream) {
      shownCaptureStream = state.stream;
      video.srcObject = state.stream;
      block.querySelector("#cam-preview-idle").classList.toggle("hidden", !!state.stream);
      if (state.stream) await window.SentinelPublisher.populateDeviceSelect(block.querySelector("#cam-device"));
    }
  }

  // Closing the form ends a preview but never a stream: the picker is a window onto the
  // publisher, not its owner, and a camera saved with "stream from this device" on keeps going
  // after the form is gone - that is the whole point of it.
  function releaseWebcamCapture() {
    if (webcamCaptureUnsubscribe) {
      webcamCaptureUnsubscribe();
      webcamCaptureUnsubscribe = null;
    }
    shownCaptureStream = null;
    const publisher = window.SentinelPublisher;
    if (publisher && !publisher.snapshot().publishing) publisher.stop();
  }

  // What the form asked for, read before closeForm tears the picker down. Null when this page
  // can't capture, in which case saving changes nothing about who is streaming.
  function readWebcamIntent() {
    const streamHere = document.getElementById("cam-stream-here");
    if (!streamHere) return null;
    return {
      streamHere: streamHere.checked && !streamHere.disabled,
      deviceId: document.getElementById("cam-device")?.value ?? ""
    };
  }

  function applyWebcamIntent(cameraId, name, includeMic, intent) {
    const publisher = window.SentinelPublisher;
    const current = publisher.snapshot();
    const streamingHere = current.camera?.id === cameraId;
    if (!intent.streamHere) {
      if (streamingHere) publisher.stop();
      return;
    }
    // Already doing exactly this - a rename shouldn't cost a second of video.
    if (streamingHere && current.deviceId === intent.deviceId && current.includeMic === !!includeMic) return;
    publisher.start({ camera: { id: cameraId, name }, deviceId: intent.deviceId, includeMic, host: "dashboard" });
  }

  function openForm(camera) {
    if (camera?.isBrowserPublished) return openWebcamForm(camera);
    publishedForm = false;
    document.getElementById("camera-test-btn").classList.remove("hidden");
    closeDiscovery();
    closeGateCheckForm();
    editingId = camera ? camera.id : null;
    const wrap = document.getElementById("camera-form-wrap");
    const fields = document.getElementById("camera-form-fields");
    const errors = document.getElementById("camera-form-errors");
    document.getElementById("camera-form-title").textContent =
      camera ? `edit camera - ${camera.name}` : "add camera";
    errors.innerHTML = "";
    fields.innerHTML = "";
    setTestStatus("", null);

    fields.appendChild(buildTextField("cam-name", "Name", camera?.name ?? "",
      "Whatever you want to call this camera, e.g. 'front gate'. Shown on the Live and Events tabs."));
    fields.appendChild(buildTextField("cam-url", "Stream URL (rtsp:// or http://)", camera?.streamUrl ?? "",
      "The camera's video stream address, not its web page. Look for an RTSP or streaming section in the camera's own settings."));
    fields.appendChild(buildTextField("cam-username", "Username (optional)", camera?.username ?? "",
      "Only needed if the camera requires a login to view its stream."));
    fields.appendChild(buildPasswordField("cam-password", "Password (optional)", camera?.password ?? "",
      "Password that goes with the username above. Stored on this machine so the stream can be opened.",
      camera?.id ? `/api/cameras/${camera.id}/password` : null));
    fields.appendChild(buildCheckboxField("cam-audio", "Include audio", camera?.includeAudio ?? false,
      "Include the camera's microphone in the live view and saved clips, if it has one."));
    fields.appendChild(buildCheckboxField("cam-ptz", "Pan/Tilt/Zoom (PTZ) joystick", camera?.ptzEnabled ?? false,
      "Shows on-screen directional controls in Live view for cameras that can physically move. " +
      "Turned on automatically when Test Connection detects an ONVIF PTZ service - you can also " +
      "turn it on by hand, but the controls won't do anything without one."));
    // Not shown to the user directly - just carries whatever Test Connection last detected (or
    // this camera's existing values, if editing) through to saveCamera(), the same way the
    // Stream URL/Name fields get silently filled in above rather than the user typing them.
    fields.appendChild(buildHiddenField("cam-ptz-service-url", camera?.onvifPtzServiceUrl ?? ""));
    fields.appendChild(buildHiddenField("cam-ptz-token", camera?.onvifProfileToken ?? ""));

    wrap.classList.remove("hidden");
    wrap.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  function closeForm() {
    editingId = null;
    releaseWebcamCapture();
    document.getElementById("camera-form-wrap").classList.add("hidden");
    // Both panels share one footer, so a test-connection error left behind by the URL form would
    // otherwise still be sitting there the next time the webcam form - which has nothing to test -
    // is opened.
    document.getElementById("camera-form-errors").innerHTML = "";
    setTestStatus("", null);
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

  // `revealUrl` is null when adding a camera - there is nothing stored to fetch, so this is an
  // ordinary password box with a show/hide toggle, exactly as it always was.
  //
  // On an existing camera the box arrives holding the mask, and "show" fetches the real password
  // before revealing it. Until then the box is read-only: leaving the mask in place is what tells
  // the server to keep the stored password (see the PUT in CameraEndpoints), and read-only stops
  // a click-and-type from turning that into "••••••••newpass" and saving it. Pressing show
  // unlocks the box as well as filling it in, so checking a password and changing it are one
  // step rather than two.
  function buildPasswordField(id, label, value, tip, revealUrl = null) {
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

    const masked = value === SECRET_MASK && !!revealUrl;
    input.readOnly = masked;
    if (revealUrl) input.dataset.revealUrl = revealUrl;

    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "field-toggle-btn";
    toggle.textContent = "show";
    toggle.setAttribute("data-tip-right", "");
    toggle.dataset.tip = masked
      ? "Fetch this camera's saved password from the server and show it."
      : "Reveal the password so you can check it.";

    toggle.addEventListener("click", async () => {
      if (input.value === SECRET_MASK && input.dataset.revealUrl) {
        toggle.disabled = true;
        toggle.textContent = "…";
        const real = await fetchStoredPassword(input.dataset.revealUrl);
        toggle.disabled = false;
        if (real === null) {
          // Swapping bullets for bullets reads as a broken button, so say what happened.
          toggle.textContent = "show";
          toggle.dataset.tip = "Could not read the saved password from the server. Try again, or type a new one over it.";
          return;
        }
        input.value = real;
        input.readOnly = false;
        input.type = "text";
        toggle.textContent = "hide";
        toggle.dataset.tip = "Reveal the password so you can check it.";
        return;
      }
      input.type = input.type === "password" ? "text" : "password";
      toggle.textContent = input.type === "password" ? "show" : "hide";
    });

    // Backing out of an edit: an empty box saves as "this camera has no password", so if the
    // field started masked and is left empty, put the mask back rather than letting a change of
    // mind clear a working credential. Typing a real new one is unaffected.
    if (masked) {
      input.addEventListener("blur", () => {
        if (input.value !== "") return;
        input.value = SECRET_MASK;
        input.type = "password";
        input.readOnly = true;
        toggle.textContent = "show";
      });
    }

    wrap.append(input, toggle);
    row.appendChild(wrap);
    return row;
  }

  async function fetchStoredPassword(url) {
    try {
      const response = await fetch(url);
      if (!response.ok) return null;
      return (await response.json()).password ?? "";
    } catch {
      return null;
    }
  }

  function buildHiddenField(id, value) {
    const input = document.createElement("input");
    input.type = "hidden";
    input.id = id;
    input.value = value;
    return input;
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
      isBrowserPublished: publishedForm,
      // Sent as-is, never coerced to null: the server reads null as "the caller didn't touch this,
      // keep what's stored" and "" as "the user emptied the box on purpose, clear it". Folding an
      // empty box into null - which this used to do - asked the server to keep a credential the
      // user had just deleted, and folding it the other way wiped credentials nobody edited.
      username: document.getElementById("cam-username").value.trim(),
      password: document.getElementById("cam-password").value,
      includeAudio: document.getElementById("cam-audio").checked,
      ptzEnabled: document.getElementById("cam-ptz")?.checked ?? false,
      onvifPtzServiceUrl: document.getElementById("cam-ptz-service-url").value || null,
      onvifProfileToken: document.getElementById("cam-ptz-token").value || null
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

      // Both read before closeForm: it clears editingId and tears the picker down.
      const savedId = editingId ?? payload.id;
      const webcamIntent = publishedForm ? readWebcamIntent() : null;
      closeForm();
      if (webcamIntent && savedId) applyWebcamIntent(savedId, body.name, body.includeAudio, webcamIntent);
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

  // What is really in a password box: whatever was typed, or - if it still holds the mask - the
  // password the server has stored for this camera. Only the paths that actually use the password
  // need this; a save can send the mask untouched and mean "keep it".
  async function currentPassword(field) {
    if (field.value !== SECRET_MASK) return field.value || null;
    if (!field.dataset.revealUrl) return null;
    return await fetchStoredPassword(field.dataset.revealUrl);
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
          // The mask would just fail the camera's login, so resolve it to the stored password
          // first - this dials the camera for real.
          password: await currentPassword(passwordField)
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

        // Always carry through the latest detection, but only ever turn the checkbox ON here,
        // never off - a user who manually enabled PTZ for a camera this particular test didn't
        // detect it on shouldn't have that silently undone by re-testing the connection.
        document.getElementById("cam-ptz-service-url").value = payload.onvifPtzServiceUrl || "";
        document.getElementById("cam-ptz-token").value = payload.onvifProfileToken || "";
        if (payload.supportsPtz) document.getElementById("cam-ptz").checked = true;
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
    closeGateCheckForm();
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

    const deepScan = document.getElementById("discovery-deep-scan").checked;

    // The deep scan contacts every address on the subnet, so it runs for tens of seconds rather
    // than the broadcast's two - say so up front instead of leaving a bare "scanning…" that looks
    // hung. Results stream in as they're found either way.
    setDiscoveryStatus(deepScan ? "scanning every address - this takes a while…" : "scanning…", null);
    document.getElementById("discovery-results-root").innerHTML = "";

    try {
      const response = await fetch("/api/cameras/discovery/scan", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ interfaceAddresses: selected, deepScan })
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
      // The make/model is already the card's name whenever it's known, so repeating it here just
      // prints it twice - now that the discovery scopes name almost every camera, that's the
      // common case rather than the rare one.
      const details = [
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
          openForm({
            name: item.suggestedName, streamUrl: item.ipAddress, username: "", password: "", includeAudio: false,
            ptzEnabled: false, onvifPtzServiceUrl: null, onvifProfileToken: null
          });
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
      const response = await fetch(`/api/cameras/${camera.id}`, { method: "DELETE" });
      if (!response.ok) {
        // Said out loud: a refused delete that silently reloads the same list reads as a button
        // that does nothing.
        const payload = await response.json().catch(() => ({}));
        alert(response.status === 403
          ? "Only an admin account can remove a camera."
          : ((payload.errors || []).join(" ") || payload.error || "Could not remove the camera."));
        return;
      }
      await load();
      renderList();
    } catch (err) {
      console.error("Failed to delete camera", err);
      alert("Network error while removing the camera.");
    }
  }

  // ---------------- Camera groups ----------------

  function openCameraGroups() {
    closeForm();
    closeGateCheckForm();
    closeDiscovery();
    renderGroupsList();
    document.getElementById("camera-groups-wrap").classList.remove("hidden");
    document.getElementById("camera-groups-wrap").scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  function closeCameraGroups() {
    closeGroupForm();
    document.getElementById("camera-groups-wrap").classList.add("hidden");
  }

  function cameraName(id) {
    return cameras.find(c => c.id === id)?.name ?? "deleted camera";
  }

  function renderGroupsList() {
    const root = document.getElementById("camera-groups-list-root");
    root.innerHTML = "";
    if (cameraGroups.length === 0) {
      root.innerHTML = `<div class="empty-state">No camera groups yet - a group links cameras that are physically related, e.g. a gate, driveway, and front door covering one path onto the property.</div>`;
      return;
    }

    for (const group of cameraGroups) {
      const card = document.createElement("div");
      card.className = "camera-card";

      const info = document.createElement("div");
      info.className = "camera-card-info";
      info.innerHTML = `
        <span class="camera-card-name">${escapeHtml(group.name)}</span>
        <span class="camera-card-meta">${group.cameraIds.map(id => escapeHtml(cameraName(id))).join(", ")}</span>
        <span class="camera-card-meta">correlation window: ${group.correlationWindowSeconds}s${group.recordCompanionClips ? ", records companions" : ""}${group.analyzeCompanionCaptures ? ", analyzes companions" : ""}</span>
      `;

      const actions = document.createElement("div");
      actions.className = "camera-card-actions";
      const editBtn = document.createElement("button");
      editBtn.className = "btn";
      editBtn.textContent = "edit";
      editBtn.addEventListener("click", () => openGroupForm(group));
      const deleteBtn = document.createElement("button");
      deleteBtn.className = "btn";
      deleteBtn.textContent = "delete";
      deleteBtn.addEventListener("click", () => removeGroup(group));
      actions.append(editBtn, deleteBtn);

      card.append(info, actions);
      root.appendChild(card);
    }
  }

  function buildCameraMultiSelect(selectedIds) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide";
    row.dataset.tip = "Which of your cameras belong to this group. A camera can only be in one group at a time.";
    row.innerHTML = `<label class="field-label">Cameras in this group</label>`;

    const list = document.createElement("div");
    list.id = "camera-group-camera-list";
    for (const camera of cameras) {
      const optionRow = document.createElement("label");
      optionRow.className = "field-checkbox-row";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = camera.id;
      input.dataset.role = "camera-group-member";
      input.checked = selectedIds.includes(camera.id);
      const text = document.createElement("span");
      text.textContent = camera.name;
      optionRow.append(input, text);
      list.appendChild(optionRow);
    }
    row.appendChild(list);
    return row;
  }

  function openGroupForm(group) {
    groupEditingId = group ? group.id : null;
    const wrap = document.getElementById("camera-group-form-wrap");
    const fields = document.getElementById("camera-group-form-fields");
    const errors = document.getElementById("camera-group-form-errors");
    errors.innerHTML = "";
    fields.innerHTML = "";

    fields.appendChild(buildTextField("camera-group-name", "Name", group?.name ?? "",
      "A label for your own reference, e.g. \"Driveway path\"."));
    fields.appendChild(buildCameraMultiSelect(group?.cameraIds ?? []));
    fields.appendChild(buildNumberField("camera-group-window", "Correlation window (seconds)",
      group?.correlationWindowSeconds ?? 45, 5,
      "How many seconds apart counts as the same incident - roughly how long it takes to walk between these cameras."));
    fields.appendChild(buildCheckboxField("camera-group-record", "Record companion clips",
      group?.recordCompanionClips ?? true,
      "When one camera in this group triggers, also save a clip from the others - as long as they have \"save a video clip\" turned on for their own trigger zone."));
    fields.appendChild(buildCheckboxField("camera-group-analyze", "Also analyze companion cameras with AI",
      group?.analyzeCompanionCaptures ?? false,
      "Off by default. When on, every camera in the group gets its own full AI analysis and its own Feed entry/notification whenever any one of them triggers - not just a recorded clip. Costs proportionally more AI processing per trigger."));

    wrap.classList.remove("hidden");
  }

  function closeGroupForm() {
    groupEditingId = null;
    document.getElementById("camera-group-form-wrap").classList.add("hidden");
  }

  async function saveGroup() {
    const cameraIds = [...document.querySelectorAll('input[data-role="camera-group-member"]:checked')]
      .map(input => input.value);

    const body = {
      name: document.getElementById("camera-group-name").value.trim(),
      cameraIds,
      correlationWindowSeconds: parseInt(document.getElementById("camera-group-window").value, 10) || 45,
      recordCompanionClips: document.getElementById("camera-group-record").checked,
      analyzeCompanionCaptures: document.getElementById("camera-group-analyze").checked
    };

    const errorsEl = document.getElementById("camera-group-form-errors");
    errorsEl.innerHTML = "";

    const endpoint = groupEditingId ? `/api/camera-groups/${groupEditingId}` : "/api/camera-groups";
    const method = groupEditingId ? "PUT" : "POST";

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

      closeGroupForm();
      await load();
      renderGroupsList();
    } catch {
      const li = document.createElement("li");
      li.textContent = "Network error while saving.";
      errorsEl.appendChild(li);
    }
  }

  async function removeGroup(group) {
    if (!confirm(`Remove the camera group "${group.name}"? The cameras themselves aren't affected.`)) return;

    try {
      const response = await fetch(`/api/camera-groups/${group.id}`, { method: "DELETE" });
      if (!response.ok) {
        const payload = await response.json().catch(() => ({}));
        alert(response.status === 403
          ? "Only an admin account can remove a camera group."
          : ((payload.errors || []).join(" ") || payload.error || "Could not remove the camera group."));
        return;
      }
      await load();
      renderGroupsList();
    } catch (err) {
      console.error("Failed to delete camera group", err);
      alert("Network error while removing the camera group.");
    }
  }

  // ---------------- Live tab ----------------

  function showLive() {
    if (liveTeardownTimer) {
      clearTimeout(liveTeardownTimer);
      liveTeardownTimer = null;
    }

    // Membership, not order: which cameras exist is what decides whether the players have to be
    // rebuilt. Order is applied as a CSS property below, so a tile moving costs nothing and never
    // forces a reconnect.
    const currentIds = cameras.map(c => c.id).sort().join(",");
    if (liveBuilt && currentIds === liveBuiltCameraIds) {
      // Still-warm from a recent visit within the grace period - resume rather than
      // reconnecting every player from scratch.
      applyLiveOrder(orderedCameraIds()); // may have been rearranged elsewhere while we were away
      document.querySelectorAll("#live-root video").forEach(video => video.play().catch(() => {}));
      return;
    }
    if (liveBuilt) teardownLive(); // camera list changed since the last visit - rebuild clean

    const root = document.getElementById("live-root");
    root.innerHTML = "";

    if (cameras.length === 0) {
      root.innerHTML = placeholderMarkup("no cameras added yet - add one in the Cameras tab");
      return;
    }

    for (const camera of cameras) {
      const tile = document.createElement("div");
      tile.className = "live-tile";
      tile.dataset.cameraId = camera.id;

      const video = document.createElement("video");
      video.muted = true;
      video.autoplay = true;
      video.playsInline = true;
      video.controls = true;
      // Shows immediately instead of a blank/black tile while the stream connects and buffers -
      // see HlsStreamManager's thumbnail side-output on the server.
      video.poster = `/api/cameras/${camera.id}/thumbnail.jpg`;

      const label = document.createElement("div");
      label.className = "live-tile-label";
      label.textContent = camera.name;
      label.dataset.tip = "Live view. Use the player controls for fullscreen and volume.";

      const status = document.createElement("div");
      status.className = "live-tile-status";
      status.textContent = "connecting…";

      // Which of the three live tiers this tile ended up on - see reportLivePath. Blank until
      // something actually plays, so it never claims a tier the viewer isn't watching.
      const tier = document.createElement("div");
      tier.className = "live-tile-tier";
      tier.dataset.tip = "How this tile is receiving video. webrtc is lowest latency, hls the highest.";
      liveTierBadges.set(camera.id, tier);

      tile.append(video, label, status, tier);
      tile.appendChild(buildZoomControls(tile, video));
      // Nothing to arrange with a single camera - the handle would just be clutter over the frame.
      if (cameras.length > 1) tile.appendChild(buildDragHandle(camera));
      if (camera.ptzEnabled) tile.appendChild(buildPtzControls(camera));
      root.appendChild(tile);

      // Going fullscreen is the moment this tile's pixels start being worth 4K, and leaving it is
      // the moment they stop. Reconnecting at the matching rendition is the only way to change it:
      // the quality is fixed when the stream opens.
      tile.addEventListener("fullscreenchange", () => syncTileQuality(video, status, camera, tile));

      attachPlayer(video, status, camera);
    }

    // Tiles are appended in the camera store's order above and then placed by CSS, so the DOM
    // order and the on-screen order are deliberately independent - see applyLiveOrder.
    applyLiveOrder(orderedCameraIds());

    liveBuilt = true;
    liveBuiltCameraIds = currentIds;
  }

  // ---------------- Live tab: per-account tile order ----------------
  //
  // The order is applied purely through the grid items' CSS `order` property, never by moving
  // tiles in the DOM. Re-parenting a playing <video> risks tearing down what it's showing (an HLS
  // media source, or a live WebRTC track), and a drag is precisely the moment the picture needs to
  // keep running - so the players stay exactly where they were appended and only their placement
  // changes.

  function orderedCameraIds() {
    const remaining = new Set(cameras.map(camera => camera.id));
    const ordered = [];
    for (const id of liveOrderIds) {
      if (!remaining.has(id)) continue; // camera deleted since this order was saved
      ordered.push(id);
      remaining.delete(id);
    }
    // Cameras the saved order has never seen - added since - keep the camera list's own order, at
    // the end, so adding a camera never shuffles tiles somebody has already placed.
    ordered.push(...remaining);
    return ordered;
  }

  function applyLiveOrder(ids) {
    liveVisibleIds = ids;
    for (const tile of document.querySelectorAll("#live-root .live-tile")) {
      const position = ids.indexOf(tile.dataset.cameraId);
      tile.style.order = position < 0 ? ids.length : position;
    }
  }

  function buildDragHandle(camera) {
    const handle = document.createElement("button");
    handle.type = "button";
    handle.className = "live-tile-drag";
    handle.draggable = true;
    handle.textContent = "⠿";
    handle.setAttribute("aria-label", `Move ${camera.name} in the live grid`);
    handle.dataset.tip = "Drag this to move the camera around the grid. Arrow keys work too. The order is saved to your own account - it doesn't change what anyone else sees.";

    handle.addEventListener("dragstart", event => {
      draggingCameraId = camera.id;
      event.dataTransfer.effectAllowed = "move";
      // Firefox ignores a drag that carries no data at all.
      event.dataTransfer.setData("text/plain", camera.id);
      // Drag the whole tile as the ghost image rather than the little handle button.
      const tile = handle.closest(".live-tile");
      if (tile) {
        tile.classList.add("dragging");
        event.dataTransfer.setDragImage(tile, tile.clientWidth / 2, tile.clientHeight / 2);
      }
    });

    handle.addEventListener("dragend", () => {
      draggingCameraId = null;
      document.querySelectorAll("#live-root .live-tile.dragging").forEach(t => t.classList.remove("dragging"));
      saveLiveOrder();
    });

    // Keyboard equivalent of the drag, and the fallback on touch, where HTML5 drag-and-drop
    // doesn't fire at all. Both axes move by one position: on a wrapping grid "left" and "up" are
    // the same request - put this camera one place earlier.
    handle.addEventListener("keydown", event => {
      const step = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1
        : event.key === "ArrowRight" || event.key === "ArrowDown" ? 1
        : 0;
      if (step === 0) return;

      event.preventDefault();
      const from = liveVisibleIds.indexOf(camera.id);
      const to = from + step;
      if (from < 0 || to < 0 || to >= liveVisibleIds.length) return;

      const next = liveVisibleIds.slice();
      next.splice(to, 0, ...next.splice(from, 1));
      applyLiveOrder(next);
      saveLiveOrder();
    });

    return handle;
  }

  // Bound once, on the container, rather than per tile per rebuild - the grid is emptied and
  // refilled whenever the camera list changes, and per-tile listeners would have to be rebound
  // every time (or, worse, quietly stack up).
  function bindLiveDragTargets(root) {
    // dragover's only job is to say "a drop is allowed here"; the reordering itself hangs off
    // dragenter, which fires once per tile crossed instead of continuously - continuous
    // reordering makes tiles flip back and forth under a stationary cursor.
    root.addEventListener("dragover", event => {
      if (!draggingCameraId) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = "move";
    });

    root.addEventListener("dragenter", event => {
      if (!draggingCameraId) return;
      const tile = event.target.closest?.(".live-tile");
      if (!tile || tile.dataset.cameraId === draggingCameraId) return;

      const from = liveVisibleIds.indexOf(draggingCameraId);
      const to = liveVisibleIds.indexOf(tile.dataset.cameraId);
      if (from < 0 || to < 0) return;

      const next = liveVisibleIds.slice();
      next.splice(to, 0, ...next.splice(from, 1));
      applyLiveOrder(next);
    });

    // The grid has already been rearranged live by then - the drop just ends the gesture. Handled
    // anyway so the browser doesn't fall back to its default (navigating to the dragged text).
    root.addEventListener("drop", event => {
      if (!draggingCameraId) return;
      event.preventDefault();
    });
  }

  // Coalesced: a drag crossing several tiles, or a held-down arrow key, is one intent and should
  // cost one write, not one per position.
  function saveLiveOrder() {
    if (liveOrderSaveTimer) clearTimeout(liveOrderSaveTimer);
    liveOrderSaveTimer = setTimeout(async () => {
      liveOrderSaveTimer = null;
      const cameraIds = liveVisibleIds.slice();
      try {
        const response = await fetch("/api/live-order", {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ cameraIds })
        });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        liveOrderIds = (await response.json()).cameraIds ?? cameraIds;
      } catch (err) {
        // Keep the arrangement on screen - it's what the person just asked for - but don't
        // pretend it survived. The next load falls back to the last order the server did save.
        console.error("Failed to save the live order", err);
      }
    }, 400);
  }

  // ---------------- Live tab: digital zoom ----------------
  //
  // Zoom that has nothing to do with the camera's own lens. The PTZ pad below sends a command to
  // the camera and moves the picture for everyone watching; this scales the frames already in this
  // browser, works on every camera including fixed ones, and is nobody else's business. What it
  // can't do is add detail the stream never carried - past a few times magnification you are
  // looking at the encoder's pixels, hence the ceiling.
  const MAX_DIGITAL_ZOOM = 8;

  function buildZoomControls(tile, video) {
    const state = { scale: 1, x: 0, y: 0 };

    const wrap = document.createElement("div");
    wrap.className = "live-tile-zoom";
    wrap.dataset.tip = "Zoom into the picture itself. Ctrl/⌘ + scroll or pinch does the same thing, and you can drag the frame around once it's zoomed in.";

    const button = (text, tip, onClick) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "live-tile-zoom-btn";
      btn.textContent = text;
      btn.dataset.tip = tip;
      btn.addEventListener("click", onClick);
      return btn;
    };

    const out = button("−", "Zoom out.", () => scaleAbout(state.scale / 1.5, 0, 0));
    const readout = button("1×", "Back to the whole frame.", reset);
    readout.classList.add("live-tile-zoom-readout");
    const into = button("+", "Zoom in on the middle of the frame.", () => scaleAbout(state.scale * 1.5, 0, 0));
    // The native transport bar is hidden while zoomed (it would be magnified and clipped along
    // with the picture), so its two useful buttons live here instead.
    // A note with a line through it when muted, rather than a speaker emoji: the emoji renders in
    // full colour and is the only thing on the dashboard that does.
    const mute = button("♪", "Mute or unmute this camera.", () => {
      video.muted = !video.muted;
      render();
    });
    const full = button("⛶", "Fill the screen. The zoom and these controls come with it.", () => {
      if (document.fullscreenElement === tile) document.exitFullscreen();
      else tile.requestFullscreen().catch(() => { /* denied or unsupported - nothing to recover */ });
    });
    wrap.append(out, readout, into, mute, full);

    function clampScale(scale) {
      return Math.min(Math.max(scale, 1), MAX_DIGITAL_ZOOM);
    }

    // (ax, ay) is the point to keep still, measured from the centre of the video - that is what
    // the transform scales about. A point sitting at scale * p + offset has to still be there
    // afterwards, which is what the ratio below works out.
    function scaleAbout(scale, ax, ay) {
      const next = clampScale(scale);
      const ratio = next / state.scale;
      state.x = ax - ratio * (ax - state.x);
      state.y = ay - ratio * (ay - state.y);
      state.scale = next;
      render();
    }

    function panBy(dx, dy) {
      state.x += dx;
      state.y += dy;
      render();
    }

    function reset() {
      state.scale = 1;
      state.x = 0;
      state.y = 0;
      render();
    }

    function render() {
      // Stops the picture being dragged off the edge and leaving a black tile with nothing to aim
      // at: at scale s there is (s - 1) / 2 of a frame hidden on each side, and that is exactly how
      // far it may travel.
      const limitX = (video.clientWidth * (state.scale - 1)) / 2;
      const limitY = (video.clientHeight * (state.scale - 1)) / 2;
      state.x = Math.min(Math.max(state.x, -limitX), limitX);
      state.y = Math.min(Math.max(state.y, -limitY), limitY);

      const zoomed = state.scale > 1.01;
      video.style.transform = zoomed ? `translate(${state.x}px, ${state.y}px) scale(${state.scale})` : "";
      // The browser's own controls are part of the video element, so they would be blown up and
      // then clipped by the tile along with the picture. Hidden while zoomed, back the moment the
      // whole frame is on screen again.
      video.controls = !zoomed;
      // On the tile rather than on this cluster: the cursor and the "keep me visible" rule both
      // belong to the picture, which is a sibling of these buttons.
      tile.classList.toggle("live-tile-zoomed", zoomed);
      readout.textContent = zoomed ? `${state.scale.toFixed(1)}×` : "1×";
      mute.classList.toggle("live-tile-zoom-muted", video.muted);
    }

    // Ctrl/⌘ + wheel is also what a trackpad pinch arrives as. A plain wheel is left alone: it is
    // how the page scrolls, and a grid of tiles that swallowed it would be unusable.
    video.addEventListener("wheel", e => {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      const rect = video.getBoundingClientRect();
      scaleAbout(
        state.scale * Math.exp(-e.deltaY / 200),
        e.clientX - rect.left - rect.width / 2,
        e.clientY - rect.top - rect.height / 2
      );
    }, { passive: false });

    // Two-finger pinch. The video's touch-action allows vertical panning only, which is what makes
    // these events cancellable: the browser would otherwise take a pinch for its own page zoom and
    // hand us uncancellable moves.
    let pinch = null;
    const touchDistance = touches => Math.hypot(
      touches[0].clientX - touches[1].clientX,
      touches[0].clientY - touches[1].clientY
    );

    video.addEventListener("touchstart", e => {
      if (e.touches.length !== 2) return;
      const rect = video.getBoundingClientRect();
      pinch = {
        distance: touchDistance(e.touches),
        scale: state.scale,
        x: (e.touches[0].clientX + e.touches[1].clientX) / 2 - rect.left - rect.width / 2,
        y: (e.touches[0].clientY + e.touches[1].clientY) / 2 - rect.top - rect.height / 2
      };
    }, { passive: true });

    video.addEventListener("touchmove", e => {
      if (!pinch || e.touches.length !== 2 || pinch.distance <= 0) return;
      e.preventDefault();
      // Against the distance the pinch *started* at, not the last frame's: accumulating frame to
      // frame squares the gesture and makes the picture bolt away from the fingers.
      scaleAbout(pinch.scale * (touchDistance(e.touches) / pinch.distance), pinch.x, pinch.y);
    }, { passive: false });

    video.addEventListener("touchend", () => { pinch = null; });
    video.addEventListener("touchcancel", () => { pinch = null; });

    // Drag to move the zoomed picture around. Only once zoomed - at 1x there is nothing to pan,
    // and grabbing the tile is how it gets rearranged.
    let dragFrom = null;
    video.addEventListener("pointerdown", e => {
      if (state.scale <= 1.01 || (e.pointerType === "touch" && pinch)) return;
      dragFrom = { x: e.clientX, y: e.clientY };
      video.setPointerCapture(e.pointerId);
    });
    video.addEventListener("pointermove", e => {
      if (!dragFrom) return;
      panBy(e.clientX - dragFrom.x, e.clientY - dragFrom.y);
      dragFrom = { x: e.clientX, y: e.clientY };
    });
    for (const name of ["pointerup", "pointercancel"]) {
      video.addEventListener(name, () => { dragFrom = null; });
    }

    // Leaving fullscreen changes how big the video is, and the pan limits are in pixels of it.
    tile.addEventListener("fullscreenchange", render);

    render();
    return wrap;
  }

  // ---------------- PTZ (pan/tilt/zoom) joystick ----------------
  //
  // Fire-and-forget by design (errors are swallowed, not surfaced per-click) - a dropped PTZ
  // command just means the camera didn't move that instant, which is self-evident from the video
  // itself, not something worth an error toast over. Continuous-move semantics: pressing a
  // direction starts the camera moving at a fixed moderate speed, and it keeps moving until
  // released - there's no ONVIF "move this far and stop automatically" in this mode.
  const PTZ_SPEED = 0.5;

  function buildPtzControls(camera) {
    const wrap = document.createElement("div");
    wrap.className = "ptz-controls";
    wrap.dataset.tip = "Press and hold a direction to move this camera. Release to stop.";

    const pad = document.createElement("div");
    pad.className = "ptz-pad";
    const directions = [
      [-1, 1, "↖"], [0, 1, "↑"], [1, 1, "↗"],
      [-1, 0, "←"], null, [1, 0, "→"],
      [-1, -1, "↙"], [0, -1, "↓"], [1, -1, "↘"]
    ];
    for (const dir of directions) {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "ptz-btn";
      if (dir === null) {
        btn.classList.add("ptz-home");
        btn.textContent = "⌂";
        btn.dataset.tip = "Return to this camera's home position, if it has one set.";
        btn.addEventListener("click", () => sendPtzCommand(camera.id, "home"));
      } else {
        const [pan, tilt, label] = dir;
        btn.textContent = label;
        bindHoldButton(btn, camera.id, () => sendPtzMove(camera.id, pan * PTZ_SPEED, tilt * PTZ_SPEED, 0));
      }
      pad.appendChild(btn);
    }

    const zoom = document.createElement("div");
    zoom.className = "ptz-zoom";
    const zoomIn = document.createElement("button");
    zoomIn.type = "button";
    zoomIn.className = "ptz-btn";
    zoomIn.textContent = "+";
    zoomIn.dataset.tip = "Zoom in.";
    bindHoldButton(zoomIn, camera.id, () => sendPtzMove(camera.id, 0, 0, PTZ_SPEED));
    const zoomOut = document.createElement("button");
    zoomOut.type = "button";
    zoomOut.className = "ptz-btn";
    zoomOut.textContent = "−";
    zoomOut.dataset.tip = "Zoom out.";
    bindHoldButton(zoomOut, camera.id, () => sendPtzMove(camera.id, 0, 0, -PTZ_SPEED));
    zoom.append(zoomIn, zoomOut);

    wrap.append(pad, zoom);
    return wrap;
  }

  // pointerdown/up/cancel rather than mouse+touch listeners separately - pointer events cover
  // both with one code path. Pointer capture keeps the release firing on THIS button even if a
  // touch drifts off it while held, which matters more here than for a normal button since
  // missing the release would leave the camera moving indefinitely.
  function bindHoldButton(button, cameraId, onPress) {
    const release = (event) => {
      if (event.pointerId !== undefined) {
        try { button.releasePointerCapture(event.pointerId); } catch { /* already released */ }
      }
      sendPtzStop(cameraId);
    };
    button.addEventListener("pointerdown", (event) => {
      event.preventDefault();
      try { button.setPointerCapture(event.pointerId); } catch { /* not supported, still works without capture */ }
      onPress();
    });
    button.addEventListener("pointerup", release);
    button.addEventListener("pointercancel", release);
  }

  // PTZ commands for one camera run one at a time, in the order they were issued.
  //
  // Firing them independently is two requests with no ordering between them, and press and
  // release are milliseconds apart on a quick tap. Whenever the stop won that race the camera was
  // sent stop-then-move and kept turning - a coin flip on request timing, invisible on a long
  // press (where the release lands long after the move) and common on a tap. Chained per camera
  // rather than globally, so driving one camera never waits on another's request.
  const ptzChains = new Map();

  function enqueuePtz(cameraId, operation) {
    // catch() before the chain step: an operation that rejects must not poison the chain, or the
    // next command would be dropped - and the command most likely to follow a failed move is the
    // stop that ends it.
    const previous = ptzChains.get(cameraId) ?? Promise.resolve();
    const next = previous.catch(() => {}).then(operation);
    ptzChains.set(cameraId, next);
    return next;
  }

  function sendPtzMove(cameraId, pan, tilt, zoom) {
    return enqueuePtz(cameraId, async () => {
      try {
        await fetch(`/api/cameras/${cameraId}/ptz/move`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ pan, tilt, zoom })
        });
      } catch { /* best effort - see comment above */ }
    });
  }

  // How many times a stop is attempted before giving up, and the first gap between attempts
  // (doubling). The whole sequence is under a second: a camera that is still turning is not
  // something to be patient about.
  const PTZ_STOP_ATTEMPTS = 3;
  const PTZ_STOP_BACKOFF_MS = 150;

  // A stop is retried where a move is not. Every other PTZ command here is best-effort because a
  // command that didn't land shows up as a camera that didn't move, which the video makes
  // obvious. A stop is the exception: when it doesn't land the camera keeps going, and no amount
  // of looking at the picture fixes that.
  function sendPtzStop(cameraId) {
    return enqueuePtz(cameraId, async () => {
      for (let attempt = 0; attempt < PTZ_STOP_ATTEMPTS; attempt += 1) {
        try {
          const response = await fetch(`/api/cameras/${cameraId}/ptz/stop`, { method: "POST" });
          if (response.ok) return;
        } catch { /* fall through to the retry */ }
        if (attempt < PTZ_STOP_ATTEMPTS - 1) {
          await new Promise((resolve) => setTimeout(resolve, PTZ_STOP_BACKOFF_MS << attempt));
        }
      }
    });
  }

  function sendPtzCommand(cameraId, action) {
    return enqueuePtz(cameraId, async () => {
      try {
        await fetch(`/api/cameras/${cameraId}/ptz/${action}`, { method: "POST" });
      } catch { /* best effort - see comment above */ }
    });
  }

  // Which tier each tile is currently playing on, shown on the tile itself. The server log answers
  // "why was it stuttering last night"; this answers "what am I looking at right now", which is
  // the question you have while standing in front of a feed that looks wrong.
  const liveTierBadges = new Map();

  // A tile can fail and recover in place several times a second - hls.js raises a fatal error, the
  // recovery re-arms the loader, the same error comes straight back. That is worth one log line,
  // not two hundred, and a diagnostic that buries the thing you're looking for is no better than
  // none. Only degraded reports are throttled: playing and failed are transitions, and there are
  // only ever a handful of those per tile.
  const DEGRADED_REPORT_INTERVAL_MS = 5000;
  const lastDegradedReport = new Map();

  // Live-path reporting. The grid degrades silently through three tiers whose latency differs by
  // an order of magnitude, and which one a tile lands on is settled by timing races that leave no
  // trace - so every transition is reported to the server, which logs it. See the /live-path
  // endpoint in CameraEndpoints.
  //
  // Fire-and-forget, and wrapped so thoroughly it cannot throw: a diagnostic that can break a live
  // tile is worse than no diagnostic at all.
  function reportLivePath(camera, tier, outcome, reason, elapsedMs) {
    if (outcome === "degraded") {
      const previous = lastDegradedReport.get(camera.id) ?? 0;
      if (Date.now() - previous < DEGRADED_REPORT_INTERVAL_MS) return;
      lastDegradedReport.set(camera.id, Date.now());
    }

    if (outcome === "playing") {
      const badge = liveTierBadges.get(camera.id);
      if (badge) {
        badge.textContent = tier;
        badge.className = `live-tile-tier live-tile-tier-${tier}`;
      }
    }

    try {
      fetch(`/api/cameras/${camera.id}/live-path`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        // So a report about a tile that is being torn down still gets out - the interesting
        // failures are exactly the ones where something is going away.
        keepalive: true,
        body: JSON.stringify({
          tier,
          outcome,
          reason: reason ?? null,
          elapsedMs: Math.max(0, Math.round(elapsedMs))
        })
      }).catch(() => { /* best effort */ });
    } catch { /* reporting must never break playback */ }
  }

  function attachPlayer(video, status, camera, quality = GRID_QUALITY) {
    liveTileQuality.set(camera.id, quality);
    // Bound once per element, not once per call. attachPlayer runs again every time a tile changes
    // rendition (see syncTileQuality), and these two listeners outlive any single stream - binding
    // them per call would stack a fresh pair on every fullscreen toggle.
    if (!livePlayerBound.has(video)) {
      livePlayerBound.add(video);
      // Not { once: true }: a tile that recovers from a stall has to be able to clear this again,
      // and showStatus below puts it back up whenever playback actually stops.
      let hasPlayed = false;
      video.addEventListener("playing", () => {
        hasPlayed = true;
        status.classList.add("hidden");
      });
      // Only once the tile has played something - "waiting" also fires while a stream is still
      // starting up, and "buffering…" over a tile that has never shown a frame is just a worse way
      // of saying "connecting…".
      video.addEventListener("waiting", () => {
        if (hasPlayed) showStatus(status, "buffering…");
      });
    }

    // MSE first, which is where the Apple apps already start (see LiveStreamPlayer.swift) and for
    // the same reason: it is within a couple hundred milliseconds of WebRTC, and unlike WebRTC it
    // negotiates a codec before any media moves.
    //
    // That negotiation is the whole difference. connectMse below offers go2rtc the exact list this
    // browser can decode - avc1.640033 among them, H.264 High profile level 5.1. WebRTC has no
    // equivalent step, so go2rtc passes the camera's stream through as-is and the browser's WebRTC
    // decoder, which advertises a far narrower profile set, silently renders nothing. Every camera
    // on this network reports High level 5.1, so on 2026-08-27 every tile did this on every load:
    //
    //   webrtc  connected but no frames arrived within 8s   gave up at 12-20s
    //   mse     playing, 4001ms to first frame
    //
    // Twelve to twenty seconds per tile, paid before the rung that works even got its turn - and
    // paid again in every new tab, since the "WebRTC is dead here" latch is sessionStorage with a
    // ten-minute life. That is the whole of the "the website is sluggish but the Mac app isn't"
    // gap, and it is the gap the Apple apps closed by not having this rung at all.
    //
    // WebRTC is kept for the one case MSE cannot serve - a browser with no MediaSource - rather
    // than deleted, because nothing here has shown WebRTC to be broken in general, only that it
    // cannot carry these cameras' profile without a transcode nobody is asking go2rtc to do.
    // HLS remains the last rung: go2rtc missing from the server (the one-time 503 above), or a
    // network path that only forwards HTTP.
    if (webrtcUnavailable()) {
      attachHls(video, status, camera, quality); // no go2rtc on the server - MSE would 503 too
    } else if ("MediaSource" in window) {
      connectMse(video, status, camera, quality);
    } else if (typeof RTCPeerConnection !== "undefined" && !webrtcKnownDead()) {
      connectWebRtc(video, status, camera, 0, quality);
    } else {
      attachHls(video, status, camera, quality);
    }
  }

  // Drop just this tile's stream, leaving the rest of the Live tab alone. Used when a tile changes
  // rendition: the quality is chosen when the connection opens, so switching means reconnecting.
  function teardownCameraStream(cameraId, video) {
    const hls = hlsInstances.get(cameraId);
    if (hls) {
      try { hls.destroy(); } catch { /* already gone */ }
      hlsInstances.delete(cameraId);
    }

    const pc = webrtcConnections.get(cameraId);
    if (pc) {
      try { pc.close(); } catch { /* already closed */ }
      webrtcConnections.delete(cameraId);
    }

    // Through the session's own cleanup rather than by hand: it is the only thing that takes the
    // reportPlaying listener back off the element, and this tile is about to get a new session
    // that adds another one.
    const session = mseConnections.get(cameraId);
    if (session) {
      if (session.cleanup) session.cleanup();
      else {
        session.done = true;
        for (const t of session.timers) clearInterval(t);
        session.timers = [];
        if (session.ws) { try { session.ws.close(); } catch { /* already closed */ } }
        if (session.objectUrl) { try { URL.revokeObjectURL(session.objectUrl); } catch { /* fine */ } }
      }
      mseConnections.delete(cameraId);
    }

    video.pause();
    video.playbackRate = 1;
    video.srcObject = null;
    video.removeAttribute("src");
    video.load();
  }

  // Fullscreen wants detail, the grid wants latency. Reconnect the tile at whichever rendition
  // matches the size it is now, but only when that is not the one it is already on - the browser
  // fires fullscreenchange on every tile, and reconnecting one that did not change size would
  // black out a working picture for no reason.
  function syncTileQuality(video, status, camera, tile) {
    const wanted = document.fullscreenElement === tile ? FULLSCREEN_QUALITY : GRID_QUALITY;
    if (liveTileQuality.get(camera.id) === wanted) return;
    teardownCameraStream(camera.id, video);
    showStatus(status, "connecting…");
    attachPlayer(video, status, camera, wanted);
  }

  // How long the whole WebRTC attempt gets - offer, answer, ICE, first media - before the tile
  // gives up on this attempt. Long enough that the first attempt after a server restart can pay
  // for go2rtc's own startup and the camera connect; short enough that an environment where the
  // media port is simply unreachable (VPN, tunnel, client firewall) doesn't hold a black tile
  // for tens of seconds before the HLS fallback gets its turn.
  const WEBRTC_CONNECT_TIMEOUT_MS = 6000;

  // Straight failures (never showed a frame) before the tile stops burning time on WebRTC and
  // falls back to HLS, which has its own recovery ladder.
  const WEBRTC_MAX_FAILURES = 2;

  // Set (per tab) once a tile has run the whole WebRTC ladder without ever rendering a frame,
  // so reloads and later tiles in this tab go straight to HLS instead of re-proving that the
  // media port is unreachable, one 6-second attempt at a time.
  //
  // This stored a bare "1" and was documented as being cleared by opening a new tab. In practice
  // that was the *only* thing that cleared it, and the flag was a one-way door: webrtcKnownDead()
  // routes straight past connectWebRtc, while rememberWebrtcAlive() only ever runs from inside
  // connectWebRtc's own watchdog - so nothing reachable after the latch closed could reopen it.
  // A single unlucky moment (the server restarting, go2rtc still coming up, the six-second budget
  // below expiring twice) therefore pinned that tab to HLS for as long as it stayed open, which on
  // a dashboard people leave up for days is effectively forever. It is a cache of a fact about the
  // network, and facts about the network expire, so it now stores when it was learned.
  //
  // A stale "1" from a previous release reads back as Number("1") = 1, i.e. learned in 1970, and
  // so expires on sight - no migration needed.
  const WEBRTC_NO_MEDIA_KEY = "webrtcNoMedia";
  const WEBRTC_NO_MEDIA_TTL_MS = 10 * 60 * 1000;
  const webrtcKnownDead = () => {
    try {
      const learnedAt = Number(sessionStorage.getItem(WEBRTC_NO_MEDIA_KEY));
      if (!learnedAt) return false;
      if (Date.now() - learnedAt < WEBRTC_NO_MEDIA_TTL_MS) return true;
      sessionStorage.removeItem(WEBRTC_NO_MEDIA_KEY);
      return false;
    } catch { return false; }
  };
  const rememberWebrtcDead = () => {
    try { sessionStorage.setItem(WEBRTC_NO_MEDIA_KEY, String(Date.now())); } catch { /* storage off - skip */ }
  };
  const rememberWebrtcAlive = () => {
    try { sessionStorage.removeItem(WEBRTC_NO_MEDIA_KEY); } catch { /* storage off - skip */ }
  };

  async function connectWebRtc(video, status, camera, priorFailures, quality = GRID_QUALITY) {
    const pc = new RTCPeerConnection();
    webrtcConnections.set(camera.id, pc);
    const startedAt = Date.now();

    // True while this pc is still the one the tile owns - teardownLive or a newer attempt
    // replaces/removes the map entry, at which point every callback below must do nothing.
    const isCurrent = () => webrtcConnections.get(camera.id) === pc;

    let sawConnected = false;
    let disconnectTimer = null;

    // The peer connection's own state isn't enough to know the feed is alive: if the camera
    // dies (reboot, network blip) while go2rtc stays up, the browser<->go2rtc connection remains
    // happily "connected" and the tile freezes on the last frame it got - which looks exactly
    // like a healthy still scene. So also watch whether decoded time is actually advancing, and
    // treat a clock that has stopped as a dead stream. Reconnecting makes go2rtc pull the camera
    // afresh, which is what recovers it once the camera is back. Paused doesn't count - leaving
    // the Live tab pauses tiles on purpose.
    let lastTime = -1;
    let sawMedia = false;
    let stalledSinceMs = null;
    const stallWatchdog = setInterval(() => {
      if (!isCurrent()) { clearInterval(stallWatchdog); return; }
      if (!sawConnected || video.paused || !video.srcObject) { stalledSinceMs = null; return; }
      if (video.currentTime !== lastTime) {
        if (lastTime >= 0 && !sawMedia) {
          sawMedia = true; // the clock moved between ticks - real frames arrived
          rememberWebrtcAlive();
          reportLivePath(camera, "webrtc", "playing", null, Date.now() - startedAt);
        }
        lastTime = video.currentTime;
        stalledSinceMs = null;
        return;
      }
      stalledSinceMs ??= Date.now();
      if (Date.now() - stalledSinceMs > 8000) {
        fail(sawMedia ? "the stream stalled for 8s" : "connected but no frames arrived within 8s");
      }
    }, 2000);

    const fail = (reason) => {
      if (!isCurrent()) return;
      webrtcConnections.delete(camera.id);
      clearInterval(stallWatchdog);
      if (disconnectTimer) clearTimeout(disconnectTimer);
      try { pc.close(); } catch { /* already closed */ }
      video.srcObject = null;
      reportLivePath(camera, "webrtc", "failed", reason, Date.now() - startedAt);

      // A connection that genuinely worked and then died resets the count - that's a camera or
      // network hiccup worth retrying afresh, not evidence WebRTC doesn't work here. "Worked"
      // means frames actually rendered, not merely that ICE reached "connected": a handshake
      // that succeeds but never delivers a frame (e.g. the server was too overloaded to start
      // the camera pull inside the window) used to reset the count here, so the tile re-ran
      // that doomed handshake forever and never reached the HLS fallback below.
      const failures = (sawMedia ? 0 : priorFailures) + 1;
      if (webrtcUnavailable() || failures >= WEBRTC_MAX_FAILURES) {
        // A ladder that ends with zero frames ever rendered means this environment can't do
        // WebRTC media at all right now - remember that for the rest of the tab so the next
        // reload doesn't sit through the whole ladder again before showing video. MSE is the
        // next rung: same go2rtc stream, but the media rides the dashboard's own HTTP
        // connection instead of a separate port, so it survives the tunnels/VPNs/firewalls
        // that break WebRTC. It falls through to HLS itself if it can't run either.
        if (!sawMedia) rememberWebrtcDead();
        connectMse(video, status, camera, quality);
        return;
      }

      showStatus(status, "reconnecting…");
      setTimeout(() => {
        // Only if the Live tab still exists and nothing else took this tile over meanwhile.
        if (liveBuilt && !webrtcConnections.has(camera.id)) connectWebRtc(video, status, camera, failures, quality);
      }, 2000);
    };

    pc.addEventListener("track", (event) => {
      if (!isCurrent()) return;
      video.srcObject = event.streams[0];
      video.play().catch(() => {});
    });

    pc.addEventListener("connectionstatechange", () => {
      if (!isCurrent()) return;
      switch (pc.connectionState) {
        case "connected":
          sawConnected = true;
          if (disconnectTimer) {
            clearTimeout(disconnectTimer);
            disconnectTimer = null;
          }
          break;
        case "disconnected":
          // Often self-heals within a second or two - only a "disconnected" that persists is
          // treated as dead. "failed" below is definitive and gets no such grace.
          if (!disconnectTimer) {
            disconnectTimer = setTimeout(() => fail("the peer connection stayed disconnected for 4s"), 4000);
          }
          break;
        case "failed":
          fail("the peer connection failed - the media port is usually unreachable");
          break;
      }
    });

    try {
      pc.addTransceiver("video", { direction: "recvonly" });
      if (camera.includeAudio) pc.addTransceiver("audio", { direction: "recvonly" });

      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);
      // The exchange is a single POST with no trickle channel, so give the browser a moment to
      // put its candidates in the offer. Bounded: on a LAN gathering is near-instant, and even
      // an offer without our candidates usually still connects (the server learns our address
      // from the ICE checks we send it).
      await waitForIceGathering(pc, 1000);

      const response = await fetch(`/api/cameras/${camera.id}/webrtc/${quality}`, {
        method: "POST",
        headers: { "Content-Type": "application/sdp" },
        body: pc.localDescription.sdp
      });
      if (response.status === 503) {
        webrtcUnavailableUntil = Date.now() + TIER_UNAVAILABLE_TTL_MS; // see declaration
        throw new Error("WebRTC not available on this server");
      }
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const answer = await response.text();

      if (!isCurrent()) return;
      await pc.setRemoteDescription({ type: "answer", sdp: answer });
      await waitForConnection(pc, WEBRTC_CONNECT_TIMEOUT_MS);
    } catch (error) {
      fail(error?.message ?? "the WebRTC handshake failed");
    }
  }

  function waitForIceGathering(pc, timeoutMs) {
    if (pc.iceGatheringState === "complete") return Promise.resolve();
    return new Promise((resolve) => {
      const finish = () => {
        clearTimeout(timer);
        pc.removeEventListener("icegatheringstatechange", check);
        resolve();
      };
      const check = () => {
        if (pc.iceGatheringState === "complete") finish();
      };
      const timer = setTimeout(finish, timeoutMs);
      pc.addEventListener("icegatheringstatechange", check);
    });
  }

  function waitForConnection(pc, timeoutMs) {
    return new Promise((resolve, reject) => {
      const cleanup = () => {
        clearTimeout(timer);
        pc.removeEventListener("connectionstatechange", check);
      };
      const check = () => {
        if (pc.connectionState === "connected") {
          cleanup();
          resolve();
        } else if (pc.connectionState === "failed" || pc.connectionState === "closed") {
          cleanup();
          reject(new Error(`WebRTC connection ${pc.connectionState}`));
        }
      };
      const timer = setTimeout(() => {
        cleanup();
        reject(new Error("WebRTC connection timed out"));
      }, timeoutMs);
      pc.addEventListener("connectionstatechange", check);
      check();
    });
  }

  // Middle rung of the live ladder: go2rtc streams fMP4 over a WebSocket on the dashboard's own
  // origin (see /api/cameras/{id}/mse server-side), the browser feeds it to MediaSource. Latency
  // sits around a second - more than WebRTC's few hundred ms, far less than HLS's several
  // seconds - and because the media rides the same HTTP connection as the dashboard itself, it
  // works in every environment where the page loads at all. Any failure lands on attachHls.
  function connectMse(video, status, camera, quality = GRID_QUALITY) {
    if (webrtcUnavailable() || mseUnavailable() || !("MediaSource" in window)) {
      attachHls(video, status, camera, quality);
      return;
    }

    const session = { ws: null, timers: [], objectUrl: null, done: false };
    mseConnections.set(camera.id, session);
    const isCurrent = () => mseConnections.get(camera.id) === session;
    const startedAt = Date.now();

    // Whether this tile has been seen actually rendering. Declared up here because both the
    // element event below and the watchdog further down need it, and only the first of them to
    // fire may report.
    let sawMedia = false;

    // Reported from the video element's own event, the way attachHls does it - not from the
    // watchdog poll below. The poll only notices media on its *second* tick, since the first
    // merely seeds lastTime, so the earliest figure it could ever produce is two 2s periods. Every
    // healthy tile on every camera duly reported "4001ms to first frame", which is the poll's
    // period and not the camera's latency: it made the one number this log exists for unable to
    // tell a fast tile from a slow one, and made the MSE rung look four seconds worse than the HLS
    // rung it is several seconds better than.
    const reportPlaying = () => {
      if (session.done || !isCurrent() || sawMedia) return;
      sawMedia = true;
      reportLivePath(camera, "mse", "playing", null, Date.now() - startedAt);
    };

    const cleanup = () => {
      session.done = true;
      for (const t of session.timers) clearInterval(t);
      session.timers = [];
      // The element outlives this session, so a tile torn down mid-catch-up would hand HLS a
      // video still set to play at 1.08x.
      video.playbackRate = 1;
      // The element outlives this session - a tile that falls through to HLS keeps the same
      // <video> - so the listener has to come off, or a day of reconnects leaves a stack of them
      // and an HLS "playing" gets reported as MSE.
      video.removeEventListener("playing", reportPlaying);
      if (session.ws) { try { session.ws.close(); } catch { /* already closed */ } }
      if (session.objectUrl) { try { URL.revokeObjectURL(session.objectUrl); } catch { /* fine */ } }
      if (isCurrent()) mseConnections.delete(camera.id);
    };
    // Exposed so a caller holding only the session (teardownCameraStream) tears it down the same
    // way this rung does, listener removal included.
    session.cleanup = cleanup;

    video.addEventListener("playing", reportPlaying);

    const fail = (reason) => {
      if (session.done) return;
      const owned = isCurrent();
      cleanup();
      if (!owned) return;
      reportLivePath(camera, "mse", "failed", reason, Date.now() - startedAt);
      video.removeAttribute("src");
      video.load();
      attachHls(video, status, camera, quality);
    };

    // Codecs this browser can decode from fMP4, offered to go2rtc so it picks what to send.
    // H.264 profiles cover every camera this app supports; AAC (mp4a) covers camera audio
    // without transcoding; opus in case go2rtc already has a transcoded track running.
    const candidates = ["avc1.640033", "avc1.64002A", "avc1.640029", "avc1.4D4029", "avc1.42E01F", "mp4a.40.2", "mp4a.40.5", "opus"];
    const supported = candidates.filter(c =>
      MediaSource.isTypeSupported(`${c.startsWith("avc1") ? "video" : "audio"}/mp4; codecs="${c}"`));

    const scheme = location.protocol === "https:" ? "wss" : "ws";
    let ws;
    try {
      ws = new WebSocket(`${scheme}://${location.host}/api/cameras/${camera.id}/mse/${quality}`);
    } catch {
      mseUnavailableUntil = Date.now() + TIER_UNAVAILABLE_TTL_MS;
      fail("the browser refused to open the MSE WebSocket");
      return;
    }
    session.ws = ws;
    ws.binaryType = "arraybuffer";
    showStatus(status, "connecting…");

    let mediaSource = null;
    let sourceBuffer = null;
    let pendingMime = null;
    let sourceOpen = false;
    const chunks = [];
    let receivedAnyMedia = false;

    // One watchdog covers both "never got a first byte" and "bytes stopped rendering": if the
    // playhead hasn't moved between ticks (and the tile isn't deliberately paused), give up and
    // let HLS take over. Generous first deadline - the server may be starting go2rtc.
    let lastTime = -1;
    let stalledSinceMs = null;
    session.timers.push(setInterval(() => {
      if (session.done || !isCurrent()) { cleanup(); return; }
      if (video.paused && receivedAnyMedia) { stalledSinceMs = null; return; }
      if (video.currentTime !== lastTime) {
        if (lastTime >= 0 && !sawMedia) {
          sawMedia = true; // the playhead moved between ticks - real frames are rendering
          reportLivePath(camera, "mse", "playing", null, Date.now() - startedAt);
        }
        lastTime = video.currentTime;
        stalledSinceMs = null;
        return;
      }
      stalledSinceMs ??= Date.now();
      if (Date.now() - stalledSinceMs > 10000) {
        fail(receivedAnyMedia ? "media arrived but the playhead stalled for 10s" : "no media within 10s");
      }
    }, 2000));

    // Keep the tile pinned near the live edge: fMP4 keeps arriving while a tab is throttled or a
    // fetch hiccups, so the playhead drifts back from what's already buffered and stays there.
    //
    // This used to allow three full seconds of drift before jumping to half a second behind. That
    // is the right shape for a tile somebody is watching and the wrong one for a camera somebody
    // is *driving*: on a PTZ pad, every one of those seconds is time between pressing a direction
    // and seeing the frame move, and the tile is free to sit at 2.9s of lag indefinitely because
    // nothing pulls it forward until it crosses the line.
    //
    // So: hold a short target, and close small gaps by playing slightly fast rather than by
    // jumping. Rate is invisible where a seek is a visible discontinuity, which is what makes it
    // affordable to correct at a fraction of a second instead of at three. Seeks are kept for
    // gaps too large to walk off - a throttled background tab, a laptop resumed from sleep -
    // where fast-forwarding would take minutes.
    const LIVE_TARGET_S = 0.35;   // how far behind the buffered edge to sit
    const LIVE_CATCHUP_S = 0.15;  // drift past target before easing the rate up
    // Drift past which walking forward costs more than the jump does. 1.08x closes only 0.08s per
    // second, so this bound is really a promise about the worst case: at 1.0 the crawl never has
    // more than ~0.65s to close and recovers in about six seconds, where at 2.0 a 1.7s gap - the
    // measured cost of one throttled tab - took eighteen. Eighteen seconds a second behind is a
    // long time to be steering a camera you cannot see keep up, and a single visible jump is the
    // cheaper of the two. Above this the seek lands on the same target the crawl aims for.
    const LIVE_SEEK_S = 1.0;
    session.timers.push(setInterval(() => {
      if (session.done || !sourceBuffer || video.paused) return;
      try {
        const buffered = sourceBuffer.buffered;
        if (buffered.length === 0) return;
        const end = buffered.end(buffered.length - 1);
        const drift = end - video.currentTime;

        if (drift > LIVE_SEEK_S) {
          video.currentTime = end - LIVE_TARGET_S;
          video.playbackRate = 1;
        } else if (drift > LIVE_TARGET_S + LIVE_CATCHUP_S) {
          // Gentle on purpose. Faster closes the gap sooner but is audible on a camera with sound
          // and visibly jerky without it, and the gap is a fraction of a second to begin with.
          video.playbackRate = 1.08;
        } else if (video.playbackRate !== 1) {
          video.playbackRate = 1;
        }
      } catch { /* buffered can throw while the source is closing */ }
    }, 500));

    const pump = () => {
      if (session.done || !sourceBuffer || sourceBuffer.updating) return;
      // Trim watched video so an all-day tile doesn't grow without bound; only when idle so it
      // never competes with an append.
      try {
        const buffered = sourceBuffer.buffered;
        if (chunks.length === 0 && buffered.length > 0 && video.currentTime - buffered.start(0) > 30) {
          sourceBuffer.remove(buffered.start(0), video.currentTime - 15);
          return; // updateend re-enters pump
        }
      } catch { /* fine - trim is best effort */ }

      const next = chunks.shift();
      if (!next) return;
      try {
        sourceBuffer.appendBuffer(next);
      } catch {
        // QuotaExceeded or a closed source - either way this player is wedged.
        fail("the media buffer rejected an append");
      }
    };

    const openSourceBuffer = () => {
      if (!sourceOpen || !pendingMime || sourceBuffer || session.done) return;
      try {
        sourceBuffer = mediaSource.addSourceBuffer(pendingMime);
      } catch {
        fail(`this browser can't decode ${pendingMime}`); // codec string it can't actually handle
        return;
      }
      sourceBuffer.mode = "segments";
      sourceBuffer.addEventListener("updateend", pump);
      sourceBuffer.addEventListener("error", () => fail("the media source buffer errored"));
      pump();
    };

    ws.addEventListener("open", () => {
      if (session.done) { cleanup(); return; }
      ws.send(JSON.stringify({ type: "mse", value: supported.join(",") }));
      mediaSource = new MediaSource();
      session.objectUrl = URL.createObjectURL(mediaSource);
      mediaSource.addEventListener("sourceopen", () => { sourceOpen = true; openSourceBuffer(); });
      video.srcObject = null;
      video.src = session.objectUrl;
      video.play().catch(() => {});
    });

    ws.addEventListener("message", (event) => {
      if (session.done) return;
      if (typeof event.data === "string") {
        try {
          const msg = JSON.parse(event.data);
          if (msg.type === "mse" && msg.value) {
            pendingMime = msg.value;
            openSourceBuffer();
          }
        } catch { /* not for us */ }
        return;
      }
      receivedAnyMedia = true;
      chunks.push(event.data);
      pump();
    });

    // A refused handshake (older server without the endpoint, go2rtc gone) shows up as an error
    // or an immediate close before any media - mark MSE off for this page load in that case so
    // other tiles don't repeat the attempt. A close after media flowed is just a dropped
    // connection: fail this tile over to HLS but leave MSE available for retries elsewhere.
    ws.addEventListener("error", () => {
      if (!receivedAnyMedia) mseUnavailableUntil = Date.now() + TIER_UNAVAILABLE_TTL_MS;
      fail(receivedAnyMedia ? "the MSE WebSocket errored mid-stream" : "the MSE WebSocket was refused");
    });
    ws.addEventListener("close", () => {
      if (!session.done) fail(receivedAnyMedia ? "the MSE WebSocket closed mid-stream" : "the MSE WebSocket closed before any media");
    });
  }

  function attachHls(video, status, camera, quality = GRID_QUALITY) {
    // This is the bottom rung - the tier a tile lands on when WebRTC and MSE are both unavailable,
    // and the one that visibly rebuffers - so it is the last place that wants to be pulling a
    // 2560x1920 stream per tile. It follows the same rendition rule as the rungs above it: the
    // grid gets the low stream, fullscreen the main one. A camera with no low stream is served its
    // main one either way, so asking for the low one costs nothing where it can't help.
    const src = `/api/cameras/${camera.id}/stream/${quality}/stream.m3u8`;
    video.srcObject = null; // in case a WebRTC attempt got as far as attaching a stream
    const startedAt = Date.now();

    // The bottom rung reports too. It is the tier that visibly rebuffers, so knowing a tile spent
    // the evening here - and how long it took to show anything - is the whole point of the log.
    video.addEventListener("playing", () => {
      reportLivePath(camera, "hls", "playing", null, Date.now() - startedAt);
    }, { once: true });

    if (window.Hls && Hls.isSupported()) {
      const hls = new Hls(LIVE_HLS_CONFIG);
      hls.on(Hls.Events.ERROR, (_event, data) => handleHlsError(hls, video, status, camera, data, quality));
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

  // How the live grid asks hls.js to sit relative to the live edge.
  //
  // This used to be { liveSyncDurationCount: 1 }, chasing the lowest possible delay, and that one
  // number was the main reason tiles rebuffered every few seconds on a perfectly healthy LAN.
  // Two things make 1 unworkable. The server copies video rather than re-encoding it, so it can
  // only cut a segment on a keyframe and the real segment length is the camera's GOP - typically
  // 2-4s, not the 1s the muxer is asked for. And hls.js derives liveMaxLatencyDurationCount from
  // this value (3x by default), so "1 segment behind" also means "hard-seek back to the live edge
  // the moment we're 3 segments behind". Sitting one segment from the edge with a three-segment
  // tripwire means the tripwire fires constantly, and every trip is a visible stall-and-restart.
  //
  // 3 is the figure the HLS spec itself recommends a client start at, and it is expressed in
  // segments rather than seconds so it scales with whatever GOP the camera actually uses. The
  // cost is real - roughly one extra segment of delay - but the feed is steady, and a security
  // camera that stutters every five seconds is worse to watch than one that is two seconds older.
  const LIVE_HLS_CONFIG = {
    liveSyncDurationCount: 3,
    // Well clear of liveSyncDurationCount so ordinary jitter is absorbed by the catch-up below
    // instead of by a seek. Only a genuinely stuck playhead should ever trip this.
    liveMaxLatencyDurationCount: 10,
    // The part that replaces most of those seeks: when the tile has drifted behind its target,
    // hls.js plays slightly fast until it has caught up, instead of jumping. Imperceptible on a
    // camera feed, where nobody knows what the real-time pace is meant to look like.
    maxLiveSyncPlaybackRate: 1.5,
    // A live tile is never scrubbed backwards, so buffered-but-played video is pure memory. Left
    // unbounded a grid of tiles left open all day grows without limit.
    backBufferLength: 30
  };

  // hls.js reports plenty of non-fatal errors in normal operation (a segment that 404s because it
  // rolled off, a gap it skipped) and retries those itself - reacting to them would put a
  // "buffering" badge over a tile that is fine. Only fatal errors need us, and of those, network
  // and media errors are both recoverable in place: restarting the loader or flushing the decoder
  // keeps the same player rather than tearing the tile down. Previously any fatal error left the
  // tile reading "stream unavailable" until the whole Live tab was rebuilt, even after the camera
  // came back.
  // `quality` is the rendition this player was opened at. The restart below has to reopen the
  // same one, and it used to read a `quality` that was not in scope here - a ReferenceError
  // thrown inside the setTimeout, so a tile that hit a fatal demuxer/decoder error was destroyed
  // and never rebuilt: it sat on "restarting stream…" until the whole Live tab was rebuilt.
  function handleHlsError(hls, video, status, camera, data, quality = GRID_QUALITY) {
    if (!data.fatal) return;

    // Reported as degraded rather than failed: the tile stays on HLS and recovers in place. These
    // are the events behind "it went live and then buffered again", so they are exactly what makes
    // the log worth reading - a run of them on one camera says the problem is that camera or its
    // ffmpeg, a run across all of them says it is the server or the network.
    reportLivePath(camera, "hls", "degraded", `fatal ${data.details ?? data.type}`, 0);

    switch (data.type) {
      case Hls.ErrorTypes.NETWORK_ERROR:
        showStatus(status, "reconnecting…");
        hls.startLoad();
        break;
      case Hls.ErrorTypes.MEDIA_ERROR:
        showStatus(status, "recovering…");
        hls.recoverMediaError();
        break;
      default:
        showStatus(status, "restarting stream…");
        hls.destroy();
        if (hlsInstances.get(camera.id) === hls) hlsInstances.delete(camera.id);
        // Fatal demuxer/decoder failures cannot always be recovered on the same MediaSource.
        // Rebuild it once the camera/server is healthy instead of leaving this tile dead until
        // the user changes tabs.
        setTimeout(() => {
          if (!video.isConnected || hlsInstances.has(camera.id)) return;
          attachHls(video, status, camera, quality);
        }, 2000);
        break;
    }
  }

  function showStatus(status, text) {
    status.textContent = text;
    status.classList.remove("hidden");
  }

  function hideLive() {
    // Pause now, but don't tear the players down yet - see LIVE_TEARDOWN_GRACE_MS. A quick flip
    // to another tab and back should just resume, not pay for a full reconnect.
    document.querySelectorAll("#live-root video").forEach(video => video.pause());

    if (liveTeardownTimer) clearTimeout(liveTeardownTimer);
    liveTeardownTimer = setTimeout(teardownLive, LIVE_TEARDOWN_GRACE_MS);
  }

  function teardownLive() {
    liveTeardownTimer = null;
    liveBuilt = false;
    liveBuiltCameraIds = null;
    // The badges point at DOM the next buildLive replaces - holding them would pin detached nodes
    // and let a late report write into a tile nobody is looking at.
    liveTierBadges.clear();
    liveTileQuality.clear();

    for (const hls of hlsInstances.values()) {
      try { hls.destroy(); } catch { /* already gone */ }
    }
    hlsInstances.clear();

    // Closing the peer connection is what actually stops WebRTC media flowing - unlike HLS,
    // a paused <video> still receives (and discards) every frame the server pushes.
    for (const pc of webrtcConnections.values()) {
      try { pc.close(); } catch { /* already closed */ }
    }
    webrtcConnections.clear();

    // Same for MSE: the WebSocket keeps delivering fMP4 to a paused tile until it's closed.
    for (const session of mseConnections.values()) {
      session.done = true;
      for (const t of session.timers) clearInterval(t);
      if (session.ws) { try { session.ws.close(); } catch { /* already closed */ } }
      if (session.objectUrl) { try { URL.revokeObjectURL(session.objectUrl); } catch { /* fine */ } }
    }
    mseConnections.clear();

    document.querySelectorAll("#live-root video").forEach(video => {
      video.pause();
      video.srcObject = null;
      video.removeAttribute("src");
      video.load();
    });
  }

  bindLiveDragTargets(document.getElementById("live-root"));

  document.getElementById("add-camera-btn").addEventListener("click", () => openForm(null));
  document.getElementById("camera-cancel-btn").addEventListener("click", closeForm);
  document.getElementById("camera-save-btn").addEventListener("click", saveCamera);
  document.getElementById("camera-test-btn").addEventListener("click", testConnection);
  document.getElementById("discover-cameras-btn").addEventListener("click", openDiscovery);
  document.getElementById("add-webcam-btn").addEventListener("click", () => openWebcamForm(null));
  // The card of a camera streaming from this browser shows the publisher's status line, which
  // changes every few seconds - keep it honest while the tab is on screen.
  window.SentinelPublisher?.onChange(() => {
    if (loadState === "ready" && !document.getElementById("view-cameras").classList.contains("hidden")) renderList();
  });
  // If this dashboard was streaming a camera when the page last went away - a settings save
  // reloads it, a browser update restarts it - pick that straight back up.
  window.SentinelPublisher?.resume("dashboard");
  document.getElementById("discovery-cancel-btn").addEventListener("click", closeDiscovery);
  document.getElementById("discovery-scan-btn").addEventListener("click", startScan);
  document.getElementById("gate-check-save-btn").addEventListener("click", saveGateCheck);
  document.getElementById("gate-check-cancel-btn").addEventListener("click", closeGateCheckForm);
  document.getElementById("manage-camera-groups-btn").addEventListener("click", openCameraGroups);
  document.getElementById("camera-groups-close-btn").addEventListener("click", closeCameraGroups);
  document.getElementById("add-camera-group-btn").addEventListener("click", () => openGroupForm(null));
  document.getElementById("camera-group-save-btn").addEventListener("click", saveGroup);
  document.getElementById("camera-group-cancel-btn").addEventListener("click", closeGroupForm);

  return { load, renderList, showLive, hideLive };
})();
