// Kiosk navigation.
//
// On the wall kiosk (html.kiosk) the sixteen-tab desktop header is the wrong shape: it wraps onto
// two rows, takes a quarter of a 1280x800 screen on every tab, and puts setup screens (cameras,
// automations, accounts...) next to the things people actually walk up to the wall for. Here the
// same #tabs nav becomes one row of big icon buttons along the bottom - the everyday tabs - and a
// "more" button opens a sheet holding the rest, plus sign out and the way out of kiosk mode. No
// tab is lost, and nothing here runs outside kiosk mode: the CSS only applies under html.kiosk.
//
// Also kiosk-only: after a few minutes untouched the wall goes back to Home, so whoever walks up
// next finds the glanceable page rather than whatever the last person left open (and the Pi stops
// decoding camera streams nobody is watching). The Events filters fold behind one button.
(function () {
  const PRIMARY = ["home", "live", "events", "organizer", "weather", "alarm", "music"];
  const IDLE_HOME_MS = 3 * 60 * 1000;

  const ICONS = {
    home: '<path d="M3 11l9-7 9 7"/><path d="M5 10v10h14V10"/><path d="M10 20v-6h4v6"/>',
    live: '<rect x="3" y="6" width="13" height="12" rx="2"/><path d="M16 10l5-3v10l-5-3"/>',
    events: '<path d="M4 6h16M4 12h16M4 18h10"/>',
    organizer: '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>',
    weather: '<path d="M7 18a4 4 0 010-8 5 5 0 019.6-1.5A3.5 3.5 0 0117 18z"/>',
    alarm: '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
    music: '<path d="M9 18V5l11-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="17" cy="16" r="3"/>',
    more: '<circle cx="5" cy="12" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="19" cy="12" r="1.6"/>'
  };

  const LABELS = { organizer: "calendar" };

  const isKiosk = () => document.documentElement.classList.contains("kiosk");
  const nav = document.getElementById("tabs");
  const tabs = () => Array.from(nav.querySelectorAll(".tab[data-view]"));

  function svg(name) {
    return `<svg class="kiosk-tab-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name]}</svg>`;
  }

  // Icons and short labels go in alongside the existing text, which stays for the desktop layout.
  for (const tab of tabs()) {
    const view = tab.dataset.view;
    if (!PRIMARY.includes(view)) {
      tab.classList.add("kiosk-secondary");
      continue;
    }
    tab.classList.add("kiosk-primary");
    tab.style.setProperty("--kiosk-order", String(PRIMARY.indexOf(view)));
    tab.insertAdjacentHTML("afterbegin", svg(view));
    tab.insertAdjacentHTML("beforeend", `<span class="kiosk-tab-label">${LABELS[view] || view}</span>`);
  }

  // Not a .tab: app.js treats every #tabs .tab as a view switch.
  const moreBtn = document.createElement("button");
  moreBtn.type = "button";
  moreBtn.className = "kiosk-more-btn";
  moreBtn.innerHTML = `${svg("more")}<span class="kiosk-tab-label">more</span>`;
  nav.appendChild(moreBtn);

  const sheet = document.createElement("div");
  sheet.className = "kiosk-more hidden";
  sheet.innerHTML = `<div class="kiosk-more-panel" role="dialog" aria-label="more">
      <div class="kiosk-more-grid" id="kiosk-more-grid"></div>
      <div class="kiosk-more-foot">
        <button type="button" class="btn" id="kiosk-more-exit">leave kiosk mode</button>
        <button type="button" class="btn" id="kiosk-more-logout">sign out</button>
      </div>
    </div>`;
  document.body.appendChild(sheet);

  function openMore() {
    const grid = sheet.querySelector("#kiosk-more-grid");
    grid.innerHTML = "";
    for (const tab of tabs()) {
      if (!tab.classList.contains("kiosk-secondary") || tab.classList.contains("hidden")) continue;
      const item = document.createElement("button");
      item.type = "button";
      item.className = `kiosk-more-item${tab.classList.contains("active") ? " active" : ""}`;
      item.textContent = tab.textContent.trim();
      item.addEventListener("click", () => {
        closeMore();
        tab.click();
      });
      grid.appendChild(item);
    }
    // Sign out is only there when the dashboard has a login.
    sheet.querySelector("#kiosk-more-logout").classList.toggle("hidden", document.getElementById("logout-btn").classList.contains("hidden"));
    sheet.classList.remove("hidden");
    moreBtn.classList.add("open");
  }

  function closeMore() {
    sheet.classList.add("hidden");
    moreBtn.classList.remove("open");
  }

  moreBtn.addEventListener("click", () => (sheet.classList.contains("hidden") ? openMore() : closeMore()));
  sheet.addEventListener("click", (e) => { if (e.target === sheet) closeMore(); });
  sheet.querySelector("#kiosk-more-logout").addEventListener("click", () => {
    closeMore();
    document.getElementById("logout-btn").click();
  });
  sheet.querySelector("#kiosk-more-exit").addEventListener("click", () => {
    closeMore();
    try { localStorage.setItem("sentinelcore.kiosk", "false"); } catch { /* storage blocked */ }
    document.documentElement.classList.remove("kiosk");
    // Home only exists in kiosk mode, so don't leave the page sitting on it.
    if (currentView() === "home") document.querySelector('#tabs .tab[data-view="events"]').click();
  });

  // "more" lights up while one of its tabs is the open one, so the bar always shows where you are.
  function syncMore() {
    const active = nav.querySelector(".tab.active");
    moreBtn.classList.toggle("active", !!active && active.classList.contains("kiosk-secondary"));
  }
  new MutationObserver(syncMore).observe(nav, { subtree: true, attributes: true, attributeFilter: ["class"] });

  function currentView() {
    return nav.querySelector(".tab.active")?.dataset.view;
  }

  function goHome() {
    document.querySelector('#tabs .tab[data-view="home"]')?.click();
  }

  // Land on Home when the kiosk loads - unless the address asked for a particular tab.
  window.addEventListener("DOMContentLoaded", () => {
    if (isKiosk() && !location.hash.replace(/^#/, "")) goHome();
  });

  // Turning kiosk mode on from Settings is a fine moment to see what it looks like.
  new MutationObserver(() => {
    if (!isKiosk()) closeMore();
  }).observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });

  // ---- back to Home when nobody is using it ----------------------------------------------------

  let lastInput = Date.now();
  for (const type of ["pointerdown", "keydown", "wheel", "touchstart"]) {
    document.addEventListener(type, () => { lastInput = Date.now(); }, { capture: true, passive: true });
  }

  function busy() {
    if (document.fullscreenElement) return true;
    const focused = document.activeElement;
    if (focused && focused.matches?.("input, textarea, select, [contenteditable]")) return true;
    // An open dialog, lightbox or Now Playing is somebody's work in progress.
    // Every dialog here is a .lightbox or some *-modal (the alarm's, Movies', Music's, Files'...),
    // so anything of either kind that is actually on screen counts.
    for (const el of document.querySelectorAll('.lightbox, [class*="modal"], .au-np, dialog[open]')) {
      if (el.getClientRects().length) return true;
    }
    // A film playing on the Movies tab is being watched, not abandoned.
    for (const video of document.querySelectorAll("#view-movies video")) {
      if (!video.paused) return true;
    }
    return false;
  }

  setInterval(() => {
    if (!isKiosk() || currentView() === "home") return;
    if (Date.now() - lastInput < IDLE_HOME_MS || busy()) return;
    closeMore();
    goHome();
  }, 15000);

  // ---- Events: filters behind one button -------------------------------------------------------

  const filters = document.getElementById("feed-filters");
  const ask = document.getElementById("feed-ask");
  if (filters && ask) {
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "btn kiosk-filters-toggle";
    toggle.textContent = "filters";
    toggle.addEventListener("click", () => {
      const open = filters.classList.toggle("kiosk-open");
      toggle.classList.toggle("btn-on", open);
    });
    ask.appendChild(toggle);
  }
})();
