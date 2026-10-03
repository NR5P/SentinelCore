// Podcasts tab (core/docs/MUSIC_AND_PODCASTS.md §5-6): Listen Now, the Up Next queue, the shows
// someone follows, a show's page with its episodes and settings, an episode's page (notes,
// chapters, transcript, bookmarks, download, transcribe, clips), Discover (directory search,
// top charts, recommendations, preview before following, RSS and OPML), bookmarks, clips,
// searching every transcript, "Fill my drive" and stats. Playing is audio-player.js's job.
//
// Hash routes: #podcasts, #podcasts/upnext, #podcasts/shows, #podcasts/discover,
// #podcasts/bookmarks, #podcasts/clips, #podcasts/stats, #podcasts/show/<id>,
// #podcasts/episode/<id>, #podcasts/search/<q>.
window.SentinelPodcasts = (function () {
  const A = window.SentinelAudio;
  const U = A.util;
  const ICON = A.ICON;
  const { escapeHtml: h, enc, request, fmtClock, fmtDuration, fmtDate, plural, art, wireImages, rowMarkup, wireRows, loadingMarkup, errorMarkup, emptyMarkup, showToast } = U;
  const el = (id) => document.getElementById(id);

  const NAV = ["home", "upnext", "shows", "discover", "bookmarks", "clips", "stats"];
  const EP_PAGE = 50;

  let visible = false;
  let route = { name: "home" };
  let routeToken = 0;
  let status = null;
  let upNext = [];
  const rowData = new Map();
  const scrollMemory = {};
  let current = null;
  let searchTimer = null;
  let dlTimer = null;
  const showState = { id: null, filter: "all", sort: null, season: "", q: "", offset: 0 };
  const discover = { q: "", genre: "", genres: null, results: null };
  const preview = { audio: null, playingUrl: null };

  // ---- routing --------------------------------------------------------------------------------

  function parseHash() {
    const hash = location.hash.replace(/^#/, "");
    if (hash !== "podcasts" && !hash.startsWith("podcasts/")) return null;
    const parts = hash.split("/").slice(1);
    const arg = parts.length > 1 ? decodeURIComponent(parts.slice(1).join("/")) : "";
    switch (parts[0]) {
      case "show": return arg ? { name: "show", id: arg } : { name: "shows" };
      case "episode": return arg ? { name: "episode", id: arg } : { name: "home" };
      case "search": return arg ? { name: "search", id: arg } : { name: "home" };
      default: return NAV.includes(parts[0]) ? { name: parts[0] } : { name: "home" };
    }
  }

  function hashFor(r) {
    if (r.name === "home") return "#podcasts";
    if (r.id) return `#podcasts/${r.name}/${enc(r.id)}`;
    return `#podcasts/${r.name}`;
  }

  function go(r) {
    const hash = hashFor(r);
    if (location.hash === hash) { routeTo(r); return; }
    location.hash = hash;
  }

  function navKey(r) {
    return { show: "shows", episode: "shows", search: "home" }[r.name] || r.name;
  }

  async function routeTo(r) {
    const token = ++routeToken;
    if (route && (route.name !== r.name || route.id !== r.id)) scrollMemory[hashFor(route)] = window.scrollY;
    route = r;
    clearTimeout(dlTimer);
    for (const b of document.querySelectorAll("#pc-subtabs [data-pc-nav]")) b.classList.toggle("active", b.dataset.pcNav === navKey(r));
    const search = el("pc-search");
    if (r.name === "search" && document.activeElement !== search) search.value = r.id;
    if (r.name !== "search" && document.activeElement !== search) search.value = "";
    await U.ensureAuth();
    if (token !== routeToken) return;
    el("pc-settings-btn").classList.toggle("hidden", !U.isAdmin());
    try {
      switch (r.name) {
        case "home": await renderHome(token); break;
        case "upnext": await renderUpNext(token); break;
        case "shows": await renderShows(token); break;
        case "discover": await renderDiscover(token); break;
        case "bookmarks": await renderBookmarks(token); break;
        case "clips": await renderClips(token); break;
        case "stats": renderStats(); break;
        case "show": await renderShow(r.id, token); break;
        case "episode": await renderEpisode(r.id, token); break;
        case "search": await renderSearch(r.id, token); break;
        default: await renderHome(token);
      }
    } catch (err) {
      console.error(err);
      if (token === routeToken) setContent(errorMarkup(err.message || "Something went wrong."));
    }
    if (token !== routeToken) return;
    const y = scrollMemory[hashFor(r)];
    if (y != null) window.scrollTo(0, y);
  }

  function refreshCurrent() {
    if (!visible) return;
    scrollMemory[hashFor(route)] = window.scrollY;
    routeTo(route);
  }

  function setContent(html) {
    const content = el("pc-content");
    content.innerHTML = html;
    wireImages(content);
    wireRows(content);
    A.markNow(content);
    return content;
  }

  function backButton() {
    return `<button type="button" class="mv-back" data-act="back">${ICON.back}<span>back</span></button>`;
  }

  async function refreshStatus() {
    const res = await request("/api/podcasts/status");
    if (res.ok) status = res.data;
    return status;
  }

  // ---- bits of an episode ---------------------------------------------------------------------

  function progressOf(ep) {
    const p = ep && ep.progress;
    if (!p) return { played: false, pct: 0, left: ep && ep.durationSeconds ? ep.durationSeconds : 0, started: false };
    const dur = p.durationSeconds || ep.durationSeconds || 0;
    return { played: !!p.played, pct: p.played ? 100 : Number(p.percent) || (dur ? 100 * (p.positionSeconds || 0) / dur : 0), left: Math.max(0, dur - (p.positionSeconds || 0)), started: !p.played && (p.positionSeconds || 0) > 0 };
  }

  function epMeta(ep) {
    const pr = progressOf(ep);
    const parts = [];
    if (ep.publishedAt) parts.push(fmtDate(ep.publishedAt));
    if (pr.played) parts.push("played");
    else if (pr.started) parts.push(`${fmtDuration(pr.left)} left`);
    else if (ep.durationSeconds) parts.push(fmtDuration(ep.durationSeconds));
    if (ep.season || ep.episode) parts.push(`${ep.season ? `S${ep.season} ` : ""}${ep.episode ? `E${ep.episode}` : ""}`.trim());
    if (ep.episodeType && ep.episodeType !== "full") parts.push(ep.episodeType);
    return parts;
  }

  function dlBadge(ep) {
    const d = ep.download || {};
    if (d.state === "done") return `<span class="au-dl-badge done" title="Downloaded to the server${d.enhanced ? " (enhanced)" : ""}">${ICON.download}</span>`;
    if (d.state === "downloading") return `<span class="au-dl-badge busy" title="Downloading ${Math.round((d.progress || 0) * 100)}%">${Math.round((d.progress || 0) * 100)}%</span>`;
    if (d.state === "queued") return `<span class="au-dl-badge busy" title="Waiting to download">${ICON.clock}</span>`;
    if (d.state === "failed") return `<span class="au-dl-badge bad" title="${h(d.error || "Download failed")}">!</span>`;
    return "";
  }

  function progressBarHtml(ep) {
    const pr = progressOf(ep);
    if (!pr.started) return "";
    return `<div class="mv-progress"><span style="width:${Math.min(100, pr.pct).toFixed(1)}%"></span></div>`;
  }

  // A square card for rows: the art plays, the text opens the episode.
  function episodeCard(row) {
    return (ep, i) => {
      const pr = progressOf(ep);
      return `<div class="au-card au-ep-card ${pr.played ? "au-played" : ""}" data-now-id="${h(ep.id)}">
        <div class="au-card-art-wrap">
          <button type="button" class="au-card-art" data-act="open-episode" data-id="${h(ep.id)}" title="${h(ep.title)}">${art(ep.artUrl, "au-square", ep.podcastTitle || ep.title, "", progressBarHtml(ep) + `<span class="au-card-eq"><span class="au-eqbars" aria-hidden="true"><i></i><i></i><i></i></span></span>`, ICON.mic)}</button>
          <button type="button" class="au-card-play" data-act="play-row" data-row="${h(row)}" data-i="${i}" aria-label="Play ${h(ep.title)}" title="${pr.started ? "Resume" : "Play"}">${ICON.play}</button>
        </div>
        <button type="button" class="au-card-text" data-act="open-episode" data-id="${h(ep.id)}">
          <span class="au-ep-card-show">${h(ep.podcastTitle || "")}</span>
          <span class="mv-card-title au-two-lines">${h(ep.title)}</span>
          <span class="mv-card-sub">${h(epMeta(ep).join(" · "))}</span>
        </button>
      </div>`;
    };
  }

  function showCard(p) {
    if (!p) return "";
    const unplayed = Number(p.unplayedCount) || 0;
    return `<div class="au-card">
      <div class="au-card-art-wrap">
        <button type="button" class="au-card-art" data-act="open-show" data-id="${h(p.id)}" title="${h(p.title)}">${art(p.artUrl, "au-square", p.title, p.author, unplayed ? `<span class="mv-badge mv-badge-count">${unplayed > 99 ? "99+" : unplayed}</span>` : "", ICON.mic)}</button>
      </div>
      <button type="button" class="au-card-text" data-act="open-show" data-id="${h(p.id)}">
        <span class="mv-card-title">${h(p.title)}</span>
        <span class="mv-card-sub">${h(p.author || "")}</span>
      </button>
    </div>`;
  }

  // Directory results (not in the household yet, or already): click previews.
  function dirCard(s, i, list) {
    return `<div class="au-card">
      <div class="au-card-art-wrap">
        <button type="button" class="au-card-art" data-act="preview" data-list="${h(list)}" data-i="${i}" title="${h(s.title)}">${art(s.artUrl, "au-square", s.title, s.author, s.subscribed ? `<span class="mv-badge mv-badge-watched" title="You follow this">${ICON.check}</span>` : "", ICON.mic)}</button>
      </div>
      <button type="button" class="au-card-text" data-act="preview" data-list="${h(list)}" data-i="${i}">
        <span class="mv-card-title">${h(s.title)}</span>
        <span class="mv-card-sub">${h([s.author, s.genre].filter(Boolean).join(" · "))}</span>
      </button>
    </div>`;
  }

  function bookmarkCard(b) {
    return `<div class="au-bm-card" data-now-id="${h(b.episodeId)}">
      <button type="button" class="au-bm-card-main" data-act="bm-play" data-ep="${h(b.episodeId)}" data-t="${Number(b.positionSeconds) || 0}" title="Play from ${fmtClock(b.positionSeconds)}">
        <span class="au-bm-art">${U.safeSrc(b.artUrl) ? `<img src="${h(b.artUrl)}" alt="" loading="lazy">` : ICON.bookmark}<span class="au-bm-time">${fmtClock(b.positionSeconds)}</span></span>
        <span class="au-bm-text"><span class="au-bm-show">${h(b.podcastTitle || "")}</span><span class="au-bm-title">${h(b.episodeTitle || "")}</span>
        ${b.note ? `<span class="au-bm-note">${h(b.note)}</span>` : b.transcriptSnippet ? `<span class="au-bm-snippet">“${h(b.transcriptSnippet)}”</span>` : ""}</span>
      </button>
    </div>`;
  }

  // A full-width row for lists: show page, Up Next, search.
  function episodeRow(ep, i, opts = {}) {
    const pr = progressOf(ep);
    const inQueue = !!ep.inQueue || upNext.some((u) => u.id === ep.id);
    return `<div class="au-ep ${pr.played ? "au-played" : ""}" data-now-id="${h(ep.id)}" ${opts.sortable ? 'data-sort="1"' : ""} data-i="${i}">
      ${opts.sortable ? `<span class="au-drag" title="Drag to reorder" aria-hidden="true">${ICON.drag}</span>` : ""}
      ${opts.art !== false ? `<button type="button" class="au-ep-art" data-act="open-episode" data-id="${h(ep.id)}" aria-label="Open ${h(ep.title)}">${U.safeSrc(ep.artUrl) ? `<img src="${h(ep.artUrl)}" alt="" loading="lazy">` : ICON.mic}</button>` : ""}
      <div class="au-ep-body">
        <div class="au-ep-meta">${opts.showTitle ? `<span class="au-ep-show">${h(ep.podcastTitle || "")}</span> · ` : ""}${h(epMeta(ep).join(" · "))} ${dlBadge(ep)}${ep.bookmarkCount ? ` <span class="au-dl-badge" title="${plural(ep.bookmarkCount, "bookmark")}">${ICON.bookmark}</span>` : ""}${ep.hasTranscript ? ` <span class="au-dl-badge" title="Has a transcript">${ICON.transcript}</span>` : ""}</div>
        <button type="button" class="au-ep-title" data-act="open-episode" data-id="${h(ep.id)}">${h(ep.title)}</button>
        ${ep.summary && opts.summary !== false ? `<div class="au-ep-summary">${h(ep.summary)}</div>` : ""}
        ${pr.started ? `<div class="au-ep-progress"><span style="width:${Math.min(100, pr.pct).toFixed(1)}%"></span></div>` : ""}
      </div>
      <div class="au-ep-actions">
        <button type="button" class="au-ep-play" data-act="play-list" data-list="${h(opts.list)}" data-i="${i}" title="${pr.started ? "Resume" : "Play"}">${ICON.play}<span>${pr.started ? fmtDuration(pr.left) : ep.durationSeconds ? fmtDuration(ep.durationSeconds) : "play"}</span></button>
        ${opts.removable
          ? `<button type="button" class="au-ctl au-ctl-xs" data-act="queue-remove" data-id="${h(ep.id)}" title="Remove from Up Next" aria-label="Remove from Up Next">${ICON.close}</button>`
          : `<button type="button" class="au-ctl au-ctl-xs ${inQueue ? "on" : ""}" data-act="${inQueue ? "queue-remove" : "queue-add"}" data-id="${h(ep.id)}" title="${inQueue ? "In Up Next (click to remove)" : "Add to Up Next"}" aria-label="${inQueue ? "Remove from Up Next" : "Add to Up Next"}">${inQueue ? ICON.check : ICON.queue}</button>`}
        <button type="button" class="au-ctl au-ctl-xs ${pr.played ? "on" : ""}" data-act="toggle-played" data-list="${h(opts.list)}" data-i="${i}" title="${pr.played ? "Mark unplayed" : "Mark played"}" aria-label="${pr.played ? "Mark unplayed" : "Mark played"}">${ICON.check}</button>
        <button type="button" class="au-ctl au-ctl-xs" data-act="ep-menu" data-list="${h(opts.list)}" data-i="${i}" title="More" aria-label="More">${ICON.more}</button>
      </div>
    </div>`;
  }

  const lists = new Map(); // list key -> { episodes, context }

  function episodeList(key, episodes, context, opts = {}) {
    lists.set(key, { episodes, context });
    if (!episodes.length) return `<div class="au-q-empty">${opts.empty || "No episodes."}</div>`;
    return `<div class="au-eps" data-key="${h(key)}">${episodes.map((ep, i) => episodeRow(ep, i, { ...opts, list: key })).join("")}</div>`;
  }

  function listEpisode(target) {
    const list = lists.get(target.dataset.list);
    if (!list) return null;
    return { list, i: Number(target.dataset.i), ep: list.episodes[Number(target.dataset.i)] };
  }

  // Playing one episode carries on into Up Next afterwards (minus that episode).
  async function playEpisode(ep, position) {
    if (!upNext.length) await loadQueue();
    const rest = upNext.filter((u) => u.id !== ep.id);
    A.playEpisodes([ep, ...rest], 0, { type: "podcast", id: ep.podcastId, name: ep.podcastTitle || "Podcast" }, position != null ? { position, resume: false } : {});
  }

  function playFromList(key, i) {
    const list = lists.get(key);
    if (!list) return;
    if (key === "upnext") { A.playEpisodes(list.episodes, i, { type: "queue", id: "upnext", name: "Up Next" }); return; }
    const ep = list.episodes[i];
    if (ep) playEpisode(ep);
  }

  // ---- Up Next (server queue) -----------------------------------------------------------------

  async function loadQueue() {
    const res = await request("/api/podcasts/queue");
    if (res.ok && Array.isArray(res.data)) upNext = res.data;
    return upNext;
  }

  async function queueAdd(id, position = "last") {
    const res = await request("/api/podcasts/queue", { method: "POST", body: { episodeId: id, position } });
    if (!res.ok) { showToast(res.error, false); return false; }
    upNext = Array.isArray(res.data) ? res.data : upNext;
    showToast(position === "next" ? "Up Next: playing next" : "Added to Up Next", true);
    markQueueButtons();
    return true;
  }

  async function queueRemove(id) {
    const res = await request(`/api/podcasts/queue/${enc(id)}`, { method: "DELETE" });
    if (!res.ok) { showToast(res.error, false); return false; }
    upNext = Array.isArray(res.data) ? res.data : upNext.filter((u) => u.id !== id);
    if (route.name === "upnext") renderUpNextList();
    else markQueueButtons();
    return true;
  }

  function markQueueButtons() {
    const ids = new Set(upNext.map((u) => u.id));
    for (const b of document.querySelectorAll("#view-podcasts [data-act=queue-add], #view-podcasts [data-act=queue-remove]")) {
      if (route.name === "upnext") continue;
      const inQ = ids.has(b.dataset.id);
      b.dataset.act = inQ ? "queue-remove" : "queue-add";
      b.classList.toggle("on", inQ);
      b.innerHTML = inQ ? ICON.check : ICON.queue;
      b.title = inQ ? "In Up Next (click to remove)" : "Add to Up Next";
    }
  }

  async function setPlayed(ep, played) {
    const res = await request(`/api/podcasts/episodes/${enc(ep.id)}/played`, { method: "POST", body: { played } });
    if (!res.ok) { showToast(res.error, false); return; }
    ep.progress = res.data || ep.progress;
    if (played) upNext = upNext.filter((u) => u.id !== ep.id);
    refreshCurrent();
  }

  async function setDownload(ep, on) {
    const res = await request(`/api/podcasts/episodes/${enc(ep.id)}/download`, { method: on ? "POST" : "DELETE" });
    if (!res.ok) { showToast(res.error, false); return; }
    Object.assign(ep, res.data || {});
    showToast(on ? "Downloading to the server" : "Download removed", true);
    refreshCurrent();
  }

  function episodeMenu(anchor, ep) {
    const pr = progressOf(ep);
    const inQueue = upNext.some((u) => u.id === ep.id) || ep.inQueue;
    const d = (ep.download || {}).state;
    const link = U.safeLink(ep.link);
    U.openMenu(anchor, [
      { label: "Play next", icon: ICON.queue, run: () => A.enqueue([ep], "next", "episode") },
      { label: "Play after the current queue", icon: ICON.plus, run: () => A.enqueue([ep], "last", "episode") },
      inQueue ? { label: "Remove from Up Next", icon: ICON.close, run: () => queueRemove(ep.id) } : { label: "Add to top of Up Next", icon: ICON.queue, run: () => queueAdd(ep.id, "next") },
      "-",
      { label: pr.played ? "Mark unplayed" : "Mark played", icon: ICON.check, run: () => setPlayed(ep, !pr.played) },
      d === "done" ? { label: "Remove download", icon: ICON.trash, run: () => setDownload(ep, false) }
        : d === "queued" || d === "downloading" ? { label: "Downloading…", icon: ICON.download, disabled: true }
        : { label: "Download to the server", icon: ICON.download, run: () => setDownload(ep, true) },
      { label: "Bookmark…", icon: ICON.bookmark, run: () => openBookmark(ep, 0) },
      { label: "Make a clip…", icon: ICON.scissors, run: () => openClipMaker(ep, 0) },
      "-",
      { label: "Episode page", icon: ICON.info, run: () => go({ name: "episode", id: ep.id }) },
      ep.podcastId ? { label: "Go to show", icon: ICON.mic, run: () => go({ name: "show", id: ep.podcastId }) } : null,
      link ? { label: "Open the episode's web page", icon: ICON.external, run: () => window.open(link, "_blank", "noopener") } : null
    ], { title: ep.title });
  }

  // ---- Listen Now -----------------------------------------------------------------------------

  async function renderHome(token) {
    const content = el("pc-content");
    if (!content.querySelector(".au-home")) content.innerHTML = loadingMarkup();
    const res = await request("/api/podcasts/home");
    if (token !== routeToken) return;
    if (!res.ok) { setContent(errorMarkup(res.error)); return; }
    const d = res.data || {};
    upNext = d.upNext || [];
    rowData.clear();
    const keep = (k, v) => { rowData.set(k, v || []); return v; };
    const saved = Number(d.timeSavedSeconds) || 0;
    const subs = d.subscriptions || [];
    if (!subs.length && !upNext.length) { await renderOnboarding(token); return; }
    const head = `<div class="au-home-head">
        <div><div class="mv-kicker">Listen now</div>
          <div class="au-home-counts">${upNext.length ? `${plural(upNext.length, "episode")} up next · ${fmtDuration(upNext.reduce((s, e) => s + progressOf(e).left, 0))}` : "Up Next is empty"}</div>
          ${saved >= 60 ? `<div class="au-saved">${ICON.speed}<span>You've saved <b>${h(fmtDuration(saved))}</b> with speed, trimmed silences and skipped intros.</span></div>` : ""}</div>
        <div class="mv-actions">
          ${upNext.length ? `<button type="button" class="mv-btn mv-btn-primary" data-act="play-upnext">${ICON.play}<span>Play Up Next</span></button>` : ""}
          <button type="button" class="mv-btn mv-btn-glass" data-act="fill">${ICON.clock}<span>Fill my drive</span></button>
          <button type="button" class="mv-btn mv-btn-glass" data-act="briefing">${ICON.sun}<span>Morning briefing</span></button>
        </div>
      </div>`;
    const rows = [
      rowMarkup("upnext", "Up next", keep("upnext", upNext), episodeCard("upnext"), `<a class="au-row-action" href="#podcasts/upnext">${ICON.queue}<span>manage</span></a>`),
      rowMarkup("continue", "Continue listening", keep("continue", d.continueListening), episodeCard("continue")),
      rowMarkup("new", "New episodes", keep("new", d.newEpisodes), episodeCard("new")),
      rowMarkup("short", "Short on time? Under 20 minutes", keep("short", d.shortOnes), episodeCard("short")),
      rowMarkup("downloaded", "Downloaded", keep("downloaded", d.downloaded), episodeCard("downloaded")),
      rowMarkup("subs", "Your shows", subs, showCard, `<a class="au-row-action" href="#podcasts/shows">${ICON.right}<span>all</span></a>`),
      rowMarkup("bookmarks", "Bookmarks", d.bookmarks, bookmarkCard, `<a class="au-row-action" href="#podcasts/bookmarks">${ICON.right}<span>all</span></a>`)
    ].join("");
    setContent(`<div class="au-home">${head}${rows}</div>`);
  }

  async function renderOnboarding(token) {
    const res = await request("/api/podcasts/recommended");
    if (token !== routeToken) return;
    const rec = res.ok && Array.isArray(res.data) ? res.data : [];
    discover.lists = { rec };
    setContent(`<div class="mv-setup au-setup">
        <div class="mv-setup-glyph">${ICON.mic}</div>
        <h2>Follow your first show</h2>
        <p>Search the podcast directory, paste an RSS address, or bring your subscriptions from another app as an OPML file. The server downloads new episodes and keeps them, even after a show drops them from its feed.</p>
        <div class="mv-actions au-center-actions">
          <button type="button" class="mv-btn mv-btn-primary" data-pc-nav="discover">${ICON.search}<span>Discover shows</span></button>
          <button type="button" class="mv-btn mv-btn-glass" data-act="opml-import">${ICON.download}<span>Import OPML</span></button>
        </div>
      </div>
      ${rec.length ? rowMarkup("rec", "Popular right now", rec, (s, i) => dirCard(s, i, "rec")) : ""}`);
  }

  // ---- Up Next page ---------------------------------------------------------------------------

  async function renderUpNext(token) {
    setContent(loadingMarkup());
    await loadQueue();
    if (token !== routeToken) return;
    renderUpNextList();
  }

  function renderUpNextList() {
    const total = upNext.reduce((s, e) => s + progressOf(e).left, 0);
    setContent(`<div class="au-page-head au-page-head-plain">
        <div><h2 class="au-page-title">Up Next</h2><div class="au-page-sub">${upNext.length ? `${plural(upNext.length, "episode")} · ${fmtDuration(total)}` : "Nothing queued"} · follows you to every device</div></div>
        <div class="mv-actions">
          ${upNext.length ? `<button type="button" class="mv-btn mv-btn-primary" data-act="play-upnext">${ICON.play}<span>Play all</span></button>` : ""}
          <button type="button" class="mv-btn mv-btn-glass" data-act="fill">${ICON.clock}<span>Fill my drive</span></button>
        </div></div>
      ${upNext.length > 1 ? `<div class="au-list-note au-edit-note">${ICON.drag} drag to reorder · finished episodes leave on their own · shows set to auto-queue add new episodes at the end</div>` : ""}
      ${episodeList("upnext", upNext, { type: "queue", id: "upnext", name: "Up Next" }, { sortable: true, removable: true, showTitle: true, empty: "Add episodes with the queue button on any episode, or turn on auto-queue in a show's settings." })}`);
    const list = el("pc-content").querySelector('.au-eps[data-key="upnext"]');
    if (list) U.makeSortable(list, reorderUpNext);
  }

  async function reorderUpNext(from, to) {
    upNext = U.moveInArray(upNext, from, to);
    renderUpNextList();
    const res = await request("/api/podcasts/queue", { method: "PUT", body: { episodeIds: upNext.map((e) => e.id) } });
    if (!res.ok) { showToast(res.error, false); await loadQueue(); renderUpNextList(); return; }
    if (Array.isArray(res.data)) upNext = res.data;
  }

  // ---- shows ----------------------------------------------------------------------------------

  let showAll = false;

  async function renderShows(token) {
    setContent(loadingMarkup());
    const res = await request(`/api/podcasts${showAll ? "?all=true" : ""}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(errorMarkup(res.error)); return; }
    const list = Array.isArray(res.data) ? res.data : [];
    setContent(`<div class="au-page-head au-page-head-plain">
        <div><h2 class="au-page-title">${showAll ? "Every show in the household" : "Your shows"}</h2><div class="au-page-sub">${plural(list.length, "show")}${showAll ? "" : ` · ${plural(list.reduce((s, p) => s + (Number(p.unplayedCount) || 0), 0), "unplayed episode")}`}</div></div>
        <div class="mv-actions">
          <button type="button" class="btn mv-toggle ${showAll ? "btn-on" : ""}" data-act="toggle-all" aria-pressed="${showAll}">household</button>
          <button type="button" class="mv-btn mv-btn-glass" data-act="refresh-all">${ICON.refresh}<span>Check for new</span></button>
          <button type="button" class="mv-btn mv-btn-primary" data-pc-nav="discover">${ICON.plus}<span>Add a show</span></button>
        </div></div>
      ${list.length ? `<div class="mv-grid au-grid">${list.map(showCard).join("")}</div>` : emptyMarkup("No shows yet", "Find some under Discover.", ICON.mic)}`);
  }

  async function renderShow(id, token) {
    if (showState.id !== id) Object.assign(showState, { id, filter: "all", sort: null, season: "", q: "", offset: 0 });
    if (!current || current.kind !== "show" || current.id !== id) setContent(backButton() + loadingMarkup());
    const res = await request(`/api/podcasts/${enc(id)}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(backButton() + errorMarkup(res.error)); return; }
    const p = res.data;
    current = { kind: "show", id, data: p };
    if (!showState.sort) showState.sort = (p.mySettings && p.mySettings.sort) || "newest";
    const funding = (p.funding || []).map((f) => ({ url: U.safeLink(f.url), title: f.title || "Support the show" })).filter((f) => f.url);
    const link = U.safeLink(p.link);
    const facts = [plural(p.episodeCount || 0, "episode"), p.subscribed && p.unplayedCount ? `${p.unplayedCount} unplayed` : "", p.subscriberCount ? `${plural(p.subscriberCount, "follower")} here` : "", p.lastRefreshedAt ? `checked ${U.ago(p.lastRefreshedAt)}` : "", p.explicit ? "explicit" : ""].filter(Boolean);
    setContent(`${backButton()}
      <div class="au-hero">
        <div class="au-hero-bg">${U.safeSrc(p.artUrl) ? `<img src="${h(p.artUrl)}" alt="">` : ""}</div>
        <div class="au-hero-art">${art(p.artUrl, "au-square", p.title, p.author, "", ICON.mic)}</div>
        <div class="au-hero-info">
          <div class="mv-kicker">Podcast</div>
          <h2 class="au-hero-title">${h(p.title)}</h2>
          <div class="au-hero-by">${h(p.author || "")}</div>
          <div class="mv-meta">${facts.map(h).join('<span class="mv-dot">&middot;</span>')}</div>
          ${(p.categories || []).length ? `<div class="mv-genres">${p.categories.map((c) => `<span class="mv-genre">${h(c)}</span>`).join("")}</div>` : ""}
          ${p.description ? `<p class="au-hero-desc au-clamp" data-act="expand">${h(p.description)}</p>` : ""}
          ${p.refreshError && U.isAdmin() ? `<div class="mv-note mv-note-warn">The last feed check failed: ${h(p.refreshError)}</div>` : ""}
          <div class="mv-actions">
            ${p.subscribed
              ? `<button type="button" class="mv-btn mv-btn-glass au-following" data-act="unsubscribe">${ICON.check}<span>Following</span></button>`
              : `<button type="button" class="mv-btn mv-btn-primary" data-act="subscribe">${ICON.plus}<span>Follow</span></button>`}
            <button type="button" class="mv-btn mv-btn-glass" data-act="play-latest">${ICON.play}<span>Latest</span></button>
            <button type="button" class="au-ctl" data-act="my-settings" title="Your settings for this show: speed, skip intro, notifications">${ICON.speed}</button>
            <button type="button" class="au-ctl" data-act="show-menu" title="More" aria-label="More">${ICON.more}</button>
          </div>
          ${funding.length || link ? `<div class="au-links">${funding.map((f) => `<a class="au-fund" href="${h(f.url)}" target="_blank" rel="noopener noreferrer">${ICON.heart}<span>${h(f.title)}</span></a>`).join("")}${link ? `<a class="au-sublink" href="${h(link)}" target="_blank" rel="noopener noreferrer">${ICON.external} website</a>` : ""}</div>` : ""}
        </div>
      </div>
      <div class="au-ep-filters mv-filters" id="pc-ep-filters">
        <div class="mv-seg" role="radiogroup">${[["all", "All"], ["unplayed", "Unplayed"], ["inProgress", "In progress"], ["downloaded", "Downloaded"], ["bookmarked", "Bookmarked"]].map(([k, l]) => `<button type="button" class="${showState.filter === k ? "active" : ""}" data-act="ep-filter" data-v="${k}">${l}</button>`).join("")}</div>
        <label class="mv-search">${ICON.search}<input type="search" class="field-input" id="pc-ep-q" placeholder="Search episodes" value="${h(showState.q)}"></label>
        <select class="field-input mv-select hidden" id="pc-ep-season" aria-label="Season"></select>
        <select class="field-input mv-select" id="pc-ep-sort" aria-label="Sort"><option value="newest">Newest first</option><option value="oldest">Oldest first</option></select>
        <span class="mv-count" id="pc-ep-count"></span>
      </div>
      <div id="pc-ep-host">${loadingMarkup()}</div>`);
    el("pc-ep-sort").value = showState.sort;
    await loadShowEpisodes(token);
  }

  async function loadShowEpisodes(token) {
    const host = el("pc-ep-host");
    if (!host || !current || current.kind !== "show") return;
    host.classList.add("mv-busy");
    const params = new URLSearchParams({ sort: showState.sort, filter: showState.filter, offset: String(showState.offset), limit: String(EP_PAGE) });
    if (showState.q) params.set("q", showState.q);
    if (showState.season) params.set("season", showState.season);
    const res = await request(`/api/podcasts/${enc(current.id)}/episodes?${params}`);
    if (token != null && token !== routeToken) return;
    host.classList.remove("mv-busy");
    if (!res.ok) { host.innerHTML = errorMarkup(res.error); return; }
    const d = res.data || {};
    const items = d.items || [];
    const seasons = d.seasons || [];
    const seasonSel = el("pc-ep-season");
    seasonSel.classList.toggle("hidden", seasons.length < 2);
    seasonSel.innerHTML = `<option value="">All seasons</option>` + seasons.map((s) => `<option value="${h(s)}">Season ${h(s)}</option>`).join("");
    seasonSel.value = showState.season;
    el("pc-ep-count").textContent = d.total ? plural(d.total, "episode") : "";
    const total = d.total || 0;
    const to = Math.min(total, showState.offset + items.length);
    const pager = total > EP_PAGE ? `<div class="mv-pager">
        <button type="button" class="btn" data-act="ep-page" data-dir="-1" ${showState.offset <= 0 ? "disabled" : ""}>${ICON.left} newer</button>
        <span>${showState.offset + 1}&ndash;${to} of ${total}</span>
        <button type="button" class="btn" data-act="ep-page" data-dir="1" ${to >= total ? "disabled" : ""}>older ${ICON.right}</button></div>` : "";
    host.innerHTML = episodeList("show", items, { type: "podcast", id: current.id, name: current.data.title }, { art: false, empty: showState.filter === "all" && !showState.q ? "No episodes in this feed yet." : "No episodes match." }) + pager;
    wireImages(host);
    A.markNow(host);
  }

  function showMenu(anchor) {
    const p = current && current.data;
    if (!p) return;
    U.openMenu(anchor, [
      { label: "Check for new episodes", icon: ICON.refresh, run: () => refreshShow(p) },
      { label: "Mark every episode played", icon: ICON.check, run: () => markAllPlayed(p, true) },
      { label: "Mark every episode unplayed", icon: ICON.check, run: () => markAllPlayed(p, false) },
      { label: "Your settings…", icon: ICON.speed, run: () => openMySettings(p) },
      U.isAdmin() ? { label: "Household settings…", icon: ICON.eq, run: () => openShowSettings(p) } : null,
      { label: "Copy the feed address", icon: ICON.copy, run: () => copyText(p.feedUrl, "Feed address copied") },
      U.isAdmin() ? "-" : null,
      U.isAdmin() ? { label: "Delete show for everyone", icon: ICON.trash, danger: true, run: () => deleteShow(p) } : null
    ], { title: p.title });
  }

  async function copyText(text, done) {
    try { await navigator.clipboard.writeText(text); showToast(done, true); }
    catch { prompt("Copy this:", text); }
  }

  async function refreshShow(p) {
    showToast("Checking the feed…", true);
    const res = await request(`/api/podcasts/${enc(p.id)}/refresh`, { method: "POST" });
    if (!res.ok) { showToast(res.error, false); return; }
    showToast("Feed checked", true);
    refreshCurrent();
  }

  async function markAllPlayed(p, played) {
    if (played && !confirm(`Mark every episode of ${p.title} played?`)) return;
    const res = await request(`/api/podcasts/${enc(p.id)}/played`, { method: "POST", body: { played } });
    if (!res.ok) { showToast(res.error, false); return; }
    showToast(played ? "All marked played" : "All marked unplayed", true);
    refreshCurrent();
  }

  async function subscribe(body) {
    const res = await request("/api/podcasts/subscriptions", { method: "POST", body });
    if (!res.ok) { showToast(res.error, false); return null; }
    showToast(`Following ${res.data.title}`, true);
    return res.data;
  }

  async function unsubscribe(p) {
    if (!confirm(`Stop following ${p.title}? Your progress and bookmarks are kept.`)) return;
    const res = await request(`/api/podcasts/${enc(p.id)}/subscription`, { method: "DELETE" });
    if (!res.ok) { showToast(res.error, false); return; }
    showToast(`Unfollowed ${p.title}`, true);
    refreshCurrent();
  }

  async function deleteShow(p) {
    if (!confirm(`Delete ${p.title} for everyone? Its episodes, downloads, progress and bookmarks are forgotten.`)) return;
    const res = await request(`/api/podcasts/${enc(p.id)}`, { method: "DELETE" });
    if (!res.ok) { showToast(res.error, false); return; }
    showToast("Show deleted", true);
    go({ name: "shows" });
  }

  async function playLatest() {
    const p = current && current.data;
    if (!p) return;
    const res = await request(`/api/podcasts/${enc(p.id)}/episodes?${new URLSearchParams({ sort: "newest", filter: "unplayed", limit: "1" })}`);
    let ep = res.ok && res.data && res.data.items && res.data.items[0];
    if (!ep) {
      const any = await request(`/api/podcasts/${enc(p.id)}/episodes?${new URLSearchParams({ sort: "newest", limit: "1" })}`);
      ep = any.ok && any.data && any.data.items && any.data.items[0];
    }
    if (!ep) { showToast("No episodes to play yet.", false); return; }
    playEpisode(ep);
  }

  function switchRow(id, label, sub, checked) {
    return `<label class="au-check-row"><span class="switch"><input type="checkbox" id="${id}" ${checked ? "checked" : ""}><span class="switch-track"></span></span><span>${label}${sub ? ` <small>${sub}</small>` : ""}</span></label>`;
  }

  function openMySettings(p) {
    const my = p.mySettings || {};
    const speeds = [0.8, 0.9, 1, 1.1, 1.2, 1.25, 1.3, 1.4, 1.5, 1.75, 2, 2.5, 3];
    U.openModal(`${U.modalHeader("Your settings", h(p.title))}
      <form class="mv-form" id="pc-my-form">
        <label class="mv-field"><span>Speed</span><select class="field-input" id="pc-my-speed"><option value="">Same as other shows</option>${speeds.map((s) => `<option value="${s}" ${Number(my.playbackSpeed) === s ? "selected" : ""}>${s}×</option>`).join("")}</select></label>
        <div class="au-two-col">
          <label class="mv-field"><span>Skip intro (seconds)</span><input class="field-input" type="number" min="0" max="600" id="pc-my-intro" value="${h(my.skipIntroSeconds || 0)}"></label>
          <label class="mv-field"><span>Skip outro (seconds)</span><input class="field-input" type="number" min="0" max="600" id="pc-my-outro" value="${h(my.skipOutroSeconds || 0)}"></label>
        </div>
        ${switchRow("pc-my-notify", "Notify me", "push when a new episode arrives", my.notify !== false)}
        ${switchRow("pc-my-queue", "Auto-queue", "put each new episode at the end of Up Next", !!my.autoQueue)}
        ${switchRow("pc-my-brief", "In my Morning Briefing", "its newest episode plays after the weather and calendar", !!my.inBriefing)}
        <ul class="field-errors" id="pc-my-errors"></ul>
        <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Cancel</button><button type="submit" class="mv-btn mv-btn-primary">Save</button></div>
      </form>`);
  }

  async function saveMySettings() {
    const p = current && current.data;
    if (!p) return;
    const speed = el("pc-my-speed").value;
    const body = {
      ...(p.mySettings || {}),
      playbackSpeed: speed ? Number(speed) : null,
      skipIntroSeconds: Math.max(0, Number(el("pc-my-intro").value) || 0),
      skipOutroSeconds: Math.max(0, Number(el("pc-my-outro").value) || 0),
      notify: el("pc-my-notify").checked, autoQueue: el("pc-my-queue").checked, inBriefing: el("pc-my-brief").checked,
      sort: showState.sort || (p.mySettings && p.mySettings.sort) || "newest"
    };
    const res = await request(`/api/podcasts/${enc(p.id)}/my-settings`, { method: "PUT", body });
    if (!res.ok) { U.showErrors(el("pc-my-errors"), res.error); return; }
    A.refreshShow(p.id);
    U.closeModal();
    showToast("Saved", true);
    refreshCurrent();
  }

  function openShowSettings(p) {
    const s = p.settings || {};
    const tr = status && status.transcripts;
    U.openModal(`${U.modalHeader("Household settings", `${h(p.title)} · these apply to everyone`)}
      <form class="mv-form" id="pc-show-form">
        ${switchRow("pc-s-auto", "Download new episodes", "onto the server, as they come out", s.autoDownload !== false)}
        <label class="mv-field"><span>Keep the newest (0 = keep all)</span><input class="field-input" type="number" min="0" max="1000" id="pc-s-keep" value="${h(s.keepEpisodes ?? 10)}"></label>
        ${switchRow("pc-s-voice", "Voice boost", "evens out loudness and clarity", !!s.voiceBoost)}
        ${switchRow("pc-s-trim", "Trim silence", "cuts pauses over a second down to 0.3 s when downloading", !!s.trimSilence)}
        ${switchRow("pc-s-trans", "Transcribe new episodes", tr && !tr.enabled ? "transcripts are off in Settings → Podcasts" : tr && tr.available === false ? h(tr.error || "whisper isn't set up") : "written on this server with whisper", !!s.transcribe)}
        <ul class="field-errors" id="pc-show-errors"></ul>
        <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Cancel</button><button type="submit" class="mv-btn mv-btn-primary">Save</button></div>
      </form>`);
  }

  async function saveShowSettings() {
    const p = current && current.data;
    if (!p) return;
    const body = {
      autoDownload: el("pc-s-auto").checked, keepEpisodes: Math.max(0, Number(el("pc-s-keep").value) || 0),
      voiceBoost: el("pc-s-voice").checked, trimSilence: el("pc-s-trim").checked, transcribe: el("pc-s-trans").checked
    };
    const res = await request(`/api/podcasts/${enc(p.id)}/settings`, { method: "PUT", body });
    if (!res.ok) { U.showErrors(el("pc-show-errors"), res.error); return; }
    U.closeModal();
    showToast("Saved for the household", true);
    refreshCurrent();
  }

  // ---- episode page ---------------------------------------------------------------------------

  async function renderEpisode(id, token) {
    if (!current || current.kind !== "episode" || current.id !== id) setContent(backButton() + loadingMarkup());
    const [res] = await Promise.all([request(`/api/podcasts/episodes/${enc(id)}`), status ? null : refreshStatus(), upNext.length ? null : loadQueue()]);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(backButton() + errorMarkup(res.error)); return; }
    const ep = res.data;
    const keepTranscript = current && current.kind === "episode" && current.id === id && current.showTranscript;
    current = { kind: "episode", id, data: ep, showTranscript: !!keepTranscript };
    lists.set("episode", { episodes: [ep], context: null });
    const pr = progressOf(ep);
    const d = ep.download || {};
    const inQueue = upNext.some((u) => u.id === ep.id) || ep.inQueue;
    const tr = status && status.transcripts;
    const canTranscribe = !ep.hasTranscript && tr && tr.enabled && tr.available !== false;
    const dlLabel = d.state === "done" ? `on the server${d.enhanced ? " · enhanced" : ""}${d.timeSavedSeconds ? ` · ${fmtDuration(d.timeSavedSeconds)} silence trimmed` : ""}`
      : d.state === "downloading" ? `downloading ${Math.round((d.progress || 0) * 100)}%` : d.state === "queued" ? "waiting to download" : d.state === "failed" ? `download failed: ${d.error || ""}` : "streams from the publisher";
    const link = U.safeLink(ep.link);
    setContent(`${backButton()}
      <div class="au-hero au-hero-episode">
        <div class="au-hero-bg">${U.safeSrc(ep.artUrl) ? `<img src="${h(ep.artUrl)}" alt="">` : ""}</div>
        <div class="au-hero-art">${art(ep.artUrl, "au-square", ep.podcastTitle, "", progressBarHtml(ep), ICON.mic)}</div>
        <div class="au-hero-info">
          <a class="mv-kicker au-kicker-link" href="#podcasts/show/${enc(ep.podcastId)}">${h(ep.podcastTitle || "Podcast")}</a>
          <h2 class="au-hero-title au-hero-title-sm">${h(ep.title)}</h2>
          <div class="mv-meta">${epMeta(ep).map(h).join('<span class="mv-dot">&middot;</span>')}${ep.explicit ? '<span class="mv-dot">&middot;</span><span class="mv-cert">E</span>' : ""}${ep.archived ? '<span class="mv-dot">&middot;</span><span class="mv-tag" title="No longer in the feed - kept on the server">archived</span>' : ""}</div>
          <div class="mv-actions">
            <button type="button" class="mv-btn mv-btn-primary" data-act="play-list" data-list="episode" data-i="0">${ICON.play}<span>${pr.started ? `Resume · ${fmtDuration(pr.left)} left` : "Play"}</span></button>
            ${pr.started ? `<button type="button" class="mv-btn mv-btn-glass" data-act="play-start">${ICON.back15}<span>From the start</span></button>` : ""}
            <button type="button" class="au-ctl ${inQueue ? "on" : ""}" data-act="${inQueue ? "queue-remove" : "queue-add"}" data-id="${h(ep.id)}" title="${inQueue ? "In Up Next (click to remove)" : "Add to Up Next"}">${inQueue ? ICON.check : ICON.queue}</button>
            <button type="button" class="au-ctl ${pr.played ? "on" : ""}" data-act="toggle-played" data-list="episode" data-i="0" title="${pr.played ? "Mark unplayed" : "Mark played"}">${ICON.check}</button>
            <button type="button" class="au-ctl" data-act="ep-bookmark" title="Bookmark a moment">${ICON.bookmark}</button>
            <button type="button" class="au-ctl" data-act="ep-clip" title="Make a clip to share">${ICON.scissors}</button>
            <button type="button" class="au-ctl" data-act="ep-menu" data-list="episode" data-i="0" title="More">${ICON.more}</button>
          </div>
          <div class="au-ep-dl">
            <span class="au-ep-dl-state au-dl-${h(d.state || "none")}">${ICON.download}<span>${h(dlLabel)}</span></span>
            ${d.state === "done" ? (U.isAdmin() ? `<button type="button" class="mv-btn mv-btn-quiet" data-act="dl-remove-ep">remove download</button>` : "")
              : d.state === "downloading" || d.state === "queued" ? "" : `<button type="button" class="mv-btn mv-btn-quiet" data-act="dl-ep">download to the server</button>`}
            ${canTranscribe ? `<button type="button" class="mv-btn mv-btn-quiet" data-act="transcribe">${ICON.transcript}<span>transcribe</span></button>` : ""}
            <a class="mv-btn mv-btn-quiet" href="${h(U.withQuery(ep.audioUrl || `/api/podcasts/episodes/${enc(ep.id)}/audio`, { download: "1" }))}" download>${ICON.download}<span>save file</span></a>
            ${link ? `<a class="mv-btn mv-btn-quiet" href="${h(link)}" target="_blank" rel="noopener noreferrer">${ICON.external}<span>web page</span></a>` : ""}
          </div>
        </div>
      </div>
      <div class="au-ep-page">
        <section class="mv-section au-notes"><h3 class="mv-section-title">Show notes</h3>
          <div class="au-notes-body">${ep.description ? U.sanitizeHtml(ep.description) : h(ep.descriptionText || ep.summary || "No description.")}</div></section>
        <section class="mv-section" id="pc-ep-chapters"></section>
        <section class="mv-section" id="pc-ep-bookmarks"></section>
        <section class="mv-section" id="pc-ep-transcript">
          <h3 class="mv-section-title">Transcript</h3>
          ${ep.hasTranscript ? `<div id="pc-tr-host">${current.showTranscript ? loadingMarkup() : `<button type="button" class="mv-btn" data-act="show-transcript">${ICON.transcript}<span>show the transcript</span></button>`}</div>`
            : `<div class="au-q-empty">${canTranscribe ? "None yet. Press transcribe above and the server writes one (it downloads the episode first if it has to)." : tr && !tr.enabled ? "None in the feed. Transcripts can be written on the server once they're turned on in Settings → Podcasts." : "None in the feed."}</div>`}
        </section>
      </div>`);
    loadEpisodeExtras(ep, token);
    if (current.showTranscript) loadTranscript(ep);
    if (d.state === "downloading" || d.state === "queued") dlTimer = setTimeout(() => { if (route.name === "episode" && route.id === id) routeTo(route); }, 3000);
  }

  async function loadEpisodeExtras(ep, token) {
    const [ch, bm] = await Promise.all([
      ep.hasChapters ? request(`/api/podcasts/episodes/${enc(ep.id)}/chapters`) : Promise.resolve({ ok: true, data: [] }),
      request(`/api/podcasts/bookmarks?${new URLSearchParams({ episodeId: ep.id })}`)
    ]);
    if (token !== routeToken) return;
    const chapters = ch.ok && Array.isArray(ch.data) ? ch.data : [];
    const chBox = el("pc-ep-chapters");
    if (chBox) chBox.innerHTML = chapters.length ? `<h3 class="mv-section-title">Chapters <span class="mv-section-sub">${chapters.length}</span></h3>
      <div class="au-chapters au-chapters-page">${chapters.map((c, i) => `<div class="au-chapter"><button type="button" class="au-chapter-main" data-act="play-at" data-t="${Number(c.startSeconds) || 0}">
        ${U.safeSrc(c.imageUrl) ? `<img src="${h(c.imageUrl)}" alt="" loading="lazy">` : `<span class="au-chapter-n">${i + 1}</span>`}
        <span class="au-chapter-title">${h(c.title || `Chapter ${i + 1}`)}</span><span class="au-chapter-time">${fmtClock(c.startSeconds)}</span></button></div>`).join("")}</div>` : "";
    const bookmarks = bm.ok && Array.isArray(bm.data) ? bm.data : [];
    const bmBox = el("pc-ep-bookmarks");
    if (bmBox) bmBox.innerHTML = bookmarks.length ? `<h3 class="mv-section-title">Your bookmarks <span class="mv-section-sub">${bookmarks.length}</span></h3>${bookmarkList(bookmarks, false)}` : "";
    wireImages(el("pc-content"));
  }

  async function loadTranscript(ep) {
    const host = el("pc-tr-host");
    if (!host) return;
    host.innerHTML = loadingMarkup();
    const res = await request(`/api/podcasts/episodes/${enc(ep.id)}/transcript`);
    if (!el("pc-tr-host")) return;
    if (!res.ok) { host.innerHTML = `<div class="au-q-empty">${h(res.error)}</div>`; return; }
    const segs = (res.data && res.data.segments) || [];
    let last = null;
    host.innerHTML = `<div class="au-tr-bar"><label class="mv-search au-tr-search">${ICON.search}<input type="search" class="field-input" id="pc-tr-q" placeholder="Search this transcript"></label><span class="au-tr-count" id="pc-tr-count"></span></div>
      <div class="au-transcript au-transcript-page" id="pc-tr">${segs.map((s) => {
        const sp = s.speaker && s.speaker !== last ? `<span class="au-tr-speaker">${h(s.speaker)}</span>` : "";
        last = s.speaker || last;
        return `<button type="button" class="au-line au-tr-seg" data-act="play-at" data-t="${Number(s.start) || 0}">${sp}<span class="au-tr-time">${fmtClock(s.start)}</span><span class="au-tr-text">${h(s.text)}</span></button>`;
      }).join("")}</div>
      <div class="au-words-source">${res.data && res.data.source === "whisper" ? "Transcribed on this server by whisper" : "From the podcast's feed"} · press a line to play from there</div>`;
  }

  function filterTranscript(q) {
    const box = el("pc-tr");
    if (!box) return;
    const needle = q.trim().toLowerCase();
    let n = 0;
    let first = null;
    for (const row of box.querySelectorAll(".au-tr-seg")) {
      const text = row.querySelector(".au-tr-text");
      const plain = text.textContent;
      const hit = needle && plain.toLowerCase().includes(needle);
      row.classList.toggle("au-tr-dim", !!needle && !hit);
      text.innerHTML = hit ? h(plain).replace(new RegExp(h(needle).replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi"), (m) => `<mark>${m}</mark>`) : h(plain);
      if (hit) { n++; if (!first) first = row; }
    }
    el("pc-tr-count").textContent = needle ? `${n} found` : "";
    if (first) first.scrollIntoView({ block: "center", behavior: "smooth" });
  }

  async function playAt(seconds) {
    const ep = current && current.kind === "episode" ? current.data : null;
    if (!ep) return;
    const now = A.current();
    if (now && now.type === "episode" && now.id === ep.id) { A.seek(seconds); if (!A.isPlaying()) A.resume(); return; }
    playEpisode(ep, seconds);
  }

  async function transcribe(button) {
    const ep = current && current.data;
    if (!ep) return;
    button.disabled = true;
    const res = await request(`/api/podcasts/episodes/${enc(ep.id)}/transcribe`, { method: "POST" });
    button.disabled = false;
    if (!res.ok) { showToast(res.error, false); return; }
    showToast("Queued for transcription - it appears here when it's done", true);
  }

  // ---- bookmarks ------------------------------------------------------------------------------

  function bookmarkList(list, withShow) {
    return `<div class="au-bms">${list.map((b) => `<div class="au-bm" data-bm="${h(b.id)}" data-now-id="${h(b.episodeId)}">
      <button type="button" class="au-bm-play" data-act="bm-play" data-ep="${h(b.episodeId)}" data-t="${Number(b.positionSeconds) || 0}" title="Play from here">${ICON.play}<span>${fmtClock(b.positionSeconds)}</span></button>
      <div class="au-bm-body">
        ${withShow ? `<div class="au-bm-where">${U.safeSrc(b.artUrl) ? `<img src="${h(b.artUrl)}" alt="" loading="lazy">` : ""}<a class="au-sublink" href="#podcasts/episode/${enc(b.episodeId)}">${h(b.episodeTitle || "")}</a> <span class="au-bm-showname">${h(b.podcastTitle || "")}</span></div>` : ""}
        <div class="au-bm-note-line">${b.note ? h(b.note) : '<span class="au-dim">no note</span>'}</div>
        ${b.transcriptSnippet ? `<blockquote class="au-bm-quote">${h(b.transcriptSnippet)}</blockquote>` : ""}
        <div class="au-bm-date">${h(U.ago(b.createdAt))}</div>
      </div>
      <div class="au-bm-actions">
        <button type="button" class="au-ctl au-ctl-xs" data-act="bm-note" data-id="${h(b.id)}" title="Save to a note in the Organizer">${ICON.transcript}</button>
        <button type="button" class="au-ctl au-ctl-xs" data-act="bm-edit" data-id="${h(b.id)}" title="Edit the note">${ICON.edit}</button>
        <button type="button" class="au-ctl au-ctl-xs" data-act="bm-delete" data-id="${h(b.id)}" title="Delete">${ICON.trash}</button>
      </div>
    </div>`).join("")}</div>`;
  }

  let bookmarksCache = [];

  async function renderBookmarks(token) {
    setContent(loadingMarkup());
    const res = await request("/api/podcasts/bookmarks");
    if (token !== routeToken) return;
    if (!res.ok) { setContent(errorMarkup(res.error)); return; }
    bookmarksCache = Array.isArray(res.data) ? res.data : [];
    setContent(`<div class="au-page-head au-page-head-plain"><div><h2 class="au-page-title">Bookmarks</h2>
        <div class="au-page-sub">Moments worth coming back to. Press <kbd>B</kbd> while an episode plays, or the bookmark button in the player.</div></div></div>
      ${bookmarksCache.length ? bookmarkList(bookmarksCache, true) : emptyMarkup("No bookmarks yet", "", ICON.bookmark)}`);
  }

  async function playBookmark(episodeId, t) {
    const now = A.current();
    if (now && now.type === "episode" && now.id === episodeId) { A.seek(t); if (!A.isPlaying()) A.resume(); return; }
    const res = await request(`/api/podcasts/episodes/${enc(episodeId)}`);
    if (!res.ok) { showToast(res.error, false); return; }
    playEpisode(res.data, t);
  }

  function findBookmark(id) {
    return bookmarksCache.find((b) => b.id === id) || null;
  }

  async function bookmarkToNote(id, button) {
    button.disabled = true;
    const res = await request(`/api/podcasts/bookmarks/${enc(id)}/to-note`, { method: "POST" });
    button.disabled = false;
    if (res.status === 404 && !res.data) { showToast("The Organizer is turned off, so there's nowhere to save it.", false); return; }
    if (!res.ok) { showToast(res.error, false); return; }
    showToast("Saved as a note in the Organizer", true);
  }

  async function editBookmark(id) {
    let b = findBookmark(id);
    if (!b) {
      const res = await request(`/api/podcasts/bookmarks${current && current.kind === "episode" ? `?episodeId=${enc(current.id)}` : ""}`);
      b = res.ok && Array.isArray(res.data) ? res.data.find((x) => x.id === id) : null;
    }
    if (!b) return;
    U.openModal(`${U.modalHeader("Bookmark note", `${h(b.episodeTitle || "")} · ${fmtClock(b.positionSeconds)}`)}
      <form class="mv-form" id="pc-bm-edit" data-id="${h(b.id)}">
        <textarea class="field-textarea" id="pc-bm-edit-note" rows="4" maxlength="2000" autofocus>${h(b.note || "")}</textarea>
        <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Cancel</button><button type="submit" class="mv-btn mv-btn-primary">Save</button></div>
      </form>`);
  }

  async function deleteBookmark(id) {
    if (!confirm("Delete this bookmark?")) return;
    const res = await request(`/api/podcasts/bookmarks/${enc(id)}`, { method: "DELETE" });
    if (!res.ok) { showToast(res.error, false); return; }
    refreshCurrent();
  }

  function parseClock(text) {
    const parts = String(text || "").trim().split(":").map((x) => Number(x));
    if (!parts.length || parts.some((x) => !isFinite(x))) return null;
    return parts.reduce((acc, x) => acc * 60 + x, 0);
  }

  function openBookmark(ep, position) {
    if (!ep) return;
    const now = A.current();
    const pos = position || (now && now.type === "episode" && now.id === ep.id ? A.position() : 0);
    bookmarkTarget = ep;
    U.openModal(`${U.modalHeader("Bookmark", h(ep.title))}
      <form class="mv-form" id="pc-bm-new">
        <label class="mv-field"><span>At</span><input class="field-input au-time-input" id="pc-bm-at" value="${fmtClock(pos)}" inputmode="numeric"></label>
        <label class="mv-field"><span>Note</span><textarea class="field-textarea" id="pc-bm-note" rows="3" maxlength="2000" placeholder="What was worth keeping?" autofocus></textarea></label>
        <div class="mv-field-note">When the episode has a transcript, the words around this moment are saved with it.</div>
        <ul class="field-errors" id="pc-bm-errors"></ul>
        <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Cancel</button><button type="submit" class="mv-btn mv-btn-primary">${ICON.bookmark}<span>Save</span></button></div>
      </form>`);
  }

  let bookmarkTarget = null;

  async function saveBookmark() {
    const ep = bookmarkTarget;
    if (!ep) return;
    const at = parseClock(el("pc-bm-at").value);
    if (at == null) { U.showErrors(el("pc-bm-errors"), "Write the time like 12:34 or 1:02:03."); return; }
    const res = await request(`/api/podcasts/episodes/${enc(ep.id)}/bookmarks`, { method: "POST", body: { positionSeconds: at, note: el("pc-bm-note").value.trim() } });
    if (!res.ok) { U.showErrors(el("pc-bm-errors"), res.error); return; }
    U.closeModal();
    showToast(`Bookmarked ${fmtClock(at)}`, true);
    if (route.name === "bookmarks" || (route.name === "episode" && route.id === ep.id)) refreshCurrent();
  }

  // ---- clips ----------------------------------------------------------------------------------

  let clipTarget = null;

  function previewAudio() {
    if (!preview.audio) {
      preview.audio = new Audio();
      preview.audio.addEventListener("ended", () => { preview.playingUrl = null; drawPreviewButtons(); });
      preview.audio.addEventListener("pause", () => drawPreviewButtons());
      preview.audio.addEventListener("play", () => drawPreviewButtons());
    }
    return preview.audio;
  }

  function stopPreview() {
    if (preview.audio) { preview.audio.pause(); preview.audio.removeAttribute("src"); try { preview.audio.load(); } catch { /* nothing */ } }
    preview.playingUrl = null;
    clearTimeout(preview.stopTimer);
  }

  function drawPreviewButtons() {
    for (const b of document.querySelectorAll("[data-preview-url]")) {
      const on = preview.playingUrl === b.dataset.previewUrl && preview.audio && !preview.audio.paused;
      b.classList.toggle("on", on);
      b.innerHTML = on ? `${ICON.pause}<span>stop</span>` : `${ICON.play}<span>${h(b.dataset.label || "preview")}</span>`;
    }
  }

  function togglePreview(url, start, length) {
    const audio = previewAudio();
    if (preview.playingUrl === url && !audio.paused) { stopPreview(); drawPreviewButtons(); return; }
    if (A.isPlaying()) A.pause();
    clearTimeout(preview.stopTimer);
    preview.playingUrl = url;
    if (audio.getAttribute("src") !== url) audio.src = url;
    const begin = () => {
      if (start) audio.currentTime = start;
      audio.play().catch(() => showToast("This browser wouldn't play the preview.", false));
      if (length) preview.stopTimer = setTimeout(() => { audio.pause(); preview.playingUrl = null; drawPreviewButtons(); }, length * 1000);
    };
    if (audio.readyState >= 1) begin(); else audio.addEventListener("loadedmetadata", begin, { once: true });
    drawPreviewButtons();
  }

  function openClipMaker(ep, position) {
    if (!ep) return;
    clipTarget = ep;
    const now = A.current();
    const pos = position || (now && now.type === "episode" && now.id === ep.id ? A.position() : 0);
    const start = Math.max(0, Math.floor(pos - 15));
    U.openModal(`${U.modalHeader("Make a clip", h(ep.title))}
      <form class="mv-form" id="pc-clip-form">
        <div class="au-two-col">
          <label class="mv-field"><span>Starts at</span>
            <div class="mv-path-row"><button type="button" class="mv-btn" data-mact="pc-clip-nudge" data-d="-5">-5</button><input class="field-input au-time-input" id="pc-clip-start" value="${fmtClock(start)}"><button type="button" class="mv-btn" data-mact="pc-clip-nudge" data-d="5">+5</button></div></label>
          <label class="mv-field"><span>Length <b id="pc-clip-len-v">30 s</b></span><input type="range" class="field-range" min="5" max="180" step="1" value="30" id="pc-clip-len"></label>
        </div>
        <label class="mv-field"><span>Title (optional)</span><input class="field-input" id="pc-clip-title" maxlength="120" placeholder="${h(ep.podcastTitle || "")} highlight"></label>
        <div class="mv-actions"><button type="button" class="mv-btn" data-mact="pc-clip-preview" data-preview-url="${h(ep.audioUrl || "")}" data-label="hear it first">${ICON.play}<span>hear it first</span></button></div>
        <ul class="field-errors" id="pc-clip-errors"></ul>
        <div id="pc-clip-result"></div>
        <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Close</button><button type="submit" class="mv-btn mv-btn-primary">${ICON.scissors}<span>Make the clip</span></button></div>
      </form>`, stopPreview);
    el("pc-clip-len").addEventListener("input", (e) => { el("pc-clip-len-v").textContent = `${e.target.value} s`; });
  }

  async function makeClip(form) {
    const ep = clipTarget;
    if (!ep) return;
    const start = parseClock(el("pc-clip-start").value);
    const len = Number(el("pc-clip-len").value) || 30;
    if (start == null) { U.showErrors(el("pc-clip-errors"), "Write the start like 12:34."); return; }
    const submit = form.querySelector('[type="submit"]');
    submit.disabled = true;
    submit.classList.add("mv-working");
    const res = await request(`/api/podcasts/episodes/${enc(ep.id)}/clips`, { method: "POST", body: { startSeconds: start, durationSeconds: len, title: el("pc-clip-title").value.trim() } });
    submit.disabled = false;
    submit.classList.remove("mv-working");
    if (!res.ok) { U.showErrors(el("pc-clip-errors"), res.error); return; }
    U.showErrors(el("pc-clip-errors"), []);
    const url = U.safeSrc(res.data.url);
    el("pc-clip-result").innerHTML = url ? `<div class="au-clip-made">
        <div class="au-clip-made-title">${ICON.check} Clip ready · ${h(res.data.durationSeconds || len)} s</div>
        <audio controls src="${h(url)}"></audio>
        <div class="mv-actions"><a class="mv-btn mv-btn-primary" href="${h(url)}" download>${ICON.download}<span>download</span></a>
          <button type="button" class="mv-btn" data-mact="pc-clip-share" data-url="${h(url)}" data-title="${h(el("pc-clip-title").value.trim() || ep.title)}">${ICON.share}<span>share</span></button></div>
      </div>` : "";
  }

  async function shareClip(url, title) {
    const absolute = new URL(url, location.href).href;
    try {
      if (navigator.canShare) {
        const blob = await fetch(url).then((r) => r.blob());
        const file = new File([blob], `${title.replace(/[^\w\- ]+/g, "").slice(0, 60) || "clip"}.m4a`, { type: blob.type || "audio/mp4" });
        if (navigator.canShare({ files: [file] })) { await navigator.share({ files: [file], title }); return; }
      }
      if (navigator.share) { await navigator.share({ url: absolute, title }); return; }
    } catch (err) {
      if (err && err.name === "AbortError") return;
    }
    copyText(absolute, "Link to the clip copied");
  }

  let clipsCache = [];

  async function renderClips(token) {
    setContent(loadingMarkup());
    const res = await request("/api/podcasts/clips");
    if (token !== routeToken) return;
    if (!res.ok) { setContent(errorMarkup(res.error)); return; }
    clipsCache = Array.isArray(res.data) ? res.data : [];
    setContent(`<div class="au-page-head au-page-head-plain"><div><h2 class="au-page-title">Clips</h2>
        <div class="au-page-sub">Short pieces of episodes to share. Make one from the player or an episode's page.</div></div></div>
      ${clipsCache.length ? `<div class="au-clips">${clipsCache.map((c) => {
        const url = U.safeSrc(c.url) || `/api/podcasts/clips/${enc(c.id)}.m4a`;
        const title = c.title || c.episodeTitle || "Clip";
        return `<div class="au-clip">
          <button type="button" class="au-bm-play" data-preview-url="${h(url)}" data-label="${h(fmtClock(c.durationSeconds || 0))}" data-act="clip-play">${ICON.play}<span>${fmtClock(c.durationSeconds || 0)}</span></button>
          <div class="au-bm-body"><div class="au-bm-note-line">${h(title)}</div>
            <div class="au-bm-date">${h([c.podcastTitle, c.episodeTitle && c.episodeTitle !== title ? c.episodeTitle : "", c.startSeconds != null ? `from ${fmtClock(c.startSeconds)}` : "", c.createdAt ? U.ago(c.createdAt) : ""].filter(Boolean).join(" · "))}</div></div>
          <div class="au-bm-actions">
            <a class="au-ctl au-ctl-xs" href="${h(url)}" download title="Download">${ICON.download}</a>
            <button type="button" class="au-ctl au-ctl-xs" data-act="clip-share" data-url="${h(url)}" data-title="${h(title)}" title="Share">${ICON.share}</button>
            <button type="button" class="au-ctl au-ctl-xs" data-act="clip-delete" data-id="${h(c.id)}" title="Delete">${ICON.trash}</button>
          </div></div>`;
      }).join("")}</div>` : emptyMarkup("No clips yet", "", ICON.scissors)}`);
  }

  // ---- discover -------------------------------------------------------------------------------

  async function renderDiscover(token) {
    discover.lists = discover.lists || {};
    setContent(`<div class="au-page-head au-page-head-plain"><div><h2 class="au-page-title">Discover</h2><div class="au-page-sub">Search the podcast directory, browse the charts, or add any show by its RSS address.</div></div>
        <div class="mv-actions">
          <button type="button" class="mv-btn mv-btn-glass" data-act="opml-import">${ICON.download}<span>Import OPML</span></button>
          <a class="mv-btn mv-btn-glass" href="/api/podcasts/opml" download="subscriptions.opml">${ICON.share}<span>Export OPML</span></a>
        </div></div>
      <form class="mv-match-form au-dir-search" id="pc-dir-form"><label class="mv-search">${ICON.search}<input type="search" class="field-input" id="pc-dir-q" placeholder="Search for a show" value="${h(discover.q)}" autocomplete="off"></label><button type="submit" class="mv-btn mv-btn-primary">search</button></form>
      <div id="pc-dir-results"></div>
      <form class="mv-match-form au-rss-form" id="pc-rss-form"><input class="field-input" id="pc-rss" placeholder="…or paste an RSS feed address: https://example.com/feed.xml" autocomplete="off" spellcheck="false"><button type="submit" class="mv-btn">${ICON.plus}<span>follow</span></button></form>
      <div id="pc-rec"></div>
      <section class="mv-section"><h3 class="mv-section-title">Top charts</h3><div class="au-chips" id="pc-genres"></div><div id="pc-top">${loadingMarkup()}</div></section>`);
    if (discover.q && discover.lists.search) drawDirResults();
    const [genres, rec] = await Promise.all([
      discover.genres ? Promise.resolve({ ok: true, data: discover.genres }) : request("/api/podcasts/directory/genres"),
      request("/api/podcasts/recommended")
    ]);
    if (token !== routeToken) return;
    discover.genres = genres.ok && Array.isArray(genres.data) ? genres.data : [];
    discover.lists.rec = rec.ok && Array.isArray(rec.data) ? rec.data : [];
    el("pc-rec").innerHTML = discover.lists.rec.length ? rowMarkup("rec", "Recommended for the household", discover.lists.rec, (s, i) => dirCard(s, i, "rec")) : "";
    wireImages(el("pc-rec"));
    wireRows(el("pc-rec"));
    drawGenreChips();
    loadTop(token);
  }

  function drawGenreChips() {
    const box = el("pc-genres");
    if (!box) return;
    box.innerHTML = [{ id: "", name: "All" }, ...discover.genres].map((g) => `<button type="button" class="au-chip-btn ${String(g.id) === String(discover.genre) ? "active" : ""}" data-act="dir-genre" data-id="${h(g.id)}">${h(g.name)}</button>`).join("");
  }

  async function loadTop(token) {
    const host = el("pc-top");
    if (!host) return;
    host.classList.add("mv-busy");
    const res = await request(`/api/podcasts/directory/top${discover.genre ? `?genre=${enc(discover.genre)}` : ""}`);
    if (token != null && token !== routeToken) return;
    if (!el("pc-top")) return;
    host.classList.remove("mv-busy");
    if (!res.ok) { host.innerHTML = `<div class="mv-empty mv-error">${h(res.error)}</div>`; return; }
    discover.lists.top = Array.isArray(res.data) ? res.data : [];
    host.innerHTML = `<div class="au-charts">${discover.lists.top.map((s, i) => `<button type="button" class="au-chart-row" data-act="preview" data-list="top" data-i="${i}">
        <span class="au-chart-n">${i + 1}</span>
        <span class="au-chart-art">${U.safeSrc(s.artUrl) ? `<img src="${h(s.artUrl)}" alt="" loading="lazy">` : ICON.mic}</span>
        <span class="au-chart-text"><span class="au-chart-title">${h(s.title)}</span><span class="au-chart-sub">${h([s.author, s.genre].filter(Boolean).join(" · "))}</span></span>
        ${s.subscribed ? `<span class="mv-lib-kind mv-have">following</span>` : s.podcastId ? `<span class="mv-lib-kind">in household</span>` : ""}
      </button>`).join("")}</div>`;
    wireImages(host);
  }

  async function dirSearch(q) {
    discover.q = q;
    const box = el("pc-dir-results");
    if (!box) return;
    if (!q) { box.innerHTML = ""; discover.lists.search = null; return; }
    box.innerHTML = loadingMarkup("searching the directory");
    const res = await request(`/api/podcasts/directory/search?${new URLSearchParams({ q })}`);
    if (!el("pc-dir-results") || discover.q !== q) return;
    if (!res.ok) { box.innerHTML = `<div class="mv-empty mv-error">${h(res.error)}</div>`; return; }
    discover.lists.search = Array.isArray(res.data) ? res.data : [];
    drawDirResults();
  }

  function drawDirResults() {
    const box = el("pc-dir-results");
    if (!box) return;
    const list = discover.lists.search || [];
    box.innerHTML = list.length ? `<section class="mv-section"><h3 class="mv-section-title">Results</h3><div class="mv-grid au-grid">${list.map((s, i) => dirCard(s, i, "search")).join("")}</div></section>` : `<div class="mv-empty">No shows called that. Try fewer words, or paste the feed address below.</div>`;
    wireImages(box);
  }

  // Look before you follow: the feed read without subscribing.
  async function openPreview(dir) {
    if (!dir) return;
    if (dir.podcastId && dir.subscribed) { go({ name: "show", id: dir.podcastId }); return; }
    U.openModal(`${U.modalHeader(dir.title || "Preview")}<div id="pc-preview">${loadingMarkup("reading the feed")}</div>`, stopPreview, true);
    const params = dir.itunesId ? { itunesId: String(dir.itunesId) } : { feedUrl: dir.feedUrl };
    const res = await request(`/api/podcasts/preview?${new URLSearchParams(params)}`);
    const box = el("pc-preview");
    if (!box) return;
    if (!res.ok) { box.innerHTML = `<div class="mv-empty mv-error">${h(res.error)}</div>`; return; }
    const p = (res.data && res.data.podcast) || {};
    const eps = ((res.data && res.data.episodes) || []).slice(0, 25);
    previewTarget = dir;
    box.innerHTML = `<div class="au-preview-head">
        ${art(p.artUrl || dir.artUrl, "au-square au-preview-art", p.title || dir.title, "", "", ICON.mic)}
        <div class="au-preview-info">
          <div class="mv-match-title">${h(p.title || dir.title)}</div>
          <div class="mv-match-overview">${h(p.author || dir.author || "")}</div>
          ${(p.categories || []).length ? `<div class="mv-genres">${p.categories.map((c) => `<span class="mv-genre">${h(c)}</span>`).join("")}</div>` : ""}
          <div class="mv-actions">${dir.subscribed ? `<button type="button" class="mv-btn" data-mact="pc-open-show" data-id="${h(dir.podcastId)}">open</button>`
            : `<button type="button" class="mv-btn mv-btn-primary" data-mact="pc-subscribe">${ICON.plus}<span>Follow</span></button>`}</div>
        </div>
      </div>
      ${p.description ? `<p class="au-preview-desc">${h(p.description)}</p>` : ""}
      <div class="au-preview-eps">${eps.map((e) => {
        const url = U.safeSrc(e.audioUrl);
        return `<div class="au-preview-ep"><div class="au-preview-ep-text"><span class="au-preview-ep-title">${h(e.title)}</span><span class="au-preview-ep-meta">${h([fmtDate(e.publishedAt), e.durationSeconds ? fmtDuration(e.durationSeconds) : ""].filter(Boolean).join(" · "))}</span></div>
          ${url ? `<button type="button" class="mv-btn mv-btn-quiet" data-mact="pc-preview-play" data-preview-url="${h(url)}" data-label="preview">${ICON.play}<span>preview</span></button>` : ""}</div>`;
      }).join("") || '<div class="mv-empty">This feed lists no episodes.</div>'}</div>`;
    wireImages(box);
  }

  let previewTarget = null;

  async function subscribeFromPreview(button) {
    const dir = previewTarget;
    if (!dir) return;
    button.disabled = true;
    button.classList.add("mv-working");
    const show = await subscribe(dir.itunesId ? { itunesId: dir.itunesId } : { feedUrl: dir.feedUrl });
    button.disabled = false;
    button.classList.remove("mv-working");
    if (!show) return;
    U.closeModal();
    go({ name: "show", id: show.id });
  }

  function importOpml() {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = ".opml,.xml,text/xml,application/xml,text/x-opml";
    input.addEventListener("change", async () => {
      const file = input.files && input.files[0];
      if (!file) return;
      const text = await file.text();
      showToast("Importing…", true);
      const res = await request("/api/podcasts/opml", { method: "POST", raw: text, contentType: "text/xml" });
      if (!res.ok) { showToast(res.error, false); return; }
      const d = res.data || {};
      const failed = d.failed || [];
      U.openModal(`${U.modalHeader("OPML imported")}
        <p class="mv-modal-intro"><b>${h(d.added || 0)}</b> shows added, <b>${h(d.existing || 0)}</b> already followed. The new feeds are being read in the background - they appear in Your shows over the next minute.</p>
        ${failed.length ? `<div class="mv-note mv-note-warn">${plural(failed.length, "feed")} couldn't be added:<ul>${failed.map((f) => `<li><code>${h(f.feedUrl)}</code> - ${h(f.error)}</li>`).join("")}</ul></div>` : ""}
        <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-primary" data-mact="close">OK</button></div>`);
      if (route.name === "home" || route.name === "shows") setTimeout(refreshCurrent, 1500);
    });
    input.click();
  }

  // ---- transcript search ----------------------------------------------------------------------

  async function renderSearch(q, token) {
    if (!el("pc-content").querySelector(".au-search-results")) setContent(loadingMarkup("searching transcripts"));
    const res = await request(`/api/podcasts/transcripts/search?${new URLSearchParams({ q })}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(errorMarkup(res.error)); return; }
    const hits = Array.isArray(res.data) ? res.data : [];
    const words = q.toLowerCase().split(/\s+/).filter(Boolean).map((w) => w.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
    const mark = (text) => {
      let out = h(text);
      if (words.length) out = out.replace(new RegExp(`(${words.map((w) => h(w)).join("|")})`, "gi"), "<mark>$1</mark>");
      return out;
    };
    setContent(`<div class="au-search-results">
      <h2 class="au-page-title au-search-title">“${h(q)}” in transcripts</h2>
      <div class="au-page-sub">${hits.length ? `${plural(hits.length, "moment")}${hits.length >= 50 ? " (the first 50)" : ""} · press one to play from there` : ""}</div>
      ${hits.length ? `<div class="au-hits">${hits.map((x) => `<button type="button" class="au-hit" data-act="bm-play" data-ep="${h(x.episodeId)}" data-t="${Number(x.startSeconds) || 0}" data-now-id="${h(x.episodeId)}">
          <span class="au-hit-art"><img src="/api/podcasts/${enc(x.podcastId)}/art" alt="" loading="lazy"></span>
          <span class="au-hit-text"><span class="au-hit-where">${h(x.podcastTitle || "")} · <b>${h(x.episodeTitle || "")}</b></span><span class="au-hit-snippet">…${mark(x.snippet || "")}…</span></span>
          <span class="au-hit-time">${ICON.play}${fmtClock(x.startSeconds)}</span>
        </button>`).join("")}</div>` : emptyMarkup(`Nobody said “${q}”`, "Only episodes with a transcript are searched - from the feed, or written by the server when transcripts are on.", ICON.transcript)}
    </div>`);
  }

  // ---- fill my drive --------------------------------------------------------------------------

  function openFill() {
    U.openModal(`${U.modalHeader("Fill my drive", "Pick how long you have. Unplayed and half-finished episodes from your shows are fitted in, Up Next first.")}
      <div class="au-chips au-fill-chips">${[10, 15, 20, 30, 45, 60, 90, 120].map((m) => `<button type="button" class="au-chip-btn" data-mact="pc-fill" data-m="${m}">${m < 60 ? `${m} min` : `${Math.floor(m / 60)}${m % 60 ? "½" : ""} h`}</button>`).join("")}
        <label class="au-fill-custom"><input type="number" class="field-input" min="5" max="600" id="pc-fill-m" placeholder="min"><button type="button" class="mv-btn" data-mact="pc-fill-custom">go</button></label></div>
      <div id="pc-fill-result"></div>`);
  }

  let fillResult = null;

  async function runFill(minutes) {
    const box = el("pc-fill-result");
    if (!box) return;
    for (const b of document.querySelectorAll('[data-mact="pc-fill"]')) b.classList.toggle("active", Number(b.dataset.m) === minutes);
    box.innerHTML = loadingMarkup("fitting episodes");
    const res = await request(`/api/podcasts/fill?minutes=${minutes}`);
    if (!el("pc-fill-result")) return;
    if (!res.ok) { box.innerHTML = `<div class="mv-empty mv-error">${h(res.error)}</div>`; return; }
    fillResult = { minutes, ...(res.data || {}) };
    const eps = fillResult.episodes || [];
    box.innerHTML = eps.length ? `<div class="au-fill-total">${plural(eps.length, "episode")} · ${fmtDuration(fillResult.totalSeconds)} of ${minutes} min</div>
      <div class="au-fill-bar"><span style="width:${Math.min(100, 100 * (fillResult.totalSeconds || 0) / (minutes * 60)).toFixed(1)}%"></span></div>
      <div class="au-fill-list">${eps.map((e) => `<div class="au-fill-ep"><span class="au-q-art">${U.safeSrc(e.artUrl) ? `<img src="${h(e.artUrl)}" alt="">` : ICON.mic}</span><span class="au-q-text"><span class="au-q-title">${h(e.title)}</span><span class="au-q-sub">${h(e.podcastTitle || "")}</span></span><span class="au-q-dur">${fmtDuration(progressOf(e).left)}</span></div>`).join("")}</div>
      <div class="mv-modal-foot"><button type="button" class="mv-btn" data-mact="pc-fill-queue">${ICON.queue}<span>add to Up Next</span></button><button type="button" class="mv-btn mv-btn-primary" data-mact="pc-fill-play">${ICON.play}<span>play these</span></button></div>`
      : `<div class="mv-empty">Nothing unplayed fits in ${minutes} minutes. Try longer, or follow more shows.</div>`;
    wireImages(box);
  }

  async function fillToQueue() {
    if (!fillResult) return;
    for (const e of fillResult.episodes || []) {
      if (upNext.some((u) => u.id === e.id)) continue;
      const res = await request("/api/podcasts/queue", { method: "POST", body: { episodeId: e.id, position: "last" } });
      if (res.ok && Array.isArray(res.data)) upNext = res.data;
    }
    U.closeModal();
    showToast("Added to Up Next", true);
    if (route.name === "home" || route.name === "upnext") refreshCurrent();
  }

  // ---- stats ----------------------------------------------------------------------------------

  function renderStats() {
    const content = setContent(`<div id="pc-stats"></div>`);
    if (window.SentinelAudioExtras) window.SentinelAudioExtras.renderStats(content.querySelector("#pc-stats"), "podcasts");
  }

  // ---- events ---------------------------------------------------------------------------------

  U.onModalAction((act, target) => {
    switch (act) {
      case "submit:pc-my-form": saveMySettings(); return true;
      case "submit:pc-show-form": saveShowSettings(); return true;
      case "submit:pc-bm-new": saveBookmark(); return true;
      case "submit:pc-bm-edit": {
        const id = target.dataset.id;
        request(`/api/podcasts/bookmarks/${enc(id)}`, { method: "PUT", body: { note: el("pc-bm-edit-note").value.trim() } }).then((res) => {
          if (!res.ok) { showToast(res.error, false); return; }
          U.closeModal();
          refreshCurrent();
        });
        return true;
      }
      case "submit:pc-clip-form": makeClip(target); return true;
      case "pc-clip-nudge": {
        const input = el("pc-clip-start");
        const t = Math.max(0, (parseClock(input.value) || 0) + Number(target.dataset.d));
        input.value = fmtClock(t);
        return true;
      }
      case "pc-clip-preview": {
        const start = parseClock(el("pc-clip-start").value) || 0;
        togglePreview(target.dataset.previewUrl, start, Number(el("pc-clip-len").value) || 30);
        return true;
      }
      case "pc-clip-share": shareClip(target.dataset.url, target.dataset.title); return true;
      case "pc-preview-play": togglePreview(target.dataset.previewUrl, 0, 0); return true;
      case "pc-subscribe": subscribeFromPreview(target); return true;
      case "pc-open-show": U.closeModal(); go({ name: "show", id: target.dataset.id }); return true;
      case "pc-fill": runFill(Number(target.dataset.m)); return true;
      case "pc-fill-custom": { const m = Number(el("pc-fill-m").value); if (m >= 5) runFill(Math.round(m)); return true; }
      case "pc-fill-play":
        if (fillResult && fillResult.episodes && fillResult.episodes.length) {
          A.playEpisodes(fillResult.episodes, 0, { type: "queue", id: "fill", name: `Fill ${fillResult.minutes} min` });
          U.closeModal();
        }
        return true;
      case "pc-fill-queue": fillToQueue(); return true;
      default: return false;
    }
  });

  function wireView() {
    const view = el("view-podcasts");
    view.addEventListener("click", async (e) => {
      const nav = e.target.closest("[data-pc-nav]");
      if (nav && view.contains(nav)) { go({ name: nav.dataset.pcNav }); return; }
      const target = e.target.closest("[data-act]");
      if (!target || !view.contains(target)) return;
      const act = target.dataset.act;
      const id = target.dataset.id;
      switch (act) {
        case "back": history.length > 1 ? history.back() : go({ name: "home" }); break;
        case "retry": refreshCurrent(); break;
        case "scroll": U.scrollRow(target); break;
        case "open-episode": go({ name: "episode", id }); break;
        case "open-show": go({ name: "show", id }); break;
        case "play-row": {
          const row = target.dataset.row;
          const items = rowData.get(row) || [];
          const i = Number(target.dataset.i) || 0;
          if (row === "upnext") A.playEpisodes(items, i, { type: "queue", id: "upnext", name: "Up Next" });
          else if (items[i]) playEpisode(items[i]);
          break;
        }
        case "play-upnext": if (!upNext.length) await loadQueue(); A.playEpisodes(upNext, 0, { type: "queue", id: "upnext", name: "Up Next" }); break;
        case "play-list": playFromList(target.dataset.list, Number(target.dataset.i) || 0); break;
        case "play-start": { const ep = current && current.data; if (ep) playEpisode(ep, 0); break; }
        case "play-at": playAt(Number(target.dataset.t) || 0); break;
        case "queue-add": queueAdd(id); break;
        case "queue-remove": queueRemove(id); break;
        case "toggle-played": { const ref = listEpisode(target); if (ref && ref.ep) setPlayed(ref.ep, !progressOf(ref.ep).played); break; }
        case "ep-menu": { const ref = listEpisode(target); if (ref && ref.ep) episodeMenu(target, ref.ep); break; }
        case "ep-filter": showState.filter = target.dataset.v; showState.offset = 0;
          for (const b of target.parentElement.children) b.classList.toggle("active", b === target);
          loadShowEpisodes(routeToken); break;
        case "ep-page": showState.offset = Math.max(0, showState.offset + Number(target.dataset.dir) * EP_PAGE); loadShowEpisodes(routeToken).then(() => el("pc-ep-filters").scrollIntoView({ behavior: "smooth" })); break;
        case "expand": target.classList.toggle("au-clamp"); break;
        case "subscribe": { const p = current && current.data; if (p) { const s = await subscribe({ feedUrl: p.feedUrl }); if (s) refreshCurrent(); } break; }
        case "unsubscribe": if (current && current.data) unsubscribe(current.data); break;
        case "play-latest": playLatest(); break;
        case "my-settings": if (current && current.data) openMySettings(current.data); break;
        case "show-menu": showMenu(target); break;
        case "ep-bookmark": if (current && current.data) openBookmark(current.data, 0); break;
        case "ep-clip": if (current && current.data) openClipMaker(current.data, 0); break;
        case "dl-ep": if (current && current.data) setDownload(current.data, true); break;
        case "dl-remove-ep": if (current && current.data && confirm("Remove the server's copy of this episode? It streams from the publisher again.")) setDownload(current.data, false); break;
        case "transcribe": transcribe(target); break;
        case "show-transcript": if (current && current.data) { current.showTranscript = true; loadTranscript(current.data); } break;
        case "bm-play": playBookmark(target.dataset.ep, Number(target.dataset.t) || 0); break;
        case "bm-note": bookmarkToNote(id, target); break;
        case "bm-edit": editBookmark(id); break;
        case "bm-delete": deleteBookmark(id); break;
        case "clip-play": togglePreview(target.dataset.previewUrl, 0, 0); break;
        case "clip-share": shareClip(target.dataset.url, target.dataset.title); break;
        case "clip-delete":
          if (!confirm("Delete this clip?")) break;
          request(`/api/podcasts/clips/${enc(id)}`, { method: "DELETE" }).then((res) => { if (!res.ok) showToast(res.error, false); refreshCurrent(); });
          break;
        case "toggle-all": showAll = !showAll; refreshCurrent(); break;
        case "refresh-all": {
          const res = await request("/api/podcasts/refresh", { method: "POST" });
          showToast(res.ok ? "Checking every feed - new episodes appear in a minute" : res.error, res.ok);
          break;
        }
        case "preview": { const list = (discover.lists || {})[target.dataset.list] || []; openPreview(list[Number(target.dataset.i)]); break; }
        case "dir-genre": discover.genre = target.dataset.id; drawGenreChips(); loadTop(); break;
        case "opml-import": importOpml(); break;
        case "fill": openFill(); break;
        case "briefing": if (window.SentinelAudioExtras) window.SentinelAudioExtras.openBriefing(); break;
        default: break;
      }
    });
    view.addEventListener("submit", async (e) => {
      if (e.target.id === "pc-dir-form") { e.preventDefault(); dirSearch(el("pc-dir-q").value.trim()); }
      if (e.target.id === "pc-rss-form") {
        e.preventDefault();
        const url = el("pc-rss").value.trim();
        if (!/^https?:\/\//i.test(url)) { showToast("Paste the feed's full address, starting with https://", false); return; }
        const button = e.target.querySelector("button");
        button.disabled = true;
        const show = await subscribe({ feedUrl: url });
        button.disabled = false;
        if (show) go({ name: "show", id: show.id });
      }
    });
    view.addEventListener("change", (e) => {
      if (e.target.id === "pc-ep-sort") {
        showState.sort = e.target.value;
        showState.offset = 0;
        loadShowEpisodes(routeToken);
        const p = current && current.data;
        if (p && p.subscribed) request(`/api/podcasts/${enc(p.id)}/my-settings`, { method: "PUT", body: { ...(p.mySettings || {}), sort: showState.sort } });
      }
      if (e.target.id === "pc-ep-season") { showState.season = e.target.value; showState.offset = 0; loadShowEpisodes(routeToken); }
    });
    view.addEventListener("input", (e) => {
      if (e.target.id === "pc-ep-q") {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => { showState.q = e.target.value.trim(); showState.offset = 0; loadShowEpisodes(routeToken); }, 300);
      }
      if (e.target.id === "pc-tr-q") filterTranscript(e.target.value);
    });
    const search = el("pc-search");
    search.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { const q = search.value.trim(); if (q) go({ name: "search", id: q }); }
      if (e.key === "Escape") { search.value = ""; if (route.name === "search") go({ name: "home" }); }
    });
    search.addEventListener("input", () => {
      clearTimeout(searchTimer);
      const q = search.value.trim();
      if (q.length < 3) return;
      searchTimer = setTimeout(() => go({ name: "search", id: q }), 500);
    });
    el("pc-fill-btn").addEventListener("click", openFill);
    el("pc-briefing-btn").addEventListener("click", () => { if (window.SentinelAudioExtras) window.SentinelAudioExtras.openBriefing(); });
    el("pc-alarms-btn").addEventListener("click", () => { if (window.SentinelAudioExtras) window.SentinelAudioExtras.openAlarms(); });
    el("pc-settings-btn").addEventListener("click", () => openSettingsSection("PODCASTS"));
    A.on("bookmark", ({ episode, positionSeconds }) => openBookmark(episode, positionSeconds));
    A.on("clip", ({ episode, positionSeconds }) => openClipMaker(episode, positionSeconds));
  }

  function openSettingsSection(title) {
    const tab = document.querySelector('#tabs .tab[data-view="settings"]');
    if (!tab) return;
    tab.click();
    let tries = 0;
    const find = setInterval(() => {
      tries++;
      const t = Array.from(document.querySelectorAll(".settings-section-title")).find((x) => x.textContent.trim() === title);
      if (t || tries > 25) clearInterval(find);
      if (!t) return;
      const section = t.closest(".settings-section");
      if (section && !section.classList.contains("open")) t.closest(".settings-section-header").click();
      t.scrollIntoView({ behavior: "smooth", block: "start" });
    }, 150);
  }

  // ---- public ---------------------------------------------------------------------------------

  function show() {
    visible = true;
    refreshStatus();
    routeTo(parseHash() || route);
  }

  function hide() {
    visible = false;
    scrollMemory[hashFor(route)] = window.scrollY;
    clearTimeout(dlTimer);
    stopPreview();
  }

  wireView();

  return { show, hide, go, playEpisode };
})();
