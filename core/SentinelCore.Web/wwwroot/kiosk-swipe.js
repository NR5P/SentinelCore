// Word guessing for the kiosk keyboard (kiosk-keyboard.js): finishing a half-typed word, and
// turning a finger dragged across the letters into the word it spelled ("swipe typing").
//
// The words come from kiosk-words.txt, one per line, commonest first, so a word's line number is
// how common it is. Words the kiosk sees typed are remembered (in localStorage, by the keyboard)
// and rank higher next time; a word the list doesn't have joins it once it's been typed twice.
//
// Swipe matching: every word has an ideal path, the straight lines joining its letters' key
// centres ("hello" = h-e-l-o; a doubled letter is one key). The finger's path is compared with
// the ideal paths of the words that start near where the finger went down and end near where it
// lifted, on three things - the two paths' overall shape, whether the finger came close to each
// letter in order, and how common the word is. Distances are in key widths, so the guess doesn't
// care how big the keyboard is drawn.
//
//   node --test core/SentinelCore.Web/scripts/
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.KioskWords = factory();
})(typeof self !== "undefined" ? self : this, function () {
  const SAMPLES = 32;          // points both paths are resampled to before comparing shapes
  const TRACE = 80;            // points the finger's path is resampled to for finding its letters
  const SHORTLIST = 60;        // best rough matches that get the letter-by-letter check
  const GIVE_UP = 6.0;         // a best match scoring worse than this means no word was meant
  const END_REACH = 1.25;      // key widths: how far from the first/last letter a swipe may start/end
  const MISS_LIMIT = 1.3;      // key widths: a letter the finger never came this close to rules a word out
  const LEARN_JOIN = 2;        // times an unlisted word must be typed before it's suggested
  const LEARN_MAX = 2000;      // remembered words kept

  // Letters of a word as typed on the keys: lowercase, no apostrophe.
  const keysOf = (word) => word.toLowerCase().replace(/[^a-z]/g, "");

  function create(text, learned) {
    const words = [];          // { word, keys, rank }
    const byKey = new Map();   // lowercase word -> entry
    const byFirst = new Map(); // first key letter -> entries, commonest first
    const byEnds = new Map();  // first + last key letter -> entries (two letters or more)
    let counts = new Map();    // lowercase word -> times typed

    function add(word, rank) {
      const keys = keysOf(word);
      if (!keys || byKey.has(word.toLowerCase())) return null;
      const entry = { word, keys, rank, template: null };
      words.push(entry);
      byKey.set(word.toLowerCase(), entry);
      if (!byFirst.has(keys[0])) byFirst.set(keys[0], []);
      byFirst.get(keys[0]).push(entry);
      if (keys.length > 1) {
        const ends = keys[0] + keys[keys.length - 1];
        if (!byEnds.has(ends)) byEnds.set(ends, []);
        byEnds.get(ends).push(entry);
      }
      return entry;
    }

    for (const line of text.split("\n")) {
      const word = line.trim();
      if (word && !word.startsWith("#")) add(word, words.length + 1);
    }
    const listed = words.length;

    // How likely a word is before looking at the finger: common words cost less, and a word the
    // kiosk has typed counts as at least fairly common (4 times typed = about the 100th word).
    function cost(entry) {
      const typed = counts.get(entry.word.toLowerCase()) || 0;
      const rank = typed ? Math.min(entry.rank, 400 / typed) : entry.rank;
      return Math.log(1 + rank);
    }

    function usable(entry) {
      return entry.rank <= listed || (counts.get(entry.word.toLowerCase()) || 0) >= LEARN_JOIN;
    }

    function setLearned(map) {
      counts = new Map();
      for (const [word, n] of Object.entries(map || {})) {
        if (typeof n === "number" && n > 0) {
          counts.set(word.toLowerCase(), n);
          if (!byKey.has(word.toLowerCase())) add(word, listed + 1);
        }
      }
    }

    // Remembers a typed word. Returns the counts to save, or null if nothing changed.
    function learn(word) {
      if (!/^[A-Za-z]+(?:'[A-Za-z]+)?$/.test(word) || keysOf(word).length < 2) return null;
      const key = word.toLowerCase();
      const known = byKey.get(key);
      // "Monday" typed as "monday" is still Monday; an unlisted word keeps the case it was typed in.
      counts.set(key, (counts.get(key) || 0) + 1);
      if (!known) add(word, listed + 1);
      if (counts.size > LEARN_MAX) {
        // Forget the least used.
        const sorted = [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, LEARN_MAX);
        counts = new Map(sorted);
      }
      return saved();
    }

    function saved() {
      const out = {};
      for (const [key, n] of counts) out[byKey.get(key) ? byKey.get(key).word : key] = n;
      return out;
    }

    // ---- finishing a word ----

    // Up to `limit` words starting with `prefix`, likeliest first, in the prefix's case.
    function complete(prefix, limit = 3) {
      const keys = prefix.toLowerCase();
      if (!keys || !/^[a-z]/.test(keys)) return [];
      const found = [];
      for (const entry of byFirst.get(keys[0]) || []) {
        if (entry.word.toLowerCase().startsWith(keys) && usable(entry)) found.push(entry);
      }
      found.sort((a, b) => cost(a) - cost(b));
      return found.slice(0, limit).map((e) => matchCase(e.word, prefix));
    }

    // ---- swipes ----

    let geometry = null;       // { keys: {letter: {x, y}} in key widths, id }

    // `keys` maps each letter to its key's centre in pixels; `keyWidth` is one letter key's width.
    function setKeys(keys, keyWidth) {
      const scaled = {};
      for (const [letter, p] of Object.entries(keys)) scaled[letter] = { x: p.x / keyWidth, y: p.y / keyWidth };
      const id = JSON.stringify(scaled);
      if (geometry && geometry.id === id) return;
      geometry = { keys: scaled, id, keyWidth };
      for (const entry of words) entry.template = null;
    }

    function templateOf(entry) {
      if (entry.template) return entry.template;
      const pts = [];
      for (const c of entry.keys) {
        const k = geometry.keys[c];
        if (!k) return null;
        const last = pts[pts.length - 1];
        if (!last || last.x !== k.x || last.y !== k.y) pts.push(k);
      }
      entry.template = { letters: pts, path: resample(pts, SAMPLES), length: pathLength(pts) };
      return entry.template;
    }

    // Likeliest words for a finger path (pixel points relative to the same origin as the keys),
    // best first: [{ word, score }]. Empty when nothing is close enough to call it a word.
    function decode(points, limit = 3) {
      if (!geometry || points.length < 2) return [];
      const kw = geometry.keyWidth;
      let pts = points.map((p) => ({ x: p.x / kw, y: p.y / kw }));
      pts = pts.filter((p, i) => i === 0 || dist(p, pts[i - 1]) > 0.02);
      if (pts.length < 2) return [];
      const start = pts[0];
      const end = pts[pts.length - 1];
      const length = pathLength(pts);
      const path = resample(pts, SAMPLES);
      const trace = resample(pts, TRACE);

      const near = (p) => Object.entries(geometry.keys)
        .map(([letter, k]) => ({ letter, d: dist(p, k) }))
        .filter((k) => k.d < END_REACH);
      const starts = near(start);
      const ends = near(end);

      // Rough pass on every candidate (shape, ends, how common), then the letter-by-letter check
      // on only the best of those - it's the slow part, and on the Pi it has to stay quick.
      const rough = [];
      for (const s of starts) {
        for (const e of ends) {
          for (const entry of byEnds.get(s.letter + e.letter) || []) {
            if (!usable(entry)) continue;
            const t = templateOf(entry);
            if (!t || t.letters.length < 2) continue;
            // A swipe much longer or shorter than the word's own path isn't that word.
            if (Math.abs(t.length - length) > 1.2 + 0.45 * Math.max(t.length, length)) continue;
            let shape = 0;
            for (let i = 0; i < SAMPLES; i++) shape += dist(path[i], t.path[i]);
            shape /= SAMPLES;
            rough.push({ entry, score: 2.4 * shape + 1.2 * (s.d + e.d) + 0.28 * cost(entry) });
          }
        }
      }
      rough.sort((a, b) => a.score - b.score);
      const scored = [];
      const seen = new Set();
      for (const r of rough.slice(0, SHORTLIST)) {
        if (seen.has(r.entry)) continue;
        seen.add(r.entry);
        const miss = letterMiss(r.entry.template.letters, trace);
        if (miss === null) continue;
        const score = r.score + 1.5 * miss;
        if (score <= GIVE_UP) scored.push({ word: r.entry.word, score });
      }
      scored.sort((a, b) => a.score - b.score);
      const out = scored.slice(0, limit);
      return out;
    }

    if (learned) setLearned(learned);
    return { complete, decode, setKeys, learn, setLearned, has: (w) => byKey.has(w.toLowerCase()), size: () => words.length };
  }

  // Average distance from each middle letter to the point the finger passed it, matching the
  // letters to the path in order (the finger can't visit the third letter before the second, and
  // a word with a letter twice - t-o-m-o-r-o-w - visits it twice). Worked out by dynamic
  // programming: best[i] is the cheapest way to place the letters so far with the latest one at
  // point i. null when the finger never came near one of them.
  function letterMiss(letters, path) {
    const inner = letters.length - 2;
    if (inner <= 0) return 0;
    const n = path.length;
    let best = new Array(n).fill(0);
    for (let li = 1; li <= inner; li++) {
      const next = new Array(n);
      let lowest = Infinity;
      for (let i = 0; i < n; i++) {
        lowest = Math.min(lowest, best[i]);
        const d = dist(path[i], letters[li]);
        next[i] = d > MISS_LIMIT ? Infinity : lowest + d;
      }
      best = next;
    }
    const total = Math.min(...best);
    return total === Infinity ? null : total / inner;
  }

  function dist(a, b) {
    return Math.hypot(a.x - b.x, a.y - b.y);
  }

  function pathLength(pts) {
    let n = 0;
    for (let i = 1; i < pts.length; i++) n += dist(pts[i - 1], pts[i]);
    return n;
  }

  // `n` points evenly spaced along the path.
  function resample(pts, n) {
    const total = pathLength(pts);
    if (total === 0) return Array.from({ length: n }, () => ({ x: pts[0].x, y: pts[0].y }));
    const step = total / (n - 1);
    const out = [{ x: pts[0].x, y: pts[0].y }];
    let carried = 0;
    for (let i = 1; i < pts.length && out.length < n; i++) {
      let a = pts[i - 1];
      const b = pts[i];
      let seg = dist(a, b);
      while (carried + seg >= step && out.length < n) {
        const t = (step - carried) / seg;
        a = { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t };
        out.push(a);
        seg = dist(a, b);
        carried = 0;
      }
      carried += seg;
    }
    while (out.length < n) out.push({ x: pts[pts.length - 1].x, y: pts[pts.length - 1].y });
    return out;
  }

  // "Hel" + "hello" = "Hello"; "HEL" + "hello" = "HELLO"; listed capitals ("I'm") stay.
  function matchCase(word, typed) {
    if (typed.length > 1 && typed === typed.toUpperCase()) return word.toUpperCase();
    if (typed[0] && typed[0] !== typed[0].toLowerCase()) return word[0].toUpperCase() + word.slice(1);
    return word;
  }

  return { create, keysOf, matchCase };
});
