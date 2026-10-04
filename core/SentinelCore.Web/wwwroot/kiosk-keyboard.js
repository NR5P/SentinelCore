// Kiosk on-screen keyboard.
//
// The wall kiosk runs Chromium full screen on a Raspberry Pi touchscreen with no keyboard. The
// Pi's own on-screen keyboard (squeekboard) is drawn underneath a full-screen window, so tapping
// a text box - the sign-in screen, a search, a note - gave no way to type at all. In kiosk mode
// (html.kiosk) this page brings its own: tapping a typing field slides a keyboard up from the
// bottom, and the sign-in screen opens with it already up. Anywhere else (kiosk mode off) it
// never appears.
//
// Keys type through execCommand("insertText"), so each one is a real edit: the field's input
// events, undo and maxlength all behave as if typed. Enter sends the same keydown the page's own
// Enter handlers listen for (the sign-in screen has no <form>), then submits a form if there is one.
(function () {
  const TYPED_INPUTS = new Set(["", "text", "password", "search", "email", "url", "tel", "number"]);
  // A focus this soon after a press came from that press, rather than a script focusing a field.
  const TAP_FOCUS_MS = 1000;
  const REPEAT_DELAY = 450;   // ms holding backspace before it starts repeating
  const REPEAT_EVERY = 60;    // ms between repeats after that
  const DOUBLE_TAP = 400;     // ms between two shift taps that locks capitals on

  const LAYOUTS = {
    abc: [
      ["q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "{bksp}"],
      ["a", "s", "d", "f", "g", "h", "j", "k", "l", "{enter}"],
      ["{shift}", "z", "x", "c", "v", "b", "n", "m", "@", ".", "{shift}"],
      ["{123}", "{space}", "{hide}"],
    ],
    num: [
      ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "{bksp}"],
      ["-", "/", ":", ";", "(", ")", "$", "&", "@", "\"", "{enter}"],
      ["{sym}", ".", ",", "?", "!", "'", "_", "%", "*", "+", "{sym}"],
      ["{abc}", "{space}", "{hide}"],
    ],
    sym: [
      ["[", "]", "{", "}", "#", "%", "^", "*", "+", "=", "{bksp}"],
      ["_", "\\", "|", "~", "<", ">", "`", "€", "£", "•", "{enter}"],
      ["{123}", ".", ",", "?", "!", "'", "\"", ":", ";", "/", "{123}"],
      ["{abc}", "{space}", "{hide}"],
    ],
  };
  const LABELS = {
    "{bksp}": "⌫", "{enter}": "enter", "{shift}": "⇧", "{space}": " ", "{hide}": "hide ⌄",
    "{123}": "123", "{abc}": "ABC", "{sym}": "#+=",
  };

  const isOn = () => document.documentElement.classList.contains("kiosk");

  let kb = null;           // the keyboard element, built the first time it's needed
  let target = null;       // the field being typed into while the keyboard is up
  let layout = "abc";
  let shift = "off";       // "off", "once" (next letter only) or "lock"
  let lastShiftTap = 0;
  let lastPress = 0;
  let watch = null;        // interval that notices the field going away
  let repeat = null;

  function typeable(el) {
    if (!el || el.nodeType !== 1 || el.disabled || el.readOnly) return false;
    if (el.closest(".kiosk-kb")) return false;
    if (el.tagName === "TEXTAREA") return true;
    if (el.tagName === "INPUT") return TYPED_INPUTS.has((el.getAttribute("type") || "").toLowerCase());
    return el.isContentEditable;
  }

  function visible(el) {
    return el.isConnected && el.getClientRects().length > 0;
  }

  function build() {
    kb = document.createElement("div");
    kb.className = "kiosk-kb";
    kb.setAttribute("aria-hidden", "true");
    // The field has to keep focus, so nothing on the keyboard may take it - not a key, not a gap.
    kb.addEventListener("pointerdown", onKeyDown);
    kb.addEventListener("mousedown", (e) => e.preventDefault());
    for (const type of ["pointerup", "pointercancel", "pointerleave"]) kb.addEventListener(type, onKeyUp);
    kb.addEventListener("contextmenu", (e) => e.preventDefault());
    document.body.appendChild(kb);
    render();
  }

  function render() {
    kb.textContent = "";
    kb.dataset.layout = layout;
    for (const row of LAYOUTS[layout]) {
      const rowEl = document.createElement("div");
      rowEl.className = "kiosk-kb-row";
      for (const key of row) {
        const k = document.createElement("div");
        k.className = "kiosk-kb-key";
        k.dataset.key = key;
        if (key.startsWith("{")) {
          k.classList.add("kiosk-kb-special", "kiosk-kb-" + key.slice(1, -1));
          k.textContent = LABELS[key];
          if (key === "{shift}" && shift !== "off") k.classList.add("kiosk-kb-on");
          if (key === "{shift}" && shift === "lock") k.classList.add("kiosk-kb-lock");
        } else {
          k.textContent = shift !== "off" && layout === "abc" ? key.toUpperCase() : key;
        }
        rowEl.appendChild(k);
      }
      kb.appendChild(rowEl);
    }
  }

  function show(el) {
    if (!kb) build();
    target = el;
    if (kb.classList.contains("open")) return;
    layout = "abc";
    shift = "off";
    render();
    kb.classList.add("open");
    const root = document.documentElement;
    root.style.setProperty("--kiosk-kb-h", kb.offsetHeight + "px");
    root.classList.add("kiosk-kb-open");
    // Once the page has made room, make sure the field isn't hidden under the keyboard.
    requestAnimationFrame(() => {
      if (!target) return;
      const bottom = target.getBoundingClientRect().bottom;
      if (bottom > window.innerHeight - kb.offsetHeight - 8) target.scrollIntoView({ block: "center" });
    });
    clearInterval(watch);
    watch = setInterval(() => {
      // Kiosk mode switched off, or the field hidden/removed (the sign-in screen closing after
      // Enter) without a focusout to say so.
      if (!isOn() || !target || !visible(target) || document.activeElement !== target) hide();
    }, 300);
  }

  function hide() {
    clearInterval(watch);
    watch = null;
    stopRepeat();
    target = null;
    if (!kb || !kb.classList.contains("open")) return;
    kb.classList.remove("open");
    document.documentElement.classList.remove("kiosk-kb-open");
  }

  // ---- typing ----

  function insert(text) {
    const el = target;
    if (!el) return;
    if (document.activeElement !== el) el.focus({ preventScroll: true });
    if (document.execCommand("insertText", false, text)) return;
    // Fields with no text selection (type=number) can refuse an edit command.
    el.value += text;
    el.dispatchEvent(new Event("input", { bubbles: true }));
  }

  function backspace() {
    const el = target;
    if (!el) return;
    if (document.activeElement !== el) el.focus({ preventScroll: true });
    if (document.execCommand("delete")) return;
    if (el.value) {
      el.value = el.value.slice(0, -1);
      el.dispatchEvent(new Event("input", { bubbles: true }));
    }
  }

  function enter() {
    const el = target;
    if (!el) return;
    if (el.tagName === "TEXTAREA") { insert("\n"); return; }
    if (el.isContentEditable) { document.execCommand("insertParagraph"); return; }
    const init = { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true, cancelable: true };
    const proceed = el.dispatchEvent(new KeyboardEvent("keydown", init));
    el.dispatchEvent(new KeyboardEvent("keyup", init));
    if (proceed && el.form && document.activeElement === el) el.form.requestSubmit();
  }

  function stopRepeat() {
    clearTimeout(repeat);
    clearInterval(repeat);
    repeat = null;
  }

  function onKeyDown(e) {
    e.preventDefault();
    const keyEl = e.target.closest(".kiosk-kb-key");
    if (!keyEl) return;
    keyEl.classList.add("kiosk-kb-down");
    const key = keyEl.dataset.key;
    switch (key) {
      case "{bksp}":
        backspace();
        stopRepeat();
        repeat = setTimeout(() => { repeat = setInterval(backspace, REPEAT_EVERY); }, REPEAT_DELAY);
        return;
      case "{enter}": enter(); return;
      case "{space}": insert(" "); return;
      case "{hide}": {
        const el = target;
        hide();
        // Let go of the field too, so tapping it again brings the keyboard back.
        if (el) el.blur();
        return;
      }
      case "{shift}": {
        const now = performance.now();
        if (shift === "off") shift = now - lastShiftTap < DOUBLE_TAP ? "lock" : "once";
        else if (shift === "once" && now - lastShiftTap < DOUBLE_TAP) shift = "lock";
        else shift = "off";
        lastShiftTap = now;
        render();
        return;
      }
      case "{123}": layout = "num"; render(); return;
      case "{sym}": layout = "sym"; render(); return;
      case "{abc}": layout = "abc"; render(); return;
    }
    insert(shift !== "off" && layout === "abc" ? key.toUpperCase() : key);
    if (shift === "once") { shift = "off"; render(); }
  }

  function onKeyUp() {
    stopRepeat();
    for (const k of kb.querySelectorAll(".kiosk-kb-down")) k.classList.remove("kiosk-kb-down");
  }

  // ---- when to show ----

  document.addEventListener("pointerdown", (e) => {
    lastPress = performance.now();
    // Tapping the field that already has focus (after "hide") brings the keyboard back.
    if (isOn() && typeable(e.target) && e.target === document.activeElement) show(e.target);
  }, true);

  document.addEventListener("focusin", (e) => {
    if (!isOn() || !typeable(e.target)) return;
    // A field focused by a tap gets the keyboard. One a script focused on its own (a tab that
    // focuses its search box) doesn't, so the keyboard doesn't cover the page uninvited - except
    // on the sign-in screen, where typing is the only thing to do.
    const tapped = performance.now() - lastPress < TAP_FOCUS_MS;
    if (tapped || e.target.closest("#login-gate") || (kb && kb.classList.contains("open"))) show(e.target);
  });

  // The sign-in screen can focus its username box before the browser window itself has focus
  // (Chromium still starting up), in which case no focusin reaches us - catch up once it does.
  window.addEventListener("focus", () => {
    const el = document.activeElement;
    if (isOn() && typeable(el) && el.closest("#login-gate")) show(el);
  });

  document.addEventListener("focusout", () => {
    // Moving between two fields fires focusout then focusin; only hide if focus didn't land
    // somewhere typeable.
    setTimeout(() => {
      if (!typeable(document.activeElement)) hide();
    }, 0);
  });
})();
