// Automation rules: "when this camera sees that, publish a custom MQTT message or call an API
// endpoint" - the generalized version of the fixed-topic MQTT/single-URL Webhook channels under
// Settings. Rules themselves live in the main app (see AutomationRuleEndpoints/
// AutomationAlertAction in SentinelCore); this dashboard only talks to the proxy routes under
// /api/automation-rules (see AutomationRuleProxyEndpoints.cs).
window.GateWatchAutomations = (function () {
  let rules = [];
  let metadata = { categories: [], tokens: [] };
  let editingId = null;
  let loaded = false;

  function escapeHtml(value) {
    const div = document.createElement("div");
    div.textContent = value ?? "";
    return div.innerHTML;
  }

  async function load() {
    const root = document.getElementById("automations-root");
    try {
      if (!loaded) {
        const metaResponse = await fetch("/api/automation-rules/metadata");
        if (metaResponse.ok) metadata = await metaResponse.json();
        loaded = true;
      }

      const response = await fetch("/api/automation-rules");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      rules = await response.json();
      renderList();
    } catch (err) {
      console.error("Failed to load automation rules", err);
      root.innerHTML = `<div class="empty-state">could not load automation rules</div>`;
    }
  }

  function renderList() {
    const root = document.getElementById("automations-root");
    root.innerHTML = "";
    if (rules.length === 0) {
      root.innerHTML = `<div class="empty-state">no automation rules yet - add one to fire a custom MQTT message or API call when the AI detects something specific</div>`;
      return;
    }

    for (const rule of rules) root.appendChild(buildRuleCard(rule));
  }

  function summarizeCameras(rule) {
    return rule.cameras.length === 0 ? "any camera" : rule.cameras.join(", ");
  }

  function summarizeCategories(rule) {
    return rule.categories.length === 0 ? "any detection" : rule.categories.join(", ");
  }

  function buildRuleCard(rule) {
    const card = document.createElement("div");
    card.className = "camera-card";

    const info = document.createElement("div");
    info.className = "camera-card-info";
    const actionSummary = rule.actionType === "mqtt"
      ? `MQTT &rarr; ${escapeHtml(rule.mqttTopic) || "(no topic set)"}`
      : `Webhook &rarr; ${escapeHtml(rule.webhookMethod)} ${escapeHtml(rule.webhookUrl) || "(no URL set)"}`;
    info.innerHTML = `
      <span class="camera-card-name">${escapeHtml(rule.name)}${rule.enabled ? "" : " (disabled)"}</span>
      <span class="camera-card-meta">${escapeHtml(summarizeCameras(rule))} &middot; ${escapeHtml(summarizeCategories(rule))}</span>
      <span class="camera-card-meta">${actionSummary}</span>
      <span class="camera-card-meta" id="automation-status-${rule.id}"></span>
    `;

    const actions = document.createElement("div");
    actions.className = "camera-card-actions";

    const testBtn = document.createElement("button");
    testBtn.className = "btn";
    testBtn.textContent = "test";
    testBtn.setAttribute("data-tip-right", "");
    testBtn.dataset.tip = "Fire this rule right now with sample data, so you can check the MQTT topic/payload or webhook URL/body without waiting for a real detection.";
    testBtn.addEventListener("click", () => testRule(rule, testBtn));

    const editBtn = document.createElement("button");
    editBtn.className = "btn";
    editBtn.textContent = "edit";
    editBtn.setAttribute("data-tip-right", "");
    editBtn.dataset.tip = "Change this rule's cameras, detections, or what it does when they match.";
    editBtn.addEventListener("click", () => openForm(rule));

    const deleteBtn = document.createElement("button");
    deleteBtn.className = "btn";
    deleteBtn.textContent = "delete";
    deleteBtn.setAttribute("data-tip-right", "");
    deleteBtn.dataset.tip = "Remove this rule. It stops firing immediately.";
    deleteBtn.addEventListener("click", () => removeRule(rule));

    actions.append(testBtn, editBtn, deleteBtn);
    card.append(info, actions);
    return card;
  }

  async function testRule(rule, button) {
    const statusEl = document.getElementById(`automation-status-${rule.id}`);
    button.disabled = true;
    const originalText = button.textContent;
    button.textContent = "testing…";
    if (statusEl) { statusEl.textContent = ""; statusEl.className = "camera-card-meta"; }

    try {
      const response = await fetch(`/api/automation-rules/${rule.id}/test`, { method: "POST" });
      const payload = await response.json().catch(() => ({}));
      if (statusEl) {
        statusEl.textContent = response.ok ? `ok: ${payload.detail}` : `failed: ${payload.error || "unknown error"}`;
        statusEl.classList.add(response.ok ? "camera-test-status-ok" : "camera-test-status-error");
      }
    } catch {
      if (statusEl) {
        statusEl.textContent = "network error while testing";
        statusEl.classList.add("camera-test-status-error");
      }
    } finally {
      button.disabled = false;
      button.textContent = originalText;
    }
  }

  async function removeRule(rule) {
    if (!confirm(`Remove automation rule "${rule.name}"?`)) return;

    try {
      await fetch(`/api/automation-rules/${rule.id}`, { method: "DELETE" });
      await load();
    } catch (err) {
      console.error("Failed to delete automation rule", err);
    }
  }

  // ---------------- Form ----------------

  function openForm(rule) {
    editingId = rule ? rule.id : null;
    const wrap = document.getElementById("automation-form-wrap");
    const fields = document.getElementById("automation-form-fields");
    const errors = document.getElementById("automation-form-errors");
    errors.innerHTML = "";
    fields.innerHTML = "";

    fields.appendChild(buildTextField("auto-name", "Name", rule?.name ?? "",
      "A short label for this rule, e.g. 'Person at gate MQTT'."));
    fields.appendChild(buildCheckboxField("auto-enabled", "Enabled", rule?.enabled ?? true,
      "Turn this rule off without deleting it."));
    fields.appendChild(buildTextField("auto-cameras", "Cameras (comma-separated, blank = any)", (rule?.cameras ?? []).join(", "),
      "Match the camera name exactly as it appears on the Events tab - the Blue Iris camera name, or one of your own added cameras. Leave blank to match every camera."));
    fields.appendChild(buildCategoryCheckboxes(rule?.categories ?? []));
    fields.appendChild(buildRangeField("auto-min-confidence", "Minimum confidence", rule?.minimumConfidence ?? 0.6,
      "Only fire when the AI's confidence is at least this. 0 fires on anything, 1 requires certainty."));
    fields.appendChild(buildCheckboxField("auto-only-recommended", "Only when a notification is recommended", rule?.onlyWhenNotificationRecommended ?? true,
      "Skip alerts the AI itself judged not worth telling anyone about. Turn this off to fire on every matching detection regardless."));
    fields.appendChild(buildNumberField("auto-cooldown", "Cooldown minutes (0 = none)", rule?.duplicateCooldownMinutes ?? 5, 0, 1440,
      "Suppress repeats of the same camera/detection within this many minutes, so one lingering car doesn't fire the rule over and over."));
    fields.appendChild(buildActionTypeField(rule?.actionType ?? "mqtt"));
    fields.appendChild(buildMqttFields(rule));
    fields.appendChild(buildWebhookFields(rule));
    fields.appendChild(buildTokenLegend());

    applyActionTypeVisibility(document.getElementById("auto-action-type").value);

    wrap.classList.remove("hidden");
    wrap.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  function closeForm() {
    editingId = null;
    document.getElementById("automation-form-wrap").classList.add("hidden");
  }

  function fieldRow(tip, wide) {
    const row = document.createElement("div");
    row.className = "field-row" + (wide ? " field-row-wide" : "");
    if (tip) row.dataset.tip = tip;
    return row;
  }

  function buildTextField(id, label, value, tip) {
    const row = fieldRow(tip, true);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "text";
    input.className = "field-input";
    input.id = id;
    input.value = value;
    row.appendChild(input);
    return row;
  }

  function buildNumberField(id, label, value, min, max, tip) {
    const row = fieldRow(tip, false);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "number";
    input.className = "field-input";
    input.id = id;
    input.min = min;
    input.max = max;
    input.step = 1;
    input.value = value;
    row.appendChild(input);
    return row;
  }

  function buildRangeField(id, label, value, tip) {
    const row = fieldRow(tip, false);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "number";
    input.className = "field-input";
    input.id = id;
    input.min = 0;
    input.max = 1;
    input.step = 0.05;
    input.value = value;
    row.appendChild(input);
    return row;
  }

  function buildCheckboxField(id, label, checked, tip) {
    const row = fieldRow(tip, true);
    row.classList.add("field-checkbox-row");
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

  function buildTextareaField(id, label, value, tip, rows) {
    const row = fieldRow(tip, true);
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const textarea = document.createElement("textarea");
    textarea.className = "field-textarea";
    textarea.id = id;
    textarea.rows = rows || 3;
    textarea.value = value ?? "";
    row.appendChild(textarea);
    return row;
  }

  function buildCategoryCheckboxes(selected) {
    const row = fieldRow(
      "Which detected categories fire this rule. Leave everything unchecked to match any detection, including ones that don't fall into a named category.",
      true);
    row.innerHTML = `<label class="field-label">Detected categories (blank = any)</label>`;
    const grid = document.createElement("div");
    grid.id = "auto-categories";
    grid.style.display = "flex";
    grid.style.flexWrap = "wrap";
    grid.style.gap = "4px 16px";

    for (const category of metadata.categories) {
      const label = document.createElement("label");
      label.className = "field-checkbox-row";
      label.style.marginRight = "4px";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = category;
      input.checked = selected.includes(category);
      input.dataset.role = "auto-category";
      const text = document.createElement("span");
      text.textContent = category;
      label.append(input, text);
      grid.appendChild(label);
    }
    row.appendChild(grid);
    return row;
  }

  function buildActionTypeField(value) {
    const row = fieldRow("What this rule does when it matches an alert.", true);
    row.innerHTML = `<label class="field-label" for="auto-action-type">Action</label>`;
    const select = document.createElement("select");
    select.className = "field-input";
    select.id = "auto-action-type";
    select.innerHTML = `
      <option value="mqtt">Publish a custom MQTT message</option>
      <option value="webhook">Call an API endpoint (webhook)</option>
    `;
    select.value = value;
    select.addEventListener("change", () => applyActionTypeVisibility(select.value));
    row.appendChild(select);
    return row;
  }

  function buildMqttFields(rule) {
    const wrap = document.createElement("div");
    wrap.id = "auto-mqtt-fields";
    wrap.style.display = "contents";
    wrap.appendChild(buildTextField("auto-mqtt-topic", "MQTT topic", rule?.mqttTopic ?? "",
      "Where to publish, e.g. home/sentinelcore/{{camera}}/{{category}}. Requires the MQTT integration to be turned on under Settings."));
    wrap.appendChild(buildTextareaField("auto-mqtt-payload", "MQTT payload", rule?.mqttPayload ?? '{"camera":"{{camera}}","summary":"{{summary}}"}',
      "The message body. Use the tokens below - they're filled in from the alert that matched.", 4));
    wrap.appendChild(buildCheckboxField("auto-mqtt-retain", "Retain (broker keeps the last message for new subscribers)", rule?.mqttRetain ?? false,
      "Turn on for a status-style topic a new subscriber should see immediately. Leave off for a one-off event notification."));
    return wrap;
  }

  function buildWebhookFields(rule) {
    const wrap = document.createElement("div");
    wrap.id = "auto-webhook-fields";
    wrap.style.display = "contents";
    wrap.appendChild(buildTextField("auto-webhook-url", "URL", rule?.webhookUrl ?? "",
      "The full URL to call, e.g. https://example.com/hooks/gate. Tokens like {{camera}} work here too."));

    const methodRow = fieldRow("The HTTP method to use. Most APIs expect POST.", false);
    methodRow.innerHTML = `<label class="field-label" for="auto-webhook-method">Method</label>`;
    const methodSelect = document.createElement("select");
    methodSelect.className = "field-input";
    methodSelect.id = "auto-webhook-method";
    methodSelect.innerHTML = ["POST", "PUT", "PATCH", "GET", "DELETE"]
      .map(m => `<option value="${m}">${m}</option>`).join("");
    methodSelect.value = rule?.webhookMethod ?? "POST";
    methodRow.appendChild(methodSelect);
    wrap.appendChild(methodRow);

    wrap.appendChild(buildTextField("auto-webhook-content-type", "Content-Type", rule?.webhookContentType ?? "application/json",
      "Sent as the request's Content-Type header when there's a body."));
    wrap.appendChild(buildTextareaField("auto-webhook-headers", "Extra headers (one per line, Name: Value)", rule?.webhookHeaders ?? "",
      "Optional. One header per line, e.g. Authorization: Bearer xyz. Tokens like {{camera}} work in values too.", 3));
    wrap.appendChild(buildTextareaField("auto-webhook-body", "Body", rule?.webhookBody ?? '{"camera":"{{camera}}","summary":"{{summary}}"}',
      "Sent as the request body (ignored for GET/DELETE). Use the tokens below - they're filled in from the alert that matched.", 4));
    return wrap;
  }

  function buildTokenLegend() {
    const row = fieldRow(null, true);
    const label = document.createElement("div");
    label.className = "field-label";
    label.textContent = "Available tokens (use as {{token}} in the fields above):";
    const list = document.createElement("div");
    list.className = "camera-card-meta";
    list.style.whiteSpace = "normal";
    list.textContent = metadata.tokens.map(t => `{{${t}}}`).join("  ");
    row.append(label, list);
    return row;
  }

  function applyActionTypeVisibility(actionType) {
    document.getElementById("auto-mqtt-fields").style.display = actionType === "mqtt" ? "contents" : "none";
    document.getElementById("auto-webhook-fields").style.display = actionType === "webhook" ? "contents" : "none";
  }

  async function saveRule() {
    const categories = [...document.querySelectorAll('input[data-role="auto-category"]:checked')].map(i => i.value);
    const body = {
      name: document.getElementById("auto-name").value.trim(),
      enabled: document.getElementById("auto-enabled").checked,
      cameras: document.getElementById("auto-cameras").value.split(",").map(c => c.trim()).filter(Boolean),
      categories,
      minimumConfidence: Number(document.getElementById("auto-min-confidence").value),
      onlyWhenNotificationRecommended: document.getElementById("auto-only-recommended").checked,
      duplicateCooldownMinutes: Number(document.getElementById("auto-cooldown").value),
      actionType: document.getElementById("auto-action-type").value,
      mqttTopic: document.getElementById("auto-mqtt-topic").value.trim(),
      mqttPayload: document.getElementById("auto-mqtt-payload").value,
      mqttRetain: document.getElementById("auto-mqtt-retain").checked,
      webhookUrl: document.getElementById("auto-webhook-url").value.trim(),
      webhookMethod: document.getElementById("auto-webhook-method").value,
      webhookHeaders: document.getElementById("auto-webhook-headers").value,
      webhookBody: document.getElementById("auto-webhook-body").value,
      webhookContentType: document.getElementById("auto-webhook-content-type").value.trim()
    };

    const errorsEl = document.getElementById("automation-form-errors");
    errorsEl.innerHTML = "";

    const endpoint = editingId ? `/api/automation-rules/${editingId}` : "/api/automation-rules";
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

      closeForm();
      await load();
    } catch {
      const li = document.createElement("li");
      li.textContent = "Network error while saving.";
      errorsEl.appendChild(li);
    }
  }

  document.getElementById("add-automation-btn").addEventListener("click", () => openForm(null));
  document.getElementById("automation-cancel-btn").addEventListener("click", closeForm);
  document.getElementById("automation-save-btn").addEventListener("click", saveRule);

  return { load, renderList };
})();
