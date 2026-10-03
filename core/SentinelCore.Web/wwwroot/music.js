// Music tab: the household's own music files (core/docs/MUSIC_AND_PODCASTS.md §2-4). Home rows,
// browsing by artist, album, song, genre, playlist and folder, album and artist pages, search,
// playlists (including smart playlists and their rule builder), radio, stats and the "+ add"
// album downloads. Playing is audio-player.js's job; this file decides what to hand it.
//
// Routing rides the URL hash like Movies: #music, #music/albums, #music/album/<id>,
// #music/artist/<id>, #music/playlist/<id>, #music/genre/<name>, #music/decade/<year>,
// #music/search/<q>, #music/folders, #music/stats, #music/downloads.
window.SentinelMusic = (function () {
  const A = window.SentinelAudio;
  const U = A.util;
  const ICON = A.ICON;
  const { escapeHtml: h, enc, request, fmtClock, fmtDuration, plural, art, mosaic, wireImages, rowMarkup, wireRows, loadingMarkup, errorMarkup, emptyMarkup, showToast, stars } = U;
  const el = (id) => document.getElementById(id);

  const GRID_PAGE = 60;
  const SONG_PAGE = 100;
  const NAV = ["home", "artists", "albums", "songs", "genres", "playlists", "folders", "stats", "downloads"];
  const PHASES = { files: "finding files", tags: "reading tags", artwork: "fetching artwork", lyrics: "fetching lyrics" };

  let visible = false;
  let route = { name: "home" };
  let routeToken = 0;
  let status = null;
  let statusTimer = null;
  let wasScanning = false;
  let genresCache = null;
  let librariesCache = null;
  const scrollMemory = {};
  const lists = new Map();      // list key -> { tracks, context, playlist? }
  const rowData = new Map();    // home row key -> items
  let current = null;           // the album / artist / playlist on screen
  let searchTimer = null;

  const browse = {
    artists: { q: "", genre: "", sort: "name", order: "asc", albumArtistsOnly: true, offset: 0 },
    albums: { q: "", genre: "", decade: "", sort: "added", order: "desc", offset: 0 },
    songs: { q: "", genre: "", decade: "", loved: false, minRating: "", sort: "added", order: "desc", offset: 0 },
    genre: { offset: 0 },
    decade: { offset: 0 }
  };

  // ---- routing --------------------------------------------------------------------------------

  function parseHash() {
    const hash = location.hash.replace(/^#/, "");
    if (hash !== "music" && !hash.startsWith("music/")) return null;
    const parts = hash.split("/").slice(1);
    const arg = parts.length > 1 ? decodeURIComponent(parts.slice(1).join("/")) : "";
    switch (parts[0]) {
      case "album": return arg ? { name: "album", id: arg } : { name: "albums" };
      case "artist": return arg ? { name: "artist", id: arg } : { name: "artists" };
      case "playlist": return arg ? { name: "playlist", id: arg } : { name: "playlists" };
      case "genre": return arg ? { name: "genre", id: arg } : { name: "genres" };
      case "decade": return arg ? { name: "decade", id: arg } : { name: "home" };
      case "search": return arg ? { name: "search", id: arg } : { name: "home" };
      case "folders": return { name: "folders", id: arg };
      default: return NAV.includes(parts[0]) ? { name: parts[0] } : { name: "home" };
    }
  }

  function hashFor(r) {
    if (r.name === "home") return "#music";
    if (r.id != null && r.id !== "") return `#music/${r.name}/${enc(r.id)}`;
    return `#music/${r.name}`;
  }

  function go(r) {
    const hash = hashFor(r);
    if (location.hash === hash) { routeTo(r); return; }
    location.hash = hash;
  }

  function navKey(r) {
    const map = { album: "albums", artist: "artists", playlist: "playlists", genre: "genres", decade: "home", search: "home" };
    return map[r.name] || r.name;
  }

  async function routeTo(r) {
    const token = ++routeToken;
    const moved = !!route && (route.name !== r.name || route.id !== r.id);
    if (moved) scrollMemory[hashFor(route)] = window.scrollY;
    route = r;
    for (const b of document.querySelectorAll("#mu-subtabs [data-mu-nav]")) b.classList.toggle("active", b.dataset.muNav === navKey(r));
    const search = el("mu-search");
    if (r.name !== "search" && document.activeElement !== search) search.value = "";
    if (r.name === "search" && document.activeElement !== search) search.value = r.id;
    await U.ensureAuth();
    if (token !== routeToken) return;
    el("mu-libraries-btn").classList.toggle("hidden", !U.isAdmin());
    const content = el("mu-content");
    content.dataset.route = r.name;
    // The browse pages keep their filter bar between visits to the same page; anything else
    // draws over it, so it has to be built again next time.
    if (!["artists", "albums", "songs"].includes(r.name)) content.dataset.built = "";
    try {
      switch (r.name) {
        case "home": await renderHome(token); break;
        case "artists": await renderArtists(token); break;
        case "albums": await renderAlbums(token); break;
        case "songs": await renderSongs(token); break;
        case "genres": await renderGenres(token); break;
        case "playlists": await renderPlaylists(token); break;
        case "folders": await renderFolders(token, r.id || ""); break;
        case "stats": renderStats(); break;
        case "downloads": await renderDownloads(token); break;
        case "album": await renderAlbum(r.id, token); break;
        case "artist": await renderArtist(r.id, token); break;
        case "playlist": await renderPlaylist(r.id, token); break;
        case "genre": await renderGenre(r.id, token); break;
        case "decade": await renderDecade(r.id, token); break;
        case "search": await renderSearch(r.id, token); break;
        default: await renderHome(token);
      }
    } catch (err) {
      console.error(err);
      if (token === routeToken) content.innerHTML = errorMarkup(err.message || "Something went wrong.");
    }
    if (token !== routeToken) return;
    const y = scrollMemory[hashFor(r)];
    if (y != null) window.scrollTo(0, y);
    else if (moved) U.scrollToTopOf(el("mu-subtabs"));
    A.markNow(content);
  }

  function refreshCurrent() {
    if (!visible) return;
    scrollMemory[hashFor(route)] = window.scrollY;
    routeTo(route);
  }

  function setContent(html) {
    const content = el("mu-content");
    content.innerHTML = html;
    wireImages(content);
    wireRows(content);
    A.markNow(content);
    return content;
  }

  function backButton() {
    return `<button type="button" class="mv-back" data-act="back">${ICON.back}<span>back</span></button>`;
  }

  // ---- status, scan ---------------------------------------------------------------------------

  async function refreshStatus() {
    clearTimeout(statusTimer);
    const res = await request("/api/music/status");
    if (res.ok && res.data) {
      status = res.data;
      const finished = wasScanning && !status.scanning;
      wasScanning = !!status.scanning;
      const span = el("mu-scan-state");
      const s = status.scan;
      const text = status.scanning ? `scanning${s && s.libraryName ? " " + s.libraryName : ""} · ${(s && (PHASES[s.phase] || s.phase)) || ""}${s && s.total ? ` ${s.processed}/${s.total}` : ""}` : "";
      span.classList.toggle("hidden", !text);
      span.innerHTML = text ? `<span class="mv-pulse"></span>${h(text)}` : "";
      if (finished && visible) { showToast("Music scan finished", true); genresCache = null; if (route.name === "home") refreshCurrent(); }
    }
    if (visible && status && status.scanning) statusTimer = setTimeout(refreshStatus, 3000);
    return status;
  }

  function openSettings() {
    const tab = document.querySelector('#tabs .tab[data-view="settings"]');
    if (!tab) return;
    tab.click();
    let tries = 0;
    const find = setInterval(() => {
      tries++;
      const title = Array.from(document.querySelectorAll(".settings-section-title")).find((t) => t.textContent.trim() === "MUSIC");
      if (title || tries > 25) clearInterval(find);
      if (!title) return;
      const section = title.closest(".settings-section");
      if (section && !section.classList.contains("open")) title.closest(".settings-section-header").click();
      title.scrollIntoView({ behavior: "smooth", block: "start" });
    }, 150);
  }

  // ---- cards ----------------------------------------------------------------------------------

  function playOverlay(act, id, label, extra = "") {
    return `<button type="button" class="au-card-play" data-act="${act}" data-id="${h(id)}" ${extra} aria-label="${h(label)}" title="${h(label)}">${ICON.play}</button>`;
  }

  function albumCard(a) {
    if (!a) return "";
    const sub = [a.artist, a.year].filter(Boolean).join(" · ");
    return `<div class="au-card" data-now-album="${h(a.id)}">
      <div class="au-card-art-wrap">
        <button type="button" class="au-card-art" data-act="open-album" data-id="${h(a.id)}" title="${h(a.title)}">${art(a.artUrl, "au-square", a.title, a.artist)}</button>
        ${playOverlay("play-album", a.id, `Play ${a.title}`)}
      </div>
      <button type="button" class="au-card-text" data-act="open-album" data-id="${h(a.id)}">
        <span class="mv-card-title">${h(a.title)}${a.loved ? ` <span class="au-heart-tag">${ICON.heartFill}</span>` : ""}</span>
        <span class="mv-card-sub">${h(sub)}</span>
      </button>
    </div>`;
  }

  function artistCard(a) {
    if (!a) return "";
    return `<div class="au-card au-card-artist">
      <div class="au-card-art-wrap">
        <button type="button" class="au-card-art" data-act="open-artist" data-id="${h(a.id)}" title="${h(a.name)}">${art(a.artUrl, "au-square au-round", a.name, "")}</button>
        ${playOverlay("radio-artist", a.id, `${a.name} radio`, `data-name="${h(a.name)}"`)}
      </div>
      <button type="button" class="au-card-text" data-act="open-artist" data-id="${h(a.id)}">
        <span class="mv-card-title">${h(a.name)}</span>
        <span class="mv-card-sub">${a.albumCount ? plural(a.albumCount, "album") : plural(a.trackCount || 0, "song")}</span>
      </button>
    </div>`;
  }

  function playlistCard(p) {
    if (!p) return "";
    const badges = [p.smart ? "smart" : "", p.collaborative ? "collab" : p.shared && p.mine ? "shared" : "", !p.mine ? `by ${p.ownerName || "someone"}` : ""].filter(Boolean);
    return `<div class="au-card">
      <div class="au-card-art-wrap">
        <button type="button" class="au-card-art" data-act="open-playlist" data-id="${h(p.id)}" title="${h(p.name)}">${mosaic(p.artUrls, "au-square", p.name, "", p.pinned ? `<span class="au-pin-badge">${ICON.pin}</span>` : "")}</button>
        ${playOverlay("play-playlist", p.id, `Play ${p.name}`)}
      </div>
      <button type="button" class="au-card-text" data-act="open-playlist" data-id="${h(p.id)}">
        <span class="mv-card-title">${h(p.name)}</span>
        <span class="mv-card-sub">${h([plural(p.trackCount || 0, "song"), ...badges].join(" · "))}</span>
      </button>
    </div>`;
  }

  function mixCard(m) {
    if (!m) return "";
    return `<div class="au-card au-card-mix">
      <button type="button" class="au-card-art" data-act="play-mix" data-id="${h(m.id)}" data-name="${h(m.name)}" title="Play ${h(m.name)}">
        ${mosaic(m.artUrls, "au-square", m.name, "", `<span class="au-mix-shade au-mix-${h(m.seedType || "genre")}"></span><span class="au-mix-name">${h(m.name)}</span><span class="au-mix-play">${ICON.play}</span>`)}
      </button>
      <div class="au-card-text au-card-text-static"><span class="mv-card-sub">${h(m.description || "")}</span></div>
    </div>`;
  }

  // A track as a square card in a row: the art plays the row from here.
  function trackCard(row) {
    return (t, i) => `<div class="au-card" data-now-id="${h(t.id)}">
      <div class="au-card-art-wrap">
        <button type="button" class="au-card-art" data-act="play-row" data-row="${h(row)}" data-i="${i}" title="Play ${h(t.title)}">${art(t.artUrl, "au-square", t.title, t.artist, `<span class="au-card-eq">${eqBars()}</span>`)}</button>
        ${playOverlay("play-row", t.id, `Play ${t.title}`, `data-row="${h(row)}" data-i="${i}"`)}
      </div>
      <div class="au-card-text au-card-text-static">
        <span class="mv-card-title">${h(t.title)}</span>
        <span class="mv-card-sub">${artistLinks(t)}</span>
      </div>
    </div>`;
  }

  function decadeCard(d) {
    return `<button type="button" class="au-decade" data-act="open-decade" data-id="${h(d.decade)}">
      <span class="au-decade-label">${h(d.label || `${d.decade}s`)}</span>
      <span class="au-decade-sub">${plural(d.trackCount || 0, "song")}</span>
    </button>`;
  }

  function recentCard(r) {
    if (!r) return "";
    if (r.kind === "album") return albumCard(r.album);
    if (r.kind === "playlist") return playlistCard(r.playlist);
    if (r.kind === "artist") return artistCard(r.artist);
    if (r.kind === "mix") return mixCard(r.mix);
    return "";
  }

  function eqBars() {
    return `<span class="au-eqbars" aria-hidden="true"><i></i><i></i><i></i></span>`;
  }

  function artistLinks(t) {
    if (!t) return "";
    if (t.artistId) return `<a class="au-sublink" href="#music/artist/${enc(t.artistId)}">${h(t.artist || t.albumArtist || "")}</a>`;
    return h(t.artist || "");
  }

  function grid(items, card) {
    return `<div class="mv-grid au-grid">${items.map(card).join("")}</div>`;
  }

  // ---- track lists ----------------------------------------------------------------------------

  // opts: { key, context, numbers (album track numbers), art, album, editable (playlist), discs }
  function trackList(tracks, opts) {
    const key = opts.key;
    lists.set(key, { tracks, context: opts.context, playlist: opts.playlist || null });
    if (!tracks.length) return `<div class="au-q-empty">${opts.empty || "No songs."}</div>`;
    let lastDisc = null;
    const multiDisc = opts.discs && new Set(tracks.map((t) => t.discNumber || 1)).size > 1;
    const rows = tracks.map((t, i) => {
      let discHead = "";
      if (multiDisc && (t.discNumber || 1) !== lastDisc) {
        lastDisc = t.discNumber || 1;
        discHead = `<div class="au-disc">Disc ${h(lastDisc)}</div>`;
      }
      const num = opts.numbers ? (t.trackNumber || i + 1) : i + 1;
      const loved = !!t.loved;
      return `${discHead}<div class="au-tr ${opts.editable ? "au-tr-edit" : ""}" data-list="${h(key)}" data-i="${i}" data-now-id="${h(t.id)}" ${opts.editable ? 'data-sort="1"' : ""}>
        ${opts.editable ? `<span class="au-drag" title="Drag to reorder" aria-hidden="true">${ICON.drag}</span>` : ""}
        <span class="au-tr-num"><span class="au-tr-n">${h(num)}</span>${eqBars()}<button type="button" class="au-tr-play" data-act="play-list" data-list="${h(key)}" data-i="${i}" aria-label="Play ${h(t.title)}">${ICON.play}</button></span>
        ${opts.art ? `<span class="au-tr-art">${U.safeSrc(t.artUrl) ? `<img src="${h(U.artSized(t.artUrl, 96))}" alt="" loading="lazy">` : ICON.note}</span>` : ""}
        <span class="au-tr-main">
          <span class="au-tr-title">${h(t.title)}</span>
          <span class="au-tr-artist">${artistLinks(t)}${opts.album && t.album ? `<span class="au-tr-album-inline"> · <a class="au-sublink" href="#music/album/${enc(t.albumId)}">${h(t.album)}</a></span>` : ""}</span>
        </span>
        ${opts.album && t.album ? `<span class="au-tr-album"><a class="au-sublink" href="#music/album/${enc(t.albumId)}">${h(t.album)}</a></span>` : ""}
        <span class="au-tr-plays" title="Your plays">${t.playCount ? h(t.playCount) : ""}</span>
        <button type="button" class="au-ctl au-ctl-xs au-love ${loved ? "on" : ""}" data-act="love-track" data-list="${h(key)}" data-i="${i}" data-love-id="${h(t.id)}" aria-pressed="${loved}" title="${loved ? "Unlove" : "Love"}">${loved ? ICON.heartFill : ICON.heart}</button>
        <span class="au-tr-dur">${t.durationSeconds ? fmtClock(t.durationSeconds) : ""}</span>
        <button type="button" class="au-ctl au-ctl-xs" data-act="track-menu" data-list="${h(key)}" data-i="${i}" aria-label="More" title="More">${ICON.more}</button>
      </div>`;
    }).join("");
    return `<div class="au-tracks ${opts.art ? "au-tracks-art" : ""} ${opts.album ? "au-tracks-album" : ""}" data-key="${h(key)}">${rows}</div>`;
  }

  function listTrack(target) {
    const list = lists.get(target.dataset.list);
    if (!list) return null;
    return { list, i: Number(target.dataset.i), track: list.tracks[Number(target.dataset.i)] };
  }

  function playList(key, i, opts = {}) {
    const list = lists.get(key);
    if (!list) return;
    A.playTracks(list.tracks, i, list.context, opts);
  }

  function trackMenu(anchor, ref) {
    const t = ref.track;
    if (!t) return;
    const items = [
      { label: "Play next", icon: ICON.queue, run: () => A.enqueue([t], "next", "track") },
      { label: "Add to queue", icon: ICON.plus, run: () => A.enqueue([t], "last", "track") },
      { label: "Add to playlist…", icon: ICON.plus, run: () => openAddToPlaylist([t]) },
      { label: "Start radio", icon: ICON.radio, run: () => A.startRadio("track", t.id, `${t.title} Radio`) },
      "-",
      { label: t.loved ? "Unlove" : "Love", icon: t.loved ? ICON.heartFill : ICON.heart, run: () => loveTrack(t) },
      { label: `Rate${t.rating ? ` (${t.rating}★)` : ""}…`, icon: ICON.star, run: () => openRatePop(anchor, t) },
      "-",
      t.albumId ? { label: "Go to album", icon: ICON.note, run: () => go({ name: "album", id: t.albumId }) } : null,
      t.artistId ? { label: "Go to artist", icon: ICON.note, run: () => go({ name: "artist", id: t.artistId }) } : null,
      { label: "Download file", icon: ICON.download, run: () => { location.href = U.withQuery(`/api/music/tracks/${enc(t.id)}/download`, { codecs: U.codecs() }); } }
    ];
    const pl = ref.list.playlist;
    if (pl && pl.canEdit && !pl.smart && t.entryId) {
      items.push("-", { label: "Remove from this playlist", icon: ICON.trash, danger: true, run: () => removeFromPlaylist(pl, t) });
    }
    U.openMenu(anchor, items, { title: t.title });
  }

  function openRatePop(anchor, t) {
    U.openPop(anchor, `<div class="au-pop-title">${ICON.star} ${h(t.title)}</div>${stars(t.rating, "rate", t.id)}`, (pop) => {
      pop.addEventListener("click", async (e) => {
        const b = e.target.closest("[data-act=rate]");
        if (!b) return;
        const rating = Number(b.dataset.value);
        const res = await request(`/api/music/tracks/${enc(t.id)}/rating`, { method: "POST", body: { rating } });
        if (!res.ok) { showToast(res.error, false); return; }
        t.rating = rating;
        A.syncRaw("track", t.id, { rating });
        U.closePop();
      });
    });
  }

  async function loveTrack(t) {
    const loved = !t.loved;
    t.loved = loved;
    drawLove(t.id, loved);
    const res = await request(`/api/music/tracks/${enc(t.id)}/love`, { method: "POST", body: { loved } });
    if (!res.ok) { t.loved = !loved; drawLove(t.id, !loved); showToast(res.error, false); return; }
    A.syncRaw("track", t.id, { loved });
  }

  function drawLove(id, loved) {
    for (const b of document.querySelectorAll(`#view-music [data-love-id="${CSS.escape(id)}"]`)) {
      b.classList.toggle("on", loved);
      b.innerHTML = loved ? ICON.heartFill : ICON.heart;
      b.setAttribute("aria-pressed", String(loved));
    }
    for (const list of lists.values()) for (const t of list.tracks) if (t.id === id) t.loved = loved;
  }

  // ---- home -----------------------------------------------------------------------------------

  async function renderHome(token) {
    const content = el("mu-content");
    if (!content.querySelector(".au-home")) content.innerHTML = loadingMarkup();
    const [res, st] = await Promise.all([request("/api/music/home"), status ? Promise.resolve(status) : refreshStatus()]);
    if (token !== routeToken) return;
    if (st && Array.isArray(st.libraries) && st.libraries.length === 0) { renderSetup(); return; }
    if (!res.ok) { content.innerHTML = errorMarkup(res.error); return; }
    const d = res.data || {};
    rowData.clear();
    const rowFor = (key, items) => { rowData.set(key, items || []); return items; };
    const onThisDay = d.onThisDay && d.onThisDay.tracks && d.onThisDay.tracks.length ? d.onThisDay : null;
    const hour = new Date().getHours();
    const greeting = hour < 5 ? "Up late" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
    const name = U.authInfo() && U.authInfo().username ? `, ${U.authInfo().username}` : "";
    const head = `<div class="au-home-head">
        <div><div class="mv-kicker">${h(greeting + name)}</div>
          <div class="au-home-counts">${plural(d.trackCount || 0, "song")} · ${plural(d.albumCount || 0, "album")} · ${plural(d.artistCount || 0, "artist")}</div></div>
        <div class="mv-actions">
          <button type="button" class="mv-btn mv-btn-primary" data-act="shuffle-all">${ICON.shuffle}<span>Shuffle all</span></button>
          <button type="button" class="mv-btn mv-btn-glass" data-act="play-mix" data-id="loved" data-name="Loved">${ICON.heart}<span>Loved mix</span></button>
          <button type="button" class="mv-btn mv-btn-glass" data-act="play-mix" data-id="discover" data-name="Discover">${ICON.radio}<span>Discover mix</span></button>
        </div>
      </div>`;
    const rows = [
      rowMarkup("pinned", "Pinned", rowFor("pinned", d.pinned), playlistCard),
      rowMarkup("recent", "Recently played", rowFor("recent", d.recentlyPlayed), recentCard),
      rowMarkup("mixes", "Made for you", rowFor("mixes", d.mixes), mixCard),
      rowMarkup("added", "Recently added", rowFor("added", d.recentlyAdded), albumCard),
      rowMarkup("heavy", "Heavy rotation", rowFor("heavy", d.heavyRotation), albumCard),
      onThisDay ? rowMarkup("onthisday", `On this day &middot; <span class="au-row-sub">${h(onThisDay.label || "")}</span>`, rowFor("onthisday", onThisDay.tracks), trackCard("onthisday"), rowPlayAll("onthisday")) : "",
      rowMarkup("forgotten", "Forgotten favorites", rowFor("forgotten", d.forgottenFavorites), trackCard("forgotten"), rowPlayAll("forgotten")),
      rowMarkup("discover", "Discover in your library", rowFor("discover", d.discover), albumCard),
      rowMarkup("loved", "Loved songs", rowFor("loved", d.lovedTracks), trackCard("loved"), rowPlayAll("loved")),
      d.decades && d.decades.length ? `<section class="mv-row au-row"><div class="au-row-head"><h3 class="mv-row-title">Decades</h3></div><div class="au-decades">${d.decades.map(decadeCard).join("")}</div></section>` : ""
    ].join("");
    const scanning = status && status.scanning;
    const empty = rows.replace(/\s/g, "") ? "" : emptyMarkup(scanning ? "Reading your music…" : "Nothing played yet", scanning ? "The first scan is still running - albums appear as it finds them." : "Play something from Albums or Songs and this page fills up.", ICON.note);
    setContent(`<div class="au-home">${head}${notices()}${dlBanner()}${rows}${empty}</div>`);
  }

  function rowPlayAll(key) {
    return `<button type="button" class="au-row-action" data-act="play-row" data-row="${key}" data-i="0">${ICON.play}<span>play all</span></button>`;
  }

  function notices() {
    if (!status || !U.isAdmin()) return "";
    const out = [];
    if (status.ffmpegAvailable === false) out.push(`<div class="mv-note mv-note-warn">ffmpeg isn't installed on the server, so files this browser can't play natively (ALAC, WMA, APE, DSD…) won't play, and nothing can be converted for data saver. Install ffmpeg and restart the server.</div>`);
    if (status.lastScanError) out.push(`<div class="mv-note mv-note-warn">The last scan hit a problem: ${h(status.lastScanError)}</div>`);
    return out.join("");
  }

  function renderSetup() {
    const admin = U.isAdmin();
    const content = setContent(`<div class="mv-setup au-setup">
      <div class="mv-setup-glyph">${ICON.note}</div>
      <h2>${admin ? "Add your music" : "No music yet"}</h2>
      <p>${admin ? "Point Sentinel Core at a folder of music on the server, or one shared from another computer. It reads the tags and cover art, finds missing artwork and lyrics, and plays it here and in every app."
        : "An admin adds the music folders under Settings → Music, and they show up for everyone."}</p>
      ${admin ? `<div id="mu-setup-libs" class="au-setup-libs"></div>` : ""}
    </div>`);
    if (admin && window.SentinelAudioExtras) window.SentinelAudioExtras.librariesPanel(content.querySelector("#mu-setup-libs"), { onChange: () => { status = null; setTimeout(refreshCurrent, 600); } });
  }

  // ---- browse: artists, albums, songs ---------------------------------------------------------

  async function genres() {
    if (genresCache) return genresCache;
    const res = await request("/api/music/genres");
    genresCache = res.ok && Array.isArray(res.data) ? res.data : [];
    return genresCache;
  }

  function genreOptions(selected) {
    return `<option value="">All genres</option>` + (genresCache || []).map((g) => `<option value="${h(g.name)}" ${g.name === selected ? "selected" : ""}>${h(g.name)}</option>`).join("");
  }

  function decadeOptions(selected) {
    const now = new Date().getFullYear();
    let out = `<option value="">Any decade</option>`;
    for (let d = Math.floor(now / 10) * 10; d >= 1920; d -= 10) out += `<option value="${d}" ${String(d) === String(selected) ? "selected" : ""}>${d}s</option>`;
    return out;
  }

  function orderButton(s) {
    return `<button type="button" class="btn mv-toggle au-order" data-act="order" title="Reverse the order" aria-label="Order ${s.order === "asc" ? "ascending" : "descending"}">${s.order === "asc" ? "▲ asc" : "▼ desc"}</button>`;
  }

  function pager(s, total, size) {
    if (total <= size) return "";
    const from = s.offset + 1;
    const to = Math.min(total, s.offset + size);
    return `<div class="mv-pager">
      <button type="button" class="btn" data-act="page" data-dir="-1" ${s.offset <= 0 ? "disabled" : ""}>${ICON.left} prev</button>
      <span>${from}&ndash;${to} of ${total.toLocaleString()}</span>
      <button type="button" class="btn" data-act="page" data-dir="1" ${to >= total ? "disabled" : ""}>next ${ICON.right}</button>
    </div>`;
  }

  function filterBar(key, inner) {
    const s = browse[key];
    return `<div class="mv-filters au-filters" data-browse="${key}">
      <label class="mv-search">${ICON.search}<input type="search" class="field-input" data-f="q" placeholder="Filter ${key}" value="${h(s.q || "")}" aria-label="Filter ${key}"></label>
      ${inner}
      <span class="mv-count" data-f="count"></span>
    </div><div class="au-browse-host"></div>`;
  }

  async function ensureBrowse(key, toolbar) {
    const content = el("mu-content");
    if (content.dataset.built !== key) {
      await genres();
      content.innerHTML = filterBar(key, toolbar());
      content.dataset.built = key;
    }
    return content.querySelector(".au-browse-host");
  }

  async function renderArtists(token) {
    const s = browse.artists;
    const host = await ensureBrowse("artists", () => `
      <select class="field-input mv-select" data-f="genre" aria-label="Genre">${genreOptions(s.genre)}</select>
      <select class="field-input mv-select" data-f="sort" aria-label="Sort"><option value="name">Name</option><option value="plays">Most played</option><option value="added">Recently added</option></select>
      ${orderButton(s)}
      <button type="button" class="btn mv-toggle ${s.albumArtistsOnly ? "btn-on" : ""}" data-act="toggle" data-f="albumArtistsOnly" aria-pressed="${s.albumArtistsOnly}">album artists only</button>`);
    el("mu-content").querySelector('[data-f="sort"]').value = s.sort;
    if (!host.firstChild) host.innerHTML = loadingMarkup();
    else host.classList.add("mv-busy");
    const params = new URLSearchParams({ sort: s.sort, order: s.order, offset: String(s.offset), limit: String(GRID_PAGE) });
    if (s.q) params.set("q", s.q);
    if (s.genre) params.set("genre", s.genre);
    if (s.albumArtistsOnly) params.set("albumArtistsOnly", "true");
    const res = await request(`/api/music/artists?${params}`);
    if (token !== routeToken) return;
    host.classList.remove("mv-busy");
    if (!res.ok) { host.innerHTML = errorMarkup(res.error); return; }
    const items = (res.data && res.data.items) || [];
    const total = (res.data && res.data.total) || 0;
    el("mu-content").querySelector('[data-f="count"]').textContent = total ? plural(total, "artist") : "";
    host.innerHTML = items.length ? grid(items, artistCard) + pager(s, total, GRID_PAGE) : emptyMarkup("No artists match", "Try a different filter.");
    wireImages(host);
  }

  async function renderAlbums(token) {
    const s = browse.albums;
    const host = await ensureBrowse("albums", () => `
      <select class="field-input mv-select" data-f="genre" aria-label="Genre">${genreOptions(s.genre)}</select>
      <select class="field-input mv-select" data-f="decade" aria-label="Decade">${decadeOptions(s.decade)}</select>
      <select class="field-input mv-select" data-f="sort" aria-label="Sort"><option value="added">Recently added</option><option value="title">Title</option><option value="artist">Artist</option><option value="year">Year</option><option value="plays">Most played</option><option value="rating">Your rating</option></select>
      ${orderButton(s)}`);
    el("mu-content").querySelector('[data-f="sort"]').value = s.sort;
    if (!host.firstChild) host.innerHTML = loadingMarkup();
    else host.classList.add("mv-busy");
    const params = new URLSearchParams({ sort: s.sort, order: s.order, offset: String(s.offset), limit: String(GRID_PAGE) });
    if (s.q) params.set("q", s.q);
    if (s.genre) params.set("genre", s.genre);
    if (s.decade) params.set("decade", s.decade);
    const res = await request(`/api/music/albums?${params}`);
    if (token !== routeToken) return;
    host.classList.remove("mv-busy");
    if (!res.ok) { host.innerHTML = errorMarkup(res.error); return; }
    const items = (res.data && res.data.items) || [];
    const total = (res.data && res.data.total) || 0;
    el("mu-content").querySelector('[data-f="count"]').textContent = total ? plural(total, "album") : "";
    host.innerHTML = items.length ? grid(items, albumCard) + pager(s, total, GRID_PAGE) : emptyMarkup("No albums match", "Try a different filter.");
    wireImages(host);
  }

  async function renderSongs(token) {
    const s = browse.songs;
    const host = await ensureBrowse("songs", () => `
      <select class="field-input mv-select" data-f="genre" aria-label="Genre">${genreOptions(s.genre)}</select>
      <select class="field-input mv-select" data-f="decade" aria-label="Decade">${decadeOptions(s.decade)}</select>
      <select class="field-input mv-select" data-f="minRating" aria-label="Rating"><option value="">Any rating</option><option value="1">★ and up</option><option value="2">★★ and up</option><option value="3">★★★ and up</option><option value="4">★★★★ and up</option><option value="5">★★★★★</option></select>
      <select class="field-input mv-select" data-f="sort" aria-label="Sort"><option value="added">Recently added</option><option value="title">Title</option><option value="artist">Artist</option><option value="album">Album</option><option value="year">Year</option><option value="plays">Most played</option><option value="lastPlayed">Last played</option><option value="rating">Your rating</option><option value="duration">Length</option></select>
      ${orderButton(s)}
      <button type="button" class="btn mv-toggle ${s.loved ? "btn-on" : ""}" data-act="toggle" data-f="loved" aria-pressed="${s.loved}">${ICON.heart} loved</button>`);
    const content = el("mu-content");
    content.querySelector('[data-f="sort"]').value = s.sort;
    content.querySelector('[data-f="minRating"]').value = s.minRating;
    if (!host.firstChild) host.innerHTML = loadingMarkup();
    else host.classList.add("mv-busy");
    const params = new URLSearchParams({ sort: s.sort, order: s.order, offset: String(s.offset), limit: String(SONG_PAGE) });
    if (s.q) params.set("q", s.q);
    if (s.genre) params.set("genre", s.genre);
    if (s.decade) params.set("decade", s.decade);
    if (s.loved) params.set("loved", "true");
    if (s.minRating) params.set("minRating", s.minRating);
    const res = await request(`/api/music/tracks?${params}`);
    if (token !== routeToken) return;
    host.classList.remove("mv-busy");
    if (!res.ok) { host.innerHTML = errorMarkup(res.error); return; }
    const items = (res.data && res.data.items) || [];
    const total = (res.data && res.data.total) || 0;
    content.querySelector('[data-f="count"]').textContent = total ? plural(total, "song") : "";
    host.innerHTML = items.length
      ? `<div class="au-list-actions"><button type="button" class="mv-btn mv-btn-primary" data-act="play-list" data-list="songs" data-i="0">${ICON.play}<span>Play</span></button>
          <button type="button" class="mv-btn mv-btn-glass" data-act="shuffle-list" data-list="songs">${ICON.shuffle}<span>Shuffle</span></button>
          ${total > items.length ? `<span class="au-list-note">plays the ${items.length} on this page</span>` : ""}</div>`
        + trackList(items, { key: "songs", art: true, album: true, context: { type: "queue", id: null, name: "Songs" } }) + pager(s, total, SONG_PAGE)
      : emptyMarkup("No songs match", "Try a different filter.");
    wireImages(host);
    A.markNow(host);
  }

  // ---- genres, decades ------------------------------------------------------------------------

  async function renderGenres(token) {
    el("mu-content").dataset.built = "";
    setContent(loadingMarkup());
    genresCache = null;
    const list = await genres();
    if (token !== routeToken) return;
    if (!list.length) { setContent(emptyMarkup("No genres yet", "Genres come from your files' tags.", ICON.note)); return; }
    setContent(`<div class="au-genres">${list.map((g, i) => `<button type="button" class="au-genre-tile au-hue-${i % 8}" data-act="open-genre" data-id="${h(g.name)}">
        ${mosaic(g.artUrls, "au-square", g.name, "")}
        <span class="au-genre-shade"></span>
        <span class="au-genre-name">${h(g.name)}</span>
        <span class="au-genre-sub">${plural(g.albumCount || 0, "album")} · ${plural(g.trackCount || 0, "song")}</span>
      </button>`).join("")}</div>`);
  }

  async function renderGenre(name, token) {
    el("mu-content").dataset.built = "";
    const s = browse.genre;
    if (s.name !== name) { s.name = name; s.offset = 0; }
    if (!el("mu-content").querySelector(".au-page-head")) setContent(backButton() + loadingMarkup());
    const params = new URLSearchParams({ genre: name, sort: "plays", order: "desc", offset: String(s.offset), limit: String(GRID_PAGE) });
    const [albums, songs] = await Promise.all([
      request(`/api/music/albums?${params}`),
      request(`/api/music/tracks?${new URLSearchParams({ genre: name, sort: "plays", order: "desc", limit: "10" })}`)
    ]);
    if (token !== routeToken) return;
    if (!albums.ok) { setContent(backButton() + errorMarkup(albums.error)); return; }
    const items = (albums.data && albums.data.items) || [];
    const total = (albums.data && albums.data.total) || 0;
    const top = (songs.ok && songs.data && songs.data.items) || [];
    setContent(`${backButton()}
      <div class="au-page-head">
        <div class="au-page-glyph">${ICON.note}</div>
        <div><div class="mv-kicker">Genre</div><h2 class="au-page-title">${h(name)}</h2><div class="au-page-sub">${plural(total, "album")}</div></div>
        <div class="mv-actions">
          <button type="button" class="mv-btn mv-btn-primary" data-act="radio" data-seed="genre" data-id="${h(name)}" data-name="${h(name)} Radio">${ICON.radio}<span>${h(name)} radio</span></button>
          <button type="button" class="mv-btn mv-btn-glass" data-act="shuffle-genre" data-id="${h(name)}">${ICON.shuffle}<span>Shuffle</span></button>
        </div>
      </div>
      ${top.length ? `<section class="mv-section"><h3 class="mv-section-title">Most played</h3>${trackList(top, { key: "genre-top", art: true, album: true, context: { type: "queue", id: name, name } })}</section>` : ""}
      <section class="mv-section"><h3 class="mv-section-title">Albums</h3>${grid(items, albumCard)}${pager(s, total, GRID_PAGE)}</section>`);
  }

  async function renderDecade(decade, token) {
    el("mu-content").dataset.built = "";
    const s = browse.decade;
    if (s.name !== decade) { s.name = decade; s.offset = 0; }
    setContent(backButton() + loadingMarkup());
    const params = new URLSearchParams({ decade, sort: "year", order: "asc", offset: String(s.offset), limit: String(GRID_PAGE) });
    const res = await request(`/api/music/albums?${params}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(backButton() + errorMarkup(res.error)); return; }
    const items = (res.data && res.data.items) || [];
    const total = (res.data && res.data.total) || 0;
    const label = `${String(decade).slice(2)}s`;
    setContent(`${backButton()}
      <div class="au-page-head au-decade-head">
        <div class="au-decade-big">${h(label)}</div>
        <div><div class="mv-kicker">Decade</div><h2 class="au-page-title">The ${h(decade)}s</h2><div class="au-page-sub">${plural(total, "album")}</div></div>
        <div class="mv-actions"><button type="button" class="mv-btn mv-btn-primary" data-act="play-mix" data-id="decade-${h(decade)}" data-name="The ${h(decade)}s">${ICON.play}<span>Play the ${h(decade)}s</span></button></div>
      </div>
      ${grid(items, albumCard)}${pager(s, total, GRID_PAGE)}`);
  }

  // ---- album, artist --------------------------------------------------------------------------

  async function renderAlbum(id, token) {
    el("mu-content").dataset.built = "";
    if (!current || current.kind !== "album" || current.id !== id) setContent(backButton() + loadingMarkup());
    const res = await request(`/api/music/albums/${enc(id)}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(backButton() + errorMarkup(res.error)); return; }
    const a = res.data;
    current = { kind: "album", id, data: a };
    const tracks = a.tracks || [];
    const meta = [a.year, plural(a.trackCount || tracks.length, "song"), fmtDuration(a.durationSeconds), (a.formats || []).join(", "), U.sizeLabel(a.totalBytes), a.playCount ? `you've played it ${plural(a.playCount, "time")}` : ""].filter(Boolean);
    setContent(`${backButton()}
      <div class="au-hero">
        <div class="au-hero-bg">${U.safeSrc(a.artUrl) ? `<img src="${h(U.artSized(a.artUrl, 600))}" alt="">` : ""}</div>
        <div class="au-hero-art">${art(U.artSized(a.artUrl, 600), "au-square", a.title, a.artist)}</div>
        <div class="au-hero-info">
          <div class="mv-kicker">${a.compilation ? "Compilation" : "Album"}</div>
          <h2 class="au-hero-title">${h(a.title)}</h2>
          <div class="au-hero-by">${a.artistId ? `<a class="au-sublink" href="#music/artist/${enc(a.artistId)}">${h(a.artist)}</a>` : h(a.artist || "")}</div>
          <div class="mv-meta">${meta.map(h).join('<span class="mv-dot">&middot;</span>')}</div>
          ${(a.genres || []).length ? `<div class="mv-genres">${a.genres.map((g) => `<a class="mv-genre" href="#music/genre/${enc(g)}">${h(g)}</a>`).join("")}</div>` : ""}
          <div class="mv-actions">
            <button type="button" class="mv-btn mv-btn-primary" data-act="play-list" data-list="album" data-i="0">${ICON.play}<span>Play</span></button>
            <button type="button" class="mv-btn mv-btn-glass" data-act="shuffle-list" data-list="album">${ICON.shuffle}<span>Shuffle</span></button>
            <button type="button" class="mv-btn mv-btn-glass" data-act="radio" data-seed="album" data-id="${h(a.id)}" data-name="${h(a.title)} Radio">${ICON.radio}<span>Radio</span></button>
            <button type="button" class="au-ctl au-love ${a.loved ? "on" : ""}" data-act="love-album" title="${a.loved ? "Unlove" : "Love"} this album" aria-pressed="${!!a.loved}">${a.loved ? ICON.heartFill : ICON.heart}</button>
            ${stars(a.rating, "rate-album", a.id)}
            <button type="button" class="au-ctl" data-act="album-menu" title="More" aria-label="More">${ICON.more}</button>
          </div>
        </div>
      </div>
      ${trackList(tracks, { key: "album", numbers: true, discs: true, context: { type: "album", id: a.id, name: a.title } })}
      <div class="au-album-foot">${tracks.length ? `${plural(tracks.length, "song")} · ${fmtDuration(a.durationSeconds)}` : ""}</div>
      ${rowMarkup("more", `More by ${h(a.artist || "this artist")}`, a.moreByArtist, albumCard)}`);
  }

  async function renderArtist(id, token) {
    el("mu-content").dataset.built = "";
    if (!current || current.kind !== "artist" || current.id !== id) setContent(backButton() + loadingMarkup());
    const res = await request(`/api/music/artists/${enc(id)}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(backButton() + errorMarkup(res.error)); return; }
    const a = res.data;
    current = { kind: "artist", id, data: a, showAllTop: current && current.id === id ? current.showAllTop : false };
    const top = a.topTracks || [];
    const shownTop = current.showAllTop ? top : top.slice(0, 5);
    setContent(`${backButton()}
      <div class="au-hero au-hero-artist">
        <div class="au-hero-bg">${U.safeSrc(a.artUrl) ? `<img src="${h(U.artSized(a.artUrl, 600))}" alt="">` : ""}</div>
        <div class="au-hero-art">${art(U.artSized(a.artUrl, 600), "au-square au-round", a.name, "")}</div>
        <div class="au-hero-info">
          <div class="mv-kicker">Artist</div>
          <h2 class="au-hero-title">${h(a.name)}</h2>
          <div class="mv-meta">${[plural(a.albumCount || 0, "album"), plural(a.trackCount || 0, "song"), a.playCount ? `${plural(a.playCount, "play")} by you` : ""].filter(Boolean).map(h).join('<span class="mv-dot">&middot;</span>')}</div>
          ${(a.genres || []).length ? `<div class="mv-genres">${a.genres.map((g) => `<a class="mv-genre" href="#music/genre/${enc(g)}">${h(g)}</a>`).join("")}</div>` : ""}
          <div class="mv-actions">
            <button type="button" class="mv-btn mv-btn-primary" data-act="play-artist" data-id="${h(a.id)}">${ICON.play}<span>Play</span></button>
            <button type="button" class="mv-btn mv-btn-glass" data-act="play-artist" data-id="${h(a.id)}" data-shuffle="1">${ICON.shuffle}<span>Shuffle</span></button>
            <button type="button" class="mv-btn mv-btn-glass" data-act="radio" data-seed="artist" data-id="${h(a.id)}" data-name="${h(a.name)} Radio">${ICON.radio}<span>Radio</span></button>
            <button type="button" class="au-ctl au-love ${a.loved ? "on" : ""}" data-act="love-artist" title="${a.loved ? "Unlove" : "Love"}" aria-pressed="${!!a.loved}">${a.loved ? ICON.heartFill : ICON.heart}</button>
          </div>
        </div>
      </div>
      ${top.length ? `<section class="mv-section"><h3 class="mv-section-title">Top songs <span class="mv-section-sub">most played in the household</span></h3>
        ${trackList(shownTop, { key: "artist-top", art: true, album: true, context: { type: "artist", id: a.id, name: a.name } })}
        ${top.length > 5 ? `<button type="button" class="mv-link au-more-link" data-act="toggle-top">${current.showAllTop ? "show fewer" : `show all ${top.length}`}</button>` : ""}</section>` : ""}
      ${(a.albums || []).length ? `<section class="mv-section"><h3 class="mv-section-title">Albums</h3>${grid(a.albums, albumCard)}</section>` : ""}
      ${rowMarkup("appears", "Appears on", a.appearsOn, albumCard)}
      ${rowMarkup("similar", "Similar artists", a.similar, artistCard)}`);
  }

  async function playArtist(id, shuffle) {
    const res = await request(`/api/music/tracks?${new URLSearchParams({ artistId: id, sort: "album", order: "asc", limit: "1000" })}`);
    if (!res.ok) { showToast(res.error, false); return; }
    const tracks = (res.data && res.data.items) || [];
    const name = current && current.kind === "artist" && current.data ? current.data.name : "Artist";
    A.playTracks(tracks, 0, { type: "artist", id, name }, { shuffle: !!shuffle });
  }

  async function playAlbum(id, shuffle) {
    const res = await request(`/api/music/albums/${enc(id)}`);
    if (!res.ok) { showToast(res.error, false); return; }
    A.playTracks(res.data.tracks || [], 0, { type: "album", id, name: res.data.title }, { shuffle: !!shuffle });
  }

  async function loveAlbumOrArtist(kind, button) {
    if (!current || current.kind !== kind) return;
    const item = current.data;
    const loved = !item.loved;
    const res = await request(`/api/music/${kind === "album" ? "albums" : "artists"}/${enc(item.id)}/love`, { method: "POST", body: { loved } });
    if (!res.ok) { showToast(res.error, false); return; }
    item.loved = loved;
    button.classList.toggle("on", loved);
    button.innerHTML = loved ? ICON.heartFill : ICON.heart;
    button.setAttribute("aria-pressed", String(loved));
  }

  async function rateAlbum(rating) {
    if (!current || current.kind !== "album") return;
    const res = await request(`/api/music/albums/${enc(current.id)}/rating`, { method: "POST", body: { rating } });
    if (!res.ok) { showToast(res.error, false); return; }
    current.data.rating = rating;
    const box = el("mu-content").querySelector(".au-hero .au-stars");
    if (box) box.outerHTML = stars(rating, "rate-album", current.id);
  }

  // ---- search ---------------------------------------------------------------------------------

  async function renderSearch(q, token) {
    el("mu-content").dataset.built = "";
    if (!el("mu-content").querySelector(".au-search-results")) setContent(loadingMarkup("searching"));
    const res = await request(`/api/music/search?${new URLSearchParams({ q })}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(errorMarkup(res.error)); return; }
    const d = res.data || {};
    const any = ["artists", "albums", "tracks", "playlists", "genres"].some((k) => (d[k] || []).length);
    if (!any) { setContent(`<div class="au-search-results">${emptyMarkup(`Nothing for “${q}”`, "Search matches the start of words in titles, artists and albums.", ICON.search)}</div>`); return; }
    const top = (d.artists || [])[0];
    setContent(`<div class="au-search-results">
      <h2 class="au-page-title au-search-title">Results for “${h(q)}”</h2>
      ${(d.genres || []).length ? `<div class="mv-genres au-search-genres">${d.genres.map((g) => `<a class="mv-genre" href="#music/genre/${enc(g)}">${h(g)}</a>`).join("")}</div>` : ""}
      ${(d.tracks || []).length ? `<section class="mv-section"><h3 class="mv-section-title">Songs</h3>${trackList(d.tracks, { key: "search", art: true, album: true, context: { type: "search", id: q, name: `“${q}”` } })}</section>` : ""}
      ${rowMarkup("s-artists", "Artists", d.artists, artistCard)}
      ${rowMarkup("s-albums", "Albums", d.albums, albumCard)}
      ${rowMarkup("s-playlists", "Playlists", d.playlists, playlistCard)}
    </div>`);
  }

  // ---- playlists ------------------------------------------------------------------------------

  async function renderPlaylists(token) {
    el("mu-content").dataset.built = "";
    setContent(loadingMarkup());
    const res = await request("/api/music/playlists");
    if (token !== routeToken) return;
    if (!res.ok) { setContent(errorMarkup(res.error)); return; }
    const all = Array.isArray(res.data) ? res.data : [];
    const pinned = all.filter((p) => p.pinned);
    const mine = all.filter((p) => p.mine);
    const shared = all.filter((p) => !p.mine);
    const head = `<div class="au-page-head au-page-head-plain">
        <div><h2 class="au-page-title">Playlists</h2><div class="au-page-sub">${plural(all.length, "playlist")} · yours, and the ones others share with the household</div></div>
        <div class="mv-actions">
          <button type="button" class="mv-btn mv-btn-primary" data-act="new-playlist">${ICON.plus}<span>New playlist</span></button>
          <button type="button" class="mv-btn mv-btn-glass" data-act="new-smart">${ICON.eq}<span>New smart playlist</span></button>
        </div></div>`;
    if (!all.length) {
      setContent(head + emptyMarkup("No playlists yet", "Make one here, or add songs to a new playlist from any song's ⋯ menu. Smart playlists fill themselves from rules like “rated 4★+, not played in 90 days”.", ICON.queue));
      return;
    }
    setContent(head
      + (pinned.length ? `<section class="mv-section"><h3 class="mv-section-title">Pinned</h3>${grid(pinned, playlistCard)}</section>` : "")
      + (mine.length ? `<section class="mv-section"><h3 class="mv-section-title">Yours</h3>${grid(mine, playlistCard)}</section>` : "")
      + (shared.length ? `<section class="mv-section"><h3 class="mv-section-title">Shared with the household</h3>${grid(shared, playlistCard)}</section>` : ""));
  }

  function canEditPlaylist(p) {
    return !!(p && (p.mine || U.isAdmin()));
  }

  function canEditTracks(p) {
    return !!(p && !p.smart && (p.mine || p.collaborative || U.isAdmin()));
  }

  async function renderPlaylist(id, token) {
    el("mu-content").dataset.built = "";
    if (!current || current.kind !== "playlist" || current.id !== id) setContent(backButton() + loadingMarkup());
    const res = await request(`/api/music/playlists/${enc(id)}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(backButton() + errorMarkup(res.error)); return; }
    const p = res.data;
    p.canEdit = canEditTracks(p);
    current = { kind: "playlist", id, data: p };
    const tracks = p.tracks || [];
    const kicker = p.smart ? "Smart playlist" : p.collaborative ? "Collaborative playlist" : "Playlist";
    const meta = [plural(p.trackCount ?? tracks.length, "song"), fmtDuration(p.durationSeconds), p.mine ? (p.shared ? "shared with the household" : "only you") : `by ${p.ownerName || "someone"}`, `updated ${U.ago(p.updatedAt)}`].filter(Boolean);
    setContent(`${backButton()}
      <div class="au-hero">
        <div class="au-hero-bg">${(p.artUrls || []).length ? `<img src="${h(U.safeSrc(p.artUrls[0]) || "")}" alt="">` : ""}</div>
        <div class="au-hero-art">${mosaic(p.artUrls, "au-square", p.name, "", "", p.smart ? ICON.eq : ICON.queue)}</div>
        <div class="au-hero-info">
          <div class="mv-kicker">${kicker}</div>
          <h2 class="au-hero-title">${h(p.name)}</h2>
          ${p.description ? `<p class="au-hero-desc">${h(p.description)}</p>` : ""}
          <div class="mv-meta">${meta.map(h).join('<span class="mv-dot">&middot;</span>')}</div>
          ${p.smart && p.rules ? `<div class="au-rules-summary">${rulesSummary(p.rules)}</div>` : ""}
          <div class="mv-actions">
            <button type="button" class="mv-btn mv-btn-primary" data-act="play-list" data-list="playlist" data-i="0" ${tracks.length ? "" : "disabled"}>${ICON.play}<span>Play</span></button>
            <button type="button" class="mv-btn mv-btn-glass" data-act="shuffle-list" data-list="playlist" ${tracks.length ? "" : "disabled"}>${ICON.shuffle}<span>Shuffle</span></button>
            <button type="button" class="au-ctl ${p.pinned ? "on" : ""}" data-act="pin-playlist" title="${p.pinned ? "Unpin from" : "Pin to"} Music home" aria-pressed="${!!p.pinned}">${ICON.pin}</button>
            <button type="button" class="au-ctl" data-act="playlist-menu" title="More" aria-label="More">${ICON.more}</button>
          </div>
        </div>
      </div>
      ${p.canEdit && tracks.length > 1 ? `<div class="au-list-note au-edit-note">${ICON.drag} drag songs to reorder${p.collaborative && !p.mine ? " - this playlist is collaborative" : ""}</div>` : ""}
      ${trackList(tracks, { key: "playlist", art: true, album: true, editable: p.canEdit, playlist: p, context: { type: "playlist", id: p.id, name: p.name }, empty: p.smart ? "Nothing matches these rules yet." : "No songs yet. Add some from any song's ⋯ menu → Add to playlist." })}`);
    const list = el("mu-content").querySelector('.au-tracks[data-key="playlist"]');
    if (list && p.canEdit) U.makeSortable(list, (from, to) => reorderPlaylist(p, from, to));
  }

  async function reorderPlaylist(p, from, to) {
    const tracks = U.moveInArray(p.tracks, from, to);
    p.tracks = tracks;
    lists.get("playlist").tracks = tracks;
    renderPlaylistTracksOnly(p);
    const res = await request(`/api/music/playlists/${enc(p.id)}/order`, { method: "PUT", body: { entryIds: tracks.map((t) => t.entryId) } });
    if (!res.ok) { showToast(res.error, false); refreshCurrent(); }
  }

  function renderPlaylistTracksOnly(p) {
    const old = el("mu-content").querySelector('.au-tracks[data-key="playlist"]');
    if (!old) return;
    const wrap = document.createElement("div");
    wrap.innerHTML = trackList(p.tracks, { key: "playlist", art: true, album: true, editable: p.canEdit, playlist: p, context: { type: "playlist", id: p.id, name: p.name } });
    const fresh = wrap.firstElementChild;
    old.replaceWith(fresh);
    wireImages(fresh);
    A.markNow(fresh);
    if (p.canEdit) U.makeSortable(fresh, (a, b) => reorderPlaylist(p, a, b));
  }

  async function removeFromPlaylist(p, t) {
    const res = await request(`/api/music/playlists/${enc(p.id)}/tracks/${enc(t.entryId)}`, { method: "DELETE" });
    if (!res.ok) { showToast(res.error, false); return; }
    p.tracks = p.tracks.filter((x) => x.entryId !== t.entryId);
    showToast(`Removed ${t.title}`, true);
    if (route.name === "playlist") refreshCurrent();
  }

  function playlistMenu(anchor) {
    const p = current && current.data;
    if (!p) return;
    const items = [
      { label: "Add all to queue", icon: ICON.plus, run: () => A.enqueue(p.tracks || [], "last", "track") },
      canEditPlaylist(p) ? { label: p.smart ? "Edit rules…" : "Edit details…", icon: ICON.edit, run: () => openPlaylistEditor(p) } : null,
      { label: p.smart ? "Save as a regular playlist" : "Duplicate", icon: ICON.copy, run: () => duplicatePlaylist(p) },
      { label: "Export M3U", icon: ICON.download, run: () => { location.href = `/api/music/playlists/${enc(p.id)}/m3u`; } },
      canEditPlaylist(p) ? "-" : null,
      canEditPlaylist(p) ? { label: "Delete playlist", icon: ICON.trash, danger: true, run: () => deletePlaylist(p) } : null
    ];
    U.openMenu(anchor, items, { title: p.name });
  }

  async function pinPlaylist(button) {
    const p = current && current.data;
    if (!p) return;
    const pinned = !p.pinned;
    const res = await request(`/api/music/playlists/${enc(p.id)}/pin`, { method: "POST", body: { pinned } });
    if (!res.ok) { showToast(res.error, false); return; }
    p.pinned = pinned;
    button.classList.toggle("on", pinned);
    button.setAttribute("aria-pressed", String(pinned));
    showToast(pinned ? "Pinned to Music home" : "Unpinned", true);
  }

  async function duplicatePlaylist(p) {
    const res = await request(`/api/music/playlists/${enc(p.id)}/duplicate`, { method: "POST" });
    if (!res.ok) { showToast(res.error, false); return; }
    showToast(`Made “${res.data.name}”`, true);
    go({ name: "playlist", id: res.data.id });
  }

  async function deletePlaylist(p) {
    if (!confirm(`Delete the playlist “${p.name}”? The songs themselves stay in the library.`)) return;
    const res = await request(`/api/music/playlists/${enc(p.id)}`, { method: "DELETE" });
    if (!res.ok) { showToast(res.error, false); return; }
    showToast("Playlist deleted", true);
    go({ name: "playlists" });
  }

  async function playPlaylist(id, shuffle) {
    const res = await request(`/api/music/playlists/${enc(id)}`);
    if (!res.ok) { showToast(res.error, false); return; }
    A.playTracks(res.data.tracks || [], 0, { type: "playlist", id, name: res.data.name }, { shuffle: !!shuffle });
  }

  // -- add to playlist

  async function openAddToPlaylist(tracks) {
    const list = (tracks || []).filter((t) => t && t.id);
    if (!list.length) return;
    U.openModal(`${U.modalHeader("Add to playlist", list.length === 1 ? `“${h(list[0].title)}”` : `${list.length} songs`)}
      <form class="mv-match-form" id="mu-newpl-form"><input class="field-input" id="mu-newpl-name" placeholder="New playlist name" autocomplete="off"><button type="submit" class="mv-btn mv-btn-primary">${ICON.plus}<span>create</span></button></form>
      <div class="au-pl-pick" id="mu-pl-pick">${loadingMarkup()}</div>`);
    addTarget = list;
    const res = await request("/api/music/playlists");
    const box = el("mu-pl-pick");
    if (!box) return;
    if (!res.ok) { box.innerHTML = `<div class="mv-empty mv-error">${h(res.error)}</div>`; return; }
    const editable = (res.data || []).filter((p) => !p.smart && (p.mine || p.collaborative || U.isAdmin()));
    box.innerHTML = editable.length ? editable.map((p) => `<button type="button" class="mv-match au-pl-row" data-mact="mu-add-to" data-id="${h(p.id)}">
        ${mosaic(p.artUrls, "au-square au-pl-art", p.name, "")}
        <span class="mv-match-text"><span class="mv-match-title">${h(p.name)}</span><span class="mv-match-id">${plural(p.trackCount || 0, "song")}${p.mine ? "" : ` · ${p.collaborative ? "collaborative" : "shared"} by ${h(p.ownerName || "")}`}</span></span>
      </button>`).join("") : `<div class="mv-empty">No playlists you can add to yet - name a new one above.</div>`;
    wireImages(box);
  }

  let addTarget = null;

  async function addTracksTo(playlistId, button) {
    if (!addTarget) return;
    if (button) button.disabled = true;
    const res = await request(`/api/music/playlists/${enc(playlistId)}/tracks`, { method: "POST", body: { trackIds: addTarget.map((t) => t.id), position: null } });
    if (!res.ok) { if (button) button.disabled = false; showToast(res.error, false); return; }
    U.closeModal();
    showToast(`Added to ${res.data && res.data.name ? res.data.name : "the playlist"}`, true);
    if (route.name === "playlist" && route.id === playlistId) refreshCurrent();
  }

  async function createWithTracks(name) {
    if (!name || !addTarget) return;
    const res = await request("/api/music/playlists", { method: "POST", body: { name, trackIds: addTarget.map((t) => t.id) } });
    if (!res.ok) { showToast(res.error, false); return; }
    U.closeModal();
    showToast(`Made “${res.data.name}”`, true);
  }

  // -- playlist editor (regular and smart)

  const RULE_FIELDS = {
    title: { label: "Title", type: "text" }, artist: { label: "Artist", type: "text" }, album: { label: "Album", type: "text" },
    genre: { label: "Genre", type: "text" }, composer: { label: "Composer", type: "text" },
    year: { label: "Year", type: "number" }, rating: { label: "Rating", type: "number", min: 0, max: 5 },
    playCount: { label: "Play count", type: "number", min: 0 }, durationSeconds: { label: "Length (seconds)", type: "number", min: 0 },
    bpm: { label: "BPM", type: "number", min: 0 }, loved: { label: "Loved", type: "bool" },
    added: { label: "Added", type: "days" }, lastPlayed: { label: "Last played", type: "days" },
    codec: { label: "Format", type: "codec" }, library: { label: "Library", type: "library" }
  };
  const OPS = {
    text: ["is", "isNot", "contains", "notContains"], number: ["is", "gte", "lte"], bool: ["is"],
    days: ["inLast", "notInLast"], codec: ["is", "isNot"], library: ["is", "isNot"]
  };
  const OP_LABELS = { is: "is", isNot: "is not", contains: "contains", notContains: "doesn't contain", gte: "is at least", lte: "is at most", inLast: "in the last", notInLast: "not in the last" };
  const CODECS = ["mp3", "aac", "alac", "flac", "opus", "vorbis", "wav", "aiff", "wma", "ape", "wv", "dsf"];
  const SORTS = { random: "Random", title: "Title", artist: "Artist", album: "Album", year: "Year", added: "Date added", plays: "Play count", lastPlayed: "Last played", rating: "Rating" };

  function rulesSummary(rules) {
    const join = rules.match === "any" ? " or " : " and ";
    const parts = (rules.rules || []).map((r) => {
      const f = RULE_FIELDS[r.field] || { label: r.field, type: "text" };
      let v = r.value;
      if (f.type === "bool") v = v === true || v === "true" ? "yes" : "no";
      if (f.type === "days") v = `${v} days`;
      if (f.type === "library") v = ((librariesCache || []).find((l) => l.id === v) || {}).name || "a library";
      if (r.field === "rating") v = `${v}★`;
      return `<span class="au-rule-chip">${h(f.label)} ${h(OP_LABELS[r.op] || r.op)} <b>${h(v)}</b></span>`;
    });
    const tail = `<span class="au-rule-tail">sorted by ${h((SORTS[rules.sort] || rules.sort || "random").toLowerCase())}${rules.limit ? `, ${h(rules.limit)} songs at most` : ""}</span>`;
    return parts.join(`<span class="au-rule-join">${join}</span>`) + tail;
  }

  let editor = null;

  async function openPlaylistEditor(existing, smart) {
    const isSmart = existing ? !!existing.smart : !!smart;
    if (isSmart && !librariesCache) {
      const res = await request("/api/music/libraries");
      librariesCache = res.ok && Array.isArray(res.data) ? res.data : [];
    }
    await genres();
    editor = {
      existing: existing || null, smart: isSmart,
      rules: existing && existing.rules ? JSON.parse(JSON.stringify(existing.rules)) : { match: "all", rules: [{ field: "rating", op: "gte", value: 4 }], sort: "random", order: "desc", limit: 100 }
    };
    drawEditor({ name: existing ? existing.name : "", description: existing ? existing.description || "" : "", shared: existing ? !!existing.shared : false, collaborative: existing ? !!existing.collaborative : false });
  }

  function drawEditor(form) {
    const e = editor;
    const rulesHtml = e.smart ? `<div class="au-rules">
        <div class="au-rules-head">Match <select class="field-input au-inline-select" id="mu-rules-match"><option value="all">all</option><option value="any">any</option></select> of these rules</div>
        <div class="au-rule-list" id="mu-rule-list">${e.rules.rules.map(ruleRow).join("")}</div>
        <button type="button" class="mv-btn mv-btn-quiet" data-mact="mu-rule-add">${ICON.plus}<span>add a rule</span></button>
        <div class="au-rules-foot">
          <label class="mv-field"><span>Sort by</span><select class="field-input" id="mu-rules-sort">${Object.entries(SORTS).map(([k, v]) => `<option value="${k}">${h(v)}</option>`).join("")}</select></label>
          <label class="mv-field"><span>Order</span><select class="field-input" id="mu-rules-order"><option value="desc">Highest / newest first</option><option value="asc">Lowest / oldest first</option></select></label>
          <label class="mv-field"><span>Limit (0 = no limit)</span><input class="field-input" type="number" min="0" max="5000" id="mu-rules-limit" value="${h(e.rules.limit ?? 100)}"></label>
        </div>
        <div class="mv-field-note">Worked out fresh every time it's opened, so it keeps up with what you play, love and add.</div>
      </div>` : "";
    U.openModal(`${U.modalHeader(e.existing ? (e.smart ? "Edit smart playlist" : "Edit playlist") : (e.smart ? "New smart playlist" : "New playlist"))}
      <form class="mv-form" id="mu-pl-form">
        <label class="mv-field"><span>Name</span><input class="field-input" id="mu-pl-name" value="${h(form.name)}" maxlength="120" placeholder="${e.smart ? "Top rated, not lately" : "Road trip"}" autofocus></label>
        <label class="mv-field"><span>Description</span><input class="field-input" id="mu-pl-desc" value="${h(form.description)}" maxlength="400" placeholder="optional"></label>
        <label class="au-check-row"><span class="switch"><input type="checkbox" id="mu-pl-shared" ${form.shared ? "checked" : ""}><span class="switch-track"></span></span><span>Shared <small>everyone in the household can see and play it</small></span></label>
        ${e.smart ? "" : `<label class="au-check-row"><span class="switch"><input type="checkbox" id="mu-pl-collab" ${form.collaborative ? "checked" : ""}><span class="switch-track"></span></span><span>Collaborative <small>everyone can also add, remove and reorder songs</small></span></label>`}
        ${rulesHtml}
        <ul class="field-errors" id="mu-pl-errors"></ul>
        <div class="mv-modal-foot">
          <button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Cancel</button>
          <button type="submit" class="mv-btn mv-btn-primary">${e.existing ? "Save" : "Create"}</button>
        </div>
      </form>`, () => { editor = null; }, e.smart);
    if (e.smart) {
      el("mu-rules-match").value = e.rules.match || "all";
      el("mu-rules-sort").value = e.rules.sort || "random";
      el("mu-rules-order").value = e.rules.order || "desc";
    }
    const collab = el("mu-pl-collab");
    if (collab) collab.addEventListener("change", () => { if (collab.checked) el("mu-pl-shared").checked = true; });
  }

  function ruleValueInput(rule, i) {
    const f = RULE_FIELDS[rule.field] || RULE_FIELDS.title;
    const v = rule.value ?? "";
    switch (f.type) {
      case "number": return `<input class="field-input au-rule-value" type="number" data-rule="${i}" data-k="value" value="${h(v)}" ${f.min != null ? `min="${f.min}"` : ""} ${f.max != null ? `max="${f.max}"` : ""}>`;
      case "days": return `<span class="au-rule-days"><input class="field-input au-rule-value" type="number" min="1" data-rule="${i}" data-k="value" value="${h(v || 30)}"><span>days</span></span>`;
      case "bool": return `<select class="field-input au-rule-value" data-rule="${i}" data-k="value"><option value="true" ${v === true || v === "true" ? "selected" : ""}>yes</option><option value="false" ${v === false || v === "false" ? "selected" : ""}>no</option></select>`;
      case "codec": return `<select class="field-input au-rule-value" data-rule="${i}" data-k="value">${CODECS.map((c) => `<option value="${c}" ${c === v ? "selected" : ""}>${c.toUpperCase()}</option>`).join("")}</select>`;
      case "library": return `<select class="field-input au-rule-value" data-rule="${i}" data-k="value">${(librariesCache || []).map((l) => `<option value="${h(l.id)}" ${l.id === v ? "selected" : ""}>${h(l.name)}</option>`).join("") || '<option value="">(no libraries)</option>'}</select>`;
      default: {
        const listId = rule.field === "genre" ? ' list="mu-genre-list"' : "";
        return `<input class="field-input au-rule-value" data-rule="${i}" data-k="value" value="${h(v)}"${listId}>${rule.field === "genre" ? `<datalist id="mu-genre-list">${(genresCache || []).map((g) => `<option value="${h(g.name)}">`).join("")}</datalist>` : ""}`;
      }
    }
  }

  function ruleRow(rule, i) {
    const f = RULE_FIELDS[rule.field] || RULE_FIELDS.title;
    return `<div class="au-rule" data-i="${i}">
      <select class="field-input" data-rule="${i}" data-k="field" aria-label="Field">${Object.entries(RULE_FIELDS).map(([k, d]) => `<option value="${k}" ${k === rule.field ? "selected" : ""}>${h(d.label)}</option>`).join("")}</select>
      <select class="field-input" data-rule="${i}" data-k="op" aria-label="Condition">${OPS[f.type].map((op) => `<option value="${op}" ${op === rule.op ? "selected" : ""}>${h(OP_LABELS[op])}</option>`).join("")}</select>
      ${ruleValueInput(rule, i)}
      <button type="button" class="au-ctl au-ctl-xs" data-mact="mu-rule-remove" data-i="${i}" aria-label="Remove rule" title="Remove rule">${ICON.close}</button>
    </div>`;
  }

  function defaultValue(type) {
    return { text: "", number: 0, bool: true, days: 30, codec: "flac", library: (librariesCache && librariesCache[0] && librariesCache[0].id) || "" }[type];
  }

  function readRulesFromForm() {
    const e = editor;
    if (!e || !e.smart) return;
    for (const input of document.querySelectorAll("#mu-rule-list [data-rule]")) {
      const r = e.rules.rules[Number(input.dataset.rule)];
      if (!r) continue;
      if (input.dataset.k === "value") r.value = input.value;
      else r[input.dataset.k] = input.value;
    }
    e.rules.match = el("mu-rules-match").value;
    e.rules.sort = el("mu-rules-sort").value;
    e.rules.order = el("mu-rules-order").value;
    e.rules.limit = Math.max(0, Math.min(5000, Number(el("mu-rules-limit").value) || 0));
  }

  function redrawRules() {
    const box = el("mu-rule-list");
    if (box) box.innerHTML = editor.rules.rules.map(ruleRow).join("");
  }

  function onRuleChange(target) {
    const r = editor && editor.rules.rules[Number(target.dataset.rule)];
    if (!r) return;
    if (target.dataset.k === "field") {
      readRulesFromForm();
      r.field = target.value;
      const type = RULE_FIELDS[r.field].type;
      r.op = OPS[type][0];
      r.value = defaultValue(type);
      redrawRules();
    }
  }

  function normalizedRules() {
    return {
      match: editor.rules.match === "any" ? "any" : "all",
      rules: editor.rules.rules.map((r) => {
        const type = (RULE_FIELDS[r.field] || RULE_FIELDS.title).type;
        let value = r.value;
        if (type === "number" || type === "days") value = Number(value) || 0;
        if (type === "bool") value = value === true || value === "true";
        return { field: r.field, op: r.op, value };
      }),
      sort: editor.rules.sort || "random", order: editor.rules.order || "desc", limit: Number(editor.rules.limit) || 0
    };
  }

  async function submitEditor(form) {
    const e = editor;
    if (!e) return;
    readRulesFromForm();
    const body = {
      name: el("mu-pl-name").value.trim(),
      description: el("mu-pl-desc").value.trim(),
      shared: el("mu-pl-shared").checked,
      collaborative: el("mu-pl-collab") ? el("mu-pl-collab").checked : false
    };
    const errors = [];
    if (!body.name) errors.push("Give the playlist a name.");
    if (e.smart) {
      body.rules = normalizedRules();
      if (!body.rules.rules.length) errors.push("Add at least one rule.");
      for (const r of body.rules.rules) {
        const type = RULE_FIELDS[r.field].type;
        if (type === "text" && !String(r.value).trim()) errors.push(`Fill in the value for “${RULE_FIELDS[r.field].label}”.`);
      }
    }
    if (errors.length) { U.showErrors(el("mu-pl-errors"), errors); return; }
    const submit = form.querySelector('[type="submit"]');
    submit.disabled = true;
    const res = e.existing
      ? await request(`/api/music/playlists/${enc(e.existing.id)}`, { method: "PUT", body })
      : await request("/api/music/playlists", { method: "POST", body: { ...body, trackIds: [] } });
    submit.disabled = false;
    if (!res.ok) { U.showErrors(el("mu-pl-errors"), res.error); return; }
    U.closeModal();
    showToast(e.existing ? "Playlist saved" : `Made “${res.data.name}”`, true);
    if (e.existing) refreshCurrent(); else go({ name: "playlist", id: res.data.id });
  }

  // ---- folders --------------------------------------------------------------------------------

  async function renderFolders(token, arg) {
    el("mu-content").dataset.built = "";
    const [libraryId, ...rest] = String(arg || "").split("|");
    const path = rest.join("|");
    if (!el("mu-content").querySelector(".au-folders")) setContent(loadingMarkup());
    const params = new URLSearchParams();
    if (libraryId) params.set("libraryId", libraryId);
    if (path) params.set("path", path);
    const res = await request(`/api/music/folders${params.toString() ? `?${params}` : ""}`);
    if (token !== routeToken) return;
    if (!res.ok) { setContent(errorMarkup(res.error)); return; }
    const d = res.data || {};
    const folders = d.folders || [];
    const tracks = d.tracks || [];
    // Fetch the library names first time in, or the first crumb says "Library" instead of its name.
    if (libraryId && !librariesCache) {
      const r = await request("/api/music/libraries");
      if (token !== routeToken) return;
      librariesCache = r.ok && Array.isArray(r.data) ? r.data : [];
    }
    const libName = libraryId ? ((librariesCache || []).find((l) => l.id === libraryId) || {}).name || "Library" : "";
    const crumbs = [`<a class="au-crumb" href="#music/folders">Libraries</a>`];
    if (libraryId) {
      crumbs.push(`<a class="au-crumb" href="#music/folders/${enc(libraryId + "|")}">${h(libName)}</a>`);
      let acc = "";
      for (const part of String(d.path || path || "").split("/").filter(Boolean)) {
        acc = acc ? `${acc}/${part}` : part;
        crumbs.push(`<a class="au-crumb" href="#music/folders/${enc(libraryId + "|" + acc)}">${h(part)}</a>`);
      }
    }
    const folderHref = (f) => libraryId ? `#music/folders/${enc(libraryId + "|" + f.path)}` : `#music/folders/${enc((f.libraryId || f.id || f.path) + "|")}`;
    setContent(`<div class="au-folders">
      <div class="au-crumbs">${crumbs.join(`<span class="au-crumb-sep">/</span>`)}</div>
      ${tracks.length ? `<div class="au-list-actions"><button type="button" class="mv-btn mv-btn-primary" data-act="play-list" data-list="folder" data-i="0">${ICON.play}<span>Play folder</span></button>
        <button type="button" class="mv-btn mv-btn-glass" data-act="shuffle-list" data-list="folder">${ICON.shuffle}<span>Shuffle</span></button></div>` : ""}
      ${folders.length ? `<div class="au-folder-list">${folders.map((f) => `<a class="au-folder-row" href="${folderHref(f)}">${ICON.folder}<span class="au-folder-name">${h(f.name)}</span>${f.trackCount ? `<span class="mv-folder-count">${plural(f.trackCount, "song")}</span>` : ""}</a>`).join("")}</div>` : ""}
      ${tracks.length ? trackList(tracks, { key: "folder", numbers: false, context: { type: "queue", id: null, name: d.path || libName || "Folder" } }) : ""}
      ${!folders.length && !tracks.length ? emptyMarkup("Empty folder", "") : ""}
    </div>`);
  }

  // ---- stats ----------------------------------------------------------------------------------

  function renderStats() {
    el("mu-content").dataset.built = "";
    const content = setContent(`<div id="mu-stats"></div>`);
    if (window.SentinelAudioExtras) window.SentinelAudioExtras.renderStats(content.querySelector("#mu-stats"), "music");
  }

  // ---- downloads ("+ add") --------------------------------------------------------------------

  const dl = { status: null, list: null, timer: null, states: {} };
  const add = { stage: "find", q: "", results: null, pick: null, search: null, timer: null, others: false };
  const STATE_WORDS = { queued: "waiting", downloading: "downloading", seeding: "sharing back", importing: "adding to library", done: "in the library", failed: "failed", canceled: "canceled" };

  async function refreshDownloadStatus() {
    const res = await request("/api/music/downloads/status");
    dl.status = res.ok ? res.data : null;
    const can = !!(dl.status && dl.status.canDownload);
    el("mu-add-btn").classList.toggle("hidden", !can);
    for (const b of document.querySelectorAll('#mu-subtabs [data-mu-nav="downloads"]')) b.classList.toggle("hidden", !can);
    if (can && !dl.list) pollDownloads(true);
  }

  function vpnLine(status) {
    const vpn = (status && status.vpn) || {};
    if (!status) return "";
    let tone = "idle";
    let text = "VPN idle · it connects by itself when someone searches or downloads.";
    if (vpn.state === "disabled") { tone = "off"; text = "VPN turned off in Settings · the sites see your home address."; }
    else if (vpn.state === "connected") { tone = vpn.verified ? "on" : "busy"; text = `VPN connected${vpn.location ? " · " + vpn.location : ""}${vpn.verified ? " · home address hidden" : " · checking…"}`; }
    else if (vpn.state === "connecting") { tone = "busy"; text = "VPN connecting…"; }
    else if (vpn.state === "down" || status.services === "failed") { tone = "bad"; text = vpn.error || "The VPN or torrent services failed to start."; }
    return `<div class="mv-dl-vpn mv-dl-vpn-${tone}"><span class="mv-dl-vpn-dot"></span><span class="mv-dl-vpn-label">VPN</span><span class="mv-dl-vpn-text">${h(text)}</span></div>`;
  }

  async function pollDownloads(quiet) {
    clearTimeout(dl.timer);
    const res = await request("/api/music/downloads");
    if (res.ok && res.data) {
      dl.list = res.data;
      for (const d of [...(dl.list.active || []), ...(dl.list.recent || [])]) {
        const before = dl.states[d.id];
        if (before && before !== d.state && d.state === "done" && !quiet) showToast(`${d.title}${d.subtitle ? " by " + d.subtitle : ""} is in the library`, true);
        if (before && before !== d.state && d.state === "failed" && !quiet) showToast(`${d.title}: ${d.error || "download failed"}`, false);
        dl.states[d.id] = d.state;
      }
      if (visible && route.name === "downloads") drawDownloads();
      if (visible && route.name === "home") { const b = el("mu-dl-banner"); if (b) b.outerHTML = dlBanner(); else if (dlBanner()) refreshCurrent(); }
    }
    const busy = dl.list && (dl.list.active || []).length > 0;
    if (visible && (busy || route.name === "downloads")) dl.timer = setTimeout(() => pollDownloads(false), 2500);
  }

  function dlBanner() {
    const active = (dl.list && dl.list.active) || [];
    if (!active.length) return "";
    const first = active[0];
    const pct = Math.round((Number(first.progress) || 0) * 100);
    return `<button type="button" class="mv-dl-banner" id="mu-dl-banner" data-mu-nav="downloads"><span class="mv-pulse"></span><span>${active.length === 1 ? `Downloading ${h(first.title)}${first.subtitle ? " by " + h(first.subtitle) : ""} · ${pct}%` : `${active.length} albums downloading`}</span><span class="mv-dl-banner-go">downloads ${ICON.right}</span></button>`;
  }

  async function renderDownloads(token) {
    el("mu-content").dataset.built = "";
    if (!dl.status) await refreshDownloadStatus();
    if (token !== routeToken) return;
    if (!dl.status || !dl.status.canDownload) {
      setContent(emptyMarkup("Album downloads are off", dl.status && dl.status.musicLibrary === false ? "Add a music library on this server's own disk first (Settings → Music)." : "They use the same VPN and torrent setup as Movies - turn on Movie Downloads in Settings.", ICON.download));
      return;
    }
    if (!dl.list) setContent(loadingMarkup());
    await pollDownloads(true);
    if (token !== routeToken) return;
    if (!dl.list) setContent(errorMarkup("Could not load the downloads."));
  }

  function downloadRow(d) {
    const pct = Math.round((Number(d.progress) || 0) * 1000) / 10;
    const active = ["queued", "downloading", "seeding", "importing"].includes(d.state);
    const speed = Number(d.speedBytesPerSecond) || 0;
    const facts = active
      ? [d.state === "downloading" ? `${pct}%` : STATE_WORDS[d.state], d.state === "downloading" && speed ? `${(speed / 1e6).toFixed(1)} MB/s` : "", U.sizeLabel(d.sizeBytes)]
      : [STATE_WORDS[d.state] || d.state, U.sizeLabel(d.sizeBytes), d.requestedBy ? `by ${d.requestedBy}` : "", U.ago(d.finishedAt || d.createdAt)];
    const actions = [];
    if (d.state === "failed") actions.push(`<button type="button" class="mv-btn" data-act="dl-retry" data-id="${h(d.id)}">${ICON.refresh}<span>retry</span></button>`);
    actions.push(active
      ? `<button type="button" class="mv-btn mv-btn-quiet" data-act="dl-cancel" data-id="${h(d.id)}">cancel</button>`
      : `<button type="button" class="mv-icon-btn" data-act="dl-remove" data-id="${h(d.id)}" aria-label="Remove from the list" title="Remove from the list">${ICON.close}</button>`);
    return `<div class="mv-dl mv-dl-${h(d.state)}" data-dl-id="${h(d.id)}">
      ${art(d.posterUrl, "au-square mv-dl-poster", d.title, "")}
      <div class="mv-dl-body">
        <div class="mv-dl-title">${h(d.title)}${d.subtitle ? ` <span class="mv-match-year-tag">${h(d.subtitle)}</span>` : ""}${d.year ? ` <span class="mv-match-year-tag">${h(d.year)}</span>` : ""}</div>
        <div class="mv-dl-release" title="${h(d.releaseTitle || "")}">${h(d.releaseTitle || "")}${d.site ? ` &middot; ${h(d.site)}` : ""}</div>
        ${active ? `<div class="mv-dl-bar"><span style="width:${d.state === "downloading" ? pct : d.state === "queued" ? 0 : 100}%"></span></div>` : ""}
        <div class="mv-dl-facts">${facts.filter(Boolean).map(h).join('<span class="mv-dot">&middot;</span>')}</div>
        ${d.error ? `<div class="${d.state === "failed" ? "mv-lib-error" : "mv-dl-note"}">${h(d.error)}</div>` : ""}
      </div>
      <div class="mv-lib-actions">${actions.join("")}</div>
    </div>`;
  }

  function drawDownloads() {
    const data = dl.list || {};
    const active = data.active || [];
    const recent = data.recent || [];
    const content = el("mu-content");
    const shape = [...active, ...recent].map((d) => `${d.id}:${d.state}`).join("|");
    if (content.dataset.dlShape === shape && content.querySelector(".au-dl-page")) {
      for (const d of active) {
        const row = content.querySelector(`[data-dl-id="${CSS.escape(d.id)}"]`);
        if (!row) continue;
        const scratch = document.createElement("div");
        scratch.innerHTML = downloadRow(d);
        for (const part of [".mv-dl-bar", ".mv-dl-facts"]) {
          const now = row.querySelector(part);
          const next = scratch.querySelector(part);
          if (now && next) now.replaceWith(next);
        }
      }
      return;
    }
    content.dataset.dlShape = shape;
    setContent(`<div class="au-dl-page">${vpnLine(data.status || (dl.status && dl.status.status))}
      <div class="mv-dl-head"><h3 class="mv-section-title">Downloading</h3>
        <button type="button" class="mv-btn mv-btn-primary" data-act="dl-add">${ICON.plus}<span>add an album</span></button></div>
      ${active.length ? `<div class="mv-dl-list">${active.map(downloadRow).join("")}</div>` : `<div class="mv-empty"><div>Nothing downloading. Press <b>add an album</b> to find one.</div></div>`}
      ${recent.length ? `<h3 class="mv-section-title">Recent</h3><div class="mv-dl-list">${recent.map(downloadRow).join("")}</div>` : ""}</div>`);
  }

  async function downloadAction(act, id, button) {
    if (act === "dl-cancel" && !confirm("Cancel this download? What has downloaded so far is deleted.")) return;
    if (button) button.disabled = true;
    const res = act === "dl-retry"
      ? await request(`/api/music/downloads/${enc(id)}/retry`, { method: "POST" })
      : await request(`/api/music/downloads/${enc(id)}`, { method: "DELETE" });
    if (!res.ok) { if (button) button.disabled = false; showToast(res.error, false); return; }
    pollDownloads(true);
  }

  function openAdd() {
    Object.assign(add, { stage: "find", pick: null, search: null, others: false });
    drawAdd();
    if (add.q && !Array.isArray(add.results)) lookup();
  }

  function stopAddPoll() { clearTimeout(add.timer); add.timer = null; }

  function drawAdd() {
    const title = add.stage === "releases" ? "Pick a download" : "Add an album";
    const body = add.stage === "releases" ? addReleasesMarkup() : addFindMarkup();
    const html = `${U.modalHeader(title)}<div class="mv-add au-add">${body}</div>`;
    const panel = el("au-modal-panel");
    if (U.modalOpen() && panel.querySelector(".au-add")) { panel.innerHTML = html; wireImages(panel); }
    else U.openModal(html, stopAddPoll);
    const input = el("mu-add-q");
    if (input && document.activeElement !== input) { input.focus(); input.setSelectionRange(input.value.length, input.value.length); }
  }

  function addFindMarkup() {
    let results = "";
    if (add.results === "loading") results = loadingMarkup("searching MusicBrainz");
    else if (typeof add.results === "string") results = `<div class="mv-empty mv-error">${h(add.results)}</div>`;
    else if (Array.isArray(add.results)) {
      results = add.results.length ? add.results.map((r, i) => `<button type="button" class="mv-match" data-mact="mu-add-pick" data-i="${i}">
          ${art(r.artUrl, "au-square mv-match-poster", r.title, r.artist)}
          <span class="mv-match-text">
            <span class="mv-match-title">${h(r.title)}${r.year ? ` <span class="mv-match-year-tag">${h(r.year)}</span>` : ""}${r.inLibrary ? ` <span class="mv-lib-kind mv-have">in library</span>` : ""}</span>
            <span class="mv-match-overview">${h(r.artist || "")}</span>
            <span class="mv-match-id">${h([r.type, r.trackCount ? plural(r.trackCount, "track") : ""].filter(Boolean).join(" · "))}</span>
          </span>
        </button>`).join("") : `<div class="mv-empty">Nothing on MusicBrainz called that. Try “artist album”, or fewer words.</div>`;
    }
    const st = dl.status || {};
    const vpnOff = st.status && st.status.vpn && st.status.vpn.state === "disabled" ? `<div class="mv-note mv-note-warn">The VPN is turned off in Settings, so the sites and everyone sharing will see your home address.</div>` : "";
    const noLib = st.musicLibrary === false ? `<div class="mv-note mv-note-warn">Add a music library on this server's own disk first.</div>` : "";
    return `${vpnOff}${noLib}
      <form class="mv-match-form" id="mu-add-form"><input class="field-input" id="mu-add-q" value="${h(add.q)}" placeholder="Artist and album, e.g. fleetwood mac rumours" autocomplete="off" autofocus>
        <button type="submit" class="mv-btn mv-btn-primary">${ICON.search}<span>find</span></button></form>
      <div class="mv-match-results">${results}</div>`;
  }

  async function lookup() {
    const q = add.q.trim();
    if (!q) return;
    add.results = "loading";
    drawAdd();
    const res = await request(`/api/music/downloads/lookup?${new URLSearchParams({ q })}`);
    if (add.stage !== "find" || add.q.trim() !== q) return;
    add.results = res.ok ? (Array.isArray(res.data) ? res.data : []) : res.error;
    drawAdd();
  }

  async function startSearch(pick) {
    stopAddPoll();
    add.stage = "releases";
    add.pick = pick;
    add.others = false;
    add.search = { phase: "connecting", message: "Connecting to the VPN..." };
    drawAdd();
    const res = await request("/api/music/downloads/search", { method: "POST", body: { artist: pick.artist, album: pick.title, year: pick.year || null, mbid: pick.mbid || null, artUrl: pick.artUrl || null } });
    if (add.stage !== "releases") return;
    if (!res.ok) { add.search = { phase: "failed", message: res.error }; drawAdd(); return; }
    add.search = res.data || {};
    drawAdd();
    if (add.search.phase !== "done" && add.search.phase !== "failed") pollSearch(add.search.id);
  }

  function pollSearch(id) {
    stopAddPoll();
    add.timer = setTimeout(async () => {
      const res = await request(`/api/music/downloads/search/${enc(id)}`);
      if (add.stage !== "releases" || !add.search || add.search.id !== id) return;
      if (!res.ok) { add.search = { ...add.search, phase: "failed", message: res.error }; drawAdd(); return; }
      const changed = res.data.phase !== add.search.phase || res.data.message !== add.search.message;
      add.search = res.data;
      if (changed || res.data.phase === "done") drawAdd();
      if (res.data.phase !== "done" && res.data.phase !== "failed") pollSearch(id);
    }, 2000);
  }

  function releaseMarkup(r, i, group) {
    const best = i === 0 && group === "results";
    const chips = [r.format, r.lossless ? "lossless" : ""].filter(Boolean).map((c) => `<span class="mv-lib-kind ${c === "lossless" ? "mv-have" : ""}">${h(c)}</span>`).join("");
    const facts = [U.sizeLabel(r.sizeBytes), r.seeders != null ? `${r.seeders} sharing` : "", r.site || "", r.publishedAt ? U.ago(r.publishedAt) : ""].filter(Boolean).map(h).join('<span class="mv-dot">&middot;</span>');
    return `<div class="mv-rel ${best ? "mv-rel-best" : ""}">
      <div class="mv-rel-body">
        <div class="mv-rel-chips">${best ? '<span class="mv-lib-kind mv-have">best match</span>' : ""}${chips}</div>
        <div class="mv-rel-title" title="${h(r.title)}">${h(r.title)}</div>
        <div class="mv-rel-facts">${facts}</div>
        ${r.warning ? `<div class="mv-dl-note">${h(r.warning)}</div>` : ""}
      </div>
      <button type="button" class="mv-btn ${best ? "mv-btn-primary" : ""}" data-mact="mu-add-grab" data-release="${h(r.id)}">download</button>
    </div>`;
  }

  function addReleasesMarkup() {
    const s = add.search || {};
    const p = add.pick || {};
    const what = `<div class="mv-add-what">${h(p.title || s.album || "")}${p.artist || s.artist ? ` <span class="mv-match-year-tag">${h(p.artist || s.artist)}</span>` : ""}${p.year ? ` <span class="mv-match-year-tag">${h(p.year)}</span>` : ""}</div>`;
    const back = `<button type="button" class="mv-link mv-add-back" data-mact="mu-add-back">${ICON.back} results</button>`;
    if (s.phase === "connecting" || s.phase === "searching") {
      return `${back}${what}<div class="mv-empty"><span class="mv-spinner"></span><span>${h(s.message || "Searching")}</span>
        <div class="mv-add-hint">${s.phase === "connecting" ? "The first search after a while starts the VPN; it can take a minute." : "Asking each of your sites."}</div></div>`;
    }
    if (s.phase === "failed") {
      return `${back}${what}<div class="mv-empty mv-error">${h(s.error || s.message || "The search failed.")}</div>
        <div class="mv-modal-foot"><button type="button" class="mv-btn" data-mact="mu-add-again">try again</button></div>`;
    }
    const results = s.results || [];
    const others = s.others || [];
    const locals = ((status && status.libraries) || []).filter((l) => !l.network);
    const libPick = locals.length > 1 ? `<label class="mv-field au-lib-pick"><span>Save into</span><select class="field-input" id="mu-add-lib">${locals.map((l) => `<option value="${h(l.id)}">${h(l.name)}</option>`).join("")}</select></label>` : "";
    return `${back}${what}${libPick}
      ${results.length ? `<div class="mv-rel-list">${results.map((r, i) => releaseMarkup(r, i, "results")).join("")}</div>`
        : `<div class="mv-empty">${others.length ? "None of the releases found are clearly this album with people sharing it." : h(s.message || "Nothing found on your sites.")}</div>`}
      ${others.length ? `<button type="button" class="mv-link mv-add-others" data-mact="mu-add-others">${add.others ? "hide" : "show"} ${plural(others.length, "other result")}</button>
        ${add.others ? `<div class="mv-rel-list mv-rel-others">${others.map((r, i) => releaseMarkup(r, i, "others")).join("")}</div>` : ""}` : ""}`;
  }

  async function grab(button, releaseId) {
    if (!add.search || !add.search.id) return;
    for (const b of document.querySelectorAll("#au-modal-panel [data-mact='mu-add-grab']")) b.disabled = true;
    button.classList.add("mv-working");
    const body = { searchId: add.search.id, releaseId };
    const lib = el("mu-add-lib");
    if (lib && lib.value) body.libraryId = lib.value;
    const res = await request("/api/music/downloads", { method: "POST", body });
    if (!res.ok) {
      for (const b of document.querySelectorAll("#au-modal-panel [data-mact='mu-add-grab']")) b.disabled = false;
      button.classList.remove("mv-working");
      showToast(res.error, false);
      return;
    }
    const d = res.data || {};
    dl.states[d.id] = d.state;
    U.closeModal();
    showToast(`Downloading ${d.title || "the album"}`, true);
    go({ name: "downloads" });
  }

  // ---- modal actions --------------------------------------------------------------------------

  U.onModalAction((act, target) => {
    switch (act) {
      case "mu-add-to": addTracksTo(target.dataset.id, target); return true;
      case "submit:mu-newpl-form": createWithTracks(el("mu-newpl-name").value.trim()); return true;
      case "submit:mu-pl-form": submitEditor(target); return true;
      case "mu-rule-add":
        readRulesFromForm();
        editor.rules.rules.push({ field: "genre", op: "is", value: "" });
        redrawRules();
        return true;
      case "mu-rule-remove":
        readRulesFromForm();
        editor.rules.rules.splice(Number(target.dataset.i), 1);
        redrawRules();
        return true;
      case "submit:mu-add-form": add.q = el("mu-add-q").value.trim(); add.results = null; lookup(); return true;
      case "mu-add-pick": { const r = Array.isArray(add.results) ? add.results[Number(target.dataset.i)] : null; if (r) startSearch(r); return true; }
      case "mu-add-back": stopAddPoll(); add.stage = "find"; drawAdd(); return true;
      case "mu-add-again": if (add.pick) startSearch(add.pick); return true;
      case "mu-add-others": add.others = !add.others; drawAdd(); return true;
      case "mu-add-grab": grab(target, target.dataset.release); return true;
      default: return false;
    }
  });

  function wireModalInputs() {
    el("au-modal").addEventListener("change", (e) => {
      if (e.target.dataset && e.target.dataset.rule != null && editor) onRuleChange(e.target);
    });
  }

  // ---- events ---------------------------------------------------------------------------------

  function wireView() {
    const view = el("view-music");
    view.addEventListener("click", (e) => {
      const nav = e.target.closest("[data-mu-nav]");
      if (nav && view.contains(nav)) { go({ name: nav.dataset.muNav }); return; }
      const target = e.target.closest("[data-act]");
      const row = !target && e.target.closest(".au-tr");
      if (row && !e.target.closest("a")) { playList(row.dataset.list, Number(row.dataset.i)); return; }
      if (!target || !view.contains(target)) return;
      const act = target.dataset.act;
      const id = target.dataset.id;
      switch (act) {
        case "back": history.length > 1 ? history.back() : go({ name: "home" }); break;
        case "retry": refreshCurrent(); break;
        case "scroll": U.scrollRow(target); break;
        case "open-album": go({ name: "album", id }); break;
        case "open-artist": go({ name: "artist", id }); break;
        case "open-playlist": go({ name: "playlist", id }); break;
        case "open-genre": go({ name: "genre", id }); break;
        case "open-decade": go({ name: "decade", id }); break;
        case "play-album": playAlbum(id); break;
        case "play-playlist": playPlaylist(id); break;
        case "play-mix": A.playMix(id, target.dataset.name); break;
        case "play-artist": playArtist(id, target.dataset.shuffle === "1"); break;
        case "radio-artist": A.startRadio("artist", id, `${target.dataset.name} Radio`); break;
        case "radio": A.startRadio(target.dataset.seed, id, target.dataset.name); break;
        case "shuffle-all": A.shuffleAll(); break;
        case "shuffle-genre":
          request(`/api/music/tracks?${new URLSearchParams({ genre: id, limit: "500", sort: "plays", order: "desc" })}`).then((res) => {
            if (res.ok) A.playTracks((res.data && res.data.items) || [], 0, { type: "queue", id, name: id }, { shuffle: true });
          });
          break;
        case "play-row": {
          const items = rowData.get(target.dataset.row) || [];
          const ctxName = { onthisday: "On this day", forgotten: "Forgotten favorites", loved: "Loved songs" }[target.dataset.row] || "Songs";
          A.playTracks(items, Number(target.dataset.i) || 0, { type: "queue", id: null, name: ctxName });
          break;
        }
        case "play-list": playList(target.dataset.list, Number(target.dataset.i) || 0); break;
        case "shuffle-list": playList(target.dataset.list, 0, { shuffle: true }); break;
        case "love-track": { const ref = listTrack(target); if (ref && ref.track) loveTrack(ref.track); break; }
        case "track-menu": { const ref = listTrack(target); if (ref) trackMenu(target, ref); break; }
        case "love-album": loveAlbumOrArtist("album", target); break;
        case "love-artist": loveAlbumOrArtist("artist", target); break;
        case "rate-album": rateAlbum(Number(target.dataset.value)); break;
        case "album-menu": {
          const a = current && current.data;
          if (!a) break;
          U.openMenu(target, [
            { label: "Play next", icon: ICON.queue, run: () => A.enqueue(a.tracks || [], "next", "track") },
            { label: "Add to queue", icon: ICON.plus, run: () => A.enqueue(a.tracks || [], "last", "track") },
            { label: "Add to playlist…", icon: ICON.plus, run: () => openAddToPlaylist(a.tracks || []) },
            a.artistId ? { label: "Go to artist", icon: ICON.note, run: () => go({ name: "artist", id: a.artistId }) } : null
          ], { title: a.title });
          break;
        }
        case "toggle-top": if (current) { current.showAllTop = !current.showAllTop; refreshCurrent(); } break;
        case "new-playlist": openPlaylistEditor(null, false); break;
        case "new-smart": openPlaylistEditor(null, true); break;
        case "pin-playlist": pinPlaylist(target); break;
        case "playlist-menu": playlistMenu(target); break;
        case "order": {
          const key = target.closest("[data-browse]").dataset.browse;
          const s = browse[key];
          s.order = s.order === "asc" ? "desc" : "asc";
          s.offset = 0;
          target.outerHTML = orderButton(s);
          routeTo(route);
          break;
        }
        case "toggle": {
          const key = target.closest("[data-browse]").dataset.browse;
          const s = browse[key];
          const f = target.dataset.f;
          s[f] = !s[f];
          s.offset = 0;
          target.classList.toggle("btn-on", s[f]);
          target.setAttribute("aria-pressed", String(s[f]));
          routeTo(route);
          break;
        }
        case "page": {
          const key = { artists: "artists", albums: "albums", songs: "songs", genre: "genre", decade: "decade" }[route.name];
          const s = browse[key];
          if (!s) break;
          s.offset = Math.max(0, s.offset + Number(target.dataset.dir) * (key === "songs" ? SONG_PAGE : GRID_PAGE));
          routeTo(route).then(() => el("mu-content").scrollIntoView({ behavior: "smooth", block: "start" }));
          break;
        }
        case "dl-add": openAdd(); break;
        case "dl-cancel": case "dl-retry": case "dl-remove": downloadAction(act, id, target); break;
        default: break;
      }
    });
    view.addEventListener("change", (e) => {
      const bar = e.target.closest("[data-browse]");
      if (!bar || !e.target.dataset.f || e.target.dataset.f === "q") return;
      const s = browse[bar.dataset.browse];
      s[e.target.dataset.f] = e.target.value;
      s.offset = 0;
      if (e.target.dataset.f === "sort") {
        // Each sort starts in its natural direction: "Most played" ascending listed the least
        // played first. The ▲/▼ button still reverses it.
        s.order = ["name", "title", "artist", "album"].includes(s.sort) ? "asc" : "desc";
        const ob = bar.querySelector('[data-act="order"]');
        if (ob) ob.outerHTML = orderButton(s);
      }
      routeTo(route);
    });
    view.addEventListener("input", (e) => {
      if (e.target.dataset && e.target.dataset.f === "q") {
        const bar = e.target.closest("[data-browse]");
        const s = browse[bar.dataset.browse];
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => { s.q = e.target.value.trim(); s.offset = 0; routeTo(route); }, 300);
      }
    });
    const search = el("mu-search");
    search.addEventListener("input", () => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => {
        const q = search.value.trim();
        if (q) { if (route.name === "search") history.replaceState(null, "", hashFor({ name: "search", id: q })); go({ name: "search", id: q }); }
        else if (route.name === "search") go({ name: "home" });
      }, 300);
    });
    search.addEventListener("keydown", (e) => { if (e.key === "Escape") { search.value = ""; if (route.name === "search") go({ name: "home" }); } });
    el("mu-add-btn").addEventListener("click", openAdd);
    el("mu-libraries-btn").addEventListener("click", openSettings);
    el("mu-shuffle-btn").addEventListener("click", () => A.shuffleAll());
    el("mu-alarms-btn").addEventListener("click", () => { if (window.SentinelAudioExtras) window.SentinelAudioExtras.openAlarms(); });
    A.on("add-to-playlist", (tracks) => openAddToPlaylist(tracks));
    A.on("sync", ({ type, id, patch }) => { if (type === "track" && patch.loved != null) drawLove(id, patch.loved); });
    wireModalInputs();
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
    scrollMemory[hashFor(route)] = window.scrollY;
    clearTimeout(statusTimer);
    clearTimeout(dl.timer);
    stopAddPoll();
  }

  wireView();

  return { show, hide, openAddToPlaylist, go };
})();
