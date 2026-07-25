window.GateWatchSettings = (function () {
  const SECTIONS = [
    {
      key: "bridge", title: "BRIDGE", endpoint: "/api/settings/bridge",
      fields: [
        { name: "apiKey", label: "API Key", type: "password" },
        { name: "maximumImageBytes", label: "Max Image Bytes", type: "number", min: 1024, max: 104857600 },
        { name: "retentionDays", label: "Retention Days", type: "number", min: 1, max: 3650 },
        { name: "saveImages", label: "Save Images", type: "checkbox" },
        { name: "allowedImageContentTypes", label: "Allowed Content Types (comma separated)", type: "csv" }
      ]
    },
    {
      key: "ollama", title: "OLLAMA", endpoint: "/api/settings/ollama",
      fields: [
        { name: "model", label: "Model", type: "text" },
        { name: "keepAlive", label: "Keep Alive", type: "text" },
        { name: "temperature", label: "Temperature", type: "number", min: 0, max: 2, step: 0.1 },
        { name: "maximumOutputTokens", label: "Max Output Tokens", type: "number", min: 32, max: 4096 },
        { name: "topK", label: "Top K", type: "number", min: 1, max: 100 },
        { name: "topP", label: "Top P", type: "number", min: 0, max: 1, step: 0.05 },
        { name: "maximumRetries", label: "Max Retries", type: "number", min: 0, max: 10 },
        { name: "retryDelaySeconds", label: "Retry Delay (s)", type: "number", min: 1, max: 300 }
      ]
    },
    {
      key: "imageProcessing", title: "IMAGE PROCESSING", endpoint: "/api/settings/image-processing",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox" },
        { name: "maxWidth", label: "Max Width", type: "number", min: 256, max: 4096 },
        { name: "maxHeight", label: "Max Height", type: "number", min: 256, max: 4096 },
        { name: "jpegQuality", label: "JPEG Quality", type: "number", min: 1, max: 100 },
        { name: "autoOrient", label: "Auto Orient", type: "checkbox" }
      ]
    },
    {
      key: "email", title: "EMAIL", endpoint: "/api/settings/email",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox" },
        { name: "smtpHost", label: "SMTP Host", type: "text" },
        { name: "smtpPort", label: "SMTP Port", type: "number", min: 1, max: 65535 },
        { name: "enableSsl", label: "Enable SSL", type: "checkbox" },
        { name: "username", label: "Username", type: "text" },
        { name: "appPassword", label: "App Password", type: "password" },
        { name: "fromAddress", label: "From Address", type: "text" },
        { name: "fromName", label: "From Name", type: "text" },
        { name: "toAddress", label: "To Address", type: "text" },
        { name: "duplicateCooldownMinutes", label: "Duplicate Cooldown (min)", type: "number", min: 0, max: 1440 },
        { name: "minimumConfidence", label: "Minimum Confidence", type: "number", min: 0, max: 1, step: 0.05 },
        { name: "attachImage", label: "Attach Image", type: "checkbox" }
      ]
    },
    {
      key: "dailySummary", title: "DAILY SUMMARY", endpoint: "/api/settings/daily-summary",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox" },
        { name: "morningHour", label: "Morning Hour (0-23)", type: "number", min: 0, max: 23 },
        { name: "eveningHour", label: "Evening Hour (0-23)", type: "number", min: 0, max: 23 },
        { name: "maxImagesAttached", label: "Max Images Attached", type: "number", min: 0, max: 50 }
      ]
    },
    {
      key: "telegram", title: "TELEGRAM ALERTS", endpoint: "/api/settings/telegram",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox" },
        { name: "botToken", label: "Bot Token", type: "password" },
        { name: "chatId", label: "Chat ID", type: "text" },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox" },
        { name: "minimumConfidence", label: "Minimum Confidence", type: "number", min: 0, max: 1, step: 0.05 },
        { name: "attachImage", label: "Attach Image", type: "checkbox" }
      ]
    },
    {
      key: "telegramChat", title: "TELEGRAM CHAT", endpoint: "/api/settings/telegram-chat",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox" },
        { name: "lookbackDays", label: "Lookback Days", type: "number", min: 1, max: 30 },
        { name: "maxEventsInContext", label: "Max Events In Context", type: "number", min: 1, max: 500 },
        { name: "model", label: "Model", type: "text" }
      ]
    },
    {
      key: "blueIris", title: "BLUE IRIS SNAPSHOT", endpoint: "/api/settings/blue-iris",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox" },
        { name: "camera", label: "Camera Short Name", type: "text" },
        { name: "username", label: "Username", type: "text" },
        { name: "password", label: "Password", type: "password" }
      ]
    },
    {
      key: "selfMonitoring", title: "SELF-MONITORING", endpoint: "/api/settings/self-monitoring",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox" },
        { name: "checkIntervalSeconds", label: "Check Interval (s)", type: "number", min: 10, max: 3600 },
        { name: "consecutiveFailuresBeforeAlert", label: "Consecutive Failures Before Alert", type: "number", min: 1, max: 20 },
        { name: "queueBacklogThreshold", label: "Queue Backlog Threshold", type: "number", min: 1, max: 10000 },
        { name: "consecutiveBacklogChecksBeforeAlert", label: "Consecutive Backlog Checks Before Alert", type: "number", min: 1, max: 20 },
        { name: "notifyOnRecovery", label: "Notify On Recovery", type: "checkbox" }
      ]
    },
    {
      key: "webhook", title: "WEBHOOK", endpoint: "/api/settings/webhook",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox" },
        { name: "url", label: "URL", type: "text" },
        { name: "apiKeyHeaderName", label: "API Key Header Name", type: "text" },
        { name: "apiKey", label: "API Key", type: "password" },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox" }
      ]
    }
  ];

  const WIDE_TYPES = new Set(["checkbox", "password", "csv"]);

  function buildField(field, value) {
    const row = document.createElement("div");
    row.className = "field-row" + (WIDE_TYPES.has(field.type) ? " field-row-wide" : "");

    if (field.type === "checkbox") {
      row.classList.add("field-checkbox-row");
      const label = document.createElement("label");
      label.className = "switch";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.checked = !!value;
      input.dataset.field = field.name;
      const track = document.createElement("span");
      track.className = "switch-track";
      label.append(input, track);
      const text = document.createElement("span");
      text.className = "field-label";
      text.textContent = field.label;
      row.append(label, text);
      return row;
    }

    const label = document.createElement("label");
    label.className = "field-label";
    label.textContent = field.label;
    row.appendChild(label);

    if (field.type === "password") {
      const wrap = document.createElement("div");
      wrap.className = "field-password-row";
      const input = document.createElement("input");
      input.type = "password";
      input.className = "field-input";
      input.value = value ?? "";
      input.dataset.field = field.name;
      input.autocomplete = "off";
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "field-toggle-btn";
      toggle.textContent = "show";
      toggle.addEventListener("click", () => {
        input.type = input.type === "password" ? "text" : "password";
        toggle.textContent = input.type === "password" ? "show" : "hide";
      });
      wrap.append(input, toggle);
      row.appendChild(wrap);
      return row;
    }

    if (field.type === "csv") {
      const input = document.createElement("input");
      input.type = "text";
      input.className = "field-input";
      input.value = Array.isArray(value) ? value.join(", ") : "";
      input.dataset.field = field.name;
      row.appendChild(input);
      return row;
    }

    const input = document.createElement("input");
    input.type = field.type === "number" ? "number" : "text";
    input.className = "field-input";
    input.value = value ?? "";
    input.dataset.field = field.name;
    if (field.type === "number") {
      if (field.min !== undefined) input.min = field.min;
      if (field.max !== undefined) input.max = field.max;
      input.step = field.step !== undefined ? field.step : "any";
    }
    row.appendChild(input);
    return row;
  }

  function renderSection(schema, data) {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">${schema.title}</span><span class="settings-section-caret">&#9656;</span>`;
    header.addEventListener("click", () => section.classList.toggle("open"));

    const body = document.createElement("div");
    body.className = "settings-section-body";

    const grid = document.createElement("div");
    grid.className = "field-grid";
    for (const field of schema.fields) {
      grid.appendChild(buildField(field, data ? data[field.name] : undefined));
    }
    body.appendChild(grid);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, errors);
    body.appendChild(footer);

    saveBtn.addEventListener("click", () => saveSection(schema, section, errors, saveBtn));

    section.append(header, body);
    return section;
  }

  async function saveSection(schema, section, errorsEl, saveBtn) {
    const data = {};
    for (const field of schema.fields) {
      const input = section.querySelector(`[data-field="${field.name}"]`);
      if (!input) continue;
      if (field.type === "checkbox") data[field.name] = input.checked;
      else if (field.type === "number") data[field.name] = input.value === "" ? 0 : Number(input.value);
      else if (field.type === "csv") data[field.name] = input.value.split(",").map(s => s.trim()).filter(s => s.length > 0);
      else data[field.name] = input.value;
    }

    await submit(schema.endpoint, data, schema.title, errorsEl, saveBtn);
  }

  function buildQuestionRow(value) {
    const row = document.createElement("div");
    row.className = "question-row";
    const input = document.createElement("input");
    input.type = "text";
    input.className = "field-input";
    input.value = value;
    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "icon-btn";
    removeBtn.textContent = "✕";
    removeBtn.title = "Remove question";
    removeBtn.addEventListener("click", () => row.remove());
    row.append(input, removeBtn);
    return row;
  }

  function buildProfileCard(name, profile) {
    profile = profile || { systemPrompt: "", questions: [], crop: null };
    const card = document.createElement("div");
    card.className = "profile-card";

    const header = document.createElement("div");
    header.className = "profile-card-header";
    const nameInput = document.createElement("input");
    nameInput.type = "text";
    nameInput.className = "field-input profile-name-input";
    nameInput.value = name;
    nameInput.placeholder = "profile name";
    const deleteBtn = document.createElement("button");
    deleteBtn.type = "button";
    deleteBtn.className = "icon-btn";
    deleteBtn.textContent = "✕";
    deleteBtn.title = "Delete profile";
    deleteBtn.addEventListener("click", () => card.remove());
    header.append(nameInput, deleteBtn);

    const promptRow = document.createElement("div");
    promptRow.className = "field-row";
    const promptLabel = document.createElement("label");
    promptLabel.className = "field-label";
    promptLabel.textContent = "System Prompt";
    const promptArea = document.createElement("textarea");
    promptArea.className = "field-textarea";
    promptArea.rows = 4;
    promptArea.value = profile.systemPrompt || "";
    promptRow.append(promptLabel, promptArea);

    const questionsWrap = document.createElement("div");
    questionsWrap.className = "field-row";
    const qLabel = document.createElement("label");
    qLabel.className = "field-label";
    qLabel.textContent = "Questions";
    const qList = document.createElement("div");
    qList.className = "questions-list";
    (profile.questions || []).forEach(q => qList.appendChild(buildQuestionRow(q)));
    const addQBtn = document.createElement("button");
    addQBtn.type = "button";
    addQBtn.className = "icon-btn add";
    addQBtn.textContent = "+";
    addQBtn.title = "Add question";
    addQBtn.addEventListener("click", () => qList.appendChild(buildQuestionRow("")));
    questionsWrap.append(qLabel, qList, addQBtn);

    const cropWrap = document.createElement("div");
    cropWrap.className = "field-row";
    const cropToggleRow = document.createElement("div");
    cropToggleRow.className = "field-checkbox-row";
    const cropSwitch = document.createElement("label");
    cropSwitch.className = "switch";
    const cropInput = document.createElement("input");
    cropInput.type = "checkbox";
    cropInput.checked = !!profile.crop;
    const cropTrack = document.createElement("span");
    cropTrack.className = "switch-track";
    cropSwitch.append(cropInput, cropTrack);
    const cropText = document.createElement("span");
    cropText.className = "field-label";
    cropText.textContent = "Use crop region";
    cropToggleRow.append(cropSwitch, cropText);

    const cropGrid = document.createElement("div");
    cropGrid.className = "crop-grid";
    cropGrid.style.display = profile.crop ? "grid" : "none";
    const crop = profile.crop || { left: 0, top: 0, right: 1, bottom: 1 };
    for (const key of ["left", "top", "right", "bottom"]) {
      const r = document.createElement("div");
      r.className = "field-row";
      const l = document.createElement("label");
      l.className = "field-label";
      l.textContent = key;
      const i = document.createElement("input");
      i.type = "number";
      i.className = "field-input";
      i.dataset.cropField = key;
      i.min = 0;
      i.max = 1;
      i.step = 0.01;
      i.value = crop[key] ?? (key === "right" || key === "bottom" ? 1 : 0);
      r.append(l, i);
      cropGrid.appendChild(r);
    }
    cropInput.addEventListener("change", () => {
      cropGrid.style.display = cropInput.checked ? "grid" : "none";
    });
    cropWrap.append(cropToggleRow, cropGrid);

    card.append(header, promptRow, questionsWrap, cropWrap);
    card._fields = { nameInput, promptArea, qList, cropInput, cropGrid };
    return card;
  }

  function collectProfile(card) {
    const f = card._fields;
    const name = f.nameInput.value.trim();
    const systemPrompt = f.promptArea.value.trim();
    const questions = Array.from(f.qList.querySelectorAll(".question-row input"))
      .map(i => i.value.trim())
      .filter(v => v.length > 0);

    let crop = null;
    if (f.cropInput.checked) {
      crop = {};
      for (const input of f.cropGrid.querySelectorAll("[data-crop-field]")) {
        crop[input.dataset.cropField] = parseFloat(input.value);
      }
    }

    return { name, profile: { systemPrompt, questions, crop } };
  }

  function renderPromptProfilesSection(profiles) {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">PROMPT PROFILES</span><span class="settings-section-caret">&#9656;</span>`;
    header.addEventListener("click", () => section.classList.toggle("open"));

    const body = document.createElement("div");
    body.className = "settings-section-body";

    const hint = document.createElement("div");
    hint.className = "settings-hint";
    hint.textContent = "The AI prompts sent to Ollama per camera profile. A 'default' profile is required as a fallback for unrecognized profile names.";
    body.appendChild(hint);

    const cardsWrap = document.createElement("div");
    cardsWrap.className = "settings-root";
    for (const [name, profile] of Object.entries(profiles || {})) {
      cardsWrap.appendChild(buildProfileCard(name, profile));
    }

    const toolbar = document.createElement("div");
    toolbar.className = "profiles-toolbar";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add profile";
    addBtn.addEventListener("click", () => cardsWrap.appendChild(buildProfileCard("", null)));
    toolbar.appendChild(addBtn);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, errors);

    saveBtn.addEventListener("click", async () => {
      const dict = {};
      for (const card of cardsWrap.querySelectorAll(".profile-card")) {
        const { name, profile } = collectProfile(card);
        if (name) dict[name] = profile;
      }
      await submit("/api/settings/prompt-profiles", dict, "Prompt profiles", errors, saveBtn);
    });

    body.append(cardsWrap, toolbar, footer);
    section.append(header, body);
    return section;
  }

  async function submit(endpoint, body, label, errorsEl, saveBtn) {
    errorsEl.innerHTML = "";
    saveBtn.disabled = true;
    const originalText = saveBtn.textContent;
    saveBtn.textContent = "saving...";
    try {
      const response = await fetch(endpoint, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        showFieldErrors(errorsEl, payload.errors || ["Save failed."]);
        showToast(`${label}: not saved`, false);
      } else {
        showToast(`${label} saved`, true);
      }
    } catch {
      showFieldErrors(errorsEl, ["Network error while saving."]);
      showToast("Save failed", false);
    } finally {
      saveBtn.disabled = false;
      saveBtn.textContent = originalText;
    }
  }

  function showFieldErrors(el, errors) {
    el.innerHTML = "";
    for (const message of errors) {
      const li = document.createElement("li");
      li.textContent = message;
      el.appendChild(li);
    }
  }

  let toastTimeout;
  function showToast(message, success) {
    const toast = document.getElementById("toast");
    clearTimeout(toastTimeout);
    toast.textContent = message;
    toast.className = "toast " + (success ? "success" : "error");
    toastTimeout = setTimeout(() => { toast.className = "toast hidden"; }, 3000);
  }

  async function load() {
    const root = document.getElementById("settings-root");
    root.innerHTML = `<div class="empty-state">loading settings&hellip;</div>`;
    try {
      const response = await fetch("/api/settings");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const data = await response.json();

      root.innerHTML = "";
      for (const schema of SECTIONS) {
        root.appendChild(renderSection(schema, data[schema.key]));
      }
      root.appendChild(renderPromptProfilesSection(data.promptProfiles));

      const first = root.querySelector(".settings-section");
      if (first) first.classList.add("open");
    } catch (err) {
      root.innerHTML = `<div class="empty-state">failed to load settings: ${err.message}</div>`;
    }
  }

  return { load };
})();
