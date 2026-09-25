// Spoken words to a reminder, for the dashboard's copy of the rules. The examples live in
// core/docs/voice-phrase-cases.json, which the iPhone and Android tests read too - a phrase must
// mean the same thing wherever it was heard.
//
//   node --test core/SentinelCore.Web/scripts/
//
// The cases are written in one time zone; the test pins the process to it before any Date is made.
process.env.TZ = "America/Chicago";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const { parse, dueField } = require("../wwwroot/voice-phrase.js");

const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, "../../docs/voice-phrase-cases.json"), "utf8"));

test("the shared cases", () => {
  assert.equal(fixture.timeZone, process.env.TZ);
  const now = new Date(fixture.now);
  assert.ok(fixture.cases.length > 20);
  for (const example of fixture.cases) {
    const phrase = parse(example.say, now);
    assert.equal(phrase.title, example.title, example.say);
    assert.equal(dueField(phrase), example.due, example.say);
  }
});

test("a date with no time is all-day", () => {
  const phrase = parse("dentist Friday");
  assert.ok(phrase.due);
  assert.equal(phrase.hasTime, false);
});
