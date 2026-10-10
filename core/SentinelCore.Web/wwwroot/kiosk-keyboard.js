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
//
// In ordinary text boxes (not passwords, email, numbers, sign-in, or fields marked
// spellcheck="false" like paths and host names) a row above the keys suggests words, and a finger
// dragged across the letters types the word it spelled (kiosk-swipe.js does the guessing). A
// letter then types when the finger lifts rather than when it lands, so a press that starts
// moving can still turn into a swipe. A swiped word gets a space in front of it when it follows
// another word; the row then offers the next two guesses, and backspace takes the whole word off.
(function () {
  const TYPED_INPUTS = new Set(["", "text", "password", "search", "email", "url", "tel", "number"]);
  // A focus this soon after a press came from that press, rather than a script focusing a field.
  const TAP_FOCUS_MS = 1000;
  const REPEAT_DELAY = 450;   // ms holding backspace before it starts repeating
  const REPEAT_EVERY = 60;    // ms between repeats after that
  const DOUBLE_TAP = 400;     // ms between two shift taps that locks capitals on
  const SWIPE_START = 0.45;   // key widths a press moves before it's a swipe instead of a tap
  const LEARNED_KEY = "sentinelcore.kiosk.words";  // localStorage: words this kiosk has typed
  const NO_PREDICT = new Set(["username", "email", "current-password", "new-password", "one-time-code"]);

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
  let keysEl = null;       // the rows of keys, inside the keyboard
  let bar = null;          // the suggestion row
  let trail = null;        // canvas the swipe's line is drawn on
  let words = null;        // KioskWords engine, once kiosk-words.txt has loaded
  let predicting = false;  // the field being typed into gets suggestions and swipes
  let press = null;        // a letter press not yet typed: { id, key, keyEl, points, swiping, ... }
  let barItems = [];       // what each suggestion cell does when tapped
  let lastWord = null;     // the word just swiped or picked, while it can still be swapped

  function typeable(el) {
    if (!el || el.nodeType !== 1 || el.disabled || el.readOnly) return false;
    if (el.closest(".kiosk-kb")) return false;
    if (el.tagName === "TEXTAREA") return true;
    if (el.tagName === "INPUT") return TYPED_INPUTS.has((el.getAttribute("type") || "").toLowerCase());
    return el.isContentEditable;
  }

  // Suggestions and swipes are for words - not codes, addresses, paths or the sign-in screen.
  function predicts(el) {
    if (!el || el.isContentEditable || el.closest("#login-gate")) return false;
    if (el.getAttribute("spellcheck") === "false" || el.dataset.kioskPredict === "off") return false;
    if (NO_PREDICT.has((el.getAttribute("autocomplete") || "").toLowerCase())) return false;
    if (el.tagName === "TEXTAREA") return true;
    return ["", "text", "search"].includes((el.getAttribute("type") || "").toLowerCase());
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
    kb.addEventListener("pointermove", onMove);
    kb.addEventListener("mousedown", (e) => e.preventDefault());
    for (const type of ["pointerup", "pointercancel", "pointerleave"]) kb.addEventListener(type, onKeyUp);
    kb.addEventListener("contextmenu", (e) => e.preventDefault());
    bar = document.createElement("div");
    bar.className = "kiosk-kb-bar";
    for (let i = 0; i < 3; i++) {
      const cell = document.createElement("div");
      cell.className = "kiosk-kb-sugg";
      cell.dataset.index = String(i);
      bar.appendChild(cell);
    }
    keysEl = document.createElement("div");
    keysEl.className = "kiosk-kb-keys";
    trail = document.createElement("canvas");
    trail.className = "kiosk-kb-trail";
    kb.append(bar, keysEl, trail);
    document.body.appendChild(kb);
    render();
    loadWords();
  }

  function loadWords() {
    if (!window.KioskWords) return;
    fetch("kiosk-words.txt")
      .then((r) => (r.ok ? r.text() : Promise.reject(new Error("HTTP " + r.status))))
      .then((text) => {
        let learned = null;
        try { learned = JSON.parse(localStorage.getItem(LEARNED_KEY) || "null"); } catch { /* none saved */ }
        words = window.KioskWords.create(text, learned);
        refreshBar();
      })
      .catch(() => { /* no suggestions; typing works the same */ });
  }

  function learn(word) {
    if (!words || !word) return;
    const saved = words.learn(word);
    if (!saved) return;
    try { localStorage.setItem(LEARNED_KEY, JSON.stringify(saved)); } catch { /* not kept */ }
  }

  function render() {
    keysEl.textContent = "";
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
      keysEl.appendChild(rowEl);
    }
  }

  function show(el) {
    if (!kb) build();
    const moved = target !== el;
    target = el;
    if (moved) {
      lastWord = null;
      predicting = predicts(el);
      bar.hidden = !predicting;
    }
    const root = document.documentElement;
    if (kb.classList.contains("open")) {
      // Same keyboard, another field: the suggestion row may have come or gone.
      root.style.setProperty("--kiosk-kb-h", kb.offsetHeight + "px");
      refreshBar();
      return;
    }
    layout = "abc";
    shift = "off";
    render();
    refreshBar();
    kb.classList.add("open");
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
    endPress(true);
    target = null;
    lastWord = null;
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
    // Right after a swipe, backspace takes off the whole word - it was one gesture.
    if (wordStillThere()) {
      const { start, end } = lastWord;
      lastWord = null;
      el.setSelectionRange(start, end);
      if (document.execCommand("delete")) return;
    }
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

  // ---- suggestions and swipes ----

  // The field's text up to the caret, or null for a field without one.
  function beforeCaret() {
    const el = target;
    if (!el || typeof el.value !== "string" || el.selectionStart == null) return null;
    return el.value.slice(0, el.selectionStart);
  }

  // The word being typed: the letters (and apostrophes) just before the caret.
  function wordBeforeCaret() {
    const text = beforeCaret();
    if (text == null) return "";
    return (text.match(/[A-Za-z']*$/)[0] || "").replace(/^'+/, "");
  }

  // Replaces the `count` characters before the caret with `text`, as a real edit.
  function replaceBefore(count, text) {
    const el = target;
    const at = el.selectionStart;
    el.setSelectionRange(at - count, at);
    insert(text);
  }

  // The swiped or picked word is still sitting just before the caret, untouched.
  function wordStillThere() {
    const el = target;
    return !!(lastWord && el && el.value === lastWord.value && el.selectionStart === lastWord.end);
  }

  // Remembers the word just finished (the space, Enter or punctuation after it was typed).
  function finishWord() {
    if (predicting) learn(wordBeforeCaret());
    lastWord = null;
  }

  function cased(word) {
    if (shift === "lock") return word.toUpperCase();
    if (shift === "once") return word[0].toUpperCase() + word.slice(1);
    return word;
  }

  function refreshBar() {
    if (!bar) return;
    barItems = [];
    if (predicting && words && target) {
      if (wordStillThere()) {
        // A swiped word: the other guesses either side of it, a tap swaps it.
        const { word, others } = lastWord;
        const swap = (alt) => ({ text: alt, run: () => pick(alt, word.length, "") });
        barItems = [others[0] ? swap(others[0]) : null, { text: word, best: true, run: () => {} }, others[1] ? swap(others[1]) : null];
      } else {
        const typed = wordBeforeCaret();
        const found = typed ? words.complete(typed, 3) : [];
        const finish = (w) => ({ text: w, run: () => { learn(w); replaceBefore(typed.length, w + " "); lastWord = null; } });
        // Best guess in the middle, like a phone keyboard.
        barItems = [found[1], found[0], found[2]].map((w, i) => (w ? { ...finish(w), best: i === 1 } : null));
      }
    }
    bar.querySelectorAll(".kiosk-kb-sugg").forEach((cell, i) => {
      const item = barItems[i];
      cell.textContent = item ? item.text : "";
      cell.classList.toggle("kiosk-kb-best", !!(item && item.best));
    });
  }

  // Swaps the swiped word for another guess.
  function pick(word, length, after) {
    const others = [lastWord.word, ...lastWord.others.filter((w) => w !== word)];
    learn(word);
    replaceBefore(length, word + after);
    remember(word, others);
  }

  function remember(word, others) {
    const el = target;
    lastWord = { word, others, value: el.value, start: el.selectionStart - word.length, end: el.selectionStart };
  }

  // Letter key centres, relative to the keyboard, for the swipe guesser.
  function keyGeometry(origin) {
    const keys = {};
    let width = 0;
    for (const k of keysEl.querySelectorAll(".kiosk-kb-key")) {
      const key = k.dataset.key;
      if (!/^[a-z]$/.test(key)) continue;
      const r = k.getBoundingClientRect();
      keys[key] = { x: r.left + r.width / 2 - origin.left, y: r.top + r.height / 2 - origin.top };
      if (key === "q") width = r.width;
    }
    return { keys, width };
  }

  function startTrail() {
    const ratio = window.devicePixelRatio || 1;
    trail.width = Math.round(kb.clientWidth * ratio);
    trail.height = Math.round(kb.clientHeight * ratio);
    trail.classList.remove("fade");
    const ctx = trail.getContext("2d");
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.lineWidth = 7;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.strokeStyle = getComputedStyle(kb).getPropertyValue("--green").trim() || "#3ddc84";
    drawTrail();
  }

  // The whole line each time: one stroke, so its joins don't show.
  function drawTrail() {
    const ctx = trail.getContext("2d");
    const p = press.points;
    ctx.clearRect(0, 0, trail.width, trail.height);
    ctx.beginPath();
    ctx.moveTo(p[0].x, p[0].y);
    for (const q of p) ctx.lineTo(q.x, q.y);
    ctx.stroke();
  }

  function fadeTrail() {
    trail.classList.add("fade");
  }

  // A letter goes down: wait to see whether it's a tap or the start of a swipe.
  function startPress(e, keyEl) {
    const origin = kb.getBoundingClientRect();
    press = {
      id: e.pointerId,
      key: keyEl.dataset.key,
      keyEl,
      origin,
      points: [{ x: e.clientX - origin.left, y: e.clientY - origin.top }],
      swiping: false,
      keyWidth: keyEl.getBoundingClientRect().width,
    };
    try { kb.setPointerCapture(e.pointerId); } catch { /* still works without */ }
  }

  function onMove(e) {
    if (!press || e.pointerId !== press.id) return;
    const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [];
    let began = false;
    for (const ev of events.length ? events : [e]) {
      const p = { x: ev.clientX - press.origin.left, y: ev.clientY - press.origin.top };
      press.points.push(p);
      if (!press.swiping && words && Math.hypot(p.x - press.points[0].x, p.y - press.points[0].y) > SWIPE_START * press.keyWidth) {
        press.swiping = true;
        began = true;
        press.keyEl.classList.remove("kiosk-kb-down");
      }
    }
    if (began) startTrail();
    else if (press.swiping) drawTrail();
  }

  // The finger lifted (or the press was cut short): type the letter, or the swiped word.
  function endPress(cancelled) {
    const p = press;
    if (!p) return;
    press = null;
    p.keyEl.classList.remove("kiosk-kb-down");
    if (p.swiping) {
      fadeTrail();
      if (!cancelled) typeSwipe(p);
    } else if (!cancelled) {
      typeKey(p.key);
    }
  }

  function typeSwipe(p) {
    if (!target || !words) return;
    const geo = keyGeometry(p.origin);
    if (!geo.width) return;
    words.setKeys(geo.keys, geo.width);
    const guesses = words.decode(p.points, 3).map((g) => cased(g.word));
    if (!guesses.length) return;
    // A space between this word and the one before it.
    const text = beforeCaret();
    const space = text && !/[\s(\[{"\/-]$/.test(text) ? " " : "";
    if (space && predicting) learn(wordBeforeCaret());
    insert(space + guesses[0]);
    remember(guesses[0], guesses.slice(1));
    if (shift === "once") { shift = "off"; render(); }
    refreshBar();
  }

  // Types a character key, as a tap.
  function typeKey(key) {
    if (/^[^A-Za-z']$/.test(key)) finishWord();
    else lastWord = null;
    insert(shift !== "off" && layout === "abc" ? key.toUpperCase() : key);
    if (shift === "once") { shift = "off"; render(); }
    refreshBar();
  }

  function stopRepeat() {
    clearTimeout(repeat);
    clearInterval(repeat);
    repeat = null;
  }

  function onKeyDown(e) {
    e.preventDefault();
    // A second finger landing finishes the first one's press as a tap (fast two-thumb typing).
    if (press && press.id !== e.pointerId) {
      if (press.swiping) return;
      endPress(false);
    }
    const sugg = e.target.closest(".kiosk-kb-sugg");
    if (sugg) {
      const item = barItems[Number(sugg.dataset.index)];
      if (item && target) {
        item.run();
        refreshBar();
      }
      return;
    }
    const keyEl = e.target.closest(".kiosk-kb-key");
    if (!keyEl) return;
    keyEl.classList.add("kiosk-kb-down");
    const key = keyEl.dataset.key;
    if (!key.startsWith("{") && predicting && layout === "abc") {
      startPress(e, keyEl);
      return;
    }
    switch (key) {
      case "{bksp}":
        backspace();
        refreshBar();
        stopRepeat();
        repeat = setTimeout(() => { repeat = setInterval(() => { backspace(); refreshBar(); }, REPEAT_EVERY); }, REPEAT_DELAY);
        return;
      case "{enter}": finishWord(); enter(); refreshBar(); return;
      case "{space}": finishWord(); insert(" "); refreshBar(); return;
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
    typeKey(key);
  }

  function onKeyUp(e) {
    if (press && e.pointerId === press.id && e.type !== "pointerleave") endPress(e.type === "pointercancel");
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
