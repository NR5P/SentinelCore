window.GateWatchSettings = (function () {
  const SECTIONS = [
    {
      key: "bridge", title: "BRIDGE", endpoint: "/api/settings/bridge",
      fields: [
        { name: "apiKey", label: "API Key", type: "password", tip: "The secret Blue Iris must send with each alert. If this doesn't match what Blue Iris is configured with, alerts are rejected." },
        { name: "maximumImageBytes", label: "Max Image Bytes", type: "number", min: 1024, max: 104857600, tip: "Largest snapshot accepted from Blue Iris. Anything bigger is refused instead of eating memory." },
        { name: "retentionDays", label: "Retention Days", type: "number", min: 1, max: 3650, tip: "How long events and their images are kept before being cleaned up automatically." },
        { name: "saveImages", label: "Save Images", type: "checkbox", tip: "Keep a copy of each snapshot on disk. Turn off to record only the AI's text analysis." },
        { name: "allowedImageContentTypes", label: "Allowed Content Types (comma separated)", type: "csv", tip: "Image formats accepted from Blue Iris, e.g. image/jpeg. Anything else is rejected." }
      ],
      extraAction: {
        label: "re-run setup wizard",
        tip: "Bring back the first-run setup wizard (vision backend, a camera, notifications) without losing anything already configured.",
        async onClick() {
          await fetch("/api/setup/reset", { method: "POST" });
          window.location.reload();
        }
      }
    },
    {
      key: "ollama", title: "OLLAMA", endpoint: "/api/settings/ollama",
      fields: [
        { name: "model", label: "Model", type: "text", tip: "Which local vision model analyses the snapshots, e.g. gemma3:4b. It must already be pulled in Ollama." },
        { name: "keepAlive", label: "Keep Alive", type: "text", tip: "How long Ollama keeps the model loaded in memory between alerts, e.g. 5m. Longer means faster replies but more RAM held." },
        { name: "temperature", label: "Temperature", type: "number", min: 0, max: 2, step: 0.1, tip: "How much the model is allowed to improvise. Low values keep descriptions consistent and literal, which is what you want here." },
        { name: "maximumOutputTokens", label: "Max Output Tokens", type: "number", min: 32, max: 4096, tip: "Ceiling on the length of the model's reply. Too low and the analysis gets cut off mid-sentence." },
        { name: "topK", label: "Top K", type: "number", min: 1, max: 100, tip: "Limits the model to the K most likely next words. Lower is more predictable." },
        { name: "topP", label: "Top P", type: "number", min: 0, max: 1, step: 0.05, tip: "Another way to limit word choice, by probability mass rather than count. Lower is more predictable." },
        { name: "maximumRetries", label: "Max Retries", type: "number", min: 0, max: 10, tip: "How many times to retry when Ollama fails or returns something unparseable." },
        { name: "retryDelaySeconds", label: "Retry Delay (s)", type: "number", min: 1, max: 300, tip: "How long to wait between those retries." }
      ]
    },
    {
      key: "cloudVision", title: "CLOUD VISION", endpoint: "/api/settings/cloud-vision",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Allow this cloud account to be used for alert analysis, either as the primary backend or as a fallback - see VISION ROUTING below to choose which." },
        { name: "provider", label: "Provider", type: "select", options: [
            { value: "openai", label: "OpenAI (or compatible)" },
            { value: "anthropic", label: "Anthropic" },
            { value: "google", label: "Google" }
          ], tip: "Which cloud AI account the API key and model below belong to. 'OpenAI (or compatible)' also covers any other endpoint that speaks the same request format - Azure OpenAI, OpenRouter, Together.ai, Groq, or a self-hosted vLLM/LM Studio/LocalAI server - by changing CloudVision:BaseUrl in appsettings.json (needs a restart, same as Ollama's own address)." },
        { name: "apiKey", label: "API Key", type: "password", tip: "An API key for the provider selected above, with access to a vision-capable model." },
        { name: "model", label: "Model", type: "text", tip: "Which model analyses the snapshots - must match the provider above, e.g. gpt-4o-mini (OpenAI), claude-3-5-sonnet-latest (Anthropic), or gemini-1.5-flash (Google)." },
        { name: "maximumRetries", label: "Max Retries", type: "number", min: 0, max: 10, tip: "How many times to retry when the provider fails or returns something unparseable." },
        { name: "retryDelaySeconds", label: "Retry Delay (s)", type: "number", min: 1, max: 300, tip: "How long to wait between those retries." }
      ]
    },
    {
      key: "visionRouting", title: "VISION ROUTING", endpoint: "/api/settings/vision-routing",
      fields: [
        { name: "primary", label: "Primary Backend", type: "select", options: [
            { value: "ollama", label: "Ollama (local)" },
            { value: "cloud", label: "Cloud Vision (OpenAI)" }
          ], tip: "Which backend analyses alerts first. If it's Cloud Vision but CLOUD VISION above isn't enabled/configured, Ollama is used instead automatically." },
        { name: "fallbackEnabled", label: "Fall Back To The Other Backend", type: "checkbox", tip: "If the primary backend fails after its own retries, automatically retry once against the other one before giving up - slower, but the alert still gets analyzed instead of being lost. Requires both backends to be usable (Ollama running, Cloud Vision enabled and configured)." }
      ]
    },
    {
      key: "imageProcessing", title: "IMAGE PROCESSING", endpoint: "/api/settings/image-processing",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Shrink snapshots before sending them to the AI. Usually faster with no real loss in accuracy." },
        { name: "maxWidth", label: "Max Width", type: "number", min: 256, max: 4096, tip: "Images wider than this are scaled down before analysis." },
        { name: "maxHeight", label: "Max Height", type: "number", min: 256, max: 4096, tip: "Images taller than this are scaled down before analysis." },
        { name: "jpegQuality", label: "JPEG Quality", type: "number", min: 1, max: 100, tip: "Quality of the resized image. Lower means smaller and faster but blurrier for the model to read." },
        { name: "autoOrient", label: "Auto Orient", type: "checkbox", tip: "Rotate images upright based on their orientation tag, so the model isn't looking at a sideways scene." }
      ]
    },
    {
      key: "email", title: "EMAIL", endpoint: "/api/settings/email",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Send email when an alert is worth telling you about." },
        { name: "smtpHost", label: "SMTP Host", type: "text", tip: "Outgoing mail server, e.g. smtp.gmail.com." },
        { name: "smtpPort", label: "SMTP Port", type: "number", min: 1, max: 65535, tip: "Port for the mail server. Usually 587 with SSL on." },
        { name: "enableSsl", label: "Enable SSL", type: "checkbox", tip: "Encrypt the connection to the mail server. Leave on unless your server can't do it." },
        { name: "username", label: "Username", type: "text", tip: "Account used to log in to the mail server, usually the full email address." },
        { name: "appPassword", label: "App Password", type: "password", tip: "For Gmail this is an app password, not your normal one. Regular passwords are refused." },
        { name: "fromAddress", label: "From Address", type: "text", tip: "Address the alert appears to come from." },
        { name: "fromName", label: "From Name", type: "text", tip: "Display name shown as the sender, e.g. Sentinel Core." },
        { name: "toAddress", label: "To Address", type: "text", tip: "Where alerts are sent." },
        { name: "duplicateCooldownMinutes", label: "Duplicate Cooldown (min)", type: "number", min: 0, max: 1440, tip: "Suppress repeat emails about the same kind of event for this long, so one visitor doesn't fill your inbox." },
        { name: "minimumConfidence", label: "Minimum Confidence", type: "number", min: 0, max: 1, step: 0.05, tip: "How sure the AI must be before emailing. Raise it if you get false alarms, lower it if real events slip through." },
        { name: "attachImage", label: "Attach Image", type: "checkbox", tip: "Include the snapshot in the email rather than text only." }
      ]
    },
    {
      key: "dailySummary", title: "DAILY SUMMARY", endpoint: "/api/settings/daily-summary",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Email a digest of everything that happened, instead of only alerting on notable events." },
        { name: "morningHour", label: "Morning Hour (0-23)", type: "number", min: 0, max: 23, tip: "Hour of day the morning digest is sent, in 24-hour time. 7 means 7am." },
        { name: "eveningHour", label: "Evening Hour (0-23)", type: "number", min: 0, max: 23, tip: "Hour of day the evening digest is sent, in 24-hour time. 19 means 7pm." },
        { name: "maxImagesAttached", label: "Max Images Attached", type: "number", min: 0, max: 50, tip: "Cap on how many snapshots ride along in the digest, to keep the email a sane size." }
      ]
    },
    {
      key: "telegram", title: "TELEGRAM ALERTS", endpoint: "/api/settings/telegram",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Push alerts to your phone through a Telegram bot." },
        { name: "botToken", label: "Bot Token", type: "password", tip: "The token BotFather gave you when you created the bot." },
        { name: "chatId", label: "Chat ID", type: "text", tip: "Which Telegram conversation to send to. This is the only chat the bot will talk to." },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox", tip: "Only message when the AI judges the event worth your attention, rather than for every single one." },
        { name: "minimumConfidence", label: "Minimum Confidence", type: "number", min: 0, max: 1, step: 0.05, tip: "How sure the AI must be before messaging you." },
        { name: "attachImage", label: "Attach Image", type: "checkbox", tip: "Send the snapshot with the message rather than text only." },
        { name: "duplicateCooldownMinutes", label: "Duplicate Cooldown (min)", type: "number", min: 0, max: 1440, tip: "Suppress repeat Telegram messages about the same kind of event for this long, so one vehicle sitting at the gate doesn't message you over and over. 0 disables this." }
      ]
    },
    {
      key: "telegramChat", title: "TELEGRAM CHAT", endpoint: "/api/settings/telegram-chat",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Let you message the bot and ask questions about recent gate activity." },
        { name: "lookbackDays", label: "Lookback Days", type: "number", min: 1, max: 30, tip: "How far back the bot looks when answering a question." },
        { name: "maxEventsInContext", label: "Max Events In Context", type: "number", min: 1, max: 500, tip: "How many events the bot can consider at once. Higher gives fuller answers but is slower." },
        { name: "model", label: "Model", type: "text", tip: "Which Ollama model answers your questions. Can differ from the vision model used for images." }
      ]
    },
    {
      key: "blueIris", title: "BLUE IRIS SNAPSHOT", endpoint: "/api/settings/blue-iris",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Allow the /photo command to pull a fresh snapshot from Blue Iris on demand." },
        { name: "camera", label: "Camera Short Name", type: "text", tip: "The camera's short name in Blue Iris, e.g. gateCamera. Must match exactly." },
        { name: "username", label: "Username", type: "text", tip: "Blue Iris login used to request the snapshot." },
        { name: "password", label: "Password", type: "password", tip: "Password for that Blue Iris account." }
      ]
    },
    {
      key: "selfMonitoring", title: "SELF-MONITORING", endpoint: "/api/settings/self-monitoring",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Watch the bridge's own health and tell you if it stops working properly." },
        { name: "checkIntervalSeconds", label: "Check Interval (s)", type: "number", min: 10, max: 3600, tip: "How often to run the health check." },
        { name: "consecutiveFailuresBeforeAlert", label: "Consecutive Failures Before Alert", type: "number", min: 1, max: 20, tip: "How many checks must fail in a row before you're told, so one hiccup doesn't wake you." },
        { name: "queueBacklogThreshold", label: "Queue Backlog Threshold", type: "number", min: 1, max: 10000, tip: "How many unprocessed alerts count as falling behind. Usually means the AI can't keep up." },
        { name: "consecutiveBacklogChecksBeforeAlert", label: "Consecutive Backlog Checks Before Alert", type: "number", min: 1, max: 20, tip: "How many checks in a row must show a backlog before alerting, ignoring brief bursts." },
        { name: "notifyOnRecovery", label: "Notify On Recovery", type: "checkbox", tip: "Also tell you when things go back to normal, not just when they break." }
      ]
    },
    {
      key: "quietHours", title: "QUIET HOURS", endpoint: "/api/settings/quiet-hours",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "During the window below, raise the confidence bar for Email and Telegram alerts so only confident detections still ping you. Nothing is ever fully muted, and the Feed and daily digest are unaffected." },
        { name: "startHour", label: "Start Hour (0-23)", type: "number", min: 0, max: 23, tip: "Hour the quiet window begins, in 24-hour time. 22 means 10pm." },
        { name: "endHour", label: "End Hour (0-23)", type: "number", min: 0, max: 23, tip: "Hour the quiet window ends, in 24-hour time. 6 means 6am. Can be earlier than Start Hour to span midnight, e.g. 22 to 6." },
        { name: "minimumConfidence", label: "Minimum Confidence During Window", type: "number", min: 0, max: 1, step: 0.05, tip: "How sure the AI must be to still alert during quiet hours. Only used if it's higher than the channel's own Minimum Confidence in Email/Telegram settings - it can raise the bar, never lower it." }
      ]
    },
    {
      key: "webhook", title: "WEBHOOK", endpoint: "/api/settings/webhook",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "POST each analysed event to another system, for home automation or logging." },
        { name: "url", label: "URL", type: "text", tip: "Address the event data is posted to." },
        { name: "apiKeyHeaderName", label: "API Key Header Name", type: "text", tip: "Name of the header the key is sent in, e.g. X-API-Key. Whatever the receiving end expects." },
        { name: "apiKey", label: "API Key", type: "password", tip: "Secret sent in that header so the receiving end knows it's you." },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox", tip: "Only post events the AI flagged as notable, rather than every one." }
      ]
    },
    {
      key: "mqtt", title: "MQTT / HOME ASSISTANT", endpoint: "/api/settings/mqtt",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Publish every analysed event over MQTT, with Home Assistant auto-discovery so cameras show up as devices with no manual setup on the Home Assistant side." },
        { name: "runEmbeddedBroker", label: "Run Built-In Broker", type: "checkbox", tip: "Have this app act as its own MQTT broker on the port below, so you don't need a separate one (like Mosquitto) running anywhere. Leave off to connect to a broker you already have - e.g. Home Assistant's own Mosquitto add-on. Needs a restart to take effect if changed." },
        { name: "embeddedBrokerPort", label: "Built-In Broker Port", type: "number", min: 1, max: 65535, tip: "Port the built-in broker listens on, if enabled above. Point other MQTT clients (including this app's own publisher) at this host on this port." },
        { name: "brokerHost", label: "Broker Host", type: "text", tip: "Address of the MQTT broker to connect to. Ignored if Run Built-In Broker is on - this app connects to its own broker instead." },
        { name: "brokerPort", label: "Broker Port", type: "number", min: 1, max: 65535, tip: "Port of the external broker. 1883 is the standard unencrypted MQTT port." },
        { name: "username", label: "Username", type: "text", tip: "Leave blank if the broker allows anonymous connections (Mosquitto's default is often anonymous on a LAN)." },
        { name: "password", label: "Password", type: "password", tip: "Only needed if the broker requires a username above." },
        { name: "useTls", label: "Use TLS", type: "checkbox", tip: "Encrypt the connection to an external broker. Leave off for a local/LAN broker without TLS configured." },
        { name: "topicPrefix", label: "Topic Prefix", type: "text", tip: "Root of every topic this app publishes, e.g. 'sentinelcore' produces sentinelcore/gateCamera/event/state." },
        { name: "homeAssistantDiscovery", label: "Home Assistant Discovery", type: "checkbox", tip: "Automatically publish Home Assistant's MQTT discovery config so each camera appears as a device with its own entities - no manual YAML or UI entity setup needed." },
        { name: "homeAssistantDiscoveryPrefix", label: "Discovery Prefix", type: "text", tip: "Must match the discovery prefix configured in Home Assistant's own MQTT integration. 'homeassistant' is Home Assistant's default - only change this if you changed it there too." },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox", tip: "Only publish events the AI flagged as notable. Leave off if you want every event in Home Assistant to build your own history or automations from." }
      ]
    },
    {
      key: "bugReports", title: "BUG REPORTS", endpoint: "/api/settings/bug-reports",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Email the developer when something in the bridge or dashboard hits an internal error, using the same account configured under Email above. Goes to a fixed developer address, not one you set - there's nothing else to configure here." }
      ]
    }
  ];

  const WIDE_TYPES = new Set(["checkbox", "password", "csv"]);

  const SECTION_TIPS = {
    bridge: "How alerts arrive from Blue Iris, and how long they're kept.",
    ollama: "The local AI model that looks at each snapshot, and how it's tuned.",
    cloudVision: "An offsite AI model (OpenAI, Anthropic, or Google) that can analyse alerts instead of, or as a backup for, the local Ollama model.",
    visionRouting: "Which vision backend handles alerts first, and whether to fall back to the other one if it fails.",
    imageProcessing: "Resizing snapshots before the AI sees them, to speed up analysis.",
    email: "Email alerts when something notable happens.",
    dailySummary: "A twice-daily digest of everything that happened, separate from individual alerts.",
    telegram: "Push alerts to your phone through a Telegram bot.",
    telegramChat: "Lets you message the bot to ask questions about recent activity.",
    blueIris: "Pulling a fresh snapshot from Blue Iris on demand via the /photo command.",
    selfMonitoring: "Watches the bridge itself and warns you if it stops keeping up.",
    quietHours: "Raise the bar for Email/Telegram alerts during a window (e.g. overnight) without ever fully muting them.",
    webhook: "Forward each analysed event to another system.",
    mqtt: "Publish events over MQTT with Home Assistant auto-discovery, optionally using this app as its own broker.",
    bugReports: "Email the developer when something breaks internally, using the Email account above."
  };

  function buildField(field, value) {
    const row = document.createElement("div");
    row.className = "field-row" + (WIDE_TYPES.has(field.type) ? " field-row-wide" : "");
    // The tip lives on the row because <input> can't render the ::after pseudo-element.
    if (field.tip) row.dataset.tip = field.tip;

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
      toggle.setAttribute("data-tip-right", "");
      toggle.dataset.tip = "Reveal this secret so you can check it.";
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

    if (field.type === "select") {
      const select = document.createElement("select");
      select.className = "field-input";
      select.dataset.field = field.name;
      for (const option of field.options) {
        const opt = document.createElement("option");
        opt.value = option.value;
        opt.textContent = option.label;
        if (option.value === value) opt.selected = true;
        select.appendChild(opt);
      }
      row.appendChild(select);
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
    if (SECTION_TIPS[schema.key]) header.dataset.tip = SECTION_TIPS[schema.key];
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

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
    saveBtn.dataset.tip = "Save this section. Changes take effect immediately - no restart needed.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn);
    if (schema.extraAction) {
      const actionBtn = document.createElement("button");
      actionBtn.className = "btn";
      actionBtn.textContent = schema.extraAction.label;
      if (schema.extraAction.tip) actionBtn.dataset.tip = schema.extraAction.tip;
      actionBtn.addEventListener("click", () => schema.extraAction.onClick());
      footer.append(actionBtn);
    }
    footer.append(errors);
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
    removeBtn.setAttribute("data-tip-right", "");
    removeBtn.dataset.tip = "Remove this question from the profile.";
    removeBtn.addEventListener("click", () => row.remove());
    row.append(input, removeBtn);
    return row;
  }

  function buildProfileCard(name, profile, options) {
    profile = profile || { displayName: "", systemPrompt: "", questions: [], crop: null };
    const startExpanded = !!(options && options.startExpanded);
    const card = document.createElement("div");
    card.className = "profile-card" + (startExpanded ? " open" : "");
    card.dataset.searchText = `${profile.displayName || ""} ${name}`.toLowerCase();

    const header = document.createElement("div");
    header.className = "profile-card-header";
    header.dataset.tip = "Click to expand and edit this profile.";

    const caret = document.createElement("span");
    caret.className = "profile-card-caret";
    caret.textContent = "▸";

    const summary = document.createElement("span");
    summary.className = "profile-card-summary";
    summary.textContent = profile.displayName || name || "(untitled profile)";

    const summaryKey = document.createElement("span");
    summaryKey.className = "profile-card-summary-key";
    summaryKey.textContent = name;

    const deleteBtn = document.createElement("button");
    deleteBtn.type = "button";
    deleteBtn.className = "icon-btn";
    deleteBtn.textContent = "✕";
    deleteBtn.setAttribute("data-tip-right", "");
    deleteBtn.dataset.tip = "Delete this whole profile. The 'default' profile is required and can't be removed.";
    deleteBtn.addEventListener("click", e => {
      e.stopPropagation();
      card.remove();
    });

    header.append(caret, summary, summaryKey, deleteBtn);
    header.addEventListener("click", () => {
      card.classList.toggle("open");
      caret.textContent = card.classList.contains("open") ? "▾" : "▸";
    });

    const body = document.createElement("div");
    body.className = "profile-card-body";

    const displayNameRow = document.createElement("div");
    displayNameRow.className = "field-row";
    displayNameRow.dataset.tip = "Friendly label shown in this list and in the trigger-zone profile picker. Optional - falls back to the technical name below.";
    const displayNameLabel = document.createElement("label");
    displayNameLabel.className = "field-label";
    displayNameLabel.textContent = "Display Name";
    const displayNameInput = document.createElement("input");
    displayNameInput.type = "text";
    displayNameInput.className = "field-input";
    displayNameInput.value = profile.displayName || "";
    displayNameInput.placeholder = "e.g. Chicken Coop Predator Watch";
    displayNameInput.addEventListener("input", () => {
      summary.textContent = displayNameInput.value.trim() || nameInput.value.trim() || "(untitled profile)";
      card.dataset.searchText = `${displayNameInput.value} ${nameInput.value}`.toLowerCase();
    });
    displayNameRow.append(displayNameLabel, displayNameInput);

    const nameRow = document.createElement("div");
    nameRow.className = "field-row";
    nameRow.dataset.tip = "Technical name. Blue Iris and camera profile pickers select a profile by this name, and 'default' is used for anything that doesn't match.";
    const nameLabel = document.createElement("label");
    nameLabel.className = "field-label";
    nameLabel.textContent = "Name (technical)";
    const nameInput = document.createElement("input");
    nameInput.type = "text";
    nameInput.className = "field-input profile-name-input";
    nameInput.value = name;
    nameInput.placeholder = "profile-name";
    nameInput.addEventListener("input", () => {
      summaryKey.textContent = nameInput.value.trim();
      if (!displayNameInput.value.trim()) summary.textContent = nameInput.value.trim() || "(untitled profile)";
      card.dataset.searchText = `${displayNameInput.value} ${nameInput.value}`.toLowerCase();
    });
    nameRow.append(nameLabel, nameInput);

    const promptRow = document.createElement("div");
    promptRow.className = "field-row";
    promptRow.dataset.tip = "Instructions sent to the AI with every image from this camera. Sets what it looks for and how it should reply.";
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
    questionsWrap.dataset.tip = "Specific things to ask about each image. The answers become the structured fields you see on the Feed tab.";
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
    addQBtn.dataset.tip = "Add another question for the AI to answer about each image.";
    addQBtn.addEventListener("click", () => qList.appendChild(buildQuestionRow("")));
    questionsWrap.append(qLabel, qList, addQBtn);

    const cropWrap = document.createElement("div");
    cropWrap.className = "field-row";
    cropWrap.dataset.tip = "Send only part of the image to the AI. Useful for cutting out a road or sky so the model focuses on the area you care about.";
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

    const plateWrap = document.createElement("div");
    plateWrap.className = "field-row field-checkbox-row";
    plateWrap.dataset.tip = "Also ask the AI to transcribe a vehicle's license plate when clearly legible, and check it against the Known Vehicles list below. Off by default - needs a camera with enough resolution and angle to actually read plates, and never affects whether an alert fires.";
    const plateSwitch = document.createElement("label");
    plateSwitch.className = "switch";
    const plateInput = document.createElement("input");
    plateInput.type = "checkbox";
    plateInput.checked = !!profile.licensePlateRecognitionEnabled;
    const plateTrack = document.createElement("span");
    plateTrack.className = "switch-track";
    plateSwitch.append(plateInput, plateTrack);
    const plateText = document.createElement("span");
    plateText.className = "field-label";
    plateText.textContent = "License plate recognition";
    plateWrap.append(plateSwitch, plateText);

    body.append(displayNameRow, nameRow, promptRow, questionsWrap, cropWrap, plateWrap);
    card.append(header, body);
    card._fields = { displayNameInput, nameInput, promptArea, qList, cropInput, cropGrid, plateInput };
    return card;
  }

  function collectProfile(card) {
    const f = card._fields;
    const name = f.nameInput.value.trim();
    const displayName = f.displayNameInput.value.trim();
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

    const licensePlateRecognitionEnabled = f.plateInput.checked;

    return { name, profile: { displayName, systemPrompt, questions, crop, licensePlateRecognitionEnabled } };
  }

  function buildKnownVehicleRow(vehicle) {
    vehicle = vehicle || { plate: "", label: "" };
    const row = document.createElement("div");
    row.className = "field-row-wide known-vehicle-row";

    const plateInput = document.createElement("input");
    plateInput.type = "text";
    plateInput.className = "field-input";
    plateInput.placeholder = "plate, e.g. ABC1234";
    plateInput.value = vehicle.plate || "";

    const labelInput = document.createElement("input");
    labelInput.type = "text";
    labelInput.className = "field-input";
    labelInput.placeholder = "label, e.g. Nathan's Truck";
    labelInput.value = vehicle.label || "";

    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "icon-btn";
    removeBtn.textContent = "✕";
    removeBtn.setAttribute("data-tip-right", "");
    removeBtn.dataset.tip = "Remove this vehicle.";
    removeBtn.addEventListener("click", () => row.remove());

    row.append(plateInput, labelInput, removeBtn);
    row._fields = { plateInput, labelInput };
    return row;
  }

  function renderLicenseSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">LICENSE</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Install the .lic file you received after purchase. Missing or invalid only shows a notice - every feature keeps working either way.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";

    const status = document.createElement("div");
    status.className = "settings-hint";
    status.textContent = "checking license…";
    body.appendChild(status);

    const row = document.createElement("div");
    row.className = "field-row-wide";
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = ".lic";
    fileInput.id = "license-file-input";
    row.appendChild(fileInput);
    body.appendChild(row);

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    body.appendChild(errors);

    async function refreshStatus() {
      try {
        const response = await fetch("/api/license/status");
        const payload = await response.json();
        status.textContent = payload.valid
          ? `licensed to ${payload.customerName}`
          : "no valid license installed - running unlicensed.";
      } catch {
        status.textContent = "could not check license status.";
      }
    }

    fileInput.addEventListener("change", async () => {
      const file = fileInput.files[0];
      if (!file) return;
      errors.innerHTML = "";
      status.textContent = "installing…";
      try {
        const contents = await file.text();
        const response = await fetch("/api/license/install", {
          method: "POST",
          headers: { "Content-Type": "text/plain" },
          body: contents
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showFieldErrors(errors, [payload.error || "Could not install that file."]);
          showToast("License not installed", false);
        } else {
          showToast(`Licensed to ${payload.customerName}`, true);
          window.GateWatchLicense.check();
        }
      } catch {
        showFieldErrors(errors, ["Network error while uploading."]);
      } finally {
        fileInput.value = "";
        await refreshStatus();
      }
    });

    refreshStatus();

    section.append(header, body);
    return section;
  }

  function renderAuthSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">LOGIN</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Require a username and password to open this dashboard. Off by default - anyone who can reach this network can otherwise use it freely.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";

    const status = document.createElement("div");
    status.className = "settings-hint";
    status.textContent = "checking login status…";
    body.appendChild(status);

    const formWrap = document.createElement("div");
    body.appendChild(formWrap);

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    body.appendChild(errors);

    function renderEnableForm() {
      formWrap.innerHTML = `
        <div class="field-row field-row-wide">
          <label class="field-label">Username</label>
          <input type="text" class="field-input" id="auth-new-username" autocomplete="username" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Password</label>
          <input type="password" class="field-input" id="auth-new-password" autocomplete="new-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Confirm Password</label>
          <input type="password" class="field-input" id="auth-new-confirm" autocomplete="new-password" />
        </div>
        <button class="btn" id="auth-enable-btn">require login</button>`;

      document.getElementById("auth-enable-btn").addEventListener("click", async () => {
        errors.innerHTML = "";
        const username = document.getElementById("auth-new-username").value.trim();
        const password = document.getElementById("auth-new-password").value;
        const confirmPassword = document.getElementById("auth-new-confirm").value;
        if (password !== confirmPassword) {
          showFieldErrors(errors, ["Passwords do not match."]);
          return;
        }
        try {
          const response = await fetch("/api/auth/enable", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password })
          });
          const payload = await response.json().catch(() => ({}));
          if (!response.ok) {
            showFieldErrors(errors, payload.errors || ["Could not enable login."]);
            showToast("Login not enabled", false);
            return;
          }
          showToast("Login required", true);
          window.GateWatchAuth.check();
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });
    }

    function renderChangeForm(currentUsername) {
      formWrap.innerHTML = `
        <div class="field-row field-row-wide">
          <label class="field-label">Current Password</label>
          <input type="password" class="field-input" id="auth-current-password" autocomplete="current-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Username</label>
          <input type="text" class="field-input" id="auth-username" value="${escapeHtml(currentUsername)}" autocomplete="username" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">New Password (leave blank to keep current)</label>
          <input type="password" class="field-input" id="auth-password" autocomplete="new-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Confirm New Password</label>
          <input type="password" class="field-input" id="auth-confirm" autocomplete="new-password" />
        </div>
        <div class="settings-section-footer">
          <button class="btn" id="auth-update-btn">save</button>
          <button class="btn" id="auth-disable-btn">turn off login</button>
        </div>`;

      document.getElementById("auth-update-btn").addEventListener("click", async () => {
        errors.innerHTML = "";
        const currentPassword = document.getElementById("auth-current-password").value;
        const newUsername = document.getElementById("auth-username").value.trim();
        const newPassword = document.getElementById("auth-password").value;
        const confirmNewPassword = document.getElementById("auth-confirm").value;
        if (newPassword !== confirmNewPassword) {
          showFieldErrors(errors, ["Passwords do not match."]);
          return;
        }
        try {
          const response = await fetch("/api/auth/update", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ currentPassword, newUsername, newPassword: newPassword || null })
          });
          const payload = await response.json().catch(() => ({}));
          if (!response.ok) {
            showFieldErrors(errors, payload.errors || ["Could not save."]);
            showToast("Not saved", false);
            return;
          }
          showToast("Login updated", true);
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });

      document.getElementById("auth-disable-btn").addEventListener("click", async () => {
        if (!confirm("Turn off login? Anyone on the network will then have full access to this dashboard.")) return;
        try {
          const response = await fetch("/api/auth/disable", { method: "POST" });
          if (!response.ok) {
            showToast("Could not turn off login", false);
            return;
          }
          showToast("Login turned off", true);
          window.GateWatchAuth.check();
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });
    }

    async function refreshStatus() {
      errors.innerHTML = "";
      try {
        const response = await fetch("/api/auth/status");
        const payload = await response.json();
        if (payload.enabled) {
          status.textContent = "login is required to open this dashboard.";
          renderChangeForm(payload.username || "");
        } else {
          status.textContent = "no login required - open to anyone on the network.";
          renderEnableForm();
        }
      } catch {
        status.textContent = "could not check login status.";
      }
    }

    refreshStatus();

    section.append(header, body);
    return section;
  }

  function renderKnownVehiclesSection(vehicles) {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">KNOWN VEHICLES</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Vehicles checked against a transcribed plate, for cameras/profiles with license plate recognition turned on. Informational only - a match or non-match never changes whether an alert fires.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";

    const hint = document.createElement("div");
    hint.className = "settings-hint";
    hint.textContent = "Shared across every camera/profile that has license plate recognition enabled.";
    body.appendChild(hint);

    const rowsWrap = document.createElement("div");
    rowsWrap.className = "known-vehicles-list";
    for (const vehicle of vehicles || []) {
      rowsWrap.appendChild(buildKnownVehicleRow(vehicle));
    }
    body.appendChild(rowsWrap);

    const toolbar = document.createElement("div");
    toolbar.className = "profiles-toolbar";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add vehicle";
    addBtn.dataset.tip = "Add another known vehicle by plate and label.";
    addBtn.addEventListener("click", () => rowsWrap.appendChild(buildKnownVehicleRow(null)));
    toolbar.appendChild(addBtn);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save the known vehicle list.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, errors);

    saveBtn.addEventListener("click", async () => {
      const list = Array.from(rowsWrap.querySelectorAll(".known-vehicle-row"))
        .map(row => ({ plate: row._fields.plateInput.value.trim(), label: row._fields.labelInput.value.trim() }))
        .filter(v => v.plate.length > 0);
      await submit("/api/settings/known-vehicles", list, "Known vehicles", errors, saveBtn);
    });

    body.append(toolbar, footer);
    section.append(header, body);
    return section;
  }

  function renderPromptProfilesSection(profiles) {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">PROMPT PROFILES</span><span class="settings-section-caret">&#9656;</span>`;
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";

    const count = Object.keys(profiles || {}).length;
    const hint = document.createElement("div");
    hint.className = "settings-hint";
    hint.textContent = `The AI prompts sent to Ollama per camera profile (${count} available). A 'default' profile is required as a fallback for unrecognized profile names. Click a profile to expand it.`;
    body.appendChild(hint);

    const searchRow = document.createElement("div");
    searchRow.className = "field-row profiles-search-row";
    const searchInput = document.createElement("input");
    searchInput.type = "text";
    searchInput.className = "field-input";
    searchInput.placeholder = "search profiles by name (e.g. \"chicken\", \"gate\", \"garage\")…";
    searchRow.appendChild(searchInput);
    body.appendChild(searchRow);

    const cardsWrap = document.createElement("div");
    cardsWrap.className = "settings-root";
    for (const [name, profile] of Object.entries(profiles || {})) {
      cardsWrap.appendChild(buildProfileCard(name, profile));
    }

    searchInput.addEventListener("input", () => {
      const term = searchInput.value.trim().toLowerCase();
      for (const card of cardsWrap.querySelectorAll(".profile-card")) {
        const matches = !term || card.dataset.searchText.includes(term);
        card.classList.toggle("filtered-out", !matches);
      }
    });

    const toolbar = document.createElement("div");
    toolbar.className = "profiles-toolbar";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add profile";
    addBtn.dataset.tip = "Create another prompt profile. Blue Iris picks one per camera by name.";
    addBtn.addEventListener("click", () => {
      const card = buildProfileCard("", null, { startExpanded: true });
      cardsWrap.appendChild(card);
      card.scrollIntoView({ behavior: "smooth", block: "center" });
    });
    toolbar.appendChild(addBtn);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save every profile. New prompts apply to the next alert that comes in.";
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
      root.appendChild(renderAuthSection());
      root.appendChild(renderLicenseSection());
      root.appendChild(renderKnownVehiclesSection(data.knownVehicles));
      root.appendChild(renderPromptProfilesSection(data.promptProfiles));

      const first = root.querySelector(".settings-section");
      if (first) first.classList.add("open");
    } catch (err) {
      root.innerHTML = `<div class="empty-state">failed to load settings: ${err.message}</div>`;
    }
  }

  return { load };
})();
