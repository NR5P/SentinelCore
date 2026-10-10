// Word guessing for the kiosk keyboard (wwwroot/kiosk-swipe.js), against the real word list and
// the keyboard's real letter layout. Swipes are simulated: a finger path through each word's
// letters, wobbled and corner-cut the way a real one is, from a fixed random seed.
//
//   node --test core/SentinelCore.Web/scripts/
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const { create, matchCase } = require("../wwwroot/kiosk-swipe.js");

const text = fs.readFileSync(path.join(__dirname, "../wwwroot/kiosk-words.txt"), "utf8");

// Key centres as styles.css lays out the abc keyboard on the Pi (1024 CSS px wide): rows are flex
// with 6px gaps inside 8px padding, specials are wider (bksp/enter/shift 1.5 keys).
function layout(width = 1024) {
  const rows = [
    [..."qwertyuiop", 1.5],
    [..."asdfghjkl", 1.5],
    [1.5, ..."zxcvbnm", "@", ".", 1.5],
  ];
  const inner = Math.min(width, 1100 + 16) - 16;
  const keys = {};
  let keyWidth = 0;
  rows.forEach((row, r) => {
    const units = row.reduce((n, k) => n + (typeof k === "number" ? k : 1), 0);
    const unit = (inner - 6 * (row.length - 1)) / units;
    let x = 8;
    for (const k of row) {
      const w = (typeof k === "number" ? k : 1) * unit;
      if (typeof k === "string" && /[a-z]/.test(k)) keys[k] = { x: x + w / 2, y: 8 + r * 64 + 29 };
      if (k === "q") keyWidth = w;
      x += w + 6;
    }
  });
  return { keys, keyWidth };
}

function random(seed) {
  return () => {
    seed = (seed * 1664525 + 1013904223) % 4294967296;
    return seed / 4294967296;
  };
}

function gaussian(rand) {
  return Math.sqrt(-2 * Math.log(rand() + 1e-9)) * Math.cos(2 * Math.PI * rand());
}

// A finger's path through `word` on `geo`: each letter aimed at with `wobble` key widths of
// error, the corners rounded, sampled every few pixels like pointermove events.
function swipe(word, geo, rand, wobble = 0.22) {
  const kw = geo.keyWidth;
  const aims = [];
  for (const c of word.toLowerCase().replace(/[^a-z]/g, "")) {
    const k = geo.keys[c];
    const p = { x: k.x + gaussian(rand) * wobble * kw, y: k.y + gaussian(rand) * wobble * kw };
    const last = aims[aims.length - 1];
    if (!last || last.c !== c) aims.push({ ...p, c });
  }
  const pts = [];
  for (let i = 1; i < aims.length; i++) {
    const a = aims[i - 1];
    const b = aims[i];
    const steps = Math.max(2, Math.ceil(Math.hypot(b.x - a.x, b.y - a.y) / 7));
    for (let s = 0; s < steps; s++) {
      const t = s / steps;
      pts.push({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
    }
  }
  pts.push({ x: aims[aims.length - 1].x, y: aims[aims.length - 1].y });
  // Round the corners: a finger doesn't stop dead on each letter.
  const smooth = pts.map((p, i) => {
    const w = pts.slice(Math.max(0, i - 3), i + 4);
    return { x: w.reduce((n, q) => n + q.x, 0) / w.length, y: w.reduce((n, q) => n + q.y, 0) / w.length };
  });
  smooth[0] = pts[0];
  smooth[smooth.length - 1] = pts[pts.length - 1];
  return smooth;
}

function engine() {
  const e = create(text);
  const geo = layout();
  e.setKeys(geo.keys, geo.keyWidth);
  return { e, geo };
}

test("the word list loads, commonest first", () => {
  const e = create(text);
  assert.ok(e.size() > 30000);
  assert.ok(e.has("the") && e.has("I'm") && e.has("Monday") && e.has("kitchen"));
  assert.equal(e.complete("th")[0], "the");
});

test("finishing a typed word", () => {
  const { e } = engine();
  assert.equal(e.complete("tom")[0], "tomorrow");
  assert.equal(e.complete("Tom")[0], "Tomorrow");
  assert.equal(e.complete("rem")[0], "reminder");
  assert.ok(e.complete("remi").includes("remind"));
  assert.equal(e.complete("mon")[0], "Monday");
  assert.deepEqual(e.complete(""), []);
  assert.deepEqual(e.complete("1"), []);
  assert.deepEqual(e.complete("zzzzq"), []);
});

test("everyday swipes come out right", () => {
  const { e, geo } = engine();
  const rand = random(7);
  const words = ["the", "hello", "you", "kitchen", "garage", "tomorrow", "reminder", "camera", "dinner",
    "milk", "pick", "school", "thanks", "door", "front", "driveway", "appointment", "Friday", "don't",
    "what", "time", "great", "love", "check", "please", "people", "because"];
  const wrong = [];
  for (const word of words) {
    const got = e.decode(swipe(word, geo, rand, 0.15)).map((g) => g.word);
    if (got[0] !== word) wrong.push(`${word} -> ${got.join(",")}`);
  }
  assert.ok(wrong.length <= 1, wrong.join("\n"));
});

test("accuracy over the 2000 commonest words", () => {
  const { e, geo } = engine();
  const rand = random(42);
  const words = text.split("\n").filter((w) => w && !w.startsWith("#"))
    .filter((w) => w.replace(/[^a-z]/gi, "").length > 1).slice(0, 2000);
  let top1 = 0;
  let top3 = 0;
  const started = Date.now();
  for (const word of words) {
    const got = e.decode(swipe(word, geo, rand)).map((g) => g.word);
    if (got[0] === word) top1++;
    if (got.includes(word)) top3++;
  }
  const ms = (Date.now() - started) / words.length;
  console.log(`top-1 ${(100 * top1 / words.length).toFixed(1)}%  top-3 ${(100 * top3 / words.length).toFixed(1)}%  ${ms.toFixed(2)} ms/swipe`);
  // Words that swipe identically ("to"/"too", "for"/"fit"-ish neighbours) cap top-1; the
  // suggestion row is the fix for those, so top-3 is what has to be high.
  assert.ok(top1 / words.length > 0.85);
  assert.ok(top3 / words.length > 0.96);
});

test("swipes scale with the keyboard", () => {
  const e = create(text);
  const small = layout(800);
  e.setKeys(small.keys, small.keyWidth);
  const got = e.decode(swipe("kitchen", small, random(3), 0.15));
  assert.equal(got[0].word, "kitchen");
});

test("a scribble is not a word", () => {
  const { e, geo } = engine();
  // Back and forth along the top row, ending far from where it started.
  const pts = [];
  for (let i = 0; i < 6; i++) pts.push(geo.keys[i % 2 ? "p" : "q"]);
  pts.push(geo.keys.m);
  const many = [];
  for (let i = 1; i < pts.length; i++) {
    for (let t = 0; t < 1; t += 0.05) many.push({ x: pts[i - 1].x + (pts[i].x - pts[i - 1].x) * t, y: pts[i - 1].y + (pts[i].y - pts[i - 1].y) * t });
  }
  assert.deepEqual(e.decode(many), []);
});

test("typed words are remembered", () => {
  const { e, geo } = engine();
  // An unlisted word joins after being typed twice.
  assert.equal(e.has("Zigbee"), false);
  assert.equal(e.learn("Zigbee") !== null, true);
  assert.deepEqual(e.complete("zigb"), []);
  const saved = e.learn("Zigbee");
  assert.equal(saved.Zigbee, 2);
  assert.equal(e.complete("zigb")[0], "Zigbee");
  assert.equal(e.decode(swipe("zigbee", geo, random(9), 0.12))[0].word, "Zigbee");
  // And survives a reload.
  const again = create(text, saved);
  assert.equal(again.complete("zig")[0], "Zigbee");
  // Junk isn't learned.
  assert.equal(e.learn("x"), null);
  assert.equal(e.learn("1234"), null);
  assert.equal(e.learn("ab-cd"), null);
});

test("typing a word often moves it up", () => {
  const { e } = engine();
  assert.notEqual(e.complete("spr")[0], "sprinkler");
  for (let i = 0; i < 4; i++) e.learn("sprinkler");
  assert.equal(e.complete("spr")[0], "sprinkler");
});

test("suggestions follow the typed case", () => {
  assert.equal(matchCase("hello", "He"), "Hello");
  assert.equal(matchCase("hello", "HE"), "HELLO");
  assert.equal(matchCase("hello", "he"), "hello");
  assert.equal(matchCase("I'm", "i"), "I'm");
});
