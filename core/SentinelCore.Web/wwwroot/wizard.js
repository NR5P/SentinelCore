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
      id: "login",
      title: "DASHBOARD LOGIN",
      skippable: true,
      async render() {
        bodyEl.innerHTML = `<p>Checking&hellip;</p>`;
        try {
          const response = await fetch("/api/auth/status");
          const payload = await response.json();
          if (payload.enabled) {
            bodyEl.innerHTML = `<p>A login is already required for this dashboard. Change the
              username/password or turn it off anytime in SETTINGS.</p>`;
            return;
          }
        } catch {
          // Fall through to the normal form - if the status check itself failed, offering to
          // set one up (rather than silently skipping) is the safer default.
        }

        bodyEl.innerHTML = `
          <p>Require a username and password to open this dashboard? Worth it if it might ever
          be reachable outside your own network - skip this if it's staying LAN-only.</p>
          <div class="field-row field-row-wide">
            <label class="field-label">Username</label>
            <input type="text" id="wizard-auth-username" class="field-input" autocomplete="username" />
          </div>
          <div class="field-row field-row-wide">
            <label class="field-label">Password</label>
            <input type="password" id="wizard-auth-password" class="field-input" autocomplete="new-password" />
          </div>
          <div class="field-row field-row-wide">
            <label class="field-label">Confirm Password</label>
            <input type="password" id="wizard-auth-confirm" class="field-input" autocomplete="new-password" />
          </div>`;
      },
      async onNext() {
        const usernameInput = document.getElementById("wizard-auth-username");
        // Either already configured (nothing rendered to submit) or the status check failed
        // and this is just the informational branch - either way, nothing to do.
        if (!usernameInput) return true;

        const username = usernameInput.value.trim();
        const password = document.getElementById("wizard-auth-password").value;
        const confirm = document.getElementById("wizard-auth-confirm").value;

        // Nothing entered - same as skip, login stays off.
        if (!username && !password) return true;

        if (password !== confirm) {
          showErrors(["Passwords do not match."]);
          return false;
        }

        try {
          const response = await fetch("/api/auth/enable", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password })
          });
          const payload = await response.json().catch(() => ({}));
          if (!response.ok) {
            showErrors(payload.errors || ["Could not enable login."]);
            return false;
          }
          return true;
        } catch {
          showErrors(["Network error while enabling login."]);
          return false;
        }
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

  async function renderStep() {
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
