// A spoken reminder split into what to do and when: "add a reminder to walk the dog tomorrow at
// 1" is "Walk the dog", due tomorrow at 1 PM. The browser's speech recognizer hands the dashboard
// the words as said, so the date has to come out of them here - by plain rules, no model, so it
// is instant and works with no server.
//
// The phone apps have the same rules: ios/OrganizerKit VoicePhrase.swift and android
// organizer/VoicePhrase.kt. All three test suites read core/docs/voice-phrase-cases.json, so a
// phrase means the same thing wherever it was heard. Change them together.
//
// Loads in the dashboard as window.SentinelVoicePhrase, and in Node (for the tests) as a module.
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.SentinelVoicePhrase = factory();
})(typeof self !== "undefined" ? self : this, function () {
  // Morning, afternoon, evening and night as clock hours - Apple Reminders' choices.
  const PERIOD_HOURS = { morning: 9, afternoon: 14, evening: 18, night: 20, tonight: 20 };
  const NUMBER_WORDS = {
    a: 1, an: 1, one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8, nine: 9, ten: 10,
    eleven: 11, twelve: 12, fifteen: 15, twenty: 20, thirty: 30, "forty-five": 45, "forty five": 45, ninety: 90,
  };
  const WEEKDAYS = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
  const MONTHS = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

  const NUMBER = "(half an|an?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty[- ]five|ninety|\\d+)";
  const RELATIVE = new RegExp("\\b(?:in|within)\\s+" + NUMBER + "\\s+(minutes?|mins?|hours?|hrs?|days?|weeks?)\\b", "gi");
  const DAY = /\b(?:(?:on|by|this|next)\s+)?(today|tonight|tomorrow|sunday|monday|tuesday|wednesday|thursday|friday|saturday)(?:\s+(morning|afternoon|evening|night))?\b/gi;
  const THIS_PERIOD = /\bthis\s+(morning|afternoon|evening)\b/gi;
  const MONTH_DATE = /\b(?:(?:on|by)\s+)?(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec)\.?\s+(\d{1,2})(?:st|nd|rd|th)?\b/gi;
  const ORDINAL_DAY = /\b(?:(?:on|by)\s+)?the\s+(\d{1,2})(?:st|nd|rd|th)\b/gi;
  const MERIDIEM_TIME = /\b(?:(?:at|by|around)\s+)?(\d{1,2})(?::(\d{2}))?\s*([ap])\.?\s?m\b\.?/gi;
  const AT_TIME = /\b(?:at|by|around)\s+(\d{1,2})(?::(\d{2}))?(?:\s*o'?clock)?\b/gi;
  const NAMED_TIME = /\b(?:(?:at|by|around)\s+)?(noon|midnight)\b/gi;
  const PERIOD_TIME = /\b(?:in\s+the\s+(morning|afternoon|evening)|at\s+(night))\b/gi;
  const CLOCK_TIME = /\b(\d{1,2}):(\d{2})\b/gi;
  // "Remind me to…", "add a reminder to…": the words that mean "this is a reminder" rather than
  // what it is about.
  const LEAD = /^\s*(?:please\s+)?(?:remind\s+me\s+(?:to\s+|that\s+)?|(?:(?:add|set|create|make|new|put)\s+(?:a\s+|an?\s+new\s+|new\s+)?)?reminder\s+(?:to\s+|that\s+|for\s+(?:me\s+to\s+)?)?)/i;
  const DANGLING = /\s+(?:at|on|by|in|around|due)\s*$/i;

  const pad = (n) => String(n).padStart(2, "0");
  const ymd = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  // Local time with its offset, the way organizer.js writes every timed field.
  function isoLocal(d) {
    const offset = -d.getTimezoneOffset();
    const sign = offset >= 0 ? "+" : "-";
    const abs = Math.abs(offset);
    return `${ymd(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}:00${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
  }
  const startOfDay = (d) => new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const addDays = (d, n) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
  const at = (day, hour, minute) => new Date(day.getFullYear(), day.getMonth(), day.getDate(), hour, minute);
  const roundedToMinute = (d) => { const r = new Date(d); r.setSeconds(0, 0); return r; };
  function validDate(year, month, day) {
    const d = new Date(year, month - 1, day);
    return d.getMonth() === month - 1 && d.getDate() === day ? d : null;
  }

  function parse(text, now) {
    now = now || new Date();
    const removed = [];
    const today = startOfDay(now);

    // The first match that doesn't overlap the range already taken by the day words.
    function first(expression, avoiding) {
      expression.lastIndex = 0;
      for (const match of text.matchAll(expression)) {
        const start = match.index;
        const end = start + match[0].length;
        if (avoiding && start < avoiding[1] && end > avoiding[0]) continue;
        return { match, range: [start, end] };
      }
      return null;
    }
    const group = (m, i) => (m.match[i] == null ? null : m.match[i].toLowerCase());

    function finish(due, hasTime) {
      let title = text;
      for (const [start, end] of removed.sort((a, b) => b[0] - a[0])) {
        title = title.slice(0, start) + " " + title.slice(end);
      }
      title = title.replace(LEAD, "");
      title = title.split(/\s+/).filter(Boolean).join(" ");
      for (;;) {
        const trimmed = title.replace(DANGLING, "").replace(/^[ ,.;:-]+|[ ,.;:-]+$/g, "");
        if (trimmed === title) break;
        title = trimmed;
      }
      if (!title) title = text.trim();
      if (title) title = title[0].toUpperCase() + title.slice(1);
      return { title, due: due || null, hasTime: !!due && hasTime };
    }

    // "Half an hour" is the one fraction people say; it comes back as minutes.
    function amount(word, unit) {
      if (word == null) return null;
      if (word === "half an") return unit.startsWith("h") ? 30 : null;
      const n = parseInt(word, 10);
      return Number.isNaN(n) ? NUMBER_WORDS[word] ?? null : n;
    }

    const relative = first(RELATIVE);
    if (relative && group(relative, 2) != null) {
      const unit = group(relative, 2);
      const n = amount(group(relative, 1), unit);
      if (n != null) {
        removed.push(relative.range);
        if (group(relative, 1) === "half an") return finish(roundedToMinute(new Date(now.getTime() + n * 60000)), true);
        if (unit.startsWith("m")) return finish(roundedToMinute(new Date(now.getTime() + n * 60000)), true);
        if (unit.startsWith("h")) return finish(roundedToMinute(new Date(now.getTime() + n * 3600000)), true);
        return finish(addDays(today, unit.startsWith("w") ? n * 7 : n), false);
      }
    }

    // Which day, and a time of day the words carried with it ("tomorrow evening").
    let day = null;
    let dayPeriod = null;
    let dayRange = null;
    let m;
    if ((m = first(DAY))) {
      const word = group(m, 1);
      dayRange = m.range;
      dayPeriod = group(m, 2) ? PERIOD_HOURS[group(m, 2)] : null;
      if (word === "today") day = today;
      else if (word === "tonight") { day = today; dayPeriod = dayPeriod ?? PERIOD_HOURS.tonight; }
      else if (word === "tomorrow") day = addDays(today, 1);
      else {
        const index = WEEKDAYS.indexOf(word);
        if (index >= 0) {
          let ahead = (index - today.getDay() + 7) % 7;
          if (ahead === 0) ahead = 7;
          day = addDays(today, ahead);
        }
      }
    } else if ((m = first(THIS_PERIOD))) {
      dayRange = m.range;
      day = today;
      dayPeriod = PERIOD_HOURS[group(m, 1)];
    } else if ((m = first(MONTH_DATE))) {
      const name = group(m, 1);
      const number = parseInt(group(m, 2), 10);
      const month = MONTHS.findIndex((short) => name.startsWith(short));
      const year = today.getFullYear();
      const date = month >= 0 ? validDate(year, month + 1, number) : null;
      if (date) {
        dayRange = m.range;
        day = date < today ? validDate(year + 1, month + 1, number) : date;
      }
    } else if ((m = first(ORDINAL_DAY))) {
      const number = parseInt(group(m, 1), 10);
      const year = today.getFullYear();
      const month = today.getMonth() + 1;
      if (number >= today.getDate() && validDate(year, month, number)) {
        dayRange = m.range;
        day = validDate(year, month, number);
      } else {
        const next = new Date(year, month, 1);
        const date = validDate(next.getFullYear(), next.getMonth() + 1, number);
        if (date) { dayRange = m.range; day = date; }
      }
    }
    if (dayRange) removed.push(dayRange);

    // The time of day: hour and minute, and whether AM/PM was said.
    let time = null;
    if ((m = first(MERIDIEM_TIME, dayRange)) && parseInt(group(m, 1), 10) >= 1 && parseInt(group(m, 1), 10) <= 12) {
      const hour = parseInt(group(m, 1), 10);
      const minute = group(m, 2) ? parseInt(group(m, 2), 10) : 0;
      time = { hour: (hour % 12) + (group(m, 3) === "p" ? 12 : 0), minute, certain: true };
      removed.push(m.range);
    } else if ((m = first(NAMED_TIME, dayRange))) {
      time = { hour: group(m, 1) === "noon" ? 12 : 0, minute: 0, certain: true };
      removed.push(m.range);
    } else if ((m = first(AT_TIME, dayRange)) && parseInt(group(m, 1), 10) <= 23) {
      const hour = parseInt(group(m, 1), 10);
      time = { hour, minute: group(m, 2) ? parseInt(group(m, 2), 10) : 0, certain: hour === 0 || hour > 12 };
      removed.push(m.range);
    } else if ((m = first(CLOCK_TIME, dayRange)) && parseInt(group(m, 1), 10) <= 23) {
      const hour = parseInt(group(m, 1), 10);
      time = { hour, minute: parseInt(group(m, 2), 10), certain: hour === 0 || hour > 12 };
      removed.push(m.range);
    } else if ((m = first(PERIOD_TIME, dayRange))) {
      time = { hour: PERIOD_HOURS[group(m, 1) || group(m, 2)] ?? 9, minute: 0, certain: true };
      removed.push(m.range);
    }
    if (time && time.minute > 59) time.minute = 0;

    // "At 5" is 5 PM - nobody sets a 5 AM reminder without saying so. 7 to 11 could be either.
    const candidateHours = (hour) => (hour === 12 ? [12] : hour >= 1 && hour <= 6 ? [hour + 12] : [hour, hour + 12]);
    const guessHour = (hour, period) => (period != null && period >= 12 && hour < 12 ? hour + 12 : candidateHours(hour)[0]);

    if (!day && !time) return finish(null, false);
    if (day && !time) {
      if (dayPeriod != null) {
        const date = at(day, dayPeriod, 0);
        // "Tonight" said after 8 PM means soon, not a time already gone.
        if (date <= now && ymd(day) === ymd(today)) return finish(roundedToMinute(new Date(now.getTime() + 3600000)), true);
        return finish(date, true);
      }
      return finish(day, false);
    }
    if (day && time) {
      const hour = time.certain ? time.hour : guessHour(time.hour, dayPeriod);
      return finish(at(day, hour, time.minute), true);
    }
    // No day: the next time the clock reads that, today or tomorrow.
    const hours = time.certain ? [time.hour] : candidateHours(time.hour);
    for (const hour of hours) {
      const date = at(today, hour, time.minute);
      if (date > now) return finish(date, true);
    }
    return finish(at(addDays(today, 1), hours[0], time.minute), true);
  }

  // The value for a reminder's `due` field (ORGANIZER_SYNC.md), or null for none.
  function dueField(phrase) {
    if (!phrase.due) return null;
    return phrase.hasTime ? isoLocal(phrase.due) : ymd(phrase.due);
  }

  return { parse, dueField };
});
