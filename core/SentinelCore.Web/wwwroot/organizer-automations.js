// Board automations: a no-code rule builder for one board. People drag blocks from the palette
// into WHEN / ONLY IF / THEN (or click a block to add it), fill in the blanks, and read the rule
// back as a plain sentence. The rules run on the server (OrganizerAutomationEngine), so a card
// scheduled for 7am appears on every device at 7 with all of them asleep. This file is only the
// editor; the board itself stays in organizer.js.
window.SentinelBoardAutomations = (function () {
  const S = window.SentinelOrganizerSync;
  const BLOCK_TYPE = "application/x-sentinelcore-rule-block";
  const DAY_SHORT = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const TOKENS = [["{date}", "today's date"], ["{weekday}", "day of the week"], ["{month}", "month"], ["{card}", "the card's title"], ["{due}", "the card's due date"], ["{board}", "this board's name"]];
  const DUE_OFFSETS = [[0, "at the time it's due"], [15, "15 minutes before"], [60, "1 hour before"], [180, "3 hours before"], [1440, "1 day before"], [2880, "2 days before"], [10080, "1 week before"]];

  let board = null;
  let rules = [];
  let draft = null;         // the rule being edited, in the server's shape
  let dragging = null;      // { zone, type } from the palette, or { zone: "actions", from: index } when reordering
  let lastTextInput = null; // where a clicked {token} goes
  let previewTimer = null;
  let people = [];
  let channels = { pushReady: true, emailReady: true };

  const el = (id) => document.getElementById(id);
  const zone = () => Intl.DateTimeFormat().resolvedOptions().timeZone || "";

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function toast(message, success) {
    const node = el("toast");
    if (!node) return;
    node.textContent = message;
    node.className = `toast ${success ? "success" : "error"}`;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => { node.className = "toast hidden"; }, 3500);
  }

  // ---- the board's lists and labels -----------------------------------------------------------

  const byPosition = (a, b) => (a.fields.position || 0) - (b.fields.position || 0);

  function columnsOf(boardId) {
    return S.children(boardId).filter((c) => c.kind === "column").sort(byPosition);
  }

  function columnName(id) {
    const column = id && S.get(id);
    return column ? (column.fields.title || "list") : "(pick a list)";
  }

  // This board's lists first; other boards' lists underneath, for "move it to another board".
  function columnOptions(selected, allowAny) {
    const here = columnsOf(board.id);
    const others = S.all("board").filter((b) => b.id !== board.id && !b.fields.archived).sort(byPosition);
    let html = allowAny ? `<option value=""${!selected ? " selected" : ""}>any list</option>` : (!selected ? '<option value="" selected disabled>pick a list</option>' : "");
    html += here.map((c) => `<option value="${escapeHtml(c.id)}"${c.id === selected ? " selected" : ""}>${escapeHtml(c.fields.title || "list")}</option>`).join("");
    if (!allowAny) {
      for (const other of others) {
        const cols = columnsOf(other.id);
        if (!cols.length) continue;
        html += `<optgroup label="on ${escapeHtml(other.fields.title || "board")}">${cols.map((c) =>
          `<option value="${escapeHtml(c.id)}"${c.id === selected ? " selected" : ""}>${escapeHtml(c.fields.title || "list")}</option>`).join("")}</optgroup>`;
      }
    }
    return html;
  }

  function knownLabels() {
    const names = new Set();
    for (const column of columnsOf(board.id)) {
      for (const card of S.children(column.id).filter((c) => c.kind === "card")) {
        for (const label of card.fields.labels || []) if (label && label.name) names.add(label.name);
      }
    }
    return [...names].sort();
  }

  // ---- the blocks ----------------------------------------------------------------------------
  // Each block: what it looks like in the palette, the sentence it shows once placed (with blanks
  // to fill), and how it reads in the plain-words summary. `needsCard` blocks act on "the card".

  const time12 = (hhmm) => {
    const [h, m] = String(hhmm || "07:00").split(":").map(Number);
    const d = new Date(2000, 0, 1, h || 0, m || 0);
    return d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  };
  const dueWords = (inDays, time) => {
    if (inDays === null || inDays === undefined || inDays === "") return "no due date";
    const n = Number(inDays);
    const day = n === 0 ? "today" : n === 1 ? "tomorrow" : `in ${n} days`;
    return time ? `${day} at ${time12(time)}` : day;
  };
  const q = (text) => `“${text || "…"}”`;

  function scheduleMode(s) {
    if (s.repeat !== "daily") return s.repeat;
    const days = [...(s.days || [])].sort().join(",");
    if (!days) return "day";
    if (days === "1,2,3,4,5") return "weekdays";
    if (days === "0,6") return "weekends";
    return "days";
  }

  function scheduleWords(s) {
    const at = time12(s.time);
    switch (scheduleMode(s)) {
      case "day": return `Every day at ${at}`;
      case "weekdays": return `Every weekday at ${at}`;
      case "weekends": return `Every Saturday and Sunday at ${at}`;
      case "days": return `Every ${(s.days || []).slice().sort().map((d) => DAY_SHORT[d]).join(", ") || "…"} at ${at}`;
      case "monthly": return `${s.dayOfMonth === -1 || !s.dayOfMonth ? "On the last day" : `On day ${s.dayOfMonth}`} of every month at ${at}`;
      case "hourly": return `Every ${s.everyHours || 1} hour${(s.everyHours || 1) === 1 ? "" : "s"} from ${at} until midnight`;
      case "once": return `Once, on ${s.date ? new Date(`${s.date}T00:00`).toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" }) : "…"} at ${at}`;
      default: return "On a schedule";
    }
  }

  const TRIGGERS = {
    schedule: {
      icon: "⏰", label: "On a schedule", hint: "every morning, every Monday, once…",
      make: () => ({ type: "schedule", schedule: { repeat: "daily", time: "07:00", days: [], timeZone: zone() } }),
      render: (t) => {
        const s = t.schedule;
        const mode = scheduleMode(s);
        const modes = [["day", "every day"], ["weekdays", "every weekday"], ["weekends", "every weekend day"], ["days", "on certain days"], ["monthly", "every month"], ["hourly", "every few hours"], ["once", "just once"]];
        let extra = "";
        if (mode === "days") extra = daysPicker("schedule.days", s.days || []);
        if (mode === "monthly") {
          extra = `on <select class="field-input" data-f="schedule.dayOfMonth" data-kind="int">${
            Array.from({ length: 31 }, (_, i) => `<option value="${i + 1}"${s.dayOfMonth === i + 1 ? " selected" : ""}>day ${i + 1}</option>`).join("")
          }<option value="-1"${s.dayOfMonth === -1 || !s.dayOfMonth ? " selected" : ""}>the last day</option></select>`;
        }
        if (mode === "hourly") {
          extra = `every <select class="field-input" data-f="schedule.everyHours" data-kind="int">${
            [1, 2, 3, 4, 6, 8, 12].map((n) => `<option value="${n}"${(s.everyHours || 1) === n ? " selected" : ""}>${n} hour${n === 1 ? "" : "s"}</option>`).join("")
          }</select> starting`;
        }
        if (mode === "once") extra = `on <input type="date" class="field-input" data-f="schedule.date" value="${escapeHtml(s.date || "")}" />`;
        return `<select class="field-input" data-schedule-mode>${modes.map(([v, l]) => `<option value="${v}"${v === mode ? " selected" : ""}>${l}</option>`).join("")}</select>
          ${extra}
          at <input type="time" class="field-input" data-f="schedule.time" value="${escapeHtml(s.time || "07:00")}" />
          <div class="auto-next" id="auto-next"></div>`;
      },
      words: (t) => scheduleWords(t.schedule),
    },
    cardAdded: {
      icon: "＋", label: "When a card is added", hint: "to a list, or anywhere on the board",
      make: () => ({ type: "cardAdded", columnId: "" }),
      render: (t) => `to <select class="field-input" data-f="columnId">${columnOptions(t.columnId, true)}</select>`,
      words: (t) => `When a card is added to ${t.columnId ? columnName(t.columnId) : "any list"}`,
    },
    cardMoved: {
      icon: "⇢", label: "When a card is moved", hint: "into a particular list",
      make: () => ({ type: "cardMoved", columnId: lastColumnId() }),
      render: (t) => `into <select class="field-input" data-f="columnId">${columnOptions(t.columnId, true)}</select>`,
      words: (t) => `When a card is moved into ${t.columnId ? columnName(t.columnId) : "any list"}`,
    },
    cardCompleted: {
      icon: "✓", label: "When a card is marked done", hint: "ticked off anywhere on the board",
      make: () => ({ type: "cardCompleted" }),
      render: () => "",
      words: () => "When a card is marked done",
    },
    checklistCompleted: {
      icon: "☑", label: "When a checklist is finished", hint: "every item on a card ticked",
      make: () => ({ type: "checklistCompleted" }),
      render: () => "",
      words: () => "When every checklist item on a card is ticked",
    },
    cardDue: {
      icon: "⏳", label: "When a card is due", hint: "or a while before - a reminder",
      make: () => ({ type: "cardDue", minutesBefore: 60, columnId: "" }),
      render: (t) => `<select class="field-input" data-f="minutesBefore" data-kind="int">${DUE_OFFSETS.map(([v, l]) =>
          `<option value="${v}"${Number(t.minutesBefore || 0) === v ? " selected" : ""}>${l}</option>`).join("")}</select>
        for cards in <select class="field-input" data-f="columnId">${columnOptions(t.columnId, true)}</select>
        <div class="auto-next">A card with a date but no time counts as due at 9:00 AM that day. Finished cards are left alone.</div>`,
      words: (t) => {
        const offset = DUE_OFFSETS.find(([v]) => v === Number(t.minutesBefore || 0));
        const when = !t.minutesBefore ? "When a card is due" : `${offset ? offset[1][0].toUpperCase() + offset[1].slice(1) : `${t.minutesBefore} minutes before`} a card is due`;
        return `${when}${t.columnId ? ` in ${columnName(t.columnId)}` : ""}`;
      },
    },
    labelAdded: {
      icon: "🏷", label: "When a label is added", hint: "a particular label, or any",
      make: () => ({ type: "labelAdded", label: "" }),
      render: (t) => `called ${labelInput("label", t.label, "any label")}`,
      words: (t) => `When ${t.label ? `the ${q(t.label)} label` : "any label"} is added to a card`,
    },
  };

  const CONDITIONS = {
    titleContains: {
      icon: "Aa", label: "Title contains…", needsCard: true,
      make: () => ({ type: "titleContains", text: "" }),
      render: (c) => `the card's title contains ${textInput("text", c.text, "e.g. asap")}`,
      words: (c) => `the title contains ${q(c.text)}`,
    },
    hasLabel: {
      icon: "🏷", label: "Has a label", needsCard: true,
      make: () => ({ type: "hasLabel", text: "" }),
      render: (c) => `the card has the label ${labelInput("text", c.text, "label")}`,
      words: (c) => `it has the ${q(c.text)} label`,
    },
    lacksLabel: {
      icon: "⊘", label: "Doesn't have a label", needsCard: true,
      make: () => ({ type: "lacksLabel", text: "" }),
      render: (c) => `the card does not have the label ${labelInput("text", c.text, "label")}`,
      words: (c) => `it doesn't have the ${q(c.text)} label`,
    },
    inColumn: {
      icon: "▤", label: "Is in a list", needsCard: true,
      make: () => ({ type: "inColumn", columnId: firstColumnId() }),
      render: (c) => `the card is in <select class="field-input" data-f="columnId">${columnOptions(c.columnId)}</select>`,
      words: (c) => `it's in ${columnName(c.columnId)}`,
    },
    onDays: {
      icon: "📅", label: "Only on certain days",
      make: () => ({ type: "onDays", days: [1, 2, 3, 4, 5] }),
      render: (c) => `today is ${daysPicker("days", c.days || [])}`,
      words: (c) => `it's ${(c.days || []).slice().sort().map((d) => DAY_SHORT[d]).join(", ") || "…"}`,
    },
  };

  const ACTIONS = {
    createCard: {
      icon: "＋", label: "Create a card", hint: "then the steps below act on it",
      make: () => ({ type: "createCard", columnId: firstColumnId(), title: "", position: "bottom", skipIfExists: true }),
      render: (a) => `called ${textInput("title", a.title, "e.g. pick up eggs", true)}
        in <select class="field-input" data-f="columnId">${columnOptions(a.columnId)}</select>
        at the <select class="field-input" data-f="position"><option value="bottom"${a.position !== "top" ? " selected" : ""}>bottom</option><option value="top"${a.position === "top" ? " selected" : ""}>top</option></select>
        <div class="auto-more">
          <label class="org-check"><input type="checkbox" data-f="skipIfExists" data-kind="bool"${a.skipIfExists ? " checked" : ""}> skip if an unfinished card with this title is already there</label>
          <label class="org-check"><input type="checkbox" data-toggle-due${a.inDays !== null && a.inDays !== undefined ? " checked" : ""}> due</label>
          ${a.inDays !== null && a.inDays !== undefined ? dueFields(a) : ""}
          <div class="auto-sub">labels ${textInput("labels", (a.labels || []).join(", "), "comma separated", false, "csv")}</div>
          <div class="auto-sub">checklist <textarea class="field-textarea" rows="2" data-f="items" data-kind="lines" placeholder="one item per line">${escapeHtml((a.items || []).join("\n"))}</textarea></div>
          ${people.length ? `<div class="auto-sub">assign to ${peopleSelect(a.username, true)}</div>` : ""}
        </div>`,
      words: (a) => `create a card ${q(a.title)} in ${columnName(a.columnId)}${a.inDays !== null && a.inDays !== undefined ? `, due ${dueWords(a.inDays, a.time)}` : ""}${a.skipIfExists ? " (unless one is still there)" : ""}`,
    },
    moveCard: {
      icon: "⇢", label: "Move the card", needsCard: true,
      make: () => ({ type: "moveCard", columnId: lastColumnId(), position: "top" }),
      render: (a) => `to <select class="field-input" data-f="columnId">${columnOptions(a.columnId)}</select>
        at the <select class="field-input" data-f="position"><option value="top"${a.position !== "bottom" ? " selected" : ""}>top</option><option value="bottom"${a.position === "bottom" ? " selected" : ""}>bottom</option></select>`,
      words: (a) => `move it to the ${a.position === "bottom" ? "bottom" : "top"} of ${columnName(a.columnId)}`,
    },
    addLabel: {
      icon: "🏷", label: "Add a label", needsCard: true,
      make: () => ({ type: "addLabel", name: "", color: "" }),
      render: (a) => `${labelInput("name", a.name, "label")} colour <input type="color" class="auto-color" data-f="color" value="${escapeHtml(a.color || "#39ff9c")}" />`,
      words: (a) => `label it ${q(a.name)}`,
    },
    removeLabel: {
      icon: "⊘", label: "Remove a label", needsCard: true,
      make: () => ({ type: "removeLabel", name: "" }),
      render: (a) => labelInput("name", a.name, "label"),
      words: (a) => `take off the ${q(a.name)} label`,
    },
    setDue: {
      icon: "🕑", label: "Set the due date", needsCard: true,
      make: () => ({ type: "setDue", inDays: 1, time: "" }),
      render: (a) => `<label class="org-check"><input type="checkbox" data-toggle-due${a.inDays !== null && a.inDays !== undefined ? " checked" : ""}> set one (untick to clear it)</label> ${a.inDays !== null && a.inDays !== undefined ? dueFields(a) : ""}`,
      words: (a) => (a.inDays === null || a.inDays === undefined ? "clear its due date" : `make it due ${dueWords(a.inDays, a.time)}`),
    },
    complete: {
      icon: "✓", label: "Mark done / not done", needsCard: true,
      make: () => ({ type: "complete", done: true }),
      render: (a) => `as <select class="field-input" data-f="done" data-kind="boolselect"><option value="true"${a.done !== false ? " selected" : ""}>done</option><option value="false"${a.done === false ? " selected" : ""}>not done</option></select>`,
      words: (a) => (a.done === false ? "mark it not done" : "mark it done"),
    },
    addChecklist: {
      icon: "☑", label: "Add checklist items", needsCard: true,
      make: () => ({ type: "addChecklist", items: [] }),
      render: (a) => `<textarea class="field-textarea" rows="2" data-f="items" data-kind="lines" placeholder="one item per line">${escapeHtml((a.items || []).join("\n"))}</textarea>`,
      words: (a) => `add ${(a.items || []).length || "some"} checklist item${(a.items || []).length === 1 ? "" : "s"}`,
    },
    resetChecklist: {
      icon: "↺", label: "Untick the checklist", needsCard: true,
      make: () => ({ type: "resetChecklist" }),
      render: () => "so it can be done again",
      words: () => "untick its checklist",
    },
    addComment: {
      icon: "💬", label: "Add a comment", needsCard: true,
      make: () => ({ type: "addComment", text: "" }),
      render: (a) => textInput("text", a.text, "e.g. finished on {date}", true),
      words: (a) => `comment ${q(a.text)}`,
    },
    assign: {
      icon: "👤", label: "Assign someone", needsCard: true, needsPeople: true,
      make: () => ({ type: "assign", username: people[0] ? people[0].username : "" }),
      render: (a) => peopleSelect(a.username),
      words: (a) => `assign it to ${a.username || "…"}`,
    },
    deleteCard: {
      icon: "🗑", label: "Delete the card", needsCard: true,
      make: () => ({ type: "deleteCard" }),
      render: () => "for good",
      words: () => "delete it",
    },
    notify: {
      icon: "📣", label: "Send a notification", hint: "to phones - everyone's, or chosen people",
      make: () => ({ type: "notify", everyone: people.length === 0, people: [], title: "", text: "" }),
      render: (a) => `${channels.pushReady ? "" : '<div class="auto-warn-text auto-wide">Push notifications aren\'t set up on this server yet (Settings → Push), so this won\'t reach anyone until they are.</div>'}
        to ${peoplePicker(a, true)}
        <div class="auto-sub">title ${textInput("title", a.title, "optional - the board's name if empty", true)}</div>
        <div class="auto-sub">message ${textInput("text", a.text, "e.g. {card} is due {due}", true)}</div>`,
      words: (a) => `notify ${a.everyone ? "everyone" : whoWords(a.people)} “${a.text || "…"}”`,
    },
    email: {
      icon: "✉", label: "Send an email", hint: "to people here, or any address",
      make: () => ({ type: "email", people: [], addresses: [], title: "", text: "" }),
      render: (a) => `${channels.emailReady ? "" : '<div class="auto-warn-text auto-wide">Email isn\'t set up on this server yet (Settings → Email), so nothing will be sent until it is.</div>'}
        ${people.length ? `to ${peoplePicker(a, false)}` : ""}
        <div class="auto-sub">${people.length ? "and" : "to"} ${textInput("addresses", (a.addresses || []).join(", "), "other addresses, comma separated", false, "csv")}</div>
        <div class="auto-sub">subject ${textInput("title", a.title, "optional", true)}</div>
        <div class="auto-sub">message <textarea class="field-textarea auto-takes-tokens" rows="3" data-f="text" placeholder="e.g. {card} is due {due}">${escapeHtml(a.text || "")}</textarea></div>`,
      words: (a) => `email ${[whoWords(a.people, true), ...(a.addresses || [])].filter(Boolean).join(", ") || "…"} “${a.text || "…"}”`,
    },
    moveAllCards: {
      icon: "⇶", label: "Move every card in a list",
      make: () => ({ type: "moveAllCards", columnId: lastColumnId(), toColumnId: firstColumnId(), onlyCompleted: false }),
      render: (a) => `from <select class="field-input" data-f="columnId">${columnOptions(a.columnId)}</select>
        to <select class="field-input" data-f="toColumnId">${columnOptions(a.toColumnId)}</select>
        <label class="org-check"><input type="checkbox" data-f="onlyCompleted" data-kind="bool"${a.onlyCompleted ? " checked" : ""}> only the ones marked done</label>`,
      words: (a) => `move every ${a.onlyCompleted ? "finished " : ""}card from ${columnName(a.columnId)} to ${columnName(a.toColumnId)}`,
    },
    deleteAllCards: {
      icon: "🧹", label: "Clear out a list",
      make: () => ({ type: "deleteAllCards", columnId: lastColumnId(), onlyCompleted: true }),
      render: (a) => `<select class="field-input" data-f="columnId">${columnOptions(a.columnId)}</select>
        <label class="org-check"><input type="checkbox" data-f="onlyCompleted" data-kind="bool"${a.onlyCompleted ? " checked" : ""}> only the cards marked done</label>`,
      words: (a) => `delete every ${a.onlyCompleted ? "finished " : ""}card in ${columnName(a.columnId)}`,
    },
  };

  const ZONES = { trigger: TRIGGERS, conditions: CONDITIONS, actions: ACTIONS };

  function firstColumnId() { const c = columnsOf(board.id); return c.length ? c[0].id : ""; }
  function lastColumnId() { const c = columnsOf(board.id); return c.length ? c[c.length - 1].id : ""; }

  function textInput(field, value, placeholder, tokens, kind) {
    return `<input class="field-input auto-text${tokens ? " auto-takes-tokens" : ""}" data-f="${field}"${kind ? ` data-kind="${kind}"` : ""} value="${escapeHtml(value || "")}" placeholder="${escapeHtml(placeholder || "")}" />`;
  }

  function labelInput(field, value, placeholder) {
    return `<input class="field-input auto-text" list="auto-labels" data-f="${field}" value="${escapeHtml(value || "")}" placeholder="${escapeHtml(placeholder || "")}" />`;
  }

  function peopleSelect(selected, allowNone) {
    return `<select class="field-input" data-f="username">${allowNone ? `<option value=""${!selected ? " selected" : ""}>nobody</option>` : ""}${
      people.map((p) => `<option value="${escapeHtml(p.username)}"${p.username === selected ? " selected" : ""}>${escapeHtml(p.displayName || p.username)}</option>`).join("")}</select>`;
  }

  // Chips for choosing people. For a notification there's an "everyone" chip too; for email,
  // people with no address on their account are shown but can't be picked.
  function peoplePicker(a, allowEveryone) {
    const chosen = a.people || [];
    const chips = people.map((p) => {
      const noEmail = !allowEveryone && !p.hasEmail;
      const on = !a.everyone && chosen.includes(p.username);
      return `<button type="button" class="org-chip${on ? " active" : ""}" data-person="${escapeHtml(p.username)}"${noEmail ? ' disabled title="no email address on this account - add one under Accounts"' : ""}>${escapeHtml(p.displayName || p.username)}${noEmail ? " (no email)" : ""}</button>`;
    }).join("");
    const everyone = allowEveryone
      ? `<button type="button" class="org-chip${a.everyone ? " active" : ""}" data-everyone${people.length ? "" : " disabled title=\"with login off, every phone gets it\""}>everyone</button>`
      : "";
    return `<span class="auto-days" data-people>${everyone}${chips}</span>`;
  }

  function whoWords(usernames, quiet) {
    const names = (usernames || []).map((u) => {
      const person = people.find((p) => p.username === u);
      return person ? (person.displayName || person.username) : u;
    });
    if (!names.length) return quiet ? "" : "…";
    return names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
  }

  function daysPicker(field, days) {
    return `<span class="auto-days" data-days="${field}">${DAY_SHORT.map((name, i) =>
      `<button type="button" class="org-chip${days.includes(i) ? " active" : ""}" data-day="${i}">${name}</button>`).join("")}</span>`;
  }

  function dueFields(a) {
    return `<select class="field-input" data-f="inDays" data-kind="int">${[[0, "today"], [1, "tomorrow"], [2, "in 2 days"], [3, "in 3 days"], [7, "in a week"], [14, "in 2 weeks"], [30, "in 30 days"]]
      .map(([v, l]) => `<option value="${v}"${Number(a.inDays) === v ? " selected" : ""}>${l}</option>`).join("")}</select>
      <input type="time" class="field-input" data-f="time" value="${escapeHtml(a.time || "")}" title="leave empty for no time" />`;
  }

  // ---- recipes -------------------------------------------------------------------------------

  function recipes() {
    const first = firstColumnId();
    const last = lastColumnId();
    const tz = zone();
    return [
      {
        name: "Daily to-do", why: "a card every morning",
        rule: { name: "Pick up eggs every morning", trigger: { type: "schedule", schedule: { repeat: "daily", time: "07:00", days: [], timeZone: tz } }, conditions: [],
          actions: [{ type: "createCard", columnId: first, title: "pick up eggs", position: "bottom", skipIfExists: true }] }
      },
      {
        name: "Weekly chore", why: "every Sunday, with a checklist",
        rule: { name: "Sunday chores", trigger: { type: "schedule", schedule: { repeat: "daily", time: "09:00", days: [0], timeZone: tz } }, conditions: [],
          actions: [{ type: "createCard", columnId: first, title: "Chores for {date}", position: "bottom", skipIfExists: true, items: ["take out the trash", "water the plants", "vacuum"] }] }
      },
      {
        name: "Done means done", why: "moving to the last list ticks it off",
        rule: { name: `Moved to ${columnName(last)} → done`, trigger: { type: "cardMoved", columnId: last }, conditions: [],
          actions: [{ type: "complete", done: true }] }
      },
      {
        name: "Tidy up weekly", why: "clear finished cards each Monday",
        rule: { name: `Clear ${columnName(last)} on Mondays`, trigger: { type: "schedule", schedule: { repeat: "daily", time: "06:00", days: [1], timeZone: tz } }, conditions: [],
          actions: [{ type: "deleteAllCards", columnId: last, onlyCompleted: true }] }
      },
      {
        name: "Due tomorrow", why: "new cards get a due date",
        rule: { name: "New cards are due tomorrow", trigger: { type: "cardAdded", columnId: first }, conditions: [],
          actions: [{ type: "setDue", inDays: 1, time: "" }] }
      },
      {
        name: "Due reminder", why: "a notification an hour before a card is due",
        rule: { name: "Remind me before cards are due", trigger: { type: "cardDue", minutesBefore: 60, columnId: "" }, conditions: [],
          actions: [{ type: "notify", everyone: true, people: [], title: "", text: "{card} is due {due}" }] }
      },
      {
        name: "Bin night", why: "a notification every Sunday evening",
        rule: { name: "Bins out on Sunday", trigger: { type: "schedule", schedule: { repeat: "daily", time: "18:00", days: [0], timeZone: tz } }, conditions: [],
          actions: [{ type: "notify", everyone: true, people: [], title: "", text: "Take the bins out tonight" }] }
      },
      {
        name: "Urgent to the top", why: "the urgent label jumps the queue",
        rule: { name: "Urgent cards to the top", trigger: { type: "labelAdded", label: "urgent" }, conditions: [],
          actions: [{ type: "moveCard", columnId: first, position: "top" }] }
      },
    ];
  }

  // ---- the list of rules ----------------------------------------------------------------------

  function panel() { return el("org-auto-panel"); }

  async function open(boardItem) {
    board = boardItem;
    el("org-auto-modal").classList.remove("hidden");
    panel().innerHTML = '<div class="org-empty">loading…</div>';
    if (S.status() && S.status().personalSpaces) {
      try {
        const response = await fetch("/api/organizer/people");
        people = response.ok ? (await response.json()).people : [];
      } catch {
        people = [];
      }
    }
    await load();
  }

  function close() {
    el("org-auto-modal").classList.add("hidden");
    panel().innerHTML = "";
    draft = null;
    clearTimeout(previewTimer);
  }

  async function load() {
    try {
      const response = await fetch(`/api/organizer/automations?boardId=${encodeURIComponent(board.id)}`);
      if (!response.ok) throw new Error();
      const data = await response.json();
      rules = data.automations || [];
      channels = { pushReady: data.pushReady !== false, emailReady: data.emailReady !== false };
      renderList();
    } catch {
      panel().innerHTML = `<div class="zone-editor-header"><span>AUTOMATIONS</span><button class="org-x" data-auto-close aria-label="close">&times;</button></div>
        <div class="org-empty">Automations need the server - they can't be changed offline.</div>`;
      panel().querySelector("[data-auto-close]").addEventListener("click", close);
    }
  }

  function ruleSentence(rule) {
    const t = TRIGGERS[rule.trigger.type];
    const when = t ? t.words(rule.trigger) : "When…";
    const ifs = (rule.conditions || []).map((c) => (CONDITIONS[c.type] ? CONDITIONS[c.type].words(c) : c.type));
    const thens = (rule.actions || []).map((a) => (ACTIONS[a.type] ? ACTIONS[a.type].words(a) : a.type));
    return `${when}${ifs.length ? `, if ${ifs.join(" and ")}` : ""}, ${thens.length ? thens.join(", then ") : "do…"}.`;
  }

  function formatWhen(iso) {
    const date = new Date(iso);
    return date.toLocaleString([], { weekday: "short", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
  }

  function renderList() {
    panel().innerHTML = `
      <div class="zone-editor-header"><span>AUTOMATIONS · ${escapeHtml(board.fields.title || "board")}</span><button class="org-x" data-auto-close aria-label="close">&times;</button></div>
      <p class="auto-intro">Rules that look after this board for you. They run on the server, so they happen even when every phone and computer is off.</p>
      <div class="auto-recipes">
        <button class="btn" id="auto-new">+ build a rule</button>
        <span class="auto-recipes-label">or start from a recipe:</span>
        ${recipes().map((r, i) => `<button class="auto-recipe" data-recipe="${i}"><b>${escapeHtml(r.name)}</b><span>${escapeHtml(r.why)}</span></button>`).join("")}
      </div>
      <div class="auto-rules">
        ${rules.length ? rules.map(ruleRow).join("") : '<div class="org-empty">No rules yet. Build one, or pick a recipe above and change it to suit.</div>'}
      </div>`;

    panel().querySelector("[data-auto-close]").addEventListener("click", close);
    el("auto-new").addEventListener("click", () => edit(null));
    for (const button of panel().querySelectorAll("[data-recipe]")) {
      button.addEventListener("click", () => edit(null, recipes()[Number(button.dataset.recipe)].rule));
    }
    for (const row of panel().querySelectorAll("[data-rule]")) {
      const rule = rules.find((r) => r.id === row.dataset.rule);
      row.querySelector("[data-edit]").addEventListener("click", () => edit(rule));
      row.querySelector("[data-delete]").addEventListener("click", () => remove(rule));
      row.querySelector("[data-history]").addEventListener("click", () => history(rule, row));
      row.querySelector("[data-enabled]").addEventListener("change", (event) => setEnabled(rule, event.target.checked));
      const run = row.querySelector("[data-run]");
      if (run) run.addEventListener("click", () => runNow(rule));
    }
  }

  function ruleRow(rule) {
    const scheduled = rule.trigger.type === "schedule";
    const status = rule.lastRunAt
      ? `<span class="${rule.lastOk ? "auto-ok" : "auto-bad"}">${rule.lastOk ? "✓" : "!"} ${escapeHtml(formatWhen(rule.lastRunAt))}: ${escapeHtml(rule.lastResult || "")}</span>`
      : '<span class="auto-dim">hasn\'t run yet</span>';
    return `<div class="auto-rule${rule.enabled ? "" : " auto-off"}" data-rule="${escapeHtml(rule.id)}">
      <label class="auto-switch" title="on / off"><input type="checkbox" data-enabled${rule.enabled ? " checked" : ""}><span></span></label>
      <div class="auto-rule-body">
        <div class="auto-rule-name">${escapeHtml(rule.name)}</div>
        <div class="auto-rule-sentence">${escapeHtml(ruleSentence(rule))}</div>
        <div class="auto-rule-status">
          ${scheduled && rule.enabled && rule.nextRunAt ? `<span>next: ${escapeHtml(formatWhen(rule.nextRunAt))}</span>` : ""}
          ${status}
        </div>
        <div class="auto-history hidden"></div>
      </div>
      <div class="auto-rule-actions">
        ${scheduled ? '<button class="btn btn-small" data-run title="do it right now, to try it out">run now</button>' : ""}
        <button class="btn btn-small" data-edit>edit</button>
        <button class="btn btn-small" data-history>history</button>
        <button class="btn btn-small org-danger" data-delete>delete</button>
      </div>
    </div>`;
  }

  async function send(method, url, body) {
    const response = await fetch(url, {
      method,
      headers: body ? { "Content-Type": "application/json" } : {},
      body: body ? JSON.stringify(body) : undefined
    });
    let data = null;
    try { data = await response.json(); } catch { /* empty body */ }
    if (!response.ok) {
      const errors = (data && (data.errors || (data.error ? [data.error] : null))) || [`The server said ${response.status}.`];
      throw Object.assign(new Error(errors.join(" ")), { errors });
    }
    return data;
  }

  async function setEnabled(rule, enabled) {
    try {
      await send("PUT", `/api/organizer/automations/${encodeURIComponent(rule.id)}`, { ...rule, enabled });
      await load();
    } catch (error) {
      toast(error.message, false);
      await load();
    }
  }

  async function runNow(rule) {
    try {
      const run = await send("POST", `/api/organizer/automations/${encodeURIComponent(rule.id)}/run`);
      toast(run.message || "Done.", run.ok);
      S.sync();
      await load();
    } catch (error) {
      toast(error.message, false);
    }
  }

  async function remove(rule) {
    if (!confirm(`Delete the rule "${rule.name}"? Cards it already made stay where they are.`)) return;
    try {
      await send("DELETE", `/api/organizer/automations/${encodeURIComponent(rule.id)}`);
      await load();
    } catch (error) {
      toast(error.message, false);
    }
  }

  async function history(rule, row) {
    const box = row.querySelector(".auto-history");
    if (!box.classList.contains("hidden")) { box.classList.add("hidden"); return; }
    box.classList.remove("hidden");
    box.innerHTML = "loading…";
    try {
      const { runs } = await send("GET", `/api/organizer/automations/${encodeURIComponent(rule.id)}/runs`);
      box.innerHTML = runs.length
        ? runs.map((r) => `<div class="${r.ok ? "" : "auto-bad"}">${r.ok ? "✓" : "!"} ${escapeHtml(formatWhen(r.at))} - ${escapeHtml(r.message)}</div>`).join("")
        : '<div class="auto-dim">Nothing yet.</div>';
    } catch (error) {
      box.textContent = error.message;
    }
  }

  // ---- the editor ----------------------------------------------------------------------------

  function edit(rule, recipe) {
    const source = rule || recipe || { name: "", trigger: null, conditions: [], actions: [] };
    draft = JSON.parse(JSON.stringify({
      id: rule ? rule.id : null,
      name: source.name || "",
      enabled: rule ? rule.enabled : true,
      boardId: board.id,
      trigger: source.trigger,
      conditions: source.conditions || [],
      actions: source.actions || []
    }));
    panel().innerHTML = `
      <div class="zone-editor-header"><span>${rule ? "EDIT RULE" : "NEW RULE"} · ${escapeHtml(board.fields.title || "board")}</span><button class="org-x" data-auto-close aria-label="close">&times;</button></div>
      <input class="field-input org-title-input" id="auto-name" placeholder="name this rule, e.g. Eggs every morning" value="${escapeHtml(draft.name)}" />
      <datalist id="auto-labels">${knownLabels().map((l) => `<option value="${escapeHtml(l)}">`).join("")}</datalist>
      <div class="auto-builder">
        <aside class="auto-palette" aria-label="blocks">
          <p class="auto-dim">Drag a block into the rule, or click it.</p>
          ${paletteGroup("trigger", "WHEN", TRIGGERS)}
          ${paletteGroup("conditions", "ONLY IF", CONDITIONS)}
          ${paletteGroup("actions", "THEN", ACTIONS)}
          <div class="auto-palette-group">
            <div class="auto-palette-title">WORDS THAT FILL THEMSELVES IN</div>
            <div class="auto-tokens">${TOKENS.map(([token, what]) => `<span class="auto-token" draggable="true" data-token="${token}" title="${what} - drag into a title or comment, or click">${token}</span>`).join("")}</div>
          </div>
        </aside>
        <section class="auto-canvas" id="auto-canvas"></section>
      </div>
      <div class="auto-summary"><span class="auto-dim">In plain words:</span> <span id="auto-sentence"></span></div>
      <ul class="field-errors" id="auto-errors"></ul>
      <div class="zone-editor-footer">
        <button class="btn" id="auto-save">save rule</button>
        <button class="btn" id="auto-cancel">back</button>
      </div>`;

    panel().querySelector("[data-auto-close]").addEventListener("click", close);
    el("auto-cancel").addEventListener("click", renderList);
    el("auto-save").addEventListener("click", save);
    el("auto-name").addEventListener("input", (event) => { draft.name = event.target.value; });
    bindPalette();
    renderCanvas();
  }

  function paletteGroup(zoneName, title, blocks) {
    return `<div class="auto-palette-group">
      <div class="auto-palette-title auto-${zoneName}-title">${title}</div>
      ${Object.entries(blocks).filter(([, b]) => !b.needsPeople || people.length).map(([type, b]) =>
        `<div class="auto-block auto-block-${zoneName}" draggable="true" tabindex="0" role="button" data-zone="${zoneName}" data-type="${type}">
          <span class="auto-icon">${b.icon}</span><span><b>${escapeHtml(b.label)}</b>${b.hint ? `<small>${escapeHtml(b.hint)}</small>` : ""}</span>
        </div>`).join("")}
    </div>`;
  }

  function bindPalette() {
    for (const block of panel().querySelectorAll(".auto-palette .auto-block")) {
      block.addEventListener("dragstart", (event) => {
        dragging = { zone: block.dataset.zone, type: block.dataset.type };
        event.dataTransfer.effectAllowed = "copy";
        event.dataTransfer.setData(BLOCK_TYPE, JSON.stringify(dragging));
        highlightZones(true);
      });
      block.addEventListener("dragend", () => { dragging = null; highlightZones(false); });
      const add = () => addBlock(block.dataset.zone, block.dataset.type, null);
      block.addEventListener("click", add);
      block.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); add(); } });
    }
    for (const token of panel().querySelectorAll(".auto-token")) {
      token.addEventListener("dragstart", (event) => event.dataTransfer.setData("text/plain", token.dataset.token));
      token.addEventListener("mousedown", (event) => event.preventDefault()); // keep the text box focused
      token.addEventListener("click", () => insertToken(token.dataset.token));
    }
  }

  function insertToken(token) {
    const input = lastTextInput && document.body.contains(lastTextInput) ? lastTextInput : panel().querySelector(".auto-takes-tokens");
    if (!input) { toast("Add a card title or comment first, then drop the word into it.", false); return; }
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? input.value.length;
    input.value = input.value.slice(0, start) + token + input.value.slice(end);
    input.focus();
    input.setSelectionRange(start + token.length, start + token.length);
    input.dispatchEvent(new Event("input", { bubbles: true }));
  }

  function highlightZones(on) {
    for (const drop of panel().querySelectorAll("[data-drop]")) {
      drop.classList.toggle("auto-drop-ready", on && dragging && dragging.zone === drop.dataset.drop);
      if (!on) drop.classList.remove("auto-drop-over");
    }
    for (const marker of panel().querySelectorAll(".auto-insert")) marker.remove();
  }

  // What the rule has on hand to act on at each step: a card rule starts with one, a schedule
  // only once a "create a card" step has run.
  function cardAvailableBefore(index) {
    const before = draft.actions.slice(0, index).map((a) => a.type);
    const lastCreate = before.lastIndexOf("createCard");
    const lastDelete = before.lastIndexOf("deleteCard");
    if (draft.trigger && draft.trigger.type === "schedule") return lastCreate > lastDelete;
    return lastDelete === -1 || lastCreate > lastDelete;
  }

  function renderCanvas() {
    const canvas = el("auto-canvas");
    const scheduled = draft.trigger && draft.trigger.type === "schedule";
    const trigger = draft.trigger && TRIGGERS[draft.trigger.type];

    canvas.innerHTML = `
      <div class="auto-slot auto-slot-trigger">
        <div class="auto-slot-title auto-trigger-title">WHEN</div>
        <div class="auto-drop" data-drop="trigger">
          ${trigger ? placedBlock("trigger", 0, trigger, draft.trigger) : '<div class="auto-placeholder">drop a WHEN block here - what starts the rule?</div>'}
        </div>
      </div>
      <div class="auto-slot auto-slot-conditions">
        <div class="auto-slot-title auto-conditions-title">ONLY IF <span class="auto-dim">(optional)</span></div>
        <div class="auto-drop" data-drop="conditions">
          ${draft.conditions.map((c, i) => CONDITIONS[c.type] ? placedBlock("conditions", i, CONDITIONS[c.type], c) : "").join("")}
          <div class="auto-placeholder">${draft.conditions.length ? "drop another to narrow it further" : "drop ONLY IF blocks here to narrow it down"}</div>
        </div>
      </div>
      <div class="auto-slot auto-slot-actions">
        <div class="auto-slot-title auto-actions-title">THEN <span class="auto-dim">(in this order - drag to reorder)</span></div>
        <div class="auto-drop" data-drop="actions">
          ${draft.actions.map((a, i) => ACTIONS[a.type] ? placedBlock("actions", i, ACTIONS[a.type], a) : "").join("")}
          <div class="auto-placeholder">${draft.actions.length ? "drop another step here" : "drop THEN blocks here - what should happen?"}</div>
        </div>
      </div>`;

    // Warn, in place, about steps that would have no card to act on.
    draft.actions.forEach((action, index) => {
      const def = ACTIONS[action.type];
      if (def && def.needsCard && !cardAvailableBefore(index)) {
        const node = canvas.querySelector(`[data-placed="actions"][data-index="${index}"]`);
        if (node) {
          node.classList.add("auto-warn");
          node.insertAdjacentHTML("beforeend", `<div class="auto-warn-text">${scheduled ? "A schedule has no card of its own - put “Create a card” above this step." : "The card was deleted by an earlier step."}</div>`);
        }
      }
    });
    for (const [i, c] of draft.conditions.entries()) {
      if (scheduled && CONDITIONS[c.type] && CONDITIONS[c.type].needsCard) {
        const node = canvas.querySelector(`[data-placed="conditions"][data-index="${i}"]`);
        if (node) {
          node.classList.add("auto-warn");
          node.insertAdjacentHTML("beforeend", '<div class="auto-warn-text">A schedule has no card to check - this would never be true.</div>');
        }
      }
    }

    bindCanvas();
    renderSentence();
    queuePreview();
  }

  function placedBlock(zoneName, index, def, block) {
    const movable = zoneName === "actions";
    const count = zoneName === "actions" ? draft.actions.length : draft.conditions.length;
    return `<div class="auto-placed auto-block-${zoneName}" data-placed="${zoneName}" data-index="${index}"${movable ? ' draggable="true"' : ""}>
      <div class="auto-placed-head">
        ${movable ? '<span class="auto-grip" title="drag to reorder">⋮⋮</span>' : ""}
        <span class="auto-icon">${def.icon}</span>
        <b>${escapeHtml(def.label)}</b>
        <span class="org-spacer"></span>
        ${movable && index > 0 ? '<button class="org-x" data-up aria-label="move up">▲</button>' : ""}
        ${movable && index < count - 1 ? '<button class="org-x" data-down aria-label="move down">▼</button>' : ""}
        <button class="org-x" data-remove aria-label="remove">&times;</button>
      </div>
      <div class="auto-placed-body">${def.render(block)}</div>
    </div>`;
  }

  function blockAt(zoneName, index) {
    return zoneName === "trigger" ? draft.trigger : draft[zoneName][index];
  }

  function setPath(target, path, value) {
    const parts = path.split(".");
    let node = target;
    for (const part of parts.slice(0, -1)) node = node[part] = node[part] || {};
    node[parts[parts.length - 1]] = value;
  }

  function readInput(input) {
    switch (input.dataset.kind) {
      case "bool": return input.checked;
      case "boolselect": return input.value === "true";
      case "int": return input.value === "" ? null : Number(input.value);
      case "csv": return input.value.split(",").map((s) => s.trim()).filter(Boolean);
      case "lines": return input.value.split("\n").map((s) => s.trim()).filter(Boolean);
      default: return input.value;
    }
  }

  function bindCanvas() {
    const canvas = el("auto-canvas");

    for (const node of canvas.querySelectorAll("[data-placed]")) {
      const zoneName = node.dataset.placed;
      const index = Number(node.dataset.index);
      const block = blockAt(zoneName, index);

      for (const input of node.querySelectorAll("[data-f]")) {
        const structural = input.tagName === "SELECT" || input.type === "checkbox";
        input.addEventListener(structural ? "change" : "input", () => {
          setPath(block, input.dataset.f, readInput(input));
          renderSentence();
          if (input.dataset.f.startsWith("schedule.")) queuePreview();
        });
        if (input.classList.contains("auto-takes-tokens")) input.addEventListener("focus", () => { lastTextInput = input; });
      }

      const mode = node.querySelector("[data-schedule-mode]");
      if (mode) {
        mode.addEventListener("change", () => {
          const s = block.schedule;
          const choice = mode.value;
          s.repeat = ["monthly", "hourly", "once"].includes(choice) ? choice : "daily";
          if (choice === "day") s.days = [];
          if (choice === "weekdays") s.days = [1, 2, 3, 4, 5];
          if (choice === "weekends") s.days = [0, 6];
          if (choice === "days") s.days = s.days && s.days.length ? s.days : [1];
          if (choice === "monthly") { s.days = []; s.dayOfMonth = s.dayOfMonth || 1; }
          if (choice === "hourly") { s.days = []; s.everyHours = s.everyHours || 3; }
          if (choice === "once") { s.days = []; if (!s.date) { const t = new Date(Date.now() + 86400000); s.date = `${t.getFullYear()}-${String(t.getMonth() + 1).padStart(2, "0")}-${String(t.getDate()).padStart(2, "0")}`; } }
          renderCanvas();
        });
      }

      for (const picker of node.querySelectorAll("[data-days]")) {
        for (const chip of picker.querySelectorAll("[data-day]")) {
          chip.addEventListener("click", () => {
            const path = picker.dataset.days;
            const current = path.split(".").reduce((n, p) => n && n[p], block) || [];
            const day = Number(chip.dataset.day);
            const next = current.includes(day) ? current.filter((d) => d !== day) : [...current, day].sort();
            // Unticking the last day would silently mean "every day"; keep at least one.
            if (!next.length) return;
            setPath(block, path, next);
            renderCanvas();
          });
        }
      }

      const picker = node.querySelector("[data-people]");
      if (picker) {
        for (const chip of picker.querySelectorAll("[data-person]")) {
          chip.addEventListener("click", () => {
            const name = chip.dataset.person;
            const list = block.people || [];
            block.people = list.includes(name) ? list.filter((p) => p !== name) : [...list, name];
            block.everyone = false;
            renderCanvas();
          });
        }
        const everyone = picker.querySelector("[data-everyone]");
        if (everyone) {
          everyone.addEventListener("click", () => {
            block.everyone = !block.everyone;
            if (block.everyone) block.people = [];
            renderCanvas();
          });
        }
      }

      const dueToggle = node.querySelector("[data-toggle-due]");
      if (dueToggle) {
        dueToggle.addEventListener("change", () => {
          block.inDays = dueToggle.checked ? 1 : null;
          if (!dueToggle.checked) block.time = "";
          renderCanvas();
        });
      }

      node.querySelector("[data-remove]").addEventListener("click", () => {
        if (zoneName === "trigger") draft.trigger = null;
        else draft[zoneName].splice(index, 1);
        renderCanvas();
      });
      const up = node.querySelector("[data-up]");
      if (up) up.addEventListener("click", () => { moveAction(index, index - 1); });
      const down = node.querySelector("[data-down]");
      if (down) down.addEventListener("click", () => { moveAction(index, index + 2); });

      if (zoneName === "actions") {
        node.addEventListener("dragstart", (event) => {
          // Only from the block itself, not while selecting text in one of its boxes.
          if (event.target !== node) return;
          dragging = { zone: "actions", from: index };
          event.dataTransfer.effectAllowed = "move";
          event.dataTransfer.setData(BLOCK_TYPE, JSON.stringify(dragging));
          node.classList.add("auto-dragging");
          highlightZones(true);
        });
        node.addEventListener("dragend", () => { node.classList.remove("auto-dragging"); dragging = null; highlightZones(false); });
      }
    }

    for (const drop of canvas.querySelectorAll("[data-drop]")) {
      drop.addEventListener("dragover", (event) => {
        if (!dragging || dragging.zone !== drop.dataset.drop) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = dragging.from !== undefined ? "move" : "copy";
        drop.classList.add("auto-drop-over");
        if (drop.dataset.drop !== "trigger") showInsertMarker(drop, event.clientY);
      });
      drop.addEventListener("dragleave", (event) => {
        if (!drop.contains(event.relatedTarget)) {
          drop.classList.remove("auto-drop-over");
          for (const marker of drop.querySelectorAll(".auto-insert")) marker.remove();
        }
      });
      drop.addEventListener("drop", (event) => {
        if (!dragging || dragging.zone !== drop.dataset.drop) return;
        event.preventDefault();
        const at = insertIndex(drop, event.clientY);
        const info = dragging;
        dragging = null;
        highlightZones(false);
        if (info.from !== undefined) moveAction(info.from, at);
        else addBlock(info.zone, info.type, at);
      });
    }
  }

  // Where among the placed blocks the pointer is: before the first whose middle is below it.
  function insertIndex(drop, y) {
    const placed = [...drop.querySelectorAll("[data-placed]")];
    const hit = placed.findIndex((node) => {
      const box = node.getBoundingClientRect();
      return y < box.top + box.height / 2;
    });
    return hit === -1 ? placed.length : hit;
  }

  function showInsertMarker(drop, y) {
    for (const marker of drop.querySelectorAll(".auto-insert")) marker.remove();
    const placed = [...drop.querySelectorAll("[data-placed]")];
    const at = insertIndex(drop, y);
    const marker = document.createElement("div");
    marker.className = "auto-insert";
    if (at < placed.length) placed[at].before(marker);
    else if (placed.length) placed[placed.length - 1].after(marker);
    else drop.prepend(marker);
  }

  function addBlock(zoneName, type, at) {
    const def = ZONES[zoneName][type];
    if (!def) return;
    const block = def.make();
    if (zoneName === "trigger") {
      draft.trigger = block;
      if (!draft.name) {
        draft.name = def.label;
        el("auto-name").value = draft.name;
      }
    } else {
      const list = draft[zoneName];
      list.splice(at === null || at === undefined ? list.length : at, 0, block);
    }
    renderCanvas();
    const placed = el("auto-canvas").querySelector(zoneName === "trigger"
      ? '[data-placed="trigger"]'
      : `[data-placed="${zoneName}"][data-index="${at === null || at === undefined ? draft[zoneName].length - 1 : at}"]`);
    if (placed) {
      placed.classList.add("auto-just-added");
      const first = placed.querySelector("input, select, textarea");
      if (first && first.type !== "checkbox") first.focus();
    }
  }

  function moveAction(from, to) {
    // `to` is an insertion point in the list as it was, before `from` came out.
    const [moved] = draft.actions.splice(from, 1);
    draft.actions.splice(to > from ? to - 1 : to, 0, moved);
    renderCanvas();
  }

  function renderSentence() {
    const target = el("auto-sentence");
    if (!target) return;
    target.textContent = draft.trigger || draft.actions.length ? ruleSentence({ ...draft, trigger: draft.trigger || { type: "" } }) : "drag a WHEN block and a THEN block into the rule to get started.";
  }

  // The next few times it will run, from the server, so they're what will really happen.
  function queuePreview() {
    clearTimeout(previewTimer);
    if (!draft.trigger || draft.trigger.type !== "schedule") return;
    previewTimer = setTimeout(async () => {
      const target = el("auto-next");
      if (!target) return;
      try {
        const { runs } = await send("POST", "/api/organizer/automations/preview", draft.trigger.schedule);
        target.textContent = runs.length ? `next: ${runs.map(formatWhen).join("  ·  ")}` : "this won't run - that time has passed";
        target.classList.toggle("auto-bad", !runs.length);
      } catch {
        target.textContent = "";
      }
    }, 250);
  }

  async function save() {
    draft.name = el("auto-name").value.trim();
    const errors = [];
    if (!draft.trigger) errors.push("Drag a WHEN block in - what starts the rule?");
    if (!draft.actions.length) errors.push("Drag at least one THEN block in - what should happen?");
    if (!draft.name) errors.push("Give the rule a name.");
    if (errors.length) { showErrors(errors); return; }

    const body = { ...draft, timeZone: zone() };
    delete body.id;
    try {
      if (draft.id) await send("PUT", `/api/organizer/automations/${encodeURIComponent(draft.id)}`, body);
      else await send("POST", "/api/organizer/automations", body);
      toast("Rule saved.", true);
      draft = null;
      await load();
    } catch (error) {
      showErrors(error.errors || [error.message]);
    }
  }

  function showErrors(errors) {
    el("auto-errors").innerHTML = errors.map((e) => `<li>${escapeHtml(e)}</li>`).join("");
  }

  window.addEventListener("DOMContentLoaded", () => {
    const modal = el("org-auto-modal");
    if (!modal) return;
    modal.addEventListener("mousedown", (event) => { if (event.target === modal && !draft) close(); });
    document.addEventListener("keydown", (event) => { if (event.key === "Escape" && !modal.classList.contains("hidden") && !draft) close(); });
  });

  return { open };
})();
