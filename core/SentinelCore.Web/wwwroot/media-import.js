// "Import a list" for Music and Movies: pick a CSV/JSON exported from Spotify, Letterboxd, IMDb
// and the like, check what was found, and the server works through it in the background
// (search -> best release -> download queue). Shared by music.js and movies.js:
//   MediaImport.open(kind)            opens the import dialog ("music" | "movies")
//   MediaImport.mount(kind, element)  fills element with the "Imported lists" cards and keeps
//                                     them current while the element is on screen
// Contract: core/docs/MOVIE_DOWNLOADS.md "Import a list".
(function () {
  "use strict";

  const BASE = { music: "/api/music/downloads", movies: "/api/media/downloads" };
  const WORDS = {
    music: { thing: "album", things: "albums", tab: "Music" },
    movies: { thing: "movie", things: "movies", tab: "Movies" }
  };
  const STATE = {
    waiting: "waiting", searching: "searching…", queued: "queued", downloading: "downloading", seeding: "sharing back",
    importing: "adding to library", done: "in the library", inLibrary: "already had it", notFound: "not found",
    failed: "failed", canceled: "left out"
  };
  const SHOWN_AT_ONCE = 400;

  const state = { music: { jobs: null, timer: null, el: null }, movies: { jobs: null, timer: null, el: null } };
  const dialog = { kind: "music", stage: "pick", files: [], preview: null, checked: new Set(), filter: "", name: "", busy: false, error: "", detail: null, detailFilter: "all", detailTimer: null };

  const h = (value) => String(value == null ? "" : value).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const plural = (n, word, words) => `${n} ${n === 1 ? word : (words || word + "s")}`;

  async function request(url, options = {}) {
    const init = { method: options.method || "GET", headers: {} };
    if (options.body !== undefined) {
      init.body = JSON.stringify(options.body);
      init.headers["Content-Type"] = "application/json";
    }
    let response;
    try { response = await fetch(url, init); } catch { return { ok: false, data: null, error: "Could not reach the server." }; }
    let data = null;
    const text = await response.text().catch(() => "");
    if (text) { try { data = JSON.parse(text); } catch { data = null; } }
    return { ok: response.ok, status: response.status, data, error: response.ok ? null : (data && data.error) || `The server answered ${response.status}.` };
  }

  let toastTimer;
  function toast(message, success) {
    const t = document.getElementById("toast");
    if (!t) return;
    clearTimeout(toastTimer);
    t.textContent = message;
    t.className = "toast " + (success ? "success" : "error");
    toastTimer = setTimeout(() => { t.className = "toast hidden"; }, 3500);
  }

  // ---- the dialog -------------------------------------------------------------------------------

  function modal() {
    let root = document.getElementById("mi-modal");
    if (root) return root;
    root = document.createElement("div");
    root.id = "mi-modal";
    root.className = "lightbox hidden";
    root.innerHTML = `<div class="zone-editor-panel mv-modal-panel mi-panel" id="mi-panel"></div>`;
    document.body.appendChild(root);
    root.addEventListener("click", (e) => {
      if (e.target === root) { close(); return; }
      const target = e.target.closest("[data-mi]");
      if (target) action(target.dataset.mi, target);
    });
    root.addEventListener("change", (e) => {
      if (e.target.id === "mi-file") readFiles(e.target.files);
      else if (e.target.dataset.miItem !== undefined) {
        const key = e.target.dataset.miItem;
        if (e.target.checked) dialog.checked.add(key); else dialog.checked.delete(key);
        updateStartButton();
      }
    });
    root.addEventListener("input", (e) => {
      if (e.target.id === "mi-filter") { dialog.filter = e.target.value; drawList(); }
      else if (e.target.id === "mi-name") dialog.name = e.target.value;
    });
    root.addEventListener("dragover", (e) => { if (dialog.stage === "pick") { e.preventDefault(); root.querySelector(".mi-drop")?.classList.add("mi-drop-over"); } });
    root.addEventListener("dragleave", () => root.querySelector(".mi-drop")?.classList.remove("mi-drop-over"));
    root.addEventListener("drop", (e) => {
      if (dialog.stage !== "pick") return;
      e.preventDefault();
      readFiles(e.dataTransfer.files);
    });
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !root.classList.contains("hidden")) close();
    });
    return root;
  }

  function close() {
    const root = document.getElementById("mi-modal");
    if (!root || root.classList.contains("hidden")) return;
    root.classList.add("hidden");
    document.getElementById("mi-panel").innerHTML = "";
    clearTimeout(dialog.detailTimer);
    dialog.detail = null;
  }

  function head(title) {
    return `<div class="mv-modal-head"><h3>${h(title)}</h3>
      <button type="button" class="mv-icon-btn" data-mi="close" aria-label="Close">✕</button></div>`;
  }

  function open(kind) {
    Object.assign(dialog, { kind, stage: "pick", files: [], preview: null, checked: new Set(), filter: "", name: "", busy: false, error: "", detail: null });
    modal().classList.remove("hidden");
    draw();
  }

  function draw() {
    const panel = document.getElementById("mi-panel");
    if (!panel) return;
    const w = WORDS[dialog.kind];
    if (dialog.stage === "detail") { drawDetail(); return; }
    if (dialog.stage === "pick") {
      const help = dialog.kind === "music"
        ? `<ul class="mi-help">
             <li><b>Spotify, quickest:</b> open <a href="https://exportify.net" target="_blank" rel="noopener">exportify.net</a>, sign in with Spotify, and press <i>Export</i> next to Liked Songs or any playlist. Drop the .csv here.</li>
             <li><b>Spotify, everything:</b> Spotify → Account → Privacy settings → <i>Download your data</i> (takes a few days). Drop <code>YourLibrary.json</code> and the <code>Playlist*.json</code> files here.</li>
             <li>Also reads TuneMyMusic and Soundiiz exports, or any spreadsheet saved as CSV with <i>Artist</i>, <i>Album</i> and <i>Track</i> columns.</li>
           </ul>
           <p class="mv-modal-intro">Torrent sites share whole albums, not songs, so songs are grouped into the albums they come from and each album is downloaded once.</p>`
        : `<ul class="mi-help">
             <li><b>Letterboxd:</b> Settings → Data → <i>Export your data</i>; drop <code>watchlist.csv</code> (or <code>watched.csv</code>) here.</li>
             <li><b>IMDb:</b> open your Watchlist or a list → ⋯ → <i>Export</i>.</li>
             <li>Also reads Trakt JSON, or any CSV with <i>Title</i> and <i>Year</i> columns. TV shows are left out: add those with <b>add</b> so you can pick seasons.</li>
           </ul>`;
      panel.innerHTML = `${head(`Import a list of ${w.things}`)}${help}
        <label class="mi-drop" for="mi-file">
          <span class="mi-drop-big">${dialog.busy ? "Reading…" : "Drop files here or click to choose"}</span>
          <span class="mi-drop-small">.csv or .json · several files at once is fine</span>
          <input type="file" id="mi-file" accept=".csv,.json,.txt,.tsv,text/csv,application/json" multiple hidden>
        </label>
        ${dialog.error ? `<div class="mv-empty mv-error">${h(dialog.error)}</div>` : ""}`;
      return;
    }

    const p = dialog.preview;
    const items = listItems();
    const have = items.filter((i) => i.inLibrary).length;
    const facts = [];
    if (p.sources && p.sources.length) facts.push(`From ${p.sources.map(h).join(" + ")}`);
    if (dialog.kind === "music") facts.push(`${plural(p.tracks, "song")} → ${plural(items.length, "album")}`);
    else facts.push(plural(items.length, "movie"));
    if (have) facts.push(`${have} already in the library`);
    const notes = [];
    if (p.noAlbum) notes.push(`${plural(p.noAlbum, "song")} had no album, so ${p.noAlbum === 1 ? "it was" : "they were"} left out.`);
    if (p.shows) notes.push(`${plural(p.shows, "TV show")} left out: add ${p.shows === 1 ? "it" : "them"} with <b>add</b> to pick seasons.`);
    if (p.unreadable) notes.push(`${p.unreadable} row${p.unreadable === 1 ? "" : "s"} could not be read (podcast episodes, local files, blank titles).`);
    for (const problem of p.problems || []) notes.push(h(problem));
    const singles = items.filter((i) => i.single && !i.inLibrary).length;
    if (singles) notes.push(`${plural(singles, "single")}: one-song releases are rarely shared on their own, so expect some of these to come back <i>not found</i>.`);

    panel.innerHTML = `${head(`Import a list of ${w.things}`)}
      <div class="mi-facts">${facts.join('<span class="mv-dot">&middot;</span>')}</div>
      ${notes.length ? `<div class="mi-notes">${notes.map((n) => `<div class="mv-dl-note">${n}</div>`).join("")}</div>` : ""}
      ${items.length ? `
        <div class="mi-tools">
          <input class="field-input" id="mi-filter" placeholder="Filter" value="${h(dialog.filter)}" autocomplete="off">
          <button type="button" class="mv-link" data-mi="all">all</button>
          <button type="button" class="mv-link" data-mi="none">none</button>
          <button type="button" class="mv-link" data-mi="missing">only missing</button>
        </div>
        <div class="mi-list" id="mi-list"></div>
        <label class="mv-field mi-name"><span>Name this list</span><input class="field-input" id="mi-name" value="${h(dialog.name)}" maxlength="120"></label>
        <p class="mv-modal-intro">The server searches your sites for each one through the VPN, picks the best release with people sharing it, and keeps a few downloading at a time. You can close this page; it carries on, and the list shows under Downloads.</p>`
        : `<div class="mv-empty">No ${w.things} found in ${dialog.files.length === 1 ? "that file" : "those files"}.</div>`}
      ${dialog.error ? `<div class="mv-empty mv-error">${h(dialog.error)}</div>` : ""}
      <div class="mv-modal-foot">
        <button type="button" class="mv-btn mv-btn-quiet" data-mi="back">choose other files</button>
        ${items.length ? `<button type="button" class="mv-btn mv-btn-primary" data-mi="start" id="mi-start"></button>` : ""}
      </div>`;
    drawList();
    updateStartButton();
  }

  function listItems() {
    const p = dialog.preview || {};
    return dialog.kind === "music"
      ? (p.albums || []).map((a) => ({ ...a, key: `${a.artist}\u0001${a.title}` }))
      : (p.movies || []).map((m) => ({ ...m, key: `${m.title}\u0001${m.year || ""}` }));
  }

  function drawList() {
    const box = document.getElementById("mi-list");
    if (!box) return;
    const q = dialog.filter.trim().toLowerCase();
    const items = listItems().filter((i) => !q || `${i.artist || ""} ${i.title} ${i.year || ""}`.toLowerCase().includes(q));
    const shown = items.slice(0, SHOWN_AT_ONCE);
    box.innerHTML = shown.map((i) => `<label class="mi-row ${i.inLibrary ? "mi-row-have" : ""}">
        <input type="checkbox" data-mi-item="${h(i.key)}" ${dialog.checked.has(i.key) ? "checked" : ""}>
        <span class="mi-row-text">
          <span class="mi-row-title">${h(i.title)}${i.year ? ` <span class="mv-match-year-tag">${h(i.year)}</span>` : ""}${i.inLibrary ? ' <span class="mv-lib-kind mv-have">in library</span>' : ""}${i.single ? ' <span class="mv-lib-kind">single</span>' : ""}</span>
          ${dialog.kind === "music" ? `<span class="mi-row-sub">${h(i.artist)}${i.trackCount ? ` · ${plural(i.trackCount, "song")} on your list` : ""}</span>` : ""}
        </span>
      </label>`).join("") + (items.length > shown.length ? `<div class="mi-more">…and ${items.length - shown.length} more (all still included). Filter to find one.</div>` : "");
  }

  function updateStartButton() {
    const button = document.getElementById("mi-start");
    if (!button) return;
    const n = dialog.checked.size;
    const w = WORDS[dialog.kind];
    button.disabled = n === 0 || dialog.busy;
    button.textContent = dialog.busy ? "Starting…" : `download ${plural(n, w.thing, w.things)}`;
  }

  async function readFiles(fileList) {
    const files = Array.from(fileList || []);
    if (!files.length) return;
    dialog.busy = true;
    dialog.error = "";
    draw();
    try {
      dialog.files = await Promise.all(files.map(async (f) => ({ name: f.name, text: await f.text() })));
    } catch {
      dialog.busy = false;
      dialog.error = "That file could not be read.";
      draw();
      return;
    }
    const res = await request(`${BASE[dialog.kind]}/import/preview`, { method: "POST", body: { files: dialog.files } });
    dialog.busy = false;
    if (!res.ok) { dialog.error = res.error; draw(); return; }
    dialog.preview = res.data;
    dialog.stage = "preview";
    dialog.name = files.length === 1 ? files[0].name.replace(/\.(csv|json|txt|tsv)$/i, "").replace(/[_]+/g, " ") : `${WORDS[dialog.kind].tab} import`;
    dialog.checked = new Set(listItems().filter((i) => !i.inLibrary).map((i) => i.key));
    draw();
  }

  async function start() {
    const chosen = listItems().filter((i) => dialog.checked.has(i.key));
    if (!chosen.length) return;
    dialog.busy = true;
    dialog.error = "";
    updateStartButton();
    const res = await request(`${BASE[dialog.kind]}/imports`, {
      method: "POST",
      body: { name: dialog.name, items: chosen.map((i) => ({ artist: i.artist || null, title: i.title, year: i.year || null, trackCount: i.trackCount || 0 })) }
    });
    dialog.busy = false;
    if (!res.ok) { dialog.error = res.error; draw(); return; }
    const kind = dialog.kind;
    close();
    toast(`Working through ${plural(chosen.length, WORDS[kind].thing, WORDS[kind].things)}. Watch it under Downloads.`, true);
    refresh(kind);
    window.dispatchEvent(new CustomEvent("media-import-started", { detail: { kind } }));
  }

  // ---- one list's details -------------------------------------------------------------------------

  async function openDetail(kind, id) {
    Object.assign(dialog, { kind, stage: "detail", detail: { id, job: null }, detailFilter: "all" });
    modal().classList.remove("hidden");
    await loadDetail();
  }

  async function loadDetail() {
    clearTimeout(dialog.detailTimer);
    if (dialog.stage !== "detail" || !dialog.detail) return;
    const id = dialog.detail.id;
    const res = await request(`${BASE[dialog.kind]}/imports/${encodeURIComponent(id)}`);
    if (dialog.stage !== "detail" || !dialog.detail || dialog.detail.id !== id) return;
    if (!res.ok) { dialog.detail.error = res.error; drawDetail(); return; }
    dialog.detail.job = res.data;
    drawDetail();
    if (!res.data.finished) dialog.detailTimer = setTimeout(loadDetail, 4000);
  }

  const FILTERS = [
    ["all", "all", () => true],
    ["active", "in progress", (s) => ["waiting", "searching", "queued", "downloading", "seeding", "importing"].includes(s)],
    ["done", "done", (s) => s === "done" || s === "inLibrary"],
    ["missing", "not found", (s) => s === "notFound" || s === "failed"]
  ];

  function drawDetail() {
    const panel = document.getElementById("mi-panel");
    if (!panel || !dialog.detail) return;
    const job = dialog.detail.job;
    if (!job) { panel.innerHTML = `${head("Imported list")}${dialog.detail.error ? `<div class="mv-empty mv-error">${h(dialog.detail.error)}</div>` : '<div class="mv-empty"><span class="mv-spinner"></span></div>'}`; return; }
    const filter = FILTERS.find((f) => f[0] === dialog.detailFilter) || FILTERS[0];
    const items = job.items.filter((i) => filter[2](i.state));
    const list = document.getElementById("mi-detail-list");
    const scroll = list ? list.scrollTop : 0;
    panel.innerHTML = `${head(job.name)}
      ${summary(job)}
      <div class="mi-tools">${FILTERS.map((f) => `<button type="button" class="mv-btn ${f[0] === filter[0] ? "mv-btn-primary" : "mv-btn-quiet"}" data-mi="filter" data-f="${f[0]}">${f[1]}</button>`).join("")}</div>
      <div class="mi-list" id="mi-detail-list">${items.slice(0, SHOWN_AT_ONCE * 2).map((i) => `<div class="mi-row mi-row-${h(i.state)}">
          <span class="mi-row-text">
            <span class="mi-row-title">${h(i.title)}${i.year ? ` <span class="mv-match-year-tag">${h(i.year)}</span>` : ""}</span>
            ${i.artist ? `<span class="mi-row-sub">${h(i.artist)}</span>` : ""}
            ${i.releaseTitle ? `<span class="mi-row-sub" title="${h(i.releaseTitle)}">${h(i.releaseTitle)}</span>` : ""}
            ${i.message ? `<span class="mi-row-sub mi-row-msg">${h(i.message)}</span>` : ""}
          </span>
          <span class="mi-row-state">${h(i.state === "downloading" && i.progress != null ? `${Math.round(i.progress * 100)}%` : STATE[i.state] || i.state)}</span>
          ${["waiting", "notFound", "failed"].includes(i.state) ? `<button type="button" class="mv-icon-btn" data-mi="skip" data-item="${h(i.id)}" title="Leave this one out" aria-label="Leave this one out">✕</button>` : ""}
        </div>`).join("") || '<div class="mv-empty">Nothing here.</div>'}</div>
      <div class="mv-modal-foot">${jobButtons(job, true)}</div>`;
    const again = document.getElementById("mi-detail-list");
    if (again) again.scrollTop = scroll;
  }

  // ---- the cards under Downloads ----------------------------------------------------------------

  function summary(job) {
    const c = job.counts;
    const settled = c.done + c.inLibrary + c.notFound + c.failed + c.canceled;
    const pct = c.total ? Math.round((settled / c.total) * 100) : 100;
    const bits = [];
    if (c.done) bits.push(`${c.done} downloaded`);
    if (c.downloading) bits.push(`${c.downloading} downloading`);
    if (c.waiting) bits.push(`${c.waiting} to search`);
    if (c.inLibrary) bits.push(`${c.inLibrary} already had`);
    if (c.notFound) bits.push(`${c.notFound} not found`);
    if (c.failed) bits.push(`${c.failed} failed`);
    const status = job.paused ? "paused" : job.finished ? "finished" : "working";
    return `<div class="mi-sum">
        <div class="mv-dl-facts"><span class="mv-lib-kind ${status === "working" ? "mv-have" : ""}">${status}</span> ${bits.map(h).join('<span class="mv-dot">&middot;</span>')}</div>
        <div class="mv-dl-bar"><span style="width:${pct}%"></span></div>
        ${job.message && !job.paused ? `<div class="mv-dl-note">${h(job.message)}</div>` : ""}
      </div>`;
  }

  function jobButtons(job, inDialog) {
    const b = [];
    if (!inDialog) b.push(`<button type="button" class="mv-btn" data-mi-card="detail" data-id="${h(job.id)}">details</button>`);
    const attr = inDialog ? "data-mi" : "data-mi-card";
    if (!job.finished || job.paused) b.push(`<button type="button" class="mv-btn mv-btn-quiet" ${attr}="pause" data-id="${h(job.id)}" data-paused="${job.paused ? "0" : "1"}">${job.paused ? "resume" : "pause"}</button>`);
    if (job.counts.notFound + job.counts.failed > 0) b.push(`<button type="button" class="mv-btn mv-btn-quiet" ${attr}="retry" data-id="${h(job.id)}">search missing again</button>`);
    b.push(`<button type="button" class="mv-btn mv-btn-quiet" ${attr}="remove" data-id="${h(job.id)}">remove list</button>`);
    return b.join("");
  }

  function drawCards(kind) {
    const s = state[kind];
    if (!s.el || !s.el.isConnected) return;
    const jobs = s.jobs || [];
    s.el.innerHTML = `<div class="mv-dl-head"><h3 class="mv-section-title">Imported lists</h3>
        <button type="button" class="mv-btn" data-mi-card="import">import a list</button></div>
      ${jobs.length ? `<div class="mv-dl-list">${jobs.map((job) => `<div class="mv-dl mi-card">
          <div class="mv-dl-body">
            <div class="mv-dl-title">${h(job.name)} <span class="mv-match-year-tag">${plural(job.counts.total, WORDS[kind].thing, WORDS[kind].things)}</span></div>
            ${summary(job)}
          </div>
          <div class="mv-lib-actions">${jobButtons(job, false)}</div>
        </div>`).join("")}</div>`
        : `<div class="mi-empty-line">Got a ${kind === "movies" ? "Letterboxd or IMDb" : "Spotify"} list? <b>Import a list</b> downloads everything on it.</div>`}`;
  }

  async function refresh(kind) {
    const s = state[kind];
    clearTimeout(s.timer);
    const res = await request(`${BASE[kind]}/imports`);
    if (res.ok && res.data) s.jobs = res.data.imports || [];
    drawCards(kind);
    if (s.el && s.el.isConnected && !document.hidden) {
      const busy = (s.jobs || []).some((j) => !j.finished && !j.paused);
      s.timer = setTimeout(() => refresh(kind), busy ? 5000 : 30000);
    }
  }

  function mount(kind, element) {
    const s = state[kind];
    if (!element) return;
    if (s.el !== element) {
      s.el = element;
      element.addEventListener("click", (e) => {
        const target = e.target.closest("[data-mi-card]");
        if (target) { e.stopPropagation(); cardAction(kind, target.dataset.miCard, target); }
      });
    }
    if (s.jobs) drawCards(kind);
    refresh(kind);
  }

  async function change(kind, act, id, target) {
    let res;
    if (act === "pause") res = await request(`${BASE[kind]}/imports/${encodeURIComponent(id)}/pause`, { method: "POST", body: { paused: target.dataset.paused === "1" } });
    else if (act === "retry") res = await request(`${BASE[kind]}/imports/${encodeURIComponent(id)}/retry`, { method: "POST" });
    else if (act === "remove") {
      if (!confirm("Remove this list? Nothing more on it is searched for. Downloads it already started carry on under Downloads.")) return false;
      res = await request(`${BASE[kind]}/imports/${encodeURIComponent(id)}`, { method: "DELETE" });
    }
    if (res && !res.ok) { toast(res.error, false); return false; }
    refresh(kind);
    return true;
  }

  function cardAction(kind, act, target) {
    if (act === "import") open(kind);
    else if (act === "detail") openDetail(kind, target.dataset.id);
    else change(kind, act, target.dataset.id, target);
  }

  async function action(act, target) {
    switch (act) {
      case "close": close(); break;
      case "back": Object.assign(dialog, { stage: "pick", preview: null, error: "" }); draw(); break;
      case "start": start(); break;
      case "all": case "none": case "missing": {
        const items = listItems();
        dialog.checked = new Set(act === "none" ? [] : items.filter((i) => act === "all" || !i.inLibrary).map((i) => i.key));
        drawList();
        updateStartButton();
        break;
      }
      case "filter": dialog.detailFilter = target.dataset.f; drawDetail(); break;
      case "skip": {
        const job = dialog.detail && dialog.detail.job;
        if (!job) break;
        const res = await request(`${BASE[dialog.kind]}/imports/${encodeURIComponent(job.id)}/items/${encodeURIComponent(target.dataset.item)}`, { method: "DELETE" });
        if (!res.ok) toast(res.error, false); else { dialog.detail.job = res.data; drawDetail(); refresh(dialog.kind); }
        break;
      }
      case "pause": case "retry": case "remove": {
        const kind = dialog.kind;
        if (await change(kind, act, target.dataset.id, target)) {
          if (act === "remove") close(); else loadDetail();
        }
        break;
      }
      default: break;
    }
  }

  window.MediaImport = { open, mount, refresh };
})();
