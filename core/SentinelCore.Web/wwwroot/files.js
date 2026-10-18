// FILES tab: the household's shared file library (see FamilyFiles.cs on the server). A folder
// browser with upload (button, drag-and-drop), download, rename/move and delete-to-trash for
// everyone; backup drives, the nightly duplicate check and the trash bin's emptying for admins.
//
// Everything is fetched fresh from the server - unlike the Organizer there is no local copy, a
// file library being far too big for one. While a background job (a backup, a restore, a
// duplicate check) runs, the status is polled every couple of seconds to show its progress.
window.SentinelFiles = (function () {
  const API = "/api/family-files";
  const JOB_POLL_MS = 2000;

  let status = null;
  let currentPath = "";
  let listing = null;
  let searchQuery = "";
  let visible = false;
  let jobTimer = null;
  let uploadQueue = [];
  let uploading = false;
  let modalRefresh = null;

  const el = (id) => document.getElementById(id);

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  function showToast(message, success) {
    const toast = el("toast");
    toast.textContent = message;
    toast.className = `toast ${success ? "success" : "error"}`;
    clearTimeout(showToast.timer);
    showToast.timer = setTimeout(() => toast.classList.add("hidden"), success ? 3500 : 6000);
  }

  function formatSize(bytes) {
    if (bytes === null || bytes === undefined) return "";
    if (bytes >= 1 << 30) return `${(bytes / (1 << 30)).toFixed(1)} GB`;
    if (bytes >= 1 << 20) return `${(bytes / (1 << 20)).toFixed(1)} MB`;
    if (bytes >= 1 << 10) return `${Math.round(bytes / (1 << 10))} KB`;
    return `${bytes} bytes`;
  }

  function formatDate(value) {
    if (!value) return "";
    const date = new Date(value);
    return isNaN(date) ? "" : date.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
  }

  function encodePath(path) {
    return path.split("/").map(encodeURIComponent).join("/");
  }

  function nameOf(path) {
    const slash = path.lastIndexOf("/");
    return slash < 0 ? path : path.slice(slash + 1);
  }

  function parentOf(path) {
    const slash = path.lastIndexOf("/");
    return slash < 0 ? "" : path.slice(0, slash);
  }

  async function api(path, options) {
    const response = await fetch(API + path, options);
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      const message = payload.error || (payload.errors && payload.errors.join(" ")) || `${response.status} ${response.statusText}`;
      throw new Error(message);
    }
    return payload;
  }

  const json = (method, body) => ({ method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });

  // ---- status and the tab -------------------------------------------------------------------

  async function refreshStatus() {
    try {
      status = await api("/status");
    } catch {
      status = null;
    }
    el("tab-files").classList.toggle("hidden", !(status && status.enabled));
    if (status && status.enabled) {
      const admin = status.isAdmin;
      el("files-drives-btn").textContent = admin ? "backup drives" : "backups";
      el("files-summary").textContent = `${status.library.files} file(s), ${formatSize(status.library.bytes)}` +
        (status.libraryOnNetwork ? " · on another computer" : "") +
        (status.libraryProblem ? ` · ${status.libraryProblem}` : "");
      renderJob();
      if (status.job) startJobPolling(); else stopJobPolling();
    }
    return status;
  }

  function renderJob() {
    const banner = el("files-job");
    const job = status && status.job;
    if (!job) {
      banner.classList.add("hidden");
      return;
    }
    const progress = job.total > 0 ? ` (${job.done} of ${job.total})` : "";
    banner.innerHTML = `<span class="files-spinner"></span> ${escapeHtml(job.message)}${escapeHtml(progress)}`;
    banner.classList.remove("hidden");
  }

  function startJobPolling() {
    if (jobTimer) return;
    jobTimer = setInterval(async () => {
      const before = status && status.job;
      await refreshStatus();
      const after = status && status.job;
      if (before && !after) {
        // A job just finished: whatever it changed, show it.
        if (visible) loadListing();
        if (modalRefresh) modalRefresh();
      }
    }, JOB_POLL_MS);
  }

  function stopJobPolling() {
    if (jobTimer) clearInterval(jobTimer);
    jobTimer = null;
  }

  // ---- the browser ---------------------------------------------------------------------------

  async function loadListing() {
    const list = el("files-list");
    try {
      if (searchQuery) {
        const result = await api(`/search?q=${encodeURIComponent(searchQuery)}&limit=300`);
        listing = { path: currentPath, folders: [], files: result.files, search: true };
      } else {
        listing = await api(`/list?path=${encodeURIComponent(currentPath)}`);
      }
    } catch (error) {
      list.innerHTML = `<div class="empty-state">${escapeHtml(error.message)}</div>`;
      return;
    }
    renderCrumbs();
    renderListing();
  }

  function renderCrumbs() {
    const crumbs = el("files-crumbs");
    const parts = currentPath ? currentPath.split("/") : [];
    let built = "";
    const links = [`<a href="#" data-crumb="">Family Files</a>`];
    for (const part of parts) {
      built = built ? `${built}/${part}` : part;
      links.push(`<a href="#" data-crumb="${escapeHtml(built)}">${escapeHtml(part)}</a>`);
    }
    crumbs.innerHTML = links.join(`<span class="files-crumb-sep">/</span>`) +
      (searchQuery ? `<span class="files-crumb-sep">·</span><span class="event-count">search: ${escapeHtml(searchQuery)}</span>` : "");
    for (const link of crumbs.querySelectorAll("[data-crumb]")) {
      link.addEventListener("click", (event) => {
        event.preventDefault();
        searchQuery = "";
        el("files-search").value = "";
        openFolder(link.dataset.crumb);
      });
    }
  }

  function fileIcon(file) {
    if (file.isImage) {
      return `<img class="files-thumb" src="${API}/thumbnail/${encodePath(file.path)}" alt="" loading="lazy"
        onerror="this.replaceWith(Object.assign(document.createElement('span'),{className:'files-ext',textContent:'IMG'}))" />`;
    }
    const ext = (file.name.includes(".") ? file.name.split(".").pop() : "file").slice(0, 4).toUpperCase();
    return `<span class="files-ext">${escapeHtml(ext)}</span>`;
  }

  function renderListing() {
    const list = el("files-list");
    if (!listing) return;
    const rows = [];

    if (currentPath && !listing.search) {
      rows.push(`<div class="camera-card files-row files-folder" data-open-folder="${escapeHtml(parentOf(currentPath))}">
        <div class="files-row-main"><span class="files-ext files-ext-folder">&#8617;</span>
          <div class="camera-card-info"><div class="camera-card-name">..</div><div class="camera-card-meta">up to ${escapeHtml(parentOf(currentPath) || "Family Files")}</div></div></div>
      </div>`);
    }

    for (const folder of listing.folders) {
      rows.push(`<div class="camera-card files-row files-folder" data-open-folder="${escapeHtml(folder.path)}">
        <div class="files-row-main"><span class="files-ext files-ext-folder">DIR</span>
          <div class="camera-card-info"><div class="camera-card-name">${escapeHtml(folder.name)}</div><div class="camera-card-meta">folder</div></div></div>
        <div class="camera-card-actions">
          <button class="btn btn-small" data-rename="${escapeHtml(folder.path)}" data-tip="Rename this folder.">rename</button>
          <button class="btn btn-small" data-move="${escapeHtml(folder.path)}" data-tip="Move this folder somewhere else in the library.">move</button>
          <button class="btn btn-small files-danger" data-trash="${escapeHtml(folder.path)}" data-tip="Move this folder and everything in it to the trash. It can be put back from there.">trash</button>
        </div>
      </div>`);
    }

    for (const file of listing.files) {
      const url = `${API}/files/${encodePath(file.path)}`;
      rows.push(`<div class="camera-card files-row">
        <div class="files-row-main">
          <a class="files-preview" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer">${fileIcon(file)}</a>
          <div class="camera-card-info">
            <div class="camera-card-name"><a href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer">${escapeHtml(file.name)}</a></div>
            <div class="camera-card-meta">${escapeHtml(formatSize(file.size))} · ${escapeHtml(formatDate(file.modifiedUtc))}${listing.search ? ` · ${escapeHtml(parentOf(file.path) || "Family Files")}` : ""}</div>
          </div>
        </div>
        <div class="camera-card-actions">
          <a class="btn btn-small" href="${escapeHtml(url)}?download=true" download="${escapeHtml(file.name)}" data-tip="Save a copy of this file to this computer.">get</a>
          <button class="btn btn-small" data-rename="${escapeHtml(file.path)}" data-tip="Rename this file.">rename</button>
          <button class="btn btn-small" data-move="${escapeHtml(file.path)}" data-tip="Move this file to another folder.">move</button>
          <button class="btn btn-small files-danger" data-trash="${escapeHtml(file.path)}" data-tip="Move this file to the trash. It can be put back from there.">trash</button>
        </div>
      </div>`);
    }

    if (rows.length === 0) {
      rows.push(`<div class="empty-state">${listing.search
        ? "Nothing matches that search."
        : "Nothing here yet. Drop files onto this page, or use \"add files\" above."}</div>`);
    }
    list.innerHTML = rows.join("");

    for (const row of list.querySelectorAll("[data-open-folder]")) {
      row.addEventListener("click", (event) => {
        if (event.target.closest("button")) return;
        openFolder(row.dataset.openFolder);
      });
    }
    for (const button of list.querySelectorAll("[data-rename]")) button.addEventListener("click", () => rename(button.dataset.rename));
    for (const button of list.querySelectorAll("[data-move]")) button.addEventListener("click", () => move(button.dataset.move));
    for (const button of list.querySelectorAll("[data-trash]")) button.addEventListener("click", () => trash(button.dataset.trash));
    if (window.GateWatchTooltips) window.GateWatchTooltips.schedule();
  }

  function openFolder(path) {
    currentPath = path;
    loadListing();
  }

  async function rename(path) {
    const current = nameOf(path);
    const name = await window.SentinelAsk.text("New name:", current);
    if (!name || name.trim() === current) return;
    try {
      await api("/move", json("POST", { from: path, to: `${parentOf(path)}${parentOf(path) ? "/" : ""}${name.trim()}` }));
      loadListing();
    } catch (error) {
      showToast(error.message, false);
    }
  }

  async function move(path) {
    const target = await window.SentinelAsk.text("Move to which folder? (e.g. Photos/2026, or blank for the top level)", parentOf(path));
    if (target === null) return;
    const folder = target.trim().replace(/^\/+|\/+$/g, "");
    try {
      await api("/move", json("POST", { from: path, to: `${folder}${folder ? "/" : ""}${nameOf(path)}` }));
      loadListing();
      showToast(`Moved to ${folder || "Family Files"}.`, true);
    } catch (error) {
      showToast(error.message, false);
    }
  }

  async function trash(path) {
    if (!confirm(`Move "${nameOf(path)}" to the trash? It can be put back from there for ${status.nightly.trashKeepDays} days.`)) return;
    try {
      const result = await api(`/files/${encodePath(path)}`, { method: "DELETE" });
      showToast(`Moved ${result.trashed} file(s) to the trash.`, true);
      loadListing();
      refreshStatus();
    } catch (error) {
      showToast(error.message, false);
    }
  }

  async function newFolder() {
    const name = await window.SentinelAsk.text("Folder name:");
    if (!name || !name.trim()) return;
    try {
      const made = await api("/folders", json("POST", { path: `${currentPath}${currentPath ? "/" : ""}${name.trim()}` }));
      openFolder(made.path);
    } catch (error) {
      showToast(error.message, false);
    }
  }

  // ---- uploads -------------------------------------------------------------------------------

  function queueUploads(files, folder) {
    for (const file of files) uploadQueue.push({ file, folder });
    if (!uploading) runUploads();
  }

  async function runUploads() {
    uploading = true;
    const banner = el("files-uploads");
    const total = uploadQueue.length;
    let done = 0;
    let failed = 0;
    while (uploadQueue.length) {
      const { file, folder } = uploadQueue.shift();
      done++;
      const label = `uploading ${done} of ${Math.max(total, done)}: ${file.name}`;
      banner.innerHTML = `<span class="files-spinner"></span> <span class="files-upload-label">${escapeHtml(label)}</span>
        <div class="system-stat-bar files-upload-bar"><div class="system-stat-bar-fill" style="width:0%"></div></div>`;
      banner.classList.remove("hidden");
      if (status && file.size > status.maxUploadBytes) {
        failed++;
        showToast(`${file.name} is bigger than the ${formatSize(status.maxUploadBytes)} limit (Settings -> Family Files).`, false);
        continue;
      }
      try {
        const result = await putFile(file, folder, (fraction) => {
          const fill = banner.querySelector(".system-stat-bar-fill");
          if (fill) fill.style.width = `${Math.round(fraction * 100)}%`;
        });
        if (result.alreadyThere) showToast(`${file.name} was already in the library - nothing to do.`, true);
        else if (nameOf(result.path) !== file.name) showToast(`A different ${file.name} was already there, so this one is ${nameOf(result.path)}.`, true);
      } catch (error) {
        failed++;
        showToast(`${file.name}: ${error.message}`, false);
      }
      if (visible && folder === currentPath) loadListing();
    }
    banner.classList.add("hidden");
    uploading = false;
    if (failed === 0 && total > 0) showToast(`Added ${total} file(s).`, true);
    refreshStatus();
  }

  // XMLHttpRequest rather than fetch: it is the one way to show upload progress on a big file.
  function putFile(file, folder, onProgress) {
    return new Promise((resolve, reject) => {
      const path = `${folder}${folder ? "/" : ""}${file.name}`;
      const modified = file.lastModified ? `?modified=${encodeURIComponent(new Date(file.lastModified).toISOString())}` : "";
      const xhr = new XMLHttpRequest();
      xhr.open("PUT", `${API}/files/${encodePath(path)}${modified}`);
      xhr.upload.addEventListener("progress", (event) => { if (event.lengthComputable) onProgress(event.loaded / event.total); });
      xhr.addEventListener("load", () => {
        let payload = {};
        try { payload = JSON.parse(xhr.responseText); } catch { /* not JSON */ }
        if (xhr.status >= 200 && xhr.status < 300) resolve(payload);
        else reject(new Error(payload.error || (payload.errors && payload.errors.join(" ")) || `upload failed (${xhr.status})`));
      });
      xhr.addEventListener("error", () => reject(new Error("the connection dropped during the upload")));
      xhr.addEventListener("abort", () => reject(new Error("the upload was cancelled")));
      xhr.send(file);
    });
  }

  // ---- modal ---------------------------------------------------------------------------------

  function openModal(html) {
    el("files-modal-panel").innerHTML = html;
    el("files-modal").classList.remove("hidden");
    if (window.GateWatchTooltips) window.GateWatchTooltips.schedule();
  }

  function closeModal() {
    el("files-modal").classList.add("hidden");
    el("files-modal-panel").innerHTML = "";
    modalRefresh = null;
  }

  function resultLine(result, message) {
    const cls = result === "ok" ? "camera-test-status-ok" : result === "failed" ? "camera-test-status-error" : result ? "camera-test-status-warn" : "";
    return `<span class="camera-test-status ${cls}">${escapeHtml(result || "never run")}</span> ${escapeHtml(message || "")}`;
  }

  // ---- drives ----------------------------------------------------------------------------------

  async function openDrives() {
    await refreshStatus();
    if (!status || !status.enabled) return;
    const admin = status.isAdmin;
    const nightly = status.nightly;
    const dup = status.duplicates;
    const drives = status.drives;

    const driveRows = drives.length === 0
      ? `<div class="empty-state">No backup drives yet.${admin ? " Add one below - a USB disk or network share plugged into the server, or another computer over SFTP." : ""}</div>`
      : drives.map((d) => `<div class="camera-card files-row">
          <div class="camera-card-info">
            <div class="camera-card-name">${escapeHtml(d.name)} ${d.enabled ? "" : `<span class="event-count">(off)</span>`}</div>
            <div class="camera-card-meta">${escapeHtml(d.kind === "sftp" ? "another computer (SFTP)" : "folder")} · ${escapeHtml(d.location)}</div>
            <div class="camera-card-meta">${resultLine(d.lastResult, d.lastMessage)}</div>
            <div class="camera-card-meta">${d.lastSuccessUtc ? `last good backup ${escapeHtml(formatDate(d.lastSuccessUtc))}` : "no backup yet"}${d.lastFreeBytes !== null && d.lastFreeBytes !== undefined ? ` · ${escapeHtml(formatSize(d.lastFreeBytes))} free` : ""}${d.lastHostKeyFingerprint ? ` · host key ${escapeHtml(d.lastHostKeyFingerprint)}` : ""}</div>
          </div>
          ${admin ? `<div class="camera-card-actions">
            <button class="btn btn-small" data-sync="${escapeHtml(d.id)}" data-tip="Copy the library to this drive now.">back up now</button>
            <button class="btn btn-small" data-restore="${escapeHtml(d.id)}" data-tip="Copy anything on this drive that the library doesn't have back into the library. Nothing already in the library is touched.">bring files back</button>
            <button class="btn btn-small" data-edit="${escapeHtml(d.id)}">edit</button>
          </div>` : ""}
        </div>`).join("");

    const location = admin ? `
        <div class="org-subsection">LIBRARY LOCATION</div>
        <div class="camera-card files-row"><div class="camera-card-info">
          <div class="camera-card-name">${escapeHtml(status.libraryFolder || "")}</div>
          <div class="camera-card-meta">${status.libraryOnNetwork ? "A shared folder on another computer, reached over the network." : "A folder on this server."}${status.libraryProblem ? ` <span class="camera-test-status camera-test-status-error">problem</span> ${escapeHtml(status.libraryProblem)}` : ""}</div>
        </div><div class="camera-card-actions"><button class="btn btn-small" id="files-location-edit" data-tip="Keep the library on this server or in a folder shared from another computer (Windows, Linux with Samba, a NAS).">change</button></div></div>` : "";

    openModal(`
      <div class="zone-editor-header"><span>BACKUP DRIVES</span><button class="btn btn-small" id="files-modal-close">close</button></div>
      <div class="files-modal-body">
        ${location}
        <div class="org-subsection">DRIVES</div>
        <p class="settings-hint">The library is copied to every drive below each night${nightly.nextRunUtc ? ` (next at ${escapeHtml(formatDate(nightly.nextRunUtc))})` : ""} and whenever you press "back up now". A drive keeps a plain copy of the folders anyone can open, and only ever loses a file that has left the library.</p>
        <div class="settings-root" id="files-drive-rows">${driveRows}</div>
        ${admin ? `<div class="files-actions-row">
          <button class="btn" id="files-add-drive">+ add a drive</button>
          <button class="btn" id="files-sync-all" ${drives.some((d) => d.enabled) ? "" : "disabled"} data-tip="Copy the library to every drive that is switched on.">back up to all drives now</button>
        </div>` : ""}

        <div class="org-subsection">NIGHTLY PASS</div>
        <div class="camera-card files-row"><div class="camera-card-info">
          <div class="camera-card-meta">Runs at ${String(nightly.hour).padStart(2, "0")}:${String(nightly.minute).padStart(2, "0")}: empties trash older than ${nightly.trashKeepDays} days, ${nightly.removeDuplicates ? "moves duplicate files to the trash" : "lists duplicate files (removal is off in Settings)"}, then backs up to every drive.</div>
          <div class="camera-card-meta">${nightly.lastRunUtc ? `last run ${escapeHtml(formatDate(nightly.lastRunUtc))}: ` : ""}${resultLine(nightly.lastResult, nightly.lastMessage)}</div>
        </div>${admin ? `<div class="camera-card-actions"><button class="btn btn-small" id="files-nightly-now">run it now</button></div>` : ""}</div>

        <div class="org-subsection">DUPLICATE FILES</div>
        <div class="camera-card files-row"><div class="camera-card-info">
          <div class="camera-card-meta">${dup
            ? `Last check ${escapeHtml(formatDate(dup.ranUtc))} over ${dup.filesChecked} file(s): ${dup.groups === 0 ? "no duplicates." : `${dup.copies} extra cop${dup.copies === 1 ? "y" : "ies"} of ${dup.groups} file(s), ${escapeHtml(formatSize(dup.bytes))}${dup.removed ? " - moved to the trash" : " - still in the library"}.`}`
            : "Not checked yet. The check runs every night; it looks at what is in each file, not its name."}</div>
          <div id="files-dup-detail"></div>
        </div>${admin ? `<div class="camera-card-actions">
          ${dup && dup.groups > 0 ? `<button class="btn btn-small" id="files-dup-show">show them</button>` : ""}
          <button class="btn btn-small" id="files-dup-check" data-tip="Look for duplicates now and list them without moving anything.">check now</button>
          <button class="btn btn-small files-danger" id="files-dup-remove" data-tip="Look for duplicates now and move every copy but the oldest to the trash.">check and remove</button>
        </div>` : ""}</div>
      </div>`);

    el("files-modal-close").addEventListener("click", closeModal);
    modalRefresh = openDrives;
    if (!admin) return;

    el("files-location-edit").addEventListener("click", editLocation);
    el("files-add-drive").addEventListener("click", () => editDrive(null));
    for (const button of el("files-drive-rows").querySelectorAll("[data-edit]"))
      button.addEventListener("click", () => editDrive(drives.find((d) => d.id === button.dataset.edit)));
    for (const button of el("files-drive-rows").querySelectorAll("[data-sync]"))
      button.addEventListener("click", () => startJob(`/drives/${button.dataset.sync}/sync`));
    for (const button of el("files-drive-rows").querySelectorAll("[data-restore]"))
      button.addEventListener("click", () => {
        const drive = drives.find((d) => d.id === button.dataset.restore);
        if (confirm(`Bring back anything on "${drive.name}" that the library doesn't have? Nothing already in the library is changed; a file that exists in both at a different size comes back as "name (from drive)".`))
          startJob(`/drives/${drive.id}/restore`);
      });
    el("files-sync-all").addEventListener("click", () => startJob("/sync"));
    el("files-nightly-now").addEventListener("click", () => startJob("/nightly/run"));
    el("files-dup-check").addEventListener("click", () => startJob("/duplicates/check?remove=false"));
    el("files-dup-remove").addEventListener("click", () => {
      if (confirm("Check for duplicate files now and move every copy but the oldest to the trash?")) startJob("/duplicates/check?remove=true");
    });
    const show = el("files-dup-show");
    if (show) show.addEventListener("click", showDuplicates);
  }

  async function startJob(path) {
    try {
      const result = await api(path, { method: "POST" });
      showToast(result.detail || "Started.", true);
      await refreshStatus();
      if (modalRefresh) modalRefresh();
    } catch (error) {
      showToast(error.message, false);
    }
  }

  async function showDuplicates() {
    const detail = el("files-dup-detail");
    try {
      const { report } = await api("/duplicates");
      if (!report || !report.sample || report.sample.length === 0) {
        detail.innerHTML = `<div class="camera-card-meta">Nothing to show.</div>`;
        return;
      }
      detail.innerHTML = `<div class="files-dup-list">${report.sample.map((g) => `
        <div class="files-dup-group">
          <div class="camera-card-meta"><span class="camera-test-status camera-test-status-ok">kept</span> ${escapeHtml(g.kept)} <span class="event-count">(${escapeHtml(formatSize(g.size))})</span></div>
          ${g.copies.map((c) => `<div class="camera-card-meta files-dup-copy"><span class="camera-test-status ${report.removed ? "camera-test-status-warn" : ""}">${report.removed ? "trashed" : "copy"}</span> ${escapeHtml(c)}</div>`).join("")}
        </div>`).join("")}${report.groups > report.sample.length ? `<div class="event-count">and ${report.groups - report.sample.length} more group(s)</div>` : ""}</div>`;
    } catch (error) {
      detail.innerHTML = `<div class="camera-card-meta">${escapeHtml(error.message)}</div>`;
    }
  }

  function editDrive(drive) {
    const d = drive || { name: "", kind: "folder", enabled: true, folderPath: "", sftpHost: "", sftpPort: 22, sftpUsername: "", sftpFolder: "", sftpHostKeyFingerprint: "", hasPassword: false };
    openModal(`
      <div class="zone-editor-header"><span>${drive ? "EDIT DRIVE" : "ADD A BACKUP DRIVE"}</span><button class="btn btn-small" id="files-modal-close">close</button></div>
      <form class="files-form" id="files-drive-form">
        <div class="field-grid">
          <div class="field-row"><label class="field-label">Name</label><input class="field-input" name="name" value="${escapeHtml(d.name)}" placeholder="USB drive, NAS, Grandma's Mac..." required /></div>
          <div class="field-row"><label class="field-label">Where it is</label>
            <select class="field-input" name="kind">
              <option value="folder" ${d.kind === "folder" ? "selected" : ""}>A folder / drive on the server</option>
              <option value="sftp" ${d.kind === "sftp" ? "selected" : ""}>Another computer (SFTP)</option>
            </select></div>
        </div>
        <div class="field-grid" data-kind="folder">
          <div class="field-row field-row-wide"><label class="field-label">Folder</label><input class="field-input" name="folderPath" value="${escapeHtml(d.folderPath || "")}" placeholder="/mnt/usb/Family Files  or  \\\\nas\\backups\\family" />
            <div class="field-note">A full path on the server: a plugged-in USB disk, a mounted network share, or a Windows \\\\server\\share. Created if it doesn't exist. Other files already in it are never touched.</div></div>
        </div>
        <div class="field-grid" data-kind="sftp">
          <div class="field-row"><label class="field-label">Address</label><input class="field-input" name="sftpHost" value="${escapeHtml(d.sftpHost || "")}" placeholder="nas.local or 192.168.1.20" /></div>
          <div class="field-row"><label class="field-label">Port</label><input class="field-input" name="sftpPort" type="number" min="1" max="65535" value="${escapeHtml(d.sftpPort || 22)}" /></div>
          <div class="field-row"><label class="field-label">Username</label><input class="field-input" name="sftpUsername" value="${escapeHtml(d.sftpUsername || "")}" /></div>
          <div class="field-row"><label class="field-label">Password</label><input class="field-input" name="sftpPassword" type="password" placeholder="${d.hasPassword ? "(unchanged)" : ""}" autocomplete="new-password" /></div>
          <div class="field-row field-row-wide"><label class="field-label">Folder on that computer</label><input class="field-input" name="sftpFolder" value="${escapeHtml(d.sftpFolder || "")}" placeholder="Backups/Family Files" />
            <div class="field-note">Relative paths start from that account's home folder. Created if it doesn't exist.</div></div>
          <div class="field-row field-row-wide"><label class="field-label">Host key fingerprint (optional)</label><input class="field-input" name="sftpHostKeyFingerprint" value="${escapeHtml(d.sftpHostKeyFingerprint || "")}" placeholder="SHA256:..." />
            <div class="field-note">When set, nothing is sent if a different machine answers at that address. "test" shows the fingerprint it saw, ready to paste here.</div></div>
        </div>
        <label class="org-check"><input type="checkbox" name="enabled" ${d.enabled ? "checked" : ""} /> Back up to this drive (untick to keep it in the list but skip it)</label>
        <ul class="field-errors" id="files-drive-errors"></ul>
        <div class="zone-editor-footer">
          <button class="btn" type="submit">save</button>
          <button class="btn" type="button" id="files-drive-test">test</button>
          ${drive ? `<button class="btn files-danger" type="button" id="files-drive-remove">remove</button>` : ""}
          <span class="event-count" id="files-drive-note"></span>
        </div>
      </form>`);

    const form = el("files-drive-form");
    const showKind = () => {
      const kind = form.elements.kind.value;
      for (const grid of form.querySelectorAll("[data-kind]")) grid.classList.toggle("hidden", grid.dataset.kind !== kind);
    };
    form.elements.kind.addEventListener("change", showKind);
    showKind();
    el("files-modal-close").addEventListener("click", openDrives);

    const read = () => ({
      id: drive ? drive.id : "",
      name: form.elements.name.value,
      kind: form.elements.kind.value,
      enabled: form.elements.enabled.checked,
      folderPath: form.elements.folderPath.value,
      sftpHost: form.elements.sftpHost.value,
      sftpPort: Number(form.elements.sftpPort.value) || 22,
      sftpUsername: form.elements.sftpUsername.value,
      sftpPassword: form.elements.sftpPassword.value,
      sftpFolder: form.elements.sftpFolder.value,
      sftpHostKeyFingerprint: form.elements.sftpHostKeyFingerprint.value
    });
    const errors = (messages) => {
      el("files-drive-errors").innerHTML = messages.map((m) => `<li>${escapeHtml(m)}</li>`).join("");
    };

    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      errors([]);
      try {
        await api(drive ? `/drives/${drive.id}` : "/drives", json(drive ? "PUT" : "POST", read()));
        showToast(drive ? "Drive saved." : "Drive added. Press \"back up now\" to try it.", true);
        openDrives();
      } catch (error) {
        errors(error.message.split(/(?<=\.) /));
      }
    });
    el("files-drive-test").addEventListener("click", async () => {
      errors([]);
      const note = el("files-drive-note");
      note.textContent = "testing…";
      try {
        const result = await api("/drives/test", json("POST", read()));
        note.textContent = result.detail;
        if (result.hostKeyFingerprint && !form.elements.sftpHostKeyFingerprint.value && form.elements.kind.value === "sftp" &&
            confirm(`The server's host key is ${result.hostKeyFingerprint}. Save it so nothing is sent if a different machine ever answers at that address?`))
          form.elements.sftpHostKeyFingerprint.value = result.hostKeyFingerprint;
      } catch (error) {
        note.textContent = "";
        errors([error.message]);
      }
    });
    const remove = el("files-drive-remove");
    if (remove) remove.addEventListener("click", async () => {
      if (!confirm(`Remove "${drive.name}" from the list? The copy on it is left alone.`)) return;
      try {
        await api(`/drives/${drive.id}`, { method: "DELETE" });
        openDrives();
      } catch (error) {
        errors([error.message]);
      }
    });
  }

  // ---- where the library lives ---------------------------------------------------------------

  function editLocation() {
    const current = status.libraryFolder || "";
    const onNetwork = !!status.libraryOnNetwork;
    openModal(`
      <div class="zone-editor-header"><span>LIBRARY LOCATION</span><button class="btn btn-small" id="files-modal-close">close</button></div>
      <form class="files-form" id="files-location-form">
        <div class="field-grid">
          <div class="field-row field-row-wide"><label class="field-label">Where the files live</label>
            <select class="field-input" name="kind">
              <option value="local" ${onNetwork ? "" : "selected"}>A folder on this server</option>
              <option value="network" ${onNetwork ? "selected" : ""}>A shared folder on another computer</option>
            </select></div>
        </div>
        <div class="field-grid" data-kind="local">
          <div class="field-row field-row-wide"><label class="field-label">Folder</label><input class="field-input" name="localPath" value="${onNetwork ? "" : escapeHtml(current)}" placeholder="/mnt/bigdisk/Family Files  (blank = the data folder)" />
            <div class="field-note">A full path on the server. Blank keeps the library under the server's data folder.</div></div>
        </div>
        <div class="field-grid" data-kind="network">
          <div class="field-row field-row-wide"><label class="field-label">Shared folder</label><input class="field-input" name="networkPath" value="${onNetwork ? escapeHtml(current) : ""}" placeholder="smb://192.168.1.20/Family/Files  or  \\\\DESKTOP-PC\\Family" />
            <div class="field-note">The computer's name or address, then the shared folder's name, then any folder inside it (made if it isn't there).
              On Windows, share the folder (Properties → Sharing) and give the account "Change" permission. On Linux, share it with Samba (<code>smbpasswd -a</code> for the user) with <code>writable = yes</code>.</div></div>
          <div class="field-row"><label class="field-label">Username</label><input class="field-input" name="username" autocomplete="off" placeholder="the account on that computer" /></div>
          <div class="field-row"><label class="field-label">Password</label><input class="field-input" name="password" type="password" autocomplete="new-password" placeholder="blank = the one already saved" /></div>
          <div class="field-row field-row-wide"><div class="field-note">The sign-in is saved for that computer and shared with Movies libraries on it. Leave both blank to use a saved sign-in, or to try as a guest.</div></div>
        </div>
        <p class="settings-hint">Changing this doesn't move anything: files in the old place stay there. To fill the new place, copy them across yourself, or use "bring files back" on a backup drive.</p>
        <ul class="field-errors" id="files-location-errors"></ul>
        <div class="zone-editor-footer">
          <button class="btn" type="submit">use this location</button>
          <button class="btn" type="button" id="files-location-test">test</button>
          <span class="event-count" id="files-location-note"></span>
        </div>
      </form>`);

    const form = el("files-location-form");
    const showKind = () => {
      for (const grid of form.querySelectorAll("[data-kind]")) grid.classList.toggle("hidden", grid.dataset.kind !== form.elements.kind.value);
    };
    form.elements.kind.addEventListener("change", showKind);
    showKind();
    el("files-modal-close").addEventListener("click", openDrives);

    const read = () => {
      const network = form.elements.kind.value === "network";
      let location = (network ? form.elements.networkPath.value : form.elements.localPath.value).trim();
      // A bare "computer/Share" typed into the network box.
      if (network && location && !/^(smb:)?[\\/]{2}/i.test(location)) location = `smb://${location}`;
      return {
        location,
        username: network ? form.elements.username.value : "",
        password: network ? form.elements.password.value : ""
      };
    };
    const errors = (messages) => {
      el("files-location-errors").innerHTML = messages.map((m) => `<li>${escapeHtml(m)}</li>`).join("");
    };
    const note = el("files-location-note");

    el("files-location-test").addEventListener("click", async () => {
      errors([]);
      note.textContent = "testing…";
      try {
        note.textContent = (await api("/library/test", json("POST", read()))).detail;
      } catch (error) {
        note.textContent = "";
        errors([error.message]);
      }
    });
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      errors([]);
      const body = read();
      if (body.location === current) { openDrives(); return; }
      if (!confirm(`Move the library to ${body.location || "the server's data folder"}? Files already in ${current} stay there and stop showing in the library.`)) return;
      note.textContent = "checking…";
      try {
        const result = await api("/library", json("PUT", body));
        showToast(result.detail, true);
        currentPath = "";
        loadListing();
        openDrives();
      } catch (error) {
        note.textContent = "";
        errors([error.message]);
      }
    });
  }

  // ---- trash -----------------------------------------------------------------------------------

  async function openTrash() {
    let batches;
    try {
      batches = (await api("/trash")).batches;
    } catch (error) {
      showToast(error.message, false);
      return;
    }
    const admin = status && status.isAdmin;
    openModal(`
      <div class="zone-editor-header"><span>TRASH</span><button class="btn btn-small" id="files-modal-close">close</button></div>
      <div class="files-modal-body">
        <p class="settings-hint">Deleted files and retired duplicates wait here for ${status ? status.nightly.trashKeepDays : "some"} days before they are deleted for good. Put anything back with one press.</p>
        ${batches.length === 0 ? `<div class="empty-state">The trash is empty.</div>` : ""}
        <div class="settings-root">${batches.map((b) => `
          <div class="camera-card files-row files-trash-batch">
            <div class="camera-card-info">
              <div class="camera-card-name">${escapeHtml(formatDate(b.deletedUtc))} · ${escapeHtml(b.reason === "duplicate" ? "duplicates" : "deleted")}${b.by ? ` by ${escapeHtml(b.by)}` : ""}</div>
              ${b.files.map((f) => `<div class="camera-card-meta files-trash-file">
                <span>${escapeHtml(f.path)} <span class="event-count">(${escapeHtml(formatSize(f.size))})</span>${f.keptCopy ? ` <span class="event-count">- same as ${escapeHtml(f.keptCopy)}</span>` : ""}</span>
                <button class="btn btn-small" data-restore-file="${escapeHtml(f.path)}" data-batch="${escapeHtml(b.id)}">put back</button>
              </div>`).join("")}
            </div>
            <div class="camera-card-actions"><button class="btn btn-small" data-restore-batch="${escapeHtml(b.id)}">put all ${b.files.length} back</button></div>
          </div>`).join("")}</div>
        ${admin && batches.length ? `<div class="files-actions-row"><button class="btn files-danger" id="files-empty-trash">empty the trash now</button></div>` : ""}
      </div>`);
    el("files-modal-close").addEventListener("click", closeModal);
    modalRefresh = openTrash;

    const restore = async (body) => {
      try {
        const result = await api("/trash/restore", json("POST", body));
        showToast(result.detail, true);
        openTrash();
        loadListing();
        refreshStatus();
      } catch (error) {
        showToast(error.message, false);
      }
    };
    for (const button of el("files-modal-panel").querySelectorAll("[data-restore-batch]"))
      button.addEventListener("click", () => restore({ batchId: button.dataset.restoreBatch }));
    for (const button of el("files-modal-panel").querySelectorAll("[data-restore-file]"))
      button.addEventListener("click", () => restore({ batchId: button.dataset.batch, path: button.dataset.restoreFile }));
    const empty = el("files-empty-trash");
    if (empty) empty.addEventListener("click", async () => {
      if (!confirm("Delete everything in the trash for good? This cannot be undone.")) return;
      try {
        const result = await api("/trash", { method: "DELETE" });
        showToast(result.detail, true);
        openTrash();
        refreshStatus();
      } catch (error) {
        showToast(error.message, false);
      }
    });
  }

  // ---- wiring ----------------------------------------------------------------------------------

  async function init() {
    if (!el("view-files")) return;
    const input = el("files-input");
    el("files-upload-btn").addEventListener("click", () => input.click());
    input.addEventListener("change", () => {
      queueUploads([...input.files], currentPath);
      input.value = "";
    });
    el("files-folder-btn").addEventListener("click", newFolder);
    el("files-drives-btn").addEventListener("click", openDrives);
    el("files-trash-btn").addEventListener("click", openTrash);

    const search = el("files-search");
    let searchTimer = null;
    search.addEventListener("input", () => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => {
        searchQuery = search.value.trim();
        loadListing();
      }, 250);
    });

    const view = el("view-files");
    view.addEventListener("dragover", (event) => {
      if (![...event.dataTransfer.types].includes("Files")) return;
      event.preventDefault();
      view.classList.add("files-drop-target");
    });
    view.addEventListener("dragleave", (event) => { if (!view.contains(event.relatedTarget)) view.classList.remove("files-drop-target"); });
    view.addEventListener("drop", (event) => {
      event.preventDefault();
      view.classList.remove("files-drop-target");
      const files = [...(event.dataTransfer ? event.dataTransfer.files : [])];
      if (files.length) queueUploads(files, currentPath);
    });

    el("files-modal").addEventListener("mousedown", (event) => { if (event.target === el("files-modal")) closeModal(); });
    document.addEventListener("keydown", (event) => { if (event.key === "Escape" && !el("files-modal").classList.contains("hidden")) closeModal(); });

    await refreshStatus();
  }

  async function show() {
    visible = true;
    await refreshStatus();
    if (status && status.enabled) loadListing();
  }

  function hide() {
    visible = false;
    if (!(status && status.job)) stopJobPolling();
  }

  window.addEventListener("DOMContentLoaded", init);

  return { show, hide, refreshStatus };
})();
