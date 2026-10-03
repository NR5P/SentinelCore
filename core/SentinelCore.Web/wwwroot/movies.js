// Movies tab: the family media library (see core/docs/MOVIES.md for the API). Home rows, poster
// grids for movies and shows, a detail page, the full-screen player and - for admins - the
// libraries the catalog is scanned from. The server does all the real work (scanning, TMDB,
// ffmpeg); this file is presentation plus the player's progress reporting.
//
// Routing rides the URL hash so a refresh or the browser's back button lands on the same page:
// #movies, #movies/movies, #movies/shows, #movies/item/<id>, #movies/libraries. Setting the hash
// fires app.js's landOnHash, which clicks the Movies tab, which calls show(), which routes.
window.SentinelMovies = (function () {
  const PAGE_SIZE = 60;
  const STATUS_POLL_MS = 3000;
  const PROGRESS_EVERY_MS = 10000;
  const UP_NEXT_SECONDS = 10;
  const CHROME_IDLE_MS = 3000;
  const HEVC_PROBE = 'video/mp4; codecs="hvc1.1.6.L120.B0"';
  const PANES = ["home", "movies", "shows", "detail", "libraries", "downloads"];
  const SORT_ORDER = { title: "asc", added: "desc", year: "desc", rating: "desc", plays: "desc" };
  const PHASES = { files: "finding files", metadata: "fetching details", probing: "reading file info", idle: "idle" };

  const ICON = {
    play: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5.2v13.6L19 12z" fill="currentColor"/></svg>',
    check: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7.5" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    info: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="2"/><path d="M12 11v6M12 7.5v.5" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/></svg>',
    back: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M15 5l-7 7 7 7" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    left: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M15 5l-7 7 7 7" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    right: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M9 5l7 7-7 7" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    restart: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M4.5 12a7.5 7.5 0 1 0 2.2-5.3M4.5 4.5v4h4" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    folder: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v7.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/></svg>',
    computer: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><rect x="3.5" y="4.5" width="17" height="11" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M9 19.5h6M12 15.5v4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
    plus: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/></svg>',
    close: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/></svg>',
    search: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6.5" fill="none" stroke="currentColor" stroke-width="2"/><path d="M16 16l4.5 4.5" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/></svg>',
    film: '<svg class="mv-ico" viewBox="0 0 24 24" aria-hidden="true"><rect x="3.5" y="4.5" width="17" height="15" rx="2" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M7.5 4.5v15M16.5 4.5v15M3.5 9h4M3.5 15h4M16.5 9h4M16.5 15h4" stroke="currentColor" stroke-width="1.6"/></svg>'
  };

  const el = (id) => document.getElementById(id);

  let visible = false;
  let isAdmin = false;
  let adminCheck = null;
  let status = null;
  let statusTimer = null;
  let wasScanning = false;
  let route = { name: "home" };
  let lastList = "home";
  let routeToken = 0;
  let homeData = null;
  let detail = null;
  let detailSeason = null;
  let flashEpisodeId = null;
  let searchTimer = null;
  const scrollMemory = {};
  const browse = {
    movies: { kind: "movie", noun: "movies", q: "", genre: "", sort: "title", unwatched: false, list: false, offset: 0, total: 0, genres: [], built: false },
    shows: { kind: "show", noun: "shows", q: "", genre: "", sort: "title", unwatched: false, list: false, offset: 0, total: 0, genres: [], built: false }
  };

  // ---- small helpers --------------------------------------------------------------------------

  // Everything TMDB hands us (titles, overviews, cast) and every file or folder name is text from
  // outside, and half the interpolations below are into attributes - so quotes are escaped too.
  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // Artwork is our own /api/media/... route or a TMDB https URL (the fix-match search). Anything
  // else - a javascript: URL out of a poisoned metadata field - is simply not rendered.
  function safeSrc(url) {
    const text = String(url || "");
    return /^(\/(?!\/)|https:\/\/)/i.test(text) ? text : null;
  }

  let toastTimeout;
  function showToast(message, success) {
    const toast = el("toast");
    clearTimeout(toastTimeout);
    toast.textContent = message;
    toast.className = "toast " + (success ? "success" : "error");
    toastTimeout = setTimeout(() => { toast.className = "toast hidden"; }, 3500);
  }

  function errorFrom(data, statusCode) {
    if (data && data.errors) {
      const list = Array.isArray(data.errors) ? data.errors : Object.values(data.errors).flat();
      if (list.length) return list.join(" ");
    }
    if (data && data.error) return data.error;
    return statusCode ? `The server answered ${statusCode}.` : "Could not reach the server.";
  }

  // fetch + JSON with the error already worded, so every caller can just show res.error.
  async function request(url, options = {}) {
    const init = { method: options.method || "GET", headers: {} };
    if (options.body !== undefined) {
      init.body = JSON.stringify(options.body);
      init.headers["Content-Type"] = "application/json";
    }
    if (options.keepalive) init.keepalive = true;
    let response;
    try {
      response = await fetch(url, init);
    } catch {
      return { ok: false, status: 0, data: null, error: "Could not reach the server." };
    }
    let data = null;
    const text = await response.text().catch(() => "");
    if (text) { try { data = JSON.parse(text); } catch { data = null; } }
    return { ok: response.ok, status: response.status, data, error: response.ok ? null : errorFrom(data, response.status) };
  }

  const enc = encodeURIComponent;

  function fmtRuntime(minutes) {
    const m = Math.round(Number(minutes) || 0);
    if (m <= 0) return "";
    const h = Math.floor(m / 60);
    return h ? `${h}h ${String(m % 60).padStart(2, "0")}m` : `${m}m`;
  }

  function fmtClock(seconds) {
    const s = Math.max(0, Math.floor(Number(seconds) || 0));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = String(s % 60).padStart(2, "0");
    return h ? `${h}:${String(m).padStart(2, "0")}:${sec}` : `${m}:${sec}`;
  }

  // "2009-04-10" is a calendar date, not an instant - parsed as local so it never shows the day before.
  function fmtDate(value) {
    if (!value) return "";
    const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(value));
    const date = m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : new Date(value);
    if (isNaN(date)) return "";
    return date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
  }

  function ago(iso) {
    if (!iso) return "never";
    const seconds = (Date.now() - new Date(iso).getTime()) / 1000;
    if (!isFinite(seconds)) return "";
    if (seconds < 60) return "just now";
    if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
    if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`;
    if (seconds < 86400 * 7) return `${Math.round(seconds / 86400)} d ago`;
    return fmtDate(iso);
  }

  function plural(n, word) { return `${n} ${word}${n === 1 ? "" : "s"}`; }

  function episodeCode(item) {
    const parts = [];
    if (item.seasonNumber != null) parts.push(`S${item.seasonNumber}`);
    if (item.episodeNumber != null) parts.push(`E${item.episodeNumber}`);
    return parts.join(" · ");
  }

  function episodeLabel(item) {
    const code = episodeCode(item);
    return code ? `${code} · ${item.title || ""}` : (item.title || "");
  }

  function runtimeOf(item) {
    return item.runtimeMinutes || (item.durationSeconds ? item.durationSeconds / 60 : 0);
  }

  function inProgress(item) {
    const p = item && item.progress;
    return !!(p && !p.watched && p.positionSeconds > 0);
  }

  // The play count is everyone's, so it only goes on the detail page, not the hero or the cards.
  function metaLine(item, withPlays) {
    const parts = [];
    if (item.year) parts.push(escapeHtml(item.year));
    if (item.kind === "show") {
      if (item.seasonCount) parts.push(plural(item.seasonCount, "season"));
    } else if (runtimeOf(item)) {
      parts.push(fmtRuntime(runtimeOf(item)));
    }
    if (Number(item.rating) > 0) parts.push(`<span class="mv-star">&#9733;</span> ${Number(item.rating).toFixed(1)}`);
    if (item.certification) parts.push(`<span class="mv-cert">${escapeHtml(item.certification)}</span>`);
    const plays = Number(item.playCount) || 0;
    if (withPlays && plays > 0) parts.push(plays === 1 ? "Played once" : `Played ${plays} times`);
    return parts.join('<span class="mv-dot">&middot;</span>');
  }

  function resolutionLabel(width, height) {
    const w = Number(width) || 0;
    const h = Number(height) || 0;
    if (!w && !h) return "";
    if (w >= 3200 || h >= 2000) return "4K";
    if (w >= 1800 || h >= 1000) return "1080p";
    if (w >= 1200 || h >= 700) return "720p";
    return h ? `${h}p` : "SD";
  }

  const CODEC_NAMES = { h264: "H.264", avc: "H.264", hevc: "HEVC", h265: "HEVC", av1: "AV1", vp9: "VP9", vp8: "VP8", mpeg4: "MPEG-4", mpeg2video: "MPEG-2", vc1: "VC-1", aac: "AAC", ac3: "AC3", eac3: "EAC3", dts: "DTS", truehd: "TrueHD", mp3: "MP3", opus: "Opus", flac: "FLAC", vorbis: "Vorbis" };
  function codecName(codec) {
    if (!codec) return "";
    return CODEC_NAMES[String(codec).toLowerCase()] || String(codec).toUpperCase();
  }

  function channelsLabel(channels) {
    const c = Number(channels) || 0;
    if (!c) return "";
    if (c === 1) return "Mono";
    if (c === 2) return "Stereo";
    if (c === 6) return "5.1";
    if (c === 8) return "7.1";
    return `${c}ch`;
  }

  function sizeLabel(bytes) {
    const b = Number(bytes) || 0;
    if (!b) return "";
    if (b >= 1e9) return `${(b / 1e9).toFixed(1)} GB`;
    return `${Math.max(1, Math.round(b / 1e6))} MB`;
  }

  // "1080p · HEVC · EAC3 5.1 · 1.4 GB"
  function fileLine(file) {
    if (!file) return "";
    const audio = [codecName(file.audioCodec), channelsLabel(file.audioChannels)].filter(Boolean).join(" ");
    return [resolutionLabel(file.width, file.height), codecName(file.videoCodec), audio, sizeLabel(file.sizeBytes)]
      .filter(Boolean).map(escapeHtml).join('<span class="mv-dot">&middot;</span>');
  }

  // ---- artwork --------------------------------------------------------------------------------

  // The text card is always drawn under the image; an image that fails (a 404 from the artwork
  // route, a TMDB outage) is removed and the card shows through. Never a broken-image icon.
  function art(url, cls, title, sub, extra = "") {
    const src = safeSrc(url);
    return `<div class="mv-art ${cls}">
      <div class="mv-art-fallback"><span class="mv-art-glyph">${ICON.film}</span><span class="mv-art-title">${escapeHtml(title)}</span>${sub ? `<span class="mv-art-sub">${escapeHtml(sub)}</span>` : ""}</div>
      ${src ? `<img src="${escapeHtml(src)}" alt="" loading="lazy" decoding="async">` : ""}
      ${extra}
    </div>`;
  }

  // Called straight after every innerHTML, before an error event can have been dispatched.
  function wireImages(root) {
    for (const img of root.querySelectorAll("img:not([data-mv-wired])")) {
      img.dataset.mvWired = "1";
      img.addEventListener("load", () => img.classList.add("mv-loaded"), { once: true });
      img.addEventListener("error", () => img.remove(), { once: true });
    }
  }

  function progressBar(percent) {
    const p = Math.max(0, Math.min(100, Number(percent) || 0));
    return `<div class="mv-progress"><span style="width:${p.toFixed(1)}%"></span></div>`;
  }

  function watchedBadge() {
    return `<span class="mv-badge mv-badge-watched" title="Watched">${ICON.check}</span>`;
  }

  // ---- cards ----------------------------------------------------------------------------------

  function posterCard(item) {
    let extra = "";
    if (item.kind === "show") {
      const total = item.episodeCount || 0;
      const left = total - (item.watchedEpisodeCount || 0);
      if (total && left <= 0) extra += watchedBadge();
      else if (left > 0) extra += `<span class="mv-badge mv-badge-count" title="${plural(left, "unwatched episode")}">${left}</span>`;
    } else if (item.progress && item.progress.watched) {
      extra += watchedBadge();
    }
    if (inProgress(item)) extra += progressBar(item.progress.percent);
    const sub = item.kind === "show" && item.seasonCount ? plural(item.seasonCount, "season") : (item.year || "");
    return `<button type="button" class="mv-card" data-act="open" data-id="${escapeHtml(item.id)}" title="${escapeHtml(item.title)}">
      ${art(item.posterUrl, "mv-poster", item.title, item.year, extra)}
      <span class="mv-card-title">${escapeHtml(item.title)}</span>
      <span class="mv-card-sub">${escapeHtml(sub)}</span>
    </button>`;
  }

  // Landscape card for Continue Watching and New Episodes: the art plays, the title opens details.
  // In Continue Watching an episode with no progress is the "next up" of a show you're following;
  // in New Episodes the same state means nothing, so only that row's caller asks for the chip.
  function continueCard(item) { return wideCard(item, true); }
  function episodeCard(item) { return wideCard(item, false); }

  function wideCard(item, nextUpChip) {
    const isEpisode = item.kind === "episode";
    const image = isEpisode ? (item.thumbUrl || item.backdropUrl || item.posterUrl) : (item.backdropUrl || item.posterUrl);
    const heading = isEpisode ? (item.showTitle || item.title) : item.title;
    let sub;
    if (isEpisode) sub = episodeLabel(item);
    else if (inProgress(item)) sub = `${fmtRuntime(Math.max(1, (item.progress.durationSeconds - item.progress.positionSeconds) / 60))} left`;
    else sub = [item.year, fmtRuntime(runtimeOf(item))].filter(Boolean).join(" · ");
    let extra = `<span class="mv-wide-play">${ICON.play}</span>`;
    if (inProgress(item)) extra += progressBar(item.progress.percent);
    else if (item.progress && item.progress.watched) extra += watchedBadge();
    else if (isEpisode && !item.progress && nextUpChip) extra += `<span class="mv-chip-new">next up</span>`;
    const openId = isEpisode && item.showId ? item.showId : item.id;
    return `<div class="mv-wide">
      <button type="button" class="mv-wide-art-btn" data-act="play" data-id="${escapeHtml(item.id)}" title="Play ${escapeHtml(heading)}">
        ${art(image, "mv-wide-art", heading, isEpisode ? episodeCode(item) : item.year, extra)}
      </button>
      <button type="button" class="mv-wide-text" data-act="open" data-id="${escapeHtml(openId)}" data-episode="${isEpisode ? escapeHtml(item.id) : ""}">
        <span class="mv-card-title">${escapeHtml(heading)}</span>
        <span class="mv-card-sub">${escapeHtml(sub)}</span>
      </button>
    </div>`;
  }

  function rowMarkup(key, title, items, card) {
    if (!items || !items.length) return "";
    return `<section class="mv-row" data-row="${escapeHtml(key)}">
      <h3 class="mv-row-title">${title}</h3>
      <div class="mv-row-wrap">
        <button type="button" class="mv-row-arrow mv-prev" data-act="scroll" data-dir="-1" aria-label="Scroll left">${ICON.left}</button>
        <div class="mv-row-scroller">${items.map(card).join("")}</div>
        <button type="button" class="mv-row-arrow mv-next" data-act="scroll" data-dir="1" aria-label="Scroll right">${ICON.right}</button>
      </div>
    </section>`;
  }

  function updateRowArrows(scroller) {
    const wrap = scroller.parentElement;
    const max = scroller.scrollWidth - scroller.clientWidth - 2;
    wrap.querySelector(".mv-prev").disabled = scroller.scrollLeft <= 2;
    wrap.querySelector(".mv-next").disabled = scroller.scrollLeft >= max;
  }

  function wireRows(root, restore) {
    for (const scroller of root.querySelectorAll(".mv-row-scroller")) {
      const key = scroller.closest(".mv-row").dataset.row;
      if (restore && restore[key]) scroller.scrollLeft = restore[key];
      scroller.addEventListener("scroll", () => updateRowArrows(scroller), { passive: true });
      updateRowArrows(scroller);
    }
  }

  function loadingMarkup(text = "loading") {
    return `<div class="mv-empty"><span class="mv-spinner"></span><span>${escapeHtml(text)}&hellip;</span></div>`;
  }

  function errorMarkup(message) {
    return `<div class="mv-empty mv-error"><div class="mv-empty-title">Couldn't load this</div><div>${escapeHtml(message)}</div>
      <button type="button" class="mv-btn" data-act="retry">try again</button></div>`;
  }

  // ---- routing --------------------------------------------------------------------------------

  function parseHash() {
    const hash = location.hash.replace(/^#/, "");
    if (hash !== "movies" && !hash.startsWith("movies/")) return null;
    const parts = hash.split("/").slice(1);
    switch (parts[0]) {
      case "movies": return { name: "movies" };
      case "shows": return { name: "shows" };
      case "libraries": return { name: "libraries" };
      case "downloads": return { name: "downloads" };
      case "item": return parts[1] ? { name: "detail", id: decodeURIComponent(parts[1]) } : { name: "home" };
      default: return { name: "home" };
    }
  }

  function hashFor(r) {
    if (r.name === "detail") return `#movies/item/${enc(r.id)}`;
    if (r.name === "home") return "#movies";
    return `#movies/${r.name}`;
  }

  function go(r) {
    const hash = hashFor(r);
    if (location.hash === hash) { routeTo(r); return; }
    // hashchange -> landOnHash -> the Movies tab's click -> show() -> routeTo. Going through the
    // hash (rather than routing here and then writing it) keeps back/forward working for free.
    location.hash = hash;
  }

  function goBack() {
    go({ name: lastList });
  }

  function rememberScroll() {
    if (route && (route.name === "home" || route.name === "movies" || route.name === "shows")) {
      scrollMemory[route.name] = window.scrollY;
    }
  }

  async function routeTo(r) {
    const token = ++routeToken;
    const previous = route;
    if (previous.name !== r.name || previous.id !== r.id) rememberScroll();
    route = r;
    if (r.name === "home" || r.name === "movies" || r.name === "shows") lastList = r.name;
    for (const p of PANES) el(`mv-pane-${p}`).classList.toggle("hidden", p !== r.name);
    el("mv-surprise-btn").classList.toggle("hidden", r.name === "libraries" || r.name === "downloads");
    for (const button of document.querySelectorAll("#mv-subtabs [data-mv-pane]")) {
      button.classList.toggle("active", button.dataset.mvPane === (r.name === "detail" ? lastList : r.name));
    }
    el("mv-libraries-btn").classList.toggle("btn-on", r.name === "libraries");
    await ensureAdmin();
    if (token !== routeToken) return;
    if (r.name === "home") await renderHome(token);
    else if (r.name === "movies" || r.name === "shows") await renderBrowse(r.name, token);
    else if (r.name === "detail") await renderDetail(r.id, token, previous.name !== "detail" || previous.id !== r.id);
    else if (r.name === "libraries") renderLibraries();
    else if (r.name === "downloads") await renderDownloads(token);
  }

  function restoreScroll(name) {
    const y = scrollMemory[name];
    if (y != null) window.scrollTo(0, y);
  }

  function refreshCurrent() {
    if (!visible) return;
    rememberScroll();
    const r = route;
    routeTo(r).then(() => restoreScroll(r.name));
  }

  // ---- admin + status -------------------------------------------------------------------------

  // The library and metadata controls are admin-only on the server; asking once is enough to
  // hide them from a Viewer rather than let every button end in a 403.
  function ensureAdmin() {
    if (!adminCheck) {
      adminCheck = request("/api/auth/status").then((res) => {
        isAdmin = !!(res.ok && res.data && (!res.data.enabled || res.data.role === "Admin"));
        el("mv-libraries-btn").classList.toggle("hidden", !isAdmin);
      });
    }
    return adminCheck;
  }

  async function refreshStatus() {
    clearTimeout(statusTimer);
    const res = await request("/api/media/status");
    if (res.ok && res.data) {
      status = res.data;
      const finished = wasScanning && !status.scanning;
      wasScanning = !!status.scanning;
      renderScanState();
      if (route.name === "libraries" && visible) updateLibrariesLive();
      if (finished && visible) {
        showToast("Library scan finished", true);
        if (route.name !== "libraries") refreshCurrent();
      }
    }
    // Only polled while a scan runs and the tab is open: an idle library changes only when
    // someone adds a folder, and that path starts the poll itself.
    if (visible && status && status.scanning) statusTimer = setTimeout(refreshStatus, STATUS_POLL_MS);
  }

  function scanText() {
    if (!status || !status.scanning) return "";
    const s = status.scan;
    if (!s) return "scanning…";
    const count = s.total ? ` ${s.processed}/${s.total}` : "";
    return `scanning ${s.libraryName || ""} · ${PHASES[s.phase] || s.phase || ""}${count}`;
  }

  function renderScanState() {
    const span = el("mv-scan-state");
    const text = scanText();
    span.classList.toggle("hidden", !text);
    span.innerHTML = text ? `<span class="mv-pulse"></span>${escapeHtml(text)}` : "";
  }

  function noticesMarkup() {
    if (!status || !isAdmin) return "";
    const out = [];
    if (status.tmdbConfigured === false) {
      out.push(`<div class="mv-note">No TMDB key yet, so titles come from file names and there is no artwork or description.
        Add a free key in <button type="button" class="mv-link" data-act="open-settings">Settings &rarr; MOVIES &amp; TV</button>, then rescan.</div>`);
    }
    if (status.ffmpegAvailable === false) {
      out.push(`<div class="mv-note mv-note-warn">ffmpeg is not installed on the server. MP4 files the browser can decode still play,
        but MKV, AVI and anything that needs converting will not, and file details stay blank. Install ffmpeg and restart the server.</div>`);
    }
    return out.join("");
  }

  function openSettings() {
    const tab = document.querySelector('#tabs .tab[data-view="settings"]');
    if (!tab) return;
    tab.click();
    // The Settings sections are built from the server's schema after the tab first opens, so
    // look for ours for a moment rather than once.
    let tries = 0;
    const find = setInterval(() => {
      tries++;
      const title = Array.from(document.querySelectorAll(".settings-section-title")).find((t) => /movies/i.test(t.textContent));
      if (title || tries > 20) clearInterval(find);
      if (!title) return;
      const section = title.closest(".settings-section");
      if (section && !section.classList.contains("open")) title.closest(".settings-section-header").click();
      title.scrollIntoView({ behavior: "smooth", block: "start" });
    }, 150);
  }

  // ---- home -----------------------------------------------------------------------------------

  async function renderHome(token) {
    const pane = el("mv-pane-home");
    const rowScroll = {};
    for (const s of pane.querySelectorAll(".mv-row-scroller")) rowScroll[s.closest(".mv-row").dataset.row] = s.scrollLeft;
    if (!homeData) pane.innerHTML = loadingMarkup();
    const res = await request("/api/media/home");
    if (token !== routeToken) return;
    if (!res.ok) { pane.innerHTML = errorMarkup(res.error); return; }
    homeData = res.data || {};
    if (!homeData.libraries || homeData.libraries.length === 0) {
      pane.innerHTML = setupMarkup();
      wireImages(pane);
      return;
    }
    // Everything after New episodes is missing from an older server, which rowMarkup treats as empty.
    const shelves = (homeData.genreShelves || []).filter((shelf) => shelf && shelf.genre);
    const rows = [
      rowMarkup("continue", "Continue watching", homeData.continueWatching, continueCard),
      rowMarkup("mylist", "My List", homeData.myList, posterCard),
      rowMarkup("movies", "Recently added movies", homeData.recentMovies, posterCard),
      rowMarkup("shows", "Recently added shows", homeData.recentShows, posterCard),
      rowMarkup("episodes", "New episodes", homeData.recentEpisodes, episodeCard),
      rowMarkup("mostplayed", "Most played", homeData.mostPlayed, posterCard),
      rowMarkup("toprated", "Top rated", homeData.topRated, posterCard),
      ...shelves.map((shelf) => rowMarkup(`genre-${shelf.genre}`, escapeHtml(shelf.genre), shelf.items, posterCard)),
      rowMarkup("again", "Watch again", homeData.recentlyWatched, posterCard)
    ].join("");
    const hero = homeData.featured ? heroMarkup(homeData.featured) : "";
    const empty = rows ? "" : `<div class="mv-empty"><div class="mv-empty-title">Nothing here yet</div>
      <div>${status && status.scanning ? "The first scan is still running - titles appear as it finds them." : "The libraries are set up but no videos were found in them yet."}</div></div>`;
    pane.innerHTML = noticesMarkup() + hero + rows + empty;
    wireImages(pane);
    wireRows(pane, rowScroll);
    if (homeData.featured) fillHeroDetails(homeData.featured.id, token);
    drawDownloadBanner();
  }

  function playTarget(item) {
    return item.kind === "show" ? item.nextEpisodeId : item.id;
  }

  function heroMarkup(item) {
    const target = playTarget(item);
    const resume = inProgress(item);
    const playLabel = resume ? `Resume ${fmtClock(item.progress.positionSeconds)}` : (item.kind === "show" && (item.watchedEpisodeCount || 0) > 0 ? "Continue" : "Play");
    const genres = (item.genres || []).slice(0, 3).map((g) => `<span class="mv-genre">${escapeHtml(g)}</span>`).join("");
    const src = safeSrc(item.backdropUrl);
    return `<section class="mv-hero">
      <div class="mv-hero-art">${src ? `<img src="${escapeHtml(src)}" alt="">` : ""}</div>
      <div class="mv-hero-shade"></div>
      <div class="mv-hero-body">
        <div class="mv-kicker">Featured ${item.kind === "show" ? "series" : "film"}</div>
        <h2 class="mv-hero-title">${escapeHtml(item.title)}</h2>
        <div class="mv-meta">${metaLine(item)}</div>
        ${genres ? `<div class="mv-genres">${genres}</div>` : ""}
        <p class="mv-hero-overview" id="mv-hero-overview"></p>
        <div class="mv-actions">
          ${target ? `<button type="button" class="mv-btn mv-btn-primary" data-act="play" data-id="${escapeHtml(target)}">${ICON.play}<span>${playLabel}</span></button>` : ""}
          <button type="button" class="mv-btn mv-btn-glass" data-act="open" data-id="${escapeHtml(item.id)}">${ICON.info}<span>Details</span></button>
        </div>
        ${resume ? `<div class="mv-hero-progress">${progressBar(item.progress.percent)}</div>` : ""}
      </div>
    </section>`;
  }

  // The home payload's summaries carry no overview, so the hero asks for the one detail it shows.
  async function fillHeroDetails(id, token) {
    const res = await request(`/api/media/items/${enc(id)}`);
    if (token !== routeToken || !res.ok || !res.data) return;
    const target = el("mv-hero-overview");
    if (!target) return;
    target.textContent = res.data.overview || res.data.tagline || "";
  }

  // ---- first-run setup ------------------------------------------------------------------------

  const setup = { name: "Movies", kind: "movies", path: "" };

  function setupMarkup() {
    if (!isAdmin) {
      return `<div class="mv-setup"><div class="mv-setup-glyph">${ICON.film}</div>
        <h2>No libraries yet</h2><p>An admin adds the folders of movies and TV shows - on the server or shared from another computer - in the apps under Settings → Movies &amp; TV Libraries, or here, and they show up for everyone.</p></div>`;
    }
    return `<div class="mv-setup">
      <div class="mv-setup-glyph">${ICON.film}</div>
      <h2>Add your first library</h2>
      <p>Point Sentinel Core at a folder of movies or TV shows on the server. It finds the videos, fetches artwork and
        descriptions from TMDB, and plays them here and in every app.</p>
      ${noticesMarkup()}
      <div class="mv-form">
        <label class="mv-field"><span>Name</span><input class="field-input" id="mv-setup-name" value="${escapeHtml(setup.name)}" maxlength="80"></label>
        <div class="mv-field"><span>Kind</span>
          <div class="mv-seg" role="radiogroup">
            <button type="button" class="${setup.kind === "movies" ? "active" : ""}" data-act="setup-kind" data-kind="movies" role="radio" aria-checked="${setup.kind === "movies"}">Movies</button>
            <button type="button" class="${setup.kind === "shows" ? "active" : ""}" data-act="setup-kind" data-kind="shows" role="radio" aria-checked="${setup.kind === "shows"}">TV Shows</button>
          </div>
        </div>
        <label class="mv-field"><span>Folder (on the server or another computer)</span>
          <div class="mv-path-row"><input class="field-input" id="mv-setup-path" placeholder="/mnt/movies or smb://PC/Movies" value="${escapeHtml(setup.path)}" spellcheck="false">
          <button type="button" class="mv-btn" data-act="setup-browse">${ICON.folder}<span>browse</span></button></div>
        </label>
        <div id="mv-setup-hint">${mountHint(setup.path)}</div>
        <ul class="field-errors" id="mv-setup-errors"></ul>
        <div class="mv-actions"><button type="button" class="mv-btn mv-btn-primary" data-act="setup-add">Add library</button></div>
      </div>
    </div>`;
  }

  function readSetup() {
    const name = el("mv-setup-name");
    const path = el("mv-setup-path");
    if (name) setup.name = name.value;
    if (path) setup.path = path.value;
  }

  async function submitSetup(button) {
    readSetup();
    button.disabled = true;
    const res = await saveLibrary(null, { name: setup.name.trim() || (setup.kind === "shows" ? "TV Shows" : "Movies"), kind: setup.kind, path: setup.path.trim() });
    button.disabled = false;
    if (!res.ok) {
      showErrors(el("mv-setup-errors"), res.error);
      el("mv-setup-hint").innerHTML = mountHint(setup.path, true);
      return;
    }
    setup.path = "";
    showToast("Library added - scanning now", true);
    homeData = null;
    refreshCurrent();
  }

  function showErrors(list, message) {
    if (!list) return;
    list.innerHTML = message ? `<li>${escapeHtml(message)}</li>` : "";
  }

  // A folder on another computer is read over the network by the server itself; nothing has to
  // be mounted. Typed or pasted as \\PC\Share or smb://PC/Share, it gets a line saying so, and a
  // local path that failed gets a pointer to the picker's Network computers section.
  function mountHint(path, afterFailure) {
    const text = String(path || "").trim();
    const share = /^(?:\\\\|\/\/|smb:\/\/)([^\\/]+)/i.exec(text);
    if (share) {
      return `<div class="mv-note">A shared folder on <code>${escapeHtml(share[1])}</code>. The server reads it over the network - if that computer wants a password, press browse and sign in to it under Network computers.</div>`;
    }
    if (afterFailure) {
      return `<div class="mv-note">If the videos are on another computer, press browse and choose it under Network computers, or type its address like <code>smb://PC/Movies</code>.</div>`;
    }
    return "";
  }

  // ---- browse grids ---------------------------------------------------------------------------

  function browseToolbar(key) {
    const s = browse[key];
    return `<div class="mv-filters">
      <label class="mv-search">${ICON.search}<input type="search" class="field-input" data-f="q" placeholder="Search ${s.noun}" value="${escapeHtml(s.q)}" aria-label="Search ${s.noun}"></label>
      <select class="field-input mv-select" data-f="genre" aria-label="Genre"><option value="">All genres</option></select>
      <select class="field-input mv-select" data-f="sort" aria-label="Sort">
        <option value="title">Title</option><option value="added">Recently added</option><option value="year">Year</option><option value="rating">Rating</option><option value="plays">Most played</option>
      </select>
      <button type="button" class="btn mv-toggle ${s.unwatched ? "btn-on" : ""}" data-act="unwatched" aria-pressed="${s.unwatched}">unwatched</button>
      <button type="button" class="btn mv-toggle ${s.list ? "btn-on" : ""}" data-act="mylist" aria-pressed="${s.list}">my list</button>
      <span class="mv-count" data-f="count"></span>
    </div>
    <div class="mv-grid-host"></div>`;
  }

  function buildBrowse(key) {
    const s = browse[key];
    const pane = el(`mv-pane-${key}`);
    pane.innerHTML = browseToolbar(key);
    pane.querySelector('[data-f="sort"]').value = s.sort;
    pane.querySelector('[data-f="q"]').addEventListener("input", (e) => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => { s.q = e.target.value.trim(); s.offset = 0; loadBrowse(key, routeToken); }, 300);
    });
    pane.querySelector('[data-f="genre"]').addEventListener("change", (e) => { s.genre = e.target.value; s.offset = 0; loadBrowse(key, routeToken); });
    pane.querySelector('[data-f="sort"]').addEventListener("change", (e) => { s.sort = e.target.value; s.offset = 0; loadBrowse(key, routeToken); });
    s.built = true;
  }

  async function renderBrowse(key, token) {
    if (!browse[key].built) buildBrowse(key);
    await loadBrowse(key, token);
    restoreScroll(key);
  }

  async function loadBrowse(key, token) {
    const s = browse[key];
    const pane = el(`mv-pane-${key}`);
    const host = pane.querySelector(".mv-grid-host");
    if (!host.firstChild) host.innerHTML = loadingMarkup();
    else host.classList.add("mv-busy");
    const params = new URLSearchParams({ kind: s.kind, sort: s.sort, order: SORT_ORDER[s.sort] || "asc", offset: String(s.offset), limit: String(PAGE_SIZE) });
    if (s.q) params.set("q", s.q);
    if (s.genre) params.set("genre", s.genre);
    if (s.unwatched) params.set("unwatched", "true");
    if (s.list) params.set("list", "true");
    const res = await request(`/api/media/items?${params}`);
    if (token !== routeToken) return;
    host.classList.remove("mv-busy");
    if (!res.ok) { host.innerHTML = errorMarkup(res.error); return; }
    const data = res.data || {};
    const items = data.items || [];
    s.total = data.total || 0;
    if (Array.isArray(data.genres)) {
      s.genres = data.genres;
      const select = pane.querySelector('[data-f="genre"]');
      select.innerHTML = `<option value="">All genres</option>` + s.genres.map((g) => `<option value="${escapeHtml(g)}">${escapeHtml(g)}</option>`).join("");
      select.value = s.genre;
      if (select.value !== s.genre) { s.genre = ""; select.value = ""; }
    }
    pane.querySelector('[data-f="count"]').textContent = s.total ? plural(s.total, key === "movies" ? "movie" : "show") : "";
    if (!items.length) {
      if (offersDownload(s)) { showOffer(key, host, token); return; }
      const filtered = s.q || s.genre || s.unwatched || s.list;
      host.innerHTML = `<div class="mv-empty"><div class="mv-empty-title">${filtered ? `No ${s.noun} match` : `No ${s.noun} yet`}</div>
        <div>${filtered ? "Try a different search or clear the filters." : "Add a library of " + s.noun + " (the gear button up top) and they appear here once scanned."}</div></div>`;
      return;
    }
    const from = s.offset + 1;
    const to = s.offset + items.length;
    const pager = s.total > PAGE_SIZE ? `<div class="mv-pager">
        <button type="button" class="btn" data-act="page" data-dir="-1" ${s.offset <= 0 ? "disabled" : ""}>${ICON.left} prev</button>
        <span>${from}&ndash;${to} of ${s.total}</span>
        <button type="button" class="btn" data-act="page" data-dir="1" ${to >= s.total ? "disabled" : ""}>next ${ICON.right}</button>
      </div>` : "";
    host.innerHTML = `<div class="mv-grid">${items.map(posterCard).join("")}</div>${pager}`;
    wireImages(host);
  }

  // ---- search offers a download ---------------------------------------------------------------

  // A library search that finds nothing offers to download what was typed instead
  // (MOVIE_DOWNLOADS.md, "Search offers a download"). Music gets a row here when it can be downloaded.
  const OFFER_KINDS = { movie: "Movie", show: "TV show" };
  const OFFER_MAX = 6;
  const offer = { q: "", results: null, tmdb: true };

  // Only the search narrowed it to nothing: with a genre or toggle on, the title may well be in
  // the library and merely filtered out.
  function offersDownload(s) {
    return !!(dl.status && dl.status.canDownload) && s.q.length >= 2 && !s.genre && !s.unwatched && !s.list;
  }

  async function showOffer(key, host, token) {
    const q = browse[key].q;
    if (offer.q !== q) {
      Object.assign(offer, { q, results: "loading", tmdb: dl.status.tmdbConfigured !== false });
      if (offer.tmdb) {
        host.innerHTML = offerMarkup();
        const res = await request(`/api/media/downloads/lookup?${new URLSearchParams({ q, kind: "any" })}`);
        if (offer.q !== q) return;
        if (!res.ok) offer.results = res.error;
        else {
          offer.tmdb = !!res.data.tmdbConfigured;
          offer.results = (res.data.results || []).filter((r) => OFFER_KINDS[r.kind]).slice(0, OFFER_MAX);
        }
      } else offer.results = [];
    }
    if (token !== routeToken || browse[key].q !== q) return;
    host.innerHTML = offerMarkup();
    wireImages(host);
    // A failed lookup is asked again next time rather than remembered.
    if (typeof offer.results === "string") offer.q = "";
  }

  function offerMarkup() {
    const q = escapeHtml(offer.q);
    let found = "";
    if (offer.tmdb) {
      if (offer.results === "loading") found = loadingMarkup("Looking it up");
      else if (typeof offer.results === "string") found = `<div class="mv-offer-error">${escapeHtml(offer.results)}</div>`;
      else if (!offer.results.length) found = `<div class="mv-offer-none">Nothing on TMDB called &ldquo;${q}&rdquo;.</div>`;
      else found = `<div class="mv-offer-results">${offer.results.map((r, i) => `<button type="button" class="mv-match" data-act="offer-pick" data-i="${i}">
          ${art(r.posterUrl, "mv-poster mv-match-poster", r.title, "")}
          <span class="mv-match-text">
            <span class="mv-match-title">${escapeHtml(r.title)}${r.year ? ` <span class="mv-match-year-tag">${escapeHtml(r.year)}</span>` : ""}</span>
            <span class="mv-offer-chips"><span class="mv-lib-kind">${OFFER_KINDS[r.kind]}</span>${r.inLibrary ? `<span class="mv-lib-kind mv-have">In library</span>` : ""}</span>
            <span class="mv-match-overview">${escapeHtml(r.overview || "No description.")}</span>
          </span>
        </button>`).join("")}</div>`;
    }
    return `<div class="mv-offer">
      <div class="mv-empty-title">Not in your library</div>
      <div class="mv-offer-ask">Want to download &ldquo;${q}&rdquo;?</div>
      ${found}
      <div class="mv-offer-direct"><span>Search the sites for &ldquo;${q}&rdquo; as a</span>
        ${Object.entries(OFFER_KINDS).map(([kind, label]) => `<button type="button" class="mv-btn" data-act="offer-direct" data-kind="${kind}">${label}</button>`).join("")}
      </div>
    </div>`;
  }

  function pickOffer(i) {
    const r = Array.isArray(offer.results) ? offer.results[i] : null;
    if (!r) return;
    if (r.inLibrary && r.itemId) { go({ name: "detail", id: r.itemId }); return; }
    openAddAt({ kind: r.kind, title: offer.q, pick: r });
  }

  // ---- detail ---------------------------------------------------------------------------------

  async function renderDetail(id, token, fresh) {
    const pane = el("mv-pane-detail");
    if (fresh) {
      detailSeason = null;
      pane.innerHTML = `<button type="button" class="mv-back" data-act="back">${ICON.back}<span>back</span></button>` + loadingMarkup();
      window.scrollTo(0, 0);
    }
    let res = await request(`/api/media/items/${enc(id)}`);
    if (token !== routeToken) return;
    // An episode link opens its show at that season, with the episode picked out.
    if (res.ok && res.data && res.data.kind === "episode" && res.data.showId) {
      detailSeason = res.data.seasonNumber;
      flashEpisodeId = res.data.id;
      const showId = res.data.showId;
      history.replaceState(null, "", hashFor({ name: "detail", id: showId }));
      route = { name: "detail", id: showId };
      res = await request(`/api/media/items/${enc(showId)}`);
      if (token !== routeToken) return;
    }
    if (!res.ok) {
      pane.innerHTML = `<button type="button" class="mv-back" data-act="back">${ICON.back}<span>back</span></button>` + errorMarkup(res.error);
      return;
    }
    detail = res.data;
    drawDetail();
  }

  function reloadDetail() {
    if (route.name !== "detail") return;
    renderDetail(route.id, routeToken, false);
  }

  function findEpisode(show, id) {
    for (const season of show.seasons || []) {
      for (const ep of season.episodes || []) if (ep.id === id) return ep;
    }
    return null;
  }

  function detailActions(item) {
    const out = [];
    if (item.kind === "show") {
      const next = item.nextEpisodeId ? findEpisode(item, item.nextEpisodeId) : null;
      if (item.nextEpisodeId) {
        const code = next ? episodeCode(next) : "";
        const label = next && inProgress(next) ? `Resume ${code}` : `Play ${code}`;
        out.push(`<button type="button" class="mv-btn mv-btn-primary" data-act="play" data-id="${escapeHtml(item.nextEpisodeId)}">${ICON.play}<span>${escapeHtml(label.trim())}</span></button>`);
      }
      const all = item.episodeCount && (item.watchedEpisodeCount || 0) >= item.episodeCount;
      out.push(`<button type="button" class="mv-btn mv-btn-glass" data-act="watched" data-id="${escapeHtml(item.id)}" data-value="${!all}">${ICON.check}<span>${all ? "Mark unwatched" : "Mark all watched"}</span></button>`);
    } else {
      if (inProgress(item)) {
        out.push(`<button type="button" class="mv-btn mv-btn-primary" data-act="play" data-id="${escapeHtml(item.id)}">${ICON.play}<span>Resume ${fmtClock(item.progress.positionSeconds)}</span></button>`);
        out.push(`<button type="button" class="mv-btn mv-btn-glass" data-act="play" data-id="${escapeHtml(item.id)}" data-start="0">${ICON.restart}<span>Start over</span></button>`);
      } else {
        out.push(`<button type="button" class="mv-btn mv-btn-primary" data-act="play" data-id="${escapeHtml(item.id)}">${ICON.play}<span>Play</span></button>`);
      }
      const watched = !!(item.progress && item.progress.watched);
      out.push(`<button type="button" class="mv-btn mv-btn-glass" data-act="watched" data-id="${escapeHtml(item.id)}" data-value="${!watched}">${ICON.check}<span>${watched ? "Mark unwatched" : "Mark watched"}</span></button>`);
    }
    // My List holds movies and shows; an episode page would only ever show it unsaved.
    if (item.kind !== "episode") out.push(listButton(item));
    if (isAdmin) {
      out.push(`<button type="button" class="mv-btn mv-btn-quiet" data-act="fix-match">Fix match&hellip;</button>`);
      out.push(`<button type="button" class="mv-btn mv-btn-quiet" data-act="refresh-meta">Refresh metadata</button>`);
    }
    if (item.canDelete) out.push(`<button type="button" class="mv-btn mv-danger" data-act="delete-item">Delete</button>`);
    return out.join("");
  }

  function listButton(item) {
    const on = !!item.inList;
    return `<button type="button" class="mv-btn mv-btn-glass mv-list-btn ${on ? "on" : ""}" data-act="list" data-id="${escapeHtml(item.id)}" aria-pressed="${on}">${on ? ICON.check : ICON.plus}<span>My List</span></button>`;
  }

  function drawDetail() {
    const item = detail;
    const pane = el("mv-pane-detail");
    const backdrop = safeSrc(item.backdropUrl);
    const genres = (item.genres || []).map((g) => `<span class="mv-genre">${escapeHtml(g)}</span>`).join("");
    const tmdb = /^https:\/\/(www\.)?themoviedb\.org\//i.test(item.tmdbUrl || "") ? item.tmdbUrl : null;
    let progressNote = "";
    if (inProgress(item)) {
      progressNote = `<div class="mv-detail-progress">${progressBar(item.progress.percent)}<span>${fmtClock(item.progress.positionSeconds)} of ${fmtClock(item.progress.durationSeconds)} watched</span></div>`;
    } else if (item.kind === "show" && item.episodeCount) {
      progressNote = `<div class="mv-detail-progress">${progressBar(100 * (item.watchedEpisodeCount || 0) / item.episodeCount)}<span>${item.watchedEpisodeCount || 0} of ${plural(item.episodeCount, "episode")} watched</span></div>`;
    }
    pane.innerHTML = `
      <div class="mv-detail">
        <div class="mv-detail-hero">
          <div class="mv-detail-art">${backdrop ? `<img src="${escapeHtml(backdrop)}" alt="">` : ""}</div>
          <div class="mv-detail-shade"></div>
          <button type="button" class="mv-back" data-act="back">${ICON.back}<span>back</span></button>
          <div class="mv-detail-main">
            <div class="mv-detail-poster">${art(item.posterUrl, "mv-poster", item.title, item.year)}</div>
            <div class="mv-detail-info">
              <h2 class="mv-detail-title">${escapeHtml(item.title)}</h2>
              <div class="mv-meta">${metaLine(item, true)}</div>
              ${genres ? `<div class="mv-genres">${genres}</div>` : ""}
              ${item.tagline ? `<p class="mv-tagline">${escapeHtml(item.tagline)}</p>` : ""}
              ${item.overview ? `<p class="mv-overview">${escapeHtml(item.overview)}</p>` : ""}
              <div class="mv-actions">${detailActions(item)}</div>
              ${progressNote}
            </div>
          </div>
        </div>
        ${item.kind === "show" ? seasonsMarkup(item) : ""}
        ${castMarkup(item.cast)}
        ${item.kind !== "show" ? fileMarkup(item) : ""}
        ${tmdb ? `<div class="mv-attrib">Details and artwork from <a href="${escapeHtml(tmdb)}" target="_blank" rel="noopener noreferrer">TMDB</a>.</div>` : ""}
      </div>`;
    wireImages(pane);
    if (flashEpisodeId) {
      const row = pane.querySelector(`[data-ep="${CSS.escape(flashEpisodeId)}"]`);
      flashEpisodeId = null;
      if (row) {
        row.classList.add("mv-ep-flash");
        setTimeout(() => row.scrollIntoView({ behavior: "smooth", block: "center" }), 80);
      }
    }
  }

  function castMarkup(cast) {
    if (!cast || !cast.length) return "";
    const people = cast.slice(0, 18).map((c) => {
      const initials = String(c.name || "?").split(/\s+/).filter(Boolean).slice(0, 2).map((w) => w[0]).join("").toUpperCase();
      return `<div class="mv-cast"><span class="mv-cast-avatar">${escapeHtml(initials)}</span>
        <span class="mv-cast-text"><span class="mv-cast-name">${escapeHtml(c.name)}</span>${c.character ? `<span class="mv-cast-role">${escapeHtml(c.character)}</span>` : ""}</span></div>`;
    }).join("");
    return `<section class="mv-section"><h3 class="mv-section-title">Cast</h3><div class="mv-cast-grid">${people}</div></section>`;
  }

  function fileMarkup(item) {
    const file = item.file;
    if (!file) return "";
    const rows = [];
    const video = [resolutionLabel(file.width, file.height), codecName(file.videoCodec), file.videoProfile, file.width && file.height ? `${file.width}×${file.height}` : ""].filter(Boolean).join(" · ");
    if (video) rows.push(["Video", escapeHtml(video)]);
    const audio = (item.audioTracks && item.audioTracks.length)
      ? item.audioTracks.map((t) => {
          const label = t.label || t.language || `Track ${t.index + 1}`;
          const channels = channelsLabel(t.channels);
          // The server's label often already says "English 5.1" - don't repeat the 5.1.
          const parts = [label, codecName(t.codec), label.includes(channels) ? "" : channels];
          return escapeHtml(parts.filter(Boolean).join(" · ")) + (t.isDefault ? ' <span class="mv-tag">default</span>' : "");
        }).join("<br>")
      : escapeHtml([codecName(file.audioCodec), channelsLabel(file.audioChannels)].filter(Boolean).join(" "));
    if (audio) rows.push(["Audio", audio]);
    if (item.subtitles && item.subtitles.length) {
      rows.push(["Subtitles", item.subtitles.map((s) => escapeHtml(s.label || s.language || `Track ${s.index + 1}`) + (s.forced ? ' <span class="mv-tag">forced</span>' : "")).join(", ")]);
    }
    const size = [String(file.container || "").toUpperCase(), sizeLabel(file.sizeBytes), file.bitrate ? `${(file.bitrate / 1e6).toFixed(1)} Mb/s` : ""].filter(Boolean).join(" · ");
    if (size) rows.push(["File", escapeHtml(size)]);
    if (file.path) rows.push(["Path", `<code class="mv-path">${escapeHtml(file.path)}</code>`]);
    return `<section class="mv-section"><h3 class="mv-section-title">File <span class="mv-section-sub">${fileLine(file)}</span></h3>
      <dl class="mv-file">${rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join("")}</dl></section>`;
  }

  function seasonsMarkup(show) {
    const seasons = show.seasons || [];
    if (!seasons.length) return `<section class="mv-section"><div class="mv-empty">No episodes found for this show.</div></section>`;
    if (detailSeason == null || !seasons.some((s) => s.number === detailSeason)) {
      const next = show.nextEpisodeId ? findEpisode(show, show.nextEpisodeId) : null;
      detailSeason = next && next.seasonNumber != null ? next.seasonNumber : seasons[0].number;
    }
    const season = seasons.find((s) => s.number === detailSeason);
    const buttons = seasons.length > 1 || seasons[0].name ? seasons.map((s) => {
      const done = s.episodeCount && s.watchedCount >= s.episodeCount;
      return `<button type="button" class="mv-season-btn ${s.number === detailSeason ? "active" : ""}" data-act="season" data-n="${s.number}">
        ${escapeHtml(s.name || `Season ${s.number}`)}${done ? ` <span class="mv-season-done">${ICON.check}</span>` : ` <span class="mv-season-count">${s.watchedCount || 0}/${s.episodeCount || 0}</span>`}</button>`;
    }).join("") : "";
    const allWatched = season.episodeCount && season.watchedCount >= season.episodeCount;
    const episodes = (season.episodes || []).map(episodeRow).join("");
    return `<section class="mv-section">
      <div class="mv-seasons-head">
        <div class="mv-seasons">${buttons}</div>
        <button type="button" class="btn mv-season-mark" data-act="season-watched" data-n="${season.number}" data-value="${!allWatched}">${allWatched ? "mark season unwatched" : "mark season watched"}</button>
      </div>
      <div class="mv-episodes">${episodes || '<div class="mv-empty">No episodes in this season.</div>'}</div>
    </section>`;
  }

  function episodeRow(ep) {
    const watched = !!(ep.progress && ep.progress.watched);
    const resume = inProgress(ep);
    let extra = `<span class="mv-wide-play">${ICON.play}</span>`;
    if (resume) extra += progressBar(ep.progress.percent);
    const meta = [fmtRuntime(runtimeOf(ep)), fmtDate(ep.airDate)].filter(Boolean).join(" · ");
    const number = ep.episodeNumber != null ? `E${ep.episodeNumber}` : "";
    return `<div class="mv-ep ${watched ? "mv-ep-watched" : ""}" data-ep="${escapeHtml(ep.id)}">
      <button type="button" class="mv-ep-thumb" data-act="play" data-id="${escapeHtml(ep.id)}" title="${resume ? "Resume" : "Play"} ${escapeHtml(ep.title)}">
        ${art(ep.thumbUrl, "mv-wide-art mv-ep-art", number ? String(ep.episodeNumber) : ep.title, "", extra)}
      </button>
      <div class="mv-ep-body">
        <div class="mv-ep-title">${number ? `<span class="mv-ep-num">${number}</span> · ` : ""}${escapeHtml(ep.title)}</div>
        <div class="mv-ep-meta">${escapeHtml(meta)}${resume ? ` <span class="mv-ep-left">· ${fmtClock(ep.progress.positionSeconds)} in</span>` : ""}</div>
        ${ep.overview ? `<p class="mv-ep-overview">${escapeHtml(ep.overview)}</p>` : ""}
      </div>
      <div class="mv-ep-side">
        <button type="button" class="mv-ep-check ${watched ? "on" : ""}" data-act="watched" data-id="${escapeHtml(ep.id)}" data-value="${!watched}" title="${watched ? "Mark unwatched" : "Mark watched"}" aria-label="${watched ? "Mark unwatched" : "Mark watched"}">${ICON.check}</button>
      </div>
    </div>`;
  }

  async function setWatched(id, value, season) {
    const url = season != null ? `/api/media/items/${enc(id)}/seasons/${enc(season)}/watched` : `/api/media/items/${enc(id)}/watched`;
    const res = await request(url, { method: "POST", body: { watched: value } });
    if (!res.ok) { showToast(res.error, false); return; }
    homeData = null;
    if (route.name === "detail") reloadDetail();
    else refreshCurrent();
  }

  // Flipped on screen straight away and put back if the server says no. Home is refetched next
  // time it opens either way, so the My List row catches up there.
  async function setInList(button) {
    if (!detail) return;
    const item = detail;
    const value = !item.inList;
    item.inList = value;
    button.outerHTML = listButton(item);
    const res = await request(`/api/media/items/${enc(item.id)}/list`, { method: "POST", body: { inList: value } });
    if (res.ok) { homeData = null; return; }
    item.inList = !value;
    const current = detail === item && el("mv-pane-detail").querySelector('[data-act="list"]');
    if (current) current.outerHTML = listButton(item);
    showToast(res.error, false);
  }

  // Asks the server for one title, within whatever grid the user came from (its kind and genre),
  // and opens it. Nothing matching is a 404 with the reason worded for us.
  async function surprise(button) {
    const s = browse[route.name === "detail" ? lastList : route.name];
    const params = new URLSearchParams();
    if (s) {
      params.set("kind", s.kind);
      if (s.genre) params.set("genre", s.genre);
    }
    button.disabled = true;
    const res = await request(`/api/media/random${params.toString() ? `?${params}` : ""}`);
    button.disabled = false;
    if (!res.ok || !res.data || !res.data.id) { showToast(res.error || "Nothing in the library matches that yet.", false); return; }
    go({ name: "detail", id: res.data.id });
  }

  async function refreshMetadata(button) {
    if (!detail) return;
    button.disabled = true;
    button.classList.add("mv-working");
    const res = await request(`/api/media/items/${enc(detail.id)}/refresh`, { method: "POST", body: {} });
    button.disabled = false;
    button.classList.remove("mv-working");
    if (!res.ok) { showToast(res.error, false); return; }
    detail = res.data || detail;
    drawDetail();
    showToast("Metadata refreshed", true);
  }

  // Deletes the movie's files on the server for everyone, then goes back to a list refetched
  // without it (home is cached, so it is dropped; the grids reload on every visit anyway).
  async function deleteItem(button) {
    if (!detail) return;
    const item = detail;
    if (!confirm(`Delete ${item.title}? The movie's file is deleted from the server's disk for everyone. This can't be undone.`)) return;
    button.disabled = true;
    button.classList.add("mv-working");
    const res = await request(`/api/media/items/${enc(item.id)}`, { method: "DELETE" });
    button.disabled = false;
    button.classList.remove("mv-working");
    if (!res.ok) { showToast(res.error, false); return; }
    homeData = null;
    if (detail === item) detail = null;
    showToast(`${item.title} deleted`, true);
    if (route.name === "detail" && route.id === item.id) goBack();
    else refreshCurrent();
  }

  // ---- modal ----------------------------------------------------------------------------------

  let modalClose = null;

  function openModal(html, onClose) {
    const panel = el("mv-modal-panel");
    panel.innerHTML = html;
    el("mv-modal").classList.remove("hidden");
    modalClose = onClose || null;
    wireImages(panel);
    const focus = panel.querySelector("[autofocus]") || panel.querySelector("input") || panel.querySelector(".mv-modal-foot .mv-btn-primary");
    if (focus) focus.focus();
  }

  function closeModal() {
    if (el("mv-modal").classList.contains("hidden")) return;
    el("mv-modal").classList.add("hidden");
    el("mv-modal-panel").innerHTML = "";
    const done = modalClose;
    modalClose = null;
    if (done) done();
  }

  function modalHeader(title) {
    return `<div class="mv-modal-head"><h3>${escapeHtml(title)}</h3>
      <button type="button" class="mv-icon-btn" data-mact="close" aria-label="Close">${ICON.close}</button></div>`;
  }

  // ---- fix match ------------------------------------------------------------------------------

  function openFixMatch() {
    if (!detail) return;
    const kind = detail.kind === "show" ? "show" : "movie";
    openModal(`${modalHeader("Fix match")}
      <p class="mv-modal-intro">Pick the right ${kind === "show" ? "series" : "film"} on TMDB. The match is locked, so later scans keep it.</p>
      <form class="mv-match-form" id="mv-match-form">
        <input class="field-input" id="mv-match-q" value="${escapeHtml(detail.title)}" placeholder="Title" autofocus>
        <input class="field-input mv-match-year" id="mv-match-year" value="${escapeHtml(detail.year || "")}" placeholder="Year" inputmode="numeric" maxlength="4">
        <button type="submit" class="mv-btn mv-btn-primary">${ICON.search}<span>Search</span></button>
      </form>
      <div class="mv-match-results" id="mv-match-results"></div>
      <div class="mv-modal-foot">
        <button type="button" class="mv-btn mv-btn-quiet" data-mact="unlock" title="Forget the manual match and let the scanner pick again">Unlock &amp; re-match automatically</button>
      </div>`);
    el("mv-match-form").addEventListener("submit", (e) => { e.preventDefault(); searchTmdb(kind); });
    searchTmdb(kind);
  }

  async function searchTmdb(kind) {
    const results = el("mv-match-results");
    if (!results) return;
    const q = el("mv-match-q").value.trim();
    const year = el("mv-match-year").value.trim();
    if (!q) { results.innerHTML = ""; return; }
    results.innerHTML = loadingMarkup("searching");
    const params = new URLSearchParams({ q, kind });
    if (/^\d{4}$/.test(year)) params.set("year", year);
    const res = await request(`/api/media/tmdb/search?${params}`);
    if (!el("mv-match-results")) return;
    if (!res.ok) { results.innerHTML = `<div class="mv-empty mv-error">${escapeHtml(res.error)}</div>`; return; }
    const list = Array.isArray(res.data) ? res.data : [];
    if (!list.length) { results.innerHTML = `<div class="mv-empty">Nothing on TMDB for that. Try fewer words, or no year.</div>`; return; }
    results.innerHTML = list.map((r) => `<button type="button" class="mv-match" data-mact="match" data-tmdb="${escapeHtml(r.tmdbId)}">
        ${art(r.posterUrl, "mv-poster mv-match-poster", r.title, "")}
        <span class="mv-match-text">
          <span class="mv-match-title">${escapeHtml(r.title)}${r.year ? ` <span class="mv-match-year-tag">${escapeHtml(r.year)}</span>` : ""}</span>
          <span class="mv-match-overview">${escapeHtml(r.overview || "No description.")}</span>
          <span class="mv-match-id">TMDB ${escapeHtml(r.tmdbId)}</span>
        </span>
      </button>`).join("");
    wireImages(results);
  }

  async function applyMatch(button, tmdbId) {
    if (!detail) return;
    for (const b of document.querySelectorAll("#mv-match-results .mv-match")) b.disabled = true;
    button.classList.add("mv-working");
    const res = await request(`/api/media/items/${enc(detail.id)}/match`, { method: "POST", body: { tmdbId: Number(tmdbId) } });
    if (!res.ok) {
      for (const b of document.querySelectorAll("#mv-match-results .mv-match")) b.disabled = false;
      button.classList.remove("mv-working");
      showToast(res.error, false);
      return;
    }
    closeModal();
    detail = res.data || detail;
    homeData = null;
    drawDetail();
    showToast("Match updated", true);
  }

  async function unlockMatch(button) {
    if (!detail) return;
    button.disabled = true;
    const res = await request(`/api/media/items/${enc(detail.id)}/refresh`, { method: "POST", body: { unlock: true } });
    button.disabled = false;
    if (!res.ok) { showToast(res.error, false); return; }
    closeModal();
    detail = res.data || detail;
    drawDetail();
    showToast("Unlocked and re-matched", true);
  }

  // ---- folder browser -------------------------------------------------------------------------

  // Folders on the server, and folders shared by other computers on the network (read over SMB
  // by the server itself - nothing to mount). The server's /api/media/browse does the work; a
  // computer that wants a password answers with needsLogin and the sign-in form appears here.
  let folderPick = null;

  function openFolderBrowser(startPath, onPick, onCancel) {
    folderPick = { onPick, onCancel, path: null };
    openModal(`${modalHeader("Choose a folder")}
      <p class="mv-modal-intro">Folders on this server, or shared from another computer on your network. The number beside a folder is how many videos sit directly inside it.</p>
      <div class="mv-browse-bar">
        <button type="button" class="mv-icon-btn" data-mact="up" aria-label="Up one folder" title="Up one folder">${ICON.back}</button>
        <input class="field-input mv-browse-input" id="mv-browse-input" spellcheck="false" autocomplete="off"
          placeholder="/mnt/movies or smb://computer/SharedFolder" aria-label="Folder path">
        <button type="button" class="mv-btn" data-mact="go">go</button>
      </div>
      <div class="mv-folders" id="mv-folders">${loadingMarkup()}</div>
      <div class="mv-modal-foot">
        <button type="button" class="mv-btn mv-btn-quiet mv-foot-start" data-mact="new-folder" id="mv-browse-new" disabled>+ New folder</button>
        <button type="button" class="mv-btn mv-btn-quiet" data-mact="cancel">Cancel</button>
        <button type="button" class="mv-btn mv-btn-primary" data-mact="pick" id="mv-browse-pick" disabled>Use this folder</button>
      </div>`, () => { const f = folderPick; folderPick = null; if (f && f.onCancel && !f.picked) f.onCancel(); });
    el("mv-browse-input").addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); loadFolder(e.target.value.trim()); }
    });
    loadFolder(startPath || "", true);
  }

  function folderButton(f, act) {
    return `<button type="button" class="mv-folder" data-mact="${act || "folder"}" data-path="${escapeHtml(f.path)}">
        ${f.icon || ICON.folder}<span class="mv-folder-name">${escapeHtml(f.name)}</span>
        ${f.videoCount ? `<span class="mv-folder-count">${plural(f.videoCount, "video")}</span>` : ""}
        ${f.note ? `<span class="mv-folder-note">${escapeHtml(f.note)}</span>` : ""}
      </button>`;
  }

  // fallbackToRoots: the picker opens on whatever was typed in the path box, which may not exist
  // any more - then it opens on the server's roots instead of on an error.
  async function loadFolder(path, fallbackToRoots) {
    const list = el("mv-folders");
    if (!list || !folderPick) return;
    list.innerHTML = loadingMarkup();
    const res = await request(`/api/media/browse${path ? `?path=${enc(path)}` : ""}`);
    if (!folderPick || !el("mv-folders")) return;
    const data = res.data || {};
    if ((!res.ok || (data.error && !data.needsLogin)) && fallbackToRoots && path) { loadFolder(""); return; }
    if (!res.ok) {
      // Stay where we were: a folder that refuses to open shouldn't strand the picker.
      list.innerHTML = `<div class="mv-empty mv-error">${escapeHtml(res.error)}</div>` + (folderPick.listing || "");
      return;
    }
    folderPick.path = data.path || "";
    folderPick.parent = data.parent == null ? null : data.parent;
    folderPick.kind = data.kind;
    el("mv-browse-input").value = folderPick.path;
    // A computer is not a folder; one of its shared folders (or anything inside one) is.
    el("mv-browse-pick").disabled = !folderPick.path || data.kind === "computer" || !!data.error;
    // New folders go inside a folder: not on the starting page or a computer's list of shares.
    el("mv-browse-new").disabled = !folderPick.path || (data.kind !== "local" && data.kind !== "network") || !!data.error;
    document.querySelector('#mv-modal [data-mact="up"]').disabled = !folderPick.path;

    if (data.error) {
      folderPick.listing = "";
      list.innerHTML = `<div class="mv-empty mv-error">${escapeHtml(data.error)}</div>`
        + (data.needsLogin && data.host ? signInMarkup({ host: data.host }) : "");
      return;
    }

    let listing = "";
    const folders = data.folders || [];
    if (data.kind === "roots") {
      listing += `<div class="mv-folder-section">On this server</div>`;
      listing += folders.map((f) => folderButton(f)).join("");
      listing += `<div class="mv-folder-section">Network computers</div>`;
      listing += (data.computers || []).map((c) => `<div class="mv-folder-row">
          ${folderButton({ name: c.name, path: c.path, icon: ICON.computer, note: c.username ? `as ${c.username}` : "guest" })}
          <button type="button" class="mv-btn mv-btn-quiet mv-folder-side" data-mact="net-edit" data-id="${escapeHtml(c.id)}">sign-in</button>
        </div>`).join("");
      listing += `<div class="mv-folder-actions">
          <button type="button" class="mv-btn" data-mact="net-add">+ Add a computer</button>
          <button type="button" class="mv-btn" data-mact="net-find">${ICON.search}<span>Find computers</span></button>
        </div>
        <div id="mv-net-found"></div>`;
    } else {
      listing = folders.length
        ? folders.map((f) => folderButton(f)).join("")
        : `<div class="mv-empty">${data.kind === "computer" ? "No shared folders listed." : "No folders inside this one."}</div>`;
      if (data.hint) listing = `<div class="mv-note">${escapeHtml(data.hint)}</div>` + listing;
      if (data.videoCount) listing = `<div class="mv-folder-here">${plural(data.videoCount, "video")} in this folder</div>` + listing;
    }
    folderPick.listing = listing;
    list.innerHTML = listing;
    folderPick.computers = data.computers || folderPick.computers || [];
  }

  // "New folder": the library's folder may not exist yet (a TV Shows folder beside Movies). The
  // server makes it inside the open folder and the picker opens it, ready for "Use this folder".
  function showNewFolder() {
    const list = el("mv-folders");
    if (!list || !folderPick || el("mv-newfolder")) return;
    list.insertAdjacentHTML("afterbegin", `<form class="mv-form mv-newfolder" id="mv-newfolder">
        <div class="mv-newfolder-row">
          <input class="field-input" id="mv-newfolder-name" placeholder="New folder name, like TV Shows" spellcheck="false" autocomplete="off" aria-label="New folder name">
          <button type="button" class="mv-btn mv-btn-quiet" data-mact="new-folder-cancel">Cancel</button>
          <button type="submit" class="mv-btn mv-btn-primary">Create</button>
        </div>
        <ul class="field-errors" id="mv-newfolder-errors"></ul>
      </form>`);
    el("mv-newfolder-name").focus();
  }

  async function submitNewFolder(form, route) {
    const name = el("mv-newfolder-name").value.trim();
    if (!name) { showErrors(el("mv-newfolder-errors"), "Type a name for the new folder."); return; }
    const submit = form.querySelector('[type="submit"]');
    submit.disabled = true;
    const res = await request(route, { method: "POST", body: { parent: folderPick.path, name } });
    if (!el("mv-newfolder")) return;
    submit.disabled = false;
    if (!res.ok) { showErrors(el("mv-newfolder-errors"), res.error); return; }
    return res.data.path;
  }

  // Signing in to a computer: its name or address, and an account on it. Blank username tries
  // a guest sign-in; on an edit, a blank password keeps the saved one.
  function signInMarkup(computer) {
    const saved = computer.id ? computer : null;
    return `<form class="mv-form mv-signin" id="mv-signin" data-id="${escapeHtml(saved ? saved.id : "")}">
      <div class="mv-signin-title">${saved ? `Sign-in for ${escapeHtml(saved.name)}` : "Sign in to a computer"}</div>
      <label class="mv-field"><span>Computer name or IP address</span>
        <input class="field-input" id="mv-net-host" value="${escapeHtml(computer.host || "")}" placeholder="DESKTOP-PC or 192.168.1.20" spellcheck="false" autocomplete="off"></label>
      <label class="mv-field"><span>Name to show (optional)</span>
        <input class="field-input" id="mv-net-name" value="${escapeHtml(saved && saved.name !== saved.host ? saved.name : "")}" placeholder="Den PC" autocomplete="off"></label>
      <label class="mv-field"><span>Username on that computer</span>
        <input class="field-input" id="mv-net-user" value="${escapeHtml(computer.username || "")}" placeholder="blank to try as a guest" spellcheck="false" autocomplete="off"></label>
      <label class="mv-field"><span>Password</span>
        <input class="field-input" id="mv-net-pass" type="password" autocomplete="new-password" placeholder="${saved && saved.hasPassword ? "blank keeps the saved password" : ""}"></label>
      <div class="mv-field-note">For a Windows PC, use the Windows account (a Microsoft account works: its email and password). On a Mac, turn on File Sharing in System Settings. Windows computers don't show up under Find computers - type the PC's name or address here.</div>
      <ul class="field-errors" id="mv-net-errors"></ul>
      <div class="mv-signin-foot">
        ${saved ? `<button type="button" class="mv-btn mv-btn-quiet" data-mact="net-forget" data-id="${escapeHtml(saved.id)}">Forget this computer</button>` : ""}
        <span class="mv-spacer"></span>
        <button type="button" class="mv-btn mv-btn-quiet" data-mact="net-back">Back</button>
        <button type="submit" class="mv-btn mv-btn-primary">Sign in</button>
      </div>
    </form>`;
  }

  function showSignIn(computer) {
    const list = el("mv-folders");
    if (!list) return;
    list.innerHTML = signInMarkup(computer || {});
    el(computer && computer.host ? "mv-net-user" : "mv-net-host").focus();
  }

  async function submitSignIn(form) {
    const id = form.dataset.id;
    const body = {
      host: el("mv-net-host").value.trim(),
      name: el("mv-net-name").value.trim(),
      username: el("mv-net-user").value.trim(),
      password: el("mv-net-pass").value
    };
    if (!body.host) { showErrors(el("mv-net-errors"), "Type the computer's name or IP address."); return; }
    const submit = form.querySelector('[type="submit"]');
    submit.disabled = true;
    submit.textContent = "Signing in…";
    const res = await request(id ? `/api/media/network/${enc(id)}` : "/api/media/network", { method: id ? "PUT" : "POST", body });
    if (!el("mv-signin")) return;
    submit.disabled = false;
    submit.textContent = "Sign in";
    if (!res.ok) { showErrors(el("mv-net-errors"), res.error); return; }
    loadFolder(res.data.computer.path);
  }

  async function findComputers(button) {
    const box = el("mv-net-found");
    if (!box) return;
    button.disabled = true;
    box.innerHTML = `<div class="mv-empty">Looking for computers sharing files…</div>`;
    const res = await request("/api/media/network/discover");
    button.disabled = false;
    if (!el("mv-net-found")) return;
    if (!res.ok) { box.innerHTML = `<div class="mv-empty mv-error">${escapeHtml(res.error)}</div>`; return; }
    const found = (res.data || []).filter((h) => !h.savedId);
    box.innerHTML = found.length
      ? found.map((h) => folderButton({ name: h.name, path: h.path, icon: ICON.computer, note: h.address || h.host })).join("")
      : `<div class="mv-empty">No other computers answered. Windows PCs don't announce themselves - use Add a computer with the PC's name or address.</div>`;
  }

  // ---- libraries ------------------------------------------------------------------------------

  let libraries = null;

  async function renderLibraries() {
    const pane = el("mv-pane-libraries");
    if (!isAdmin) {
      pane.innerHTML = `<button type="button" class="mv-back" data-act="back">${ICON.back}<span>back</span></button>
        <div class="mv-empty">Only an admin can change the libraries.</div>`;
      return;
    }
    pane.innerHTML = `<button type="button" class="mv-back" data-act="back">${ICON.back}<span>back</span></button>
      <div class="mv-libs-head">
        <div><h2 class="mv-page-title">Libraries</h2><div class="mv-page-sub">The folders the catalog is built from - on the server, or shared from another computer on the network.</div></div>
        <div class="mv-actions">
          <button type="button" class="mv-btn mv-btn-glass" data-act="scan-all">${ICON.restart}<span>Scan all</span></button>
          <button type="button" class="mv-btn mv-btn-primary" data-act="lib-add"><span>+ Add library</span></button>
        </div>
      </div>
      <div id="mv-libs-notices"></div>
      <div id="mv-libs-scan"></div>
      <div id="mv-libs-list">${loadingMarkup()}</div>`;
    await refreshStatus();
    const res = await request("/api/media/libraries");
    if (route.name !== "libraries") return;
    if (!res.ok) { el("mv-libs-list").innerHTML = errorMarkup(res.error); return; }
    libraries = Array.isArray(res.data) ? res.data : [];
    updateLibrariesLive();
  }

  // Redrawn on every status poll while scanning, so counts and last-scan times move live.
  function updateLibrariesLive() {
    const list = el("mv-libs-list");
    if (!list) return;
    el("mv-libs-notices").innerHTML = noticesMarkup();
    el("mv-libs-scan").innerHTML = scanBoxMarkup();
    const byId = new Map(((status && status.libraries) || []).map((l) => [l.id, l]));
    const libs = (libraries || []).map((l) => ({ ...l, ...(byId.get(l.id) || {}) }));
    if (!libraries) return;
    if (!libs.length) {
      list.innerHTML = `<div class="mv-empty"><div class="mv-empty-title">No libraries yet</div><div>Add a folder of movies or TV shows to get started.</div></div>`;
      return;
    }
    const scanningName = status && status.scanning && status.scan ? status.scan.libraryName : null;
    list.innerHTML = libs.map((l) => `<div class="mv-lib">
        <div class="mv-lib-icon">${ICON.film}</div>
        <div class="mv-lib-body">
          <div class="mv-lib-name">${escapeHtml(l.name)} <span class="mv-lib-kind">${l.kind === "shows" ? "TV shows" : "Movies"}</span>
            ${scanningName && scanningName === l.name ? '<span class="mv-lib-scanning"><span class="mv-pulse"></span>scanning</span>' : ""}</div>
          <code class="mv-path">${escapeHtml(l.path)}</code>
          <div class="mv-lib-meta">${plural(l.itemCount || 0, l.kind === "shows" ? "show" : "movie")} · last scanned ${escapeHtml(ago(l.lastScanFinishedAt))}</div>
          ${l.lastScanError ? `<div class="mv-lib-error">${escapeHtml(l.lastScanError)}</div>` : ""}
        </div>
        <div class="mv-lib-actions">
          <button type="button" class="btn" data-act="lib-scan" data-id="${escapeHtml(l.id)}">scan now</button>
          <button type="button" class="btn" data-act="lib-edit" data-id="${escapeHtml(l.id)}">edit</button>
          <button type="button" class="btn mv-danger" data-act="lib-delete" data-id="${escapeHtml(l.id)}">delete</button>
        </div>
      </div>`).join("");
  }

  function scanBoxMarkup() {
    if (!status) return "";
    if (!status.scanning) {
      return `<div class="mv-scanbox">${ICON.check}<span>Last full scan ${escapeHtml(ago(status.lastScanFinishedAt))} · ${plural(status.movieCount || 0, "movie")} · ${plural(status.showCount || 0, "show")} · ${plural(status.episodeCount || 0, "episode")}</span></div>`;
    }
    const s = status.scan || {};
    const pct = s.total ? Math.min(100, 100 * (s.processed || 0) / s.total) : null;
    return `<div class="mv-scanbox mv-scanbox-live">
      <div class="mv-scan-line"><span class="mv-pulse"></span><span>Scanning ${escapeHtml(s.libraryName || "")} &middot; ${escapeHtml(PHASES[s.phase] || s.phase || "")}</span>
        <span class="mv-scan-count">${s.total ? `${s.processed || 0} / ${s.total}` : ""}</span></div>
      <div class="mv-scan-bar ${pct == null ? "mv-indeterminate" : ""}"><span style="width:${pct == null ? 30 : pct.toFixed(1)}%"></span></div>
      <div class="mv-scan-note">Playback keeps working while the scan runs.</div>
    </div>`;
  }

  async function saveLibrary(id, body) {
    const res = await request(id ? `/api/media/libraries/${enc(id)}` : "/api/media/libraries", { method: id ? "PUT" : "POST", body });
    if (res.ok) {
      homeData = null;
      wasScanning = true; // adding (or re-pathing) starts a scan - poll until it's done
      refreshStatus();
    }
    return res;
  }

  function openLibraryEditor(existing, draft) {
    const form = draft || { name: existing ? existing.name : "Movies", kind: existing ? existing.kind : "movies", path: existing ? existing.path : "" };
    openModal(`${modalHeader(existing ? "Edit library" : "Add library")}
      <div class="mv-form">
        <label class="mv-field"><span>Name</span><input class="field-input" id="mv-lib-name" value="${escapeHtml(form.name)}" placeholder="Movies" maxlength="80" autofocus></label>
        <label class="mv-field"><span>Kind</span>
          <select class="field-input" id="mv-lib-kind"><option value="movies">Movies</option><option value="shows">TV Shows</option></select></label>
        <label class="mv-field"><span>Folder (on the server or another computer)</span>
          <div class="mv-path-row"><input class="field-input" id="mv-lib-path" value="${escapeHtml(form.path)}" placeholder="/mnt/movies or smb://PC/Movies" spellcheck="false">
            <button type="button" class="mv-btn" data-mact="lib-browse">${ICON.folder}<span>browse</span></button></div></label>
        <div id="mv-lib-hint">${mountHint(form.path)}</div>
        ${existing ? '<div class="mv-field-note">Changing the folder rescans the library. Watched progress is kept for files that are still there.</div>' : '<div class="mv-field-note">Adding a library starts a scan of it straight away.</div>'}
        <ul class="field-errors" id="mv-lib-errors"></ul>
      </div>
      <div class="mv-modal-foot">
        <button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Cancel</button>
        <button type="button" class="mv-btn mv-btn-primary" data-mact="lib-save">${existing ? "Save" : "Add library"}</button>
      </div>`);
    el("mv-lib-kind").value = form.kind === "shows" ? "shows" : "movies";
    el("mv-lib-path").addEventListener("input", (e) => { el("mv-lib-hint").innerHTML = mountHint(e.target.value); });
    // A name nobody typed follows the kind, so "TV Shows" isn't left labelled "Movies".
    el("mv-lib-kind").addEventListener("change", (e) => {
      const name = el("mv-lib-name");
      if (!name.value.trim() || name.value === "Movies" || name.value === "TV Shows") name.value = e.target.value === "shows" ? "TV Shows" : "Movies";
    });
    libEditor = existing;
  }

  let libEditor = null;

  function readLibraryForm() {
    return { name: el("mv-lib-name").value.trim(), kind: el("mv-lib-kind").value, path: el("mv-lib-path").value.trim() };
  }

  async function submitLibraryForm(button) {
    const body = readLibraryForm();
    const errors = [];
    if (!body.name) errors.push("Give the library a name.");
    if (!body.path) errors.push("Choose the folder it lives in.");
    if (errors.length) { el("mv-lib-errors").innerHTML = errors.map((e) => `<li>${escapeHtml(e)}</li>`).join(""); return; }
    button.disabled = true;
    const existing = libEditor;
    const res = await saveLibrary(existing ? existing.id : null, body);
    button.disabled = false;
    if (!res.ok) {
      showErrors(el("mv-lib-errors"), res.error);
      el("mv-lib-hint").innerHTML = mountHint(body.path, true);
      return;
    }
    closeModal();
    showToast(existing ? "Library saved" : "Library added - scanning now", true);
    if (route.name === "libraries") renderLibraries();
    else refreshCurrent();
  }

  async function deleteLibrary(id) {
    const lib = (libraries || []).find((l) => l.id === id);
    if (!lib) return;
    if (!confirm(`Remove the library "${lib.name}"?\n\nIts titles and everyone's watched progress for them are removed from Sentinel Core. The video files themselves are not touched.`)) return;
    const res = await request(`/api/media/libraries/${enc(id)}`, { method: "DELETE" });
    if (!res.ok) { showToast(res.error, false); return; }
    homeData = null;
    showToast("Library removed", true);
    renderLibraries();
  }

  async function startScan(id) {
    const res = await request(id ? `/api/media/libraries/${enc(id)}/scan` : "/api/media/scan", { method: "POST" });
    if (res.status === 409) { showToast("A scan is already running", false); return; }
    if (!res.ok) { showToast(res.error, false); return; }
    showToast("Scan started", true);
    wasScanning = true;
    refreshStatus();
  }

  // ---- player ---------------------------------------------------------------------------------

  const player = {
    open: false, token: 0, session: null, itemId: null, offset: 0, hls: null,
    progressTimer: null, upNextTimer: null, idleTimer: null, lastReported: -1, dirty: false, recovered: false
  };

  function videoCodecs() {
    const list = ["h264"];
    try { if (window.MediaSource && MediaSource.isTypeSupported(HEVC_PROBE)) list.push("hevc"); } catch { /* no MSE */ }
    return list.join(",");
  }

  function absolutePosition() {
    const video = el("mv-video");
    return player.offset + (video.currentTime || 0);
  }

  function totalDuration() {
    const video = el("mv-video");
    if (player.session && player.session.durationSeconds) return player.session.durationSeconds;
    return isFinite(video.duration) ? player.offset + video.duration : 0;
  }

  function showPlayerMessage(html, kind) {
    const msg = el("mv-player-msg");
    msg.className = `mv-player-msg ${kind || ""}`;
    msg.innerHTML = html;
  }

  function hidePlayerMessage() {
    el("mv-player-msg").className = "mv-player-msg hidden";
  }

  function showPlayerError(message) {
    showPlayerMessage(`<div class="mv-player-card">
      <div class="mv-player-card-title">Can't play this</div>
      <div class="mv-player-card-text">${escapeHtml(message)}</div>
      <div class="mv-actions">
        ${player.itemId ? '<button type="button" class="mv-btn mv-btn-glass" data-pact="retry">Try again</button>' : ""}
        <button type="button" class="mv-btn mv-btn-primary" data-pact="close">Close</button>
      </div></div>`, "mv-player-error");
    wakeChrome(true);
  }

  // start: undefined resumes from saved progress (the server decides), a number plays from there.
  async function play(itemId, start) {
    const playerEl = el("mv-player");
    const token = ++player.token;
    if (player.open) { reportProgress(true); teardownMedia(); }
    player.open = true;
    player.itemId = itemId;
    player.session = null;
    player.offset = 0;
    player.recovered = false;
    player.lastReported = -1;
    hideUpNext();
    playerEl.classList.remove("hidden");
    playerEl.setAttribute("aria-hidden", "false");
    document.body.classList.add("mv-noscroll");
    el("mv-player-title").textContent = "";
    el("mv-player-sub").textContent = "";
    el("mv-player-time").textContent = "";
    el("mv-player-restart").classList.add("hidden");
    showPlayerMessage(`<span class="mv-spinner mv-spinner-lg"></span>`, "mv-player-loading");
    wakeChrome(true);

    const params = new URLSearchParams();
    if (start != null && start !== "") params.set("start", String(Math.max(0, Math.floor(Number(start)))));
    params.set("video", videoCodecs());
    params.set("audio", "aac,mp3");
    const res = await request(`/api/media/items/${enc(itemId)}/playback?${params}`);
    if (token !== player.token || !player.open) return;
    if (!res.ok || !res.data) { showPlayerError(res.error || "The server sent nothing to play."); return; }

    const pb = res.data;
    player.session = pb;
    player.itemId = pb.itemId || itemId;
    // An HLS playlist's clock starts where the session started; a direct file's is the film's own.
    player.offset = pb.mode === "hls" ? Number(pb.startSeconds) || 0 : 0;
    el("mv-player-title").textContent = pb.title || "";
    el("mv-player-sub").textContent = pb.subtitle || "";
    el("mv-player-restart").classList.toggle("hidden", !(pb.mode === "hls" && player.offset > 0));
    attachMedia(pb, token);
  }

  function attachMedia(pb, token) {
    const video = el("mv-video");
    const poster = safeSrc(pb.backdropUrl);
    if (poster) video.poster = poster; else video.removeAttribute("poster");
    const start = Number(pb.startSeconds) || 0;
    const onPlaying = () => { if (token === player.token) hidePlayerMessage(); };
    video.addEventListener("playing", onPlaying, { once: true });

    if (pb.mode === "hls") {
      if (window.Hls && Hls.isSupported()) {
        // hls.js defaults, except where to begin: the playlist is an EVENT playlist still being
        // written, which hls.js would otherwise treat as live and join near its end.
        const hls = new Hls({ startPosition: 0 });
        player.hls = hls;
        hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => {}));
        hls.on(Hls.Events.ERROR, (_event, data) => {
          if (!data.fatal || token !== player.token) return;
          if (data.type === Hls.ErrorTypes.MEDIA_ERROR && !player.recovered) {
            player.recovered = true;
            hls.recoverMediaError();
            return;
          }
          showPlayerError(data.type === Hls.ErrorTypes.NETWORK_ERROR
            ? "The stream from the server stopped. It may have timed out while paused - try again to pick up from here."
            : `The browser could not decode this stream (${data.details}).`);
        });
        hls.loadSource(pb.url);
        hls.attachMedia(video);
      } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
        video.src = pb.url;
        video.addEventListener("loadedmetadata", () => {
          if (video.currentTime > 5) video.currentTime = 0; // Safari joins an EVENT playlist at its end
          video.play().catch(() => {});
        }, { once: true });
      } else {
        showPlayerError("This browser can't play HLS video.");
        return;
      }
    } else {
      video.src = pb.url;
      for (const sub of pb.subtitles || []) {
        const src = safeSrc(sub.url);
        if (!src) continue;
        const track = document.createElement("track");
        track.kind = "subtitles";
        track.label = sub.label || sub.language || `Subtitles ${sub.index + 1}`;
        if (sub.language) track.srclang = sub.language;
        track.src = src;
        if (sub.forced) track.default = true;
        video.appendChild(track);
      }
      video.addEventListener("loadedmetadata", () => {
        if (start > 0 && start < (video.duration || Infinity)) video.currentTime = start;
        video.play().catch(() => {});
      }, { once: true });
    }
    clearInterval(player.progressTimer);
    player.progressTimer = setInterval(() => {
      if (!video.paused && !video.ended) reportProgress(false);
    }, PROGRESS_EVERY_MS);
  }

  function reportProgress(final) {
    if (!player.session || !player.itemId) return;
    const video = el("mv-video");
    if (!video.currentSrc && !player.hls) return;
    const position = video.ended ? totalDuration() : absolutePosition();
    if (!(position > 0)) return;
    if (!final && Math.abs(position - player.lastReported) < 1) return;
    if (final && Math.abs(position - player.lastReported) < 0.5) return;
    player.lastReported = position;
    player.dirty = true;
    const duration = totalDuration();
    request(`/api/media/items/${enc(player.itemId)}/progress`, {
      method: "POST",
      body: { positionSeconds: Math.round(position * 10) / 10, durationSeconds: duration ? Math.round(duration * 10) / 10 : null },
      keepalive: final
    });
  }

  function teardownMedia() {
    const video = el("mv-video");
    clearInterval(player.progressTimer);
    player.progressTimer = null;
    if (player.hls) {
      try { player.hls.destroy(); } catch { /* already gone */ }
      player.hls = null;
    }
    video.pause();
    for (const track of Array.from(video.querySelectorAll("track"))) track.remove();
    video.removeAttribute("src");
    video.removeAttribute("poster");
    try { video.load(); } catch { /* nothing loaded */ }
  }

  function closePlayer() {
    if (!player.open) return;
    reportProgress(true);
    player.token++;
    player.open = false;
    hideUpNext();
    teardownMedia();
    clearTimeout(player.idleTimer);
    const playerEl = el("mv-player");
    playerEl.classList.add("hidden");
    playerEl.classList.remove("mv-idle");
    playerEl.setAttribute("aria-hidden", "true");
    document.body.classList.remove("mv-noscroll");
    if (document.fullscreenElement === playerEl) document.exitFullscreen().catch(() => {});
    hidePlayerMessage();
    if (player.dirty) {
      player.dirty = false;
      homeData = null;
      // Progress was saved - redraw what's behind so its bars and resume points are current.
      if (route.name === "detail") reloadDetail();
      else refreshCurrent();
    }
    player.session = null;
  }

  function updatePlayerTime() {
    if (!player.session) return;
    const total = totalDuration();
    el("mv-player-time").textContent = `${fmtClock(absolutePosition())}${total ? ` / ${fmtClock(total)}` : ""}`;
  }

  // Seek in the film's own clock. An HLS session can only seek inside what it has produced, so a
  // jump before its start (or far past its end) opens a new session at that point instead.
  function seekTo(target) {
    if (!player.session) return;
    const video = el("mv-video");
    const total = totalDuration();
    const t = Math.max(0, total ? Math.min(target, total - 1) : target);
    if (player.session.mode === "hls") {
      const relative = t - player.offset;
      const seekable = video.seekable;
      const end = seekable && seekable.length ? seekable.end(seekable.length - 1) : 0;
      if (relative < 0 || relative > end + 15) {
        reportProgress(true);
        play(player.itemId, t);
        return;
      }
      video.currentTime = Math.min(relative, Math.max(0, end - 0.5));
    } else {
      video.currentTime = t;
    }
    wakeChrome();
  }

  function togglePlay() {
    const video = el("mv-video");
    if (video.paused) video.play().catch(() => {}); else video.pause();
    wakeChrome();
  }

  function toggleFullscreen() {
    const playerEl = el("mv-player");
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else if (playerEl.requestFullscreen) playerEl.requestFullscreen().catch(() => {});
  }

  // The title bar fades out while a film plays and nobody touches the mouse.
  function wakeChrome(stay) {
    const playerEl = el("mv-player");
    playerEl.classList.remove("mv-idle");
    clearTimeout(player.idleTimer);
    if (stay) return;
    player.idleTimer = setTimeout(() => {
      const video = el("mv-video");
      if (player.open && !video.paused && el("mv-upnext").classList.contains("hidden")) playerEl.classList.add("mv-idle");
    }, CHROME_IDLE_MS);
  }

  function showUpNext() {
    const pb = player.session;
    if (!pb || !pb.nextEpisodeId) return false;
    const card = el("mv-upnext");
    let left = UP_NEXT_SECONDS;
    card.innerHTML = `<div class="mv-upnext-kicker">Up next</div>
      <div class="mv-upnext-title">${escapeHtml(pb.nextEpisodeTitle || "Next episode")}</div>
      <div class="mv-upnext-row">
        <span class="mv-countdown"><svg viewBox="0 0 36 36" aria-hidden="true"><circle cx="18" cy="18" r="15.5"/><circle class="mv-countdown-ring" cx="18" cy="18" r="15.5" style="animation-duration:${UP_NEXT_SECONDS}s"/></svg><span id="mv-upnext-n">${left}</span></span>
        <button type="button" class="mv-btn mv-btn-primary" data-pact="next">${ICON.play}<span>Play now</span></button>
        <button type="button" class="mv-btn mv-btn-glass" data-pact="cancel-next">Cancel</button>
      </div>`;
    card.classList.remove("hidden");
    wakeChrome(true);
    clearInterval(player.upNextTimer);
    player.upNextTimer = setInterval(() => {
      left--;
      const n = el("mv-upnext-n");
      if (n) n.textContent = String(Math.max(0, left));
      if (left <= 0) { clearInterval(player.upNextTimer); play(pb.nextEpisodeId); }
    }, 1000);
    return true;
  }

  function hideUpNext() {
    clearInterval(player.upNextTimer);
    player.upNextTimer = null;
    const card = el("mv-upnext");
    card.classList.add("hidden");
    card.innerHTML = "";
  }

  function wirePlayer() {
    const playerEl = el("mv-player");
    const video = el("mv-video");
    video.addEventListener("pause", () => { if (player.open) { reportProgress(false); wakeChrome(true); } });
    video.addEventListener("play", () => wakeChrome());
    video.addEventListener("timeupdate", updatePlayerTime);
    video.addEventListener("ended", () => {
      if (!player.open) return;
      reportProgress(true);
      if (!showUpNext()) closePlayer();
    });
    video.addEventListener("error", () => {
      if (!player.open || !player.session || player.hls) return;
      const err = video.error;
      if (!err || err.code === 1) return; // MEDIA_ERR_ABORTED: our own teardown
      showPlayerError(err.code === 4
        ? "This browser can't decode this file directly. Ask an admin to check the server has ffmpeg, which converts it on the fly."
        : `The video stopped loading (${err.message || `error ${err.code}`}).`);
    });
    playerEl.addEventListener("mousemove", () => wakeChrome());
    playerEl.addEventListener("click", (e) => {
      const button = e.target.closest("[data-pact]");
      if (!button) return;
      const act = button.dataset.pact;
      if (act === "close") closePlayer();
      else if (act === "retry") {
        const at = player.session ? absolutePosition() : undefined;
        play(player.itemId, at && at > 1 ? at : undefined);
      } else if (act === "next" && player.session) play(player.session.nextEpisodeId);
      else if (act === "cancel-next") hideUpNext();
      else if (act === "restart") seekTo(0);
    });
    el("mv-player-close").addEventListener("click", closePlayer);

    // Capture phase, so the player's keys win over the video element's own (which would otherwise
    // toggle play a second time on space when the video has focus).
    window.addEventListener("keydown", (e) => {
      if (!player.open) {
        if (e.key === "Escape" && !el("mv-modal").classList.contains("hidden")) { e.preventDefault(); closeModal(); }
        return;
      }
      if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) return;
      const video = el("mv-video");
      switch (e.key) {
        case "Escape": e.preventDefault(); closePlayer(); break;
        case " ": case "k": case "K": e.preventDefault(); togglePlay(); break;
        case "ArrowLeft": e.preventDefault(); seekTo(absolutePosition() - 10); break;
        case "ArrowRight": e.preventDefault(); seekTo(absolutePosition() + 10); break;
        case "f": case "F": e.preventDefault(); toggleFullscreen(); break;
        case "m": case "M": e.preventDefault(); video.muted = !video.muted; break;
        default: break;
      }
    }, true);

    window.addEventListener("pagehide", () => { if (player.open) reportProgress(true); });
  }

  // ---- downloads (+ add) ----------------------------------------------------------------------

  // "+ add": find a title on TMDB, pick a release from the household's torrent sites, and the
  // server downloads it through the VPN and files it into the library (core/docs/MOVIE_DOWNLOADS.md).
  // The Downloads pane follows the queue and, for admins, manages the sites.
  const dl = { status: null, list: null, timer: null, states: {}, sites: null, sitesTimer: null };
  const add = { stage: "find", kind: "movie", q: "", results: null, tmdb: true, show: null, season: null, search: null, others: false, timer: null, back: "find" };
  const DL_POLL_MS = 2500;
  const STATE_WORDS = { queued: "waiting", downloading: "downloading", seeding: "sharing back", importing: "adding to library", done: "ready", failed: "failed", canceled: "canceled" };

  async function refreshDownloadStatus() {
    const res = await request("/api/media/downloads/status");
    dl.status = res.ok ? res.data : null;
    const can = !!(dl.status && dl.status.canDownload);
    el("mv-add-btn").classList.toggle("hidden", !can);
    el("mv-downloads-tab").classList.toggle("hidden", !can);
    drawVpnLight(dl.status && dl.status.status);
    if (can && !dl.list) pollDownloads(true);
  }

  // What the VPN light and the Downloads card say: a tone for the colour and a few words.
  //   on    green  connected and proven to hide the home address
  //   busy  cyan   coming up
  //   idle  grey   switched off until needed (the normal resting state)
  //   off   amber  Use VPN is turned off in Settings - downloads use the home connection
  //   bad   red    not installed, failed, or not getting through
  function vpnLook(status) {
    const vpn = (status && status.vpn) || {};
    const services = status && status.services;
    if (vpn.state === "disabled") return { tone: "off", short: "VPN off", text: "Turned off in Settings &middot; searching and downloading use your <b>home</b> connection, so the sites see your home address." };
    if (services === "notInstalled") return { tone: "bad", short: "VPN not set up", text: "The VPN and torrent services are not installed on the server yet. Run core/downloads/install-downloads.sh on it once." };
    if (vpn.account && vpn.account.expired) return { tone: "bad", short: "VPN out of time", text: `Your Mullvad account ran out of time on ${paidDate(vpn.account.paidUntil)}. Add time at ${MULLVAD_LINK} and downloads work again.` };
    if (vpn.state === "connected") {
      const where = `${vpn.location ? " &middot; " + escapeHtml(vpn.location) : ""}${vpn.ip ? ` &middot; <code>${escapeHtml(vpn.ip)}</code>` : ""}`;
      return vpn.verified
        ? { tone: "on", short: vpn.country ? `VPN · ${vpn.country}` : "VPN on", text: `Connected and verified${where} &middot; your home address is hidden` }
        : { tone: "busy", short: "VPN checking", text: `Connected${where} &middot; checking it hides your home address&hellip;` };
    }
    if (services === "starting" || vpn.state === "connecting") return { tone: "busy", short: "VPN connecting", text: "Connecting&hellip;" };
    if (services === "failed" || vpn.state === "down") return { tone: "bad", short: "VPN problem", text: escapeHtml(vpn.error || "The VPN or torrent services failed to start. Check the server's log (journalctl -u sentinelcore-vpn).") };
    if (services === "external") return { tone: "idle", short: "VPN idle", text: "qBittorrent and Prowlarr are run outside this server; the VPN is checked when they are used." };
    return { tone: "idle", short: "VPN idle", text: "Off until someone searches or downloads, then it connects by itself. Press test to check it now." };
  }

  const MULLVAD_LINK = `<a href="https://mullvad.net/en/account" target="_blank" rel="noopener noreferrer">mullvad.net</a>`;

  // "Oct 27", with the year when it is not this one.
  function paidDate(iso, now = new Date()) {
    const at = new Date(iso);
    const text = at.toLocaleDateString("en-US", { month: "short", day: "numeric" });
    return at.getFullYear() === now.getFullYear() ? text : `${text}, ${at.getFullYear()}`;
  }

  // The line under the VPN on the Downloads screen saying how long the Mullvad account has left
  // ("The account line" in MOVIE_DOWNLOADS.md). Null for any other VPN.
  function accountLine(account, now = new Date()) {
    if (!account || !account.paidUntil) return null;
    const date = paidDate(account.paidUntil, now);
    const days = Math.max(0, Number(account.daysLeft) || 0);
    if (account.expired) return { tone: "bad", text: `Mullvad ran out on ${date} &middot; add time at ${MULLVAD_LINK}` };
    if (account.runningOut) {
      const when = days === 0 ? "today" : days === 1 ? "tomorrow" : `in ${days} days`;
      return { tone: "warn", text: `Mullvad runs out ${when} (${date}) &middot; add time at ${MULLVAD_LINK}` };
    }
    const left = days === 0 ? "less than a day left" : days === 1 ? "1 day left" : `${days} days left`;
    return { tone: "plain", text: `Mullvad paid until ${date} &middot; ${left}` };
  }

  function drawVpnLight(status) {
    const light = el("mv-vpn-light");
    const can = !!(dl.status && dl.status.canDownload);
    light.classList.toggle("hidden", !can || !status);
    if (!status) return;
    const look = vpnLook(status);
    light.className = `btn mv-vpn-light mv-vpn-${look.tone}${can ? "" : " hidden"}`;
    light.querySelector(".mv-vpn-light-text").textContent = look.short;
  }

  async function testVpn(button) {
    button.disabled = true;
    button.classList.add("mv-working");
    const label = button.querySelector("span:last-child");
    if (label) label.textContent = "testing…";
    const res = await request("/api/media/downloads/vpn/test", { method: "POST" });
    button.disabled = false;
    button.classList.remove("mv-working");
    if (label) label.textContent = "test";
    if (!res.ok) { showToast(res.error, false); return; }
    showToast(res.data.message, !!res.data.ok);
    if (dl.list) dl.list.status = res.data.status;
    drawVpnLight(res.data.status);
    if (route.name === "downloads") { el("mv-pane-downloads").dataset.shape = ""; drawDownloads(); }
  }

  function speedLabel(bytesPerSecond) {
    const b = Number(bytesPerSecond) || 0;
    if (b <= 0) return "";
    if (b >= 1e6) return `${(b / 1e6).toFixed(1)} MB/s`;
    return `${Math.max(1, Math.round(b / 1e3))} KB/s`;
  }

  function etaLabel(seconds) {
    const s = Number(seconds);
    if (!isFinite(s) || s <= 0) return "";
    if (s < 90) return "about a minute left";
    if (s < 3600) return `${Math.round(s / 60)} min left`;
    return `${Math.floor(s / 3600)} h ${Math.round((s % 3600) / 60)} min left`;
  }

  // Follows the queue while anything is moving, and says so when something lands in the library.
  async function pollDownloads(quiet) {
    clearTimeout(dl.timer);
    const res = await request("/api/media/downloads");
    if (res.ok && res.data) {
      dl.list = res.data;
      let landed = false;
      for (const d of [...(dl.list.active || []), ...(dl.list.recent || [])]) {
        const before = dl.states[d.id];
        if (before && before !== d.state && d.state === "done") {
          landed = true;
          if (!quiet) showToast(`${d.title}${d.subtitle ? " " + d.subtitle : ""} is in the library`, true);
        }
        if (before && before !== d.state && d.state === "failed" && !quiet) showToast(`${d.title}: ${d.error || "download failed"}`, false);
        dl.states[d.id] = d.state;
      }
      if (landed) homeData = null;
      drawVpnLight(dl.list.status);
      if (visible && route.name === "downloads") drawDownloads();
      if (visible && route.name === "home") drawDownloadBanner();
    }
    const busy = dl.list && (dl.list.active || []).length > 0;
    if (visible && (busy || route.name === "downloads")) dl.timer = setTimeout(() => pollDownloads(false), DL_POLL_MS);
  }

  function drawDownloadBanner() {
    const pane = el("mv-pane-home");
    let banner = el("mv-dl-banner");
    const active = (dl.list && dl.list.active) || [];
    if (!active.length) { if (banner) banner.remove(); return; }
    if (!banner) {
      banner = document.createElement("button");
      banner.type = "button";
      banner.id = "mv-dl-banner";
      banner.className = "mv-dl-banner";
      banner.dataset.mvPane = "downloads";
      pane.prepend(banner);
    }
    const first = active[0];
    const pct = Math.round((Number(first.progress) || 0) * 100);
    banner.innerHTML = `<span class="mv-pulse"></span><span>${active.length === 1
      ? `Downloading ${escapeHtml(first.title)}${first.subtitle ? " " + escapeHtml(first.subtitle) : ""} &middot; ${pct}%`
      : `${active.length} downloads in progress`}</span><span class="mv-dl-banner-go">downloads ${ICON.right}</span>`;
  }

  async function renderDownloads(token) {
    const pane = el("mv-pane-downloads");
    if (!dl.list) pane.innerHTML = loadingMarkup();
    await pollDownloads(true);
    if (token !== routeToken) return;
    if (!dl.list) pane.innerHTML = errorMarkup("Could not load the downloads.");
  }

  function vpnMarkup(status) {
    if (!status) return "";
    const look = vpnLook(status);
    const account = accountLine(status.vpn && status.vpn.account);
    return `<div class="mv-dl-vpn mv-dl-vpn-${look.tone}">
      <span class="mv-dl-vpn-dot"></span>
      <span class="mv-dl-vpn-label">VPN</span>
      <span class="mv-dl-vpn-text">${look.text}</span>
      <button type="button" class="mv-btn" data-act="dl-vpn-test" title="Connect now and prove the sites see the VPN's address, not your home's">${ICON.check}<span>test</span></button>
      ${dl.status && dl.status.canManageSites ? `<button type="button" class="mv-btn" data-act="dl-sites">${ICON.search}<span>sites</span></button>` : ""}
      ${account ? `<div class="mv-dl-vpn-account mv-dl-vpn-account-${account.tone}">${account.text}</div>` : ""}
    </div>`;
  }

  function downloadRow(d) {
    const pct = Math.round((Number(d.progress) || 0) * 1000) / 10;
    const active = ["queued", "downloading", "seeding", "importing"].includes(d.state);
    const facts = active
      ? [d.state === "downloading" ? `${pct}%` : STATE_WORDS[d.state], d.state === "downloading" ? speedLabel(d.speedBytesPerSecond) : "",
         d.state === "downloading" ? etaLabel(d.etaSeconds) : "", sizeLabel(d.sizeBytes), d.state === "downloading" && d.peers ? plural(d.peers, "peer") : ""]
      : [STATE_WORDS[d.state] || d.state, sizeLabel(d.sizeBytes), d.requestedBy ? `by ${d.requestedBy}` : "", ago(d.finishedAt || d.createdAt)];
    const actions = [];
    if (d.state === "done" && d.itemId) actions.push(`<button type="button" class="mv-btn mv-btn-primary" data-act="open" data-id="${escapeHtml(d.itemId)}">${ICON.play}<span>open</span></button>`);
    if (d.state === "failed") actions.push(`<button type="button" class="mv-btn" data-act="dl-retry" data-id="${escapeHtml(d.id)}">${ICON.restart}<span>retry</span></button>`);
    actions.push(active
      ? `<button type="button" class="mv-btn mv-btn-quiet" data-act="dl-cancel" data-id="${escapeHtml(d.id)}">cancel</button>`
      : `<button type="button" class="mv-icon-btn" data-act="dl-remove" data-id="${escapeHtml(d.id)}" aria-label="Remove from the list" title="Remove from the list">${ICON.close}</button>`);
    return `<div class="mv-dl mv-dl-${escapeHtml(d.state)}" data-dl-id="${escapeHtml(d.id)}">
      ${art(d.posterUrl, "mv-poster mv-dl-poster", d.title, "")}
      <div class="mv-dl-body">
        <div class="mv-dl-title">${escapeHtml(d.title)}${d.year ? ` <span class="mv-match-year-tag">${escapeHtml(d.year)}</span>` : ""}${d.subtitle ? ` <span class="mv-lib-kind">${escapeHtml(d.subtitle)}</span>` : ""}${d.replacesItemId && active ? ` <span class="mv-lib-kind">Replaces the copy you have</span>` : ""}</div>
        <div class="mv-dl-release" title="${escapeHtml(d.releaseTitle)}">${escapeHtml(d.releaseTitle)}${d.site ? ` &middot; ${escapeHtml(d.site)}` : ""}</div>
        ${active ? `<div class="mv-dl-bar"><span style="width:${d.state === "downloading" ? pct : d.state === "queued" ? 0 : 100}%"></span></div>` : ""}
        <div class="mv-dl-facts">${facts.filter(Boolean).map(escapeHtml).join('<span class="mv-dot">&middot;</span>')}</div>
        <div class="${d.state === "failed" ? "mv-lib-error" : "mv-dl-note"}${d.error ? "" : " hidden"}">${escapeHtml(d.error || "")}</div>
      </div>
      <div class="mv-lib-actions">${actions.join("")}</div>
    </div>`;
  }

  // What decides the pane's layout: which downloads, in which state, with which buttons. While
  // it stays the same, a poll only patches the numbers, so nothing under the pointer is
  // replaced between a press and its release.
  function downloadsShape(data) {
    const rows = [...(data.active || []), ...(data.recent || [])].map((d) => `${d.id}:${d.state}:${d.itemId || ""}`);
    const v = (data.status && data.status.vpn) || {};
    const a = v.account || {};
    const vpn = data.status ? `${data.status.services}:${v.state}:${v.ip}:${v.error}:${v.verified}:${a.paidUntil}:${a.daysLeft}:${a.runningOut}:${a.expired}` : "";
    return rows.join("|") + "#" + vpn;
  }

  function drawDownloads() {
    const pane = el("mv-pane-downloads");
    const data = dl.list || {};
    const shape = downloadsShape(data);
    if (pane.dataset.shape === shape && pane.firstChild) {
      const scratch = document.createElement("div");
      for (const d of data.active || []) {
        const row = pane.querySelector(`[data-dl-id="${CSS.escape(d.id)}"]`);
        if (!row) continue;
        scratch.innerHTML = downloadRow(d);
        for (const part of [".mv-dl-bar", ".mv-dl-facts", ".mv-dl-note"]) {
          const now = row.querySelector(part);
          const next = scratch.querySelector(part);
          if (now && next && now.outerHTML !== next.outerHTML) now.replaceWith(next);
        }
      }
      return;
    }
    pane.dataset.shape = shape;
    const active = data.active || [];
    const recent = data.recent || [];
    pane.innerHTML = `${vpnMarkup(data.status)}
      <div class="mv-dl-head"><h3 class="mv-section-title">Downloading</h3>
        <button type="button" class="mv-btn mv-btn-primary" data-act="dl-add">${ICON.plus}<span>add a movie or show</span></button></div>
      ${active.length ? `<div class="mv-dl-list">${active.map(downloadRow).join("")}</div>`
        : `<div class="mv-empty"><div>Nothing downloading. Press <b>add</b> to find a movie or TV episodes.</div></div>`}
      ${recent.length ? `<h3 class="mv-section-title">Recent</h3><div class="mv-dl-list">${recent.map(downloadRow).join("")}</div>` : ""}`;
    wireImages(pane);
  }

  async function downloadAction(act, id, button) {
    if (act === "dl-cancel" && !confirm("Cancel this download? What has downloaded so far is deleted.")) return;
    if (button) button.disabled = true;
    const res = act === "dl-retry"
      ? await request(`/api/media/downloads/${enc(id)}/retry`, { method: "POST" })
      : await request(`/api/media/downloads/${enc(id)}`, { method: "DELETE" });
    if (!res.ok) { if (button) button.disabled = false; showToast(res.error, false); return; }
    pollDownloads(true);
  }

  // -- the add dialog: find -> (seasons) -> releases

  function openAdd() {
    const seeded = add.seeded;
    Object.assign(add, { stage: "find", results: add.q && !seeded ? add.results : null, show: null, season: null, search: null, others: false, already: null, seeded: false });
    drawAdd();
    if (add.q && !add.results) lookup(!seeded);
  }

  // Opened from a library search's offer: already at the picked title, or at a site search for
  // exactly what was typed. `seeded` marks that Find never looked anything up, so going back
  // there looks the typed text up first.
  function openAddAt(start) {
    Object.assign(add, { stage: "find", kind: start.kind, q: start.title, results: start.pick ? [start.pick] : null,
      show: null, season: null, search: null, others: false, already: null, seeded: true });
    if (start.pick) pickResult(0);
    else startReleaseSearch({ kind: start.kind, title: start.title }, "find");
  }

  function stopAddPoll() {
    clearTimeout(add.timer);
    add.timer = null;
  }

  function drawAdd() {
    const title = add.stage === "already" ? alreadyTitle() : add.stage === "releases" ? "Pick a download" : add.stage === "show" ? (add.show ? add.show.title : "TV show") : "Add a movie or show";
    let body = "";
    if (add.stage === "find") body = addFindMarkup();
    else if (add.stage === "show") body = addShowMarkup();
    else if (add.stage === "already") body = addAlreadyMarkup();
    else body = addReleasesMarkup();
    const panel = el("mv-modal-panel");
    const open = !el("mv-modal").classList.contains("hidden") && panel.querySelector(".mv-add");
    const html = `${modalHeader(title)}<div class="mv-add">${body}</div>`;
    if (open) { panel.innerHTML = html; wireImages(panel); }
    else openModal(html, stopAddPoll);
    const input = el("mv-add-q");
    if (input && add.stage === "find" && document.activeElement !== input) { input.focus(); input.setSelectionRange(input.value.length, input.value.length); }
  }

  function addFindMarkup() {
    const tmdbMissing = dl.status && dl.status.tmdbConfigured === false;
    let results = "";
    if (add.results === "loading") results = loadingMarkup("searching");
    else if (typeof add.results === "string") results = `<div class="mv-empty mv-error">${escapeHtml(add.results)}</div>`;
    else if (Array.isArray(add.results)) {
      results = add.results.length
        ? add.results.map((r, i) => `<button type="button" class="mv-match" data-mact="add-pick" data-i="${i}">
            ${art(r.posterUrl, "mv-poster mv-match-poster", r.title, "")}
            <span class="mv-match-text">
              <span class="mv-match-title">${escapeHtml(r.title)}${r.year ? ` <span class="mv-match-year-tag">${escapeHtml(r.year)}</span>` : ""}${r.inLibrary ? ` <span class="mv-lib-kind mv-have">in library</span>` : ""}</span>
              <span class="mv-match-overview">${escapeHtml(r.overview || "No description.")}</span>
            </span>
          </button>`).join("")
        : `<div class="mv-empty">Nothing on TMDB called that. Try fewer words.</div>`;
    }
    const direct = (tmdbMissing || !add.tmdb) && add.q
      ? `<div class="mv-note">${tmdbMissing ? "No TMDB key, so there are no posters or episode lists - the sites are searched for exactly what you type." : ""}
          ${add.kind === "show" ? `<div class="mv-add-direct"><input class="field-input" id="mv-add-season" placeholder="Season" inputmode="numeric" maxlength="2">
            <input class="field-input" id="mv-add-episode" placeholder="Episode (blank = whole season)" inputmode="numeric" maxlength="3"></div>` : ""}
          <button type="button" class="mv-btn mv-btn-primary" data-mact="add-direct">${ICON.search}<span>search sites for &ldquo;${escapeHtml(add.q)}&rdquo;</span></button></div>`
      : "";
    const vpnOff = dl.status && dl.status.status && dl.status.status.vpn && dl.status.status.vpn.state === "disabled"
      ? `<div class="mv-note mv-note-warn">The VPN is turned off in Settings, so the sites and everyone sharing will see your home address.</div>`
      : "";
    const noLibrary = dl.status && ((add.kind === "movie" && !dl.status.movieLibrary) || (add.kind === "show" && !dl.status.showLibrary))
      ? `<div class="mv-note mv-note-warn">There is no ${add.kind === "movie" ? "movie" : "TV"} library on the server's own disks to save into yet. Add one under &#9881; libraries${add.kind === "show" ? " (kind: TV Shows)" : ""} first.</div>`
      : "";
    return `<div class="mv-seg" role="radiogroup">
        <button type="button" class="${add.kind === "movie" ? "active" : ""}" data-mact="add-kind" data-kind="movie" role="radio" aria-checked="${add.kind === "movie"}">Movie</button>
        <button type="button" class="${add.kind === "show" ? "active" : ""}" data-mact="add-kind" data-kind="show" role="radio" aria-checked="${add.kind === "show"}">TV show</button>
      </div>
      ${vpnOff}${noLibrary}
      <form class="mv-match-form" id="mv-add-form">
        <input class="field-input" id="mv-add-q" value="${escapeHtml(add.q)}" placeholder="${add.kind === "movie" ? "Movie title" : "Show title"}" autocomplete="off" autofocus>
        <button type="submit" class="mv-btn mv-btn-primary">${ICON.search}<span>find</span></button>
      </form>
      ${direct}
      <div class="mv-match-results">${results}</div>`;
  }

  async function lookup(searchSitesIfNoTmdb = true) {
    const q = add.q.trim();
    if (!q) return;
    add.results = "loading";
    drawAdd();
    const res = await request(`/api/media/downloads/lookup?${new URLSearchParams({ q, kind: add.kind })}`);
    if (add.stage !== "find" || add.q.trim() !== q) return;
    if (!res.ok) { add.results = res.error; drawAdd(); return; }
    add.tmdb = !!res.data.tmdbConfigured;
    add.results = add.tmdb ? (res.data.results || []) : [];
    // No TMDB key: there are no posters to pick from, so Find searches the sites straight away
    // rather than stopping at a second button.
    if (!add.tmdb && searchSitesIfNoTmdb) { directSearch(); return; }
    drawAdd();
  }

  // The sites searched for exactly what was typed (plus a season and episode for TV, when given).
  function directSearch() {
    const season = (el("mv-add-season") || {}).value;
    const episode = (el("mv-add-episode") || {}).value;
    startReleaseSearch({ kind: add.kind, title: add.q.trim(),
      season: add.kind === "show" && /^\d+$/.test(season || "") ? Number(season) : null,
      episode: add.kind === "show" && /^\d+$/.test(season || "") && /^\d+$/.test(episode || "") ? Number(episode) : null }, "find");
  }

  async function pickResult(i) {
    const r = Array.isArray(add.results) ? add.results[i] : null;
    if (!r) return;
    if (r.kind === "movie") {
      startReleaseSearch({ kind: "movie", title: r.title, year: r.year, tmdbId: r.tmdbId, posterPath: r.posterPath }, "find");
      return;
    }
    add.stage = "show";
    add.show = { title: r.title, year: r.year, tmdbId: r.tmdbId, posterPath: r.posterPath, posterUrl: r.posterUrl, loading: true };
    drawAdd();
    const res = await request(`/api/media/downloads/show/${enc(r.tmdbId)}`);
    if (add.stage !== "show" || !add.show || add.show.tmdbId !== r.tmdbId) return;
    if (!res.ok) { add.show.error = res.error; add.show.loading = false; drawAdd(); return; }
    add.show = { ...res.data, loading: false };
    const seasons = add.show.seasons || [];
    // Start on the first season with something missing that has aired.
    const firstGap = seasons.find((s) => s.number > 0 && (s.episodes || []).some((e) => e.aired && !e.inLibrary));
    add.season = (firstGap || seasons[0] || {}).number ?? null;
    drawAdd();
  }

  function addShowMarkup() {
    const show = add.show;
    if (!show || show.loading) return loadingMarkup("loading seasons");
    if (show.error) return `<div class="mv-empty mv-error">${escapeHtml(show.error)}</div><div class="mv-modal-foot"><button type="button" class="mv-btn" data-mact="add-back">back</button></div>`;
    const seasons = show.seasons || [];
    const season = seasons.find((s) => s.number === add.season);
    const tabs = seasons.map((s) => `<button type="button" class="${s.number === add.season ? "active" : ""}" data-mact="add-season" data-n="${s.number}">${s.number === 0 ? "Specials" : "S" + s.number}${s.ownedCount === s.episodeCount && s.episodeCount ? " &#10003;" : ""}</button>`).join("");
    const episodes = season ? (season.episodes || []).map((e) => `<div class="mv-add-ep${e.inLibrary ? " mv-add-ep-have" : ""}">
        <span class="mv-add-ep-num">${e.number}</span>
        <span class="mv-add-ep-name">${escapeHtml(e.name || `Episode ${e.number}`)}<span class="mv-add-ep-date">${e.airDate ? escapeHtml(fmtDate(e.airDate)) : ""}${e.inLibrary ? " &middot; in library" : ""}${e.aired === false ? " &middot; not out yet" : ""}</span></span>
        <button type="button" class="mv-btn" data-mact="add-episode" data-n="${e.number}" ${e.aired === false ? "disabled" : ""}>get</button>
      </div>`).join("") : "";
    return `<button type="button" class="mv-link mv-add-back" data-mact="add-back">${ICON.back} results</button>
      <div class="mv-add-seasons mv-seg">${tabs}</div>
      ${season ? `<div class="mv-add-season-head"><span>${escapeHtml(season.name || "Season " + season.number)} &middot; ${plural(season.episodeCount, "episode")}${season.ownedCount ? ` &middot; ${season.ownedCount} in library` : ""}</span>
        <button type="button" class="mv-btn mv-btn-primary" data-mact="add-whole-season">get the whole season</button></div>
        <div class="mv-add-eps">${episodes}</div>` : `<div class="mv-empty">TMDB lists no seasons for this show.</div>`}`;
  }

  async function startReleaseSearch(input, back) {
    stopAddPoll();
    add.stage = "releases";
    add.back = back;
    add.others = false;
    add.search = { phase: "connecting", message: "Connecting to the VPN...", input };
    drawAdd();
    const res = await request("/api/media/downloads/search", { method: "POST", body: input });
    if (add.stage !== "releases") return;
    if (!res.ok) { add.search = { phase: "failed", message: res.error, input }; drawAdd(); return; }
    add.search = { ...res.data, input };
    drawAdd();
    pollSearch(res.data.id);
  }

  async function pollSearch(id) {
    stopAddPoll();
    add.timer = setTimeout(async () => {
      const res = await request(`/api/media/downloads/search/${enc(id)}`);
      if (add.stage !== "releases" || !add.search || add.search.id !== id) return;
      if (!res.ok) { add.search = { ...add.search, phase: "failed", message: res.error }; drawAdd(); return; }
      const changed = res.data.phase !== add.search.phase || res.data.message !== add.search.message;
      add.search = { ...res.data, input: add.search.input };
      if (changed || res.data.phase === "done") drawAdd();
      if (res.data.phase !== "done" && res.data.phase !== "failed") pollSearch(id);
    }, 1000);
  }

  function releaseMarkup(r, i, group) {
    const chips = [r.quality, r.codec, r.source, r.hdr ? "HDR" : "", r.seasonPack ? "whole season" : ""]
      .filter(Boolean).map((c) => `<span class="mv-lib-kind">${escapeHtml(c)}</span>`).join("");
    const facts = [sizeLabel(r.sizeBytes), r.seeders != null ? `${r.seeders} sharing` : "", r.site || "", r.publishedAt ? ago(r.publishedAt) : ""]
      .filter(Boolean).map(escapeHtml).join('<span class="mv-dot">&middot;</span>');
    return `<div class="mv-rel${i === 0 && group === "results" ? " mv-rel-best" : ""}">
      <div class="mv-rel-body">
        <div class="mv-rel-chips">${i === 0 && group === "results" ? '<span class="mv-lib-kind mv-have">best match</span>' : ""}${chips}</div>
        <div class="mv-rel-title" title="${escapeHtml(r.title)}">${escapeHtml(r.title)}</div>
        <div class="mv-rel-facts">${facts}</div>
        ${r.warning ? `<div class="mv-dl-note">${escapeHtml(r.warning)}</div>` : ""}
      </div>
      <button type="button" class="mv-btn ${i === 0 && group === "results" ? "mv-btn-primary" : ""}" data-mact="add-grab" data-release="${escapeHtml(r.id)}">download</button>
    </div>`;
  }

  function addReleasesMarkup() {
    const s = add.search || {};
    const input = s.input || {};
    const what = `${escapeHtml(input.title || "")}${input.year ? ` (${escapeHtml(input.year)})` : ""}${input.season != null ? ` &middot; ${input.episode != null ? `S${String(input.season).padStart(2, "0")}E${String(input.episode).padStart(2, "0")}` : `season ${escapeHtml(input.season)}`}` : ""}`;
    const back = `<button type="button" class="mv-link mv-add-back" data-mact="add-back">${ICON.back} ${add.back === "show" ? "episodes" : "results"}</button>`;
    if (s.phase === "connecting" || s.phase === "searching") {
      return `${back}<div class="mv-add-what">${what}</div>
        <div class="mv-empty"><span class="mv-spinner"></span><span>${escapeHtml(s.message || "Searching")}</span>
        <div class="mv-add-hint">${s.phase === "connecting" ? "The first search after a while starts the VPN; it takes a few seconds." : "Asking each of your sites."}</div></div>`;
    }
    if (s.phase === "failed") {
      return `${back}<div class="mv-add-what">${what}</div><div class="mv-empty mv-error">${escapeHtml(s.message || s.error || "The search failed.")}</div>
        <div class="mv-modal-foot"><button type="button" class="mv-btn" data-mact="add-again">try again</button></div>`;
    }
    const results = s.results || [];
    const others = s.others || [];
    return `${back}<div class="mv-add-what">${what}</div>
      ${results.length ? `<div class="mv-rel-list">${results.map((r, i) => releaseMarkup(r, i, "results")).join("")}</div>`
        : `<div class="mv-empty">${others.length ? "None of the releases found are clearly this title with people sharing it." : escapeHtml(s.message || "Nothing found on your sites.")}</div>`}
      ${others.length ? `<button type="button" class="mv-link mv-add-others" data-mact="add-others">${add.others ? "hide" : "show"} ${plural(others.length, "other result")}</button>
        ${add.others ? `<div class="mv-rel-list mv-rel-others">${others.map((r, i) => releaseMarkup(r, i, "others")).join("")}</div>` : ""}` : ""}`;
  }

  // The movie is already in the library: the server queued nothing, so ask, then send the same
  // body again with replace true (the old copy goes once the new one lands) or false (keep both).
  function alreadyTitle() {
    const e = (add.already && add.already.existing) || {};
    return e.title ? `You already have ${e.title}${e.year ? ` (${e.year})` : ""}` : (add.already && add.already.error) || "Already in the library";
  }

  function addAlreadyMarkup() {
    const e = add.already.existing || {};
    const copy = [e.height ? `${e.height}p` : "", sizeLabel(e.sizeBytes)].filter(Boolean).join(", ");
    return `<p class="mv-modal-intro">${copy ? `The copy you have: ${escapeHtml(copy)}.` : "It is already in the library."}
        ${e.canReplace ? "Replace it and the old copy's files are deleted once the new one has downloaded."
          : "The copy you have is on another computer's share, so it can't be replaced from here."}</p>
      <div class="mv-modal-foot">
        <button type="button" class="mv-btn mv-btn-quiet" data-mact="add-already-cancel">Cancel</button>
        <button type="button" class="mv-btn${e.canReplace ? "" : " mv-btn-primary"}" data-mact="add-replace" data-replace="false">Keep both</button>
        ${e.canReplace ? `<button type="button" class="mv-btn mv-btn-primary" data-mact="add-replace" data-replace="true">Replace it</button>` : ""}
      </div>`;
  }

  async function grab(button, releaseId, replace) {
    if (!add.search || !add.search.id) return;
    const buttons = "#mv-modal-panel [data-mact='add-grab'], #mv-modal-panel [data-mact='add-replace']";
    for (const b of document.querySelectorAll(buttons)) b.disabled = true;
    button.classList.add("mv-working");
    const body = { searchId: add.search.id, releaseId };
    if (replace !== undefined) body.replace = replace;
    const res = await request("/api/media/downloads", { method: "POST", body });
    if (res.status === 409 && res.data && res.data.code === "alreadyInLibrary" && replace === undefined) {
      if (el("mv-modal").classList.contains("hidden")) return;
      add.stage = "already";
      add.already = { releaseId, error: res.error, existing: res.data.existing || {} };
      drawAdd();
      return;
    }
    if (!res.ok) {
      for (const b of document.querySelectorAll(buttons)) b.disabled = false;
      button.classList.remove("mv-working");
      showToast(res.error, false);
      return;
    }
    const d = res.data || {};
    dl.states[d.id] = d.state;
    closeModal();
    showToast(`Downloading ${d.title || ""}${d.subtitle ? " " + d.subtitle : ""}`, true);
    go({ name: "downloads" });
  }

  function addModalClick(act, target) {
    switch (act) {
      case "add-kind":
        if (add.kind === target.dataset.kind) return true;
        add.kind = target.dataset.kind;
        add.results = null;
        drawAdd();
        // Flipping Movie / TV show re-checks TMDB but never starts a site search by itself.
        if (add.q) lookup(false);
        return true;
      case "add-pick": pickResult(Number(target.dataset.i)); return true;
      case "add-season": add.season = Number(target.dataset.n); drawAdd(); return true;
      case "add-whole-season":
      case "add-episode": {
        const show = add.show;
        if (!show) return true;
        startReleaseSearch({ kind: "show", title: show.title, year: show.year, tmdbId: show.tmdbId, posterPath: show.posterPath,
          season: add.season, episode: act === "add-episode" ? Number(target.dataset.n) : null }, "show");
        return true;
      }
      case "add-direct": directSearch(); return true;
      case "add-back":
        stopAddPoll();
        add.stage = add.stage === "releases" && add.back === "show" && add.show ? "show" : "find";
        if (add.stage === "find") add.show = null;
        if (add.stage === "find" && add.seeded) {
          add.seeded = false;
          add.results = null;
          drawAdd();
          lookup(false);
          return true;
        }
        drawAdd();
        return true;
      case "add-again": if (add.search && add.search.input) startReleaseSearch(add.search.input, add.back); return true;
      case "add-others": add.others = !add.others; drawAdd(); return true;
      case "add-grab": grab(target, target.dataset.release); return true;
      case "add-replace": if (add.already) grab(target, add.already.releaseId, target.dataset.replace === "true"); return true;
      case "add-already-cancel": add.stage = "releases"; add.already = null; drawAdd(); return true;
      default: return false;
    }
  }

  // -- sites (admin)

  function openSites() {
    dl.sites = { view: "list", list: null, catalog: null, q: "", adding: null, message: null };
    openModal(`${modalHeader("Torrent sites")}<div id="mv-sites-body"></div>`, () => { clearTimeout(dl.sitesTimer); dl.sites = null; });
    loadSites();
  }

  // Prowlarr lives inside the VPN, so the first request may find it still starting: the server
  // says so with a 503 { starting }, and this simply asks again.
  async function sitesRequest(url, options, retry) {
    const res = await request(url, options);
    if (res.status === 503 && res.data && res.data.starting && dl.sites) {
      dl.sites.message = res.error;
      drawSites();
      await new Promise((resolve) => { dl.sitesTimer = setTimeout(resolve, 3000); });
      return dl.sites ? sitesRequest(url, options, retry) : res;
    }
    return res;
  }

  async function loadSites() {
    if (!dl.sites) return;
    dl.sites.message = "Connecting to the VPN...";
    drawSites();
    const res = await sitesRequest("/api/media/downloads/sites");
    if (!dl.sites) return;
    dl.sites.message = res.ok ? null : res.error;
    dl.sites.list = res.ok ? res.data || [] : null;
    drawSites();
  }

  async function loadCatalog() {
    if (!dl.sites) return;
    dl.sites.catalog = "loading";
    drawSites();
    const res = await sitesRequest(`/api/media/downloads/sites/catalog?${new URLSearchParams({ q: dl.sites.q })}`);
    if (!dl.sites) return;
    dl.sites.catalog = res.ok ? res.data || [] : res.error;
    drawSites();
  }

  function drawSites() {
    const body = el("mv-sites-body");
    const s = dl.sites;
    if (!body || !s) return;
    if (s.view === "list") {
      const list = s.list;
      body.innerHTML = `<p class="mv-modal-intro">The sites searched when anyone adds a movie or show. Prowlarr keeps them working; everything goes through the VPN.</p>
        ${s.message ? `<div class="mv-empty"><span class="mv-spinner"></span><span>${escapeHtml(s.message)}</span></div>` : ""}
        ${Array.isArray(list) ? (list.length ? `<div class="mv-site-list">${list.map((site) => `<div class="mv-site">
            <div class="mv-site-body"><div class="mv-dl-title">${escapeHtml(site.name)} <span class="mv-lib-kind">${escapeHtml(site.privacy || "")}</span></div>
            ${site.description ? `<div class="mv-lib-meta">${escapeHtml(site.description)}</div>` : ""}</div>
            <label class="mv-site-toggle"><input type="checkbox" data-mact="site-toggle" data-id="${escapeHtml(site.id)}" ${site.enabled ? "checked" : ""}> on</label>
            <button type="button" class="mv-icon-btn" data-mact="site-remove" data-id="${escapeHtml(site.id)}" aria-label="Remove ${escapeHtml(site.name)}" title="Remove">${ICON.close}</button>
          </div>`).join("")}</div>` : `<div class="mv-empty">No sites yet. Add one to start searching.</div>`) : ""}
        <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-primary" data-mact="site-catalog" ${Array.isArray(list) ? "" : "disabled"}>${ICON.plus}<span>add a site</span></button></div>`;
      return;
    }
    if (s.view === "catalog") {
      const c = s.catalog;
      let results = "";
      if (c === "loading") results = loadingMarkup(s.message || "loading the site list");
      else if (typeof c === "string") results = `<div class="mv-empty mv-error">${escapeHtml(c)}</div>`;
      else if (Array.isArray(c)) results = c.length ? c.map((d, i) => `<div class="mv-site">
          <div class="mv-site-body"><div class="mv-dl-title">${escapeHtml(d.name)} <span class="mv-lib-kind">${escapeHtml(d.privacy || "")}</span>${d.language ? ` <span class="mv-lib-meta">${escapeHtml(d.language)}</span>` : ""}</div>
          ${d.description ? `<div class="mv-lib-meta">${escapeHtml(d.description)}</div>` : ""}</div>
          <button type="button" class="mv-btn" data-mact="site-pick" data-i="${i}">${d.needsSetup ? "set up" : "add"}</button>
        </div>`).join("") : `<div class="mv-empty">No site by that name.</div>`;
      body.innerHTML = `<button type="button" class="mv-link mv-add-back" data-mact="site-list">${ICON.back} your sites</button>
        <form class="mv-match-form" id="mv-site-search"><input class="field-input" id="mv-site-q" value="${escapeHtml(s.q)}" placeholder="Site name" autocomplete="off" autofocus>
          <button type="submit" class="mv-btn mv-btn-primary">${ICON.search}<span>find</span></button></form>
        <div class="mv-match-results mv-site-list">${results}</div>`;
      const q = el("mv-site-q");
      if (q) q.focus();
      return;
    }
    const d = s.adding;
    const fields = (d.fields || []).map((f) => {
      const name = `mv-sf-${f.name}`;
      const help = f.help ? `<div class="mv-lib-meta mv-field-help">${escapeHtml(f.help)}</div>` : "";
      if (f.type === "checkbox") return `<label class="mv-field mv-field-check"><input type="checkbox" id="${escapeHtml(name)}" ${f.value === true ? "checked" : ""}> <span>${escapeHtml(f.label)}</span></label>${help}`;
      if (f.type === "select") return `<label class="mv-field"><span>${escapeHtml(f.label)}</span><select class="field-input" id="${escapeHtml(name)}">${(f.options || []).map((o) => `<option value="${escapeHtml(o.value)}" ${String(f.value) === String(o.value) ? "selected" : ""}>${escapeHtml(o.label)}</option>`).join("")}</select></label>${help}`;
      return `<label class="mv-field"><span>${escapeHtml(f.label)}</span><input class="field-input" id="${escapeHtml(name)}" type="${f.type === "password" ? "password" : f.type === "number" ? "number" : "text"}" value="${escapeHtml(f.value ?? "")}" autocomplete="off"></label>${help}`;
    }).join("");
    body.innerHTML = `<button type="button" class="mv-link mv-add-back" data-mact="site-catalog">${ICON.back} all sites</button>
      <form class="mv-form" id="mv-site-form"><div class="mv-dl-title">${escapeHtml(d.name)}</div>
        ${d.description ? `<p class="mv-modal-intro">${escapeHtml(d.description)}</p>` : ""}
        ${fields}
        <ul class="field-errors" id="mv-site-errors"></ul>
        <div class="mv-modal-foot"><button type="submit" class="mv-btn mv-btn-primary" id="mv-site-save">add site</button></div></form>`;
  }

  async function addSite(definition, fields, button) {
    if (button) { button.disabled = true; button.classList.add("mv-working"); }
    const res = await sitesRequest("/api/media/downloads/sites", { method: "POST", body: { definition: definition.definition, fields } });
    if (button) { button.disabled = false; button.classList.remove("mv-working"); }
    if (!dl.sites) return;
    if (!res.ok) {
      if (dl.sites.view === "form") showErrors(el("mv-site-errors"), res.error);
      else showToast(res.error, false);
      return;
    }
    showToast(`${definition.name} added`, true);
    dl.sites.view = "list";
    loadSites();
  }

  function submitSiteForm() {
    const d = dl.sites && dl.sites.adding;
    if (!d) return;
    const fields = {};
    for (const f of d.fields || []) {
      const input = el(`mv-sf-${f.name}`);
      if (!input) continue;
      if (f.type === "checkbox") fields[f.name] = input.checked;
      else if (f.type === "number") fields[f.name] = input.value === "" ? null : Number(input.value);
      else if (f.type === "select") fields[f.name] = /^-?\d+$/.test(input.value) ? Number(input.value) : input.value;
      else fields[f.name] = input.value;
    }
    addSite(d, fields, el("mv-site-save"));
  }

  function sitesModalClick(act, target) {
    const s = dl.sites;
    if (!s) return false;
    switch (act) {
      case "site-list": s.view = "list"; drawSites(); return true;
      case "site-catalog": s.view = "catalog"; if (!Array.isArray(s.catalog)) loadCatalog(); else drawSites(); return true;
      case "site-pick": {
        const d = Array.isArray(s.catalog) ? s.catalog[Number(target.dataset.i)] : null;
        if (!d) return true;
        if (!d.needsSetup) { addSite(d, {}, target); return true; }
        s.adding = d;
        s.view = "form";
        drawSites();
        return true;
      }
      case "site-toggle": {
        const enabled = target.checked;
        target.disabled = true;
        sitesRequest(`/api/media/downloads/sites/${enc(target.dataset.id)}`, { method: "PUT", body: { enabled } }).then((res) => {
          target.disabled = false;
          if (!res.ok) { target.checked = !enabled; showToast(res.error, false); }
        });
        return true;
      }
      case "site-remove":
        if (!confirm("Remove this site? It is no longer searched.")) return true;
        sitesRequest(`/api/media/downloads/sites/${enc(target.dataset.id)}`, { method: "DELETE" }).then((res) => {
          if (!res.ok) showToast(res.error, false);
          loadSites();
        });
        return true;
      default: return false;
    }
  }

  // ---- event wiring ---------------------------------------------------------------------------

  function wireView() {
    const view = el("view-movies");
    view.addEventListener("click", (e) => {
      const target = e.target.closest("[data-act], [data-mv-pane]");
      if (!target || !view.contains(target)) return;
      if (target.dataset.mvPane) { go({ name: target.dataset.mvPane }); return; }
      const act = target.dataset.act;
      const id = target.dataset.id;
      switch (act) {
        case "open":
          if (target.dataset.episode) flashEpisodeId = target.dataset.episode;
          go({ name: "detail", id });
          break;
        case "play": play(id, target.dataset.start); break;
        case "back": goBack(); break;
        case "retry": refreshCurrent(); break;
        case "scroll": {
          const scroller = target.parentElement.querySelector(".mv-row-scroller");
          scroller.scrollBy({ left: Number(target.dataset.dir) * scroller.clientWidth * 0.85, behavior: "smooth" });
          break;
        }
        case "watched": setWatched(id, target.dataset.value === "true"); break;
        case "season": detailSeason = Number(target.dataset.n); drawDetail(); break;
        case "season-watched": if (detail) setWatched(detail.id, target.dataset.value === "true", target.dataset.n); break;
        case "fix-match": openFixMatch(); break;
        case "refresh-meta": refreshMetadata(target); break;
        case "delete-item": deleteItem(target); break;
        case "list": setInList(target); break;
        case "surprise": surprise(target); break;
        case "unwatched": {
          const s = browse[route.name];
          if (!s) break;
          s.unwatched = !s.unwatched;
          s.offset = 0;
          target.classList.toggle("btn-on", s.unwatched);
          target.setAttribute("aria-pressed", String(s.unwatched));
          loadBrowse(route.name, routeToken);
          break;
        }
        case "mylist": {
          const s = browse[route.name];
          if (!s) break;
          s.list = !s.list;
          s.offset = 0;
          target.classList.toggle("btn-on", s.list);
          target.setAttribute("aria-pressed", String(s.list));
          loadBrowse(route.name, routeToken);
          break;
        }
        case "page": {
          const s = browse[route.name];
          if (!s) break;
          s.offset = Math.max(0, s.offset + Number(target.dataset.dir) * PAGE_SIZE);
          loadBrowse(route.name, routeToken).then(() => el(`mv-pane-${route.name}`).scrollIntoView({ behavior: "smooth", block: "start" }));
          break;
        }
        case "open-settings": openSettings(); break;
        case "setup-kind":
          readSetup();
          setup.kind = target.dataset.kind;
          if (!setup.name || setup.name === "Movies" || setup.name === "TV Shows") setup.name = setup.kind === "shows" ? "TV Shows" : "Movies";
          el("mv-pane-home").innerHTML = setupMarkup();
          break;
        case "setup-browse":
          readSetup();
          openFolderBrowser(setup.path, (path) => {
            setup.path = path;
            const input = el("mv-setup-path");
            if (input) { input.value = path; el("mv-setup-hint").innerHTML = ""; }
          });
          break;
        case "setup-add": submitSetup(target); break;
        case "scan-all": startScan(null); break;
        case "lib-scan": startScan(id); break;
        case "lib-add": openLibraryEditor(null); break;
        case "lib-edit": openLibraryEditor((libraries || []).find((l) => l.id === id) || null); break;
        case "lib-delete": deleteLibrary(id); break;
        case "dl-add": openAdd(); break;
        case "offer-pick": pickOffer(Number(target.dataset.i)); break;
        case "offer-direct": openAddAt({ kind: target.dataset.kind, title: offer.q }); break;
        case "dl-sites": openSites(); break;
        case "dl-vpn-test": testVpn(target); break;
        case "dl-cancel": case "dl-retry": case "dl-remove": downloadAction(act, id, target); break;
        default: break;
      }
    });
    el("mv-add-btn").addEventListener("click", openAdd);
    view.addEventListener("input", (e) => {
      if (e.target.id === "mv-setup-path") el("mv-setup-hint").innerHTML = mountHint(e.target.value);
    });
    el("mv-libraries-btn").addEventListener("click", () => go({ name: "libraries" }));

    const modal = el("mv-modal");
    modal.addEventListener("submit", (e) => {
      if (e.target.id === "mv-signin") { e.preventDefault(); submitSignIn(e.target); }
      if (e.target.id === "mv-newfolder" && folderPick) {
        e.preventDefault();
        submitNewFolder(e.target, "/api/media/browse/folder").then((path) => { if (path) loadFolder(path); });
      }
      if (e.target.id === "mv-add-form") {
        e.preventDefault();
        add.q = el("mv-add-q").value.trim();
        add.results = null;
        lookup();
      }
      if (e.target.id === "mv-site-search" && dl.sites) { e.preventDefault(); dl.sites.q = el("mv-site-q").value.trim(); loadCatalog(); }
      if (e.target.id === "mv-site-form") { e.preventDefault(); submitSiteForm(); }
    });
    modal.addEventListener("click", (e) => {
      if (e.target === modal) { closeModal(); return; }
      const target = e.target.closest("[data-mact]");
      if (!target) return;
      switch (target.dataset.mact) {
        case "close": case "cancel": closeModal(); break;
        case "match": applyMatch(target, target.dataset.tmdb); break;
        case "unlock": unlockMatch(target); break;
        case "folder": loadFolder(target.dataset.path); break;
        case "go": loadFolder(el("mv-browse-input").value.trim()); break;
        case "net-add": showSignIn({}); break;
        case "new-folder": showNewFolder(); break;
        case "new-folder-cancel": el("mv-newfolder")?.remove(); break;
        case "net-edit": showSignIn((folderPick && folderPick.computers || []).find((c) => c.id === target.dataset.id) || {}); break;
        case "net-back": loadFolder(""); break;
        case "net-find": findComputers(target); break;
        case "net-forget": {
          if (!confirm("Forget this computer's sign-in? Libraries on it stop scanning until you sign in again.")) break;
          request(`/api/media/network/${enc(target.dataset.id)}`, { method: "DELETE" }).then(() => loadFolder(""));
          break;
        }
        case "up": if (folderPick) loadFolder(folderPick.parent || ""); break;
        case "pick":
          if (folderPick && folderPick.path) {
            const pick = folderPick;
            pick.picked = true;
            closeModal();
            pick.onPick(pick.path);
          }
          break;
        case "lib-browse": {
          const existing = libEditor;
          const draft = readLibraryForm();
          // One modal at a time: the editor steps aside for the picker and comes back with the
          // chosen folder (or unchanged on cancel), everything typed so far intact.
          modalClose = null;
          openFolderBrowser(draft.path,
            (path) => openLibraryEditor(existing, { ...draft, path }),
            () => openLibraryEditor(existing, draft));
          break;
        }
        case "lib-save": submitLibraryForm(target); break;
        default: if (!addModalClick(target.dataset.mact, target)) sitesModalClick(target.dataset.mact, target); break;
      }
    });
  }

  // ---- public ---------------------------------------------------------------------------------

  function show() {
    visible = true;
    const wanted = parseHash();
    refreshStatus();
    refreshDownloadStatus();
    routeTo(wanted || route);
  }

  function hide() {
    visible = false;
    rememberScroll();
    closePlayer();
    // Leaving the tab closes everything; a folder picker's "cancel" must not reopen its editor.
    modalClose = null;
    folderPick = null;
    closeModal();
    clearTimeout(statusTimer);
    statusTimer = null;
    clearTimeout(dl.timer);
    clearTimeout(dl.sitesTimer);
    stopAddPoll();
  }

  wireView();
  wirePlayer();

  return { show, hide, play };
})();
