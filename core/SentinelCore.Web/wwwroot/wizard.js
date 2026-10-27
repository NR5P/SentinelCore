window.GateWatchWizard = (function () {
  const overlay = document.getElementById("setup-wizard");
  const titleEl = document.getElementById("wizard-title");
  const stepCountEl = document.getElementById("wizard-step-count");
  const bodyEl = document.getElementById("wizard-body");
  const errorsEl = document.getElementById("wizard-errors");
  const backBtn = document.getElementById("wizard-back-btn");
  const skipBtn = document.getElementById("wizard-skip-btn");
  const nextBtn = document.getElementById("wizard-next-btn");

  let stepIndex = 0;

  const STEPS = [
    {
      id: "welcome",
      title: "WELCOME",
      render() {
        bodyEl.innerHTML = `
          <p>This walks through the handful of settings needed to get alerts working:
          which AI analyses snapshots, at least one camera, and (optionally) how you want
          to be notified.</p>
          <p>Everything here can also be changed later in SETTINGS - nothing here is final,
          and every step past this one can be skipped if you'd rather configure it yourself
          afterward.</p>`;
      }
    },
    {
      id: "api-key",
      title: "API KEY",
      skippable: false,
      async render() {
        bodyEl.innerHTML = `<p>Setting up an API key&hellip;</p>`;
        try {
          const response = await fetch("/api/setup/generate-api-key", { method: "POST" });
          const payload = await response.json();
          bodyEl.innerHTML = `
            <p>Blue Iris authenticates alerts with this key. You only need it if you're
            connecting Blue Iris - if you're only using cameras added here, skip past this.</p>
            <div class="field-row field-row-wide">
              <label class="field-label">API Key</label>
              <div class="field-password-row">
                <input type="text" class="field-input" id="wizard-api-key" value="${escapeHtml(payload.apiKey)}" readonly />
                <button type="button" class="field-toggle-btn" id="wizard-copy-key-btn">copy</button>
              </div>
            </div>
            <p class="wizard-hint">${payload.generated ? "Generated just now." : "Already configured - left as-is."}</p>`;
          document.getElementById("wizard-copy-key-btn").addEventListener("click", () => {
            navigator.clipboard?.writeText(payload.apiKey);
            showToast("API key copied", true);
          });
        } catch {
          bodyEl.innerHTML = `<p>Could not reach the server to set up an API key. You can set
            <code>Bridge:ApiKey</code> by hand later if you need Blue Iris.</p>`;
        }
      }
    },
    {
      id: "vision",
      title: "VISION BACKEND",
      skippable: true,
      render() {
        bodyEl.innerHTML = `
          <p>Which AI actually looks at each snapshot. Ollama runs locally and free but
          needs real hardware; a cloud provider works on any machine but costs a little
          per alert and needs an API key.</p>
          <div class="field-row field-row-wide">
            <label class="field-label">Backend</label>
            <select id="wizard-vision-backend" class="field-input">
              <option value="ollama">Ollama (local)</option>
              <option value="cloud">Cloud Vision</option>
            </select>
          </div>
          <div id="wizard-cloud-fields" class="hidden">
            <div class="field-row field-row-wide">
              <label class="field-label">Provider</label>
              <select id="wizard-cloud-provider" class="field-input">
                <option value="openai">OpenAI (or compatible)</option>
                <option value="anthropic">Anthropic</option>
                <option value="google">Google</option>
              </select>
            </div>
            <div class="field-row field-row-wide">
              <label class="field-label">API Key</label>
              <input type="password" id="wizard-cloud-key" class="field-input" autocomplete="off" />
            </div>
            <div class="field-row field-row-wide">
              <label class="field-label">Model</label>
              <input type="text" id="wizard-cloud-model" class="field-input" placeholder="e.g. gpt-4o-mini" />
            </div>
          </div>
          <div class="wizard-actions">
            <button type="button" class="btn" id="wizard-test-connection-btn">test connection</button>
            <span id="wizard-connection-result"></span>
          </div>`;

        const backendSelect = document.getElementById("wizard-vision-backend");
        const cloudFields = document.getElementById("wizard-cloud-fields");
        backendSelect.addEventListener("change", () => {
          cloudFields.classList.toggle("hidden", backendSelect.value !== "cloud");
        });

        document.getElementById("wizard-test-connection-btn").addEventListener("click", async () => {
          const resultEl = document.getElementById("wizard-connection-result");
          resultEl.textContent = "saving and testing...";
          await saveVisionFields();
          try {
            const response = await fetch("/api/setup/vision-status");
            const payload = await response.json();
            resultEl.textContent = payload.reachable ? "reachable" : "not reachable";
            resultEl.className = payload.reachable ? "wizard-status-ok" : "wizard-status-bad";
          } catch {
            resultEl.textContent = "could not check";
            resultEl.className = "wizard-status-bad";
          }
        });
      },
      async onNext() {
        return saveVisionFields();
      }
    },
    {
      id: "camera",
      title: "ADD A CAMERA",
      skippable: true,
      render() {
        bodyEl.innerHTML = `
          <p>Pick a camera found on your network, or type its IP address or full stream URL
          (most IP cameras expose an <code>rtsp://</code> address). You can add more, or edit
          this later, from the CAMERAS tab.</p>
          <div id="wizard-scan-interfaces"></div>
          <div class="wizard-actions">
            <button type="button" class="btn" id="wizard-scan-btn">scan network</button>
            <span id="wizard-scan-status" class="wizard-hint"></span>
          </div>
          <div id="wizard-scan-results" class="wizard-scan-results"></div>
          <div class="field-row field-row-wide">
            <label class="field-label">Name</label>
            <input type="text" id="wizard-camera-name" class="field-input" placeholder="e.g. front gate" />
          </div>
          <div class="field-row field-row-wide">
            <label class="field-label">IP Address or Stream URL</label>
            <input type="text" id="wizard-camera-url" class="field-input" placeholder="192.168.1.50 or rtsp://192.168.1.50:554/stream" />
          </div>
          <div class="field-row">
            <label class="field-label">Username</label>
            <input type="text" id="wizard-camera-user" class="field-input" />
          </div>
          <div class="field-row">
            <label class="field-label">Password</label>
            <input type="password" id="wizard-camera-pass" class="field-input" autocomplete="off" />
          </div>
          <div class="wizard-actions">
            <button type="button" class="btn" id="wizard-add-camera-btn">add camera</button>
            <span id="wizard-camera-result"></span>
          </div>`;

        document.getElementById("wizard-scan-btn").addEventListener("click", startCameraScan);
        document.getElementById("wizard-add-camera-btn").addEventListener("click", addCamera);

        // Kick the scan off straight away - the multicast probe only takes a couple of seconds,
        // and on a first run "here are your cameras" is far friendlier than an empty URL box.
        loadScanInterfaces().then(startCameraScan);
      },
      leave() {
        stopScanPolling();
      }
    },
    {
      id: "notifications",
      title: "NOTIFICATIONS",
      skippable: true,
      render() {
        bodyEl.innerHTML = `
          <p>Optional - how you want to hear about alerts. Both can also be set up later
          in SETTINGS.</p>
          <div class="field-row field-row-wide field-checkbox-row">
            <label class="switch"><input type="checkbox" id="wizard-email-enabled" /><span class="switch-track"></span></label>
            <span class="field-label">Email</span>
          </div>
          <div id="wizard-email-fields" class="hidden">
            <div class="field-row"><label class="field-label">SMTP Host</label><input type="text" id="wizard-email-host" class="field-input" value="smtp.gmail.com" /></div>
            <div class="field-row"><label class="field-label">SMTP Port</label><input type="number" id="wizard-email-port" class="field-input" value="587" min="1" max="65535" /></div>
            <div class="field-row"><label class="field-label">Username</label><input type="text" id="wizard-email-user" class="field-input" /></div>
            <div class="field-row"><label class="field-label">Password</label><input type="password" id="wizard-email-pass" class="field-input" autocomplete="off" /></div>
            <div class="field-row"><label class="field-label">From Address</label><input type="text" id="wizard-email-from" class="field-input" placeholder="same as username" /></div>
            <div class="field-row"><label class="field-label">To Address</label><input type="text" id="wizard-email-to" class="field-input" /></div>
          </div>
          <div class="field-row field-row-wide field-checkbox-row">
            <label class="switch"><input type="checkbox" id="wizard-telegram-enabled" /><span class="switch-track"></span></label>
            <span class="field-label">Telegram</span>
          </div>
          <div id="wizard-telegram-fields" class="hidden">
            <div class="field-row"><label class="field-label">Bot Token</label><input type="password" id="wizard-telegram-token" class="field-input" autocomplete="off" /></div>
            <div class="field-row"><label class="field-label">Chat ID</label><input type="text" id="wizard-telegram-chat" class="field-input" /></div>
          </div>`;

        document.getElementById("wizard-email-enabled").addEventListener("change", (e) => {
          document.getElementById("wizard-email-fields").classList.toggle("hidden", !e.target.checked);
        });
        document.getElementById("wizard-telegram-enabled").addEventListener("change", (e) => {
          document.getElementById("wizard-telegram-fields").classList.toggle("hidden", !e.target.checked);
        });
      },
      async onNext() {
        const errors = [];

        const emailEnabled = document.getElementById("wizard-email-enabled").checked;
        if (emailEnabled) {
          const ok = await put("/api/settings/email", {
            enabled: true,
            smtpHost: document.getElementById("wizard-email-host").value.trim(),
            smtpPort: Number(document.getElementById("wizard-email-port").value) || 587,
            enableSsl: true,
            username: document.getElementById("wizard-email-user").value.trim(),
            appPassword: document.getElementById("wizard-email-pass").value,
            fromAddress: document.getElementById("wizard-email-from").value.trim(),
            fromName: "Gate Camera",
            toAddress: document.getElementById("wizard-email-to").value.trim(),
            timeoutSeconds: 30,
            duplicateCooldownMinutes: 10,
            minimumConfidence: 0.6,
            attachImage: true
          });
          if (!ok) errors.push("Email settings could not be saved - check the fields.");
        }

        const telegramEnabled = document.getElementById("wizard-telegram-enabled").checked;
        if (telegramEnabled) {
          const ok = await put("/api/settings/telegram", {
            enabled: true,
            botToken: document.getElementById("wizard-telegram-token").value.trim(),
            chatId: document.getElementById("wizard-telegram-chat").value.trim(),
            onlyWhenNotificationRecommended: true,
            minimumConfidence: 0.6,
            attachImage: true,
            timeoutSeconds: 20,
            duplicateCooldownMinutes: 10
          });
          if (!ok) errors.push("Telegram settings could not be saved - check the fields.");
        }

        if (errors.length > 0) {
          showErrors(errors);
          return false;
        }
        return true;
      }
    },
    {
      id: "finish",
      title: "FINISH",
      render() {
        bodyEl.innerHTML = `<p>That's the essentials. Everything else - prompt profiles,
          trigger zones, quiet hours, MQTT, and more - is in SETTINGS whenever you want it.</p>`;
      }
    }
  ];

  // ---------------- Camera step: network scan + add ----------------
  // The same discovery endpoints the CAMERAS tab uses (CameraDiscoveryEndpoints), cut down to
  // what a first run needs: scan the non-virtual interfaces straight away, list what answered,
  // and let one click put a camera's address into the form below.

  let scanPollTimer = null;

  async function loadScanInterfaces() {
    let interfaces;
    try {
      const response = await fetch("/api/cameras/discovery/interfaces");
      if (!response.ok) throw new Error();
      interfaces = await response.json();
    } catch {
      interfaces = [];
    }

    // The user may have moved on to another step while that was loading.
    const root = document.getElementById("wizard-scan-interfaces");
    if (!root) return;
    root.innerHTML = "";
    if (interfaces.length === 0) return;

    // Shown rather than silently picked, same as the CAMERAS tab: the virtual-adapter check is a
    // name heuristic, and a wrong guess either scans nothing useful or sweeps a VPN subnet.
    const label = document.createElement("div");
    label.className = "field-label";
    label.textContent = "networks to scan:";
    root.appendChild(label);
    for (const iface of interfaces) {
      const row = document.createElement("label");
      row.className = "field-checkbox-row";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = iface.address;
      input.dataset.role = "wizard-scan-interface";
      input.checked = !iface.looksVirtual;
      const text = document.createElement("span");
      text.textContent = `${iface.address} (${iface.name}${iface.looksVirtual ? " - looks virtual" : ""})`;
      row.append(input, text);
      root.appendChild(row);
    }

    const deepRow = document.createElement("label");
    deepRow.className = "field-checkbox-row";
    deepRow.dataset.tip = "Also tries every address on the network, for cameras that don't answer the quick scan (ONVIF turned off, or multicast blocked). Takes much longer.";
    const deepInput = document.createElement("input");
    deepInput.type = "checkbox";
    deepInput.id = "wizard-scan-deep";
    const deepText = document.createElement("span");
    deepText.textContent = "check every address (slower)";
    deepRow.append(deepInput, deepText);
    root.appendChild(deepRow);
  }

  function setScanStatus(message, kind) {
    const el = document.getElementById("wizard-scan-status");
    if (!el) return;
    el.textContent = message;
    el.className = "wizard-hint" + (kind ? ` wizard-status-${kind}` : "");
  }

  async function startCameraScan() {
    const resultsEl = document.getElementById("wizard-scan-results");
    if (!resultsEl) return;

    const selected = [...document.querySelectorAll('input[data-role="wizard-scan-interface"]:checked')]
      .map(input => input.value);
    if (selected.length === 0) {
      setScanStatus("no network selected - type the camera's address below instead", "bad");
      return;
    }

    const deepScan = document.getElementById("wizard-scan-deep")?.checked ?? false;
    stopScanPolling();
    resultsEl.innerHTML = "";
    setScanStatus(deepScan ? "checking every address - this takes a while..." : "scanning...", null);

    try {
      const response = await fetch("/api/cameras/discovery/scan", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ interfaceAddresses: selected, deepScan })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        setScanStatus((payload.errors || ["scan could not start"]).join(" "), "bad");
        return;
      }
      pollCameraScan(payload.scanId);
    } catch {
      setScanStatus("network error while starting the scan", "bad");
    }
  }

  function pollCameraScan(scanId) {
    stopScanPolling();
    scanPollTimer = setInterval(async () => {
      // Left the camera step mid-scan - nothing to draw into any more.
      if (!document.getElementById("wizard-scan-results")) {
        stopScanPolling();
        return;
      }
      try {
        const response = await fetch(`/api/cameras/discovery/scan/${scanId}`);
        if (!response.ok) {
          stopScanPolling();
          setScanStatus("scan result no longer available", "bad");
          return;
        }
        const snapshot = await response.json();
        renderScanResults(snapshot.results);
        if (snapshot.status === "Completed") {
          stopScanPolling();
          setScanStatus(snapshot.results.length === 0
            ? "no cameras answered - type the camera's IP address below, or tick \"check every address\" and scan again"
            : `found ${snapshot.results.length} camera(s) - pick one to fill in the form`,
            snapshot.results.length === 0 ? null : "ok");
        }
      } catch {
        stopScanPolling();
        setScanStatus("network error while checking scan progress", "bad");
      }
    }, 1000);
  }

  function stopScanPolling() {
    if (scanPollTimer) {
      clearInterval(scanPollTimer);
      scanPollTimer = null;
    }
  }

  function renderScanResults(results) {
    const root = document.getElementById("wizard-scan-results");
    if (!root) return;
    root.innerHTML = "";

    for (const item of results) {
      const card = document.createElement("div");
      card.className = "camera-card";

      const info = document.createElement("div");
      info.className = "camera-card-info";
      const meta = [item.ipAddress, item.needsCredentials ? "needs username/password" : null,
        item.alreadySaved ? "already added" : null].filter(Boolean).join(" - ");
      info.innerHTML = `
        <span class="camera-card-name">${escapeHtml(item.suggestedName)}</span>
        <span class="camera-card-meta">${escapeHtml(meta)}</span>`;
      card.appendChild(info);

      if (!item.alreadySaved) {
        const actions = document.createElement("div");
        actions.className = "camera-card-actions";
        const useBtn = document.createElement("button");
        useBtn.type = "button";
        useBtn.className = "btn";
        useBtn.textContent = "use";
        useBtn.addEventListener("click", () => {
          // Just the bare address: "add camera" probes it into a real, verified stream URL, once
          // the user has had the chance to type the credentials this scan never asked for.
          document.getElementById("wizard-camera-name").value = item.suggestedName;
          document.getElementById("wizard-camera-url").value = item.ipAddress;
          setCameraResult("", null);
          document.getElementById(item.needsCredentials ? "wizard-camera-user" : "wizard-camera-name").focus();
        });
        actions.appendChild(useBtn);
        card.appendChild(actions);
      }

      root.appendChild(card);
    }
  }

  function setCameraResult(message, kind) {
    const el = document.getElementById("wizard-camera-result");
    if (!el) return;
    el.textContent = message;
    el.className = kind ? `wizard-status-${kind}` : "";
  }

  // A bare IP/host, or a URL with no path yet - what someone types when they know where the
  // camera is but not its stream path. Anything with a real path is taken as-is.
  function looksLikeABareAddress(raw) {
    try {
      const url = new URL(raw.includes("://") ? raw : `rtsp://${raw}`);
      return !url.pathname || url.pathname === "/";
    } catch {
      return true;
    }
  }

  function hostOf(raw) {
    try {
      return new URL(raw.includes("://") ? raw : `rtsp://${raw}`).hostname;
    } catch {
      return raw;
    }
  }

  async function addCamera() {
    const nameField = document.getElementById("wizard-camera-name");
    const urlField = document.getElementById("wizard-camera-url");
    const username = document.getElementById("wizard-camera-user").value.trim() || null;
    const password = document.getElementById("wizard-camera-pass").value || null;
    const input = urlField.value.trim();
    if (!input) {
      setCameraResult("pick a camera above, or enter its IP address or stream URL", "bad");
      return;
    }

    let streamUrl = input;
    let ptz = { ptzEnabled: false, onvifPtzServiceUrl: null, onvifProfileToken: null };

    // An address alone isn't something the camera list can play - turn it into the camera's real
    // stream URL first, with the same probe the CAMERAS tab's Test Connection uses.
    if (looksLikeABareAddress(input)) {
      setCameraResult("looking for the camera's stream...", null);
      try {
        const response = await fetch("/api/cameras/discovery/probe", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ address: hostOf(input), username, password })
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          setCameraResult((payload.errors || ["could not check that address"]).join(" "), "bad");
          return;
        }
        if (payload.status !== "Found") {
          setCameraResult(payload.message || "no camera stream found at that address", "bad");
          return;
        }
        streamUrl = payload.streamUrl;
        urlField.value = streamUrl;
        if (!nameField.value.trim() && payload.suggestedName) nameField.value = payload.suggestedName;
        if (payload.supportsPtz) {
          ptz = {
            ptzEnabled: true,
            onvifPtzServiceUrl: payload.onvifPtzServiceUrl || null,
            onvifProfileToken: payload.onvifProfileToken || null
          };
        }
      } catch {
        setCameraResult("network error while looking for the camera", "bad");
        return;
      }
    }

    const name = nameField.value.trim();
    if (!name) {
      setCameraResult("give the camera a name", "bad");
      nameField.focus();
      return;
    }

    setCameraResult("adding...", null);
    try {
      const response = await fetch("/api/cameras", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name, streamUrl, username, password, includeAudio: false, ...ptz })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        setCameraResult((payload.errors || ["could not add camera"]).join(" "), "bad");
        return;
      }
      // Cleared so the next camera can go straight in - adding several is the normal case.
      for (const id of ["wizard-camera-name", "wizard-camera-url", "wizard-camera-user", "wizard-camera-pass"]) {
        document.getElementById(id).value = "";
      }
      setCameraResult(`added "${name}" - add another, or press next`, "ok");
    } catch {
      setCameraResult("network error", "bad");
    }
  }

  async function saveVisionFields() {
    const backend = document.getElementById("wizard-vision-backend").value;
    const routingOk = await put("/api/settings/vision-routing", {
      primary: backend === "cloud" ? "cloud" : "ollama",
      fallbackEnabled: false
    });
    if (backend !== "cloud") return routingOk;

    const cloudOk = await put("/api/settings/cloud-vision", {
      enabled: true,
      provider: document.getElementById("wizard-cloud-provider").value,
      apiKey: document.getElementById("wizard-cloud-key").value.trim(),
      model: document.getElementById("wizard-cloud-model").value.trim() || "gpt-4o-mini",
      maximumRetries: 2,
      retryDelaySeconds: 3
    });
    if (!cloudOk) showErrors(["Cloud Vision settings could not be saved - check the API key and model."]);
    return routingOk && cloudOk;
  }

  async function put(endpoint, body) {
    try {
      const response = await fetch(endpoint, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      return response.ok;
    } catch {
      return false;
    }
  }

  function showErrors(messages) {
    errorsEl.innerHTML = "";
    for (const message of messages) {
      const li = document.createElement("li");
      li.textContent = message;
      errorsEl.appendChild(li);
    }
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

  let toastTimeout;
  function showToast(message, success) {
    const toast = document.getElementById("toast");
    clearTimeout(toastTimeout);
    toast.textContent = message;
    toast.className = "toast " + (success ? "success" : "error");
    toastTimeout = setTimeout(() => { toast.className = "toast hidden"; }, 3000);
  }

  let renderedStep = null;

  async function renderStep() {
    errorsEl.innerHTML = "";
    renderedStep?.leave?.();
    const step = STEPS[stepIndex];
    renderedStep = step;
    titleEl.textContent = step.title;
    stepCountEl.textContent = `${stepIndex + 1} / ${STEPS.length}`;
    backBtn.classList.toggle("hidden", stepIndex === 0);
    skipBtn.classList.toggle("hidden", step.skippable !== true);
    nextBtn.textContent = stepIndex === STEPS.length - 1 ? "finish" : "next";
    await step.render();
  }

  async function goNext() {
    const step = STEPS[stepIndex];
    if (step.onNext) {
      nextBtn.disabled = true;
      const ok = await step.onNext();
      nextBtn.disabled = false;
      if (!ok) return;
    }
    await advance();
  }

  async function advance() {
    if (stepIndex === STEPS.length - 1) {
      await fetch("/api/setup/complete", { method: "POST" });
      overlay.classList.add("hidden");
      window.location.reload();
      return;
    }
    stepIndex++;
    await renderStep();
  }

  async function skip() {
    stepIndex++;
    await renderStep();
  }

  function back() {
    if (stepIndex === 0) return;
    stepIndex--;
    renderStep();
  }

  backBtn.addEventListener("click", back);
  skipBtn.addEventListener("click", skip);
  nextBtn.addEventListener("click", goNext);

  async function checkAndMaybeShow() {
    try {
      const response = await fetch("/api/setup/status");
      const payload = await response.json();
      if (payload.completed) return;
    } catch {
      return;
    }

    overlay.classList.remove("hidden");
    stepIndex = 0;
    await renderStep();
  }

  return { checkAndMaybeShow };
})();
