// The dashboard's half of the Organizer sync protocol - see core/docs/ORGANIZER_SYNC.md.
//
// Keeps a working copy in IndexedDB so edits made while the server is unreachable (a restart, a
// dropped Wi-Fi) are kept and sent when it's back. A browser's storage can be cleared at any time,
// so unlike the phone and desktop apps this copy is never counted on for disaster recovery - it is
// here so that nothing typed is lost, not as a backup.
//
// Every edit goes through update()/create(): it lands in the local copy at once, with a fresh
// clock per field, and in a pending queue that sync() pushes. Pending edits for one item coalesce,
// so typing into a note sends one change, not one per keystroke.
window.SentinelOrganizerSync = (function () {
  const DB_NAME = "sentinel-organizer";
  const STORES = ["items", "pending", "meta", "blobs"];

  let db = null;
  const items = new Map();
  // Edits not yet sent, and edits sent but not yet acknowledged. Kept apart so an edit made while
  // a sync is in flight isn't mistaken for one the server has already seen.
  const pending = new Map();
  let inflight = new Map();
  let meta = { serverId: null, cursor: 0, spaces: [] };
  let status = null;
  const listeners = new Set();
  const localBlobUrls = new Map();

  let syncing = false;
  let syncAgain = false;
  let syncTimer = null;
  let waiting = false;
  let started = false;
  let online = true;
  let lastSyncedAt = null;
  let lastError = null;
  let lastClock = { ms: 0, counter: 0 };
  let resetHandler = async () => false;

  const deviceId = (() => {
    try {
      let id = localStorage.getItem("organizer.deviceId");
      if (!id) {
        id = "web-" + Math.random().toString(36).slice(2, 10);
        localStorage.setItem("organizer.deviceId", id);
      }
      return id;
    } catch {
      return "web-" + Math.random().toString(36).slice(2, 10);
    }
  })();

  // ---- storage ------------------------------------------------------------------------------

  function openDb() {
    return new Promise((resolve) => {
      if (!window.indexedDB) return resolve(null);
      let request;
      try {
        request = indexedDB.open(DB_NAME, 1);
      } catch {
        return resolve(null);
      }
      request.onupgradeneeded = () => {
        const database = request.result;
        for (const name of STORES) {
          if (!database.objectStoreNames.contains(name)) database.createObjectStore(name);
        }
      };
      request.onsuccess = () => resolve(request.result);
      // Private windows and blocked storage land here - carry on in memory only.
      request.onerror = () => resolve(null);
      request.onblocked = () => resolve(null);
    });
  }

  function tx(store, mode, work) {
    if (!db) return Promise.resolve(undefined);
    return new Promise((resolve) => {
      try {
        const transaction = db.transaction(store, mode);
        const result = work(transaction.objectStore(store));
        transaction.oncomplete = () => resolve(result && "result" in result ? result.result : undefined);
        transaction.onerror = () => resolve(undefined);
        transaction.onabort = () => resolve(undefined);
      } catch {
        resolve(undefined);
      }
    });
  }

  const put = (store, key, value) => tx(store, "readwrite", (s) => s.put(value, key));
  const del = (store, key) => tx(store, "readwrite", (s) => s.delete(key));
  const get = (store, key) => tx(store, "readonly", (s) => s.get(key));
  const clear = (store) => tx(store, "readwrite", (s) => s.clear());

  function getAll(store) {
    if (!db) return Promise.resolve([]);
    return new Promise((resolve) => {
      try {
        const out = [];
        const request = db.transaction(store, "readonly").objectStore(store).openCursor();
        request.onsuccess = () => {
          const cursor = request.result;
          if (!cursor) return resolve(out);
          out.push([cursor.key, cursor.value]);
          cursor.continue();
        };
        request.onerror = () => resolve(out);
      } catch {
        resolve([]);
      }
    });
  }

  function saveMeta() {
    return put("meta", "state", meta);
  }

  // What the queue for one item looks like on disk: whatever is in flight plus whatever is newer.
  // Closing the tab mid-sync then loses nothing - worst case it is sent twice, which is harmless.
  function persistPending(id) {
    const merged = mergeChanges(inflight.get(id), pending.get(id));
    return merged ? put("pending", id, merged) : del("pending", id);
  }

  // ---- clocks -------------------------------------------------------------------------------

  // Hybrid logical clock: never goes backwards, and never behind anything already seen, so an
  // edit made here is always newer than the version of the field it replaced.
  function nextClock() {
    const now = Date.now();
    if (now > lastClock.ms) lastClock = { ms: now, counter: 0 };
    else lastClock = { ms: lastClock.ms, counter: (lastClock.counter + 1) % 10000 };
    return `${String(lastClock.ms).padStart(13, "0")}-${String(lastClock.counter).padStart(4, "0")}-${deviceId}`;
  }

  function observeClock(clock) {
    if (typeof clock !== "string") return;
    const ms = Number(clock.slice(0, 13));
    const counter = Number(clock.slice(14, 18));
    if (ms > lastClock.ms || (ms === lastClock.ms && counter > lastClock.counter)) lastClock = { ms, counter };
  }

  // ---- local edits --------------------------------------------------------------------------

  function newId() {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    return "id-" + Date.now().toString(36) + "-" + Math.random().toString(36).slice(2, 12);
  }

  function mergeChanges(older, newer) {
    if (!older) return newer ? clone(newer) : null;
    if (!newer) return clone(older);
    const merged = clone(older);
    for (const [name, value] of Object.entries(newer.fields)) {
      merged.fields[name] = value;
      merged.clocks[name] = newer.clocks[name];
    }
    // The first base wins: it's the version the whole run of edits started from.
    if (newer.baseClocks) merged.baseClocks = { ...newer.baseClocks, ...(older.baseClocks || {}) };
    return merged;
  }

  function clone(value) {
    return value == null ? value : JSON.parse(JSON.stringify(value));
  }

  function applyLocal(item, name, value, clock) {
    if (name === "spaceId") item.spaceId = value;
    else if (name === "parentId") item.parentId = value;
    else if (name === "deleted") item.deleted = !!value;
    else if (value === undefined || value === null) delete item.fields[name];
    else item.fields[name] = clone(value);
    item.clocks[name] = clock;
  }

  function create(kind, fields, where) {
    const id = newId();
    const parent = where && where.parentId ? items.get(where.parentId) : null;
    const item = {
      id,
      kind,
      spaceId: parent ? parent.spaceId : where && where.spaceId,
      parentId: parent ? parent.id : null,
      deleted: false,
      fields: {},
      clocks: {},
      createdAt: new Date().toISOString(),
      createdBy: status && status.username,
      updatedAt: new Date().toISOString()
    };
    items.set(id, item);
    const structural = parent ? { parentId: parent.id } : { spaceId: item.spaceId };
    edit(item, { ...structural, ...fields });
    return item;
  }

  function update(id, changes) {
    const item = items.get(id);
    if (!item) return null;
    edit(item, changes);
    // A board moved to another space takes its columns and cards along; mirror what the server
    // will do so they don't flicker into the wrong list until the next sync.
    if ("spaceId" in changes) cascadeSpace(item);
    return item;
  }

  function remove(id) {
    const item = items.get(id);
    if (!item) return;
    edit(item, { deleted: true });
  }

  function edit(item, changes) {
    const change = pending.get(item.id) || { id: item.id, kind: item.kind, fields: {}, clocks: {} };
    for (const [name, value] of Object.entries(changes)) {
      if (name === "body" && item.kind === "note" && item.clocks.body && !(change.baseClocks && change.baseClocks.body)
        && !(inflight.get(item.id) && inflight.get(item.id).baseClocks)) {
        change.baseClocks = { body: item.clocks.body };
      }
      const clock = nextClock();
      applyLocal(item, name, value, clock);
      change.fields[name] = value === undefined ? null : clone(value);
      change.clocks[name] = clock;
    }
    item.updatedAt = new Date().toISOString();
    pending.set(item.id, change);
    put("items", item.id, item);
    persistPending(item.id);
    emit();
    scheduleSync(800);
  }

  function cascadeSpace(root) {
    const queue = [root.id];
    while (queue.length) {
      const parentId = queue.shift();
      for (const child of items.values()) {
        if (child.parentId !== parentId) continue;
        queue.push(child.id);
        if (child.spaceId !== root.spaceId) {
          child.spaceId = root.spaceId;
          put("items", child.id, child);
        }
      }
    }
  }

  // ---- attachments --------------------------------------------------------------------------

  async function attach(parentId, file) {
    if (status && status.maxAttachmentBytes && file.size > status.maxAttachmentBytes) {
      throw new Error(`${file.name} is larger than the ${Math.round(status.maxAttachmentBytes / 1048576)} MB limit.`);
    }
    const buffer = await file.arrayBuffer();
    const hash = await sha256Hex(buffer);
    const blob = new Blob([buffer], { type: file.type || "application/octet-stream" });
    await put("blobs", hash, blob);
    localBlobUrls.set(hash, URL.createObjectURL(blob));

    const siblings = children(parentId).filter((child) => child.kind === "attachment");
    const item = create("attachment", {
      hash,
      name: file.name || "attachment",
      contentType: file.type || "application/octet-stream",
      size: file.size,
      position: siblings.length
    }, { parentId });
    uploadBlobs();
    return item;
  }

  let uploading = false;
  async function uploadBlobs() {
    if (uploading || !online) return;
    uploading = true;
    try {
      for (const [hash, blob] of await getAll("blobs")) {
        const response = await fetch(`/api/organizer/blobs/${hash}`, { method: "PUT", body: blob });
        if (response.ok) {
          await del("blobs", hash);
        } else if (response.status === 413 || response.status === 400) {
          // Won't ever succeed - drop it rather than retrying forever.
          await del("blobs", hash);
          lastError = "An attachment couldn't be uploaded: " + ((await response.json().catch(() => ({}))).errors || ["rejected"]).join(" ");
          emit();
        } else {
          break;
        }
      }
    } catch {
      // Offline - the next sync retries.
    } finally {
      uploading = false;
    }
  }

  // Before the upload lands the server can't serve the file, so show the local copy.
  function blobUrl(hash, thumbnail) {
    if (localBlobUrls.has(hash)) return localBlobUrls.get(hash);
    return `/api/organizer/blobs/${hash}${thumbnail ? "/thumbnail" : ""}`;
  }

  // crypto.subtle only exists on secure origins, and this dashboard is very often opened over
  // plain http on the LAN - so a small fallback implementation.
  async function sha256Hex(buffer) {
    if (window.crypto && crypto.subtle && window.isSecureContext) {
      const digest = await crypto.subtle.digest("SHA-256", buffer);
      return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0")).join("");
    }
    return sha256Fallback(new Uint8Array(buffer));
  }

  function sha256Fallback(bytes) {
    const K = new Uint32Array([
      0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
      0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
      0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
      0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
      0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
      0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
      0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
      0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
    ]);
    const H = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
    const length = bytes.length;
    const padded = new Uint8Array(((length + 9 + 63) >> 6) << 6);
    padded.set(bytes);
    padded[length] = 0x80;
    const view = new DataView(padded.buffer);
    view.setUint32(padded.length - 8, Math.floor(length / 0x20000000), false);
    view.setUint32(padded.length - 4, (length << 3) >>> 0, false);
    const w = new Uint32Array(64);
    const rotr = (x, n) => (x >>> n) | (x << (32 - n));
    for (let offset = 0; offset < padded.length; offset += 64) {
      for (let i = 0; i < 16; i++) w[i] = view.getUint32(offset + i * 4, false);
      for (let i = 16; i < 64; i++) {
        const s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3);
        const s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10);
        w[i] = (w[i - 16] + s0 + w[i - 7] + s1) >>> 0;
      }
      let [a, b, c, d, e, f, g, h] = H;
      for (let i = 0; i < 64; i++) {
        const t1 = (h + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i]) >>> 0;
        const t2 = ((rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) >>> 0;
        h = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
      }
      H[0] += a; H[1] += b; H[2] += c; H[3] += d; H[4] += e; H[5] += f; H[6] += g; H[7] += h;
    }
    return Array.from(H, (x) => x.toString(16).padStart(8, "0")).join("");
  }

  // ---- sync ---------------------------------------------------------------------------------

  function scheduleSync(delay) {
    clearTimeout(syncTimer);
    syncTimer = setTimeout(sync, delay);
  }

  async function sync() {
    if (!started) return;
    if (syncing) {
      syncAgain = true;
      return;
    }
    syncing = true;
    try {
      do {
        syncAgain = false;
        await syncOnce();
      } while (syncAgain);
      online = true;
      lastError = null;
      lastSyncedAt = new Date();
      uploadBlobs();
    } catch (err) {
      online = false;
      lastError = err && err.message;
      // Back off; an edit or the browser coming back online will try sooner.
      scheduleSync(15000);
    } finally {
      syncing = false;
      emit();
    }
  }

  async function syncOnce(extra) {
    // Everything pending becomes in flight for this request.
    for (const [id, change] of pending) inflight.set(id, mergeChanges(inflight.get(id), change));
    pending.clear();

    let hasMore = true;
    let first = true;
    while (hasMore) {
      const body = {
        serverId: meta.serverId,
        cursor: meta.cursor,
        knownSpaces: meta.spaces.map((space) => space.id),
        changes: first ? [...inflight.values()] : [],
        ...(first ? extra : {})
      };
      let response;
      try {
        response = await fetch("/api/organizer/sync", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body)
        });
      } catch (err) {
        restoreInflight();
        throw new Error("offline");
      }
      if (!response.ok) {
        restoreInflight();
        throw new Error(response.status === 404 ? "The Organizer is turned off." : `sync failed (${response.status})`);
      }
      const reply = await response.json();

      if (reply.reset) {
        restoreInflight();
        await handleReset(reply);
        return;
      }

      await applyReply(reply, first);
      first = false;
      hasMore = reply.hasMore;
    }
  }

  function restoreInflight() {
    for (const [id, change] of inflight) pending.set(id, mergeChanges(change, pending.get(id)));
    inflight = new Map();
  }

  async function applyReply(reply, settlesInflight) {
    for (const id of reply.removed || []) {
      items.delete(id);
      del("items", id);
    }

    for (const wire of reply.items || []) {
      for (const clock of Object.values(wire.clocks || {})) observeClock(clock);
      const local = {
        id: wire.id,
        kind: wire.kind,
        spaceId: wire.spaceId,
        parentId: wire.parentId,
        deleted: wire.deleted,
        fields: wire.fields || {},
        clocks: wire.clocks || {},
        createdAt: wire.createdAt,
        createdBy: wire.createdBy,
        updatedAt: wire.updatedAt,
        updatedBy: wire.updatedBy
      };
      // Edits made while this request was out are newer than anything in it - lay them back on.
      const newer = pending.get(wire.id);
      if (newer) {
        for (const [name, value] of Object.entries(newer.fields)) applyLocal(local, name, value, newer.clocks[name]);
      }
      items.set(local.id, local);
      put("items", local.id, local);
    }

    if (settlesInflight) {
      for (const rejection of reply.rejected || []) {
        lastError = rejection.error;
        // A brand-new item the server refused doesn't exist anywhere else - drop it here too.
        const item = items.get(rejection.id);
        if (item && !(reply.items || []).some((wire) => wire.id === rejection.id)) {
          items.delete(rejection.id);
          del("items", rejection.id);
        }
      }
      const settled = [...inflight.keys()];
      inflight = new Map();
      for (const id of settled) persistPending(id);
    }

    // Spaces this person can no longer see take their items with them.
    meta.spaces = reply.spaces || [];
    const visible = new Set(meta.spaces.map((space) => space.id));
    for (const [id, item] of items) {
      if (!visible.has(item.spaceId) && !pending.has(id)) {
        items.delete(id);
        del("items", id);
      }
    }

    meta.cursor = reply.cursor;
    meta.serverId = reply.serverId;
    await saveMeta();

    if ((reply.missingBlobs || []).length) uploadBlobs();
    emit();
  }

  // The server this browser last synced with isn't the one answering now: it was rebuilt, or
  // this is a different install. Nothing has been applied yet.
  async function handleReset(reply) {
    const liveItems = [...items.values()].filter((item) => !item.deleted);
    await refreshStatus();
    const upload = liveItems.length > 0 && await resetHandler({ localCount: liveItems.length, serverItemCount: status ? status.itemCount : null });

    if (upload) {
      // Every field with the clock it already had, so the merge treats it exactly like the
      // original edits - newer edits already on the new server still win.
      const changes = [...items.values()].map((item) => {
        const fields = { ...clone(item.fields), spaceId: item.spaceId, deleted: item.deleted };
        if (item.parentId) fields.parentId = item.parentId;
        const clocks = {};
        const fallback = nextClock();
        for (const name of Object.keys(fields)) clocks[name] = item.clocks[name] || fallback;
        return { id: item.id, kind: item.kind, fields, clocks };
      });
      pending.clear();
      await clear("pending");
      meta.serverId = reply.serverId;
      meta.cursor = 0;
      const restoreSpaces = meta.spaces.filter((space) => space.kind === "Group");
      for (const change of changes) inflight.set(change.id, change);
      await syncOnce({ restoreSpaces });
      return;
    }

    // Start over from the server's copy. Pending edits go too: they describe items on a server
    // that no longer exists.
    items.clear();
    pending.clear();
    inflight = new Map();
    await Promise.all([clear("items"), clear("pending")]);
    meta = { serverId: reply.serverId, cursor: 0, spaces: [] };
    await saveMeta();
    await syncOnce();
  }

  async function waitLoop() {
    if (waiting) return;
    waiting = true;
    while (started) {
      try {
        const response = await fetch(`/api/organizer/wait?cursor=${meta.cursor}&timeout=25`);
        if (!response.ok) {
          await sleep(10000);
          continue;
        }
        const { cursor } = await response.json();
        if (cursor > meta.cursor || cursor < meta.cursor) await sync();
      } catch {
        online = false;
        emit();
        await sleep(10000);
      }
    }
    waiting = false;
  }

  function sleep(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // ---- lifecycle ----------------------------------------------------------------------------

  async function refreshStatus() {
    try {
      const response = await fetch("/api/organizer/status");
      if (!response.ok) return null;
      status = await response.json();
      return status;
    } catch {
      return status;
    }
  }

  async function start() {
    if (started) return;
    started = true;
    db = await openDb();
    const stored = await get("meta", "state");
    if (stored) meta = { serverId: null, cursor: 0, spaces: [], ...stored };
    for (const [id, item] of await getAll("items")) {
      items.set(id, item);
      for (const clock of Object.values(item.clocks || {})) observeClock(clock);
    }
    for (const [id, change] of await getAll("pending")) pending.set(id, change);
    for (const [hash, blob] of await getAll("blobs")) localBlobUrls.set(hash, URL.createObjectURL(blob));

    // Signing in as someone else in this browser must not show them the last person's copy.
    await refreshStatus();
    const owner = status ? status.username || "" : null;
    if (owner !== null && stored && stored.owner !== undefined && stored.owner !== owner) {
      items.clear();
      pending.clear();
      await Promise.all([clear("items"), clear("pending"), clear("blobs")]);
      meta = { serverId: null, cursor: 0, spaces: [] };
    }
    if (owner !== null) meta.owner = owner;

    emit();
    window.addEventListener("online", () => sync());
    await sync();
    waitLoop();
  }

  function emit() {
    for (const listener of listeners) {
      try { listener(); } catch (err) { console.error(err); }
    }
  }

  // ---- queries ------------------------------------------------------------------------------

  function all(kind) {
    const out = [];
    for (const item of items.values()) {
      if (!item.deleted && (!kind || item.kind === kind)) out.push(item);
    }
    return out;
  }

  function children(parentId) {
    return all().filter((item) => item.parentId === parentId);
  }

  return {
    start,
    sync: () => sync(),
    refreshStatus,
    status: () => status,
    spaces: () => meta.spaces,
    all,
    get: (id) => items.get(id),
    children,
    create,
    update,
    remove,
    attach,
    blobUrl,
    subscribe: (listener) => { listeners.add(listener); return () => listeners.delete(listener); },
    onServerReset: (handler) => { resetHandler = handler; },
    state: () => ({
      online,
      syncing,
      lastSyncedAt,
      lastError,
      waitingChanges: pending.size + inflight.size
    }),
    clearError: () => { lastError = null; emit(); },
    sha256Hex
  };
})();
