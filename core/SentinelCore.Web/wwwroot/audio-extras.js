// The parts Music and Podcasts share that aren't the player (core/docs/MUSIC_AND_PODCASTS.md §1,
// §2, §9): the music libraries manager and the hand-built bits of the MUSIC and PODCASTS
// settings sections, wake-up alarms, the Morning Briefing, listening stats with a year-in-review
// "Wrapped" story, and ListenBrainz.
window.SentinelAudioExtras = (function () {
  const A = window.SentinelAudio;
  const U = A.util;
  const ICON = A.ICON;
  const { escapeHtml: h, enc, request, fmtClock, fmtDuration, plural, art, wireImages, loadingMarkup, showToast } = U;
  const el = (id) => document.getElementById(id);

  // ---- music libraries (admin) ----------------------------------------------------------------

  const PHASES = { files: "finding files", tags: "reading tags", artwork: "fetching artwork", lyrics: "fetching lyrics" };

  // Renders into container and keeps itself current while a scan runs and it's on screen.
  function librariesPanel(container, opts = {}) {
    if (!container) return;
    const state = { libs: null, status: null, timer: null, opts };
    container.classList.add("au-libs");
    container.innerHTML = loadingMarkup();
    container.auLibs = state;
    container.addEventListener("click", (e) => {
      const b = e.target.closest("[data-lib]");
      if (!b) return;
      const id = b.dataset.id;
      const lib = (state.libs || []).find((l) => l.id === id);
      switch (b.dataset.lib) {
        case "add": openLibraryEditor(container, null); break;
        case "edit": if (lib) openLibraryEditor(container, lib); break;
        case "scan": startScan(container, id); break;
        case "scan-all": startScan(container, null); break;
        case "remove": if (lib) removeLibrary(container, lib); break;
        default: break;
      }
    });
    loadLibraries(container);
  }

  async function loadLibraries(container) {
    const state = container.auLibs;
    clearTimeout(state.timer);
    const [libs, st] = await Promise.all([request("/api/music/libraries"), request("/api/music/status")]);
    if (!container.isConnected) return;
    if (!libs.ok) {
      container.innerHTML = `<div class="mv-note mv-note-warn">${h(libs.status === 404 ? "Music is turned off - switch it on above and save, then the libraries can be set up here." : libs.error)}</div>`;
      return;
    }
    state.libs = Array.isArray(libs.data) ? libs.data : [];
    state.status = st.ok ? st.data : null;
    drawLibraries(container);
    if (state.status && state.status.scanning) state.timer = setTimeout(() => { if (container.isConnected) loadLibraries(container); }, 3000);
  }

  function drawLibraries(container) {
    const { libs, status } = container.auLibs;
    const byId = new Map(((status && status.libraries) || []).map((l) => [l.id, l]));
    const scanning = status && status.scanning;
    const s = (status && status.scan) || {};
    const pct = s.total ? Math.min(100, 100 * (s.processed || 0) / s.total) : null;
    const scanBox = !status ? "" : scanning
      ? `<div class="mv-scanbox mv-scanbox-live">
          <div class="mv-scan-line"><span class="mv-pulse"></span><span>Scanning ${h(s.libraryName || "")} &middot; ${h(PHASES[s.phase] || s.phase || "")}</span><span class="mv-scan-count">${s.total ? `${s.processed || 0} / ${s.total}` : ""}</span></div>
          <div class="mv-scan-bar ${pct == null ? "mv-indeterminate" : ""}"><span style="width:${pct == null ? 30 : pct.toFixed(1)}%"></span></div>
          <div class="mv-scan-note">Playing keeps working while it scans.</div></div>`
      : `<div class="mv-scanbox">${ICON.check}<span>${plural(status.trackCount || 0, "song")} · ${plural(status.albumCount || 0, "album")} · ${plural(status.artistCount || 0, "artist")}${status.totalDurationSeconds ? ` · ${h(fmtDuration(status.totalDurationSeconds))}` : ""}${status.totalBytes ? ` · ${h(U.sizeLabel(status.totalBytes))}` : ""} · last scan ${h(U.ago(status.lastScanFinishedAt))}</span></div>`;
    const warn = status && status.ffmpegAvailable === false ? `<div class="mv-note mv-note-warn">ffmpeg isn't installed on the server: files a browser can't play natively won't play, and nothing can be converted for data saver.</div>` : "";
    const rows = libs.map((l0) => {
      const l = { ...l0, ...(byId.get(l0.id) || {}) };
      const busy = scanning && s.libraryName === l.name;
      return `<div class="mv-lib">
        <div class="mv-lib-icon">${l.network ? ICON.computer : ICON.note}</div>
        <div class="mv-lib-body">
          <div class="mv-lib-name">${h(l.name)}${l.network ? ' <span class="mv-lib-kind">network</span>' : ""}${busy ? '<span class="mv-lib-scanning"><span class="mv-pulse"></span>scanning</span>' : ""}</div>
          <code class="mv-path">${h(l.path)}</code>
          <div class="mv-lib-meta">${plural(l.trackCount || 0, "song")} · last scanned ${h(U.ago(l.lastScanFinishedAt))}</div>
          ${l.lastScanError ? `<div class="mv-lib-error">${h(l.lastScanError)}</div>` : ""}
        </div>
        <div class="mv-lib-actions">
          <button type="button" class="btn" data-lib="scan" data-id="${h(l.id)}">scan now</button>
          <button type="button" class="btn" data-lib="edit" data-id="${h(l.id)}">edit</button>
          <button type="button" class="btn mv-danger" data-lib="remove" data-id="${h(l.id)}">remove</button>
        </div>
      </div>`;
    }).join("");
    container.innerHTML = `${warn}${scanBox}
      <div class="au-libs-list">${rows || `<div class="au-q-empty">No music folders yet.</div>`}</div>
      <div class="mv-actions au-libs-actions">
        <button type="button" class="mv-btn mv-btn-primary" data-lib="add">${ICON.plus}<span>Add a music folder</span></button>
        ${libs.length ? `<button type="button" class="mv-btn mv-btn-glass" data-lib="scan-all">${ICON.refresh}<span>Scan all</span></button>` : ""}
      </div>`;
  }

  function openLibraryEditor(container, existing, draft) {
    const form = draft || { name: existing ? existing.name : (container.auLibs.libs.length ? "" : "Music"), path: existing ? existing.path : "" };
    U.openModal(`${U.modalHeader(existing ? "Edit music folder" : "Add a music folder")}
      <form class="mv-form" id="au-lib-form">
        <label class="mv-field"><span>Name</span><input class="field-input" id="au-lib-name" value="${h(form.name)}" maxlength="80" placeholder="Music" autofocus></label>
        <label class="mv-field"><span>Folder (on the server or another computer)</span>
          <div class="mv-path-row"><input class="field-input" id="au-lib-path" value="${h(form.path)}" placeholder="/mnt/music or smb://PC/Music" spellcheck="false">
          <button type="button" class="mv-btn" data-mact="au-lib-browse">${ICON.folder}<span>choose…</span></button></div></label>
        <div class="mv-field-note">${existing ? "Changing the folder rescans it. Plays, loves and playlists are kept for songs that are still there." : "Adding a folder starts a scan straight away. Tags, cover art and lyrics are read as it goes."}</div>
        <ul class="field-errors" id="au-lib-errors"></ul>
        <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Cancel</button><button type="submit" class="mv-btn mv-btn-primary">${existing ? "Save" : "Add"}</button></div>
      </form>`);
    libEditor = { container, existing };
  }

  let libEditor = null;

  async function submitLibrary(form) {
    const { container, existing } = libEditor || {};
    if (!container) return;
    const body = { name: el("au-lib-name").value.trim(), path: el("au-lib-path").value.trim() };
    const errors = [];
    if (!body.name) errors.push("Give it a name.");
    if (!body.path) errors.push("Choose the folder.");
    if (errors.length) { U.showErrors(el("au-lib-errors"), errors); return; }
    const submit = form.querySelector('[type="submit"]');
    submit.disabled = true;
    const res = await request(existing ? `/api/music/libraries/${enc(existing.id)}` : "/api/music/libraries", { method: existing ? "PUT" : "POST", body });
    submit.disabled = false;
    if (!res.ok) { U.showErrors(el("au-lib-errors"), res.error); return; }
    U.closeModal();
    showToast(existing ? "Saved" : "Added - scanning now", true);
    loadLibraries(container);
    if (container.auLibs.opts.onChange) container.auLibs.opts.onChange();
  }

  async function startScan(container, id) {
    const res = await request(id ? `/api/music/libraries/${enc(id)}/scan` : "/api/music/scan", { method: "POST" });
    if (res.status === 409) { showToast("A scan is already running", false); return; }
    if (!res.ok) { showToast(res.error, false); return; }
    showToast("Scan started", true);
    setTimeout(() => loadLibraries(container), 400);
  }

  async function removeLibrary(container, lib) {
    if (!confirm(`Remove “${lib.name}”?\n\nIts songs disappear from Music. The files aren't touched, and plays, loves and playlists are kept in case the folder comes back.`)) return;
    const res = await request(`/api/music/libraries/${enc(lib.id)}`, { method: "DELETE" });
    if (!res.ok) { showToast(res.error, false); return; }
    showToast("Removed", true);
    loadLibraries(container);
    if (container.auLibs.opts.onChange) container.auLibs.opts.onChange();
  }

  // ---- settings sections ----------------------------------------------------------------------

  // MUSIC: the schema's fields, then Libraries and ListenBrainz. Loaded when the section opens.
  function musicSettingsPanel(section, body, loaders) {
    const wrap = document.createElement("div");
    wrap.className = "au-settings-extra";
    wrap.innerHTML = `<div class="au-settings-block"><div class="au-settings-head">${ICON.folder}<span>Libraries</span></div><div class="au-settings-libs"></div></div>
      <div class="au-settings-block"><div class="au-settings-head">${ICON.stats}<span>ListenBrainz (your account)</span></div><div class="au-settings-lb"></div></div>`;
    const load = () => {
      librariesPanel(wrap.querySelector(".au-settings-libs"));
      listenBrainzCard(wrap.querySelector(".au-settings-lb"));
    };
    if (loaders) loaders.push(load); else load();
    insertBeforeFooter(body, wrap);
  }

  // PODCASTS: a "Choose…" beside downloadFolder, the storage line, and OPML.
  function podcastsSettingsPanel(section, body, loaders) {
    const input = section.querySelector('[data-field="downloadFolder"]');
    if (input && !input.dataset.auPicker) {
      input.dataset.auPicker = "1";
      const row = document.createElement("div");
      row.className = "mv-path-row au-settings-path";
      input.parentNode.insertBefore(row, input);
      row.appendChild(input);
      const choose = document.createElement("button");
      choose.type = "button";
      choose.className = "btn";
      choose.textContent = "choose…";
      choose.addEventListener("click", () => {
        U.openFolderBrowser(input.value.trim(), (path) => { input.value = path; input.dispatchEvent(new Event("input", { bubbles: true })); },
          null, "Where downloaded episodes are kept. Blank in the box means the server's own data folder. Save the section after choosing.");
      });
      row.appendChild(choose);
    }
    const wrap = document.createElement("div");
    wrap.className = "au-settings-extra";
    wrap.innerHTML = `<div class="au-settings-block"><div class="au-settings-head">${ICON.download}<span>Storage</span></div><div class="au-settings-storage">${loadingMarkup()}</div></div>
      <div class="au-settings-block"><div class="au-settings-head">${ICON.share}<span>Subscriptions (OPML)</span></div>
        <div class="mv-actions"><button type="button" class="btn" data-opml="import">import OPML…</button><a class="btn" href="/api/podcasts/opml" download="subscriptions.opml">export OPML</a>
        <button type="button" class="btn" data-opml="refresh">check every feed now</button></div>
        <div class="settings-hint">Import brings your shows over from another app; export is your own subscriptions.</div></div>`;
    wrap.addEventListener("click", async (e) => {
      const b = e.target.closest("[data-opml]");
      if (!b) return;
      if (b.dataset.opml === "import") importOpmlFile();
      if (b.dataset.opml === "refresh") {
        const res = await request("/api/podcasts/refresh", { method: "POST" });
        showToast(res.ok ? "Checking every feed" : res.error, res.ok);
      }
    });
    const load = () => storageLine(wrap.querySelector(".au-settings-storage"));
    if (loaders) loaders.push(load); else load();
    insertBeforeFooter(body, wrap);
  }

  function insertBeforeFooter(body, node) {
    const footer = body.querySelector(":scope > .settings-section-footer");
    if (footer) body.insertBefore(node, footer); else body.appendChild(node);
  }

  async function storageLine(box) {
    const res = await request("/api/podcasts/status");
    if (!box.isConnected) return;
    if (!res.ok) { box.innerHTML = `<div class="settings-hint">${h(res.status === 404 ? "Podcasts are off - switch them on above and save." : res.error)}</div>`; return; }
    const s = res.data || {};
    const used = Number(s.storageBytes) || 0;
    const max = Number(s.maxStorageBytes) || 0;
    const pct = max ? Math.min(100, 100 * used / max) : 0;
    const tr = s.transcripts || {};
    box.innerHTML = `<div class="au-storage">
        <div class="au-storage-line"><b>${h(U.sizeLabel(used) || "0 MB")}</b> used${max ? ` of ${h(U.sizeLabel(max))}` : " (no limit)"} · ${plural(s.podcastCount || 0, "show")} · ${plural(s.episodeCount || 0, "episode")}${s.downloadsActive || s.downloadsQueued ? ` · ${s.downloadsActive || 0} downloading, ${s.downloadsQueued || 0} waiting` : ""}</div>
        ${max ? `<div class="mv-scan-bar au-storage-bar ${pct > 90 ? "au-storage-full" : ""}"><span style="width:${pct.toFixed(1)}%"></span></div>` : ""}
        ${s.downloadFolder ? `<div class="settings-hint">In <code>${h(s.downloadFolder)}</code></div>` : ""}
        <div class="settings-hint">Transcripts: ${tr.enabled ? (tr.available ? `ready${tr.queued ? ` · ${tr.queued} waiting` : ""}` : `on, but ${h(tr.error || "whisper isn't working")}`) : "off"}${s.ffmpegAvailable === false ? " · ffmpeg is missing, so voice boost, trim silence and clips won't work" : ""}</div>
      </div>`;
  }

  function importOpmlFile() {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = ".opml,.xml,text/xml,application/xml";
    input.addEventListener("change", async () => {
      const file = input.files && input.files[0];
      if (!file) return;
      const res = await request("/api/podcasts/opml", { method: "POST", raw: await file.text(), contentType: "text/xml" });
      if (!res.ok) { showToast(res.error, false); return; }
      const d = res.data || {};
      showToast(`${d.added || 0} added, ${d.existing || 0} already followed${(d.failed || []).length ? `, ${d.failed.length} failed` : ""}`, true);
    });
    input.click();
  }

  // ---- ListenBrainz ---------------------------------------------------------------------------

  async function listenBrainzCard(box) {
    if (!box) return;
    box.innerHTML = loadingMarkup();
    const res = await request("/api/audio/me/listenbrainz");
    if (!box.isConnected) return;
    const d = res.ok && res.data ? res.data : { connected: false };
    box.innerHTML = d.connected
      ? `<div class="au-lb"><span class="au-lb-on">${ICON.check}<span>Sending your plays to ListenBrainz as <b>${h(d.username || "")}</b></span></span>
          <a class="mv-btn mv-btn-quiet" href="https://listenbrainz.org/user/${enc(d.username || "")}/" target="_blank" rel="noopener noreferrer">${ICON.external}<span>profile</span></a>
          <button type="button" class="mv-btn mv-btn-quiet" data-lb="off">disconnect</button></div>`
      : `<form class="au-lb au-lb-form"><span class="au-lb-intro">Keep a public history of what you listen to. Paste your user token from <a href="https://listenbrainz.org/settings/" target="_blank" rel="noopener noreferrer">listenbrainz.org/settings</a>.</span>
          <div class="mv-path-row"><input class="field-input" type="password" autocomplete="off" placeholder="ListenBrainz user token" data-lb="token"><button type="submit" class="mv-btn">connect</button></div>
          <ul class="field-errors"></ul></form>`;
    const form = box.querySelector("form");
    if (form) form.addEventListener("submit", async (e) => {
      e.preventDefault();
      const token = form.querySelector('[data-lb="token"]').value.trim();
      if (!token) return;
      const r = await request("/api/audio/me/listenbrainz", { method: "PUT", body: { token } });
      if (!r.ok) { U.showErrors(form.querySelector(".field-errors"), r.error); return; }
      showToast("ListenBrainz connected", true);
      listenBrainzCard(box);
    });
    const off = box.querySelector('[data-lb="off"]');
    if (off) off.addEventListener("click", async () => {
      const r = await request("/api/audio/me/listenbrainz", { method: "PUT", body: { token: "" } });
      if (!r.ok) { showToast(r.error, false); return; }
      listenBrainzCard(box);
    });
  }

  // ---- wake-up alarms -------------------------------------------------------------------------

  const DAYS = [["mon", "M"], ["tue", "T"], ["wed", "W"], ["thu", "T"], ["fri", "F"], ["sat", "S"], ["sun", "S"]];
  const SOURCE_LABELS = { playlist: "A playlist", album: "An album", artist: "An artist", mix: "A mix", shuffle: "Shuffle all", briefing: "Morning Briefing", podcast: "A podcast's newest episode" };
  const alarms = { list: [], speakers: [], editing: null, picks: null };

  function daysLabel(days) {
    const d = days || [];
    if (!d.length) return "once";
    if (d.length === 7) return "every day";
    const wk = ["mon", "tue", "wed", "thu", "fri"];
    if (d.length === 5 && wk.every((x) => d.includes(x))) return "weekdays";
    if (d.length === 2 && d.includes("sat") && d.includes("sun")) return "weekends";
    return DAYS.filter(([k]) => d.includes(k)).map(([k]) => k[0].toUpperCase() + k.slice(1)).join(" ");
  }

  function fmtAlarmTime(t) {
    const m = /^(\d{1,2}):(\d{2})/.exec(t || "");
    if (!m) return t || "";
    const date = new Date(2000, 0, 1, Number(m[1]), Number(m[2]));
    return date.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
  }

  async function openAlarms() {
    U.openModal(`${U.modalHeader("Wake-up alarms", "Music or a podcast that fades up on a speaker at a set time. Times are the server's time zone.")}<div id="au-alarms">${loadingMarkup()}</div>`, null, true);
    await loadAlarms();
  }

  async function loadAlarms() {
    const [a, s] = await Promise.all([request("/api/audio/alarms"), request("/api/audio/speakers")]);
    const box = el("au-alarms");
    if (!box) return;
    if (!a.ok) { box.innerHTML = `<div class="mv-empty mv-error">${h(a.error)}</div>`; return; }
    alarms.list = Array.isArray(a.data) ? a.data : [];
    alarms.speakers = s.ok && Array.isArray(s.data) ? s.data : [];
    drawAlarms();
  }

  function drawAlarms() {
    const box = el("au-alarms");
    if (!box) return;
    const speakerName = (id) => { const sp = alarms.speakers.find((x) => x.deviceId === id); return sp ? sp : null; };
    box.innerHTML = `${alarms.list.length ? `<div class="au-alarms">${alarms.list.map((al) => {
        const sp = speakerName(al.speakerId);
        return `<div class="au-alarm ${al.enabled ? "" : "off"}">
          <div class="au-alarm-time">${h(fmtAlarmTime(al.time))}</div>
          <div class="au-alarm-body">
            <div class="au-alarm-name">${h(al.name || "Alarm")} <span class="mv-lib-kind">${h(daysLabel(al.days))}</span></div>
            <div class="au-alarm-meta">${h((al.source && (al.source.name || SOURCE_LABELS[al.source.type])) || "")} · on ${h(sp ? sp.name : "a speaker that's gone")}${sp && !sp.online ? ' <span class="au-warn">(offline now)</span>' : ""} · ${Math.round((al.volume ?? 0.5) * 100)}%${al.rampSeconds ? ` · fades up over ${h(fmtDuration(al.rampSeconds))}` : ""}</div>
            ${al.enabled && al.nextFireAt ? `<div class="au-alarm-next">next ${h(new Date(al.nextFireAt).toLocaleString(undefined, { weekday: "short", hour: "numeric", minute: "2-digit" }))}</div>` : ""}
          </div>
          <div class="au-alarm-actions">
            <label class="switch" title="${al.enabled ? "On" : "Off"}"><input type="checkbox" data-mact="au-alarm-toggle" data-id="${h(al.id)}" ${al.enabled ? "checked" : ""}><span class="switch-track"></span></label>
            <button type="button" class="au-ctl au-ctl-xs" data-mact="au-alarm-test" data-id="${h(al.id)}" title="Play it now on the speaker">${ICON.play}</button>
            <button type="button" class="au-ctl au-ctl-xs" data-mact="au-alarm-edit" data-id="${h(al.id)}" title="Edit">${ICON.edit}</button>
            <button type="button" class="au-ctl au-ctl-xs" data-mact="au-alarm-delete" data-id="${h(al.id)}" title="Delete">${ICON.trash}</button>
          </div>
        </div>`;
      }).join("")}</div>` : `<div class="mv-empty"><div class="au-empty-glyph">${ICON.alarm}</div><div>No alarms yet.</div></div>`}
      ${alarms.speakers.length ? "" : `<div class="mv-note">Alarms play on a speaker: a kiosk, the Apple TV, or any app with <b>Let others play here</b> turned on. None are set up yet.</div>`}
      <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-primary" data-mact="au-alarm-new">${ICON.plus}<span>New alarm</span></button></div>`;
  }

  async function loadAlarmPicks() {
    if (alarms.picks) return alarms.picks;
    const flags = A.enabled();
    const [pl, home, pods] = await Promise.all([
      flags.music ? request("/api/music/playlists") : Promise.resolve({ ok: false }),
      flags.music ? request("/api/music/home") : Promise.resolve({ ok: false }),
      flags.podcasts ? request("/api/podcasts") : Promise.resolve({ ok: false })
    ]);
    const mixes = home.ok && home.data ? [...(home.data.mixes || []).map((m) => ({ id: m.id, name: m.name })), ...(home.data.decades || []).map((d) => ({ id: `decade-${d.decade}`, name: `The ${d.label || d.decade + "s"}` })), { id: "loved", name: "Loved" }, { id: "discover", name: "Discover" }] : [];
    alarms.picks = {
      playlist: pl.ok && Array.isArray(pl.data) ? pl.data.map((p) => ({ id: p.id, name: p.name })) : [],
      mix: mixes,
      podcast: pods.ok && Array.isArray(pods.data) ? pods.data.map((p) => ({ id: p.id, name: p.title })) : []
    };
    return alarms.picks;
  }

  async function openAlarmEditor(existing) {
    const flags = A.enabled();
    const box = el("au-alarms");
    if (!box) return;
    box.innerHTML = loadingMarkup();
    await loadAlarmPicks();
    const al = existing ? JSON.parse(JSON.stringify(existing)) : {
      name: "Wake up", time: "07:00", days: ["mon", "tue", "wed", "thu", "fri"], speakerId: (alarms.speakers.find((s) => s.online) || alarms.speakers[0] || {}).deviceId || "",
      source: flags.music ? { type: "shuffle", id: null, name: "Shuffle all" } : { type: "briefing", id: null, name: "Morning Briefing" }, volume: 0.4, rampSeconds: 120, enabled: true
    };
    alarms.editing = al;
    const types = Object.keys(SOURCE_LABELS).filter((t) => (["podcast"].includes(t) ? flags.podcasts : t === "briefing" ? true : flags.music));
    box.innerHTML = `<form class="mv-form" id="au-alarm-form">
      <div class="au-two-col">
        <label class="mv-field"><span>Time</span><input class="field-input au-alarm-time-input" type="time" id="au-al-time" value="${h(al.time)}" required></label>
        <label class="mv-field"><span>Name</span><input class="field-input" id="au-al-name" value="${h(al.name || "")}" maxlength="60"></label>
      </div>
      <div class="mv-field"><span>Days <small class="au-dim">(none = once)</small></span>
        <div class="au-days">${DAYS.map(([k, l]) => `<button type="button" class="au-day ${(al.days || []).includes(k) ? "on" : ""}" data-mact="au-al-day" data-day="${k}" title="${k}">${l}</button>`).join("")}
          <span class="au-day-presets"><button type="button" class="mv-link" data-mact="au-al-days" data-v="wk">weekdays</button> <button type="button" class="mv-link" data-mact="au-al-days" data-v="we">weekends</button> <button type="button" class="mv-link" data-mact="au-al-days" data-v="all">every day</button></span></div></div>
      <label class="mv-field"><span>Speaker</span><select class="field-input" id="au-al-speaker">${alarms.speakers.map((s) => `<option value="${h(s.deviceId)}" ${s.deviceId === al.speakerId ? "selected" : ""}>${h(s.name)}${s.online ? "" : " (offline)"}</option>`).join("") || '<option value="">No speakers yet</option>'}</select></label>
      <label class="mv-field"><span>Play</span><select class="field-input" id="au-al-type">${types.map((t) => `<option value="${t}" ${al.source && al.source.type === t ? "selected" : ""}>${h(SOURCE_LABELS[t])}</option>`).join("")}</select></label>
      <div id="au-al-source"></div>
      <div class="au-two-col">
        <label class="mv-field"><span>Volume <b id="au-al-vol-v">${Math.round((al.volume ?? 0.4) * 100)}%</b></span><input type="range" class="field-range" min="5" max="100" value="${Math.round((al.volume ?? 0.4) * 100)}" id="au-al-vol"></label>
        <label class="mv-field"><span>Fade up over</span><select class="field-input" id="au-al-ramp">${[[0, "no fade"], [30, "30 seconds"], [60, "1 minute"], [120, "2 minutes"], [300, "5 minutes"], [600, "10 minutes"]].map(([v, l]) => `<option value="${v}" ${Number(al.rampSeconds) === v ? "selected" : ""}>${l}</option>`).join("")}</select></label>
      </div>
      <ul class="field-errors" id="au-al-errors"></ul>
      <div class="mv-modal-foot"><button type="button" class="mv-btn mv-btn-quiet" data-mact="au-alarm-cancel">Back</button><button type="submit" class="mv-btn mv-btn-primary">${existing ? "Save" : "Add alarm"}</button></div>
    </form>`;
    drawSourcePicker();
    el("au-al-type").addEventListener("change", (e) => {
      alarms.editing.source = { type: e.target.value, id: null, name: SOURCE_LABELS[e.target.value] };
      drawSourcePicker();
    });
    el("au-al-vol").addEventListener("input", (e) => { el("au-al-vol-v").textContent = `${e.target.value}%`; });
  }

  function drawSourcePicker() {
    const al = alarms.editing;
    const box = el("au-al-source");
    if (!box || !al) return;
    const type = al.source.type;
    const pickList = alarms.picks && alarms.picks[type];
    if (pickList) {
      if (!al.source.id && pickList[0]) al.source = { type, id: pickList[0].id, name: pickList[0].name };
      box.innerHTML = pickList.length
        ? `<select class="field-input" id="au-al-pick">${pickList.map((p) => `<option value="${h(p.id)}" ${p.id === al.source.id ? "selected" : ""}>${h(p.name)}</option>`).join("")}</select>`
        : `<div class="au-q-empty">None to choose from yet.</div>`;
      const sel = el("au-al-pick");
      if (sel) sel.addEventListener("change", () => { const p = pickList.find((x) => x.id === sel.value); if (p) al.source = { type, id: p.id, name: p.name }; });
      return;
    }
    if (type === "album" || type === "artist") {
      box.innerHTML = `<div class="mv-path-row"><input class="field-input" id="au-al-q" placeholder="Search for ${type === "album" ? "an album" : "an artist"}" autocomplete="off"></div>
        <div class="au-al-chosen">${al.source.id ? `Chosen: <b>${h(al.source.name)}</b>` : ""}</div><div class="au-al-results" id="au-al-results"></div>`;
      let timer = null;
      el("au-al-q").addEventListener("input", (e) => {
        clearTimeout(timer);
        timer = setTimeout(async () => {
          const q = e.target.value.trim();
          const results = el("au-al-results");
          if (!q || !results) return;
          const res = await request(`/api/music/search?${new URLSearchParams({ q })}`);
          if (!el("au-al-results") || !res.ok) return;
          const list = type === "album" ? (res.data.albums || []).map((a) => ({ id: a.id, name: a.title, sub: a.artist, art: a.artUrl })) : (res.data.artists || []).map((a) => ({ id: a.id, name: a.name, sub: plural(a.albumCount || 0, "album"), art: a.artUrl }));
          results.innerHTML = list.slice(0, 8).map((x) => `<button type="button" class="au-al-result" data-mact="au-al-pick" data-id="${h(x.id)}" data-name="${h(x.name)}">${art(x.art, "au-square au-al-art", x.name, "")}<span><b>${h(x.name)}</b> <span class="au-dim">${h(x.sub || "")}</span></span></button>`).join("") || `<div class="au-q-empty">Nothing found.</div>`;
          wireImages(results);
        }, 300);
      });
      return;
    }
    box.innerHTML = `<div class="mv-field-note">${type === "briefing" ? "The weather, today's calendar and reminders are read out, then the newest episode of each show you've put in your briefing." : "A shuffle of the whole library, weighted away from what you've played lately."}</div>`;
    al.source = { type, id: null, name: SOURCE_LABELS[type] };
  }

  async function saveAlarm() {
    const al = alarms.editing;
    if (!al) return;
    const body = {
      name: el("au-al-name").value.trim() || "Alarm", time: el("au-al-time").value, days: al.days || [],
      speakerId: el("au-al-speaker").value, source: al.source, volume: Number(el("au-al-vol").value) / 100,
      rampSeconds: Number(el("au-al-ramp").value) || 0, enabled: al.enabled !== false
    };
    const errors = [];
    if (!/^\d{2}:\d{2}$/.test(body.time)) errors.push("Pick a time.");
    if (!body.speakerId) errors.push("Choose a speaker. Turn on “Let others play here” on one first.");
    if (["playlist", "album", "artist", "mix", "podcast"].includes(body.source.type) && !body.source.id) errors.push("Choose what to play.");
    if (errors.length) { U.showErrors(el("au-al-errors"), errors); return; }
    const res = al.id ? await request(`/api/audio/alarms/${enc(al.id)}`, { method: "PUT", body }) : await request("/api/audio/alarms", { method: "POST", body });
    if (!res.ok) { U.showErrors(el("au-al-errors"), res.error); return; }
    alarms.editing = null;
    showToast(al.id ? "Alarm saved" : "Alarm set", true);
    loadAlarms();
  }

  // ---- Morning Briefing -----------------------------------------------------------------------

  const briefing = { data: null, speaking: false, cancel: false };

  async function openBriefing() {
    U.openModal(`${U.modalHeader("Morning Briefing")}<div id="au-brief">${loadingMarkup("putting it together")}</div>`, () => { briefing.cancel = true; if ("speechSynthesis" in window) window.speechSynthesis.cancel(); });
    const res = await request("/api/audio/briefing");
    const box = el("au-brief");
    if (!box) return;
    if (!res.ok) { box.innerHTML = `<div class="mv-empty mv-error">${h(res.error)}</div>`; return; }
    briefing.data = res.data || {};
    const d = briefing.data;
    const eps = d.episodes || [];
    box.innerHTML = `<div class="au-brief">
      <div class="au-brief-greeting">${ICON.sun}<span>${h(d.greeting || "Good morning.")}</span></div>
      <ol class="au-brief-lines">${(d.speech || []).map((line, i) => `<li data-line="${i}">${h(line)}</li>`).join("")}</ol>
      ${eps.length ? `<div class="au-brief-eps-title">Then ${plural(eps.length, "episode")}:</div>
        <div class="au-brief-eps">${eps.map((e) => `<div class="au-fill-ep"><span class="au-q-art">${U.safeSrc(e.artUrl) ? `<img src="${h(e.artUrl)}" alt="">` : ICON.mic}</span><span class="au-q-text"><span class="au-q-title">${h(e.title)}</span><span class="au-q-sub">${h(e.podcastTitle || "")}</span></span><span class="au-q-dur">${e.durationSeconds ? fmtDuration(e.durationSeconds) : ""}</span></div>`).join("")}</div>`
        : `<div class="mv-field-note">No episodes yet: switch on “In my Morning Briefing” in a show's settings (Podcasts → a show → speed button).</div>`}
      <div class="mv-modal-foot">
        <button type="button" class="mv-btn" data-mact="au-brief-speaker">${ICON.cast}<span>play on a speaker</span></button>
        <button type="button" class="mv-btn mv-btn-primary" data-mact="au-brief-play">${ICON.play}<span>Play here</span></button>
      </div></div>`;
    wireImages(box);
  }

  async function playBriefingHere(button) {
    const d = briefing.data;
    if (!d) return;
    if (button) button.disabled = true;
    briefing.cancel = false;
    if (A.isPlaying()) A.pause();
    const lines = [d.greeting, ...(d.speech || [])].filter(Boolean);
    for (let i = 0; i < lines.length; i++) {
      if (briefing.cancel) return;
      for (const li of document.querySelectorAll(".au-brief-lines li")) li.classList.toggle("au-current", Number(li.dataset.line) === i - 1);
      await U.speak(lines[i]);
    }
    if (briefing.cancel) return;
    const eps = d.episodes || [];
    if (eps.length) A.playEpisodes(eps, 0, { type: "queue", id: "briefing", name: "Morning Briefing" });
    U.closeModal();
  }

  async function briefingToSpeaker(anchor) {
    const d = briefing.data;
    if (!d) return;
    const res = await A.listSpeakers();
    const list = res.ok && Array.isArray(res.data) ? res.data.filter((s) => s.online && s.deviceId !== U.deviceId()) : [];
    if (!list.length) { showToast("No speakers are online.", false); return; }
    U.openMenu(anchor, list.map((s) => ({
      label: s.name, icon: ICON.speaker, run: async () => {
        const text = [d.greeting, ...(d.speech || [])].filter(Boolean).join(" ");
        const a = await A.sendCommand(s.deviceId, { action: "announce", text });
        if (!a.ok) return;
        const eps = d.episodes || [];
        if (eps.length) await A.sendCommand(s.deviceId, { action: "play", queue: eps.map((e) => ({ type: "episode", id: e.id })), index: 0, positionSeconds: 0, shuffle: false, context: { type: "queue", id: "briefing", name: "Morning Briefing" } });
        showToast(`Briefing playing on ${s.name}`, true);
        U.closeModal();
      }
    })), { title: "Play the briefing on…" });
  }

  // ---- stats ----------------------------------------------------------------------------------

  const statsState = { period: "month", year: new Date().getFullYear() };
  const WEEKDAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

  function hourLabel(hr) {
    const d = new Date(2000, 0, 1, hr);
    return d.toLocaleTimeString(undefined, { hour: "numeric" });
  }

  function hoursMinutes(minutes) {
    const m = Math.round(Number(minutes) || 0);
    if (m < 60) return `${m} min`;
    const hrs = Math.floor(m / 60);
    return `${hrs.toLocaleString()} h${m % 60 ? ` ${m % 60} min` : ""}`;
  }

  // One-hue bar chart (magnitude only), each bar with a tooltip and the peak labelled.
  function barChart(values, labels, unit, cls) {
    const list = (values || []).map((v) => Number(v) || 0);
    const max = Math.max(1, ...list);
    const peak = list.indexOf(Math.max(...list));
    return `<div class="au-bars ${cls || ""}" role="img" aria-label="${h(labels.map((l, i) => `${l}: ${Math.round(list[i] || 0)} ${unit}`).join(", "))}">
      ${list.map((v, i) => `<div class="au-bar-col ${i === peak && v > 0 ? "peak" : ""}" title="${h(labels[i])}: ${Math.round(v)} ${unit}">
        <span class="au-bar-fill" style="height:${(100 * v / max).toFixed(1)}%"></span>
        ${i === peak && v > 0 ? `<span class="au-bar-peak">${Math.round(v)}</span>` : ""}
        <span class="au-bar-label">${h(labels[i])}</span></div>`).join("")}
    </div>`;
  }

  function tile(value, label, sub) {
    return `<div class="au-stat-tile"><div class="au-stat-value">${value}</div><div class="au-stat-label">${h(label)}</div>${sub ? `<div class="au-stat-sub">${h(sub)}</div>` : ""}</div>`;
  }

  async function renderStats(container, focus) {
    if (!container) return;
    container.auFocus = focus;
    const nowYear = new Date().getFullYear();
    const years = [];
    for (let y = nowYear; y >= nowYear - 6; y--) years.push(y);
    container.innerHTML = `<div class="au-stats">
      <div class="au-stats-head">
        <div><h2 class="au-page-title">Your listening</h2><div class="au-page-sub">Only yours - nobody else in the household sees these.</div></div>
        <div class="au-stats-controls">
          <div class="mv-seg" role="radiogroup">${[["week", "Week"], ["month", "Month"], ["year", "Year"], ["all", "All time"]].map(([k, l]) => `<button type="button" class="${statsState.period === k ? "active" : ""}" data-st="period" data-v="${k}">${l}</button>`).join("")}</div>
          <select class="field-input mv-select ${statsState.period === "year" ? "" : "hidden"}" data-st="year" aria-label="Year">${years.map((y) => `<option value="${y}" ${y === statsState.year ? "selected" : ""}>${y}</option>`).join("")}</select>
        </div>
      </div>
      <div class="au-stats-body">${loadingMarkup()}</div>
    </div>`;
    if (!container.dataset.wired) {
      container.dataset.wired = "1";
      container.addEventListener("click", (e) => {
        const b = e.target.closest("[data-st]");
        if (!b) return;
        if (b.dataset.st === "period") { statsState.period = b.dataset.v; renderStats(container, container.auFocus); }
        if (b.dataset.st === "wrapped") openWrapped(Number(b.dataset.year) || statsState.year);
        if (b.dataset.st === "artist") location.hash = `#music/artist/${enc(b.dataset.id)}`;
        if (b.dataset.st === "album") location.hash = `#music/album/${enc(b.dataset.id)}`;
        if (b.dataset.st === "show") location.hash = `#podcasts/show/${enc(b.dataset.id)}`;
      });
      container.addEventListener("change", (e) => {
        if (e.target.dataset.st === "year") { statsState.year = Number(e.target.value); renderStats(container, container.auFocus); }
      });
    }
    const params = new URLSearchParams({ period: statsState.period });
    if (statsState.period === "year") params.set("year", String(statsState.year));
    const res = await request(`/api/audio/stats?${params}`);
    const body = container.querySelector(".au-stats-body");
    if (!body) return;
    if (!res.ok) { body.innerHTML = `<div class="mv-empty mv-error">${h(res.error)}</div>`; return; }
    const d = res.data || {};
    const flags = A.enabled();
    const wrappedCard = statsState.period === "year" ? `<button type="button" class="au-wrapped-card" data-st="wrapped" data-year="${statsState.year}">
        <span class="au-wrapped-card-kicker">${ICON.gift} Year in review</span><span class="au-wrapped-card-title">Your ${statsState.year} Wrapped</span><span class="au-wrapped-card-go">play the story ${ICON.right}</span></button>` : "";
    const music = flags.music && d.music ? musicStats(d.music) : "";
    const pods = flags.podcasts && d.podcasts ? podcastStats(d.podcasts) : "";
    const lb = flags.music ? `<section class="mv-section"><h3 class="mv-section-title">ListenBrainz</h3><div class="au-stats-lb"></div></section>` : "";
    body.innerHTML = wrappedCard + (focus === "podcasts" ? pods + music : music + pods) + lb;
    wireImages(body);
    const lbBox = body.querySelector(".au-stats-lb");
    if (lbBox) listenBrainzCard(lbBox);
  }

  function musicStats(m) {
    if (!m.plays && !m.minutes) return `<section class="mv-section"><h3 class="mv-section-title">Music</h3><div class="au-q-empty">No music played in this period yet.</div></section>`;
    const topArtists = m.topArtists || [];
    const maxPlays = Math.max(1, ...topArtists.map((a) => a.plays || 0));
    return `<section class="mv-section"><h3 class="mv-section-title">Music</h3>
      <div class="au-stat-tiles">
        ${tile(h(hoursMinutes(m.minutes)), "listened")}
        ${tile(h((m.plays || 0).toLocaleString()), "plays", m.skips ? `${m.skips} skipped` : "")}
        ${tile(h((m.distinctTracks || 0).toLocaleString()), "different songs")}
        ${tile(h((m.distinctArtists || 0).toLocaleString()), "artists", m.newArtists ? `${m.newArtists} new to you` : "")}
        ${m.longestStreakDays ? tile(h(m.longestStreakDays), "day streak", "longest run of days with music") : ""}
      </div>
      <div class="au-stats-grid">
        ${topArtists.length ? `<div class="au-stats-card"><h4>Top artists</h4><div class="au-rank">${topArtists.map((a, i) => `<button type="button" class="au-rank-row" data-st="artist" data-id="${h(a.artist && a.artist.id)}">
            <span class="au-rank-n">${i + 1}</span><span class="au-rank-art au-round">${U.safeSrc(a.artist && a.artist.artUrl) ? `<img src="${h(U.artSized(a.artist.artUrl, 96))}" alt="">` : ICON.note}</span>
            <span class="au-rank-text"><span class="au-rank-title">${h(a.artist && a.artist.name)}</span><span class="au-rank-meter"><span style="width:${(100 * (a.plays || 0) / maxPlays).toFixed(1)}%"></span></span></span>
            <span class="au-rank-val">${plural(a.plays || 0, "play")}</span></button>`).join("")}</div></div>` : ""}
        ${(m.topTracks || []).length ? `<div class="au-stats-card"><h4>Top songs</h4><div class="au-rank">${m.topTracks.map((t, i) => `<div class="au-rank-row">
            <span class="au-rank-n">${i + 1}</span><span class="au-rank-art">${U.safeSrc(t.track && t.track.artUrl) ? `<img src="${h(U.artSized(t.track.artUrl, 96))}" alt="">` : ICON.note}</span>
            <span class="au-rank-text"><span class="au-rank-title">${h(t.track && t.track.title)}</span><span class="au-rank-sub">${h(t.track && t.track.artist)}</span></span>
            <span class="au-rank-val">${plural(t.plays || 0, "play")}</span></div>`).join("")}</div></div>` : ""}
      </div>
      ${(m.topAlbums || []).length ? `<div class="au-stats-card"><h4>Top albums</h4><div class="au-stat-albums">${m.topAlbums.map((a, i) => `<button type="button" class="au-stat-album" data-st="album" data-id="${h(a.album && a.album.id)}">${art(U.artSized(a.album && a.album.artUrl, 300), "au-square", a.album && a.album.title, a.album && a.album.artist, `<span class="au-stat-album-n">${i + 1}</span>`)}<span class="mv-card-title">${h(a.album && a.album.title)}</span><span class="mv-card-sub">${plural(a.plays || 0, "play")}</span></button>`).join("")}</div></div>` : ""}
      <div class="au-stats-grid">
        ${(m.topGenres || []).length ? `<div class="au-stats-card"><h4>Genres</h4>${genreBars(m.topGenres)}</div>` : ""}
        <div class="au-stats-card"><h4>When you listen</h4>${barChart(m.byHour, Array.from({ length: 24 }, (_, i) => (i % 6 === 0 ? hourLabel(i) : "")), "min", "au-bars-hours")}
          <div class="au-bars-gap"></div>${barChart(m.byWeekday, WEEKDAYS, "min", "au-bars-week")}</div>
      </div>
      ${m.firstPlay && m.firstPlay.track ? `<div class="au-first-play">${ICON.clock}<span>First song of the period: <b>${h(m.firstPlay.track.title)}</b> by ${h(m.firstPlay.track.artist)}, ${h(new Date(m.firstPlay.playedAt).toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }))}</span></div>` : ""}
    </section>`;
  }

  function genreBars(list) {
    const max = Math.max(1, ...list.map((g) => g.minutes || 0));
    return `<div class="au-hbars">${list.map((g) => `<div class="au-hbar" title="${h(g.genre)}: ${h(hoursMinutes(g.minutes))}"><span class="au-hbar-label">${h(g.genre)}</span><span class="au-hbar-track"><span style="width:${(100 * (g.minutes || 0) / max).toFixed(1)}%"></span></span><span class="au-hbar-val">${h(hoursMinutes(g.minutes))}</span></div>`).join("")}</div>`;
  }

  function podcastStats(p) {
    if (!p.minutes && !p.episodesFinished) return `<section class="mv-section"><h3 class="mv-section-title">Podcasts</h3><div class="au-q-empty">No podcasts played in this period yet.</div></section>`;
    const shows = p.topShows || [];
    const max = Math.max(1, ...shows.map((s) => s.minutes || 0));
    return `<section class="mv-section"><h3 class="mv-section-title">Podcasts</h3>
      <div class="au-stat-tiles">
        ${tile(h(hoursMinutes(p.minutes)), "listened")}
        ${tile(h((p.episodesFinished || 0).toLocaleString()), "episodes finished")}
        ${p.timeSavedMinutes ? tile(h(hoursMinutes(p.timeSavedMinutes)), "saved", "by speed, trimmed silence and skips") : ""}
      </div>
      <div class="au-stats-grid">
        ${shows.length ? `<div class="au-stats-card"><h4>Top shows</h4><div class="au-rank">${shows.map((s, i) => `<button type="button" class="au-rank-row" data-st="show" data-id="${h(s.podcast && s.podcast.id)}">
            <span class="au-rank-n">${i + 1}</span><span class="au-rank-art">${U.safeSrc(s.podcast && s.podcast.artUrl) ? `<img src="${h(s.podcast.artUrl)}" alt="">` : ICON.mic}</span>
            <span class="au-rank-text"><span class="au-rank-title">${h(s.podcast && s.podcast.title)}</span><span class="au-rank-meter"><span style="width:${(100 * (s.minutes || 0) / max).toFixed(1)}%"></span></span></span>
            <span class="au-rank-val">${h(hoursMinutes(s.minutes))}</span></button>`).join("")}</div></div>` : ""}
        <div class="au-stats-card"><h4>When you listen</h4>${barChart(p.byHour, Array.from({ length: 24 }, (_, i) => (i % 6 === 0 ? hourLabel(i) : "")), "min", "au-bars-hours")}
          <div class="au-bars-gap"></div>${barChart(p.byWeekday, WEEKDAYS, "min", "au-bars-week")}</div>
      </div>
    </section>`;
  }

  // ---- Wrapped --------------------------------------------------------------------------------

  // The year as a story: full-screen slides that advance on their own, tap the right side to
  // go on and the left to go back, arrows and Esc on a keyboard.
  const wrapped = { slides: [], i: 0, timer: null, root: null };
  const SLIDE_MS = 6500;

  async function openWrapped(year) {
    let root = el("au-wrapped");
    if (!root) {
      root = document.createElement("div");
      root.id = "au-wrapped";
      root.className = "au-wrapped";
      document.body.appendChild(root);
      root.addEventListener("click", (e) => {
        if (e.target.closest("[data-wr=close]")) { closeWrapped(); return; }
        if (e.target.closest("[data-wr=replay]")) { showSlide(0); return; }
        if (e.target.closest("a, button")) return;
        const box = root.getBoundingClientRect();
        if (e.clientX < box.left + box.width * 0.3) showSlide(wrapped.i - 1); else showSlide(wrapped.i + 1);
      });
      window.addEventListener("keydown", (e) => {
        if (!wrapped.root || wrapped.root.classList.contains("hidden")) return;
        if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); closeWrapped(); }
        if (e.key === "ArrowRight" || e.key === " ") { e.preventDefault(); e.stopPropagation(); showSlide(wrapped.i + 1); }
        if (e.key === "ArrowLeft") { e.preventDefault(); e.stopPropagation(); showSlide(wrapped.i - 1); }
      }, true);
    }
    wrapped.root = root;
    root.classList.remove("hidden");
    document.body.classList.add("au-noscroll");
    root.innerHTML = `<div class="au-wr-loading">${loadingMarkup("unwrapping " + year)}</div>`;
    const res = await request(`/api/audio/stats?${new URLSearchParams({ period: "year", year: String(year) })}`);
    if (root.classList.contains("hidden")) return;
    if (!res.ok) { root.innerHTML = `<div class="au-wr-loading"><div class="mv-empty mv-error">${h(res.error)}</div><button type="button" class="mv-btn" data-wr="close">close</button></div>`; return; }
    wrapped.slides = buildSlides(res.data || {}, year);
    root.innerHTML = `<div class="au-wr-progress">${wrapped.slides.map(() => `<span><i></i></span>`).join("")}</div>
      <button type="button" class="au-wr-close" data-wr="close" aria-label="Close">${ICON.close}</button>
      <div class="au-wr-stage"></div>`;
    showSlide(0);
  }

  function closeWrapped() {
    clearTimeout(wrapped.timer);
    if (wrapped.root) { wrapped.root.classList.add("hidden"); wrapped.root.innerHTML = ""; }
    document.body.classList.remove("au-noscroll");
  }

  function showSlide(i) {
    const n = wrapped.slides.length;
    if (!n || !wrapped.root) return;
    if (i >= n) { closeWrapped(); return; }
    i = Math.max(0, i);
    wrapped.i = i;
    clearTimeout(wrapped.timer);
    const slide = wrapped.slides[i];
    const stage = wrapped.root.querySelector(".au-wr-stage");
    stage.innerHTML = `<div class="au-wr-slide au-wr-${h(slide.theme)}">${slide.html}</div>`;
    wireImages(stage);
    const bars = wrapped.root.querySelectorAll(".au-wr-progress > span");
    bars.forEach((b, k) => { b.className = k < i ? "done" : k === i ? "now" : ""; });
    const now = bars[i] && bars[i].querySelector("i");
    if (now) { now.style.animation = "none"; void now.offsetWidth; now.style.animation = ""; now.style.animationDuration = `${SLIDE_MS}ms`; }
    for (const c of stage.querySelectorAll("[data-count]")) countUp(c);
    if (i < n - 1) wrapped.timer = setTimeout(() => showSlide(i + 1), SLIDE_MS);
  }

  function countUp(node) {
    const target = Number(node.dataset.count) || 0;
    const started = performance.now();
    const dur = 1400;
    const step = () => {
      const k = Math.min(1, (performance.now() - started) / dur);
      const eased = 1 - Math.pow(1 - k, 3);
      node.textContent = Math.round(target * eased).toLocaleString();
      if (k < 1 && node.isConnected) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
    // Animation frames stop in a hidden tab; the real number still has to land.
    setTimeout(() => { if (node.isConnected) node.textContent = target.toLocaleString(); }, dur + 100);
  }

  function buildSlides(d, year) {
    const m = d.music || {};
    const p = d.podcasts || {};
    const flags = A.enabled();
    const name = (U.authInfo() && U.authInfo().username) || "";
    const slides = [];
    slides.push({ theme: "intro", html: `<div class="au-wr-kicker">${h(name ? `${name}, this was` : "This was")}</div><div class="au-wr-huge">${h(year)}</div><div class="au-wr-line">in music and podcasts</div><div class="au-wr-hint">tap to go on</div>` });
    const totalMin = (Number(m.minutes) || 0) + (Number(p.minutes) || 0);
    if (!totalMin) {
      slides.push({ theme: "minutes", html: `<div class="au-wr-line">Not much listening in ${h(year)} yet.</div><div class="au-wr-sub">Come back once you've played a few things.</div>` });
      return slides;
    }
    if (flags.music && m.minutes) {
      const days = (m.minutes / 1440).toFixed(1);
      slides.push({ theme: "minutes", html: `<div class="au-wr-kicker">You listened to</div><div class="au-wr-huge" data-count="${Math.round(m.minutes)}">0</div><div class="au-wr-line">minutes of music</div><div class="au-wr-sub">That's ${h(days)} whole days. ${m.plays ? `${Number(m.plays).toLocaleString()} plays of ${Number(m.distinctTracks || 0).toLocaleString()} different songs.` : ""}</div>` });
    }
    const g = (m.topGenres || [])[0];
    if (flags.music && g) {
      slides.push({ theme: "genre", html: `<div class="au-wr-kicker">Your sound was</div><div class="au-wr-big">${h(g.genre)}</div>
        <div class="au-wr-list au-wr-genres">${(m.topGenres || []).slice(0, 5).map((x, i) => `<div style="--d:${i * 120}ms"><span>${i + 1}</span>${h(x.genre)}</div>`).join("")}</div>` });
    }
    const top = (m.topArtists || [])[0];
    if (flags.music && top && top.artist) {
      slides.push({ theme: "artist", html: `<div class="au-wr-kicker">Your top artist</div>
        <div class="au-wr-art au-round">${art(U.artSized(top.artist.artUrl, 600), "au-square au-round", top.artist.name, "")}</div>
        <div class="au-wr-big">${h(top.artist.name)}</div><div class="au-wr-sub">${plural(top.plays || 0, "play")} · ${h(hoursMinutes(top.minutes))}</div>` });
      if ((m.topArtists || []).length > 1) {
        slides.push({ theme: "artists", html: `<div class="au-wr-kicker">Your top artists</div><div class="au-wr-list">${m.topArtists.slice(0, 5).map((a, i) => `<div style="--d:${i * 150}ms"><span>${i + 1}</span><span class="au-wr-li-art">${U.safeSrc(a.artist && a.artist.artUrl) ? `<img src="${h(U.artSized(a.artist.artUrl, 120))}" alt="">` : ""}</span>${h(a.artist && a.artist.name)}</div>`).join("")}</div>` });
      }
    }
    if (flags.music && (m.topTracks || []).length) {
      const t = m.topTracks[0];
      slides.push({ theme: "song", html: `<div class="au-wr-kicker">Your song of the year</div>
        <div class="au-wr-art">${art(U.artSized(t.track.artUrl, 600), "au-square", t.track.title, t.track.artist)}</div>
        <div class="au-wr-big">${h(t.track.title)}</div><div class="au-wr-sub">${h(t.track.artist)} · you played it ${plural(t.plays || 0, "time")}</div>
        <div class="au-wr-list au-wr-small">${m.topTracks.slice(1, 5).map((x, i) => `<div style="--d:${400 + i * 120}ms"><span>${i + 2}</span>${h(x.track.title)} <em>${h(x.track.artist)}</em></div>`).join("")}</div>` });
    }
    if (flags.music && (m.topAlbums || []).length) {
      const a = m.topAlbums[0];
      slides.push({ theme: "album", html: `<div class="au-wr-kicker">The album on repeat</div><div class="au-wr-art au-wr-spin">${art(U.artSized(a.album.artUrl, 600), "au-square", a.album.title, a.album.artist)}</div><div class="au-wr-big">${h(a.album.title)}</div><div class="au-wr-sub">${h(a.album.artist)} · ${plural(a.plays || 0, "play")}</div>` });
    }
    const hours = (m.byHour || []).map((v, i) => [Number(v) || 0, i]);
    if (flags.music && hours.length && hours.some(([v]) => v > 0)) {
      const [, peakHour] = hours.reduce((a, b) => (b[0] > a[0] ? b : a));
      const persona = peakHour < 5 ? ["Night owl", "Most of your listening happened after midnight."] : peakHour < 9 ? ["Early riser", "You started the day with music."] : peakHour < 12 ? ["Morning person", "Late mornings were your time."] : peakHour < 17 ? ["Afternoon groover", "The afternoon belonged to your playlists."] : peakHour < 21 ? ["Evening unwinder", "Music was how you wound down."] : ["Late-night listener", "Your best listening came after dark."];
      const wk = (m.byWeekday || []).map((v) => Number(v) || 0);
      const topDay = wk.length ? WEEKDAYS[wk.indexOf(Math.max(...wk))] : "";
      slides.push({ theme: "clock", html: `<div class="au-wr-kicker">Your listening clock</div><div class="au-wr-big">${h(persona[0])}</div>
        <div class="au-wr-clock">${barChart(m.byHour, Array.from({ length: 24 }, (_, i) => (i % 6 === 0 ? hourLabel(i) : "")), "min", "au-bars-hours au-bars-wr")}</div>
        <div class="au-wr-sub">${h(persona[1])} Busiest at ${h(hourLabel(peakHour))}${topDay ? `, and ${h(topDay)}s most of all` : ""}.</div>` });
    }
    if (flags.music && (m.longestStreakDays || m.newArtists)) {
      slides.push({ theme: "streak", html: `<div class="au-wr-kicker">Keeping it going</div>
        ${m.longestStreakDays ? `<div class="au-wr-huge" data-count="${m.longestStreakDays}">0</div><div class="au-wr-line">days in a row with music</div>` : ""}
        ${m.newArtists ? `<div class="au-wr-sub">and ${plural(m.newArtists, "artist")} you'd never played before</div>` : ""}` });
    }
    if (flags.podcasts && p.minutes) {
      const show = (p.topShows || [])[0];
      slides.push({ theme: "pods", html: `<div class="au-wr-kicker">In your ears</div><div class="au-wr-huge" data-count="${Math.round(p.minutes)}">0</div><div class="au-wr-line">minutes of podcasts</div>
        ${show && show.podcast ? `<div class="au-wr-row">${art(show.podcast.artUrl, "au-square au-wr-thumb", show.podcast.title, "", "", ICON.mic)}<div><div class="au-wr-sub">most of all</div><div class="au-wr-mid">${h(show.podcast.title)}</div><div class="au-wr-sub">${plural(show.episodes || 0, "episode")} · ${h(hoursMinutes(show.minutes))}</div></div></div>` : ""}
        ${p.timeSavedMinutes ? `<div class="au-wr-sub">Speed and trimmed silences gave you back <b>${h(hoursMinutes(p.timeSavedMinutes))}</b>.</div>` : ""}` });
    }
    slides.push({ theme: "outro", html: `<div class="au-wr-kicker">${h(year)} in short</div>
      <div class="au-wr-summary">
        ${m.minutes ? `<div><b>${h(hoursMinutes(m.minutes))}</b><span>music</span></div>` : ""}
        ${top && top.artist ? `<div><b>${h(top.artist.name)}</b><span>top artist</span></div>` : ""}
        ${(m.topTracks || [])[0] ? `<div><b>${h(m.topTracks[0].track.title)}</b><span>top song</span></div>` : ""}
        ${g ? `<div><b>${h(g.genre)}</b><span>top genre</span></div>` : ""}
        ${p.minutes ? `<div><b>${h(hoursMinutes(p.minutes))}</b><span>podcasts</span></div>` : ""}
        ${p.episodesFinished ? `<div><b>${h(p.episodesFinished)}</b><span>episodes finished</span></div>` : ""}
      </div>
      <div class="mv-actions au-wr-actions"><button type="button" class="mv-btn mv-btn-glass" data-wr="replay">${ICON.refresh}<span>watch again</span></button><button type="button" class="mv-btn mv-btn-primary" data-wr="close">done</button></div>` });
    return slides;
  }

  // ---- modal actions --------------------------------------------------------------------------

  U.onModalAction((act, target) => {
    switch (act) {
      case "submit:au-lib-form": submitLibrary(target); return true;
      case "au-lib-browse": {
        const editorState = libEditor;
        const draft = { name: el("au-lib-name").value, path: el("au-lib-path").value };
        U.openFolderBrowser(draft.path.trim(),
          (path) => openLibraryEditor(editorState.container, editorState.existing, { ...draft, path }),
          () => openLibraryEditor(editorState.container, editorState.existing, draft));
        return true;
      }
      case "au-alarm-new": openAlarmEditor(null); return true;
      case "au-alarm-edit": openAlarmEditor(alarms.list.find((a) => a.id === target.dataset.id)); return true;
      case "au-alarm-cancel": alarms.editing = null; drawAlarms(); return true;
      case "submit:au-alarm-form": saveAlarm(); return true;
      case "au-alarm-toggle": {
        const al = alarms.list.find((a) => a.id === target.dataset.id);
        if (!al) return true;
        const enabled = target.checked;
        request(`/api/audio/alarms/${enc(al.id)}`, { method: "PUT", body: { ...al, enabled } }).then((res) => {
          if (!res.ok) { target.checked = !enabled; showToast(res.error, false); return; }
          Object.assign(al, res.data || { enabled });
          drawAlarms();
        });
        return true;
      }
      case "au-alarm-test":
        request(`/api/audio/alarms/${enc(target.dataset.id)}/test`, { method: "POST" }).then((res) => showToast(res.ok ? "Playing it on the speaker now" : res.error, res.ok));
        return true;
      case "au-alarm-delete":
        if (!confirm("Delete this alarm?")) return true;
        request(`/api/audio/alarms/${enc(target.dataset.id)}`, { method: "DELETE" }).then((res) => { if (!res.ok) showToast(res.error, false); loadAlarms(); });
        return true;
      case "au-al-day": {
        const al = alarms.editing;
        if (!al) return true;
        const d = target.dataset.day;
        al.days = (al.days || []).includes(d) ? al.days.filter((x) => x !== d) : [...(al.days || []), d];
        target.classList.toggle("on", al.days.includes(d));
        return true;
      }
      case "au-al-days": {
        const al = alarms.editing;
        if (!al) return true;
        al.days = target.dataset.v === "wk" ? ["mon", "tue", "wed", "thu", "fri"] : target.dataset.v === "we" ? ["sat", "sun"] : DAYS.map(([k]) => k);
        for (const b of document.querySelectorAll("#au-alarm-form .au-day")) b.classList.toggle("on", al.days.includes(b.dataset.day));
        return true;
      }
      case "au-al-pick": {
        const al = alarms.editing;
        if (!al) return true;
        al.source = { type: al.source.type, id: target.dataset.id, name: target.dataset.name };
        const chosen = document.querySelector("#au-alarm-form .au-al-chosen");
        if (chosen) chosen.innerHTML = `Chosen: <b>${h(target.dataset.name)}</b>`;
        for (const b of document.querySelectorAll("#au-al-results .au-al-result")) b.classList.toggle("active", b === target);
        return true;
      }
      case "au-brief-play": playBriefingHere(target); return true;
      case "au-brief-speaker": briefingToSpeaker(target); return true;
      default: return false;
    }
  });

  return { librariesPanel, musicSettingsPanel, podcastsSettingsPanel, listenBrainzCard, openAlarms, openBriefing, renderStats, openWrapped, fmtClock };
})();
