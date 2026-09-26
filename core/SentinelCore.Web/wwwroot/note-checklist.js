// Checklists inside a note. A note stays plain Markdown - an item is a line like "- [ ] milk" or
// "- [x] milk" - so these only rewrite lines of the body, and nothing about sync changes. They let
// a list be built and ticked off without typing the brackets: add an item, clear the done ones,
// turn a pasted list into checkboxes, and carry "- [ ] " onto the next line when Enter is pressed.
//
// The apps have the same rules: ios/OrganizerKit NoteChecklist.swift, android
// organizer/NoteChecklist.kt and windows SentinelCore.Client Organizer/NoteChecklist.cs. All four
// test suites read core/docs/note-checklist-cases.json. Change them together.
//
// Loads in the dashboard as window.SentinelNoteChecklist, and in Node (for the tests) as a module.
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.SentinelNoteChecklist = factory();
})(typeof self !== "undefined" ? self : this, function () {
  const ITEM = /^(\s*)([-*])\s+\[( |x|X)\]\s+(.*)$/;
  const BULLET = /^(\s*)([-*])\s+(.*)$/;
  const NUMBERED = /^(\s*)\d+[.)]\s+(.*)$/;
  const HEADING = /^#{1,6}\s/;
  const QUOTE = /^\s*>/;

  const isFence = (line) => line.trim().startsWith("```");

  // Each line with whether it sits inside a ``` block - code is never a checklist.
  function scan(body) {
    let inCode = false;
    return String(body || "").split("\n").map((text) => {
      if (isFence(text)) { inCode = !inCode; return { text, code: true }; }
      return { text, code: inCode };
    });
  }

  function item(line) {
    const m = ITEM.exec(line.text);
    return line.code || !m ? null : { done: m[3] !== " ", text: m[4] };
  }

  function progress(body) {
    let done = 0;
    let total = 0;
    for (const line of scan(body)) {
      const it = item(line);
      if (!it) continue;
      total++;
      if (it.done) done++;
    }
    return { done, total };
  }

  function toggle(body, index) {
    const lines = String(body || "").split("\n");
    if (lines[index] === undefined) return body;
    lines[index] = lines[index].replace(/\[( |x|X)\]/, (m, mark) => (mark === " " ? "[x]" : "[ ]"));
    return lines.join("\n");
  }

  // A new item goes after the last one, so it joins the list rather than landing under whatever
  // was written below it.
  function addItem(body, text) {
    const clean = String(text || "").trim();
    if (!clean) return body;
    const entry = `- [ ] ${clean}`;
    const lines = scan(body);
    let last = -1;
    lines.forEach((line, index) => { if (item(line)) last = index; });
    if (last >= 0) {
      const texts = lines.map((line) => line.text);
      texts.splice(last + 1, 0, entry);
      return texts.join("\n");
    }
    const trimmed = String(body || "").replace(/\n+$/, "");
    return trimmed.trim() ? `${trimmed}\n${entry}` : entry;
  }

  function removeLine(body, index) {
    const lines = String(body || "").split("\n");
    if (index < 0 || index >= lines.length) return body;
    lines.splice(index, 1);
    return lines.join("\n");
  }

  function clearChecked(body) {
    return scan(body).filter((line) => { const it = item(line); return !(it && it.done); }).map((line) => line.text).join("\n");
  }

  // Every plain line and bullet becomes an unticked item; headings, quotes, code and blank lines
  // are left alone so a list under a title stays a list under a title.
  function makeChecklist(body) {
    return scan(body).map((line) => {
      const text = line.text;
      if (line.code || !text.trim() || HEADING.test(text) || QUOTE.test(text) || item(line)) return text;
      let m;
      if ((m = BULLET.exec(text))) return `${m[1]}- [ ] ${m[3]}`;
      if ((m = NUMBERED.exec(text))) return `${m[1]}- [ ] ${m[2]}`;
      return `- [ ] ${text.trim()}`;
    }).join("\n");
  }

  // What Enter should do at the end of this line: carry the item or bullet on ({ insert }), end the
  // list when the item is empty ({ endList }), or nothing special (null).
  function continueOnEnter(line) {
    let m;
    if ((m = /^(\s*)([-*])\s+\[( |x|X)\](\s*)(.*)$/.exec(line))) {
      if (!m[4] && !m[5]) return null;
      return m[5].trim() ? { insert: `${m[1]}${m[2]} [ ] ` } : { endList: true };
    }
    if ((m = /^(\s*)([-*])(\s+)(.*)$/.exec(line))) {
      return m[4].trim() ? { insert: `${m[1]}${m[2]} ` } : { endList: true };
    }
    return null;
  }

  return { progress, toggle, addItem, removeLine, clearChecked, makeChecklist, continueOnEnter };
});
