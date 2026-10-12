// Kiosk-friendly text questions.
//
// The browser's own prompt() box is drawn by Chromium, outside the page, so on the wall kiosk the
// page's on-screen keyboard (kiosk-keyboard.js) can't type into it - naming a board was impossible.
// SentinelAsk.text() asks the same question in an in-page dialog when kiosk mode is on (and with
// prompt() everywhere else), sitting above the keyboard with a large field.
//
// SentinelAsk.compose() is the kiosk's way to add several things in a row (board cards): a big
// centered box, Enter adds what's typed and clears it for the next one, Done closes.
(function () {
  const isOn = () => document.documentElement.classList.contains("kiosk");

  let overlay = null;
  let finish = null;   // closes the open dialog, answering its question

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]));
  }

  function open(html) {
    if (finish) finish(null);
    if (!overlay) {
      overlay = document.createElement("div");
      overlay.className = "kiosk-ask";
      overlay.addEventListener("pointerdown", (e) => {
        if (e.target === overlay && finish) { e.preventDefault(); finish(null); }
      });
      document.body.appendChild(overlay);
    }
    overlay.innerHTML = `<div class="kiosk-ask-panel">${html}</div>`;
    overlay.classList.add("open");
    return overlay.firstChild;
  }

  function close() {
    finish = null;
    if (!overlay) return;
    overlay.classList.remove("open");
    overlay.innerHTML = "";
  }

  // The field opens with the keyboard already up (data-kiosk-kb-auto). Text already in it is
  // selected, as prompt() does, so typing replaces it and a tap on the field keeps it.
  function focusField(field) {
    field.focus({ preventScroll: true });
    try { field.setSelectionRange(0, field.value.length); } catch { /* type=email etc. */ }
  }

  function text(message, value = "", options = {}) {
    if (!isOn()) return Promise.resolve(window.prompt(message, value));
    return new Promise((resolve) => {
      const type = ["password", "email"].includes(options.type) ? options.type : "text";
      const panel = open(`
        <div class="kiosk-ask-title">${escapeHtml(message)}</div>
        <input class="kiosk-ask-field" type="${type}" data-kiosk-kb-auto autocomplete="${type === "password" ? "new-password" : "off"}" />
        <div class="kiosk-ask-buttons">
          <button type="button" class="btn kiosk-ask-cancel">cancel</button>
          <button type="button" class="btn kiosk-ask-go kiosk-ask-ok">ok</button>
        </div>`);
      const field = panel.querySelector(".kiosk-ask-field");
      field.value = value == null ? "" : String(value);
      finish = (answer) => { close(); resolve(answer); };
      panel.querySelector(".kiosk-ask-cancel").addEventListener("click", () => finish && finish(null));
      panel.querySelector(".kiosk-ask-ok").addEventListener("click", () => finish && finish(field.value));
      field.addEventListener("keydown", (e) => {
        if (e.key === "Enter") { e.preventDefault(); if (finish) finish(field.value); }
        else if (e.key === "Escape") { e.preventDefault(); if (finish) finish(null); }
      });
      focusField(field);
    });
  }

  // options: { title, placeholder, add(text) } - add is called once per non-empty entry.
  function compose(options) {
    const panel = open(`
      <div class="kiosk-ask-title">${escapeHtml(options.title || "")}</div>
      <textarea class="kiosk-ask-field kiosk-ask-compose" rows="3" data-kiosk-kb-auto data-kiosk-enter="submit"
        placeholder="${escapeHtml(options.placeholder || "")}"></textarea>
      <div class="kiosk-ask-added"></div>
      <div class="kiosk-ask-buttons">
        <button type="button" class="btn kiosk-ask-go kiosk-ask-add">add</button>
        <button type="button" class="btn kiosk-ask-done">done</button>
      </div>`);
    const field = panel.querySelector(".kiosk-ask-field");
    const added = panel.querySelector(".kiosk-ask-added");
    finish = () => close();
    const submit = () => {
      const value = field.value.replace(/\s+/g, " ").trim();
      if (!value) return;
      options.add(value);
      added.insertAdjacentHTML("beforeend", `<span>&check; ${escapeHtml(value)}</span>`);
      field.value = "";
      field.dispatchEvent(new Event("input", { bubbles: true }));
      focusField(field);
    };
    // Tapping add mustn't take focus from the box, or the keyboard drops and comes back.
    panel.querySelector(".kiosk-ask-add").addEventListener("mousedown", (e) => e.preventDefault());
    panel.querySelector(".kiosk-ask-add").addEventListener("click", submit);
    panel.querySelector(".kiosk-ask-done").addEventListener("click", () => finish && finish());
    field.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); submit(); }
      else if (e.key === "Escape") { e.preventDefault(); if (finish) finish(); }
    });
    focusField(field);
  }

  window.SentinelAsk = { text, compose };
})();
