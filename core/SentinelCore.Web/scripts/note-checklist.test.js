// Checklists inside a note, for the dashboard's copy of the rules. The examples live in
// core/docs/note-checklist-cases.json, which the iPhone, Android and Windows tests read too - a
// note must tick off the same way wherever it is open.
//
//   node --test core/SentinelCore.Web/scripts/

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const checklist = require("../wwwroot/note-checklist.js");

const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, "../../docs/note-checklist-cases.json"), "utf8"));

test("progress", () => {
  for (const c of fixture.progress) assert.deepEqual(checklist.progress(c.body), { done: c.done, total: c.total }, c.body);
});

test("addItem", () => {
  for (const c of fixture.addItem) assert.equal(checklist.addItem(c.body, c.text), c.expect, `${c.body} + ${c.text}`);
});

test("removeLine", () => {
  for (const c of fixture.removeLine) assert.equal(checklist.removeLine(c.body, c.line), c.expect, c.body);
});

test("clearChecked", () => {
  for (const c of fixture.clearChecked) assert.equal(checklist.clearChecked(c.body), c.expect, c.body);
});

test("makeChecklist", () => {
  for (const c of fixture.makeChecklist) assert.equal(checklist.makeChecklist(c.body), c.expect, c.body);
});

test("continueOnEnter", () => {
  for (const c of fixture.continueOnEnter) assert.deepEqual(checklist.continueOnEnter(c.line), c.expect, c.line);
});

test("toggle flips just that line", () => {
  assert.equal(checklist.toggle("- [ ] a\n- [ ] b", 1), "- [ ] a\n- [x] b");
  assert.equal(checklist.toggle("- [X] a", 0), "- [ ] a");
  assert.equal(checklist.toggle("- [ ] a", 4), "- [ ] a");
});
