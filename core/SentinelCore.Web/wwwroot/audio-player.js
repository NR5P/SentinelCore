// The one audio player the Music and Podcasts tabs share (see core/docs/MUSIC_AND_PODCASTS.md).
// It lives outside both tabs: a mini player bar fixed to the bottom of every dashboard tab, a
// full-screen Now Playing view, and everything around them - the queue, crossfade between two
// <audio> decks, Sound Check and a 10-band equalizer through Web Audio, the sleep timer, lyrics
// and transcripts, lock-screen controls, reporting plays and progress, "Continue from <device>",
// playing to a speaker elsewhere in the house, and making this browser a speaker itself.
//
// It also carries the small helpers music.js, podcasts.js and audio-extras.js lean on (requests,
// artwork, rows, the modal, the pop-up menu and the folder picker), so those files stay about
// their own screens.
window.SentinelAudio = (function () {
  const LS = "sentinelcore.audio.";
  const NOW_PLAYING_EVERY_MS = 30000;
  const PODCAST_PROGRESS_EVERY_MS = 15000;
  const SPEAKER_HEARTBEAT_MS = 60000;
  const SPEAKER_STATE_EVERY_MS = 15000;
  const REMOTE_POLL_MS = 2000;
  const CONTINUE_CHECK_MS = 5 * 60000;
  const PRELOAD_BEFORE_END = 20;
  const SLEEP_FADE_SECONDS = 10;
  const EQ_BANDS = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
  const EQ_PRESETS = {
    flat: { name: "Flat", gains: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0] },
    bass: { name: "Bass boost", gains: [6, 5, 4, 2, 0, 0, 0, 0, 0, 0] },
    treble: { name: "Treble boost", gains: [0, 0, 0, 0, 0, 1, 2, 4, 5, 6] },
    vocal: { name: "Vocal", gains: [-2, -2, -1, 1, 3, 4, 3, 1, 0, -1] },
    spoken: { name: "Spoken word", gains: [-4, -3, -1, 1, 3, 4, 4, 2, 0, -2] },
    rock: { name: "Rock", gains: [5, 4, 2, -1, -2, -1, 2, 3, 4, 4] },
    pop: { name: "Pop", gains: [-1, 1, 3, 4, 3, 0, -1, -1, 1, 2] },
    jazz: { name: "Jazz", gains: [3, 2, 1, 2, -1, -1, 0, 1, 2, 3] },
    classical: { name: "Classical", gains: [4, 3, 2, 1, -1, -1, 0, 2, 3, 4] },
    electronic: { name: "Electronic", gains: [5, 4, 1, 0, -2, 2, 1, 1, 4, 5] },
    loudness: { name: "Loudness", gains: [6, 4, 0, 0, -2, 0, -1, -2, 4, 2] },
    night: { name: "Late night", gains: [-3, -2, 0, 1, 2, 2, 1, 0, -2, -4] }
  };

  const ICON = (function () {
    const s = (body, extra = "") => `<svg class="mv-ico au-ico" viewBox="0 0 24 24" aria-hidden="true"${extra}>${body}</svg>`;
    const st = 'fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"';
    return {
      play: s('<path d="M8 5.2v13.6L19 12z" fill="currentColor"/>'),
      pause: s('<rect x="6.5" y="5" width="4" height="14" rx="1" fill="currentColor"/><rect x="13.5" y="5" width="4" height="14" rx="1" fill="currentColor"/>'),
      next: s('<path d="M5 5.5v13l9-6.5z" fill="currentColor"/><rect x="15.5" y="5.5" width="3" height="13" rx="1" fill="currentColor"/>'),
      prev: s('<path d="M19 5.5v13l-9-6.5z" fill="currentColor"/><rect x="5.5" y="5.5" width="3" height="13" rx="1" fill="currentColor"/>'),
      shuffle: s(`<path ${st} d="M3.5 7h3.2c2.2 0 3.3 1.1 4.6 3.4l1.4 2.4c1.3 2.3 2.4 3.4 4.6 3.4h3.2M17.5 4l3 3-3 3M3.5 17h3.2c1.2 0 2-.3 2.8-1M14.4 8c.8-.7 1.6-1 2.9-1M17.5 14l3 3-3 3"/>`),
      repeat: s(`<path ${st} d="M4 11V9a3 3 0 0 1 3-3h12.5M16.5 3l3 3-3 3M20 13v2a3 3 0 0 1-3 3H4.5M7.5 21l-3-3 3-3"/>`),
      repeat1: s(`<path ${st} d="M4 11V9a3 3 0 0 1 3-3h12.5M16.5 3l3 3-3 3M20 13v2a3 3 0 0 1-3 3H4.5M7.5 21l-3-3 3-3"/><path d="M11.2 10.6l1.4-.9v5" ${st} stroke-width="1.7"/>`),
      back15: s(`<path ${st} d="M4.5 12a7.5 7.5 0 1 0 2.4-5.5M4.5 4v4.2h4.2"/><text x="12.3" y="15.6" text-anchor="middle" font-size="7.2" font-weight="700" fill="currentColor" font-family="sans-serif">15</text>`),
      fwd30: s(`<path ${st} d="M19.5 12a7.5 7.5 0 1 1-2.4-5.5M19.5 4v4.2h-4.2"/><text x="11.7" y="15.6" text-anchor="middle" font-size="7.2" font-weight="700" fill="currentColor" font-family="sans-serif">30</text>`),
      heart: s(`<path ${st} d="M12 20s-7.5-4.6-7.5-10.2A4.3 4.3 0 0 1 12 7.3a4.3 4.3 0 0 1 7.5 2.5C19.5 15.4 12 20 12 20z"/>`),
      heartFill: s('<path fill="currentColor" d="M12 20s-7.5-4.6-7.5-10.2A4.3 4.3 0 0 1 12 7.3a4.3 4.3 0 0 1 7.5 2.5C19.5 15.4 12 20 12 20z"/>'),
      queue: s(`<path ${st} d="M4 6h11M4 11h11M4 16h7M17 14v6l4-3z"/>`),
      // "In Up Next": the list with a tick, so it never looks like the "Mark played" tick beside it.
      queued: s(`<path ${st} d="M4 6h11M4 11h11M4 16h7M14.5 16.5l2.5 2.5 4.5-5"/>`),
      lyrics: s(`<path ${st} d="M4 5.5h16v10H9l-4 3.5v-3.5H4z"/><path ${st} d="M8 9h8M8 12h5"/>`),
      transcript: s(`<path ${st} d="M6 3.5h9l3 3V20.5H6z"/><path ${st} d="M9 10h6M9 13.5h6M9 17h4"/>`),
      chapters: s(`<path ${st} d="M5 6h2M5 12h2M5 18h2M10 6h9M10 12h9M10 18h9"/>`),
      cast: s(`<path ${st} d="M4 18.5a2 2 0 0 1 2 2M4 14.5a6 6 0 0 1 6 6M4 10.5a10 10 0 0 1 10 10"/><path ${st} d="M4 7V5.5a1.5 1.5 0 0 1 1.5-1.5h13A1.5 1.5 0 0 1 20 5.5v13a1.5 1.5 0 0 1-1.5 1.5H17"/>`),
      speaker: s(`<rect ${st} x="6" y="3" width="12" height="18" rx="2.5"/><circle ${st} cx="12" cy="14" r="3.2"/><circle cx="12" cy="7.2" r="1.2" fill="currentColor"/>`),
      volume: s(`<path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor"/><path ${st} d="M15.5 9a4 4 0 0 1 0 6M18 6.5a7.5 7.5 0 0 1 0 11"/>`),
      mute: s(`<path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor"/><path ${st} d="M16 9.5l5 5M21 9.5l-5 5"/>`),
      moon: s(`<path ${st} d="M19.5 14.5A8 8 0 0 1 9.5 4.5a8 8 0 1 0 10 10z"/>`),
      eq: s(`<path ${st} d="M6 4v16M12 4v16M18 4v16"/><rect x="4" y="13" width="4" height="3" rx="1" fill="currentColor"/><rect x="10" y="7" width="4" height="3" rx="1" fill="currentColor"/><rect x="16" y="11" width="4" height="3" rx="1" fill="currentColor"/>`),
      more: s('<circle cx="5.5" cy="12" r="1.8" fill="currentColor"/><circle cx="12" cy="12" r="1.8" fill="currentColor"/><circle cx="18.5" cy="12" r="1.8" fill="currentColor"/>'),
      close: s(`<path ${st} stroke-width="2.4" d="M6 6l12 12M18 6L6 18"/>`),
      plus: s(`<path ${st} stroke-width="2.4" d="M12 5v14M5 12h14"/>`),
      check: s(`<path ${st} stroke-width="2.6" d="M5 12.5l4.5 4.5L19 7.5"/>`),
      search: s(`<circle ${st} cx="11" cy="11" r="6.5"/><path ${st} stroke-width="2.2" d="M16 16l4.5 4.5"/>`),
      folder: s(`<path ${st} stroke-width="1.8" d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v7.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>`),
      computer: s(`<rect ${st} stroke-width="1.8" x="3.5" y="4.5" width="17" height="11" rx="1.5"/><path ${st} stroke-width="1.8" d="M9 19.5h6M12 15.5v4"/>`),
      note: s('<path d="M9 17.5V6.2l10-2.2v11.3" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"/><ellipse cx="6.6" cy="17.6" rx="2.6" ry="2.1" fill="currentColor"/><ellipse cx="16.6" cy="15.4" rx="2.6" ry="2.1" fill="currentColor"/>'),
      mic: s(`<rect ${st} x="9" y="3" width="6" height="11" rx="3"/><path ${st} d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M8.5 21h7"/>`),
      download: s(`<path ${st} d="M12 4v11M7 10.5l5 5 5-5M5 19.5h14"/>`),
      bookmark: s(`<path ${st} d="M6.5 4h11v16.5L12 16.5l-5.5 4z"/>`),
      share: s(`<path ${st} d="M12 3.5v12M7.5 8L12 3.5 16.5 8M5 12.5v6a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-6"/>`),
      clock: s(`<circle ${st} cx="12" cy="12" r="8.5"/><path ${st} d="M12 7.5V12l3 2"/>`),
      left: s(`<path ${st} stroke-width="2.6" d="M15 5l-7 7 7 7"/>`),
      right: s(`<path ${st} stroke-width="2.6" d="M9 5l7 7-7 7"/>`),
      back: s(`<path ${st} stroke-width="2.4" d="M15 5l-7 7 7 7"/>`),
      down: s(`<path ${st} stroke-width="2.4" d="M5 9l7 7 7-7"/>`),
      drag: s('<circle cx="9" cy="6" r="1.6" fill="currentColor"/><circle cx="15" cy="6" r="1.6" fill="currentColor"/><circle cx="9" cy="12" r="1.6" fill="currentColor"/><circle cx="15" cy="12" r="1.6" fill="currentColor"/><circle cx="9" cy="18" r="1.6" fill="currentColor"/><circle cx="15" cy="18" r="1.6" fill="currentColor"/>'),
      radio: s(`<circle cx="12" cy="12" r="2.2" fill="currentColor"/><path ${st} d="M8 8a5.7 5.7 0 0 0 0 8M16 8a5.7 5.7 0 0 1 0 8M5.2 5.2a9.6 9.6 0 0 0 0 13.6M18.8 5.2a9.6 9.6 0 0 1 0 13.6"/>`),
      star: s('<path fill="currentColor" d="M12 3.6l2.5 5.3 5.8.7-4.3 4 1.1 5.7L12 16.5l-5.1 2.8 1.1-5.7-4.3-4 5.8-.7z"/>'),
      starOutline: s(`<path ${st} stroke-width="1.6" d="M12 3.6l2.5 5.3 5.8.7-4.3 4 1.1 5.7L12 16.5l-5.1 2.8 1.1-5.7-4.3-4 5.8-.7z"/>`),
      trash: s(`<path ${st} d="M4.5 7h15M9.5 7V4.5h5V7M6.5 7l1 13h9l1-13M10 11v5.5M14 11v5.5"/>`),
      edit: s(`<path ${st} d="M14.5 5.5l4 4L8 20H4v-4z"/>`),
      pin: s(`<path ${st} d="M9 3.5h6l-1 5 3.5 3.5v1.5h-11V12L10 8.5zM12 13.5V20.5"/>`),
      copy: s(`<rect ${st} x="8.5" y="8.5" width="11" height="11" rx="2"/><path ${st} d="M15.5 8.5V6a1.5 1.5 0 0 0-1.5-1.5H6A1.5 1.5 0 0 0 4.5 6v8A1.5 1.5 0 0 0 6 15.5h2.5"/>`),
      speed: s(`<path ${st} d="M4.5 16.5a8 8 0 1 1 15 0"/><path ${st} d="M12 13l4-4.5"/><circle cx="12" cy="13.2" r="1.6" fill="currentColor"/>`),
      info: s(`<circle ${st} cx="12" cy="12" r="9"/><path ${st} stroke-width="2.2" d="M12 11v6M12 7.5v.5"/>`),
      scissors: s(`<circle ${st} cx="6.5" cy="6.5" r="2.8"/><circle ${st} cx="6.5" cy="17.5" r="2.8"/><path ${st} d="M8.6 8.3L20 18M8.6 15.7L20 6"/>`),
      sun: s(`<circle ${st} cx="12" cy="12" r="4"/><path ${st} d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4"/>`),
      alarm: s(`<circle ${st} cx="12" cy="13" r="7"/><path ${st} d="M12 9.5V13l2.5 1.5M4 5.5L7 3M20 5.5L17 3"/>`),
      expand: s(`<path ${st} d="M5 9V5h4M19 9V5h-4M5 15v4h4M19 15v4h-4"/>`),
      collapse: s(`<path ${st} stroke-width="2.4" d="M6 9l6 6 6-6"/>`),
      stats: s(`<path ${st} d="M5 20V11M10 20V5M15 20v-7M20 20V8"/>`),
      gift: s(`<rect ${st} x="4" y="9" width="16" height="11" rx="1.5"/><path ${st} d="M3.5 9h17v-3h-17zM12 6v14M12 6c-1.5-3.5-5.5-3.5-5 0M12 6c1.5-3.5 5.5-3.5 5 0"/>`),
      refresh: s(`<path ${st} d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v4h-4"/>`),
      external: s(`<path ${st} d="M14 4.5h5.5V10M19.5 4.5L11 13M17 14v4.5a1.5 1.5 0 0 1-1.5 1.5h-10A1.5 1.5 0 0 1 4 18.5v-10A1.5 1.5 0 0 1 5.5 7H10"/>`),
      announce: s(`<path ${st} d="M4 10v4h3l6 4.5v-13L7 10z"/><path ${st} d="M16.5 9.5a3.5 3.5 0 0 1 0 5M19 7a7 7 0 0 1 0 10"/>`),
      tap: s(`<path ${st} d="M9.5 11V5.5a1.5 1.5 0 0 1 3 0V11l4.4.9a2 2 0 0 1 1.6 2.2l-.6 4.9H9.5L6 14.5a1.6 1.6 0 0 1 2.3-2.2L9.5 13.5"/>`)
    };
  })();

  const el = (id) => document.getElementById(id);
  const enc = encodeURIComponent;

  // ---- small helpers --------------------------------------------------------------------------

  // Tags, feed text and file names are all text from outside, and half the interpolations are
  // into attributes - so quotes are escaped too.
  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // Our own /api/... routes, or https artwork from the podcast directory and Cover Art Archive.
  // Anything else (a javascript: URL in a poisoned feed) is simply not used.
  function safeSrc(url) {
    const text = String(url || "");
    return /^(\/(?!\/)|https:\/\/)/i.test(text) ? text : null;
  }

  function safeLink(url) {
    const text = String(url || "");
    return /^https?:\/\//i.test(text) ? text : null;
  }

  let toastTimeout;
  function showToast(message, success) {
    const toast = el("toast");
    if (!toast) return;
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
  // options.raw sends a string body as-is (OPML) with options.contentType.
  async function request(url, options = {}) {
    const init = { method: options.method || "GET", headers: {} };
    if (options.raw !== undefined) {
      init.body = options.raw;
      init.headers["Content-Type"] = options.contentType || "text/plain";
    } else if (options.body !== undefined) {
      init.body = JSON.stringify(options.body);
      init.headers["Content-Type"] = "application/json";
    }
    if (options.keepalive) init.keepalive = true;
    if (options.signal) init.signal = options.signal;
    let response;
    try {
      response = await fetch(url, init);
    } catch (err) {
      if (err && err.name === "AbortError") return { ok: false, status: 0, data: null, error: "aborted", aborted: true };
      return { ok: false, status: 0, data: null, error: "Could not reach the server." };
    }
    let data = null;
    const text = await response.text().catch(() => "");
    if (text) { try { data = JSON.parse(text); } catch { data = null; } }
    return { ok: response.ok, status: response.status, data, error: response.ok ? null : errorFrom(data, response.status) };
  }

  function fmtClock(seconds) {
    const s = Math.max(0, Math.floor(Number(seconds) || 0));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = String(s % 60).padStart(2, "0");
    return h ? `${h}:${String(m).padStart(2, "0")}:${sec}` : `${m}:${sec}`;
  }

  // "1 h 12 min", "38 min", "45 s"
  function fmtDuration(seconds) {
    const s = Math.max(0, Math.round(Number(seconds) || 0));
    if (s < 60) return `${s} s`;
    const m = Math.round(s / 60);
    if (m < 60) return `${m} min`;
    const h = Math.floor(m / 60);
    const rest = m % 60;
    return rest ? `${h} h ${rest} min` : `${h} h`;
  }

  function fmtDate(value) {
    if (!value) return "";
    const date = new Date(value);
    if (isNaN(date)) return "";
    const now = new Date();
    const opts = { month: "short", day: "numeric" };
    if (date.getFullYear() !== now.getFullYear()) opts.year = "numeric";
    return date.toLocaleDateString(undefined, opts);
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

  function plural(n, word, many) {
    const num = Number(n) || 0;
    return `${num.toLocaleString()} ${num === 1 ? word : (many || word + "s")}`;
  }

  function sizeLabel(bytes) {
    const b = Number(bytes) || 0;
    if (!b) return "";
    if (b >= 1e12) return `${(b / 1e12).toFixed(1)} TB`;
    if (b >= 1e9) return `${(b / 1e9).toFixed(1)} GB`;
    return `${Math.max(1, Math.round(b / 1e6))} MB`;
  }

  function clamp(v, lo, hi) { return Math.min(hi, Math.max(lo, v)); }

  function lsGet(key, fallback) {
    try {
      const v = localStorage.getItem(LS + key);
      return v === null ? fallback : JSON.parse(v);
    } catch { return fallback; }
  }

  function lsSet(key, value) {
    try { localStorage.setItem(LS + key, JSON.stringify(value)); } catch { /* storage blocked */ }
  }

  function isKiosk() {
    return document.documentElement.classList.contains("kiosk");
  }

  // Podcast descriptions arrive sanitized by the server; this re-checks on the way in, keeping
  // only the tags the contract allows and only http(s) links, so a server bug can't become XSS.
  const ALLOWED_TAGS = new Set(["P", "BR", "A", "B", "I", "EM", "STRONG", "UL", "OL", "LI", "BLOCKQUOTE"]);
  function sanitizeHtml(html) {
    const doc = new DOMParser().parseFromString(`<div>${String(html || "")}</div>`, "text/html");
    const out = document.createElement("div");
    function walk(node, into) {
      for (const child of Array.from(node.childNodes)) {
        if (child.nodeType === 3) { into.appendChild(document.createTextNode(child.textContent)); continue; }
        if (child.nodeType !== 1) continue;
        if (/^(SCRIPT|STYLE|IFRAME|OBJECT|EMBED|TEMPLATE|NOSCRIPT|SVG|MATH)$/.test(child.tagName)) continue;
        if (!ALLOWED_TAGS.has(child.tagName)) { walk(child, into); continue; }
        const copy = document.createElement(child.tagName.toLowerCase());
        if (child.tagName === "A") {
          const href = safeLink(child.getAttribute("href"));
          if (href) { copy.setAttribute("href", href); copy.setAttribute("target", "_blank"); copy.setAttribute("rel", "noopener noreferrer"); }
        }
        walk(child, copy);
        into.appendChild(copy);
      }
    }
    walk(doc.body.firstChild || doc.body, out);
    return out.innerHTML;
  }

  // ---- identity -------------------------------------------------------------------------------

  // Each open tab is its own device. The browser's remembered id (localStorage) belongs to one tab
  // at a time - the one holding a fresh "claim" - and any other tab opened alongside it gets an id
  // of its own (kept in sessionStorage, so a reload keeps it). Before this every tab shared one id:
  // a second tab could not see or control the first tab's "Let others play here" speaker (it hid
  // itself from the picker), and both tabs answered every command sent to it.
  const TAB_NONCE = Math.random().toString(36).slice(2);
  const CLAIM_EVERY_MS = 5000;
  const CLAIM_STALE_MS = 90000; // a hidden tab's timers can be throttled to once a minute
  let tabDeviceId = null;

  function newDeviceId() {
    const rand = (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : Math.random().toString(36).slice(2) + Date.now().toString(36);
    return `web-${rand}`;
  }

  function deviceId() {
    if (tabDeviceId) return tabDeviceId;
    let id = null;
    try { id = sessionStorage.getItem(LS + "tabDeviceId"); } catch { /* blocked */ }
    if (!id) {
      const shared = lsGet("deviceId", null);
      const claim = lsGet("deviceIdClaim", null);
      const heldElsewhere = claim && claim.tab !== TAB_NONCE && Date.now() - (Number(claim.at) || 0) < CLAIM_STALE_MS;
      if (!shared) { id = newDeviceId(); lsSet("deviceId", id); }
      else id = heldElsewhere ? newDeviceId() : shared;
      try { sessionStorage.setItem(LS + "tabDeviceId", id); } catch { /* blocked */ }
    }
    tabDeviceId = id;
    if (isPrimaryTab()) {
      const claimNow = () => lsSet("deviceIdClaim", { tab: TAB_NONCE, at: Date.now() });
      claimNow();
      setInterval(claimNow, CLAIM_EVERY_MS);
      window.addEventListener("pagehide", () => {
        const c = lsGet("deviceIdClaim", null);
        if (c && c.tab === TAB_NONCE) { try { localStorage.removeItem(LS + "deviceIdClaim"); } catch { /* blocked */ } }
      });
    }
    return id;
  }

  // The tab that owns the browser's remembered id: the one that is "this browser" as a speaker.
  function isPrimaryTab() {
    return !!tabDeviceId && tabDeviceId === lsGet("deviceId", null);
  }

  function defaultDeviceName() {
    if (isKiosk()) return "Kiosk";
    const ua = navigator.userAgent || "";
    const browser = /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox" : /Chrome\//.test(ua) ? "Chrome" : /Safari\//.test(ua) ? "Safari" : "Browser";
    const os = /iPhone|iPad/.test(ua) ? "iOS" : /Android/.test(ua) ? "Android" : /Mac OS X/.test(ua) ? "Mac" : /Windows/.test(ua) ? "Windows" : /Linux/.test(ua) ? "Linux" : "";
    return os ? `${browser} on ${os}` : browser;
  }

  function baseDeviceName() {
    return lsGet("deviceName", "") || defaultDeviceName();
  }

  // A second tab of the same browser says so, or "Play on…" and "Continue from" show two
  // "Kitchen Speaker"s that are different tabs.
  function deviceName() {
    const base = baseDeviceName();
    return tabDeviceId && !isPrimaryTab() ? `${base} (another tab)` : base;
  }

  let authInfo = null;
  let authCheck = null;
  function ensureAuth() {
    if (!authCheck) {
      authCheck = request("/api/auth/status").then((res) => {
        authInfo = res.ok && res.data ? res.data : { enabled: false };
        return authInfo;
      });
    }
    return authCheck;
  }

  function isAdmin() {
    return !!(authInfo && (!authInfo.enabled || authInfo.role === "Admin"));
  }

  // ---- artwork, rows, cards -------------------------------------------------------------------

  // The text tile is always drawn under the image; an image that 404s is removed and the tile
  // shows through. Never a broken-image icon.
  function art(url, cls, title, sub, extra = "", glyph = ICON.note) {
    const src = safeSrc(url);
    return `<div class="mv-art au-art ${cls || ""}">
      <div class="mv-art-fallback"><span class="mv-art-glyph">${glyph}</span><span class="mv-art-title">${escapeHtml(title)}</span>${sub ? `<span class="mv-art-sub">${escapeHtml(sub)}</span>` : ""}</div>
      ${src ? `<img src="${escapeHtml(src)}" alt="" loading="lazy" decoding="async">` : ""}
      ${extra}
    </div>`;
  }

  // Up to four album covers as a 2x2 mosaic; one cover fills the tile.
  function mosaic(urls, cls, title, sub, extra = "", glyph = ICON.note) {
    const list = (urls || []).map(safeSrc).filter(Boolean).slice(0, 4);
    if (list.length < 4) return art(list[0] || null, cls, title, sub, extra, glyph);
    return `<div class="mv-art au-art au-mosaic ${cls || ""}">
      <div class="mv-art-fallback"><span class="mv-art-glyph">${glyph}</span><span class="mv-art-title">${escapeHtml(title)}</span></div>
      <div class="au-mosaic-grid">${list.map((u) => `<img src="${escapeHtml(u)}" alt="" loading="lazy" decoding="async">`).join("")}</div>
      ${extra}
    </div>`;
  }

  function wireImages(root) {
    for (const img of root.querySelectorAll("img:not([data-mv-wired])")) {
      img.dataset.mvWired = "1";
      if (img.complete && img.naturalWidth) img.classList.add("mv-loaded");
      img.addEventListener("load", () => img.classList.add("mv-loaded"), { once: true });
      img.addEventListener("error", () => img.remove(), { once: true });
    }
  }

  function rowMarkup(key, title, items, card, extraHead = "") {
    if (!items || !items.length) return "";
    return `<section class="mv-row au-row" data-row="${escapeHtml(key)}">
      <div class="au-row-head"><h3 class="mv-row-title">${title}</h3>${extraHead}</div>
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
    const prev = wrap.querySelector(".mv-prev");
    const next = wrap.querySelector(".mv-next");
    if (prev) prev.disabled = scroller.scrollLeft <= 2;
    if (next) next.disabled = scroller.scrollLeft >= max;
  }

  function wireRows(root, restore) {
    for (const scroller of root.querySelectorAll(".mv-row-scroller")) {
      const key = scroller.closest(".mv-row").dataset.row;
      if (restore && restore[key]) scroller.scrollLeft = restore[key];
      scroller.addEventListener("scroll", () => updateRowArrows(scroller), { passive: true });
      updateRowArrows(scroller);
    }
  }

  function scrollRow(button) {
    const scroller = button.parentElement.querySelector(".mv-row-scroller");
    if (scroller) scroller.scrollBy({ left: Number(button.dataset.dir) * scroller.clientWidth * 0.85, behavior: "smooth" });
  }

  function loadingMarkup(text = "loading") {
    return `<div class="mv-empty"><span class="mv-spinner"></span><span>${escapeHtml(text)}&hellip;</span></div>`;
  }

  function errorMarkup(message) {
    return `<div class="mv-empty mv-error"><div class="mv-empty-title">Couldn't load this</div><div>${escapeHtml(message)}</div>
      <button type="button" class="mv-btn" data-act="retry">try again</button></div>`;
  }

  function emptyMarkup(title, text, glyph) {
    return `<div class="mv-empty">${glyph ? `<div class="au-empty-glyph">${glyph}</div>` : ""}<div class="mv-empty-title">${escapeHtml(title)}</div>${text ? `<div>${text}</div>` : ""}</div>`;
  }

  function stars(rating, act, id) {
    const r = Number(rating) || 0;
    let out = `<span class="au-stars" role="radiogroup" aria-label="Rating">`;
    for (let i = 1; i <= 5; i++) {
      out += `<button type="button" class="au-star ${i <= r ? "on" : ""}" data-act="${act}" data-id="${escapeHtml(id)}" data-value="${i === r ? 0 : i}" role="radio" aria-checked="${i === r}" aria-label="${i} star${i === 1 ? "" : "s"}" title="${i === r ? "Clear rating" : `Rate ${i}`}">${i <= r ? ICON.star : ICON.starOutline}</button>`;
    }
    return out + "</span>";
  }

  // ---- modal ----------------------------------------------------------------------------------

  let modalClose = null;

  function openModal(html, onClose, wide) {
    const panel = el("au-modal-panel");
    panel.innerHTML = html;
    panel.classList.toggle("au-modal-wide", !!wide);
    el("au-modal").classList.remove("hidden");
    modalClose = onClose || null;
    wireImages(panel);
    const focus = panel.querySelector("[autofocus]") || panel.querySelector("input:not([type=checkbox]):not([type=range])");
    if (focus) setTimeout(() => focus.focus(), 30);
    return panel;
  }

  function closeModal() {
    if (el("au-modal").classList.contains("hidden")) return;
    el("au-modal").classList.add("hidden");
    el("au-modal-panel").innerHTML = "";
    const done = modalClose;
    modalClose = null;
    if (done) done();
  }

  function modalOpen() {
    return !el("au-modal").classList.contains("hidden");
  }

  function modalHeader(title, sub) {
    return `<div class="mv-modal-head"><h3>${escapeHtml(title)}</h3>
      <button type="button" class="mv-icon-btn" data-mact="close" aria-label="Close">${ICON.close}</button></div>
      ${sub ? `<p class="mv-modal-intro">${sub}</p>` : ""}`;
  }

  // Handlers registered by the other audio files for their own modal buttons (data-mact).
  const modalHandlers = [];
  function onModalAction(fn) { modalHandlers.push(fn); }

  // ---- pop-up menu ----------------------------------------------------------------------------

  // A small menu anchored to the button that opened it: [{ label, icon, run, danger, disabled }]
  // or "-" for a divider. Closes on any outside click, Escape, or scroll of the page.
  let menuState = null;

  function openMenu(anchor, items, opts = {}) {
    closeMenu();
    const menu = el("au-menu");
    menu.innerHTML = (opts.title ? `<div class="au-menu-title">${escapeHtml(opts.title)}</div>` : "") + items.filter(Boolean).map((item, i) => item === "-"
      ? `<div class="au-menu-sep"></div>`
      : `<button type="button" class="au-menu-item ${item.danger ? "au-danger" : ""} ${item.active ? "active" : ""}" data-i="${i}" ${item.disabled ? "disabled" : ""}>${item.icon || '<span class="au-menu-noicon"></span>'}<span>${escapeHtml(item.label)}</span>${item.hint ? `<span class="au-menu-hint">${escapeHtml(item.hint)}</span>` : ""}</button>`).join("");
    menu.classList.remove("hidden");
    const list = items.filter(Boolean);
    menuState = { anchor, items: list };
    const box = anchor.getBoundingClientRect();
    const mw = menu.offsetWidth;
    const mh = menu.offsetHeight;
    let left = box.right - mw;
    if (left < 8) left = Math.min(box.left, window.innerWidth - mw - 8);
    let top = box.bottom + 6;
    if (top + mh > window.innerHeight - 8) top = Math.max(8, box.top - mh - 6);
    menu.style.left = `${Math.max(8, left)}px`;
    menu.style.top = `${top}px`;
    anchor.setAttribute("aria-expanded", "true");
  }

  function closeMenu() {
    if (!menuState) return;
    const menu = el("au-menu");
    menu.classList.add("hidden");
    menu.innerHTML = "";
    if (menuState.anchor) menuState.anchor.removeAttribute("aria-expanded");
    menuState = null;
  }

  function wireMenu() {
    const menu = el("au-menu");
    menu.addEventListener("click", (e) => {
      const button = e.target.closest(".au-menu-item");
      if (!button || !menuState) return;
      const item = menuState.items[Number(button.dataset.i)];
      closeMenu();
      if (item && item.run) item.run();
    });
    document.addEventListener("pointerdown", (e) => {
      if (menuState && !menu.contains(e.target) && !(menuState.anchor && menuState.anchor.contains(e.target))) closeMenu();
    }, true);
    window.addEventListener("scroll", () => closeMenu(), { passive: true });
    window.addEventListener("resize", () => closeMenu());
  }

  // ---- folder picker (admin) ------------------------------------------------------------------

  // The Movies picker, pointed at /api/audio/browse: folders on this server, and folders shared
  // by other computers (read over SMB by the server; a computer that wants a password answers
  // needsLogin and the sign-in form appears here). Sign-ins are the same list Movies uses.
  let folderPick = null;

  function openFolderBrowser(startPath, onPick, onCancel, intro) {
    folderPick = { onPick, onCancel, path: null };
    openModal(`${modalHeader("Choose a folder")}
      <p class="mv-modal-intro">${intro || "Folders on this server, or shared from another computer on your network. The number beside a folder is how many audio files sit directly inside it."}</p>
      <div class="mv-browse-bar">
        <button type="button" class="mv-icon-btn" data-mact="fp-up" aria-label="Up one folder" title="Up one folder">${ICON.back}</button>
        <input class="field-input mv-browse-input" id="au-browse-input" spellcheck="false" autocomplete="off"
          placeholder="/mnt/music or smb://computer/SharedFolder" aria-label="Folder path">
        <button type="button" class="mv-btn" data-mact="fp-go">go</button>
      </div>
      <div class="mv-folders" id="au-folders">${loadingMarkup()}</div>
      <div class="mv-modal-foot">
        <button type="button" class="mv-btn mv-btn-quiet" data-mact="close">Cancel</button>
        <button type="button" class="mv-btn mv-btn-primary" data-mact="fp-pick" id="au-browse-pick" disabled>Use this folder</button>
      </div>`, () => { const f = folderPick; folderPick = null; if (f && f.onCancel && !f.picked) f.onCancel(); });
    el("au-browse-input").addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); loadFolder(e.target.value.trim()); }
    });
    loadFolder(startPath || "", true);
  }

  function folderButton(f, act) {
    const count = f.audioCount ?? f.videoCount;
    return `<button type="button" class="mv-folder" data-mact="${act || "fp-folder"}" data-path="${escapeHtml(f.path)}">
        ${f.icon || ICON.folder}<span class="mv-folder-name">${escapeHtml(f.name)}</span>
        ${count ? `<span class="mv-folder-count">${plural(count, "audio file")}</span>` : ""}
        ${f.note ? `<span class="mv-folder-note">${escapeHtml(f.note)}</span>` : ""}
      </button>`;
  }

  async function loadFolder(path, fallbackToRoots) {
    const list = el("au-folders");
    if (!list || !folderPick) return;
    list.innerHTML = loadingMarkup();
    const res = await request(`/api/audio/browse${path ? `?path=${enc(path)}` : ""}`);
    if (!folderPick || !el("au-folders")) return;
    const data = res.data || {};
    if ((!res.ok || (data.error && !data.needsLogin)) && fallbackToRoots && path) { loadFolder(""); return; }
    if (!res.ok) {
      list.innerHTML = `<div class="mv-empty mv-error">${escapeHtml(res.error)}</div>` + (folderPick.listing || "");
      return;
    }
    folderPick.path = data.path || "";
    folderPick.parent = data.parent == null ? null : data.parent;
    el("au-browse-input").value = folderPick.path;
    el("au-browse-pick").disabled = !folderPick.path || data.kind === "computer" || !!data.error;
    const up = document.querySelector('#au-modal [data-mact="fp-up"]');
    if (up) up.disabled = !folderPick.path;

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
          <button type="button" class="mv-btn mv-btn-quiet mv-folder-side" data-mact="fp-net-edit" data-id="${escapeHtml(c.id)}">sign-in</button>
        </div>`).join("");
      listing += `<div class="mv-folder-actions">
          <button type="button" class="mv-btn" data-mact="fp-net-add">+ Add a computer</button>
          <button type="button" class="mv-btn" data-mact="fp-net-find">${ICON.search}<span>Find computers</span></button>
        </div>
        <div id="au-net-found"></div>`;
    } else {
      listing = folders.length
        ? folders.map((f) => folderButton(f)).join("")
        : `<div class="mv-empty">${data.kind === "computer" ? "No shared folders listed." : "No folders inside this one."}</div>`;
      if (data.hint) listing = `<div class="mv-note">${escapeHtml(data.hint)}</div>` + listing;
      const here = data.audioCount ?? data.videoCount;
      if (here) listing = `<div class="mv-folder-here">${plural(here, "audio file")} in this folder</div>` + listing;
    }
    folderPick.listing = listing;
    list.innerHTML = listing;
    folderPick.computers = data.computers || folderPick.computers || [];
  }

  function signInMarkup(computer) {
    const saved = computer.id ? computer : null;
    return `<form class="mv-form mv-signin" id="au-signin" data-id="${escapeHtml(saved ? saved.id : "")}">
      <div class="mv-signin-title">${saved ? `Sign-in for ${escapeHtml(saved.name)}` : "Sign in to a computer"}</div>
      <label class="mv-field"><span>Computer name or IP address</span>
        <input class="field-input" id="au-net-host" value="${escapeHtml(computer.host || "")}" placeholder="DESKTOP-PC or 192.168.1.20" spellcheck="false" autocomplete="off"></label>
      <label class="mv-field"><span>Name to show (optional)</span>
        <input class="field-input" id="au-net-name" value="${escapeHtml(saved && saved.name !== saved.host ? saved.name : "")}" placeholder="Den PC" autocomplete="off"></label>
      <label class="mv-field"><span>Username on that computer</span>
        <input class="field-input" id="au-net-user" value="${escapeHtml(computer.username || "")}" placeholder="blank to try as a guest" spellcheck="false" autocomplete="off"></label>
      <label class="mv-field"><span>Password</span>
        <input class="field-input" id="au-net-pass" type="password" autocomplete="new-password" placeholder="${saved && saved.hasPassword ? "blank keeps the saved password" : ""}"></label>
      <div class="mv-field-note">The same sign-ins as Movies, so a computer already signed in to there works here too. For a Windows PC use the Windows account; on a Mac, turn on File Sharing.</div>
      <ul class="field-errors" id="au-net-errors"></ul>
      <div class="mv-signin-foot">
        ${saved ? `<button type="button" class="mv-btn mv-btn-quiet" data-mact="fp-net-forget" data-id="${escapeHtml(saved.id)}">Forget this computer</button>` : ""}
        <span class="mv-spacer"></span>
        <button type="button" class="mv-btn mv-btn-quiet" data-mact="fp-net-back">Back</button>
        <button type="submit" class="mv-btn mv-btn-primary">Sign in</button>
      </div>
    </form>`;
  }

  function showSignIn(computer) {
    const list = el("au-folders");
    if (!list) return;
    list.innerHTML = signInMarkup(computer || {});
    el(computer && computer.host ? "au-net-user" : "au-net-host").focus();
  }

  function showErrors(list, message) {
    if (!list) return;
    const items = Array.isArray(message) ? message : (message ? [message] : []);
    list.innerHTML = items.map((m) => `<li>${escapeHtml(m)}</li>`).join("");
  }

  async function submitSignIn(form) {
    const id = form.dataset.id;
    const body = {
      host: el("au-net-host").value.trim(),
      name: el("au-net-name").value.trim(),
      username: el("au-net-user").value.trim(),
      password: el("au-net-pass").value
    };
    if (!body.host) { showErrors(el("au-net-errors"), "Type the computer's name or IP address."); return; }
    const submit = form.querySelector('[type="submit"]');
    submit.disabled = true;
    submit.textContent = "Signing in…";
    const res = await request(id ? `/api/audio/network/${enc(id)}` : "/api/audio/network", { method: id ? "PUT" : "POST", body });
    if (!el("au-signin")) return;
    submit.disabled = false;
    submit.textContent = "Sign in";
    if (!res.ok) { showErrors(el("au-net-errors"), res.error); return; }
    loadFolder((res.data && res.data.computer && res.data.computer.path) || "");
  }

  async function findComputers(button) {
    const box = el("au-net-found");
    if (!box) return;
    button.disabled = true;
    box.innerHTML = `<div class="mv-empty">Looking for computers sharing files…</div>`;
    const res = await request("/api/audio/network/discover");
    button.disabled = false;
    if (!el("au-net-found")) return;
    if (!res.ok) { box.innerHTML = `<div class="mv-empty mv-error">${escapeHtml(res.error)}</div>`; return; }
    const found = (res.data || []).filter((h) => !h.savedId);
    box.innerHTML = found.length
      ? found.map((h) => folderButton({ name: h.name, path: h.path, icon: ICON.computer, note: h.address || h.host })).join("")
      : `<div class="mv-empty">No other computers answered. Windows PCs don't announce themselves - use Add a computer with the PC's name or address.</div>`;
  }

  function folderModalClick(act, target) {
    switch (act) {
      case "fp-folder": loadFolder(target.dataset.path); return true;
      case "fp-go": loadFolder(el("au-browse-input").value.trim()); return true;
      case "fp-up": if (folderPick) loadFolder(folderPick.parent || ""); return true;
      case "fp-net-add": showSignIn({}); return true;
      case "fp-net-edit": showSignIn(((folderPick && folderPick.computers) || []).find((c) => c.id === target.dataset.id) || {}); return true;
      case "fp-net-back": loadFolder(""); return true;
      case "fp-net-find": findComputers(target); return true;
      case "fp-net-forget":
        if (!confirm("Forget this computer's sign-in? Libraries on it (Music and Movies) stop scanning until you sign in again.")) return true;
        request(`/api/audio/network/${enc(target.dataset.id)}`, { method: "DELETE" }).then(() => loadFolder(""));
        return true;
      case "fp-pick":
        if (folderPick && folderPick.path) {
          const pick = folderPick;
          pick.picked = true;
          closeModal();
          pick.onPick(pick.path);
        }
        return true;
      default: return false;
    }
  }

  function wireModal() {
    const modal = el("au-modal");
    modal.addEventListener("click", (e) => {
      if (e.target === modal) { closeModal(); return; }
      const target = e.target.closest("[data-mact]");
      if (!target) return;
      const act = target.dataset.mact;
      if (act === "close") { closeModal(); return; }
      if (folderModalClick(act, target)) return;
      for (const fn of modalHandlers) if (fn(act, target, e)) return;
    });
    modal.addEventListener("submit", (e) => {
      if (e.target.id === "au-signin") { e.preventDefault(); submitSignIn(e.target); return; }
      for (const fn of modalHandlers) if (fn("submit:" + e.target.id, e.target, e)) { e.preventDefault(); return; }
    });
  }

  // ---- anchored pop-over ----------------------------------------------------------------------

  // Like the menu, but any content: the speaker picker, sleep timer, speed, equalizer, settings.
  let popState = null;

  function openPop(anchor, html, wire, cls) {
    closePop();
    closeMenu();
    const pop = el("au-pop");
    pop.className = `au-pop ${cls || ""}`;
    pop.innerHTML = html;
    popState = { anchor, onClose: null };
    if (wire) popState.onClose = wire(pop) || null;
    placePop(anchor, pop);
    wireImages(pop);
    return pop;
  }

  function placePop(anchor, pop) {
    const box = anchor.getBoundingClientRect();
    const pw = pop.offsetWidth;
    const ph = pop.offsetHeight;
    let left = box.left + box.width / 2 - pw / 2;
    left = clamp(left, 8, window.innerWidth - pw - 8);
    let top = box.top - ph - 10;
    if (top < 8) top = Math.min(window.innerHeight - ph - 8, box.bottom + 10);
    pop.style.left = `${left}px`;
    pop.style.top = `${Math.max(8, top)}px`;
  }

  function closePop() {
    if (!popState) return;
    const pop = el("au-pop");
    const done = popState.onClose;
    popState = null;
    pop.className = "au-pop hidden";
    pop.innerHTML = "";
    if (typeof done === "function") done();
  }

  function popOpenFor(anchor) {
    return !!(popState && popState.anchor === anchor);
  }

  function wirePop() {
    const pop = el("au-pop");
    document.addEventListener("pointerdown", (e) => {
      if (popState && !pop.contains(e.target) && !(popState.anchor && popState.anchor.contains(e.target)) && !el("au-menu").contains(e.target)) closePop();
    }, true);
    window.addEventListener("resize", () => closePop());
  }

  // ---- drag to reorder ------------------------------------------------------------------------

  // Pointer-based so it works with a finger as well as a mouse (HTML5 drag and drop does not
  // fire on touch screens). Rows carry data-sort, the grip is .au-drag; onMove(from, to) gets the
  // indexes and is expected to re-render.
  function makeSortable(list, onMove) {
    if (!list || list.dataset.sortable) return;
    list.dataset.sortable = "1";
    list.addEventListener("pointerdown", (e) => {
      const handle = e.target.closest(".au-drag");
      if (!handle || !list.contains(handle) || e.button > 0) return;
      const row = handle.closest("[data-sort]");
      if (!row) return;
      e.preventDefault();
      // The rows' own parent: a pane can wrap its sortable list in other markup.
      const host = row.parentElement;
      const rows = Array.from(host.querySelectorAll(":scope > [data-sort]"));
      const from = rows.indexOf(row);
      if (from < 0) return;
      const scroller = list.closest(".au-scroll");
      const scrollTop = () => (scroller ? scroller.scrollTop : window.scrollY);
      const startScroll = scrollTop();
      const height = row.getBoundingClientRect().height + (parseFloat(getComputedStyle(host).rowGap) || 0);
      const mids = rows.map((r) => { const b = r.getBoundingClientRect(); return b.top + b.height / 2; });
      const startY = e.clientY;
      let to = from;
      let lastY = startY;
      row.classList.add("au-dragging");
      host.classList.add("au-sorting");
      let autoTimer = null;

      const layout = () => {
        const drift = scrollTop() - startScroll;
        row.style.transform = `translateY(${lastY - startY + drift}px)`;
        let idx = 0;
        for (let i = 0; i < rows.length; i++) {
          if (i === from) continue;
          if (lastY + drift > mids[i]) idx++;
        }
        to = idx;
        rows.forEach((r, i) => {
          if (r === row) return;
          let shift = 0;
          if (from < to && i > from && i <= to) shift = -height;
          if (from > to && i >= to && i < from) shift = height;
          r.style.transform = shift ? `translateY(${shift}px)` : "";
        });
      };
      const autoscroll = () => {
        const box = scroller ? scroller.getBoundingClientRect() : { top: 0, bottom: window.innerHeight };
        const edge = 48;
        let dy = 0;
        if (lastY < box.top + edge) dy = -12;
        else if (lastY > box.bottom - edge) dy = 12;
        if (dy) { if (scroller) scroller.scrollTop += dy; else window.scrollBy(0, dy); layout(); }
      };
      const move = (ev) => { lastY = ev.clientY; layout(); };
      const end = () => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", end);
        window.removeEventListener("pointercancel", end);
        clearInterval(autoTimer);
        rows.forEach((r) => { r.style.transform = ""; });
        row.classList.remove("au-dragging");
        host.classList.remove("au-sorting");
        if (to !== from) onMove(from, to);
      };
      autoTimer = setInterval(autoscroll, 40);
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", end);
      window.addEventListener("pointercancel", end);
    });
  }

  function moveInArray(list, from, to) {
    const copy = list.slice();
    const [item] = copy.splice(from, 1);
    copy.splice(to, 0, item);
    return copy;
  }

  // ---- settings (this browser) ----------------------------------------------------------------

  const settings = {
    volume: clamp(Number(lsGet("volume", 0.8)) || 0, 0, 1),
    muted: false,
    crossfade: clamp(Number(lsGet("crossfade", 0)) || 0, 0, 12),
    soundCheck: lsGet("soundCheck", true) !== false,
    musicSpeed: clamp(Number(lsGet("musicSpeed", 1)) || 1, 0.5, 3),
    podcastSpeed: clamp(Number(lsGet("podcastSpeed", 1)) || 1, 0.5, 3),
    maxBitrate: Number(lsGet("maxBitrate", 0)) || 0,
    allowAnnouncements: lsGet("allowAnnouncements", true) !== false,
    eq: (function () {
      const saved = lsGet("eq", null);
      const base = { enabled: false, preset: "flat", gains: EQ_PRESETS.flat.gains.slice(), preamp: 0 };
      if (!saved || !Array.isArray(saved.gains) || saved.gains.length !== EQ_BANDS.length) return base;
      return { enabled: !!saved.enabled, preset: saved.preset || "custom", gains: saved.gains.map((g) => clamp(Number(g) || 0, -12, 12)), preamp: clamp(Number(saved.preamp) || 0, -12, 6) };
    })()
  };

  function saveSetting(key) { lsSet(key, settings[key]); }

  // ---- the two decks and the Web Audio graph --------------------------------------------------

  // Two <audio> elements so the next track can start while this one fades out (crossfade), or
  // sit preloaded and decoded so it starts the instant this one ends (as close to gapless as a
  // browser gets). Each deck runs into its own gain (Sound Check x crossfade), then the shared
  // preamp and 10-band EQ, then duck (announcements), fade (sleep timer, alarm ramp) and the
  // volume. Web Audio needs a user gesture to start; until then levels go on element.volume.
  function makeDeck(n) {
    const audio = document.createElement("audio");
    audio.preload = "auto";
    audio.id = `au-deck-${n}`;
    audio.setAttribute("playsinline", "");
    audio.className = "au-deck";
    document.body.appendChild(audio);
    return { n, el: audio, item: null, key: null, rg: 1, xf: 1, node: null, src: null, listened: 0, lastTime: 0, finished: false, preloaded: false, ramp: null, lastReported: -1 };
  }

  const decks = [makeDeck(0), makeDeck(1)];
  let active = 0;
  const activeDeck = () => decks[active];
  const idleDeck = () => decks[1 - active];

  const graph = { ctx: null, ready: false, eq: [], preamp: null, duck: null, fade: null, master: null };
  const lv = { duck: 1, fade: 1, ramps: {} };

  function unlockAudio() {
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return;
    if (!graph.ctx) {
      try { graph.ctx = new Ctx(); } catch { return; }
      graph.ctx.addEventListener("statechange", () => {
        if (graph.ctx.state === "running") buildGraph();
      });
    }
    if (graph.ctx.state !== "running") graph.ctx.resume().then(buildGraph).catch(() => {});
    else buildGraph();
  }

  function buildGraph() {
    if (graph.ready || !graph.ctx || graph.ctx.state !== "running") return;
    const ctx = graph.ctx;
    try {
      graph.preamp = ctx.createGain();
      let prev = graph.preamp;
      graph.eq = EQ_BANDS.map((freq, i) => {
        const band = ctx.createBiquadFilter();
        band.type = i === 0 ? "lowshelf" : i === EQ_BANDS.length - 1 ? "highshelf" : "peaking";
        band.frequency.value = freq;
        band.Q.value = 1.1;
        band.gain.value = 0;
        prev.connect(band);
        prev = band;
        return band;
      });
      graph.duck = ctx.createGain();
      graph.fade = ctx.createGain();
      graph.master = ctx.createGain();
      graph.duck.gain.value = lv.duck;
      graph.fade.gain.value = lv.fade;
      graph.master.gain.value = masterLevel();
      prev.connect(graph.duck);
      graph.duck.connect(graph.fade);
      graph.fade.connect(graph.master);
      graph.master.connect(ctx.destination);
      for (const deck of decks) {
        deck.src = ctx.createMediaElementSource(deck.el);
        deck.node = ctx.createGain();
        deck.node.gain.value = deck.rg * deck.xf;
        deck.src.connect(deck.node);
        deck.node.connect(graph.preamp);
      }
      graph.ready = true;
      applyEq();
      applyLevels(0);
    } catch (err) {
      console.warn("Audio effects unavailable; playing without Sound Check or the equalizer.", err);
    }
  }

  function masterLevel() {
    return settings.muted ? 0 : settings.volume;
  }

  function setParam(param, value, seconds) {
    const now = graph.ctx.currentTime;
    param.cancelScheduledValues(now);
    param.setValueAtTime(param.value, now);
    param.linearRampToValueAtTime(value, now + Math.max(0.015, seconds));
  }

  function applyLevels(seconds = 0.05) {
    if (graph.ready) {
      for (const deck of decks) {
        deck.el.volume = 1;
        if (!deck.ramp) setParam(deck.node.gain, deck.rg * deck.xf, seconds);
      }
      if (!lv.ramps.duck) setParam(graph.duck.gain, lv.duck, seconds);
      if (!lv.ramps.fade) setParam(graph.fade.gain, lv.fade, seconds);
      setParam(graph.master.gain, masterLevel(), seconds);
    } else {
      for (const deck of decks) deck.el.volume = clamp(deck.rg * deck.xf * lv.duck * lv.fade * masterLevel(), 0, 1);
    }
  }

  // A smooth move of one level ("duck" or "fade") over seconds. With Web Audio it is scheduled
  // on the audio clock (exact even in a background tab); without, it steps element volumes.
  function rampLevel(name, target, seconds, done) {
    const old = lv.ramps[name];
    if (old) { clearInterval(old.timer); clearTimeout(old.end); }
    lv.ramps[name] = null;
    const from = lv[name];
    if (!(seconds > 0)) { lv[name] = target; applyLevels(0.02); if (done) done(); return; }
    const started = performance.now();
    const ramp = { timer: null, end: null };
    lv.ramps[name] = ramp;
    const finish = () => {
      clearInterval(ramp.timer);
      clearTimeout(ramp.end);
      if (lv.ramps[name] !== ramp) return;
      lv.ramps[name] = null;
      lv[name] = target;
      if (!graph.ready) applyLevels(0.02);
      if (done) done();
    };
    if (graph.ready) {
      setParam(graph[name].gain, target, seconds);
      ramp.timer = setInterval(() => { lv[name] = from + (target - from) * Math.min(1, (performance.now() - started) / (seconds * 1000)); }, 250);
    } else {
      ramp.timer = setInterval(() => {
        const k = Math.min(1, (performance.now() - started) / (seconds * 1000));
        lv[name] = from + (target - from) * k;
        applyLevels();
      }, 50);
    }
    ramp.end = setTimeout(finish, seconds * 1000);
  }

  function rampDeck(deck, targetXf, seconds, done) {
    if (deck.ramp) { clearInterval(deck.ramp.timer); clearTimeout(deck.ramp.end); }
    const from = deck.xf;
    const started = performance.now();
    const ramp = { timer: null, end: null };
    deck.ramp = ramp;
    const finish = () => {
      clearInterval(ramp.timer);
      if (deck.ramp !== ramp) return;
      deck.ramp = null;
      deck.xf = targetXf;
      applyLevels(0.02);
      if (done) done();
    };
    if (graph.ready) {
      setParam(deck.node.gain, deck.rg * targetXf, seconds);
      ramp.timer = setInterval(() => { deck.xf = from + (targetXf - from) * Math.min(1, (performance.now() - started) / (seconds * 1000)); }, 250);
    } else {
      ramp.timer = setInterval(() => {
        deck.xf = from + (targetXf - from) * Math.min(1, (performance.now() - started) / (seconds * 1000));
        deck.el.volume = clamp(deck.rg * deck.xf * lv.duck * lv.fade * masterLevel(), 0, 1);
      }, 50);
    }
    ramp.end = setTimeout(finish, seconds * 1000);
  }

  function stopDeckRamp(deck) {
    if (!deck.ramp) return;
    clearInterval(deck.ramp.timer);
    clearTimeout(deck.ramp.end);
    deck.ramp = null;
  }

  function applyEq() {
    if (!graph.ready) return;
    const eq = settings.eq;
    graph.eq.forEach((band, i) => setParam(band.gain, eq.enabled ? eq.gains[i] : 0, 0.08));
    // Boosting bands can clip; the preamp (and an automatic trim of the biggest boost) keeps
    // headroom so a "bass boost" doesn't distort.
    const maxBoost = eq.enabled ? Math.max(0, ...eq.gains) : 0;
    const pre = eq.enabled ? eq.preamp - maxBoost * 0.5 : 0;
    setParam(graph.preamp.gain, Math.pow(10, pre / 20), 0.08);
  }

  function setVolume(value, persist = true) {
    settings.volume = clamp(Number(value) || 0, 0, 1);
    if (settings.volume > 0) settings.muted = false;
    if (persist) saveSetting("volume");
    applyLevels();
    emit("volume");
    speakerStateSoon();
  }

  // ---- codecs and URLs ------------------------------------------------------------------------

  let codecList = null;
  function codecs() {
    if (codecList) return codecList;
    const probe = document.createElement("audio");
    const can = (type) => { try { return !!probe.canPlayType(type); } catch { return false; } };
    const list = ["mp3", "aac"];
    if (can("audio/flac") || can('audio/ogg; codecs="flac"')) list.push("flac");
    if (can('audio/ogg; codecs="opus"') || can('audio/webm; codecs="opus"')) list.push("opus");
    if (can('audio/ogg; codecs="vorbis"')) list.push("vorbis");
    if (can("audio/wav") || can('audio/wav; codecs="1"')) list.push("wav");
    if (can('audio/mp4; codecs="alac"')) list.push("alac");
    codecList = list.join(",");
    return codecList;
  }

  function withQuery(url, params) {
    const q = new URLSearchParams(params).toString();
    if (!q) return url;
    return url + (url.includes("?") ? "&" : "?") + q;
  }

  function streamUrl(trackId, raw) {
    const base = (raw && safeSrc(raw.streamUrl)) || `/api/music/tracks/${enc(trackId)}/stream`;
    const params = { codecs: codecs() };
    if (settings.maxBitrate) params.maxBitrate = String(settings.maxBitrate);
    return withQuery(base, params);
  }

  function artSized(url, size) {
    const src = safeSrc(url);
    if (!src) return null;
    if (/^\/api\/music\//.test(src) && !/[?&]size=/.test(src)) return withQuery(src, { size: String(size) });
    return src;
  }

  function srcFor(item) {
    if (item.type === "track") return streamUrl(item.id, item.raw);
    return (item.raw && safeSrc(item.raw.audioUrl)) || `/api/podcasts/episodes/${enc(item.id)}/audio`;
  }

  // ---- queue items ----------------------------------------------------------------------------

  let keySeq = 0;
  function trackItem(t) {
    return {
      type: "track", id: t.id, key: `k${++keySeq}`,
      title: t.title || "Untitled", subtitle: t.artist || t.albumArtist || "", album: t.album || "",
      artUrl: t.artUrl || `/api/music/tracks/${enc(t.id)}/art`,
      durationSeconds: Number(t.durationSeconds) || 0, raw: t
    };
  }

  function episodeItem(e) {
    return {
      type: "episode", id: e.id, key: `k${++keySeq}`,
      title: e.title || "Episode", subtitle: e.podcastTitle || "", album: "",
      artUrl: e.artUrl || `/api/podcasts/episodes/${enc(e.id)}/art`,
      durationSeconds: Number(e.durationSeconds) || 0, raw: e
    };
  }

  function stubItem(ref) {
    return ref.type === "episode"
      ? { ...episodeItem({ id: ref.id, title: "Loading…" }), stub: true }
      : { ...trackItem({ id: ref.id, title: "Loading…" }), stub: true };
  }

  function itemFromRaw(raw, type) {
    return type === "episode" ? episodeItem(raw) : trackItem(raw);
  }

  // ---- player state ---------------------------------------------------------------------------

  const state = {
    queue: [], original: null, index: -1, shuffle: false, repeat: lsGet("repeat", "off"),
    context: null, radio: null, radioLoading: false, ended: false, blocked: false,
    xfading: null, sleep: null, lastSeekAt: 0
  };
  if (!["off", "all", "one"].includes(state.repeat)) state.repeat = "off";

  const currentItem = () => (state.index >= 0 ? state.queue[state.index] || null : null);

  const listeners = {};
  function on(name, fn) { (listeners[name] = listeners[name] || []).push(fn); }
  function emit(name, data) {
    for (const fn of listeners[name] || []) { try { fn(data); } catch (err) { console.error(err); } }
  }

  // Per-show settings (speed, skip intro/outro), fetched once per show when one of its episodes
  // starts, and kept up to date when this browser changes the speed.
  const shows = new Map();
  async function showFor(podcastId) {
    if (!podcastId) return null;
    if (shows.has(podcastId)) return shows.get(podcastId);
    const res = await request(`/api/podcasts/${enc(podcastId)}`);
    const show = res.ok && res.data ? res.data : null;
    if (show) shows.set(podcastId, show);
    return show;
  }

  function mySettingsFor(item) {
    if (!item || item.type !== "episode" || !item.raw) return null;
    const show = shows.get(item.raw.podcastId);
    return show ? show.mySettings || null : null;
  }

  function speedFor(item) {
    if (!item) return 1;
    if (item.type === "episode") {
      const my = mySettingsFor(item);
      return clamp(Number(my && my.playbackSpeed) || settings.podcastSpeed || 1, 0.5, 3);
    }
    return settings.musicSpeed || 1;
  }

  // Sound Check: volume = 10^(gain/20), clamped to 1.0; album gain when an album plays in order.
  function replayGainFor(item) {
    if (!settings.soundCheck || !item || item.type !== "track" || !item.raw || !item.raw.replayGain) return 1;
    const rg = item.raw.replayGain;
    const albumInOrder = state.context && state.context.type === "album" && !state.shuffle;
    const gain = albumInOrder && rg.albumGain != null ? rg.albumGain : (rg.trackGain != null ? rg.trackGain : rg.albumGain);
    if (gain == null || !isFinite(gain)) return 1;
    return clamp(Math.pow(10, gain / 20), 0, 1);
  }

  function durationOf(deck, item) {
    const d = deck.el.duration;
    if (isFinite(d) && d > 0) return d;
    return (item && item.durationSeconds) || 0;
  }

  function loadInto(deck, item) {
    stopDeckRamp(deck);
    deck.item = item;
    deck.key = item.key;
    deck.listened = 0;
    deck.lastTime = 0;
    deck.finished = false;
    deck.preloaded = false;
    deck.lastReported = -1;
    deck.rg = replayGainFor(item);
    const src = srcFor(item);
    if (deck.el.getAttribute("src") !== src) {
      deck.el.src = src;
    } else {
      try { deck.el.currentTime = 0; } catch { /* not loaded */ }
    }
    deck.el.playbackRate = speedFor(item);
    deck.el.defaultPlaybackRate = deck.el.playbackRate;
    if ("preservesPitch" in deck.el) deck.el.preservesPitch = true;
  }

  function unloadDeck(deck) {
    stopDeckRamp(deck);
    deck.el.pause();
    deck.el.removeAttribute("src");
    try { deck.el.load(); } catch { /* nothing loaded */ }
    deck.item = null;
    deck.key = null;
    deck.preloaded = false;
    deck.xf = 1;
  }

  function seekDeck(deck, t) {
    const set = () => { try { deck.el.currentTime = Math.max(0, t); } catch { /* not seekable yet */ } deck.lastTime = deck.el.currentTime; };
    if (deck.el.readyState >= 1) set();
    else deck.el.addEventListener("loadedmetadata", set, { once: true });
  }

  async function startDeck(deck) {
    unlockAudio();
    try {
      await deck.el.play();
      state.blocked = false;
      hideTapToPlay();
    } catch (err) {
      if (err && err.name === "NotAllowedError") {
        state.blocked = true;
        showTapToPlay();
      } else if (err && err.name !== "AbortError") {
        console.warn("Playback failed", err);
      }
    }
    emit("state");
  }

  // Starts queue[i]. opts: position (seconds), autoplay (default true), reason for the item
  // being left ("skipped" by default), noFinish when the caller already reported it.
  async function playIndex(i, opts = {}) {
    if (i < 0 || i >= state.queue.length) return;
    if (!opts.noFinish) finishItem(opts.reason || "skipped");
    cancelCrossfade();
    const old = activeDeck();
    const item = state.queue[i];
    state.index = i;
    state.ended = false;
    const idle = idleDeck();
    let deck = old;
    if (idle.key === item.key && idle.preloaded) {
      old.el.pause();
      unloadDeck(old);
      active = idle.n;
      deck = idle;
      deck.preloaded = false;
      deck.rg = replayGainFor(item);
      deck.el.playbackRate = speedFor(item);
    } else {
      if (idle.key) unloadDeck(idle);
      if (old.item && old.key !== item.key) old.el.pause();
      loadInto(deck, item);
    }
    deck.xf = 1;
    applyLevels(0.02);

    let position = opts.position;
    if (position == null && item.type === "episode" && opts.resume !== false) {
      const p = item.raw && item.raw.progress;
      if (p && !p.played && p.positionSeconds > 0) position = p.positionSeconds;
    }
    if (item.type === "episode") {
      // The show's settings may not be cached yet; speed and skip-intro apply when they arrive.
      const podcastId = item.raw && item.raw.podcastId;
      const known = shows.has(podcastId);
      applyIntroSkip(deck, item, position);
      if (!known && podcastId) {
        showFor(podcastId).then(() => {
          if (currentItem() !== item) return;
          deck.el.playbackRate = speedFor(item);
          applyIntroSkip(deck, item, position);
          emit("speed");
        });
      }
    }
    if (position != null && position > 0) seekDeck(deck, position);
    onItemChanged();
    if (opts.autoplay !== false) await startDeck(deck);
    else emit("state");
    if (state.radio) extendRadio();
  }

  function applyIntroSkip(deck, item, position) {
    const my = mySettingsFor(item);
    const intro = Number(my && my.skipIntroSeconds) || 0;
    if (intro > 0 && (position == null || position < intro)) seekDeck(deck, intro);
  }

  // Repeat is a music control: its button is hidden while an episode plays, so a repeat left on
  // from music must not loop a podcast episode forever with no visible way to stop it.
  function repeatMode() {
    const item = currentItem();
    return item && item.type === "episode" ? "off" : state.repeat;
  }

  function nextIndex(auto) {
    if (!state.queue.length) return -1;
    const mode = repeatMode();
    if (auto && mode === "one") return state.index;
    if (state.index + 1 < state.queue.length) return state.index + 1;
    if (mode === "all") return 0;
    return -1;
  }

  // Two tracks of one album that follow each other are meant to run together (live albums,
  // DJ mixes), so they never crossfade.
  function gaplessPair(a, b) {
    if (!a || !b || a.type !== "track" || b.type !== "track" || !a.raw || !b.raw) return false;
    return a.raw.albumId && a.raw.albumId === b.raw.albumId
      && (Number(b.raw.trackNumber) === Number(a.raw.trackNumber) + 1) && (b.raw.discNumber || 1) === (a.raw.discNumber || 1);
  }

  function startCrossfade(nextI, seconds) {
    const from = activeDeck();
    const to = idleDeck();
    const next = state.queue[nextI];
    if (!next) return;
    finishItem("completed");
    if (to.key !== next.key) loadInto(to, next);
    else { to.rg = replayGainFor(next); to.el.playbackRate = speedFor(next); }
    to.preloaded = false;
    to.xf = 0;
    try { to.el.currentTime = 0; } catch { /* fresh */ }
    applyLevels(0.01);
    active = to.n;
    state.index = nextI;
    state.xfading = { from };
    to.el.play().catch(() => {});
    rampDeck(to, 1, seconds);
    rampDeck(from, 0, seconds, () => {
      if (state.xfading && state.xfading.from === from) state.xfading = null;
      unloadDeck(from);
    });
    onItemChanged();
    emit("state");
    if (state.radio) extendRadio();
  }

  function cancelCrossfade() {
    if (!state.xfading) return;
    const from = state.xfading.from;
    state.xfading = null;
    unloadDeck(from);
    const deck = activeDeck();
    stopDeckRamp(deck);
    deck.xf = 1;
    applyLevels(0.05);
  }

  function preloadNext(deck) {
    if (repeatMode() === "one") return;
    const nextI = nextIndex(false);
    const next = state.queue[nextI];
    const idle = idleDeck();
    if (!next || idle === deck || idle.key === next.key) return;
    loadInto(idle, next);
    idle.preloaded = true;
    idle.xf = 1;
    try { idle.el.load(); } catch { /* fine */ }
  }

  function onDeckTime(deck) {
    if (deck !== activeDeck()) return;
    const item = deck.item;
    if (!item) return;
    const t = deck.el.currentTime;
    const delta = t - deck.lastTime;
    if (delta > 0 && delta < 3 && !deck.el.paused) deck.listened += delta;
    deck.lastTime = t;
    const dur = durationOf(deck, item);
    const rem = dur ? dur - t : Infinity;

    if (item.type === "episode") {
      const my = mySettingsFor(item);
      const outro = Number(my && my.skipOutroSeconds) || 0;
      if (outro > 0 && dur > outro + 60 && rem <= outro && !deck.el.paused) { onEnded(deck); return; }
    }
    if (state.sleep && state.sleep.mode === "end" && rem <= SLEEP_FADE_SECONDS && !state.sleep.fading && !deck.el.paused) {
      startSleepFade(Math.max(1, rem - 0.2));
    }
    if (rem < PRELOAD_BEFORE_END + settings.crossfade && !state.xfading) preloadNext(deck);
    if (!state.xfading && settings.crossfade > 0 && item.type === "track" && !deck.el.paused && !(state.sleep && state.sleep.mode === "end")) {
      const nextI = nextIndex(false);
      const next = state.queue[nextI];
      if (next && next.type === "track" && nextI !== state.index && !gaplessPair(item, next)
          && dur > settings.crossfade * 2 + 5 && rem <= settings.crossfade && rem > 0.4) {
        startCrossfade(nextI, rem);
        return;
      }
    }
    emit("time");
  }

  function onEnded(deck) {
    if (deck !== activeDeck() || !deck.item) return;
    const item = deck.item;
    finishItem("completed");
    if (state.sleep && state.sleep.mode === "end") { endSleep(true); deck.el.pause(); emit("state"); return; }
    if (repeatMode() === "one") {
      deck.finished = false;
      deck.listened = 0;
      seekDeck(deck, 0);
      startDeck(deck);
      return;
    }
    const nextI = nextIndex(true);
    if (nextI < 0) {
      state.ended = true;
      deck.el.pause();
      if (item.type === "episode") seekDeck(deck, 0);
      emit("state");
      sendNowPlayingSoon();
      speakerStateSoon();
      return;
    }
    playIndex(nextI, { noFinish: true });
  }

  // ---- reporting ------------------------------------------------------------------------------

  // A track is reported once, when it ends or is left; an episode's place is saved.
  function finishItem(reason) {
    const deck = activeDeck();
    const item = deck.item;
    if (!item || deck.finished) return;
    deck.finished = true;
    if (item.type === "track") {
      const seconds = Math.round(deck.listened);
      if (seconds >= 1) {
        request(`/api/music/tracks/${enc(item.id)}/played`, {
          method: "POST",
          keepalive: true,
          body: { secondsListened: seconds, completed: reason === "completed", skipped: reason === "skipped", deviceName: deviceName() }
        });
        if (item.raw) {
          item.raw.playCount = (Number(item.raw.playCount) || 0) + (seconds >= 30 ? 1 : 0);
        }
      }
    } else if (item.type === "episode") {
      reportProgress(true, reason === "completed");
    }
  }

  function reportProgress(final, completed) {
    const deck = activeDeck();
    const item = deck.item;
    if (!item || item.type !== "episode") return;
    const dur = durationOf(deck, item);
    const pos = completed && dur ? dur : deck.el.currentTime;
    if (!(pos > 0)) return;
    if (!final && Math.abs(pos - deck.lastReported) < 1) return;
    if (final && Math.abs(pos - deck.lastReported) < 0.5) return;
    deck.lastReported = pos;
    const body = { positionSeconds: Math.round(pos * 10) / 10, durationSeconds: dur ? Math.round(dur * 10) / 10 : null, speed: deck.el.playbackRate || 1, deviceName: deviceName() };
    request(`/api/podcasts/episodes/${enc(item.id)}/progress`, { method: "POST", body, keepalive: !!final }).then((res) => {
      if (res.ok && res.data && item.raw) {
        item.raw.progress = res.data;
        emit("progress", item);
      }
    });
  }

  function nowPlayingBody() {
    const deck = activeDeck();
    const item = currentItem();
    if (!item) return null;
    let refs = state.queue.map((i) => ({ type: i.type, id: i.id }));
    let index = state.index;
    if (refs.length > 500) {
      const start = clamp(index - 250, 0, refs.length - 500);
      refs = refs.slice(start, start + 500);
      index -= start;
    }
    return {
      deviceId: deviceId(), deviceName: deviceName(), kind: item.type === "episode" ? "podcast" : "music",
      queue: refs, index, positionSeconds: Math.round((deck.el.currentTime || 0) * 10) / 10,
      playing: !deck.el.paused && !state.ended, shuffle: state.shuffle, repeat: state.repeat,
      speed: deck.el.playbackRate || 1, context: state.context || { type: "queue", id: null, name: "Queue" }
    };
  }

  let npDebounce = null;
  let npInterval = null;
  async function sendNowPlaying(keepalive) {
    clearTimeout(npDebounce);
    if (!enabledFlags.music && !enabledFlags.podcasts) return;
    const body = nowPlayingBody();
    if (!body) return;
    await request("/api/audio/now-playing", { method: "PUT", body, keepalive: !!keepalive });
  }

  function sendNowPlayingSoon() {
    clearTimeout(npDebounce);
    npDebounce = setTimeout(() => sendNowPlaying(), 800);
  }

  // ---- Media Session (lock screen, hardware keys) ---------------------------------------------

  function absoluteUrl(url) {
    try { return new URL(url, location.href).href; } catch { return url; }
  }

  function updateMediaSession() {
    if (!("mediaSession" in navigator)) return;
    const item = currentItem();
    if (!item) { navigator.mediaSession.metadata = null; return; }
    const art = artSized(item.artUrl, 512);
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: item.title || "",
        artist: item.subtitle || "",
        album: item.type === "track" ? item.album || "" : (item.raw && item.raw.podcastTitle) || "",
        artwork: art ? [{ src: absoluteUrl(art), sizes: "512x512", type: "image/jpeg" }] : []
      });
    } catch { /* old browser */ }
  }

  let lastPositionState = 0;
  function updatePositionState(force) {
    if (!("mediaSession" in navigator) || !navigator.mediaSession.setPositionState) return;
    const now = Date.now();
    if (!force && now - lastPositionState < 4000) return;
    lastPositionState = now;
    const deck = activeDeck();
    const dur = durationOf(deck, deck.item);
    if (!dur || !isFinite(dur)) return;
    try {
      navigator.mediaSession.setPositionState({ duration: dur, playbackRate: deck.el.playbackRate || 1, position: clamp(deck.el.currentTime || 0, 0, dur) });
    } catch { /* position past duration while loading */ }
  }

  function wireMediaSession() {
    if (!("mediaSession" in navigator)) return;
    const set = (action, fn) => { try { navigator.mediaSession.setActionHandler(action, fn); } catch { /* unsupported action */ } };
    set("play", () => resume());
    set("pause", () => pause());
    set("stop", () => pause());
    set("previoustrack", () => previous());
    set("nexttrack", () => next());
    set("seekbackward", (d) => skipBy(-((d && d.seekOffset) || (isEpisodeNow() ? 15 : 10))));
    set("seekforward", (d) => skipBy((d && d.seekOffset) || (isEpisodeNow() ? 30 : 10)));
    set("seekto", (d) => { if (d && d.seekTime != null) seek(d.seekTime); });
  }

  // ---- transport ------------------------------------------------------------------------------

  function isEpisodeNow() {
    const item = remote.target ? null : currentItem();
    if (remote.target) return remote.target.state && remote.target.state.kind === "podcast";
    return !!(item && item.type === "episode");
  }

  function isPlaying() {
    if (remote.target) return !!(remote.target.state && remote.target.state.playing);
    const deck = activeDeck();
    return !!(deck.item && !deck.el.paused && !state.ended);
  }

  function pause() {
    if (remote.target) { remoteCommand({ action: "pause" }); return; }
    const deck = activeDeck();
    if (state.xfading) cancelCrossfade();
    deck.el.pause();
    emit("state");
  }

  function resume() {
    if (remote.target) { remoteCommand({ action: "resume" }); return; }
    const item = currentItem();
    if (!item) {
      if (state.queue.length) { playIndex(0); return; }
      return;
    }
    if (state.ended) { state.ended = false; seekDeck(activeDeck(), 0); }
    startDeck(activeDeck());
  }

  function togglePlay() {
    unlockAudio();
    if (isPlaying()) pause(); else resume();
  }

  function next() {
    if (remote.target) { remoteCommand({ action: "next" }); return; }
    if (!state.queue.length) return;
    const i = nextIndex(false);
    if (i < 0) {
      // At the end with repeat off: stop on the last item rather than wrap.
      finishItem("skipped");
      const deck = activeDeck();
      deck.el.pause();
      state.ended = true;
      emit("state");
      return;
    }
    playIndex(i, { reason: "skipped" });
  }

  function previous() {
    if (remote.target) { remoteCommand({ action: "previous" }); return; }
    const deck = activeDeck();
    if (!deck.item) return;
    if (deck.el.currentTime > 3 || state.index <= 0) {
      if (state.index <= 0 && repeatMode() === "all" && deck.el.currentTime <= 3 && state.queue.length > 1) {
        playIndex(state.queue.length - 1, { reason: "skipped", resume: false, position: 0 });
        return;
      }
      seek(0);
      return;
    }
    playIndex(state.index - 1, { reason: "skipped", resume: false, position: 0 });
  }

  function seek(t) {
    if (remote.target) {
      remoteCommand({ action: "seek", positionSeconds: Math.max(0, t) });
      if (remote.target.state) { remote.target.state.positionSeconds = Math.max(0, t); remote.fetchedAt = Date.now(); }
      emit("time");
      return;
    }
    const deck = activeDeck();
    if (!deck.item) return;
    if (state.xfading) cancelCrossfade();
    const dur = durationOf(deck, deck.item);
    const target = clamp(t, 0, dur ? dur - 0.25 : t);
    seekDeck(deck, target);
    state.ended = false;
    state.lastSeekAt = Date.now();
    emit("time");
    updatePositionState(true);
    sendNowPlayingSoon();
    speakerStateSoon();
  }

  function skipBy(delta) {
    seek(currentPosition() + delta);
  }

  function currentPosition() {
    if (remote.target) return remoteView().position;
    return activeDeck().el.currentTime || 0;
  }

  function setShuffle(on) {
    on = !!on;
    if (state.shuffle === on) return;
    state.shuffle = on;
    const current = currentItem();
    if (on) {
      state.original = state.queue.slice();
      if (current) {
        const rest = state.queue.filter((i) => i !== current);
        state.queue = [current, ...shuffled(rest)];
        state.index = 0;
      } else {
        state.queue = shuffled(state.queue);
      }
    } else if (state.original) {
      // Back to the order it was in, keeping anything added since at the end.
      const kept = new Set(state.queue);
      const back = state.original.filter((i) => kept.has(i));
      const added = state.queue.filter((i) => !state.original.includes(i));
      state.queue = back.concat(added);
      state.index = current ? state.queue.indexOf(current) : -1;
      state.original = null;
    }
    invalidatePreload();
    for (const deck of decks) if (deck.item) deck.rg = replayGainFor(deck.item);
    applyLevels();
    emit("queue");
    emit("state");
    sendNowPlayingSoon();
  }

  function shuffled(list) {
    const copy = list.slice();
    for (let i = copy.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [copy[i], copy[j]] = [copy[j], copy[i]];
    }
    return copy;
  }

  // The green tint was the only sign of shuffle/repeat state; say it to screen readers and in the
  // tooltip too ("Repeat: one").
  function labelModes(shuffle, rep, isRemote) {
    if (shuffle) {
      const on = state.shuffle && !isRemote;
      shuffle.setAttribute("aria-pressed", on ? "true" : "false");
      shuffle.title = on ? "Shuffle: on" : "Shuffle: off";
    }
    if (rep) {
      const mode = isRemote ? "off" : state.repeat;
      rep.setAttribute("aria-pressed", mode !== "off" ? "true" : "false");
      rep.title = mode === "one" ? "Repeat: this song" : mode === "all" ? "Repeat: all" : "Repeat: off";
      rep.setAttribute("aria-label", rep.title);
    }
  }

  function cycleRepeat() {
    state.repeat = state.repeat === "off" ? "all" : state.repeat === "all" ? "one" : "off";
    lsSet("repeat", state.repeat);
    invalidatePreload();
    emit("state");
    sendNowPlayingSoon();
  }

  // A queued next item that no longer is next (reorder, remove, shuffle) must not play.
  function invalidatePreload() {
    const idle = idleDeck();
    if (idle.preloaded) unloadDeck(idle);
  }

  // Speed: per show for podcasts (saved to the show's my-settings, so every device uses it),
  // one remembered speed for music.
  let speedSaveTimer = null;
  function setSpeed(value) {
    const v = clamp(Math.round(Number(value) * 100) / 100 || 1, 0.5, 3);
    const item = currentItem();
    const deck = activeDeck();
    deck.el.playbackRate = v;
    deck.el.defaultPlaybackRate = v;
    if (item && item.type === "episode") {
      const podcastId = item.raw && item.raw.podcastId;
      const show = podcastId && shows.get(podcastId);
      if (show) {
        show.mySettings = { ...(show.mySettings || {}), playbackSpeed: v };
        clearTimeout(speedSaveTimer);
        speedSaveTimer = setTimeout(() => {
          request(`/api/podcasts/${enc(podcastId)}/my-settings`, { method: "PUT", body: show.mySettings }).then((res) => {
            if (res.ok && res.data) shows.set(podcastId, res.data);
          });
        }, 900);
      } else {
        settings.podcastSpeed = v;
        saveSetting("podcastSpeed");
      }
    } else {
      settings.musicSpeed = v;
      saveSetting("musicSpeed");
    }
    emit("speed");
    sendNowPlayingSoon();
  }

  function currentSpeed() {
    return activeDeck().el.playbackRate || 1;
  }

  // ---- sleep timer ----------------------------------------------------------------------------

  // Minutes, or the end of this track/episode; either way the last 10 s fade out.
  function setSleep(minutes) {
    endSleep(false);
    if (minutes === "end") {
      state.sleep = { mode: "end" };
    } else if (Number(minutes) > 0) {
      const ms = Number(minutes) * 60000;
      const until = Date.now() + ms;
      state.sleep = { mode: "minutes", until, minutes: Number(minutes) };
      state.sleep.timer = setTimeout(() => startSleepFade(SLEEP_FADE_SECONDS), Math.max(0, ms - SLEEP_FADE_SECONDS * 1000));
    }
    emit("sleep");
  }

  function startSleepFade(seconds) {
    if (!state.sleep || state.sleep.fading) return;
    state.sleep.fading = true;
    rampLevel("fade", 0, seconds, () => {
      const deck = activeDeck();
      deck.el.pause();
      endSleep(true);
      emit("state");
    });
  }

  function endSleep(fired) {
    if (!state.sleep) return;
    clearTimeout(state.sleep.timer);
    const wasFading = state.sleep.fading;
    state.sleep = null;
    if (wasFading || fired) {
      // Put the level back for next time, after the pause has landed.
      setTimeout(() => rampLevel("fade", 1, 0.05), 150);
    }
    emit("sleep");
  }

  function sleepLabel() {
    if (!state.sleep) return "";
    if (state.sleep.mode === "end") return "end of " + (isEpisodeNow() ? "episode" : "track");
    const left = Math.max(0, state.sleep.until - Date.now());
    return `${Math.ceil(left / 60000)} min`;
  }

  // ---- loading things to play -----------------------------------------------------------------

  // tracks: Track objects. context: { type, id, name }. opts: { shuffle, position, radio }
  function playTracks(tracks, start = 0, context = null, opts = {}) {
    const list = (tracks || []).filter((t) => t && t.id);
    if (!list.length) { showToast("Nothing to play here.", false); return; }
    loadQueue(list.map(trackItem), start, context, opts);
  }

  function playEpisodes(episodes, start = 0, context = null, opts = {}) {
    const list = (episodes || []).filter((e) => e && e.id);
    if (!list.length) { showToast("Nothing to play here.", false); return; }
    loadQueue(list.map(episodeItem), start, context, opts);
  }

  // While this browser is controlling a speaker, whatever is picked plays there instead.
  function loadQueue(items, start, context, opts) {
    if (remote.target && !opts.local) { sendQueueToRemote(items, clamp(Number(start) || 0, 0, items.length - 1), context, opts); return; }
    exitRemote(false);
    unlockAudio();
    finishItem("skipped");
    cancelCrossfade();
    for (const deck of decks) if (deck.item) deck.finished = true;
    state.queue = items;
    state.original = null;
    state.context = context || null;
    state.radio = opts.radio || null;
    state.shuffle = false;
    let index = clamp(Number(start) || 0, 0, items.length - 1);
    if (opts.shuffle) {
      state.shuffle = true;
      state.original = items.slice();
      const first = opts.keepStart ? items[index] : items[Math.floor(Math.random() * items.length)];
      state.queue = [first, ...shuffled(items.filter((i) => i !== first))];
      index = 0;
    }
    state.index = -1;
    emit("queue");
    playIndex(index, { noFinish: true, position: opts.position, resume: opts.resume });
    if (opts.openNowPlaying || (isKiosk() && opts.fromRemote)) openNowPlaying(opts.tab);
  }

  // "next" puts them after what's playing, "last" at the end. With nothing loaded they start.
  function enqueue(raws, where, type) {
    const items = (raws || []).filter((r) => r && r.id).map((r) => itemFromRaw(r, type || (r.podcastId ? "episode" : "track")));
    if (!items.length) return;
    if (!currentItem() || remote.target) {
      if (remote.target) exitRemote(false);
      loadQueue(items, 0, { type: "queue", id: null, name: "Queue" }, {});
      return;
    }
    if (where === "next") state.queue.splice(state.index + 1, 0, ...items);
    else state.queue.push(...items);
    if (state.original) state.original.push(...items);
    invalidatePreload();
    emit("queue");
    sendNowPlayingSoon();
    showToast(items.length === 1 ? `${where === "next" ? "Playing next" : "Added to the queue"}: ${items[0].title}` : `${items.length} added to the queue`, true);
  }

  function removeFromQueue(i) {
    if (i < 0 || i >= state.queue.length) return;
    if (i === state.index) {
      // Removing what's playing moves on to whatever was after it.
      const wasPlaying = isPlaying();
      finishItem("skipped");
      state.queue.splice(i, 1);
      if (!state.queue.length) { stop(); return; }
      const nextI = Math.min(i, state.queue.length - 1);
      state.index = -1;
      playIndex(nextI, { noFinish: true, autoplay: wasPlaying });
    } else {
      const [gone] = state.queue.splice(i, 1);
      if (state.original) state.original = state.original.filter((x) => x !== gone);
      if (i < state.index) state.index--;
      invalidatePreload();
      emit("queue");
      sendNowPlayingSoon();
    }
  }

  function moveInQueue(from, to) {
    if (from === to) return;
    const current = currentItem();
    state.queue = moveInArray(state.queue, from, to);
    state.index = current ? state.queue.indexOf(current) : -1;
    invalidatePreload();
    emit("queue");
    sendNowPlayingSoon();
  }

  function clearUpcoming() {
    if (state.index < 0) return;
    state.queue = state.queue.slice(0, state.index + 1);
    if (state.original) state.original = state.original.filter((x) => state.queue.includes(x));
    state.radio = null;
    invalidatePreload();
    emit("queue");
    sendNowPlayingSoon();
  }

  function stop() {
    finishItem("skipped");
    cancelCrossfade();
    for (const deck of decks) unloadDeck(deck);
    active = 0;
    state.queue = [];
    state.original = null;
    state.index = -1;
    state.context = null;
    state.radio = null;
    state.ended = false;
    endSleep(false);
    onItemChanged();
    emit("queue");
    emit("state");
    if (enabledFlags.music || enabledFlags.podcasts) request(`/api/audio/now-playing/${enc(deviceId())}`, { method: "DELETE" });
    closeNowPlaying();
  }

  // Radio: the server's mix, extended endlessly - asked again with everything already queued
  // excluded whenever fewer than five tracks are left.
  async function startRadio(seedType, seedId, name) {
    const params = new URLSearchParams({ seedType, seedId: String(seedId), limit: "50" });
    showToast(`Starting ${name || "radio"}…`, true);
    const res = await request(`/api/music/mix?${params}`);
    if (!res.ok) { showToast(res.error, false); return; }
    const data = res.data || {};
    playTracks(data.tracks || [], 0, { type: "radio", id: `${seedType}:${seedId}`, name: data.name || name || "Radio" }, { radio: { seedType, seedId: String(seedId) } });
  }

  async function playMix(mixId, name) {
    const res = await request(`/api/music/mix?${new URLSearchParams({ mix: mixId })}`);
    if (!res.ok) { showToast(res.error, false); return; }
    const data = res.data || {};
    playTracks(data.tracks || [], 0, { type: "mix", id: mixId, name: data.name || name || "Mix" });
  }

  async function shuffleAll() {
    const res = await request("/api/music/shuffle?limit=200");
    if (!res.ok) { showToast(res.error, false); return; }
    const tracks = Array.isArray(res.data) ? res.data : (res.data && (res.data.tracks || res.data.items)) || [];
    playTracks(tracks, 0, { type: "queue", id: "shuffle", name: "Shuffle all" });
    if (tracks.length) state.shuffle = true;
    emit("state");
  }

  async function extendRadio() {
    if (!state.radio || state.radioLoading) return;
    if (state.queue.length - state.index > 5) return;
    state.radioLoading = true;
    const exclude = state.queue.slice(-200).map((i) => i.id).join(",");
    const params = new URLSearchParams({ seedType: state.radio.seedType, seedId: state.radio.seedId, limit: "25", excludeIds: exclude });
    const radio = state.radio;
    const res = await request(`/api/music/mix?${params}`);
    state.radioLoading = false;
    if (state.radio !== radio || !res.ok || !res.data) return;
    const have = new Set(state.queue.map((i) => i.id));
    const more = (res.data.tracks || []).filter((t) => t && t.id && !have.has(t.id)).map(trackItem);
    if (!more.length) return;
    state.queue.push(...more);
    emit("queue");
  }

  // ---- love and rating ------------------------------------------------------------------------

  async function toggleLove(item) {
    item = item || currentItem();
    if (!item || item.type !== "track" || !item.raw) return;
    const loved = !item.raw.loved;
    item.raw.loved = loved;
    emit("love", item);
    const res = await request(`/api/music/tracks/${enc(item.id)}/love`, { method: "POST", body: { loved } });
    if (!res.ok) { item.raw.loved = !loved; emit("love", item); showToast(res.error, false); return; }
    syncRaw("track", item.id, { loved });
    showToast(loved ? `Loved ${item.title}` : `Removed ${item.title} from Loved`, true);
  }

  async function rateTrack(item, rating) {
    if (!item || item.type !== "track" || !item.raw) return;
    const before = item.raw.rating;
    item.raw.rating = rating;
    emit("love", item);
    const res = await request(`/api/music/tracks/${enc(item.id)}/rating`, { method: "POST", body: { rating } });
    if (!res.ok) { item.raw.rating = before; emit("love", item); showToast(res.error, false); return; }
    syncRaw("track", item.id, { rating });
  }

  // When a page loves/rates a track that is also in the queue, the queue's copy follows.
  function syncRaw(type, id, patch) {
    for (const item of state.queue) if (item.type === type && item.id === id && item.raw) Object.assign(item.raw, patch);
    emit("love", currentItem());
    emit("sync", { type, id, patch });
  }

  // ---- what changed ---------------------------------------------------------------------------

  let lastItemKey = null;
  function onItemChanged() {
    const item = currentItem();
    const key = item ? item.key : null;
    if (key === lastItemKey) { emit("state"); return; }
    lastItemKey = key;
    updateMediaSession();
    markNow();
    emit("item", item);
    emit("queue");
    sendNowPlayingSoon();
    speakerStateSoon();
    if (item && item.type === "track" && enabledFlags.music) {
      request("/api/music/now-playing-track", { method: "POST", body: { trackId: item.id } });
    }
  }

  // Rows anywhere on the page with data-now-id get .au-now while that item is the one playing.
  function markNow(root) {
    const item = remote.target ? null : currentItem();
    const scope = root || document;
    if (!root) for (const n of document.querySelectorAll(".au-now")) n.classList.remove("au-now");
    if (!item) return;
    for (const n of scope.querySelectorAll(`[data-now-id="${CSS.escape(item.id)}"]`)) n.classList.add("au-now");
  }

  // ---- what the screen shows ------------------------------------------------------------------

  // The bar and Now Playing draw from this, whether the music is here or on a speaker this
  // browser is controlling.
  function display() {
    if (remote.target) return remoteView();
    const deck = activeDeck();
    const item = currentItem();
    if (!item) return null;
    return {
      remote: false, item, type: item.type, title: item.title, subtitle: item.subtitle,
      artUrl: item.artUrl, playing: isPlaying(), position: deck.el.currentTime || 0,
      duration: durationOf(deck, item), volume: settings.volume
    };
  }

  // ---- mini bar -------------------------------------------------------------------------------

  let bar = null;
  let rafPending = false;

  function buildBar() {
    bar = el("au-bar");
    bar.innerHTML = `
      <div class="au-continue hidden" id="au-continue"></div>
      <div class="au-bar-main hidden" id="au-bar-main">
        <div class="au-bar-seek" id="au-bar-seek">
          <div class="au-bar-seek-fill" id="au-bar-fill"></div>
          <input type="range" min="0" max="1000" step="1" value="0" id="au-bar-range" aria-label="Seek">
        </div>
        <button type="button" class="au-bar-now" data-pl="open" title="Open Now Playing">
          <span class="au-bar-art" id="au-bar-art"></span>
          <span class="au-bar-titles">
            <span class="au-bar-title" id="au-bar-title"></span>
            <span class="au-bar-sub" id="au-bar-sub"></span>
          </span>
        </button>
        <div class="au-bar-controls">
          <button type="button" class="au-ctl au-only-track au-hide-narrow" data-pl="shuffle" title="Shuffle" aria-label="Shuffle">${ICON.shuffle}</button>
          <button type="button" class="au-ctl au-only-track" data-pl="prev" title="Previous (P)" aria-label="Previous">${ICON.prev}</button>
          <button type="button" class="au-ctl au-only-episode" data-pl="back15" title="Back 15 seconds" aria-label="Back 15 seconds">${ICON.back15}</button>
          <button type="button" class="au-ctl au-ctl-play" data-pl="toggle" title="Play/pause (space)" aria-label="Play">${ICON.play}</button>
          <button type="button" class="au-ctl au-only-episode" data-pl="fwd30" title="Forward 30 seconds" aria-label="Forward 30 seconds">${ICON.fwd30}</button>
          <button type="button" class="au-ctl" data-pl="next" title="Next (N)" aria-label="Next">${ICON.next}</button>
          <button type="button" class="au-ctl au-only-track au-hide-narrow" data-pl="repeat" title="Repeat" aria-label="Repeat">${ICON.repeat}</button>
        </div>
        <div class="au-bar-time" id="au-bar-time"></div>
        <div class="au-bar-side">
          <span class="au-remote-badge hidden" id="au-bar-remote"></span>
          <span class="au-sleep-badge hidden" id="au-bar-sleep"></span>
          <button type="button" class="au-ctl au-ctl-sm au-only-track au-hide-narrow" data-pl="love" title="Love (L)" aria-label="Love">${ICON.heart}</button>
          <button type="button" class="au-ctl au-ctl-sm au-hide-narrow" data-pl="words" title="Lyrics / transcript" aria-label="Lyrics or transcript">${ICON.lyrics}</button>
          <button type="button" class="au-ctl au-ctl-sm au-hide-narrow" data-pl="queue" title="Queue" aria-label="Queue">${ICON.queue}</button>
          <button type="button" class="au-ctl au-ctl-sm" data-pl="cast" title="Play on… another speaker" aria-label="Play on another speaker">${ICON.cast}</button>
          <span class="au-vol au-hide-narrow">
            <button type="button" class="au-ctl au-ctl-sm" data-pl="mute" title="Mute" aria-label="Mute">${ICON.volume}</button>
            <input type="range" min="0" max="100" step="1" class="au-vol-range" id="au-bar-vol" aria-label="Volume">
          </span>
          <button type="button" class="au-ctl au-ctl-sm au-hide-narrow" data-pl="settings" title="Player settings" aria-label="Player settings">${ICON.more}</button>
        </div>
      </div>`;
    bar.addEventListener("click", (e) => {
      const b = e.target.closest("[data-pl]");
      if (b) playerAction(b.dataset.pl, b, e);
    });
    const range = el("au-bar-range");
    range.addEventListener("input", () => {
      const d = display();
      if (!d || !d.duration) return;
      seekPreview = (Number(range.value) / 1000) * d.duration;
      drawTime();
    });
    range.addEventListener("change", () => {
      const d = display();
      if (d && d.duration) seek((Number(range.value) / 1000) * d.duration);
      seekPreview = null;
    });
    const vol = el("au-bar-vol");
    vol.addEventListener("input", () => volumeInput(Number(vol.value) / 100));
    renderBar();
  }

  let seekPreview = null;

  function volumeInput(v) {
    if (remote.target) {
      remote.target.volume = v;
      if (remote.target.state) remote.target.state.volume = v;
      clearTimeout(remote.volTimer);
      remote.volTimer = setTimeout(() => remoteCommand({ action: "volume", volume: v }), 250);
      return;
    }
    setVolume(v);
  }

  function renderBar() {
    if (!bar) return;
    const d = display();
    const main = el("au-bar-main");
    const show = !!d;
    main.classList.toggle("hidden", !show);
    document.body.classList.toggle("au-has-bar", show || !el("au-continue").classList.contains("hidden"));
    bar.classList.toggle("hidden", !show && el("au-continue").classList.contains("hidden"));
    if (!d) return;
    bar.classList.toggle("au-mode-episode", d.type === "episode");
    bar.classList.toggle("au-mode-remote", !!d.remote);
    const art = el("au-bar-art");
    const src = artSized(d.artUrl, 120);
    const glyph = d.type === "episode" ? ICON.mic : ICON.note;
    if (art.dataset.src !== (src || "")) {
      art.dataset.src = src || "";
      art.innerHTML = `<span class="au-bar-art-glyph">${glyph}</span>${src ? `<img src="${escapeHtml(src)}" alt="">` : ""}`;
      wireImages(art);
    }
    el("au-bar-title").textContent = d.title || "";
    el("au-bar-sub").textContent = d.subtitle || "";
    const remoteBadge = el("au-bar-remote");
    remoteBadge.classList.toggle("hidden", !d.remote);
    if (d.remote) remoteBadge.innerHTML = `${ICON.cast}<span>${escapeHtml(remote.target.name)}${remote.target.online === false ? " (offline)" : ""}</span>`;
    renderBarState();
    drawTime();
  }

  function renderBarState() {
    if (!bar) return;
    const d = display();
    const play = bar.querySelector('[data-pl="toggle"]');
    if (play) {
      const playing = !!(d && d.playing);
      play.innerHTML = playing ? ICON.pause : ICON.play;
      play.setAttribute("aria-label", playing ? "Pause" : "Play");
    }
    bar.querySelector('[data-pl="shuffle"]').classList.toggle("on", state.shuffle && !remote.target);
    const rep = bar.querySelector('[data-pl="repeat"]');
    rep.classList.toggle("on", state.repeat !== "off" && !remote.target);
    rep.innerHTML = state.repeat === "one" ? ICON.repeat1 : ICON.repeat;
    labelModes(bar.querySelector('[data-pl="shuffle"]'), rep, !!remote.target);
    const item = remote.target ? null : currentItem();
    const love = bar.querySelector('[data-pl="love"]');
    love.classList.add("au-love");
    const loved = !!(item && item.raw && item.raw.loved);
    love.classList.toggle("on", loved);
    love.innerHTML = loved ? ICON.heartFill : ICON.heart;
    love.disabled = !item || item.type !== "track";
    bar.querySelector('[data-pl="cast"]').classList.toggle("on", !!remote.target);
    const vol = el("au-bar-vol");
    const v = d ? d.volume : settings.volume;
    if (document.activeElement !== vol) vol.value = String(Math.round((v ?? 0) * 100));
    bar.querySelector('[data-pl="mute"]').innerHTML = settings.muted || !v ? ICON.mute : ICON.volume;
    const sleep = el("au-bar-sleep");
    sleep.classList.toggle("hidden", !state.sleep);
    if (state.sleep) sleep.innerHTML = `${ICON.moon}<span>${escapeHtml(sleepLabel())}</span>`;
    document.body.classList.toggle("au-is-playing", !!(d && d.playing));
  }

  function drawTime() {
    if (!bar) return;
    const d = display();
    if (!d) return;
    const pos = seekPreview != null ? seekPreview : d.position;
    const pct = d.duration ? clamp(pos / d.duration, 0, 1) : 0;
    el("au-bar-fill").style.width = `${(pct * 100).toFixed(2)}%`;
    const range = el("au-bar-range");
    if (document.activeElement !== range || seekPreview == null) range.value = String(Math.round(pct * 1000));
    el("au-bar-time").textContent = d.duration ? `${fmtClock(pos)} / ${fmtClock(d.duration)}` : fmtClock(pos);
    if (npOpen) drawNpTime(d, pos);
  }

  function scheduleDraw() {
    if (rafPending) return;
    rafPending = true;
    requestAnimationFrame(() => {
      rafPending = false;
      drawTime();
      if (npOpen) syncWords();
    });
  }

  // ---- buttons ----------------------------------------------------------------------------------

  function playerAction(act, button, e) {
    unlockAudio();
    switch (act) {
      case "open": openNowPlaying(); break;
      case "toggle": togglePlay(); break;
      case "prev": previous(); break;
      case "next": next(); break;
      case "back15": skipBy(-15); break;
      case "fwd30": skipBy(30); break;
      case "shuffle": if (!remote.target) setShuffle(!state.shuffle); break;
      case "repeat": if (!remote.target) cycleRepeat(); break;
      case "love": toggleLove(); break;
      case "words": openNowPlaying(isEpisodeNow() ? "transcript" : "lyrics"); break;
      case "queue": openNowPlaying("queue"); break;
      case "cast": openSpeakerPicker(button); break;
      case "mute":
        if (remote.target) { volumeInput(remoteView().volume > 0 ? 0 : 0.5); break; }
        settings.muted = !settings.muted;
        applyLevels();
        emit("volume");
        break;
      case "settings": openSettingsPop(button); break;
      case "sleep": openSleepPop(button); break;
      case "speed": openSpeedPop(button); break;
      case "eq": openEqPop(button); break;
      case "close-np": closeNowPlaying(); break;
      case "np-more": openItemMenu(button); break;
      case "bookmark": addBookmarkHere(); break;
      case "clip": openClipMakerHere(); break;
      case "go-album": goToItem("album"); break;
      case "go-artist": goToItem("artist"); break;
      case "go-show": goToItem("show"); break;
      case "go-episode": goToItem("episode"); break;
      case "fullscreen": toggleFullscreen(); break;
      case "speaker-mode": setSpeakerMode(!speaker.on); break;
      default: break;
    }
    if (e) e.stopPropagation();
  }

  function toggleFullscreen() {
    const np = el("au-np");
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else if (np.requestFullscreen) np.requestFullscreen().catch(() => {});
  }

  function goToItem(kind) {
    const item = currentItem();
    if (!item || !item.raw) return;
    let hash = null;
    if (kind === "album" && item.raw.albumId) hash = `#music/album/${enc(item.raw.albumId)}`;
    if (kind === "artist" && item.raw.artistId) hash = `#music/artist/${enc(item.raw.artistId)}`;
    if (kind === "show" && item.raw.podcastId) hash = `#podcasts/show/${enc(item.raw.podcastId)}`;
    if (kind === "episode") hash = `#podcasts/episode/${enc(item.id)}`;
    if (!hash) return;
    closeNowPlaying();
    location.hash = hash;
  }

  // The ⋯ menu for what's playing.
  function openItemMenu(anchor) {
    const item = currentItem();
    if (!item || remote.target) return;
    const items = [];
    if (item.type === "track") {
      items.push({ label: item.raw && item.raw.loved ? "Unlove" : "Love", icon: ICON.heart, run: () => toggleLove() });
      items.push({ label: "Start radio from this", icon: ICON.radio, run: () => startRadio("track", item.id, `${item.title} Radio`) });
      items.push({ label: "Add to playlist…", icon: ICON.plus, run: () => emit("add-to-playlist", [item.raw]) });
      items.push("-");
      if (item.raw && item.raw.albumId) items.push({ label: "Go to album", icon: ICON.note, run: () => goToItem("album") });
      if (item.raw && item.raw.artistId) items.push({ label: "Go to artist", icon: ICON.note, run: () => goToItem("artist") });
      items.push({ label: "Download file", icon: ICON.download, run: () => { location.href = withQuery(`/api/music/tracks/${enc(item.id)}/download`, { codecs: codecs() }); } });
    } else {
      items.push({ label: "Bookmark this moment", icon: ICON.bookmark, run: () => addBookmarkHere() });
      items.push({ label: "Make a clip…", icon: ICON.scissors, run: () => openClipMakerHere() });
      items.push({ label: "Mark played and skip", icon: ICON.check, run: () => markPlayedAndSkip(item) });
      items.push("-");
      items.push({ label: "Go to episode", icon: ICON.mic, run: () => goToItem("episode") });
      items.push({ label: "Go to show", icon: ICON.mic, run: () => goToItem("show") });
    }
    items.push("-");
    items.push({ label: "Stop and clear", icon: ICON.close, danger: true, run: () => stop() });
    openMenu(anchor, items);
  }

  async function markPlayedAndSkip(item) {
    const deck = activeDeck();
    deck.finished = true;
    await request(`/api/podcasts/episodes/${enc(item.id)}/played`, { method: "POST", body: { played: true } });
    next();
  }

  function addBookmarkHere() {
    const item = currentItem();
    if (!item || item.type !== "episode") return;
    emit("bookmark", { episode: item.raw, positionSeconds: activeDeck().el.currentTime || 0 });
  }

  function openClipMakerHere() {
    const item = currentItem();
    if (!item || item.type !== "episode") return;
    emit("clip", { episode: item.raw, positionSeconds: activeDeck().el.currentTime || 0 });
  }

  // ---- Now Playing ----------------------------------------------------------------------------

  let npOpen = false;
  let npTab = "queue";
  let npBuilt = false;

  function buildNp() {
    const np = el("au-np");
    np.innerHTML = `
      <div class="au-np-bg" id="au-np-bg"></div>
      <div class="au-np-top">
        <button type="button" class="au-ctl" data-pl="close-np" title="Close (Esc)" aria-label="Close Now Playing">${ICON.collapse}</button>
        <div class="au-np-context" id="au-np-context"></div>
        <div class="au-np-top-actions">
          <button type="button" class="au-ctl au-ctl-sm" data-pl="fullscreen" title="Full screen" aria-label="Full screen">${ICON.expand}</button>
          <button type="button" class="au-ctl au-ctl-sm" data-pl="cast" title="Play on… another speaker" aria-label="Play on another speaker">${ICON.cast}</button>
          <button type="button" class="au-ctl au-ctl-sm" data-pl="settings" title="Player settings" aria-label="Player settings">${ICON.more}</button>
        </div>
      </div>
      <div class="au-np-main">
        <div class="au-np-left">
          <div class="au-np-art" id="au-np-art"></div>
          <div class="au-np-titles">
            <div class="au-np-title" id="au-np-title"></div>
            <div class="au-np-sub" id="au-np-sub"></div>
            <div class="au-np-chapter hidden" id="au-np-chapter"></div>
          </div>
          <div class="au-np-meta" id="au-np-meta"></div>
          <div class="au-np-seek">
            <input type="range" min="0" max="1000" step="1" value="0" id="au-np-range" aria-label="Seek">
            <div class="au-np-times"><span id="au-np-pos">0:00</span><span id="au-np-dur">0:00</span></div>
          </div>
          <div class="au-np-controls">
            <button type="button" class="au-ctl au-only-track" data-pl="shuffle" title="Shuffle" aria-label="Shuffle">${ICON.shuffle}</button>
            <button type="button" class="au-ctl au-only-episode" data-pl="back15" title="Back 15 seconds" aria-label="Back 15 seconds">${ICON.back15}</button>
            <button type="button" class="au-ctl au-ctl-lg" data-pl="prev" title="Previous" aria-label="Previous">${ICON.prev}</button>
            <button type="button" class="au-ctl au-ctl-play au-ctl-xl" data-pl="toggle" title="Play/pause" aria-label="Play">${ICON.play}</button>
            <button type="button" class="au-ctl au-ctl-lg" data-pl="next" title="Next" aria-label="Next">${ICON.next}</button>
            <button type="button" class="au-ctl au-only-episode" data-pl="fwd30" title="Forward 30 seconds" aria-label="Forward 30 seconds">${ICON.fwd30}</button>
            <button type="button" class="au-ctl au-only-track" data-pl="repeat" title="Repeat" aria-label="Repeat">${ICON.repeat}</button>
          </div>
          <div class="au-np-tools">
            <span class="au-vol">
              <button type="button" class="au-ctl au-ctl-sm" data-pl="mute" title="Mute" aria-label="Mute">${ICON.volume}</button>
              <input type="range" min="0" max="100" step="1" class="au-vol-range" id="au-np-vol" aria-label="Volume">
            </span>
            <button type="button" class="au-pill" data-pl="speed" id="au-np-speed" title="Playback speed">${ICON.speed}<span>1×</span></button>
            <button type="button" class="au-pill" data-pl="sleep" id="au-np-sleep" title="Sleep timer">${ICON.moon}<span>sleep</span></button>
            <button type="button" class="au-pill au-local-only" data-pl="eq" id="au-np-eq" title="Equalizer">${ICON.eq}<span>EQ</span></button>
          </div>
          <div class="au-np-remote hidden" id="au-np-remote"></div>
        </div>
        <div class="au-np-right">
          <nav class="au-np-tabs" id="au-np-tabs" role="tablist"></nav>
          <div class="au-np-panel au-scroll" id="au-np-panel"></div>
        </div>
      </div>`;
    np.addEventListener("click", (e) => {
      const b = e.target.closest("[data-pl]");
      if (b) { playerAction(b.dataset.pl, b, e); return; }
      const tab = e.target.closest("[data-np-tab]");
      if (tab) { setNpTab(tab.dataset.npTab); return; }
      const act = e.target.closest("[data-np]");
      if (act) npPanelAction(act.dataset.np, act, e);
    });
    np.addEventListener("input", (e) => {
      if (e.target.id === "au-tr-search") transcriptSearch(e.target.value);
    });
    np.addEventListener("keydown", (e) => {
      if (e.target.id === "au-tr-search" && e.key === "Enter") { e.preventDefault(); transcriptJump(e.shiftKey ? -1 : 1); }
    });
    const range = el("au-np-range");
    range.addEventListener("input", () => {
      const d = display();
      if (!d || !d.duration) return;
      seekPreview = (Number(range.value) / 1000) * d.duration;
      drawTime();
    });
    range.addEventListener("change", () => {
      const d = display();
      if (d && d.duration) seek((Number(range.value) / 1000) * d.duration);
      seekPreview = null;
    });
    const vol = el("au-np-vol");
    vol.addEventListener("input", () => volumeInput(Number(vol.value) / 100));
    const panel = el("au-np-panel");
    const userScrolled = () => { words.userScrollAt = Date.now(); };
    panel.addEventListener("wheel", userScrolled, { passive: true });
    panel.addEventListener("touchmove", userScrolled, { passive: true });
    makeSortable(panel, (from, to) => {
      // Rows in the Up Next list are numbered by their place in the whole queue.
      const rows = Array.from(panel.querySelectorAll("[data-sort]"));
      const qFrom = Number(rows[from] && rows[from].dataset.qi);
      const qTo = Number(rows[to] && rows[to].dataset.qi);
      if (isFinite(qFrom) && isFinite(qTo)) moveInQueue(qFrom, qTo);
    });
    npBuilt = true;
  }

  function openNowPlaying(tab) {
    if (!display()) return;
    if (!npBuilt) buildNp();
    npOpen = true;
    const np = el("au-np");
    np.classList.remove("hidden");
    np.setAttribute("aria-hidden", "false");
    document.body.classList.add("au-np-open");
    const valid = npTabs().map((t) => t.key);
    if (tab && valid.includes(tab)) npTab = tab;
    else if (isKiosk() && valid.includes("lyrics")) npTab = "lyrics";
    if (!valid.includes(npTab)) npTab = valid[0];
    renderNp();
  }

  function closeNowPlaying() {
    if (!npOpen) return;
    npOpen = false;
    const np = el("au-np");
    np.classList.add("hidden");
    np.setAttribute("aria-hidden", "true");
    document.body.classList.remove("au-np-open");
    if (document.fullscreenElement === np) document.exitFullscreen().catch(() => {});
  }

  function npTabs() {
    const d = display();
    if (!d) return [];
    if (d.remote) return [{ key: "remote", label: "Speaker" }, { key: "queue", label: "Up next" }];
    if (d.type === "episode") {
      const raw = d.item.raw || {};
      return [
        { key: "queue", label: "Up next" },
        { key: "chapters", label: "Chapters", dim: raw.hasChapters === false },
        { key: "transcript", label: "Transcript", dim: raw.hasTranscript === false },
        { key: "info", label: "Info" }
      ];
    }
    return [{ key: "queue", label: "Up next" }, { key: "lyrics", label: "Lyrics" }, { key: "info", label: "Info" }];
  }

  function setNpTab(tab) {
    npTab = tab;
    renderNpTabs();
    renderNpPanel();
  }

  function renderNp() {
    if (!npOpen) return;
    const d = display();
    if (!d) { closeNowPlaying(); return; }
    const np = el("au-np");
    np.classList.toggle("au-mode-episode", d.type === "episode");
    np.classList.toggle("au-mode-remote", !!d.remote);
    const src = artSized(d.artUrl, 800);
    const artBox = el("au-np-art");
    if (artBox.dataset.src !== (src || "")) {
      artBox.dataset.src = src || "";
      artBox.innerHTML = `<div class="au-np-art-glyph">${d.type === "episode" ? ICON.mic : ICON.note}</div>${src ? `<img src="${escapeHtml(src)}" alt="">` : ""}`;
      el("au-np-bg").innerHTML = src ? `<img src="${escapeHtml(src)}" alt="">` : "";
      wireImages(artBox);
      wireImages(el("au-np-bg"));
    }
    const item = d.item;
    el("au-np-title").textContent = d.title || "";
    const sub = el("au-np-sub");
    if (item && item.type === "track" && item.raw) {
      sub.innerHTML = `${item.raw.artistId ? `<button type="button" class="au-link" data-pl="go-artist">${escapeHtml(item.subtitle)}</button>` : escapeHtml(item.subtitle)}${item.album ? ` <span class="mv-dot">&middot;</span> ${item.raw.albumId ? `<button type="button" class="au-link" data-pl="go-album">${escapeHtml(item.album)}</button>` : escapeHtml(item.album)}` : ""}`;
    } else if (item && item.type === "episode") {
      sub.innerHTML = `<button type="button" class="au-link" data-pl="go-show">${escapeHtml(item.subtitle)}</button>${item.raw && item.raw.publishedAt ? ` <span class="mv-dot">&middot;</span> ${escapeHtml(fmtDate(item.raw.publishedAt))}` : ""}`;
    } else {
      sub.textContent = d.subtitle || "";
    }
    const ctx = el("au-np-context");
    if (d.remote) ctx.innerHTML = `<span class="au-np-kicker">Playing on</span> ${escapeHtml(remote.target.name)}`;
    else if (state.context && state.context.name) ctx.innerHTML = `<span class="au-np-kicker">Playing from ${escapeHtml(contextWord(state.context.type))}</span> ${escapeHtml(state.context.name)}`;
    else ctx.innerHTML = `<span class="au-np-kicker">Now playing</span>`;
    renderNpMeta();
    renderNpState();
    renderNpTabs();
    renderNpPanel();
    drawTime();
  }

  function contextWord(type) {
    return { album: "album", playlist: "playlist", artist: "artist", mix: "mix", podcast: "show", queue: "", search: "search", radio: "radio" }[type] || "";
  }

  function renderNpMeta() {
    const meta = el("au-np-meta");
    if (!meta) return;
    const d = display();
    if (!d || d.remote) { meta.innerHTML = ""; return; }
    const item = d.item;
    if (item.type === "track") {
      const loved = !!(item.raw && item.raw.loved);
      meta.innerHTML = `<button type="button" class="au-ctl au-ctl-sm au-love ${loved ? "on" : ""}" data-pl="love" title="Love (L)" aria-label="Love">${loved ? ICON.heartFill : ICON.heart}</button>
        ${stars(item.raw && item.raw.rating, "np-rate", item.id).replace(/data-act=/g, "data-np=")}
        <button type="button" class="au-ctl au-ctl-sm" data-pl="np-more" title="More" aria-label="More">${ICON.more}</button>`;
    } else {
      meta.innerHTML = `<button type="button" class="au-pill" data-pl="bookmark" title="Bookmark this moment (B)">${ICON.bookmark}<span>bookmark</span></button>
        <button type="button" class="au-pill" data-pl="clip" title="Make a clip to share">${ICON.scissors}<span>clip</span></button>
        <button type="button" class="au-ctl au-ctl-sm" data-pl="np-more" title="More" aria-label="More">${ICON.more}</button>`;
    }
  }

  function renderNpState() {
    if (!npOpen) return;
    const d = display();
    if (!d) return;
    const np = el("au-np");
    const play = np.querySelector('[data-pl="toggle"]');
    play.innerHTML = d.playing ? ICON.pause : ICON.play;
    play.setAttribute("aria-label", d.playing ? "Pause" : "Play");
    np.querySelector('.au-np-controls [data-pl="shuffle"]').classList.toggle("on", state.shuffle && !d.remote);
    const rep = np.querySelector('.au-np-controls [data-pl="repeat"]');
    rep.classList.toggle("on", state.repeat !== "off" && !d.remote);
    rep.innerHTML = state.repeat === "one" ? ICON.repeat1 : ICON.repeat;
    labelModes(np.querySelector('.au-np-controls [data-pl="shuffle"]'), rep, !!d.remote);
    const vol = el("au-np-vol");
    if (document.activeElement !== vol) vol.value = String(Math.round((d.volume ?? 0) * 100));
    np.querySelector('.au-np-tools [data-pl="mute"]').innerHTML = settings.muted || !d.volume ? ICON.mute : ICON.volume;
    el("au-np-speed").querySelector("span").textContent = `${Number(currentSpeed().toFixed(2))}×`;
    el("au-np-speed").classList.toggle("on", Math.abs(currentSpeed() - 1) > 0.001);
    el("au-np-sleep").querySelector("span").textContent = state.sleep ? sleepLabel() : "sleep";
    el("au-np-sleep").classList.toggle("on", !!state.sleep);
    el("au-np-eq").classList.toggle("on", settings.eq.enabled);
    np.querySelector('.au-np-top [data-pl="cast"]').classList.toggle("on", !!d.remote);
    const remoteBox = el("au-np-remote");
    remoteBox.classList.toggle("hidden", !d.remote);
    if (d.remote) remoteBox.innerHTML = `${ICON.cast}<span>Controlling <b>${escapeHtml(remote.target.name)}</b>${remote.target.online === false ? " &middot; offline" : ""}</span><button type="button" class="mv-btn mv-btn-quiet" data-np="remote-leave">play here instead</button>`;
    renderNpMeta();
  }

  function drawNpTime(d, pos) {
    const range = el("au-np-range");
    if (!range) return;
    const pct = d.duration ? clamp(pos / d.duration, 0, 1) : 0;
    if (document.activeElement !== range || seekPreview == null) range.value = String(Math.round(pct * 1000));
    range.style.setProperty("--au-pct", `${(pct * 100).toFixed(2)}%`);
    el("au-np-pos").textContent = fmtClock(pos);
    el("au-np-dur").textContent = d.duration ? `-${fmtClock(Math.max(0, d.duration - pos))}` : "";
    const ch = currentChapter(pos);
    const chEl = el("au-np-chapter");
    chEl.classList.toggle("hidden", !ch);
    if (ch && chEl.dataset.t !== String(ch.startSeconds)) { chEl.dataset.t = String(ch.startSeconds); chEl.textContent = ch.title || ""; }
  }

  function renderNpTabs() {
    const nav = el("au-np-tabs");
    if (!nav) return;
    const tabs = npTabs();
    if (!tabs.some((t) => t.key === npTab)) npTab = tabs[0] ? tabs[0].key : "queue";
    nav.innerHTML = tabs.map((t) => `<button type="button" class="au-np-tab ${t.key === npTab ? "active" : ""} ${t.dim ? "dim" : ""}" data-np-tab="${t.key}" role="tab" aria-selected="${t.key === npTab}">${escapeHtml(t.label)}</button>`).join("");
  }

  function renderNpPanel() {
    if (!npOpen) return;
    const panel = el("au-np-panel");
    words.kind = null;
    switch (npTab) {
      case "queue": panel.innerHTML = queueMarkup(); wireImages(panel); markNow(panel); break;
      case "lyrics": renderLyrics(panel); break;
      case "transcript": renderTranscript(panel); break;
      case "chapters": renderChapters(panel); break;
      case "info": panel.innerHTML = infoMarkup(); break;
      case "remote": panel.innerHTML = remoteInfoMarkup(); wireImages(panel); break;
      default: panel.innerHTML = "";
    }
  }

  function queueRow(item, qi, cls, sortable) {
    const art = safeSrc(artSized(item.artUrl, 120));
    return `<div class="au-q-row ${cls}" ${sortable ? 'data-sort="1"' : ""} data-qi="${qi}" data-now-id="${escapeHtml(item.id)}">
      ${sortable ? `<span class="au-drag" title="Drag to reorder" aria-hidden="true">${ICON.drag}</span>` : '<span class="au-drag-space"></span>'}
      <button type="button" class="au-q-main" data-np="q-play" data-qi="${qi}">
        <span class="au-q-art">${art ? `<img src="${escapeHtml(art)}" alt="" loading="lazy">` : (item.type === "episode" ? ICON.mic : ICON.note)}</span>
        <span class="au-q-text"><span class="au-q-title">${escapeHtml(item.title)}</span><span class="au-q-sub">${escapeHtml(item.subtitle)}</span></span>
      </button>
      <span class="au-q-dur">${item.durationSeconds ? fmtClock(item.durationSeconds) : ""}</span>
      ${cls === "au-q-current" ? "" : `<button type="button" class="au-ctl au-ctl-xs" data-np="q-remove" data-qi="${qi}" title="Remove from queue" aria-label="Remove">${ICON.close}</button>`}
    </div>`;
  }

  function queueMarkup() {
    if (remote.target) {
      return `<div class="mv-empty">This speaker's own queue isn't shown here. Use the controls to skip, or pick something in Music or Podcasts and it plays there.</div>`;
    }
    const item = currentItem();
    if (!item) return emptyMarkup("Nothing queued", "");
    const upcoming = state.queue.slice(state.index + 1);
    const history = state.queue.slice(0, state.index);
    const left = upcoming.reduce((s, i) => s + (i.durationSeconds || 0), 0);
    return `<div class="au-q-head"><span class="au-q-label">Now playing</span></div>
      <div class="au-q-list">${queueRow(item, state.index, "au-q-current", false)}</div>
      <div class="au-q-head"><span class="au-q-label">Up next${upcoming.length ? ` <span class="au-q-count">${plural(upcoming.length, "item")}${left ? ` &middot; ${fmtDuration(left)}` : ""}</span>` : ""}</span>
        <span class="au-q-actions">
          ${state.radio ? `<span class="au-chip">${ICON.radio} endless radio</span>` : ""}
          ${upcoming.length ? `<button type="button" class="mv-btn mv-btn-quiet" data-np="q-clear">clear</button>` : ""}
        </span></div>
      ${upcoming.length ? `<div class="au-q-list au-q-sortable">${upcoming.map((it, i) => queueRow(it, state.index + 1 + i, "", true)).join("")}</div>`
        : `<div class="au-q-empty">${repeatMode() === "all" ? "Repeat is on: the queue starts over." : "Nothing after this. Add songs or episodes with “Play next” or “Add to queue”."}</div>`}
      ${history.length ? `<details class="au-q-history"><summary>Played before (${history.length})</summary><div class="au-q-list">${history.map((it, i) => queueRow(it, i, "au-q-past", false)).join("")}</div></details>` : ""}`;
  }

  function infoMarkup() {
    const item = currentItem();
    if (!item || !item.raw) return "";
    const r = item.raw;
    const rows = [];
    if (item.type === "track") {
      if (r.album) rows.push(["Album", escapeHtml(r.album) + (r.year ? ` (${escapeHtml(r.year)})` : "")]);
      if (r.albumArtist && r.albumArtist !== r.artist) rows.push(["Album artist", escapeHtml(r.albumArtist)]);
      if (r.trackNumber) rows.push(["Track", `${escapeHtml(r.trackNumber)}${r.discNumber > 1 ? ` · disc ${escapeHtml(r.discNumber)}` : ""}`]);
      if (r.genres && r.genres.length) rows.push(["Genre", escapeHtml(r.genres.join(", "))]);
      if (r.composer) rows.push(["Composer", escapeHtml(r.composer)]);
      if (r.bpm) rows.push(["BPM", escapeHtml(r.bpm)]);
      const fmt = [String(r.codec || "").toUpperCase(), r.bitDepth ? `${r.bitDepth}-bit` : "", r.sampleRate ? `${(r.sampleRate / 1000).toFixed(1)} kHz` : "", r.bitrate ? `${Math.round(r.bitrate / 1000)} kbps` : "", r.channels === 1 ? "mono" : ""].filter(Boolean).join(" · ");
      if (fmt) rows.push(["Format", escapeHtml(fmt)]);
      if (r.sizeBytes) rows.push(["Size", escapeHtml(sizeLabel(r.sizeBytes))]);
      if (r.replayGain && (r.replayGain.trackGain != null || r.replayGain.albumGain != null)) {
        rows.push(["Sound Check", `${r.replayGain.trackGain != null ? `track ${Number(r.replayGain.trackGain).toFixed(1)} dB` : ""}${r.replayGain.albumGain != null ? ` · album ${Number(r.replayGain.albumGain).toFixed(1)} dB` : ""}${settings.soundCheck ? "" : " (off)"}`]);
      }
      rows.push(["Your plays", escapeHtml(String(r.playCount || 0)) + (r.lastPlayedAt ? ` · last ${escapeHtml(ago(r.lastPlayedAt))}` : "")]);
      if (r.path) rows.push(["File", `<code class="mv-path">${escapeHtml(r.path)}</code>`]);
    } else {
      if (r.podcastTitle) rows.push(["Show", escapeHtml(r.podcastTitle)]);
      if (r.publishedAt) rows.push(["Published", escapeHtml(fmtDate(r.publishedAt))]);
      if (r.season || r.episode) rows.push(["Episode", `${r.season ? `Season ${escapeHtml(r.season)} · ` : ""}${r.episode ? `#${escapeHtml(r.episode)}` : ""}`]);
      if (r.durationSeconds) rows.push(["Length", escapeHtml(fmtDuration(r.durationSeconds))]);
      if (r.download && r.download.state === "done") rows.push(["Download", `on the server${r.download.enhanced ? " · enhanced" : ""}${r.download.timeSavedSeconds ? ` · ${escapeHtml(fmtDuration(r.download.timeSavedSeconds))} of silence trimmed` : ""}`]);
      if (r.summary) rows.push(["About", escapeHtml(r.summary)]);
    }
    const speedRow = `<dt>Speed</dt><dd>${Number(currentSpeed().toFixed(2))}× ${item.type === "episode" ? "(remembered for this show)" : ""}</dd>`;
    return `<dl class="mv-file au-info">${rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join("")}${speedRow}</dl>`;
  }

  function npPanelAction(act, target) {
    const qi = Number(target.dataset.qi);
    switch (act) {
      case "q-play": if (isFinite(qi) && qi !== state.index) playIndex(qi, { reason: "skipped" }); break;
      case "q-remove": removeFromQueue(qi); break;
      case "q-clear": clearUpcoming(); break;
      case "np-rate": rateTrack(currentItem(), Number(target.dataset.value)); break;
      case "line": seek(Number(target.dataset.t)); if (!isPlaying()) resume(); break;
      case "chapter": seek(Number(target.dataset.t) + 0.05); if (!isPlaying()) resume(); break;
      case "follow": words.userScrollAt = 0; syncWords(true); break;
      case "tr-next": transcriptJump(1); break;
      case "tr-prev": transcriptJump(-1); break;
      case "tr-bookmark": addBookmarkHere(); break;
      case "remote-leave": takeOverFromRemote(); break;
      case "words-retry": wordsCache.delete(target.dataset.key); renderNpPanel(); break;
      default: break;
    }
  }

  // ---- lyrics, transcript, chapters -----------------------------------------------------------

  const wordsCache = new Map(); // "lyrics:<id>" | "transcript:<id>" | "chapters:<id>" -> { status, data }
  const words = { kind: null, items: [], index: -1, userScrollAt: 0, matches: [], matchAt: -1 };

  async function fetchWords(kind, item) {
    const key = `${kind}:${item.id}`;
    const hit = wordsCache.get(key);
    if (hit) return hit;
    const entry = { status: "loading", data: null, promise: null };
    wordsCache.set(key, entry);
    const url = kind === "lyrics" ? `/api/music/tracks/${enc(item.id)}/lyrics`
      : kind === "transcript" ? `/api/podcasts/episodes/${enc(item.id)}/transcript`
      : `/api/podcasts/episodes/${enc(item.id)}/chapters`;
    entry.promise = request(url).then((res) => {
      if (res.ok) { entry.status = "ok"; entry.data = res.data; }
      else if (res.status === 404) { entry.status = "none"; entry.error = res.error; }
      else { entry.status = "error"; entry.error = res.error; }
      return entry;
    });
    return entry;
  }

  function panelLoading(panel, kind, item) {
    panel.innerHTML = loadingMarkup(kind === "lyrics" ? "finding lyrics" : kind === "transcript" ? "loading the transcript" : "loading chapters");
    const want = item.key;
    fetchWords(kind, item).then((entry) => entry.promise).then(() => {
      const now = currentItem();
      if (npOpen && npTab === kind && now && now.key === want) renderNpPanel();
    });
  }

  function renderLyrics(panel) {
    const item = currentItem();
    if (!item || item.type !== "track") { panel.innerHTML = ""; return; }
    const entry = wordsCache.get(`lyrics:${item.id}`);
    if (!entry || entry.status === "loading") { panelLoading(panel, "lyrics", item); return; }
    if (entry.status !== "ok") {
      panel.innerHTML = entry.status === "none"
        ? emptyMarkup("No lyrics for this one", "None in the file, beside it, or on LRCLIB.", ICON.lyrics)
        : `<div class="mv-empty mv-error">${escapeHtml(entry.error)}<button type="button" class="mv-btn" data-np="words-retry" data-key="lyrics:${escapeHtml(item.id)}">try again</button></div>`;
      return;
    }
    const data = entry.data || {};
    const lines = Array.isArray(data.lines) ? data.lines : [];
    const synced = !!data.synced && lines.some((l) => l.time != null);
    const source = data.source ? `<div class="au-words-source">Lyrics from ${escapeHtml({ sidecar: "a .lrc file", embedded: "the file's tags", lrclib: "LRCLIB" }[data.source] || data.source)}</div>` : "";
    if (synced) {
      panel.innerHTML = `<div class="au-lyrics au-synced">${lines.map((l, i) => `<button type="button" class="au-line ${l.text ? "" : "au-line-gap"}" data-np="line" data-i="${i}" data-t="${Number(l.time) || 0}">${l.text ? escapeHtml(l.text) : "♪"}</button>`).join("")}</div>${source}`;
      words.kind = "lyrics";
      words.items = lines.map((l) => Number(l.time) || 0);
      words.index = -1;
      syncWords(true);
    } else {
      const text = data.plain || lines.map((l) => l.text).join("\n");
      panel.innerHTML = `<div class="au-lyrics au-plain">${escapeHtml(text).replace(/\n/g, "<br>")}</div>${source}`;
    }
  }

  function renderTranscript(panel) {
    const item = currentItem();
    if (!item || item.type !== "episode") { panel.innerHTML = ""; return; }
    const entry = wordsCache.get(`transcript:${item.id}`);
    if (!entry || entry.status === "loading") { panelLoading(panel, "transcript", item); return; }
    if (entry.status !== "ok") {
      panel.innerHTML = entry.status === "none"
        ? emptyMarkup("No transcript yet", "The feed doesn't offer one. An admin can have the server write one from the episode's page (Transcribe).", ICON.transcript)
        : `<div class="mv-empty mv-error">${escapeHtml(entry.error)}<button type="button" class="mv-btn" data-np="words-retry" data-key="transcript:${escapeHtml(item.id)}">try again</button></div>`;
      return;
    }
    const segs = (entry.data && entry.data.segments) || [];
    let lastSpeaker = null;
    const body = segs.map((s, i) => {
      const speakerLabel = s.speaker && s.speaker !== lastSpeaker ? `<span class="au-tr-speaker">${escapeHtml(s.speaker)}</span>` : "";
      lastSpeaker = s.speaker || lastSpeaker;
      return `<button type="button" class="au-line au-tr-seg" data-np="line" data-i="${i}" data-t="${Number(s.start) || 0}">${speakerLabel}<span class="au-tr-time">${fmtClock(s.start)}</span><span class="au-tr-text">${escapeHtml(s.text)}</span></button>`;
    }).join("");
    panel.innerHTML = `<div class="au-tr-bar">
        <label class="mv-search au-tr-search">${ICON.search}<input type="search" class="field-input" id="au-tr-search" placeholder="Search this transcript" autocomplete="off"></label>
        <span class="au-tr-count" id="au-tr-count"></span>
        <button type="button" class="au-ctl au-ctl-xs" data-np="tr-prev" title="Previous match" aria-label="Previous match">${ICON.left}</button>
        <button type="button" class="au-ctl au-ctl-xs" data-np="tr-next" title="Next match" aria-label="Next match">${ICON.right}</button>
        <button type="button" class="au-pill" data-np="follow" title="Jump back to where the episode is">${ICON.down}<span>follow</span></button>
      </div>
      <div class="au-transcript">${body || '<div class="mv-empty">The transcript is empty.</div>'}</div>
      <div class="au-words-source">${entry.data && entry.data.source === "whisper" ? "Transcribed on this server by whisper" : "Transcript from the podcast's feed"}</div>`;
    words.kind = "transcript";
    words.items = segs.map((s) => Number(s.start) || 0);
    words.index = -1;
    words.matches = [];
    words.matchAt = -1;
    syncWords(true);
  }

  function transcriptSearch(q) {
    const panel = el("au-np-panel");
    const rows = Array.from(panel.querySelectorAll(".au-tr-seg"));
    const needle = q.trim().toLowerCase();
    words.matches = [];
    for (const row of rows) {
      const text = row.querySelector(".au-tr-text");
      const plain = text.textContent;
      if (needle && plain.toLowerCase().includes(needle)) {
        words.matches.push(row);
        text.innerHTML = escapeHtml(plain).replace(new RegExp(escapeHtml(needle).replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi"), (m) => `<mark>${m}</mark>`);
      } else if (text.querySelector("mark")) {
        text.textContent = plain;
      }
      row.classList.toggle("au-tr-dim", !!needle && !plain.toLowerCase().includes(needle));
    }
    words.matchAt = -1;
    const count = el("au-tr-count");
    if (count) count.textContent = needle ? `${words.matches.length} found` : "";
    if (words.matches.length) transcriptJump(1);
  }

  function transcriptJump(dir) {
    if (!words.matches.length) return;
    words.matchAt = (words.matchAt + dir + words.matches.length) % words.matches.length;
    const row = words.matches[words.matchAt];
    words.userScrollAt = Date.now();
    for (const r of words.matches) r.classList.remove("au-tr-hit");
    row.classList.add("au-tr-hit");
    row.scrollIntoView({ block: "center", inline: "nearest", behavior: "smooth" });
    const count = el("au-tr-count");
    if (count) count.textContent = `${words.matchAt + 1} of ${words.matches.length}`;
  }

  async function renderChapters(panel) {
    const item = currentItem();
    if (!item || item.type !== "episode") { panel.innerHTML = ""; return; }
    const entry = wordsCache.get(`chapters:${item.id}`);
    if (!entry || entry.status === "loading") { panelLoading(panel, "chapters", item); return; }
    const list = entry.status === "ok" && Array.isArray(entry.data) ? entry.data : [];
    if (!list.length) { panel.innerHTML = emptyMarkup("No chapters", "This episode doesn't mark any.", ICON.chapters); return; }
    panel.innerHTML = `<div class="au-chapters">${list.map((c, i) => {
      const img = safeSrc(c.imageUrl);
      const link = safeLink(c.url);
      return `<div class="au-chapter" data-i="${i}">
        <button type="button" class="au-chapter-main" data-np="chapter" data-t="${Number(c.startSeconds) || 0}">
          ${img ? `<img src="${escapeHtml(img)}" alt="" loading="lazy">` : `<span class="au-chapter-n">${i + 1}</span>`}
          <span class="au-chapter-title">${escapeHtml(c.title || `Chapter ${i + 1}`)}</span>
          <span class="au-chapter-time">${fmtClock(c.startSeconds)}</span>
        </button>
        ${link ? `<a class="au-ctl au-ctl-xs" href="${escapeHtml(link)}" target="_blank" rel="noopener noreferrer" title="Open the chapter's link">${ICON.external}</a>` : ""}
      </div>`;
    }).join("")}</div>`;
    wireImages(panel);
    words.kind = "chapters";
    words.items = list.map((c) => Number(c.startSeconds) || 0);
    words.index = -1;
    syncWords(true);
  }

  function currentChapter(pos) {
    const item = remote.target ? null : currentItem();
    if (!item || item.type !== "episode") return null;
    const entry = wordsCache.get(`chapters:${item.id}`);
    if (!entry) { if (item.raw && item.raw.hasChapters) fetchWords("chapters", item); return null; }
    if (entry.status !== "ok" || !Array.isArray(entry.data)) return null;
    let found = null;
    for (const c of entry.data) if ((Number(c.startSeconds) || 0) <= pos + 0.1) found = c;
    return found;
  }

  // Highlight the line / segment / chapter at the current position and keep it in view, unless
  // someone scrolled the panel themselves in the last few seconds.
  function syncWords(force) {
    if (!npOpen || !words.kind || remote.target) return;
    const t = activeDeck().el.currentTime || 0;
    let idx = -1;
    for (let i = 0; i < words.items.length; i++) { if (words.items[i] <= t + 0.15) idx = i; else break; }
    if (idx === words.index && !force) return;
    const panel = el("au-np-panel");
    const sel = words.kind === "chapters" ? ".au-chapter" : ".au-line";
    const rows = panel.querySelectorAll(sel);
    if (words.index >= 0 && rows[words.index]) rows[words.index].classList.remove("au-current");
    words.index = idx;
    const row = idx >= 0 ? rows[idx] : null;
    if (!row) return;
    row.classList.add("au-current");
    if (words.kind === "lyrics") {
      for (let i = 0; i < rows.length; i++) rows[i].classList.toggle("au-past", i < idx);
    }
    if (Date.now() - words.userScrollAt > 4000 || force) {
      const box = panel.getBoundingClientRect();
      const r = row.getBoundingClientRect();
      const target = panel.scrollTop + (r.top - box.top) - box.height * (words.kind === "lyrics" ? 0.38 : 0.3);
      panel.scrollTo({ top: Math.max(0, target), behavior: force ? "auto" : "smooth" });
    }
  }

  // ---- pop-overs: sleep, speed, equalizer, settings -------------------------------------------

  function openSleepPop(anchor) {
    if (popOpenFor(anchor)) { closePop(); return; }
    const opts = [["off", "Off"], [5, "5 minutes"], [10, "10 minutes"], [15, "15 minutes"], [30, "30 minutes"], [45, "45 minutes"], [60, "1 hour"], [90, "1½ hours"], ["end", isEpisodeNow() ? "End of episode" : "End of track"]];
    const now = state.sleep ? (state.sleep.mode === "end" ? "end" : state.sleep.minutes) : "off";
    openPop(anchor, `<div class="au-pop-title">${ICON.moon} Sleep timer</div>
      ${state.sleep ? `<div class="au-pop-note">Stops in ${escapeHtml(sleepLabel())}, fading out over the last 10 seconds.</div>` : `<div class="au-pop-note">Fades out over the last 10 seconds, then pauses.</div>`}
      <div class="au-pop-list">${opts.map(([v, label]) => `<button type="button" class="au-pop-opt ${String(now) === String(v) ? "active" : ""}" data-v="${v}">${escapeHtml(label)}</button>`).join("")}</div>`, (pop) => {
      pop.addEventListener("click", (e) => {
        const b = e.target.closest("[data-v]");
        if (!b) return;
        setSleep(b.dataset.v === "off" ? 0 : b.dataset.v === "end" ? "end" : Number(b.dataset.v));
        closePop();
      });
    });
  }

  function openSpeedPop(anchor) {
    if (popOpenFor(anchor)) { closePop(); return; }
    const presets = [0.5, 0.75, 0.9, 1, 1.1, 1.2, 1.3, 1.5, 1.75, 2, 2.5, 3];
    const item = currentItem();
    const where = item && item.type === "episode" ? `for ${escapeHtml(item.subtitle || "this show")} (on every device)` : "for music on this browser";
    const draw = (pop) => {
      const cur = currentSpeed();
      pop.querySelector(".au-speed-now").textContent = `${Number(cur.toFixed(2))}×`;
      for (const b of pop.querySelectorAll("[data-v]")) b.classList.toggle("active", Math.abs(Number(b.dataset.v) - cur) < 0.001);
      const r = pop.querySelector("input");
      if (document.activeElement !== r) r.value = String(cur);
    };
    openPop(anchor, `<div class="au-pop-title">${ICON.speed} Speed <span class="au-speed-now"></span></div>
      <div class="au-pop-note">Remembered ${where}. Pitch is kept.</div>
      <input type="range" class="field-range" min="0.5" max="3" step="0.05" aria-label="Speed">
      <div class="au-pop-grid">${presets.map((v) => `<button type="button" class="au-pop-opt" data-v="${v}">${v}×</button>`).join("")}</div>`, (pop) => {
      draw(pop);
      pop.addEventListener("click", (e) => { const b = e.target.closest("[data-v]"); if (b) { setSpeed(Number(b.dataset.v)); draw(pop); } });
      pop.querySelector("input").addEventListener("input", (e) => { setSpeed(Number(e.target.value)); draw(pop); });
    });
  }

  function openEqPop(anchor) {
    if (popOpenFor(anchor)) { closePop(); return; }
    const eq = settings.eq;
    const label = (f) => (f >= 1000 ? `${f / 1000}k` : String(f));
    openPop(anchor, `<div class="au-pop-title">${ICON.eq} Equalizer
        <label class="switch au-pop-switch"><input type="checkbox" id="au-eq-on" ${eq.enabled ? "checked" : ""}><span class="switch-track"></span></label></div>
      <select class="field-input" id="au-eq-preset" aria-label="Preset">
        ${Object.entries(EQ_PRESETS).map(([k, p]) => `<option value="${k}">${escapeHtml(p.name)}</option>`).join("")}<option value="custom">Custom</option>
      </select>
      <div class="au-eq ${eq.enabled ? "" : "au-eq-off"}" id="au-eq-bands">
        ${EQ_BANDS.map((f, i) => `<label class="au-eq-band"><span class="au-eq-db" id="au-eq-db-${i}">${eq.gains[i] > 0 ? "+" : ""}${eq.gains[i]}</span>
          <input type="range" min="-12" max="12" step="1" value="${eq.gains[i]}" data-band="${i}" aria-label="${label(f)} Hz"><span class="au-eq-f">${label(f)}</span></label>`).join("")}
      </div>
      <label class="au-pop-row">Preamp <input type="range" class="field-range" min="-12" max="6" step="1" value="${eq.preamp}" id="au-eq-pre"><span id="au-eq-pre-v">${eq.preamp} dB</span></label>
      <div class="au-pop-note">${graph.ready ? "Saved on this browser." : "Starts working the moment you press play (browsers only allow audio effects after a click)."}</div>
      <div class="au-pop-foot"><button type="button" class="mv-btn mv-btn-quiet" id="au-eq-reset">reset</button></div>`, (pop) => {
      pop.querySelector("#au-eq-preset").value = EQ_PRESETS[eq.preset] ? eq.preset : "custom";
      const save = () => { lsSet("eq", settings.eq); applyEq(); emit("state"); };
      const drawBands = () => {
        pop.querySelector("#au-eq-bands").classList.toggle("au-eq-off", !settings.eq.enabled);
        settings.eq.gains.forEach((g, i) => {
          pop.querySelector(`[data-band="${i}"]`).value = String(g);
          pop.querySelector(`#au-eq-db-${i}`).textContent = `${g > 0 ? "+" : ""}${g}`;
        });
      };
      pop.querySelector("#au-eq-on").addEventListener("change", (e) => { unlockAudio(); settings.eq.enabled = e.target.checked; drawBands(); save(); });
      pop.querySelector("#au-eq-preset").addEventListener("change", (e) => {
        const p = EQ_PRESETS[e.target.value];
        if (!p) return;
        settings.eq.preset = e.target.value;
        settings.eq.gains = p.gains.slice();
        settings.eq.enabled = true;
        pop.querySelector("#au-eq-on").checked = true;
        drawBands();
        save();
      });
      pop.addEventListener("input", (e) => {
        const i = e.target.dataset.band;
        if (i != null) {
          settings.eq.gains[Number(i)] = Number(e.target.value);
          settings.eq.preset = "custom";
          pop.querySelector("#au-eq-preset").value = "custom";
          drawBands();
          save();
        } else if (e.target.id === "au-eq-pre") {
          settings.eq.preamp = Number(e.target.value);
          pop.querySelector("#au-eq-pre-v").textContent = `${settings.eq.preamp} dB`;
          save();
        }
      });
      pop.querySelector("#au-eq-reset").addEventListener("click", () => {
        settings.eq = { enabled: false, preset: "flat", gains: EQ_PRESETS.flat.gains.slice(), preamp: 0 };
        pop.querySelector("#au-eq-on").checked = false;
        pop.querySelector("#au-eq-preset").value = "flat";
        pop.querySelector("#au-eq-pre").value = "0";
        pop.querySelector("#au-eq-pre-v").textContent = "0 dB";
        drawBands();
        save();
      });
    }, "au-pop-eq");
  }

  function openSettingsPop(anchor) {
    if (popOpenFor(anchor)) { closePop(); return; }
    openPop(anchor, `<div class="au-pop-title">${ICON.more} Player</div>
      <label class="au-pop-row au-pop-row-wide"><span>Crossfade <b id="au-xf-v">${settings.crossfade ? settings.crossfade + " s" : "off"}</b></span>
        <input type="range" class="field-range" min="0" max="12" step="1" value="${settings.crossfade}" id="au-xf"></label>
      <div class="au-pop-note">Overlaps songs as one ends. Never between tracks that run on in an album, and never for podcasts.</div>
      <label class="au-pop-check"><span>Sound Check <small>evens out loudness with ReplayGain</small></span>
        <span class="switch"><input type="checkbox" id="au-sc" ${settings.soundCheck ? "checked" : ""}><span class="switch-track"></span></span></label>
      <label class="au-pop-row au-pop-row-wide"><span>Streaming quality</span>
        <select class="field-input" id="au-br">
          <option value="0">Original (lossless when the file is)</option><option value="320">High (320 kbps)</option><option value="192">Medium (192 kbps)</option><option value="128">Data saver (128 kbps)</option><option value="96">Lowest (96 kbps)</option>
        </select></label>
      <label class="au-pop-row au-pop-row-wide"><span>Default podcast speed</span>
        <select class="field-input" id="au-pspeed">${[0.8, 0.9, 1, 1.1, 1.2, 1.25, 1.3, 1.5, 1.75, 2].map((v) => `<option value="${v}">${v}×</option>`).join("")}</select></label>
      <div class="au-pop-sep"></div>
      <label class="au-pop-row au-pop-row-wide"><span>This browser's name</span>
        <input class="field-input" id="au-devname" value="${escapeHtml(baseDeviceName())}" maxlength="60"></label>
      <label class="au-pop-check"><span>Let others play here <small>this browser shows up as a speaker in every app</small></span>
        <span class="switch"><input type="checkbox" id="au-spk" ${speaker.on ? "checked" : ""}><span class="switch-track"></span></span></label>
      <label class="au-pop-check"><span>Allow announcements <small>camera alerts and messages are spoken here</small></span>
        <span class="switch"><input type="checkbox" id="au-ann" ${settings.allowAnnouncements ? "checked" : ""}><span class="switch-track"></span></span></label>
      <div class="au-pop-sep"></div>
      <div class="au-pop-note au-keys">Keys: <kbd>space</kbd> play/pause · <kbd>←</kbd><kbd>→</kbd> seek · <kbd>↑</kbd><kbd>↓</kbd> volume · <kbd>N</kbd>/<kbd>P</kbd> next/previous · <kbd>L</kbd> love · <kbd>B</kbd> bookmark · <kbd>Q</kbd> queue · <kbd>Esc</kbd> close</div>`, (pop) => {
      pop.querySelector("#au-br").value = String(settings.maxBitrate || 0);
      const ps = pop.querySelector("#au-pspeed");
      ps.value = String(settings.podcastSpeed);
      if (ps.value !== String(settings.podcastSpeed)) ps.value = "1";
      pop.querySelector("#au-xf").addEventListener("input", (e) => {
        settings.crossfade = Number(e.target.value);
        saveSetting("crossfade");
        pop.querySelector("#au-xf-v").textContent = settings.crossfade ? `${settings.crossfade} s` : "off";
      });
      pop.querySelector("#au-sc").addEventListener("change", (e) => {
        settings.soundCheck = e.target.checked;
        saveSetting("soundCheck");
        for (const deck of decks) if (deck.item) deck.rg = replayGainFor(deck.item);
        applyLevels(0.3);
      });
      pop.querySelector("#au-br").addEventListener("change", (e) => { settings.maxBitrate = Number(e.target.value); saveSetting("maxBitrate"); });
      ps.addEventListener("change", (e) => { settings.podcastSpeed = Number(e.target.value); saveSetting("podcastSpeed"); });
      pop.querySelector("#au-devname").addEventListener("change", (e) => {
        lsSet("deviceName", e.target.value.trim());
        if (speaker.on) speakerHeartbeat();
        sendNowPlayingSoon();
      });
      pop.querySelector("#au-spk").addEventListener("change", (e) => setSpeakerMode(e.target.checked));
      pop.querySelector("#au-ann").addEventListener("change", (e) => {
        settings.allowAnnouncements = e.target.checked;
        saveSetting("allowAnnouncements");
        if (speaker.on) speakerHeartbeat();
      });
    }, "au-pop-settings");
  }

  // ---- tap to play (autoplay blocked) and announcements ---------------------------------------

  function showTapToPlay() {
    const tap = el("au-tap");
    if (!tap || !currentItem()) return;
    tap.innerHTML = `<button type="button" class="au-tap-btn">${ICON.tap}<span>Tap to start playback</span><small>${escapeHtml(currentItem().title || "")}</small></button>`;
    tap.classList.remove("hidden");
  }

  function hideTapToPlay() {
    const tap = el("au-tap");
    if (tap) tap.classList.add("hidden");
  }

  function showAnnouncement(text, from) {
    const box = el("au-announce");
    if (!box) return;
    box.innerHTML = `<span class="au-announce-icon">${ICON.announce}</span><span class="au-announce-text">${escapeHtml(text)}</span>${from ? `<span class="au-announce-from">${escapeHtml(from)}</span>` : ""}`;
    box.classList.remove("hidden");
  }

  function hideAnnouncement() {
    const box = el("au-announce");
    if (box) box.classList.add("hidden");
  }

  // Speaks with the system voice; resolves when done (or after a generous timeout if the browser
  // never says, or refuses because nothing on the page was clicked yet).
  function speak(text) {
    return new Promise((resolve) => {
      if (!text) { resolve(); return; }
      if (!("speechSynthesis" in window)) { setTimeout(resolve, 2500 + text.length * 60); return; }
      let done = false;
      const finish = () => { if (!done) { done = true; resolve(); } };
      try {
        const u = new SpeechSynthesisUtterance(text);
        u.rate = 1;
        u.onend = finish;
        u.onerror = finish;
        window.speechSynthesis.speak(u);
      } catch { finish(); return; }
      setTimeout(finish, 15000 + text.length * 90);
    });
  }

  // Ducks whatever is playing, speaks, puts the music back.
  async function announce(text, from) {
    showAnnouncement(text, from);
    const playing = isPlaying();
    if (playing) rampLevel("duck", 0.2, 0.4);
    await speak(text);
    if (playing || lv.duck < 1) rampLevel("duck", 1, 0.8);
    setTimeout(hideAnnouncement, 1500);
  }

  // ---- controlling another speaker ("Play on…") -----------------------------------------------

  const remote = { target: null, timer: null, fetchedAt: 0, volTimer: null };

  function remoteView() {
    const t = remote.target;
    const s = (t && t.state) || {};
    const elapsed = s.playing ? (Date.now() - remote.fetchedAt) / 1000 : 0;
    const dur = Number(s.durationSeconds) || 0;
    let pos = (Number(s.positionSeconds) || 0) + elapsed;
    if (dur) pos = Math.min(pos, dur);
    return {
      remote: true, item: null, type: s.kind === "podcast" ? "episode" : "track",
      title: s.title || (t && t.online === false ? "Speaker is offline" : "Nothing playing"),
      subtitle: s.subtitle || (t ? t.name : ""), artUrl: s.artUrl || null,
      playing: !!s.playing, position: pos, duration: dur, volume: s.volume ?? (t ? t.volume : 0)
    };
  }

  async function sendCommand(targetId, cmd) {
    const res = await request(`/api/audio/speakers/${enc(targetId)}/commands`, { method: "POST", body: cmd });
    if (!res.ok) showToast(res.error, false);
    return res;
  }

  async function remoteCommand(cmd) {
    const t = remote.target;
    if (!t) return;
    // Show the change straight away; the next poll brings the speaker's own word.
    if (t.state) {
      if (cmd.action === "pause") t.state.playing = false;
      if (cmd.action === "resume") { t.state.positionSeconds = remoteView().position; remote.fetchedAt = Date.now(); t.state.playing = true; }
    }
    renderBarState();
    renderNpState();
    await sendCommand(t.deviceId, cmd);
    setTimeout(pollRemote, 700);
  }

  async function pollRemote() {
    if (!remote.target) return;
    const id = remote.target.deviceId;
    const res = await request("/api/audio/speakers");
    if (!remote.target || remote.target.deviceId !== id || !res.ok || !Array.isArray(res.data)) return;
    const found = res.data.find((s) => s.deviceId === id);
    if (!found) { showToast(`${remote.target.name} is no longer a speaker`, false); exitRemote(); return; }
    const before = remote.target.state && remote.target.state.itemId;
    remote.target = found;
    remote.fetchedAt = Date.now();
    if (before !== (found.state && found.state.itemId)) { renderBar(); if (npOpen) renderNp(); }
    else { renderBarState(); renderNpState(); drawTime(); }
  }

  function enterRemote(spk) {
    remote.target = spk;
    remote.fetchedAt = Date.now();
    clearInterval(remote.timer);
    remote.timer = setInterval(pollRemote, REMOTE_POLL_MS);
    markNow();
    renderBar();
    if (npOpen) renderNp();
    pollRemote();
  }

  function exitRemote(render = true) {
    if (!remote.target) return;
    clearInterval(remote.timer);
    remote.timer = null;
    remote.target = null;
    if (render) { renderBar(); if (npOpen) renderNp(); }
  }

  // "Play here instead": the speaker pauses and this browser carries on from the same spot.
  async function takeOverFromRemote() {
    const t = remote.target;
    if (!t) return;
    const view = remoteView();
    const s = t.state || {};
    exitRemote();
    sendCommand(t.deviceId, { action: "pause" });
    const item = currentItem();
    if (item && s.itemId === item.id) {
      seek(view.position);
      resume();
      return;
    }
    if (!s.itemId) return;
    if (s.kind === "podcast") {
      const res = await request(`/api/podcasts/episodes/${enc(s.itemId)}`);
      if (res.ok && res.data) loadQueue([episodeItem(res.data)], 0, null, { position: view.position, local: true });
    } else {
      loadQueue([trackItem({ id: s.itemId, title: s.title, artist: s.subtitle, artUrl: s.artUrl })], 0, null, { position: view.position, local: true });
    }
  }

  function sendQueueToRemote(items, index, context, opts) {
    const t = remote.target;
    let list = items;
    let start = index;
    if (opts.shuffle) {
      const first = opts.keepStart ? items[index] : items[Math.floor(Math.random() * items.length)];
      list = [first, ...shuffled(items.filter((i) => i !== first))];
      start = 0;
    }
    let refs = list.map((i) => ({ type: i.type, id: i.id }));
    if (refs.length > 500) { const from = clamp(start - 250, 0, refs.length - 500); refs = refs.slice(from, from + 500); start -= from; }
    sendCommand(t.deviceId, { action: "play", queue: refs, index: start, positionSeconds: opts.position || 0, shuffle: !!opts.shuffle, context: context || { type: "queue", id: null, name: "Queue" } })
      .then((res) => { if (res.ok) { showToast(`Playing on ${t.name}`, true); setTimeout(pollRemote, 900); } });
  }

  async function openSpeakerPicker(anchor) {
    if (popOpenFor(anchor)) { closePop(); return; }
    const pop = openPop(anchor, `<div class="au-pop-title">${ICON.cast} Play on…</div>
      <div class="au-spk-list" id="au-spk-list">${loadingMarkup("finding speakers")}</div>
      <form class="au-announce-form hidden" id="au-ann-form">
        <div class="au-pop-sep"></div>
        <div class="au-pop-label">${ICON.announce} Announce on a speaker</div>
        <select class="field-input" id="au-ann-target" aria-label="Speaker"></select>
        <div class="mv-path-row"><input class="field-input" id="au-ann-text" maxlength="300" placeholder="Dinner's ready!" autocomplete="off"><button type="submit" class="mv-btn">say</button></div>
      </form>`, (p) => {
      p.addEventListener("click", (e) => {
        const b = e.target.closest("[data-spk]");
        if (!b) return;
        const id = b.dataset.spk;
        if (id === "here") { closePop(); if (remote.target) takeOverFromRemote(); return; }
        const s = (p.speakers || []).find((x) => x.deviceId === id);
        if (s) chooseSpeaker(s);
      });
      p.addEventListener("input", (e) => {
        if (!e.target.dataset.spkVol) return;
        const v = Number(e.target.value) / 100;
        clearTimeout(remote.volTimer);
        remote.volTimer = setTimeout(() => sendCommand(e.target.dataset.spkVol, { action: "volume", volume: v }), 250);
      });
      p.querySelector("#au-ann-form").addEventListener("submit", (e) => {
        e.preventDefault();
        const text = p.querySelector("#au-ann-text").value.trim();
        const target = p.querySelector("#au-ann-target").value;
        if (!text || !target) return;
        sendCommand(target, { action: "announce", text }).then((res) => { if (res.ok) { showToast("Announced", true); p.querySelector("#au-ann-text").value = ""; } });
      });
    }, "au-pop-speakers");
    const res = await request("/api/audio/speakers");
    if (!popOpenFor(anchor)) return;
    const list = el("au-spk-list");
    if (!res.ok) { list.innerHTML = `<div class="mv-empty mv-error">${escapeHtml(res.error)}</div>`; return; }
    const speakers = (Array.isArray(res.data) ? res.data : []).filter((s) => s.deviceId !== deviceId());
    pop.speakers = speakers;
    const kindIcon = (k) => (k === "tv" ? ICON.computer : k === "kiosk" ? ICON.speaker : k === "phone" || k === "tablet" ? ICON.speaker : ICON.computer);
    const row = (s) => {
      const st = s.state;
      const activeRow = remote.target && remote.target.deviceId === s.deviceId;
      const line = !s.online ? `offline · seen ${ago(s.lastSeenAt)}` : st && st.title ? `${st.playing ? "playing" : "paused"}: ${st.title}${st.subtitle ? " · " + st.subtitle : ""}` : "idle";
      return `<div class="au-spk ${activeRow ? "active" : ""} ${s.online ? "" : "offline"}">
        <button type="button" class="au-spk-main" data-spk="${escapeHtml(s.deviceId)}" ${s.online ? "" : "disabled"}>
          <span class="au-spk-icon">${kindIcon(s.kind)}</span>
          <span class="au-spk-text"><span class="au-spk-name">${escapeHtml(s.name)} <span class="au-spk-kind">${escapeHtml(s.kind || "")}</span></span><span class="au-spk-line">${escapeHtml(line)}</span></span>
          <span class="au-spk-dot ${s.online ? "on" : ""}"></span>
        </button>
        ${s.online ? `<input type="range" min="0" max="100" class="au-vol-range au-spk-vol" value="${Math.round(((st && st.volume) ?? s.volume ?? 0.5) * 100)}" data-spk-vol="${escapeHtml(s.deviceId)}" aria-label="${escapeHtml(s.name)} volume">` : ""}
      </div>`;
    };
    const here = `<div class="au-spk ${remote.target ? "" : "active"}"><button type="button" class="au-spk-main" data-spk="here">
        <span class="au-spk-icon">${ICON.computer}</span>
        <span class="au-spk-text"><span class="au-spk-name">This browser <span class="au-spk-kind">${escapeHtml(deviceName())}</span></span><span class="au-spk-line">${remote.target ? "take over: carry on here from the same spot" : "playing here"}</span></span>
        <span class="au-spk-dot on"></span></button></div>`;
    list.innerHTML = here + (speakers.length ? speakers.map(row).join("") : `<div class="au-pop-note">No other speakers yet. Turn on <b>Let others play here</b> on a kiosk, the Apple TV or another open app, and it appears here.</div>`);
    const announcers = speakers.filter((s) => s.online && s.allowAnnouncements);
    const form = el("au-ann-form");
    form.classList.toggle("hidden", !announcers.length);
    el("au-ann-target").innerHTML = announcers.map((s) => `<option value="${escapeHtml(s.deviceId)}">${escapeHtml(s.name)}</option>`).join("");
    placePop(anchor, pop);
  }

  async function chooseSpeaker(s) {
    closePop();
    if (!s.online) { showToast(`${s.name} is offline`, false); return; }
    const item = remote.target ? null : currentItem();
    if (item) {
      const body = nowPlayingBody();
      const res = await sendCommand(s.deviceId, { action: "play", queue: body.queue, index: body.index, positionSeconds: body.positionSeconds, shuffle: state.shuffle, context: body.context });
      if (!res.ok) return;
      activeDeck().el.pause();
      emit("state");
      showToast(`Playing on ${s.name}`, true);
    }
    enterRemote(s);
  }

  // ---- this browser as a speaker ("Let others play here") -------------------------------------

  const speaker = { on: false, heartbeat: null, stateTimer: null, stateSoon: null, abort: null, loopId: 0, after: -1 };

  function speakerPref() {
    const v = lsGet("speakerMode", null);
    return v === null ? isKiosk() : !!v;
  }

  async function setSpeakerMode(on) {
    deviceId();
    if (isPrimaryTab()) lsSet("speakerMode", !!on);
    if (on) await startSpeaker(); else await stopSpeaker();
    const box = document.querySelector("#au-pop #au-spk");
    if (box) box.checked = speaker.on;
    emit("speaker");
  }

  async function startSpeaker() {
    if (speaker.on || !(enabledFlags.music || enabledFlags.podcasts)) return;
    speaker.on = true;
    await speakerHeartbeat();
    clearInterval(speaker.heartbeat);
    speaker.heartbeat = setInterval(speakerHeartbeat, SPEAKER_HEARTBEAT_MS);
    clearInterval(speaker.stateTimer);
    speaker.stateTimer = setInterval(() => { if (isPlaying()) pushSpeakerState(); }, SPEAKER_STATE_EVERY_MS);
    pushSpeakerState();
    commandLoop();
  }

  async function stopSpeaker() {
    if (!speaker.on) return;
    speaker.on = false;
    speaker.loopId++;
    clearInterval(speaker.heartbeat);
    clearInterval(speaker.stateTimer);
    if (speaker.abort) speaker.abort.abort();
    await request(`/api/audio/speakers/${enc(deviceId())}`, { method: "DELETE" });
  }

  function speakerHeartbeat() {
    if (!speaker.on) return Promise.resolve();
    return request(`/api/audio/speakers/${enc(deviceId())}`, {
      method: "PUT",
      body: { name: deviceName(), kind: isKiosk() ? "kiosk" : "web", allowAnnouncements: settings.allowAnnouncements, volume: settings.volume }
    });
  }

  function speakerState() {
    const deck = activeDeck();
    const item = currentItem();
    return {
      playing: isPlaying() && !remote.target, title: item ? item.title : null, subtitle: item ? item.subtitle : null,
      artUrl: item ? item.artUrl : null, positionSeconds: Math.round((deck.el.currentTime || 0) * 10) / 10,
      durationSeconds: item ? Math.round(durationOf(deck, item)) || null : null, volume: settings.volume,
      kind: item && item.type === "episode" ? "podcast" : "music", itemId: item ? item.id : null
    };
  }

  function pushSpeakerState() {
    clearTimeout(speaker.stateSoon);
    if (!speaker.on) return;
    request(`/api/audio/speakers/${enc(deviceId())}/state`, { method: "PUT", body: speakerState() });
  }

  function speakerStateSoon() {
    if (!speaker.on) return;
    clearTimeout(speaker.stateSoon);
    speaker.stateSoon = setTimeout(pushSpeakerState, 400);
  }

  const sleepMs = (ms) => new Promise((r) => setTimeout(r, ms));

  // The long poll. Commands run strictly in order, and an announce is spoken to the end before
  // the next one starts (an alarm's briefing is an announce followed by a play).
  async function commandLoop() {
    const loop = ++speaker.loopId;
    let after = -1;
    while (speaker.on && loop === speaker.loopId) {
      speaker.abort = new AbortController();
      const res = await request(`/api/audio/speakers/${enc(deviceId())}/commands?after=${after}&wait=25`, { signal: speaker.abort.signal });
      if (!speaker.on || loop !== speaker.loopId) break;
      if (!res.ok) {
        if (res.aborted) continue;
        await sleepMs(res.status === 404 ? 60000 : 5000);
        continue;
      }
      const data = res.data || {};
      const commands = (data.commands || []).slice().sort((a, b) => a.seq - b.seq);
      for (const cmd of commands) {
        if (cmd.seq <= after) continue;
        after = cmd.seq;
        try { await runCommand(cmd); } catch (err) { console.error("Speaker command failed", err); }
      }
      if (typeof data.latestSeq === "number" && data.latestSeq > after && !commands.length) after = data.latestSeq;
    }
  }

  let unduckTimer = null;
  async function runCommand(cmd) {
    switch (cmd.action) {
      case "play": await remotePlay(cmd); break;
      case "pause": pause(); break;
      case "resume": resume(); break;
      case "next": next(); break;
      case "previous": previous(); break;
      case "seek": seek(Number(cmd.positionSeconds) || 0); break;
      case "volume":
        if (cmd.volume != null) {
          if (cmd.rampSeconds > 0) { setVolume(cmd.volume); lv.fade = 0; applyLevels(0.02); rampLevel("fade", 1, Number(cmd.rampSeconds)); }
          else setVolume(cmd.volume);
        }
        break;
      case "stop": stop(); break;
      case "duck":
        rampLevel("duck", 0.2, 0.4);
        clearTimeout(unduckTimer);
        unduckTimer = setTimeout(() => rampLevel("duck", 1, 0.8), 30000);
        break;
      case "unduck": clearTimeout(unduckTimer); rampLevel("duck", 1, 0.8); break;
      case "announce":
        if (settings.allowAnnouncements && cmd.text) await announce(cmd.text, cmd.from ? `from ${cmd.from}` : "");
        break;
      default: break;
    }
    speakerStateSoon();
  }

  // A play command carries only ids. It starts at once; the titles and art follow from
  // resolving this browser's own now-playing (the one route that turns a queue of ids into
  // Tracks and Episodes in a single request).
  async function remotePlay(cmd) {
    const refs = (cmd.queue || []).filter((r) => r && r.id && (r.type === "track" || r.type === "episode"));
    if (!refs.length) return;
    exitRemote(false);
    const items = refs.map(stubItem);
    const ramp = Number(cmd.rampSeconds) || 0;
    if (cmd.volume != null) setVolume(cmd.volume);
    if (ramp > 0) { lv.fade = 0; applyLevels(0.02); }
    loadQueue(items, clamp(Number(cmd.index) || 0, 0, items.length - 1), cmd.context || null, { position: Number(cmd.positionSeconds) || 0, resume: false, fromRemote: true, local: true, tab: "lyrics" });
    if (cmd.shuffle) { state.shuffle = true; emit("state"); }
    if (ramp > 0) rampLevel("fade", 1, ramp);
    if (cmd.from) showToast(`${cmd.from} started playing here`, true);
    await sendNowPlaying();
    const res = await request(`/api/audio/now-playing/${enc(deviceId())}/resolve`);
    if (!res.ok || !res.data) return;
    const byKey = new Map();
    for (const t of res.data.tracks || []) byKey.set(`track:${t.id}`, t);
    for (const e of res.data.episodes || []) byKey.set(`episode:${e.id}`, e);
    for (const item of state.queue) {
      const raw = byKey.get(`${item.type}:${item.id}`);
      if (!raw || !item.stub) continue;
      const fresh = itemFromRaw(raw, item.type);
      Object.assign(item, { title: fresh.title, subtitle: fresh.subtitle, album: fresh.album, artUrl: fresh.artUrl, durationSeconds: fresh.durationSeconds || item.durationSeconds, raw, stub: false });
    }
    const deck = activeDeck();
    if (deck.item) {
      deck.rg = replayGainFor(deck.item);
      applyLevels(0.3);
    }
    lastItemKey = null;
    onItemChanged();
    if (npOpen) renderNp();
  }

  // ---- "Continue from <device>" ---------------------------------------------------------------

  let continueData = null;

  async function checkContinue() {
    if (!(enabledFlags.music || enabledFlags.podcasts) || currentItem() || remote.target) { hideContinue(); return; }
    const res = await request("/api/audio/now-playing");
    if (!res.ok || !Array.isArray(res.data)) return;
    const mine = deviceId();
    const cand = res.data.find((n) => n && n.deviceId !== mine && Array.isArray(n.queue) && n.queue.length
      && Date.now() - new Date(n.updatedAt).getTime() < 24 * 3600 * 1000
      && (n.kind === "podcast" ? enabledFlags.podcasts : enabledFlags.music));
    if (!cand) { hideContinue(); return; }
    let dismissed = null;
    try { dismissed = sessionStorage.getItem(LS + "continueDismissed"); } catch { /* blocked */ }
    if (dismissed === `${cand.deviceId}|${cand.updatedAt}`) return;
    const r = await request(`/api/audio/now-playing/${enc(cand.deviceId)}/resolve`);
    if (!r.ok || !r.data || currentItem()) return;
    const np = r.data.nowPlaying || cand;
    const byKey = new Map();
    for (const t of r.data.tracks || []) byKey.set(`track:${t.id}`, t);
    for (const e of r.data.episodes || []) byKey.set(`episode:${e.id}`, e);
    const ref = np.queue[np.index] || np.queue[0];
    const raw = ref && byKey.get(`${ref.type}:${ref.id}`);
    if (!raw) return;
    continueData = { np, byKey };
    const box = el("au-continue");
    const item = itemFromRaw(raw, ref.type);
    const art = artSized(item.artUrl, 120);
    box.innerHTML = `<span class="au-continue-art">${art ? `<img src="${escapeHtml(art)}" alt="">` : ref.type === "episode" ? ICON.mic : ICON.note}</span>
      <span class="au-continue-text">
        <span class="au-continue-kicker">${np.playing ? "Playing" : "Paused"} on ${escapeHtml(np.deviceName || "another device")} &middot; ${escapeHtml(ago(np.updatedAt))}</span>
        <span class="au-continue-title">${escapeHtml(item.title)}${item.subtitle ? ` <span class="au-continue-sub">${escapeHtml(item.subtitle)}</span>` : ""}</span>
      </span>
      <button type="button" class="mv-btn mv-btn-primary" data-cont="go">${ICON.play}<span>Continue here</span></button>
      <button type="button" class="au-ctl au-ctl-sm" data-cont="dismiss" title="Not now" aria-label="Dismiss">${ICON.close}</button>`;
    wireImages(box);
    box.classList.remove("hidden");
    renderBar();
  }

  function hideContinue() {
    const box = el("au-continue");
    if (!box || box.classList.contains("hidden")) return;
    box.classList.add("hidden");
    box.innerHTML = "";
    renderBar();
  }

  async function continueHere() {
    if (!continueData) return;
    const { np, byKey } = continueData;
    continueData = null;
    hideContinue();
    const items = [];
    let index = 0;
    np.queue.forEach((ref, i) => {
      const raw = byKey.get(`${ref.type}:${ref.id}`);
      if (!raw) return;
      if (i === np.index) index = items.length;
      items.push(itemFromRaw(raw, ref.type));
    });
    if (!items.length) return;
    let position = Number(np.positionSeconds) || 0;
    if (np.playing && np.updatedAt) position += Math.max(0, (Date.now() - new Date(np.updatedAt).getTime()) / 1000) * (Number(np.speed) || 1);
    const dur = items[index].durationSeconds;
    if (dur && position > dur - 2) position = Math.max(0, dur - 2);
    if (["off", "all", "one"].includes(np.repeat)) state.repeat = np.repeat;
    loadQueue(items, index, np.context || null, { position, resume: false, local: true });
    if (np.shuffle) { state.shuffle = true; emit("state"); }
    // If that device is a speaker that is still playing, it stops there.
    const res = await request("/api/audio/speakers");
    if (res.ok && Array.isArray(res.data)) {
      const spk = res.data.find((s) => s.deviceId === np.deviceId && s.online);
      if (spk && spk.state && spk.state.playing) sendCommand(spk.deviceId, { action: "pause" });
    }
  }

  function wireContinue() {
    el("au-bar").addEventListener("click", (e) => {
      const b = e.target.closest("[data-cont]");
      if (!b) return;
      if (b.dataset.cont === "go") { unlockAudio(); continueHere(); }
      else {
        if (continueData) {
          try { sessionStorage.setItem(LS + "continueDismissed", `${continueData.np.deviceId}|${continueData.np.updatedAt}`); } catch { /* blocked */ }
        }
        continueData = null;
        hideContinue();
      }
    });
  }

  // ---- keyboard -------------------------------------------------------------------------------

  function onAudioScreen() {
    if (npOpen) return true;
    const music = el("view-music");
    const pods = el("view-podcasts");
    return !!((music && !music.classList.contains("hidden")) || (pods && !pods.classList.contains("hidden")));
  }

  function wireKeys() {
    window.addEventListener("keydown", (e) => {
      if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
      const t = e.target;
      if (t && (/^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName) || t.isContentEditable)) return;
      const mv = el("mv-player");
      if (mv && !mv.classList.contains("hidden")) return;
      if (e.key === "Escape") {
        if (popState) closePop();
        else if (menuState) closeMenu();
        else if (modalOpen()) closeModal();
        else if (npOpen) closeNowPlaying();
        else return;
        e.preventDefault();
        return;
      }
      if (modalOpen() || !display()) return;
      const ep = isEpisodeNow();
      switch (e.key) {
        case " ":
          if (!onAudioScreen() || (t && /^(BUTTON|A)$/.test(t.tagName) && !t.closest("#au-np, #au-bar"))) return;
          e.preventDefault();
          togglePlay();
          break;
        case "ArrowLeft":
          if (!onAudioScreen()) return;
          e.preventDefault();
          if (e.shiftKey) previous(); else skipBy(ep ? -15 : -10);
          break;
        case "ArrowRight":
          if (!onAudioScreen()) return;
          e.preventDefault();
          if (e.shiftKey) next(); else skipBy(ep ? 30 : 10);
          break;
        case "ArrowUp":
        case "ArrowDown":
          if (!npOpen) return;
          e.preventDefault();
          volumeInput(clamp((display().volume || 0) + (e.key === "ArrowUp" ? 0.05 : -0.05), 0, 1));
          renderBarState();
          renderNpState();
          break;
        case "n": case "N": e.preventDefault(); next(); break;
        case "p": case "P": e.preventDefault(); previous(); break;
        case "l": case "L": if (!ep) { e.preventDefault(); toggleLove(); } break;
        case "b": case "B": if (ep) { e.preventDefault(); addBookmarkHere(); } break;
        case "q": case "Q": e.preventDefault(); if (npOpen && npTab === "queue") closeNowPlaying(); else openNowPlaying("queue"); break;
        default: break;
      }
    });
  }

  // ---- which tabs exist -----------------------------------------------------------------------

  const enabledFlags = { music: false, podcasts: false, checked: false };
  let servicesStarted = false;

  // /api/audio/enabled always answers on a current server; an older one 404s, which is "off".
  async function checkEnabled() {
    const res = await request("/api/audio/enabled");
    const data = res.ok && res.data ? res.data : {};
    enabledFlags.music = data.music === true;
    enabledFlags.podcasts = data.podcasts === true;
    enabledFlags.checked = true;
    const mt = el("tab-music");
    const pt = el("tab-podcasts");
    if (mt) mt.classList.toggle("hidden", !enabledFlags.music);
    if (pt) pt.classList.toggle("hidden", !enabledFlags.podcasts);
    if (enabledFlags.music || enabledFlags.podcasts) startServices();
    else if (servicesStarted) { stopSpeaker(); servicesStarted = false; }
    emit("enabled", { ...enabledFlags });
    return { ...enabledFlags };
  }

  function startServices() {
    if (servicesStarted) return;
    servicesStarted = true;
    ensureAuth();
    // Only the browser's own tab comes back as its speaker; a second tab opened beside it is a
    // separate device that can control it (and can be made a speaker itself from its switch).
    deviceId();
    if (speakerPref() && isPrimaryTab()) startSpeaker();
    checkContinue();
  }

  // ---- wiring ---------------------------------------------------------------------------------

  let errorRun = 0;
  function wireDecks() {
    for (const deck of decks) {
      const a = deck.el;
      a.addEventListener("timeupdate", () => {
        onDeckTime(deck);
        if (deck === activeDeck()) { scheduleDraw(); updatePositionState(false); }
      });
      a.addEventListener("ended", () => onEnded(deck));
      a.addEventListener("playing", () => { if (deck === activeDeck()) errorRun = 0; });
      for (const evt of ["play", "pause"]) {
        a.addEventListener(evt, () => {
          if (deck !== activeDeck()) return;
          if (evt === "pause" && deck.item && deck.item.type === "episode") reportProgress(false);
          if ("mediaSession" in navigator) navigator.mediaSession.playbackState = a.paused ? "paused" : "playing";
          emit("state");
          sendNowPlayingSoon();
          speakerStateSoon();
          if (evt === "play" && isKiosk() && !npOpen && deck.item) openNowPlaying();
        });
      }
      a.addEventListener("loadedmetadata", () => { if (deck === activeDeck()) { drawTime(); updatePositionState(true); } });
      a.addEventListener("error", () => {
        if (deck !== activeDeck() || !deck.item || !a.getAttribute("src")) return;
        const err = a.error;
        if (!err || err.code === 1) return;
        errorRun++;
        showToast(`Couldn't play “${deck.item.title}”${err.code === 4 ? " - this browser can't decode it" : ""}`, false);
        deck.finished = true;
        // Skip on past a bad file, but don't spin through a whole queue that all fails.
        if (errorRun < 3 && nextIndex(false) >= 0) setTimeout(() => { if (deck === activeDeck()) next(); }, 1200);
        else emit("state");
      });
    }
  }

  function wireEvents() {
    on("item", () => { renderBar(); if (npOpen) renderNp(); hideContinue(); });
    on("state", () => { renderBarState(); renderNpState(); });
    on("time", scheduleDraw);
    on("queue", () => { if (npOpen && npTab === "queue") renderNpPanel(); });
    on("love", () => { renderBarState(); renderNpMeta(); });
    on("volume", () => { renderBarState(); renderNpState(); });
    on("speed", () => { renderNpState(); });
    on("sleep", () => { renderBarState(); renderNpState(); });
  }

  function init() {
    wireMenu();
    wirePop();
    wireModal();
    buildBar();
    wireContinue();
    wireDecks();
    wireEvents();
    wireKeys();
    wireMediaSession();
    el("au-tap").addEventListener("click", () => { unlockAudio(); hideTapToPlay(); resume(); });
    el("au-announce").addEventListener("click", hideAnnouncement);
    // Web Audio may only start from a gesture; the first click or key anywhere starts it.
    const unlock = () => unlockAudio();
    document.addEventListener("pointerdown", unlock, { capture: true });
    document.addEventListener("keydown", unlock, { capture: true });
    // A film starting in the Movies tab pauses the music.
    const mv = el("mv-video");
    if (mv) mv.addEventListener("play", () => { if (isPlaying() && !remote.target) pause(); });
    setInterval(() => { if (isPlaying() && !remote.target && currentItem()) sendNowPlaying(); }, NOW_PLAYING_EVERY_MS);
    setInterval(() => { const i = currentItem(); if (i && i.type === "episode" && isPlaying() && !remote.target) reportProgress(false); }, PODCAST_PROGRESS_EVERY_MS);
    setInterval(() => { if (!currentItem()) checkContinue(); }, CONTINUE_CHECK_MS);
    setInterval(() => { if (state.sleep && state.sleep.mode === "minutes") { renderBarState(); renderNpState(); } }, 20000);
    document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible" && !currentItem()) checkContinue(); });
    window.addEventListener("pagehide", () => {
      const item = currentItem();
      if (!item || remote.target) return;
      if (item.type === "episode") reportProgress(true);
      else finishItem("stopped");
      const body = nowPlayingBody();
      if (body) { body.playing = false; request("/api/audio/now-playing", { method: "PUT", body, keepalive: true }); }
    });
    checkEnabled();
  }

  // A newly opened page (an episode, an album) starts at its top: without this it kept the
  // scroll position of the list it was opened from and showed up half scrolled past its header.
  function scrollToTopOf(node) {
    if (!node) return;
    const top = Math.max(0, node.getBoundingClientRect().top + window.scrollY - 8);
    if (window.scrollY > top) window.scrollTo(0, top);
  }

  init();

  return {
    ICON, EQ_PRESETS,
    util: {
      escapeHtml, safeSrc, safeLink, request, enc, fmtClock, fmtDuration, fmtDate, ago, plural, sizeLabel, clamp,
      art, mosaic, wireImages, rowMarkup, wireRows, scrollRow, loadingMarkup, errorMarkup, emptyMarkup, stars,
      showToast, showErrors, sanitizeHtml, openModal, closeModal, modalOpen, modalHeader, onModalAction,
      openMenu, closeMenu, openPop, closePop, makeSortable, moveInArray, openFolderBrowser, ensureAuth, isAdmin,
      authInfo: () => authInfo, isKiosk, deviceId, deviceName, lsGet, lsSet, artSized, withQuery, codecs, speak, scrollToTopOf
    },
    on, checkEnabled, enabled: () => ({ ...enabledFlags }),
    playTracks, playEpisodes, enqueue, startRadio, playMix, shuffleAll, stop,
    current: () => (remote.target ? null : currentItem()), isPlaying, togglePlay, pause, resume, seek, position: currentPosition,
    openNowPlaying, closeNowPlaying, markNow, syncRaw, toggleLove, refreshShow: (id) => shows.delete(id),
    listSpeakers: () => request("/api/audio/speakers"), sendCommand, announce, openSpeakerPicker,
    remoteTarget: () => remote.target, setSpeakerMode, speakerOn: () => speaker.on
  };
})();
