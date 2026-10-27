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
      // A new install starts on the fast detector (VisionRouting:Primary "fastlocal") and has no
      // Ollama model downloaded, so skipping this step leaves alerts on quick detection. Picking
      // Ollama here is what downloads its model - the main app's OllamaModelProvisioner starts the
      // pull as soon as the routing change lands, and alerts stay on the detector until it's done.
      id: "vision",
      title: "VISION BACKEND",
      skippable: true,
      render() {
        bodyEl.innerHTML = `
          <p>Which AI looks at each snapshot. The full AI tells you what is actually happening -
          "delivery driver leaving a package at the gate" instead of just "person" - and is what
          makes alerts worth reading.</p>
          <label class="wizard-choice">
            <input type="radio" name="wizard-vision-backend" value="ollama" checked />
            <span><strong>Full AI on this machine (Ollama) - recommended</strong><br />
            Free and private. Downloads a 4.3 GB model in the background; alerts use quick
            detection until it finishes. Slow on a machine without a graphics card.</span>
          </label>
          <label class="wizard-choice">
            <input type="radio" name="wizard-vision-backend" value="cloud" />
            <span><strong>Full AI in the cloud</strong><br />
            The same detailed descriptions on any hardware. Costs a little per alert and needs an
            API key.</span>
          </label>
          <label class="wizard-choice">
            <input type="radio" name="wizard-vision-backend" value="fastlocal" />
            <span><strong>Quick detection only</strong><br />
            Just "person", "vehicle" or "animal" - fast on anything, but no descriptions, and
            gate/door open checks won't work. This is what skipping gives you; you can switch to
            the full AI any time in SETTINGS.</span>
          </label>
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
            <div class="wizard-actions">
              <button type="button" class="btn" id="wizard-test-connection-btn">test connection</button>
              <span id="wizard-connection-result"></span>
            </div>
          </div>`;

        const cloudFields = document.getElementById("wizard-cloud-fields");
        for (const radio of bodyEl.querySelectorAll('input[name="wizard-vision-backend"]')) {
          radio.addEventListener("change", () => {
            cloudFields.classList.toggle("hidden", selectedBackend() !== "cloud");
          });
        }

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
        if (!(await saveVisionFields())) return false;
        if (selectedBackend() !== "ollama") return true;

        // Ollama itself has to be there to download into. The installers set it up unless it was
        // skipped; if it's missing, go back to quick detection rather than leave alerts pointed
        // at a server that isn't running.
        const status = await fullAiStatus();
        if (status?.state === "ollama-unreachable") {
          await put("/api/settings/vision-routing", { primary: "fastlocal", fallbackEnabled: false });
          showErrors([
            "Ollama isn't running on this machine, so the full AI can't be downloaded yet. Install it " +
            "from ollama.com (or re-run the installer and say yes to Ollama), then pick it in SETTINGS -> " +
            "VISION ROUTING. For now you can choose the cloud option or quick detection."
          ]);
          return false;
        }
        return true;
      }
    },
    {
      id: "camera",
      title: "ADD A CAMERA",
      skippable: true,
      render() {
        bodyEl.innerHTML = `
          <p>Add at least one camera by its stream URL (most IP cameras expose an
          <code>rtsp://</code> address). You can add more, or edit this later, from the
          CAMERAS tab.</p>
          <div class="field-row field-row-wide">
            <label class="field-label">Name</label>
            <input type="text" id="wizard-camera-name" class="field-input" placeholder="e.g. front gate" />
          </div>
          <div class="field-row field-row-wide">
            <label class="field-label">Stream URL</label>
            <input type="text" id="wizard-camera-url" class="field-input" placeholder="rtsp://192.168.1.50:554/stream" />
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

        document.getElementById("wizard-add-camera-btn").addEventListener("click", async () => {
          const resultEl = document.getElementById("wizard-camera-result");
          const name = document.getElementById("wizard-camera-name").value.trim();
          const streamUrl = document.getElementById("wizard-camera-url").value.trim();
          if (!name || !streamUrl) {
            resultEl.textContent = "name and stream URL are required";
            resultEl.className = "wizard-status-bad";
            return;
          }
          resultEl.textContent = "adding...";
          try {
            const response = await fetch("/api/cameras", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({
                name,
                streamUrl,
                username: document.getElementById("wizard-camera-user").value.trim() || null,
                password: document.getElementById("wizard-camera-pass").value || null,
                includeAudio: false
              })
            });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok) {
              resultEl.textContent = (payload.errors || ["could not add camera"]).join(" ");
              resultEl.className = "wizard-status-bad";
              return;
            }
            resultEl.textContent = "camera added";
            resultEl.className = "wizard-status-ok";
          } catch {
            resultEl.textContent = "network error";
            resultEl.className = "wizard-status-bad";
          }
        });
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
          trigger zones, quiet hours, MQTT, and more - is in SETTINGS whenever you want it.</p>
          <div id="wizard-full-ai" class="hidden">
            <p id="wizard-full-ai-note"></p>
            <div id="wizard-full-ai-bar" class="wizard-progress hidden"><div class="wizard-progress-fill"></div></div>
          </div>`;
        showFullAiProgress(false);
      }
    }
  ];

  // FINISH deliberately doesn't wait for the Ollama model: a 4.3 GB pull can take an hour on a slow
  // line, it carries on server-side whatever this page does, and alerts are on quick detection
  // meanwhile. So this shows it live for anyone who wants to watch, and says plainly that leaving
  // is fine. Polls only while a download is running and only while FINISH is on screen.
  let fullAiPoll = null;

  async function showFullAiProgress(wasDownloading) {
    clearTimeout(fullAiPoll);
    const status = await fullAiStatus();
    const box = document.getElementById("wizard-full-ai");
    if (!box || !status) return;
    const note = document.getElementById("wizard-full-ai-note");
    const bar = document.getElementById("wizard-full-ai-bar");
    note.className = "wizard-hint";
    bar.classList.add("hidden");

    if (status.state === "downloading") {
      note.textContent = `Downloading the full AI (${status.model}) - ${status.percent}%. You can finish and ` +
        "close this - it keeps downloading. Alerts use quick detection until it's done, then switch over " +
        "automatically.";
      bar.firstElementChild.style.width = `${status.percent}%`;
      bar.classList.remove("hidden");
      fullAiPoll = setTimeout(() => showFullAiProgress(true), 3000);
    } else if (status.state === "full" && status.primary === "ollama") {
      // Either it finished while this was on screen, or it was already pulled - by an older
      // installer or a previous run of this wizard. Said out loud either way, or picking Ollama
      // looks like it did nothing.
      note.textContent = wasDownloading
        ? `Full AI ready (${status.model}) - alerts use it from now on.`
        : `The full AI (${status.model}) was already downloaded on this machine, so alerts use it from now on.`;
      note.className = "wizard-status-ok";
    } else if (status.state === "download-failed") {
      note.textContent = `The full AI didn't download (${status.error}). It retries every 10 minutes on its ` +
        "own, and alerts use quick detection meanwhile - the banner at the top shows how it's going.";
      note.className = "wizard-status-bad";
    } else if (status.state === "quick") {
      note.textContent = "Alerts are on quick detection. For full descriptions, use the banner at the top " +
        "of the dashboard to switch to the full AI whenever you're ready.";
    } else {
      return;
    }
    box.classList.remove("hidden");
  }

  function selectedBackend() {
    return bodyEl.querySelector('input[name="wizard-vision-backend"]:checked')?.value ?? "ollama";
  }

  async function fullAiStatus() {
    try {
      const response = await fetch("/api/system/full-ai");
      return response.ok ? await response.json() : null;
    } catch {
      return null;
    }
  }

  async function saveVisionFields() {
    const backend = selectedBackend();
    const routingOk = await put("/api/settings/vision-routing", {
      primary: backend,
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

  async function renderStep() {
    clearTimeout(fullAiPoll);
    errorsEl.innerHTML = "";
    const step = STEPS[stepIndex];
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
      clearTimeout(fullAiPoll);
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
